using ClickHouse.Driver.ADO.Parameters;
using Customize_ORM.Enums;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

namespace Customize_ORM.Extensions.Sql
{
    public sealed class ClickHouseDialect : SqlDialect
    {
        public override DBType DbType => DBType.ClickHouse;

        public override string ParameterPrefix => "@";

        public override object? NormalizeParameterValue(object? value)
            => value is DateTime dateTime
                ? dateTime.ToString("yyyy-MM-dd HH:mm:ss")
                : value;

        protected override DbParameter CreateParameterCore(string name, object? value)
            => new ClickHousePlaceholderParameter(name, value);

        /// <summary>
        /// ClickHouse 无事务性删除语义，不支持按主键删除。
        /// </summary>
        public override string BuildDeleteByKeysSql(
            string tableName,
            string keyColumn,
            IReadOnlyList<object?> keyValues,
            Type keyType,
            out List<DbParameter> parameters)
        {
            parameters = [];
            throw new NotSupportedException(
                $"ClickHouse 不支持按主键删除（{tableName}.{keyColumn}），请改用 ALTER TABLE ... DELETE 或重建表。");
        }

        internal static ClickHouseDbParameter Materialize(DbParameter parameter)
        {
            ArgumentNullException.ThrowIfNull(parameter);

            return new ClickHouseDbParameter
            {
                // ClickHouse SQL can use @-style placeholders, but the driver binds parameters by bare name.
                ParameterName = NormalizeParameterNameForBinding(parameter.ParameterName),
                Value = parameter.Value ?? DBNull.Value,
                DbType = parameter.DbType,
                Direction = parameter.Direction,
                IsNullable = parameter.IsNullable,
                SourceColumn = parameter.SourceColumn,
                SourceColumnNullMapping = parameter.SourceColumnNullMapping,
                Size = parameter.Size
            };
        }

        private static string NormalizeParameterNameForBinding(string parameterName)
            => parameterName.StartsWith("@", StringComparison.Ordinal)
                ? parameterName[1..]
                : parameterName;

        private sealed class ClickHousePlaceholderParameter : DbParameter
        {
            public ClickHousePlaceholderParameter(string name, object? value)
            {
                ParameterName = name;
                Value = value;
            }

            public override DbType DbType { get; set; }

            public override ParameterDirection Direction { get; set; } = ParameterDirection.Input;

            public override bool IsNullable { get; set; }

            [AllowNull]
            public override string ParameterName { get; set; } = string.Empty;

            [AllowNull]
            public override string SourceColumn { get; set; } = string.Empty;

            public override object? Value { get; set; }

            public override bool SourceColumnNullMapping { get; set; }

            public override int Size { get; set; }

            public override void ResetDbType()
            {
            }
        }
    }
}
