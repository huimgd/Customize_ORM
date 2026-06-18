using Customize_ORM.Enums;
using Customize_ORM.Extensions.Sql;
using System.Data.Common;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace Customize_ORM.Extensions
{
    /// <summary>
    /// Builds SQL fragments from expression trees while preserving the existing fluent API.
    /// </summary>
    public class SqlExpression<T>
    {
        protected readonly EntityMapping<T> _mapping;

        private readonly SqlDialect _dialect;
        private readonly SqlParameterContext _parameterContext;
        private readonly List<(string Sql, string? Operator)> _whereClauses = [];
        private readonly StringBuilder _orderByBuilder = new();
        private string? _pagingClause;
        private string? _selectClause;
        private string? _tableAlias;

        protected virtual string ParameterPrefix => _parameterContext.ParameterPrefix;

        public SqlExpression(EntityMapping<T> mapping, DBType dBType = DBType.PostgreSQL)
        {
            _mapping = mapping ?? throw new ArgumentNullException(nameof(mapping));
            _dialect = SqlDialect.For(dBType);
            _parameterContext = new SqlParameterContext(_dialect);
        }

        public SqlExpression<T> WithTableAlias(string alias)
        {
            _tableAlias = alias;
            return this;
        }

        public SqlExpression<T> Select()
        {
            _selectClause = $"SELECT {string.Join(", ", _mapping.AllColumns)} FROM {_mapping.TableName}";
            return this;
        }

        public SqlExpression<T> First()
        {
            _pagingClause = _dialect.BuildLimitClause(1);
            return this;
        }

        public SqlExpression<T> Firest() => First();

        public SqlExpression<T> Select(Expression<Func<T, object>> selector)
        {
            ArgumentNullException.ThrowIfNull(selector);

            var columns = ExtractColumnsFromSelector(selector);
            if (columns.Length == 0)
            {
                throw new ArgumentException($"Projection expression returned no properties for {typeof(T).Name}.");
            }

            _selectClause = $"SELECT {string.Join(", ", columns)} FROM {_mapping.TableName}";
            return this;
        }

        public SqlExpression<T> Where(Expression<Func<T, bool>>? expression)
        {
            if (expression != null)
            {
                AppendWhere("AND", expression);
            }

            return this;
        }

        public SqlExpression<T> AndWhere(Expression<Func<T, bool>>? expression)
        {
            if (_whereClauses.Count == 0)
            {
                throw new InvalidOperationException("Call Where before chaining AndWhere.");
            }

            if (expression != null)
            {
                AppendWhere("AND", expression);
            }

            return this;
        }

        public SqlExpression<T> OrWhere(Expression<Func<T, bool>>? expression)
        {
            if (_whereClauses.Count == 0)
            {
                throw new InvalidOperationException("Call Where before chaining OrWhere.");
            }

            if (expression != null)
            {
                AppendWhere("OR", expression);
            }

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
            _pagingClause = _dialect.BuildPaginationClause(pageNumber, pageSize);
            return this;
        }

        public (string WhereClause, List<DbParameter> Parameters) BuildWhereClause()
        {
            return (GetWhereClauseWithPlaceholders(), GetParameters());
        }

        public string BuildWhereStr()
        {
            var whereWithPlaceholders = GetWhereClauseWithPlaceholders();
            return string.IsNullOrEmpty(whereWithPlaceholders)
                ? string.Empty
                : ReplaceParametersWithLiterals(whereWithPlaceholders);
        }

        public string BuildOrderByClause()
        {
            return _orderByBuilder.Length > 0 ? _orderByBuilder.ToString().Trim() : string.Empty;
        }

        public List<DbParameter> GetParameters() => [.. _parameterContext.Parameters];

        public string ToSql(out List<DbParameter> parameters)
        {
            parameters = GetParameters();
            return ToSql();
        }

        public override string ToString() => ToSql();

        public string ToSql()
        {
            var sql = new StringBuilder();

            if (string.IsNullOrEmpty(_selectClause))
            {
                _selectClause = $"SELECT {string.Join(", ", _mapping.AllColumns)} FROM {_mapping.TableName}";
            }

            sql.Append(_selectClause);

            if (_whereClauses.Count > 0)
            {
                sql.Append(" WHERE ");
                sql.Append(GetWhereClauseWithPlaceholders());
            }

            if (_orderByBuilder.Length > 0)
            {
                sql.Append(_orderByBuilder);
            }

            if (!string.IsNullOrEmpty(_pagingClause))
            {
                if (_orderByBuilder.Length == 0)
                {
                    sql.Append($" ORDER BY {_mapping.Key} ASC");
                }

                sql.Append(_pagingClause);
            }

            return sql.ToString();
        }

        private string[] ExtractColumnsFromSelector(Expression<Func<T, object>> selector)
        {
            var columns = new List<string>();
            var seenColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            CollectProjectedColumns(selector.Body, columns, seenColumns);
            return [.. columns];
        }

        private void CollectProjectedColumns(Expression expression, List<string> columns, HashSet<string> seenColumns)
        {
            expression = UnwrapConvert(expression);

            switch (expression)
            {
                case MemberExpression memberExpression when IsEntityPropertyAccess(memberExpression, out var property):
                    AddProjectedColumn(property, columns, seenColumns);
                    return;
                case NewExpression newExpression:
                    foreach (var argument in newExpression.Arguments)
                    {
                        CollectProjectedColumns(argument, columns, seenColumns);
                    }
                    return;
                case MemberInitExpression memberInitExpression:
                    foreach (var binding in memberInitExpression.Bindings.OfType<MemberAssignment>())
                    {
                        CollectProjectedColumns(binding.Expression, columns, seenColumns);
                    }
                    return;
                case NewArrayExpression newArrayExpression:
                    foreach (var item in newArrayExpression.Expressions)
                    {
                        CollectProjectedColumns(item, columns, seenColumns);
                    }
                    return;
                default:
                    throw new ArgumentException(
                        $"Projection expression '{expression}' does not resolve to mapped properties on {typeof(T).Name}.",
                        nameof(expression));
            }
        }

        private void AddProjectedColumn(PropertyInfo property, List<string> columns, HashSet<string> seenColumns)
        {
            var columnName = _mapping.GetColumnName(property);
            if (seenColumns.Add(columnName))
            {
                columns.Add(columnName);
            }
        }

        private static bool IsEntityPropertyAccess(MemberExpression memberExpression, out PropertyInfo property)
        {
            if (memberExpression.Expression is ParameterExpression &&
                memberExpression.Member is PropertyInfo entityProperty)
            {
                property = entityProperty;
                return true;
            }

            property = null!;
            return false;
        }

        private static Expression UnwrapConvert(Expression expression)
        {
            while (expression.NodeType is ExpressionType.Convert or ExpressionType.ConvertChecked)
            {
                expression = ((UnaryExpression)expression).Operand;
            }

            return expression;
        }

        private string GetWhereClauseWithPlaceholders()
        {
            if (_whereClauses.Count == 0)
            {
                return string.Empty;
            }

            var builder = new StringBuilder();

            for (var i = 0; i < _whereClauses.Count; i++)
            {
                if (i > 0 && _whereClauses[i].Operator != null)
                {
                    builder.Append(' ').Append(_whereClauses[i].Operator).Append(' ');
                }

                builder.Append('(').Append(_whereClauses[i].Sql).Append(')');
            }

            return builder.ToString();
        }

        private string ReplaceParametersWithLiterals(string sql)
        {
            return Regex.Replace(
                sql,
                $@"{Regex.Escape(ParameterPrefix)}[a-zA-Z0-9_]+",
                match => _parameterContext.LiteralValues.TryGetValue(match.Value, out var value)
                    ? _dialect.RenderLiteral(value)
                    : match.Value);
        }

        private void AppendWhere(string conditionOperator, Expression<Func<T, bool>> expression)
        {
            var sqlFragment = CreateVisitor().TranslatePredicate(expression);
            _whereClauses.Add((sqlFragment, _whereClauses.Count == 0 ? null : conditionOperator));
        }

        private void AppendOrderBy(Expression<Func<T, object?>> selector, string direction)
        {
            ArgumentNullException.ThrowIfNull(selector);

            var column = CreateVisitor().TranslateOrderBy(selector);
            if (_orderByBuilder.Length == 0)
            {
                _orderByBuilder.Append($" ORDER BY {column} {direction}");
            }
            else
            {
                _orderByBuilder.Append($", {column} {direction}");
            }
        }

        private SqlExpressionVisitor<T> CreateVisitor()
        {
            return new SqlExpressionVisitor<T>(_mapping, _dialect, _parameterContext, _tableAlias);
        }
    }
}
