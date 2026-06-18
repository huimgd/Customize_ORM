using Customize_ORM.Enums;
using Customize_ORM.Models;
using MessagePack;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Customize_ORM.Extensions
{
    /// <summary>
    /// 批量处理器
    /// </summary>
    public class BatchProcessor<T> : IHostedService
    {
        /// <summary>
        /// 批处理回调，当收集到指定数量或达到指定时间时回调
        /// </summary>
        public delegate ValueTask ProcessBatchDelegate(List<T> list, CancellationToken token);

        /// <summary>
        /// 批次大小
        /// </summary>
        private int _batchSize;

        /// <summary>
        /// 一个批次的最大等待时间
        /// </summary>
        private int _batchTime;

        /// <summary>
        /// 批处理回调，当收集到指定数量或达到指定时间时回调
        /// </summary>
        private readonly ProcessBatchDelegate _processBatch;

        private readonly CancellationTokenSource _cts = new();

        private readonly Channel<T> _channel;

        private readonly ILogger _logger;
        private Task? _task;
        private static readonly HashSet<string> Tags = [];
        private static readonly object TagsLock = new();
        public string Tag { get; init; }
        private readonly FileUtils _fileUtils;
        private const string BatchProcessorName = "BatchProcessor";
        private readonly Func<T, object>? _groupKeySelector;

        /// <summary>
        /// 待处理数量
        /// </summary>
        public int Count => _channel.Reader.Count;

        /// <summary>
        /// 批处理器
        /// </summary>
        /// <param name="batchSize">批次大小</param>
        /// <param name="batchTime">最长等待时间</param>
        /// <param name="logger">日志服务</param>
        /// <param name="tag">标签，唯一值，序列化为文件时作为文件夹名称</param>
        /// <param name="processBatch">批处理回调</param>
        /// <param name="groupKeySelector">分组键选择器</param>
        /// <param name="capacity">内部最大容量，超出即异步等待</param>
        /// <exception cref="InvalidOperationException">异常信息</exception> 
        public BatchProcessor(int batchSize, int batchTime, ILogger logger, string tag, ProcessBatchDelegate processBatch, Func<T, object>? groupKeySelector = null, int capacity = 20_000)
        {
            if (batchTime <= 0 || batchSize <= 0)
            {
                throw new InvalidOperationException("BatchSize and BatchTime must be greater than zero.");
            }

            if (capacity <= 0)
            {
                throw new InvalidOperationException("capacity must be greater than zero.");
            }

            if (string.IsNullOrWhiteSpace(tag))
            {
                throw new ArgumentException($"Tag {tag} is empty.");
            }

            lock (TagsLock)
            {
                if (!Tags.Add(tag))
                {
                    throw new ArgumentException($"Tag {tag} is already in use.");
                }
            }

            ValidateTypeMessagePackAttributes();
            Tag = tag;
            _groupKeySelector = groupKeySelector;
            _channel = Channel.CreateBounded<T>(new BoundedChannelOptions(capacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
            });
            var savePath = Path.Combine(Directory.GetCurrentDirectory(), AppSettings.Current.FileSettings.TempDirectoryPath, "BatchProcessor", tag);
            _fileUtils = new FileUtils(savePath);
            _batchSize = batchSize;
            _batchTime = batchTime;
            _processBatch = processBatch ?? throw new ArgumentNullException(nameof(processBatch));
            _logger = logger;
        }

        /// <summary>
        /// 修改批处理配置
        /// </summary>
        /// <param name="batchSize">批次大小</param>
        /// <param name="batchTime">和包等待时间</param>
        /// <exception cref="InvalidOperationException"></exception>
        public void UpdateBatchConfig(int batchSize, int batchTime)
        {
            if (batchTime <= 0 || batchSize <= 0)
            {
                throw new InvalidOperationException("BatchSize and BatchTime must be greater than zero.");
            }
            _batchSize = batchSize;
            _batchTime = batchTime;
        }
        /// <summary>
        /// 校验泛型是否可被 MessagePack 序列化
        /// </summary>
        private static void ValidateTypeMessagePackAttributes()
        {
            var type = typeof(T);
            if (typeof(ITuple).IsAssignableFrom(type))
            {
                //元组类型默认支持直接跳过
                return;
            }

            var attr = type.GetCustomAttribute<MessagePackObjectAttribute>();
            if (attr == null)
            {
                throw new ArgumentException($"Type '{type.FullName}' must be marked with {nameof(MessagePackObjectAttribute)}.");
            }
        }

        public async ValueTask EnqueueAsync(T item)
        {
            await _channel.Writer.WriteAsync(item, _cts.Token);
        }

        public bool Enqueue(T item)
        {
            return _channel.Writer.TryWrite(item);
        }

        public async Task StartAsync(CancellationToken token)
        {
            await LoadFromFile(FileType.Cache, _cts.Token);
            _task = RunBatchTask(_cts.Token);
        }

        public async Task StopAsync(CancellationToken token)
        {
            try
            {
                _channel.Writer.Complete();
                await _cts.CancelAsync();
                _logger.LogInformation("{Tag} batch processor task Stopping...", Tag);
                if (_task != null)
                {
                    await _task.IgnoreCancel();
                }

                // 释放标签，允许后续重新创建相同标签的处理器
                lock (TagsLock)
                {
                    Tags.Remove(Tag);
                }

                _logger.LogInformation("{Tag} batch processor task stopped.", Tag);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "StopBatchProcessor {Tag} failed", Tag);
            }
        }

        /// <summary>
        /// 保存数据到文件
        /// </summary>
        private async ValueTask Save2File(FileType fileType, List<T> items)
        {
            // 32KB缓冲区
            using MemoryStream stream = new(32 * 1024);
            await MessagePackSerializer.SerializeAsync(stream, items);
            await _fileUtils.WriteToAsync(stream, fileType, items.Count);
        }

        /// <summary>
        /// 保存 channel 剩余数据
        /// </summary>
        private async ValueTask SaveChannelData2File(FileType fileType)
        {
            try
            {
                var count = _channel.Reader.Count;
                const int saveBatchSize = 2000;
                if (count <= 0)
                {
                    return;
                }

                _logger.LogInformation("{BatchProcessorName} {Method} {Tag} batch processor saving data, count = {Count}", BatchProcessorName, nameof(SaveChannelData2File), Tag, count);
                var watch = Stopwatch.StartNew();
                var list = new List<T>(saveBatchSize);
                while (_channel.Reader.TryRead(out var item))
                {
                    list.Add(item);
                    if (list.Count != saveBatchSize) continue;
                    await Save2File(fileType, list);
                    list.Clear();
                }

                if (list.Count > 0)
                {
                    await Save2File(fileType, list);
                }

                watch.Stop();
                _logger.LogInformation("{BatchProcessorName} {Method} {Tag} batch processor saved data, time = {Time} ms", BatchProcessorName, nameof(SaveChannelData2File), Tag, watch.ElapsedMilliseconds);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "{BatchProcessorName} {Method} {Tag} batch processor saved data failed", BatchProcessorName, nameof(SaveChannelData2File), Tag);
            }
        }

        /// <summary>
        /// 从文件加载数据到 channel
        /// </summary>
        private async ValueTask LoadFromFile(FileType fileType, CancellationToken token)
        {
            var hasData = false;
            var watch = Stopwatch.StartNew();
            await foreach (var stream in _fileUtils.ReadAllAsync(fileType, token, pattern: "*", remove: true))
            {
                if (!hasData)
                {
                    hasData = true;
                    _logger.LogInformation("{BatchProcessorName} {Method} {Tag} batch processor start.", BatchProcessorName, nameof(LoadFromFile), Tag);
                }

                var list = await MessagePackSerializer.DeserializeAsync<IEnumerable<T>>(stream, cancellationToken: token);
                foreach (var item in list)
                {
                    await _channel.Writer.WriteAsync(item, token);
                }
            }

            watch.Stop();
            if (hasData)
            {
                _logger.LogInformation("{BatchProcessorName} {Method} {Tag} batch processor finished. time = {Time} ms", BatchProcessorName, nameof(LoadFromFile), Tag, watch.ElapsedMilliseconds);
            }
            else
            {
                _logger.LogInformation("{BatchProcessorName} {Method} {Tag} batch processor not found Mytek.Fsmgw.Cache files", BatchProcessorName, nameof(LoadFromFile), Tag);
            }
        }

        #region 分组批量收集

        private async Task RunBatchTask(CancellationToken token)
        {
            var groupBuffers = new Dictionary<object, List<T>>();
            var groupTimers = new Dictionary<object, Stopwatch>();

            while (!token.IsCancellationRequested)
            {
                try
                {
                    while ((groupBuffers.Count > 0 && !token.IsCancellationRequested) || await _channel.Reader.WaitToReadAsync(token))
                    {
                        if (_channel.Reader.TryRead(out var item))
                        {
                            var key = _groupKeySelector?.Invoke(item) ?? "_default";

                            if (!groupBuffers.TryGetValue(key, out var buffer))
                            {
                                buffer = new List<T>(_batchSize);
                                groupBuffers[key] = buffer;
                                groupTimers[key] = Stopwatch.StartNew();
                            }

                            buffer.Add(item);

                            var timer = groupTimers[key];
                            if (!timer.IsRunning)
                            {
                                timer.Restart();
                            }

                            // 满批次 或 超时
                            if (buffer.Count >= _batchSize || timer.ElapsedMilliseconds >= _batchTime)
                            {
                                await ProcessGroup(key, buffer, token);
                                groupBuffers.Remove(key);
                                groupTimers.Remove(key);
                            }
                        }

                        // 扫描超时但没满批次的分组
                        foreach (var (key, buffer) in groupBuffers.ToArray())
                        {
                            var timer = groupTimers[key];

                            if (buffer.Count > 0 && timer.ElapsedMilliseconds >= _batchTime)
                            {
                                await ProcessGroup(key, buffer, token);
                                groupBuffers.Remove(key);
                                groupTimers.Remove(key);
                            }
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "{BatchProcessorName} {Method} {Tag} Exception occurred during batch processing", BatchProcessorName, nameof(RunBatchTask), Tag);
                }
            }

            // 停止时保存文件
            foreach (var kv in groupBuffers.Where(kv => kv.Value.Count > 0))
            {
                await Save2File(FileType.Cache, kv.Value);
            }

            await SaveChannelData2File(FileType.Cache);
        }


        private async Task ProcessGroup(object key, List<T> buffer, CancellationToken token)
        {
            try
            {
                token.ThrowIfCancellationRequested();
                await _processBatch(buffer, token);
            }
            catch (OperationCanceledException)
            {
                await Save2File(FileType.Cache, buffer);
                _logger.LogInformation("{Tag} group={Key} saved data count: {Count} after canceled", Tag, key, buffer.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "{Tag} group={Key} Error processing batch of {Count}", Tag, key, buffer.Count);
                await Save2File(FileType.Errors, buffer);
            }
        }

        #endregion

        #region 弃用，普通批量收集

        private async Task RunBatchTaskOld(CancellationToken token)
        {
            var buffer = new List<T>(_batchSize);

            while (!token.IsCancellationRequested)
            {
                try
                {
                    var batchWatch = new Stopwatch();
                    while (await _channel.Reader.WaitToReadAsync(token))
                    {
                        batchWatch.Restart();
                        // 填充缓冲区
                        while (buffer.Count < _batchSize && batchWatch.ElapsedMilliseconds < _batchTime)
                        {
                            if (_channel.Reader.TryRead(out var item))
                                buffer.Add(item);
                        }

                        if (buffer.Count == 0)
                        {
                            await Task.Delay(10, token);
                            continue;
                        }

                        try
                        {
                            token.ThrowIfCancellationRequested();
                            await _processBatch(buffer, token);
                        }
                        catch (OperationCanceledException)
                        {
                            //ignored
                            await Save2File(FileType.Cache, buffer);
                            _logger.LogInformation("{BatchProcessorName} {Method} {Tag} saved data count: {Count} after canceled", BatchProcessorName, nameof(RunBatchTaskOld), Tag, buffer.Count);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError("{BatchProcessorName} {Method} {Tag} Error processing batch of {Count} items Error:{ex}", BatchProcessorName, nameof(RunBatchTaskOld), Tag, buffer.Count, ex);
                            await Save2File(FileType.Errors, buffer);
                            _logger.LogInformation("{BatchProcessorName} {Method} {Tag} saved data count: {Count} after exception", BatchProcessorName, nameof(RunBatchTaskOld), Tag, buffer.Count);
                        }
                        finally
                        {
                            buffer.Clear();
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break; // 正常取消
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "{BatchProcessorName} {Method} {Tag} Exception occurred during batch processing", BatchProcessorName, nameof(RunBatchTaskOld), Tag);
                }
                finally
                {
                    // 异常时处理剩余数据
                    if (buffer.Count > 0)
                    {
                        try
                        {
                            var watch = Stopwatch.StartNew();
                            await Save2File(FileType.Errors, buffer);
                            watch.Stop();
                            _logger.LogInformation("{BatchProcessorName} {Method} {Tag} saved remaining {Count} items after error. time = {Time} ms", BatchProcessorName, nameof(RunBatchTaskOld), buffer.Count, watch.ElapsedMilliseconds, Tag);
                        }
                        catch (Exception innerEx)
                        {
                            _logger.LogError(innerEx, "{BatchProcessorName} {Method} {Tag} Failed to process remaining items after error", BatchProcessorName, nameof(RunBatchTaskOld), Tag);
                        }
                        finally
                        {
                            buffer.Clear();
                        }
                    }
                }
            }

            await SaveChannelData2File(FileType.Cache);
        }

        #endregion
    }
}
