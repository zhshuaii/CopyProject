using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.IO;
using System.Security.Principal;
using System.Threading;

namespace CopyProject
{
    internal sealed class SqlDatabaseBackup : IDatabaseBackup
    {
        private readonly string connectionString;
        private readonly bool requireWinccInstance;
        public string ServiceAccount { get; private set; }
        private const int MetadataTimeout = 30;
        private const int CleanupTimeout = 60;
        private const int BackupTimeout = 3600;

        public SqlDatabaseBackup() : this(@".\WINCC", null) { }

        // The alternate instance/account are only used by the isolated integration tests.
        internal SqlDatabaseBackup(string instance, string account)
        {
            connectionString = new SqlConnectionStringBuilder { DataSource = instance, InitialCatalog = "master", IntegratedSecurity = true, ConnectTimeout = 5, Pooling = false, Enlist = false, ApplicationName = "CopyProject" }.ConnectionString;
            ServiceAccount = account;
            requireWinccInstance = instance.Equals(@".\WINCC", StringComparison.OrdinalIgnoreCase);
        }

        public DatabaseInfo[] Inspect(BackupPlan plan, CancellationToken token)
        {
            using (SqlConnection connection = Open())
            {
                RequireMetadata(connection);
                const string createPermission = "SELECT CASE WHEN IS_SRVROLEMEMBER('sysadmin')=1 OR IS_SRVROLEMEMBER('dbcreator')=1 OR HAS_PERMS_BY_NAME(NULL,'SERVER','CREATE ANY DATABASE')=1 THEN 1 ELSE 0 END;";
                if (ScalarInt(connection, createPermission) != 1) throw new InvalidOperationException("当前 SQL 登录缺少创建/恢复临时数据库权限。Windows 管理员不等于 SQL 管理员。");
                var result = new[] { InspectOne(connection, plan, plan.ProjectName, token), InspectOne(connection, plan, plan.ProjectName + "RT", token) };
                if (result[0].Id == result[1].Id) throw new InvalidOperationException("组态库与 Runtime 库身份重复。");
                if (ServiceAccount == null) ServiceAccount = ReadServiceAccount();
                new NTAccount(ServiceAccount).Translate(typeof(SecurityIdentifier));
                return result;
            }
        }

        private static DatabaseInfo InspectOne(SqlConnection connection, BackupPlan plan, string baseName, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            string mdf = PathSafety.Child(plan.SourceDirectory, baseName + ".mdf");
            string ldf = PathSafety.Child(plan.SourceDirectory, baseName + ".ldf");
            if (!File.Exists(mdf) || !File.Exists(ldf)) throw new FileNotFoundException("必须存在数据库文件：" + baseName + ".mdf/.ldf");
            int id = 0;
            string name = null;
            using (var command = new SqlCommand("SELECT d.database_id,d.name,d.state_desc,f.physical_name FROM sys.databases d JOIN sys.master_files f ON f.database_id=d.database_id WHERE f.type=0;", connection))
            {
                command.CommandTimeout = MetadataTimeout;
                using (SqlDataReader reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        if (!string.Equals(Path.GetFullPath(reader.GetString(3)), mdf, StringComparison.OrdinalIgnoreCase)) continue;
                        if (id != 0 || reader.GetString(2) != "ONLINE") throw new InvalidOperationException("数据库状态或路径身份不明确：" + baseName);
                        id = reader.GetInt32(0);
                        name = reader.GetString(1);
                    }
                }
            }
            if (id <= 4) throw new InvalidOperationException("无法确认本机实例中的在线用户数据库：" + mdf + "。不会回退到直接复制 MDF/LDF。");
            List<SqlFile> files = ReadDatabaseFiles(connection, id);
            ValidatePair(files);
            SqlFile data = files.Find(f => f.Type == "D");
            SqlFile log = files.Find(f => f.Type == "L");
            if (!SamePath(data.Physical, mdf) || !SamePath(log.Physical, ldf)) throw new InvalidOperationException("数据库文件与项目目录不一致：" + name);
            using (var command = new SqlCommand("SELECT HAS_PERMS_BY_NAME(@db,'DATABASE','BACKUP DATABASE');", connection))
            {
                command.CommandTimeout = MetadataTimeout;
                command.Parameters.Add("@db", SqlDbType.NVarChar, 128).Value = name;
                if (Convert.ToString(command.ExecuteScalar()) != "1") throw new InvalidOperationException("没有数据库 BACKUP 权限：" + name);
            }
            return new DatabaseInfo { Name = name, Id = id, BaseName = baseName, DataPath = mdf, LogPath = ldf, DataLogical = data.Logical, LogLogical = log.Logical, Bytes = checked(data.Bytes + log.Bytes) };
        }

        public void Export(DatabaseInfo database, BackupWorkspace workspace, string slot, CancellationToken token)
        {
            string temporary = workspace.TemporaryDatabase(slot);
            string backup = PathSafety.Child(workspace.SqlDirectory, slot + ".bak");
            string mdf = PathSafety.Child(workspace.ProjectDirectory, database.BaseName + ".mdf");
            string ldf = PathSafety.Child(workspace.ProjectDirectory, database.BaseName + ".ldf");
            using (SqlConnection connection = Open())
            {
                RequireMetadata(connection);
                List<SqlFile> live = ReadDatabaseFiles(connection, database.Id);
                ValidatePair(live);
                if (!SamePath(live.Find(f => f.Type == "D").Physical, database.DataPath) || !SamePath(live.Find(f => f.Type == "L").Physical, database.LogPath)) throw new InvalidOperationException("备份前数据库身份发生变化。");
                using (var check = new SqlCommand("SELECT CASE WHEN DB_ID(@name)=@id AND DATABASEPROPERTYEX(@name,'Status')='ONLINE' THEN 1 ELSE 0 END;", connection))
                {
                    check.CommandTimeout = MetadataTimeout;
                    check.Parameters.AddWithValue("@name", database.Name);
                    check.Parameters.AddWithValue("@id", database.Id);
                    if (Convert.ToInt32(check.ExecuteScalar()) != 1) throw new InvalidOperationException("源数据库不再是预检查时的在线数据库。");
                }
                Execute(connection, "BACKUP DATABASE " + Quote(database.Name) + " TO DISK=@backup WITH COPY_ONLY, INIT, CHECKSUM;", BackupTimeout, token, new SqlParameter("@backup", backup));
                List<SqlFile> files = ReadBackupFiles(connection, backup, token);
                ValidatePair(files);
                SqlFile data = files.Find(f => f.Type == "D");
                SqlFile log = files.Find(f => f.Type == "L");
                if (data.Logical != database.DataLogical || log.Logical != database.LogLogical) throw new InvalidOperationException("备份文件结构与预检查结果不一致。");
                if (DatabaseId(connection, temporary) != 0) throw new InvalidOperationException("临时库名已存在，拒绝接管：" + temporary);
                workspace.MarkRestoreAttempt(slot);
                string restore = "IF DB_ID(@temp) IS NOT NULL BEGIN RAISERROR(N'Temporary database already exists',16,1); RETURN; END; RESTORE DATABASE " + Quote(temporary) + " FROM DISK=@backup WITH MOVE N'" + Literal(data.Logical) + "' TO N'" + Literal(mdf) + "', MOVE N'" + Literal(log.Logical) + "' TO N'" + Literal(ldf) + "', RECOVERY, CHECKSUM;";
                Execute(connection, restore, BackupTimeout, token, new SqlParameter("@backup", backup), new SqlParameter("@temp", temporary));
                VerifyOwnedDatabase(connection, temporary, workspace);
                Execute(connection, "EXEC master.dbo.sp_detach_db @dbname=@db, @skipchecks=N'true';", CleanupTimeout, token, new SqlParameter("@db", temporary));
                if (DatabaseId(connection, temporary) != 0 || !File.Exists(mdf) || !File.Exists(ldf)) throw new IOException("分离后数据库状态或输出文件不符合预期：" + temporary);
                workspace.Note("Detached=" + temporary);
            }
        }

        public void Cleanup(BackupWorkspace workspace)
        {
            var errors = new List<Exception>();
            foreach (string slot in workspace.RestoreAttempts)
            {
                string name = workspace.TemporaryDatabase(slot);
                try
                {
                    using (SqlConnection connection = Open())
                    {
                        RequireMetadata(connection);
                        if (DatabaseId(connection, name) == 0) continue;
                        VerifyOwnedDatabase(connection, name, workspace);
                        Execute(connection, "ALTER DATABASE " + Quote(name) + " SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE " + Quote(name) + ";", CleanupTimeout, CancellationToken.None);
                        if (DatabaseId(connection, name) != 0) throw new IOException("临时数据库仍存在：" + name);
                    }
                }
                catch (Exception ex) { errors.Add(new InvalidOperationException("未确认已清理临时库：" + name, ex)); }
            }
            if (errors.Count != 0) throw new AggregateException("SQL 临时库清理失败；保留工作目录，不删除数据库文件。", errors);
        }

        private static void VerifyOwnedDatabase(SqlConnection connection, string name, BackupWorkspace workspace)
        {
            if (name != workspace.TemporaryDatabase("C") && name != workspace.TemporaryDatabase("R")) throw new InvalidOperationException("数据库不属于本任务。");
            int id = DatabaseId(connection, name);
            if (id <= 4) throw new InvalidOperationException("拒绝操作系统库或未知库。");
            List<SqlFile> files = ReadDatabaseFiles(connection, id);
            ValidatePair(files);
            string[] expected = workspace.DatabasePaths(name == workspace.TemporaryDatabase("C") ? "C" : "R");
            if (!SamePath(files.Find(f => f.Type == "D").Physical, expected[0]) || !SamePath(files.Find(f => f.Type == "L").Physical, expected[1])) throw new InvalidOperationException("临时库文件名不匹配任务记录，拒绝操作。");
            foreach (SqlFile file in files)
            {
                PathSafety.NoLinks(file.Physical);
                if (!PathSafety.IsWithin(file.Physical, workspace.ProjectDirectory) || !string.Equals(Path.GetDirectoryName(file.Physical), workspace.ProjectDirectory, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("临时库物理路径不属于本任务，拒绝分离或删除。");
            }
            using (var command = new SqlCommand("SELECT CASE WHEN owner_sid=SUSER_SID() THEN 1 ELSE 0 END FROM sys.databases WHERE database_id=@id;", connection))
            {
                command.CommandTimeout = MetadataTimeout;
                command.Parameters.AddWithValue("@id", id);
                if (Convert.ToString(command.ExecuteScalar()) != "1") throw new InvalidOperationException("临时数据库所有者不是当前 SQL 登录，拒绝删除。");
            }
        }

        internal sealed class SqlFile
        {
            public string Logical;
            public string Physical;
            public string Type;
            public long Bytes;
        }

        internal static void ValidatePair(List<SqlFile> files)
        {
            if (files == null || files.Count != 2 || files.FindAll(f => f.Type == "D").Count != 1 || files.FindAll(f => f.Type == "L").Count != 1) throw new InvalidOperationException("仅支持恰好一个 D 数据文件与一个 L 日志文件，其他类型/附加文件一律拒绝。");
            if (string.IsNullOrWhiteSpace(files[0].Logical) || string.IsNullOrWhiteSpace(files[1].Logical) || string.Equals(files[0].Logical, files[1].Logical, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("数据库逻辑文件名为空或重复。");
        }

        private static List<SqlFile> ReadDatabaseFiles(SqlConnection connection, int id)
        {
            var result = new List<SqlFile>();
            using (var command = new SqlCommand("SELECT name,physical_name,type,CAST(size AS bigint)*8192 FROM sys.master_files WHERE database_id=@id;", connection))
            {
                command.CommandTimeout = MetadataTimeout;
                command.Parameters.AddWithValue("@id", id);
                using (SqlDataReader reader = command.ExecuteReader())
                    while (reader.Read()) result.Add(new SqlFile { Logical = reader.GetString(0), Physical = reader.GetString(1), Type = reader.GetByte(2) == 0 ? "D" : reader.GetByte(2) == 1 ? "L" : "Unsupported", Bytes = reader.GetInt64(3) });
            }
            return result;
        }

        private static List<SqlFile> ReadBackupFiles(SqlConnection connection, string backup, CancellationToken token)
        {
            var result = new List<SqlFile>();
            using (var command = new SqlCommand("RESTORE FILELISTONLY FROM DISK=@backup;", connection))
            {
                command.CommandTimeout = MetadataTimeout;
                command.Parameters.AddWithValue("@backup", backup);
                token.ThrowIfCancellationRequested();
                using (SqlDataReader reader = command.ExecuteReader())
                    while (reader.Read()) result.Add(new SqlFile { Logical = Convert.ToString(reader["LogicalName"]), Physical = Convert.ToString(reader["PhysicalName"]), Type = Convert.ToString(reader["Type"]), Bytes = Convert.ToInt64(reader["Size"]) });
            }
            return result;
        }

        private SqlConnection Open()
        {
            var connection = new SqlConnection(connectionString);
            try
            {
                connection.Open();
                using (var command = new SqlCommand("SELECT CONVERT(nvarchar(128),SERVERPROPERTY('MachineName')),CONVERT(nvarchar(128),SERVERPROPERTY('InstanceName'));", connection))
                {
                    command.CommandTimeout = MetadataTimeout;
                    using (SqlDataReader reader = command.ExecuteReader())
                    {
                        if (!reader.Read() || !string.Equals(reader.GetString(0), Environment.MachineName, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("SQL 实例不是本机实例，拒绝操作。");
                        if (requireWinccInstance && (reader.IsDBNull(1) || !string.Equals(reader.GetString(1), "WINCC", StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("SQL 实例不是预期的 WINCC 实例。");
                    }
                }
                return connection;
            }
            catch { connection.Dispose(); throw; }
        }

        internal static void RequireMetadata(SqlConnection connection)
        {
            const string sql = "SELECT CASE WHEN IS_SRVROLEMEMBER('sysadmin')=1 OR (HAS_PERMS_BY_NAME(NULL,'SERVER','VIEW ANY DEFINITION')=1 AND HAS_PERMS_BY_NAME(NULL,'SERVER','VIEW ANY DATABASE')=1) THEN 1 ELSE 0 END;";
            if (ScalarInt(connection, sql) != 1) throw new InvalidOperationException("无法完整核实 SQL 元数据权限；要求 VIEW ANY DEFINITION 与 VIEW ANY DATABASE（或 sysadmin）。不会推测数据库离线。");
        }

        private static int ScalarInt(SqlConnection connection, string sql)
        {
            using (var command = new SqlCommand(sql, connection)) { command.CommandTimeout = MetadataTimeout; return Convert.ToInt32(command.ExecuteScalar()); }
        }

        private static int DatabaseId(SqlConnection connection, string name)
        {
            using (var command = new SqlCommand("SELECT ISNULL(DB_ID(@db),0);", connection))
            {
                command.CommandTimeout = MetadataTimeout;
                command.Parameters.AddWithValue("@db", name);
                return Convert.ToInt32(command.ExecuteScalar());
            }
        }

        private static void Execute(SqlConnection connection, string sql, int timeout, CancellationToken token, params SqlParameter[] parameters)
        {
            using (var command = new SqlCommand(sql, connection))
            {
                command.CommandTimeout = timeout;
                command.Parameters.AddRange(parameters);
                token.ThrowIfCancellationRequested();
                using (token.Register(() =>
                {
                    try { command.Cancel(); }
                    catch (InvalidOperationException) { /* Completion raced cancellation; the command result and cleanup remain authoritative. */ }
                    catch (SqlException) { /* Cleanup still checks SQL state on a fresh connection. */ }
                }))
                {
                    try { command.ExecuteNonQuery(); }
                    catch (SqlException) when (token.IsCancellationRequested) { throw new OperationCanceledException(token); }
                }
                token.ThrowIfCancellationRequested();
            }
        }

        private static string ReadServiceAccount()
        {
            using (RegistryKey key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\MSSQL$WINCC"))
            {
                string account = key == null ? null : key.GetValue("ObjectName") as string;
                if (string.IsNullOrWhiteSpace(account)) throw new InvalidOperationException("无法确认 MSSQL$WINCC 服务账户，拒绝猜测并授予权限。");
                if (account.Equals("LocalSystem", StringComparison.OrdinalIgnoreCase)) return @"NT AUTHORITY\SYSTEM";
                if (account.Equals("NetworkService", StringComparison.OrdinalIgnoreCase)) return @"NT AUTHORITY\NETWORK SERVICE";
                if (account.Equals("LocalService", StringComparison.OrdinalIgnoreCase)) return @"NT AUTHORITY\LOCAL SERVICE";
                return account.StartsWith(@".\", StringComparison.Ordinal) ? Environment.MachineName + account.Substring(1) : account;
            }
        }

        private static bool SamePath(string left, string right) { return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase); }
        internal static string Quote(string value) { return "[" + value.Replace("]", "]]") + "]"; }
        internal static string Literal(string value) { return value.Replace("'", "''"); }
    }
}
