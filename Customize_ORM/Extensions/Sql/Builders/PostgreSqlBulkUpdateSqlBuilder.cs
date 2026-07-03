using Customize_ORM.Extensions;

namespace Customize_ORM.Extensions.Sql
{
    /// <summary>
    /// PostgreSQL 批量更新 SQL 构建器，统一生成 staging 表、临时表和 UPDATE FROM 语句。
    /// </summary>
    internal sealed class PostgreSqlBulkUpdateSqlBuilder<T>
    {
        private readonly EntityMapping<T> _mapping;

        public PostgreSqlBulkUpdateSqlBuilder(EntityMapping<T> mapping)
        {
            _mapping = mapping ?? throw new ArgumentNullException(nameof(mapping));
        }

        public string BuildColumnDefinitions(IEnumerable<string> columns)
        {
            ArgumentNullException.ThrowIfNull(columns);

            var definitions = columns
                .Select(column => $"{column} {GetPostgreSqlColumnType(column)}")
                .ToArray();

            if (definitions.Length == 0)
            {
                throw new ArgumentException("Columns cannot be empty.", nameof(columns));
            }

            return string.Join(", ", definitions);
        }

        public string BuildCreateStagingTableSql(string stagingTableName)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(stagingTableName);

            var otherColumns = _mapping.AllColumns.Where(column => !string.Equals(column, _mapping.Key, StringComparison.OrdinalIgnoreCase));
            var auditColumn = ResolveStagingAuditColumnName();

            return $@"
                        CREATE UNLOGGED TABLE IF NOT EXISTS {stagingTableName} (
                            batch_id UUID NOT NULL,
                            {_mapping.Key} {GetPostgreSqlColumnType(_mapping.Key)} NOT NULL,
                            {BuildColumnDefinitions(otherColumns)},
                            {auditColumn} TIMESTAMP WITH TIME ZONE DEFAULT NOW()
                        );";
        }

        public string BuildCreateTemporaryTableSql(string tableName, IEnumerable<string> columns)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
            return $@"CREATE TEMPORARY TABLE {tableName}({BuildColumnDefinitions(columns)}) ON  COMMIT DROP";
        }

        public string BuildCopySql(string tableName, IEnumerable<string> columns, bool includeBatchId = false)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
            ArgumentNullException.ThrowIfNull(columns);

            var columnList = includeBatchId
                ? $"batch_id,{string.Join(",", columns)}"
                : string.Join(",", columns);

            if (string.IsNullOrWhiteSpace(columnList))
            {
                throw new ArgumentException("Columns cannot be empty.", nameof(columns));
            }

            return $"COPY {tableName} ({columnList}) FROM STDIN (FORMAT BINARY)";
        }

        public string BuildUpdateFromSourceSql(
            string targetTableName,
            string sourceTableName,
            string sourceAlias,
            IEnumerable<string> updateColumns,
            string? additionalWhereClause = null,
            string? sourcePredicate = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(targetTableName);
            ArgumentException.ThrowIfNullOrWhiteSpace(sourceTableName);
            ArgumentException.ThrowIfNullOrWhiteSpace(sourceAlias);
            ArgumentNullException.ThrowIfNull(updateColumns);

            var setClauses = updateColumns.Select(column => $"{column} = {sourceAlias}.{column}").ToArray();
            var whereClause = BuildJoinWhereClause(targetTableName, sourceAlias, additionalWhereClause);
            if (!string.IsNullOrWhiteSpace(sourcePredicate))
            {
                whereClause = $"{sourcePredicate} AND {whereClause}";
            }

            return $@"
                UPDATE {targetTableName}
                SET {string.Join(", ", setClauses)}
                FROM {sourceTableName} AS {sourceAlias}
                WHERE {whereClause}";
        }

        public string BuildLegacyTemporaryUpdateSql(
            string targetTableName,
            string sourceTableName,
            IEnumerable<string> updateColumns,
            string? additionalWhereClause = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(targetTableName);
            ArgumentException.ThrowIfNullOrWhiteSpace(sourceTableName);
            ArgumentNullException.ThrowIfNull(updateColumns);

            var setClauses = string.Join(", ", updateColumns.Select(column => $"{column} = {sourceTableName}.{column}"));
            var whereClause = BuildJoinWhereClause(targetTableName, sourceTableName, additionalWhereClause, legacyTemporarySpacing: true);

            // 保留临时表策略原有 SQL 格式，降低重构期间的行为差异。
            return $@" UPDATE {targetTableName} 
                                            SET {setClauses}
                                            FROM {sourceTableName} 
                                            WHERE {whereClause}";
        }

        private string BuildJoinWhereClause(
            string targetTableName,
            string sourceReference,
            string? additionalWhereClause,
            bool legacyTemporarySpacing = false)
        {
            var whereClause = legacyTemporarySpacing
                ? $"{targetTableName}.{_mapping.Key} = {sourceReference}.{_mapping.Key} AND {targetTableName}.{_mapping.PartitionKey}= {sourceReference}.{_mapping.PartitionKey}"
                : $"{targetTableName}.{_mapping.Key} = {sourceReference}.{_mapping.Key} AND {targetTableName}.{_mapping.PartitionKey} = {sourceReference}.{_mapping.PartitionKey}";

            if (!string.IsNullOrWhiteSpace(additionalWhereClause))
            {
                whereClause += $" AND {additionalWhereClause}";
            }

            return whereClause;
        }

        private string GetPostgreSqlColumnType(string columnName)
        {
            if (!_mapping.PropertyMap.TryGetValue(columnName, out var property))
            {
                return "text";
            }

            if (_mapping.JsonbColumns.Contains(columnName))
            {
                return "jsonb";
            }

            var propertyType = property.PropertyType;
            if (propertyType.IsArray)
            {
                var elementType = propertyType.GetElementType();
                if (elementType != null)
                {
                    return PostgreSqlTypeMapper.GetPostgreSqlArrayType(elementType);
                }
            }

            return PostgreSqlTypeMapper.GetNpgsqlDbType(propertyType).ToString().ToLowerInvariant();
        }

        private string ResolveStagingAuditColumnName()
        {
            const string defaultAuditColumnName = "created_at";
            // 实体自身可能已有 created_at 字段，staging 元数据列需要避让。
            return _mapping.PropertyMap.ContainsKey(defaultAuditColumnName)
                ? "staging_created_at"
                : defaultAuditColumnName;
        }
    }
}
