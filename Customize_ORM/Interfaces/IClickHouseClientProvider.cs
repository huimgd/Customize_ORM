using ClickHouse.Driver;

namespace Customize_ORM.Interfaces
{
    /// <summary>
    /// ClickHouse 专属能力：二进制批量写入依赖驱动的 <see cref="ClickHouseClient"/>，
    /// 无法用 ADO.NET 抽象表达，因此单独抽取为扩展接口，不污染通用连接工厂契约。
    /// </summary>
    public interface IClickHouseClientProvider
    {
        /// <summary>
        /// 获取 ClickHouse 客户端实例（进程内复用，由实现方保证线程安全）。
        /// </summary>
        ClickHouseClient GetClient();
    }
}
