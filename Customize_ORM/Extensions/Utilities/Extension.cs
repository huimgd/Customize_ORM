using Customize_ORM.Impl;
using Customize_ORM.Interfaces.ConnectionFactory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Customize_ORM.Extensions
{
    public static class Extension
    {
        public static IServiceCollection AddRepositories(this IServiceCollection services, IConfiguration configuration)
        {
            //pgsql开启DateTimeOffset支持
            AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);
            //注入pgsql
            services.AddSingleton<IPgConnectionFactory>(sp =>
            { 
                var connectionString = configuration.GetSection("Database").Value;
                if (string.IsNullOrEmpty(connectionString))
                {
                    throw new InvalidOperationException($"Pgsql Connection string 'Database' not found in configuration.");
                }

                var logger = sp.GetRequiredService<ILogger<NpgsqlConnectionFactory>>();
                return new NpgsqlConnectionFactory(connectionString);
            });
            //注入clickhouse
            services.AddSingleton<IChConnectionFactory>(sp =>
            {
                var connectionString = configuration.GetSection("ClickHouse").Value;
                if (string.IsNullOrEmpty(connectionString))
                {
                    throw new InvalidOperationException($"Clickhouse Connection string 'ClickHouse' not found in configuration.");
                }
                var logger = sp.GetRequiredService<ILogger<ClickhouseConnectionFactory>>();
                return new ClickhouseConnectionFactory(connectionString, logger);
            });
            //注入虚谷（配置在单例委托内才求值，未配置虚谷的存量应用初始化不受影响）
            services.AddSingleton<IXuguConnectionFactory>(sp =>
            {
                var connectionString = configuration.GetSection("Xugu").Value;
                if (string.IsNullOrEmpty(connectionString))
                {
                    throw new InvalidOperationException($"Xugu Connection string 'Xugu' not found in configuration.");
                }
                return new XuguConnectionFactory(connectionString);
            });
            return services;
        }
        /// <summary>
        /// 命名转换
        /// </summary>
        /// <param name="pascalCase"></param>
        /// <returns></returns>
        internal static string ToSnakeCase(string pascalCase)
        {
            if (string.IsNullOrEmpty(pascalCase)) return pascalCase;
            return string.Concat(pascalCase.Select((c, i) => i > 0 && char.IsUpper(c) ? "_" + c : c.ToString())).ToLower();
        }
        /// <summary>
        /// 截取到小时（去除分钟、秒、毫秒）
        /// </summary>
        internal static DateTime TruncateToHour(this DateTime dateTime)
        {
            return new DateTime(dateTime.Year, dateTime.Month, dateTime.Day, dateTime.Hour, 0, 0, dateTime.Kind);
        }
    }
}
