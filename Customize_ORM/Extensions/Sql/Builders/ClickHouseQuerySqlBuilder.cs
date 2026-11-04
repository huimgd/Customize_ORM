using Customize_ORM.Extensions;
using Customize_ORM.Interfaces.Sql;

namespace Customize_ORM.Extensions.Sql
{
    /// <summary>
    /// ClickHouse 查询 SQL 构建器，统一处理 FINAL、默认排序、分页和基础 SELECT/COUNT 片段。
    /// </summary>
    internal sealed class ClickHouseQuerySqlBuilder<T> : IQuerySqlBuilder<T>
    {
        private readonly string _defaultOrderColumn;

        public ClickHouseQuerySqlBuilder(EntityMapping<T> mapping, string? tableName = null)
        {
            ArgumentNullException.ThrowIfNull(mapping);

            TableName = string.IsNullOrWhiteSpace(tableName) ? mapping.TableName : tableName;
            AllColumns = string.Join(", ", mapping.AllColumns);
            _defaultOrderColumn = !string.IsNullOrWhiteSpace(mapping.Key)
                ? mapping.Key
                : mapping.AllColumns.FirstOrDefault()
                    ?? throw new InvalidOperationException($"{typeof(T).Name} does not define any mapped columns.");
            DefaultOrderByClause = $"ORDER BY {_defaultOrderColumn}";
        }

        public string TableName { get; }

        public string AllColumns { get; }

        public string DefaultOrderByClause { get; }

        public string Count(bool final = false)
            => $"SELECT COUNT(*) FROM {TableName}{(final ? " FINAL" : string.Empty)}";

        /// <summary>
        /// 接口约定的无参 COUNT。
        /// </summary>
        string IQuerySqlBuilder<T>.Count() => Count();

        public string SelectAll(bool final = false)
            => Select(AllColumns, final);

        /// <summary>
        /// 接口约定的全列 SELECT。
        /// </summary>
        string IQuerySqlBuilder<T>.SelectAll() => SelectAll();

        public string Select(string columns, bool final = false)
        {
            if (string.IsNullOrWhiteSpace(columns))
            {
                throw new ArgumentException("Select columns cannot be empty.", nameof(columns));
            }

            return $"SELECT {columns} FROM {TableName}{(final ? " FINAL" : string.Empty)}";
        }

        public string ApplyWhere(string sql, string? whereClause)
            => string.IsNullOrWhiteSpace(whereClause)
                ? sql
                : $"{sql} WHERE {whereClause}";

        /// <summary>
        /// ClickHouse 不支持单行 UPDATE，接口方法仅用于满足契约。
        /// </summary>
        /// <param name="columns">需要更新的列</param>
        public string Update(IEnumerable<string> columns)
            => throw new NotSupportedException($"ClickHouse 不支持单行 UPDATE 语句：{typeof(T).Name}。");

        /// <summary>
        /// ClickHouse 不支持按主键单行定位更新，接口方法仅用于满足契约。
        /// </summary>
        public string WhereByKey()
            => throw new NotSupportedException($"ClickHouse 不支持按主键单行定位更新：{typeof(T).Name}。");

        public string ResolveOrderBy(string? orderByClause = null)
        {
            if (string.IsNullOrWhiteSpace(orderByClause))
            {
                return DefaultOrderByClause;
            }

            var trimmed = orderByClause.Trim();
            return trimmed.StartsWith("ORDER BY ", StringComparison.OrdinalIgnoreCase)
                ? trimmed
                : $"ORDER BY {trimmed}";
        }

        public string ApplyOrderBy(string sql, string? orderByClause = null)
            => $"{sql} {ResolveOrderBy(orderByClause)}";

        public string ApplyLimit(string sql, int offset, int pageSize)
        {
            if (offset < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(offset), offset, "Offset cannot be negative.");
            }

            if (pageSize <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(pageSize), pageSize, "Page size must be greater than zero.");
            }

            return $"{sql} LIMIT {offset}, {pageSize}";
        }
    }
}
