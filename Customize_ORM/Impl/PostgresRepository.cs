using Customize_ORM.Extensions;
using Customize_ORM.Extensions.Sql;
using Customize_ORM.Interfaces;
using Customize_ORM.Interfaces.Sql;
using Microsoft.Extensions.Logging;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;

namespace Customize_ORM.Impl
{
    /// <summary>
    /// PostgreSQL 数据库操作实现类：实现统一的 <see cref="IRepository{T}"/> 契约，
    /// 方法签名仅使用 ADO.NET 抽象类型，PostgreSQL 专属能力（COPY 二进制导入、数组参数）由参数工厂与构建器承担。
    /// </summary>
    /// <typeparam name="T">实体类型</typeparam>
    public class PostgresRepository<T> : IRepository<T> where T : class
    {
        private sealed class ReaderMappingException(string entityName, Exception innerException)
            : Exception($"Failed to map {entityName} from database reader.", innerException);

        protected static readonly EntityMapping<T> _mapping = ObjectMapping<T>.MappingForInsert;
        protected static readonly EntityMapping<T> _updateMapping = ObjectMapping<T>.MappingForUpdate;
        protected static readonly ObjectMapping<T>.DRowFactory _rowFactory = ObjectMapping<T>.RowFactory;
        protected static readonly Func<DbDataReader, T> _entityReader = EntityDataReaderMapper<T>.Create(_mapping);
        protected readonly string _tableName;
        protected readonly IDbConnectionFactory _connectionFactory;
        protected readonly IQueryDialect _dialect;
        protected readonly ILogger _logger;
        protected readonly bool _hasPartitionKey;
        protected readonly Func<T, object?>? _partitionKeyGetter;

        public PostgresRepository(IDbConnectionFactory connectionFactory, ILogger<PostgresRepository<T>> logger)
        {
            ArgumentNullException.ThrowIfNull(connectionFactory);
            _tableName = _mapping.TableName;
            _connectionFactory = connectionFactory;
            _dialect = SqlDialect.For(Enums.DBType.PostgreSQL);
            _logger = logger;
            _hasPartitionKey = !string.IsNullOrWhiteSpace(_mapping.PartitionKey);
            if (_hasPartitionKey && _mapping.ValueGetters.TryGetValue(_mapping.PartitionKey, out var getter))
            {
                _partitionKeyGetter = getter;
            }
        }

        /// <summary>
        /// 返回当前实体对应的查询构建器（可指定分区后缀表名）。
        /// </summary>
        /// <param name="tableName">分区后缀表名；空则使用映射表名。</param>
        private protected static IQuerySqlBuilder<T> CreateQueryBuilder(string? tableName = null)
            => new PostgreSqlQuerySqlBuilder<T>(_mapping, tableName);

        /// <summary>
        /// 获取 PostgreSQL 连接（已打开，由调用方负责释放）。
        /// </summary>
        public async Task<DbConnection> GetConnectionAsync(CancellationToken token = default)
        {
            var connection = await _connectionFactory.OpenConnectionAsync(token);
            ArgumentNullException.ThrowIfNull(connection, nameof(connection));
            return connection;
        }

        private static async Task<List<TResult>> ReadAllAsync<TResult>(
            DbConnection connection,
            string sql,
            IReadOnlyList<DbParameter>? parameters,
            Func<DbDataReader, TResult> map,
            CancellationToken token,
            DbTransaction? transaction = null)
        {
            await using var command = DbCommandExecution.CreateCommand(connection, sql, parameters, transaction);
            await using var reader = await command.ExecuteReaderAsync(token);
            var results = new List<TResult>();
            while (await reader.ReadAsync(token))
            {
                results.Add(map(reader));
            }

            return results;
        }

        private static async Task<TResult?> ReadFirstOrDefaultAsync<TResult>(
            DbConnection connection,
            string sql,
            IReadOnlyList<DbParameter>? parameters,
            CancellationToken token,
            CommandBehavior behavior = CommandBehavior.SingleRow)
        {
            await using var command = DbCommandExecution.CreateCommand(connection, sql, parameters);
            await using var reader = await command.ExecuteReaderAsync(behavior, token);
            if (!await reader.ReadAsync(token))
            {
                return default;
            }

            return DbRecordMapper.MapSingle<TResult>(reader);
        }

        #region 基础查询
        public async ValueTask<long> GetCountAsync(Expression<Func<T, bool>>? expression, CancellationToken token)
        {
            try
            {
                await using var connection = await _connectionFactory.OpenConnectionAsync(token);
                var queryBuilder = CreateQueryBuilder();
                var sqlExpr = new SqlExpression<T>(_mapping, Enums.DBType.PostgreSQL);
                if (expression != null)
                {
                    sqlExpr.Where(expression);
                }

                var (whereClause, parameters) = sqlExpr.BuildWhereClause();
                var sql = queryBuilder.ApplyWhere(queryBuilder.Count(), whereClause);

                var result = await DbCommandExecution.ExecuteScalarAsync(connection, sql, parameters, token);
                return Convert.ToInt64(result ?? 0);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "{fnName} {EntityName}", nameof(GetCountAsync), typeof(T).Name);
            }
            return 0;
        }


        public async ValueTask<TValue?> QueryFirstOrDefaultAsync<TValue>(string sql, CancellationToken token, params DbParameter[] parameters)
        {
            try
            {
                await using var connection = await _connectionFactory.OpenConnectionAsync();
                if (string.IsNullOrWhiteSpace(sql))
                    throw new ArgumentException($"{nameof(QueryFirstOrDefaultAsync)} {typeof(T).Name} SQL query cannot be empty or whitespace.", nameof(sql));

                return await ReadFirstOrDefaultAsync<TValue>(connection, sql, parameters, token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "{fnName} {entityName} sql=>{sqlstr}", nameof(QueryFirstOrDefaultAsync), typeof(T).Name, sql);
            }
            return default;
        }

        public async Task<int> ExecuteNonQueryAsync(string sql, CancellationToken token, params DbParameter[] parameters)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(sql))
                    throw new ArgumentException($"{nameof(ExecuteNonQueryAsync)} {typeof(T).Name} SQL query cannot be empty or whitespace.", nameof(sql));
                await using var connection = await _connectionFactory.OpenConnectionAsync();
                return await DbCommandExecution.ExecuteNonQueryAsync(connection, sql, parameters, token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "{fnName} {entityName} sql=>{sqlstr}", nameof(ExecuteNonQueryAsync), typeof(T).Name, sql);
            }
            return 0;
        }

        public async ValueTask<IEnumerable<TValue>> QueryAsync<TValue>(string sql, CancellationToken token, params DbParameter[] parameters)
        {
            try
            {
                await using var connection = await _connectionFactory.OpenConnectionAsync();
                if (string.IsNullOrWhiteSpace(sql))
                    throw new ArgumentException($"{nameof(QueryAsync)} {typeof(T).Name} SQL query cannot be empty or whitespace.", nameof(sql));

                return await ReadAllAsync(connection, sql, parameters, DbRecordMapper.MapSingle<TValue>, token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "{fnName} {entityName} sql=>{sqlstr}", nameof(QueryAsync), typeof(T).Name, sql);
            }
            return [];
        }

        public async ValueTask<List<T>> QueryAndDeleteAsync(string sql, CancellationToken token, string tableName, params DbParameter[] parameters)
        {
            await using var connection = await _connectionFactory.OpenConnectionAsync(token);
            ArgumentNullException.ThrowIfNull(connection, nameof(connection));
            await using var transaction = await connection.BeginTransactionAsync(token);
            try
            {
                if (string.IsNullOrWhiteSpace(sql))
                    throw new ArgumentException($"{nameof(QueryAsync)} {typeof(T).Name} SQL query cannot be empty or whitespace.", nameof(sql));
                List<T> dataArry = await ReadAllAsync(connection, sql, parameters, MapFromReader, token, transaction);
                if (dataArry.Count != 0)
                {
                    // 主键集合删除语句的语法差异（数组参数 vs IN 列表）收敛到方言层
                    var keyColumn = _mapping.Key;
                    var idGetter = _mapping.ValueGetters[keyColumn];
                    List<object?> ids = [];
                    foreach (var item in dataArry)
                    {
                        var idValue = idGetter(item);
                        if (idValue != null)
                        {
                            ids.Add(idValue);
                        }
                    }

                    var deleteSql = _dialect.BuildDeleteByKeysSql(
                        tableName,
                        keyColumn,
                        ids,
                        GetPropertyType(keyColumn),
                        out var deleteParameters);
                    await using var deleteCommand = DbCommandExecution.CreateCommand(
                        connection,
                        deleteSql,
                        deleteParameters,
                        transaction);
                    await deleteCommand.ExecuteNonQueryAsync(token);
                    await transaction.CommitAsync(token);
                    return dataArry;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await SafeRollbackAsync(transaction, token);
                _logger.LogError(ex, "{fnName} {entityName} sql=>{sqlstr}", nameof(QueryAndDeleteAsync), typeof(T).Name, sql);
            }

            return [];
        }

        public async Task<DbDataReader> QueryAsync(string sql, CancellationToken token, params DbParameter[] parameters)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(sql))
                    throw new ArgumentException($"{nameof(QueryAsync)} {typeof(T).Name} SQL query cannot be empty or whitespace.", nameof(sql));

                var connection = await _connectionFactory.OpenConnectionAsync(token);
                return await DbCommandExecution.ExecuteOwnedReaderAsync(connection, sql, parameters, token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "{fnName} {entityName} sql=>{sqlstr}", nameof(QueryAsync), typeof(T).Name, sql);
            }
            return new DataTable().CreateDataReader();
        }

        public async ValueTask<IEnumerable<T>> QueryAsync(Expression<Func<T, bool>>? expression = null, CancellationToken token = default)
        {
            try
            {
                await using var connection = await _connectionFactory.OpenConnectionAsync(token);
                ArgumentNullException.ThrowIfNull(connection);

                var sqlExpr = new SqlExpression<T>(_mapping, Enums.DBType.PostgreSQL).Select();
                if (expression != null)
                {
                    sqlExpr.Where(expression);
                }

                var sql = sqlExpr.ToSql(out List<DbParameter> parameters);
                await using var cmd = DbCommandExecution.CreateCommand(connection, sql, parameters);

                var results = new List<T>();
                await using var reader = await cmd.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token))
                {
                    results.Add(MapFromReader(reader));
                }

                return results;
            }
            catch (ReaderMappingException)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "{fnName} {EntityName}", nameof(QueryAsync), typeof(T).Name);
                throw;
            }
        }

        /// <summary>
        /// 流式读取：PostgreSQL 通过服务端游标按 <paramref name="batchSize"/> 分批 FETCH，严格遵守批大小。
        /// </summary>
        public async IAsyncEnumerable<T?> QueryStreamAsync(DbConnection connection, Expression<Func<T, bool>>? expression = null, int batchSize = 100, [EnumeratorCancellation] CancellationToken token = default)
        {
            ArgumentNullException.ThrowIfNull(connection);
            if (batchSize <= 0)
            {
                batchSize = DefaultStreamBatchSize;
            }

            //开始事务（游标必须在事务中使用）
            await using var transaction = await connection.BeginTransactionAsync(token);

            //声明一个唯一游标名（避免并发冲突）
            string cursorName = $"cursor_{Guid.NewGuid():N}";
            var sqlExpression = new SqlExpression<T>(_mapping, Enums.DBType.PostgreSQL);
            sqlExpression.Select().Where(expression);
            string sql = sqlExpression.ToSql(out List<DbParameter> parameters);
            // 创建声明游标的命令
            string declareSql = $"DECLARE {cursorName} NO SCROLL CURSOR FOR {sql}";
            await using (var declareCmd = DbCommandExecution.CreateCommand(connection, declareSql, parameters, transaction))
            {
                await declareCmd.ExecuteNonQueryAsync(token);
            }

            try
            {
                bool hasMore = true;
                while (hasMore && !token.IsCancellationRequested)
                {
                    // 获取下一批数据
                    string fetchSql = $"FETCH FORWARD {batchSize} FROM {cursorName}";
                    await using var fetchCmd = DbCommandExecution.CreateCommand(connection, fetchSql, null, transaction);

                    // 使用 CommandBehavior.SequentialAccess 同样可以提高读取效率
                    await using var reader = await fetchCmd.ExecuteReaderAsync(CommandBehavior.SequentialAccess, token);

                    bool anyRow = false;
                    while (await reader.ReadAsync(token))
                    {
                        anyRow = true;
                        yield return MapFromReader(reader);
                    }

                    // 如果这一次没有读到任何行，说明游标已经结束
                    if (!anyRow)
                        hasMore = false;
                }
            }
            finally
            {
                //关闭游标
                string closeSql = $"CLOSE {cursorName}";
                await using var closeCmd = DbCommandExecution.CreateCommand(connection, closeSql, null, transaction);
                await closeCmd.ExecuteNonQueryAsync(token);

                // 提交事务（或者回滚，这里我们只读所以提交也行）
                await transaction.CommitAsync(token);
            }
        }

        /// <summary>
        /// 流式读取的默认批大小（调用方传入非正数时使用）。
        /// </summary>
        protected const int DefaultStreamBatchSize = 100;

        private T MapFromReader(DbDataReader reader)
        {
            try
            {
                return _entityReader(reader);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "{fnName} {EntityName}", nameof(MapFromReader), typeof(T).Name);
                throw new ReaderMappingException(typeof(T).Name, ex);
            }
        }

        public async ValueTask<T?> QueryFirstOrDefaultAsync(Expression<Func<T, bool>>? expression = null, CancellationToken token = default)
        {
            try
            {
                await using var connection = await _connectionFactory.OpenConnectionAsync(token);
                ArgumentNullException.ThrowIfNull(connection);

                var sqlExpression = new SqlExpression<T>(_mapping, Enums.DBType.PostgreSQL).Select().First();
                if (expression != null)
                {
                    sqlExpression.Where(expression);
                }

                string sql = sqlExpression.ToSql(out List<DbParameter> parameters);
                await using var cmd = DbCommandExecution.CreateCommand(connection, sql, parameters);

                await using var reader = await cmd.ExecuteReaderAsync(CommandBehavior.SingleRow, token);
                if (await reader.ReadAsync(token))
                {
                    return MapFromReader(reader);
                }
            }
            catch (ReaderMappingException)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "{fnName} {EntityName}", nameof(QueryFirstOrDefaultAsync), typeof(T).Name);
                throw;
            }
            return default;
        }

        public async ValueTask<bool> UpdateAsync(T entity, Expression<Func<T, bool>> expression)
        {
            string sql = string.Empty;
            try
            {
                string tableName = GetPartition(entity);
                var queryBuilder = CreateQueryBuilder(tableName);
                await using var connection = await _connectionFactory.OpenConnectionAsync();
                var sqlExpr = new SqlExpression<T>(_mapping, Enums.DBType.PostgreSQL).WithTableAlias(tableName);
                sqlExpr.Where(expression);
                var (whereClause, parameters) = sqlExpr.BuildWhereClause();
                if (string.IsNullOrWhiteSpace(whereClause))
                {
                    throw new InvalidOperationException($"{nameof(UpdateAsync)} {typeof(T).Name} requires a predicate.");
                }

                var valueGetters = _updateMapping.ValueGetters;
                foreach (var col in _updateMapping.AllColumns)
                {
                    if (valueGetters.TryGetValue(col, out var getter))
                    {
                        parameters.Add(PostgreSqlParameterFactory.Create($"{_dialect.ParameterPrefix}{col}", getter(entity), GetPropertyType(col)));
                    }
                }
                sql = queryBuilder.ApplyWhere(queryBuilder.Update(_updateMapping.AllColumns), whereClause);
                return await DbCommandExecution.ExecuteNonQueryAsync(connection, sql, parameters, CancellationToken.None) > 0;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "{fnName} {EntityName} sql=>{sqlstr}", nameof(UpdateAsync), typeof(T).Name, sql);
            }
            return false;
        }

        public async ValueTask<bool> UpdateAsync(T entity, ReadOnlyMemory<string> columns, CancellationToken token)
        {
            string sql = string.Empty;
            try
            {
                string tableName = GetPartition(entity);
                var queryBuilder = CreateQueryBuilder(tableName);
                await using var connection = await _connectionFactory.OpenConnectionAsync();
                ArgumentNullException.ThrowIfNull(connection, nameof(connection));
                var valueGetters = _mapping.ValueGetters;
                var updateColumns = columns.ToArray();
                string[] allColumns = new string[columns.Length + 1];
                allColumns[0] = _mapping.Key;
                columns.Span.CopyTo(allColumns.AsSpan(1));
                sql = queryBuilder.ApplyWhere(queryBuilder.Update(updateColumns), queryBuilder.WhereByKey());
                var parameters = new List<DbParameter>(allColumns.Length);
                foreach (var column in allColumns)
                {
                    if (valueGetters.TryGetValue(column, out var getter))
                    {
                        parameters.Add(PostgreSqlParameterFactory.Create($"{_dialect.ParameterPrefix}{column}", getter(entity), GetPropertyType(column)));
                    }
                }

                return await DbCommandExecution.ExecuteNonQueryAsync(connection, sql, parameters, token) > 0;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "{fnName} {EntityName} sql=>{sqlstr}", nameof(UpdateAsync), typeof(T).Name, sql);
            }
            return false;
        }
        #endregion

        #region 单条插入
        public async Task<bool> InsertAsync(T entity, CancellationToken token, string partition = "")
        {
            string sql = string.Empty;
            try
            {
                ArgumentNullException.ThrowIfNull(entity, nameof(entity));
                Stopwatch sw = Stopwatch.StartNew();
                string tableName = _tableName;
                if (!string.IsNullOrWhiteSpace(partition))
                {
                    tableName = _tableName + "_" + partition;
                }
                sql = new PostgreSqlQuerySqlBuilder<T>(_mapping, tableName).CopyBinaryImport(_mapping.AllColumns);
                await using var dbConnection = await _connectionFactory.OpenConnectionAsync();
                ArgumentNullException.ThrowIfNull(dbConnection, nameof(dbConnection));
                // COPY 二进制导入是 Npgsql 专属能力，无法用 ADO.NET 抽象表达，故在此处收敛驱动依赖
                if (dbConnection is not Npgsql.NpgsqlConnection connection)
                {
                    throw new NotSupportedException(
                        $"{nameof(InsertAsync)} requires an NpgsqlConnection for COPY binary import, but got {dbConnection.GetType().Name}.");
                }

                await using var writer = await connection.BeginBinaryImportAsync(sql, CancellationToken.None);
                var values = _rowFactory(entity);
                await writer.WriteRowAsync(CancellationToken.None, values).ConfigureAwait(false);
                var writeCount = await writer.CompleteAsync().ConfigureAwait(false);
                _logger.LogInformation("{fnName} {EntityName} Times{Elapsed}ms", nameof(InsertAsync), typeof(T).Name, sw.ElapsedMilliseconds);
                return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "{fnName} {entityName} sql=>{sqlstr}", nameof(InsertAsync), typeof(T).Name, sql);
            }
            return false;
        }
        #endregion

        #region 对象映射 
        /// <summary>
        /// 解析实体对应的物理表名：有分区键时追加按小时截断的分区后缀。
        /// </summary>
        /// <param name="entity">数据实体</param>
        protected string GetPartition(T entity)
        {
            if (entity is null)
            {
                throw new ArgumentNullException($"{nameof(GetPartition)} {typeof(T).Name}", "entity is null");
            }

            DateTime hourKey;

            // 如果没有分区键，直接使用表名
            if (!_hasPartitionKey || _partitionKeyGetter == null)
            {
                return _tableName;
            }
            else
            {
                hourKey = GetHourKeyFromPartitionValue(_partitionKeyGetter(entity));
            }

            // 使用缓存的分区名称，避免重复创建字符串
            return _tableName + "_" + hourKey.ToString("yyyyMMddHH");
        }
        /// <summary>
        /// 生成分区名称
        /// </summary>
        /// <param name="partitionValue">分区字段</param>
        /// <returns></returns>
        private static DateTime GetHourKeyFromPartitionValue(object? partitionValue)
        {
            return partitionValue switch
            {
                DateTime dateTime => dateTime.TruncateToHour(),
                string stringValue when DateTime.TryParse(stringValue, out var parsedDate) => parsedDate.TruncateToHour(),
                _ => DateTime.Now.TruncateToHour()
            };
        }

        /// <summary>
        /// 回滚事务并吞掉回滚自身的异常，避免掩盖原始业务异常。
        /// </summary>
        /// <param name="transaction">待回滚事务</param>
        /// <param name="token">取消令牌</param>
        protected static async Task SafeRollbackAsync(DbTransaction transaction, CancellationToken token)
        {
            try
            {
                await transaction.RollbackAsync(token);
            }
            catch
            {
                // 回滚失败无法补救，保留原始异常语义
            }
        }

        #endregion

        #region pg相关映射

        /// <summary>
        /// C#数据类型转pgsql数组类型
        /// </summary>
        /// <param name="elementType"></param>
        /// <returns></returns>
        protected static string GetPostgreSqlArrayType(Type elementType)
        {
            return PostgreSqlTypeMapper.GetPostgreSqlArrayType(elementType);
        }
        // 辅助方法：获取属性的CLR类型
        protected Type GetPropertyType(string columnName)
        {
            if (_mapping.PropertyMap.TryGetValue(columnName, out var propertyInfo))
            {
                return propertyInfo.PropertyType;
            }

            return typeof(object);
        }
        #endregion

    }
}
