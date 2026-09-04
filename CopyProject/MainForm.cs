using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CopyProject
{
    public partial class MainForm : Form
    {
        public MainForm()
        {
            InitializeComponent();
            txtVersion.Text = "Version：" + Application.ProductVersion;
        }

        private void BtnSelectSource_Click(object sender, EventArgs e)
        {
            using (OpenFileDialog dialog = new OpenFileDialog())
            {
                dialog.Filter = "WinCC Project (*.mcp)|*.mcp|All files (*.*)|*.*";
                dialog.Title = "选择 WinCC 项目文件";

                if (dialog.ShowDialog() == DialogResult.OK)
                    txtSourcePath.Text = dialog.FileName;
            }
        }

        private async void BtnCopyProject_Click(object sender, EventArgs e)
        {
            if (string.IsNullOrWhiteSpace(txtSourcePath.Text) || !File.Exists(txtSourcePath.Text))
            {
                MessageBox.Show("请先选择有效的 WinCC MCP 项目文件。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using (FolderBrowserDialog dialog = new FolderBrowserDialog())
            {
                dialog.Description = "选择备份 ZIP 的保存目录";

                if (dialog.ShowDialog() != DialogResult.OK)
                    return;

                SetBusy(true);
                progressBar1.Value = 0;
                labelProgress.Text = "准备中...";

                try
                {
                    BackupEngine engine = new BackupEngine();
                    Progress<BackupProgress> progress = new Progress<BackupProgress>(UpdateProgress);

                    string zipPath = await Task.Run(() =>
                        engine.CreateZipBackup(txtSourcePath.Text, dialog.SelectedPath, progress));

                    MessageBox.Show(
                        "备份完成！\r\n\r\n" + zipPath,
                        "成功",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(
                        "备份失败：\r\n" + ex.Message,
                        "错误",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                }
                finally
                {
                    SetBusy(false);
                }
            }
        }

        private void UpdateProgress(BackupProgress progress)
        {
            progressBar1.Value = progress.Percent;
            labelProgress.Text = progress.Message;
        }

        private void SetBusy(bool busy)
        {
            BtnSelectSource.Enabled = !busy;
            BtnCopyProject.Enabled = !busy;
            txtSourcePath.Enabled = !busy;
        }
    }
}
