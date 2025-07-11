using System;
using System.Data;
using System.Data.SqlClient;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CopyProject
{
    public partial class MainForm : Form
    {
        const string ConnString = "Server=.\\WINCC;Database=master;Integrated Security=true;";
        public MainForm()
        {
            InitializeComponent();
            txtVersion.Text = "Version：" + Application.ProductVersion;
        }
        private void BtnSelectSource_Click(object sender, EventArgs e)
        {
            using var dialog = new OpenFileDialog
            {
                Filter = "WinCC Project (*.mcp)|*.mcp|All files (*.*)|*.*",
                Title = "选择 WinCC 项目文件"
            };

            if (dialog.ShowDialog() == DialogResult.OK)
            {
                txtSourcePath.Text = dialog.FileName;
            }
        }
        private async void BtnCopyProject_Click(object sender, EventArgs e)
        {
            try
            {
                using var dialog = new FolderBrowserDialog
                {
                    Description = "选择目标文件夹"
                };

                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    var sourceDir = Path.GetDirectoryName(txtSourcePath.Text) ?? throw new InvalidOperationException("未指定来源目录");
                    var targetRoot = dialog.SelectedPath;
                    var projectName = Path.GetFileNameWithoutExtension(txtSourcePath.Text);
                    var destDir = Path.Combine(targetRoot, new DirectoryInfo(sourceDir).Name);

                    var totalFiles = CountFiles(sourceDir);
                    var copiedFiles = 0;

                    var progress = new Progress<int>(value =>
                    {
                        int percent = (int)((value * 100.0) / totalFiles);
                        progressBar1.Value = percent;
                        labelProgress.Text = $"{value}/{totalFiles} ({percent}%)";
                    });

                    await Task.Run(() =>
                    {
                        CopyDirectory(sourceDir, destDir, ref copiedFiles, totalFiles, (current, _) => ((IProgress<int>)progress).Report(current));
                    });
                    CopyDatabase(projectName, sourceDir, destDir);
                    CopyDatabase(projectName + "RT", sourceDir, destDir);

                    if (MessageBox.Show("复制完成！是否归档为 ZIP？", "确认", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK)
                    {
                        ZipFile.CreateFromDirectory(destDir, $"{destDir}_{DateTime.Now:yyyyMMdd_HHmmss}.zip", CompressionLevel.Fastest, includeBaseDirectory: false);
                        MessageBox.Show("归档完成！", "成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                }

            }
            catch (Exception ex)
            {
                MessageBox.Show($"发生错误：{ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        private void CopyDatabase(string baseName, string sourceDir, string destDir)
        {
            var mdfPath = Path.Combine(sourceDir, baseName + ".mdf");
            var ldfPath = Path.Combine(sourceDir, baseName + ".ldf");

            if (!IsMdfAttached(mdfPath))
            {
                File.Copy(mdfPath, Path.Combine(destDir, Path.GetFileName(mdfPath)), overwrite: true);
                File.Copy(ldfPath, Path.Combine(destDir, Path.GetFileName(ldfPath)), overwrite: true);

                return;
            }

            var dbName = GetDatabaseName(mdfPath);
            var bakFile = Path.Combine(destDir, $"{dbName}_{DateTime.Now:yyyyMMdd_HHmmss}.bak");
            BackupDatabase(dbName, bakFile);
            RestoreDatabase(baseName, bakFile);

            File.Delete(bakFile);
        }
        private bool IsMdfAttached(string mdfPath)
        {
            const string sql = @"SELECT COUNT(*) FROM sys.master_files WHERE LOWER(physical_name) = LOWER(@mdf);";

            using (var cn = new SqlConnection(ConnString))
            using (var cmd = new SqlCommand(sql, cn))
            {
                cmd.Parameters.AddWithValue("@mdf", mdfPath);
                cn.Open();
                int count = (int)cmd.ExecuteScalar();
                return count > 0;
            }
        }
        private string GetDatabaseName(string mdfPath)
        {
            const string sql = @"SELECT DB_NAME(database_id) FROM sys.master_files WHERE LOWER(physical_name) = LOWER(@mdf);";

            using (var cn = new SqlConnection(ConnString))
            using (var cmd = new SqlCommand(sql, cn))
            {
                cmd.Parameters.AddWithValue("@mdf", mdfPath);
                cn.Open();
                object obj = cmd.ExecuteScalar();
                if (obj == null || obj == DBNull.Value)
                    throw new Exception("在 sys.master_files 中未找到对应的 mdf 路径。");
                return (string)obj;
            }
        }
        private void BackupDatabase(string dbName, string bakFile)
        {
            string sql = $@"BACKUP DATABASE [{dbName}] TO DISK = @bak WITH INIT, COPY_ONLY;";

            using (var cn = new SqlConnection(ConnString))
            using (var cmd = new SqlCommand(sql, cn))
            {
                cmd.Parameters.AddWithValue("@bak", bakFile);
                cn.Open();
                cmd.ExecuteNonQuery();
            }
        }
        private void RestoreDatabase(string baseName, string bakPath)
        {
            // 读出逻辑名
            const string fileListSql = "RESTORE FILELISTONLY FROM DISK = @bak";
            string logicalData = null, logicalLog = null;
            using (var cn = new SqlConnection(ConnString))
            using (var cmd = new SqlCommand(fileListSql, cn))
            {
                cmd.Parameters.AddWithValue("@bak", bakPath);
                cn.Open();
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var type = reader["Type"].ToString();
                        if (type == "D") logicalData = reader["LogicalName"].ToString();
                        else if (type == "L") logicalLog = reader["LogicalName"].ToString();
                    }
                }
            }
            if (logicalData == null || logicalLog == null)
                throw new InvalidOperationException("无法从备份中获取逻辑文件名");

            // 生成临时库名并还原
            var tempDb = "Temp_" + Guid.NewGuid().ToString("N");
            var dataDest = Path.Combine(Path.GetDirectoryName(bakPath), baseName + ".mdf");
            var logDest = Path.Combine(Path.GetDirectoryName(bakPath), baseName + ".ldf");
            var restoreSql = $@"
                RESTORE DATABASE [{tempDb}]
                    FROM DISK = @bak
                    WITH
                    MOVE @logicalData TO @dataDest,
                    MOVE @logicalLog  TO @logDest,
                    RECOVERY, REPLACE;";

            using (var cn = new SqlConnection(ConnString))
            using (var cmd = new SqlCommand(restoreSql, cn))
            {
                cmd.Parameters.AddWithValue("@bak", bakPath);
                cmd.Parameters.AddWithValue("@logicalData", logicalData);
                cmd.Parameters.AddWithValue("@logicalLog", logicalLog);
                cmd.Parameters.AddWithValue("@dataDest", dataDest);
                cmd.Parameters.AddWithValue("@logDest", logDest);

                cn.Open();
                cmd.ExecuteNonQuery();
            }

            // 第三步：卸载临时库
            using (var cn = new SqlConnection(ConnString))
            using (var cmd = new SqlCommand("EXEC sp_detach_db @db, 'true';", cn))
            {
                cmd.Parameters.AddWithValue("@db", tempDb);
                cn.Open();
                cmd.ExecuteNonQuery();
            }
        }
        private int CountFiles(string sourceDir)
        {
            int count = 0;

            foreach (string file in Directory.GetFiles(sourceDir))
            {
                string ext = Path.GetExtension(file).ToLower();
                if (ext != ".mdf" && ext != ".ldf" && ext != ".lck")
                    count++;
            }

            foreach (string dir in Directory.GetDirectories(sourceDir))
            {
                count += CountFiles(dir);
            }

            return count;
        }
        private void CopyDirectory(string sourceDir, string targetDir, ref int copied, int total, Action<int, int> progressCallback)
        {
            Directory.CreateDirectory(targetDir);

            foreach (string file in Directory.GetFiles(sourceDir))
            {
                string ext = Path.GetExtension(file).ToLower();
                if (ext != ".mdf" && ext != ".ldf" && ext != ".lck")
                {
                    string targetFile = Path.Combine(targetDir, Path.GetFileName(file));
                    File.Copy(file, targetFile, true);
                    copied++;
                    progressCallback?.Invoke(copied, total);
                }
            }

            foreach (string dir in Directory.GetDirectories(sourceDir))
            {
                string targetSubDir = Path.Combine(targetDir, Path.GetFileName(dir));
                CopyDirectory(dir, targetSubDir, ref copied, total, progressCallback);
            }
        }
    }
}
