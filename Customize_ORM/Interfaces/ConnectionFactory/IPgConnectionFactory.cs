using Npgsql;

namespace Customize_ORM.Interfaces.ConnectionFactory
{
    public interface IPgConnectionFactory
    {
        Task<NpgsqlConnection> GetConnectionAsync(CancellationToken token = default);
    }
}
