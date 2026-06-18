using Customize_ORM.Interfaces.ConnectionFactory;
using Npgsql;

namespace Customize_ORM.Impl
{
    public class NpgsqlConnectionFactory : IPgConnectionFactory
    {
        private readonly NpgsqlDataSource _dataSource;

        public NpgsqlConnectionFactory(string connectionString)
        {
            var builder = new NpgsqlConnectionStringBuilder(connectionString)
            {
                Pooling = true,
                MaxPoolSize = 100,
                ConnectionIdleLifetime = 30,
                ConnectionLifetime = 1800
            };
            _dataSource = NpgsqlDataSource.Create(builder.ConnectionString);
        }
        /// <summary>
        /// 创建pgsql数据库连接
        /// </summary>
        /// <returns></returns>
        public async Task<NpgsqlConnection> GetConnectionAsync(CancellationToken token = default)
        {
            return await _dataSource.OpenConnectionAsync(token);//不能try，这里就是要把异常抛到对应业务逻辑上           
        }
    }
}
