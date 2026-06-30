using Customize_ORM.Enums;
using Customize_ORM.Extensions;
using Customize_ORM.Extensions.Sql;
using System.Data.Common;
using System.Linq.Expressions;
using Test.Entities;

namespace Test
{
    internal static class SqlExpressionRegression
    {
        private static readonly EntityMapping<AdminEntity> Mapping = ObjectMapping<AdminEntity>.MappingForInsert;
        private static readonly string SelectPrefix = $"SELECT {string.Join(", ", Mapping.AllColumns)} FROM {Mapping.TableName}";

        /// <summary>
        /// 运行 SqlExpression 相关回归测试，覆盖表达式翻译和参数化行为。
        /// </summary>
        public static void Run()
        {
            var failures = new List<string>();

            RunCase(
                failures,
                "Dialect prefixes are available for PostgreSQL, ClickHouse, and MySQL",
                () =>
                {
                    AssertDialectPrefix(DBType.PostgreSQL, "@");
                    AssertDialectPrefix(DBType.ClickHouse, "@");
                    AssertDialectPrefix(DBType.MySQL, "@");
                });

            RunCase(
                failures,
                "Parameter context creates PostgreSQL, ClickHouse, and MySQL parameters",
                () =>
                {
                    AssertParameterContext(
                        DBType.PostgreSQL,
                        value: "abc",
                        expectedName: "@p1",
                        expectedValue: "abc");

                    var clickHouseDate = new DateTime(2024, 1, 2, 3, 4, 5);
                    AssertParameterContext(
                        DBType.ClickHouse,
                        value: clickHouseDate,
                        expectedName: "@p1",
                        expectedValue: "2024-01-02 03:04:05");

                    AssertParameterContext(
                        DBType.MySQL,
                        value: 123,
                        expectedName: "@p1",
                        expectedValue: 123);

                    AssertParameterContext(
                        DBType.MySQL,
                        value: null,
                        expectedName: "@p1",
                        expectedValue: null);
                });

            RunCase(
                failures,
                "PostgreSQL: !x.IsDeleted && x.Remark != null",
                () =>
                {
                    var expression = CreateExpression(
                        DBType.PostgreSQL,
                        x => !x.IsDeleted && x.Remark != null);

                    AssertSql(
                        expression,
                        expectedWhereClause: "((NOT (is_deleted) AND remark IS NOT NULL))",
                        expectedLiteralWhere: "((NOT (is_deleted) AND remark IS NOT NULL))",
                        expectedParameters: []);
                });

            RunCase(
                failures,
                "PostgreSQL: x.Remark != null && x.Remark.Contains(\"abc\")",
                () =>
                {
                    var expression = CreateExpression(
                        DBType.PostgreSQL,
                        x => x.Remark != null && x.Remark.Contains("abc"));

                    AssertSql(
                        expression,
                        expectedWhereClause: "((remark IS NOT NULL AND remark LIKE @p1))",
                        expectedLiteralWhere: "((remark IS NOT NULL AND remark LIKE '%abc%'))",
                        expectedParameters: ["@p1=%abc%"]);
                });

            RunCase(
                failures,
                "PostgreSQL: x.NullableInt.Value == 5",
                () =>
                {
                    var expression = CreateExpression(
                        DBType.PostgreSQL,
                        x => x.NullableInt!.Value == 5);

                    AssertSql(
                        expression,
                        expectedWhereClause: "(nullable_int = @p1)",
                        expectedLiteralWhere: "(nullable_int = 5)",
                        expectedParameters: ["@p1=5"]);
                });

            RunCase(
                failures,
                "PostgreSQL: x.NullableInt.HasValue and !x.NullableInt.HasValue",
                () =>
                {
                    var hasValueExpression = CreateExpression(
                        DBType.PostgreSQL,
                        x => x.NullableInt.HasValue);

                    AssertSql(
                        hasValueExpression,
                        expectedWhereClause: "(nullable_int IS NOT NULL)",
                        expectedLiteralWhere: "(nullable_int IS NOT NULL)",
                        expectedParameters: []);

                    var notHasValueExpression = CreateExpression(
                        DBType.PostgreSQL,
                        x => !x.NullableInt.HasValue);

                    AssertSql(
                        notHasValueExpression,
                        expectedWhereClause: "(NOT (nullable_int IS NOT NULL))",
                        expectedLiteralWhere: "(NOT (nullable_int IS NOT NULL))",
                        expectedParameters: []);
                });

            RunCase(
                failures,
                "PostgreSQL: x.NullableInt != null && x.NullableInt.Value > 0",
                () =>
                {
                    var expression = CreateExpression(
                        DBType.PostgreSQL,
                        x => x.NullableInt != null && x.NullableInt.Value > 0);

                    AssertSql(
                        expression,
                        expectedWhereClause: "((nullable_int IS NOT NULL AND nullable_int > @p1))",
                        expectedLiteralWhere: "((nullable_int IS NOT NULL AND nullable_int > 0))",
                        expectedParameters: ["@p1=0"]);
                });

            RunCase(
                failures,
                "PostgreSQL: string.IsNullOrWhiteSpace(x.Name) and negation",
                () =>
                {
                    var expression = CreateExpression(
                        DBType.PostgreSQL,
                        x => string.IsNullOrWhiteSpace(x.Name));

                    AssertSql(
                        expression,
                        expectedWhereClause: "((name IS NULL OR TRIM(name) = @p1))",
                        expectedLiteralWhere: "((name IS NULL OR TRIM(name) = ''))",
                        expectedParameters: ["@p1="]);

                    var notExpression = CreateExpression(
                        DBType.PostgreSQL,
                        x => !string.IsNullOrWhiteSpace(x.Name));

                    AssertSql(
                        notExpression,
                        expectedWhereClause: "(NOT ((name IS NULL OR TRIM(name) = @p1)))",
                        expectedLiteralWhere: "(NOT ((name IS NULL OR TRIM(name) = '')))",
                        expectedParameters: ["@p1="]);
                });

            RunCase(
                failures,
                "PostgreSQL: string.IsNullOrEmpty(x.Name)",
                () =>
                {
                    var expression = CreateExpression(
                        DBType.PostgreSQL,
                        x => string.IsNullOrEmpty(x.Name));

                    AssertSql(
                        expression,
                        expectedWhereClause: "((name IS NULL OR name = @p1))",
                        expectedLiteralWhere: "((name IS NULL OR name = ''))",
                        expectedParameters: ["@p1="]);
                });

            RunCase(
                failures,
                "PostgreSQL: x.Name.Trim().ToLower().Contains(\"abc\")",
                () =>
                {
                    var expression = CreateExpression(
                        DBType.PostgreSQL,
                        x => x.Name!.Trim().ToLower().Contains("abc"));

                    AssertSql(
                        expression,
                        expectedWhereClause: "(LOWER(TRIM(name)) LIKE @p1)",
                        expectedLiteralWhere: "(LOWER(TRIM(name)) LIKE '%abc%')",
                        expectedParameters: ["@p1=%abc%"]);
                });

            RunCase(
                failures,
                "PostgreSQL: x.Status.ToString() == \"5\"",
                () =>
                {
                    var expression = CreateExpression(
                        DBType.PostgreSQL,
                        x => x.Status.ToString() == "5");

                    AssertSql(
                        expression,
                        expectedWhereClause: "(CAST(status AS TEXT) = @p1)",
                        expectedLiteralWhere: "(CAST(status AS TEXT) = '5')",
                        expectedParameters: ["@p1=5"]);
                });

            RunCase(
                failures,
                "PostgreSQL: checked convert still resolves entity columns",
                () =>
                {
                    var expression = CreateExpression(
                        DBType.PostgreSQL,
                        x => checked((long)x.Status) > 3L);

                    AssertSql(
                        expression,
                        expectedWhereClause: "(status > @p1)",
                        expectedLiteralWhere: "(status > 3)",
                        expectedParameters: ["@p1=3"]);
                });

            RunCase(
                failures,
                "PostgreSQL: x.Remark.ToString().Contains(\"abc\")",
                () =>
                {
                    var expression = CreateExpression(
                        DBType.PostgreSQL,
                        x => x.Remark!.ToString().Contains("abc"));

                    AssertSql(
                        expression,
                        expectedWhereClause: "(remark LIKE @p1)",
                        expectedLiteralWhere: "(remark LIKE '%abc%')",
                        expectedParameters: ["@p1=%abc%"]);
                });

            RunCase(
                failures,
                "PostgreSQL: x.Name.ToUpper().StartsWith(\"ABC\")",
                () =>
                {
                    var expression = CreateExpression(
                        DBType.PostgreSQL,
                        x => x.Name!.ToUpper().StartsWith("ABC"));

                    AssertSql(
                        expression,
                        expectedWhereClause: "(UPPER(name) LIKE @p1)",
                        expectedLiteralWhere: "(UPPER(name) LIKE 'ABC%')",
                        expectedParameters: ["@p1=ABC%"]);
                });

            RunCase(
                failures,
                "PostgreSQL: captured method calls still evaluate to parameters",
                () =>
                {
                    var search = "  AbC  ";
                    var expression = CreateExpression(
                        DBType.PostgreSQL,
                        x => x.Remark == search.Trim().ToLower());

                    AssertSql(
                        expression,
                        expectedWhereClause: "(remark = @p1)",
                        expectedLiteralWhere: "(remark = 'abc')",
                        expectedParameters: ["@p1=abc"]);
                });

            RunCase(
                failures,
                "PostgreSQL: unsupported entity methods throw clear exceptions",
                () =>
                {
                    AssertThrows<NotSupportedException>(() =>
                        CreateExpression(DBType.PostgreSQL, x => CustomNormalize(x.Name!) == "abc"));

                    AssertThrows<NotSupportedException>(() =>
                        CreateExpression(DBType.PostgreSQL, x => x.Name!.Replace("a", "b") == "bbb"));
                });

            RunCase(
                failures,
                "ClickHouse: new[] { 1, 2, 3 }.Contains(x.Status)",
                () =>
                {
                    var expression = CreateExpression(
                        DBType.ClickHouse,
                        x => new[] { 1, 2, 3 }.Contains(x.Status));

                    AssertSql(
                        expression,
                        expectedWhereClause: "(status IN (@p1, @p2, @p3))",
                        expectedLiteralWhere: "(status IN (1, 2, 3))",
                        expectedParameters: ["@p1=1", "@p2=2", "@p3=3"]);
                });

            RunCase(
                failures,
                "ClickHouse materialized parameters strip the @ prefix for driver binding",
                () =>
                {
                    var expression = CreateExpression(
                        DBType.ClickHouse,
                        x => x.Status == 1);

                    var (whereClause, parameters) = expression.BuildWhereClause();
                    var materialized = ClickHouseDialect.Materialize(parameters.Single());

                    var mismatches = new List<string>();
                    CollectMismatch(mismatches, "where clause", "(status = @p1)", whereClause);
                    CollectMismatch(mismatches, "parameter name", "p1", materialized.ParameterName);
                    CollectMismatch(mismatches, "parameter value", "1", materialized.Value?.ToString() ?? "NULL");

                    if (mismatches.Count > 0)
                    {
                        throw new InvalidOperationException(string.Join(Environment.NewLine + Environment.NewLine, mismatches));
                    }
                });

            RunCase(
                failures,
                "PostgreSQL: captured null comparisons use IS NULL semantics",
                () =>
                {
                    string? value = null;

                    var equalsExpression = CreateExpression(
                        DBType.PostgreSQL,
                        x => x.Remark == value);

                    AssertSql(
                        equalsExpression,
                        expectedWhereClause: "(remark IS NULL)",
                        expectedLiteralWhere: "(remark IS NULL)",
                        expectedParameters: []);

                    var notEqualsExpression = CreateExpression(
                        DBType.PostgreSQL,
                        x => x.Remark != value);

                    AssertSql(
                        notEqualsExpression,
                        expectedWhereClause: "(remark IS NOT NULL)",
                        expectedLiteralWhere: "(remark IS NOT NULL)",
                        expectedParameters: []);
                });

            RunCase(
                failures,
                "String search rejects null search values",
                () =>
                {
                    string? search = null;

                    AssertThrows<ArgumentNullException>(() =>
                        CreateExpression(DBType.PostgreSQL, x => x.Remark!.Contains(search!)));

                    AssertThrows<ArgumentNullException>(() =>
                        CreateExpression(DBType.PostgreSQL, x => x.Remark!.StartsWith(search!)));

                    AssertThrows<ArgumentNullException>(() =>
                        CreateExpression(DBType.PostgreSQL, x => x.Remark!.EndsWith(search!)));
                });

            RunCase(
                failures,
                "OrderBy supports boxed selectors, descending order, and aliases",
                () =>
                {
                    var ascClause = new SqlExpression<AdminEntity>(Mapping, DBType.PostgreSQL)
                        .OrderBy(x => x.Remark)
                        .BuildOrderByClause();

                    var boxedDescClause = new SqlExpression<AdminEntity>(Mapping, DBType.PostgreSQL)
                        .OrderByDescending(x => (object)x.Status)
                        .BuildOrderByClause();

                    var aliasedClause = new SqlExpression<AdminEntity>(Mapping, DBType.PostgreSQL)
                        .WithTableAlias("a")
                        .OrderByDescending(x => (object)x.Status)
                        .BuildOrderByClause();

                    var mismatches = new List<string>();
                    CollectMismatch(mismatches, "order by asc", "ORDER BY remark ASC", ascClause);
                    CollectMismatch(mismatches, "order by desc", "ORDER BY status DESC", boxedDescClause);
                    CollectMismatch(mismatches, "aliased order by desc", "ORDER BY a.status DESC", aliasedClause);

                    if (mismatches.Count > 0)
                    {
                        throw new InvalidOperationException(string.Join(Environment.NewLine + Environment.NewLine, mismatches));
                    }
                });

            RunCase(
                failures,
                "First() is available and Firest() remains a compatibility alias",
                () =>
                {
                    var firstSql = new SqlExpression<AdminEntity>(Mapping, DBType.PostgreSQL)
                        .Select()
                        .First()
                        .ToSql();

                    var legacySql = new SqlExpression<AdminEntity>(Mapping, DBType.PostgreSQL)
                        .Select()
                        .Firest()
                        .ToSql();

                    var expectedSql = $"{SelectPrefix} ORDER BY {Mapping.Key} ASC LIMIT 1";

                    if (!string.Equals(expectedSql, firstSql, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            $"First() sql mismatch{Environment.NewLine}expected: {expectedSql}{Environment.NewLine}actual:   {firstSql}");
                    }

                    if (!string.Equals(firstSql, legacySql, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            $"Firest() compatibility mismatch{Environment.NewLine}expected: {firstSql}{Environment.NewLine}actual:   {legacySql}");
                    }
                });

            RunCase(
                failures,
                "PostgreSQL pagination uses LIMIT size OFFSET offset",
                () =>
                {
                    var sql = new SqlExpression<AdminEntity>(Mapping, DBType.PostgreSQL)
                        .Select()
                        .Page(pageNumber: 2, pageSize: 10)
                        .ToSql();

                    AssertExactSql(
                        $"{SelectPrefix} ORDER BY {Mapping.Key} ASC LIMIT 10 OFFSET 10",
                        sql);
                });

            RunCase(
                failures,
                "ClickHouse and MySQL pagination keep LIMIT offset, size",
                () =>
                {
                    var clickHouseSql = new SqlExpression<AdminEntity>(Mapping, DBType.ClickHouse)
                        .Select()
                        .Page(pageNumber: 2, pageSize: 10)
                        .ToSql();

                    var mySqlSql = new SqlExpression<AdminEntity>(Mapping, DBType.MySQL)
                        .Select()
                        .Page(pageNumber: 2, pageSize: 10)
                        .ToSql();

                    var expectedSql = $"{SelectPrefix} ORDER BY {Mapping.Key} ASC LIMIT 10, 10";
                    AssertExactSql(expectedSql, clickHouseSql);
                    AssertExactSql(expectedSql, mySqlSql);
                });

            RunCase(
                failures,
                "Select handles direct and boxed member projections",
                () =>
                {
                    var directSql = new SqlExpression<AdminEntity>(Mapping, DBType.PostgreSQL)
                        .Select(x => x.Remark!)
                        .ToSql();

                    var boxedSql = new SqlExpression<AdminEntity>(Mapping, DBType.PostgreSQL)
                        .Select(x => (object)x.Status)
                        .ToSql();

                    AssertExactSql("SELECT remark FROM admin", directSql);
                    AssertExactSql("SELECT status FROM admin", boxedSql);
                });

            RunCase(
                failures,
                "Select handles anonymous projections with converted members from T",
                () =>
                {
                    var sql = new SqlExpression<AdminEntity>(Mapping, DBType.PostgreSQL)
                        .Select(x => new { x.Remark, BoxedStatus = (object)x.Status })
                        .ToSql();

                    AssertExactSql("SELECT remark, status FROM admin", sql);
                });

            RunCase(
                failures,
                "Select handles member-init projections with boxed members from T",
                () =>
                {
                    var sql = new SqlExpression<AdminEntity>(Mapping, DBType.PostgreSQL)
                        .Select(x => new AdminProjection
                        {
                            Remark = x.Remark,
                            Status = (object)x.Status
                        })
                        .ToSql();

                    AssertExactSql("SELECT remark, status FROM admin", sql);
                });

            if (failures.Count > 0)
            {
                throw new InvalidOperationException(
                    "SqlExpression regression failures:" + Environment.NewLine +
                    string.Join(Environment.NewLine + Environment.NewLine, failures));
            }

            Console.WriteLine("SqlExpression regression harness passed.");
        }

        /// <summary>
        /// 为指定数据库方言构建带筛选条件的 SqlExpression。
        /// </summary>
        private static SqlExpression<AdminEntity> CreateExpression(
            DBType dbType,
            Expression<Func<AdminEntity, bool>> predicate)
        {
            var expression = new SqlExpression<AdminEntity>(Mapping, dbType);
            expression.Where(predicate);
            return expression;
        }

        /// <summary>
        /// 断言 Where 子句、参数和最终 SQL 均符合预期。
        /// </summary>
        private static void AssertSql(
            SqlExpression<AdminEntity> expression,
            string expectedWhereClause,
            string expectedLiteralWhere,
            IReadOnlyList<string> expectedParameters)
        {
            var (whereClause, parameters) = expression.BuildWhereClause();
            var actualParameters = parameters.Select(FormatParameter).ToArray();
            var sql = expression.ToSql();
            var mismatches = new List<string>();

            CollectMismatch(mismatches, "where clause", expectedWhereClause, whereClause);
            CollectMismatch(mismatches, "literal where", expectedLiteralWhere, expression.BuildWhereStr());
            CollectMismatch(mismatches, "sql prefix", SelectPrefix, ExtractSqlPrefix(sql));
            CollectMismatch(mismatches, "sql predicate", $" WHERE {expectedWhereClause}", ExtractSqlPredicate(sql));
            CollectSequenceMismatches(mismatches, "parameters", expectedParameters, actualParameters);

            if (mismatches.Count > 0)
            {
                throw new InvalidOperationException(string.Join(Environment.NewLine + Environment.NewLine, mismatches));
            }
        }

        /// <summary>
        /// 断言方言参数前缀符合预期。
        /// </summary>
        private static void AssertDialectPrefix(DBType dbType, string expectedPrefix)
        {
            var actualPrefix = SqlDialect.For(dbType).ParameterPrefix;

            if (!string.Equals(expectedPrefix, actualPrefix, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"parameter prefix mismatch{Environment.NewLine}expected: {expectedPrefix}{Environment.NewLine}actual:   {actualPrefix}");
            }
        }

        /// <summary>
        /// 断言参数上下文能生成正确的占位符和参数实例。
        /// </summary>
        private static void AssertParameterContext(
            DBType dbType,
            object? value,
            string expectedName,
            object? expectedValue)
        {
            var context = new SqlParameterContext(SqlDialect.For(dbType));
            var actualName = context.Add(value);
            var parameter = context.Parameters.Single();
            var actualValue = context.LiteralValues[actualName];

            if (!string.Equals(expectedName, actualName, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"parameter name mismatch{Environment.NewLine}expected: {expectedName}{Environment.NewLine}actual:   {actualName}");
            }

            if (!Equals(expectedValue, actualValue))
            {
                throw new InvalidOperationException(
                    $"parameter literal mismatch{Environment.NewLine}expected: {expectedValue}{Environment.NewLine}actual:   {actualValue}");
            }

            if (!string.Equals(expectedName, parameter.ParameterName, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"parameter instance name mismatch{Environment.NewLine}expected: {expectedName}{Environment.NewLine}actual:   {parameter.ParameterName}");
            }

            if (!Equals(expectedValue, parameter.Value))
            {
                throw new InvalidOperationException(
                    $"parameter instance value mismatch{Environment.NewLine}expected: {expectedValue}{Environment.NewLine}actual:   {parameter.Value}");
            }
        }

        /// <summary>
        /// 收集单项字符串断言不一致的信息。
        /// </summary>
        private static void CollectMismatch(List<string> mismatches, string label, string expected, string actual)
        {
            if (!string.Equals(expected, actual, StringComparison.Ordinal))
            {
                mismatches.Add(
                    $"{label} mismatch{Environment.NewLine}expected: {expected}{Environment.NewLine}actual:   {actual}");
            }
        }

        /// <summary>
        /// 断言完整 SQL 文本完全一致。
        /// </summary>
        private static void AssertExactSql(string expected, string actual)
        {
            if (!string.Equals(expected, actual, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"sql mismatch{Environment.NewLine}expected: {expected}{Environment.NewLine}actual:   {actual}");
            }
        }

        /// <summary>
        /// 断言指定操作会抛出预期异常。
        /// </summary>
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

            throw new InvalidOperationException($"Expected {typeof(TException).Name} but no exception was thrown.");
        }

        /// <summary>
        /// 收集参数序列不一致的信息。
        /// </summary>
        private static void CollectSequenceMismatches(
            List<string> mismatches,
            string label,
            IReadOnlyList<string> expected,
            IReadOnlyList<string> actual)
        {
            if (expected.Count != actual.Count)
            {
                mismatches.Add(
                    $"{label} count mismatch{Environment.NewLine}expected: [{string.Join(", ", expected)}]{Environment.NewLine}actual:   [{string.Join(", ", actual)}]");
                return;
            }

            for (var i = 0; i < expected.Count; i++)
            {
                if (!string.Equals(expected[i], actual[i], StringComparison.Ordinal))
                {
                    mismatches.Add(
                        $"{label} mismatch at index {i}{Environment.NewLine}expected: {expected[i]}{Environment.NewLine}actual:   {actual[i]}");
                }
            }
        }

        /// <summary>
        /// 提取 SQL 的 SELECT 前缀部分。
        /// </summary>
        private static string ExtractSqlPrefix(string sql)
        {
            var whereIndex = sql.IndexOf(" WHERE ", StringComparison.Ordinal);
            return whereIndex >= 0 ? sql[..whereIndex] : sql;
        }

        /// <summary>
        /// 提取 SQL 的 WHERE 子句部分。
        /// </summary>
        private static string ExtractSqlPredicate(string sql)
        {
            var whereIndex = sql.IndexOf(" WHERE ", StringComparison.Ordinal);
            return whereIndex >= 0 ? sql[whereIndex..] : string.Empty;
        }

        /// <summary>
        /// 将参数格式化为便于断言的文本。
        /// </summary>
        private static string FormatParameter(DbParameter parameter)
        {
            var value = parameter.Value is null or DBNull ? "NULL" : parameter.Value.ToString();
            return $"{parameter.ParameterName}={value}";
        }

        /// <summary>
        /// 运行单个回归用例，并将失败信息收集到统一列表中。
        /// </summary>
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

        /// <summary>
        /// 用于验证不支持的实体方法调用仍会明确报错。
        /// </summary>
        private static string CustomNormalize(string value)
        {
            return value.Trim().ToLowerInvariant();
        }

        private sealed class AdminProjection
        {
            public string? Remark { get; init; }

            public object? Status { get; init; }
        }
    }
}
