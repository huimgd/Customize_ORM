using Npgsql;
using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq.Expressions;
using System.Text;

namespace Customize_ORM.Interfaces
{
    /// <summary>
    /// 数据库操作接口
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public interface IPostgresRepository<T> where T : class
    {
        /// <summary>
        /// 流式读取数据，适用于大数据量查询，减少内存占用
        /// </summary>
        /// <param name="connection">数据库连接</param>
        /// <param name="expression">查询条件表达式</param>
        /// <param name="batchSize">批量大小</param>
        /// <param name="token">取消令牌</param>
        /// <returns></returns>
        IAsyncEnumerable<T?> QueryStreamAsync(NpgsqlConnection connection, Expression<Func<T, bool>>? expression = null, int batchSize = 100, CancellationToken token = default);
        /// <summary>
        /// 获取数据库连接
        /// </summary>
        /// <param name="token"></param>
        /// <returns></returns>
        Task<NpgsqlConnection> GetConnectionAsync(CancellationToken token);
        /// <summary>
        /// 单条数据插入
        /// </summary>
        /// <param name="entitys"></param>
        /// <param name="token"></param>
        /// <param name="partition"></param>
        /// <returns></returns>
        Task<bool> InsertAsync(T entity, CancellationToken token, string partition = "");
        /// <summary>
        /// 获取数据数量
        /// </summary>
        /// <param name="expression"></param>
        /// <param name="token"></param>
        /// <returns></returns>
        ValueTask<long> GetCountAsync(Expression<Func<T, bool>>? expression, CancellationToken token);
        /// <summary>
        /// 执行查询返回第一条数据
        /// </summary>
        /// <typeparam name="TValue"></typeparam>
        /// <param name="sql"></param>
        /// <param name="token"></param>
        /// <param name="parameters"></param>
        /// <returns></returns>
        ValueTask<TValue?> QueryFirstOrDefaultAsync<TValue>(string sql, CancellationToken token, params DbParameter[] parameters);
        /// <summary>
        /// 执行查询返回第一条数据
        /// </summary>
        /// <typeparam name="TValue"></typeparam>
        /// <param name="sql"></param>
        /// <param name="token"></param>
        /// <param name="parameters"></param>
        /// <returns></returns>
        ValueTask<T?> QueryFirstOrDefaultAsync(Expression<Func<T, bool>>? expression = null, CancellationToken token = default);
        /// <summary>
        /// 数据实时更新，直接操作数据库
        /// </summary>
        /// <param name="entity">数据实体</param>
        /// <param name="columns">更新列</param>
        /// <param name="token">取消token</param>
        /// <param name="expression">自定义条件</param>
        /// <returns>是否更新成功</returns>
        ValueTask<bool> UpdateAsync(T entity, ReadOnlyMemory<string> columns, CancellationToken token);
        /// <summary>
        /// 数据实时更新，直接操作数据库
        /// </summary>
        /// <param name="entity">数据实体</param>
        /// <param name="token">取消token</param>
        /// <param name="expression">自定义条件</param>
        /// <returns>是否更新成功</returns>
        ValueTask<bool> UpdateAsync(T entity, Expression<Func<T, bool>> expression);
        /// <summary>
        /// 执行查询
        /// </summary>
        /// <param name="sql"></param>
        /// <param name="token"></param>
        /// <param name="parameters"></param>
        /// <returns></returns>
        ValueTask<IEnumerable<TValue>> QueryAsync<TValue>(string sql, CancellationToken token, params DbParameter[] parameters);
        /// <summary>
        /// 执行语句返回DbDataReader
        /// </summary>
        /// <param name="sql">sql语句</param>
        /// <param name="token">取消token</param>
        /// <param name="parameters">参数</param>
        /// <returns></returns> 
        Task<DbDataReader> QueryAsync(string sql, CancellationToken token, params DbParameter[] parameters);
        /// <summary>
        /// 获取对象集合
        /// </summary>
        /// <param name="expression"></param>
        /// <param name="token"></param>
        /// <returns></returns>
        ValueTask<IEnumerable<T>> QueryAsync(Expression<Func<T, bool>>? expression = null, CancellationToken token = default);
        /// <summary>
        /// 执行语句返回受影响行数
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="sql"></param>
        /// <param name="token"></param>
        /// <param name="parameters"></param>
        /// <returns></returns>
        Task<int> ExecuteNonQueryAsync(string sql, CancellationToken token, params DbParameter[] parameters);
        /// <summary>
        /// 查询并删除
        /// </summary>
        /// <param name="sql"></param>
        /// <param name="token"></param>
        /// <param name="tableName"></param>
        /// <param name="parameters"></param>
        /// <returns></returns>
        ValueTask<List<T>> QueryAndDeleteAsync(string sql, CancellationToken token, string tableName, params DbParameter[] parameters);


    }
}
