using Customize_ORM.Enums;
using System.Data.Common;

namespace Customize_ORM.Interfaces
{
    /// <summary>
    /// 统一的数据库连接工厂契约。所有数据库实现都只通过本接口对外暴露连接创建能力，
    /// 返回类型为 ADO.NET 抽象的 <see cref="DbConnection"/>，仓储层不再依赖任何驱动专属类型。
    /// </summary>
    public interface IDbConnectionFactory
    {
        /// <summary>
        /// 当前工厂对应的数据库类型，用于 DI 按类型区分与方言选择。
        /// </summary>
        DBType DbType { get; }

        /// <summary>
        /// 创建并打开一个数据库连接。异常直接向上抛出，由业务调用方决定重试或降级，
        /// 工厂内部不做吞异常处理。
        /// </summary>
        /// <param name="token">取消令牌</param>
        /// <returns>已打开的数据库连接，调用方负责释放</returns>
        Task<DbConnection> OpenConnectionAsync(CancellationToken token = default);
    }
}
