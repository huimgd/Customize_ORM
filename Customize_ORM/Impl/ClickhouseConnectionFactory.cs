using ClickHouse.Driver;
using Customize_ORM.Interfaces.ConnectionFactory;
using Microsoft.Extensions.Logging;

namespace Customize_ORM.Impl
{
    public class ClickhouseConnectionFactory : IChConnectionFactory
    {
        private readonly string _connectionString;

        public ClickhouseConnectionFactory(string connectionString, ILogger<ClickhouseConnectionFactory> logger)
        {
            _connectionString = connectionString;
        }
        public ClickHouseClient GetClientAsync()
        {
            return new ClickHouseClient(_connectionString);
        }
    }
}
