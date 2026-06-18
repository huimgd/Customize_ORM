using System.Collections;
using System.Linq.Expressions;
using System.Reflection;

namespace Customize_ORM.Extensions.Sql
{
    /// <summary>
    /// Translates LINQ expressions into SQL fragments and delegates parameter creation to SqlParameterContext.
    /// </summary>
    public sealed class SqlExpressionVisitor<T>
    {
        private readonly EntityMapping<T> _mapping;
        private readonly SqlDialect _dialect;
        private readonly SqlParameterContext _parameterContext;
        private readonly string? _tableAlias;

        public SqlExpressionVisitor(
            EntityMapping<T> mapping,
            SqlDialect dialect,
            SqlParameterContext parameterContext,
            string? tableAlias = null)
        {
            _mapping = mapping ?? throw new ArgumentNullException(nameof(mapping));
            _dialect = dialect ?? throw new ArgumentNullException(nameof(dialect));
            _parameterContext = parameterContext ?? throw new ArgumentNullException(nameof(parameterContext));
            _tableAlias = tableAlias;

            if (!string.Equals(_dialect.ParameterPrefix, _parameterContext.ParameterPrefix, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("SqlDialect and SqlParameterContext must use the same parameter prefix.");
            }
        }

        public string TranslatePredicate(Expression<Func<T, bool>> expression)
        {
            ArgumentNullException.ThrowIfNull(expression);
            return Translate(expression);
        }

        public string TranslateOrderBy(Expression<Func<T, object?>> expression)
        {
            ArgumentNullException.ThrowIfNull(expression);
            return GetColumnReference(expression.Body);
        }

        public string Translate(Expression expression)
        {
            ArgumentNullException.ThrowIfNull(expression);

            return expression.NodeType switch
            {
                ExpressionType.Lambda => Translate(((LambdaExpression)expression).Body),
                ExpressionType.LessThan or ExpressionType.LessThanOrEqual or
                ExpressionType.GreaterThan or ExpressionType.GreaterThanOrEqual or
                ExpressionType.Equal or ExpressionType.NotEqual or
                ExpressionType.AndAlso or ExpressionType.OrElse => TranslateBinary((BinaryExpression)expression),
                ExpressionType.Convert or ExpressionType.Not => TranslateUnary((UnaryExpression)expression),
                ExpressionType.MemberAccess => TranslateMemberAccess((MemberExpression)expression),
                ExpressionType.Constant => TranslateConstant((ConstantExpression)expression),
                ExpressionType.Call => TranslateMethodCall((MethodCallExpression)expression),
                _ => throw new NotSupportedException($"Unsupported expression type: {expression.NodeType}"),
            };
        }

        private string TranslateBinary(BinaryExpression binary)
        {
            if (binary.NodeType == ExpressionType.AndAlso || binary.NodeType == ExpressionType.OrElse)
            {
                var left = Translate(binary.Left);
                var right = Translate(binary.Right);
                var op = binary.NodeType == ExpressionType.AndAlso ? "AND" : "OR";
                return $"({left} {op} {right})";
            }

            if (TryTranslateNullComparison(binary, out var nullComparison))
            {
                return nullComparison;
            }

            var leftFragment = GetSqlFragment(binary.Left);
            var rightFragment = GetSqlFragment(binary.Right);
            return $"{leftFragment} {GetSqlOperator(binary.NodeType)} {rightFragment}";
        }

        private string TranslateUnary(UnaryExpression unary)
        {
            return unary.NodeType switch
            {
                ExpressionType.Not => $"NOT ({Translate(unary.Operand)})",
                ExpressionType.Convert => Translate(unary.Operand),
                _ => throw new NotSupportedException($"Unsupported unary expression: {unary.NodeType}"),
            };
        }

        private string TranslateMemberAccess(MemberExpression member)
        {
            if (TryGetColumnReference(member, out var column))
            {
                return column;
            }

            return AddParameter(Evaluate(member));
        }

        private string TranslateConstant(ConstantExpression constant)
        {
            return constant.Value is null
                ? "NULL"
                : AddParameter(constant.Value);
        }

        private string TranslateMethodCall(MethodCallExpression call)
        {
            var method = call.Method.Name.ToLowerInvariant();

            return method switch
            {
                "contains" when call.Object?.Type == typeof(string) => TranslateStringSearch(call, TextSearch.Contains),
                "startswith" => TranslateStringSearch(call, TextSearch.StartsWith),
                "endswith" => TranslateStringSearch(call, TextSearch.EndsWith),
                "contains" => TranslateInClause(call),
                _ => throw new NotSupportedException($"Unsupported method: {method}"),
            };
        }

        private string TranslateStringSearch(MethodCallExpression call, TextSearch search)
        {
            var column = GetColumnReference(call.Object!);
            var value = Evaluate(call.Arguments[0])
                ?? throw new ArgumentNullException("value", "String search values cannot be null.");
            var pattern = search switch
            {
                TextSearch.Contains => $"%{value}%",
                TextSearch.StartsWith => $"{value}%",
                TextSearch.EndsWith => $"%{value}",
                _ => value?.ToString() ?? string.Empty
            };

            return $"{column} LIKE {AddParameter(pattern)}";
        }

        private string TranslateInClause(MethodCallExpression call)
        {
            Expression collectionExpression;
            Expression valueExpression;

            if (call.Object != null && call.Arguments.Count == 1)
            {
                collectionExpression = call.Object;
                valueExpression = call.Arguments[0];
            }
            else if (call.Arguments.Count == 2)
            {
                collectionExpression = call.Arguments[0];
                valueExpression = call.Arguments[1];
            }
            else
            {
                throw new NotSupportedException("Unsupported IN expression shape.");
            }

            collectionExpression = NormalizeCollectionExpression(collectionExpression);

            if (Evaluate(collectionExpression) is not IEnumerable values)
            {
                throw new ArgumentException("Could not evaluate the IN collection.");
            }

            var column = GetColumnReference(valueExpression);
            var placeholders = new List<string>();

            foreach (var value in values)
            {
                placeholders.Add(AddParameter(value));
            }

            return placeholders.Count == 0
                ? "1 = 0"
                : $"{column} IN ({string.Join(", ", placeholders)})";
        }

        private string GetSqlFragment(Expression expression)
        {
            return TryGetColumnReference(expression, out var column)
                ? column
                : expression is ConstantExpression { Value: null }
                    ? "NULL"
                    : AddParameter(Evaluate(expression));
        }

        private string GetColumnReference(Expression expression)
        {
            if (TryGetColumnReference(expression, out var column))
            {
                return column;
            }

            throw new ArgumentException("Expression does not resolve to a mapped column.", nameof(expression));
        }

        private bool TryGetColumnReference(Expression expression, out string column)
        {
            expression = UnwrapConvert(expression);

            if (expression is MemberExpression member && member.Expression is ParameterExpression)
            {
                var property = member.Member as PropertyInfo
                    ?? throw new ArgumentException($"Member {member.Member.Name} is not a property.");
                var columnName = _mapping.GetColumnName(property);
                column = string.IsNullOrWhiteSpace(_tableAlias)
                    ? columnName
                    : $"{_tableAlias}.{columnName}";
                return true;
            }

            column = string.Empty;
            return false;
        }

        private static Expression UnwrapConvert(Expression expression)
        {
            while (expression.NodeType == ExpressionType.Convert)
            {
                expression = ((UnaryExpression)expression).Operand;
            }

            return expression;
        }

        private bool TryTranslateNullComparison(BinaryExpression binary, out string sql)
        {
            if (TryResolveNullComparison(binary.Left, binary.Right, binary.NodeType, out sql))
            {
                return true;
            }

            if (TryResolveNullComparison(binary.Right, binary.Left, binary.NodeType, out sql))
            {
                return true;
            }

            sql = string.Empty;
            return false;
        }

        private bool TryResolveNullComparison(
            Expression candidateNullSide,
            Expression candidateColumnSide,
            ExpressionType nodeType,
            out string sql)
        {
            if (TryGetColumnReference(candidateColumnSide, out var column) && IsNullValue(candidateNullSide))
            {
                sql = BuildNullComparison(column, nodeType);
                return true;
            }

            sql = string.Empty;
            return false;
        }

        private string BuildNullComparison(string column, ExpressionType nodeType)
        {
            return nodeType switch
            {
                ExpressionType.Equal => $"{column} IS NULL",
                ExpressionType.NotEqual => $"{column} IS NOT NULL",
                _ => throw new NotSupportedException($"NULL comparisons do not support operator {nodeType}."),
            };
        }

        private static bool IsNullValue(Expression expression)
        {
            expression = UnwrapConvert(expression);

            if (expression is ConstantExpression { Value: null })
            {
                return true;
            }

            return TryEvaluate(expression, out var value) && value == null;
        }

        private string AddParameter(object? value)
        {
            return _parameterContext.Add(value);
        }

        private static object? Evaluate(Expression expression)
        {
            expression = UnwrapConvert(expression);

            if (TryEvaluateWithoutCompilation(expression, out var value))
            {
                return value;
            }

            var lambda = Expression.Lambda<Func<object?>>(
                Expression.Convert(expression, typeof(object)));
            return lambda.Compile(preferInterpretation: true).Invoke();
        }

        private static bool TryEvaluate(Expression expression, out object? value)
        {
            try
            {
                value = Evaluate(expression);
                return true;
            }
            catch
            {
                value = null;
                return false;
            }
        }

        private static bool TryEvaluateWithoutCompilation(Expression expression, out object? value)
        {
            switch (expression)
            {
                case ConstantExpression constantExpression:
                    value = constantExpression.Value;
                    return true;
                case NewArrayExpression newArrayExpression:
                    var elementType = expression.Type.GetElementType() ?? typeof(object);
                    var array = Array.CreateInstance(elementType, newArrayExpression.Expressions.Count);
                    for (var i = 0; i < newArrayExpression.Expressions.Count; i++)
                    {
                        array.SetValue(Evaluate(newArrayExpression.Expressions[i]), i);
                    }

                    value = array;
                    return true;
                case ListInitExpression listInitExpression:
                    var list = Evaluate(listInitExpression.NewExpression);
                    foreach (var initializer in listInitExpression.Initializers)
                    {
                        var arguments = initializer.Arguments.Select(Evaluate).ToArray();
                        initializer.AddMethod.Invoke(list, arguments);
                    }

                    value = list;
                    return true;
                case MemberExpression memberExpression:
                    return TryEvaluateMemberAccess(memberExpression, out value);
                default:
                    value = null;
                    return false;
            }
        }

        private static Expression NormalizeCollectionExpression(Expression expression)
        {
            while (true)
            {
                expression = UnwrapConvert(expression);

                if (expression is MethodCallExpression methodCall &&
                    TryUnwrapCollectionFactory(methodCall, out var sourceExpression))
                {
                    expression = sourceExpression;
                    continue;
                }

                return expression;
            }
        }

        private static bool TryUnwrapCollectionFactory(MethodCallExpression methodCall, out Expression sourceExpression)
        {
            if (methodCall.Method.Name == "AsSpan")
            {
                sourceExpression = methodCall.Object ?? methodCall.Arguments[0];
                return true;
            }

            if (methodCall.Method.Name == "op_Implicit" &&
                methodCall.Arguments.Count == 1 &&
                methodCall.Method.ReturnType.IsByRefLike)
            {
                sourceExpression = methodCall.Arguments[0];
                return true;
            }

            if (methodCall.Method.Name == "ToArray" &&
                methodCall.Object == null &&
                methodCall.Arguments.Count == 1)
            {
                sourceExpression = methodCall.Arguments[0];
                return true;
            }

            sourceExpression = null!;
            return false;
        }

        private static bool TryEvaluateMemberAccess(MemberExpression memberExpression, out object? value)
        {
            object? target = null;
            if (memberExpression.Expression != null &&
                !TryEvaluateWithoutCompilation(UnwrapConvert(memberExpression.Expression), out target))
            {
                value = null;
                return false;
            }

            switch (memberExpression.Member)
            {
                case FieldInfo fieldInfo:
                    value = fieldInfo.GetValue(target);
                    return true;
                case PropertyInfo propertyInfo when propertyInfo.GetIndexParameters().Length == 0:
                    value = propertyInfo.GetValue(target);
                    return true;
                default:
                    value = null;
                    return false;
            }
        }

        private static string GetSqlOperator(ExpressionType type) => type switch
        {
            ExpressionType.Equal => "=",
            ExpressionType.NotEqual => "<>",
            ExpressionType.GreaterThan => ">",
            ExpressionType.GreaterThanOrEqual => ">=",
            ExpressionType.LessThan => "<",
            ExpressionType.LessThanOrEqual => "<=",
            _ => throw new NotSupportedException($"Unsupported operator: {type}"),
        };

        private enum TextSearch
        {
            Contains,
            StartsWith,
            EndsWith
        }
    }
}
