using Customize_ORM.Models;
using System.Linq.Expressions;

namespace Customize_ORM.Interfaces
{
    /// <summary>
    /// 批量更新与失败重试能力扩展。
    /// </summary>
    /// <typeparam name="T">实体类型</typeparam>
    public interface IBatchUpdateRepository<T> where T : class
    {
        /// <summary>
        /// 数据批量更新（非实时，走后台批量处理通道）。
        /// </summary>
        /// <param name="entity">实体</param>
        /// <param name="columns">需要更新的列</param>
        /// <param name="token">取消令牌</param>
        /// <param name="expression">自定义更新条件</param>
        /// <param name="partition">分区后缀；不支持分区表的实现忽略该参数</param>
        ValueTask<bool> BulkUpdateAsync(T entity, IEnumerable<string> columns, CancellationToken token, Expression<Func<T, bool>>? expression = null, string partition = "");

        /// <summary>
        /// 对失败批次执行重试写入。
        /// </summary>
        /// <param name="operation">重试数据对象</param>
        /// <param name="token">取消令牌</param>
        ValueTask<bool> RetryUpdateAsync(RetryOperation<T> operation, CancellationToken token);

        /// <summary>
        /// 停止后台批量处理任务。
        /// </summary>
        /// <param name="timeout">等待超时时间</param>
        ValueTask StopAsync(TimeSpan timeout = default);
    }
}
