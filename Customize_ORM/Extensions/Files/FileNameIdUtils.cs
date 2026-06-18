using IdGen;
using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using System.Text;

namespace Customize_ORM.Extensions
{
    
    /// <summary>
    /// 文件名称ID生成器
    /// </summary>
    public static class FileNameIdUtils
    {
        public static IdGenerator CreateIdGenerator()
        {
            //使用ip地址构造
            var ipAddress = NetworkUtils.GetAllLocalIPv4Addresses(true).First();
            var bytes = ipAddress.GetAddressBytes();
            // uint 确保结果是正数
            var ipValue = (uint)bytes[0] << 24 | (uint)bytes[1] << 16 | (uint)bytes[2] << 8 | bytes[3];
            //进程id
            var processId = Environment.ProcessId;
            var ipHash = (ipValue * 2654435761U) & 0x3FF; // 乘法哈希取低10位
            var processHash = ((uint)processId * 2654435761U) & 0x3FF;
            var generatorId = (int)((ipHash ^ processHash) % 1024);
            //序列号溢出则等待
            var options = new IdGeneratorOptions(sequenceOverflowStrategy: SequenceOverflowStrategy.SpinWait);
            return new IdGenerator(generatorId, options);
        }
        /// <summary>
        /// 雪花 ID 生成器
        /// </summary>
        private static readonly IdGenerator IdGenerator = CreateIdGenerator();

        /// <summary>
        /// 生成下一个 id
        /// </summary>
        public static long NextId() => IdGenerator.CreateId();

        /// <summary>
        /// 生成下一个 id（字符串）
        /// </summary>
        /// <returns></returns>
        public static string NextIdStr() => NextId().ToString();
    }
}
