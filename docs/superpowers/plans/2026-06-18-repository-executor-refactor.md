# Repository Executor Refactor Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Introduce a shared internal execution helper for PostgreSQL and ClickHouse repositories to reduce duplicated command and parameter boilerplate.

**Architecture:** Add a small provider-agnostic command execution layer in the ORM library, then migrate `PostgresRepository<T>` and `ClickhouseRepository<T>` onto it while preserving their existing SQL composition and mapping code. Keep batch PostgreSQL logic unchanged in this pass.

**Tech Stack:** .NET 10, `DbConnection`/`DbCommand`, Npgsql, ClickHouse.Driver, existing ORM mapping infrastructure

---

### Task 1: Add shared execution primitives

**Files:**
- Create: `C:\Code\测试项目\Customize_ORM\Customize_ORM\Extensions\Sql\DbCommandExecution.cs`
- Reuse: `C:\Code\测试项目\Customize_ORM\Customize_ORM\Extensions\Sql\OwnedDbDataReader.cs`

- [ ] Add a shared internal helper that can attach parameters and execute scalar/non-query/reader paths.
- [ ] Keep provider specifics at the call site via connection/command factory delegates.
- [ ] Preserve command-owned reader disposal behavior.

### Task 2: Refactor PostgreSQL repository onto the helper

**Files:**
- Modify: `C:\Code\测试项目\Customize_ORM\Customize_ORM\Impl\PostgresRepository.cs`

- [ ] Replace duplicated parameter-attach and simple execution paths with shared helper calls.
- [ ] Keep expression SQL building and `MapFromReader` logic in the repository.
- [ ] Preserve cancellation behavior and logging semantics.

### Task 3: Refactor ClickHouse repository onto the helper

**Files:**
- Modify: `C:\Code\测试项目\Customize_ORM\Customize_ORM\Impl\ClickhouseRepository.cs`
- Reuse: `C:\Code\测试项目\Customize_ORM\Customize_ORM\Extensions\Sql\ClickHouseDialect.cs`

- [ ] Move repeated command creation and parameter materialization behind the helper.
- [ ] Keep row mapping, projection caching, and SQL assembly inside the repository.
- [ ] Avoid changing public method signatures or query results.

### Task 4: Verify behavior

**Files:**
- Reuse: `C:\Code\测试项目\Customize_ORM\Test\SqlExpressionRegression.cs`
- Reuse: `C:\Code\测试项目\Customize_ORM\Test\OwnedDbDataReaderRegression.cs`

- [ ] Run `dotnet build C:\Code\测试项目\Customize_ORM\Customize_ORM.slnx --ignore-failed-sources`.
- [ ] Run `dotnet test C:\Code\测试项目\Customize_ORM\Customize_ORM.slnx --no-build`.
- [ ] Run `C:\Code\测试项目\Customize_ORM\Test\bin\Debug\net10.0\Test.exe --regression-only`.
