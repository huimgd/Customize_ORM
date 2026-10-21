using Customize_ORM.Extensions;
using Customize_ORM.Extensions.Sql;
using Customize_ORM.Interfaces;
using Customize_ORM.Interfaces.ConnectionFactory;
using Microsoft.Extensions.Logging;
using System.Data;
using System.Data.Common;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using XuguClient;

namespace Customize_ORM.Impl
{
    /// <summary>
    /// 虚谷数据库仓储基础实现：表达式查询、参数化写入与 ADO.NET 抽象执行。
    /// </summary>
    public class XuguRepository<T> : IXuguRepository<T> where T : class
    {
        private sealed class ReaderMappingException(string entityName, Exception innerException)
            : Exception($"Failed to map {entityName} from database reader.", innerException);

        protected static readonly EntityMapping<T> _mapping = ObjectMapping<T>.MappingForInsert;
        protected static readonly EntityMapping<T> _updateMapping = ObjectMapping<T>.MappingForUpdate;
        protected static readonly Func<DbDataReader, T> _entityReader = EntityDataReaderMapper<T>.Create(_mapping);
        protected readonly string _tableName;
        protected readonly IXuguConnectionFactory _xugu;
        protected readonly ILogger _logger;
        protected readonly bool _hasPartitionKey;
        protected readonly Func<T, object?>? _partitionKeyGetter;

        /// <summary>
        /// 表达式查询生成的占位符统一走虚谷方言（:name），主键列表删除占位符从 0 起编号。
        /// </summary>
        public XuguRepository(IXuguConnectionFactory xugu, ILogger<XuguRepository<T>> logger)
        {
            _tableName = _mapping.TableName;
            _xugu = xugu;
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
        private static XuguQuerySqlBuilder<T> CreateQueryBuilder(string? tableName = null)
            => new(_mapping, tableName);

        /// <summary>
        /// 获取虚谷数据库连接。
        /// </summary>
        public async Task<DbConnection> GetConnectionAsync(CancellationToken token = default)
        {
            var connection = await _xugu.GetConnectionAsync(token);
            ArgumentNullException.ThrowIfNull(connection, nameof(connection));
            return connection;
        }

        /// <summary>
        /// 映射读取器为实体的统一入口，映射异常包装成可识别类型向上抛出。
        /// </summary>
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

        /// <summary>
        /// 获取属性 CLR 类型；映射外的列按 object 处理（不参与类型化参数生成）。
        /// </summary>
        protected Type GetPropertyType(string columnName)
        {
            return _mapping.PropertyMap.TryGetValue(columnName, out var propertyInfo)
                ? propertyInfo.PropertyType
                : typeof(object);
        }

        #region 查询
        public async ValueTask<long> GetCountAsync(Expression<Func<T, bool>>? expression, CancellationToken token)
        {
            try
            {
                await using var connection = await _xugu.GetConnectionAsync(token);
                var queryBuilder = CreateQueryBuilder();
                var sqlExpr = new SqlExpression<T>(_mapping, Enums.DBType.Xugu);
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
                _logger.LogError(ex, $"{nameof(GetCountAsync)} {typeof(T).Name}");
            }
            return 0;
        }

        public async ValueTask<TValue?> QueryFirstOrDefaultAsync<TValue>(string sql, CancellationToken token, params DbParameter[] parameters)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(sql))
                    throw new ArgumentException($"{nameof(QueryFirstOrDefaultAsync)} {typeof(T).Name} SQL query cannot be empty or whitespace.", nameof(sql));

                await using var connection = await _xugu.GetConnectionAsync(token);
                await using var command = DbCommandExecution.CreateCommand(connection, sql, parameters);
                await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, token);
                if (!await reader.ReadAsync(token))
                {
                    return default;
                }

                return DbRecordMapper.MapSingle<TValue>(reader);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "{fnName} {entityName} sql=>{sqlstr}", nameof(QueryFirstOrDefaultAsync), typeof(T).Name, sql);
            }
            return default;
        }

        public async ValueTask<T?> QueryFirstOrDefaultAsync(Expression<Func<T, bool>>? expression = null, CancellationToken token = default)
        {
            try
            {
                await using var connection = await _xugu.GetConnectionAsync(token);
                var sqlExpression = new SqlExpression<T>(_mapping, Enums.DBType.Xugu).Select().First();
                if (expression != null)
                {
                    sqlExpression.Where(expression);
                }

                string sql = sqlExpression.ToSql(out List<DbParameter> parameters);
                await using var command = DbCommandExecution.CreateCommand(connection, sql, parameters);
                await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, token);
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

        public async Task<DbDataReader> QueryAsync(string sql, CancellationToken token, params DbParameter[] parameters)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(sql))
                    throw new ArgumentException($"{nameof(QueryAsync)} {typeof(T).Name} SQL query cannot be empty or whitespace.", nameof(sql));

                var connection = await _xugu.GetConnectionAsync(token);
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
                await using var connection = await _xugu.GetConnectionAsync(token);
                var sqlExpr = new SqlExpression<T>(_mapping, Enums.DBType.Xugu).Select();
                if (expression != null)
                {
                    sqlExpr.Where(expression);
                }

                var sql = sqlExpr.ToSql(out List<DbParameter> parameters);
                await using var command = DbCommandExecution.CreateCommand(connection, sql, parameters);
                var results = new List<T>();
                await using var reader = await command.ExecuteReaderAsync(token);
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

        public async ValueTask<IEnumerable<TValue>> QueryAsync<TValue>(string sql, CancellationToken token, params DbParameter[] parameters)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(sql))
                    throw new ArgumentException($"{nameof(QueryAsync)} {typeof(T).Name} SQL query cannot be empty or whitespace.", nameof(sql));

                await using var connection = await _xugu.GetConnectionAsync(token);
                await using var command = DbCommandExecution.CreateCommand(connection, sql, parameters);
                await using var reader = await command.ExecuteReaderAsync(token);
                var results = new List<TValue>();
                while (await reader.ReadAsync(token))
                {
                    results.Add(DbRecordMapper.MapSingle<TValue>(reader));
                }

                return results;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "{fnName} {entityName} sql=>{sqlstr}", nameof(QueryAsync), typeof(T).Name, sql);
            }
            return [];
        }

        public async Task<int> ExecuteNonQueryAsync(string sql, CancellationToken token, params DbParameter[] parameters)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(sql))
                    throw new ArgumentException($"{nameof(ExecuteNonQueryAsync)} {typeof(T).Name} SQL query cannot be empty or whitespace.", nameof(sql));

                await using var connection = await _xugu.GetConnectionAsync(token);
                return await DbCommandExecution.ExecuteNonQueryAsync(connection, sql, parameters, token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "{fnName} {entityName} sql=>{sqlstr}", nameof(ExecuteNonQueryAsync), typeof(T).Name, sql);
            }
            return 0;
        }

        public async ValueTask<List<T>> QueryAndDeleteAsync(string sql, CancellationToken token, string tableName, params DbParameter[] parameters)
        {
            await using var connection = await _xugu.GetConnectionAsync(token);
            await using var transaction = await connection.BeginTransactionAsync(token);
            try
            {
                if (string.IsNullOrWhiteSpace(sql))
                    throw new ArgumentException($"{nameof(QueryAsync)} {typeof(T).Name} SQL query cannot be empty or whitespace.", nameof(sql));

                await using var queryCommand = DbCommandExecution.CreateCommand(connection, sql, parameters, transaction);
                var data = new List<T>();
                await using var reader = await queryCommand.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token))
                {
                    data.Add(MapFromReader(reader));
                }

                if (data.Count == 0)
                {
                    return data;
                }

                // 虚谷不支持 pg 的 id = ANY(数组)，主键列表以 :id0..:idN 命名参数展开为 IN
                var deleteSql = BuildIdsInClause(tableName, data, out var deleteParameters);
                await using var deleteCommand = DbCommandExecution.CreateCommand(connection, deleteSql, deleteParameters, transaction);
                await deleteCommand.ExecuteNonQueryAsync(token);
                await transaction.CommitAsync(token);
                return data;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await SafeRollbackAsync(transaction, token);
                _logger.LogError(ex, "{fnName} {entityName} sql=>{sqlstr}", nameof(QueryAndDeleteAsync), typeof(T).Name, sql);
            }

            return [];
        }
        #endregion

        #region 更新
        public async ValueTask<bool> UpdateAsync(T entity, Expression<Func<T, bool>>? expression)
        {
            string sql = string.Empty;
            try
            {
                string tableName = GetPartition(entity);
                var queryBuilder = CreateQueryBuilder(tableName);
                using var connection = await _xugu.GetConnectionAsync();
                var sqlExpr = new SqlExpression<T>(_mapping, Enums.DBType.Xugu).WithTableAlias(tableName);
                if (expression != null)
                {
                    sqlExpr.Where(expression);
                }

                var (whereClause, parameters) = sqlExpr.BuildWhereClause();
                if (string.IsNullOrWhiteSpace(whereClause))
                {
                    throw new InvalidOperationException($"{nameof(UpdateAsync)} {typeof(T).Name} requires a predicate.");
                }

                foreach (var col in _updateMapping.AllColumns)
                {
                    if (_updateMapping.ValueGetters.TryGetValue(col, out var getter))
                    {
                        parameters.Add(XuguParameterFactory.Create($":{col}", getter(entity), GetPropertyType(col)));
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
                using var connection = await _xugu.GetConnectionAsync();
                var valueGetters = _updateMapping.ValueGetters;
                var updateColumns = columns.ToArray();
                sql = queryBuilder.ApplyWhere(queryBuilder.Update(updateColumns), queryBuilder.WhereByKey());
                var parameters = new List<DbParameter>(updateColumns.Length + 1);
                parameters.Add(XuguParameterFactory.Create($":{_updateMapping.Key}", valueGetters[_updateMapping.Key](entity), GetPropertyType(_updateMapping.Key)));
                foreach (var column in updateColumns)
                {
                    if (valueGetters.TryGetValue(column, out var getter))
                    {
                        parameters.Add(XuguParameterFactory.Create($":{column}", getter(entity), GetPropertyType(column)));
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

        public async Task<bool> InsertAsync(T entity, CancellationToken token, string partition = "")
        {
            string sql = string.Empty;
            try
            {
                ArgumentNullException.ThrowIfNull(entity, nameof(entity));
                System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
                string tableName = _tableName;
                if (!string.IsNullOrWhiteSpace(partition))
                {
                    tableName = _tableName + "_" + partition;
                }

                var queryBuilder = CreateQueryBuilder(tableName);
                sql = queryBuilder.BuildSingleInsertSql(_mapping.AllColumns);
                using var connection = await _xugu.GetConnectionAsync();
                var parameters = new List<DbParameter>(_mapping.AllColumns.Length);
                foreach (var col in _mapping.AllColumns)
                {
                    if (_mapping.ValueGetters.TryGetValue(col, out var getter))
                    {
                        parameters.Add(XuguParameterFactory.Create($":{col}", getter(entity), GetPropertyType(col)));
                    }
                }

                var affected = await DbCommandExecution.ExecuteNonQueryAsync(connection, sql, parameters, token);
                _logger.LogInformation("{fnName} {entityName} affected={affected} Times={time}ms", nameof(InsertAsync), typeof(T).Name, affected, sw.ElapsedMilliseconds);
                return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "{fnName} {entityName} sql=>{sqlstr}", nameof(InsertAsync), typeof(T).Name, sql);
            }
            return false;
        }
        #endregion

        #region 流式查询
        public async IAsyncEnumerable<T?> QueryStreamAsync(DbConnection connection, Expression<Func<T, bool>>? expression = null, int batchSize = 100, [EnumeratorCancellation] CancellationToken token = default)
        {
            var sqlExpression = new SqlExpression<T>(_mapping, Enums.DBType.Xugu);
            sqlExpression.Select().Where(expression);
            string sql = sqlExpression.ToSql(out List<DbParameter> parameters);

            // 虚谷用服务端游标承载结果集，避免大结果集整体缓冲到本地
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            if (command is XGCommand xgCommand)
            {
                xgCommand.Use_Server_Cursor = true;
            }

            foreach (var param in parameters)
            {
                command.Parameters.Add(param);
            }

            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token) && !token.IsCancellationRequested)
            {
                yield return MapFromReader(reader);
            }
        }
        #endregion

        #region 辅助
        /// <summary>
        /// 生成主键 IN 删除语句并按实体集合的真实主键值绑定命名参数（:id0..:idN）。
        /// </summary>
        /// <param name="tableName">删除语句的目标表名。</param>
        /// <param name="entities">已查询出的待删除实体。</param>
        /// <param name="parameters">输出的绑定参数集合。</param>
        private string BuildIdsInClause(string tableName, List<T> entities, out List<DbParameter> parameters)
        {
            var keyColumn = _mapping.Key;
            var getter = _mapping.ValueGetters[keyColumn];
            var keyType = GetPropertyType(keyColumn);
            parameters = new List<DbParameter>(entities.Count);
            var index = 0;
            var placeholders = new List<string>(entities.Count);
            foreach (var entity in entities)
            {
                var parameterName = $":id{index}";
                parameters.Add(XuguParameterFactory.Create(parameterName, getter(entity), keyType));
                placeholders.Add(parameterName);
                index++;
            }

            return $"DELETE FROM {tableName} WHERE {keyColumn} IN ({string.Join(", ", placeholders)})";
        }

        /// <summary>
        /// 安全回滚：事务可能已中止或销毁，回滚失败不应掩盖原始错误。
        /// </summary>
        protected static async Task SafeRollbackAsync(DbTransaction transaction, CancellationToken token)
        {
            try
            {
                await transaction.RollbackAsync(token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 回滚失败仅在极端场景发生（事务已销毁），此时无法继续，保持静默不上报
            }
        }

        /// <summary>
        /// 按分区键生成小时级后缀表名，与 PostgreSQL 仓储语义一致。
        /// </summary>
        protected string GetPartition(T entity)
        {
            if (entity is null)
            {
                throw new ArgumentNullException($"{nameof(GetPartition)} {typeof(T).Name}", "entity is null");
            }

            // 无分区键则直接使用映射表名
            if (!_hasPartitionKey || _partitionKeyGetter == null)
            {
                return _tableName;
            }

            var hourKey = GetHourKeyFromPartitionValue(_partitionKeyGetter(entity));
            return _tableName + "_" + hourKey.ToString("yyyyMMddHH");
        }

        /// <summary>
        /// 分区字段归一为整点小时，保证同名分区一致性。
        /// </summary>
        private static DateTime GetHourKeyFromPartitionValue(object? partitionValue)
        {
            return partitionValue switch
            {
                DateTime dateTime => dateTime.TruncateToHour(),
                string stringValue when DateTime.TryParse(stringValue, out var parsedDate) => parsedDate.TruncateToHour(),
                _ => DateTime.Now.TruncateToHour()
            };
        }
        #endregion
    }
}
