using Customize_ORM.Enums;
using NLog;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;

namespace Customize_ORM.Extensions
{
    /// <summary>
    ///  混合缓存工具
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public class HybridCache<T> where T : class
    {
        private readonly Channel<T> _channel;
        private readonly ConcurrentBag<T> _list;
        private readonly string _filePath;
        private readonly int _memoryThreshold;
        private readonly int _queueThreshold;
        private volatile HybridCacheType _currentMode;
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private readonly SemaphoreSlim _readLock = new(1, 1);
        private readonly Logger _logger;
        private long _count = 0;
        /// <summary>
        /// 获取channel内数据条数
        /// </summary>
        /// <returns></returns>
        public long Count { get => _count; }
        public HybridCache(string filePath, int memoryThreshold = 1000000, int queueThreshold = 5000)
        {
            _logger = LogManager.GetCurrentClassLogger();
            _filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
            _memoryThreshold = memoryThreshold;
            _queueThreshold = queueThreshold;
            _currentMode = HybridCacheType.Memory;
            if (FileUtils.ExistenceFile(filePath))
            {
                _currentMode = HybridCacheType.File;
            }
            var options = new UnboundedChannelOptions()
            {
                SingleReader = false,
                SingleWriter = false
            };

            _channel = Channel.CreateUnbounded<T>(options);
            _list = [];
        }


        /// <summary>
        /// 写入数据
        /// </summary>
        /// <param name="item">要写入的数据项</param>
        public async ValueTask WriteAsync(T item)
        {
            await _writeLock.WaitAsync();
            Interlocked.Increment(ref _count);
            try
            {
                if (_currentMode == HybridCacheType.Memory)
                {
                    await _channel.Writer.WriteAsync(item);

                    if (_channel.Reader.Count >= _memoryThreshold)
                    {
                        _currentMode = HybridCacheType.File;
                        _logger.Info("{WriteAsync}:{obj} memory to file", nameof(WriteAsync), typeof(T).Name);
                    }
                }
                else
                {
                    _list.Add(item);

                    // 检查是否需要写到文件
                    if (_list.Count >= _queueThreshold)
                    {
                        await FileUtils.WriteBackupFileAsync(_filePath, _list, _list.Count);
                        _list.Clear();
                    }
                }
            }
            finally
            {
                _writeLock.Release();
            }
        }
        /// <summary>
        /// 保存文件
        /// </summary>
        /// <param name="item"></param>
        /// <returns></returns>
        public async ValueTask SaveFileAsync(IEnumerable<T> items, FileType fileType)
        {
            if (items.Any())
            {
                if (fileType == FileType.Errors)
                {
                    string folder = typeof(T).Name.ToString();
                    await FileUtils.WriteToAsync(_filePath, items, items.Count(), FileType.Errors, folder);
                }
                else if (fileType == FileType.Cache)
                {
                    await FileUtils.WriteBackupFileAsync(_filePath, items, items.Count());
                }
                else
                {
                    string folder = typeof(T).Name.ToString();
                    await FileUtils.WriteToAsync(_filePath, items, items.Count(), FileType.None, folder);
                }
            }
        }
        public async ValueTask SaveFileAsync(T? items, FileType fileType)
        {
            if (items != null)
            {
                if (fileType == FileType.Errors)
                {
                    string folder = typeof(T).Name.ToString();
                    await FileUtils.WriteToAsync(_filePath, new[] { items }, 1, FileType.Errors, folder);
                }
                else if (fileType == FileType.Cache)
                {
                    await FileUtils.WriteBackupFileAsync(_filePath, new[] { items }, 1);
                }
                else
                {
                    string folder = typeof(T).Name.ToString();
                    await FileUtils.WriteToAsync(_filePath, new[] { items }, 1, FileType.None, folder);
                }
            }
        }

        /// <summary>
        /// 读取失败文件
        /// </summary>
        /// <param name="items"></param>
        /// <param name="count"></param>
        /// <returns></returns>
        public async ValueTask<List<T>> ReadFileAsync(IEnumerable<T> items, int count)
        {
            string folder = typeof(T).Name.ToString();
            return await FileUtils.ReadFileAsync<T>(_filePath, FileType.Errors, count, true, folder);
        }
        /// <summary>
        /// 写入数据
        /// </summary>
        /// <param name="list"></param>
        /// <returns></returns>
        public async ValueTask WriteAsync(IEnumerable<T> list)
        {
            if (list is null || !list.Any())
                return;
            await _writeLock.WaitAsync();
            Interlocked.Add(ref _count, list.Count());
            try
            {
                if (_currentMode == HybridCacheType.Memory)
                {
                    foreach (var item in list)
                    {
                        await _channel.Writer.WriteAsync(item);
                    }

                    if (_channel.Reader.Count >= _memoryThreshold)
                    {
                        _currentMode = HybridCacheType.File;
                        _logger.Info("{WriteAsync}:{obj} memory to file", nameof(WriteAsync), typeof(T).Name);
                    }
                }
                else
                {
                    foreach (var item in list)
                    {
                        _list.Add(item);
                    }
                    ;

                    // 检查是否需要刷新到文件
                    if (_list.Count >= _queueThreshold)
                    {
                        await FileUtils.WriteBackupFileAsync(_filePath, _list, _list.Count);
                        _list.Clear();
                    }
                }
            }
            finally
            {
                _writeLock.Release();
            }
        }
        /// <summary>
        /// 读取数据
        /// </summary>
        /// <returns></returns>
        public async ValueTask<T?> ReadAsync()
        {
            await _readLock.WaitAsync();
            Interlocked.Decrement(ref _count);
            try
            {
                if (_channel.Reader.TryRead(out var item))
                {
                    return item;
                }

                if (_currentMode == HybridCacheType.File)
                {
                    var resultList = await FileUtils.ReadFileAsync<T>(_filePath, 1);
                    if (resultList.Count > 0)
                    {
                        for (int i = 1; i < resultList.Count; i++)
                        {
                            await _channel.Writer.WriteAsync(resultList[i]);
                        }
                        return resultList[0];
                    }
                    else
                    {
                        _currentMode = HybridCacheType.Memory;
                        //把中转list写入channel
                        foreach (var transfer in _list)
                        {
                            await _channel.Writer.WriteAsync(transfer);
                        }
                        _list.Clear();
                        _logger.Info("{ReadAsync}:{obj} file to memory", nameof(ReadAsync), typeof(T).Name);
                    }
                }

                return default;
            }
            finally
            {
                _readLock.Release();
            }
        }
        /// <summary>
        /// 读取数据
        /// </summary>
        /// <returns>读取的数据项</returns>
        public async ValueTask<List<T>> ReadAsync(int count)
        {
            if (count <= 0)
                throw new ArgumentOutOfRangeException("count must be greater than 0");

            await _readLock.WaitAsync();
            Interlocked.Add(ref _count, count * -1);
            try
            {
                List<T> list = new List<T>();

                if (_currentMode == HybridCacheType.Memory)
                {
                    while (list.Count < count && _channel.Reader.TryRead(out var item))
                    {
                        list.Add(item);
                    }
                }
                else
                {
                    while (list.Count < count && _channel.Reader.TryRead(out var channelItem))
                    {
                        list.Add(channelItem);
                    }

                    do
                    {
                        var resultList = await FileUtils.ReadFileAsync<T>(_filePath, 1);
                        if (resultList.Count > 0)
                        {
                            int i = 0;
                            while (list.Count < count && i < resultList.Count)
                            {
                                list.Add(resultList[i]);
                                i++;
                            }

                            for (; i < resultList.Count; i++)
                            {
                                await _channel.Writer.WriteAsync(resultList[i]);
                            }

                            if (list.Count == count)
                            {
                                break;
                            }
                        }
                        else
                        {
                            _currentMode = HybridCacheType.Memory;
                            //把中转list写入channel
                            foreach (var transfer in _list)
                            {
                                await _channel.Writer.WriteAsync(transfer);
                            }
                            _list.Clear();
                            _logger.Info("{ReadAsync}:{obj} file to memory", nameof(ReadAsync), typeof(T).Name);
                            break;
                        }
                    } while (true);
                }

                return list;
            }
            finally
            {
                _readLock.Release();
            }
        }

        /// <summary>
        /// 程序停止
        /// </summary>
        /// <returns></returns>
        public async Task StopAsync()
        {
            if (_list.Count > 0)
            {
                await FileUtils.WriteBackupFileAsync(_filePath, _list, _list.Count);
                _logger.Info("{Method} {Tag} saved list data, count ={Count} ", nameof(StopAsync), typeof(T).Name.ToString(), _list.Count);
            }
            if (_channel.Reader.Count > 0)
            {
                var count = _channel.Reader.Count;
                _logger.Info("{Method} {Tag} saving channel data, count = {Count}", nameof(StopAsync), typeof(T).Name.ToString(), count);
                var watch = Stopwatch.StartNew();
                var list = new List<T>(_queueThreshold);
                while (_channel.Reader.TryRead(out var item))
                {
                    list.Add(item);
                    if (list.Count != _queueThreshold) continue;
                    await FileUtils.WriteBackupFileAsync(_filePath, list, list.Count());
                    list.Clear();
                }

                if (list.Count > 0)
                {
                    await FileUtils.WriteBackupFileAsync(_filePath, list, list.Count());
                }
                watch.Stop();
                _logger.Info("{Method} {Tag} saved channel data, time = {Time} ms", nameof(StopAsync), typeof(T).Name.ToString(), watch.ElapsedMilliseconds);
            }
        }

    }
}
