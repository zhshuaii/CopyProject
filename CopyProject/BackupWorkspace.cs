using System;
using System.Collections.Generic;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;

namespace CopyProject
{
    internal sealed class BackupWorkspace
    {
        private FileStream journal;
        private bool ownsRecord;
        private bool ownsWork;
        private bool ownsPartial;
        private readonly BackupPlan plan;
        public readonly string Id = Guid.NewGuid().ToString("N");
        public readonly HashSet<string> RestoreAttempts = new HashSet<string>(StringComparer.Ordinal);
        public string WorkDirectory { get; private set; }
        public string ProjectDirectory { get; private set; }
        public string SqlDirectory { get; private set; }
        public string PartialPath { get; private set; }
        public string FinalPath { get; private set; }
        public string RecordPath { get; private set; }

        public BackupWorkspace(BackupPlan plan)
        {
            this.plan = plan;
            WorkDirectory = PathSafety.Child(plan.TargetDirectory, ".CopyProject_" + Id);
            ProjectDirectory = PathSafety.Child(WorkDirectory, "Project");
            SqlDirectory = PathSafety.Child(WorkDirectory, "Sql");
            RecordPath = PathSafety.Child(plan.TargetDirectory, ".CopyProject_" + Id + ".task");
            PartialPath = PathSafety.Child(plan.TargetDirectory, ".CopyProject_" + Id + ".zip.partial");
            FinalPath = PathSafety.Child(plan.TargetDirectory, plan.ProjectName + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + Id + ".zip");
            // Validate every generated path before creating any resource.
            foreach (ProjectFile file in plan.Files) PathSafety.Child(ProjectDirectory, file.RelativePath);
            foreach (DatabaseInfo db in plan.Databases)
            {
                PathSafety.Child(ProjectDirectory, db.BaseName + ".mdf");
                PathSafety.Child(ProjectDirectory, db.BaseName + ".ldf");
            }
        }

        public static string[] FindResiduals(string target)
        {
            PathSafety.LocalDirectory(target);
            return Directory.GetFileSystemEntries(target, ".CopyProject_*");
        }

        public string TemporaryDatabase(string slot)
        {
            if (slot != "C" && slot != "R") throw new InvalidOperationException("无效数据库槽位。");
            return "CopyProject_TEMP_" + Id + "_" + slot;
        }

        internal string[] DatabasePaths(string slot)
        {
            TemporaryDatabase(slot);
            string name = plan.Databases[slot == "C" ? 0 : 1].BaseName;
            return new[] { PathSafety.Child(ProjectDirectory, name + ".mdf"), PathSafety.Child(ProjectDirectory, name + ".ldf") };
        }

        public void Create(string serviceAccount)
        {
            journal = new FileStream(RecordPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            ownsRecord = true;
            Note("CopyProjectTask/1\nId=" + Id + "\nSource=" + plan.SourceMcp + "\nWork=" + WorkDirectory + "\nPartial=" + PartialPath + "\nFinal=" + FinalPath + "\nConfigDatabase=" + TemporaryDatabase("C") + "\nRuntimeDatabase=" + TemporaryDatabase("R"));
            if (Directory.Exists(WorkDirectory) || File.Exists(WorkDirectory)) throw new IOException("工作区已存在，拒绝接管：" + WorkDirectory);
            Directory.CreateDirectory(WorkDirectory);
            ownsWork = true;
            SetPrivateAcl(WorkDirectory, serviceAccount);
            File.SetAttributes(WorkDirectory, File.GetAttributes(WorkDirectory) | FileAttributes.Hidden);
            Directory.CreateDirectory(ProjectDirectory);
            Directory.CreateDirectory(SqlDirectory);
        }

        public void Note(string message)
        {
            if (journal == null) throw new InvalidOperationException("任务记录尚未打开。");
            byte[] bytes = new UTF8Encoding(false).GetBytes(DateTime.UtcNow.ToString("O") + " " + message + "\r\n");
            journal.Write(bytes, 0, bytes.Length);
            journal.Flush(true);
        }

        public void MarkRestoreAttempt(string slot)
        {
            TemporaryDatabase(slot);
            Note("RestoreAttempt=" + slot);
            RestoreAttempts.Add(slot);
        }

        private static void SetPrivateAcl(string path, string account)
        {
            SecurityIdentifier caller = WindowsIdentity.GetCurrent().User;
            if (caller == null || string.IsNullOrWhiteSpace(account)) throw new InvalidOperationException("无法确认用户或 SQL 服务账户。");
            var service = (SecurityIdentifier)new NTAccount(account).Translate(typeof(SecurityIdentifier));
            var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(true, false);
            InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            security.AddAccessRule(new FileSystemAccessRule(caller, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            if (!service.Equals(caller) && !service.Equals(system)) security.AddAccessRule(new FileSystemAccessRule(service, FileSystemRights.Modify, inherit, PropagationFlags.None, AccessControlType.Allow));
            Directory.SetAccessControl(path, security);
        }

        public FileStream CreatePartial()
        {
            PathSafety.NoLinks(PartialPath);
            FileStream stream = new FileStream(PartialPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            ownsPartial = true;
            return stream;
        }

        public void DeleteWork()
        {
            if (!ownsWork) return;
            Retry.Io(() =>
            {
                if (!Directory.Exists(WorkDirectory)) return;
                PathSafety.CheckTree(WorkDirectory);
                Directory.Delete(WorkDirectory, true);
            }, CancellationToken.None);
            ownsWork = false;
        }

        public void DeletePartial()
        {
            if (!ownsPartial) return;
            Retry.Io(() => { PathSafety.NoLinks(PartialPath); File.Delete(PartialPath); }, CancellationToken.None);
            ownsPartial = false;
        }

        public void Publish()
        {
            if (ownsWork || !ownsPartial) throw new InvalidOperationException("尚未清理工作区或没有本任务的 partial。");
            PathSafety.NoLinks(FinalPath);
            File.Move(PartialPath, FinalPath); // Never overwrite or delete another task's output.
            ownsPartial = false;
        }

        public void FinishRecord(bool retain)
        {
            try { if (journal != null) Note(retain ? "Terminal=CleanupPending" : "Terminal=Clean"); }
            finally { if (journal != null) { journal.Dispose(); journal = null; } }
            if (!retain && ownsRecord)
            {
                Retry.Io(() => { PathSafety.NoLinks(RecordPath); File.Delete(RecordPath); }, CancellationToken.None);
                ownsRecord = false;
            }
        }

        public IEnumerable<string> RemainingResources()
        {
            if (ownsWork) yield return WorkDirectory;
            if (ownsPartial) yield return PartialPath;
            if (ownsRecord) yield return RecordPath;
        }
    }
}
