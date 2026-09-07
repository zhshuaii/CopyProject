using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace CopyProject
{
    internal sealed class BackupProgress
    {
        public BackupProgress(int percent, string message) { Percent = Math.Max(0, Math.Min(100, percent)); Message = message; }
        public int Percent { get; private set; }
        public string Message { get; private set; }
    }

    internal sealed class BackupResult
    {
        public string OutputPath { get; internal set; }
        public string Stage { get; internal set; }
        public Exception Error { get; internal set; }
        public bool Cancelled { get; internal set; }
        public List<string> CleanupErrors { get; } = new List<string>();
        public List<string> Residuals { get; } = new List<string>();
        public bool Success { get { return OutputPath != null && Error == null && CleanupErrors.Count == 0 && Residuals.Count == 0; } }
        public string Details
        {
            get
            {
                string text = "阶段：" + Stage;
                if (OutputPath != null) text += "\r\n已发布 ZIP：" + OutputPath;
                if (Cancelled) text += "\r\n任务已取消。";
                if (Error != null) text += "\r\n\r\n原始错误：\r\n" + Error;
                if (CleanupErrors.Count != 0) text += "\r\n\r\n清理错误：\r\n" + string.Join("\r\n", CleanupErrors);
                if (Residuals.Count != 0) text += "\r\n\r\n残留或待核实资源：\r\n" + string.Join("\r\n", Residuals);
                return text;
            }
        }
    }

    internal sealed class ProjectFile
    {
        public string RelativePath;
        public long Length;
        public DateTime WrittenUtc;
        public string Hash;
    }

    internal sealed class DatabaseInfo
    {
        public string Name;
        public int Id;
        public string BaseName;
        public string DataPath;
        public string LogPath;
        public string DataLogical;
        public string LogLogical;
        public long Bytes;
    }

    internal sealed class BackupPlan
    {
        public string SourceDirectory;
        public string SourceMcp;
        public string TargetDirectory;
        public string ProjectName;
        public List<ProjectFile> Files;
        public List<string> Directories;
        public DatabaseInfo[] Databases;

        public static BackupPlan FromPaths(string mcp, string target)
        {
            if (string.IsNullOrWhiteSpace(mcp) || !string.Equals(Path.GetExtension(mcp), ".mcp", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("请选择有效的 .mcp 项目文件。");
            mcp = PathSafety.FullPath(mcp);
            PathSafety.NoLinks(mcp);
            if (!File.Exists(mcp)) throw new FileNotFoundException("未找到 MCP 文件。", mcp);
            string source = PathSafety.LocalDirectory(Path.GetDirectoryName(mcp));
            string output = PathSafety.LocalDirectory(target);
            if (PathSafety.IsWithin(output, source)) throw new InvalidOperationException("备份目录不能是源项目目录或其子目录。");
            string name = Path.GetFileNameWithoutExtension(mcp);
            if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("项目文件名不能为空。");
            return new BackupPlan { SourceMcp = mcp, SourceDirectory = source, TargetDirectory = output, ProjectName = name };
        }
    }

    internal interface IDatabaseBackup
    {
        string ServiceAccount { get; }
        DatabaseInfo[] Inspect(BackupPlan plan, CancellationToken token);
        void Export(DatabaseInfo database, BackupWorkspace workspace, string slot, CancellationToken token);
        void Cleanup(BackupWorkspace workspace);
    }

    internal static class Retry
    {
        public static void Io(Action action, CancellationToken token)
        {
            int[] delays = { 200, 500, 1000 };
            for (int attempt = 0; ; attempt++)
            {
                token.ThrowIfCancellationRequested();
                try { action(); return; }
                catch (IOException) { if (attempt == delays.Length) throw; }
                catch (UnauthorizedAccessException) { if (attempt == delays.Length) throw; }
                if (token.WaitHandle.WaitOne(delays[attempt])) token.ThrowIfCancellationRequested();
            }
        }
    }
}
