using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace CopyProject
{
    internal static class PathSafety
    {
        // A deliberately bounded legacy-Windows path policy, not a general link resolver.
        public static string FullPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path)) throw new IOException("必须使用本地绝对路径。");
            string full = Path.GetFullPath(path);
            if (full.StartsWith(@"\\", StringComparison.Ordinal) || full.Length > 240) throw new IOException("不支持 UNC、设备路径或超过 240 字符的路径：" + full);
            if (full.IndexOf(':', 2) >= 0) throw new IOException("不支持备用数据流路径：" + full);
            return full.Length > 3 ? full.TrimEnd(Path.DirectorySeparatorChar) : full;
        }

        public static bool IsWithin(string path, string root)
        {
            string p = FullPath(path);
            string r = FullPath(root);
            return string.Equals(p, r, StringComparison.OrdinalIgnoreCase) || p.StartsWith(r.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
        }

        public static string Child(string root, string relative)
        {
            if (string.IsNullOrEmpty(relative) || Path.IsPathRooted(relative)) throw new IOException("无效相对路径。");
            string path = FullPath(Path.Combine(root, relative));
            if (string.Equals(path, FullPath(root), StringComparison.OrdinalIgnoreCase) || !IsWithin(path, root)) throw new IOException("路径越出任务目录：" + path);
            NoLinks(path);
            return path;
        }

        public static void NoLinks(string path)
        {
            string current = FullPath(path);
            while (!string.IsNullOrEmpty(current))
            {
                try
                {
                    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("不支持链接、Junction 或挂载点：" + current);
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
                current = Path.GetDirectoryName(current);
            }
        }

        public static string LocalDirectory(string path)
        {
            string full = FullPath(path);
            NoLinks(full);
            if (!Directory.Exists(full)) throw new DirectoryNotFoundException("目录不存在：" + full);
            DriveInfo drive = new DriveInfo(Path.GetPathRoot(full));
            if (!drive.IsReady || (drive.DriveType != DriveType.Fixed && drive.DriveType != DriveType.Removable)) throw new IOException("请选择可用的本地磁盘。");
            if (!string.Equals(drive.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase) && !string.Equals(drive.DriveFormat, "ReFS", StringComparison.OrdinalIgnoreCase)) throw new IOException("工作目录和源目录只支持 NTFS/ReFS。");
            using (SafeFileHandle handle = OpenDirectory(full))
            {
                StringBuilder value = new StringBuilder(1024);
                uint length = GetFinalPathNameByHandle(handle, value, (uint)value.Capacity, 0);
                if (length == 0 || length >= value.Capacity) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法核实真实目录位置。");
                string actual = value.ToString();
                if (actual.StartsWith(@"\\?\", StringComparison.Ordinal)) actual = actual.Substring(4);
                if (!string.Equals(FullPath(actual), full, StringComparison.OrdinalIgnoreCase)) throw new IOException("不支持 SUBST、短文件名或其他路径别名，请选择真实路径：" + actual);
            }
            return full;
        }

        public static IDisposable PinAncestors(params string[] paths)
        {
            var lease = new DirectoryLease();
            try
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string path in paths)
                {
                    string current = FullPath(path);
                    while (!string.IsNullOrEmpty(current))
                    {
                        NoLinks(current);
                        if (seen.Add(current)) lease.Handles.Add(OpenDirectory(current));
                        current = Path.GetDirectoryName(current);
                    }
                }
                return lease;
            }
            catch { lease.Dispose(); throw; }
        }

        public static void CheckTree(string root)
        {
            NoLinks(root);
            foreach (string file in Directory.EnumerateFiles(root)) NoLinks(file);
            foreach (string directory in Directory.EnumerateDirectories(root)) CheckTree(directory);
        }

        private static SafeFileHandle OpenDirectory(string path)
        {
            // Do not share DELETE: keep the selected directory and its ancestors in place while a task runs.
            SafeFileHandle handle = CreateFile(path, 0, 3, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw new Win32Exception(error, "无法锁定目录位置：" + path);
            }
            return handle;
        }

        private sealed class DirectoryLease : IDisposable
        {
            internal readonly List<SafeFileHandle> Handles = new List<SafeFileHandle>();
            public void Dispose() { foreach (SafeFileHandle handle in Handles) handle.Dispose(); }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint size, uint flags);
    }
}
