using Customize_ORM.Extensions;
using Customize_ORM.Extensions.Sql;
using NpgsqlTypes;
using System.Data.Common;
using Test.Entities;

namespace Test
{
    internal static class PostgreSqlCteUpdateCommandBuilderRegression
    {
        private static readonly EntityMapping<BatchUpdateEntity> Mapping = ObjectMapping<BatchUpdateEntity>.MappingForUpdate;

        public static void Run()
        {
            var failures = new List<string>();

            RunCase(
                failures,
                "PostgreSQL CTE update builder emits deterministic SQL for bulk updates",
                () =>
                {
                    var builder = new PostgreSqlCteUpdateCommandBuilder<BatchUpdateEntity>(Mapping);
                    var result = builder.Build(
                        entities:
                        [
                            new BatchUpdateEntity
                            {
                                Id = Guid.Parse("00000000-0000-0000-0000-000000000001"),
                                CreatedAt = new DateTime(2026, 6, 18, 10, 0, 0),
                                Counter = 1UL,
                                Tags = ["a", "b"],
                                Status = 1
                            },
                            new BatchUpdateEntity
                            {
                                Id = Guid.Parse("00000000-0000-0000-0000-000000000002"),
                                CreatedAt = new DateTime(2026, 6, 18, 11, 0, 0),
                                Counter = 2UL,
                                Tags = [],
                                Status = 2
                            }
                        ],
                        columns: ["counter", "tags", "status"],
                        tableName: "batch_update_entity_2026061810",
                        cteName: "cte_test",
                        additionalWhereClause: "batch_update_entity_2026061810.status <> 9");

                    AssertExact(
                        " WITH cte_test AS (" + Environment.NewLine +
                        "                        SELECT unnest(@id) as id, unnest(@created_at) as created_at, unnest(@counter) as counter, unnest(@tags)::text[] as tags, unnest(@status) as status" + Environment.NewLine +
                        "                        )UPDATE batch_update_entity_2026061810 " + Environment.NewLine +
                        "                            SET counter = cte_test.counter, tags = cte_test.tags, status = cte_test.status" + Environment.NewLine +
                        "                            FROM cte_test" + Environment.NewLine +
                        "                            WHERE batch_update_entity_2026061810.id = cte_test.id AND batch_update_entity_2026061810.created_at = cte_test.created_at AND batch_update_entity_2026061810.status <> 9",
                        result.Sql);
                });

            RunCase(
                failures,
                "PostgreSQL CTE update builder normalizes scalar and array parameters",
                () =>
                {
                    var builder = new PostgreSqlCteUpdateCommandBuilder<BatchUpdateEntity>(Mapping);
                    var result = builder.Build(
                        entities:
                        [
                            new BatchUpdateEntity
                            {
                                Id = Guid.Parse("00000000-0000-0000-0000-000000000001"),
                                CreatedAt = new DateTime(2026, 6, 18, 10, 0, 0),
                                Counter = 1UL,
                                Tags = ["a", "b"],
                                Status = 1
                            },
                            new BatchUpdateEntity
                            {
                                Id = Guid.Parse("00000000-0000-0000-0000-000000000002"),
                                CreatedAt = new DateTime(2026, 6, 18, 11, 0, 0),
                                Counter = 2UL,
                                Tags = null,
                                Status = 2
                            }
                        ],
                        columns: ["counter", "tags", "status"],
                        tableName: "batch_update_entity_2026061810",
                        cteName: "cte_test");

                    var parameters = result.Parameters.ToDictionary(p => p.ParameterName, StringComparer.Ordinal);

                    AssertParameter(parameters["@counter"], new decimal[] { 1m, 2m }, NpgsqlDbType.Array | NpgsqlDbType.Numeric);
                    AssertParameter(parameters["@status"], new int[] { 1, 2 }, NpgsqlDbType.Array | NpgsqlDbType.Integer);
                    AssertParameter(parameters["@tags"], new[] { "{\"a\",\"b\"}", "{}" }, NpgsqlDbType.Array | NpgsqlDbType.Text);
                });

            if (failures.Count > 0)
            {
                throw new InvalidOperationException(
                    "PostgreSqlCteUpdateCommandBuilder regression failures:" + Environment.NewLine +
                    string.Join(Environment.NewLine + Environment.NewLine, failures));
            }

            Console.WriteLine("PostgreSqlCteUpdateCommandBuilder regression harness passed.");
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

        private static void AssertParameter(DbParameter parameter, Array expectedValue, NpgsqlDbType expectedType)
        {
            if (parameter.Value is not Array actualValue)
            {
                throw new InvalidOperationException($"Expected array parameter for {parameter.ParameterName}.");
            }

            if (parameter is not Npgsql.NpgsqlParameter npgsqlParameter)
            {
                throw new InvalidOperationException($"Expected NpgsqlParameter for {parameter.ParameterName}.");
            }

            if (npgsqlParameter.NpgsqlDbType != expectedType)
            {
                throw new InvalidOperationException(
                    $"parameter type mismatch{Environment.NewLine}expected: {expectedType}{Environment.NewLine}actual:   {npgsqlParameter.NpgsqlDbType}");
            }

            if (expectedValue.Length != actualValue.Length || expectedValue.GetType() != actualValue.GetType())
            {
                throw new InvalidOperationException(
                    $"array shape mismatch{Environment.NewLine}expected: {expectedValue.GetType().Name}[{expectedValue.Length}]{Environment.NewLine}actual:   {actualValue.GetType().Name}[{actualValue.Length}]");
            }

            for (var i = 0; i < expectedValue.Length; i++)
            {
                var expectedItem = expectedValue.GetValue(i);
                var actualItem = actualValue.GetValue(i);
                if (!Equals(expectedItem, actualItem))
                {
                    throw new InvalidOperationException(
                        $"array value mismatch at index {i}{Environment.NewLine}expected: {expectedItem}{Environment.NewLine}actual:   {actualItem}");
                }
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
