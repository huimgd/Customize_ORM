namespace Customize_ORM.Enums
{
    /// <summary>
    /// 操作类型
    /// </summary>
    public enum OperationType : byte
    {
        None = 0,

        /// <summary>
        /// 插入操作
        /// </summary>
        Insert = 1,

        /// <summary>
        /// 更新操作
        /// </summary>
        Update = 2,

        /// <summary>
        /// 重试操作
        /// </summary>
        Retry = 3
    }
}
