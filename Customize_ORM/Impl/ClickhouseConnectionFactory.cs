using ClickHouse.Driver;
using Customize_ORM.Interfaces.ConnectionFactory;

namespace Customize_ORM.Impl
{
    public class ClickhouseConnectionFactory : IChConnectionFactory
    {
        private readonly string _connectionString;

        public ClickhouseConnectionFactory(string connectionString) => _connectionString = connectionString;
        public ClickHouseClient GetClientAsync()
        {
            return new ClickHouseClient(_connectionString);
        }
    }
}
