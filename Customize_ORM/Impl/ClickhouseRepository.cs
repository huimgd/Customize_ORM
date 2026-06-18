using ClickHouse.Driver;
using ClickHouse.Driver.ADO;
using ClickHouse.Driver.ADO.Parameters;
using Customize_ORM.Extensions;
using Customize_ORM.Interfaces;
using Customize_ORM.Interfaces.ConnectionFactory;
using Customize_ORM.Models;
using Dapper;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Metadata;
using System.Runtime.CompilerServices;

namespace Customize_ORM.Impl
{
    public class ClickhouseRepository<T>(IChConnectionFactory factory, ILogger<ClickhouseRepository<T>> logger) : IClickhouseRepository<T> where T : class
    {
        private static readonly EntityMapping<T> _mapping = ObjectMapping<T>.MappingForInsert;
        private static readonly ObjectMapping<T>.DRowFactory _rowFactory = ObjectMapping<T>.RowFactory;
        private static readonly List<Action<T, object>> _settersByOrder;
        private static readonly Func<IDataRecord, T> _rowReader;
        private static readonly ConcurrentDictionary<string, object> _projectionCache = new();
        private readonly ClickHouseClient _client = factory.GetClientAsync();
        private readonly ILogger<ClickhouseRepository<T>> _logger = logger;

        static ClickhouseRepository()
        {
            // 按列顺序准备 Setter
            _settersByOrder = new List<Action<T, object>>(_mapping.AllColumns.Length);
            foreach (var col in _mapping.AllColumns)
            {
                if (_mapping.ValueSetters != null && _mapping.ValueSetters.TryGetValue(col, out var setter))
                    _settersByOrder.Add(setter);
            }
            _rowReader = CreateRowReader();
        }

        private static Func<IDataRecord, T> CreateRowReader()
        {
            var recordParam = Expression.Parameter(typeof(IDataRecord), "record");
            var entityVar = Expression.Variable(typeof(T), "entity");
            var assignExprs = new List<Expression>
            {
                Expression.Assign(entityVar, Expression.New(typeof(T)))
            };
            for (int colIndex = 0; colIndex < _mapping.AllColumns.Length; colIndex++)
            {
                var column = _mapping.AllColumns[colIndex];
                if (_mapping.ValueSetters != null && _mapping.ValueSetters.TryGetValue(column, out var setter))
                {
                    var getValueCall = Expression.Call(recordParam, nameof(IDataRecord.GetValue), null, Expression.Constant(colIndex));
                    var isDbNull = Expression.Call(recordParam, nameof(IDataRecord.IsDBNull), null, Expression.Constant(colIndex));
                    var valueExpr = Expression.Condition(isDbNull, Expression.Constant(null, typeof(object)), getValueCall);
                    var callSetter = Expression.Invoke(Expression.Constant(setter), entityVar, valueExpr);
                    assignExprs.Add(callSetter);
                }
            }
            assignExprs.Add(entityVar);
            var block = Expression.Block([entityVar], assignExprs);
            return Expression.Lambda<Func<IDataRecord, T>>(block, recordParam).Compile();
        }

        /// <summary>
        /// 创建带参数的命令 
        /// </summary>
        /// <param name="sql">sql语句</param>
        /// <param name="parameters">参数化数据</param>
        /// <param name="existingConnection">外不传入连接（高频调用时使用）</param>
        /// <returns></returns>
        private ClickHouseCommand CreateCommand(
             string sql,
             List<DbParameter>? parameters, ClickHouseConnection? existingConnection = null)
        {
            var connection = existingConnection ?? _client.CreateConnection();
            var command = connection.CreateCommand();
            command.CommandText = sql;
            
            if (parameters != null)
            {
                foreach (var item in parameters)
                {
                    command.Parameters.Add(item);
                }
            }
            return command;
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
                if (string.IsNullOrWhiteSpace(tableName))
                    tableName = _mapping.TableName;

                var sqlExpr = new SqlExpression<T>(_mapping, Enums.DBType.ClickHouse);
                if (expression != null)
                    sqlExpr.Where(expression);

                var (whereClause, parameters) = sqlExpr.BuildWhereClause();
                var sql = $"SELECT COUNT(*) FROM {tableName}";
                if (!string.IsNullOrEmpty(whereClause))
                    sql += $" WHERE {whereClause}";

                using var cmd = CreateCommand(sql, parameters);
                var result = await cmd.ExecuteScalarAsync(token);
                return Convert.ToInt64(result ?? 0);
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
                if (string.IsNullOrWhiteSpace(tableName))
                    tableName = _mapping.TableName;

                var sqlExpr = new SqlExpression<T>(_mapping, Enums.DBType.ClickHouse);
                if (expression != null)
                    sqlExpr.Where(expression);

                // 获取 WHERE 子句和参数
                var (whereClause, parameters) = sqlExpr.BuildWhereClause();
                // 获取 ORDER BY（如果没有则使用主键）
                var orderBy = sqlExpr.BuildOrderByClause();
                if (string.IsNullOrEmpty(orderBy))
                    orderBy = $"ORDER BY {_mapping.Key ?? _mapping.AllColumns.FirstOrDefault()}";

                // 构建完整 SQL（显式指定列顺序）
                string columns = string.Join(", ", _mapping.AllColumns);
                var sql = $"SELECT {columns} FROM {tableName}";
                if (!string.IsNullOrEmpty(whereClause))
                    sql += $" WHERE {whereClause}";
                sql += $" {orderBy} LIMIT {offset}, {pageSize}";

                using var cmd = CreateCommand(sql, parameters);
                using var reader = await cmd.ExecuteReaderAsync(token);
                var results = new List<T>();
                while (await reader.ReadAsync(token))
                    results.Add(_rowReader(reader));
                return results;
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
            if (string.IsNullOrWhiteSpace(tableName))
                tableName = _mapping.TableName;

            var sqlExpr = new SqlExpression<T>(_mapping, Enums.DBType.ClickHouse);
            if (expression != null)
                sqlExpr.Where(expression);

            var (whereClause, parameters) = sqlExpr.BuildWhereClause();
            string columns = string.Join(", ", _mapping.AllColumns);
            var sql = $"SELECT {columns} FROM {tableName} FINAL";
            if (!string.IsNullOrEmpty(whereClause))
                sql += $" WHERE {whereClause}";
            sql += $" ORDER BY {_mapping.Key ?? _mapping.AllColumns.FirstOrDefault()}";

            using var cmd = CreateCommand(sql, parameters);
            using var reader = await cmd.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
                yield return _rowReader(reader);
        }
        public async IAsyncEnumerable<TValue> QueryStreamAsync<TValue>(
            string columnName,
            Expression<Func<T, bool>>? expression = null,
            string tableName = "",
            [EnumeratorCancellation] CancellationToken token = default)
        {
            if (string.IsNullOrWhiteSpace(tableName))
                tableName = _mapping.TableName;

            var sqlExpr = new SqlExpression<T>(_mapping, Enums.DBType.ClickHouse);
            if (expression != null)
                sqlExpr.Where(expression);

            var (whereClause, parameters) = sqlExpr.BuildWhereClause();
            var sql = $"SELECT {columnName} FROM {tableName} FINAL";
            if (!string.IsNullOrEmpty(whereClause))
                sql += $" WHERE {whereClause}";

            using var cmd = CreateCommand(sql, parameters);
            using var reader = await cmd.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                // 读取当前行的第一列（索引0）
                var rawValue = reader.IsDBNull(0) ? null : reader.GetValue(0);
                yield return ConvertValue<TValue>(rawValue);
            }
        }
        public async ValueTask<IEnumerable<T>> QueryAsync(
            Expression<Func<T, bool>>? expression = null,
            string tableName = "",
            CancellationToken token = default)
        {
            if (string.IsNullOrWhiteSpace(tableName))
                tableName = _mapping.TableName;
            string columns = string.Join(", ", _mapping.AllColumns);
            var sql = $"SELECT {columns} FROM {tableName}  FINAL";
            try
            {
                var sqlExpr = new SqlExpression<T>(_mapping, Enums.DBType.ClickHouse);
                if (expression != null)
                    sqlExpr.Where(expression);

                var (whereClause, parameters) = sqlExpr.BuildWhereClause();


                if (!string.IsNullOrEmpty(whereClause))
                    sql += $" WHERE {whereClause}";

                using var cmd = CreateCommand(sql, parameters);
                using var reader = await cmd.ExecuteReaderAsync(token);
                var results = new List<T>();
                while (await reader.ReadAsync(token))
                    results.Add(_rowReader(reader));
                return results;
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
            if (string.IsNullOrWhiteSpace(tableName))
                tableName = _mapping.TableName;
            string sql = string.Empty;
            try
            {
                var (projectionInfo, mapper) = GetOrCreateMapper(selector);
                var columnNames = projectionInfo.ColumnNames;
                var sqlExpr = new SqlExpression<T>(_mapping, Enums.DBType.ClickHouse);
                if (filter != null) sqlExpr.Where(filter);
                var (whereClause, parameters) = sqlExpr.BuildWhereClause();

                var columns = string.Join(", ", columnNames);
                sql = $"SELECT {columns} FROM {tableName} FINAL";
                if (!string.IsNullOrEmpty(whereClause))
                    sql += $" WHERE {whereClause}";
                sql += $" ORDER BY {_mapping.Key ?? _mapping.AllColumns.FirstOrDefault()}";

                using var cmd = CreateCommand(sql, parameters);
                using var reader = await cmd.ExecuteReaderAsync(token);
                var results = new List<TValue>();
                while (await reader.ReadAsync(token))
                    results.Add(mapper(reader));

                return results;
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
                if (string.IsNullOrWhiteSpace(tableName))
                    tableName = _mapping.TableName;

                // 构建 SQL
                var sqlExpr = new SqlExpression<T>(_mapping, Enums.DBType.ClickHouse);
                if (filter != null)
                    sqlExpr.Where(filter);
                var (whereClause, parameters) = sqlExpr.BuildWhereClause();

                sql = $"SELECT {columnName} FROM {tableName} FINAL";
                if (!string.IsNullOrEmpty(whereClause))
                    sql += $" WHERE {whereClause}";

                using var cmd = CreateCommand(sql, parameters);
                using var reader = await cmd.ExecuteReaderAsync(token);

                var results = new List<TValue>();
                while (await reader.ReadAsync(token))
                {
                    // 处理 DBNull 和类型转换
                    var value = reader.IsDBNull(0) ? null : reader.GetValue(0);
                    var converted = ConvertValue<TValue>(value);
                    results.Add(converted);
                }
                return results;
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
            if (string.IsNullOrWhiteSpace(tableName))
                tableName = _mapping.TableName;

            var (projectionInfo, mapper) = GetOrCreateMapper(selector);
            var columnNames = projectionInfo.ColumnNames;
            var columns = string.Join(", ", columnNames);

            var sqlExpr = new SqlExpression<T>(_mapping, Enums.DBType.ClickHouse);
            if (filter != null)
                sqlExpr.Where(filter);
            var (whereClause, parameters) = sqlExpr.BuildWhereClause();

            // 总数查询
            var countSql = $"SELECT COUNT(*) FROM {tableName} FINAL";
            if (!string.IsNullOrEmpty(whereClause))
                countSql += $" WHERE {whereClause}";
            using var countCmd = CreateCommand(countSql, parameters);
            var totalCount = Convert.ToInt64(await countCmd.ExecuteScalarAsync(token));

            if (totalCount == 0)
                return new PagedResult<TValue>([], pageIndex, pageSize, 0);

            // 分页查询
            int offset = pageIndex * pageSize;
            var dataSql = $"SELECT {columns} FROM {tableName} FINAL";
            if (!string.IsNullOrEmpty(whereClause))
                dataSql += $" WHERE {whereClause}";
            var orderByClause = string.IsNullOrEmpty(orderBy)
                ? $"ORDER BY {_mapping.Key ?? _mapping.AllColumns.FirstOrDefault()}"
                : $"ORDER BY {orderBy}";
            dataSql += $" {orderByClause} LIMIT {offset}, {pageSize}";

            using var dataCmd = CreateCommand(dataSql, parameters);
            using var reader = await dataCmd.ExecuteReaderAsync(token);
            var items = new List<TValue>();
            while (await reader.ReadAsync(token))
                items.Add(mapper(reader));

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
            if (string.IsNullOrWhiteSpace(tableName))
                tableName = _mapping.TableName;

            var sqlExpr = new SqlExpression<T>(_mapping, Enums.DBType.ClickHouse);
            if (filter != null)
                sqlExpr.Where(filter);
            var (whereClause, parameters) = sqlExpr.BuildWhereClause();

            // 1. 获取总数（COUNT）
            var countSql = $"SELECT COUNT(*) FROM {tableName} FINAL";
            if (!string.IsNullOrEmpty(whereClause))
                countSql += $" WHERE {whereClause}";
            using var countCmd = CreateCommand(countSql, parameters);
            var totalCount = Convert.ToInt64(await countCmd.ExecuteScalarAsync(token));

            if (totalCount == 0)
                return new PagedResult<TValue>([], pageIndex, pageSize, 0);

            // 2. 分页获取数据
            int offset = pageIndex * pageSize;
            var dataSql = $"SELECT {columnName} FROM {tableName} FINAL";
            if (!string.IsNullOrEmpty(whereClause))
                dataSql += $" WHERE {whereClause}";
            dataSql += $" ORDER BY {_mapping.Key ?? _mapping.AllColumns.FirstOrDefault()} LIMIT {offset}, {pageSize}";

            using var dataCmd = CreateCommand(dataSql, parameters);
            using var reader = await dataCmd.ExecuteReaderAsync(token);
            var items = new List<TValue>();
            while (await reader.ReadAsync(token))
            {
                var value = reader.IsDBNull(0) ? null : reader.GetValue(0);
                items.Add(ConvertValue<TValue>(value));
            }

            return new PagedResult<TValue>(items, pageIndex, pageSize, totalCount);
        }


        #region 辅助方法
        private static TValue ConvertValue<TValue>(object? value)
        {
            if (value == null || value == DBNull.Value)
            {
                // 如果目标类型是引用类型或 Nullable<T>，返回 default
                if (typeof(TValue).IsClass || Nullable.GetUnderlyingType(typeof(TValue)) != null)
                    return default!;
                throw new InvalidOperationException($"Cannot convert NULL to non-nullable type {typeof(TValue)}");
            }

            Type targetType = typeof(TValue);
            Type underlyingType = Nullable.GetUnderlyingType(targetType) ?? targetType;

            // 如果类型完全匹配，直接返回（避免不必要的转换）
            if (value.GetType() == underlyingType)
                return (TValue)value;

            // 枚举处理
            if (underlyingType.IsEnum)
            {
                // 支持整数、字符串转换
                if (value is string strVal)
                    return (TValue)Enum.Parse(underlyingType, strVal);
                // 尝试转换为枚举的基础类型（通常是 int）
                var enumBase = Enum.GetUnderlyingType(underlyingType);
                var convertedBase = Convert.ChangeType(value, enumBase);
                return (TValue)Enum.ToObject(underlyingType, convertedBase);
            }

            // 字符串直接转换
            if (value is string s)
            {
                if (underlyingType == typeof(Guid))
                    return (TValue)(object)Guid.Parse(s);
                if (underlyingType == typeof(DateTime))
                    return (TValue)(object)DateTime.Parse(s);
                // 其他类型通过 ChangeType（字符串通常支持 IConvertible）
                return (TValue)Convert.ChangeType(s, underlyingType);
            }

            // 处理数值类型间的转换（通过 Convert 类）
            // 注意：Convert.ChangeType 对于某些类型（如 ulong -> long）会失败，改用强制转换或更宽松的方式
            try
            {
                // 尝试直接转换（部分CLR类型兼容）
                return (TValue)Convert.ChangeType(value, underlyingType);
            }
            catch (InvalidCastException)
            {
                // 如果 ChangeType 失败，尝试通过字符串中转（效率稍低但兼容性好）
                string intermediate = value.ToString()!;
                return (TValue)Convert.ChangeType(intermediate, underlyingType);
            }
        }
        /// <summary>
        /// 创建从 IDataRecord 到 TResult 的映射委托
        /// </summary>
        private Func<IDataRecord, TResult> CreateMapper<TResult>(ProjectionInfo info) where TResult : class
        {
            var recordParam = Expression.Parameter(typeof(IDataRecord), "record");
            var resultVar = Expression.Variable(typeof(TResult), "result");

            // 创建 TResult 实例
            var newResult = Expression.New(typeof(TResult));
            var assignments = new List<Expression> { Expression.Assign(resultVar, newResult) };

            // 为每个属性生成赋值表达式
            foreach (var (property, columnName) in info.PropertyAssignments)
            {
                // 找到列索引（根据列名顺序，默认按 SELECT 顺序）
                int columnIndex = Array.IndexOf(info.ColumnNames, columnName);
                if (columnIndex < 0)
                    continue;

                // 读取值：record.GetValue(index)
                var getValue = Expression.Call(recordParam, nameof(IDataRecord.GetValue), null, Expression.Constant(columnIndex));
                var isDbNull = Expression.Call(recordParam, nameof(IDataRecord.IsDBNull), null, Expression.Constant(columnIndex));

                // 处理 null 和类型转换
                var propertyType = property.PropertyType;
                var valueExpr = Expression.Condition(isDbNull,
                    Expression.Constant(null, typeof(object)),
                    getValue);

                // 转换到目标类型（处理可空、枚举等）
                var convertedValue = ClickhouseRepository<T>.ConvertToExpression(valueExpr, propertyType);

                // result.Property = convertedValue
                var propertyAccess = Expression.Property(resultVar, property);
                var assign = Expression.Assign(propertyAccess, convertedValue);
                assignments.Add(assign);
            }

            assignments.Add(resultVar);
            var block = Expression.Block(new[] { resultVar }, assignments);
            return Expression.Lambda<Func<IDataRecord, TResult>>(block, recordParam).Compile();
        }
        /// <summary>
        /// 将表达式值转换为目标类型的表达式树
        /// </summary>
        private static Expression ConvertToExpression(Expression valueExpr, Type targetType)
        {
            var underlyingType = Nullable.GetUnderlyingType(targetType) ?? targetType;
            if (underlyingType.IsEnum)
            {
                // 枚举转换：先转为枚举基础类型（int），再转为枚举
                var intValue = Expression.Convert(valueExpr, typeof(int));
                var enumValue = Expression.Convert(intValue, underlyingType);
                return Expression.Convert(enumValue, targetType);
            }

            // 普通类型转换（包括可空类型）
            return Expression.Convert(valueExpr, targetType);
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
        private (ProjectionInfo, Func<IDataRecord, TValue>) GetOrCreateMapper<TValue>(
       Expression<Func<T, TValue>> selector) where TValue : class
        {
            // 缓存键组合：目标类型全名 + 表达式的字符串形式
            string key = $"{typeof(TValue).FullName}_{selector.ToString()}";
            if (_projectionCache.TryGetValue(key, out object? cached) &&
        cached is (ProjectionInfo proj, Func<IDataRecord, TValue> mapper))
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
