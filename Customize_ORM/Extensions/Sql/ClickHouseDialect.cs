using ClickHouse.Driver.ADO.Parameters;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

namespace Customize_ORM.Extensions.Sql
{
    public sealed class ClickHouseDialect : SqlDialect
    {
        public override string ParameterPrefix => "@";

        public override object? NormalizeParameterValue(object? value)
            => value is DateTime dateTime
                ? dateTime.ToString("yyyy-MM-dd HH:mm:ss")
                : value;

        protected override DbParameter CreateParameterCore(string name, object? value)
            => new ClickHousePlaceholderParameter(name, value);

        internal static ClickHouseDbParameter Materialize(DbParameter parameter)
        {
            ArgumentNullException.ThrowIfNull(parameter);

            return new ClickHouseDbParameter
            {
                ParameterName = parameter.ParameterName,
                Value = parameter.Value ?? DBNull.Value,
                DbType = parameter.DbType,
                Direction = parameter.Direction,
                IsNullable = parameter.IsNullable,
                SourceColumn = parameter.SourceColumn,
                SourceColumnNullMapping = parameter.SourceColumnNullMapping,
                Size = parameter.Size
            };
        }

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
