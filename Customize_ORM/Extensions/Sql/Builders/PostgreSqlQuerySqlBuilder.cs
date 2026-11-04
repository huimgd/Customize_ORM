using Customize_ORM.Extensions;
using Customize_ORM.Interfaces.Sql;

namespace Customize_ORM.Extensions.Sql
{
    /// <summary>
    /// PostgreSQL 基础查询 SQL 构建器，集中生成仓储常用的 SELECT、COUNT、UPDATE 和 COPY 语句。
    /// </summary>
    internal sealed class PostgreSqlQuerySqlBuilder<T> : IQuerySqlBuilder<T>, IBulkInsertSqlBuilder<T>
    {
        public PostgreSqlQuerySqlBuilder(EntityMapping<T> mapping, string? tableName = null)
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

        /// <summary>
        /// 生成指定列的 SELECT 语句。
        /// </summary>
        /// <param name="columns">逗号分隔的列清单</param>
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
                .Select(column => $"{column} = @{column}")
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
            => $"{Mapping.Key}=@{Mapping.Key}";

        public string CopyBinaryImport(IEnumerable<string> columns)
        {
            ArgumentNullException.ThrowIfNull(columns);

            var columnList = string.Join(",", columns);
            if (string.IsNullOrWhiteSpace(columnList))
            {
                throw new ArgumentException("Copy columns cannot be empty.", nameof(columns));
            }

            return $"COPY {TableName} ({columnList}) FROM STDIN (FORMAT BINARY)";
        }

        /// <summary>
        /// 生成单条 INSERT 语句（占位符使用 @列名）。
        /// </summary>
        /// <param name="columns">插入列</param>
        public string BuildSingleInsertSql(IEnumerable<string> columns)
        {
            ArgumentNullException.ThrowIfNull(columns);

            var columnList = string.Join(", ", columns);
            if (string.IsNullOrWhiteSpace(columnList))
            {
                throw new ArgumentException("Insert columns cannot be empty.", nameof(columns));
            }

            var placeholders = string.Join(", ", columns.Select(column => $"@{column}"));
            return $"INSERT INTO {TableName} ({columnList}) VALUES ({placeholders})";
        }

        /// <summary>
        /// 生成多值 INSERT 语句；占位符按行索引命名（如 @r0_id），避免不同行同名参数冲突。
        /// </summary>
        /// <param name="columns">插入列</param>
        /// <param name="rowCount">本语句承载的行数</param>
        public string BuildNamedInsertSql(IEnumerable<string> columns, int rowCount)
        {
            ArgumentNullException.ThrowIfNull(columns);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rowCount);

            var columnArray = columns.ToArray();
            var columnList = string.Join(", ", columnArray);
            if (string.IsNullOrWhiteSpace(columnList))
            {
                throw new ArgumentException("Insert columns cannot be empty.", nameof(columns));
            }

            var rows = new string[rowCount];
            for (var row = 0; row < rowCount; row++)
            {
                rows[row] = $"({string.Join(",", columnArray.Select(column => $"@r{row}_{column}"))})";
            }

            return $"INSERT INTO {TableName} ({columnList}) VALUES {string.Join(",", rows)}";
        }
    }
}
