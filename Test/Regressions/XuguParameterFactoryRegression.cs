using Customize_ORM.Extensions.Sql;
using System.Data;
using System.Text.Json;
using XuguClient;

namespace Test
{
    /// <summary>
    /// 回归用例：验证虚谷参数工厂的值归一化与 DbType 绑定（不连接真实数据库）。
    /// </summary>
    internal static class XuguParameterFactoryRegression
    {
        public static void Run()
        {
            var failures = new List<string>();

            RunCase(
                failures,
                "XuguParameterFactory preserves colon-name and binds core scalar DbTypes",
                () =>
                {
                    AssertParameter(":p_id", 7L, typeof(long), DbType.Int64, 7L);
                    AssertParameter(":p_status", 123, typeof(int), DbType.Int32, 123);
                    AssertParameter(":p_bool", true, typeof(bool), DbType.Boolean, true);
                    AssertParameter(":p_double", 1.5d, typeof(double), DbType.Double, 1.5d);
                    AssertParameter(":p_decimal", 1.05m, typeof(decimal), DbType.Decimal, 1.05m);
                    AssertParameter(":p_datetime", new DateTime(2026, 6, 18, 10, 0, 0), typeof(DateTime), DbType.DateTime, new DateTime(2026, 6, 18, 10, 0, 0));
                });

            RunCase(
                failures,
                "XuguParameterFactory maps special CLR types",
                () =>
                {
                    var guid = Guid.NewGuid();
                    // 驱动 XGDbType 无 GUID 前缀，TypeSetter 会把 DbType.Guid 强转为 Binary；此处按驱动实际口径断言
                    AssertParameter(":p_guid", guid, typeof(Guid), DbType.Binary, guid);

                    var bytes = new byte[] { 1, 2, 3 };
                    AssertParameter(":p_blob", bytes, typeof(byte[]), DbType.Binary, bytes);

                    // ulong 归一为 decimal 值并按 Decimal 绑定，超出 BigInt 范围的数值不再截断
                    AssertParameter(":p_ulong", 1UL, typeof(ulong), DbType.Decimal, 1m);

                    var json = JsonSerializer.SerializeToElement(new { a = 1 });
                    AssertParameter(":p_json", json, typeof(JsonElement), DbType.String, json.GetRawText());
                });

            RunCase(
                failures,
                "XuguParameterFactory converts and normalizes values",
                () =>
                {
                    // int 值按 long 属性类型收敛后绑定
                    AssertParameter(":p_scale", 5, typeof(long), DbType.Int64, 5L);
                    // null 值显式归一为 DBNull
                    AssertParameter(":p_null", null, typeof(long), null, DBNull.Value);
                });

            RunCase(
                failures,
                "XuguParameterFactory serializes array properties as JSON text",
                () =>
                {
                    // 数组属性与 DDL 层 TEXT 列对齐：参数侧统一 JSON 文本绑定，不再拒绝
                    AssertParameter(":p_tags", new[] { "a", "b" }, typeof(string[]), DbType.String, "[\"a\",\"b\"]");
                    AssertParameter(":p_numbers", new[] { 1, 2 }, typeof(int[]), DbType.String, "[1,2]");
                });

            if (failures.Count > 0)
            {
                throw new InvalidOperationException(
                    "XuguParameterFactory regression failures:" + Environment.NewLine +
                    string.Join(Environment.NewLine + Environment.NewLine, failures));
            }

            Console.WriteLine("XuguParameterFactory regression harness passed.");
        }

        /// <summary>
        /// 断言参数名、值与 DbType；expectedDbType 为 null 时不校验 DbType（null 值保留驱动默认）。
        /// </summary>
        private static void AssertParameter(string name, object? value, Type propertyType, DbType? expectedDbType, object? expectedValue)
        {
            var parameter = XuguParameterFactory.Create(name, value, propertyType);

            if (parameter is not XGParameters xgParameter)
            {
                throw new InvalidOperationException($"parameter for '{name}' should be XGParameters, actual: {parameter.GetType().Name}");
            }

            if (!string.Equals(name, xgParameter.ParameterName, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"parameter name mismatch{Environment.NewLine}expected: {name}{Environment.NewLine}actual:   {xgParameter.ParameterName}");
            }

            if (!Equals(expectedValue, xgParameter.Value))
            {
                throw new InvalidOperationException($"parameter value mismatch{Environment.NewLine}expected: {expectedValue}{Environment.NewLine}actual:   {xgParameter.Value}");
            }

            if (expectedDbType.HasValue && xgParameter.DbType != expectedDbType.Value)
            {
                throw new InvalidOperationException($"parameter DbType mismatch{Environment.NewLine}expected: {expectedDbType.Value}{Environment.NewLine}actual:   {xgParameter.DbType}");
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
