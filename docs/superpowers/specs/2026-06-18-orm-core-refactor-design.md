# ORM Core Refactor Design

## Goal

在不回退到 Dapper/Dommel 查询扩展的前提下，完成一轮可落地的 ORM 内核整理，覆盖以下目标：

- 修复 `PostgresRepository<T>.QueryAsync(Expression<Func<T, bool>>?)` 的编译错误和执行路径混乱问题
- 重构 `SqlExpression<T>`，把表达式解析、SQL 片段拼接、参数创建、方言差异处理拆开
- 补齐 PostgreSQL、ClickHouse 的方言边界，并为 MySQL 兼容预留统一扩展点
- 让查询链路更稳定、更易读，并减少不必要的运行时分配，为高性能版本打基础

## Current Problems

### 1. PostgreSQL 查询路径残留旧依赖

`PostgresRepository<T>.QueryAsync(Expression<Func<T, bool>>?)` 当前同时存在三套逻辑：

- 手写 `NpgsqlCommand`
- 已失效的 `_rowReader()` 调用
- 已失效的 `SelectAsync/GetAllAsync` 调用

这说明仓储执行层还没有完全脱离旧的 Dapper/Dommel 风格扩展，导致当前直接编译失败。

### 2. `SqlExpression<T>` 职责过多

当前 `SqlExpression<T>` 同时承担：

- `Expression` 解析
- `WHERE/ORDER/PAGE/SELECT` 拼接
- `DbParameter` 实例创建
- 参数字面值替换
- PostgreSQL/ClickHouse 分支判断

这些职责耦合在一个类中，会让后续继续支持 MySQL 时复杂度快速上升。

### 3. 方言抽象不完整

当前 `DBType` 仅包含 PostgreSQL 和 ClickHouse，且差异处理只散落在少数分支中，例如：

- 参数前缀
- 分页语法
- `DateTime` 参数格式
- 标识符/排序/布尔值/null 的表达方式

这不足以支撑“兼容 pgsql、mysql、clickhouse”的目标。

### 4. 可维护性与性能边界不清晰

当前仓储层存在以下维护成本：

- 构建 SQL 与执行 SQL 的责任混在一起
- 同一类查询逻辑在 `PostgresRepository` 与 `ClickhouseRepository` 中重复
- 查询结果映射策略不统一
- 有的地方使用参数化，有的地方使用字面值替换，安全性和行为不一致

## Design Decision

采用“中等幅度重构”的方案：本轮不做完整的三数据库仓储体系重建，但先把查询构建内核和方言边界整理好，让 PostgreSQL 与 ClickHouse 先走上同一套表达式翻译模型，同时为 MySQL 留出实现槽位。

## Proposed Architecture

### A. 查询表达式层拆分

保留 `SqlExpression<T>` 作为面向仓储层的 fluent builder，但内部改为组合式结构：

- `SqlDialect`
  - 描述数据库方言能力
  - 负责参数前缀、分页语法、`LIKE` 拼接、标识符输出、空集合 `IN` 行为
- `SqlParameterContext`
  - 负责参数名递增、参数缓存、`DbParameter` 创建
  - 对外返回 `List<DbParameter>`
- `SqlExpressionVisitor<T>`
  - 专注把 `Expression<Func<T, bool>>` 翻译成 SQL 片段
  - 不再关心完整 `SELECT` 语句
- `SqlExpression<T>`
  - 负责组织 `SELECT/WHERE/ORDER/PAGE`
  - 提供清晰的 `BuildSelectSql()`、`BuildWhereClause()`、`BuildCommand()` 之类入口

这样调用方式仍然保持接近当前风格，但内部职责更单一。

### B. 仓储执行层统一

`PostgresRepository<T>` 的表达式查询将改为：

1. 用 `SqlExpression<T>` 构建 `SELECT column1, column2 ... FROM table WHERE ...`
2. 由仓储层统一创建命令对象并附加参数
3. 统一通过 `DbDataReader` + `MapFromReader` 映射实体

这样可以彻底删除：

- `_rowReader()` 残留调用
- `SelectAsync`
- `GetAllAsync`

同时，ClickHouse 仓储继续沿用显式列顺序读取，但会共享新的表达式翻译和方言接口。

### C. 方言层标准化

新增或补齐方言抽象，至少覆盖：

- PostgreSQL
- ClickHouse
- MySQL 占位实现

每个方言需要明确：

- `DbType`
- 参数名前缀
- 分页语法
- `LIKE` / `StartsWith` / `EndsWith`
- `IN` 子句空集合策略
- 布尔值与时间类型参数规范
- 参数对象工厂

MySQL 本轮先补到表达式和参数工厂层，不强行新增完整 `MySqlRepository<T>`，避免把范围推大；但确保后续新增仓储时不需要再重写表达式系统。

### D. 性能方向

本轮以“去掉冗余路径并减少无意义对象分配”为主，不引入过早复杂优化。重点包括：

- 避免在 PostgreSQL 查询中先手写读取、后又重复走其他查询路径
- 查询默认显式列名，避免 `SELECT *`
- 继续复用 `ObjectMapping<T>` 的 getter/setter 缓存
- 保持参数化 SQL，避免频繁字面值拼接进入执行路径
- 尽量让 `SqlExpression<T>` 构建一次后只输出需要的片段

## File-Level Changes

### Modify

- `Customize_ORM/Impl/PostgresRepository.cs`
  - 修复 `QueryAsync(Expression<Func<T, bool>>?)`
  - 统一表达式查询执行方式
  - 删除对旧扩展查询 API 的依赖

- `Customize_ORM/Extensions/SqlExpression.cs`
  - 拆分职责
  - 明确 builder 接口
  - 改善命名与内部结构

- `Customize_ORM/Enums/DBType.cs`
  - 增加 `MySQL`

### Add

- `Customize_ORM/Extensions/Sql/SqlDialect.cs`
  - 方言抽象基类或接口

- `Customize_ORM/Extensions/Sql/PostgreSqlDialect.cs`
  - PostgreSQL 方言实现

- `Customize_ORM/Extensions/Sql/ClickHouseDialect.cs`
  - ClickHouse 方言实现

- `Customize_ORM/Extensions/Sql/MySqlDialect.cs`
  - MySQL 方言占位实现

- `Customize_ORM/Extensions/Sql/SqlParameterContext.cs`
  - 参数上下文

- `Customize_ORM/Extensions/Sql/SqlExpressionVisitor.cs`
  - 表达式翻译器

## Error Handling

- 保持仓储层日志输出，但优先让编译期能约束错误路径
- 参数创建失败、表达式不支持时直接抛出明确异常
- 执行层继续只吞掉业务允许吞掉的异常，并保留实体类型、方法名、SQL 片段

## Testing Strategy

本轮至少补 3 类验证：

1. 编译验证
   - `dotnet build`

2. 表达式翻译验证
   - `Contains/StartsWith/EndsWith`
   - `IN`
   - `null` 比较
   - `AndAlso/OrElse`

3. PostgreSQL 仓储回归验证
   - `QueryAsync()` 无条件
   - `QueryAsync(e => ...)` 有条件
   - `QueryFirstOrDefaultAsync(e => ...)`

如果当前项目里没有现成单元测试工程，本轮可以先在 `Test` 项目中补最小可运行验证，后续再抽成正式测试项目。

## Non-Goals

以下内容不在本轮强行完成：

- 完整的 `MySqlRepository<T>` 实现
- 全量 CRUD 全仓储统一基类重写
- 对批量更新策略做大范围结构重写
- 引入复杂表达式优化器

## Expected Outcome

完成后应达到以下状态：

- 项目恢复可编译
- PostgreSQL 查询路径不再依赖已移除的旧扩展方法
- `SqlExpression<T>` 对仓储调用仍然直观，但内部结构更干净
- PostgreSQL 与 ClickHouse 的方言差异有明确归属
- 后续补 MySQL 时主要新增仓储和连接工厂，不需要推翻表达式层
