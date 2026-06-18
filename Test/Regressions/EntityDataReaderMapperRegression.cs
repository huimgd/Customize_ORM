using Customize_ORM.Extensions;
using Customize_ORM.Extensions.Sql;
using System.Data;
using System.Data.Common;
using Test.Entities;

namespace Test
{
    internal static class EntityDataReaderMapperRegression
    {
        private static readonly Func<DbDataReader, AdminEntity> Mapper =
            EntityDataReaderMapper<AdminEntity>.Create(ObjectMapping<AdminEntity>.MappingForInsert);

        public static void Run()
        {
            var failures = new List<string>();

            RunCase(
                failures,
                "EntityDataReaderMapper maps ordered entity columns through shared mapping metadata",
                () =>
                {
                    var mapping = ObjectMapping<AdminEntity>.MappingForInsert;
                    using var reader = CreateReader(mapping.AllColumns);
                    reader.Read();

                    var entity = Mapper(reader);

                    var expectedId = Guid.Parse("00000000-0000-0000-0000-00000000000a");
                    if (entity.Id != expectedId)
                    {
                        throw new InvalidOperationException($"Expected Id={expectedId} but found {entity.Id}.");
                    }

                    if (!string.Equals(entity.Remark, "alice", StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException($"Expected Remark=alice but found {entity.Remark}.");
                    }

                    if (entity.IsDeleted)
                    {
                        throw new InvalidOperationException("Expected IsDeleted=false.");
                    }
                });

            if (failures.Count > 0)
            {
                throw new InvalidOperationException(
                    "EntityDataReaderMapper regression failures:" + Environment.NewLine +
                    string.Join(Environment.NewLine + Environment.NewLine, failures));
            }

            Console.WriteLine("EntityDataReaderMapper regression harness passed.");
        }

        private static DataTableReader CreateReader(string[] columns)
        {
            var mapping = ObjectMapping<AdminEntity>.MappingForInsert;
            var table = new DataTable();
            foreach (var column in columns)
            {
                table.Columns.Add(column, typeof(object));
            }

            var row = table.NewRow();
            for (var i = 0; i < columns.Length; i++)
            {
                row[i] = columns[i] switch
                {
                    "id" => Guid.Parse("00000000-0000-0000-0000-00000000000a"),
                    "remark" => "alice",
                    "is_deleted" => false,
                    _ => GetDefaultValue(mapping, columns[i])
                };
            }

            table.Rows.Add(row);
            return table.CreateDataReader();
        }

        private static object GetDefaultValue(EntityMapping<AdminEntity> mapping, string columnName)
        {
            if (!mapping.PropertyMap.TryGetValue(columnName, out var property))
            {
                return DBNull.Value;
            }

            var propertyType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            return propertyType.IsValueType ? Activator.CreateInstance(propertyType)! : DBNull.Value;
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
