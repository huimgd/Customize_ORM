using Npgsql;
using Customize_ORM.Enums;
using System.Data.Common;

namespace Customize_ORM.Extensions.Sql
{
    public sealed class PostgreSqlDialect : SqlDialect
    {
        public override DBType DbType => DBType.PostgreSQL;

        /// <summary>
        /// PostgreSQL 使用 @ 作为参数前缀。
        /// </summary>
        public override string ParameterPrefix => "@";

        /// <summary>
        /// PostgreSQL 使用 LIMIT size OFFSET offset 语法分页。
        /// </summary>
        public override string BuildPaginationClause(int pageNumber, int pageSize)
        {
            if (pageNumber <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(pageNumber), "Page number must be greater than zero.");
            }

            if (pageSize <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(pageSize), "Page size must be greater than zero.");
            }

            var offset = (pageNumber - 1) * pageSize;
            return $" LIMIT {pageSize} OFFSET {offset}";
        }

        /// <summary>
        /// PostgreSQL 使用 TEXT 作为通用字符串转换目标类型。
        /// </summary>
        public override string RenderToString(string sqlExpression, Type valueType)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sqlExpression);
            ArgumentNullException.ThrowIfNull(valueType);
            return $"CAST({sqlExpression} AS TEXT)";
        }

        /// <summary>
        /// 创建 PostgreSQL 参数对象；数组、jsonb 等需要按属性类型决定绑定方式的列交给参数工厂。
        /// </summary>
        protected override DbParameter CreateParameterCore(string name, object? value, Type propertyType)
            => PostgreSqlParameterFactory.Create(name, value, propertyType);

        /// <summary>
        /// 创建 PostgreSQL 参数对象（不关心属性类型时的兜底）。
        /// </summary>
        protected override DbParameter CreateParameterCore(string name, object? value)
            => new NpgsqlParameter(name, value);

        /// <summary>
        /// PostgreSQL 支持数组参数，主键集合一次绑定为 <c>ANY(@key)</c>，避免 IN 列表随数据量膨胀。
        /// </summary>
        /// <param name="tableName">目标表名</param>
        /// <param name="keyColumn">主键列名</param>
        /// <param name="keyValues">主键值集合</param>
        /// <param name="keyType">主键值的 CLR 类型</param>
        /// <param name="parameters">输出参数集合</param>
        public override string BuildDeleteByKeysSql(
            string tableName,
            string keyColumn,
            IReadOnlyList<object?> keyValues,
            Type keyType,
            out List<DbParameter> parameters)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
            ArgumentException.ThrowIfNullOrWhiteSpace(keyColumn);
            ArgumentNullException.ThrowIfNull(keyValues);
            ArgumentNullException.ThrowIfNull(keyType);

            if (keyValues.Count == 0)
            {
                throw new ArgumentException("Key values cannot be empty.", nameof(keyValues));
            }

            var elementType = Nullable.GetUnderlyingType(keyType) ?? keyType;
            var array = Array.CreateInstance(elementType, keyValues.Count);
            for (var i = 0; i < keyValues.Count; i++)
            {
                array.SetValue(keyValues[i], i);
            }

            var parameterName = $"{ParameterPrefix}{keyColumn}s";
            parameters = [PostgreSqlParameterFactory.Create(parameterName, array, elementType.MakeArrayType())];
            return $"DELETE FROM {tableName} WHERE {keyColumn} = ANY({parameterName})";
        }
    }
}
