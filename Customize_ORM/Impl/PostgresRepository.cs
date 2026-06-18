using Customize_ORM.Extensions;
using Customize_ORM.Interfaces;
using Customize_ORM.Interfaces.ConnectionFactory; 
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;

using Dapper;

namespace Customize_ORM.Impl
{
    /// <summary>
    /// 数据库操作实现类
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public class PostgresRepository<T> : IPostgresRepository<T> where T : class
    {
        protected static readonly EntityMapping<T> _mapping = ObjectMapping<T>.MappingForInsert;
        protected static readonly EntityMapping<T> _upadteMapping = ObjectMapping<T>.MappingForUpdate;
        protected static readonly ObjectMapping<T>.DRowFactory _rowFactory = ObjectMapping<T>.RowFactory;
        protected readonly string _tableName;
        protected readonly IPgConnectionFactory _postgreSql;
        protected readonly ILogger _logger;
        protected readonly bool _hasPartitionKey;
        protected readonly Func<T, object?>? _partitionKeyGetter;
        public PostgresRepository(IPgConnectionFactory postgreSql, ILogger<PostgresRepository<T>> logger)
        {
            _tableName = _mapping.TableName;
            _postgreSql = postgreSql;
            _logger = logger;
            _hasPartitionKey = !string.IsNullOrWhiteSpace(_mapping.PartitionKey);
            if (_hasPartitionKey && _mapping.ValueGetters.TryGetValue(_mapping.PartitionKey, out var getter))
            {
                _partitionKeyGetter = getter;
            }
        }
        public async Task<NpgsqlConnection> GetConnectionAsync(CancellationToken token)
        {
            var connection = await _postgreSql.GetConnectionAsync(token);
            ArgumentNullException.ThrowIfNull(connection, nameof(connection));
            return connection;
        }
        #region 基础查询
        public async ValueTask<long> GetCountAsync(Expression<Func<T, bool>>? expression, CancellationToken token)
        {
            try
            {
                await using var connection = await _postgreSql.GetConnectionAsync();
                string sql = $"SELECT COUNT(*) FROM {_tableName} Where ";
                if (expression != null)
                {
                    var sqlExpr = new SqlExpression<T>(_mapping);
                    sqlExpr.Where(expression);
                    sql += sqlExpr.BuildWhereStr(); 
                   
                }
                using var cmd = new NpgsqlCommand(sql, connection);
                var result = cmd.ExecuteScalar();  // 返回第一行第一列
                return Convert.ToInt32(result);
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
                await using var connection = await _postgreSql.GetConnectionAsync();
                if (string.IsNullOrWhiteSpace(sql))
                    throw new ArgumentException($"{nameof(QueryFirstOrDefaultAsync)} {typeof(T).Name} SQL query cannot be empty or whitespace.", nameof(sql));
                if (parameters.Any())
                {
                    var dynamicParams = new DynamicParameters();
                    foreach (var param in parameters)
                    {
                        dynamicParams.Add(
                            param.ParameterName,
                            param.Value,
                            param.DbType,
                            direction: param.Direction,
                            size: param.Size);
                    }
                    return await connection.QueryFirstOrDefaultAsync<TValue>(sql, dynamicParams);
                }
                else
                {
                    return await connection.QueryFirstOrDefaultAsync<TValue>(sql);
                }
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
                await using var connection = await _postgreSql.GetConnectionAsync();
                using var command = connection.CreateCommand();
                command.CommandText = sql;
                AttachParameters(command.Parameters, parameters);
                return await command.ExecuteNonQueryAsync();
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
                await using var connection = await _postgreSql.GetConnectionAsync();
                if (string.IsNullOrWhiteSpace(sql))
                    throw new ArgumentException($"{nameof(QueryAsync)} {typeof(T).Name} SQL query cannot be empty or whitespace.", nameof(sql));
                if (parameters.Length != 0)
                {
                    var dynamicParams = new DynamicParameters();
                    foreach (var param in parameters)
                    {
                        dynamicParams.Add(
                            param.ParameterName,
                            param.Value,
                            param.DbType,
                            direction: param.Direction,
                            size: param.Size);
                    }
                    return await connection.QueryAsync<TValue>(sql, dynamicParams);
                }
                else
                {
                    return await connection.QueryAsync<TValue>(sql);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "{fnName} {entityName} sql=>{sqlstr}", nameof(QueryAsync), typeof(T).Name, sql);
            }
            return [];
        }
        public async ValueTask<List<T>> QueryAndDeleteAsync(string sql, CancellationToken token, string tableName, params DbParameter[] parameters)
        {
            await using var connection = await _postgreSql.GetConnectionAsync();
            ArgumentNullException.ThrowIfNull(connection, nameof(connection));
            await using var transaction = await connection.BeginTransactionAsync(token);
            try
            {
                if (string.IsNullOrWhiteSpace(sql))
                    throw new ArgumentException($"{nameof(QueryAsync)} {typeof(T).Name} SQL query cannot be empty or whitespace.", nameof(sql));
                List<T> dataArry = [];
                if (parameters.Length != 0)
                {
                    var dynamicParams = new DynamicParameters();
                    foreach (var param in parameters)
                    {
                        dynamicParams.Add(
                            param.ParameterName,
                            param.Value,
                            param.DbType,
                            direction: param.Direction,
                            size: param.Size);
                    }
                    dataArry = [.. (await connection.QueryAsync<T>(sql, dynamicParams, transaction))];

                }
                else
                {
                    dataArry = [.. (await connection.QueryAsync<T>(sql, null, transaction))];
                }
                if (dataArry.Count != 0)
                {
                    var deleteSql = $"DELETE FROM {tableName} WHERE id = ANY(@Ids)";
                    var idGetter = _mapping.ValueGetters["id"];
                    List<long> ids = [];
                    foreach (var item in dataArry)
                    {
                        var idValue = idGetter(item);
                        if (idValue != null)
                        {
                            ids.Add((long)idValue);
                        }
                    }
                    await connection.ExecuteAsync(deleteSql, new { Ids = ids }, transaction);
                    await transaction.CommitAsync(token);
                    return dataArry;
                }
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
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
                using var command = (await _postgreSql.GetConnectionAsync()).CreateCommand();
                command.CommandText = sql;
                if (parameters.Count() > 0)
                {
                    AttachParameters(command.Parameters, parameters);
                }
                return await command.ExecuteReaderAsync();
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
                await using var connection = await _postgreSql.GetConnectionAsync();
                ArgumentNullException.ThrowIfNull(connection);
                var sqlExpr = new SqlExpression<T>(_mapping, Enums.DBType.PostgreSQL);
                if (expression != null)
                    sqlExpr.Where(expression);

                var (whereClause, parameters) = sqlExpr.BuildWhereClause();
                var sql = $"SELECT * FROM {_tableName}";
                if (!string.IsNullOrEmpty(whereClause))
                    sql += $" WHERE {whereClause}"; 
                await using var cmd = new NpgsqlCommand(sql, connection);
                if(parameters!=null)
                {
                    foreach (var item in parameters)
                    {
                        cmd.Parameters.Add(item);
                    }
                }
                using var reader = await cmd.ExecuteReaderAsync(token);
                while (reader.Read())
                {
                    _rowReader();
                }
                    if (expression != null)
                {
                    return await connection.SelectAsync<T>(expression, null, token);
                }
                else
                {
                    return await connection.GetAllAsync<T>(null, token);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "{fnName} {EntityName}", nameof(QueryAsync), typeof(T).Name);
            }
            return [];
        }
        public async IAsyncEnumerable<T?> QueryStreamAsync(NpgsqlConnection connection, Expression<Func<T, bool>>? expression = null, int batchSize = 100, [EnumeratorCancellation] CancellationToken token = default)
        {
            //开始事务（游标必须在事务中使用）
            using var transaction = await connection.BeginTransactionAsync(token);

            //声明一个唯一游标名（避免并发冲突）
            string cursorName = $"cursor_{Guid.NewGuid():N}";
            var sqlExpression = new Extensions.SqlExpression<T>(_mapping);
            sqlExpression.Select().Where(expression);
            string sql = sqlExpression.ToSql(out List<DbParameter> parameters);
            // 创建声明游标的命令
            string declareSql = $"DECLARE {cursorName} NO SCROLL CURSOR FOR {sql}";
            using var declareCmd = new NpgsqlCommand(declareSql, connection, transaction);
            foreach (var param in parameters)
            {
                declareCmd.Parameters.AddWithValue(param.ParameterName, param.Value ?? DBNull.Value);
            }
            await declareCmd.ExecuteNonQueryAsync(token);

            try
            {
                bool hasMore = true;
                while (hasMore && !token.IsCancellationRequested)
                {
                    // 获取下一批数据
                    string fetchSql = $"FETCH FORWARD {batchSize} FROM {cursorName}";
                    using var fetchCmd = new NpgsqlCommand(fetchSql, connection, transaction);

                    // 使用 CommandBehavior.SequentialAccess 同样可以提高读取效率
                    using var reader = await fetchCmd.ExecuteReaderAsync(CommandBehavior.SequentialAccess, token);

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
                using var closeCmd = new NpgsqlCommand(closeSql, connection, transaction);
                await closeCmd.ExecuteNonQueryAsync(token);

                // 提交事务（或者回滚，这里我们只读所以提交也行）
                await transaction.CommitAsync(token);
            }
        }
        private T MapFromReader(DbDataReader reader)
        {
            var entity = Activator.CreateInstance<T>();
            try
            {
                for (int i = 0; i < _mapping.AllColumns.Length && i < reader.FieldCount; i++)
                {
                    var columnName = _mapping.AllColumns[i];
                    if (_mapping.ValueSetters != null && _mapping.ValueSetters.TryGetValue(columnName, out var setter))
                    {
                        var value = reader.IsDBNull(i) ? null : reader.GetValue(i);
                        if (value != null)
                        {
                            setter(entity, value);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "{fnName} {EntityName}", nameof(MapFromReader), typeof(T).Name);
            }

            return entity;
        }
        public async ValueTask<T?> QueryFirstOrDefaultAsync(Expression<Func<T, bool>>? expression = null, CancellationToken token = default)
        {
            try
            {
                if (expression != null)
                {
                    await using var connection = await _postgreSql.GetConnectionAsync(token);
                    ArgumentNullException.ThrowIfNull(connection);
                    var sqlExpression = new Extensions.SqlExpression<T>(_mapping);
                    sqlExpression.Select().Where(expression).Firest();
                    string sql = sqlExpression.ToSql(out List<DbParameter> parameters);
                    return await connection.QueryFirstOrDefaultAsync<T>(sql, parameters, null);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {

                _logger.LogError(ex, "{fnName} {EntityName}", nameof(QueryFirstOrDefaultAsync), typeof(T).Name);
            }
            return default;
        }
        public async ValueTask<bool> UpdateAsync(T entity, Expression<Func<T, bool>>? expression)
        {
            string sql = string.Empty;
            try
            {
                string tableName = GetPartition(entity);
                using var connection = await _postgreSql.GetConnectionAsync();
                var sqlExpr = new Extensions.SqlExpression<T>(_mapping).WithTableAlias(tableName);
                if (expression != null)
                {
                    sqlExpr.Where(expression);
                }
                var (whereClause, parameters) = sqlExpr.BuildWhereClause();
                var valueGetters = _upadteMapping.ValueGetters;
                var setClauses = new List<string>();
                foreach (var col in _upadteMapping.AllColumns)
                {
                    setClauses.Add($"{col} = @{col}");
                }
                foreach (var col in _upadteMapping.AllColumns)
                {
                    if (valueGetters.TryGetValue(col, out var getter))
                    {
                        parameters.Add(new NpgsqlParameter(col, getter(entity)));
                    }
                }
                sql = $@" UPDATE {tableName} SET {string.Join(", ", setClauses)} WHERE {whereClause}";
                return await connection.ExecuteAsync(sql, parameters) > 0;
            }
            catch (Exception ex)
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
                using var connection = await _postgreSql.GetConnectionAsync();
                ArgumentNullException.ThrowIfNull(connection, nameof(connection));
                var valueGetters = _mapping.ValueGetters;
                var updateColumns = columns.ToArray();
                string[] allColumns = new string[columns.Length + 1];
                allColumns[0] = _mapping.Key;
                columns.Span.CopyTo(allColumns.AsSpan(1));



                var setClauses = updateColumns.Select(col => $"{col} = :{col}").ToList();
                sql = $@"UPDATE {tableName} 
                    SET {string.Join(", ", setClauses)} 
                    WHERE {_mapping.Key}=:{_mapping.Key}";
                var parameters = new DynamicParameters();
                foreach (var column in allColumns)
                {
                    if (valueGetters.TryGetValue(column, out var getter))
                    {
                        var value = getter(entity);
                        var propertyType = GetPropertyType(column);
                        var elementType = GetClrTypeForProperty(propertyType);

                        if (elementType.IsArray)
                        {
                            var arrayElementType = elementType.GetElementType() ?? typeof(object);

                            if (value is not Array arrayValue || arrayValue.Length == 0)
                            {
                                arrayValue = Array.CreateInstance(arrayElementType, 0);
                            }
                            parameters.Add(column, arrayValue, dbType: DbType.Object);
                        }
                        else
                        {
                            parameters.Add(column, value);
                        }
                    }
                }

                return await connection.ExecuteAsync(sql, parameters) > 0;
            }
            catch (Exception ex)
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
                sql = $"COPY {tableName} ({string.Join(",", _mapping.AllColumns)}) FROM STDIN (FORMAT BINARY)";
                using var connection = await _postgreSql.GetConnectionAsync();
                ArgumentNullException.ThrowIfNull(connection, nameof(connection));
                await using var writer = await connection.BeginBinaryImportAsync(sql, CancellationToken.None);
                var values = _rowFactory(entity);
                await writer.WriteRowAsync(CancellationToken.None, values).ConfigureAwait(false);
                var writeCount = await writer.CompleteAsync().ConfigureAwait(false);
                _logger.LogInformation($"{nameof(InsertAsync)} {typeof(T).Name} Times{sw.ElapsedMilliseconds}ms");
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

        #endregion

        #region pg相关映射

        private static void AttachParameters(DbParameterCollection parametersCollection, IReadOnlyList<DbParameter> parameters)
        {
            foreach (var item in parameters)
            {
                parametersCollection.Add(item);
            }
        }
        /// <summary>
        /// C#数据类型转pgsql数组类型
        /// </summary>
        /// <param name="elementType"></param>
        /// <returns></returns>
        protected static string GetPostgreSqlArrayType(Type elementType)
        {
            //剥离 Nullable
            var nullableUnderlying = Nullable.GetUnderlyingType(elementType);
            if (nullableUnderlying != null)
                elementType = nullableUnderlying;

            //剥离枚举，取其底层整数类型
            if (elementType.IsEnum)
                elementType = Enum.GetUnderlyingType(elementType);

            //映射
            return Type.GetTypeCode(elementType) switch
            {
                TypeCode.Boolean => "boolean[]",
                // byte/sbyte 统一映射为 smallint[]（除非你确定就是要 bytea）
                TypeCode.Byte => "smallint[]",      // 或 "bytea" 仅当确为二进制
                TypeCode.SByte => "smallint[]",
                TypeCode.Char => "char[]",          // 若要 Unicode 可改为 "text[]"
                TypeCode.DateTime => "timestamp[]",
                TypeCode.Decimal => "numeric[]",
                TypeCode.Double => "double precision[]",
                TypeCode.Int16 => "smallint[]",
                TypeCode.Int32 => "integer[]",
                TypeCode.Int64 => "bigint[]",
                TypeCode.Single => "real[]",
                TypeCode.String => "text[]",
                TypeCode.UInt16 => "integer[]",
                TypeCode.UInt32 => "bigint[]",
                TypeCode.UInt64 => "numeric[]",
                _ when elementType == typeof(Guid) => "uuid[]",
                _ when elementType == typeof(byte[]) => "bytea[]", // 极少见的场景
                _ when elementType == typeof(TimeSpan) => "interval[]",
                _ when elementType == typeof(DateTimeOffset) => "timestamptz[]",
                _ => "text[]"
            };
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

        // 辅助方法：获取属性的CLR类型（用于数组创建）
        protected static Type GetClrTypeForProperty(Type propertyType)
        {
            // 处理Nullable类型
            if (propertyType.IsGenericType && propertyType.GetGenericTypeDefinition() == typeof(Nullable<>))
            {
                return Nullable.GetUnderlyingType(propertyType) ?? typeof(object);
            }

            // 处理枚举类型
            if (propertyType.IsEnum)
            {
                return Enum.GetUnderlyingType(propertyType);
            }

            return propertyType;
        }
        /// <summary>
        /// C#数据类型转pgsql数据类型
        /// </summary>
        /// <param name="type"></param>
        /// <returns></returns>
        protected static NpgsqlDbType GetNpgsqlDbType(Type type)
        {
            ArgumentNullException.ThrowIfNull(type);

            // 处理Nullable类型
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Nullable<>))
            {
                return GetNpgsqlDbType(Nullable.GetUnderlyingType(type)!);
            }

            // 处理枚举类型
            if (type.IsEnum)
            {
                return GetNpgsqlDbType(Enum.GetUnderlyingType(type));
            }

            // 注意：移除数组类型的处理，因为我们在调用这个方法时已经处理了数组元素类型

            // 根据类型映射到NpgsqlDbType
            return Type.GetTypeCode(type) switch
            {
                TypeCode.Boolean => NpgsqlDbType.Boolean,
                TypeCode.Byte => NpgsqlDbType.Smallint,
                TypeCode.Char => NpgsqlDbType.Char,
                TypeCode.DateTime => NpgsqlDbType.Timestamp,
                TypeCode.Decimal => NpgsqlDbType.Numeric,
                TypeCode.Double => NpgsqlDbType.Double,
                TypeCode.Int16 => NpgsqlDbType.Smallint,
                TypeCode.Int32 => NpgsqlDbType.Integer,
                TypeCode.Int64 => NpgsqlDbType.Bigint,
                TypeCode.SByte => NpgsqlDbType.Smallint,
                TypeCode.Single => NpgsqlDbType.Real,
                TypeCode.String => NpgsqlDbType.Text,
                TypeCode.UInt16 => NpgsqlDbType.Integer,
                TypeCode.UInt32 => NpgsqlDbType.Bigint,
                TypeCode.UInt64 => NpgsqlDbType.Numeric,
                _ when type == typeof(Guid) => NpgsqlDbType.Uuid,
                _ when type == typeof(byte[]) => NpgsqlDbType.Bytea,
                _ when type == typeof(string[]) => NpgsqlDbType.Text,
                _ when type == typeof(ulong) => NpgsqlDbType.Numeric,
                _ when type == typeof(long) => NpgsqlDbType.Bigint,
                _ when type == typeof(TimeSpan) => NpgsqlDbType.Interval,
                _ when type == typeof(DateTimeOffset) => NpgsqlDbType.TimestampTz,
                _ when type.FullName == "System.Text.Json.JsonElement" => NpgsqlDbType.Jsonb,
                _ => NpgsqlDbType.Text // 默认类型
            };
        }
        protected string GetPostgreSqlColumnType(string columnName)
        {
            var propType = GetPropertyType(columnName); // 注意这里应该用 _upadteMapping 定位
            if (propType.IsArray)
            {
                var elemType = propType.GetElementType();
                if (elemType != null)
                {
                    // 其他数组：映射为 pg 数组类型
                    return GetPostgreSqlArrayType(elemType);
                }
            }
            // 非数组
            return GetNpgsqlDbType(propType).ToString().ToLowerInvariant(); // 如 "integer", "text"
        }
        #endregion 

    }
}
