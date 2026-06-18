# Repository Executor Refactor Design

## Goal

Extract the repeated command-execution plumbing from `PostgresRepository<T>` and `ClickhouseRepository<T>` into a shared internal helper so the repositories focus on SQL composition and result mapping instead of connection/command boilerplate.

## Scope

- Include only `PostgresRepository<T>` and `ClickhouseRepository<T>`.
- Do not change public repository interfaces in this pass.
- Do not refactor `BatchPostgresRepository<T>` yet.

## Design

### Shared execution helper

Create an internal helper in the ORM library responsible for:

- copying `DbParameter` values into provider command collections
- executing scalar, non-query, and reader operations with cancellation support
- wrapping provider readers that must own command lifetime until disposal

The helper should expose small provider-agnostic methods and accept factory delegates for provider-specific command creation.

### PostgreSQL integration

`PostgresRepository<T>` should delegate:

- parameter attachment
- `ExecuteNonQueryAsync`
- raw `QueryAsync(string sql, ...)` reader execution
- scalar execution used by count and similar simple paths

Reader lifetime safety must remain intact by preserving close-on-dispose behavior.

### ClickHouse integration

`ClickhouseRepository<T>` should delegate:

- command creation from SQL + `DbParameter` list
- provider parameter materialization for `ClickHouseDbParameter`
- repeated reader/scalar execution scaffolding

Repository methods should keep their current SQL-building and row/projection mapping logic.

## Non-Goals

- No public API redesign.
- No full Dapper removal in this pass.
- No batch-write pipeline changes.

## Verification

- `dotnet build C:\Code\测试项目\Customize_ORM\Customize_ORM.slnx --ignore-failed-sources`
- `dotnet test C:\Code\测试项目\Customize_ORM\Customize_ORM.slnx --no-build`
- `C:\Code\测试项目\Customize_ORM\Test\bin\Debug\net10.0\Test.exe --regression-only`
