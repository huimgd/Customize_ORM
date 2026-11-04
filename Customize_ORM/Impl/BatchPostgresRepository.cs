using Customize_ORM.Enums;
using Customize_ORM.Extensions;
using Customize_ORM.Extensions.Sql;
using Customize_ORM.Interfaces;
using Customize_ORM.Models;
using MessagePack;
using Microsoft.Extensions.Logging;
using Npgsql;
using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using System.Linq.Expressions;
using System.Threading.Channels;

namespace Customize_ORM.Impl
{
    /// <summary>
    /// 数据库批量操作类
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public class BatchPostgresRepository<T> : PostgresRepository<T>, IBatchUpdateRepository<T>, IBulkInsertRepository<T> where T : class
    {
        private readonly HybridCache<T> _insertChannel;
        private readonly HybridCache<BatchOperation<T>> _updateChannel;
        private readonly Channel<RetryOperation<T>> _retryChannel;
        private readonly string _backupFilePath;
        private readonly Task _updateTask;
        private readonly Task _retryTask;
        private readonly FileUtils _fileUtils;
        private readonly CancellationTokenSource _cts;
        private const int UpdateBatchSize = 10000;
        private const int MaxRetryCount = 8;
        private readonly string _stagingTableName;
        private bool _stagingTableChecked = false;
        private readonly TimeSpan _fileRetryInterval = TimeSpan.FromMinutes(10);
        private static readonly ConcurrentDictionary<string, Func<T, object[]>> _rowFactoryCache = new();
        private static readonly SemaphoreSlim _stagingTableLock = new(1, 1);
        public BatchPostgresRepository(IDbConnectionFactory postgreSql, ILogger<PostgresRepository<T>> logger) : base(postgreSql, logger)
        {
            _stagingTableName = $"staging_{_tableName}";
            _backupFilePath = Path.Combine(Directory.GetCurrentDirectory(), AppSettings.Current.FileSettings.TempDirectoryPath, "PostgresRepository", typeof(T).Name);
            _fileUtils = new FileUtils(_backupFilePath);
            _insertChannel = new HybridCache<T>(Path.Combine(_backupFilePath, "InsertChannel"));
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
        /// <param name="entity"></param>
        /// <param name="columns"></param>
        /// <param name="expression"></param>
        /// <param name="token"></param>
        /// <param name="partition"></param>
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
                    var sqlExpr = new SqlExpression<T>(_mapping).WithTableAlias(tableName);
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
        /// <param name="operation"></param>
        /// <param name="token"></param>
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
        /// 批量插入
        /// </summary>
        /// <param name="entitys"></param>
        /// <param name="token"></param>
        /// <param name="partition"></param>
        /// <returns></returns>
        public async Task<(bool, ulong)> BulkInsertAsync(IReadOnlyList<T> entitys, string tableName = "", bool isRetry = false)
        {

            string sql = string.Empty;
            try
            {
                if (entitys.Count > 0)
                {
                    Stopwatch sw = Stopwatch.StartNew();
                    ArgumentNullException.ThrowIfNull(entitys, nameof(entitys));
                    if (string.IsNullOrWhiteSpace(tableName))
                    {
                        tableName = _tableName;
                    }
                    sql = $"COPY {tableName} ({string.Join(",", _updateMapping.AllColumns)}) FROM STDIN (FORMAT BINARY)";
                    using var connection = await _connectionFactory.OpenConnectionAsync();
                    ArgumentNullException.ThrowIfNull(connection, nameof(connection));
                    await using var writer = await BeginBinaryImportAsync(connection, sql, CancellationToken.None);

                    foreach (var entity in entitys)
                    {
                        var values = _rowFactory(entity);
                        await writer.WriteRowAsync(CancellationToken.None, values).ConfigureAwait(false);
                    }
                    var writeCount = await writer.CompleteAsync().ConfigureAwait(false);
                    _logger.LogInformation("{funName} {entityName}: DataCount{dataCount},Times{time}ms", nameof(BulkInsertAsync), typeof(T).Name, entitys.Count, sw.ElapsedMilliseconds);
                    return (true, writeCount);
                }
            }
            catch (PostgresException ex) when (ex.SqlState == "23505")
            {
                //插入键重复异常
                return (true, 0);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "{fnName} {entityName} sql=>{sqlstr}", nameof(BulkInsertAsync), typeof(T).Name, sql);
                if (!isRetry)
                {
                    using MemoryStream stream = new(256 * 1024); // 256KB缓冲区
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
        /// <param name="timeout"></param>
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
                await _insertChannel.StopAsync();

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

        #region 临时表方案更新


        /// <summary>
        /// 程序首次执行更新时创建UNLOGGED临时表避免产生大量WAL日志
        /// </summary>
        /// <returns></returns>
        private async Task EnsureStagingTableExistsAsync()
        {
            if (_stagingTableChecked) return;
            await _stagingTableLock.WaitAsync();
            try
            {
                if (_stagingTableChecked) return;
                var bulkSqlBuilder = new PostgreSqlBulkUpdateSqlBuilder<T>(_updateMapping);
                var createSql = bulkSqlBuilder.BuildCreateStagingTableSql(_stagingTableName);
                await using var conn = await _connectionFactory.OpenConnectionAsync();
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
            await EnsureStagingTableExistsAsync();
            var bulkSqlBuilder = new PostgreSqlBulkUpdateSqlBuilder<T>(_updateMapping);
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
            string[] allColumns = [_updateMapping.Key, _updateMapping.PartitionKey, .. columns];
            // 写入 staging 表
            await using (var stagingConn = await _connectionFactory.OpenConnectionAsync())
            {
                var copySql = bulkSqlBuilder.BuildCopySql(_stagingTableName, allColumns, includeBatchId: true);
                await using var writer = await BeginBinaryImportAsync(stagingConn, copySql, token);
                var columnCount = allColumns.Length;
                var data = new object[columnCount + 1];
                data[0] = batchId;
                var rowFactory = GetRowFactoryForColumns(allColumns);
                foreach (var entity in entitys)
                {
                    var values = rowFactory(entity);
                    var rowData = new object[1 + values.Length];
                    rowData[0] = batchId;
                    Array.Copy(values, 0, rowData, 1, values.Length);
                    await writer.WriteRowAsync(CancellationToken.None, rowData);
                }
                await writer.CompleteAsync(token);
            }
            // 步骤2：极短事务执行 UPDATE FROM staging
            var updateSql = bulkSqlBuilder.BuildUpdateFromSourceSql(
                targetTableName: tableName,
                sourceTableName: _stagingTableName,
                sourceAlias: "staging",
                updateColumns: columns,
                additionalWhereClause: expressionStr,
                sourcePredicate: "staging.batch_id = @batchId");
            updateSql = AppendReturningIfRetry(updateSql, tableName, retryCount);
            await using var connection = await _connectionFactory.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync(token);
            try
            {
                await ExecuteBatchUpdateAsync(connection, transaction, updateSql,
                    cmd => cmd.Parameters.Add(PostgreSqlParameterFactory.Create("@batchId", batchId, typeof(Guid))), entitys,
                    columns, expressionStr, tableName, retryCount, nameof(StagingStrategyAsync),
                    sw, lazyRetry, token);
            }
            catch (PostgresException pgEx) when (IsTransientError(pgEx))
            {
                await SafeRollbackAsync(transaction, token);
                await _retryChannel.Writer.WriteAsync(lazyRetry.Value, token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                //不可重试的错误，记录日志并保存重试数据
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
                    //初次更新过程中的异常
                    List<BatchOperation<T>> batchOperations = [.. entitys
                             .Select(entity => new BatchOperation<T>
                             {
                                 Entity = entity,
                                 UpdateColumns = columns,
                                 TableName = tableName,
                                 Expression = expressionStr ?? string.Empty
                             })];
                    using MemoryStream stream = new(256 * 1024);
                    await MessagePackSerializer.SerializeAsync(stream, batchOperations, cancellationToken: token);
                    await _fileUtils.WriteToAsync(stream, FileType.Errors, entitys.Length, OperationType.Update.ToString());
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
                var sql = $"DELETE FROM {_stagingTableName} WHERE batch_id = @batchId";
                await DbCommandExecution.ExecuteNonQueryAsync(
                    conn,
                    sql,
                    [new NpgsqlParameter("batchId", batchId)],
                    CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to clean staging batch {BatchId} for {Table}", batchId, _stagingTableName);
            }
        }



        public async Task TemporaryTableStrategyAsync(T[] entitys, string[] columns, string? expressionStr, string tableName, int retryCount, CancellationToken token)
        {
            Stopwatch sw = Stopwatch.StartNew();
            string tmp = $"tmp_{Guid.NewGuid():N}";
            var bulkSqlBuilder = new PostgreSqlBulkUpdateSqlBuilder<T>(_updateMapping);
            string[] allColumns = [_updateMapping.Key, _updateMapping.PartitionKey, .. columns];
            var lazyRetry = new Lazy<RetryOperation<T>>(() => new RetryOperation<T>
            {
                Entitys = entitys,
                UpdateColumns = columns,
                TableName = tableName,
                Expression = expressionStr,
                RetryCount = retryCount + 1,
                RetryTime = CalcNextRetryTime(retryCount + 1)
            }, LazyThreadSafetyMode.ExecutionAndPublication);
            //创建临时表  
            string sql = bulkSqlBuilder.BuildCreateTemporaryTableSql(tmp, allColumns);
            await using var connection = await _connectionFactory.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync(token);
            try
            {
                await using var command = DbCommandExecution.CreateCommand(connection, sql, transaction: transaction);
                var rel = await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                if (-1 == rel)
                {
                    var copySql = bulkSqlBuilder.BuildCopySql(tmp, allColumns);
                    using (var writer = await BeginBinaryImportAsync(connection, copySql, token))
                    {

                        var rowFactory = GetRowFactoryForColumns(allColumns);
                        foreach (var entity in entitys)
                        {
                            var rowData = rowFactory(entity);
                            await writer.WriteRowAsync(CancellationToken.None, rowData);
                        }
                        await writer.CompleteAsync(token);
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
            }
            catch (PostgresException pgEx) when (IsTransientError(pgEx))
            {
                await SafeRollbackAsync(transaction, token);
                await _retryChannel.Writer.WriteAsync(lazyRetry.Value, token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                //不可重试的错误，记录日志并保存重试数据
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
                    //初次更新过程中的异常
                    List<BatchOperation<T>> batchOperations = [.. entitys
                             .Select(entity => new BatchOperation<T>
                             {
                                 Entity = entity,
                                 UpdateColumns = columns,
                                 TableName = tableName,
                                 Expression = expressionStr ?? string.Empty
                             })];
                    using MemoryStream stream = new(256 * 1024);
                    await MessagePackSerializer.SerializeAsync(stream, batchOperations, cancellationToken: token);
                    await _fileUtils.WriteToAsync(stream, FileType.Errors, entitys.Length, OperationType.Update.ToString());
                }
            }
        }
        #endregion

        #region CTE方式更新
        private async Task CteStrategyAsync(T[] entitys, string[] columns, string? expressionStr, string tableName, int retryCount, CancellationToken token)
        {
            Stopwatch sw = Stopwatch.StartNew();
            var lazyRetry = new Lazy<RetryOperation<T>>(() => new RetryOperation<T>
            {
                Entitys = entitys,
                UpdateColumns = columns,
                TableName = tableName,
                Expression = expressionStr,
                RetryCount = retryCount + 1,
                RetryTime = CalcNextRetryTime(retryCount + 1)
            }, LazyThreadSafetyMode.ExecutionAndPublication);

            string guidShort = Guid.NewGuid().ToString("N")[..4];
            string timestampShort = DateTime.Now.ToString("HHmmssfff");
            string tmp = $"cte_{guidShort}{timestampShort}";
            var commandBuilder = new PostgreSqlCteUpdateCommandBuilder<T>(_updateMapping);
            var commandDefinition = commandBuilder.Build(entitys, columns, tableName, tmp, expressionStr);
            // 重试路径追加 RETURNING 主键，用于识别实际命中行，从而把已更新成功的数据从重试数据中过滤掉
            string sql = AppendReturningIfRetry(commandDefinition.Sql, tableName, retryCount);
            await using var connection = await _connectionFactory.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync(token);
            try
            {
                await ExecuteBatchUpdateAsync(connection, transaction, sql,
                    command =>
                    {
                        // CTE 的数组参数需逐个挂到真正执行的命令上（ExecuteBatchUpdateAsync 内部会新建命令）
                        foreach (var p in commandDefinition.Parameters)
                        {
                            command.Parameters.Add(p);
                        }
                    }, entitys, columns,
                    expressionStr, tableName, retryCount, nameof(CteStrategyAsync), sw, lazyRetry, token);
            }
            catch (PostgresException pgEx) when (IsTransientError(pgEx))
            {
                await transaction.RollbackAsync(token);
                await _retryChannel.Writer.WriteAsync(lazyRetry.Value, token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                //不可重试的错误，记录日志并保存重试数据
                await transaction.RollbackAsync(token);
                _logger.LogError(ex, "{funName} {typeName} DataCount: {dataCount}", nameof(CteStrategyAsync), typeof(T).Name, entitys.Length);
                if (retryCount > 0)
                {
                    //重试过程中的异常
                    using MemoryStream stream = new(256 * 1024);
                    await MessagePackSerializer.SerializeAsync(stream, lazyRetry.Value, cancellationToken: token);
                    await _fileUtils.WriteToAsync(stream, FileType.Errors, entitys.Length, OperationType.Retry.ToString(), retryCount.ToString());
                }
                else
                {
                    //初次更新过程中的异常
                    List<BatchOperation<T>> batchOperations = [.. entitys
                             .Select(entity => new BatchOperation<T>
                             {
                                 Entity = entity,
                                 UpdateColumns = columns,
                                 TableName = tableName,
                                 Expression = expressionStr ?? string.Empty
                             })];
                    using MemoryStream stream = new(256 * 1024);
                    await MessagePackSerializer.SerializeAsync(stream, batchOperations, cancellationToken: token);
                    await _fileUtils.WriteToAsync(stream, FileType.Errors, entitys.Length, OperationType.Update.ToString());
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
                            if (entities.Length > 5000)
                            {
                                await TemporaryTableStrategyAsync(entities, updateGroupColumns, updateGroupExpression, updateGroupTableName, 0, ct);
                            }
                            else
                            {
                                await CteStrategyAsync(entities, updateGroupColumns, updateGroupExpression, updateGroupTableName, 0, ct);
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
        /// <param name="op"></param>
        /// <returns></returns>
        private static string GetGroupKey(BatchOperation<T> op) => BatchRepositoryShared<T>.GetGroupKey(op);
        #endregion

        #region 内存重试 
        /// <summary>
        /// 重试channel
        /// </summary>
        /// <param name="token"></param>
        /// <returns></returns>
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
                            if (data.Length > 5000)
                            {
                                await TemporaryTableStrategyAsync(data, retryData.UpdateColumns, retryData.Expression, retryData.TableName, retryData.RetryCount, token);
                            }
                            else
                            {
                                await CteStrategyAsync(data, retryData.UpdateColumns, retryData.Expression, retryData.TableName, retryData.RetryCount, token);
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
                                    var list = await MessagePackSerializer.DeserializeAsync<IEnumerable<T>>(stream, cancellationToken: token);
                                    if (list?.Any() == true)
                                    {
                                        var (success, _) = await BulkInsertAsync([.. list], _tableName, true);
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
        /// 执行批量 UPDATE：正常路径不带 RETURNING 保持性能；重试路径带 RETURNING 识别实际命中行，避免状态迟滞导致整批死循环
        /// </summary>
        /// <param name="connection">数据库连接</param>
        /// <param name="transaction">当前事务</param>
        /// <param name="sql">UPDATE 语句（重试路径已追加 RETURNING 主键）</param>
        /// <param name="configureParameters">向命令补充参数的委托（各策略参数不同）</param>
        /// <param name="entitys">待更新实体</param>
        /// <param name="columns">更新列</param>
        /// <param name="expressionStr">自定义条件</param>
        /// <param name="tableName">表名称</param>
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
            await using var command = DbCommandExecution.CreateCommand(connection, sql, null, transaction);
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
        /// 打开 Npgsql 二进制导入通道：COPY FORMAT BINARY 是 Npgsql 专属能力，无法用 ADO.NET 抽象表达，
        /// 因此把驱动类型转换集中在此处，其余逻辑保持抽象。
        /// </summary>
        /// <param name="connection">已打开的连接，必须是 NpgsqlConnection</param>
        /// <param name="copySql">COPY 语句</param>
        /// <param name="token">取消令牌</param>
        private static async Task<NpgsqlBinaryImporter> BeginBinaryImportAsync(DbConnection connection, string copySql, CancellationToken token)
        {
            if (connection is not NpgsqlConnection npgsqlConnection)
            {
                throw new NotSupportedException(
                    $"COPY binary import requires an NpgsqlConnection, but got {connection.GetType().Name}.");
            }

            return await npgsqlConnection.BeginBinaryImportAsync(copySql, token);
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
        /// <param name="tableName">表名称</param>
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
        /// 根据实际命中主键筛选出未命中的实体，主键值按写入转换规则归一化，保证与数据库返回值可比较
        /// </summary>
        /// <param name="entitys">整批实体</param>
        /// <param name="matchedKeys">数据库实际命中的主键集合</param>
        /// <returns>未命中的实体列表</returns>
        private static List<T> ComputeUnmatchedEntities(T[] entitys, HashSet<object?> matchedKeys)
        {
            var keyColumn = _updateMapping.Key;
            var getter = _updateMapping.ValueGetters[keyColumn];
            var isJsonb = _updateMapping.JsonbColumns.Contains(keyColumn);
            return entitys.Where(entity => !matchedKeys.Contains(ObjectMapping<T>.ConvertValueForWrite(getter(entity), isJsonb))).ToList();
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
        /// 数据库瞬时错误
        /// 这种还可以重试
        /// </summary>
        /// <param name="ex"></param>
        /// <returns></returns>
        private static bool IsTransientError(PostgresException ex)
        {
            // 使用HashSet进行快速查找
            return ex.SqlState switch
            {
                "40001" or "40P01" or "08006" or "08000" or "08003" or "08004" or "08007" or "57014" or "55P03" or "55P02" => true,
                _ => false
            };
        }
        /// <summary>
        /// 获取实体指定列的数据
        /// </summary>
        /// <param name="columns"></param>
        /// <returns></returns>
        /// <exception cref="InvalidOperationException"></exception>
        private static Func<T, object[]> GetRowFactoryForColumns(string[] columns)
        {
            // 构建缓存键（列名排序后拼接，忽略大小写差异）
            var key = string.Join("\x1f", columns.OrderBy(c => c, StringComparer.OrdinalIgnoreCase));

            return _rowFactoryCache.GetOrAdd(key, _ =>
            {
                // 使用 _updateMapping（已有的、经过验证的映射）
                var getters = new List<Func<T, object?>>(columns.Length);
                var isJsonbColumns = new List<bool>(columns.Length);
                foreach (var col in columns)
                {
                    // ValueGetters 字典已配置为忽略大小写
                    if (_updateMapping.ValueGetters.TryGetValue(col, out var getter))
                    {
                        getters.Add(getter);
                        isJsonbColumns.Add(_updateMapping.JsonbColumns.Contains(col));
                    }
                    else
                    {
                        throw new InvalidOperationException(
                            $"列 '{col}' 在类型 {typeof(T).Name} 的映射中不存在。可用列：{string.Join(", ", _updateMapping.ValueGetters.Keys)}");
                    }
                }

                var gettersArray = getters.ToArray();
                var jsonbArray = isJsonbColumns.ToArray();
                return entity =>
                {
                    var values = new object[gettersArray.Length];
                    for (int i = 0; i < gettersArray.Length; i++)
                    {
                        var rawValue = gettersArray[i](entity);
                        values[i] = ObjectMapping<T>.ConvertValueForWrite(rawValue, jsonbArray[i]);
                    }
                    return values;
                };
            });
        }
    }
}
