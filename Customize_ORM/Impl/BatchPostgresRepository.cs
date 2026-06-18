using Customize_ORM.Enums;
using Customize_ORM.Extensions;
using Customize_ORM.Interfaces;
using Customize_ORM.Interfaces.ConnectionFactory;
using Customize_ORM.Models;
using MessagePack;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq.Expressions;
using System.Threading.Channels;

namespace Customize_ORM.Impl
{
    /// <summary>
    /// 数据库批量操作类
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public class BatchPostgresRepository<T> : PostgresRepository<T>, IBatchPostgresRepository<T> where T : class
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
        public BatchPostgresRepository(IPgConnectionFactory postgreSql, ILogger<PostgresRepository<T>> logger) : base(postgreSql, logger)
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
                await _retryChannel.Writer.WriteAsync(operation);
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
                    sql = $"COPY {tableName} ({string.Join(",", _upadteMapping.AllColumns)}) FROM STDIN (FORMAT BINARY)";
                    using var connection = await _postgreSql.GetConnectionAsync();
                    ArgumentNullException.ThrowIfNull(connection, nameof(connection));
                    await using var writer = await connection.BeginBinaryImportAsync(sql, CancellationToken.None);

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
                var columnsDefs = string.Join(", ",
                    _upadteMapping.AllColumns.Where(c => c != _upadteMapping.Key)
                        .Select(c => $"{c} {GetPostgreSqlColumnType(c)}"));

                var createSql = $@"
                        CREATE UNLOGGED TABLE IF NOT EXISTS {_stagingTableName} (
                            batch_id UUID NOT NULL,
                            {_upadteMapping.Key} {GetPostgreSqlColumnType(_upadteMapping.Key)} NOT NULL,
                            {columnsDefs},
                            created_at TIMESTAMP WITH TIME ZONE DEFAULT NOW()
                        );";
                await using var conn = await _postgreSql.GetConnectionAsync();
                using var cmd = new NpgsqlCommand(createSql, conn);
                await cmd.ExecuteNonQueryAsync();
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
            string[] allColumns = [_upadteMapping.Key, _upadteMapping.PartitionKey, .. columns];
            // 写入 staging 表
            await using (var stagingConn = await _postgreSql.GetConnectionAsync())
            {
                var copySql = $"COPY {_stagingTableName} (batch_id,{string.Join(",", allColumns)}) FROM STDIN (FORMAT BINARY)";
                await using var writer = await stagingConn.BeginBinaryImportAsync(copySql, token);
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
            var setClauses = columns.Select(c => $"{c} = staging.{c}");
            var whereClause = $"{tableName}.{_upadteMapping.Key} = staging.{_upadteMapping.Key} AND {tableName}.{_upadteMapping.PartitionKey} = staging.{_upadteMapping.PartitionKey}";
            if (!string.IsNullOrWhiteSpace(expressionStr))
                whereClause += $" AND {expressionStr}";

            var updateSql = $@"
                UPDATE {tableName}
                SET {string.Join(", ", setClauses)}
                FROM {_stagingTableName} AS staging
                WHERE staging.batch_id = @batchId AND {whereClause}";

            await using var connection = await _postgreSql.GetConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync(token);
            try
            {
                // 仅对本次事务关闭同步提交，其他会话不受影响
                //using (var setCmd = new NpgsqlCommand("SET LOCAL synchronous_commit = off;", connection, transaction))
                //{
                //    await setCmd.ExecuteNonQueryAsync(token);
                //}
                using var cmd = new NpgsqlCommand(updateSql, connection, transaction);
                cmd.Parameters.AddWithValue("batchId", batchId);
                int affected = await cmd.ExecuteNonQueryAsync(token);
                if (affected != entitys.Length)
                {
                    //操作未能全部成功，回滚并构造重试数据
                    await transaction.RollbackAsync(token);
                    _logger.LogWarning("Not all operations were successful！Entity:{type},Total Count:{totalCount},Success Count:{successCount} ,Function Name:{funName}", typeof(T).Name, entitys.Length, affected, nameof(StagingStrategyAsync));
                    await _retryChannel.Writer.WriteAsync(lazyRetry.Value, token);
                }
                else
                {
                    await transaction.CommitAsync(token);
                    _logger.LogInformation("{funName} {entityName}: DataCount{dataCount},Times{time}ms", nameof(StagingStrategyAsync), typeof(T).Name, entitys.Length, sw.ElapsedMilliseconds);
                }

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
                await using var conn = await _postgreSql.GetConnectionAsync();
                var sql = $"DELETE FROM {_stagingTableName} WHERE batch_id = @batchId";
                using var cmd = new NpgsqlCommand(sql, conn);
                cmd.Parameters.AddWithValue("batchId", batchId);
                await cmd.ExecuteNonQueryAsync();
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
            string[] allColumns = [_upadteMapping.Key, _upadteMapping.PartitionKey, .. columns];
            var lazyRetry = new Lazy<RetryOperation<T>>(() => new RetryOperation<T>
            {
                Entitys = entitys,
                UpdateColumns = columns,
                TableName = tableName,
                Expression = expressionStr,
                RetryCount = retryCount + 1,
                RetryTime = CalcNextRetryTime(retryCount + 1)
            }, LazyThreadSafetyMode.ExecutionAndPublication);
            var columnsDefs = string.Join(", ",
                  allColumns.Select(c => $"{c} {GetPostgreSqlColumnType(c)}"));
            //创建临时表  
            string sql = $@"CREATE TEMPORARY TABLE {tmp}({columnsDefs}) ON  COMMIT DROP";
            await using var connection = await _postgreSql.GetConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync(token);
            try
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = sql;
                var rel = await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                if (-1 == rel)
                {
                    var copySql = $"COPY {tmp} ({string.Join(",", allColumns)}) FROM STDIN (FORMAT BINARY)";
                    using (var writer = await connection.BeginBinaryImportAsync(copySql, token))
                    {

                        var rowFactory = GetRowFactoryForColumns(allColumns);
                        foreach (var entity in entitys)
                        {
                            var rowData = rowFactory(entity);
                            await writer.WriteRowAsync(CancellationToken.None, rowData);
                        }
                        await writer.CompleteAsync(token);
                    }
                    ;

                    string whereClause = $"{tableName}.{_upadteMapping.Key} = {tmp}.{_upadteMapping.Key} AND {tableName}.{_upadteMapping.PartitionKey}= {tmp}.{_upadteMapping.PartitionKey}";
                    if (!string.IsNullOrWhiteSpace(expressionStr))
                    {
                        whereClause += $" AND {expressionStr}";
                    }
                    string[] setClauseArray = new string[columns.Length];
                    for (int i = 0; i < columns.Length; i++)
                    {
                        setClauseArray[i] = $"{columns[i]} = {tmp}.{columns[i]}";
                    }
                    string setClauses = string.Join(", ", setClauseArray);
                    string updateSql = $@" UPDATE {tableName} 
                                            SET {setClauses}
                                            FROM {tmp} 
                                            WHERE {whereClause}";
                    using var updateCommand = connection.CreateCommand();
                    updateCommand.Transaction = transaction;
                    updateCommand.CommandText = updateSql;
                    int affected = await updateCommand.ExecuteNonQueryAsync(token);
                    if (affected != entitys.Length)
                    {
                        //操作未能全部成功，回滚并构造重试数据
                        await transaction.RollbackAsync(token);
                        _logger.LogWarning("Not all operations were successful！Entity:{type},Total Count:{totalCount},Success Count:{successCount} ,Function Name:{funName}", typeof(T).Name, entitys.Length, affected, nameof(TemporaryTableStrategyAsync));
                        await _retryChannel.Writer.WriteAsync(lazyRetry.Value, token);
                    }
                    else
                    {
                        await transaction.CommitAsync(token);
                        _logger.LogInformation("{funName} {entityName}: DataCount{dataCount},Times{time}ms", nameof(TemporaryTableStrategyAsync), typeof(T).Name, entitys.Length, sw.ElapsedMilliseconds);
                    }
                }
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
            var valueGetters = _upadteMapping.ValueGetters;
            string[] allColumns = [_upadteMapping.Key, _upadteMapping.PartitionKey, .. columns];
            // 构建SET子句
            var setClauses = new List<string>();
            foreach (var col in columns)
            {
                setClauses.Add($"{col} = {tmp}.{col}");
            }
            // 构建完整的WHERE条件（用于更新）
            List<string> whereConditions = [$"{tableName}.{_upadteMapping.Key} = {tmp}.{_upadteMapping.Key}", $"{tableName}.{_upadteMapping.PartitionKey} = {tmp}.{_upadteMapping.PartitionKey}"];
            if (!string.IsNullOrWhiteSpace(expressionStr))
            {
                whereConditions.Add(expressionStr);
            }
            List<NpgsqlParameter> parameters = [];
            List<string> selectColumns = [];
            // 为每一列准备数组参数
            foreach (var column in allColumns)
            {
                if (valueGetters.TryGetValue(column, out var getter))
                {
                    var propertyType = GetPropertyType(column);
                    var values = Array.CreateInstance(GetClrTypeForProperty(propertyType), entitys.Length);
                    for (var i = 0; i < entitys.Length; i++)
                    {
                        var value = getter(entitys[i]);
                        values.SetValue(value, i);
                    }
                    var parameter = new NpgsqlParameter($"@{column}", values);
                    var elementType = GetClrTypeForProperty(propertyType);
                    if (elementType.IsArray)
                    {
                        // 处理数组类型的逻辑
                        var arrayElementType = elementType.GetElementType() ?? typeof(object);
                        string[] arrayStrings = new string[entitys.Length];
                        for (int i = 0; i < entitys.Length; i++)
                        {
                            if (getter(entitys[i]) is not Array arrayValue || arrayValue.Length == 0)
                                arrayStrings[i] = "{}";
                            else
                            {
                                List<string> elements = new List<string>();
                                foreach (var item in arrayValue)
                                {
                                    if (item == null || item == DBNull.Value)
                                        elements.Add("NULL");
                                    else
                                    {
                                        string elementStr = item.ToString() ?? "";
                                        elementStr = elementStr.Replace("\"", "\"\"");
                                        elements.Add($"\"{elementStr}\"");
                                    }
                                }
                                arrayStrings[i] = "{" + string.Join(",", elements) + "}";
                            }
                        }
                        string type = GetPostgreSqlArrayType(arrayElementType);
                        selectColumns.Add($"unnest(@{column})::{type} as {column}");
                        parameter = new NpgsqlParameter($"@{column}", arrayStrings);
                    }
                    else
                    {
                        selectColumns.Add($"unnest(@{column}) as {column}");
                    }
                    var elementDbType = GetNpgsqlDbType(elementType);
                    parameter.NpgsqlDbType = NpgsqlDbType.Array | elementDbType;
                    parameters.Add(parameter);
                }
            }

            string sql = $@" WITH {tmp} AS (
                        SELECT {string.Join(", ", selectColumns)}
                        )UPDATE {tableName} 
                            SET {string.Join(", ", setClauses)}
                            FROM {tmp}
                            WHERE {string.Join(" AND ", whereConditions)}";
            await using var connection = await _postgreSql.GetConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync(token);
            try
            {
                await using var command = new NpgsqlCommand(sql, connection, transaction);
                command.Parameters.AddRange(parameters.ToArray());
                int affected = await command.ExecuteNonQueryAsync(token);
                if (affected != entitys.Length)
                {
                    //操作未能全部成功，回滚并构造重试数据
                    await transaction.RollbackAsync(token);
                    _logger.LogWarning("Not all operations were successful！Entity:{type},Total Count:{totalCount},Success Count:{successCount} ,Function Name:{funName}", typeof(T).Name, entitys.Length, affected, nameof(CteStrategyAsync));
                    await _retryChannel.Writer.WriteAsync(lazyRetry.Value, token);
                }
                else
                {
                    await transaction.CommitAsync(token);
                    _logger.LogInformation("{funName} {entityName}: DataCount{dataCount},Times{time}ms", nameof(CteStrategyAsync), typeof(T).Name, entitys.Length, sw.ElapsedMilliseconds);
                }
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
        private static string GetGroupKey(BatchOperation<T> op) =>
    $"{string.Join(",", op.UpdateColumns.OrderBy(c => c))}|{op.Expression ?? ""}|{op.TableName}";
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
            var waitSeconds = Math.Pow(2, retryCnt);
            return DateTimeOffset.Now.AddSeconds(waitSeconds);
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
        private Func<T, object[]> GetRowFactoryForColumns(string[] columns)
        {
            // 构建缓存键（列名排序后拼接，忽略大小写差异）
            var key = string.Join("\x1f", columns.OrderBy(c => c, StringComparer.OrdinalIgnoreCase));

            return _rowFactoryCache.GetOrAdd(key, _ =>
            {
                // 使用 _upadteMapping（已有的、经过验证的映射）
                var getters = new List<Func<T, object?>>(columns.Length);
                foreach (var col in columns)
                {
                    // ValueGetters 字典已配置为忽略大小写
                    if (_upadteMapping.ValueGetters.TryGetValue(col, out var getter))
                    {
                        getters.Add(getter);
                    }
                    else
                    {
                        throw new InvalidOperationException(
                            $"列 '{col}' 在类型 {typeof(T).Name} 的映射中不存在。可用列：{string.Join(", ", _upadteMapping.ValueGetters.Keys)}");
                    }
                }

                var gettersArray = getters.ToArray();
                return entity =>
                {
                    var values = new object[gettersArray.Length];
                    for (int i = 0; i < gettersArray.Length; i++)
                    {
                        var rawValue = gettersArray[i](entity);
                        values[i] = rawValue switch
                        {
                            ulong ul => (decimal)ul,
                            ulong[] ula => Array.ConvertAll(ula, x => (decimal)x),
                            null => DBNull.Value,
                            _ => rawValue
                        };
                    }
                    return values;
                };
            });
        }
    }
}
