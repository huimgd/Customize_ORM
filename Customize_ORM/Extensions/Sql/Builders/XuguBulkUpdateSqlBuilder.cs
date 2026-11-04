using Customize_ORM.Extensions;
using Customize_ORM.Interfaces.Sql;

namespace Customize_ORM.Extensions.Sql
{
    /// <summary>
    /// 虚谷批量更新 SQL 构建器，统一生成 staging 表、临时表和 UPDATE FROM 语句。
    /// </summary>
    internal sealed class XuguBulkUpdateSqlBuilder<T> : IBulkUpdateSqlBuilder<T>
    {
        /// <summary>
        /// staging 表批次号列宽；批次号为 Guid 字符串（36 字符），取 64 保留余量。
        /// </summary>
        private const int BatchIdColumnLength = 64;

        /// <summary>
        /// staging 表创建时间列名；实体已有该字段时加前缀避让。
        /// </summary>
        private const string AuditColumnName = "created_at";
        private const string AuditColumnFallbackName = "staging_created_at";

        private readonly EntityMapping<T> _mapping;

        /// <summary>
        /// 基于 XuguTypeMapper 的 CLR→虚谷 DDL 类型映射生成批量更新用表结构。
        /// </summary>
        public XuguBulkUpdateSqlBuilder(EntityMapping<T> mapping)
        {
            _mapping = mapping ?? throw new ArgumentNullException(nameof(mapping));
        }

        public string BuildColumnDefinitions(IEnumerable<string> columns)
        {
            ArgumentNullException.ThrowIfNull(columns);

            var definitions = columns
                .Select(column => $"{column} {GetXuguColumnType(column)}")
                .ToArray();

            if (definitions.Length == 0)
            {
                throw new ArgumentException("Columns cannot be empty.", nameof(columns));
            }

            return string.Join(", ", definitions);
        }

        /// <summary>
        /// 生成持久化 staging 表 DDL；虚谷无 UNLOGGED 语义，普通表 + 事务内短 UPDATE 已满足批量更新需要。
        /// </summary>
        public string BuildCreateStagingTableSql(string stagingTableName)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(stagingTableName);

            var otherColumns = _mapping.AllColumns.Where(column => !string.Equals(column, _mapping.Key, StringComparison.OrdinalIgnoreCase));
            var auditColumn = ResolveStagingAuditColumnName();

            return $@"
                        CREATE TABLE {stagingTableName} (
                            batch_id VARCHAR({BatchIdColumnLength}) NOT NULL,
                            {_mapping.Key} {GetXuguColumnType(_mapping.Key)} NOT NULL,
                            {BuildColumnDefinitions(otherColumns)},
                            {auditColumn} DATETIME
                        );";
        }

        /// <summary>
        /// 生成 TEMPORARY 临时表 DDL；默认 ON COMMIT 语义未在虚谷文档中确认，显式不追加，事务内建表内用内清。
        /// </summary>
        public string BuildCreateTemporaryTableSql(string tableName, IEnumerable<string> columns)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
            return $@"CREATE TEMPORARY TABLE {tableName}({BuildColumnDefinitions(columns)})";
        }

        /// <summary>
        /// 生成向 staging/临时表写入数据的多值 INSERT 列清单（不含批次号列）。
        /// </summary>
        public string BuildInsertColumnList(IEnumerable<string> columns, bool includeBatchId = false)
        {
            ArgumentNullException.ThrowIfNull(columns);

            var columnList = includeBatchId
                ? $"batch_id,{string.Join(",", columns)}"
                : string.Join(",", columns);

            if (string.IsNullOrWhiteSpace(columnList))
            {
                throw new ArgumentException("Columns cannot be empty.", nameof(columns));
            }

            return columnList;
        }

        /// <summary>
        /// 生成 UPDATE ... FROM 源表语句；源表以别名引用，批次过滤谓词由调用方传占位符参数（:batchId）。
        /// </summary>
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

        /// <summary>
        /// 生成临时表策略 UPDATE 语句；临时表无别名直接以表名引用。
        /// </summary>
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
            var whereClause = BuildJoinWhereClause(targetTableName, sourceTableName, additionalWhereClause);

            return $@" UPDATE {targetTableName} 
                                            SET {setClauses}
                                            FROM {sourceTableName} 
                                            WHERE {whereClause}";
        }

        private string BuildJoinWhereClause(
            string targetTableName,
            string sourceReference,
            string? additionalWhereClause)
        {
            var whereClause = $"{targetTableName}.{_mapping.Key} = {sourceReference}.{_mapping.Key} AND {targetTableName}.{_mapping.PartitionKey} = {sourceReference}.{_mapping.PartitionKey}";

            if (!string.IsNullOrWhiteSpace(additionalWhereClause))
            {
                whereClause += $" AND {additionalWhereClause}";
            }

            return whereClause;
        }

        private string GetXuguColumnType(string columnName)
        {
            if (!_mapping.PropertyMap.TryGetValue(columnName, out var property))
            {
                return XuguTypeMapper.GetXuguColumnType(typeof(string));
            }

            // 数组/JSON/二进制等特殊类型统一交由 XuguTypeMapper 解析（数组列映射 TEXT），与参数侧 JSON 文本口径一致
            return XuguTypeMapper.GetXuguColumnType(property.PropertyType);
        }

        private string ResolveStagingAuditColumnName()
        {
            // staging 表自身的元数据列；实体可能已有 created_at 字段，需避让
            return _mapping.PropertyMap.ContainsKey(AuditColumnName)
                ? AuditColumnFallbackName
                : AuditColumnName;
        }
    }
}
