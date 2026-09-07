using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CopyProject
{
    public partial class MainForm : Form
    {
        private readonly Func<string, string, IProgress<BackupProgress>, CancellationToken, BackupResult> backup;
        private CancellationTokenSource cancellation;
        private bool busy;
        private bool closeRequested;

        public MainForm() : this(new BackupEngine().Run) { }
        internal MainForm(Func<string, string, IProgress<BackupProgress>, CancellationToken, BackupResult> backup)
        {
            this.backup = backup;
            InitializeComponent();
            txtVersion.Text = "Version：" + Application.ProductVersion;
        }

        private void BtnSelectSource_Click(object sender, EventArgs e)
        {
            using (var dialog = new OpenFileDialog { Filter = "WinCC Project (*.mcp)|*.mcp", Title = "选择 WinCC 项目文件" })
                if (dialog.ShowDialog(this) == DialogResult.OK) txtSourcePath.Text = dialog.FileName;
        }

        private async void BtnCopyProject_Click(object sender, EventArgs e)
        {
            // Capture controls on the UI thread. The worker receives only immutable strings.
            string source = txtSourcePath.Text;
            if (string.IsNullOrWhiteSpace(source) || !File.Exists(source))
            {
                MessageBox.Show(this, "请选择有效的 MCP 文件。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            using (var dialog = new FolderBrowserDialog { Description = "选择本地 NTFS/ReFS 备份目录；备份期间请勿编辑或切换工程" })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                string target = dialog.SelectedPath;
                try
                {
                    BackupResult result = await RunBackupAsync(source, target);
                    if (result.Success) MessageBox.Show(this, "备份完成：\r\n" + result.OutputPath, "完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    else if (!result.Cancelled || result.Error != null || result.CleanupErrors.Count != 0 || result.Residuals.Count != 0) ShowDetails(result);
                    if (closeRequested) Close();
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, ex.ToString(), "任务错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    if (closeRequested) Close();
                }
            }
        }

        internal async Task<BackupResult> RunBackupAsync(string source, string target)
        {
            if (busy) throw new InvalidOperationException("已有任务运行中。");
            SetBusy(true);
            progressBar1.Value = 0;
            labelProgress.Text = "正在预检查...";
            cancellation = new CancellationTokenSource();
            CancellationToken token = cancellation.Token;
            var progress = new Progress<BackupProgress>(p =>
            {
                if (IsDisposed || !busy) return;
                progressBar1.Value = p.Percent;
                labelProgress.Text = closeRequested ? "正在取消并清理，完成后关闭..." : p.Message;
            });
            try
            {
                BackupResult result = await Task.Run(() => backup(source, target, progress, token));
                labelProgress.Text = result.Success ? "备份完成" : result.Cancelled ? "已取消；请核对清理结果" : "任务未完整完成；请查看详情";
                return result;
            }
            finally
            {
                cancellation.Dispose();
                cancellation = null;
                SetBusy(false);
            }
        }

        private void BtnCancel_Click(object sender, EventArgs e)
        {
            if (cancellation == null) return;
            cancellation.Cancel();
            BtnCancel.Enabled = false;
            labelProgress.Text = "正在取消并清理...";
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (busy)
            {
                e.Cancel = true;
                closeRequested = true;
                BtnCancel_Click(this, EventArgs.Empty);
                labelProgress.Text = "正在取消并清理，完成后关闭...";
            }
            base.OnFormClosing(e);
        }

        private void SetBusy(bool value)
        {
            busy = value;
            BtnSelectSource.Enabled = !value;
            BtnCopyProject.Enabled = !value;
            txtSourcePath.Enabled = !value;
            BtnCancel.Enabled = value;
        }

        private void ShowDetails(BackupResult result)
        {
            using (var dialog = new Form { Text = result.OutputPath == null ? "备份未完成（可复制详情）" : "ZIP 已发布，但清理未完成", Width = 820, Height = 500, StartPosition = FormStartPosition.CenterParent })
            using (var text = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Fill, Text = result.Details })
            {
                dialog.Controls.Add(text);
                dialog.ShowDialog(this);
            }
        }
    }
}
