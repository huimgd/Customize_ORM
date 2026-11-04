namespace Customize_ORM.Interfaces
{
    /// <summary>
    /// 批量插入能力扩展。
    /// </summary>
    /// <typeparam name="T">实体类型</typeparam>
    public interface IBulkInsertRepository<T> where T : class
    {
        /// <summary>
        /// 批量插入数据。
        /// </summary>
        /// <param name="entitys">数据集合</param>
        /// <param name="tableName">表名，为空则使用映射表名</param>
        /// <param name="isRetry">是否为失败重试写入</param>
        /// <returns>是否全部成功，以及已写入行数</returns>
        Task<(bool, ulong)> BulkInsertAsync(IReadOnlyList<T> entitys, string tableName = "", bool isRetry = false);
    }
}
