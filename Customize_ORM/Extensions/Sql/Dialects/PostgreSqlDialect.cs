using Npgsql;
using System.Data.Common;

namespace Customize_ORM.Extensions.Sql
{
    public sealed class PostgreSqlDialect : SqlDialect
    {
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
        /// 创建 PostgreSQL 参数对象。
        /// </summary>
        protected override DbParameter CreateParameterCore(string name, object? value)
            => new NpgsqlParameter(name, value);
    }
}
