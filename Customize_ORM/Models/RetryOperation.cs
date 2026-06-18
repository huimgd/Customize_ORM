using MessagePack;

namespace Customize_ORM.Models
{

    /// <summary>
    /// 重试数据对象
    /// </summary>
    /// <typeparam name="T"></typeparam>
    [MessagePackObject]
    public class RetryOperation<T> where T : class
    {
        /// <summary>
        /// 数据实体
        /// </summary>
        [Key(0)]
        public required IReadOnlyList<T> Entitys { get; set; }

        /// <summary>
        /// 更新列
        /// </summary>
        [Key(1)]
        public string[] UpdateColumns { get; set; } = [];

        /// <summary>
        /// 表名称
        /// </summary>
        [Key(2)]
        public string TableName { get; set; } = string.Empty;

        /// <summary>
        /// 自定义条件
        /// </summary>
        [Key(3)]
        public string? Expression { get; set; } = string.Empty;

        /// <summary>
        /// 重试次数
        /// </summary>
        [Key(4)]
        public int RetryCount { get; set; }

        /// <summary>
        /// 下次重试时间
        /// </summary>
        [Key(5)]
        public DateTimeOffset RetryTime { get; set; }

    }
}
