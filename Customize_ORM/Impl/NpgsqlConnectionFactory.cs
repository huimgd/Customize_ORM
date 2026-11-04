using Customize_ORM.Enums;
using Customize_ORM.Interfaces;
using Npgsql;
using System.Data.Common;

namespace Customize_ORM.Impl
{
    /// <summary>
    /// PostgreSQL 连接工厂：基于 NpgsqlDataSource 复用连接池，对外只暴露 ADO.NET 抽象连接。
    /// </summary>
    public sealed class NpgsqlConnectionFactory : IDbConnectionFactory
    {
        private readonly NpgsqlDataSource _dataSource;

        public NpgsqlConnectionFactory(string connectionString)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
            var builder = new NpgsqlConnectionStringBuilder(connectionString)
            {
                Pooling = true,
                MaxPoolSize = 100,
                ConnectionIdleLifetime = 30,
                ConnectionLifetime = 1800
            };
            _dataSource = NpgsqlDataSource.Create(builder.ConnectionString);
        }

        public DBType DbType => DBType.PostgreSQL;

        /// <summary>
        /// 从连接池创建并打开一个 PostgreSQL 连接；异常不由工厂吞掉，交由业务层感知。
        /// </summary>
        /// <param name="token">取消令牌</param>
        public async Task<DbConnection> OpenConnectionAsync(CancellationToken token = default)
        {
            return await _dataSource.OpenConnectionAsync(token);
        }
    }
}
