using Customize_ORM.Enums;
using MessagePack;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading.Channels;

namespace Customize_ORM.Extensions
{
    /// <summary>
    /// Channel 扩展方法
    /// </summary>
    public static class ChannelExtension
    {
        /// <summary>
        /// 从文件加载数据到 channel
        /// </summary>
        public static async Task LoadFromFileAsync<T>(this Channel<T> channel, ILogger logger, FileUtils fileUtils, FileType fileType, string tag, CancellationToken token)
        {
            var hasData = false;
            var watch = Stopwatch.StartNew();
            var count = 0;
            try
            {
                await foreach (var stream in fileUtils.ReadAllAsync(fileType, token, pattern: Path.Combine(tag, "*"), remove: true))
                {
                    if (!hasData)
                    {
                        hasData = true;
                        logger.LogInformation("{Method} {Tag} start.", nameof(LoadFromFileAsync), tag);
                    }

                    var list = await MessagePackSerializer.DeserializeAsync<IEnumerable<T>>(stream, cancellationToken: token);
                    foreach (var item in list)
                    {
                        await channel.Writer.WriteAsync(item, token);
                        count++;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                logger.LogInformation("LoadFromFileAsync  Canceled");
            }

            watch.Stop();
            if (hasData)
            {
                logger.LogInformation("{Method} {Tag} finished. Count = {Count}, time = {Time} ms", nameof(LoadFromFileAsync), tag, count, watch.ElapsedMilliseconds);
            }
            else
            {
                logger.LogInformation("{Method} {Tag} not found Mytek.Fsmgw.Cache files", nameof(LoadFromFileAsync), tag);
            }
        }

        /// <summary>
        /// 序列化到文件
        /// </summary>
        public static async ValueTask<bool> SaveFileAsync<T>(this Channel<T> channel, ILogger logger, FileUtils fileUtils, FileType fileType, string tag)
        {
            try
            {
                var count = channel.Reader.Count;
                const int saveBatchSize = 2000;
                if (count <= 0)
                {
                    return false;
                }

                logger.LogInformation("{Method} {Tag} saving data, count = {Count}", nameof(SaveFileAsync), tag, count);
                var watch = Stopwatch.StartNew();
                var list = new List<T>(saveBatchSize);
                while (channel.Reader.TryRead(out var item))
                {
                    list.Add(item);
                    if (list.Count != saveBatchSize) continue;
                    await SaveFileAsync(fileUtils, fileType, list, tag);
                    list.Clear();
                }

                if (list.Count > 0)
                {
                    await SaveFileAsync(fileUtils, fileType, list, tag);
                }

                watch.Stop();
                logger.LogInformation("{Method} {Tag} saved data, time = {Time} ms", nameof(SaveFileAsync), tag, watch.ElapsedMilliseconds);
            }
            catch (Exception e)
            {
                logger.LogError(e, "{Method} {Tag} saved data failed", nameof(SaveFileAsync), tag);
                return false;
            }

            return true;
        }

        /// <summary>
        /// 保存数据到文件
        /// </summary>
        private static async ValueTask SaveFileAsync<T>(FileUtils fileUtils, FileType fileType, List<T> items, string tag)
        {
            // 256KB缓冲区
            using MemoryStream stream = new(256 * 1024);
            await MessagePackSerializer.SerializeAsync(stream, items);
            await fileUtils.WriteToAsync(stream, fileType, items.Count, tag);
        }
    }
}
