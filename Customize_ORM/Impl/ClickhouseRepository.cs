using ClickHouse.Driver;
using Customize_ORM.Extensions;
using Customize_ORM.Extensions.Sql;
using Customize_ORM.Interfaces;
using Customize_ORM.Models;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Data.Common;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Customize_ORM.Impl
{
    /// <summary>
    /// ClickHouse 仓储：列投影、分页与流式查询为主，同时以显式接口实现满足统一的 <see cref="IRepository{T}"/> 契约。
    /// ClickHouse 不支持单行插入/更新，相关方法抛 <see cref="NotSupportedException"/> 并提示改用批量写入。
    /// </summary>
    public class ClickhouseRepository<T>(IDbConnectionFactory connectionFactory, ILogger<ClickhouseRepository<T>> logger) : IQueryableRepository<T>, IBulkInsertRepository<T> where T : class
    {
        private static readonly EntityMapping<T> _mapping = ObjectMapping<T>.MappingForInsert;
        private static readonly ObjectMapping<T>.DRowFactory _rowFactory = ObjectMapping<T>.RowFactory;
        private static readonly Func<DbDataReader, T> _rowReader = EntityDataReaderMapper<T>.Create(_mapping);
        private static readonly ConcurrentDictionary<string, object> _projectionCache = new();
        private readonly IDbConnectionFactory _connectionFactory = connectionFactory;
        private readonly ClickHouseClient _client = ResolveClient(connectionFactory);
        private readonly ILogger<ClickhouseRepository<T>> _logger = logger;

        private static readonly Func<DbParameter, DbParameter> _materializeClickHouseParameter = ClickHouseDialect.Materialize;

        /// <summary>
        /// 二进制批量写入需要驱动客户端；连接工厂未提供该能力时给出明确异常。
        /// </summary>
        private static ClickHouseClient ResolveClient(IDbConnectionFactory factory)
        {
            ArgumentNullException.ThrowIfNull(factory);
            return factory is IClickHouseClientProvider provider
                ? provider.GetClient()
                : throw new InvalidOperationException(
                    $"{nameof(ClickhouseRepository<T>)} requires a connection factory implementing {nameof(IClickHouseClientProvider)}.");
        }

        private DbCommand CreateCommand(DbConnection connection, string sql, IReadOnlyList<DbParameter>? parameters = null)
        {
            return DbCommandExecution.CreateCommand(connection, sql, parameters, materializeParameter: _materializeClickHouseParameter);
        }

        private static ClickHouseQuerySqlBuilder<T> CreateQueryBuilder(string? tableName = null)
            => new(_mapping, tableName);

        private async Task<long> ExecuteScalarInt64Async(string sql, IReadOnlyList<DbParameter>? parameters, CancellationToken token)
        {
            await using var connection = _client.CreateConnection();
            var result = await DbCommandExecution.ExecuteScalarAsync(connection, sql, parameters, token, materializeParameter: _materializeClickHouseParameter);
            return Convert.ToInt64(result ?? 0);
        }

        private async Task<List<TResult>> ReadAllAsync<TResult>(
            string sql,
            IReadOnlyList<DbParameter>? parameters,
            Func<DbDataReader, TResult> map,
            CancellationToken token)
        {
            await using var connection = _client.CreateConnection();
            await using var command = CreateCommand(connection, sql, parameters);
            await using var reader = await command.ExecuteReaderAsync(token);

            var results = new List<TResult>();
            while (await reader.ReadAsync(token))
            {
                results.Add(map(reader));
            }

            return results;
        }

        /// <summary>
        /// 批量插入：走 ClickHouse 原生二进制写入，一次性提交整批。
        /// </summary>
        public async Task<(bool, ulong)> BulkInsertAsync(IReadOnlyList<T> entitys, string tableName = "", bool isRetry = false)
        {
            if (entitys == null || entitys.Count == 0) return (true, 0);
            try
            {
                List<object[]> objects = [];
                foreach (var entity in entitys)
                    objects.Add(_rowFactory(entity));
                if (string.IsNullOrWhiteSpace(tableName))
                    tableName = _mapping.TableName;
                //开启模式缓存，避免高频批量写入时重复拉取表结构
                var insertOptions = new InsertOptions { UseSchemaCache = true };
                long count = await _client.InsertBinaryAsync(tableName, _mapping.AllColumns, objects, insertOptions);
                return (true, (ulong)count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "{fnName} {EntityName}", nameof(BulkInsertAsync), typeof(T).Name);
                return (false, 0);
            }
        }
        public async ValueTask<long> GetCount(
            Expression<Func<T, bool>>? expression,
            string tableName,
            CancellationToken token)
        {
            try
            {
                var queryBuilder = CreateQueryBuilder(tableName);

                var sqlExpr = new SqlExpression<T>(_mapping, Enums.DBType.ClickHouse);
                if (expression != null)
                    sqlExpr.Where(expression);

                var (whereClause, parameters) = sqlExpr.BuildWhereClause();
                var sql = queryBuilder.ApplyWhere(queryBuilder.Count(), whereClause);

                return await ExecuteScalarInt64Async(sql, parameters, token);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, nameof(GetCount));
                return 0;
            }
        }
        public async ValueTask<IEnumerable<T>> QueryAsync(
            Expression<Func<T, bool>>? expression,
            int pageIndex,
            int pageSize,
            string tableName = "",
            CancellationToken token = default)
        {
            if (pageSize <= 0) pageSize = 1000;
            int offset = pageIndex * pageSize;

            try
            {
                var queryBuilder = CreateQueryBuilder(tableName);

                var sqlExpr = new SqlExpression<T>(_mapping, Enums.DBType.ClickHouse);
                if (expression != null)
                    sqlExpr.Where(expression);

                var (whereClause, parameters) = sqlExpr.BuildWhereClause();
                var orderBy = queryBuilder.ResolveOrderBy(sqlExpr.BuildOrderByClause());
                var sql = queryBuilder.SelectAll();
                sql = queryBuilder.ApplyWhere(sql, whereClause);
                sql = queryBuilder.ApplyOrderBy(sql, orderBy);
                sql = queryBuilder.ApplyLimit(sql, offset, pageSize);

                return await ReadAllAsync(sql, parameters, _rowReader, token);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, nameof(QueryAsync));
                return [];
            }
        }
        public async IAsyncEnumerable<T> QueryStreamAsync(
            Expression<Func<T, bool>>? expression,
            string tableName = "",
            [EnumeratorCancellation] CancellationToken token = default)
        {
            var queryBuilder = CreateQueryBuilder(tableName);

            var sqlExpr = new SqlExpression<T>(_mapping, Enums.DBType.ClickHouse);
            if (expression != null)
                sqlExpr.Where(expression);

            var (whereClause, parameters) = sqlExpr.BuildWhereClause();
            var sql = queryBuilder.SelectAll();
            sql = queryBuilder.ApplyWhere(sql, whereClause);
            sql = queryBuilder.ApplyOrderBy(sql);

            await using var connection = _client.CreateConnection();
            await using var cmd = CreateCommand(connection, sql, parameters);
            await using var reader = await cmd.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
                yield return _rowReader(reader);
        }
        public async IAsyncEnumerable<TValue> QueryStreamAsync<TValue>(
            string columnName,
            Expression<Func<T, bool>>? expression = null,
            string tableName = "",
            [EnumeratorCancellation] CancellationToken token = default)
        {
            var queryBuilder = CreateQueryBuilder(tableName);

            var sqlExpr = new SqlExpression<T>(_mapping, Enums.DBType.ClickHouse);
            if (expression != null)
                sqlExpr.Where(expression);

            var (whereClause, parameters) = sqlExpr.BuildWhereClause();
            var sql = queryBuilder.Select(columnName, final: true);
            sql = queryBuilder.ApplyWhere(sql, whereClause);

            await using var connection = _client.CreateConnection();
            await using var cmd = CreateCommand(connection, sql, parameters);
            await using var reader = await cmd.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                // 读取当前行的第一列（索引0）
                var rawValue = reader.IsDBNull(0) ? null : reader.GetValue(0);
                yield return DbRecordMapper.MapValue<TValue>(rawValue);
            }
        }
        public async ValueTask<IEnumerable<T>> QueryAsync(
            Expression<Func<T, bool>>? expression = null,
            string tableName = "",
            CancellationToken token = default)
        {
            var queryBuilder = CreateQueryBuilder(tableName);
            var sql = queryBuilder.SelectAll();
            try
            {
                var sqlExpr = new SqlExpression<T>(_mapping, Enums.DBType.ClickHouse);
                if (expression != null)
                    sqlExpr.Where(expression);

                var (whereClause, parameters) = sqlExpr.BuildWhereClause();
                sql = queryBuilder.ApplyWhere(sql, whereClause);

                return await ReadAllAsync(sql, parameters, _rowReader, token);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "{funName},{typeName} sql=>{sql}", nameof(QueryAsync), typeof(T).Name, sql);
                return [];
            }
        }
        public async ValueTask<IEnumerable<TValue>> QueryAsync<TValue>(
            Expression<Func<T, TValue>> selector,
            Expression<Func<T, bool>>? filter = null,
            string? tableName = null,
            CancellationToken token = default) where TValue : class
        {
            string sql = string.Empty;
            try
            {
                var queryBuilder = CreateQueryBuilder(tableName);
                var (projectionInfo, mapper) = GetOrCreateMapper(selector);
                var columnNames = projectionInfo.ColumnNames;
                var sqlExpr = new SqlExpression<T>(_mapping, Enums.DBType.ClickHouse);
                if (filter != null) sqlExpr.Where(filter);
                var (whereClause, parameters) = sqlExpr.BuildWhereClause();

                var columns = string.Join(", ", columnNames);
                sql = queryBuilder.Select(columns, final: true);
                sql = queryBuilder.ApplyWhere(sql, whereClause);
                sql = queryBuilder.ApplyOrderBy(sql);

                return await ReadAllAsync(sql, parameters, mapper, token);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "{funName},{typeName} sql=>{sql}", nameof(QueryAsync), typeof(T).Name, sql);
                return [];
            }
        }
        public async ValueTask<List<TValue>> QueryAsync<TValue>(
            string columnName,
            Expression<Func<T, bool>>? filter = null,
            string? tableName = null,
            CancellationToken token = default)
        {
            string sql = string.Empty;
            try
            {
                var queryBuilder = CreateQueryBuilder(tableName);

                var sqlExpr = new SqlExpression<T>(_mapping, Enums.DBType.ClickHouse);
                if (filter != null)
                    sqlExpr.Where(filter);
                var (whereClause, parameters) = sqlExpr.BuildWhereClause();

                sql = queryBuilder.Select(columnName, final: true);
                sql = queryBuilder.ApplyWhere(sql, whereClause);

                return await ReadAllAsync(
                    sql,
                    parameters,
                    reader => DbRecordMapper.MapValue<TValue>(reader.IsDBNull(0) ? null : reader.GetValue(0)),
                    token);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "{funName},{typeName} sql=>{sql}", nameof(QueryAsync), typeof(T).Name, sql);
                return [];
            }

        }
        public async ValueTask<PagedResult<TValue>> QueryAsync<TValue>(
            Expression<Func<T, TValue>> selector,
            Expression<Func<T, bool>>? filter = null,
            int pageIndex = 0,
            int pageSize = 1000,
            string? orderBy = null,
            string? tableName = null,
            CancellationToken token = default) where TValue : class
        {
            var queryBuilder = CreateQueryBuilder(tableName);
            var (projectionInfo, mapper) = GetOrCreateMapper(selector);
            var columnNames = projectionInfo.ColumnNames;
            var columns = string.Join(", ", columnNames);

            var sqlExpr = new SqlExpression<T>(_mapping, Enums.DBType.ClickHouse);
            if (filter != null)
                sqlExpr.Where(filter);
            var (whereClause, parameters) = sqlExpr.BuildWhereClause();

            var countSql = queryBuilder.Count(final: true);
            countSql = queryBuilder.ApplyWhere(countSql, whereClause);
            var totalCount = await ExecuteScalarInt64Async(countSql, parameters, token);

            if (totalCount == 0)
                return new PagedResult<TValue>([], pageIndex, pageSize, 0);

            int offset = pageIndex * pageSize;
            var dataSql = queryBuilder.Select(columns, final: true);
            dataSql = queryBuilder.ApplyWhere(dataSql, whereClause);
            dataSql = queryBuilder.ApplyOrderBy(dataSql, orderBy);
            dataSql = queryBuilder.ApplyLimit(dataSql, offset, pageSize);

            var items = await ReadAllAsync(dataSql, parameters, mapper, token);

            return new PagedResult<TValue>(items, pageIndex, pageSize, totalCount);
        }
        public async ValueTask<PagedResult<TValue>> QueryAsync<TValue>(
            string columnName,
            Expression<Func<T, bool>>? filter = null,
            int pageIndex = 0,
            int pageSize = 1000,
            string? tableName = null,
            CancellationToken token = default)
        {
            var queryBuilder = CreateQueryBuilder(tableName);
            var sqlExpr = new SqlExpression<T>(_mapping, Enums.DBType.ClickHouse);
            if (filter != null)
                sqlExpr.Where(filter);
            var (whereClause, parameters) = sqlExpr.BuildWhereClause();

            var countSql = queryBuilder.Count(final: true);
            countSql = queryBuilder.ApplyWhere(countSql, whereClause);
            var totalCount = await ExecuteScalarInt64Async(countSql, parameters, token);

            if (totalCount == 0)
                return new PagedResult<TValue>([], pageIndex, pageSize, 0);

            int offset = pageIndex * pageSize;
            var dataSql = queryBuilder.Select(columnName, final: true);
            dataSql = queryBuilder.ApplyWhere(dataSql, whereClause);
            dataSql = queryBuilder.ApplyOrderBy(dataSql);
            dataSql = queryBuilder.ApplyLimit(dataSql, offset, pageSize);

            var items = await ReadAllAsync(
                dataSql,
                parameters,
                reader => DbRecordMapper.MapValue<TValue>(reader.IsDBNull(0) ? null : reader.GetValue(0)),
                token);

            return new PagedResult<TValue>(items, pageIndex, pageSize, totalCount);
        }


        #region IRepository<T> 显式实现
        /// <summary>
        /// 获取 ClickHouse 连接（已打开，由调用方负责释放）。
        /// </summary>
        public Task<DbConnection> GetConnectionAsync(CancellationToken token = default)
            => _connectionFactory.OpenConnectionAsync(token);

        /// <summary>
        /// 获取数据数量（默认使用映射表名）。
        /// </summary>
        ValueTask<long> IRepository<T>.GetCountAsync(Expression<Func<T, bool>>? expression, CancellationToken token)
            => GetCount(expression, string.Empty, token);

        /// <summary>
        /// 执行查询返回第一条结果。
        /// </summary>
        public async ValueTask<TValue?> QueryFirstOrDefaultAsync<TValue>(string sql, CancellationToken token, params DbParameter[] parameters)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sql);
            await using var connection = await _connectionFactory.OpenConnectionAsync(token);
            await using var command = CreateCommand(connection, sql, parameters);
            await using var reader = await command.ExecuteReaderAsync(token);
            if (!await reader.ReadAsync(token))
            {
                return default;
            }

            return DbRecordMapper.MapSingle<TValue>(reader);
        }

        /// <summary>
        /// 按条件查询第一条实体。
        /// </summary>
        public async ValueTask<T?> QueryFirstOrDefaultAsync(Expression<Func<T, bool>>? expression = null, CancellationToken token = default)
        {
            await foreach (var entity in QueryStreamAsync(expression, string.Empty, token).WithCancellation(token))
            {
                return entity;
            }

            return default;
        }

        /// <summary>
        /// 执行查询返回结果集合。
        /// </summary>
        public async ValueTask<IEnumerable<TValue>> QueryAsync<TValue>(string sql, CancellationToken token, params DbParameter[] parameters)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sql);
            return await ReadAllAsync(sql, parameters, DbRecordMapper.MapSingle<TValue>, token);
        }

        /// <summary>
        /// 执行查询并返回未释放的数据读取器。
        /// </summary>
        public async Task<DbDataReader> QueryAsync(string sql, CancellationToken token, params DbParameter[] parameters)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sql);
            var connection = await _connectionFactory.OpenConnectionAsync(token);
            return await DbCommandExecution.ExecuteOwnedReaderAsync(
                connection,
                sql,
                parameters,
                token,
                materializeParameter: _materializeClickHouseParameter);
        }

        /// <summary>
        /// 按条件查询实体集合（一次性加载）。
        /// </summary>
        public async ValueTask<IEnumerable<T>> QueryAsync(Expression<Func<T, bool>>? expression = null, CancellationToken token = default)
            => await QueryAsync(expression, string.Empty, token);

        /// <summary>
        /// 流式读取：ClickHouse 通过 ADO.NET reader 流式拉取，<paramref name="batchSize"/> 为建议值，由驱动决定实际拉取节奏。
        /// </summary>
        public async IAsyncEnumerable<T?> QueryStreamAsync(DbConnection connection, Expression<Func<T, bool>>? expression = null, int batchSize = 100, [EnumeratorCancellation] CancellationToken token = default)
        {
            ArgumentNullException.ThrowIfNull(connection);

            var sqlExpr = new SqlExpression<T>(_mapping, Enums.DBType.ClickHouse);
            if (expression != null)
                sqlExpr.Where(expression);

            var queryBuilder = CreateQueryBuilder();
            var (whereClause, parameters) = sqlExpr.BuildWhereClause();
            var sql = queryBuilder.ApplyWhere(queryBuilder.SelectAll(), whereClause);
            sql = queryBuilder.ApplyOrderBy(sql);

            await using var command = CreateCommand(connection, sql, parameters);
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                yield return _rowReader(reader);
            }
        }

        /// <summary>
        /// 执行语句返回受影响行数。
        /// </summary>
        public async Task<int> ExecuteNonQueryAsync(string sql, CancellationToken token, params DbParameter[] parameters)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sql);
            await using var connection = await _connectionFactory.OpenConnectionAsync(token);
            return await DbCommandExecution.ExecuteNonQueryAsync(
                connection,
                sql,
                parameters,
                token,
                materializeParameter: _materializeClickHouseParameter);
        }

        /// <summary>
        /// ClickHouse 不支持"查询并删除"的事务语义，明确抛出异常而不是静默返回空集合。
        /// </summary>
        public ValueTask<List<T>> QueryAndDeleteAsync(string sql, CancellationToken token, string tableName, params DbParameter[] parameters)
            => throw new NotSupportedException(
                $"{typeof(T).Name} 在 ClickHouse 上不支持 {nameof(QueryAndDeleteAsync)}：ClickHouse 无事务性删除语义，请改用 ALTER TABLE ... DELETE 或重建表。");

        /// <summary>
        /// ClickHouse 不支持单行插入，明确抛出异常并提示使用批量写入。
        /// </summary>
        public Task<bool> InsertAsync(T entity, CancellationToken token, string partition = "")
            => throw new NotSupportedException(
                $"{typeof(T).Name} 在 ClickHouse 上不支持单行 {nameof(InsertAsync)}，请改用 {nameof(IBulkInsertRepository<T>)}.{nameof(BulkInsertAsync)}。");

        /// <summary>
        /// ClickHouse 不支持单行更新，明确抛出异常。
        /// </summary>
        public ValueTask<bool> UpdateAsync(T entity, ReadOnlyMemory<string> columns, CancellationToken token)
            => throw new NotSupportedException(
                $"{typeof(T).Name} 在 ClickHouse 上不支持单行 {nameof(UpdateAsync)}，请改用 ALTER TABLE ... UPDATE 或通过批量写入重建数据。");

        /// <summary>
        /// ClickHouse 不支持单行更新，明确抛出异常。
        /// </summary>
        public ValueTask<bool> UpdateAsync(T entity, Expression<Func<T, bool>> expression)
            => throw new NotSupportedException(
                $"{typeof(T).Name} 在 ClickHouse 上不支持单行 {nameof(UpdateAsync)}，请改用 ALTER TABLE ... UPDATE 或通过批量写入重建数据。");
        #endregion

        #region 辅助方法
        /// <summary>
        /// 创建从 IDataRecord 到 TResult 的映射委托
        /// </summary>
        private static Func<DbDataReader, TResult> CreateMapper<TResult>(ProjectionInfo info) where TResult : class
        {
            return reader =>
            {
                var result = Activator.CreateInstance<TResult>();
                foreach (var (property, columnName) in info.PropertyAssignments)
                {
                    int columnIndex = Array.IndexOf(info.ColumnNames, columnName);
                    if (columnIndex < 0)
                    {
                        continue;
                    }

                    var rawValue = reader.IsDBNull(columnIndex) ? null : reader.GetValue(columnIndex);
                    property.SetValue(result, DbRecordMapper.ConvertToType(rawValue, property.PropertyType));
                }

                return result;
            };
        }
        /// <summary>
        /// 解析投影表达式，返回列名列表和属性赋值器的元数据
        /// </summary>
        private ProjectionInfo ParseProjection<TResult>(Expression<Func<T, TResult>> selector)
        {
            var columnNames = new List<string>();
            var propertyAssignments = new List<(PropertyInfo Property, string ColumnName)>();

            // 处理两种投影形式：匿名对象 new { ... } 或具名类型 new Dto { ... }
            if (selector.Body is NewExpression newExpr)
            {
                // 匿名类型投影：x => new { x.Id, x.Name }
                for (int i = 0; i < newExpr.Arguments.Count; i++)
                {
                    var arg = newExpr.Arguments[i];
                    var memberExpr = GetMemberExpression(arg);
                    if (memberExpr != null && memberExpr.Member is PropertyInfo sourceProp)
                    {
                        var columnName = _mapping.GetColumnName(sourceProp);
                        var targetProp = typeof(TResult).GetProperty(newExpr.Members![i].Name);
                        if (targetProp != null && targetProp.CanWrite)
                        {
                            columnNames.Add(columnName);
                            propertyAssignments.Add((targetProp, columnName));
                        }
                    }
                }
            }
            else if (selector.Body is MemberInitExpression initExpr)
            {
                // 具名类型投影：x => new Dto { Id = x.Id, Name = x.Name }
                foreach (var binding in initExpr.Bindings.OfType<MemberAssignment>())
                {
                    var memberExpr = GetMemberExpression(binding.Expression);
                    if (memberExpr != null && memberExpr.Member is PropertyInfo sourceProp)
                    {
                        var columnName = _mapping.GetColumnName(sourceProp);
                        var targetProp = binding.Member as PropertyInfo;
                        if (targetProp != null && targetProp.CanWrite)
                        {
                            columnNames.Add(columnName);
                            propertyAssignments.Add((targetProp, columnName));
                        }
                    }
                }
            }
            else
            {
                throw new ArgumentException($"不支持的投影表达式类型: {selector.Body.NodeType}");
            }

            if (columnNames.Count == 0)
                throw new ArgumentException("未能从投影表达式中解析出任何列");

            return new ProjectionInfo(columnNames.ToArray(), propertyAssignments);
        }
        /// <summary>
        /// 缓存映射
        /// </summary>
        /// <typeparam name="TValue"></typeparam>
        /// <param name="selector"></param>
        /// <returns></returns>
        private (ProjectionInfo, Func<DbDataReader, TValue>) GetOrCreateMapper<TValue>(
       Expression<Func<T, TValue>> selector) where TValue : class
        {
            // 缓存键组合：目标类型全名 + 表达式的字符串形式
            string key = $"{typeof(TValue).FullName}_{selector.ToString()}";
            if (_projectionCache.TryGetValue(key, out object? cached) &&
        cached is (ProjectionInfo proj, Func<DbDataReader, TValue> mapper))
            {
                return (proj, mapper);
            }
            var projectionInfo = ParseProjection(selector);
            var _mapper = CreateMapper<TValue>(projectionInfo);
            var result = (projectionInfo, _mapper);
            _projectionCache.TryAdd(key, result);
            return result;
        }
        /// <summary>
        /// 从表达式中提取最内层的 MemberExpression
        /// </summary>
        private static MemberExpression? GetMemberExpression(Expression expr)
        {
            while (true)
            {
                switch (expr)
                {
                    case MemberExpression member:
                        return member;
                    case UnaryExpression unary when unary.Operand is MemberExpression member:
                        return member;
                    case LambdaExpression lambda:
                        expr = lambda.Body;
                        continue;
                    default:
                        return null;
                }
            }
        }
        /// <summary>
        /// 投影信息内部类
        /// </summary>
        private class ProjectionInfo
        {
            public string[] ColumnNames { get; }
            public List<(PropertyInfo Property, string ColumnName)> PropertyAssignments { get; }

            public ProjectionInfo(string[] columnNames, List<(PropertyInfo Property, string ColumnName)> propertyAssignments)
            {
                ColumnNames = columnNames;
                PropertyAssignments = propertyAssignments;
            }
        }

        #endregion
    }
}
