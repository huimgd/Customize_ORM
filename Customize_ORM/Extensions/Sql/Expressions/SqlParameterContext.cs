using Customize_ORM.Interfaces.Sql;
using System.Collections.ObjectModel;
using System.Data.Common;

namespace Customize_ORM.Extensions.Sql
{
    /// <summary>
    /// 跟踪生成的SQL参数及其用于表达式呈现的文字值。
    /// </summary>
    public sealed class SqlParameterContext
    {
        private readonly IQueryDialect _dialect;
        private readonly List<DbParameter> _parameters = [];
        private readonly Dictionary<string, object?> _literalValues = [];
        private int _parameterIndex;

        public SqlParameterContext(IQueryDialect dialect)
        {
            _dialect = dialect ?? throw new ArgumentNullException(nameof(dialect));
        }

        public string ParameterPrefix => _dialect.ParameterPrefix;

        public ReadOnlyCollection<DbParameter> Parameters => _parameters.AsReadOnly();

        public IReadOnlyDictionary<string, object?> LiteralValues => new ReadOnlyDictionary<string, object?>(_literalValues);

        /// <summary>
        /// 注册一个表达式参数并返回占位符名称。
        /// </summary>
        /// <param name="value">参数值</param>
        /// <param name="propertyType">值的 CLR 类型，默认按运行时类型推断；null 值按 object 处理</param>
        public string Add(object? value, Type? propertyType = null)
        {
            _parameterIndex++;

            var name = $"{ParameterPrefix}p{_parameterIndex}";
            var normalizedValue = _dialect.NormalizeParameterValue(value);
            var parameter = _dialect.CreateParameter(name, normalizedValue, propertyType ?? value?.GetType() ?? typeof(object));

            _parameters.Add(parameter);
            _literalValues[name] = normalizedValue;

            return name;
        }
    }
}
