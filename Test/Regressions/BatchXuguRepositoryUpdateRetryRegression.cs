using Customize_ORM.Extensions;
using Customize_ORM.Impl;
using System.Reflection;
using Test.Entities;

namespace Test
{
    /// <summary>
    /// 回归用例：验证虚谷批量更新重试逻辑中 RETURNING 拼接与未命中数据过滤（不连接真实数据库）。
    /// </summary>
    internal static class BatchXuguRepositoryUpdateRetryRegression
    {
        private static readonly Type RepoType = typeof(BatchXuguRepository<>).MakeGenericType(typeof(BatchUpdateEntity));

        public static void Run()
        {
            var failures = new List<string>();

            RunCase(failures,
                "AppendReturningIfRetry keeps normal-path SQL untouched (retryCount == 0)",
                () =>
                {
                    var method = GetStaticMethod("AppendReturningIfRetry");
                    const string sql = "UPDATE batch_update_entity_2026061810 SET counter = staging.counter FROM staging WHERE staging.batch_id = :batchId AND batch_update_entity_2026061810.id = staging.id";
                    var result = (string)method.Invoke(null, new object[] { sql, "batch_update_entity_2026061810", 0 })!;
                    AssertEqual(sql, result);
                });

            RunCase(failures,
                "AppendReturningIfRetry appends RETURNING primary key on retry path (retryCount > 0)",
                () =>
                {
                    var method = GetStaticMethod("AppendReturningIfRetry");
                    const string sql = "UPDATE batch_update_entity_2026061810 SET counter = staging.counter FROM staging WHERE staging.batch_id = :batchId AND batch_update_entity_2026061810.id = staging.id";
                    var result = (string)method.Invoke(null, new object[] { sql, "batch_update_entity_2026061810", 1 })!;
                    AssertEqual("UPDATE batch_update_entity_2026061810 SET counter = staging.counter FROM staging WHERE staging.batch_id = :batchId AND batch_update_entity_2026061810.id = staging.id RETURNING batch_update_entity_2026061810.id", result);
                });

            RunCase(failures,
                "ComputeUnmatchedEntities filters out rows already returned by RETURNING",
                () =>
                {
                    var method = GetStaticMethod("ComputeUnmatchedEntities");
                    var matched = Guid.Parse("00000000-0000-0000-0000-000000000001");
                    var unmatched = Guid.Parse("00000000-0000-0000-0000-000000000002");

                    var entities = new BatchUpdateEntity[]
                    {
                        new() { Id = matched, CreatedAt = new DateTime(2026, 6, 18, 10, 0, 0), Counter = 1, Status = 1 },
                        new() { Id = unmatched, CreatedAt = new DateTime(2026, 6, 18, 11, 0, 0), Counter = 2, Status = 2 }
                    };
                    var matchedKeys = new HashSet<object?> { matched };

                    var result = (System.Collections.IList)method.Invoke(null, new object[] { entities, matchedKeys })!;
                    AssertEqual(1, result.Count);
                    var remaining = (BatchUpdateEntity)result[0]!;
                    AssertEqual(unmatched, remaining.Id);
                });

            RunCase(failures,
                "ComputeUnmatchedEntities matches driver-side keys by string form",
                () =>
                {
                    // 驱动 RETURNING 可能返回与实体不同 CLR 形态的主键（如 string），按统一字符串比较兜底
                    var method = GetStaticMethod("ComputeUnmatchedEntities");
                    var matched = Guid.Parse("00000000-0000-0000-0000-000000000001");
                    var unmatched = Guid.Parse("00000000-0000-0000-0000-000000000002");

                    var entities = new BatchUpdateEntity[]
                    {
                        new() { Id = matched, CreatedAt = new DateTime(2026, 6, 18, 10, 0, 0), Counter = 1, Status = 1 },
                        new() { Id = unmatched, CreatedAt = new DateTime(2026, 6, 18, 11, 0, 0), Counter = 2, Status = 2 }
                    };
                    var matchedKeys = new HashSet<object?> { matched.ToString() };

                    var result = (System.Collections.IList)method.Invoke(null, new object[] { entities, matchedKeys })!;
                    AssertEqual(1, result.Count);
                });

            RunCase(failures,
                "ComputeUnmatchedEntities returns all entities when nothing matched",
                () =>
                {
                    var method = GetStaticMethod("ComputeUnmatchedEntities");
                    var matched = Guid.Parse("00000000-0000-0000-0000-000000000001");
                    var unmatched = Guid.Parse("00000000-0000-0000-0000-000000000002");

                    var entities = new BatchUpdateEntity[]
                    {
                        new() { Id = matched, CreatedAt = new DateTime(2026, 6, 18, 10, 0, 0), Counter = 1, Status = 1 },
                        new() { Id = unmatched, CreatedAt = new DateTime(2026, 6, 18, 11, 0, 0), Counter = 2, Status = 2 }
                    };
                    var matchedKeys = new HashSet<object?>();

                    var result = (System.Collections.IList)method.Invoke(null, new object[] { entities, matchedKeys })!;
                    AssertEqual(2, result.Count);
                });

            if (failures.Count > 0)
            {
                throw new InvalidOperationException(
                    "BatchXuguRepository update/retry regression failures:" + Environment.NewLine +
                    string.Join(Environment.NewLine + Environment.NewLine, failures));
            }

            Console.WriteLine("BatchXuguRepository update/retry regression harness passed.");
        }

        private static MethodInfo GetStaticMethod(string name)
        {
            var method = RepoType.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static);
            if (method == null)
            {
                throw new InvalidOperationException($"Cannot resolve private static method '{name}' on {RepoType.Name}.");
            }
            return method;
        }

        private static void AssertEqual<T>(T expected, T actual)
        {
            if (!Equals(expected, actual))
            {
                throw new InvalidOperationException($"assertion failed{Environment.NewLine}expected: {expected}{Environment.NewLine}actual:   {actual}");
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
