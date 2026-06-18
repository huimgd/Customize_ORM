using Customize_ORM.Extensions.Sql;
using Npgsql;
using System.Data;

namespace Test
{
    internal static class PostgreSqlParameterFactoryRegression
    {
        public static void Run()
        {
            var failures = new List<string>();

            RunCase(
                failures,
                "PostgreSQL parameter factory normalizes ulong scalars and arrays",
                () =>
                {
                    var scalar = PostgreSqlParameterFactory.Create("counter", 42UL, typeof(ulong));
                    var array = PostgreSqlParameterFactory.Create("scores", new ulong[] { 1UL, 2UL }, typeof(ulong[]));

                    AssertParameter("counter", scalar, expectedValue: 42m, expectedDbType: null);
                    AssertParameter("scores", array, expectedValue: new decimal[] { 1m, 2m }, expectedDbType: DbType.Object);
                });

            RunCase(
                failures,
                "PostgreSQL parameter factory materializes empty arrays for null array properties",
                () =>
                {
                    var parameter = PostgreSqlParameterFactory.Create("tags", null, typeof(string[]));

                    if (parameter.Value is not Array array)
                    {
                        throw new InvalidOperationException("Expected array value for null array property.");
                    }

                    if (array.Length != 0 || array.GetType() != typeof(string[]))
                    {
                        throw new InvalidOperationException($"Expected empty string[] but found {array.GetType().Name} length {array.Length}.");
                    }

                    if (parameter.DbType != DbType.Object)
                    {
                        throw new InvalidOperationException($"Expected DbType.Object but found {parameter.DbType}.");
                    }
                });

            RunCase(
                failures,
                "PostgreSQL parameter factory maps null scalars to DBNull",
                () =>
                {
                    var parameter = PostgreSqlParameterFactory.Create("remark", null, typeof(string));
                    AssertParameter("remark", parameter, expectedValue: DBNull.Value, expectedDbType: null);
                });

            if (failures.Count > 0)
            {
                throw new InvalidOperationException(
                    "PostgreSqlParameterFactory regression failures:" + Environment.NewLine +
                    string.Join(Environment.NewLine + Environment.NewLine, failures));
            }

            Console.WriteLine("PostgreSqlParameterFactory regression harness passed.");
        }

        private static void AssertParameter(string expectedName, NpgsqlParameter parameter, object expectedValue, DbType? expectedDbType)
        {
            if (!string.Equals(expectedName, parameter.ParameterName, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"parameter name mismatch{Environment.NewLine}expected: {expectedName}{Environment.NewLine}actual:   {parameter.ParameterName}");
            }

            if (expectedValue is Array expectedArray && parameter.Value is Array actualArray)
            {
                AssertArrayEqual(expectedArray, actualArray);
            }
            else if (!Equals(expectedValue, parameter.Value))
            {
                throw new InvalidOperationException(
                    $"parameter value mismatch{Environment.NewLine}expected: {expectedValue}{Environment.NewLine}actual:   {parameter.Value}");
            }

            if (expectedDbType.HasValue && parameter.DbType != expectedDbType.Value)
            {
                throw new InvalidOperationException(
                    $"parameter dbtype mismatch{Environment.NewLine}expected: {expectedDbType.Value}{Environment.NewLine}actual:   {parameter.DbType}");
            }
        }

        private static void AssertArrayEqual(Array expected, Array actual)
        {
            if (expected.Length != actual.Length || expected.GetType() != actual.GetType())
            {
                throw new InvalidOperationException(
                    $"array mismatch{Environment.NewLine}expected: {expected.GetType().Name}[{expected.Length}]{Environment.NewLine}actual:   {actual.GetType().Name}[{actual.Length}]");
            }

            for (var i = 0; i < expected.Length; i++)
            {
                var expectedValue = expected.GetValue(i);
                var actualValue = actual.GetValue(i);
                if (!Equals(expectedValue, actualValue))
                {
                    throw new InvalidOperationException(
                        $"array value mismatch at index {i}{Environment.NewLine}expected: {expectedValue}{Environment.NewLine}actual:   {actualValue}");
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
