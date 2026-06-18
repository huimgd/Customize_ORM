using Customize_ORM.Enums;
using System.Data.Common;

namespace Customize_ORM.Extensions.Sql
{
    /// <summary>
    /// 为表达式构建定义特定于数据库的SQL参数行为。
    /// </summary>
    public abstract class SqlDialect
    {
        private static readonly SqlDialect PostgreSql = new PostgreSqlDialect();
        private static readonly SqlDialect ClickHouse = new ClickHouseDialect();
        private static readonly SqlDialect MySql = new MySqlDialect();

        public abstract string ParameterPrefix { get; }

        public virtual string BuildLimitClause(int limit)
        {
            if (limit <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(limit), "Limit must be greater than zero.");
            }

            return $" LIMIT {limit}";
        }

        public virtual string BuildPaginationClause(int pageNumber, int pageSize)
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
            return $" LIMIT {offset}, {pageSize}";
        }

        public virtual object? NormalizeParameterValue(object? value) => value;

        public virtual string RenderLiteral(object? value)
        {
            if (value == null || value == DBNull.Value)
            {
                return "NULL";
            }

            var type = Nullable.GetUnderlyingType(value.GetType()) ?? value.GetType();

            if (type == typeof(bool))
            {
                return (bool)value ? "true" : "false";
            }

            if (type == typeof(string))
            {
                var escaped = ((string)value).Replace("'", "''");
                return $"'{escaped}'";
            }

            if (type == typeof(char))
            {
                return $"'{value}'";
            }

            if (type == typeof(DateTime))
            {
                var formatted = ((DateTime)value).ToString("yyyy-MM-dd HH:mm:ss.fff");
                return $"'{formatted}'";
            }

            if (type == typeof(DateTimeOffset))
            {
                var formatted = ((DateTimeOffset)value).ToString("yyyy-MM-dd HH:mm:ss.fff");
                return $"'{formatted}'";
            }

            if (type == typeof(Guid))
            {
                return $"'{value}'";
            }

            if (type.IsEnum)
            {
                var underlyingType = Enum.GetUnderlyingType(type);
                var numericValue = Convert.ChangeType(value, underlyingType);
                return numericValue?.ToString() ?? string.Empty;
            }

            if (type.IsValueType)
            {
                return value.ToString()!;
            }

            return $"'{value.ToString()?.Replace("'", "''")}'";
        }

        public DbParameter CreateParameter(string name, object? value)
        {
            var parameter = CreateParameterCore(name, value);

            if (!string.Equals(name, parameter.ParameterName, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"{GetType().Name} returned parameter name '{parameter.ParameterName}' for requested placeholder '{name}'.");
            }

            return parameter;
        }

        protected abstract DbParameter CreateParameterCore(string name, object? value);

        public static SqlDialect For(DBType dbType) => dbType switch
        {
            DBType.PostgreSQL => PostgreSql,
            DBType.ClickHouse => ClickHouse,
            DBType.MySQL => MySql,
            _ => throw new NotSupportedException($"Database type {dbType} not supported")
        };
    }
}
