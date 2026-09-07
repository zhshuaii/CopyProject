using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;

namespace CopyProject
{
    internal static class ProjectFileCopier
    {
        public static bool Include(string path)
        {
            string extension = Path.GetExtension(path).ToLowerInvariant();
            if (extension == ".bak" || extension == ".trn") throw new IOException("无法确认此文件是数据库备份还是工程配置副本，已停止而非静默遗漏：" + path);
            return extension != ".mdf" && extension != ".ldf" && extension != ".ndf" && extension != ".lck";
        }

        public static void Inventory(BackupPlan plan, CancellationToken token)
        {
            plan.Files = new List<ProjectFile>();
            plan.Directories = new List<string>();
            Walk(plan.SourceDirectory, plan.SourceDirectory, plan.Files, plan.Directories, token);
        }

        private static void Walk(string root, string directory, List<ProjectFile> files, List<string> directories, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            PathSafety.NoLinks(directory);
            foreach (string path in Directory.EnumerateFiles(directory))
            {
                token.ThrowIfCancellationRequested();
                PathSafety.NoLinks(path);
                if (!Include(path)) continue;
                FileInfo info = new FileInfo(path);
                files.Add(new ProjectFile { RelativePath = Relative(root, path), Length = info.Length, WrittenUtc = info.LastWriteTimeUtc });
            }
            foreach (string path in Directory.EnumerateDirectories(directory))
            {
                PathSafety.NoLinks(path);
                directories.Add(Relative(root, path));
                Walk(root, path, files, directories, token);
            }
        }

        private static string Relative(string root, string path) { return path.Substring(root.TrimEnd('\\').Length + 1); }

        public static void Copy(BackupPlan plan, BackupWorkspace workspace, IProgress<BackupProgress> progress, CancellationToken token)
        {
            foreach (string relative in plan.Directories) Directory.CreateDirectory(PathSafety.Child(workspace.ProjectDirectory, relative));
            for (int index = 0; index < plan.Files.Count; index++)
            {
                ProjectFile file = plan.Files[index];
                string source = PathSafety.Child(plan.SourceDirectory, file.RelativePath);
                string destination = PathSafety.Child(workspace.ProjectDirectory, file.RelativePath);
                Retry.Io(() =>
                {
                    CheckMetadata(source, file);
                    using (FileStream input = OpenSource(source))
                    using (FileStream output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None))
                        file.Hash = Transfer(input, output, token);
                    CheckMetadata(source, file);
                    File.SetLastWriteTimeUtc(destination, file.WrittenUtc);
                }, token);
                if (progress != null) progress.Report(new BackupProgress(10 + (index + 1) * 25 / Math.Max(1, plan.Files.Count), "复制工程文件 " + (index + 1) + "/" + plan.Files.Count));
            }
        }

        public static void VerifySource(BackupPlan plan, CancellationToken token)
        {
            var current = new List<ProjectFile>();
            var directories = new List<string>();
            Walk(plan.SourceDirectory, plan.SourceDirectory, current, directories, token);
            if (current.Count != plan.Files.Count || !new HashSet<string>(directories, StringComparer.OrdinalIgnoreCase).SetEquals(plan.Directories)) throw new IOException("备份期间工程目录结构发生变化，请停止编辑后重试。");
            var map = new Dictionary<string, ProjectFile>(StringComparer.OrdinalIgnoreCase);
            foreach (ProjectFile file in current) map.Add(file.RelativePath, file);
            foreach (ProjectFile file in plan.Files)
            {
                ProjectFile observed;
                if (!map.TryGetValue(file.RelativePath, out observed) || observed.Length != file.Length || observed.WrittenUtc != file.WrittenUtc) throw new IOException("工程文件发生变化：" + file.RelativePath);
                string source = PathSafety.Child(plan.SourceDirectory, file.RelativePath);
                using (FileStream input = OpenSource(source))
                    if (Transfer(input, null, token) != file.Hash) throw new IOException("工程内容发生变化：" + file.RelativePath);
                CheckMetadata(source, file);
            }
        }

        private static FileStream OpenSource(string path)
        {
            PathSafety.NoLinks(path);
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 131072, FileOptions.SequentialScan);
        }

        private static void CheckMetadata(string path, ProjectFile expected)
        {
            PathSafety.NoLinks(path);
            FileInfo file = new FileInfo(path);
            if (!file.Exists || file.Length != expected.Length || file.LastWriteTimeUtc != expected.WrittenUtc) throw new IOException("工程文件不稳定：" + path);
        }

        public static string Transfer(Stream input, Stream output, CancellationToken token)
        {
            byte[] buffer = new byte[131072];
            using (SHA256 hash = SHA256.Create())
            {
                int count;
                while ((count = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    token.ThrowIfCancellationRequested();
                    if (output != null) output.Write(buffer, 0, count);
                    hash.TransformBlock(buffer, 0, count, buffer, 0);
                }
                token.ThrowIfCancellationRequested();
                hash.TransformFinalBlock(new byte[0], 0, 0);
                return Convert.ToBase64String(hash.Hash);
            }
        }
    }
}
