using Customize_ORM.Enums;
using Customize_ORM.Impl;
using Customize_ORM.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Customize_ORM.Extensions
{
    public static class Extension
    {
        /// <summary>
        /// PostgreSQL 连接串配置键。
        /// </summary>
        public const string PostgreSqlConfigurationKey = "Database";

        /// <summary>
        /// ClickHouse 连接串配置键。
        /// </summary>
        public const string ClickHouseConfigurationKey = "ClickHouse";

        /// <summary>
        /// 虚谷连接串配置键。
        /// </summary>
        public const string XuguConfigurationKey = "Xugu";

        /// <summary>
        /// 按数据库类型注册统一的连接工厂。
        /// 已配置连接串的库注册真实工厂；未配置的库注册占位工厂，仅在真正取连接时抛错，
        /// 避免未使用该库的部署（如只接 PG 的服务）因缺少其他库配置而启动失败。
        /// </summary>
        /// <param name="services">服务集合</param>
        /// <param name="configuration">应用配置</param>
        public static IServiceCollection AddRepositories(this IServiceCollection services, IConfiguration configuration)
        {
            //pgsql开启DateTimeOffset支持
            AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

            // 注入 pgsql
            var postgreSqlConnectionString = configuration.GetSection(PostgreSqlConfigurationKey).Value;
            if (string.IsNullOrWhiteSpace(postgreSqlConnectionString))
            {
                services.AddKeyedSingleton<IDbConnectionFactory>(DBType.PostgreSQL,
                    (_, _) => new UnconfiguredConnectionFactory(DBType.PostgreSQL, PostgreSqlConfigurationKey));
            }
            else
            {
                services.AddKeyedSingleton<IDbConnectionFactory>(DBType.PostgreSQL,
                    (_, _) => new NpgsqlConnectionFactory(postgreSqlConnectionString));
            }

            // 注入 clickhouse
            var clickHouseConnectionString = configuration.GetSection(ClickHouseConfigurationKey).Value;
            if (string.IsNullOrWhiteSpace(clickHouseConnectionString))
            {
                services.AddKeyedSingleton<IDbConnectionFactory>(DBType.ClickHouse,
                    (_, _) => new UnconfiguredConnectionFactory(DBType.ClickHouse, ClickHouseConfigurationKey));
            }
            else
            {
               services.AddKeyedSingleton<IDbConnectionFactory>(DBType.ClickHouse,
                    (_, _) => new ClickhouseConnectionFactory(clickHouseConnectionString));
            }

            // 注入虚谷
            var xuguConnectionString = configuration.GetSection(XuguConfigurationKey).Value;
            if (string.IsNullOrWhiteSpace(xuguConnectionString))
            {
                services.AddKeyedSingleton<IDbConnectionFactory>(DBType.Xugu,
                    (_, _) => new UnconfiguredConnectionFactory(DBType.Xugu, XuguConfigurationKey));
            }
            else
            {
                services.AddKeyedSingleton<IDbConnectionFactory>(DBType.Xugu,
                    (_, _) => new XuguConnectionFactory(xuguConnectionString));
            }

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
