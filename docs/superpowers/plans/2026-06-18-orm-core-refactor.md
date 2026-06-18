# ORM Core Refactor Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 修复 PostgreSQL 仓储的表达式查询回归，拆分 `SqlExpression<T>` 的职责，并建立可扩展到 MySQL 的方言基础结构。

**Architecture:** 保留 `SqlExpression<T>` 作为仓储层的 fluent 入口，但把“方言规则”“参数上下文”“表达式翻译”拆到独立类型中；`PostgresRepository<T>` 与 `ClickhouseRepository<T>` 共享新的表达式构建模型，各自只保留执行与映射责任。

**Tech Stack:** .NET 10, Npgsql, ClickHouse.Driver, Dapper（仅保留仍在使用的原始 SQL 执行能力）

---

### Task 1: Add a lightweight regression harness for expression SQL output

**Files:**
- Create: `C:\Code\测试项目\Customize_ORM\Test\SqlExpressionRegression.cs`
- Modify: `C:\Code\测试项目\Customize_ORM\Test\Program.cs`
- Test: `C:\Code\测试项目\Customize_ORM\Test\Test.csproj`

- [ ] **Step 1: Write the failing regression harness**

```csharp
using Customize_ORM.Enums;
using Customize_ORM.Extensions;
using Test.Entitys;

namespace Test;

internal static class SqlExpressionRegression
{
    public static void Run()
    {
        var mapping = ObjectMapping<AdminEntity>.MappingForInsert;

        AssertWhere(
            new SqlExpression<AdminEntity>(mapping, DBType.PostgreSQL)
                .Where(x => !x.IsDeleted && x.Remark != null),
            "(NOT (is_deleted) AND remark IS NOT NULL)");

        AssertWhere(
            new SqlExpression<AdminEntity>(mapping, DBType.PostgreSQL)
                .Where(x => x.Remark != null && x.Remark.Contains("abc")),
            "(remark IS NOT NULL AND remark LIKE @p1)");

        AssertWhere(
            new SqlExpression<AdminEntity>(mapping, DBType.ClickHouse)
                .Where(x => new[] { 1, 2, 3 }.Contains(x.Status)),
            "status IN (@p1, @p2, @p3)");
    }

    private static void AssertWhere<T>(SqlExpression<T> expression, string expected) where T : class
    {
        var (whereClause, _) = expression.BuildWhereClause();
        if (!string.Equals(whereClause, expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Expected: {expected}{Environment.NewLine}Actual:   {whereClause}");
        }
    }
}
```

- [ ] **Step 2: Run the harness to verify it fails against the current implementation**

Run: `dotnet run --project C:\Code\测试项目\Customize_ORM\Test\Test.csproj`

Expected: FAIL because the current `SqlExpression<T>` still emits unstable SQL for null checks / `NOT` / `Contains`, or because later refactor targets are not implemented yet.

- [ ] **Step 3: Wire the harness into the console test entrypoint**

```csharp
using Customize_ORM.Extensions;
using Customize_ORM.Impl;
using Customize_ORM.Interfaces;
using Customize_ORM.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Test.Entitys;

namespace Test
{
    internal class Program
    {
        static async Task Main(string[] args)
        {
            SqlExpressionRegression.Run();

            var configuration = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                .Build();

            var services = new ServiceCollection();
            services.AddLogging(builder => builder.AddConsole());
            services.AddRepositories(configuration);
            services.AddSingleton(typeof(IBatchPostgresRepository<>), typeof(BatchPostgresRepository<>));
            services.AddSingleton(typeof(IPostgresRepository<>), typeof(PostgresRepository<>));
            services.AddSingleton(typeof(IClickhouseRepository<>), typeof(ClickhouseRepository<>));

            var serviceProvider = services.BuildServiceProvider();
            var appSettings = new AppSettings();
            configuration.GetSection("FileSettings").Bind(appSettings.FileSettings);
            AppSettings.Initialize(appSettings, serviceProvider);

            var repository = serviceProvider.GetRequiredService<IPostgresRepository<AdminEntity>>();
            long count = await repository.GetCountAsync(e => e.IsDeleted == false, CancellationToken.None);
            Console.WriteLine($"未删除的管理员数量: {count}");
        }
    }
}
```

- [ ] **Step 4: Re-run the harness and keep it red**

Run: `dotnet run --project C:\Code\测试项目\Customize_ORM\Test\Test.csproj`

Expected: still FAIL, but now through the explicit regression harness instead of ad hoc compile/runtime breakage.


### Task 2: Extract SQL dialect and parameter infrastructure

**Files:**
- Create: `C:\Code\测试项目\Customize_ORM\Customize_ORM\Extensions\Sql\SqlDialect.cs`
- Create: `C:\Code\测试项目\Customize_ORM\Customize_ORM\Extensions\Sql\PostgreSqlDialect.cs`
- Create: `C:\Code\测试项目\Customize_ORM\Customize_ORM\Extensions\Sql\ClickHouseDialect.cs`
- Create: `C:\Code\测试项目\Customize_ORM\Customize_ORM\Extensions\Sql\MySqlDialect.cs`
- Create: `C:\Code\测试项目\Customize_ORM\Customize_ORM\Extensions\Sql\SqlParameterContext.cs`
- Modify: `C:\Code\测试项目\Customize_ORM\Customize_ORM\Enums\DBType.cs`
- Test: `C:\Code\测试项目\Customize_ORM\Test\SqlExpressionRegression.cs`

- [ ] **Step 1: Write the failing type additions in tests by asking for MySQL support and explicit parameter prefixes**

```csharp
AssertPrefix(DBType.PostgreSQL, "@");
AssertPrefix(DBType.ClickHouse, "@");
AssertPrefix(DBType.MySQL, "@");

static void AssertPrefix(DBType dbType, string expected)
{
    var dialect = SqlDialect.For(dbType);
    if (!string.Equals(dialect.ParameterPrefix, expected, StringComparison.Ordinal))
    {
        throw new InvalidOperationException($"Dialect {dbType} prefix mismatch.");
    }
}
```

- [ ] **Step 2: Run the harness to verify it fails because the dialect abstraction does not exist yet**

Run: `dotnet run --project C:\Code\测试项目\Customize_ORM\Test\Test.csproj`

Expected: FAIL with missing type/member errors around `SqlDialect` or `DBType.MySQL`.

- [ ] **Step 3: Add the new dialect and parameter context files**

```csharp
// SqlDialect.cs
using Customize_ORM.Enums;
using System.Data.Common;

namespace Customize_ORM.Extensions.Sql;

public abstract class SqlDialect
{
    public abstract DBType DbType { get; }
    public abstract string ParameterPrefix { get; }
    public abstract DbParameter CreateParameter(string name, object? value);

    public virtual string BuildPaginationClause(int pageNumber, int pageSize)
    {
        int offset = Math.Max(0, pageNumber - 1) * pageSize;
        return $" LIMIT {offset}, {pageSize}";
    }

    public virtual string BuildLikePattern(string value, SqlLikeKind kind) => kind switch
    {
        SqlLikeKind.Contains => $"%{value}%",
        SqlLikeKind.StartsWith => $"{value}%",
        SqlLikeKind.EndsWith => $"%{value}",
        _ => value
    };

    public static SqlDialect For(DBType dbType) => dbType switch
    {
        DBType.PostgreSQL => PostgreSqlDialect.Instance,
        DBType.ClickHouse => ClickHouseDialect.Instance,
        DBType.MySQL => MySqlDialect.Instance,
        _ => throw new NotSupportedException($"Unsupported db type: {dbType}")
    };
}

public enum SqlLikeKind
{
    Contains,
    StartsWith,
    EndsWith
}
```

```csharp
// PostgreSqlDialect.cs
using Customize_ORM.Enums;
using Npgsql;
using System.Data.Common;

namespace Customize_ORM.Extensions.Sql;

public sealed class PostgreSqlDialect : SqlDialect
{
    public static PostgreSqlDialect Instance { get; } = new();
    public override DBType DbType => DBType.PostgreSQL;
    public override string ParameterPrefix => "@";
    public override DbParameter CreateParameter(string name, object? value) => new NpgsqlParameter(name, value ?? DBNull.Value);
}
```

```csharp
// ClickHouseDialect.cs
using ClickHouse.Driver.ADO.Parameters;
using Customize_ORM.Enums;
using System.Data.Common;

namespace Customize_ORM.Extensions.Sql;

public sealed class ClickHouseDialect : SqlDialect
{
    public static ClickHouseDialect Instance { get; } = new();
    public override DBType DbType => DBType.ClickHouse;
    public override string ParameterPrefix => "@";

    public override DbParameter CreateParameter(string name, object? value) => new ClickHouseDbParameter
    {
        ParameterName = name,
        Value = value is DateTime dt ? dt.ToString("yyyy-MM-dd HH:mm:ss") : value ?? DBNull.Value
    };
}
```

```csharp
// MySqlDialect.cs
using Customize_ORM.Enums;
using System.Data.Common;

namespace Customize_ORM.Extensions.Sql;

public sealed class MySqlDialect : SqlDialect
{
    public static MySqlDialect Instance { get; } = new();
    public override DBType DbType => DBType.MySQL;
    public override string ParameterPrefix => "@";

    public override DbParameter CreateParameter(string name, object? value) =>
        new FakeDbParameter(name, value ?? DBNull.Value);

    private sealed class FakeDbParameter(string name, object value) : DbParameter
    {
        public override DbType DbType { get; set; }
        public override ParameterDirection Direction { get; set; } = ParameterDirection.Input;
        public override bool IsNullable { get; set; }
        public override string ParameterName { get; set; } = name;
        public override string SourceColumn { get; set; } = string.Empty;
        public override object Value { get; set; } = value;
        public override bool SourceColumnNullMapping { get; set; }
        public override int Size { get; set; }
        public override void ResetDbType() { }
    }
}
```

```csharp
// SqlParameterContext.cs
using System.Data.Common;

namespace Customize_ORM.Extensions.Sql;

public sealed class SqlParameterContext(SqlDialect dialect)
{
    private readonly List<DbParameter> _parameters = [];
    private int _index;

    public IReadOnlyList<DbParameter> Parameters => _parameters;

    public string Add(object? value)
    {
        _index++;
        var name = $"{dialect.ParameterPrefix}p{_index}";
        _parameters.Add(dialect.CreateParameter(name, value));
        return name;
    }
}
```

```csharp
// DBType.cs
namespace Customize_ORM.Enums
{
    public enum DBType
    {
        ClickHouse = 1,
        PostgreSQL = 2,
        MySQL = 3,
    }
}
```

- [ ] **Step 4: Run the harness to verify the new infrastructure is green**

Run: `dotnet run --project C:\Code\测试项目\Customize_ORM\Test\Test.csproj`

Expected: prefix assertions pass, while expression translation assertions still fail until Task 3 is complete.


### Task 3: Refactor `SqlExpression<T>` to use the extracted infrastructure

**Files:**
- Create: `C:\Code\测试项目\Customize_ORM\Customize_ORM\Extensions\Sql\SqlExpressionVisitor.cs`
- Modify: `C:\Code\测试项目\Customize_ORM\Customize_ORM\Extensions\SqlExpression.cs`
- Test: `C:\Code\测试项目\Customize_ORM\Test\SqlExpressionRegression.cs`

- [ ] **Step 1: Add failing expectations for full select SQL assembly**

```csharp
var mapping = ObjectMapping<AdminEntity>.MappingForInsert;
var sql = new SqlExpression<AdminEntity>(mapping, DBType.PostgreSQL)
    .Select()
    .Where(x => x.Status > 0 && !x.IsDeleted)
    .OrderBy(x => x.Id)
    .Page(1, 20)
    .ToSql();

const string expectedSql =
    "SELECT id, parent_id, status, remark, is_deleted, extra_properties FROM admin " +
    "WHERE ((status > @p1 AND NOT (is_deleted))) ORDER BY id ASC LIMIT 0, 20";

if (!string.Equals(sql, expectedSql, StringComparison.Ordinal))
{
    throw new InvalidOperationException($"Unexpected SQL:{Environment.NewLine}{sql}");
}
```

- [ ] **Step 2: Run the harness to verify it fails with the old builder behavior**

Run: `dotnet run --project C:\Code\测试项目\Customize_ORM\Test\Test.csproj`

Expected: FAIL because the current `SqlExpression<T>` still owns translation and parameter creation directly.

- [ ] **Step 3: Introduce `SqlExpressionVisitor<T>` and slim down `SqlExpression<T>`**

```csharp
// SqlExpressionVisitor.cs
using System.Collections;
using System.Linq.Expressions;
using System.Reflection;

namespace Customize_ORM.Extensions.Sql;

public sealed class SqlExpressionVisitor<T>(EntityMapping<T> mapping, SqlDialect dialect, SqlParameterContext parameterContext, string? tableAlias = null)
{
    public string Visit(Expression expression) => expression.NodeType switch
    {
        ExpressionType.Lambda => Visit(((LambdaExpression)expression).Body),
        ExpressionType.AndAlso or ExpressionType.OrElse => VisitLogical((BinaryExpression)expression),
        ExpressionType.Equal or ExpressionType.NotEqual or ExpressionType.GreaterThan or ExpressionType.GreaterThanOrEqual or ExpressionType.LessThan or ExpressionType.LessThanOrEqual => VisitBinary((BinaryExpression)expression),
        ExpressionType.Not => $"NOT ({Visit(((UnaryExpression)expression).Operand)})",
        ExpressionType.Convert => Visit(((UnaryExpression)expression).Operand),
        ExpressionType.MemberAccess => VisitMember((MemberExpression)expression),
        ExpressionType.Constant => VisitConstant((ConstantExpression)expression),
        ExpressionType.Call => VisitCall((MethodCallExpression)expression),
        _ => throw new NotSupportedException($"Unsupported node: {expression.NodeType}")
    };

    private string VisitLogical(BinaryExpression binary)
    {
        var op = binary.NodeType == ExpressionType.AndAlso ? "AND" : "OR";
        return $"({Visit(binary.Left)} {op} {Visit(binary.Right)})";
    }

    private string VisitBinary(BinaryExpression binary)
    {
        var left = ResolveOperand(binary.Left, out bool leftIsColumn);
        var right = ResolveOperand(binary.Right, out bool rightIsColumn);

        if (!rightIsColumn && right is null)
        {
            return binary.NodeType == ExpressionType.Equal
                ? $"{left} IS NULL"
                : $"{left} IS NOT NULL";
        }

        return $"{left} {ToOperator(binary.NodeType)} {right}";
    }

    private object? ResolveOperand(Expression expression, out bool isColumn)
    {
        if (TryResolveColumn(expression, out var column))
        {
            isColumn = true;
            return column;
        }

        isColumn = false;
        return expression switch
        {
            ConstantExpression constant => constant.Value is null ? null : parameterContext.Add(constant.Value),
            MemberExpression member when member.Expression?.NodeType != ExpressionType.Parameter => Parameterize(Evaluate(member)),
            _ => Parameterize(Evaluate(expression))
        };
    }

    private object? Parameterize(object? value) => value is null ? null : parameterContext.Add(value);

    private string VisitMember(MemberExpression member)
    {
        if (!TryResolveColumn(member, out var column))
        {
            return parameterContext.Add(Evaluate(member));
        }

        return column;
    }

    private string VisitConstant(ConstantExpression constant) => constant.Value is null ? "NULL" : parameterContext.Add(constant.Value);

    private string VisitCall(MethodCallExpression call)
    {
        var method = call.Method.Name;
        if (method == nameof(string.Contains) && call.Object?.Type == typeof(string))
        {
            return BuildLike(call.Object, call.Arguments[0], SqlLikeKind.Contains);
        }
        if (method == nameof(string.StartsWith))
        {
            return BuildLike(call.Object!, call.Arguments[0], SqlLikeKind.StartsWith);
        }
        if (method == nameof(string.EndsWith))
        {
            return BuildLike(call.Object!, call.Arguments[0], SqlLikeKind.EndsWith);
        }
        if (method == nameof(Enumerable.Contains))
        {
            return BuildIn(call.Arguments[0], call.Arguments[1]);
        }
        if (method == nameof(List<int>.Contains))
        {
            return BuildIn(call.Object!, call.Arguments[0]);
        }

        throw new NotSupportedException($"Unsupported method: {method}");
    }

    private string BuildLike(Expression target, Expression valueExpression, SqlLikeKind kind)
    {
        string column = (string)ResolveOperand(target, out _)!;
        var rawValue = Evaluate(valueExpression)?.ToString() ?? string.Empty;
        string parameter = parameterContext.Add(dialect.BuildLikePattern(rawValue, kind));
        return $"{column} LIKE {parameter}";
    }

    private string BuildIn(Expression collectionExpression, Expression valueExpression)
    {
        string column = (string)ResolveOperand(valueExpression, out _)!;
        var values = Evaluate(collectionExpression) as IEnumerable ?? throw new InvalidOperationException("IN source is null.");
        var parameterNames = new List<string>();
        foreach (var item in values)
        {
            parameterNames.Add(parameterContext.Add(item));
        }
        return parameterNames.Count == 0 ? "1 = 0" : $"{column} IN ({string.Join(", ", parameterNames)})";
    }

    private bool TryResolveColumn(Expression expression, out string column)
    {
        while (expression is UnaryExpression unary && unary.NodeType == ExpressionType.Convert)
        {
            expression = unary.Operand;
        }

        if (expression is MemberExpression member && member.Expression?.NodeType == ExpressionType.Parameter)
        {
            var property = (PropertyInfo)member.Member;
            var mappedColumn = mapping.GetColumnName(property);
            column = string.IsNullOrWhiteSpace(tableAlias) ? mappedColumn : $"{tableAlias}.{mappedColumn}";
            return true;
        }

        column = string.Empty;
        return false;
    }

    private static object? Evaluate(Expression expression)
    {
        var lambda = Expression.Lambda<Func<object?>>(Expression.Convert(expression, typeof(object)));
        return lambda.Compile().Invoke();
    }

    private static string ToOperator(ExpressionType type) => type switch
    {
        ExpressionType.Equal => "=",
        ExpressionType.NotEqual => "<>",
        ExpressionType.GreaterThan => ">",
        ExpressionType.GreaterThanOrEqual => ">=",
        ExpressionType.LessThan => "<",
        ExpressionType.LessThanOrEqual => "<=",
        _ => throw new NotSupportedException($"Unsupported operator: {type}")
    };
}
```

```csharp
// SqlExpression.cs
using Customize_ORM.Enums;
using Customize_ORM.Extensions.Sql;
using System.Data.Common;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;

namespace Customize_ORM.Extensions;

public class SqlExpression<T>
{
    private readonly EntityMapping<T> _mapping;
    private readonly SqlDialect _dialect;
    private readonly SqlParameterContext _parameterContext;
    private readonly List<(string Sql, string? Operator)> _whereClauses = [];
    private readonly List<string> _orderByClauses = [];
    private string? _selectClause;
    private string? _pagingClause;
    private string? _tableAlias;

    public SqlExpression(EntityMapping<T> mapping, DBType dbType = DBType.PostgreSQL)
    {
        _mapping = mapping ?? throw new ArgumentNullException(nameof(mapping));
        _dialect = SqlDialect.For(dbType);
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
        _pagingClause = " LIMIT 1";
        return this;
    }

    public SqlExpression<T> Select(Expression<Func<T, object>> selector)
    {
        var properties = ExtractProperties(selector);
        var columns = properties.Select(_mapping.GetColumnName);
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

    public SqlExpression<T> OrderBy(Expression<Func<T, object?>> selector)
    {
        _orderByClauses.Add($"{ExtractOrderBy(selector)} ASC");
        return this;
    }

    public SqlExpression<T> OrderByDescending(Expression<Func<T, object?>> selector)
    {
        _orderByClauses.Add($"{ExtractOrderBy(selector)} DESC");
        return this;
    }

    public SqlExpression<T> Page(int pageNumber, int pageSize)
    {
        _pagingClause = _dialect.BuildPaginationClause(pageNumber, pageSize);
        return this;
    }

    public (string WhereClause, List<DbParameter> Parameters) BuildWhereClause()
        => (BuildWhereOnly(), _parameterContext.Parameters.ToList());

    public string BuildWhereStr() => BuildWhereOnly();

    public string ToSql(out List<DbParameter> parameters)
    {
        parameters = _parameterContext.Parameters.ToList();
        return ToSql();
    }

    public string ToSql()
    {
        var sql = new StringBuilder();
        sql.Append(_selectClause ?? $"SELECT {string.Join(", ", _mapping.AllColumns)} FROM {_mapping.TableName}");

        var where = BuildWhereOnly();
        if (!string.IsNullOrWhiteSpace(where))
        {
            sql.Append(" WHERE ").Append(where);
        }

        if (_orderByClauses.Count > 0)
        {
            sql.Append(" ORDER BY ").Append(string.Join(", ", _orderByClauses));
        }

        if (!string.IsNullOrWhiteSpace(_pagingClause))
        {
            if (_orderByClauses.Count == 0 && _pagingClause != " LIMIT 1")
            {
                sql.Append($" ORDER BY {_mapping.Key} ASC");
            }
            sql.Append(_pagingClause);
        }

        return sql.ToString();
    }

    private void AppendWhere(string conditionOperator, Expression<Func<T, bool>> expression)
    {
        var visitor = new SqlExpressionVisitor<T>(_mapping, _dialect, _parameterContext, _tableAlias);
        var sql = visitor.Visit(expression);
        _whereClauses.Add((sql, _whereClauses.Count == 0 ? null : conditionOperator));
    }

    private string BuildWhereOnly()
    {
        if (_whereClauses.Count == 0) return string.Empty;

        var builder = new StringBuilder();
        for (int i = 0; i < _whereClauses.Count; i++)
        {
            if (i > 0 && _whereClauses[i].Operator != null)
            {
                builder.Append(' ').Append(_whereClauses[i].Operator).Append(' ');
            }
            builder.Append('(').Append(_whereClauses[i].Sql).Append(')');
        }

        return builder.ToString();
    }

    private string ExtractOrderBy(Expression<Func<T, object?>> selector)
    {
        Expression body = selector.Body is UnaryExpression unary && unary.NodeType == ExpressionType.Convert
            ? unary.Operand
            : selector.Body;

        if (body is not MemberExpression member || member.Member is not PropertyInfo property)
        {
            throw new ArgumentException("无法从表达式中解析列名。", nameof(selector));
        }

        return _mapping.GetColumnName(property);
    }

    private static PropertyInfo[] ExtractProperties(Expression<Func<T, object>> selector)
    {
        if (selector.Body is NewExpression newExpression)
        {
            return newExpression.Arguments
                .OfType<MemberExpression>()
                .Select(x => (PropertyInfo)x.Member)
                .ToArray();
        }

        if (selector.Body is MemberExpression memberExpression)
        {
            return [(PropertyInfo)memberExpression.Member];
        }

        throw new ArgumentException("无法从投影表达式中解析属性。", nameof(selector));
    }
}
```

- [ ] **Step 4: Run the harness to verify expression translation is now green**

Run: `dotnet run --project C:\Code\测试项目\Customize_ORM\Test\Test.csproj`

Expected: all SQL translation assertions PASS; database connectivity code may still be blocked by repository compile/runtime issues until Task 4 is done.


### Task 4: Repair PostgreSQL query execution and align repository usage

**Files:**
- Modify: `C:\Code\测试项目\Customize_ORM\Customize_ORM\Impl\PostgresRepository.cs`
- Modify: `C:\Code\测试项目\Customize_ORM\Customize_ORM\Impl\ClickhouseRepository.cs`
- Test: `C:\Code\测试项目\Customize_ORM\Test\Program.cs`

- [ ] **Step 1: Add a failing compile target for the PostgreSQL repository regression**

Run: `dotnet build C:\Code\测试项目\Customize_ORM\Customize_ORM.slnx`

Expected: FAIL with the current errors around `_rowReader`, `SelectAsync`, and `GetAllAsync`.

- [ ] **Step 2: Replace the mixed PostgreSQL query path with a single reader-based implementation**

```csharp
public async ValueTask<IEnumerable<T>> QueryAsync(Expression<Func<T, bool>>? expression = null, CancellationToken token = default)
{
    try
    {
        await using var connection = await _postgreSql.GetConnectionAsync(token);
        ArgumentNullException.ThrowIfNull(connection);

        var sqlExpression = new SqlExpression<T>(_mapping, Enums.DBType.PostgreSQL).Select();
        if (expression != null)
        {
            sqlExpression.Where(expression);
        }

        string sql = sqlExpression.ToSql(out List<DbParameter> parameters);
        await using var command = new NpgsqlCommand(sql, connection);
        AttachParameters(command.Parameters, parameters);

        var results = new List<T>();
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, token);
        while (await reader.ReadAsync(token))
        {
            results.Add(MapFromReader(reader));
        }

        return results;
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        _logger.LogError(ex, "{fnName} {EntityName}", nameof(QueryAsync), typeof(T).Name);
        return [];
    }
}
```

- [ ] **Step 3: Align the first-row query with the new builder API and fix the `Firest` typo**

```csharp
public async ValueTask<T?> QueryFirstOrDefaultAsync(Expression<Func<T, bool>>? expression = null, CancellationToken token = default)
{
    try
    {
        await using var connection = await _postgreSql.GetConnectionAsync(token);
        ArgumentNullException.ThrowIfNull(connection);

        var sqlExpression = new SqlExpression<T>(_mapping, Enums.DBType.PostgreSQL).Select().First();
        if (expression != null)
        {
            sqlExpression.Where(expression);
        }

        string sql = sqlExpression.ToSql(out List<DbParameter> parameters);
        await using var command = new NpgsqlCommand(sql, connection);
        AttachParameters(command.Parameters, parameters);

        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, token);
        return await reader.ReadAsync(token) ? MapFromReader(reader) : default;
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        _logger.LogError(ex, "{fnName} {EntityName}", nameof(QueryFirstOrDefaultAsync), typeof(T).Name);
        return default;
    }
}
```

- [ ] **Step 4: Update ClickHouse call sites to use the renamed builder methods and rebuild**

```csharp
var sqlExpression = new SqlExpression<T>(_mapping, Enums.DBType.ClickHouse);
sqlExpression.Select().Where(expression).First();
```

Run: `dotnet build C:\Code\测试项目\Customize_ORM\Customize_ORM.slnx`

Expected: PASS.


### Task 5: Verify end-to-end and clean up readability issues

**Files:**
- Modify: `C:\Code\测试项目\Customize_ORM\Customize_ORM\Impl\PostgresRepository.cs`
- Modify: `C:\Code\测试项目\Customize_ORM\Customize_ORM\Extensions\SqlExpression.cs`
- Modify: `C:\Code\测试项目\Customize_ORM\Test\Program.cs`
- Test: `C:\Code\测试项目\Customize_ORM\Customize_ORM.slnx`

- [ ] **Step 1: Remove leftover readability hazards**

```csharp
// PostgresRepository.cs
protected static readonly EntityMapping<T> _updateMapping = ObjectMapping<T>.MappingForUpdate;

public async ValueTask<long> GetCountAsync(Expression<Func<T, bool>>? expression, CancellationToken token)
{
    try
    {
        await using var connection = await _postgreSql.GetConnectionAsync(token);
        var sqlExpression = new SqlExpression<T>(_mapping, Enums.DBType.PostgreSQL).Select();
        if (expression != null)
        {
            sqlExpression.Where(expression);
        }

        string sql = sqlExpression.ToSql(out List<DbParameter> parameters)
            .Replace($"SELECT {string.Join(", ", _mapping.AllColumns)}", "SELECT COUNT(*)");

        await using var command = new NpgsqlCommand(sql, connection);
        AttachParameters(command.Parameters, parameters);
        var result = await command.ExecuteScalarAsync(token);
        return Convert.ToInt64(result ?? 0);
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        _logger.LogError(ex, "{fnName} {EntityName}", nameof(GetCountAsync), typeof(T).Name);
        return 0;
    }
}
```

- [ ] **Step 2: Run full verification**

Run: `dotnet build C:\Code\测试项目\Customize_ORM\Customize_ORM.slnx`

Expected: PASS

Run: `dotnet run --project C:\Code\测试项目\Customize_ORM\Test\Test.csproj`

Expected: PASS for the SQL expression regression harness; if local PostgreSQL is configured and reachable, the repository smoke query also completes successfully.

- [ ] **Step 3: Record the result**

If Git metadata is available:

```bash
git add C:\Code\测试项目\Customize_ORM\Customize_ORM\Enums\DBType.cs \
        C:\Code\测试项目\Customize_ORM\Customize_ORM\Extensions\SqlExpression.cs \
        C:\Code\测试项目\Customize_ORM\Customize_ORM\Extensions\Sql\*.cs \
        C:\Code\测试项目\Customize_ORM\Customize_ORM\Impl\PostgresRepository.cs \
        C:\Code\测试项目\Customize_ORM\Customize_ORM\Impl\ClickhouseRepository.cs \
        C:\Code\测试项目\Customize_ORM\Test\Program.cs \
        C:\Code\测试项目\Customize_ORM\Test\SqlExpressionRegression.cs
git commit -m "refactor: isolate sql dialects and repair postgres query pipeline"
```

If Git metadata is unavailable in the current workspace, skip the commit and capture verification output in the task notes instead.
