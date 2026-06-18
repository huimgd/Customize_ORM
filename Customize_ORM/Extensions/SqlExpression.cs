using ClickHouse.Driver.ADO.Parameters;
using Customize_ORM.Enums;
using Npgsql;
using System.Collections;
using System.Data;
using System.Data.Common;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace Customize_ORM.Extensions
{
    /// <summary>
    /// 将表达式树转换为 SQL 查询的帮助类，支持参数化输出。
    /// </summary>
    public class SqlExpression<T>
    {
        // 数据库方言配置（可被子类重写）
        protected virtual string ParameterPrefix => "@";
        protected virtual string BuildPaginationClause(int pageNumber, int pageSize)
        {
            int offset = (pageNumber - 1) * pageSize;
            return $" LIMIT {offset}, {pageSize}";   // ClickHouse 风格
        }

        protected readonly EntityMapping<T> _mapping;
        private readonly List<(string Sql, string? Operator)> _whereClauses = [];
        private readonly StringBuilder _orderByBuilder = new();
        private  List<DbParameter> _parameters = [];
        private readonly Dictionary<string, object?> _paramValues = [];//用于存储字段的字面值
        private string? _selectClause;
        private string? _pagingClause;
        private int _paramIndex;
        private readonly DBType _dBType;
        // 在 SqlExpression<T> 类字段区域添加
        private string? _tableAlias;

        /// <summary>
        /// 设置表别名
        /// 在构建where语句的时候使用 
        /// </summary>
        /// <param name="alias"></param>
        /// <returns>返回语句格式为（alias.cloumnName='xxx'）</returns>
        /// 使用实例
        /// var sqlExpr = new SqlExpression<T>(_mapping).WithTableAlias(tableName);
        ///sqlExpr.Where(expression);
        ///expressionStr= sqlExpr.BuildWhereStr();
        public SqlExpression<T> WithTableAlias(string alias)
        {
            _tableAlias = alias;
            return this;
        }
        public SqlExpression(EntityMapping<T> mapping, DBType dBType = DBType.PostgreSQL)
        {
            _dBType = dBType;
            _mapping = mapping ?? throw new ArgumentNullException(nameof(mapping));
        }

        // ---------- 公开构建方法 ----------
        public SqlExpression<T> Select()
        {
            _selectClause = $"SELECT {string.Join(", ", _mapping.AllColumns)} FROM {_mapping.TableName}";
            return this;
        }
        public SqlExpression<T> Firest()
        {
            _pagingClause = " LIMIT 1";
            return this;
        }

        public SqlExpression<T> Select(Expression<Func<T, object>> selector)
        {
            if (selector == null) throw new ArgumentNullException(nameof(selector));
            var props = SqlExpression<T>.ExtractPropertiesFromSelector(selector);
            if (props.Length == 0)
                throw new ArgumentException($"投影表达式未返回任何属性: {typeof(T).Name}");
            var columns = props.Select(p => _mapping.GetColumnName(p));
            _selectClause = $"SELECT {string.Join(", ", columns)} FROM {_mapping.TableName}";
            return this;
        }

        public SqlExpression<T> Where(Expression<Func<T, bool>>? expression)
        {
            if (expression != null)
                AppendWhere("AND", expression);
            return this;
        }

        public SqlExpression<T> AndWhere(Expression<Func<T, bool>>? expression)
        {
            if (_whereClauses.Count == 0)
                throw new InvalidOperationException("请先调用 Where 方法开始条件链。");
            if (expression != null)
                AppendWhere("AND", expression);
            return this;
        }

        public SqlExpression<T> OrWhere(Expression<Func<T, bool>>? expression)
        {
            if (_whereClauses.Count == 0)
                throw new InvalidOperationException("请先调用 Where 方法开始条件链。");
            if (expression != null)
                AppendWhere("OR", expression);
            return this;
        }

        public SqlExpression<T> OrderBy(Expression<Func<T, object?>> selector)
        {
            AppendOrderBy(selector, "ASC");
            return this;
        }

        public SqlExpression<T> OrderByDescending(Expression<Func<T, object?>> selector)
        {
            AppendOrderBy(selector, "DESC");
            return this;
        }

        public SqlExpression<T> Page(int pageNumber, int pageSize)
        {
            _pagingClause = BuildPaginationClause(pageNumber, pageSize);
            return this;
        }
        /// <summary>
        /// 获取带占位符的 WHERE 子句（不含 "WHERE" 关键字）
        /// </summary>
        /// <returns></returns>
        private string GetWhereClauseWithPlaceholders()
        {
            if (_whereClauses.Count == 0) return string.Empty;
            var sb = new StringBuilder();
            for (int i = 0; i < _whereClauses.Count; i++)
            {
                if (i > 0 && _whereClauses[i].Operator != null)
                    sb.Append(' ').Append(_whereClauses[i].Operator).Append(' ');
                sb.Append('(').Append(_whereClauses[i].Sql).Append(')');
            }
            return sb.ToString();
        }
        /// <summary>
        /// 参数占位符替换核心方法
        /// </summary>
        /// <param name="sql"></param>
        /// <returns></returns>
        private string ReplaceParametersWithLiterals(string sql)
        {
            return Regex.Replace(sql, $@"{Regex.Escape(ParameterPrefix)}[a-zA-Z0-9_]+", match =>
            {
                string paramName = match.Value;
                if (_paramValues.TryGetValue(paramName, out object? value))
                    return SqlExpression<T>.ConvertToSqlLiteral(value);
                return match.Value; // 未找到参数名时保留原样（一般不会发生）
            });
        }
        /// <summary>
        /// 构建参数化where条件语句
        /// </summary>
        /// <returns></returns>
        public (string WhereClause, List<DbParameter> Parameters) BuildWhereClause()
        {
            return (GetWhereClauseWithPlaceholders(), _parameters);
        }
        /// <summary>
        /// 构建非参数化where条件语句
        /// </summary>
        /// <returns></returns>
        public string BuildWhereStr()
        {
            string whereWithPlaceholders = GetWhereClauseWithPlaceholders();
            if (string.IsNullOrEmpty(whereWithPlaceholders))
                return string.Empty;

            // 替换所有参数占位符为字面值
            return ReplaceParametersWithLiterals(whereWithPlaceholders);
        }

        /// <summary>
        /// 仅生成 ORDER BY 子句（不含 "ORDER BY" 关键字）
        /// </summary>
        /// <returns></returns>
        public string BuildOrderByClause()
        {
            return _orderByBuilder.Length > 0 ? _orderByBuilder.ToString().Trim() : string.Empty;
        }

        /// <summary>
        /// 获取所有参数
        /// </summary>
        /// <returns></returns>
        public List<DbParameter> GetParameters() => _parameters;

        /// <summary>
        /// 生成最终 SQL
        /// </summary>
        /// <param name="parameters"></param>
        /// <returns></returns>
        public string ToSql(out List<DbParameter> parameters)
        {
            parameters = _parameters;
            return ToSql();
        }

        public override string ToString() => ToSql();

        public string ToSql()
        {
            var sql = new StringBuilder();

            if (string.IsNullOrEmpty(_selectClause))
                _selectClause = $"SELECT {string.Join(',', _mapping.AllColumns)} FROM {_mapping.TableName}";
            sql.Append(_selectClause);

            if (_whereClauses.Count > 0)
            {
                sql.Append(" WHERE ");
                for (int i = 0; i < _whereClauses.Count; i++)
                {
                    if (i > 0 && _whereClauses[i].Operator != null)
                        sql.Append(' ').Append(_whereClauses[i].Operator).Append(' ');
                    sql.Append('(').Append(_whereClauses[i].Sql).Append(')');
                }
            }

            if (_orderByBuilder.Length > 0)
                sql.Append(_orderByBuilder);

            if (!string.IsNullOrEmpty(_pagingClause))
            {
                if (_orderByBuilder.Length == 0)
                {
                    var keyColumn = _mapping.Key;
                    sql.Append($" ORDER BY {keyColumn} ASC");
                }
                sql.Append(_pagingClause);
            }

            return sql.ToString();
        }

        // ---------- 内部辅助 ----------
        private static PropertyInfo[] ExtractPropertiesFromSelector(Expression<Func<T, object>> selector)
        {
            if (selector.Body is NewExpression newExpr)
            {
                return newExpr.Arguments
                    .OfType<MemberExpression>()
                    .Select(m => m.Member as PropertyInfo)
                    .Where(p => p != null)
                    .ToArray()!;
            }
            if (selector.Body is MemberExpression memberExpr && memberExpr.Member is PropertyInfo prop)
                return new[] { prop };
            // 保底：编译执行（性能较低）
            var obj = selector.Compile().Invoke(Activator.CreateInstance<T>());
            return obj.GetType().GetProperties();
        }

        private void AppendWhere(string conditionOperator, Expression<Func<T, bool>> expression)
        {
            var sqlExpr = VisitExpression(expression)?.ToString() ?? "";
            _whereClauses.Add((sqlExpr, _whereClauses.Count == 0 ? null : conditionOperator));
        }

        private void AppendOrderBy(Expression<Func<T, object?>> selector, string direction)
        {
            if (selector == null) throw new ArgumentNullException(nameof(selector));
            var column = VisitExpression(selector.Body) as string;
            if (string.IsNullOrEmpty(column))
                throw new ArgumentException("无法从表达式中解析列名。");
            if (_orderByBuilder.Length == 0)
                _orderByBuilder.Append($" ORDER BY {column} {direction}");
            else
                _orderByBuilder.Append($", {column} {direction}");
        }

        /// <summary>
        /// 表达式访问核心
        /// </summary>
        /// <param name="expression"></param>
        /// <returns></returns>
        /// <exception cref="NotSupportedException"></exception>
        protected virtual object? VisitExpression(Expression expression)
        {
            return expression.NodeType switch
            {
                ExpressionType.Lambda => VisitLambda((LambdaExpression)expression),
                ExpressionType.LessThan or ExpressionType.LessThanOrEqual or
                ExpressionType.GreaterThan or ExpressionType.GreaterThanOrEqual or
                ExpressionType.Equal or ExpressionType.NotEqual or
                ExpressionType.AndAlso or ExpressionType.OrElse => VisitBinary((BinaryExpression)expression),
                ExpressionType.Convert or ExpressionType.Not => VisitUnary((UnaryExpression)expression),
                ExpressionType.MemberAccess => VisitMemberAccess((MemberExpression)expression),
                ExpressionType.Constant => VisitConstant((ConstantExpression)expression),
                ExpressionType.Call => VisitMethodCall((MethodCallExpression)expression),
                _ => throw new NotSupportedException($"不支持的表达式类型: {expression.NodeType}"),
            };
        }

        protected virtual object? VisitLambda(LambdaExpression lambda) => VisitExpression(lambda.Body);
        private string GetSqlFragment(Expression expr, out bool isParameter)
        {
            var value = VisitExpression(expr);
            // 如果表达式本身就是列引用（例如 p => p.Age）
            if (IsColumnReference(expr))
            {
                // VisitExpression 对于列引用返回的是列名字符串（可能带别名）
                isParameter = false;
                return value?.ToString() ?? throw new InvalidOperationException("列名不能为空");
            }

            // 否则，value 是一个普通 CLR 值 → 参数化
            AddParameter(value, out var paramName);
            isParameter = true;
            return paramName;
        }
        private bool IsColumnReference(Expression expr)
        {
            // 去掉 Convert 节点（如 Nullable 转换）
            while (expr.NodeType == ExpressionType.Convert)
                expr = ((UnaryExpression)expr).Operand;

            // 最简单的列引用：MemberAccess 且其 Expression 是 ParameterExpression
            if (expr is MemberExpression member &&
                member.Expression != null &&
                member.Expression.NodeType == ExpressionType.Parameter)
            {
                return true;
            }
            return false;
        }
        protected virtual object VisitBinary(BinaryExpression binary)
        {
            // 处理 AndAlso / OrElse
            if (binary.NodeType == ExpressionType.AndAlso || binary.NodeType == ExpressionType.OrElse)
            {
                var left = VisitExpression(binary.Left);
                var right = VisitExpression(binary.Right);
                var op = binary.NodeType == ExpressionType.AndAlso ? "AND" : "OR";
                return $"({left} {op} {right})";
            }

            var leftSql = GetSqlFragment(binary.Left, out var leftIsParam);
            var rightSql = GetSqlFragment(binary.Right, out var rightIsParam);
            var operand = GetSqlOperator(binary.NodeType);

            // 处理 IS NULL / IS NOT NULL
            if (rightSql == "NULL" || (!rightIsParam && rightSql == "NULL")) // 兼容原逻辑
            {
                // 其实可以根据 binary.Right 是否是 ConstantExpression(null) 更优雅，此处简化为原语义
                if (!rightIsParam && (rightSql?.Trim().ToUpper() == "NULL"))
                    return binary.NodeType == ExpressionType.Equal
                        ? $"{leftSql} IS NULL"
                        : $"{leftSql} IS NOT NULL";
            }

            // 常规二元运算
            return $"{leftSql} {operand} {rightSql}";
        }

        protected virtual object VisitUnary(UnaryExpression unary)
        {
            if (unary.NodeType == ExpressionType.Not)
                return $"NOT ({VisitExpression(unary.Operand)})";
            if (unary.NodeType == ExpressionType.Convert)
                return VisitExpression(unary.Operand) ?? "";
            throw new NotSupportedException($"不支持的一元表达式: {unary.NodeType}");
        }

        protected virtual object VisitMemberAccess(MemberExpression member)
        {
            if (member.Expression != null && member.Expression.NodeType == ExpressionType.Parameter)
            {
                var propInfo = member.Member as PropertyInfo ?? throw new ArgumentException($"成员 {member.Member.Name} 不是属性");
                var columnName = _mapping.GetColumnName(propInfo);
                // 关键：如果设置了表别名，加上前缀
                if (!string.IsNullOrEmpty(_tableAlias))
                    return $"{_tableAlias}.{columnName}";
                return columnName;
            }
            // 其他情况（常量访问等）保持不变
            var lambda = Expression.Lambda<Func<object>>(Expression.Convert(member, typeof(object)));
            return lambda.Compile()();
        }

        protected virtual object? VisitConstant(ConstantExpression constant) => constant.Value;

        protected virtual object VisitMethodCall(MethodCallExpression call)
        {
            var method = call.Method.Name.ToLowerInvariant();
            return method switch
            {
                "contains" when call.Object?.Type == typeof(string) => VisitStringContains(call, TextSearch.Contains),
                "startswith" => VisitStringContains(call, TextSearch.StartsWith),
                "endswith" => VisitStringContains(call, TextSearch.EndsWith),
                "contains" => VisitInClause(call),
                _ => throw new NotSupportedException($"不支持的方法: {method}"),
            };
        }

        private object VisitStringContains(MethodCallExpression call, TextSearch search)
        {
            var column = VisitExpression(call.Object!);
            var value = VisitExpression(call.Arguments[0]);
            string pattern = search switch
            {
                TextSearch.Contains => $"%{value}%",
                TextSearch.StartsWith => $"{value}%",
                TextSearch.EndsWith => $"%{value}",
                _ => value?.ToString() ?? ""
            };
            AddParameter(pattern, out var paramName);
            return $"{column} LIKE {paramName}";
        }

        private object VisitInClause(MethodCallExpression call)
        {
            Expression collectionExpr, propertyExpr;
            if (call.Object != null && call.Arguments.Count == 1)
            {
                collectionExpr = call.Object;
                propertyExpr = call.Arguments[0];
            }
            else if (call.Arguments.Count == 2)
            {
                collectionExpr = call.Arguments[0];
                propertyExpr = call.Arguments[1];
            }
            else
                throw new NotSupportedException("不支持的 IN 表达式格式");

            var collectionValue = VisitExpression(collectionExpr) as IEnumerable;
            if (collectionValue == null)
                throw new ArgumentException("无法解析 IN 子句的集合");

            var column = VisitExpression(propertyExpr);
            var inParams = new List<string>();
            foreach (var item in collectionValue)
            {
                AddParameter(item, out var param);
                inParams.Add(param);
            }
            return inParams.Count == 0 ? "1 = 0" : $"{column} IN ({string.Join(", ", inParams)})";
        }

        private void AddParameter(object? value, out string paramName)
        {
            _paramIndex++;
            paramName = $"{ParameterPrefix}p{_paramIndex}";

            if (_dBType == DBType.ClickHouse && value is DateTime dt)
            {
                value = dt.ToString("yyyy-MM-dd HH:mm:ss");
            }
            // 创建具体的 DbParameter 实例
            DbParameter parameter = CreateSpecificParameter(paramName, value);
            _parameters.Add(parameter);
            _paramValues[paramName] = value;
        }
        private DbParameter CreateSpecificParameter(string name, object? value)
        {
            // 根据数据库类型返回具体的 DbParameter
            // 你需要引用相应数据库的 NuGet 包
            return _dBType switch
            {
                DBType.ClickHouse =>new ClickHouseDbParameter
                {
                    ParameterName = name,
                    Value = value
                },
                DBType.PostgreSQL => new NpgsqlParameter(name, value),
                // ... 其他数据库
                _ => throw new NotSupportedException($"Database type {_dBType} not supported")
            };
        }
        private string GetSqlOperator(ExpressionType type) => type switch
        {
            ExpressionType.Equal => "=",
            ExpressionType.NotEqual => "<>",
            ExpressionType.GreaterThan => ">",
            ExpressionType.GreaterThanOrEqual => ">=",
            ExpressionType.LessThan => "<",
            ExpressionType.LessThanOrEqual => "<=",
            _ => throw new NotSupportedException($"不支持的操作符: {type}"),
        };
        private static string ConvertToSqlLiteral(object? value)
        {
            if (value == null || value == DBNull.Value)
                return "NULL";

            Type type = value.GetType();
            type = Nullable.GetUnderlyingType(type) ?? type;

            if (type == typeof(bool))
                return (bool)value ? "true" : "false";

            if (type == typeof(string))
            {
                string escaped = ((string)value).Replace("'", "''");
                return $"'{escaped}'";
            }

            if (type == typeof(char))
                return $"'{value}'";

            if (type == typeof(DateTime))
            {
                DateTime dt = (DateTime)value;
                string formatted = dt.ToString("yyyy-MM-dd HH:mm:ss.fff");
                return $"'{formatted}'";
            }

            if (type == typeof(DateTimeOffset))
            {
                DateTimeOffset dto = (DateTimeOffset)value;
                string formatted = dto.ToString("yyyy-MM-dd HH:mm:ss.fff");
                return $"'{formatted}'";
            }

            if (type == typeof(Guid))
                return $"'{value}'";

            if (type.IsEnum)
            {
                // 枚举通常按底层数值存储
                Type underlyingType = Enum.GetUnderlyingType(type);
                object? numericValue = Convert.ChangeType(value, underlyingType);
                return numericValue.ToString() ?? string.Empty;
            }

            // 数值类型（int, long, decimal, double 等）直接 ToString()
            if (type.IsValueType && !type.IsEnum)
                return value.ToString()!;

            // 其他复杂类型（如 byte[]）按需处理，这里简单转为字符串加引号
            return $"'{value.ToString()?.Replace("'", "''")}'";
        }
        private enum TextSearch { Contains, StartsWith, EndsWith }
    }
}
