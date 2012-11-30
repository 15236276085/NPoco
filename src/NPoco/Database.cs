/* NPoco 1.0 - PetaPoco v4.0.3.12 - A Tiny ORMish thing for your POCO's.
 * Copyright 2011-2012.  All Rights Reserved.
 * 
 * Apache License 2.0 - http://www.apache.org/licenses/LICENSE-2.0
 * 
 * Originally created by Brad Robinson (@toptensoftware)
 * 
 * Special thanks to Rob Conery (@robconery) for original inspiration (ie:Massive) and for 
 * use of Subsonic's T4 templates, Rob Sullivan (@DataChomp) for hard core DBA advice 
 * and Adam Schroder (@schotime) for lots of suggestions, improvements and Oracle support
 * 
 * #define POCO_NO_DYNAMIC in your project settings on .NET 3.5
 * 
 */

using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace NPoco
{
    public class Database : IDisposable, IDatabase
    {
        public const string MsSqlClientProvider = "System.Data.SqlClient";

        public Database(IDbConnection connection)
        {
            _sharedConnection = connection;
            _connectionString = connection.ConnectionString;
            _sharedConnectionDepth = 2;		// Prevent closing external connection
            CommonConstruct();
        }

        public Database(string connectionString, string providerName)
        {
            _connectionString = connectionString;
            _providerName = providerName;
            CommonConstruct();
        }

        public Database(string connectionString, DbProviderFactory provider)
        {
            _connectionString = connectionString;
            _factory = provider;
            CommonConstruct();
        }

        public Database(string connectionStringName)
        {
            // Use first?
            if (connectionStringName == "")
                connectionStringName = ConfigurationManager.ConnectionStrings[0].Name;

            // Work out connection string and provider name
            var providerName = "System.Data.SqlClient";
            if (ConfigurationManager.ConnectionStrings[connectionStringName] != null)
            {
                if (!string.IsNullOrEmpty(ConfigurationManager.ConnectionStrings[connectionStringName].ProviderName))
                    providerName = ConfigurationManager.ConnectionStrings[connectionStringName].ProviderName;
            }
            else
            {
                throw new InvalidOperationException("Can't find a connection string with the name '" + connectionStringName + "'");
            }

            // Store factory and connection string
            _connectionString = ConfigurationManager.ConnectionStrings[connectionStringName].ConnectionString;
            _providerName = providerName;
            CommonConstruct();
        }

        private DatabaseType _dbType;
        public DatabaseType DatabaseType { get { return _dbType; } }

        // Common initialization
        private void CommonConstruct()
        {
            _transactionDepth = 0;
            ForceDateTimesToUtc = true;
            EnableAutoSelect = true;

            if (_providerName != null)
                _factory = DbProviderFactories.GetFactory(_providerName);

            string DBTypeName = (_factory == null ? _sharedConnection.GetType() : _factory.GetType()).Name;
            _dbType = DatabaseType.Resolve(DBTypeName, _providerName);

            // What character is used for delimiting parameters in SQL
            _paramPrefix = _dbType.GetParameterPrefix(_connectionString);
        }

        // Automatically close one open shared connection
        public void Dispose()
        {
            // Automatically close one open connection reference
            //  (Works with KeepConnectionAlive and manually opening a shared connection)
            CloseSharedConnection();
        }

        // Set to true to keep the first opened connection alive until this object is disposed
        public bool KeepConnectionAlive { get; set; }

        // Open a connection (can be nested)
        public void OpenSharedConnection()
        {
            if (_sharedConnectionDepth == 0)
            {
                _sharedConnection = _factory.CreateConnection();
                _sharedConnection.ConnectionString = _connectionString;

                if (_sharedConnection.State == ConnectionState.Broken)
                    _sharedConnection.Close();
                if (_sharedConnection.State == ConnectionState.Closed)
                    _sharedConnection.Open();

                _sharedConnection = OnConnectionOpened(_sharedConnection);

                if (KeepConnectionAlive)
                    _sharedConnectionDepth++;		// Make sure you call Dispose
            }
            _sharedConnectionDepth++;
        }

        // Close a previously opened connection
        public void CloseSharedConnection()
        {
            if (_sharedConnectionDepth > 0)
            {
                _sharedConnectionDepth--;
                if (_sharedConnectionDepth == 0)
                {
                    OnConnectionClosing(_sharedConnection);
                    _sharedConnection.Dispose();
                    _sharedConnection = null;
                }
            }
        }

        public VersionExceptionHandling VersionException
        {
            get { return _versionException; }
            set { _versionException = value; }
        }

        // Access to our shared connection
        public IDbConnection Connection
        {
            get { return _sharedConnection; }
        }

        public IDbTransaction Transaction
        {
            get { return _transaction; }
        }

        public IDataParameter CreateParameter()
        {
            using (var conn = _sharedConnection ?? _factory.CreateConnection())
            using (var comm = conn.CreateCommand())
                return comm.CreateParameter();
        }

        // Helper to create a transaction scope
        public Transaction GetTransaction()
        {
            return GetTransaction(null);
        }

        public Transaction GetTransaction(IsolationLevel? isolationLevel)
        {
            return new Transaction(this, isolationLevel);
        }

        public IDatabase SetTransaction(IDbTransaction tran)
        {
            _transaction = tran;
            _transactionDepth++;
            return this;
        }

        // Use by derived repo generated by T4 templates
        public virtual void OnBeginTransaction() { }
        public virtual void OnEndTransaction() { }

        public void BeginTransaction()
        {
            BeginTransaction(null);
        }

        // Start a new transaction, can be nested, every call must be
        //	matched by a call to AbortTransaction or CompleteTransaction
        // Use `using (var scope=db.Transaction) { scope.Complete(); }` to ensure correct semantics
        public void BeginTransaction(IsolationLevel? isolationLevel)
        {
            _transactionDepth++;

            if (_transactionDepth == 1)
            {
                OpenSharedConnection();
                _transaction = isolationLevel == null ? _sharedConnection.BeginTransaction() : _sharedConnection.BeginTransaction(isolationLevel.Value);
                _transactionCancelled = false;
                OnBeginTransaction();
            }

        }

        // Internal helper to cleanup transaction stuff
        void CleanupTransaction()
        {
            OnEndTransaction();

            if (_transactionCancelled)
                _transaction.Rollback();
            else
                _transaction.Commit();

            _transaction.Dispose();
            _transaction = null;

            CloseSharedConnection();
        }

        // Abort the entire outer most transaction scope
        public void AbortTransaction()
        {
            _transactionCancelled = true;
            if ((--_transactionDepth) == 0)
                CleanupTransaction();
        }

        // Complete the transaction
        public void CompleteTransaction()
        {
            if ((--_transactionDepth) == 0)
                CleanupTransaction();
        }

        // Add a parameter to a DB command
        void AddParam(IDbCommand cmd, object value, string ParameterPrefix)
        {
            // Convert value to from poco type to db type
            if (Mapper != null && value!=null)
            {
                var fn = Mapper.GetParameterConverter(value.GetType());
                if (fn!=null)
                    value = fn(value);
            }

            // Support passed in parameters
            var idbParam = value as IDbDataParameter;
            if (idbParam != null)
            {
                idbParam.ParameterName = string.Format("{0}{1}", ParameterPrefix, cmd.Parameters.Count);
                cmd.Parameters.Add(idbParam);
                return;
            }
            var p = cmd.CreateParameter();
            p.ParameterName = string.Format("{0}{1}", ParameterPrefix, cmd.Parameters.Count);

            if (value == null)
            {
                p.Value = DBNull.Value;
            }
            else
            {
                // Give the database type first crack at converting to DB required type
                value = _dbType.MapParameterValue(value);

                var t = value.GetType();
                if (t.IsEnum)		// PostgreSQL .NET driver wont cast enum to int
                {
                    p.Value = (int)value;
                }
                else if (t == typeof(Guid))
                {
                    p.Value = value.ToString();
                    p.DbType = DbType.String;
                    p.Size = 40;
                }
                else if (t == typeof(string))
                {
                    // out of memory exception occurs if trying to save more than 4000 characters to SQL Server CE NText column. Set before attempting to set Size, or Size will always max out at 4000
                    if ((value as string).Length + 1 > 4000 && p.GetType().Name == "SqlCeParameter")
                        p.GetType().GetProperty("SqlDbType").SetValue(p, SqlDbType.NText, null); 

                    p.Size = Math.Max((value as string).Length + 1, 4000);		// Help query plan caching by using common size
                    p.Value = value;
                }
                else if (t == typeof(AnsiString))
                {
                    // Thanks @DataChomp for pointing out the SQL Server indexing performance hit of using wrong string type on varchar
                    p.Size = Math.Max((value as AnsiString).Value.Length + 1, 4000);
                    p.Value = (value as AnsiString).Value;
                    p.DbType = DbType.AnsiString;
                }
                else if (value.GetType().Name == "SqlGeography") //SqlGeography is a CLR Type
                {
                    p.GetType().GetProperty("UdtTypeName").SetValue(p, "geography", null); //geography is the equivalent SQL Server Type
                    p.Value = value;
                }

                else if (value.GetType().Name == "SqlGeometry") //SqlGeometry is a CLR Type
                {
                    p.GetType().GetProperty("UdtTypeName").SetValue(p, "geometry", null); //geography is the equivalent SQL Server Type
                    p.Value = value;
                }
                else
                {
                    p.Value = value;
                }
            }

            cmd.Parameters.Add(p);
        }

        // Create a command
        IDbCommand CreateCommand(IDbConnection connection, string sql, params object[] args)
        {
            // Perform parameter prefix replacements
            if (_paramPrefix != "@")
                sql = ParameterHelper.rxParamsPrefix.Replace(sql, m => _paramPrefix + m.Value.Substring(1));
            sql = sql.Replace("@@", "@");		   // <- double @@ escapes a single @

            // Create the command and add parameters
            IDbCommand cmd = connection.CreateCommand();
            cmd.Connection = connection;
            cmd.CommandText = sql;
            cmd.Transaction = _transaction;

            foreach (var item in args)
            {
                AddParam(cmd, item, _paramPrefix);
            }

            // Notify the DB type
            _dbType.PreExecute(cmd);

            if (!String.IsNullOrEmpty(sql))
                DoPreExecute(cmd);

            return cmd;
        }

        // Override this to log/capture exceptions
        public virtual void OnException(Exception x)
        {
            System.Diagnostics.Debug.WriteLine(x.ToString());
            System.Diagnostics.Debug.WriteLine(LastCommand);
        }

        // Override this to log commands, or modify command before execution
        public virtual IDbConnection OnConnectionOpened(IDbConnection conn) { return conn; }
        public virtual void OnConnectionClosing(IDbConnection conn) { }
        public virtual void OnExecutingCommand(IDbCommand cmd) { }
        public virtual void OnExecutedCommand(IDbCommand cmd) { }

        // Execute a non-query command
        public int Execute(string sql, params object[] args)
        {
            return Execute(new Sql(sql, args));
        }

        public int Execute(Sql Sql)
        {
            var sql = Sql.SQL;
            var args = Sql.Arguments;

            try
            {
                OpenSharedConnection();
                try
                {
                    using (var cmd = CreateCommand(_sharedConnection, sql, args))
                    {
                        var result = cmd.ExecuteNonQuery();
                        OnExecutedCommand(cmd);
                        return result;
                    }
                }
                finally
                {
                    CloseSharedConnection();
                }
            }
            catch (Exception x)
            {
                OnException(x);
                throw;
            }
        }

        // Execute and cast a scalar property
        public T ExecuteScalar<T>(string sql, params object[] args)
        {
            return ExecuteScalar<T>(new Sql(sql, args));
        }

        public T ExecuteScalar<T>(Sql Sql)
        {
            var sql = Sql.SQL;
            var args = Sql.Arguments;

            try
            {
                OpenSharedConnection();
                try
                {
                    using (var cmd = CreateCommand(_sharedConnection, sql, args))
                    {
                        object val = cmd.ExecuteScalar();
                        OnExecutedCommand(cmd);

                        if (val == null || val == DBNull.Value)
                            return default(T);

                        Type t = typeof(T);
                        Type u = Nullable.GetUnderlyingType(t);

                        return (T)Convert.ChangeType(val, u ?? t);
                    }
                }
                finally
                {
                    CloseSharedConnection();
                }
            }
            catch (Exception x)
            {
                OnException(x);
                throw;
            }
        }

        public bool ForceDateTimesToUtc { get; set; }
        public bool EnableAutoSelect { get; set; }

        // Return a typed list of pocos
        public List<T> Fetch<T>(string sql, params object[] args) 
        {
            return Fetch<T>(new Sql(sql, args));
        }
            
        public List<T> Fetch<T>(Sql sql) 
        {
            return Query<T>(sql).ToList();
        }

        public List<T> Fetch<T>()
        {
            return Fetch<T>("");
        }

        public void BuildPageQueries<T>(long skip, long take, string sql, ref object[] args, out string sqlCount, out string sqlPage) 
        {
            // Add auto select clause
            if (EnableAutoSelect)
                sql = AutoSelectHelper.AddSelectClause<T>(_dbType, sql);

            // Split the SQL
            PagingHelper.SQLParts parts;
            if (!PagingHelper.SplitSQL(sql, out parts))
                throw new Exception("Unable to parse SQL statement for paged query");

            sqlPage = _dbType.BuildPageQuery(skip, take, parts, ref args);
            sqlCount = parts.sqlCount;
        }

        // Fetch a page	
        public Page<T> Page<T>(long page, long itemsPerPage, string sql, params object[] args) 
        {
            string sqlCount, sqlPage;
            BuildPageQueries<T>((page-1)*itemsPerPage, itemsPerPage, sql, ref args, out sqlCount, out sqlPage);

            // Save the one-time command time out and use it for both queries
            int saveTimeout = OneTimeCommandTimeout;

            // Setup the paged result
            var result = new Page<T>();
            result.CurrentPage = page;
            result.ItemsPerPage = itemsPerPage;
            result.TotalItems = ExecuteScalar<long>(sqlCount, args);
            result.TotalPages = result.TotalItems / itemsPerPage;
            if ((result.TotalItems % itemsPerPage) != 0)
                result.TotalPages++;

            OneTimeCommandTimeout = saveTimeout;

            // Get the records
            result.Items = Fetch<T>(sqlPage, args);

            // Done
            return result;
        }

        public Page<T> Page<T>(long page, long itemsPerPage, Sql sql) 
        {
            return Page<T>(page, itemsPerPage, sql.SQL, sql.Arguments);
        }

        public List<T> Fetch<T>(long page, long itemsPerPage, string sql, params object[] args)
        {
            return SkipTake<T>((page - 1) * itemsPerPage, itemsPerPage, sql, args);
        }

        public List<T> Fetch<T>(long page, long itemsPerPage, Sql sql)
        {
            return SkipTake<T>((page - 1) * itemsPerPage, itemsPerPage, sql.SQL, sql.Arguments);
        }

        public List<T> SkipTake<T>(long skip, long take, string sql, params object[] args)
        {
            string sqlCount, sqlPage;
            BuildPageQueries<T>(skip, take, sql, ref args, out sqlCount, out sqlPage);
            return Fetch<T>(sqlPage, args);
        }

        public List<T> SkipTake<T>(long skip, long take, Sql sql)
        {
            return SkipTake<T>(skip, take, sql.SQL, sql.Arguments);
        }

        public Dictionary<TKey, TValue> Dictionary<TKey, TValue>(Sql Sql)
        {
            return Dictionary<TKey, TValue>(Sql.SQL, Sql.Arguments);
        }

        public Dictionary<TKey, TValue> Dictionary<TKey, TValue>(string sql, params object[] args)
        {
            var newDict = new Dictionary<TKey, TValue>();
            bool isConverterSet = false;
            Func<object, object> converter1 = x => x, converter2 = x => x;

            foreach (var line in Query<Dictionary<string, object>>(sql, args))
            {
                object key = line.ElementAt(0).Value;
                object value = line.ElementAt(1).Value;

                if (isConverterSet == false)
                {
                    converter1 = PocoData.GetConverter(null, typeof (TKey), key.GetType()) ?? (x => x);
                    converter2 = PocoData.GetConverter(null, typeof (TValue), value.GetType()) ?? (x => x);
                    isConverterSet = true;
                }

                var keyConverted = (TKey) Convert.ChangeType(converter1(key), typeof (TKey));

                var valueType = Nullable.GetUnderlyingType(typeof (TValue)) ?? typeof (TValue);
                var valConv = converter2(value);
                var valConverted = valConv != null ? (TValue)Convert.ChangeType(valConv, valueType) : default(TValue);

                if (keyConverted != null)
                {
                    newDict.Add(keyConverted, valConverted);
                }
            }
            return newDict;
        }

        // Return an enumerable collection of pocos
        public IEnumerable<T> Query<T>(string sql, params object[] args)
        {
            return Query<T>(new Sql(sql, args));
        }

        public IEnumerable<T> Query<T>(Sql Sql)
        {
            return Query<T>(default(T), Sql);
        }

        private IEnumerable<T> Query<T>(T instance, Sql Sql) 
        {
            var sql = Sql.SQL;
            var args = Sql.Arguments;

            if (EnableAutoSelect)
                sql = AutoSelectHelper.AddSelectClause<T>(_dbType, sql);

            OpenSharedConnection();
            try
            {
                using (var cmd = CreateCommand(_sharedConnection, sql, args))
                {
                    IDataReader r;
                    var pd = PocoData.ForType(typeof(T));
                    try
                    {
                        r = cmd.ExecuteReader();
                        OnExecutedCommand(cmd);
                    }
                    catch (Exception x)
                    {
                        OnException(x);
                        throw;
                    }
                    
                    using (r)
                    {
                        var factory = pd.GetFactory(cmd.CommandText, _sharedConnection.ConnectionString, 0, r.FieldCount, r, instance) as Func<IDataReader, T, T>;
                        while (true)
                        {
                            T poco;
                            try
                            {
                                if (!r.Read())
                                    yield break;
                                poco = factory(r, instance);
                            }
                            catch (Exception x)
                            {
                                OnException(x);
                                throw;
                            }

                            yield return poco;
                        }
                    }
                }
            }
            finally
            {
                CloseSharedConnection();
            }
        }

        // Multi Fetch
        public List<TRet> Fetch<T1, T2, TRet>(Func<T1, T2, TRet> cb, string sql, params object[] args) { return Query<T1, T2, TRet>(cb, sql, args).ToList(); }
        public List<TRet> Fetch<T1, T2, T3, TRet>(Func<T1, T2, T3, TRet> cb, string sql, params object[] args) { return Query<T1, T2, T3, TRet>(cb, sql, args).ToList(); }
        public List<TRet> Fetch<T1, T2, T3, T4, TRet>(Func<T1, T2, T3, T4, TRet> cb, string sql, params object[] args) { return Query<T1, T2, T3, T4, TRet>(cb, sql, args).ToList(); }

        // Multi Query
        public IEnumerable<TRet> Query<T1, T2, TRet>(Func<T1, T2, TRet> cb, string sql, params object[] args) { return Query<TRet>(new Type[] { typeof(T1), typeof(T2) }, cb, new Sql(sql, args)); }
        public IEnumerable<TRet> Query<T1, T2, T3, TRet>(Func<T1, T2, T3, TRet> cb, string sql, params object[] args) { return Query<TRet>(new Type[] { typeof(T1), typeof(T2), typeof(T3) }, cb, new Sql(sql, args)); }
        public IEnumerable<TRet> Query<T1, T2, T3, T4, TRet>(Func<T1, T2, T3, T4, TRet> cb, string sql, params object[] args) { return Query<TRet>(new Type[] { typeof(T1), typeof(T2), typeof(T3), typeof(T4) }, cb, new Sql(sql, args)); }

        // Multi Fetch (SQL builder)
        public List<TRet> Fetch<T1, T2, TRet>(Func<T1, T2, TRet> cb, Sql sql) { return Query<T1, T2, TRet>(cb, sql).ToList(); }
        public List<TRet> Fetch<T1, T2, T3, TRet>(Func<T1, T2, T3, TRet> cb, Sql sql) { return Query<T1, T2, T3, TRet>(cb, sql).ToList(); }
        public List<TRet> Fetch<T1, T2, T3, T4, TRet>(Func<T1, T2, T3, T4, TRet> cb, Sql sql) { return Query<T1, T2, T3, T4, TRet>(cb, sql).ToList(); }

        // Multi Query (SQL builder)
        public IEnumerable<TRet> Query<T1, T2, TRet>(Func<T1, T2, TRet> cb, Sql sql) { return Query<TRet>(new Type[] { typeof(T1), typeof(T2) }, cb, sql); }
        public IEnumerable<TRet> Query<T1, T2, T3, TRet>(Func<T1, T2, T3, TRet> cb, Sql sql) { return Query<TRet>(new Type[] { typeof(T1), typeof(T2), typeof(T3) }, cb, sql); }
        public IEnumerable<TRet> Query<T1, T2, T3, T4, TRet>(Func<T1, T2, T3, T4, TRet> cb, Sql sql) { return Query<TRet>(new Type[] { typeof(T1), typeof(T2), typeof(T3), typeof(T4) }, cb, sql); }

        // Multi Fetch (Simple)
        public List<T1> Fetch<T1, T2>(string sql, params object[] args) { return Query<T1, T2>(sql, args).ToList(); }
        public List<T1> Fetch<T1, T2, T3>(string sql, params object[] args) { return Query<T1, T2, T3>(sql, args).ToList(); }
        public List<T1> Fetch<T1, T2, T3, T4>(string sql, params object[] args) { return Query<T1, T2, T3, T4>(sql, args).ToList(); }

        // Multi Query (Simple)
        public IEnumerable<T1> Query<T1, T2>(string sql, params object[] args) { return Query<T1>(new Type[] { typeof(T1), typeof(T2) }, null, new Sql(sql, args)); }
        public IEnumerable<T1> Query<T1, T2, T3>(string sql, params object[] args) { return Query<T1>(new Type[] { typeof(T1), typeof(T2), typeof(T3) }, null, new Sql(sql, args)); }
        public IEnumerable<T1> Query<T1, T2, T3, T4>(string sql, params object[] args) { return Query<T1>(new Type[] { typeof(T1), typeof(T2), typeof(T3), typeof(T4) }, null, new Sql(sql, args)); }

        // Multi Fetch (Simple) (SQL builder)
        public List<T1> Fetch<T1, T2>(Sql sql) { return Query<T1, T2>(sql).ToList(); }
        public List<T1> Fetch<T1, T2, T3>(Sql sql) { return Query<T1, T2, T3>(sql).ToList(); }
        public List<T1> Fetch<T1, T2, T3, T4>(Sql sql) { return Query<T1, T2, T3, T4>(sql).ToList(); }

        // Multi Query (Simple) (SQL builder)
        public IEnumerable<T1> Query<T1, T2>(Sql sql) { return Query<T1>(new Type[] { typeof(T1), typeof(T2) }, null, sql); }
        public IEnumerable<T1> Query<T1, T2, T3>(Sql sql) { return Query<T1>(new Type[] { typeof(T1), typeof(T2), typeof(T3) }, null, sql); }
        public IEnumerable<T1> Query<T1, T2, T3, T4>(Sql sql) { return Query<T1>(new Type[] { typeof(T1), typeof(T2), typeof(T3), typeof(T4) }, null, sql); }

   
        // Actual implementation of the multi-poco query
        public IEnumerable<TRet> Query<TRet>(Type[] types, object cb, Sql sql)
        {
            OpenSharedConnection();
            try
            {
                using (var cmd = CreateCommand(_sharedConnection, sql.SQL, sql.Arguments))
                {
                    IDataReader r;
                    try
                    {
                        r = cmd.ExecuteReader();
                        OnExecutedCommand(cmd);
                    }
                    catch (Exception x)
                    {
                        OnException(x);
                        throw;
                    }
                    var factory = MultiPocoFactory.GetMultiPocoFactory<TRet>(types, sql.SQL, _sharedConnection.ConnectionString, r);
                    if (cb == null)
                        cb = MultiPocoFactory.GetAutoMapper(types.ToArray());
                    bool bNeedTerminator = false;
                    using (r)
                    {
                        while (true)
                        {
                            TRet poco;
                            try
                            {
                                if (!r.Read())
                                    break;
                                poco = factory(r, cb);
                            }
                            catch (Exception x)
                            {
                                OnException(x);
                                throw;
                            }

                            if (poco != null)
                                yield return poco;
                            else
                                bNeedTerminator = true;
                        }
                        if (bNeedTerminator)
                        {
                            var poco = (TRet)(cb as Delegate).DynamicInvoke(new object[types.Length]);
                            if (poco != null)
                                yield return poco;
                            else
                                yield break;
                        }
                    }
                }
            }
            finally
            {
                CloseSharedConnection();
            }
        }

        public TRet FetchMultiple<T1, T2, TRet>(Func<List<T1>, List<T2>, TRet> cb, string sql, params object[] args) { return FetchMultiple<T1, T2, DontMap, DontMap, TRet>(new[] { typeof(T1), typeof(T2) }, cb, new Sql(sql, args)); }
        public TRet FetchMultiple<T1, T2, T3, TRet>(Func<List<T1>, List<T2>, List<T3>, TRet> cb, string sql, params object[] args) { return FetchMultiple<T1, T2, T3, DontMap, TRet>(new[] { typeof(T1), typeof(T2), typeof(T3) }, cb, new Sql(sql, args)); }
        public TRet FetchMultiple<T1, T2, T3, T4, TRet>(Func<List<T1>, List<T2>, List<T3>, List<T4>, TRet> cb, string sql, params object[] args) { return FetchMultiple<T1, T2, T3, T4, TRet>(new[] { typeof(T1), typeof(T2), typeof(T3), typeof(T4) }, cb, new Sql(sql, args)); }
        public TRet FetchMultiple<T1, T2, TRet>(Func<List<T1>, List<T2>, TRet> cb, Sql sql) { return FetchMultiple<T1, T2, DontMap, DontMap, TRet>(new[] { typeof(T1), typeof(T2) }, cb, sql); }
        public TRet FetchMultiple<T1, T2, T3, TRet>(Func<List<T1>, List<T2>, List<T3>, TRet> cb, Sql sql) { return FetchMultiple<T1, T2, T3, DontMap, TRet>(new[] { typeof(T1), typeof(T2), typeof(T3) }, cb, sql); }
        public TRet FetchMultiple<T1, T2, T3, T4, TRet>(Func<List<T1>, List<T2>, List<T3>, List<T4>, TRet> cb, Sql sql) { return FetchMultiple<T1, T2, T3, T4, TRet>(new[] { typeof(T1), typeof(T2), typeof(T3), typeof(T4) }, cb, sql); }

        public Tuple<List<T1>, List<T2>> FetchMultiple<T1, T2>(string sql, params object[] args) { return FetchMultiple<T1, T2, DontMap, DontMap, Tuple<List<T1>, List<T2>>>(new[] { typeof(T1), typeof(T2) }, new Func<List<T1>, List<T2>, Tuple<List<T1>, List<T2>>>((y, z) => new Tuple<List<T1>, List<T2>>(y, z)), new Sql(sql, args)); }
        public Tuple<List<T1>, List<T2>, List<T3>> FetchMultiple<T1, T2, T3>(string sql, params object[] args) { return FetchMultiple<T1, T2, T3, DontMap, Tuple<List<T1>, List<T2>, List<T3>>>(new[] { typeof(T1), typeof(T2), typeof(T3) }, new Func<List<T1>, List<T2>, List<T3>, Tuple<List<T1>, List<T2>, List<T3>>>((x, y, z) => new Tuple<List<T1>, List<T2>, List<T3>>(x, y, z)), new Sql(sql, args)); }
        public Tuple<List<T1>, List<T2>, List<T3>, List<T4>> FetchMultiple<T1, T2, T3, T4>(string sql, params object[] args) { return FetchMultiple<T1, T2, T3, T4, Tuple<List<T1>, List<T2>, List<T3>, List<T4>>>(new[] { typeof(T1), typeof(T2), typeof(T3), typeof(T4) }, new Func<List<T1>, List<T2>, List<T3>, List<T4>, Tuple<List<T1>, List<T2>, List<T3>, List<T4>>>((w, x, y, z) => new Tuple<List<T1>, List<T2>, List<T3>, List<T4>>(w, x, y, z)), new Sql(sql, args)); }
        public Tuple<List<T1>, List<T2>> FetchMultiple<T1, T2>(Sql sql) { return FetchMultiple<T1, T2, DontMap, DontMap, Tuple<List<T1>, List<T2>>>(new[] { typeof(T1), typeof(T2) }, new Func<List<T1>, List<T2>, Tuple<List<T1>, List<T2>>>((y, z) => new Tuple<List<T1>, List<T2>>(y, z)), sql); }
        public Tuple<List<T1>, List<T2>, List<T3>> FetchMultiple<T1, T2, T3>(Sql sql) { return FetchMultiple<T1, T2, T3, DontMap, Tuple<List<T1>, List<T2>, List<T3>>>(new[] { typeof(T1), typeof(T2), typeof(T3) }, new Func<List<T1>, List<T2>, List<T3>, Tuple<List<T1>, List<T2>, List<T3>>>((x, y, z) => new Tuple<List<T1>, List<T2>, List<T3>>(x, y, z)), sql); }
        public Tuple<List<T1>, List<T2>, List<T3>, List<T4>> FetchMultiple<T1, T2, T3, T4>(Sql sql) { return FetchMultiple<T1, T2, T3, T4, Tuple<List<T1>, List<T2>, List<T3>, List<T4>>>(new[] { typeof(T1), typeof(T2), typeof(T3), typeof(T4) }, new Func<List<T1>, List<T2>, List<T3>, List<T4>, Tuple<List<T1>, List<T2>, List<T3>, List<T4>>>((w, x, y, z) => new Tuple<List<T1>, List<T2>, List<T3>, List<T4>>(w, x, y, z)), sql); }

        public class DontMap {}

        // Actual implementation of the multi query
        private TRet FetchMultiple<T1, T2, T3, T4, TRet>(Type[] types, object cb, Sql Sql)
        {
            var sql = Sql.SQL;
            var args = Sql.Arguments;

            OpenSharedConnection();
            try
            {
                using (var cmd = CreateCommand(_sharedConnection, sql, args))
                {
                    IDataReader r;
                    try
                    {
                        r = cmd.ExecuteReader();
                        OnExecutedCommand(cmd);
                    }
                    catch (Exception x)
                    {
                        OnException(x);
                        throw;
                    }

                    using (r)
                    {
                        var typeIndex = 1;
                        var list1 = new List<T1>();
                        var list2 = new List<T2>();
                        var list3 = new List<T3>();
                        var list4 = new List<T4>();
                        do
                        {
                            if (typeIndex > types.Length)
                                break;
                            
                            var pd = PocoData.ForType(types[typeIndex-1]);
                            var factory = pd.GetFactory(cmd.CommandText, _sharedConnection.ConnectionString, 0, r.FieldCount, r, null);
                            
                            while (true)
                            {
                                try
                                {
                                    if (!r.Read())
                                        break;

                                    switch (typeIndex)
                                    {
                                        case 1:
                                            list1.Add(((Func<IDataReader, T1, T1>)factory)(r, default(T1)));
                                            break;
                                        case 2:
                                            list2.Add(((Func<IDataReader, T2, T2>)factory)(r, default(T2)));
                                            break;
                                        case 3:
                                            list3.Add(((Func<IDataReader, T3, T3>)factory)(r, default(T3)));
                                            break;
                                        case 4:
                                            list4.Add(((Func<IDataReader, T4, T4>)factory)(r, default(T4)));
                                            break;
                                    }
                                }
                                catch (Exception x)
                                {
                                    OnException(x);
                                    throw;
                                }
                            }

                            typeIndex++;
                        } while (r.NextResult());

                        switch (types.Length)
                        {
                            case 2:
                                return ((Func<List<T1>, List<T2>, TRet>)cb)(list1, list2);
                            case 3:
                                return ((Func<List<T1>, List<T2>, List<T3>, TRet>)cb)(list1, list2, list3);
                            case 4:
                                return ((Func<List<T1>, List<T2>, List<T3>, List<T4>, TRet>)cb)(list1, list2, list3, list4);
                        }

                        return default(TRet);
                    }
                }
            }
            finally
            {
                CloseSharedConnection();
            }
        }

        public bool Exists<T>(object primaryKey) 
        {
            var index = 0;
            var primaryKeyValuePairs = GetPrimaryKeyValues(PocoData.ForType(typeof(T)).TableInfo.PrimaryKey, primaryKey);
            return FirstOrDefault<T>(string.Format("WHERE {0}", BuildPrimaryKeySql(primaryKeyValuePairs, ref index)), primaryKeyValuePairs.Select(x => x.Value).ToArray()) != null;
        }
        public T SingleById<T>(object primaryKey) 
        {
            var index = 0;
            var primaryKeyValuePairs = GetPrimaryKeyValues(PocoData.ForType(typeof(T)).TableInfo.PrimaryKey, primaryKey);
            return Single<T>(string.Format("WHERE {0}", BuildPrimaryKeySql(primaryKeyValuePairs, ref index)), primaryKeyValuePairs.Select(x => x.Value).ToArray());
        }
        public T SingleOrDefaultById<T>(object primaryKey) 
        {
            var index = 0;
            var primaryKeyValuePairs = GetPrimaryKeyValues(PocoData.ForType(typeof(T)).TableInfo.PrimaryKey, primaryKey);
            return SingleOrDefault<T>(string.Format("WHERE {0}", BuildPrimaryKeySql(primaryKeyValuePairs, ref index)), primaryKeyValuePairs.Select(x => x.Value).ToArray());
        }
        public T Single<T>(string sql, params object[] args) 
        {
            return Query<T>(sql, args).Single();
        }
        public T SingleInto<T>(T instance, string sql, params object[] args)
        {
            return Query<T>(instance, new Sql(sql, args)).Single();
        }
        public T SingleOrDefault<T>(string sql, params object[] args) 
        {
            return Query<T>(sql, args).SingleOrDefault();
        }
        public T SingleOrDefaultInto<T>(T instance, string sql, params object[] args)
        {
            return Query<T>(instance, new Sql(sql, args)).SingleOrDefault();
        }
        public T First<T>(string sql, params object[] args) 
        {
            return Query<T>(sql, args).First();
        }
        public T FirstInto<T>(T instance, string sql, params object[] args)
        {
            return Query<T>(instance, new Sql(sql, args)).First();
        }
        public T FirstOrDefault<T>(string sql, params object[] args) 
        {
            return Query<T>(sql, args).FirstOrDefault();
        }
        public T FirstOrDefaultInto<T>(T instance, string sql, params object[] args)
        {
            return Query<T>(instance, new Sql(sql, args)).FirstOrDefault();
        }
        public T Single<T>(Sql sql) 
        {
            return Query<T>(sql).Single();
        }
        public T SingleInto<T>(T instance, Sql sql)
        {
            return Query<T>(instance, sql).Single();
        }
        public T SingleOrDefault<T>(Sql sql) 
        {
            return Query<T>(sql).SingleOrDefault();
        }
        public T SingleOrDefaultInto<T>(T instance, Sql sql)
        {
            return Query<T>(instance, sql).SingleOrDefault();
        }
        public T First<T>(Sql sql) 
        {
            return Query<T>(sql).First();
        }
        public T FirstInto<T>(T instance, Sql sql)
        {
            return Query<T>(instance, sql).First();
        }
        public T FirstOrDefault<T>(Sql sql) 
        {
            return Query<T>(sql).FirstOrDefault();
        }
        public T FirstOrDefaultInto<T>(T instance, Sql sql)
        {
            return Query<T>(instance, sql).FirstOrDefault();
        }

        public object Insert(string tableName, string primaryKeyName, object poco)
        {
            return Insert(tableName, primaryKeyName, true, poco);
        }

        // Insert a poco into a table.  If the poco has a property with the same name 
        // as the primary key the id of the new record is assigned to it.  Either way,
        // the new id is returned.
        public object Insert(string tableName, string primaryKeyName, bool autoIncrement, object poco)
        {
            try
            {
                OpenSharedConnection();
                try
                {
                    var pd = PocoData.ForObject(poco, primaryKeyName);
                    var names = new List<string>();
                    var values = new List<string>();
                    var rawvalues = new List<object>();
                    var index = 0;
                    var versionName = "";

                    foreach (var i in pd.Columns)
                    {
                        // Don't insert result columns
                        if (i.Value.ResultColumn)
                            continue;

                        // Don't insert the primary key (except under oracle where we need bring in the next sequence value)
                        if (autoIncrement && primaryKeyName != null && string.Compare(i.Key, primaryKeyName, true)==0)
                        {
                            // Setup auto increment expression
                            string autoIncExpression = _dbType.GetAutoIncrementExpression(pd.TableInfo);
                            if (autoIncExpression != null)
                            {
                                names.Add(i.Key);
                                values.Add(autoIncExpression);
                            }
                            continue;
                        }

                        names.Add(_dbType.EscapeSqlIdentifier(i.Key));
                        values.Add(string.Format("{0}{1}", _paramPrefix, index++));

                        object val = i.Value.GetValue(poco);
                        if (Mapper != null)
                        {
                            var converter = Mapper.GetToDbConverter(i.Value.ColumnType, i.Value.PropertyInfo.PropertyType);
                            if (converter != null)
                                val = converter(val);
                        }
                        
                        if (i.Value.VersionColumn)
                        {
                            val = 1;
                            versionName = i.Key;
                        }

                        rawvalues.Add(val);
                    }

                    using (var cmd = CreateCommand(_sharedConnection, ""))
                    {
                        var sql = string.Empty;
                        var outputClause = String.Empty;
                        if (autoIncrement)
                        {
                            outputClause = _dbType.GetInsertOutputClause(primaryKeyName);
                        }

                        if (names.Count != 0)
                        {
                            sql = string.Format("INSERT INTO {0} ({1}){2} VALUES ({3})",
                                                _dbType.EscapeTableName(tableName),
                                                string.Join(",", names.ToArray()),
                                                outputClause,
                                                string.Join(",", values.ToArray()));
                        }
                        else
                        {
                            sql = _dbType.GetDefaultInsertSql(tableName, names.ToArray(), values.ToArray());
                        }

                        cmd.CommandText = sql;
                        rawvalues.ForEach(x => AddParam(cmd, x, _paramPrefix));

                        if (!autoIncrement)
                        {
                            DoPreExecute(cmd);
                            cmd.ExecuteNonQuery();
                            OnExecutedCommand(cmd);

                            PocoColumn pkColumn;
                            if (primaryKeyName != null && pd.Columns.TryGetValue(primaryKeyName, out pkColumn))
                                return pkColumn.GetValue(poco);
                            else
                                return null;
                        }

                        object id = _dbType.ExecuteInsert(this, cmd, primaryKeyName);

                        // Assign the ID back to the primary key property
                        if (primaryKeyName != null)
                        {
                            PocoColumn pc;
                            if (pd.Columns.TryGetValue(primaryKeyName, out pc))
                            {
                                pc.SetValue(poco, pc.ChangeType(id));
                            }
                        }

                        // Assign the Version column
                        if (!string.IsNullOrEmpty(versionName))
                        {
                            PocoColumn pc;
                            if (pd.Columns.TryGetValue(versionName, out pc))
                            {
                                pc.SetValue(poco, pc.ChangeType(1));
                            }
                        }

                        return id;
                    }

                }
                finally
                {
                    CloseSharedConnection();
                }
            }
            catch (Exception x)
            {
                OnException(x);
                throw;
            }
        }

        // Insert an annotated poco object
        public object Insert(object poco)
        {
            var pd = PocoData.ForType(poco.GetType());
            return Insert(pd.TableInfo.TableName, pd.TableInfo.PrimaryKey, pd.TableInfo.AutoIncrement, poco);
        }

        public int Update(string tableName, string primaryKeyName, object poco, object primaryKeyValue)
        {
            return Update(tableName, primaryKeyName, poco, primaryKeyValue, null);
        }

        // Update a record with values from a poco.  primary key value can be either supplied or read from the poco
        public int Update(string tableName, string primaryKeyName, object poco, object primaryKeyValue, IEnumerable<string> columns)
        {
            if (columns != null && !columns.Any())
                return 0;

            var sb = new StringBuilder();
            var index = 0;
            var rawvalues = new List<object>();
            var pd = PocoData.ForObject(poco,primaryKeyName);
            string versionName = null;
            object versionValue = null;

            var primaryKeyValuePairs = GetPrimaryKeyValues(primaryKeyName, primaryKeyValue);

            foreach (var i in pd.Columns)
            {
                // Don't update the primary key, but grab the value if we don't have it
                if (primaryKeyValue == null && primaryKeyValuePairs.ContainsKey(i.Key))
                {
                    primaryKeyValuePairs[i.Key] = i.Value.GetValue(poco);
                    continue;
                }

                // Dont update result only columns
                if (i.Value.ResultColumn)
                    continue;

                if (!i.Value.VersionColumn && columns != null && !columns.Contains(i.Value.ColumnName, StringComparer.OrdinalIgnoreCase))
                    continue;

                object value = i.Value.GetValue(poco);
                if (Mapper != null)
                {
                    var converter = Mapper.GetToDbConverter(i.Value.ColumnType, i.Value.PropertyInfo.PropertyType);
                    if (converter != null)
                        value = converter(value);
                }

                if (i.Value.VersionColumn)
                {
                    versionName = i.Key;
                    versionValue = value;
                    value = Convert.ToInt64(value) + 1;
                }

                // Build the sql
                if (index > 0)
                    sb.Append(", ");
                sb.AppendFormat("{0} = @{1}", _dbType.EscapeSqlIdentifier(i.Key), index++);

                rawvalues.Add(value);
            }

            if (columns != null && columns.Any() && sb.Length == 0)
                throw new ArgumentException("There were no columns in the columns list that matched your table", "columns");
                        
            var sql = string.Format("UPDATE {0} SET {1} WHERE {2}", _dbType.EscapeTableName(tableName), sb, BuildPrimaryKeySql(primaryKeyValuePairs, ref index));

            rawvalues.AddRange(primaryKeyValuePairs.Select(keyValue => keyValue.Value));

            if (!string.IsNullOrEmpty(versionName))
            {
                sql += string.Format(" AND {0} = @{1}", _dbType.EscapeSqlIdentifier(versionName), index++);
                rawvalues.Add(versionValue); 
            }

            var result = Execute(sql, rawvalues.ToArray());

            if (result == 0 && !string.IsNullOrEmpty(versionName) && VersionException == VersionExceptionHandling.Exception)
            {
                throw new DBConcurrencyException(string.Format("A Concurrency update occurred in table '{0}' for primary key value(s) = '{1}' and version = '{2}'", tableName, string.Join(",", primaryKeyValuePairs.Values.Select(x => x.ToString()).ToArray()), versionValue));
            }

            // Set Version
            if (!string.IsNullOrEmpty(versionName))
            {
                PocoColumn pc;
                if (pd.Columns.TryGetValue(versionName, out pc))
                {
                    pc.SetValue(poco, Convert.ChangeType(Convert.ToInt64(versionValue) + 1, pc.PropertyInfo.PropertyType));
                }
            }

            return result;
        }

        private string BuildPrimaryKeySql(Dictionary<string, object> primaryKeyValuePair, ref int index)
        {
            var tempIndex = index;
            index += primaryKeyValuePair.Count;
            return string.Join(" AND ", primaryKeyValuePair.Select((x, i) => string.Format("{0} = @{1}", _dbType.EscapeSqlIdentifier(x.Key), tempIndex + i)).ToArray());
        }

        private Dictionary<string, object> GetPrimaryKeyValues(string primaryKeyName, object primaryKeyValue) 
        {
            Dictionary<string, object> primaryKeyValues;

            var multiplePrimaryKeysNames = primaryKeyName.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).ToArray();
            if (primaryKeyValue != null)
            {
                if (multiplePrimaryKeysNames.Length == 1)
                    primaryKeyValues = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase) { { primaryKeyName, primaryKeyValue } };
                else
                    primaryKeyValues = multiplePrimaryKeysNames.ToDictionary(x => x,
                                                                             x => primaryKeyValue.GetType().GetProperties()
                                                                                      .Where(y => string.Equals(x, y.Name, StringComparison.OrdinalIgnoreCase))
                                                                                      .Single().GetValue(primaryKeyValue, null), StringComparer.OrdinalIgnoreCase);
            }
            else
            {
                primaryKeyValues = multiplePrimaryKeysNames.ToDictionary(x => x, x => (object)null, StringComparer.OrdinalIgnoreCase);
            }
            return primaryKeyValues;
        }

        public int Update(string tableName, string primaryKeyName, object poco)
        {
            return Update(tableName, primaryKeyName, poco, null);
        }

        public int Update(string tableName, string primaryKeyName, object poco, IEnumerable<string> columns)
        {
            return Update(tableName, primaryKeyName, poco, null, columns);
        }

        public int Update(object poco, IEnumerable<string> columns)
        {
            return Update(poco, null, columns);
        }

        public int Update(object poco)
        {
            return Update(poco, null, null);
        }

        public int Update(object poco, object primaryKeyValue)
        {
            return Update(poco, primaryKeyValue, null);
        }

        public int Update(object poco, object primaryKeyValue, IEnumerable<string> columns)
        {
            var pd = PocoData.ForType(poco.GetType());
            return Update(pd.TableInfo.TableName, pd.TableInfo.PrimaryKey, poco, primaryKeyValue, columns);
        }

        public int Update<T>(string sql, params object[] args)
        {
            var pd = PocoData.ForType(typeof(T));
            return Execute(string.Format("UPDATE {0} {1}", _dbType.EscapeTableName(pd.TableInfo.TableName), sql), args);
        }

        public int Update<T>(Sql sql)
        {
            var pd = PocoData.ForType(typeof(T));
            return Execute(new Sql(string.Format("UPDATE {0}", _dbType.EscapeTableName(pd.TableInfo.TableName))).Append(sql));
        }

        public int Delete(string tableName, string primaryKeyName, object poco)
        {
            return Delete(tableName, primaryKeyName, poco, null);
        }

        public int Delete(string tableName, string primaryKeyName, object poco, object primaryKeyValue)
        {
            var primaryKeyValuePairs = GetPrimaryKeyValues(primaryKeyName, primaryKeyValue);
            // If primary key value not specified, pick it up from the object
            if (primaryKeyValue == null)
            {
                var pd = PocoData.ForObject(poco, primaryKeyName);
                foreach (var i in pd.Columns)
                {
                    if (primaryKeyValuePairs.ContainsKey(i.Key))
                    {
                        primaryKeyValuePairs[i.Key] = i.Value.GetValue(poco);
                    }   
                }
            }

            // Do it
            var index = 0;
            var sql = string.Format("DELETE FROM {0} WHERE {1}", _dbType.EscapeTableName(tableName), BuildPrimaryKeySql(primaryKeyValuePairs, ref index));
            return Execute(sql, primaryKeyValuePairs.Select(x=>x.Value).ToArray());
        }

        public int Delete(object poco)
        {
            var pd = PocoData.ForType(poco.GetType());
            return Delete(pd.TableInfo.TableName, pd.TableInfo.PrimaryKey, poco);
        }

        public int Delete<T>(object pocoOrPrimaryKey)
        {
            if (pocoOrPrimaryKey.GetType() == typeof(T))
                return Delete(pocoOrPrimaryKey);
            var pd = PocoData.ForType(typeof(T));
            return Delete(pd.TableInfo.TableName, pd.TableInfo.PrimaryKey, null, pocoOrPrimaryKey);
        }

        public int Delete<T>(string sql, params object[] args)
        {
            var pd = PocoData.ForType(typeof(T));
            return Execute(string.Format("DELETE FROM {0} {1}", _dbType.EscapeTableName(pd.TableInfo.TableName), sql), args);
        }

        public int Delete<T>(Sql sql)
        {
            var pd = PocoData.ForType(typeof(T));
            return Execute(new Sql(string.Format("DELETE FROM {0}", _dbType.EscapeTableName(pd.TableInfo.TableName))).Append(sql));
        }

        // Check if a poco represents a new record
        public bool IsNew(string primaryKeyName, object poco)
        {
            var pd = PocoData.ForObject(poco, primaryKeyName);
            object pk;
            PocoColumn pc;
            if (pd.Columns.TryGetValue(primaryKeyName, out pc))
            {
                pk = pc.GetValue(poco);
            }
#if !POCO_NO_DYNAMIC
            else if (poco.GetType() == typeof(System.Dynamic.ExpandoObject))
            {
                return true;
            }
#endif
            else
            {
                var pi = poco.GetType().GetProperty(primaryKeyName);
                if (pi == null)
                    throw new ArgumentException(string.Format("The object doesn't have a property matching the primary key column name '{0}'", primaryKeyName));
                pk = pi.GetValue(poco, null);
            }

            if (pk == null)
                return true;

            var type = pk.GetType();

            if (type.IsValueType)
            {
                // Common primary key types
                if (type == typeof(long))
                    return (long)pk == default(long);
                else if (type == typeof(ulong))
                    return (ulong)pk == default(ulong);
                else if (type == typeof(int))
                    return (int)pk == default(int);
                else if (type == typeof(uint))
                    return (uint)pk == default(uint);
                else if (type == typeof(Guid))
                    return (Guid)pk == default(Guid);

                // Create a default instance and compare
                return pk == Activator.CreateInstance(pk.GetType());
            }
            else
            {
                return pk == null;
            }
        }

        public bool IsNew(object poco)
        {
            var pd = PocoData.ForType(poco.GetType());
            if (!pd.TableInfo.AutoIncrement)
                throw new InvalidOperationException("IsNew() and Save() are only supported on tables with auto-increment/identity primary key columns");
            return IsNew(pd.TableInfo.PrimaryKey, poco);
        }

        // Insert new record or Update existing record
        public void Save(string tableName, string primaryKeyName, object poco)
        {
            if (IsNew(primaryKeyName, poco))
            {
                Insert(tableName, primaryKeyName, true, poco);
            }
            else
            {
                Update(tableName, primaryKeyName, poco);
            }
        }

        public void Save(object poco)
        {
            var pd = PocoData.ForType(poco.GetType());
            Save(pd.TableInfo.TableName, pd.TableInfo.PrimaryKey, poco);
        }

        public int CommandTimeout { get; set; }
        public int OneTimeCommandTimeout { get; set; }

        void DoPreExecute(IDbCommand cmd)
        {
            // Setup command timeout
            if (OneTimeCommandTimeout != 0)
            {
                cmd.CommandTimeout = OneTimeCommandTimeout;
                OneTimeCommandTimeout = 0;
            }
            else if (CommandTimeout!=0)
            {
                cmd.CommandTimeout = CommandTimeout;
            }
            
            // Call hook
            OnExecutingCommand(cmd);

            // Save it
            _lastSql = cmd.CommandText;
            _lastArgs = (from IDataParameter parameter in cmd.Parameters select parameter.Value).ToArray();
        }

        public string LastSQL { get { return _lastSql; } }
        public object[] LastArgs { get { return _lastArgs; } }
        public string LastCommand
        {
            get { return FormatCommand(_lastSql, _lastArgs); }
        }

        public string FormatCommand(IDbCommand cmd)
        {
            return FormatCommand(cmd.CommandText, (from IDataParameter parameter in cmd.Parameters select parameter.Value).ToArray());
        }

        public string FormatCommand(string sql, object[] args)
        {
            var sb = new StringBuilder();
            if (sql == null)
                return "";
            sb.Append(sql);
            if (args != null && args.Length > 0)
            {
                sb.Append("\n");
                for (int i = 0; i < args.Length; i++)
                {
                    sb.AppendFormat("\t -> {0}{1} [{2}] = \"{3}\"\n", _paramPrefix, i, args[i].GetType().Name, args[i]);
                }
                sb.Remove(sb.Length - 1, 1);
            }
            return sb.ToString();
        }

        public static IMapper Mapper { get; set; }

        public static Func<Type, PocoData> PocoDataFactory = type => new PocoData(type);

        // Member variables
        string _connectionString;
        string _providerName;
        DbProviderFactory _factory;
        IDbConnection _sharedConnection;
        IDbTransaction _transaction;
        int _sharedConnectionDepth;
        int _transactionDepth;
        bool _transactionCancelled;
        string _lastSql;
        object[] _lastArgs;
        string _paramPrefix = "@";
        VersionExceptionHandling _versionException = VersionExceptionHandling.Ignore;

        internal void ExecuteNonQueryHelper(IDbCommand cmd)
        {
            DoPreExecute(cmd);
            cmd.ExecuteNonQuery();
            OnExecutedCommand(cmd);
        }

        internal object ExecuteScalarHelper(IDbCommand cmd)
        {
            DoPreExecute(cmd);
            object r = cmd.ExecuteScalar();
            OnExecutedCommand(cmd);
            return r;
        }
    }
}