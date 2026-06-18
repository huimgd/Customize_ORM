using Customize_ORM.Extensions;

namespace Customize_ORM.Extensions.Sql
{
    /// <summary>
    /// PostgreSQL 基础查询 SQL 构建器，集中生成仓储常用的 SELECT、COUNT、UPDATE 和 COPY 语句。
    /// </summary>
    internal sealed class PostgreSqlQuerySqlBuilder<T>
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
    }
}
