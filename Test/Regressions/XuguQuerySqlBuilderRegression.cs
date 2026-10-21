using Customize_ORM.Extensions;
using Customize_ORM.Extensions.Sql;
using Test.Entities;

namespace Test
{
    /// <summary>
    /// 回归用例：验证虚谷查询 SQL 构建器生成的 SELECT/COUNT/UPDATE/INSERT 语句形态（不连接真实数据库）。
    /// </summary>
    internal static class XuguQuerySqlBuilderRegression
    {
        private static readonly EntityMapping<AdminEntity> Mapping = ObjectMapping<AdminEntity>.MappingForInsert;
        private static readonly string ColumnList = string.Join(", ", Mapping.AllColumns);

        public static void Run()
        {
            var failures = new List<string>();

            RunCase(
                failures,
                "Xugu query builder exposes mapped defaults and count/select shapes",
                () =>
                {
                    var builder = CreateBuilder();

                    AssertExact("admin", builder.TableName);
                    AssertExact(string.Join(", ", Mapping.AllColumns), builder.AllColumns);
                    AssertExact($"SELECT {ColumnList} FROM admin", builder.SelectAll());
                    AssertExact("SELECT COUNT(*) FROM admin", builder.Count());
                });

            RunCase(
                failures,
                "Xugu query builder composes WHERE, UPDATE, and key predicates predictably",
                () =>
                {
                    var builder = CreateBuilder("admin_2026061810");
                    var sql = builder.Update(["remark", "status"]);

                    AssertExact("UPDATE admin_2026061810 SET remark = :remark, status = :status", sql);
                    AssertExact("UPDATE admin_2026061810 SET remark = :remark, status = :status WHERE id = :id", builder.ApplyWhere(sql, builder.WhereByKey()));
                    AssertExact("SELECT remark, status FROM admin_2026061810 WHERE id = :id", builder.ApplyWhere(builder.Select("remark, status"), builder.WhereByKey()));
                });

            RunCase(
                failures,
                "Xugu single insert sql uses column-name placeholders",
                () =>
                {
                    var builder = CreateBuilder();
                    AssertExact(
                        $"INSERT INTO admin ({ColumnList}) VALUES (:{string.Join(", :", Mapping.AllColumns)})",
                        builder.BuildSingleInsertSql(Mapping.AllColumns));
                });

            RunCase(
                failures,
                "Xugu multi-value insert sql names placeholders per row",
                () =>
                {
                    var builder = CreateBuilder();
                    static string RowPlaceholders(int row) => string.Join(",", Mapping.AllColumns.Select(column => $":r{row}_{column}"));
                    AssertExact(
                        $"INSERT INTO admin ({ColumnList}) VALUES " +
                        $"({RowPlaceholders(0)}),({RowPlaceholders(1)}),({RowPlaceholders(2)})",
                        builder.BuildNamedInsertSql(Mapping.AllColumns, 3));
                });

            RunCase(
                failures,
                "Xugu multi-value insert enforces chunk row cap",
                () =>
                {
                    AssertEqual(500, XuguQuerySqlBuilder<AdminEntity>.MaxRowsPerInsert);
                    AssertThrows<ArgumentOutOfRangeException>(() => CreateBuilder().BuildNamedInsertSql(Mapping.AllColumns, XuguQuerySqlBuilder<AdminEntity>.MaxRowsPerInsert + 1));
                });

            if (failures.Count > 0)
            {
                throw new InvalidOperationException(
                    "XuguQuerySqlBuilder regression failures:" + Environment.NewLine +
                    string.Join(Environment.NewLine + Environment.NewLine, failures));
            }

            Console.WriteLine("XuguQuerySqlBuilder regression harness passed.");
        }

        private static XuguQuerySqlBuilder<AdminEntity> CreateBuilder(string? tableName = null)
            => new(Mapping, tableName);

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

        private static void AssertEqual<T>(T expected, T actual)
        {
            if (!Equals(expected, actual))
            {
                throw new InvalidOperationException($"assertion failed{Environment.NewLine}expected: {expected}{Environment.NewLine}actual:   {actual}");
            }
        }

        private static void AssertThrows<TException>(Action action)
            where TException : Exception
        {
            try
            {
                action();
            }
            catch (TException)
            {
                return;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"expected {typeof(TException).Name} but got {ex.GetType().Name}");
            }

            throw new InvalidOperationException($"expected {typeof(TException).Name} but no exception was thrown");
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
