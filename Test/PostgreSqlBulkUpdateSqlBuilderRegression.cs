using Customize_ORM.Extensions;
using Customize_ORM.Extensions.Sql;
using Test.Entitys;

namespace Test
{
    internal static class PostgreSqlBulkUpdateSqlBuilderRegression
    {
        private static readonly EntityMapping<BatchUpdateEntity> Mapping = ObjectMapping<BatchUpdateEntity>.MappingForUpdate;

        public static void Run()
        {
            var failures = new List<string>();

            RunCase(
                failures,
                "PostgreSQL bulk update sql builder creates staging and temp table SQL consistently",
                () =>
                {
                    var builder = new PostgreSqlBulkUpdateSqlBuilder<BatchUpdateEntity>(Mapping);
                    var columns = new[] { "id", "created_at", "counter", "tags", "status" };

                    AssertExact(
                        "id uuid, created_at timestamp, counter numeric, tags text[], status integer",
                        builder.BuildColumnDefinitions(columns));

                    AssertExact(
                        "\n                        CREATE UNLOGGED TABLE IF NOT EXISTS staging_batch_update_entity (\n                            batch_id UUID NOT NULL,\n                            id uuid NOT NULL,\n                            created_at timestamp, counter numeric, tags text[], status integer,\n                            staging_created_at TIMESTAMP WITH TIME ZONE DEFAULT NOW()\n                        );",
                        builder.BuildCreateStagingTableSql("staging_batch_update_entity"));

                    AssertExact(
                        "CREATE TEMPORARY TABLE tmp_batch(id uuid, created_at timestamp, counter numeric, tags text[], status integer) ON  COMMIT DROP",
                        builder.BuildCreateTemporaryTableSql("tmp_batch", columns));
                });

            RunCase(
                failures,
                "PostgreSQL bulk update sql builder creates copy and update sql for staging and temp sources",
                () =>
                {
                    var builder = new PostgreSqlBulkUpdateSqlBuilder<BatchUpdateEntity>(Mapping);
                    var columns = new[] { "counter", "tags", "status" };
                    var allColumns = new[] { "id", "created_at", "counter", "tags", "status" };

                    AssertExact(
                        "COPY staging_batch_update_entity (batch_id,id,created_at,counter,tags,status) FROM STDIN (FORMAT BINARY)",
                        builder.BuildCopySql("staging_batch_update_entity", allColumns, includeBatchId: true));

                    AssertExact(
                        "COPY tmp_batch (id,created_at,counter,tags,status) FROM STDIN (FORMAT BINARY)",
                        builder.BuildCopySql("tmp_batch", allColumns));

                    AssertExact(
                        "\n                UPDATE batch_update_entity_2026061810\n                SET counter = staging.counter, tags = staging.tags, status = staging.status\n                FROM staging_batch_update_entity AS staging\n                WHERE staging.batch_id = @batchId AND batch_update_entity_2026061810.id = staging.id AND batch_update_entity_2026061810.created_at = staging.created_at AND batch_update_entity_2026061810.status <> 9",
                        builder.BuildUpdateFromSourceSql(
                            targetTableName: "batch_update_entity_2026061810",
                            sourceTableName: "staging_batch_update_entity",
                            sourceAlias: "staging",
                            updateColumns: columns,
                            additionalWhereClause: "batch_update_entity_2026061810.status <> 9",
                            sourcePredicate: "staging.batch_id = @batchId"));

                    AssertExact(
                        " UPDATE batch_update_entity_2026061810 \n                                            SET counter = tmp_batch.counter, tags = tmp_batch.tags, status = tmp_batch.status\n                                            FROM tmp_batch \n                                            WHERE batch_update_entity_2026061810.id = tmp_batch.id AND batch_update_entity_2026061810.created_at= tmp_batch.created_at AND batch_update_entity_2026061810.status <> 9",
                        builder.BuildLegacyTemporaryUpdateSql(
                            targetTableName: "batch_update_entity_2026061810",
                            sourceTableName: "tmp_batch",
                            updateColumns: columns,
                            additionalWhereClause: "batch_update_entity_2026061810.status <> 9"));
                });

            if (failures.Count > 0)
            {
                throw new InvalidOperationException(
                    "PostgreSqlBulkUpdateSqlBuilder regression failures:" + Environment.NewLine +
                    string.Join(Environment.NewLine + Environment.NewLine, failures));
            }

            Console.WriteLine("PostgreSqlBulkUpdateSqlBuilder regression harness passed.");
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
