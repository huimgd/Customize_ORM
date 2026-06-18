using Customize_ORM.Enums;
using MessagePack;
using Microsoft.Extensions.Logging;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;

namespace Customize_ORM.Extensions
{
    /// <summary>
    /// 无界优先队列，若设置了内存最大数量，超出将会写入文件
    /// </summary>
    public sealed class PriorityUnboundedChannel<T, TP> : Channel<T>
    {
        private readonly PriorityQueue<T, TP> _queue;
        private readonly object _lock = new();

        public delegate TP PrioritySelector(T item);

        // 只用于 Read / TryRead（元素级别）
        private readonly SemaphoreSlim _itemsCount = new(0);

        // 只用于 WaitToReadAsync（状态级别）
        private TaskCompletionSource<bool> _itemsAvailable = new(TaskCreationOptions.RunContinuationsAsynchronously);

        // 内存最大的数量，超出将会写入文件，若为 0 表示不写入文件全部放内存
        private readonly int _maxMemoryCount = 0;

        // 是否文件模式
        private bool _isFileMode = false;
        private readonly FileUtils? _fileUtils;
        private bool _completed;
        private Exception? _completionError;
        private readonly PrioritySelector _prioritySelector;
        private readonly ILogger _logger;
        private readonly string _dirName;

        public PriorityUnboundedChannel(PrioritySelector prioritySelector, ILogger logger, IComparer<TP>? comparer = null, int maxMemoryCount = 0, string dirName = "")
        {
            _prioritySelector = prioritySelector;
            _maxMemoryCount = maxMemoryCount;
            _logger = logger;
            _dirName = dirName;
            if (maxMemoryCount > 0 && string.IsNullOrWhiteSpace(dirName))
            {
                //设定了最大内存数量但是未设置文件写入路径
                throw new ArgumentException("Must specify a directory name", nameof(dirName));
            }

            if (_maxMemoryCount > 0 && !string.IsNullOrWhiteSpace(dirName))
            {
                _fileUtils = new FileUtils(Path.Combine(FileUtils.GetTempPath(), dirName));
            }

            _queue = new PriorityQueue<T, TP>(comparer);
            Reader = new PriorityUnBoundedChannelReader(this);
            Writer = new PriorityUnBoundedChannelWriter(this);
        }

        // ======================= WRITER =======================
        private sealed class PriorityUnBoundedChannelWriter : ChannelWriter<T>
        {
            private readonly PriorityUnboundedChannel<T, TP> _parent;
            private readonly BatchProcessor<T> _batchProcessor;

            public PriorityUnBoundedChannelWriter(PriorityUnboundedChannel<T, TP> parent)
            {
                this._parent = parent;
                _batchProcessor = new BatchProcessor<T>(
                    2000,
                    10,
                    parent._logger,
                    $"PriorityUnboundedChannel-{typeof(T).Name}-{parent._dirName}",
                    ProcessBatch
                );
                _batchProcessor.StartAsync(CancellationToken.None).Wait();
            }

            private async ValueTask ProcessBatch(List<T> batch, CancellationToken token)
            {
                using MemoryStream stream = new(256 * 1024);
                await MessagePackSerializer.SerializeAsync(stream, batch, cancellationToken: token);
                await (_parent._fileUtils?.WriteToAsync(stream, FileType.Cache, batch.Count) ?? Task.CompletedTask);
                //唤醒 WaitToReadAsync
                _parent._itemsAvailable.TrySetResult(true);
                _parent._itemsCount.Release(batch.Count);
            }

            public override bool TryWrite(T item)
            {
                lock (_parent._lock)
                {
                    if (_parent._completed)
                        return false;
                    var priority = _parent._prioritySelector(item);
                    if (_parent._isFileMode)
                    {
                        _batchProcessor.EnqueueAsync(item).AsTask().Wait();
                    }
                    else
                    {
                        _parent._queue.Enqueue(item, priority);
                        //唤醒 WaitToReadAsync
                        _parent._itemsAvailable.TrySetResult(true);
                        _parent._itemsCount.Release();

                        if (!_parent._isFileMode && _parent._fileUtils != null && _parent._queue.Count >= _parent._maxMemoryCount)
                        {
                            //切换到文件模式
                            _parent._logger.LogInformation("{Method} toggle to file mode", nameof(TryWrite));
                            _parent._isFileMode = true;
                        }
                    }

                    return true;
                }
            }

            public override ValueTask WriteAsync(T item, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!TryWrite(item))
                    throw new ChannelClosedException();

                return ValueTask.CompletedTask;
            }

            public override ValueTask<bool> WaitToWriteAsync(CancellationToken cancellationToken = default) => new(!_parent._completed);

            public override bool TryComplete(Exception? error = null)
            {
                lock (_parent._lock)
                {
                    if (_parent._completed)
                        return false;

                    _parent._completed = true;
                    _parent._completionError = error;
                }

                // 唤醒 WaitToReadAsync
                _parent._itemsAvailable.TrySetResult(false);

                // 唤醒 ReadAsync（让它检查 completed）
                _parent._itemsCount.Release();

                return true;
            }
        }

        // ======================= READER =======================
        private sealed class PriorityUnBoundedChannelReader(PriorityUnboundedChannel<T, TP> parent) : ChannelReader<T>
        {
            public override bool CanCount => true;
            public override bool CanPeek => true;
            public override int Count => parent._queue.Count;

            public override bool TryRead(out T item)
            {
                if (!parent._itemsCount.Wait(0))
                {
                    item = default!;
                    return false;
                }

                lock (parent._lock)
                {
                    if (parent._queue.Count == 0 && parent._fileUtils != null)
                    {
                        //内存数据已经读取完，尝试从文件加载
                        var count = 0;
                        while (true)
                        {
                            var list = parent._fileUtils.ReadFileAsync<T>(FileType.Cache, 1, remove: true).GetAwaiter().GetResult();
                            foreach (var data in list)
                            {
                                parent._queue.Enqueue(data, parent._prioritySelector.Invoke(data));
                            }

                            count += list.Count;
                            if (list.Count == 0)
                            {
                                if (parent._queue.Count < parent._maxMemoryCount)
                                {
                                    //文件已经读取完，切换到内存模式
                                    parent._logger.LogInformation("{Method} toggle to memory mode", nameof(TryRead));
                                    parent._isFileMode = false;
                                }

                                break;
                            }

                            if (parent._queue.Count >= parent._maxMemoryCount)
                            {
                                //从文件读取的数量超出设定的内存最大值，结束读取
                                break;
                            }
                        }

                        parent._logger.LogInformation("{Method} read {Count} Items to memory", nameof(TryRead), count);
                    }

                    if (parent._queue.Count > 0)
                    {
                        item = parent._queue.Dequeue();
                        if (parent._queue.Count == 0 && parent is { _completed: false, _itemsAvailable.Task.IsCompleted: true })
                        {
                            parent._itemsAvailable = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                        }

                        return true;
                    }
                }

                item = default!;
                return false;
            }

            public override async ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken = default)
            {
                while (true)
                {
                    Task<bool> waitTask;
                    lock (parent._lock)
                    {
                        if (parent._queue.Count > 0)
                            return true;

                        if (parent._completed)
                            return false;

                        waitTask = parent._itemsAvailable.Task;
                    }

                    await waitTask.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            public override async ValueTask<T> ReadAsync(CancellationToken cancellationToken = default)
            {
                await parent._itemsCount.WaitAsync(cancellationToken).ConfigureAwait(false);

                lock (parent._lock)
                {
                    if (parent._queue.Count > 0)
                    {
                        var item = parent._queue.Dequeue();

                        if (parent._queue.Count == 0 && parent is { _completed: false, _itemsAvailable.Task.IsCompleted: true })
                        {
                            parent._itemsAvailable = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                        }

                        return item;
                    }

                    if (parent._completed)
                    {
                        if (parent._completionError != null)
                            throw parent._completionError;

                        throw new ChannelClosedException();
                    }
                }

                throw new InvalidOperationException();
            }

            public override bool TryPeek([MaybeNullWhen(false)] out T item)
            {
                lock (parent._lock)
                {
                    return parent._queue.TryPeek(out item, out _);
                }
            }
        }
    }
}
