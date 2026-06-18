using Customize_ORM.Models;
using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Text;

namespace Customize_ORM.Interfaces
{
    /// <summary>
    /// 批量操作接口
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public interface IBatchPostgresRepository<T> : IPostgresRepository<T> where T : class
    {
        /// <summary>
        /// 数据批量更新(非实时)
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="entity"></param>
        /// <param name="columns"></param>
        /// <param name="expression"></param>
        /// <param name="token"></param>
        /// <param name="partition">分区信息</param>
        /// <returns></returns>
        ValueTask<bool> BulkUpdateAsync(T entity, IEnumerable<string> columns, CancellationToken token, Expression<Func<T, bool>>? expression = null, string partition = "");
        /// <summary>
        /// 重试逻辑
        /// </summary>
        /// <param name="operation">重试数据对象</param>
        /// <param name="token">取消token</param>
        /// <returns></returns>
        ValueTask<bool> RetryUpdateAsync(RetryOperation<T> operation, CancellationToken token);
        /// <summary>
        /// 数据批量插入
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="entitys"></param>
        /// <param name="isRetry">是否是文件重试</param>
        /// <param name="tableName"></param>
        /// <returns></returns>
        Task<(bool, ulong)> BulkInsertAsync(IReadOnlyList<T> entitys, string tableName = "", bool isRetry = false);
        /// <summary>
        /// 程序停止
        /// </summary>
        /// <param name="timeout"></param>
        /// <returns></returns>
        ValueTask StopAsync(TimeSpan timeout = default);
    }
}
