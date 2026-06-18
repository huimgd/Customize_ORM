using MessagePack;

namespace Customize_ORM.Models
{
    /// <summary>
    /// 批处理实体
    /// </summary>
    /// <typeparam name="T"></typeparam>
    [MessagePackObject]
    public class BatchOperation<T> where T : class
    {
        /// <summary>
        /// 数据实体
        /// </summary>
        [Key(0)]
        public required T Entity { get; set; }

        /// <summary>
        /// 更新列
        /// </summary>
        [Key(1)]
        public IEnumerable<string> UpdateColumns { get; set; } = [];

        /// <summary>
        /// 表名称
        /// </summary>
        [Key(2)]
        public string TableName { get; set; } = string.Empty;

        /// <summary>
        /// 自定义条件
        /// </summary>
        [Key(3)]
        public string Expression { get; set; } = string.Empty;
        /// <summary>
        ///时间
        /// </summary>
        [Key(4)]
        public long TimeTicks { get; set; } = DateTimeOffset.Now.Ticks;

    }
}
