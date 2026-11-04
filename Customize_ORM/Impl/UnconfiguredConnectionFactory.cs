using Customize_ORM.Enums;
using Customize_ORM.Interfaces;
using System.Data.Common;

namespace Customize_ORM.Impl
{
    /// <summary>
    /// 未配置连接串时注册的占位工厂：构造阶段不抛异常，避免未使用该库的部署启动失败；
    /// 真正取连接时才抛出带库名和配置键的明确异常。
    /// </summary>
    public sealed class UnconfiguredConnectionFactory(DBType dbType, string configurationKey) : IDbConnectionFactory
    {
        public DBType DbType { get; } = dbType;

        /// <summary>
        /// 始终抛出配置缺失异常，提示调用方补齐配置。
        /// </summary>
        /// <param name="token">取消令牌（未使用）</param>
        public Task<DbConnection> OpenConnectionAsync(CancellationToken token = default)
        {
            throw new InvalidOperationException(
                $"{DbType} connection string '{configurationKey}' not found in configuration.");
        }
    }
}
