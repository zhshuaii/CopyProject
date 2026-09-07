using CopyProject;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

internal static class Tests
{
    private static int passed;
    private static int failed;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 3 && args[0] == "--crash-fixture")
        {
            var database = new FakeDatabase { AfterExport = (w, slot) => Process.GetCurrentProcess().Kill() };
            new BackupEngine(database).Run(args[1], args[2], null, CancellationToken.None);
            return 2;
        }
        if (args.Length == 1 && args[0] == "--integration") SqlIntegration.Run();
        else UnitTests();
        Console.WriteLine("RESULT: " + passed + " passed; " + failed + " failed");
        return failed == 0 ? 0 : 1;
    }

    internal static void Case(string name, Action test)
    {
        try { test(); passed++; Console.WriteLine("PASS " + name); }
        catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + "\n" + ex); }
    }
    internal static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    internal static void Reject(Action action)
    {
        try { action(); }
        catch (IOException) { return; }
        catch (InvalidOperationException) { return; }
        throw new Exception("Expected rejection");
    }

    private static List<SqlDatabaseBackup.SqlFile> Pair(params string[] types)
    {
        return types.Select((type, i) => new SqlDatabaseBackup.SqlFile { Type = type, Logical = "File" + i }).ToList();
    }

    private static void UnitTests()
    {
        Case("restore accepts exactly D/L", () => SqlDatabaseBackup.ValidatePair(Pair("D", "L")));
        foreach (string types in new[] { "D", "DDL", "DLL", "DLS", "DLF", "DLX", "LL", "DD", "" })
        {
            string current = types;
            Case("restore rejects " + current, () => Reject(() => SqlDatabaseBackup.ValidatePair(Pair(current.Select(c => c.ToString()).ToArray()))));
        }
        Case("restore rejects duplicate logical names", () =>
        {
            var pair = Pair("D", "L"); pair[1].Logical = pair[0].Logical;
            Reject(() => SqlDatabaseBackup.ValidatePair(pair));
        });
        Case("SQL escaping", () => { Assert(SqlDatabaseBackup.Quote("a]b") == "[a]]b]", "identifier"); Assert(SqlDatabaseBackup.Literal("a'b") == "a''b", "literal"); });
        Case("file scope is explicit", () =>
        {
            foreach (string ext in new[] { ".mdf", ".ldf", ".ndf", ".lck", ".MDF" }) Assert(!ProjectFileCopier.Include("file" + ext), ext);
            Assert(ProjectFileCopier.Include("screen.pdl"), "ordinary file");
            Reject(() => ProjectFileCopier.Include("archive.bak"));
            Reject(() => ProjectFileCopier.Include("archive.trn"));
        });
        Case("path boundaries and traversal", () =>
        {
            Assert(PathSafety.IsWithin(@"C:\Source\Backup", @"C:\Source"), "inside");
            Assert(!PathSafety.IsWithin(@"C:\Source2", @"C:\Source"), "sibling prefix");
            Reject(() => PathSafety.Child(@"C:\Source", @"..\Outside"));
            Reject(() => PathSafety.FullPath(@"\\server\share"));
            Reject(() => PathSafety.FullPath(@"C:\file:stream"));
            Reject(() => PathSafety.FullPath(@"C:\" + new string('x', 241)));
        });
        Case("target inside source is rejected without writes", () =>
        {
            using (var f = new Fixture())
            {
                string target = Directory.CreateDirectory(Path.Combine(f.Source, "Backups")).FullName;
                BackupResult result = new BackupEngine(new FakeDatabase()).Run(f.Mcp, target, null, CancellationToken.None);
                Assert(!result.Success && Directory.GetFileSystemEntries(target).Length == 0, result.Details);
            }
        });
        Case("Junction and cyclic source links are rejected", () =>
        {
            using (var f = new Fixture())
            {
                string link = Path.Combine(f.Source, "Loop");
                Command("cmd.exe", "/c mklink /J \"" + link + "\" \"" + f.Source + "\"");
                try
                {
                    BackupResult result = new BackupEngine(new FakeDatabase()).Run(f.Mcp, f.Target, null, CancellationToken.None);
                    Assert(!result.Success && Directory.GetFileSystemEntries(f.Target).Length == 0, result.Details);
                    Reject(() => PathSafety.LocalDirectory(link));
                }
                finally { Directory.Delete(link); }
            }
        });
        Case("backup produces verified ZIP and leaves no work resources", () =>
        {
            using (var f = new Fixture())
            {
                File.WriteAllText(Path.Combine(f.Source, "Archive.ndf"), "excluded");
                BackupResult result = new BackupEngine(new FakeDatabase()).Run(f.Mcp, f.Target, null, CancellationToken.None);
                Assert(result.Success, result.Details);
                Assert(Directory.GetFileSystemEntries(f.Target).Length == 1, "only ZIP remains");
                using (var zip = ZipFile.OpenRead(result.OutputPath))
                {
                    foreach (string name in new[] { "ProjectA/ProjectA.mcp", "ProjectA/ProjectA.mdf", "ProjectA/ProjectA.ldf", "ProjectA/ProjectART.mdf", "ProjectA/ProjectART.ldf", "ProjectA/Empty/", "CopyProject-backup-manifest.txt" }) Assert(zip.GetEntry(name) != null, name);
                    Assert(zip.Entries.All(e => !e.FullName.EndsWith(".ndf", StringComparison.OrdinalIgnoreCase)), "no archive NDF");
                }
            }
        });
        Case("unknown SQL identity fails before workspace creation", () =>
        {
            using (var f = new Fixture())
            {
                var db = new FakeDatabase { InspectError = true };
                BackupResult result = new BackupEngine(db).Run(f.Mcp, f.Target, null, CancellationToken.None);
                Assert(!result.Success && db.ExportCount == 0 && Directory.GetFileSystemEntries(f.Target).Length == 0, result.Details);
            }
        });
        Case("source mutation is detected", () =>
        {
            using (var f = new Fixture())
            {
                var db = new FakeDatabase { AfterExport = (w, slot) => { if (slot == "R") File.WriteAllText(f.Mcp, "changed source"); } };
                BackupResult result = new BackupEngine(db).Run(f.Mcp, f.Target, null, CancellationToken.None);
                Assert(!result.Success && result.Error != null && Directory.GetFileSystemEntries(f.Target).Length == 0, result.Details);
            }
        });
        Case("cleanup error preserves primary error and task record", () =>
        {
            using (var f = new Fixture())
            {
                var db = new FakeDatabase { CleanupError = true, AfterExport = (w, slot) => { throw new IOException("primary-failure"); } };
                BackupResult result = new BackupEngine(db).Run(f.Mcp, f.Target, null, CancellationToken.None);
                Assert(!result.Success && result.Error.ToString().Contains("primary-failure") && result.CleanupErrors.Count != 0 && result.Residuals.Any(p => p.EndsWith(".task")), result.Details);
                Assert(!Directory.GetFiles(f.Target, "*.zip").Any(), "no final zip on failure");
                BackupResult next = new BackupEngine(new FakeDatabase()).Run(f.Mcp, f.Target, null, CancellationToken.None);
                Assert(!next.Success && next.Residuals.Count != 0, "next attempt must report retained task");
            }
        });
        Case("locked work file is reported instead of swallowed", () =>
        {
            using (var f = new Fixture())
            {
                FileStream held = null;
                try
                {
                    var db = new FakeDatabase { AfterExport = (w, slot) => { held = new FileStream(Path.Combine(w.ProjectDirectory, "held"), FileMode.CreateNew, FileAccess.Write, FileShare.None); throw new IOException("primary-lock-test"); } };
                    BackupResult result = new BackupEngine(db).Run(f.Mcp, f.Target, null, CancellationToken.None);
                    Assert(result.Error.ToString().Contains("primary-lock-test") && result.CleanupErrors.Count != 0 && result.Residuals.Count != 0, result.Details);
                }
                finally { if (held != null) held.Dispose(); }
            }
        });
        Case("final path collision never overwrites another file", () =>
        {
            using (var f = new Fixture())
            {
                string collision = null;
                var db = new FakeDatabase { AfterExport = (w, slot) => { if (slot == "R") { collision = w.FinalPath; File.WriteAllText(collision, "other-task"); } } };
                BackupResult result = new BackupEngine(db).Run(f.Mcp, f.Target, null, CancellationToken.None);
                Assert(!result.Success && File.ReadAllText(collision) == "other-task", result.Details);
                Assert(Directory.GetFileSystemEntries(f.Target).Length == 1, "own work/partial cleaned, other file retained");
            }
        });
        Case("same source cannot run concurrently across target folders", () =>
        {
            using (var f = new Fixture())
            using (var entered = new ManualResetEvent(false))
            using (var release = new ManualResetEvent(false))
            {
                string otherTarget = Directory.CreateDirectory(Path.Combine(f.Root, "OtherTarget")).FullName;
                var db = new FakeDatabase { AfterExport = (w, slot) => { if (slot == "C") { entered.Set(); if (!release.WaitOne(10000)) throw new TimeoutException("concurrency gate"); } } };
                Task<BackupResult> first = Task.Run(() => new BackupEngine(db).Run(f.Mcp, f.Target, null, CancellationToken.None));
                try
                {
                    Assert(entered.WaitOne(5000), "first task did not start");
                    BackupResult second = new BackupEngine(new FakeDatabase()).Run(f.Mcp, otherTarget, null, CancellationToken.None);
                    Assert(!second.Success && Directory.GetFileSystemEntries(otherTarget).Length == 0, second.Details);
                }
                finally { release.Set(); }
                Assert(first.GetAwaiter().GetResult().Success, "first task failed");
            }
        });
        Case("published ZIP survives task record cleanup failure", () =>
        {
            using (var f = new Fixture())
            {
                FileStream held = null;
                try
                {
                    var db = new FakeDatabase { AfterExport = (w, slot) => { if (slot == "R") held = new FileStream(w.RecordPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite); } };
                    BackupResult result = new BackupEngine(db).Run(f.Mcp, f.Target, null, CancellationToken.None);
                    Assert(!result.Success && result.OutputPath != null && File.Exists(result.OutputPath) && result.CleanupErrors.Count != 0 && result.Residuals.Any(p => p.EndsWith(".task")), result.Details);
                }
                finally { if (held != null) held.Dispose(); }
            }
        });
        Case("locked partial is retained and reported", () =>
        {
            using (var f = new Fixture())
            {
                BackupWorkspace workspace = null;
                FileStream held = null;
                try
                {
                    var db = new FakeDatabase { AfterExport = (w, slot) => workspace = w };
                    var progress = new InlineProgress(p => { if (p.Percent == 95) held = new FileStream(workspace.PartialPath, FileMode.Open, FileAccess.Read, FileShare.Read); });
                    BackupResult result = new BackupEngine(db).Run(f.Mcp, f.Target, progress, CancellationToken.None);
                    Assert(!result.Success && result.OutputPath == null && result.CleanupErrors.Count != 0 && result.Residuals.Any(p => p.EndsWith(".partial")), result.Details);
                }
                finally { if (held != null) held.Dispose(); }
            }
        });
        foreach (int percent in new[] { 10, 40, 60, 80, 85, 95 })
        {
            int stage = percent;
            Case("cancel at stage " + stage, () =>
            {
                using (var f = new Fixture())
                using (var cancellation = new CancellationTokenSource())
                {
                    var progress = new InlineProgress(p => { if (p.Percent == stage) cancellation.Cancel(); });
                    BackupResult result = new BackupEngine(new FakeDatabase()).Run(f.Mcp, f.Target, progress, cancellation.Token);
                    Assert(result.Cancelled && result.OutputPath == null && result.CleanupErrors.Count == 0 && Directory.GetFileSystemEntries(f.Target).Length == 0, result.Details);
                }
            });
        }
        Case("hard termination is detected on next attempt", () =>
        {
            using (var f = new Fixture())
            {
                string exe = Process.GetCurrentProcess().MainModule.FileName;
                using (var child = Process.Start(new ProcessStartInfo(exe, "--crash-fixture \"" + f.Mcp + "\" \"" + f.Target + "\"") { UseShellExecute = false }))
                {
                    Assert(child.WaitForExit(15000), "crash fixture timed out");
                    Assert(child.ExitCode != 0, "fixture must terminate abnormally");
                }
                Assert(Directory.GetFiles(f.Target, "*.task").Length == 1, "durable task record missing");
                BackupResult result = new BackupEngine(new FakeDatabase()).Run(f.Mcp, f.Target, null, CancellationToken.None);
                Assert(!result.Success && result.Residuals.Count >= 2, result.Details);
            }
        });
        Case("retry uses all three delays and four attempts", () => { int attempts = 0; Retry.Io(() => { attempts++; if (attempts < 4) throw new IOException("transient"); }, CancellationToken.None); Assert(attempts == 4, "retry count"); });
        Case("WinForms worker input and closing wait for cleanup", () =>
        {
            Control.CheckForIllegalCrossThreadCalls = true;
            int uiThread = Thread.CurrentThread.ManagedThreadId;
            using (var ready = new ManualResetEvent(false))
            using (var finish = new ManualResetEvent(false))
            using (var form = new MainForm((source, target, progress, token) =>
            {
                Assert(source == "captured-source" && target == "captured-target" && Thread.CurrentThread.ManagedThreadId != uiThread, "UI capture/worker thread");
                progress.Report(new BackupProgress(50, "test progress"));
                ready.Set();
                if (!finish.WaitOne(5000)) throw new TimeoutException("UI test cleanup gate");
                return new BackupResult { Cancelled = token.IsCancellationRequested, Stage = "test complete" };
            }))
            {
                form.Show();
                Task<BackupResult> task = form.RunBackupAsync("captured-source", "captured-target");
                Assert(ready.WaitOne(5000), "worker not started");
                Application.DoEvents();
                form.Close();
                Assert(!form.IsDisposed && !task.IsCompleted, "window must stay alive during cleanup");
                finish.Set();
                var watch = Stopwatch.StartNew();
                while (!task.IsCompleted && watch.ElapsedMilliseconds < 5000) { Application.DoEvents(); Thread.Sleep(10); }
                Assert(task.IsCompleted && task.GetAwaiter().GetResult().Cancelled, "closing did not request cancellation");
                form.Close();
            }
        });
    }

    internal static void Command(string executable, string arguments)
    {
        using (var process = Process.Start(new ProcessStartInfo(executable, arguments) { UseShellExecute = false, CreateNoWindow = true }))
        {
            if (!process.WaitForExit(10000) || process.ExitCode != 0) throw new Exception("Command failed: " + executable + " " + arguments);
        }
    }

    internal sealed class Fixture : IDisposable
    {
        public readonly string Root = Path.Combine(Path.GetTempPath(), "CPTest_" + Guid.NewGuid().ToString("N"));
        public string Source { get { return Path.Combine(Root, "Source"); } }
        public string Target { get { return Path.Combine(Root, "Target"); } }
        public string Mcp { get { return Path.Combine(Source, "ProjectA.mcp"); } }
        public Fixture()
        {
            Directory.CreateDirectory(Source);
            Directory.CreateDirectory(Target);
            Directory.CreateDirectory(Path.Combine(Source, "Empty"));
            File.WriteAllText(Mcp, "test project descriptor");
            File.WriteAllText(Path.Combine(Source, "screen.pdl"), "test screen");
        }
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }

    private sealed class InlineProgress : IProgress<BackupProgress>
    {
        private readonly Action<BackupProgress> action;
        public InlineProgress(Action<BackupProgress> action) { this.action = action; }
        public void Report(BackupProgress value) { action(value); }
    }

    private sealed class FakeDatabase : IDatabaseBackup
    {
        public bool InspectError;
        public bool CleanupError;
        public int ExportCount;
        public Action<BackupWorkspace, string> AfterExport;
        public string ServiceAccount { get { return WindowsIdentity.GetCurrent().Name; } }
        public DatabaseInfo[] Inspect(BackupPlan plan, CancellationToken token)
        {
            if (InspectError) throw new InvalidOperationException("unknown database identity");
            return new[] { new DatabaseInfo { Name = "FakeConfig", Id = 10, BaseName = plan.ProjectName }, new DatabaseInfo { Name = "FakeRuntime", Id = 11, BaseName = plan.ProjectName + "RT" } };
        }
        public void Export(DatabaseInfo database, BackupWorkspace workspace, string slot, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            ExportCount++;
            workspace.MarkRestoreAttempt(slot);
            File.WriteAllText(Path.Combine(workspace.ProjectDirectory, database.BaseName + ".mdf"), "FAKE TEST DATA, NOT A SQL DATABASE");
            File.WriteAllText(Path.Combine(workspace.ProjectDirectory, database.BaseName + ".ldf"), "FAKE TEST LOG, NOT A SQL DATABASE");
            if (AfterExport != null) AfterExport(workspace, slot);
        }
        public void Cleanup(BackupWorkspace workspace) { if (CleanupError) throw new IOException("injected SQL cleanup failure"); }
    }
}
