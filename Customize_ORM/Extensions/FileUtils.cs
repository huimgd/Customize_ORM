using Customize_ORM.Enums;
using Customize_ORM.Models;
using MessagePack;
using Microsoft.Extensions.Logging;
using Microsoft.VisualBasic.FileIO;
using NLog;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;

namespace Customize_ORM.Extensions
{
    public sealed class FileUtils
    {
        /// <summary>
        /// 文件保存路径
        /// </summary>
        public readonly string _filePath;

        private static readonly Logger _logger = LogManager.GetCurrentClassLogger();
        private static readonly HashSet<string> LuaScriptFileNames = [];

        public FileUtils(string fileSavePath)
        {
            _filePath = fileSavePath;

            if (string.IsNullOrWhiteSpace(_filePath))
            {
                throw new ArgumentException("File Save Path Cannot Be Empty", nameof(fileSavePath));
            }

            Directory.CreateDirectory(_filePath);
        }

        /// <summary>
        /// 获取存储路径
        /// </summary>
        /// <returns></returns>
        public static string GetTempPath()
        {
            return Path.Combine(Directory.GetCurrentDirectory(), AppSettings.Current.FileSettings.TempDirectoryPath);
        }

        public async Task<bool> WriteToAsync(object data, FileType fileType, params string[] folders)
        {
            // 10KB缓冲区
            using MemoryStream stream = new(10 * 1024);
            await MessagePackSerializer.SerializeAsync(stream, data);
            return await WriteToAsync(stream, fileType, 1, folders);
        }
        /// <summary>
        /// 写入文件
        /// </summary>
        /// <param name="stream">数据流</param>
        /// <param name="fileType">文件类型</param>
        /// <param name="folders">文件夹，写入时在path的基础下继续往下创建文件夹作为写入路径</param>
        /// <returns>是否保存成功</returns>
        /// <exception cref="ArgumentNullException">流中无数据</exception>
        public async Task<bool> WriteToAsync(MemoryStream stream, FileType fileType, int count, params string[] folders)
        {
            var status = false;
            if (stream == null || stream.Length == 0)
            {
                throw new ArgumentNullException(nameof(stream));
            }

            var now = DateTime.Now;
            var ticks = now.Ticks;
            var tid = Environment.CurrentManagedThreadId;
            var fileName = $"{ticks}-{tid}-{count}.dat";
            try
            {
                var path = Path.Combine(new[] { _filePath, fileType.ToString() }.Concat(folders).Append(fileName).ToArray());
                var dir = new FileInfo(path).Directory;
                if (dir is { Exists: false })
                {
                    dir.Create();
                }

                stream.Position = 0;
                await using var fileStream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
                // 重置流位置，确保从头开始写入
                stream.Position = 0;
                await stream.CopyToAsync(fileStream);
                status = true;
            }
            catch (IOException ex)
            {
                _logger.Error(ex, "{fileType} {fileName} Write IOException {Error}", fileType, fileName, ex);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "{fileType} {fileName} Write  Exception {Error}", fileType, fileName, ex);
            }

            return status;
        }
        /// <summary>
        /// 判断路径下是否存在文件
        /// </summary>
        /// <param name="path">文件夹路径</param>
        /// <returns>存在true,不存在false</returns>
        public static bool ExistenceFile(string path)
        {
            bool exists = Directory.Exists(path);
            if (exists)
            {
                return Directory.EnumerateFiles(path).Any();
            }
            else
            {
                return false;
            }

        }
        /// <summary>
        /// 写文件
        /// </summary>
        /// <param name="filePath">文件路径</param>
        /// <param name="data">数据</param>
        /// <param name="dataCount">数据条数</param>
        /// <param name="fileType">文件类型</param>
        /// <param name="folders">追加路径</param>
        /// <returns></returns>
        /// <exception cref="ArgumentNullException"></exception>
        public static async Task<bool> WriteToAsync(string filePath, object data, int dataCount, FileType fileType, params string[] folders)
        {
            // 10KB缓冲区
            using MemoryStream stream = new(10 * 1024);
            await MessagePackSerializer.SerializeAsync(stream, data);
            var status = false;
            if (stream == null || stream.Length == 0)
            {
                throw new ArgumentNullException(nameof(stream));
            }

            var now = DateTime.Now;
            var ticks = now.Ticks;
            var tid = Environment.CurrentManagedThreadId;
            var fileName = $"{ticks}-{tid}-{dataCount}.dat";
            try
            {
                var path = Path.Combine(new[] { filePath, fileType.ToString() }.Concat(folders).Append(fileName).ToArray());
                var dir = new FileInfo(path).Directory;
                if (dir is { Exists: false })
                {
                    dir.Create();
                }

                stream.Position = 0;
                await using var fileStream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
                // 重置流位置，确保从头开始写入
                stream.Position = 0;
                await stream.CopyToAsync(fileStream);
                status = true;
            }
            catch (IOException ex)
            {
                _logger.Error(ex, "{fileType} {fileName} Write IOException {Error}", fileType, fileName, ex);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "{fileType} {fileName} Write  Exception {Error}", fileType, fileName, ex);
            }

            return status;
        }
        public static async Task<List<T>> ReadFileAsync<T>(string filePath, FileType fileType, int size, bool remove = false, params string[] folders)
        {
            List<T> results = new List<T>();
            var path = Path.Combine(new[] { filePath, fileType.ToString() }.Concat(folders).ToArray());
            var directory = new DirectoryInfo(path);
            if (!directory.Exists)
                return results;

            var files = directory.GetFiles()
                .OrderBy(f => f.CreationTime)
                .Take(size)
                .ToArray();

            foreach (var file in files)
            {
                try
                {
                    await using var stream = File.OpenRead(file.FullName);
                    var deserializedObject = await MessagePackSerializer.DeserializeAsync<List<T>>(stream);
                    if (deserializedObject == null)
                        continue;

                    results.AddRange(deserializedObject);
                }
                catch (UnauthorizedAccessException ex)
                {
                    remove = false;
                    _logger.Error(ex, "{path} No Read Permission ", path);
                }
                catch (IOException ex)
                {
                    remove = false;
                    _logger.Error(ex, "{path} Read IOException", path);
                }
                catch (Exception ex)
                {
                    remove = false;
                    _logger.Error(ex, "{path} Read Exception", path);
                }
                finally
                {
                    if (remove)
                    {
                        file.Delete();
                    }
                }
            }

            return results;
        }

        /// <summary>
        /// 读取文件
        /// </summary>
        /// <param name="fileType"></param>
        /// <param name="fileName">文件名称</param>
        /// <returns></returns>
        public async Task<MemoryStream> ReadToAsync(FileType fileType, string fileName)
        {
            var memoryStream = new MemoryStream();
            try
            {
                var filePath = Path.Combine(_filePath, fileType.ToString(), fileName);
                if (!File.Exists(filePath))
                {
                    throw new FileNotFoundException($"File Does Not Exist: {filePath}");
                }

                var fileInfo = new FileInfo(filePath);
                if (fileInfo.Length < 1024 * 1024)
                {
                    var fileBytes = await File.ReadAllBytesAsync(filePath);
                    memoryStream = new MemoryStream(fileBytes);
                }
                else
                {
                    await using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read);
                    await fileStream.CopyToAsync(memoryStream);
                }
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.Error(ex, "{fileType} {fileName} No Read Permission ", fileType, fileName);
            }
            catch (IOException ex)
            {
                _logger.Error(ex, "{fileType} {fileName} Read IOException", fileType, fileName);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "{fileType} {fileName} Read Exception", fileType, fileName);
            }

            memoryStream.Position = 0;
            return memoryStream;
        }
        /// <summary>
        /// 读取指定类型/子目录下的所有文件，返回文件路径和内存流（调用方负责释放流）
        /// </summary>
        /// <param name="fileType">文件类型</param>
        /// <param name="token">取消令牌</param>
        /// <param name="pattern">可选的子目录名称</param>
        /// <returns>异步枚举，每个元素包含文件完整路径和内存流</returns>
        public async IAsyncEnumerable<(string FilePath, MemoryStream? Stream)> ReadAllWithPathAsync(
           FileType fileType,
           [EnumeratorCancellation] CancellationToken token,
           string? pattern = null)
        {
            var searchPath = string.IsNullOrEmpty(pattern)
                ? Path.Combine(_filePath, fileType.ToString())
                : Path.Combine(_filePath, fileType.ToString(), pattern);

            var files = await Task.Run(() =>
            {
                try
                {
                    DirectoryInfo dir = new(searchPath);
                    if (!dir.Exists)
                        return Array.Empty<string>();

                    return [.. dir.GetFiles()
                          .OrderBy(f => f.CreationTime)   // 按创建时间排序，先失败的先重试
                          .Select(f => f.FullName)];
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, $"{nameof(ReadAllWithPathAsync)} list files error: {searchPath}");
                    return [];
                }
            }, token);

            foreach (var filePath in files)
            {
                token.ThrowIfCancellationRequested();

                MemoryStream? stream = null;
                try
                {
                    byte[] bytes = await File.ReadAllBytesAsync(filePath, token);
                    stream = new MemoryStream(bytes);
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Failed to read file: {filePath}", filePath);
                    stream?.Dispose();
                }
                yield return (filePath, stream);

            }
        }
        /// <summary>
        /// 读取所有文件
        /// </summary>
        public async IAsyncEnumerable<MemoryStream> ReadAllAsync(FileType fileType, [EnumeratorCancellation] CancellationToken token, string? pattern = null, bool remove = false)
        {
            var files = await Task.Run(IEnumerable<string> () =>
            {
                try
                {
                    var path = Path.Combine(_filePath, fileType.ToString());
                    DirectoryInfo? dir;
                    if (string.IsNullOrEmpty(pattern))
                    {
                        dir = new DirectoryInfo(path);
                    }
                    else
                    {
                        dir = new FileInfo(Path.Combine(path, pattern)).Directory;
                    }

                    if (!(dir?.Exists ?? false))
                    {
                        return Array.Empty<string>();
                    }

                    var list = pattern != null ? Directory.GetFiles(path, pattern) : Directory.GetFiles(path);
                    return list.OrderBy(f => f);
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, $"{nameof(ReadAllAsync)} Task");
                    return Array.Empty<string>();
                }
            }, token);
            foreach (var filePath in files)
            {
                var data = await File.ReadAllBytesAsync(filePath, token).ConfigureAwait(false);
                yield return new MemoryStream(data);
                if (remove)
                {
                    RemoveFile(filePath);
                }

                token.ThrowIfCancellationRequested();
            }

            //读取完后删除对应文件夹
            try
            {
                var dirPath = new FileInfo(Path.Combine(_filePath, pattern ?? "")).Directory?.FullName;
                if (dirPath != null)
                {
                    DeleteEmptyDirectories(dirPath);
                }
            }
            catch (Exception e)
            {
                _logger.Error(e, nameof(ReadAllAsync));
            }
        }

        private static void DeleteEmptyDirectories(string dirPath)
        {
            var dir = new DirectoryInfo(dirPath);

            // 递归向上删除空目录
            while (dir != null && dir.Exists)
            {
                try
                {
                    // 检查目录是否为空
                    if (!dir.EnumerateFileSystemInfos().Any())
                    {
                        dir.Delete(false);
                        dir = dir.Parent;
                    }
                    else
                    {
                        break; // 目录非空，停止删除
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    // 处理权限或文件正在使用的情况
                    break;
                }
            }
        }

        /// <summary>
        /// 从文件读取数据
        /// </summary>
        /// <typeparam name="T">读取出来要转换的类型</typeparam>
        /// <param name="fileType">文件类型</param>
        /// <param name="size">一次读取大小</param>
        /// <param name="remove">读取后是否删除</param>
        /// <param name="folders">文件路径</param>
        /// <returns></returns>
        public async Task<List<T>> ReadFileAsync<T>(FileType fileType, int size, bool remove = false, params string[] folders)
        {
            List<T> results = new List<T>();
            var path = Path.Combine(new[] { _filePath, fileType.ToString() }.Concat(folders).ToArray());
            var directory = new DirectoryInfo(path);
            if (!directory.Exists)
                return results;

            var files = directory.GetFiles()
                .OrderBy(f => f.CreationTime)
                .Take(size)
                .ToArray();

            foreach (var file in files)
            {
                try
                {
                    await using var stream = File.OpenRead(file.FullName);
                    var deserializedObject = await MessagePackSerializer.DeserializeAsync<List<T>>(stream);
                    if (deserializedObject == null)
                        continue;

                    results.AddRange(deserializedObject);
                }
                catch (UnauthorizedAccessException ex)
                {
                    remove = false;
                    _logger.Error(ex, "{path} No Read Permission ", path);
                }
                catch (IOException ex)
                {
                    remove = false;
                    _logger.Error(ex, "{path} Read IOException", path);
                }
                catch (Exception ex)
                {
                    remove = false;
                    _logger.Error(ex, "{path} Read Exception", path);
                }
                finally
                {
                    if (remove)
                    {
                        file.Delete();
                    }
                }
            }

            return results;
        }
        /// <summary>
        /// 删除文件
        /// </summary>
        public void RemoveFile(string filePath)
        {
            try
            {
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                }

                _logger.Info("Remove file'{filePath}' ", filePath);
            }
            catch (IOException ex) // 捕获文件被占用等I/O异常
            {
                _logger.Error(ex, "{filePath} Remove IOException", filePath);
            }
            catch (UnauthorizedAccessException ex) // 捕获权限不足异常
            {
                _logger.Error(ex, "{filePath} No Remove Permission ", filePath);
            }
            catch (Exception ex) // 捕获其他未预料到的异常
            {
                _logger.Error(ex, "{filePath} Remove Exception", filePath);
            }
        }

        /// <summary>
        /// 按项目/每天的路径写入文件
        /// </summary>
        /// <param name="folder"></param>
        /// <param name="obj"></param>
        /// <param name="count"></param>
        /// <returns></returns>
        /// <exception cref="ArgumentNullException"></exception>
        public async static Task WriteFilesAtDailyIntervalAsync(string folder, object obj, int count)
        {
            try
            {
                DateTime dt = DateTime.Now;
                string path = Path.Combine(Directory.GetCurrentDirectory(), AppSettings.Current.FileSettings.TempDirectoryPath, folder, dt.ToString("yyyyMMdd"), $"{FileNameIdUtils.NextId}-{count}.bin");
                string? dir = Path.GetDirectoryName(path);
                if (dir is null)
                    throw new ArgumentNullException(path);
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                if (obj is Stream stream)
                {
                    await WriteFilesAsync(stream, path);
                    return;
                }

                using Stream stream2 = new MemoryStream();
                await MessagePackSerializer.SerializeAsync(stream2, obj);
                await WriteFilesAsync(stream2, path);
            }
            catch (Exception e)
            {
                _logger.Error(e, folder);
            }
        }

        private async static Task WriteFilesAsync(Stream stream, string path)
        {
            stream.Position = 0;
            using (FileStream fileStream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write))
                await stream.CopyToAsync(fileStream);
        }

        /// <summary>
        /// 按项目的路径写入文件
        /// </summary>
        /// <param name="path"></param>
        /// <param name="obj"></param>
        /// <param name="count"></param>
        /// <returns></returns>
        /// <exception cref="ArgumentNullException"></exception>
        public async static Task<bool> WriteBackupFileAsync(string path, object obj, int count)
        {
            if (count <= 0)
                return false;

            try
            {
                //string filePath = $"{path}/{FileNameIdUtils.NextId}-{count}.bin";
                string filePath = Path.Combine(Directory.GetCurrentDirectory(), AppSettings.Current.FileSettings.TempDirectoryPath, path, $"{FileNameIdUtils.NextIdStr()}-{count}.bin");
                string? dir = Path.GetDirectoryName(filePath);
                if (dir is null)
                    throw new ArgumentNullException(filePath);
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                using Stream stream = new MemoryStream();
                await MessagePackSerializer.SerializeAsync(stream, obj);
                stream.Position = 0;
                using (FileStream fileStream = new FileStream(filePath, FileMode.OpenOrCreate, FileAccess.Write))
                    await stream.CopyToAsync(fileStream);

                _logger.Info("{WriteBackupFileAsync}:write file '{filePath}' ", nameof(WriteBackupFileAsync), filePath);
                return true;
            }
            catch (Exception e)
            {
                _logger.Error(e, "fail:{folder} count={count}", path, count);
                return false;
            }
        }

        /// <summary>
        /// 读取指定目录下的文件
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="path"></param>
        /// <param name="size"></param>
        /// <returns></returns>
        public async static Task<List<T>> ReadFileAsync<T>(string path, int size = 1)
        {
            List<T> results = new List<T>();
            string filePath = Path.Combine(GetTempPath(), path);
            var directory = new DirectoryInfo(filePath);
            if (!directory.Exists)
                return results;

            var files = directory.GetFiles()
                .OrderBy(f => f.Name)
                .Take(size)
                .ToArray();

            foreach (var file in files)
            {
                _logger.Info("{ReadFile}:Read file'{fileName}' ", nameof(ReadFileAsync), file.FullName);
                try
                {
                    using (var stream = File.OpenRead(file.FullName))
                    {
                        var deserializedObject = await MessagePackSerializer.DeserializeAsync<List<T>>(stream);
                        if (deserializedObject == null)
                            continue;

                        results.AddRange(deserializedObject);
                    }
                }
                catch (Exception e)
                {
                    _logger.Error(e, file.FullName);
                }
                finally
                {
                    file.Delete();
                    _logger.Info("{ReadFile}:Delete file'{fileName}' ", nameof(ReadFileAsync), file.FullName);
                }
            }

            return results;
        }
    }
}
