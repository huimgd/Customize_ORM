using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

namespace Customize_ORM.Extensions.Sql
{
    /// <summary>
    /// Lightweight MySQL dialect for SQL expression generation without a concrete driver dependency.
    /// </summary>
    public sealed class MySqlDialect : SqlDialect
    {
        public override string ParameterPrefix => "@";

        protected override DbParameter CreateParameterCore(string name, object? value)
            => new MySqlPlaceholderParameter(name, value);

        private sealed class MySqlPlaceholderParameter : DbParameter
        {
            public MySqlPlaceholderParameter(string name, object? value)
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
