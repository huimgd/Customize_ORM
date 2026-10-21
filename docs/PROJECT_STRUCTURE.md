# 项目结构说明

本文档用于约定 `Customize_ORM` 当前目录职责，后续新增文件优先放入对应目录，避免工具类继续堆叠到单一目录中。

## 主项目 `Customize_ORM`

- `Attributes/`：实体映射相关特性，例如主键、分区键、默认值和忽略字段。
- `Enums/`：跨模块共享的枚举。
- `Interfaces/`：仓储接口与数据库连接工厂接口。
- `Impl/`：仓储和连接工厂的具体实现，保留数据库访问流程，不承载通用 SQL 构建规则。
- `Models/`：批量操作、分页、配置和重试等数据模型。
- `Extensions/Caching/`：通道、缓存、批处理和并发集合等基础设施。
- `Extensions/Expressions/`：面向调用方的表达式 SQL 构建入口。
- `Extensions/Files/`：文件读写、文件名和 ID 工具。
- `Extensions/Mapping/`：实体映射元数据和行工厂生成逻辑。
- `Extensions/Utilities/`：通用扩展方法与工具函数。

## SQL 内部组件 `Customize_ORM/Extensions/Sql`

- `Builders/`：数据库相关 SQL 片段构建器，例如 PostgreSQL 批量更新、CTE 更新、ClickHouse 查询 SQL 与 Xugu 查询/批量更新 SQL。
- `Dialects/`：数据库方言差异，包括参数前缀、分页语法和参数创建（Xugu 采用 `:name` 占位符）。
- `Execution/`：命令创建、参数挂载和读取器生命周期管理。
- `Expressions/`：表达式树访问器与参数上下文。
- `Mapping/`：数据库记录到标量、DTO、实体的映射，以及 PostgreSQL/Xugu 参数与类型归一化。

## 测试项目 `Test`

- `Entities/`：回归测试使用的实体类型。
- `Regressions/`：不依赖真实数据库的回归 harness，主要验证 SQL 生成、参数归一化和 reader 映射行为。
- `Program.cs`：回归 harness 的统一入口，支持 `--regression-only` 只运行本地回归。
