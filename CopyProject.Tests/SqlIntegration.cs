using CopyProject;
using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.IO;
using System.IO.Compression;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;

internal static class SqlIntegration
{
    internal static void Run()
    {
        string instance = Environment.GetEnvironmentVariable("COPYPROJECT_TEST_INSTANCE");
        if (string.IsNullOrEmpty(instance) || !instance.StartsWith(@"(localdb)\CopyProjectCI", StringComparison.OrdinalIgnoreCase))
        {
            Tests.Case("isolated SQL instance guard", () => { throw new Exception("Set COPYPROJECT_TEST_INSTANCE to a disposable (localdb)\\CopyProjectCI... instance; production instances are refused."); });
            return;
        }
        string connectionString = new SqlConnectionStringBuilder { DataSource = instance, IntegratedSecurity = true, InitialCatalog = "master", Pooling = false, ConnectTimeout = 15 }.ConnectionString;
        Tests.Case("real SQL BACKUP/RESTORE/DETACH, concurrent source writes, ZIP attach", () => RoundTrip(instance, connectionString));
        Tests.Case("real SQL unknown MDF does not fall back to copy", () =>
        {
            using (var fixture = new Tests.Fixture())
            {
                foreach (string name in new[] { "ProjectA.mdf", "ProjectA.ldf", "ProjectART.mdf", "ProjectART.ldf" }) File.WriteAllText(Path.Combine(fixture.Source, name), "not attached");
                BackupResult result = new BackupEngine(new SqlDatabaseBackup(instance, WindowsIdentity.GetCurrent().Name)).Run(fixture.Mcp, fixture.Target, null, CancellationToken.None);
                Tests.Assert(!result.Success && result.Error != null && Directory.GetFileSystemEntries(fixture.Target).Length == 0, result.Details);
            }
        });
        Tests.Case("real SQL metadata denial is rejected", () =>
        {
            string login = "CPIT_Denied_" + Guid.NewGuid().ToString("N");
            using (var connection = new SqlConnection(connectionString))
            {
                connection.Open();
                Execute(connection, "CREATE LOGIN " + Q(login) + " WITH PASSWORD=N'" + Guid.NewGuid().ToString("N") + "aA1!'; DENY VIEW ANY DEFINITION TO " + Q(login) + "; DENY VIEW ANY DATABASE TO " + Q(login) + ";");
                try
                {
                    Execute(connection, "EXECUTE AS LOGIN=N'" + login + "';");
                    try { Tests.Reject(() => SqlDatabaseBackup.RequireMetadata(connection)); }
                    finally { Execute(connection, "REVERT;"); }
                }
                finally { Execute(connection, "DROP LOGIN " + Q(login) + ";"); }
            }
        });
    }

    private static void RoundTrip(string instance, string connectionString)
    {
        using (var fixture = new Tests.Fixture())
        using (var connection = new SqlConnection(connectionString))
        {
            connection.Open();
            Console.WriteLine("SQL: " + Scalar(connection, "SELECT @@VERSION;"));
            string prefix = "CPIT_" + Guid.NewGuid().ToString("N");
            var created = new List<string>();
            string config = prefix + "_C";
            string runtime = prefix + "_R";
            try
            {
                CreateDatabase(connection, config, fixture.Source, "ProjectA"); created.Add(config);
                CreateDatabase(connection, runtime, fixture.Source, "ProjectART"); created.Add(runtime);
                Execute(connection, "CREATE TABLE " + Q(config) + ".dbo.Probe(Id int NOT NULL); INSERT INTO " + Q(config) + ".dbo.Probe VALUES(42);");
                Execute(connection, "CREATE TABLE " + Q(runtime) + ".dbo.Probe(Id int NOT NULL); INSERT INTO " + Q(runtime) + ".dbo.Probe VALUES(1);");
                int updates = 0;
                BackupResult result;
                using (var stop = new CancellationTokenSource())
                {
                    Task writer = Task.Run(() =>
                    {
                        using (var live = new SqlConnection(connectionString))
                        {
                            live.Open();
                            while (!stop.IsCancellationRequested)
                            {
                                Execute(live, "UPDATE " + Q(runtime) + ".dbo.Probe SET Id=Id+1;");
                                Interlocked.Increment(ref updates);
                                stop.Token.WaitHandle.WaitOne(20);
                            }
                        }
                    });
                    try { result = new BackupEngine(new SqlDatabaseBackup(instance, WindowsIdentity.GetCurrent().Name)).Run(fixture.Mcp, fixture.Target, null, CancellationToken.None); }
                    finally { stop.Cancel(); writer.GetAwaiter().GetResult(); }
                }
                Console.WriteLine(result.Details);
                Tests.Assert(result.Success, result.Details);
                Tests.Assert(updates > 0, "source write loop did not execute");
                Tests.Assert(Convert.ToString(Scalar(connection, "SELECT state_desc FROM sys.databases WHERE name=N'" + runtime + "';")) == "ONLINE", "source is not online");
                Tests.Assert(Convert.ToInt32(Scalar(connection, "SELECT COUNT(*) FROM sys.databases WHERE name LIKE 'CopyProject_TEMP_%';")) == 0, "temporary database remains");
                Tests.Assert(Directory.GetFileSystemEntries(fixture.Target).Length == 1, "unexpected work resources");
                string restored = Directory.CreateDirectory(Path.Combine(fixture.Root, "Verify")).FullName;
                ZipFile.ExtractToDirectory(result.OutputPath, restored);
                string project = Path.Combine(restored, "ProjectA");
                foreach (string baseName in new[] { "ProjectA", "ProjectART" })
                {
                    string name = prefix + "_Verify_" + baseName;
                    string mdf = Path.Combine(project, baseName + ".mdf");
                    string ldf = Path.Combine(project, baseName + ".ldf");
                    Execute(connection, "CREATE DATABASE " + Q(name) + " ON (FILENAME=N'" + L(mdf) + "'),(FILENAME=N'" + L(ldf) + "') FOR ATTACH;"); created.Add(name);
                    int value = Convert.ToInt32(Scalar(connection, "SELECT Id FROM " + Q(name) + ".dbo.Probe;"));
                    Tests.Assert(baseName == "ProjectA" ? value == 42 : value >= 1, "restored content mismatch");
                }
                Console.WriteLine("Concurrent source updates: " + updates);
            }
            finally
            {
                foreach (string name in created)
                {
                    // Exact names created by this isolated fixture only; never scan/drop by prefix.
                    Execute(connection, "IF DB_ID(N'" + name + "') IS NOT NULL BEGIN ALTER DATABASE " + Q(name) + " SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE " + Q(name) + "; END;");
                }
            }
        }
    }

    private static void CreateDatabase(SqlConnection connection, string name, string directory, string baseName)
    {
        string mdf = Path.Combine(directory, baseName + ".mdf");
        string ldf = Path.Combine(directory, baseName + ".ldf");
        Execute(connection, "CREATE DATABASE " + Q(name) + " ON PRIMARY (NAME=N'" + name + "D',FILENAME=N'" + L(mdf) + "',SIZE=8MB) LOG ON (NAME=N'" + name + "L',FILENAME=N'" + L(ldf) + "',SIZE=8MB);");
    }
    private static void Execute(SqlConnection connection, string sql) { using (var command = new SqlCommand(sql, connection)) { command.CommandTimeout = 60; command.ExecuteNonQuery(); } }
    private static object Scalar(SqlConnection connection, string sql) { using (var command = new SqlCommand(sql, connection)) { command.CommandTimeout = 30; return command.ExecuteScalar(); } }
    private static string Q(string value) { return SqlDatabaseBackup.Quote(value); }
    private static string L(string value) { return SqlDatabaseBackup.Literal(value); }
}
