using Customize_ORM.Extensions;
using Customize_ORM.Extensions.Sql;
using Test.Entities;

namespace Test
{
    /// <summary>
    /// 回归用例：验证虚谷批量更新 SQL 构建器生成的 staging/临时表 DLL 与 UPDATE FROM 语句形态（不连接真实数据库）。
    /// </summary>
    internal static class XuguBulkUpdateSqlBuilderRegression
    {
        private static readonly EntityMapping<BatchUpdateEntity> Mapping = ObjectMapping<BatchUpdateEntity>.MappingForUpdate;

        public static void Run()
        {
            var failures = new List<string>();

            RunCase(
                failures,
                "Xugu bulk update sql builder creates staging and temp table SQL consistently",
                () =>
                {
                    var builder = new XuguBulkUpdateSqlBuilder<BatchUpdateEntity>(Mapping);
                    var columns = new[] { "id", "created_at", "counter", "tags", "status" };

                    AssertExact(
                        "id GUID, created_at DATETIME, counter NUMERIC, tags TEXT, status INTEGER",
                        builder.BuildColumnDefinitions(columns));

                    AssertExact(
                        "\n                        CREATE TABLE staging_batch_update_entity (\n                            batch_id VARCHAR(64) NOT NULL,\n                            id GUID NOT NULL,\n                            created_at DATETIME, counter NUMERIC, tags TEXT, status INTEGER,\n                            staging_created_at DATETIME\n                        );",
                        builder.BuildCreateStagingTableSql("staging_batch_update_entity"));

                    AssertExact(
                        "CREATE TEMPORARY TABLE tmp_batch(id GUID, created_at DATETIME, counter NUMERIC, tags TEXT, status INTEGER)",
                        builder.BuildCreateTemporaryTableSql("tmp_batch", columns));
                });

            RunCase(
                failures,
                "Xugu bulk update sql builder creates insert column list and update sql for staging and temp sources",
                () =>
                {
                    var builder = new XuguBulkUpdateSqlBuilder<BatchUpdateEntity>(Mapping);
                    var columns = new[] { "counter", "tags", "status" };
                    var allColumns = new[] { "id", "created_at", "counter", "tags", "status" };

                    AssertExact(
                        "batch_id,id,created_at,counter,tags,status",
                        builder.BuildInsertColumnList(allColumns, includeBatchId: true));

                    AssertExact(
                        "id,created_at,counter,tags,status",
                        builder.BuildInsertColumnList(allColumns));

                    AssertExact(
                        "\n                UPDATE batch_update_entity_2026061810\n                SET counter = staging.counter, tags = staging.tags, status = staging.status\n                FROM staging_batch_update_entity AS staging\n                WHERE staging.batch_id = :batchId AND batch_update_entity_2026061810.id = staging.id AND batch_update_entity_2026061810.created_at = staging.created_at AND batch_update_entity_2026061810.status <> 9",
                        builder.BuildUpdateFromSourceSql(
                            targetTableName: "batch_update_entity_2026061810",
                            sourceTableName: "staging_batch_update_entity",
                            sourceAlias: "staging",
                            updateColumns: columns,
                            additionalWhereClause: "batch_update_entity_2026061810.status <> 9",
                            sourcePredicate: "staging.batch_id = :batchId"));

                    AssertExact(
                        " UPDATE batch_update_entity_2026061810 \n                                            SET counter = tmp_batch.counter, tags = tmp_batch.tags, status = tmp_batch.status\n                                            FROM tmp_batch \n                                            WHERE batch_update_entity_2026061810.id = tmp_batch.id AND batch_update_entity_2026061810.created_at = tmp_batch.created_at AND batch_update_entity_2026061810.status <> 9",
                        builder.BuildLegacyTemporaryUpdateSql(
                            targetTableName: "batch_update_entity_2026061810",
                            sourceTableName: "tmp_batch",
                            updateColumns: columns,
                            additionalWhereClause: "batch_update_entity_2026061810.status <> 9"));
                });

            if (failures.Count > 0)
            {
                throw new InvalidOperationException(
                    "XuguBulkUpdateSqlBuilder regression failures:" + Environment.NewLine +
                    string.Join(Environment.NewLine + Environment.NewLine, failures));
            }

            Console.WriteLine("XuguBulkUpdateSqlBuilder regression harness passed.");
        }

        private static void AssertExact(string expected, string actual)
        {
            var normalizedExpected = expected.Replace("\r\n", "\n", StringComparison.Ordinal);
            var normalizedActual = actual.Replace("\r\n", "\n", StringComparison.Ordinal);
            if (!string.Equals(normalizedExpected, normalizedActual, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"sql mismatch{Environment.NewLine}expected: {expected}{Environment.NewLine}actual:   {actual}");
            }
        }

        private static void RunCase(List<string> failures, string name, Action assertion)
        {
            try
            {
                assertion();
                Console.WriteLine($"[PASS] {name}");
            }
            catch (Exception ex)
            {
                failures.Add($"[FAIL] {name}{Environment.NewLine}{ex}");
                Console.WriteLine($"[FAIL] {name}");
            }
        }
    }
}
