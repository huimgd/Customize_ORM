using Customize_ORM.Extensions;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;

namespace Test
{
    /// <summary>
    /// jsonb 字段映射和 COPY 写入值转换的回归测试。
    /// </summary>
    internal static class JsonbMappingRegression
    {
        /// <summary>
        /// 执行 jsonb 映射回归测试。
        /// </summary>
        public static void Run()
        {
            var failures = new List<string>();

            RunCase(
                failures,
                "Column(TypeName=jsonb) fields are tracked as jsonb columns",
                () =>
                {
                    var mapping = ObjectMapping<JsonbInsertEntity>.MappingForInsert;

                    AssertTrue(mapping.JsonbColumns.Contains("extra_properties"), "extra_properties should be jsonb.");
                    AssertTrue(mapping.JsonbColumns.Contains("profile"), "profile should be jsonb.");
                    AssertTrue(mapping.JsonbColumns.Contains("raw_json"), "raw_json should be jsonb.");
                });

            RunCase(
                failures,
                "Dictionary and custom class values serialize to JsonElement for jsonb COPY values",
                () =>
                {
                    var entity = new JsonbInsertEntity
                    {
                        Id = 1,
                        ExtraProperties = new Dictionary<string, object>
                        {
                            ["source"] = "api",
                            ["retry"] = 3,
                        },
                        Profile = new JsonbProfile
                        {
                            Name = "Tom",
                            Level = 2,
                        }
                    };

                    var values = ObjectMapping<JsonbInsertEntity>.RowFactory(entity);

                    AssertJsonProperty(values[1], "source", "api");
                    AssertJsonProperty(values[1], "retry", 3);
                    AssertJsonProperty(values[2], "Name", "Tom");
                    AssertJsonProperty(values[2], "Level", 2);
                });

            RunCase(
                failures,
                "JsonElement string and null values are handled consistently for jsonb fields",
                () =>
                {
                    using var document = JsonDocument.Parse("""{"enabled":true}""");
                    var entity = new JsonbInsertEntity
                    {
                        Id = 2,
                        ExtraProperties = null,
                        Profile = document.RootElement,
                        RawJson = """{"status":"ok"}""",
                    };

                    var values = ObjectMapping<JsonbInsertEntity>.RowFactory(entity);

                    AssertTrue(values[1] == DBNull.Value, "null jsonb value should be DBNull.Value.");
                    AssertJsonProperty(values[2], "enabled", true);
                    AssertJsonProperty(values[3], "status", "ok");
                });

            if (failures.Count > 0)
            {
                throw new InvalidOperationException(
                    "JsonbMapping regression failures:" + Environment.NewLine +
                    string.Join(Environment.NewLine + Environment.NewLine, failures));
            }

            Console.WriteLine("JsonbMapping regression harness passed.");
        }

        /// <summary>
        /// 断言指定条件为 true。
        /// </summary>
        /// <param name="condition">待验证条件。</param>
        /// <param name="message">失败提示。</param>
        private static void AssertTrue(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }

        /// <summary>
        /// 断言 JSON 值中指定属性等于预期值。
        /// </summary>
        /// <typeparam name="TValue">预期值类型。</typeparam>
        /// <param name="actual">实际 JSON 值。</param>
        /// <param name="propertyName">属性名。</param>
        /// <param name="expected">预期值。</param>
        private static void AssertJsonProperty<TValue>(object actual, string propertyName, TValue expected)
        {
            if (actual is not JsonElement jsonElement)
            {
                throw new InvalidOperationException($"expected JsonElement, actual: {actual.GetType().Name}");
            }

            var actualValue = jsonElement.GetProperty(propertyName).Deserialize<TValue>();
            if (!EqualityComparer<TValue>.Default.Equals(actualValue, expected))
            {
                throw new InvalidOperationException(
                    $"json property mismatch. property: {propertyName}, expected: {expected}, actual: {actualValue}");
            }
        }

        /// <summary>
        /// 执行单个回归用例，并收集失败信息。
        /// </summary>
        /// <param name="failures">失败信息集合。</param>
        /// <param name="name">用例名称。</param>
        /// <param name="assertion">断言逻辑。</param>
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
        /// jsonb 回归测试实体。
        /// </summary>
        private sealed class JsonbInsertEntity
        {
            /// <summary>
            /// 主键。
            /// </summary>
            public int Id { get; set; }

            /// <summary>
            /// 字典形式的 jsonb 扩展属性。
            /// </summary>
            [Column("extra_properties", TypeName = "jsonb")]
            public Dictionary<string, object>? ExtraProperties { get; set; }

            /// <summary>
            /// 自定义对象形式的 jsonb 属性。
            /// </summary>
            [Column("profile", TypeName = "jsonb")]
            public object? Profile { get; set; }

            /// <summary>
            /// 字符串形式的 jsonb 原始内容。
            /// </summary>
            [Column("raw_json", TypeName = "jsonb")]
            public string? RawJson { get; set; }
        }

        /// <summary>
        /// 用于验证自定义对象序列化的测试模型。
        /// </summary>
        private sealed class JsonbProfile
        {
            /// <summary>
            /// 名称。
            /// </summary>
            public string Name { get; set; } = string.Empty;

            /// <summary>
            /// 等级。
            /// </summary>
            public int Level { get; set; }
        }
    }
}
