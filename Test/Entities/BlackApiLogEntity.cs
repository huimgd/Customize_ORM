using MessagePack;
using System.ComponentModel.DataAnnotations.Schema;

namespace Test.Entities
{
    /// <summary>
    /// 黑名单接口请求记录
    /// clickhouse
    /// </summary>
    [Table("black_api_log")]
    [MessagePackObject]
    public class BlackApiLogEntity
    {
        [Key(0)]
        public long Id { get; set; }
        /// <summary>
        ///来源
        /// </summary>
        [Key(1)]
        public int TargetType { get; set; }
        /// <summary>
        /// 账号/通道 编码
        /// </summary>
        [Key(2)]
        public int TargetCode { get; set; }
        /// <summary>
        ///黑名单id
        /// </summary>
        [Key(3)]
        public Guid BlackId { get; set; }
        /// <summary>
        ///手机号
        /// </summary>
        [Key(4)]
        public string? Mobile { get; set; }
        /// <summary>
        ///执行时间
        /// </summary>
        [Key(5)]
        public DateTime ExecutionTime { get; set; } = DateTime.Now;
        /// <summary>
        ///持续时间（毫秒）
        /// </summary>
        [Key(6)]
        public long ExecutionDuration { get; set; }

        /// <summary>
        /// 是否为黑名单
        /// </summary>
        [Key(7)]
        public bool IsBlackMobile { get; set; }
        /// <summary>
        /// 用于检测的api
        /// </summary>
        [Key(8)]
        public string? ApiCode { get; set; }
    }
}
