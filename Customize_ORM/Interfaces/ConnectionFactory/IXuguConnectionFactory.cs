using System.Data.Common;

namespace Customize_ORM.Interfaces.ConnectionFactory
{
    /// <summary>
    /// 虚谷数据库连接工厂，接口层仅依赖 ADO.NET 抽象，不引用虚谷驱动类型。
    /// </summary>
    public interface IXuguConnectionFactory
    {
        /// <summary>
        /// 获取已打开的虚谷数据库连接。
        /// </summary>
        Task<DbConnection> GetConnectionAsync(CancellationToken token = default);
    }
}
