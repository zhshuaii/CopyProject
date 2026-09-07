using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace CopyProject
{
    internal sealed class BackupEngine
    {
        private readonly IDatabaseBackup databases;
        public BackupEngine() : this(new SqlDatabaseBackup()) { }
        internal BackupEngine(IDatabaseBackup databases) { this.databases = databases ?? throw new ArgumentNullException(nameof(databases)); }

        public BackupResult Run(string mcpPath, string targetRoot, IProgress<BackupProgress> progress, CancellationToken token)
        {
            var result = new BackupResult { Stage = "预检查" };
            BackupWorkspace workspace = null;
            IDisposable directoryLease = null;
            Mutex projectMutex = null;
            bool mutexHeld = false;
            try
            {
                token.ThrowIfCancellationRequested();
                BackupPlan plan = BackupPlan.FromPaths(mcpPath, targetRoot);
                directoryLease = PathSafety.PinAncestors(plan.SourceDirectory, plan.TargetDirectory);
                // Recheck after acquiring handles, before any writes.
                BackupPlan.FromPaths(mcpPath, targetRoot);
                projectMutex = new Mutex(false, MutexName(plan.SourceDirectory));
                try { mutexHeld = projectMutex.WaitOne(0); }
                catch (AbandonedMutexException) { mutexHeld = true; }
                if (!mutexHeld) throw new InvalidOperationException("同一项目已有备份任务在运行。");
                string[] residuals = BackupWorkspace.FindResiduals(plan.TargetDirectory);
                if (residuals.Length != 0)
                {
                    result.Residuals.AddRange(residuals);
                    throw new InvalidOperationException("目标目录存在活动或遗留 CopyProject 任务。请先核对任务记录；不会自动接管或批量删除。");
                }
                ProjectFileCopier.Inventory(plan, token);
                plan.Databases = databases.Inspect(plan, token);
                CheckSpace(plan);
                workspace = new BackupWorkspace(plan);
                workspace.Create(databases.ServiceAccount);
                SetStage(result, workspace, progress, 10, "复制工程文件");
                ProjectFileCopier.Copy(plan, workspace, progress, token);
                SetStage(result, workspace, progress, 40, "在线备份组态数据库");
                databases.Export(plan.Databases[0], workspace, "C", token);
                SetStage(result, workspace, progress, 60, "在线备份 Runtime 数据库");
                databases.Export(plan.Databases[1], workspace, "R", token);
                SetStage(result, workspace, progress, 80, "复核源工程文件稳定性");
                ProjectFileCopier.VerifySource(plan, token);
                SetStage(result, workspace, progress, 85, "生成并校验 ZIP");
                ProjectArchive.WriteAndVerify(plan, workspace, token);
                ProjectFileCopier.VerifySource(plan, token);
                SetStage(result, workspace, progress, 95, "回收临时数据库和工作目录");
                databases.Cleanup(workspace);
                workspace.DeleteWork();
                token.ThrowIfCancellationRequested();
                workspace.Publish();
                result.OutputPath = workspace.FinalPath;
                result.Stage = "ZIP 已发布";
            }
            catch (OperationCanceledException ex)
            {
                if (token.IsCancellationRequested) result.Cancelled = true;
                else result.Error = ex;
            }
            catch (Exception ex) { result.Error = ex; }
            finally
            {
                if (workspace != null)
                {
                    if (result.OutputPath == null)
                    {
                        bool sqlClean = true;
                        try { databases.Cleanup(workspace); }
                        catch (Exception ex)
                        {
                            sqlClean = false;
                            result.CleanupErrors.Add(ex.ToString());
                            foreach (string slot in workspace.RestoreAttempts) result.Residuals.Add("SQL 待核实：" + workspace.TemporaryDatabase(slot));
                        }
                        if (sqlClean) AttemptCleanup(workspace.DeleteWork, result);
                        AttemptCleanup(workspace.DeletePartial, result);
                    }
                    AttemptCleanup(() => workspace.FinishRecord(result.CleanupErrors.Count != 0), result);
                    result.Residuals.AddRange(workspace.RemainingResources());
                }
                if (mutexHeld) projectMutex.ReleaseMutex();
                if (projectMutex != null) projectMutex.Dispose();
                if (directoryLease != null) directoryLease.Dispose();
            }
            if (result.Success)
            {
                result.Stage = "完成";
                if (progress != null) progress.Report(new BackupProgress(100, "备份完成"));
            }
            return result;
        }

        private static void AttemptCleanup(Action cleanup, BackupResult result)
        {
            try { cleanup(); }
            catch (Exception ex) { result.CleanupErrors.Add(ex.ToString()); }
        }

        private static void SetStage(BackupResult result, BackupWorkspace workspace, IProgress<BackupProgress> progress, int percent, string stage)
        {
            result.Stage = stage;
            workspace.Note("Stage=" + stage);
            if (progress != null) progress.Report(new BackupProgress(percent, stage));
        }

        private static void CheckSpace(BackupPlan plan)
        {
            long normal = 0;
            long database = 0;
            checked
            {
                foreach (ProjectFile file in plan.Files) normal += file.Length;
                foreach (DatabaseInfo item in plan.Databases) database += item.Bytes;
                long required = normal * 2 + database * 3 + 256L * 1024 * 1024;
                if (new DriveInfo(Path.GetPathRoot(plan.TargetDirectory)).AvailableFreeSpace < required) throw new IOException("目标磁盘空间不足；保守估算需要 " + required + " 字节。运行中增长仍可能使实际占用更高。");
            }
        }

        private static string MutexName(string source)
        {
            using (SHA256 hash = SHA256.Create())
                return @"Global\CopyProject_" + BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(source.ToUpperInvariant()))).Replace("-", "");
        }
    }
}
