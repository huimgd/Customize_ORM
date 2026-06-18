using ClickHouse.Driver;
using Customize_ORM.Extensions;
using Customize_ORM.Interfaces;
using Customize_ORM.Interfaces.ConnectionFactory;
using Customize_ORM.Models;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using Customize_ORM.Extensions.Sql;
using System.Data;
using System.Data.Common;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Customize_ORM.Impl
{
    public class ClickhouseRepository<T>(IChConnectionFactory factory, ILogger<ClickhouseRepository<T>> logger) : IClickhouseRepository<T> where T : class
    {
        private static readonly EntityMapping<T> _mapping = ObjectMapping<T>.MappingForInsert;
        private static readonly ObjectMapping<T>.DRowFactory _rowFactory = ObjectMapping<T>.RowFactory;
        private static readonly Func<DbDataReader, T> _rowReader = EntityDataReaderMapper<T>.Create(_mapping);
        private static readonly ConcurrentDictionary<string, object> _projectionCache = new();
        private readonly ClickHouseClient _client = factory.GetClientAsync();
        private readonly ILogger<ClickhouseRepository<T>> _logger = logger;

        private static readonly Func<DbParameter, DbParameter> _materializeClickHouseParameter = ClickHouseDialect.Materialize;

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

        public async ValueTask<bool> BulkInsertAsync(
            List<T> entitys,
            string tableName,
            CancellationToken token)
        {
            if (entitys == null || entitys.Count == 0) return true;
            try
            {
                List<object[]> objects = [];
                foreach (var entity in entitys)
                    objects.Add(_rowFactory(entity));
                if (string.IsNullOrWhiteSpace(tableName))
                    tableName = _mapping.TableName;
                long count = await _client.InsertBinaryAsync(tableName, _mapping.AllColumns, objects, cancellationToken: token);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, nameof(BulkInsertAsync));
                return false;
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
            var sql = queryBuilder.SelectAll(final: true);
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
            var sql = queryBuilder.SelectAll(final: true);
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
