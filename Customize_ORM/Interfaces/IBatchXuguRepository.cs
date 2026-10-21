using Customize_ORM.Models;
using System.Linq.Expressions;

namespace Customize_ORM.Interfaces
{
    /// <summary>
    /// 虚谷批量操作接口，与 IBatchPostgresRepository 能力对齐。
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public interface IBatchXuguRepository<T> : IXuguRepository<T> where T : class
    {
        /// <summary>
        /// 数据批量更新(非实时)
        /// </summary>
        /// <param name="entity">实体</param>
        /// <param name="columns">更新列</param>
        /// <param name="expression">条件表达式</param>
        /// <param name="token">取消令牌</param>
        /// <param name="partition">分区信息</param>
        ValueTask<bool> BulkUpdateAsync(T entity, IEnumerable<string> columns, CancellationToken token, Expression<Func<T, bool>>? expression = null, string partition = "");
        /// <summary>
        /// 重试逻辑
        /// </summary>
        /// <param name="operation">重试数据对象</param>
        /// <param name="token">取消令牌</param>
        ValueTask<bool> RetryUpdateAsync(RetryOperation<T> operation, CancellationToken token);
        /// <summary>
        /// 数据批量插入
        /// </summary>
        /// <param name="entitys">实体集合</param>
        /// <param name="tableName">表名</param>
        /// <param name="isRetry">是否是文件重试</param>
        Task<(bool, ulong)> BulkInsertAsync(IReadOnlyList<T> entitys, string tableName = "", bool isRetry = false);
        /// <summary>
        /// 程序停止
        /// </summary>
        /// <param name="timeout">超时时间</param>
        ValueTask StopAsync(TimeSpan timeout = default);
    }
}
