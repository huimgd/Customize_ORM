using Customize_ORM.Models;
using System.Linq.Expressions;

namespace Customize_ORM.Interfaces
{
    public interface IClickhouseRepository<T>
    {

        /// <summary>
        /// 批量插入
        /// </summary>
        /// <param name="entitys">数据</param>
        /// <param name="tableName">数据表别名</param>
        /// <returns></returns>
        ValueTask<bool> BulkInsertAsync(List<T> entitys, string tableName = "", CancellationToken token = default);
        /// <summary>
        /// 获取数据数量
        /// </summary>
        /// <param name="expression">条件表达式</param>
        /// <param name="tableName">表名称</param>
        /// <returns></returns>
        ValueTask<long> GetCount(Expression<Func<T, bool>>? filter, string tableName = "", CancellationToken token = default);
        /// <summary>
        /// 查询数据(一次性加载，仅限小数据集)
        /// </summary>
        /// <param name="expression">条件表达式</param>
        /// <param name="tableName">表名称</param>
        /// <param name="token"></param>
        /// <returns></returns>
        ValueTask<IEnumerable<T>> QueryAsync(Expression<Func<T, bool>>? filter = null, string tableName = "", CancellationToken token = default);
        /// <summary>
        /// 查询某列或者某几列
        /// </summary>
        /// <typeparam name="TValue">投影结果类型（可以是匿名类型或自定义类）</typeparam>
        /// <param name="selector">投影表达式，例如 x => new { x.Id, x.Name } 或 x => new Dto { Id = x.Id }</param>
        /// <param name="filter">过滤条件表达式（可选）</param>
        /// <param name="tableName">表名（可选，默认使用映射的表名）</param>
        /// <param name="token">取消令牌</param>
        /// <returns>投影结果集合</returns>
        ValueTask<IEnumerable<TValue>> QueryAsync<TValue>(Expression<Func<T, TValue>> selector, Expression<Func<T, bool>>? filter = null, string? tableName = null, CancellationToken token = default) where TValue : class;

        /// <summary>
        /// 查询表中某列数据
        /// </summary>
        /// <typeparam name="TValue"></typeparam>
        /// <param name="columnName">列名称</param>
        /// <param name="tableName">表名称（可选）</param>
        /// <param name="filter">条件</param>
        /// <param name="token"></param>
        /// <returns></returns>
        ValueTask<List<TValue>> QueryAsync<TValue>(string columnName, Expression<Func<T, bool>>? filter = null, string? tableName = null, CancellationToken token = default);
        /// <summary>
        /// 分页查询某列或者某几列
        /// </summary>
        /// <typeparam name="TValue"></typeparam>
        /// <param name="selector">需要查询的列名集合</param>
        /// <param name="filter">过滤条件表达式（可选）</param>
        /// <param name="pageIndex">页索引</param>
        /// <param name="pageSize">每页数据量</param>
        /// <param name="orderBy">排序字段（可选）</param>
        /// <param name="tableName">表名称（可选，默认使用映射的表名）</param>
        /// <param name="token">取消令牌</param>
        /// <returns></returns>
        ValueTask<PagedResult<TValue>> QueryAsync<TValue>(Expression<Func<T, TValue>> selector, Expression<Func<T, bool>>? filter = null, int pageIndex = 0, int pageSize = 1000, string? orderBy = null, string? tableName = null, CancellationToken token = default) where TValue : class;
        /// <summary>
        /// 查询表中某列数据(分页)
        /// </summary>
        /// <typeparam name="TValue"></typeparam>
        /// <param name="columnName">列名称</param>
        /// <param name="filter">条件</param>
        /// <param name="pageIndex">页索引</param>
        /// <param name="pageSize">每页数据量</param>
        /// <param name="tableName">表名称</param>
        /// <param name="token">取消令牌</param>
        /// <returns></returns>
        ValueTask<PagedResult<TValue>> QueryAsync<TValue>(string columnName, Expression<Func<T, bool>>? filter = null, int pageIndex = 0, int pageSize = 1000, string? tableName = null, CancellationToken token = default);

        /// <summary>
        /// 分页查询
        /// </summary>
        /// <param name="expression">条件表达式</param>
        /// <param name="pageIndex">页码</param>
        /// <param name="pageSize">每页数据量</param>
        /// <param name="tableName">表名称</param>
        /// <param name="token"></param>
        /// <returns></returns>
        ValueTask<IEnumerable<T>> QueryAsync(Expression<Func<T, bool>>? filter, int pageIndex, int pageSize, string tableName = "", CancellationToken token = default);
        /// <summary>
        /// 流式查询
        /// </summary>
        /// <param name="expression">条件表达式</param>
        /// <param name="tableName">表名称</param>
        /// <param name="token"></param>
        /// <returns></returns>
        IAsyncEnumerable<T> QueryStreamAsync(Expression<Func<T, bool>>? filter, string tableName = "", CancellationToken token = default);
        /// <summary>
        /// 流式查询某列数据
        /// </summary>
        /// <param name="columnName">列名称</param>
        /// <param name="expression">条件表达式</param>
        /// <param name="tableName">表名称</param>
        /// <param name="token">取消令牌</param>
        /// <returns></returns>
        IAsyncEnumerable<TValue> QueryStreamAsync<TValue>(string columnName, Expression<Func<T, bool>>? filter, string tableName = "", CancellationToken token = default);

    }
}
