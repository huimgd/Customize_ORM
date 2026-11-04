using Customize_ORM.Models;
using System.Linq.Expressions;

namespace Customize_ORM.Interfaces
{
    /// <summary>
    /// 列投影查询与分页查询能力扩展。
    /// 仅在支持的数据库上实现（当前为 ClickHouse），不支持该能力集的仓储不实现本接口。
    /// </summary>
    /// <typeparam name="T">实体类型</typeparam>
    public interface IQueryableRepository<T> : IRepository<T> where T : class
    {
        /// <summary>
        /// 查询某列或某几列。
        /// </summary>
        /// <typeparam name="TValue">投影结果类型</typeparam>
        /// <param name="selector">投影表达式</param>
        /// <param name="filter">过滤条件表达式</param>
        /// <param name="tableName">表名，为空则使用映射表名</param>
        /// <param name="token">取消令牌</param>
        ValueTask<IEnumerable<TValue>> QueryAsync<TValue>(Expression<Func<T, TValue>> selector, Expression<Func<T, bool>>? filter = null, string? tableName = null, CancellationToken token = default) where TValue : class;

        /// <summary>
        /// 查询指定列的原始值集合。
        /// </summary>
        /// <typeparam name="TValue">列值类型</typeparam>
        /// <param name="columnName">列名</param>
        /// <param name="filter">过滤条件表达式</param>
        /// <param name="tableName">表名，为空则使用映射表名</param>
        /// <param name="token">取消令牌</param>
        ValueTask<List<TValue>> QueryAsync<TValue>(string columnName, Expression<Func<T, bool>>? filter = null, string? tableName = null, CancellationToken token = default);

        /// <summary>
        /// 分页查询某列或某几列。
        /// </summary>
        /// <typeparam name="TValue">投影结果类型</typeparam>
        /// <param name="selector">投影表达式</param>
        /// <param name="filter">过滤条件表达式</param>
        /// <param name="pageIndex">页索引</param>
        /// <param name="pageSize">每页数据量</param>
        /// <param name="orderBy">排序字段</param>
        /// <param name="tableName">表名，为空则使用映射表名</param>
        /// <param name="token">取消令牌</param>
        ValueTask<PagedResult<TValue>> QueryAsync<TValue>(Expression<Func<T, TValue>> selector, Expression<Func<T, bool>>? filter = null, int pageIndex = 0, int pageSize = 1000, string? orderBy = null, string? tableName = null, CancellationToken token = default) where TValue : class;

        /// <summary>
        /// 分页查询指定列的原始值集合。
        /// </summary>
        /// <typeparam name="TValue">列值类型</typeparam>
        /// <param name="columnName">列名</param>
        /// <param name="filter">过滤条件表达式</param>
        /// <param name="pageIndex">页索引</param>
        /// <param name="pageSize">每页数据量</param>
        /// <param name="tableName">表名，为空则使用映射表名</param>
        /// <param name="token">取消令牌</param>
        ValueTask<PagedResult<TValue>> QueryAsync<TValue>(string columnName, Expression<Func<T, bool>>? filter = null, int pageIndex = 0, int pageSize = 1000, string? tableName = null, CancellationToken token = default);

        /// <summary>
        /// 分页查询实体集合。
        /// </summary>
        /// <param name="filter">过滤条件表达式</param>
        /// <param name="pageIndex">页索引</param>
        /// <param name="pageSize">每页数据量</param>
        /// <param name="tableName">表名，为空则使用映射表名</param>
        /// <param name="token">取消令牌</param>
        ValueTask<IEnumerable<T>> QueryAsync(Expression<Func<T, bool>>? filter, int pageIndex, int pageSize, string tableName = "", CancellationToken token = default);

        /// <summary>
        /// 流式查询指定列，适用于大数据量单列读取。
        /// </summary>
        /// <typeparam name="TValue">列值类型</typeparam>
        /// <param name="columnName">列名</param>
        /// <param name="filter">过滤条件表达式</param>
        /// <param name="tableName">表名，为空则使用映射表名</param>
        /// <param name="token">取消令牌</param>
        IAsyncEnumerable<TValue> QueryStreamAsync<TValue>(string columnName, Expression<Func<T, bool>>? filter, string tableName = "", CancellationToken token = default);
    }
}
