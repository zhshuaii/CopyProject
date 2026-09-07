namespace CopyProject
{
    partial class MainForm
    {
        private System.ComponentModel.IContainer components = null;

        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        private void InitializeComponent()
        {
            System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(MainForm));
            this.txtSourcePath = new System.Windows.Forms.TextBox();
            this.BtnSelectSource = new System.Windows.Forms.Button();
            this.BtnCopyProject = new System.Windows.Forms.Button();
            this.label1 = new System.Windows.Forms.Label();
            this.txtVersion = new System.Windows.Forms.Label();
            this.txtAuthor = new System.Windows.Forms.Label();
            this.label2 = new System.Windows.Forms.Label();
            this.progressBar1 = new System.Windows.Forms.ProgressBar();
            this.labelProgress = new System.Windows.Forms.Label();
            this.BtnCancel = new System.Windows.Forms.Button();
            this.BtnCancel.Location = new System.Drawing.Point(470, 65);
            this.BtnCancel.Size = new System.Drawing.Size(100, 25);
            this.BtnCancel.Text = "取消备份";
            this.BtnCancel.Enabled = false;
            this.BtnCancel.TabIndex = 16;
            this.BtnCancel.Click += new System.EventHandler(this.BtnCancel_Click);
            this.Controls.Add(this.BtnCancel);
            this.SuspendLayout();
            // 
            // txtSourcePath
            // 
            this.txtSourcePath.Font = new System.Drawing.Font("宋体", 9F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(134)));
            this.txtSourcePath.Location = new System.Drawing.Point(12, 30);
            this.txtSourcePath.Name = "txtSourcePath";
            this.txtSourcePath.Size = new System.Drawing.Size(400, 25);
            this.txtSourcePath.TabIndex = 0;
            // 
            // BtnSelectSource
            // 
            this.BtnSelectSource.Font = new System.Drawing.Font("宋体", 9F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(134)));
            this.BtnSelectSource.Location = new System.Drawing.Point(418, 30);
            this.BtnSelectSource.Name = "BtnSelectSource";
            this.BtnSelectSource.Size = new System.Drawing.Size(30, 25);
            this.BtnSelectSource.TabIndex = 2;
            this.BtnSelectSource.Text = "…";
            this.BtnSelectSource.UseVisualStyleBackColor = true;
            this.BtnSelectSource.Click += new System.EventHandler(this.BtnSelectSource_Click);
            // 
            // BtnCopyProject
            // 
            this.BtnCopyProject.Font = new System.Drawing.Font("宋体", 9F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(134)));
            this.BtnCopyProject.Location = new System.Drawing.Point(470, 30);
            this.BtnCopyProject.Name = "BtnCopyProject";
            this.BtnCopyProject.Size = new System.Drawing.Size(100, 25);
            this.BtnCopyProject.TabIndex = 9;
            this.BtnCopyProject.Text = "备份项目";
            this.BtnCopyProject.UseVisualStyleBackColor = true;
            this.BtnCopyProject.Click += new System.EventHandler(this.BtnCopyProject_Click);
            // 
            // label1
            // 
            this.label1.AutoSize = true;
            this.label1.Font = new System.Drawing.Font("宋体", 9F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(134)));
            this.label1.Location = new System.Drawing.Point(9, 9);
            this.label1.Name = "label1";
            this.label1.Size = new System.Drawing.Size(142, 15);
            this.label1.TabIndex = 10;
            this.label1.Text = "选择要备份的源项目";
            // 
            // txtVersion
            // 
            this.txtVersion.AutoSize = true;
            this.txtVersion.Location = new System.Drawing.Point(436, 101);
            this.txtVersion.Name = "txtVersion";
            this.txtVersion.Size = new System.Drawing.Size(134, 15);
            this.txtVersion.TabIndex = 11;
            this.txtVersion.Text = "";
            // 
            // txtAuthor
            // 
            this.txtAuthor.AutoSize = true;
            this.txtAuthor.Location = new System.Drawing.Point(388, 125);
            this.txtAuthor.Name = "txtAuthor";
            this.txtAuthor.Size = new System.Drawing.Size(182, 15);
            this.txtAuthor.TabIndex = 12;
            this.txtAuthor.Text = "Author：ZhangHongShuai";
            // 
            // label2
            // 
            this.label2.BorderStyle = System.Windows.Forms.BorderStyle.FixedSingle;
            this.label2.Location = new System.Drawing.Point(12, 60);
            this.label2.Name = "label2";
            this.label2.Size = new System.Drawing.Size(400, 50);
            this.label2.TabIndex = 13;
            this.label2.Text = "WinCC Runtime 无需停止即可备份项目\r\n不备份历史数据库；备份期间请勿编辑/切换工程";
            this.label2.TextAlign = System.Drawing.ContentAlignment.MiddleCenter;
            // 
            // progressBar1
            // 
            this.progressBar1.Location = new System.Drawing.Point(12, 120);
            this.progressBar1.Name = "progressBar1";
            this.progressBar1.Size = new System.Drawing.Size(200, 25);
            this.progressBar1.TabIndex = 14;
            // 
            // labelProgress
            // 
            this.labelProgress.AutoSize = false;
            this.labelProgress.Location = new System.Drawing.Point(12, 153);
            this.labelProgress.Name = "labelProgress";
            this.labelProgress.Size = new System.Drawing.Size(558, 40);
            this.labelProgress.TabIndex = 15;
            this.labelProgress.Text = "准备中...";
            // 
            // MainForm
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(120F, 120F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Dpi;
            this.ClientSize = new System.Drawing.Size(582, 205);
            this.Controls.Add(this.labelProgress);
            this.Controls.Add(this.progressBar1);
            this.Controls.Add(this.label2);
            this.Controls.Add(this.txtAuthor);
            this.Controls.Add(this.txtVersion);
            this.Controls.Add(this.label1);
            this.Controls.Add(this.BtnCopyProject);
            this.Controls.Add(this.txtSourcePath);
            this.Controls.Add(this.BtnSelectSource);
            this.FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedSingle;
            this.Icon = ((System.Drawing.Icon)(resources.GetObject("$this.Icon")));
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.Name = "MainForm";
            this.Text = "Copy Project";
            this.ResumeLayout(false);
            this.PerformLayout();
        }

        #endregion

        private System.Windows.Forms.Button BtnCancel;
        private System.Windows.Forms.TextBox txtSourcePath;
        private System.Windows.Forms.Button BtnSelectSource;
        private System.Windows.Forms.Button BtnCopyProject;
        private System.Windows.Forms.Label label1;
        private System.Windows.Forms.Label txtVersion;
        private System.Windows.Forms.Label txtAuthor;
        private System.Windows.Forms.Label label2;
        private System.Windows.Forms.ProgressBar progressBar1;
        private System.Windows.Forms.Label labelProgress;
    }
}
