using Customize_ORM.Extensions.Sql;
using System.Data;

namespace Test
{
    internal static class DbRecordMapperRegression
    {
        public static void Run()
        {
            var failures = new List<string>();

            RunCase(
                failures,
                "DbRecordMapper maps scalar values",
                () =>
                {
                    using var reader = CreateReader(("count", typeof(long), 42L));
                    reader.Read();
                    var value = DbRecordMapper.MapSingle<long>(reader);
                    AssertEqual(42L, value);
                });

            RunCase(
                failures,
                "DbRecordMapper maps nullable scalar nulls",
                () =>
                {
                    using var reader = CreateReader(("count", typeof(int), DBNull.Value));
                    reader.Read();
                    var value = DbRecordMapper.MapSingle<int?>(reader);
                    AssertEqual<int?>(null, value);
                });

            RunCase(
                failures,
                "DbRecordMapper maps underscored columns to DTO properties",
                () =>
                {
                    using var reader = CreateReader(
                        ("user_id", typeof(long), 7L),
                        ("display_name", typeof(string), "alice"),
                        ("is_deleted", typeof(bool), false));

                    reader.Read();
                    var dto = DbRecordMapper.MapSingle<UserRow>(reader);

                    AssertEqual(7L, dto.UserId);
                    AssertEqual("alice", dto.DisplayName);
                    AssertEqual(false, dto.IsDeleted);
                });

            if (failures.Count > 0)
            {
                throw new InvalidOperationException(
                    "DbRecordMapper regression failures:" + Environment.NewLine +
                    string.Join(Environment.NewLine + Environment.NewLine, failures));
            }

            Console.WriteLine("DbRecordMapper regression harness passed.");
        }

        private static DataTableReader CreateReader(params (string Name, Type Type, object Value)[] columns)
        {
            var table = new DataTable();
            foreach (var column in columns)
            {
                table.Columns.Add(column.Name, column.Type);
            }

            var row = table.NewRow();
            for (var i = 0; i < columns.Length; i++)
            {
                row[i] = columns[i].Value;
            }

            table.Rows.Add(row);
            return table.CreateDataReader();
        }

        private static void AssertEqual<T>(T expected, T actual)
        {
            if (!Equals(expected, actual))
            {
                throw new InvalidOperationException(
                    $"Expected '{expected ?? default}' but found '{actual ?? default}'.");
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

        private sealed class UserRow
        {
            public long UserId { get; set; }

            public string? DisplayName { get; set; }

            public bool IsDeleted { get; set; }
        }
    }
}
