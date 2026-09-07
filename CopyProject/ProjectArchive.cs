using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;

namespace CopyProject
{
    internal static class ProjectArchive
    {
        public static void WriteAndVerify(BackupPlan plan, BackupWorkspace workspace, CancellationToken token)
        {
            string prefix = plan.ProjectName + "/";
            var expected = new Dictionary<string, string>(StringComparer.Ordinal);
            var manifest = new StringBuilder("CopyProject backup manifest v1\nTask=" + workspace.Id + "\nCreatedUtc=" + DateTime.UtcNow.ToString("O") + "\nConsistency=individual SQL backups; not a global project snapshot\n");
            using (FileStream file = workspace.CreatePartial())
            using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
            {
                expected.Add(prefix, null);
                zip.CreateEntry(prefix);
                foreach (string directory in plan.Directories)
                {
                    string entry = prefix + directory.Replace('\\', '/') + "/";
                    expected.Add(entry, null);
                    zip.CreateEntry(entry);
                }
                foreach (ProjectFile source in plan.Files)
                {
                    string entry = prefix + source.RelativePath.Replace('\\', '/');
                    string hash = AddFile(zip, entry, PathSafety.Child(workspace.ProjectDirectory, source.RelativePath), token);
                    if (hash != source.Hash) throw new IOException("工作副本发生变化：" + source.RelativePath);
                    expected.Add(entry, hash);
                    manifest.AppendLine(hash + "  " + entry);
                }
                foreach (DatabaseInfo database in plan.Databases)
                {
                    foreach (string extension in new[] { ".mdf", ".ldf" })
                    {
                        string name = database.BaseName + extension;
                        string hash = AddFile(zip, prefix + name, PathSafety.Child(workspace.ProjectDirectory, name), token);
                        expected.Add(prefix + name, hash);
                        manifest.AppendLine(hash + "  " + prefix + name);
                    }
                }
                byte[] bytes = new UTF8Encoding(false).GetBytes(manifest.ToString());
                using (var input = new MemoryStream(bytes))
                using (Stream output = zip.CreateEntry("CopyProject-backup-manifest.txt", CompressionLevel.Fastest).Open())
                    expected.Add("CopyProject-backup-manifest.txt", ProjectFileCopier.Transfer(input, output, token));
            }
            using (var zip = ZipFile.OpenRead(workspace.PartialPath))
            {
                if (zip.Entries.Count != expected.Count) throw new IOException("ZIP 条目数量不符合清单。");
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (ZipArchiveEntry entry in zip.Entries)
                {
                    token.ThrowIfCancellationRequested();
                    string hash;
                    if (!seen.Add(entry.FullName) || !expected.TryGetValue(entry.FullName, out hash)) throw new IOException("ZIP 出现未授权或重复条目。");
                    if (hash == null) { if (entry.Length != 0) throw new IOException("ZIP 目录条目异常。"); continue; }
                    using (Stream input = entry.Open())
                        if (ProjectFileCopier.Transfer(input, null, token) != hash) throw new IOException("ZIP 内容校验失败：" + entry.FullName);
                }
            }
        }

        private static string AddFile(ZipArchive zip, string name, string path, CancellationToken token)
        {
            PathSafety.NoLinks(path);
            using (FileStream input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (Stream output = zip.CreateEntry(name, CompressionLevel.Fastest).Open())
                return ProjectFileCopier.Transfer(input, output, token);
        }
    }
}
