/*
 * PhoneGap is available under *either* the terms of the modified BSD license *or* the
 * MIT License (2008). See http://opensource.org/licenses/alphabetical for full text.
 *
 * Copyright (c) 2005-2011, Nitobi Software Inc.
 * Copyright (c) 2011, Microsoft Corporation
 */
using System.Dynamic;
using SQLite;
using System;
using System.Linq;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Runtime.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using WPCordovaClassLib.Cordova;
using WPCordovaClassLib.Cordova.Commands;
using WPCordovaClassLib.Cordova.JSON;
using Newtonsoft.Json.Linq;

namespace Cordova.Extension.Commands
{
	/// <summary>
	/// Implementes access to SQLite DB
	/// </summary>
	public class SQLitePlugin : BaseCommand
	{
		#region SQLitePlugin options

		[DataContract]
		public class SQLitePluginOpenCloseOptions
		{
			public string DBName
			{
				get
				{
					return !string.IsNullOrWhiteSpace(this.name) ? this.name : this.dbname;
				}
			}

			[DataMember]
			private string name;
			[DataMember]
			private string dbname;
		}

		[DataContract]
		public class SQLitePluginExecuteSqlBatchOptions
		{
			[DataMember(Name = "dbargs")]
			public SQLitePluginOpenCloseOptions DbArgs { get; set; }

			[DataMember(Name = "executes")]
			public TransactionsCollection Transactions { get; set; }
		}

		[CollectionDataContract]
		public class TransactionsCollection : Collection<SQLitePluginTransaction>
		{

		}

		[DataContract]
		public class SQLitePluginTransaction
		{
			/// <summary>
			/// Identifier for transaction
			/// </summary>
			//[DataMember(IsRequired = true, Name = "trans_id")]
			//public string TransId { get; set; }

			/// <summary>
			/// Identifier for transaction
			/// </summary>
			[DataMember(IsRequired = true, Name = "qid")]
			public string QueryId { get; set; }

			/// <summary>
			/// Identifier for transaction
			/// </summary>
			[DataMember(IsRequired = true, Name = "sql")]
			public string Query { get; set; }

			/// <summary>
			/// Identifier for transaction
			/// </summary>
			[DataMember(IsRequired = true, Name = "params")]
			public string[] QueryParams { get; set; }

		}

		[DataContract]
		public class SQLiteQueryRowSpecial
		{
			[DataMember(Name = "rows")]
			public List<object> Rows { get; set; }
			[DataMember(Name = "rowsAffected")]
			public int RowsAffected { get; set; }
			[DataMember(Name="insertId")]
			public long? LastInsertId { get; set; }
		}

		[DataContract]
		public class SQLiteQueryResult
		{
			[DataMember(Name = "qid")]
			public string QueryId { get; set; }
			[DataMember(Name = "result")]
			public SQLiteQueryRowSpecial ResultRows;
			[DataMember(Name = "type")]
			public string Type { get; set; }
		}

		#endregion
		private SQLitePluginOpenCloseOptions dbOptions = new SQLitePluginOpenCloseOptions();
		private readonly AutoResetEvent signal = new AutoResetEvent(false);
		private SQLiteConnection dbConnection;

		//we don't actually open here, we will do this with each db transaction
		public void open(string options)
		{
			System.Diagnostics.Debug.WriteLine("SQLitePlugin.open with options:" + options);

			try
			{
				var jsonOptions = JsonHelper.Deserialize<string[]>(options)[0];
				this.dbOptions = JsonHelper.Deserialize<SQLitePluginOpenCloseOptions>(jsonOptions);
			}
			catch (Exception)
			{
				this.DispatchCommandResult(new PluginResult(PluginResult.Status.JSON_EXCEPTION));
				return;
			}

			var callbackId = JsonHelper.Deserialize<string[]>(options)[1];
			if (string.IsNullOrEmpty(this.dbOptions.DBName))
			{
				this.DispatchCommandResult(new PluginResult(PluginResult.Status.ERROR, "No database name"), callbackId);
			}
			else
			{
				System.Diagnostics.Debug.WriteLine("SQLitePlugin.open():" + this.dbOptions.DBName);
				this.signal.Set();
				this.DispatchCommandResult(new PluginResult(PluginResult.Status.OK), callbackId);
			}
		}

		public void close(string options)
		{
			System.Diagnostics.Debug.WriteLine("SQLitePlugin.close()");

			if (this.dbConnection != null)
			{
				this.dbConnection.Close();
			}

			this.dbConnection = null;
			var callbackId = JsonHelper.Deserialize<string[]>(options)[1];
			this.DispatchCommandResult(new PluginResult(PluginResult.Status.OK), callbackId);
		}

		public void executeSqlBatch(string options)
		{
			var callOptions = JsonHelper.Deserialize<List<string>>(options);
			var results = new List<SQLiteQueryResult>();
			var dbName = string.Empty;

			try
			{
				var executeSqlBatchOptions = JsonHelper.Deserialize<SQLitePluginExecuteSqlBatchOptions>(callOptions[0]);

				dbName = !string.IsNullOrWhiteSpace(executeSqlBatchOptions.DbArgs.DBName) ?
					executeSqlBatchOptions.DbArgs.DBName : this.dbOptions.DBName;

				if (string.IsNullOrWhiteSpace(dbName))
				{
					this.signal.WaitOne(1000);
					dbName = this.dbOptions.DBName;
					if (string.IsNullOrWhiteSpace(dbName))
					{
						this.DispatchCommandResult(new PluginResult(PluginResult.Status.ERROR, "Database is not open!"), callOptions[1]);
						return;
					}
				}

				if (this.dbConnection == null)
				{
					this.dbConnection = new SQLiteConnection(dbName);
				}

				this.dbConnection.RunInTransaction(() =>
				{
					foreach (SQLitePluginTransaction transaction in executeSqlBatchOptions.Transactions)
					{
						var queryResult = new SQLiteQueryResult();
						queryResult.QueryId = transaction.QueryId;
						queryResult.Type = "success";
						queryResult.ResultRows = new SQLiteQueryRowSpecial();

						System.Diagnostics.Debug.WriteLine("queryId: " + transaction.QueryId + /*" transId: " + transaction.TransId + */" query: " + transaction.Query);
						var first = transaction.Query.IndexOf("DROP TABLE", StringComparison.OrdinalIgnoreCase);
						if (first != -1)
						{
							//-- bug where drop tabe does not work
							transaction.Query = Regex.Replace(transaction.Query, "DROP TABLE IF EXISTS", "DELETE FROM", RegexOptions.IgnoreCase);
							transaction.Query = Regex.Replace(transaction.Query, "DROP TABLE", "DELETE FROM", RegexOptions.IgnoreCase);
							//--
							this.dbConnection.Execute(transaction.Query, transaction.QueryParams);
							//TODO call the callback function if there is a query_id							
						}
						else
						{
							//--if the transaction contains only of COMMIT or ROLLBACK query - do not execute it - there is no point as RunInTransaction is releaseing savepoint at its end.
							//--So if COMMIT or ROLLBACK by itself is executed then there will be nothing to release and exception will occur.
							if (transaction.Query.Trim().ToLower() != "commit" && transaction.Query.Trim().ToLower() != "rollback")
							{

								queryResult.ResultRows.Rows = this.dbConnection.Query2(transaction.Query, transaction.QueryParams)
									.Select(sqliteRow => sqliteRow.column)
									.Select(rowColumns =>
										{
											IDictionary<string, object> result = new ExpandoObject();
											foreach (var column in rowColumns)
											{
												result.Add(column.Key, column.Value);
											}
											return (object)result;
										})
									.ToList();
							}

							queryResult.ResultRows.RowsAffected = SQLite3.Changes(this.dbConnection.Handle);
							queryResult.ResultRows.LastInsertId = SQLite3.LastInsertRowid(this.dbConnection.Handle);
						}

						if (results == null)
						{
							results = new List<SQLiteQueryResult>();
						}

						results.Add(queryResult);
					}
				});

				this.DispatchCommandResult(new PluginResult(PluginResult.Status.OK, JValue.FromObject(results).ToString()), callOptions[1]);
			}
			catch (Exception e)
			{
				System.Diagnostics.Debug.WriteLine("Error: " + e);
				this.DispatchCommandResult(new PluginResult(PluginResult.Status.JSON_EXCEPTION), callOptions[1]);
				return;
			}
		}
	}
}