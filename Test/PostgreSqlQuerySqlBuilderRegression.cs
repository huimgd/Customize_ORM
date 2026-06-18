using Customize_ORM.Extensions;
using Customize_ORM.Extensions.Sql;
using Test.Entitys;

namespace Test
{
    internal static class PostgreSqlQuerySqlBuilderRegression
    {
        private static readonly EntityMapping<AdminEntity> Mapping = ObjectMapping<AdminEntity>.MappingForInsert;

        public static void Run()
        {
            var failures = new List<string>();

            RunCase(
                failures,
                "PostgreSQL query builder exposes mapped defaults and count/select shapes",
                () =>
                {
                    var builder = CreateBuilder();

                    AssertExact("admin", builder.TableName);
                    AssertExact(string.Join(", ", Mapping.AllColumns), builder.AllColumns);
                    AssertExact("SELECT id, parent_id, status, remark, is_deleted, extra_properties FROM admin", builder.SelectAll());
                    AssertExact("SELECT COUNT(*) FROM admin", builder.Count());
                    AssertExact("COPY admin (id,parent_id,status,remark,is_deleted,extra_properties) FROM STDIN (FORMAT BINARY)", builder.CopyBinaryImport(Mapping.AllColumns));
                });

            RunCase(
                failures,
                "PostgreSQL query builder composes WHERE, UPDATE, and key predicates predictably",
                () =>
                {
                    var builder = CreateBuilder("admin_2026061810");
                    var sql = builder.Update(["remark", "status"]);
                    sql = builder.ApplyWhere(sql, "id = @id");

                    AssertExact("UPDATE admin_2026061810 SET remark = @remark, status = @status WHERE id = @id", sql);
                    AssertExact("id=@id", builder.WhereByKey());
                });

            if (failures.Count > 0)
            {
                throw new InvalidOperationException(
                    "PostgreSqlQuerySqlBuilder regression failures:" + Environment.NewLine +
                    string.Join(Environment.NewLine + Environment.NewLine, failures));
            }

            Console.WriteLine("PostgreSqlQuerySqlBuilder regression harness passed.");
        }

        private static PostgreSqlQuerySqlBuilder<AdminEntity> CreateBuilder(string? tableName = null)
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
