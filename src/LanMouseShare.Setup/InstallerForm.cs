using System;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace LanMouseShare.Setup
{
    /// <summary>
    /// 安装 / 卸载界面。
    /// 所有实际动作都跑在后台线程上，界面只负责显示进度 ——
    /// 解压和复制卸载程序加起来可能要好几百毫秒，
    /// 在 UI 线程上直接做会让窗口整个卡住、进度条一格都不动。
    /// </summary>
    internal sealed class InstallerForm : Form
    {
        private enum Stage
        {
            Ready,
            Working,
            Done
        }

        private readonly SetupOptions _options;
        private readonly bool _uninstallMode;

        private Stage _stage = Stage.Ready;
        private bool _lastRunSucceeded;
        private bool _launchAfterInstall = true;
        private string _installedExePath = string.Empty;

        private Label _titleLabel;
        private Label _subtitleLabel;
        private Panel _separator;
        private Label _infoLabel;
        private LinkLabel _linkLabel;
        private Label _statusLabel;
        private ProgressBar _progressBar;
        private Button _primaryButton;
        private Button _secondaryButton;
        private Button _cancelButton;

        /// <summary>进程退出码。0 成功，1 失败。</summary>
        public int ExitCode { get; private set; }

        public InstallerForm(SetupOptions options)
        {
            _options = options ?? new SetupOptions();
            _uninstallMode = _options.Uninstall;

            BuildUi();
            FillInfo();
        }

        // ==================================================================
        //  界面搭建
        // ==================================================================

        private void BuildUi()
        {
            SuspendLayout();

            Text = _uninstallMode
                ? "卸载 " + SetupEngine.ProductName
                : SetupEngine.ProductName + " 安装程序";
            ClientSize = new Size(560, 360);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ShowIcon = true;
            Font = UiFont(9F, FontStyle.Regular);
            BackColor = Color.FromArgb(250, 250, 252);

            try
            {
                var self = SetupEngine.CurrentExecutablePath;
                if (!string.IsNullOrEmpty(self)) { Icon = Icon.ExtractAssociatedIcon(self); }
            }
            catch (Exception)
            {
            }

            _titleLabel = new Label();
            _titleLabel.AutoSize = true;
            _titleLabel.Location = new Point(24, 18);
            _titleLabel.Font = UiFont(16F, FontStyle.Bold);
            _titleLabel.ForeColor = Color.FromArgb(24, 24, 32);
            _titleLabel.Text = _uninstallMode ? "卸载 LanMouseShare" : "安装 LanMouseShare";
            Controls.Add(_titleLabel);

            _subtitleLabel = new Label();
            _subtitleLabel.AutoSize = true;
            _subtitleLabel.Location = new Point(27, 56);
            _subtitleLabel.ForeColor = Color.FromArgb(110, 110, 122);
            _subtitleLabel.Text = "局域网键鼠共享 · 版本 " + SetupEngine.ProductVersion +
                                  " · 支持 Windows 7 SP1 / 10 / 11";
            Controls.Add(_subtitleLabel);

            _separator = new Panel();
            _separator.Location = new Point(24, 82);
            _separator.Size = new Size(512, 1);
            _separator.BackColor = Color.FromArgb(222, 222, 228);
            Controls.Add(_separator);

            _infoLabel = new Label();
            _infoLabel.Location = new Point(26, 94);
            _infoLabel.Size = new Size(508, 86);
            _infoLabel.ForeColor = Color.FromArgb(48, 48, 58);
            Controls.Add(_infoLabel);

            _linkLabel = new LinkLabel();
            _linkLabel.AutoSize = true;
            _linkLabel.Location = new Point(26, 188);
            _linkLabel.Text = "下载页：" + SetupEngine.ReleasesUrl;
            _linkLabel.LinkColor = Color.FromArgb(0, 102, 204);
            _linkLabel.ActiveLinkColor = Color.FromArgb(0, 60, 140);
            _linkLabel.LinkClicked += delegate { SetupEngine.OpenUrl(SetupEngine.ReleasesUrl); };
            Controls.Add(_linkLabel);

            _statusLabel = new Label();
            _statusLabel.AutoSize = true;
            _statusLabel.Location = new Point(26, 214);
            _statusLabel.ForeColor = Color.FromArgb(48, 48, 58);
            _statusLabel.Text = _uninstallMode ? "准备卸载。" : "准备就绪，点「安装并启动」开始。";
            Controls.Add(_statusLabel);

            _progressBar = new ProgressBar();
            _progressBar.Location = new Point(26, 240);
            _progressBar.Size = new Size(508, 16);
            _progressBar.Style = ProgressBarStyle.Continuous;
            _progressBar.Minimum = 0;
            _progressBar.Maximum = 100;
            _progressBar.Value = 0;
            Controls.Add(_progressBar);

            _primaryButton = MakeButton(
                _uninstallMode ? "卸载" : "安装并启动",
                new Rectangle(386, 296, 150, 34),
                true);
            _primaryButton.Click += OnPrimaryClick;
            Controls.Add(_primaryButton);

            _secondaryButton = MakeButton(
                _uninstallMode ? string.Empty : "仅安装",
                new Rectangle(268, 296, 110, 34),
                false);
            _secondaryButton.Click += OnSecondaryClick;
            _secondaryButton.Visible = !_uninstallMode;
            Controls.Add(_secondaryButton);

            _cancelButton = MakeButton("取消", new Rectangle(180, 296, 80, 34), false);
            _cancelButton.Click += delegate
            {
                if (_stage == Stage.Working) { return; }
                Close();
            };
            Controls.Add(_cancelButton);

            AcceptButton = _primaryButton;
            CancelButton = _cancelButton;

            ResumeLayout(false);
            PerformLayout();
        }

        private Button MakeButton(string text, Rectangle bounds, bool primary)
        {
            var b = new Button();
            b.Text = text;
            b.Bounds = bounds;
            b.FlatStyle = FlatStyle.System;
            b.UseVisualStyleBackColor = true;
            if (primary)
            {
                b.Font = UiFont(9.5F, FontStyle.Bold);
            }
            return b;
        }

        /// <summary>
        /// 按可用性挑字体。
        /// 「Microsoft YaHei UI」是 Win8 之后才有的，
        /// Win7 上直接 new Font 会静默回退成宋体，
        /// 所以这里要检查实际拿到的字体名。
        /// </summary>
        private static Font UiFont(float size, FontStyle style)
        {
            var candidates = new[] { "Microsoft YaHei UI", "Microsoft YaHei", "Segoe UI", "Tahoma" };
            foreach (var name in candidates)
            {
                try
                {
                    var f = new Font(name, size, style);
                    if (string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase))
                    {
                        return f;
                    }
                    f.Dispose();
                }
                catch (Exception)
                {
                }
            }
            return new Font(SystemFonts.MessageBoxFont.FontFamily, size, style);
        }

        private void FillInfo()
        {
            var sb = new StringBuilder();
            var dir = _uninstallMode ? SetupEngine.ResolveInstalledDirectory() : ResolveTargetDirectory();

            sb.Append("安装位置：").Append(dir).Append(Environment.NewLine);

            string dotnetDetail;
            var dotnetOk = SetupEngine.IsDotNet48Installed(out dotnetDetail);
            sb.Append("运行环境：").Append(dotnetDetail).Append(Environment.NewLine);

            if (_uninstallMode)
            {
                sb.Append("配置文件：").Append(SetupEngine.UserDataDirectory).Append("（保留）").Append(Environment.NewLine);
                sb.Append("卸载后，程序目录与桌面、开始菜单的快捷方式都会被删除。");
            }
            else
            {
                int fileCount;
                long totalBytes;
                SetupEngine.GetPayloadSummary(out fileCount, out totalBytes);
                if (fileCount <= 0)
                {
                    sb.Append("安装包内容：这个安装包不完整，里面没有程序文件。").Append(Environment.NewLine);
                }
                else
                {
                    var size = totalBytes < 1024
                        ? totalBytes + " 字节"
                        : (totalBytes / 1024.0 / 1024.0).ToString("0.00") + " MB";
                    sb.Append("安装包内容：").Append(fileCount).Append(" 个文件，共 ").Append(size).Append(Environment.NewLine);
                }
                sb.Append("快捷方式：桌面、开始菜单（程序需要管理员权限，启动时会弹 UAC）");
            }

            _infoLabel.Text = sb.ToString();
        }

        private string ResolveTargetDirectory()
        {
            if (!string.IsNullOrWhiteSpace(_options.TargetDirectory))
            {
                try { return Path.GetFullPath(_options.TargetDirectory); }
                catch (Exception) { }
            }
            return SetupEngine.DefaultTargetDirectory;
        }

        // ==================================================================
        //  按钮
        // ==================================================================

        private void OnPrimaryClick(object sender, EventArgs e)
        {
            if (_stage == Stage.Done)
            {
                if (_lastRunSucceeded)
                {
                    SetupEngine.LaunchApp(_installedExePath);
                    Close();
                    return;
                }
                StartInstall(true);
                return;
            }

            if (_stage == Stage.Working) { return; }
            StartInstall(true);
        }

        private void OnSecondaryClick(object sender, EventArgs e)
        {
            if (_stage == Stage.Done)
            {
                Close();
                return;
            }
            if (_stage == Stage.Working) { return; }
            StartInstall(false);
        }

        private void StartInstall(bool launch)
        {
            _launchAfterInstall = launch;

            if (_uninstallMode)
            {
                if (MessageBox.Show(
                        "确定要卸载 LanMouseShare 吗？" + Environment.NewLine + Environment.NewLine +
                        "程序目录和快捷方式会被删除；" + Environment.NewLine +
                        "你的配置和日志会保留在：" + Environment.NewLine +
                        SetupEngine.UserDataDirectory,
                        "确认卸载",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Question,
                        MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                {
                    return;
                }
            }

            SetStage(Stage.Working);

            var thread = new Thread(RunWorker);
            thread.IsBackground = true;
            thread.Name = "LmsSetupWorker";
            thread.Start();
        }

        private void RunWorker()
        {
            InstallResult result;
            try
            {
                result = _uninstallMode
                    ? SetupEngine.Uninstall(_options, OnReport, OnProgress)
                    : SetupEngine.Install(_options, OnReport, OnProgress);
            }
            catch (Exception ex)
            {
                result = InstallResult.Fail("出现了未处理的错误：" + ex.Message);
            }

            Post(delegate { OnFinished(result); });
        }

        private void OnFinished(InstallResult result)
        {
            _lastRunSucceeded = result.Ok;
            _installedExePath = result.ExecutablePath;

            if (result.Ok)
            {
                _progressBar.Value = 100;

                if (_uninstallMode)
                {
                    _statusLabel.Text = "卸载完成。";
                    _primaryButton.Visible = false;
                }
                else
                {
                    _statusLabel.Text = "安装完成，装在：" + result.TargetDirectory;
                    _secondaryButton.Text = "完成并关闭";
                    _cancelButton.Visible = false;
                    ExitCode = 0;
                    SetStage(Stage.Done);

                    if (_launchAfterInstall)
                    {
                        SetupEngine.LaunchApp(_installedExePath);
                        Close();
                    }
                    else
                    {
                        _primaryButton.Text = "打开 LanMouseShare";
                    }
                    return;
                }

                _secondaryButton.Text = "完成并关闭";
                _secondaryButton.Visible = true;
                _cancelButton.Visible = false;
                ExitCode = 0;
            }
            else
            {
                _progressBar.Value = 0;
                _statusLabel.Text = "失败：" + result.Message;
                _primaryButton.Text = "重试";
                _primaryButton.Visible = true;
                _secondaryButton.Visible = false;
                _cancelButton.Text = "关闭";
                ExitCode = 1;

                MessageBox.Show(this, result.Message, "安装失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }

            SetStage(Stage.Done);
        }

        private void SetStage(Stage stage)
        {
            _stage = stage;
            var busy = stage == Stage.Working;

            _primaryButton.Enabled = !busy;
            _secondaryButton.Enabled = !busy;
            _cancelButton.Enabled = !busy;

            if (busy)
            {
                _progressBar.Value = 0;
                _statusLabel.Text = _uninstallMode ? "正在卸载…" : "正在安装…";
            }
        }

        // ==================================================================
        //  后台线程到界面的桥
        // ==================================================================

        private void OnReport(string message)
        {
            Post(delegate { _statusLabel.Text = message; });
        }

        private void OnProgress(int percent)
        {
            if (percent < 0) { percent = 0; }
            if (percent > 100) { percent = 100; }
            Post(delegate
            {
                try { _progressBar.Value = percent; }
                catch (Exception) { }
            });
        }

        private void Post(Action action)
        {
            if (action == null) { return; }
            try
            {
                if (IsDisposed || !IsHandleCreated) { return; }
                BeginInvoke(action);
            }
            catch (Exception)
            {
            }
        }
    }
}
