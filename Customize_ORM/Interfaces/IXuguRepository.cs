using System.Data.Common;
using System.Linq.Expressions;

namespace Customize_ORM.Interfaces
{
    /// <summary>
    /// 虚谷数据库操作接口，与 IPostgresRepository 能力对齐，但全部使用 ADO.NET 抽象类型签名。
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public interface IXuguRepository<T> where T : class
    {
        /// <summary>
        /// 获取数据库连接
        /// </summary>
        /// <param name="token">取消令牌</param>
        Task<DbConnection> GetConnectionAsync(CancellationToken token = default);
        /// <summary>
        /// 获取数据数量
        /// </summary>
        /// <param name="expression">条件表达式</param>
        /// <param name="token">取消令牌</param>
        ValueTask<long> GetCountAsync(Expression<Func<T, bool>>? expression, CancellationToken token);
        /// <summary>
        /// 执行查询返回第一条数据
        /// </summary>
        /// <typeparam name="TValue">结果类型</typeparam>
        /// <param name="sql">sql语句</param>
        /// <param name="token">取消令牌</param>
        /// <param name="parameters">参数</param>
        ValueTask<TValue?> QueryFirstOrDefaultAsync<TValue>(string sql, CancellationToken token, params DbParameter[] parameters);
        /// <summary>
        /// 执行查询返回第一条数据
        /// </summary>
        /// <param name="expression">条件表达式</param>
        /// <param name="token">取消令牌</param>
        ValueTask<T?> QueryFirstOrDefaultAsync(Expression<Func<T, bool>>? expression = null, CancellationToken token = default);
        /// <summary>
        /// 数据实时更新，直接操作数据库
        /// </summary>
        /// <param name="entity">数据实体</param>
        /// <param name="columns">更新列</param>
        /// <param name="token">取消令牌</param>
        ValueTask<bool> UpdateAsync(T entity, ReadOnlyMemory<string> columns, CancellationToken token);
        /// <summary>
        /// 数据实时更新，直接操作数据库
        /// </summary>
        /// <param name="entity">数据实体</param>
        /// <param name="expression">自定义条件</param>
        ValueTask<bool> UpdateAsync(T entity, Expression<Func<T, bool>> expression);
        /// <summary>
        /// 单条数据插入
        /// </summary>
        /// <param name="entity">数据实体</param>
        /// <param name="token">取消令牌</param>
        /// <param name="partition">分区后缀</param>
        Task<bool> InsertAsync(T entity, CancellationToken token, string partition = "");
        /// <summary>
        /// 执行查询
        /// </summary>
        /// <typeparam name="TValue">结果类型</typeparam>
        /// <param name="sql">sql语句</param>
        /// <param name="token">取消令牌</param>
        /// <param name="parameters">参数</param>
        ValueTask<IEnumerable<TValue>> QueryAsync<TValue>(string sql, CancellationToken token, params DbParameter[] parameters);
        /// <summary>
        /// 执行语句返回DbDataReader
        /// </summary>
        /// <param name="sql">sql语句</param>
        /// <param name="token">取消令牌</param>
        /// <param name="parameters">参数</param>
        Task<DbDataReader> QueryAsync(string sql, CancellationToken token, params DbParameter[] parameters);
        /// <summary>
        /// 获取对象集合
        /// </summary>
        /// <param name="expression">条件表达式</param>
        /// <param name="token">取消令牌</param>
        ValueTask<IEnumerable<T>> QueryAsync(Expression<Func<T, bool>>? expression = null, CancellationToken token = default);
        /// <summary>
        /// 执行语句返回受影响行数
        /// </summary>
        /// <param name="sql">sql语句</param>
        /// <param name="token">取消令牌</param>
        /// <param name="parameters">参数</param>
        Task<int> ExecuteNonQueryAsync(string sql, CancellationToken token, params DbParameter[] parameters);
        /// <summary>
        /// 查询并删除
        /// </summary>
        /// <param name="sql">sql语句</param>
        /// <param name="token">取消令牌</param>
        /// <param name="tableName">表名称</param>
        /// <param name="parameters">参数</param>
        ValueTask<List<T>> QueryAndDeleteAsync(string sql, CancellationToken token, string tableName, params DbParameter[] parameters);
        /// <summary>
        /// 流式读取数据，适用于大数据量查询，减少内存占用
        /// </summary>
        /// <param name="connection">调用方自持的数据库连接</param>
        /// <param name="expression">条件表达式</param>
        /// <param name="batchSize">预留批量大小（虚谷走服务端游标，单次拉取由驱动控制）</param>
        /// <param name="token">取消令牌</param>
        IAsyncEnumerable<T?> QueryStreamAsync(DbConnection connection, Expression<Func<T, bool>>? expression = null, int batchSize = 100, CancellationToken token = default);
    }
}
