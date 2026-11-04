using Customize_ORM.Enums;
using Customize_ORM.Interfaces.Sql;
using System.Data.Common;

namespace Customize_ORM.Extensions.Sql
{
    /// <summary>
    /// 为表达式构建定义特定于数据库的SQL参数行为。
    /// 实现统一的 <see cref="IQueryDialect"/> 契约，各数据库各自实现抽象成员。
    /// </summary>
    public abstract class SqlDialect : IQueryDialect
    {
        private static readonly IQueryDialect PostgreSql = new PostgreSqlDialect();
        private static readonly IQueryDialect ClickHouse = new ClickHouseDialect();
        private static readonly IQueryDialect MySql = new MySqlDialect();
        private static readonly IQueryDialect Xugu = new XuguDialect();

        /// <summary>
        /// 当前方言对应的数据库类型。
        /// </summary>
        public abstract DBType DbType { get; }

        /// <summary>
        /// 获取当前方言使用的参数前缀。
        /// </summary>
        public abstract string ParameterPrefix { get; }

        /// <summary>
        /// 构建 LIMIT 子句。
        /// </summary>
        public virtual string BuildLimitClause(int limit)
        {
            if (limit <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(limit), "Limit must be greater than zero.");
            }

            return $" LIMIT {limit}";
        }

        /// <summary>
        /// 构建分页子句。
        /// </summary>
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

        /// <summary>
        /// 规范化参数值，便于特定驱动绑定。
        /// </summary>
        public virtual object? NormalizeParameterValue(object? value) => value;

        /// <summary>
        /// 将任意 SQL 片段包装为 TRIM 调用。
        /// </summary>
        public virtual string RenderTrim(string sqlExpression)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sqlExpression);
            return $"TRIM({sqlExpression})";
        }

        /// <summary>
        /// 将任意 SQL 片段包装为 LOWER 调用。
        /// </summary>
        public virtual string RenderLower(string sqlExpression)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sqlExpression);
            return $"LOWER({sqlExpression})";
        }

        /// <summary>
        /// 将任意 SQL 片段包装为 UPPER 调用。
        /// </summary>
        public virtual string RenderUpper(string sqlExpression)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sqlExpression);
            return $"UPPER({sqlExpression})";
        }
        /// <summary>
        /// 将标量 SQL 片段转换为字符串表达式。
        /// </summary>
        public virtual string RenderToString(string sqlExpression, Type valueType)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sqlExpression);
            ArgumentNullException.ThrowIfNull(valueType);
            return $"CAST({sqlExpression} AS CHAR)";
        }

        /// <summary>
        /// 将参数值渲染为 SQL 字面量。
        /// </summary>
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

        /// <summary>
        /// 创建参数实例，并校验返回名称与请求占位符一致。
        /// </summary>
        /// <param name="name">参数名（含占位符前缀）</param>
        /// <param name="value">参数值</param>
        /// <param name="propertyType">实体属性 CLR 类型；需要按属性类型选择绑定方式的方言（如 PostgreSQL 数组）会使用它</param>
        public DbParameter CreateParameter(string name, object? value, Type propertyType)
        {
            ArgumentNullException.ThrowIfNull(propertyType);

            var parameter = CreateParameterCore(name, value, propertyType);

            if (!string.Equals(name, parameter.ParameterName, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"{GetType().Name} returned parameter name '{parameter.ParameterName}' for requested placeholder '{name}'.");
            }

            return parameter;
        }

        /// <summary>
        /// 由具体方言实现真实参数对象创建。默认忽略属性类型，仅按值创建参数。
        /// </summary>
        /// <param name="name">参数名（含占位符前缀）</param>
        /// <param name="value">参数值</param>
        /// <param name="propertyType">实体属性 CLR 类型</param>
        protected virtual DbParameter CreateParameterCore(string name, object? value, Type propertyType)
            => CreateParameterCore(name, value);

        /// <summary>
        /// 由具体方言实现真实参数对象创建（不关心属性类型时的简化重载）。
        /// </summary>
        protected abstract DbParameter CreateParameterCore(string name, object? value);

        /// <summary>
        /// 构建"按主键集合删除"的语句。
        /// 各库语法差异明显：PostgreSQL 可用数组参数 ANY(@ids)，虚谷需展开为 IN (:key0, :key1, ...)。
        /// </summary>
        /// <param name="tableName">目标表名</param>
        /// <param name="keyColumn">主键列名</param>
        /// <param name="keyValues">主键值集合</param>
        /// <param name="keyType">主键值的 CLR 类型</param>
        /// <param name="parameters">输出：本语句需要绑定的参数集合</param>
        public abstract string BuildDeleteByKeysSql(
            string tableName,
            string keyColumn,
            IReadOnlyList<object?> keyValues,
            Type keyType,
            out List<DbParameter> parameters);

        /// <summary>
        /// 根据数据库类型获取方言实现（全局唯一的方言解析入口）。
        /// </summary>
        /// <param name="dbType">数据库类型</param>
        public static IQueryDialect For(DBType dbType) => dbType switch
        {
            DBType.PostgreSQL => PostgreSql,
            DBType.ClickHouse => ClickHouse,
            DBType.MySQL => MySql,
            DBType.Xugu => Xugu,
            _ => throw new NotSupportedException($"Database type {dbType} not supported")
        };
    }
}
