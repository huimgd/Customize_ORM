using Customize_ORM.Enums;
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
        public override DBType DbType => DBType.MySQL;

        public override string ParameterPrefix => "@";

        protected override DbParameter CreateParameterCore(string name, object? value)
            => new MySqlPlaceholderParameter(name, value);

        /// <summary>
        /// MySQL 按占位符展开主键集合。
        /// </summary>
        public override string BuildDeleteByKeysSql(
            string tableName,
            string keyColumn,
            IReadOnlyList<object?> keyValues,
            Type keyType,
            out List<DbParameter> parameters)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
            ArgumentException.ThrowIfNullOrWhiteSpace(keyColumn);
            ArgumentNullException.ThrowIfNull(keyValues);
            ArgumentNullException.ThrowIfNull(keyType);

            if (keyValues.Count == 0)
            {
                throw new ArgumentException("Key values cannot be empty.", nameof(keyValues));
            }

            parameters = new List<DbParameter>(keyValues.Count);
            var placeholders = new List<string>(keyValues.Count);
            for (var i = 0; i < keyValues.Count; i++)
            {
                var parameterName = $"@{keyColumn}{i}";
                parameters.Add(CreateParameter(parameterName, keyValues[i], keyType));
                placeholders.Add(parameterName);
            }

            return $"DELETE FROM {tableName} WHERE {keyColumn} IN ({string.Join(", ", placeholders)})";
        }

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
