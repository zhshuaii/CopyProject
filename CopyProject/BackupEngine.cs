using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.IO;
using System.IO.Compression;
using System.Security.AccessControl;
using System.Threading;

namespace CopyProject
{
    internal sealed class BackupProgress
    {
        public BackupProgress(int percent, string message)
        {
            Percent = Math.Max(0, Math.Min(100, percent));
            Message = message ?? string.Empty;
        }

        public int Percent { get; private set; }
        public string Message { get; private set; }
    }

    internal sealed class BackupEngine
    {
        private const string SqlInstance = @".\WINCC";
        private const string SqlServiceName = "MSSQL$WINCC";
        private const string ConnectionString = "Server=.\\WINCC;Database=master;Integrated Security=true;Connect Timeout=5;";

        public string CreateZipBackup(string mcpPath, string targetRoot, IProgress<BackupProgress> progress)
        {
            ValidateInput(mcpPath, targetRoot);

            string sourceMcp = Path.GetFullPath(mcpPath);
            string sourceDir = Path.GetDirectoryName(sourceMcp);
            string projectName = Path.GetFileNameWithoutExtension(sourceMcp);
            string outputRoot = Path.GetFullPath(targetRoot);

            EnsureTargetIsSafe(sourceDir, outputRoot);
            EnsureSqlIsReachable();
            Directory.CreateDirectory(outputRoot);

            string finalZipPath = GetUniqueOutputPath(outputRoot, projectName);
            string partialZipPath = finalZipPath + ".partial";
            string workDir = Path.Combine(outputRoot, ".CopyProject_" + Guid.NewGuid().ToString("N"));
            string projectWorkDir = Path.Combine(workDir, projectName);
            string sqlWorkDir = Path.Combine(workDir, "Sql");

            try
            {
                Directory.CreateDirectory(workDir);
                TryHideDirectory(workDir);
                GrantSqlServiceModifyAccess(workDir);
                Directory.CreateDirectory(projectWorkDir);
                Directory.CreateDirectory(sqlWorkDir);

                int totalFiles = CountNormalFiles(sourceDir);
                int copiedFiles = 0;

                Report(progress, 0, "正在复制工程文件...");
                CopyDirectory(sourceDir, projectWorkDir, ref copiedFiles, totalFiles, progress);
                Report(progress, 60, "工程文件复制完成");

                Report(progress, 65, "正在在线复制组态数据库...");
                CopyDatabase(projectName, sourceDir, projectWorkDir, sqlWorkDir);
                Report(progress, 75, "组态数据库复制完成");

                Report(progress, 80, "正在在线复制 Runtime 数据库...");
                CopyDatabase(projectName + "RT", sourceDir, projectWorkDir, sqlWorkDir);
                Report(progress, 90, "Runtime 数据库复制完成");

                Report(progress, 95, "正在打包 ZIP...");
                TryDeleteFile(partialZipPath);
                ZipFile.CreateFromDirectory(projectWorkDir, partialZipPath, CompressionLevel.Fastest, true);

                // 正式 ZIP 出现之前必须先把工作区清理干净。
                Report(progress, 98, "正在清理临时文件...");
                DeleteDirectoryWithRetry(workDir);
                workDir = null;

                File.Move(partialZipPath, finalZipPath);
                Report(progress, 100, "备份完成");
                return finalZipPath;
            }
            catch
            {
                TryDeleteFile(partialZipPath);
                throw;
            }
            finally
            {
                TryDeleteDirectory(workDir);
            }
        }

        private static void ValidateInput(string mcpPath, string targetRoot)
        {
            if (string.IsNullOrWhiteSpace(mcpPath))
                throw new InvalidOperationException("请选择 WinCC MCP 项目文件。");

            if (!File.Exists(mcpPath))
                throw new FileNotFoundException("未找到 WinCC MCP 项目文件。", mcpPath);

            if (!string.Equals(Path.GetExtension(mcpPath), ".mcp", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("请选择 .mcp 格式的 WinCC 项目文件。");

            if (string.IsNullOrWhiteSpace(targetRoot))
                throw new InvalidOperationException("请选择备份 ZIP 的保存目录。");
        }

        private static void EnsureTargetIsSafe(string sourceDir, string targetRoot)
        {
            if (targetRoot.StartsWith(@"\\", StringComparison.Ordinal))
                throw new InvalidOperationException("当前版本暂不支持直接备份到 UNC 网络路径，请选择本地磁盘目录。");

            string root = Path.GetPathRoot(targetRoot);
            if (!string.IsNullOrEmpty(root))
            {
                DriveInfo drive = new DriveInfo(root);
                if (drive.DriveType == DriveType.Network)
                    throw new InvalidOperationException("当前版本暂不支持映射网络驱动器，请选择本地磁盘目录。");
            }

            string source = AddTrailingSeparator(Path.GetFullPath(sourceDir));
            string target = AddTrailingSeparator(Path.GetFullPath(targetRoot));

            if (target.StartsWith(source, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("备份目录不能位于源 WinCC 项目目录内部，否则会造成递归复制。");
        }

        private static void EnsureSqlIsReachable()
        {
            try
            {
                using (SqlConnection connection = new SqlConnection(ConnectionString))
                {
                    connection.Open();
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("无法连接 WinCC SQL Server 实例 " + SqlInstance + "。请确认 WinCC/SQL Server 正常运行。", ex);
            }
        }

        private static string GetUniqueOutputPath(string outputRoot, string projectName)
        {
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string path = Path.Combine(outputRoot, projectName + "_" + stamp + ".zip");
            int index = 2;

            while (File.Exists(path) || File.Exists(path + ".partial"))
            {
                path = Path.Combine(outputRoot, projectName + "_" + stamp + "_" + index + ".zip");
                index++;
            }

            return path;
        }

        private static void CopyDatabase(string baseName, string sourceDir, string destinationDir, string sqlWorkDir)
        {
            string mdfPath = Path.Combine(sourceDir, baseName + ".mdf");
            string ldfPath = Path.Combine(sourceDir, baseName + ".ldf");

            if (!File.Exists(mdfPath) || !File.Exists(ldfPath))
                throw new FileNotFoundException("未找到数据库文件：" + baseName + ".mdf/.ldf");

            string databaseName;
            if (!TryGetAttachedDatabaseName(mdfPath, out databaseName))
            {
                CopyFileWithRetry(mdfPath, Path.Combine(destinationDir, Path.GetFileName(mdfPath)));
                CopyFileWithRetry(ldfPath, Path.Combine(destinationDir, Path.GetFileName(ldfPath)));
                return;
            }

            string token = Guid.NewGuid().ToString("N");
            string backupPath = Path.Combine(sqlWorkDir, baseName + "_" + token + ".bak");
            string temporaryDatabase = "CopyProject_TEMP_" + token;

            try
            {
                BackupDatabase(databaseName, backupPath);
                RestoreDatabase(temporaryDatabase, baseName, backupPath, destinationDir);
                DetachDatabase(temporaryDatabase);

                string restoredMdf = Path.Combine(destinationDir, baseName + ".mdf");
                string restoredLdf = Path.Combine(destinationDir, baseName + ".ldf");
                if (!File.Exists(restoredMdf) || !File.Exists(restoredLdf))
                    throw new InvalidOperationException("数据库已还原并分离，但未找到生成的 MDF/LDF 文件：" + baseName);
            }
            finally
            {
                // DETACH 成功后 DB_ID 已不存在；如果 RESTORE/DETACH 中途失败则尝试删除临时库。
                TryDropTemporaryDatabase(temporaryDatabase);
                TryDeleteFile(backupPath);
            }
        }

        private static bool TryGetAttachedDatabaseName(string mdfPath, out string databaseName)
        {
            databaseName = null;
            string expectedPath = NormalizePath(mdfPath);
            const string sql = "SELECT DB_NAME(database_id), physical_name FROM sys.master_files WHERE type = 0;";

            using (SqlConnection connection = new SqlConnection(ConnectionString))
            using (SqlCommand command = new SqlCommand(sql, connection))
            {
                command.CommandTimeout = 0;
                connection.Open();

                using (SqlDataReader reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        string name = reader.IsDBNull(0) ? null : reader.GetString(0);
                        string physicalPath = reader.IsDBNull(1) ? null : reader.GetString(1);
                        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(physicalPath))
                            continue;

                        if (string.Equals(expectedPath, NormalizePath(physicalPath), StringComparison.OrdinalIgnoreCase))
                        {
                            databaseName = name;
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        private static void BackupDatabase(string databaseName, string backupPath)
        {
            string sql = "BACKUP DATABASE " + QuoteIdentifier(databaseName) + " TO DISK = @backup WITH INIT, COPY_ONLY;";

            using (SqlConnection connection = new SqlConnection(ConnectionString))
            using (SqlCommand command = new SqlCommand(sql, connection))
            {
                command.CommandTimeout = 0;
                command.Parameters.AddWithValue("@backup", backupPath);
                connection.Open();
                command.ExecuteNonQuery();
            }
        }

        private static void RestoreDatabase(string temporaryDatabase, string baseName, string backupPath, string destinationDir)
        {
            List<RestoreFileInfo> files = ReadBackupFileList(backupPath);
            RestoreFileInfo dataFile = null;
            RestoreFileInfo logFile = null;
            int dataCount = 0;
            int logCount = 0;

            foreach (RestoreFileInfo file in files)
            {
                if (string.Equals(file.Type, "D", StringComparison.OrdinalIgnoreCase))
                {
                    dataCount++;
                    if (dataFile == null)
                        dataFile = file;
                }
                else if (string.Equals(file.Type, "L", StringComparison.OrdinalIgnoreCase))
                {
                    logCount++;
                    if (logFile == null)
                        logFile = file;
                }
            }

            if (dataCount != 1 || logCount != 1 || dataFile == null || logFile == null)
                throw new InvalidOperationException("数据库包含非预期的数据文件结构。为避免写入原始数据库路径，CopyProject 已停止恢复。");

            string dataDestination = Path.Combine(destinationDir, baseName + ".mdf");
            string logDestination = Path.Combine(destinationDir, baseName + ".ldf");

            string sql =
                "RESTORE DATABASE " + QuoteIdentifier(temporaryDatabase) + " FROM DISK = @backup WITH " +
                "MOVE N'" + EscapeSqlLiteral(dataFile.LogicalName) + "' TO N'" + EscapeSqlLiteral(dataDestination) + "', " +
                "MOVE N'" + EscapeSqlLiteral(logFile.LogicalName) + "' TO N'" + EscapeSqlLiteral(logDestination) + "', " +
                "RECOVERY;";

            using (SqlConnection connection = new SqlConnection(ConnectionString))
            using (SqlCommand command = new SqlCommand(sql, connection))
            {
                command.CommandTimeout = 0;
                command.Parameters.AddWithValue("@backup", backupPath);
                connection.Open();
                command.ExecuteNonQuery();
            }
        }

        private static List<RestoreFileInfo> ReadBackupFileList(string backupPath)
        {
            List<RestoreFileInfo> result = new List<RestoreFileInfo>();
            const string sql = "RESTORE FILELISTONLY FROM DISK = @backup;";

            using (SqlConnection connection = new SqlConnection(ConnectionString))
            using (SqlCommand command = new SqlCommand(sql, connection))
            {
                command.CommandTimeout = 0;
                command.Parameters.AddWithValue("@backup", backupPath);
                connection.Open();

                using (SqlDataReader reader = command.ExecuteReader())
                {
                    int logicalNameOrdinal = reader.GetOrdinal("LogicalName");
                    int typeOrdinal = reader.GetOrdinal("Type");

                    while (reader.Read())
                    {
                        result.Add(new RestoreFileInfo(
                            reader.GetString(logicalNameOrdinal),
                            reader.GetString(typeOrdinal)));
                    }
                }
            }

            return result;
        }

        private static void DetachDatabase(string databaseName)
        {
            using (SqlConnection connection = new SqlConnection(ConnectionString))
            using (SqlCommand command = new SqlCommand("EXEC master.dbo.sp_detach_db @db, 'true';", connection))
            {
                command.CommandTimeout = 0;
                command.Parameters.AddWithValue("@db", databaseName);
                connection.Open();
                command.ExecuteNonQuery();
            }
        }

        private static void TryDropTemporaryDatabase(string databaseName)
        {
            try
            {
                string sql =
                    "IF DB_ID(@db) IS NOT NULL BEGIN " +
                    "ALTER DATABASE " + QuoteIdentifier(databaseName) + " SET SINGLE_USER WITH ROLLBACK IMMEDIATE; " +
                    "DROP DATABASE " + QuoteIdentifier(databaseName) + "; END";

                using (SqlConnection connection = new SqlConnection(ConnectionString))
                using (SqlCommand command = new SqlCommand(sql, connection))
                {
                    command.CommandTimeout = 0;
                    command.Parameters.AddWithValue("@db", databaseName);
                    connection.Open();
                    command.ExecuteNonQuery();
                }
            }
            catch
            {
                // 不覆盖主异常；外层工作目录清理仍会再次暴露文件占用问题。
            }
        }

        private static int CountNormalFiles(string sourceDir)
        {
            int count = 0;

            foreach (string file in Directory.GetFiles(sourceDir))
            {
                if (ShouldCopyFile(file))
                    count++;
            }

            foreach (string directory in Directory.GetDirectories(sourceDir))
                count += CountNormalFiles(directory);

            return count;
        }

        private static void CopyDirectory(string sourceDir, string targetDir, ref int copiedFiles, int totalFiles, IProgress<BackupProgress> progress)
        {
            Directory.CreateDirectory(targetDir);

            foreach (string file in Directory.GetFiles(sourceDir))
            {
                if (!ShouldCopyFile(file))
                    continue;

                string targetFile = Path.Combine(targetDir, Path.GetFileName(file));
                CopyFileWithRetry(file, targetFile);
                copiedFiles++;

                int percent = totalFiles <= 0 ? 60 : (int)(copiedFiles * 60.0 / totalFiles);
                Report(progress, percent, "正在复制工程文件 " + copiedFiles + "/" + totalFiles);
            }

            foreach (string directory in Directory.GetDirectories(sourceDir))
            {
                string targetSubDir = Path.Combine(targetDir, Path.GetFileName(directory));
                CopyDirectory(directory, targetSubDir, ref copiedFiles, totalFiles, progress);
            }
        }

        private static bool ShouldCopyFile(string file)
        {
            string extension = Path.GetExtension(file);
            return !string.Equals(extension, ".mdf", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(extension, ".ldf", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(extension, ".lck", StringComparison.OrdinalIgnoreCase);
        }

        private static void CopyFileWithRetry(string sourcePath, string destinationPath)
        {
            int[] delays = { 200, 500, 1000 };
            Exception lastError = null;

            for (int attempt = 0; attempt < delays.Length; attempt++)
            {
                try
                {
                    string destinationDirectory = Path.GetDirectoryName(destinationPath);
                    if (!string.IsNullOrEmpty(destinationDirectory))
                        Directory.CreateDirectory(destinationDirectory);

                    using (FileStream source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 131072, FileOptions.SequentialScan))
                    using (FileStream destination = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 131072, FileOptions.SequentialScan))
                    {
                        source.CopyTo(destination, 131072);
                    }

                    File.SetLastWriteTimeUtc(destinationPath, File.GetLastWriteTimeUtc(sourcePath));
                    return;
                }
                catch (IOException ex)
                {
                    lastError = ex;
                }
                catch (UnauthorizedAccessException ex)
                {
                    lastError = ex;
                }

                TryDeleteFile(destinationPath);
                if (attempt < delays.Length - 1)
                    Thread.Sleep(delays[attempt]);
            }

            throw new IOException("复制文件失败：" + sourcePath, lastError);
        }

        private static void GrantSqlServiceModifyAccess(string workDir)
        {
            if (!SupportsWindowsAcl(workDir))
                return;

            string serviceAccount = GetSqlServiceAccount();

            try
            {
                DirectorySecurity security = Directory.GetAccessControl(workDir);
                FileSystemAccessRule rule = new FileSystemAccessRule(
                    serviceAccount,
                    FileSystemRights.Modify,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None,
                    AccessControlType.Allow);

                security.AddAccessRule(rule);
                Directory.SetAccessControl(workDir, security);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "无法为 WinCC SQL Server 临时授予工作目录权限。\r\n目录：" + workDir +
                    "\r\nSQL 服务账户：" + serviceAccount +
                    "\r\n请选择其他目录，或以管理员身份运行 CopyProject。", ex);
            }
        }

        private static string GetSqlServiceAccount()
        {
            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + SqlServiceName))
                {
                    string account = key == null ? null : key.GetValue("ObjectName") as string;
                    if (!string.IsNullOrWhiteSpace(account))
                        return NormalizeServiceAccount(account);
                }
            }
            catch
            {
                // 读取失败时使用 WINCC 命名实例常见的虚拟服务账户。
            }

            return @"NT SERVICE\" + SqlServiceName;
        }

        private static string NormalizeServiceAccount(string account)
        {
            if (string.Equals(account, "LocalSystem", StringComparison.OrdinalIgnoreCase))
                return @"NT AUTHORITY\SYSTEM";
            if (string.Equals(account, "NetworkService", StringComparison.OrdinalIgnoreCase))
                return @"NT AUTHORITY\NETWORK SERVICE";
            if (string.Equals(account, "LocalService", StringComparison.OrdinalIgnoreCase))
                return @"NT AUTHORITY\LOCAL SERVICE";
            return account;
        }

        private static bool SupportsWindowsAcl(string path)
        {
            try
            {
                string root = Path.GetPathRoot(path);
                if (string.IsNullOrEmpty(root))
                    return true;

                string format = new DriveInfo(root).DriveFormat;
                return string.Equals(format, "NTFS", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(format, "ReFS", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return true;
            }
        }

        private static string QuoteIdentifier(string value)
        {
            return "[" + value.Replace("]", "]]" ) + "]";
        }

        private static string EscapeSqlLiteral(string value)
        {
            return value.Replace("'", "''");
        }

        private static string NormalizePath(string path)
        {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        private static string AddTrailingSeparator(string path)
        {
            string value = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return value + Path.DirectorySeparatorChar;
        }

        private static void TryHideDirectory(string path)
        {
            try
            {
                FileAttributes attributes = File.GetAttributes(path);
                File.SetAttributes(path, attributes | FileAttributes.Hidden);
            }
            catch
            {
                // 隐藏属性只是界面体验，不影响备份。
            }
        }

        private static void DeleteDirectoryWithRetry(string path)
        {
            int[] delays = { 200, 500, 1000 };
            Exception lastError = null;

            for (int attempt = 0; attempt < delays.Length; attempt++)
            {
                try
                {
                    if (Directory.Exists(path))
                        Directory.Delete(path, true);
                    return;
                }
                catch (IOException ex)
                {
                    lastError = ex;
                }
                catch (UnauthorizedAccessException ex)
                {
                    lastError = ex;
                }

                if (attempt < delays.Length - 1)
                    Thread.Sleep(delays[attempt]);
            }

            throw new IOException("备份内容已生成，但临时工作目录无法清理：" + path, lastError);
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
            }
        }

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                    Directory.Delete(path, true);
            }
            catch
            {
            }
        }

        private static void Report(IProgress<BackupProgress> progress, int percent, string message)
        {
            if (progress != null)
                progress.Report(new BackupProgress(percent, message));
        }

        private sealed class RestoreFileInfo
        {
            public RestoreFileInfo(string logicalName, string type)
            {
                LogicalName = logicalName;
                Type = type;
            }

            public string LogicalName { get; private set; }
            public string Type { get; private set; }
        }
    }
}
