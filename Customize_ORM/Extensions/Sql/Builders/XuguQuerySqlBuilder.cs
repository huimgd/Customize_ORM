using Customize_ORM.Interfaces.Sql;
using System.Text;

namespace Customize_ORM.Extensions.Sql
{
    /// <summary>
    /// 虚谷查询 SQL 构建器：统一生成 SELECT/COUNT/UPDATE 与多值 VALUES 批量 INSERT 语句。
    /// 虚谷无 COPY，批量写入依赖 "INSERT ... VALUES(...)(...)..." 多元组语法。
    /// </summary>
    internal sealed class XuguQuerySqlBuilder<T> : IQuerySqlBuilder<T>, IBulkInsertSqlBuilder<T>
    {
        /// <summary>
        /// 单条多值 INSERT 语句允许承载的最大行数，避免语句过长和驱动解析失败。
        /// </summary>
        public const int MaxRowsPerInsert = 500;

        public XuguQuerySqlBuilder(EntityMapping<T> mapping, string? tableName = null)
        {
            ArgumentNullException.ThrowIfNull(mapping);

            Mapping = mapping;
            TableName = string.IsNullOrWhiteSpace(tableName) ? mapping.TableName : tableName;
            AllColumns = string.Join(", ", mapping.AllColumns);
        }

        public EntityMapping<T> Mapping { get; }

        public string TableName { get; }

        public string AllColumns { get; }

        public string Count()
            => $"SELECT COUNT(*) FROM {TableName}";

        public string SelectAll()
            => $"SELECT {AllColumns} FROM {TableName}";

        public string Select(string columns)
        {
            if (string.IsNullOrWhiteSpace(columns))
            {
                throw new ArgumentException("Select columns cannot be empty.", nameof(columns));
            }

            return $"SELECT {columns} FROM {TableName}";
        }

        public string Update(IEnumerable<string> columns)
        {
            ArgumentNullException.ThrowIfNull(columns);

            var setClauses = columns
                .Select(column => $"{column} = :{column}")
                .ToArray();

            if (setClauses.Length == 0)
            {
                throw new ArgumentException("Update columns cannot be empty.", nameof(columns));
            }

            return $"UPDATE {TableName} SET {string.Join(", ", setClauses)}";
        }

        public string ApplyWhere(string sql, string? whereClause)
            => string.IsNullOrWhiteSpace(whereClause)
                ? sql
                : $"{sql} WHERE {whereClause}";

        public string WhereByKey()
            => $"{Mapping.Key} = :{Mapping.Key}";

        /// <summary>
        /// 生成单条 INSERT 语句，占位符直接使用列名（如 :id）。
        /// </summary>
        /// <param name="columns">插入列。</param>
        public string BuildSingleInsertSql(IEnumerable<string> columns)
        {
            ArgumentNullException.ThrowIfNull(columns);

            var columnList = string.Join(", ", columns);
            if (string.IsNullOrWhiteSpace(columnList))
            {
                throw new ArgumentException("Insert columns cannot be empty.", nameof(columns));
            }

            var placeholders = string.Join(", ", columns.Select(column => $":{column}"));
            return $"INSERT INTO {TableName} ({columnList}) VALUES ({placeholders})";
        }

        /// <summary>
        /// 虚谷无 COPY 二进制导入能力，调用即抛异常，避免静默返回无效 SQL。
        /// </summary>
        /// <param name="columns">导入列</param>
        public string CopyBinaryImport(IEnumerable<string> columns)
            => throw new NotSupportedException("虚谷不支持 COPY 二进制导入，请使用多值 INSERT。");

        /// <summary>
        /// 生成多值 INSERT 语句；占位符按行索引命名（如 :r0_id），避免不同行同名参数冲突，参数由仓储逐行创建。
        /// </summary>
        /// <param name="columns">插入列。</param>
        /// <param name="rowCount">本语句承载的行数，须在 [1, MaxRowsPerInsert] 范围内。</param>
        public string BuildNamedInsertSql(IEnumerable<string> columns, int rowCount)
        {
            ArgumentNullException.ThrowIfNull(columns);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rowCount);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(rowCount, MaxRowsPerInsert);

            var columnList = string.Join(", ", columns);
            if (string.IsNullOrWhiteSpace(columnList))
            {
                throw new ArgumentException("Insert columns cannot be empty.", nameof(columns));
            }

            var sql = new StringBuilder();
            sql.Append($"INSERT INTO {TableName} ({columnList}) VALUES ");
            for (var row = 0; row < rowCount; row++)
            {
                if (row > 0)
                {
                    sql.Append(',');
                }

                sql.Append('(');
                sql.Append(string.Join(",", columns.Select(column => $":r{row}_{column}")));
                sql.Append(')');
            }

            return sql.ToString();
        }
    }
}
