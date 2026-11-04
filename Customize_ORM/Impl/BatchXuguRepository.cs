using Customize_ORM.Enums;
using Customize_ORM.Extensions;
using Customize_ORM.Extensions.Sql;
using Customize_ORM.Interfaces;
using Customize_ORM.Models;
using MessagePack;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using System.Linq.Expressions;
using System.Threading.Channels;

namespace Customize_ORM.Impl
{
    /// <summary>
    /// 虚谷数据库批量操作类：双通道 HybridCache + 内存重试 + 文件重试骨架与 PostgreSQL 批量仓储一致；
    /// 写入路径以分批多值 INSERT 承载，批量更新以 staging/TEMPORARY 表 UPDATE FROM 承载（虚谷无 COPY/CTE DML）。
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public class BatchXuguRepository<T> : XuguRepository<T>, IBatchUpdateRepository<T>, IBulkInsertRepository<T> where T : class
    {
        private const int UpdateBatchSize = 10000;
        private const int MaxRetryCount = 8;
        private const int LargeBatchThreshold = 5000;
        private readonly HybridCache<BatchOperation<T>> _updateChannel;
        private readonly Channel<RetryOperation<T>> _retryChannel;
        private readonly string _backupFilePath;
        private readonly Task _updateTask;
        private readonly Task _retryTask;
        private readonly FileUtils _fileUtils;
        private readonly CancellationTokenSource _cts;
        private readonly string _stagingTableName;
        private bool _stagingTableChecked = false;
        private readonly TimeSpan _fileRetryInterval = TimeSpan.FromMinutes(10);
        private static readonly ConcurrentDictionary<string, Func<T, object[]>> _rowFactoryCache = new();
        private static readonly SemaphoreSlim _stagingTableLock = new(1, 1);
        public BatchXuguRepository(IDbConnectionFactory xugu, ILogger<XuguRepository<T>> logger) : base(xugu, logger)
        {
            _stagingTableName = $"staging_{_tableName}";
            _backupFilePath = Path.Combine(Directory.GetCurrentDirectory(), AppSettings.Current.FileSettings.TempDirectoryPath, "XuguRepository", typeof(T).Name);
            _fileUtils = new FileUtils(_backupFilePath);
            _updateChannel = new HybridCache<BatchOperation<T>>(Path.Combine(_backupFilePath, "UpdateChannel"));
            _retryChannel = new PriorityUnboundedChannel<RetryOperation<T>, DateTimeOffset>(prioritySelector: t => t.RetryTime, logger);
            Task.Run(async () => { await _retryChannel.LoadFromFileAsync(_logger, _fileUtils, FileType.Cache, "Retry", CancellationToken.None); });
            _cts = new CancellationTokenSource();
            _updateTask = UpdateProcessingAsync(_cts.Token);
            _retryTask = RetryProcessingAsync(_cts.Token);
            _ = Task.Run(() => FileRetry(_cts.Token));
        }
        /// <summary>
        /// 批量更新(非实时)
        /// </summary>
        /// <param name="entity">实体</param>
        /// <param name="columns">更新列</param>
        /// <param name="token">取消令牌</param>
        /// <param name="expression">条件表达式</param>
        /// <param name="partition">分区信息</param>
        /// <returns></returns>
        public async ValueTask<bool> BulkUpdateAsync(T entity, IEnumerable<string> columns, CancellationToken token, Expression<Func<T, bool>>? expression = null, string partition = "")
        {
            try
            {
                ArgumentNullException.ThrowIfNull(entity, nameof(entity));
                string tableName = string.Empty;
                if (!string.IsNullOrWhiteSpace(partition))
                {
                    tableName = _tableName + "_" + partition;
                }
                else
                {
                    tableName = GetPartition(entity);
                }
                string expressionStr = string.Empty;
                if (expression != null)
                {
                    // 表达式字符串用于承载进批量操作对象；走虚谷方言生成与回填一致
                    var sqlExpr = new SqlExpression<T>(_mapping, DBType.Xugu).WithTableAlias(tableName);
                    sqlExpr.Where(expression);
                    expressionStr = sqlExpr.BuildWhereStr();
                }
                var operation = new BatchOperation<T>
                {
                    Entity = entity,
                    UpdateColumns = columns,
                    TableName = tableName,
                    Expression = expressionStr
                };
                await _updateChannel.WriteAsync(operation);
                return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "{fnName} {entityName}", nameof(BulkUpdateAsync), typeof(T).Name);
            }
            return false;
        }

        /// <summary>
        /// 批量重试
        /// </summary>
        /// <param name="operation">重试数据</param>
        /// <param name="token">取消令牌</param>
        /// <returns></returns>
        public async ValueTask<bool> RetryUpdateAsync(RetryOperation<T> operation, CancellationToken token)
        {
            try
            {
                ArgumentNullException.ThrowIfNull(operation, nameof(operation));
                await _retryChannel.Writer.WriteAsync(operation, token);
                return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "{fnName} {entityName}", nameof(RetryUpdateAsync), typeof(T).Name);
            }
            return false;
        }

        /// <summary>
        /// 批量插入：虚谷无 COPY，按 MaxRowsPerInsert 分批组装多值 INSERT 并在同一事务提交，保证整批一致
        /// </summary>
        /// <param name="entitys">实体集合</param>
        /// <param name="tableName">表名</param>
        /// <param name="isRetry">是否是文件重试</param>
        /// <returns></returns>
        public async Task<(bool, ulong)> BulkInsertAsync(IReadOnlyList<T> entitys, string tableName = "", bool isRetry = false)
        {
            string sql = string.Empty;
            try
            {
                if (entitys.Count == 0)
                {
                    return (false, 0);
                }
                Stopwatch sw = Stopwatch.StartNew();
                ArgumentNullException.ThrowIfNull(entitys, nameof(entitys));
                if (string.IsNullOrWhiteSpace(tableName))
                {
                    tableName = _tableName;
                }
                var queryBuilder = new XuguQuerySqlBuilder<T>(_mapping, tableName);
                var columns = _updateMapping.AllColumns;
                using var connection = await _connectionFactory.OpenConnectionAsync(CancellationToken.None);
                ArgumentNullException.ThrowIfNull(connection, nameof(connection));
                await using var transaction = await connection.BeginTransactionAsync(CancellationToken.None);
                var rowFactory = GetRowFactoryForColumns(columns);
                for (var offset = 0; offset < entitys.Count; offset += XuguQuerySqlBuilder<T>.MaxRowsPerInsert)
                {
                    var chunkCount = Math.Min(XuguQuerySqlBuilder<T>.MaxRowsPerInsert, entitys.Count - offset);
                    sql = queryBuilder.BuildNamedInsertSql(columns, chunkCount);
                    var parameters = BuildChunkInsertParameters(columns, rowFactory, entitys, offset, chunkCount, includeBatchId: false, batchId: null);
                    await DbCommandExecution.ExecuteNonQueryAsync(connection, sql, parameters, CancellationToken.None, transaction);
                }
                await transaction.CommitAsync(CancellationToken.None);
                _logger.LogInformation("{funName} {entityName}: DataCount{dataCount},Times{time}ms", nameof(BulkInsertAsync), typeof(T).Name, entitys.Count, sw.ElapsedMilliseconds);
                return (true, (ulong)entitys.Count);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "{fnName} {entityName} sql=>{sqlstr}", nameof(BulkInsertAsync), typeof(T).Name, sql);
                if (!isRetry)
                {
                    // 插入失败数据落地由文件重试通道接管；虚谷唯一键约束错误码未确认，失败一律走重试路径
                    using MemoryStream stream = new(256 * 1024);
                    await MessagePackSerializer.SerializeAsync(stream, entitys);
                    await _fileUtils.WriteToAsync(stream, FileType.Errors, entitys.Count, OperationType.Insert.ToString());
                }
            }
            return (false, 0);
        }

        #region 程序停止
        /// <summary>
        /// 程序停止
        /// </summary>
        /// <param name="timeout">超时时间</param>
        /// <returns></returns>
        public async ValueTask StopAsync(TimeSpan timeout = default)
        {
            try
            {
                if (timeout == default)
                {
                    timeout = TimeSpan.FromSeconds(5 * 60);
                }
                await _cts.CancelAsync();
                await _retryTask.WaitAsync(timeout);
                await _updateTask.WaitAsync(timeout);
                await _updateChannel.StopAsync();
            }
            catch (TimeoutException ex)
            {
                _logger.LogError(ex, $"{nameof(StopAsync)} Shutdown timeout, forcing exit");
            }
            catch (OperationCanceledException)
            {
                // 正常取消
                _logger.LogInformation("StopAsync Task Canceled");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"{nameof(StopAsync)}");
            }
        }
        #endregion

        #region 批量更新方法

        #region staging 表方案更新（大批量）

        /// <summary>
        /// 程序首次执行更新时创建持久化 staging 表；虚谷无 UNLOGGED 语义，且 IF NOT EXISTS 支持待真实库冒烟确认
        /// </summary>
        private async Task EnsureStagingTableExistsAsync()
        {
            if (_stagingTableChecked) return;
            await _stagingTableLock.WaitAsync();
            try
            {
                if (_stagingTableChecked) return;
                var bulkSqlBuilder = new XuguBulkUpdateSqlBuilder<T>(_updateMapping);
                var createSql = bulkSqlBuilder.BuildCreateStagingTableSql(_stagingTableName);
                await using var conn = await _connectionFactory.OpenConnectionAsync(CancellationToken.None);
                await DbCommandExecution.ExecuteNonQueryAsync(conn, createSql, null, CancellationToken.None);
                _stagingTableChecked = true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Create StagingTable Exception TableName:{name}", _stagingTableName);
            }
            finally
            {
                _stagingTableLock.Release();
            }
        }

        /// <summary>
        /// Staging 策略：事务外写入 Staging 表，事务内仅执行 UPDATE FROM staging（极短）
        /// </summary>
        private async Task StagingStrategyAsync(T[] entitys, string[] columns, string? expressionStr, string tableName, int retryCount, CancellationToken token)
        {
            Stopwatch sw = Stopwatch.StartNew();
            var bulkSqlBuilder = new XuguBulkUpdateSqlBuilder<T>(_updateMapping);
            var lazyRetry = new Lazy<RetryOperation<T>>(() => new RetryOperation<T>
            {
                Entitys = entitys,
                UpdateColumns = columns,
                TableName = tableName,
                Expression = expressionStr,
                RetryCount = retryCount + 1,
                RetryTime = CalcNextRetryTime(retryCount + 1)
            }, LazyThreadSafetyMode.ExecutionAndPublication);
            var batchId = Guid.NewGuid();
            // 去重防串：建表/灌数列清单与实体 getter 保持一一对应
            var stagingCandidates = new List<string>(columns.Length + 2) { _updateMapping.Key, _updateMapping.PartitionKey };
            stagingCandidates.AddRange(columns);
            string[] allColumns = [.. stagingCandidates.Where(c => !string.IsNullOrWhiteSpace(c)).Distinct(StringComparer.OrdinalIgnoreCase)];
            // 写入 staging 表（事务外，AUTO_COMMIT=on 下分批多值 INSERT 各自生效；批次数据以 batch_id 隔离）
            await using (var stagingConn = await _connectionFactory.OpenConnectionAsync(token))
            {
                var rowFactory = GetRowFactoryForColumns(allColumns);
                var effectiveColumns = EffectiveColumns(allColumns, includeBatchId: true);
                for (var offset = 0; offset < entitys.Length; offset += XuguQuerySqlBuilder<T>.MaxRowsPerInsert)
                {
                    var chunkCount = Math.Min(XuguQuerySqlBuilder<T>.MaxRowsPerInsert, entitys.Length - offset);
                    var insertSql = $"INSERT INTO {_stagingTableName} ({bulkSqlBuilder.BuildInsertColumnList(effectiveColumns)}) VALUES {BuildNamedValuesRows(effectiveColumns, chunkCount)}";
                    var parameters = BuildChunkInsertParameters(allColumns, rowFactory, entitys, offset, chunkCount, includeBatchId: true, batchId: batchId);
                    await DbCommandExecution.ExecuteNonQueryAsync(stagingConn, insertSql, parameters, token);
                }
            }
            // 步骤2：极短事务执行 UPDATE FROM staging
            var updateSql = bulkSqlBuilder.BuildUpdateFromSourceSql(
                targetTableName: tableName,
                sourceTableName: _stagingTableName,
                sourceAlias: "staging",
                updateColumns: columns,
                additionalWhereClause: expressionStr,
                sourcePredicate: "staging.batch_id = :batchId");
            updateSql = AppendReturningIfRetry(updateSql, tableName, retryCount);
            await using var connection = await _connectionFactory.OpenConnectionAsync(token);
            await using var transaction = await connection.BeginTransactionAsync(token);
            try
            {
                await ExecuteBatchUpdateAsync(connection, transaction, updateSql,
                    cmd => cmd.Parameters.Add(XuguParameterFactory.Create(":batchId", batchId.ToString(), typeof(string))),
                    entitys, columns, expressionStr, tableName, retryCount, nameof(StagingStrategyAsync),
                    sw, lazyRetry, token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                //虚谷类型化异常与瞬时错误码体系未确认，统一按可重试错误处理，落地重试通道
                await SafeRollbackAsync(transaction, token);
                _logger.LogError(ex, "{funName} {typeName} DataCount: {dataCount}", nameof(StagingStrategyAsync), typeof(T).Name, entitys.Length);
                if (retryCount > 0)
                {
                    //重试过程中的异常
                    using MemoryStream stream = new(256 * 1024);
                    await MessagePackSerializer.SerializeAsync(stream, lazyRetry.Value, cancellationToken: token);
                    await _fileUtils.WriteToAsync(stream, FileType.Errors, entitys.Length, OperationType.Retry.ToString());
                }
                else
                {
                    //初次更新过程中的异常：数据交由重试通道按指数退避续跑
                    await _retryChannel.Writer.WriteAsync(lazyRetry.Value, token);
                }
            }
            finally
            {
                // 异步清理本次批次的数据（避免表膨胀）
                await CleanupStagingBatchAsync(batchId);
            }
        }

        /// <summary>
        /// 异步清理指定批次的 Staging 数据
        /// </summary>
        private async Task CleanupStagingBatchAsync(Guid batchId)
        {
            try
            {
                await using var conn = await _connectionFactory.OpenConnectionAsync();
                var sql = $"DELETE FROM {_stagingTableName} WHERE batch_id = :batchId";
                await DbCommandExecution.ExecuteNonQueryAsync(
                    conn,
                    sql,
                    [XuguParameterFactory.Create(":batchId", batchId.ToString(), typeof(string))],
                    CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to clean staging batch {BatchId} for {Table}", batchId, _stagingTableName);
            }
        }

        #endregion

        #region 临时表方案更新（复杂性兜底）

        public async Task TemporaryTableStrategyAsync(T[] entitys, string[] columns, string? expressionStr, string tableName, int retryCount, CancellationToken token)
        {
            Stopwatch sw = Stopwatch.StartNew();
            string tmp = $"tmp_{Guid.NewGuid():N}";
            var bulkSqlBuilder = new XuguBulkUpdateSqlBuilder<T>(_updateMapping);
            // 去重防串：建表/灌数列清单与实体 getter 保持一一对应
            var tmpCandidates = new List<string>(columns.Length + 2) { _updateMapping.Key, _updateMapping.PartitionKey };
            tmpCandidates.AddRange(columns);
            string[] allColumns = [.. tmpCandidates.Where(c => !string.IsNullOrWhiteSpace(c)).Distinct(StringComparer.OrdinalIgnoreCase)];
            var lazyRetry = new Lazy<RetryOperation<T>>(() => new RetryOperation<T>
            {
                Entitys = entitys,
                UpdateColumns = columns,
                TableName = tableName,
                Expression = expressionStr,
                RetryCount = retryCount + 1,
                RetryTime = CalcNextRetryTime(retryCount + 1)
            }, LazyThreadSafetyMode.ExecutionAndPublication);
            await using var connection = await _connectionFactory.OpenConnectionAsync(token);
            await using var transaction = await connection.BeginTransactionAsync(token);
            try
            {
                // 临时表生命期由事务边界保证：事务内建表、灌数、UPDATE、提交，连接销毁即清空
                var createSql = bulkSqlBuilder.BuildCreateTemporaryTableSql(tmp, allColumns);
                await DbCommandExecution.ExecuteNonQueryAsync(connection, createSql, null, token, transaction);
                var rowFactory = GetRowFactoryForColumns(allColumns);
                var effectiveColumns = EffectiveColumns(allColumns, includeBatchId: false);
                for (var offset = 0; offset < entitys.Length; offset += XuguQuerySqlBuilder<T>.MaxRowsPerInsert)
                {
                    var chunkCount = Math.Min(XuguQuerySqlBuilder<T>.MaxRowsPerInsert, entitys.Length - offset);
                    var insertSql = $"INSERT INTO {tmp} ({bulkSqlBuilder.BuildInsertColumnList(effectiveColumns)}) VALUES {BuildNamedValuesRows(effectiveColumns, chunkCount)}";
                    var parameters = BuildChunkInsertParameters(allColumns, rowFactory, entitys, offset, chunkCount, includeBatchId: false, batchId: null);
                    await DbCommandExecution.ExecuteNonQueryAsync(connection, insertSql, parameters, token, transaction);
                }
                string updateSql = bulkSqlBuilder.BuildLegacyTemporaryUpdateSql(
                    targetTableName: tableName,
                    sourceTableName: tmp,
                    updateColumns: columns,
                    additionalWhereClause: expressionStr);
                updateSql = AppendReturningIfRetry(updateSql, tableName, retryCount);
                await ExecuteBatchUpdateAsync(connection, transaction, updateSql,
                    _ => { }, entitys, columns, expressionStr, tableName, retryCount,
                    nameof(TemporaryTableStrategyAsync), sw, lazyRetry, token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                //虚谷类型化异常与瞬时错误码体系未确认，统一按可重试错误处理
                await SafeRollbackAsync(transaction, token);
                _logger.LogError(ex, "{funName} {typeName} DataCount: {dataCount}", nameof(TemporaryTableStrategyAsync), typeof(T).Name, entitys.Length);
                if (retryCount > 0)
                {
                    //重试过程中的异常
                    using MemoryStream stream = new(256 * 1024);
                    await MessagePackSerializer.SerializeAsync(stream, lazyRetry.Value, cancellationToken: token);
                    await _fileUtils.WriteToAsync(stream, FileType.Errors, entitys.Length, OperationType.Retry.ToString());
                }
                else
                {
                    //初次更新过程中的异常：数据交由重试通道按指数退避续跑
                    await _retryChannel.Writer.WriteAsync(lazyRetry.Value, token);
                }
            }
        }
        #endregion

        #endregion

        #region 多线程更新 
        private async Task UpdateProcessingAsync(CancellationToken token)
        {
            IReadOnlyList<BatchOperation<T>> list = new List<BatchOperation<T>>(UpdateBatchSize);
            // 用于记录已成功处理的分组，避免在取消时重复保存未完成的操作
            var completedGroups = new ConcurrentHashSet<string>();
            try
            {
                using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
                var options = new ParallelOptions
                {
                    MaxDegreeOfParallelism = 5,
                    CancellationToken = token,
                };
                while (!token.IsCancellationRequested)
                {
                    list = await _updateChannel.ReadAsync(UpdateBatchSize);
                    if (list.Count > 0)
                    {
                        var updateLookup = list.ToLookup(x => new
                        {
                            UpdateColumns = x.UpdateColumns != null
                        ? string.Join(",", x.UpdateColumns.OrderBy(c => c))
                        : "",
                            x.Expression,
                            x.TableName
                        });
                        await Parallel.ForEachAsync(updateLookup, options, async (updateGroup, ct) =>
                        {
                            var operationInfo = updateGroup.First();
                            var groupKey = GetGroupKey(operationInfo);
                            var entities = updateGroup.Select(t => t.Entity).ToArray();
                            var updateGroupColumns = operationInfo.UpdateColumns?.ToArray() ?? [];
                            var updateGroupExpression = operationInfo.Expression;
                            var updateGroupTableName = operationInfo.TableName;
                            if (string.IsNullOrWhiteSpace(updateGroupTableName))
                            {
                                updateGroupTableName = GetPartition(entities[0]);
                            }
                            //根据数据量选择更新策略
                            if (entities.Length > LargeBatchThreshold)
                            {
                                await StagingStrategyAsync(entities, updateGroupColumns, updateGroupExpression, updateGroupTableName, 0, ct);
                            }
                            else
                            {
                                await TemporaryTableStrategyAsync(entities, updateGroupColumns, updateGroupExpression, updateGroupTableName, 0, ct);
                            }
                            // 成功后标记
                            completedGroups.Add(groupKey);
                        });
                        completedGroups.Clear();
                    }
                    else
                    {
                        await timer.WaitForNextTickAsync(token);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                if (list.Count > 0)
                {
                    var remaining = list.Where(op => !completedGroups.Contains(GetGroupKey(op))).ToList();
                    if (remaining.Count > 0)
                    {
                        await _updateChannel.SaveFileAsync(remaining, FileType.Cache);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "{funcName} {name} is being cancelled.", nameof(UpdateProcessingAsync), typeof(T).Name);
                await _updateChannel.SaveFileAsync(list, FileType.Errors);
            }
        }
        /// <summary>
        /// 获取分组key
        /// </summary>
        /// <param name="op">批量操作</param>
        /// <returns></returns>
        private static string GetGroupKey(BatchOperation<T> op) => BatchRepositoryShared<T>.GetGroupKey(op);
        #endregion

        #region 内存重试 
        /// <summary>
        /// 重试channel
        /// </summary>
        /// <param name="token">取消令牌</param>
        private async Task RetryProcessingAsync(CancellationToken token)
        {
            try
            {
                await foreach (var retryData in _retryChannel.Reader.ReadAllAsync(token))
                {
                    try
                    {
                        var timePassed = (retryData.RetryTime - DateTimeOffset.Now).TotalMilliseconds;
                        if (timePassed > 0)
                        {
                            var waitTime = TimeSpan.FromMilliseconds(timePassed);
                            await Task.Delay(waitTime, token);
                        }
                        if (retryData.RetryCount >= MaxRetryCount)
                        {
                            using MemoryStream stream = new(256 * 1024);
                            await MessagePackSerializer.SerializeAsync(stream, retryData, cancellationToken: token);
                            await _fileUtils.WriteToAsync(stream, FileType.Errors, retryData.Entitys?.Count ?? 0, OperationType.Retry.ToString());
                            _logger.LogWarning("{typeName} Retry Limit Exceeded Save To File,DataCount:{retryCount}", typeof(T).Name, retryData.Entitys?.Count);
                        }
                        else
                        {
                            var data = retryData.Entitys?.ToArray() ?? [];
                            //根据数据量选择更新策略
                            if (data.Length > LargeBatchThreshold)
                            {
                                await StagingStrategyAsync(data, retryData.UpdateColumns, retryData.Expression, retryData.TableName, retryData.RetryCount, token);
                            }
                            else
                            {
                                await TemporaryTableStrategyAsync(data, retryData.UpdateColumns, retryData.Expression, retryData.TableName, retryData.RetryCount, token);
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        if (retryData != null)
                        {
                            await _retryChannel.Writer.WriteAsync(retryData, cancellationToken: CancellationToken.None);
                        }
                        throw;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                await _retryChannel.SaveFileAsync(_logger, _fileUtils, FileType.Cache, "Retry");
                _logger.LogInformation("{funcName} {typeName} is being cancelled.", nameof(RetryProcessingAsync), typeof(T).Name);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "{funcName} {typeName} Exception", nameof(RetryProcessingAsync), typeof(T).Name);
                await _retryChannel.SaveFileAsync(_logger, _fileUtils, FileType.Cache, "Retry");
            }
        }
        /// <summary>
        /// 计算重试次数对应的下次重试时间
        /// </summary>
        private static DateTimeOffset CalcNextRetryTime(int retryCnt)
        {
            return BatchRepositoryShared<T>.CalcNextRetryTime(retryCnt);
        }
        #endregion

        #region 文件重试
        private async Task FileRetry(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    bool anyFileProcessed = false;

                    //重试插入失败的文件
                    await foreach (var (filePath, stream) in _fileUtils.ReadAllWithPathAsync(FileType.Errors, token, OperationType.Insert.ToString()))
                    {
                        anyFileProcessed = true;
                        try
                        {
                            if (stream != null)
                            {
                                using (stream)
                                {
                                    var list = await MessagePackSerializer.DeserializeAsync<IReadOnlyList<T>>(stream, cancellationToken: token);
                                    if (list?.Any() == true)
                                    {
                                        var (success, _) = await BulkInsertAsync(list, _tableName, true);
                                        if (success)
                                        {
                                            _fileUtils.RemoveFile(filePath);
                                            _logger.LogInformation("Insert retry succeeded for file: {filePath}", filePath);
                                        }
                                        else
                                        {
                                            // 不删除文件，保留以便下次重试
                                            _logger.LogWarning("Insert retry failed for file: {filePath}, will retry later", filePath);
                                        }
                                    }
                                    else
                                    {
                                        // 空文件直接删除
                                        _fileUtils.RemoveFile(filePath);
                                        _logger.LogInformation("Empty insert error file removed: {filePath}", filePath);
                                    }
                                }
                            }
                            else
                            {
                                _logger.LogWarning("Stream is null for file: {filePath}", filePath);
                            }

                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            // 不删除文件，保留原样
                            _logger.LogError(ex, "Failed to process insert retry file: {filePath}", filePath);
                        }
                    }
                    //重试更新失败的文件（BatchOperation<T> 形式）
                    await foreach (var (filePath, stream) in _fileUtils.ReadAllWithPathAsync(FileType.Errors, token, OperationType.Update.ToString()))
                    {
                        anyFileProcessed = true;
                        try
                        {
                            if (stream != null)
                            {
                                using (stream)
                                {
                                    var list = await MessagePackSerializer.DeserializeAsync<IEnumerable<BatchOperation<T>>>(stream, cancellationToken: token);
                                    if (list?.Any() == true)
                                    {
                                        // 批量写入更新通道（逐个写入，避免丢失）
                                        foreach (var item in list)
                                        {
                                            await _updateChannel.WriteAsync(item);
                                        }
                                        // 全部写入成功后删除原文件
                                        _fileUtils.RemoveFile(filePath);
                                        _logger.LogInformation("Update retry enqueued for file: {filePath}, count={count}", filePath, list.Count());
                                    }
                                    else
                                    {
                                        _fileUtils.RemoveFile(filePath);
                                    }
                                }
                            }
                            else
                            {
                                _logger.LogWarning("Stream is null for file: {filePath}", filePath);
                            }

                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            _logger.LogError(ex, "Failed to process update retry file: {filePath}", filePath);
                        }
                    }
                    //重试 RetryOperation 类型的错误（已经是重试过的数据）
                    await foreach (var (filePath, stream) in _fileUtils.ReadAllWithPathAsync(FileType.Errors, token, OperationType.Retry.ToString()))
                    {
                        anyFileProcessed = true;
                        try
                        {
                            if (stream != null)
                            {
                                using (stream)
                                {
                                    var list = await MessagePackSerializer.DeserializeAsync<RetryOperation<T>>(stream, cancellationToken: token);
                                    if (list?.RetryCount > 0)
                                    {
                                        // 写回重试通道，继续重试策略
                                        await _retryChannel.Writer.WriteAsync(list, token);
                                        _fileUtils.RemoveFile(filePath);
                                        _logger.LogInformation("RetryOperation reloaded for file: {filePath}, count={count}", filePath, list.RetryCount);
                                    }
                                    else
                                    {
                                        _fileUtils.RemoveFile(filePath);
                                    }
                                }
                            }
                            else
                            {
                                _logger.LogWarning("Stream is null for file: {filePath}", filePath);
                            }

                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            _logger.LogError(ex, "Failed to process retry-operation file: {filePath}", filePath);
                        }
                    }
                    if (!anyFileProcessed)
                    {
                        _logger.LogInformation("No error files found for {typeName}, waiting {interval} before next retry scan", typeof(T).Name, _fileRetryInterval);
                    }

                    // 等待下一轮扫描（支持取消）
                    await Task.Delay(_fileRetryInterval, token);

                }
                catch (OperationCanceledException)
                {
                    _logger.LogInformation("FileRetry cancelled for {typeName}", typeof(T).Name);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unexpected error in FileRetry loop for {typeName}, will retry after delay", typeof(T).Name);
                    try
                    {
                        await Task.Delay(_fileRetryInterval, token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
        }
        #endregion

        /// <summary>
        /// 重试路径下为 UPDATE 语句追加 RETURNING 主键，用于识别实际命中行；正常路径原样返回以保留事务内短语句性能
        /// </summary>
        /// <param name="sql">原始 UPDATE 语句</param>
        /// <param name="tableName">目标表名</param>
        /// <param name="retryCount">当前重试次数（大于 0 才追加 RETURNING）</param>
        /// <returns>追加 RETURNING 后的语句（正常路径返回原语句）</returns>
        private static string AppendReturningIfRetry(string sql, string tableName, int retryCount)
        {
            return retryCount > 0
                ? $"{sql} RETURNING {tableName}.{_updateMapping.Key}"
                : sql;
        }

        /// <summary>
        /// 计算灌数用列清单：批次号列在前，其后为实体行列
        /// </summary>
        /// <param name="columns">实体列</param>
        /// <param name="includeBatchId">是否补批次号列</param>
        private static string[] EffectiveColumns(string[] columns, bool includeBatchId)
        {
            return includeBatchId ? ["batch_id", .. columns] : columns;
        }

        /// <summary>
        /// 生成多值 INSERT 的 VALUES 行（(:r0_col, ...) 形式）；行内占位符与 BuildChunkInsertParameters 的参数命名保持一致
        /// </summary>
        /// <param name="effectiveColumns">本语句列清单（含批次号列时已在前）</param>
        /// <param name="rowCount">行数</param>
        private static string BuildNamedValuesRows(string[] effectiveColumns, int rowCount)
        {
            var rows = new string[rowCount];
            for (var row = 0; row < rowCount; row++)
            {
                var placeholders = new string[effectiveColumns.Length];
                for (var c = 0; c < effectiveColumns.Length; c++)
                {
                    placeholders[c] = $":r{row}_{effectiveColumns[c]}";
                }
                rows[row] = "(" + string.Join(", ", placeholders) + ")";
            }
            return string.Join(",", rows);
        }

        /// <summary>
        /// 为分块多值 INSERT 构建命名参数，按行索引命名占位符（:r{row}_{column}），与 BuildNamedValuesRows 生成的 SQL 一一对应
        /// </summary>
        /// <param name="columns">实体列</param>
        /// <param name="rowFactory">单行列值工厂</param>
        /// <param name="entitys">实体集合</param>
        /// <param name="offset">起始行</param>
        /// <param name="count">行数</param>
        /// <param name="includeBatchId">是否补批次号列</param>
        /// <param name="batchId">批次号</param>
        private static List<DbParameter> BuildChunkInsertParameters(
            string[] columns,
            Func<T, object[]> rowFactory,
            IReadOnlyList<T> entitys,
            int offset,
            int count,
            bool includeBatchId,
            Guid? batchId)
        {
            var effectiveColumns = EffectiveColumns(columns, includeBatchId);
            var parameters = new List<DbParameter>(effectiveColumns.Length * count);
            for (var row = 0; row < count; row++)
            {
                var values = rowFactory(entitys[offset + row]);
                if (includeBatchId)
                {
                    parameters.Add(XuguParameterFactory.Create($":r{row}_batch_id", batchId.GetValueOrDefault().ToString(), typeof(string)));
                }
                for (var c = 0; c < columns.Length; c++)
                {
                    var column = columns[c];
                    // 类型以 mapping 属性类型为准；JSON/枚举等归一化由 XuguParameterFactory 处理
                    parameters.Add(XuguParameterFactory.Create($":r{row}_{column}", values[c], GetPropertyTypeForColumn(column)));
                }
            }
            return parameters;
        }

        /// <summary>
        /// 批量插入参数类型解析：优先映射属性类型，未收录列按 string 承载（如 batch_id）
        /// </summary>
        /// <param name="column">列名</param>
        private static Type GetPropertyTypeForColumn(string column)
        {
            return _updateMapping.PropertyMap.TryGetValue(column, out var propertyInfo)
                ? propertyInfo.PropertyType
                : typeof(object);
        }

        /// <summary>
        /// 执行批量 UPDATE：正常路径不带 RETURNING 保持性能；重试路径带 RETURNING 识别实际命中行，避免状态迟滞导致整批死循环
        /// </summary>
        /// <param name="connection">数据库连接</param>
        /// <param name="transaction">当前事务</param>
        /// <param name="sql">UPDATE 语句（重试路径已追加 RETURNING 主键）</param>
        /// <param name="configureParameters">向命令补充参数的委托（各策略参数不同）</param>
        /// <param name="entitys">待更新实体</param>
        /// <param name="columns">更新列</param>
        /// <param name="expressionStr">自定义条件</param>
        /// <param name="tableName">目标表名</param>
        /// <param name="retryCount">当前重试次数</param>
        /// <param name="strategyName">策略名（用于日志）</param>
        /// <param name="sw">耗时统计</param>
        /// <param name="lazyRetry">整批重试数据（正常路径回滚时使用）</param>
        /// <param name="token">取消令牌</param>
        private async Task ExecuteBatchUpdateAsync(
            DbConnection connection,
            DbTransaction transaction,
            string sql,
            Action<DbCommand> configureParameters,
            T[] entitys,
            string[] columns,
            string? expressionStr,
            string tableName,
            int retryCount,
            string strategyName,
            Stopwatch sw,
            Lazy<RetryOperation<T>> lazyRetry,
            CancellationToken token)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Transaction = transaction;
            configureParameters(command);
            if (retryCount > 0)
            {
                // 重试路径：RETURNING 与 UPDATE 同语句原子返回实际命中主键，避免并发下误判
                HashSet<object?> matchedKeys;
                await using (var reader = await command.ExecuteReaderAsync(token))
                {
                    // 先完整读取并释放 reader，再提交事务，否则连接上仍有命令执行中
                    matchedKeys = await ReadMatchedKeysAsync(reader, token);
                }
                await CompleteUpdateAsync(transaction, entitys.Length, matchedKeys.Count, matchedKeys, entitys, columns, expressionStr, tableName, retryCount, strategyName, sw, lazyRetry, token);
            }
            else
            {
                // 正常路径：不带 RETURNING，保持事务内短语句高性能
                int affected = await command.ExecuteNonQueryAsync(token);
                await CompleteUpdateAsync(transaction, entitys.Length, affected, null, entitys, columns, expressionStr, tableName, retryCount, strategyName, sw, lazyRetry, token);
            }
        }

        /// <summary>
        /// 汇总批量 UPDATE 结果：全部命中提交；正常路径未全部命中回滚整批重试；重试路径提交命中行并仅重试未命中行
        /// </summary>
        /// <param name="transaction">当前事务</param>
        /// <param name="totalCount">整批实体数量</param>
        /// <param name="matchedCount">实际命中数量</param>
        /// <param name="matchedKeys">重试路径下实际命中的主键集合（正常路径为 null）</param>
        /// <param name="entitys">待更新实体</param>
        /// <param name="columns">更新列</param>
        /// <param name="expressionStr">自定义条件</param>
        /// <param name="tableName">目标表名</param>
        /// <param name="retryCount">当前重试次数</param>
        /// <param name="strategyName">策略名（用于日志）</param>
        /// <param name="sw">耗时统计</param>
        /// <param name="lazyRetry">整批重试数据（正常路径回滚时使用）</param>
        /// <param name="token">取消令牌</param>
        private async Task CompleteUpdateAsync(
            DbTransaction transaction,
            int totalCount,
            int matchedCount,
            HashSet<object?>? matchedKeys,
            T[] entitys,
            string[] columns,
            string? expressionStr,
            string tableName,
            int retryCount,
            string strategyName,
            Stopwatch sw,
            Lazy<RetryOperation<T>> lazyRetry,
            CancellationToken token)
        {
            if (matchedCount == totalCount)
            {
                // 全部命中，直接提交
                await transaction.CommitAsync(token);
                _logger.LogInformation("{funName} {entityName}: DataCount{dataCount},Times{time}ms", strategyName, typeof(T).Name, totalCount, sw.ElapsedMilliseconds);
                return;
            }

            if (retryCount == 0)
            {
                // 正常路径：未全部命中，回滚整批并整体重试（保留现状语义，给状态迟滞一次补机会）
                await SafeRollbackAsync(transaction, token);
                lazyRetry.Value.FailureReason = "Not all operations were successful";
                await _retryChannel.Writer.WriteAsync(lazyRetry.Value, token);
                _logger.LogWarning("Not all operations were successful！Entity:{type},Total Count:{totalCount},Success Count:{successCount} ,Function Name:{funName}", typeof(T).Name, totalCount, matchedCount, strategyName);
                return;
            }

            // 重试路径：提交已命中行，仅对未命中行构造新重试继续（受 MaxRetryCount 约束，避免整批死循环）
            var unmatched = ComputeUnmatchedEntities(entitys, matchedKeys ?? []);
            await transaction.CommitAsync(token);
            if (unmatched.Count > 0)
            {
                var unmatchedRetry = new RetryOperation<T>
                {
                    Entitys = unmatched,
                    UpdateColumns = columns,
                    TableName = tableName,
                    Expression = expressionStr,
                    RetryCount = retryCount + 1,
                    RetryTime = CalcNextRetryTime(retryCount + 1),
                    FailureReason = "Not all operations were successful"
                };
                await _retryChannel.Writer.WriteAsync(unmatchedRetry, token);
            }
            _logger.LogWarning("Partial update! Entity:{type},Total Count:{totalCount},Success Count:{successCount},Skipped Count:{skippedCount} ,Function Name:{funName}", typeof(T).Name, totalCount, matchedCount, totalCount - matchedCount, strategyName);
        }

        /// <summary>
        /// 根据实际命中主键筛选出未命中的实体；虚谷驱动对主键返回类型可能与实体侧不完全一致，统一以字符串形式比较
        /// </summary>
        /// <param name="entitys">整批实体</param>
        /// <param name="matchedKeys">数据库实际命中的主键集合</param>
        /// <returns>未命中的实体列表</returns>
        private static List<T> ComputeUnmatchedEntities(T[] entitys, HashSet<object?> matchedKeys)
        {
            var keyColumn = _updateMapping.Key;
            var getter = _updateMapping.ValueGetters[keyColumn];
            var normalizedKeys = new HashSet<string>(matchedKeys.Select(key => key?.ToString() ?? string.Empty), StringComparer.OrdinalIgnoreCase);
            return entitys.Where(entity => !normalizedKeys.Contains(getter(entity)?.ToString() ?? string.Empty)).ToList();
        }

        /// <summary>
        /// 读取 UPDATE ... RETURNING 返回的实际命中主键集合（与 UPDATE 同一语句完成，读取天然原子）
        /// </summary>
        /// <param name="reader">RETURNING 结果读取器，第一列为返回的主键</param>
        /// <param name="token">取消令牌</param>
        /// <returns>命中的主键集合</returns>
        private static async Task<HashSet<object?>> ReadMatchedKeysAsync(DbDataReader reader, CancellationToken token)
        {
            var matchedKeys = new HashSet<object?>();
            while (await reader.ReadAsync(token))
            {
                matchedKeys.Add(reader.GetValue(0));
            }
            return matchedKeys;
        }

        /// <summary>
        /// 获取实体指定列的数据（多值 INSERT 值工厂，含 JSON 列的字符串化）
        /// </summary>
        /// <param name="columns">列名</param>
        /// <returns></returns>
        /// <exception cref="InvalidOperationException"></exception>
        private static Func<T, object[]> GetRowFactoryForColumns(string[] columns)
        {
            // 构建缓存键（列名排序后拼接，忽略大小写差异）
            var key = string.Join("\x1f", columns.OrderBy(c => c, StringComparer.OrdinalIgnoreCase));

            return _rowFactoryCache.GetOrAdd(key, _ =>
            {
                var gettersArray = new List<Func<T, object?>>(columns.Length);
                foreach (var col in columns)
                {
                    // ValueGetters 字典已配置为忽略大小写
                    if (_updateMapping.ValueGetters.TryGetValue(col, out var getter))
                    {
                        gettersArray.Add(getter);
                    }
                    else
                    {
                        throw new InvalidOperationException(
                            $"列 '{col}' 在类型 {typeof(T).Name} 的映射中不存在。可用列：{string.Join(", ", _updateMapping.ValueGetters.Keys)}");
                    }
                }

                var getters = gettersArray.ToArray();
                return entity =>
                {
                    var values = new object[getters.Length];
                    for (int i = 0; i < getters.Length; i++)
                    {
                        // ValueGetters 语义保证按列存在默认值，空引用按 null 显式承载，交由参数工厂归一为 DBNull
                        values[i] = getters[i](entity) ?? (object)DBNull.Value;
                    }
                    return values;
                };
            });
        }
    }
}
