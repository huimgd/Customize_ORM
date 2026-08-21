using Customize_ORM.Extensions;
using Customize_ORM.Impl;
using Customize_ORM.Interfaces;
using Customize_ORM.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Test.Entities;

namespace Test
{
    internal class Program
    {
        /// <summary>
        /// 程序入口，支持按参数执行指定回归集合或示例代码。
        /// </summary>
        static async Task Main(string[] args)
        {
            //ClickHouseQuerySqlBuilderRegression.Run();
            //PostgreSqlQuerySqlBuilderRegression.Run();
            //PostgreSqlParameterFactoryRegression.Run();
            //PostgreSqlCteUpdateCommandBuilderRegression.Run();
            //PostgreSqlBulkUpdateSqlBuilderRegression.Run();
            //BatchPostgresRepositoryUpdateRetryRegression.Run();
            //EntityDataReaderMapperRegression.Run();
            //DbRecordMapperRegression.Run();
            //OwnedDbDataReaderRegression.Run();
            //SqlExpressionRegression.Run();

            // 提供 jsonb 映射转换的单项回归入口，避免执行真实数据库示例。
            if (args.Contains("--run-jsonb-mapping-regression", StringComparer.OrdinalIgnoreCase))
            {
                JsonbMappingRegression.Run();
                return;
            }

            // 提供稳定的单项回归入口，便于在修复表达式问题时反复验证。
            if (args.Contains("--run-sql-expression-regression", StringComparer.OrdinalIgnoreCase))
            {
                SqlExpressionRegression.Run();
                return;
            }

            if (args.Contains("--regression-only", StringComparer.OrdinalIgnoreCase))
            {
                return;
            }

            // 批量更新重试逻辑（RETURNING 拼接、未命中过滤）的纯逻辑回归，不连接真实数据库。
            if (args.Contains("--run-batch-update-retry-regression", StringComparer.OrdinalIgnoreCase))
            {
                BatchPostgresRepositoryUpdateRetryRegression.Run();
                return;
            }

            // 构建配置 (Configuration)
            var configuration = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                .Build();

            // 创建服务集合 (ServiceCollection)
            var services = new ServiceCollection();

            // 注入日志服务 (因为 Extension.cs 中的连接工厂依赖 ILogger)
            services.AddLogging(builder =>
            {
                builder.AddConsole(); // 添加控制台日志输出
            });

            // 调用您在 Extension.cs 中编写的扩展方法，注入数据库仓储/连接工厂
            services.AddRepositories(configuration);
            //注入postgresql数据库服务
            services.AddSingleton(typeof(IBatchPostgresRepository<>), typeof(BatchPostgresRepository<>));
            services.AddSingleton(typeof(IPostgresRepository<>), typeof(PostgresRepository<>));
            //注入clickhouse数据库服务
            services.AddSingleton(typeof(IClickhouseRepository<>), typeof(ClickhouseRepository<>));
            // 构建服务提供者 (ServiceProvider)
            var serviceProvider = services.BuildServiceProvider();

            //初始化您的全局 AppSettings
            var appSettings = new AppSettings();
            // 将 appsettings.json 中的 FileSettings 节点绑定到对象
            configuration.GetSection("FileSettings").Bind(appSettings.FileSettings);

            // 调用 AppSettings 的静态初始化方法，传入配置和 ServiceProvider
            AppSettings.Initialize(appSettings, serviceProvider);
 
            

          

            Console.WriteLine($"临时目录配置为: {AppSettings.Current.FileSettings.TempDirectoryPath}");
           // 通过局部的 serviceProvider 获取服务进行测试
            var repository = serviceProvider.GetRequiredService<IPostgresRepository<AdminEntity>>();
           long count = await repository.GetCountAsync(e => e.IsDeleted == false, CancellationToken.None);
            Console.WriteLine($"未删除的管理员数量: {count}");
            var blacklogs=serviceProvider.GetRequiredService<IClickhouseRepository<BlackApiLogEntity>>();
           var loginfos=blacklogs.QueryStreamAsync(e => e.IsBlackMobile == true);
            await foreach (var log in loginfos)
            {
                Console.WriteLine($"黑名单日志: {log.Id}, 手机号: {log.Mobile}, 持续时间: {log.ExecutionDuration}");
            }
        }
    }
}
