using System.Data.Common;
using System.Linq.Expressions;

namespace Customize_ORM.Interfaces
{
    /// <summary>
    /// 所有数据库仓储的统一公共契约。
    /// 方法签名仅使用 ADO.NET 抽象类型（<see cref="DbConnection"/> / <see cref="DbParameter"/> / <see cref="DbDataReader"/>），
    /// 不暴露任何驱动专属类型，各数据库实现按自身语义落地。
    /// </summary>
    /// <typeparam name="T">实体类型</typeparam>
    public interface IRepository<T> where T : class
    {
        /// <summary>
        /// 获取当前数据库连接（已打开，由调用方负责释放）。
        /// </summary>
        /// <param name="token">取消令牌</param>
        Task<DbConnection> GetConnectionAsync(CancellationToken token = default);

        /// <summary>
        /// 获取满足条件的数据数量。
        /// </summary>
        /// <param name="expression">条件表达式，为空表示全表统计</param>
        /// <param name="token">取消令牌</param>
        ValueTask<long> GetCountAsync(Expression<Func<T, bool>>? expression, CancellationToken token);

        /// <summary>
        /// 执行查询并返回第一条结果。
        /// </summary>
        /// <typeparam name="TValue">结果类型</typeparam>
        /// <param name="sql">SQL 语句</param>
        /// <param name="token">取消令牌</param>
        /// <param name="parameters">参数集合</param>
        ValueTask<TValue?> QueryFirstOrDefaultAsync<TValue>(string sql, CancellationToken token, params DbParameter[] parameters);

        /// <summary>
        /// 按条件查询并返回第一条实体。
        /// </summary>
        /// <param name="expression">条件表达式</param>
        /// <param name="token">取消令牌</param>
        ValueTask<T?> QueryFirstOrDefaultAsync(Expression<Func<T, bool>>? expression = null, CancellationToken token = default);

        /// <summary>
        /// 执行查询并返回结果集合。
        /// </summary>
        /// <typeparam name="TValue">结果类型</typeparam>
        /// <param name="sql">SQL 语句</param>
        /// <param name="token">取消令牌</param>
        /// <param name="parameters">参数集合</param>
        ValueTask<IEnumerable<TValue>> QueryAsync<TValue>(string sql, CancellationToken token, params DbParameter[] parameters);

        /// <summary>
        /// 执行查询并返回未释放的数据读取器，读取器释放时一并释放连接。
        /// </summary>
        /// <param name="sql">SQL 语句</param>
        /// <param name="token">取消令牌</param>
        /// <param name="parameters">参数集合</param>
        Task<DbDataReader> QueryAsync(string sql, CancellationToken token, params DbParameter[] parameters);

        /// <summary>
        /// 按条件查询实体集合。
        /// </summary>
        /// <param name="expression">条件表达式，为空表示全表查询</param>
        /// <param name="token">取消令牌</param>
        ValueTask<IEnumerable<T>> QueryAsync(Expression<Func<T, bool>>? expression = null, CancellationToken token = default);

        /// <summary>
        /// 流式读取数据，适用于大数据量查询。
        /// </summary>
        /// <param name="connection">调用方自持的数据库连接</param>
        /// <param name="expression">条件表达式</param>
        /// <param name="batchSize">
        /// 建议的每批读取行数（小于等于 0 时按默认值 100 处理）。
        /// 这是性能提示而非强制约束：支持服务端游标的实现（如 PostgreSQL）严格遵守，
        /// 由驱动自身控制拉取节奏的实现（如虚谷、ClickHouse）可忽略。
        /// </param>
        /// <param name="token">取消令牌</param>
        IAsyncEnumerable<T?> QueryStreamAsync(DbConnection connection, Expression<Func<T, bool>>? expression = null, int batchSize = 100, CancellationToken token = default);

        /// <summary>
        /// 执行语句并返回受影响行数。
        /// </summary>
        /// <param name="sql">SQL 语句</param>
        /// <param name="token">取消令牌</param>
        /// <param name="parameters">参数集合</param>
        Task<int> ExecuteNonQueryAsync(string sql, CancellationToken token, params DbParameter[] parameters);

        /// <summary>
        /// 查询并删除：先按给定 SQL 查询实体，再按主键批量删除，整个过程在事务内完成。
        /// </summary>
        /// <param name="sql">查询语句</param>
        /// <param name="token">取消令牌</param>
        /// <param name="tableName">删除语句的目标表名</param>
        /// <param name="parameters">查询参数</param>
        ValueTask<List<T>> QueryAndDeleteAsync(string sql, CancellationToken token, string tableName, params DbParameter[] parameters);

        /// <summary>
        /// 单条数据插入。
        /// </summary>
        /// <param name="entity">数据实体</param>
        /// <param name="token">取消令牌</param>
        /// <param name="partition">分区后缀；不支持分区表的实现忽略该参数</param>
        Task<bool> InsertAsync(T entity, CancellationToken token, string partition = "");

        /// <summary>
        /// 单行更新：按主键定位，更新指定列。
        /// </summary>
        /// <param name="entity">数据实体</param>
        /// <param name="columns">需要更新的列</param>
        /// <param name="token">取消令牌</param>
        ValueTask<bool> UpdateAsync(T entity, ReadOnlyMemory<string> columns, CancellationToken token);

        /// <summary>
        /// 单行更新：按自定义条件更新实体全部可更新列。
        /// </summary>
        /// <param name="entity">数据实体</param>
        /// <param name="expression">自定义更新条件</param>
        ValueTask<bool> UpdateAsync(T entity, Expression<Func<T, bool>> expression);
    }
}
