# Customize_ORM

> 一个基于表达式树的轻量级自定义 ORM，支持 **ClickHouse / PostgreSQL / MySQL**，提供仓储化 API、SqlDialect 方言体系、混合缓存与文件工具，并内置不依赖真实数据库的回归测试。

> A lightweight expression-tree-based custom ORM supporting **ClickHouse / PostgreSQL / MySQL**, with repository-style APIs, SQL dialect abstraction, hybrid caching, file utilities, and DB-free regression tests built in.

---

## 特性 / Features

- **表达式树转 SQL** / Expression-to-SQL
  - 通过 `Expression<Func<T, bool>>` 构建 `WHERE`、`ORDER BY`、分页与投影，自动参数化。
  - Fluent API：`Where` / `AndWhere` / `OrWhere` / `OrderBy` / `OrderByDescending` / `Page` / `Select` / `First`。
- **多数据库方言** / Multi-database dialects
  - `SqlDialect.For(DBType)` 统一处理参数前缀、分页语法与差异：`ClickHouse`、`PostgreSQL`、`MySQL`。
- **仓储化接口** / Repository-style interfaces
  - `IPostgresRepository<T>` / `IBatchPostgresRepository<T>`：插入、更新、批量更新/插入、流式查询、计数、原生 SQL。
  - `IClickhouseRepository<T>`：批量插入、查询/分页/投影/流式查询、计数。
- **实体映射** / Entity mapping
  - 特性：`[Table]`、`[Index]`、`[PartitionKey]`、`[Ignore]`、`[DefaultValue]`、`[DateTimeDefaultValue]`。
  - 驼峰 ⇄ 蛇形自动转换、JSONB 类型映射、MessagePack 序列化支持。
- **基础设施** / Infrastructure
  - `Extensions/Caching/`：`HybridCache`、批量处理器、优先级通道、并发集合。
  - `Extensions/Files/`：文件读写与文件名/ID 工具。
  - `AppSettings` 全局配置初始化。
- **回归测试** / Regression tests
  - `Test/Regressions/` 提供不依赖真实数据库的 harness，覆盖 SQL 生成、参数归一化与 reader 映射。

---

## 环境要求 / Requirements

- .NET 10 SDK（`TargetFramework` = `net10.0`）
- ClickHouse / PostgreSQL / MySQL 任一实例（仅运行真实示例时必需）

---

## 快速开始 / Getting Started

### 1. 引用项目 / Reference the project

```bash
git clone git@github.com:huimgd/Customize_ORM.git
```

或以 `ProjectReference` 引用主项目：

```xml
<ProjectReference Include="..\Customize_ORM\Customize_ORM.csproj" />
```

### 2. 配置连接串 / Configure connection strings

`appsettings.json` 示例：

```json
{
  "Database": "Host=127.0.0.1;Port=5432;Username=postgres;Password=your_password;Database=your_db",
  "ClickHouse": "Compress=True;CheckCompressedHash=False;Compressor=lz4;Host=127.0.0.1;Port=9123;Database=default;User=default;Password=your_password;Timeout=10",
  "FileSettings": {
    "TempDirectoryPath": "Temp"
  }
}
```

### 3. 注册服务 / Register services

```csharp
services.AddRepositories(configuration);
// PostgreSQL
services.AddSingleton(typeof(IBatchPostgresRepository<>), typeof(BatchPostgresRepository<>));
services.AddSingleton(typeof(IPostgresRepository<>), typeof(PostgresRepository<>));
// ClickHouse
services.AddSingleton(typeof(IClickhouseRepository<>), typeof(ClickhouseRepository<>));
```

### 4. 定义实体 / Define an entity

```csharp
using Customize_ORM.Attributes;
using System.ComponentModel.DataAnnotations.Schema;

[Table("admin")]
public class AdminEntity
{
    [Index]
    public Guid Id { get; set; }
    public string? Name { get; set; }
    public bool IsDeleted { get; set; }
}
```

### 5. 使用 / Usage

**ClickHouse**

```csharp
var repo = serviceProvider.GetRequiredService<IClickhouseRepository<BlackApiLogEntity>>();

// 批量插入
await repo.BulkInsertAsync(logs, tableName: "black_api_log");

// 流式查询
await foreach (var log in repo.QueryStreamAsync(e => e.IsBlackMobile == true))
{
    Console.WriteLine(log.Id);
}

// 分页 + 投影
var page = await repo.QueryAsync(e => new { e.Id, e.Mobile }, e => e.IsBlackMobile,
    pageIndex: 0, pageSize: 100, orderBy: "ExecutionTime DESC");
```

**PostgreSQL**

```csharp
var repo = serviceProvider.GetRequiredService<IPostgresRepository<AdminEntity>>();

long count = await repo.GetCountAsync(e => e.IsDeleted == false, CancellationToken.None);
var items = await repo.QueryAsync(e => e.IsDeleted == false);
await repo.UpdateAsync(entity, columns: new[] { "Name" }, CancellationToken.None);
```

---

## 项目结构 / Project Structure

```
Customize_ORM/
├── Customize_ORM.slnx
├── Customize_ORM/               # 主类库
│   ├── Attributes/              # 实体映射特性
│   ├── Enums/                   # 跨模块枚举 (DBType 等)
│   ├── Interfaces/              # 仓储接口与连接工厂接口
│   ├── Impl/                    # 仓储与连接工厂实现
│   ├── Models/                  # 分页、批量、重试、配置模型
│   ├── Extensions/
│   │   ├── Caching/             # 混合缓存、通道、批处理
│   │   ├── Expressions/         # SqlExpression 表达式 SQL 入口
│   │   ├── Files/               # 文件工具
│   │   ├── Mapping/             # 实体映射与行工厂
│   │   ├── Sql/                 # SQL 构建器、方言、执行、映射
│   │   └── Utilities/           # 通用扩展方法
│   └── Properties/
├── Test/                        # 测试项目（实体 + 回归 harness）
│   ├── Entities/
│   ├── Regressions/
│   └── Program.cs
└── docs/                        # 项目结构与设计文档
```

详细说明见 [docs/PROJECT_STRUCTURE.md](docs/PROJECT_STRUCTURE.md)。

---

## 回归测试 / Regression Tests

不依赖真实数据库，直接运行：

```bash
dotnet run --project Test -- --regression-only
```

单项回归入口（如 SQL 表达式、JSONB 映射）：

```bash
dotnet run --project Test -- --run-sql-expression-regression
dotnet run --project Test -- --run-jsonb-mapping-regression
```

---

## 技术栈 / Tech Stack

| 组件 | 说明 |
| --- | --- |
| .NET 10 | 目标框架 |
| ClickHouse.Driver 1.2.0 | ClickHouse 驱动 |
| Npgsql 10.x | PostgreSQL 驱动 |
| MessagePack 3.x | 二进制序列化 |
| IdGen 3.x | ID 生成 |
| NLog 6.x | 日志 |
| Microsoft.Extensions.* | 配置 / DI / 宿主 |

---

## 注意事项 / Notes

- 连接字符串（含密码）位于 `Test/appsettings.json`，**请勿在公开仓库中提交真实密码**，建议使用环境变量或用户机密替代。
- `ClickhouseConnectionFactory` 内部使用 `Lazy<ClickHouseClient>` 单例复用客户端，并提供 `GetConnectionAsync()` 获取独立连接。
