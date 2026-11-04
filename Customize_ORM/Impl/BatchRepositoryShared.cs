namespace Customize_ORM.Impl
{
    /// <summary>
    /// 批量仓储共用的纯逻辑：分组键与重试退避时间。
    /// 抽出为共享实现，避免每个数据库的批量仓储各自复制一份相同的计算。
    /// </summary>
    internal static class BatchRepositoryShared<T> where T : class
    {
        /// <summary>
        /// 批量更新的分组键：更新列集合 + 过滤条件 + 目标表。相同键的操作可以合并成一次写库。
        /// </summary>
        /// <param name="operation">批量操作</param>
        public static string GetGroupKey(Models.BatchOperation<T> operation)
        {
            ArgumentNullException.ThrowIfNull(operation);
            var columns = operation.UpdateColumns == null
                ? string.Empty
                : string.Join(",", operation.UpdateColumns.OrderBy(c => c));
            return $"{columns}|{operation.Expression ?? ""}|{operation.TableName}";
        }

        /// <summary>
        /// 计算重试次数对应的下次重试时间（指数退避：2^retryCnt 秒）。
        /// </summary>
        /// <param name="retryCount">当前重试次数</param>
        public static DateTimeOffset CalcNextRetryTime(int retryCount)
        {
            var waitSeconds = Math.Pow(2, retryCount);
            return DateTimeOffset.Now.AddSeconds(waitSeconds);
        }
    }
}
