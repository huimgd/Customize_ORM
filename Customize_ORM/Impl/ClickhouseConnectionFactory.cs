using ClickHouse.Driver;
using Customize_ORM.Enums;
using Customize_ORM.Interfaces;
using System.Data.Common;

namespace Customize_ORM.Impl
{
    /// <summary>
    /// ClickHouse 连接工厂：客户端实例进程内复用，负责创建 ADO.NET 连接；
    /// 同时通过 <see cref="IClickHouseClientProvider"/> 暴露驱动客户端供二进制批量写入使用。
    /// </summary>
    public sealed class ClickhouseConnectionFactory : IDbConnectionFactory, IClickHouseClientProvider
    {
        private readonly string _connectionString;
        private readonly Lazy<ClickHouseClient> _client;

        public ClickhouseConnectionFactory(string connectionString)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
            _connectionString = connectionString;
            _client = new Lazy<ClickHouseClient>(() => new ClickHouseClient(_connectionString));
        }

        public DBType DbType => DBType.ClickHouse;

        /// <summary>
        /// 获取复用的 ClickHouse 客户端，批量写入等驱动专属能力通过它获得。
        /// </summary>
        public ClickHouseClient GetClient() => _client.Value;

        /// <summary>
        /// 通过驱动客户端创建并打开一个连接；异常不吞，交由业务层感知。
        /// </summary>
        /// <param name="token">取消令牌</param>
        public async Task<DbConnection> OpenConnectionAsync(CancellationToken token = default)
        {
            var connection = _client.Value.CreateConnection();
            await connection.OpenAsync(token);
            return connection;
        }
    }
}
