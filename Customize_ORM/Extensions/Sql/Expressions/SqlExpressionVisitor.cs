using Customize_ORM.Interfaces.Sql;
using System.Collections;
using System.Linq.Expressions;
using System.Reflection;

namespace Customize_ORM.Extensions.Sql
{
    /// <summary>
    /// 将 LINQ 表达式翻译为 SQL 片段，并在必要时委托参数上下文生成参数。
    /// </summary>
    public sealed class SqlExpressionVisitor<T>
    {
        private readonly EntityMapping<T> _mapping;
        private readonly IQueryDialect _dialect;
        private readonly SqlParameterContext _parameterContext;
        private readonly string? _tableAlias;

        /// <summary>
        /// 初始化表达式访问器。
        /// </summary>
        public SqlExpressionVisitor(
            EntityMapping<T> mapping,
            IQueryDialect dialect,
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

        /// <summary>
        /// 翻译布尔筛选表达式。
        /// </summary>
        public string TranslatePredicate(Expression<Func<T, bool>> expression)
        {
            ArgumentNullException.ThrowIfNull(expression);
            return Translate(expression);
        }

        /// <summary>
        /// 翻译排序表达式。
        /// </summary>
        public string TranslateOrderBy(Expression<Func<T, object?>> expression)
        {
            ArgumentNullException.ThrowIfNull(expression);
            return GetColumnReference(expression.Body);
        }

        /// <summary>
        /// 翻译任意支持的表达式节点。
        /// </summary>
        public string Translate(Expression expression)
        {
            ArgumentNullException.ThrowIfNull(expression);

            expression = UnwrapConvert(expression);

            return expression.NodeType switch
            {
                ExpressionType.Lambda => Translate(((LambdaExpression)expression).Body),
                ExpressionType.LessThan or ExpressionType.LessThanOrEqual or
                ExpressionType.GreaterThan or ExpressionType.GreaterThanOrEqual or
                ExpressionType.Equal or ExpressionType.NotEqual or
                ExpressionType.AndAlso or ExpressionType.OrElse => TranslateBinary((BinaryExpression)expression),
                ExpressionType.Convert or ExpressionType.ConvertChecked or ExpressionType.Not => TranslateUnary((UnaryExpression)expression),
                ExpressionType.MemberAccess => TranslateMemberAccess((MemberExpression)expression),
                ExpressionType.Constant => TranslateConstant((ConstantExpression)expression),
                ExpressionType.Call => TranslateMethodCall((MethodCallExpression)expression),
                _ => throw new NotSupportedException($"Unsupported expression type: {expression.NodeType}"),
            };
        }

        /// <summary>
        /// 翻译二元表达式。
        /// </summary>
        private string TranslateBinary(BinaryExpression binary)
        {
            if (binary.NodeType == ExpressionType.AndAlso || binary.NodeType == ExpressionType.OrElse)
            {
                if (TrySimplifyLogicalBinary(binary, out var simplifiedSql))
                {
                    return simplifiedSql;
                }

                var left = Translate(binary.Left);
                var right = Translate(binary.Right);
                var op = binary.NodeType == ExpressionType.AndAlso ? "AND" : "OR";
                return $"({left} {op} {right})";
            }

            if (TryTranslateNullComparison(binary, out var nullComparison))
            {
                return nullComparison;
            }

            var leftFragment = GetSqlValueFragment(binary.Left);
            var rightFragment = GetSqlValueFragment(binary.Right);
            return $"{leftFragment} {GetSqlOperator(binary.NodeType)} {rightFragment}";
        }

        /// <summary>
        /// 尝试在翻译前折叠逻辑运算中的纯闭包布尔表达式，保留 C# 的短路语义。
        /// </summary>
        private bool TrySimplifyLogicalBinary(BinaryExpression binary, out string sql)
        {
            if (TryEvaluateBooleanOperand(binary.Left, out var leftValue))
            {
                sql = SimplifyLogicalBinaryByConstant(binary.NodeType, leftValue, binary.Right);
                return true;
            }

            if (TryEvaluateBooleanOperand(binary.Right, out var rightValue))
            {
                sql = SimplifyLogicalBinaryByConstant(binary.NodeType, rightValue, binary.Left);
                return true;
            }

            sql = string.Empty;
            return false;
        }

        /// <summary>
        /// 根据常量布尔值折叠逻辑表达式的另一侧。
        /// </summary>
        private string SimplifyLogicalBinaryByConstant(ExpressionType nodeType, bool constantValue, Expression otherExpression)
        {
            if (nodeType == ExpressionType.AndAlso)
            {
                return constantValue
                    ? Translate(otherExpression)
                    : _dialect.RenderLiteral(false);
            }

            return constantValue
                ? _dialect.RenderLiteral(true)
                : Translate(otherExpression);
        }

        /// <summary>
        /// 翻译一元表达式。
        /// </summary>
        private string TranslateUnary(UnaryExpression unary)
        {
            return unary.NodeType switch
            {
                ExpressionType.Not => $"NOT ({Translate(unary.Operand)})",
                ExpressionType.Convert or ExpressionType.ConvertChecked => Translate(unary.Operand),
                _ => throw new NotSupportedException($"Unsupported unary expression: {unary.NodeType}"),
            };
        }

        /// <summary>
        /// 翻译成员访问表达式。
        /// </summary>
        private string TranslateMemberAccess(MemberExpression member)
        {
            if (TryTranslateBooleanMember(member, out var booleanFragment))
            {
                return booleanFragment;
            }

            if (TryGetColumnReference(member, out var column))
            {
                return column;
            }

            if (ContainsEntityParameter(member))
            {
                throw CreateUnsupportedEntityExpressionException(member, "该成员访问依赖实体实例，但当前不具备可翻译的 SQL 语义。");
            }

            return AddParameter(Evaluate(member));
        }

        /// <summary>
        /// 翻译常量表达式。
        /// </summary>
        private string TranslateConstant(ConstantExpression constant)
        {
            return constant.Value is null
                ? "NULL"
                : AddParameter(constant.Value);
        }

        /// <summary>
        /// 翻译方法调用表达式。
        /// </summary>
        private string TranslateMethodCall(MethodCallExpression call)
        {
            if (!ContainsEntityParameter(call))
            {
                return TranslateEvaluatedExpression(call);
            }

            if (TryTranslateStringNullCheck(call, out var nullCheck))
            {
                return nullCheck;
            }

            if (TryTranslateStringSearch(call, out var stringSearch))
            {
                return stringSearch;
            }

            if (TryTranslateCollectionContains(call, out var inClause))
            {
                return inClause;
            }

            throw CreateUnsupportedEntityExpressionException(call, $"方法 {call.Method.DeclaringType?.Name}.{call.Method.Name} 无法翻译为 SQL。");
        }

        /// <summary>
        /// 尝试翻译布尔成员访问，例如 bool 列或 Nullable.HasValue。
        /// </summary>
        private bool TryTranslateBooleanMember(MemberExpression member, out string sql)
        {
            if (TryTranslateNullableHasValue(member, out sql))
            {
                return true;
            }

            if (member.Type == typeof(bool) && TryGetColumnReference(member, out var column))
            {
                sql = column;
                return true;
            }

            sql = string.Empty;
            return false;
        }

        /// <summary>
        /// 尝试将 Nullable.HasValue 翻译为 IS NOT NULL 语义。
        /// </summary>
        private bool TryTranslateNullableHasValue(MemberExpression member, out string sql)
        {
            if (member.Member.Name == nameof(Nullable<int>.HasValue) &&
                member.Expression != null &&
                IsNullableType(member.Expression.Type) &&
                TryGetColumnReference(member.Expression, out var column))
            {
                sql = $"{column} IS NOT NULL";
                return true;
            }

            sql = string.Empty;
            return false;
        }

        /// <summary>
        /// 尝试将闭包中的 Nullable 成员访问退化为其底层可求值表达式。
        /// </summary>
        private static bool TryUnwrapEvaluableNullableMember(MemberExpression member, out Expression expression)
        {
            expression = null!;

            if (member.Expression == null || ContainsEntityParameter(member.Expression))
            {
                return false;
            }

            if (member.Member.Name == nameof(Nullable<int>.HasValue) &&
                IsNullableType(member.Expression.Type))
            {
                expression = Expression.NotEqual(
                    UnwrapConvert(member.Expression),
                    Expression.Constant(null, member.Expression.Type));
                return true;
            }

            if (member.Member.Name == nameof(Nullable<int>.Value) &&
                IsNullableType(member.Expression.Type))
            {
                expression = member.Expression;
                return true;
            }

            return false;
        }

        /// <summary>
        /// 获取表达式的 SQL 值片段。
        /// </summary>
        private string GetSqlValueFragment(Expression expression)
        {
            expression = UnwrapConvert(expression);

            if (TryTranslateSqlValueExpression(expression, out var sql))
            {
                return sql;
            }

            if (expression is ConstantExpression { Value: null })
            {
                return "NULL";
            }

            if (ContainsEntityParameter(expression))
            {
                throw CreateUnsupportedEntityExpressionException(expression, "该表达式依赖实体实例，但当前不具备可翻译的 SQL 语义。");
            }

            return AddParameter(Evaluate(expression));
        }

        /// <summary>
        /// 获取映射列引用。
        /// </summary>
        private string GetColumnReference(Expression expression)
        {
            if (TryGetColumnReference(expression, out var column))
            {
                return column;
            }

            throw new ArgumentException("Expression does not resolve to a mapped column.", nameof(expression));
        }

        /// <summary>
        /// 尝试将表达式识别为映射列。
        /// </summary>
        private bool TryGetColumnReference(Expression expression, out string column)
        {
            expression = UnwrapConvert(expression);

            if (expression is MemberExpression nullableValueMember &&
                nullableValueMember.Member.Name == nameof(Nullable<int>.Value) &&
                nullableValueMember.Expression != null)
            {
                expression = UnwrapConvert(nullableValueMember.Expression);
            }

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

        /// <summary>
        /// 去除表达式外层的 Convert/ConvertChecked 包装。
        /// </summary>
        private static Expression UnwrapConvert(Expression expression)
        {
            while (expression.NodeType is ExpressionType.Convert or ExpressionType.ConvertChecked)
            {
                expression = ((UnaryExpression)expression).Operand;
            }

            return expression;
        }

        /// <summary>
        /// 尝试将空值比较翻译为 IS NULL / IS NOT NULL。
        /// </summary>
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

        /// <summary>
        /// 尝试解析某一侧为 null 的比较表达式。
        /// </summary>
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

        /// <summary>
        /// 构建空值比较 SQL。
        /// </summary>
        private string BuildNullComparison(string column, ExpressionType nodeType)
        {
            return nodeType switch
            {
                ExpressionType.Equal => $"{column} IS NULL",
                ExpressionType.NotEqual => $"{column} IS NOT NULL",
                _ => throw new NotSupportedException($"NULL comparisons do not support operator {nodeType}."),
            };
        }

        /// <summary>
        /// 判断表达式是否可视为 null 常量。
        /// </summary>
        private static bool IsNullValue(Expression expression)
        {
            expression = UnwrapConvert(expression);

            if (expression is ConstantExpression { Value: null })
            {
                return true;
            }

            return !ContainsEntityParameter(expression) && TryEvaluate(expression, out var value) && value == null;
        }

        /// <summary>
        /// 将值加入参数上下文并返回占位符。
        /// </summary>
        private string AddParameter(object? value)
        {
            return _parameterContext.Add(value);
        }

        /// <summary>
        /// 计算不依赖实体实例的表达式值。
        /// </summary>
        private static object? Evaluate(Expression expression)
        {
            expression = UnwrapConvert(expression);

            if (ContainsEntityParameter(expression))
            {
                throw new InvalidOperationException("Cannot evaluate an expression that still depends on the entity parameter.");
            }

            if (TryEvaluateWithoutCompilation(expression, out var value))
            {
                return value;
            }

            var lambda = Expression.Lambda<Func<object?>>(
                Expression.Convert(expression, typeof(object)));
            return lambda.Compile(preferInterpretation: true).Invoke();
        }

        /// <summary>
        /// 尝试计算表达式，并在失败时返回 false。
        /// </summary>
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

        /// <summary>
        /// 尝试将不依赖实体参数的操作数计算为布尔值。
        /// </summary>
        private static bool TryEvaluateBooleanOperand(Expression expression, out bool value)
        {
            if (!ContainsEntityParameter(expression) &&
                TryEvaluate(expression, out var evaluatedValue) &&
                evaluatedValue is bool booleanValue)
            {
                value = booleanValue;
                return true;
            }

            value = false;
            return false;
        }

        /// <summary>
        /// 优先使用轻量路径计算本地表达式，避免不必要的编译。
        /// </summary>
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

        /// <summary>
        /// 规范化 IN 集合表达式，去除工厂方法包装。
        /// </summary>
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

        /// <summary>
        /// 尝试移除集合工厂方法包装。
        /// </summary>
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

        /// <summary>
        /// 尝试计算本地成员访问表达式。
        /// </summary>
        private static bool TryEvaluateMemberAccess(MemberExpression memberExpression, out object? value)
        {
            if (TryUnwrapEvaluableNullableMember(memberExpression, out var nullableExpression))
            {
                return TryEvaluateWithoutCompilation(nullableExpression, out value) ||
                       TryEvaluate(nullableExpression, out value);
            }

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

        /// <summary>
        /// 尝试将表达式翻译为 SQL 值片段。
        /// </summary>
        private bool TryTranslateSqlValueExpression(Expression expression, out string sql)
        {
            expression = UnwrapConvert(expression);

            if (TryGetColumnReference(expression, out sql))
            {
                return true;
            }

            if (expression is MethodCallExpression call &&
                TryTranslateSqlValueMethodCall(call, out sql))
            {
                return true;
            }

            sql = string.Empty;
            return false;
        }

        /// <summary>
        /// 尝试翻译可生成值片段的方法调用。
        /// </summary>
        private bool TryTranslateSqlValueMethodCall(MethodCallExpression call, out string sql)
        {
            if (!ContainsEntityParameter(call))
            {
                sql = string.Empty;
                return false;
            }

            if (call.Object == null)
            {
                sql = string.Empty;
                return false;
            }

            var methodName = call.Method.Name;
            if (methodName == nameof(string.Trim) && call.Arguments.Count == 0)
            {
                sql = _dialect.RenderTrim(GetSqlValueFragment(call.Object));
                return true;
            }

            if (methodName == nameof(string.ToLower) && call.Arguments.Count == 0)
            {
                sql = _dialect.RenderLower(GetSqlValueFragment(call.Object));
                return true;
            }

            if (methodName == nameof(string.ToUpper) && call.Arguments.Count == 0)
            {
                sql = _dialect.RenderUpper(GetSqlValueFragment(call.Object));
                return true;
            }

            if (methodName == nameof(object.ToString) && call.Arguments.Count == 0)
            {
                var innerSql = GetSqlValueFragment(call.Object);
                var operandType = Nullable.GetUnderlyingType(call.Object.Type) ?? call.Object.Type;
                if (operandType == typeof(string))
                {
                    sql = innerSql;
                    return true;
                }

                if (IsScalarType(operandType))
                {
                    sql = _dialect.RenderToString(innerSql, operandType);
                    return true;
                }
            }

            sql = string.Empty;
            return false;
        }

        /// <summary>
        /// 尝试翻译字符串判空方法。
        /// </summary>
        private bool TryTranslateStringNullCheck(MethodCallExpression call, out string sql)
        {
            if (call.Method.DeclaringType != typeof(string) ||
                call.Object != null ||
                call.Arguments.Count != 1)
            {
                sql = string.Empty;
                return false;
            }

            var argumentSql = GetSqlValueFragment(call.Arguments[0]);
            var emptyParameter = AddParameter(string.Empty);
            sql = call.Method.Name switch
            {
                nameof(string.IsNullOrEmpty) => $"({argumentSql} IS NULL OR {argumentSql} = {emptyParameter})",
                nameof(string.IsNullOrWhiteSpace) => $"({argumentSql} IS NULL OR {_dialect.RenderTrim(argumentSql)} = {emptyParameter})",
                _ => string.Empty
            };

            return !string.IsNullOrEmpty(sql);
        }

        /// <summary>
        /// 尝试翻译字符串搜索方法。
        /// </summary>
        private bool TryTranslateStringSearch(MethodCallExpression call, out string sql)
        {
            if (call.Object?.Type != typeof(string) || call.Arguments.Count != 1)
            {
                sql = string.Empty;
                return false;
            }

            var search = call.Method.Name switch
            {
                nameof(string.Contains) => TextSearch.Contains,
                nameof(string.StartsWith) => TextSearch.StartsWith,
                nameof(string.EndsWith) => TextSearch.EndsWith,
                _ => (TextSearch?)null
            };

            if (search == null)
            {
                sql = string.Empty;
                return false;
            }

            sql = TranslateStringSearch(call.Object, call.Arguments[0], search.Value);
            return true;
        }

        /// <summary>
        /// 将字符串搜索翻译为 LIKE 子句。
        /// </summary>
        private string TranslateStringSearch(Expression targetExpression, Expression valueExpression, TextSearch search)
        {
            var column = GetSqlValueFragment(targetExpression);
            var value = Evaluate(valueExpression)
                ?? throw new ArgumentNullException("value", "String search values cannot be null.");
            var pattern = search switch
            {
                TextSearch.Contains => $"%{value}%",
                TextSearch.StartsWith => $"{value}%",
                TextSearch.EndsWith => $"%{value}",
                _ => value.ToString() ?? string.Empty
            };

            return $"{column} LIKE {AddParameter(pattern)}";
        }

        /// <summary>
        /// 尝试将 Contains 翻译为 IN 子句。
        /// </summary>
        private bool TryTranslateCollectionContains(MethodCallExpression call, out string sql)
        {
            var isCollectionContains = call.Method.Name == nameof(Enumerable.Contains) ||
                                       (call.Method.Name == nameof(List<int>.Contains) && call.Object != null);
            if (!isCollectionContains)
            {
                sql = string.Empty;
                return false;
            }

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
            if (ContainsEntityParameter(collectionExpression))
            {
                sql = string.Empty;
                return false;
            }

            if (Evaluate(collectionExpression) is not IEnumerable values)
            {
                throw new ArgumentException("Could not evaluate the IN collection.");
            }

            var column = GetSqlValueFragment(valueExpression);
            var placeholders = new List<string>();

            foreach (var value in values)
            {
                placeholders.Add(AddParameter(value));
            }

            sql = placeholders.Count == 0
                ? "1 = 0"
                : $"{column} IN ({string.Join(", ", placeholders)})";
            return true;
        }

        /// <summary>
        /// 将纯本地表达式翻译为常量 SQL 片段。
        /// </summary>
        private string TranslateEvaluatedExpression(Expression expression)
        {
            var value = Evaluate(expression);
            if (value is null)
            {
                return "NULL";
            }

            // 布尔常量直接输出字面量，避免生成无意义的占位符谓词。
            if (value is bool booleanValue)
            {
                return _dialect.RenderLiteral(booleanValue);
            }

            return AddParameter(value);
        }

        /// <summary>
        /// 判断表达式是否仍依赖实体参数。
        /// </summary>
        private static bool ContainsEntityParameter(Expression expression)
        {
            expression = UnwrapConvert(expression);

            return expression switch
            {
                ParameterExpression => true,
                LambdaExpression lambdaExpression => ContainsEntityParameter(lambdaExpression.Body),
                UnaryExpression unaryExpression => ContainsEntityParameter(unaryExpression.Operand),
                BinaryExpression binaryExpression => ContainsEntityParameter(binaryExpression.Left) || ContainsEntityParameter(binaryExpression.Right),
                MemberExpression memberExpression => memberExpression.Expression != null && ContainsEntityParameter(memberExpression.Expression),
                MethodCallExpression methodCallExpression => (methodCallExpression.Object != null && ContainsEntityParameter(methodCallExpression.Object)) ||
                                                            methodCallExpression.Arguments.Any(ContainsEntityParameter),
                ConditionalExpression conditionalExpression => ContainsEntityParameter(conditionalExpression.Test) ||
                                                               ContainsEntityParameter(conditionalExpression.IfTrue) ||
                                                               ContainsEntityParameter(conditionalExpression.IfFalse),
                NewExpression newExpression => newExpression.Arguments.Any(ContainsEntityParameter),
                NewArrayExpression newArrayExpression => newArrayExpression.Expressions.Any(ContainsEntityParameter),
                ListInitExpression listInitExpression => ContainsEntityParameter(listInitExpression.NewExpression) ||
                                                         listInitExpression.Initializers.SelectMany(i => i.Arguments).Any(ContainsEntityParameter),
                MemberInitExpression memberInitExpression => ContainsEntityParameter(memberInitExpression.NewExpression) ||
                                                             memberInitExpression.Bindings.OfType<MemberAssignment>().Any(binding => ContainsEntityParameter(binding.Expression)),
                InvocationExpression invocationExpression => ContainsEntityParameter(invocationExpression.Expression) ||
                                                             invocationExpression.Arguments.Any(ContainsEntityParameter),
                _ => false
            };
        }

        /// <summary>
        /// 创建实体相关表达式不支持时的异常。
        /// </summary>
        private static NotSupportedException CreateUnsupportedEntityExpressionException(Expression expression, string reason)
        {
            return new NotSupportedException($"Unsupported entity-bound expression '{expression}': {reason}");
        }

        /// <summary>
        /// 判断类型是否为 Nullable。
        /// </summary>
        private static bool IsNullableType(Type type)
        {
            return Nullable.GetUnderlyingType(type) != null;
        }

        /// <summary>
        /// 判断类型是否属于可安全转换为字符串的标量类型。
        /// </summary>
        private static bool IsScalarType(Type type)
        {
            type = Nullable.GetUnderlyingType(type) ?? type;
            return type.IsEnum ||
                   type == typeof(bool) ||
                   type == typeof(byte) ||
                   type == typeof(sbyte) ||
                   type == typeof(short) ||
                   type == typeof(ushort) ||
                   type == typeof(int) ||
                   type == typeof(uint) ||
                   type == typeof(long) ||
                   type == typeof(ulong) ||
                   type == typeof(float) ||
                   type == typeof(double) ||
                   type == typeof(decimal) ||
                   type == typeof(Guid) ||
                   type == typeof(DateTime) ||
                   type == typeof(DateTimeOffset) ||
                   type == typeof(TimeSpan) ||
                   type == typeof(char) ||
                   type == typeof(string);
        }

        /// <summary>
        /// 获取 SQL 比较操作符。
        /// </summary>
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

        /// <summary>
        /// 字符串搜索模式。
        /// </summary>
        private enum TextSearch
        {
            Contains,
            StartsWith,
            EndsWith
        }
    }
}
