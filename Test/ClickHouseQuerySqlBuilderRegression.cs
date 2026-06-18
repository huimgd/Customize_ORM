using Customize_ORM.Extensions;
using Customize_ORM.Extensions.Sql;
using Customize_ORM.Impl;
using Test.Entitys;

namespace Test
{
    internal static class ClickHouseQuerySqlBuilderRegression
    {
        private static readonly EntityMapping<AdminEntity> Mapping = ObjectMapping<AdminEntity>.MappingForInsert;

        public static void Run()
        {
            var failures = new List<string>();

            RunCase(
                failures,
                "ClickHouse query builder exposes consistent defaults for mapped tables",
                () =>
                {
                    var builder = CreateBuilder();

                    AssertExact("admin", builder.TableName);
                    AssertExact(string.Join(", ", Mapping.AllColumns), builder.AllColumns);
                    AssertExact("ORDER BY id", builder.DefaultOrderByClause);
                    AssertExact(
                        $"SELECT {string.Join(", ", Mapping.AllColumns)} FROM admin FINAL",
                        builder.SelectAll(final: true));
                    AssertExact("SELECT remark FROM admin", builder.Select("remark", final: false));
                    AssertExact("SELECT COUNT(*) FROM admin FINAL", builder.Count(final: true));
                });

            RunCase(
                failures,
                "ClickHouse query builder composes WHERE, ORDER BY, and LIMIT clauses predictably",
                () =>
                {
                    var builder = CreateBuilder("admin_archive");

                    var sql = builder.SelectAll(final: true);
                    sql = builder.ApplyWhere(sql, "status = @p1");
                    sql = builder.ApplyOrderBy(sql, null);
                    sql = builder.ApplyLimit(sql, 20, 10);

                    AssertExact(
                        $"SELECT {string.Join(", ", Mapping.AllColumns)} FROM admin_archive FINAL WHERE status = @p1 ORDER BY id LIMIT 20, 10",
                        sql);

                    AssertExact("ORDER BY create_time DESC", builder.ResolveOrderBy("create_time DESC"));
                });

            if (failures.Count > 0)
            {
                throw new InvalidOperationException(
                    "ClickHouseQuerySqlBuilder regression failures:" + Environment.NewLine +
                    string.Join(Environment.NewLine + Environment.NewLine, failures));
            }

            Console.WriteLine("ClickHouseQuerySqlBuilder regression harness passed.");
        }

        private static ClickHouseQuerySqlBuilder<AdminEntity> CreateBuilder(string? tableName = null)
            => new(Mapping, tableName);

        private static void AssertExact(string expected, string actual)
        {
            if (!string.Equals(expected, actual, StringComparison.Ordinal))
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
