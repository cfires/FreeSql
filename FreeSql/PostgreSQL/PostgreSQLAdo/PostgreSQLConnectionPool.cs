﻿using Npgsql;
using SafeObjectPool;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace FreeSql.PostgreSQL {

	class PostgreSQLConnectionPool : ObjectPool<DbConnection> {

		internal Action availableHandler;
		internal Action unavailableHandler;

		public PostgreSQLConnectionPool(string name, string connectionString, Action availableHandler, Action unavailableHandler) : base(null) {
			var policy = new PostgreSQLConnectionPoolPolicy {
				_pool = this,
				Name = name
			};
			this.Policy = policy;
			policy.ConnectionString = connectionString;

			this.availableHandler = availableHandler;
			this.unavailableHandler = unavailableHandler;
		}

		public void Return(Object<DbConnection> obj, Exception exception, bool isRecreate = false) {
			if (exception != null && exception is NpgsqlException) {

				if (exception is System.IO.IOException) {

					base.SetUnavailable(exception);

				} else if (obj.Value.Ping() == false) {

					base.SetUnavailable(exception);
				}
			}
			base.Return(obj, isRecreate);
		}
	}

	class PostgreSQLConnectionPoolPolicy : IPolicy<DbConnection> {

		internal PostgreSQLConnectionPool _pool;
		public string Name { get; set; } = "PostgreSQL NpgsqlConnection 对象池";
		public int PoolSize { get; set; } = 50;
		public TimeSpan SyncGetTimeout { get; set; } = TimeSpan.FromSeconds(10);
		public int AsyncGetCapacity { get; set; } = 10000;
		public bool IsThrowGetTimeoutException { get; set; } = true;
		public int CheckAvailableInterval { get; set; } = 5;

		static ConcurrentDictionary<string, int> dicConnStrIncr = new ConcurrentDictionary<string, int>(StringComparer.CurrentCultureIgnoreCase);
		private string _connectionString;
		public string ConnectionString {
			get => _connectionString;
			set {
				var connStr = value ?? "";
				var poolsizePatern = @"Maximum\s*pool\s*size\s*=\s*(\d+)";
				Match m = Regex.Match(connStr, poolsizePatern, RegexOptions.IgnoreCase);
				if (m.Success == false || int.TryParse(m.Groups[1].Value, out var poolsize) == false || poolsize <= 0) poolsize = 100;
				var connStrIncr = dicConnStrIncr.AddOrUpdate(connStr, 1, (oldkey, oldval) => oldval + 1);
				PoolSize = poolsize + connStrIncr;
				_connectionString = m.Success ?
					Regex.Replace(connStr, poolsizePatern, $"Maximum pool size={PoolSize}", RegexOptions.IgnoreCase) :
					$"{connStr};Maximum pool size={PoolSize}";

				var initConns = new List<Object<DbConnection>>();
				for (var a = 0; a < PoolSize; a++)
					try {
						var conn = _pool.Get();
						initConns.Add(conn);
						conn.Value.Ping(true);
					} catch {
						break; //预热失败一次就退出
					}
				foreach (var conn in initConns) _pool.Return(conn);
			}
		}

		public bool OnCheckAvailable(Object<DbConnection> obj) {
			if (obj.Value.State == ConnectionState.Closed) obj.Value.Open();
			return obj.Value.Ping(true);
		}

		public DbConnection OnCreate() {
			var conn = new NpgsqlConnection(_connectionString);
			return conn;
		}

		public void OnDestroy(DbConnection obj) {
			if (obj.State != ConnectionState.Closed) obj.Close();
			obj.Dispose();
		}

		public void OnGet(Object<DbConnection> obj) {

			if (_pool.IsAvailable) {

				if (obj.Value.State != ConnectionState.Open || DateTime.Now.Subtract(obj.LastReturnTime).TotalSeconds > 60 && obj.Value.Ping() == false) {

					try {
						obj.Value.Open();
					} catch (Exception ex) {
						if (_pool.SetUnavailable(ex) == true)
							throw new Exception($"【{this.Name}】状态不可用，等待后台检查程序恢复方可使用。{ex.Message}");
					}
				}
			}
		}

		async public Task OnGetAsync(Object<DbConnection> obj) {

			if (_pool.IsAvailable) {

				if (obj.Value.State != ConnectionState.Open || DateTime.Now.Subtract(obj.LastReturnTime).TotalSeconds > 60 && (await obj.Value.PingAsync()) == false) {

					try {
						await obj.Value.OpenAsync();
					} catch (Exception ex) {
						if (_pool.SetUnavailable(ex) == true)
							throw new Exception($"【{this.Name}】状态不可用，等待后台检查程序恢复方可使用。{ex.Message}");
					}
				}
			}
		}

		public void OnGetTimeout() {

		}

		public void OnReturn(Object<DbConnection> obj) {

		}

		public void OnAvailable() {
			_pool.availableHandler?.Invoke();
		}

		public void OnUnavailable() {
			_pool.unavailableHandler?.Invoke();
		}
	}

	static class DbConnectionExtensions {

		static DbCommand PingCommand(DbConnection conn) {
			var cmd = conn.CreateCommand();
			cmd.CommandTimeout = 1;
			cmd.CommandText = "select 1";
			return cmd;
		}
		public static bool Ping(this DbConnection that, bool isThrow = false) {
			try {
				PingCommand(that).ExecuteNonQuery();
				return true;
			} catch {
				if (that.State != ConnectionState.Closed) try { that.Close(); } catch { }
				if (isThrow) throw;
				return false;
			}
		}
		async public static Task<bool> PingAsync(this DbConnection that, bool isThrow = false) {
			try {
				await PingCommand(that).ExecuteNonQueryAsync();
				return true;
			} catch {
				if (that.State != ConnectionState.Closed) try { that.Close(); } catch { }
				if (isThrow) throw;
				return false;
			}
		}
	}
}
