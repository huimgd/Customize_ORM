using ClickHouse.Driver;

namespace Customize_ORM.Interfaces.ConnectionFactory
{
    public interface IChConnectionFactory
    {
        ClickHouseClient GetClientAsync();
    }
}
