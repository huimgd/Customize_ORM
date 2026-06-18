namespace Customize_ORM.Enums
{
    /// <summary>
    /// 写入文件类型
    /// </summary>
    public enum FileType : byte
    {
        None = 0,

        /// <summary>
        /// 溢出文件
        /// </summary>
        Overflow = 1,

        /// <summary>
        /// 错误文件
        /// </summary>
        Errors = 2,

        /// <summary>
        /// 缓存文件
        /// </summary>
        Cache = 3,
    }
}
