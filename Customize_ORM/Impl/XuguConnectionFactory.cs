using Customize_ORM.Enums;
using Customize_ORM.Interfaces;
using System.Data.Common;
using XuguClient;

namespace Customize_ORM.Impl
{
    /// <summary>
    /// 虚谷连接工厂：基于 XGConnection 创建连接；驱动无连接池配置接口，走驱动自身直连语义。
    /// </summary>
    public sealed class XuguConnectionFactory : IDbConnectionFactory
    {
        private readonly string _connectionString;

        public XuguConnectionFactory(string connectionString)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
            _connectionString = NormalizeAutoCommit(connectionString);
        }

        public DBType DbType => DBType.Xugu;

        /// <summary>
        /// 获取虚谷数据库连接：每次返回独立打开的 XGConnection，避免共享连接上的多读者互相干扰。
        /// 异常不吞，交由业务层感知。
        /// </summary>
        /// <param name="token">取消令牌</param>
        public async Task<DbConnection> OpenConnectionAsync(CancellationToken token = default)
        {
            var connection = new XGConnection(_connectionString);
            await connection.OpenAsync(token);
            return connection;
        }

        /// <summary>
        /// 补齐 AUTO_COMMIT=on：本仓储所有写入均依赖显式提交语义正确性；开启自动提交后，
        /// 单语句写入（非事务包裹的多值 INSERT 分批）不需要手工 COMMIT。
        /// </summary>
        /// <param name="connectionString">配置文件提供的原始连接串。</param>
        private static string NormalizeAutoCommit(string connectionString)
        {
            // 尊重调用方显式设置的 AUTO_COMMIT；缺失时默认开启，虚谷默认值未承诺
            if (!connectionString.Contains("AUTO_COMMIT", StringComparison.OrdinalIgnoreCase))
            {
                return connectionString.TrimEnd(';', ' ') + ";AUTO_COMMIT=on;";
            }

            return connectionString;
        }
    }
}
