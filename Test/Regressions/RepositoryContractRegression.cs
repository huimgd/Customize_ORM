using Customize_ORM.Enums;
using Customize_ORM.Extensions;
using Customize_ORM.Extensions.Sql;
using Customize_ORM.Impl;
using Customize_ORM.Interfaces;
using Customize_ORM.Interfaces.Sql;
using Microsoft.Extensions.Logging.Abstractions;
using System.Data.Common;
using System.Reflection;
using Test.Entities;

namespace Test.Regressions
{
    /// <summary>
    /// 统一抽象契约回归：不连接真实数据库，只校验连接工厂与仓储接口的跨库一致性，
    /// 防止后续新增数据库时漏实现公共契约或签名漂移。
    /// </summary>
    internal static class RepositoryContractRegression
    {
        public static void Run()
        {
            var failures = new List<string>();

            Try(failures, "connection factories expose a single unified contract", () =>
            {
                var factoryType = typeof(IDbConnectionFactory);
                var methodNames = factoryType.GetMethods()
                    .Where(m => !m.IsSpecialName)
                    .Select(m => m.Name)
                    .OrderBy(n => n);
                AssertEqual(nameof(IDbConnectionFactory.OpenConnectionAsync), string.Join(",", methodNames));
                AssertEqual(typeof(DbConnection), factoryType.GetMethod(nameof(IDbConnectionFactory.OpenConnectionAsync))!.ReturnType
                    .GetGenericArguments()[0]);
                AssertEqual(typeof(DBType), factoryType.GetProperty(nameof(IDbConnectionFactory.DbType))!.PropertyType);
            });

            Try(failures, "all connection factories implement IDbConnectionFactory and report their own DBType", () =>
            {
                AssertFactory<NpgsqlConnectionFactory>(DBType.PostgreSQL);
                AssertFactory<XuguConnectionFactory>(DBType.Xugu);
                AssertFactory<ClickhouseConnectionFactory>(DBType.ClickHouse);
                AssertFactory<UnconfiguredConnectionFactory>(DBType.PostgreSQL);
                AssertTrue(
                    typeof(IClickHouseClientProvider).IsAssignableFrom(typeof(ClickhouseConnectionFactory)),
                    "ClickhouseConnectionFactory must expose IClickHouseClientProvider.");
            });

            Try(failures, "repositories share the IRepository<T> contract without driver-specific types", () =>
            {
                AssertTrue(typeof(IRepository<AdminEntity>).IsAssignableFrom(typeof(PostgresRepository<AdminEntity>)),
                    "PostgresRepository must implement IRepository<T>.");
                AssertTrue(typeof(IRepository<AdminEntity>).IsAssignableFrom(typeof(XuguRepository<AdminEntity>)),
                    "XuguRepository must implement IRepository<T>.");
                AssertTrue(typeof(IQueryableRepository<AdminEntity>).IsAssignableFrom(typeof(ClickhouseRepository<AdminEntity>)),
                    "ClickhouseRepository must implement IQueryableRepository<T>.");

                // 接口方法签名中不允许出现任何驱动专属类型
                var forbiddenNamespaces = new[] { "Npgsql", "XuguClient", "ClickHouse" };
                foreach (var method in typeof(IRepository<AdminEntity>).GetMethods())
                {
                    foreach (var parameter in method.GetParameters())
                    {
                        AssertNoDriverType(parameter.ParameterType, forbiddenNamespaces, $"{method.Name}.{parameter.Name}");
                    }

                    AssertNoDriverType(method.ReturnType, forbiddenNamespaces, method.Name);
                }
            });

            Try(failures, "batch repositories expose batch update and bulk insert capabilities", () =>
            {
                AssertTrue(typeof(IBatchUpdateRepository<AdminEntity>).IsAssignableFrom(typeof(BatchPostgresRepository<AdminEntity>)),
                    "BatchPostgresRepository must implement IBatchUpdateRepository<T>.");
                AssertTrue(typeof(IBulkInsertRepository<AdminEntity>).IsAssignableFrom(typeof(BatchPostgresRepository<AdminEntity>)),
                    "BatchPostgresRepository must implement IBulkInsertRepository<T>.");
                AssertTrue(typeof(IBatchUpdateRepository<AdminEntity>).IsAssignableFrom(typeof(BatchXuguRepository<AdminEntity>)),
                    "BatchXuguRepository must implement IBatchUpdateRepository<T>.");
                AssertTrue(typeof(IBulkInsertRepository<AdminEntity>).IsAssignableFrom(typeof(BatchXuguRepository<AdminEntity>)),
                    "BatchXuguRepository must implement IBulkInsertRepository<T>.");
            });

            Try(failures, "dialect resolution is the single entry point and covers every DBType", () =>
            {
                foreach (var dbType in Enum.GetValues<DBType>())
                {
                    var dialect = SqlDialect.For(dbType);
                    AssertTrue(dialect is IQueryDialect, $"{dbType} dialect must implement IQueryDialect.");
                    AssertEqual(dbType, dialect.DbType);
                }
            });

            Try(failures, "delete-by-keys SQL is produced by the dialect, not hardcoded in repositories", () =>
            {
                var keyValues = new object?[] { 1L, 2L };

                var postgreSql = SqlDialect.For(DBType.PostgreSQL);
                var postgreSqlSql = postgreSql.BuildDeleteByKeysSql("t_admin", "id", keyValues, typeof(long), out var postgreSqlParameters);
                AssertTrue(postgreSqlSql.Contains("= ANY(", StringComparison.Ordinal), "PostgreSQL should bind the key set as an array parameter.");
                AssertEqual(1, postgreSqlParameters.Count);

                var xugu = SqlDialect.For(DBType.Xugu);
                var xuguSql = xugu.BuildDeleteByKeysSql("t_admin", "id", keyValues, typeof(long), out var xuguParameters);
                AssertTrue(xuguSql.Contains("IN (", StringComparison.Ordinal), "Xugu should expand keys into an IN list.");
                AssertEqual(keyValues.Length, xuguParameters.Count);

                AssertThrows<NotSupportedException>(
                    () => SqlDialect.For(DBType.ClickHouse).BuildDeleteByKeysSql("t_admin", "id", keyValues, typeof(long), out _));
            });

            Try(failures, "unsupported single-row writes fail loudly on ClickHouse", () =>
            {
                // 使用真实工厂以便构造仓储（客户端惰性创建，本用例不会真正连接）
                var repository = new ClickhouseRepository<AdminEntity>(
                    new ClickhouseConnectionFactory("Host=127.0.0.1;Port=8123"),
                    NullLogger<ClickhouseRepository<AdminEntity>>.Instance);

                AssertThrows<NotSupportedException>(() => repository.InsertAsync(new AdminEntity(), default));
                AssertThrows<NotSupportedException>(() => repository.UpdateAsync(new AdminEntity(), ReadOnlyMemory<string>.Empty, default));
            });

            Try(failures, "unconfigured factory reports the missing configuration key", () =>
            {
                var factory = new UnconfiguredConnectionFactory(DBType.Xugu, "Xugu");
                AssertThrows<InvalidOperationException>(() => factory.OpenConnectionAsync());
                AssertTrue(
                    AssertThrows<InvalidOperationException>(() => factory.OpenConnectionAsync()).Message.Contains("Xugu"),
                    "Exception message must name the missing configuration key.");
            });

            if (failures.Count > 0)
            {
                throw new InvalidOperationException(
                    "RepositoryContract regression failures:" + Environment.NewLine +
                    string.Join(Environment.NewLine, failures));
            }

            Console.WriteLine("RepositoryContract regression harness passed.");
        }

        private static void AssertFactory<TFactory>(DBType expected) where TFactory : IDbConnectionFactory
        {
            AssertTrue(typeof(IDbConnectionFactory).IsAssignableFrom(typeof(TFactory)),
                $"{typeof(TFactory).Name} must implement IDbConnectionFactory.");

            // UnconfiguredConnectionFactory 需要一个配置键参数，这里只校验其可构造性与 DbType 约定
            if (typeof(TFactory) == typeof(UnconfiguredConnectionFactory))
            {
                var unconfigured = new UnconfiguredConnectionFactory(expected, "Database");
                AssertEqual(expected, unconfigured.DbType);
                return;
            }

            var connectionString = typeof(TFactory) == typeof(XuguConnectionFactory)
                ? "HOST=127.0.0.1;"
                : "Host=127.0.0.1;Database=x;Username=x;Password=x";

            var factory = (IDbConnectionFactory)Activator.CreateInstance(typeof(TFactory), connectionString)!;
            AssertEqual(expected, factory.DbType);
        }

        private static void AssertNoDriverType(Type type, string[] forbiddenNamespaces, string member)
        {
            // 展开泛型参数后再判断，避免 Task<NpgsqlConnection> 之类的返回值漏检
            var candidates = type.IsGenericType
                ? type.GetGenericArguments().Prepend(type)
                : [type];

            foreach (var candidate in candidates)
            {
                if (candidate.Namespace is { } ns && forbiddenNamespaces.Any(f => ns.StartsWith(f, StringComparison.Ordinal)))
                {
                    throw new InvalidOperationException(
                        $"Interface member '{member}' exposes driver-specific type '{candidate.FullName}'.");
                }
            }
        }

        private static void Try(List<string> failures, string name, Action action)
        {
            try
            {
                action();
                Console.WriteLine($"[PASS] {name}");
            }
            catch (Exception ex)
            {
                failures.Add($"[FAIL] {name}: {ex.Message}");
            }
        }

        private static void AssertTrue(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }

        private static void AssertEqual<T>(T expected, T actual)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
            {
                throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
            }
        }

        private static TException AssertThrows<TException>(Func<Task> action) where TException : Exception
        {
            try
            {
                // 同步方法抛出的异常在调用时就已产生，这里直接捕获
                action().GetAwaiter().GetResult();
            }
            catch (TException ex)
            {
                return ex;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Expected {typeof(TException).Name}, got {ex.GetType().Name}.", ex);
            }

            throw new InvalidOperationException($"Expected {typeof(TException).Name}, but no exception was thrown.");
        }

        private static TException AssertThrows<TException>(Action action) where TException : Exception
        {
            try
            {
                action();
            }
            catch (TException ex)
            {
                return ex;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Expected {typeof(TException).Name}, got {ex.GetType().Name}.", ex);
            }

            throw new InvalidOperationException($"Expected {typeof(TException).Name}, but no exception was thrown.");
        }
    }
}
