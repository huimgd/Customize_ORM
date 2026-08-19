using ClickHouse.Driver;
using ClickHouse.Driver.ADO;
using Customize_ORM.Interfaces.ConnectionFactory;
using Microsoft.Extensions.Logging;

namespace Customize_ORM.Impl
{
    public class ClickhouseConnectionFactory : IChConnectionFactory
    {
        private readonly string _connectionString;
        private readonly Lazy<ClickHouseClient> _client;
        public ClickhouseConnectionFactory(string connectionString, ILogger<ClickhouseConnectionFactory> logger)
        {
            _connectionString = connectionString;
            _client = new Lazy<ClickHouseClient>(() => new ClickHouseClient(_connectionString));
        }
        public ClickHouseClient GetClientAsync()
        {
            return _client.Value;
        }
        public async Task<ClickHouseConnection> GetConnectionAsync()
        {
            var newConnection = new ClickHouseConnection(_connectionString);
            await newConnection.OpenAsync();
            return newConnection;
        }
    }
}
