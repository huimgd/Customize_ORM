using System.Collections.ObjectModel;
using System.Data.Common;

namespace Customize_ORM.Extensions.Sql
{
    /// <summary>
    /// Tracks generated SQL parameters and their literal values for expression rendering.
    /// </summary>
    public sealed class SqlParameterContext
    {
        private readonly SqlDialect _dialect;
        private readonly List<DbParameter> _parameters = [];
        private readonly Dictionary<string, object?> _literalValues = [];
        private int _parameterIndex;

        public SqlParameterContext(SqlDialect dialect)
        {
            _dialect = dialect ?? throw new ArgumentNullException(nameof(dialect));
        }

        public string ParameterPrefix => _dialect.ParameterPrefix;

        public ReadOnlyCollection<DbParameter> Parameters => _parameters.AsReadOnly();

        public IReadOnlyDictionary<string, object?> LiteralValues => new ReadOnlyDictionary<string, object?>(_literalValues);

        public string Add(object? value)
        {
            _parameterIndex++;

            var name = $"{ParameterPrefix}p{_parameterIndex}";
            var normalizedValue = _dialect.NormalizeParameterValue(value);
            var parameter = _dialect.CreateParameter(name, normalizedValue);

            _parameters.Add(parameter);
            _literalValues[name] = normalizedValue;

            return name;
        }
    }
}
