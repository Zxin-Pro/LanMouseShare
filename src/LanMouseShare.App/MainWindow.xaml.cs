using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using LanMouseShare.Core.Config;
using LanMouseShare.Core.Diagnostics;
using LanMouseShare.Core.Infrastructure;
using LanMouseShare.Core.Input;
using LanMouseShare.Core.Interop;
using LanMouseShare.Core.Layout;
using LanMouseShare.Core.Net;
using LanMouseShare.Core.Sharing;
using LanMouseShare.Core.Update;

namespace LanMouseShare
{
    /// <summary>
    /// 主窗口（第六阶段：主窗口代理的完整产出）。
    ///
    /// 界面只做三件事：把配置读出来、把配置写回去、把会话状态显示出来。
    /// 真正的输入捕获与网络收发都在 Core 里，窗口不碰。
    /// </summary>
    public partial class MainWindow : Window
    {
        private const int MaxLogLines = 2000;

        private AppConfig _config;
        private SharingSession _session;
        private App _host;
        private SelfTestRunner _selfTest;
        private DispatcherTimer _saveTimer;
        private bool _initializing = true;
        private bool _exitRequested;
        private UpdateInfo _lastUpdate;

        public MainWindow()
        {
            InitializeComponent();

            _saveTimer = new DispatcherTimer();
            _saveTimer.Interval = TimeSpan.FromMilliseconds(700);
            _saveTimer.Tick += OnSaveTimerTick;

            LayoutPreview.SizeChanged += OnPreviewSizeChanged;

            LogPathText.Text = "日志文件：" + (Log.CurrentFile ?? "（不可用）");
            HeaderSubtitle.Text = "版本 " + UpdateChecker.GetCurrentVersionText() + " · 一套鼠标键盘，两台电脑";
            _initializing = false;
        }

        // ------------------------------------------------------------ 与 App 对接

        /// <summary>被 App 调用，注入配置、会话与宿主回调。</summary>
        public void Attach(AppConfig config, SharingSession session, App host)
        {
            _config = config;
            _session = session;
            _host = host;

            _session.Log += OnSessionLog;
            _session.ConnectedChanged += OnSessionConnected;
            _session.LocationChanged += OnSessionLocation;
            _session.RemoteScreenKnown += OnRemoteScreenKnown;

            LoadFromConfig();
            RefreshDiagnostics();
            RefreshPills();
        }

        public bool ExitRequested
        {
            get { return _exitRequested; }
        }

        /// <summary>真正退出前调用，解除「关闭即隐藏」的行为。</summary>
        public void PrepareForExit()
        {
            _exitRequested = true;
        }

        public void SetSelfTestResult(SelfTestRunner runner)
        {
            _selfTest = runner;
            if (runner != null && SelfTestDetails != null)
            {
                SelfTestDetails.Text = runner.Details;
            }
            RefreshDiagnostics();
        }

        // ------------------------------------------------------------ 配置 <-> 界面

        private void LoadFromConfig()
        {
            if (_config == null)
            {
                return;
            }

            _initializing = true;
            try
            {
                PortBox.Text = _config.Port.ToString(CultureInfo.InvariantCulture);
                KeyBox.Text = _config.PairingKey;
                PeerHostBox.Text = _config.PeerHost;
                RoleHost.IsChecked = _config.IsHost;
                RoleClient.IsChecked = !_config.IsHost;
                KeyboardCheck.IsChecked = _config.KeyboardEnabled;
                ReconnectCheck.IsChecked = _config.AutoReconnect;
                AutoStartCheck.IsChecked = _config.AutoStart;
                UpdateOnStartCheck.IsChecked = _config.CheckUpdateOnStart;

                switch (_config.Side)
                {
                    case ScreenSide.Left: SideLeft.IsChecked = true; break;
                    case ScreenSide.Above: SideAbove.IsChecked = true; break;
                    case ScreenSide.Below: SideBelow.IsChecked = true; break;
                    default: SideRight.IsChecked = true; break;
                }

                OffsetBox.Text = _config.OffsetPixels.ToString(CultureInfo.InvariantCulture);
                GuardBox.Text = _config.ReturnGuard.ToString(CultureInfo.InvariantCulture);
            }
            finally
            {
                _initializing = false;
            }

            RefreshNetworkHint();
            RefreshLayoutSummary();
            RedrawPreview();
            RefreshRoleDependentUi();
        }

        private void CollectToConfig()
        {
            if (_config == null)
            {
                return;
            }

            int port;
            if (int.TryParse(PortBox.Text, out port) && port >= AppConfig.MinPort && port <= AppConfig.MaxPort)
            {
                _config.Port = port;
            }

            _config.PairingKey = KeyBox.Text;
            _config.PeerHost = PeerHostBox.Text;
            _config.Role = RoleHost.IsChecked == true ? "host" : "client";
            _config.KeyboardEnabled = KeyboardCheck.IsChecked == true;
            _config.AutoReconnect = ReconnectCheck.IsChecked == true;
            _config.AutoStart = AutoStartCheck.IsChecked == true;
            _config.CheckUpdateOnStart = UpdateOnStartCheck.IsChecked == true;

            if (SideLeft.IsChecked == true) _config.Side = ScreenSide.Left;
            else if (SideAbove.IsChecked == true) _config.Side = ScreenSide.Above;
            else if (SideBelow.IsChecked == true) _config.Side = ScreenSide.Below;
            else _config.Side = ScreenSide.Right;

            int offset;
            if (int.TryParse(OffsetBox.Text, out offset))
            {
                if (offset < -10000) offset = -10000;
                if (offset > 10000) offset = 10000;
                _config.OffsetPixels = offset;
            }

            int guard;
            if (int.TryParse(GuardBox.Text, out guard) && guard >= 0 && guard <= 64)
            {
                _config.ReturnGuard = guard;
            }
        }

        private void ScheduleSave()
        {
            if (_initializing || _config == null)
            {
                return;
            }

            _saveTimer.Stop();
            _saveTimer.Start();
        }

        private void OnSaveTimerTick(object sender, EventArgs e)
        {
            _saveTimer.Stop();
            SaveConfig();
        }

        public void SaveConfig()
        {
            if (_config == null)
            {
                return;
            }

            CollectToConfig();

            string error;
            if (!_config.Save(out error))
            {
                Log.Warn(error);
                FooterStatus.Text = error;
            }
        }

        // ------------------------------------------------------------ 会话控制

        public bool IsRunning
        {
            get { return _session != null && _session.IsRunning; }
        }

        public bool StartSharing()
        {
            if (_session == null || _config == null)
            {
                return false;
            }

            if (_session.IsRunning)
            {
                return true;
            }

            CollectToConfig();
            SaveConfig();

            SessionOptions options = new SessionOptions();
            options.Role = _config.IsHost ? SessionRole.Host : SessionRole.Client;
            options.PeerHost = _config.PeerHost;
            options.Port = _config.Port;
            options.PairingKey = _config.PairingKey;
            options.AutoReconnect = _config.AutoReconnect;
            options.KeyboardEnabled = _config.KeyboardEnabled;
            options.Layout = _config.ToLayout();

            string error;
            if (!_session.Start(options, out error))
            {
                Log.Warn("启动共享失败：" + error);
                AppendLog("启动失败：" + error);
                MessageBox.Show(this, "启动共享失败：\n\n" + error, "LanMouseShare",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                RefreshPills();
                return false;
            }

            RefreshPills();
            RefreshLayoutSummary();
            return true;
        }

        public void StopSharing()
        {
            if (_session == null || !_session.IsRunning)
            {
                return;
            }

            _session.Stop();
            SaveConfig();
            RefreshPills();
        }

        public void ToggleSharing()
        {
            if (IsRunning)
            {
                StopSharing();
            }
            else
            {
                StartSharing();
            }
        }

        /// <summary>从托盘菜单调用：以指定角色启动。</summary>
        public void StartAsRole(bool host)
        {
            RoleHost.IsChecked = host;
            RoleClient.IsChecked = !host;
            if (IsRunning)
            {
                StopSharing();
            }
            StartSharing();
        }

        public void ForceReturnLocal()
        {
            if (_session != null && _session.Host != null && _session.IsRunning)
            {
                _session.Host.ForceReturnLocal("界面按钮");
                RefreshPills();
            }
            else
            {
                AppendLog("当前不是主机模式，无需强制返回。");
            }
        }

        // ------------------------------------------------------------ 界面事件

        private void OnRoleChanged(object sender, RoutedEventArgs e)
        {
            if (_initializing)
            {
                return;
            }

            RefreshRoleDependentUi();
            ScheduleSave();

            if (IsRunning)
            {
                AppendLog("角色已改变，正在按新角色重启共享……");
                StopSharing();
                StartSharing();
            }
        }

        private void RefreshRoleDependentUi()
        {
            bool isHost = RoleHost.IsChecked == true;
            PeerHostBox.IsEnabled = !isHost;
            TestMoveButton.IsEnabled = isHost;
            ReturnButton.IsEnabled = isHost;
            KeyboardStateText.Text = isHost
                ? "本机是主机，键盘共享" + (KeyboardCheck.IsChecked == true ? "已开启" : "已关闭") + "。「共享」页可以切换。"
                : "本机是副机，键盘共享开关在主机上设置，本机跟随主机。";
            StartStopHint.Text = isHost
                ? "主机用法：把光标推到靠副机那一侧的屏幕边缘，不用按任何热键就会划到副机；从副机侧划回来即回到本机。"
                : "副机用法：先确认「主机 IP」和「连接密钥」与主机一致，然后点开始共享，之后什么都不用做，等主机连过来。";
        }

        private void OnPortChanged(object sender, TextChangedEventArgs e)
        {
            ScheduleSave();
        }

        private void OnKeyChanged(object sender, TextChangedEventArgs e)
        {
            ScheduleSave();
        }

        private void OnPeerHostChanged(object sender, TextChangedEventArgs e)
        {
            ScheduleSave();
        }

        private void OnGenerateKey(object sender, RoutedEventArgs e)
        {
            byte[] buffer = new byte[6];
            using (System.Security.Cryptography.RNGCryptoServiceProvider rng =
                new System.Security.Cryptography.RNGCryptoServiceProvider())
            {
                rng.GetBytes(buffer);
            }

            string text = Convert.ToBase64String(buffer).Replace("+", "A").Replace("/", "B").Replace("=", "C");
            KeyBox.Text = "lms-" + text;
            FooterStatus.Text = "已生成新的连接密钥，请把同一串密钥填到另一台电脑上。";
            ScheduleSave();
        }

        private void OnFillLocalIp(object sender, RoutedEventArgs e)
        {
            List<NetworkInfo.AddressEntry> list = NetworkInfo.GetLocalIPv4();
            if (list.Count == 0)
            {
                FooterStatus.Text = "没有检测到局域网 IPv4 地址，请检查网线或 WiFi。";
                return;
            }

            string text = list[0].Address;
            try
            {
                Clipboard.SetText(text);
                FooterStatus.Text = "本机局域网 IP " + text +
                    " 已复制到剪贴板：请把它粘到另一台电脑的「主机 IP」里（如果这一台是主机）。";
            }
            catch (Exception)
            {
                FooterStatus.Text = "本机局域网 IP：" + text;
            }

            RefreshNetworkHint();
        }

        private void OnStartStopClick(object sender, RoutedEventArgs e)
        {
            ToggleSharing();
        }

        private void OnSendTestMove(object sender, RoutedEventArgs e)
        {
            if (_session == null || !_session.IsRunning || _session.Host == null)
            {
                FooterStatus.Text = "先点「开始共享」，并且确认副机已经连上，再做测试。";
                return;
            }

            _session.Host.SendTestMove(100, 100);
            AppendLog("已发送测试指令：副机鼠标移动到 (100,100)");
        }

        private void OnForceReturn(object sender, RoutedEventArgs e)
        {
            ForceReturnLocal();
        }

        private void OnKeyboardChanged(object sender, RoutedEventArgs e)
        {
            if (_initializing)
            {
                return;
            }

            ScheduleSave();
            RefreshRoleDependentUi();

            if (_session != null && _session.IsRunning && _session.Host != null)
            {
                _session.Host.KeyboardSharingEnabled = KeyboardCheck.IsChecked == true;
                AppendLog("键盘共享已" + (KeyboardCheck.IsChecked == true ? "开启" : "关闭") + "（无需重启会话）。");
            }
        }

        private void OnReconnectChanged(object sender, RoutedEventArgs e)
        {
            ScheduleSave();
        }

        private void OnAutoStartChanged(object sender, RoutedEventArgs e)
        {
            ScheduleSave();
        }

        private void OnUpdateOnStartChanged(object sender, RoutedEventArgs e)
        {
            ScheduleSave();
        }

        private void OnSideChanged(object sender, RoutedEventArgs e)
        {
            if (_initializing)
            {
                return;
            }

            ApplyLayoutToSession();
            RefreshLayoutSummary();
            RedrawPreview();
            ScheduleSave();
        }

        private void OnOffsetChanged(object sender, TextChangedEventArgs e)
        {
            if (_initializing)
            {
                return;
            }

            ApplyLayoutToSession();
            RefreshLayoutSummary();
            RedrawPreview();
            ScheduleSave();
        }

        private void OnGuardChanged(object sender, TextChangedEventArgs e)
        {
            if (_initializing)
            {
                return;
            }

            ApplyLayoutToSession();
            RefreshLayoutSummary();
            ScheduleSave();
        }

        /// <summary>把界面上的布局参数实时推给正在运行的会话，改完立即生效，不用重启。</summary>
        private void ApplyLayoutToSession()
        {
            if (_session == null || _session.Host == null || _config == null)
            {
                return;
            }

            CollectToConfig();
            ScreenLayout layout = _config.ToLayout();
            layout.HostWidth = VirtualScreen.PrimaryWidth;
            layout.HostHeight = VirtualScreen.PrimaryHeight;

            ScreenLayout current = _session.Host.Layout;
            if (current != null && current.RemoteWidth > 0)
            {
                layout.RemoteWidth = current.RemoteWidth;
                layout.RemoteHeight = current.RemoteHeight;
            }

            _session.Host.Layout = layout;
        }

        private void RefreshLayoutSummary()
        {
            if (_config == null)
            {
                return;
            }

            CollectToConfig();

            string side;
            if (SideLeft.IsChecked == true) side = "左侧";
            else if (SideAbove.IsChecked == true) side = "上方";
            else if (SideBelow.IsChecked == true) side = "下方";
            else side = "右侧";

            int remoteWidth = _config.LastRemoteWidth > 0 ? _config.LastRemoteWidth : 1920;
            int remoteHeight = _config.LastRemoteHeight > 0 ? _config.LastRemoteHeight : 1080;

            LayoutSummary.Text = "主机 " + VirtualScreen.PrimaryWidth + "×" + VirtualScreen.PrimaryHeight +
                "（物理像素） · 副机 " + remoteWidth + "×" + remoteHeight +
                (VirtualScreen.IsMultiMonitor ? " · 注意：主机检测到多显示器，第一版只按主显示器计算" : "") +
                "\n副机在主机" + side + "，沿边偏移 " + _config.OffsetPixels + " 像素，回程守卫 " + _config.ReturnGuard + " 像素。";
        }

        private void OnPreviewSizeChanged(object sender, SizeChangedEventArgs e)
        {
            RedrawPreview();
        }

        /// <summary>
        /// 用两个矩形把当前布局画出来。这不是装饰：
        /// 「偏移像素」调多少合适，看图比看数字快得多。
        /// </summary>
        private void RedrawPreview()
        {
            if (LayoutPreview == null || LayoutPreview.Children == null)
            {
                return;
            }

            LayoutPreview.Children.Clear();

            double canvasWidth = LayoutPreview.ActualWidth;
            double canvasHeight = LayoutPreview.ActualHeight;
            if (canvasWidth < 20 || canvasHeight < 20)
            {
                return;
            }

            int hostWidth = VirtualScreen.PrimaryWidth;
            int hostHeight = VirtualScreen.PrimaryHeight;
            if (hostWidth <= 0 || hostHeight <= 0)
            {
                return;
            }

            int remoteWidth = (_config != null && _config.LastRemoteWidth > 0) ? _config.LastRemoteWidth : 1920;
            int remoteHeight = (_config != null && _config.LastRemoteHeight > 0) ? _config.LastRemoteHeight : 1080;

            ScreenLayout layout = new ScreenLayout();
            layout.HostWidth = hostWidth;
            layout.HostHeight = hostHeight;
            layout.RemoteWidth = remoteWidth;
            layout.RemoteHeight = remoteHeight;
            layout.OffsetPixels = _config != null ? _config.OffsetPixels : 0;
            if (SideLeft.IsChecked == true) layout.Side = ScreenSide.Left;
            else if (SideAbove.IsChecked == true) layout.Side = ScreenSide.Above;
            else if (SideBelow.IsChecked == true) layout.Side = ScreenSide.Below;
            else layout.Side = ScreenSide.Right;

            int remoteOriginX = layout.RemoteOriginX;
            int remoteOriginY = layout.RemoteOriginY;

            int minX = Math.Min(0, remoteOriginX);
            int minY = Math.Min(0, remoteOriginY);
            int maxX = Math.Max(hostWidth, remoteOriginX + remoteWidth);
            int maxY = Math.Max(hostHeight, remoteOriginY + remoteHeight);

            double spanX = Math.Max(1, maxX - minX);
            double spanY = Math.Max(1, maxY - minY);
            const double padding = 18.0;
            double scale = Math.Min((canvasWidth - padding * 2) / spanX, (canvasHeight - padding * 2) / spanY);
            if (scale <= 0)
            {
                return;
            }

            double offsetX = padding + (canvasWidth - padding * 2 - spanX * scale) / 2.0;
            double offsetY = padding + (canvasHeight - padding * 2 - spanY * scale) / 2.0;

            AddScreenBox("主机", LayoutPreview,
                offsetX + (0 - minX) * scale, offsetY + (0 - minY) * scale,
                hostWidth * scale, hostHeight * scale,
                Color.FromRgb(0x2E, 0x7C, 0xF6), Color.FromRgb(0x1E, 0x63, 0xD0));

            AddScreenBox("副机", LayoutPreview,
                offsetX + (remoteOriginX - minX) * scale, offsetY + (remoteOriginY - minY) * scale,
                remoteWidth * scale, remoteHeight * scale,
                Color.FromRgb(0x12, 0xA1, 0x50), Color.FromRgb(0x0B, 0x7A, 0x3B));
        }

        private static void AddScreenBox(string label, Canvas canvas, double x, double y,
            double width, double height, Color fill, Color stroke)
        {
            Rectangle rect = new Rectangle();
            rect.Width = Math.Max(4, width);
            rect.Height = Math.Max(4, height);
            rect.Fill = new SolidColorBrush(Color.FromArgb(0x33, fill.R, fill.G, fill.B));
            rect.Stroke = new SolidColorBrush(stroke);
            rect.StrokeThickness = 2;
            rect.RadiusX = 4;
            rect.RadiusY = 4;

            Canvas.SetLeft(rect, x);
            Canvas.SetTop(rect, y);
            canvas.Children.Add(rect);

            TextBlock text = new TextBlock();
            text.Text = label;
            text.Foreground = new SolidColorBrush(stroke);
            text.FontSize = 12;
            text.FontWeight = FontWeights.SemiBold;
            Canvas.SetLeft(text, x + 8);
            Canvas.SetTop(text, y + 6);
            canvas.Children.Add(text);
        }

        // ------------------------------------------------------------ 会话状态刷新

        private void OnSessionLog(string text)
        {
            Dispatcher.BeginInvoke(new Action(delegate
            {
                AppendLog(text);
            }));
        }

        private void AppendLog(string text)
        {
            string line = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "  " + text;

            try
            {
                LogList.Items.Add(line);
                while (LogList.Items.Count > MaxLogLines)
                {
                    LogList.Items.RemoveAt(0);
                }

                if (AutoScrollCheck.IsChecked == true && LogList.Items.Count > 0)
                {
                    LogList.ScrollIntoView(LogList.Items[LogList.Items.Count - 1]);
                }

                FooterStatus.Text = text;
            }
            catch (Exception)
            {
            }

            Log.Info(text);
        }

        private void OnSessionConnected(bool connected)
        {
            Dispatcher.BeginInvoke(new Action(delegate
            {
                RefreshPills();
                AppendLog(connected ? "链路已连接并完成认证。" : "链路已断开。");
            }));
        }

        private void OnSessionLocation(CursorLocation location)
        {
            Dispatcher.BeginInvoke(new Action(delegate
            {
                RefreshPills();
            }));
        }

        private void OnRemoteScreenKnown(int width, int height)
        {
            Dispatcher.BeginInvoke(new Action(delegate
            {
                if (_config != null)
                {
                    _config.LastRemoteWidth = width;
                    _config.LastRemoteHeight = height;
                    ScheduleSave();
                }

                RefreshLayoutSummary();
                RedrawPreview();
                AppendLog("副机分辨率：" + width + "×" + height);
            }));
        }

        private void RefreshPills()
        {
            bool running = IsRunning;
            bool connected = _session != null && _session.Peer != null && _session.Peer.IsConnected;
            bool isHost = _session != null && running && _session.Role == SessionRole.Host;
            CursorLocation location = (_session != null && _session.Host != null)
                ? _session.Host.Location
                : CursorLocation.Local;

            if (!running)
            {
                SetPill(PillRole, PillRoleText, "未启动", Color.FromRgb(0x33, 0x41, 0x5A));
                StartStopButton.Content = "开始共享";
                TestMoveButton.IsEnabled = false;
                ReturnButton.IsEnabled = false;
            }
            else if (isHost)
            {
                SetPill(PillRole, PillRoleText, "主机", Color.FromRgb(0x2E, 0x7C, 0xF6));
                StartStopButton.Content = "停止共享";
                TestMoveButton.IsEnabled = true;
                ReturnButton.IsEnabled = true;
            }
            else
            {
                SetPill(PillRole, PillRoleText, "副机", Color.FromRgb(0x7C, 0x4D, 0xFF));
                StartStopButton.Content = "停止共享";
                TestMoveButton.IsEnabled = false;
                ReturnButton.IsEnabled = false;
            }

            SetPill(PillConn, PillConnText,
                !running ? "未连接" : (connected ? "已连接" : "等待连接"),
                !running ? Color.FromRgb(0x33, 0x41, 0x5A)
                         : (connected ? Color.FromRgb(0x12, 0xA1, 0x50) : Color.FromRgb(0xB7, 0x79, 0x1F)));

            SetPill(PillCursor, PillCursorText,
                location == CursorLocation.Remote ? "光标：副机" : "光标：本机",
                location == CursorLocation.Remote ? Color.FromRgb(0xB7, 0x79, 0x1F) : Color.FromRgb(0x33, 0x41, 0x5A));

            if (_session != null && _session.Host != null && running && isHost)
            {
                HeaderSubtitle.Text = "版本 " + UpdateChecker.GetCurrentVersionText() +
                    " · 已跨屏 " + _session.Host.Crossings + " 次";
            }
            else
            {
                HeaderSubtitle.Text = "版本 " + UpdateChecker.GetCurrentVersionText() +
                    " · 一套鼠标键盘，两台电脑";
            }
        }

        private static void SetPill(Border border, TextBlock text, string caption, Color color)
        {
            border.Background = new SolidColorBrush(color);
            text.Text = caption;
        }

        private void RefreshNetworkHint()
        {
            List<NetworkInfo.AddressEntry> list = NetworkInfo.GetLocalIPv4();
            if (list.Count == 0)
            {
                NetworkHint.Text = "本机局域网地址：未检测到（请确认已连接路由器或交换机）";
                return;
            }

            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.Append("本机局域网地址：");
            for (int i = 0; i < list.Count && i < 4; i++)
            {
                if (i > 0)
                {
                    sb.Append("，");
                }
                sb.Append(list[i].Address);
                if (list[i].IsVirtual)
                {
                    sb.Append("（虚拟网卡）");
                }
            }
            sb.Append("　副机请把主机的地址填进「主机 IP」。");
            NetworkHint.Text = sb.ToString();
        }

        // ------------------------------------------------------------ 日志页

        private void OnClearLog(object sender, RoutedEventArgs e)
        {
            LogList.Items.Clear();
        }

        private void OnOpenLogFile(object sender, RoutedEventArgs e)
        {
            OpenPath(Log.CurrentFile);
        }

        private void OnOpenConfigFolder(object sender, RoutedEventArgs e)
        {
            OpenPath(AppPaths.RootDirectory);
        }

        private static void OpenPath(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            try
            {
                Process.Start("explorer.exe", "\"" + path + "\"");
            }
            catch (Exception ex)
            {
                Log.Warn("打开 " + path + " 失败：" + ex.Message);
            }
        }

        // ------------------------------------------------------------ 诊断与更新

        public void RefreshDiagnostics()
        {
            List<KeyValuePair<string, string>> rows = new List<KeyValuePair<string, string>>();

            rows.Add(new KeyValuePair<string, string>("产品版本", ProcessEnvironment.GetProductVersionText()));
            rows.Add(new KeyValuePair<string, string>("操作系统", ProcessEnvironment.DescribeOperatingSystem()));
            rows.Add(new KeyValuePair<string, string>("目标框架", ProcessEnvironment.GetTargetFrameworkName()));
            rows.Add(new KeyValuePair<string, string>("运行架构", ProcessEnvironment.DescribeArchitecture()));
            rows.Add(new KeyValuePair<string, string>("管理员权限",
                ProcessEnvironment.IsElevated ? "是 —— requireAdministrator 清单生效" : "否 —— 异常：应以管理员身份运行"));
            rows.Add(new KeyValuePair<string, string>("DPI 感知", DpiAwareness.Describe()));
            rows.Add(new KeyValuePair<string, string>("主显示器", VirtualScreen.PrimaryWidth + "×" + VirtualScreen.PrimaryHeight +
                (VirtualScreen.IsMultiMonitor ? "（检测到多显示器，第一版只按主显示器计算）" : "")));
            rows.Add(new KeyValuePair<string, string>("配置目录", AppPaths.RootDirectory));
            rows.Add(new KeyValuePair<string, string>("配置文件", AppConfig.FilePath));
            rows.Add(new KeyValuePair<string, string>("日志文件", Log.CurrentFile == null ? "（不可用）" : Log.CurrentFile));
            rows.Add(new KeyValuePair<string, string>("计算机名", Environment.MachineName));
            rows.Add(new KeyValuePair<string, string>("自检结果", _selfTest == null ? "尚未运行" : _selfTest.Summary));

            DiagnosticsList.ItemsSource = rows;
        }

        private void OnRefreshClick(object sender, RoutedEventArgs e)
        {
            if (_host == null)
            {
                return;
            }

            SetSelfTestResult(_host.RunSelfTest());
            AppendLog("已重新运行环境自检：" + (_selfTest == null ? "无结果" : _selfTest.Summary));
        }

        public void CheckUpdate(bool silent)
        {
            if (_initializing && !silent)
            {
                return;
            }

            CheckUpdateButton.IsEnabled = false;
            UpdateStatusText.Text = "正在检查更新……";

            Thread thread = new Thread(delegate ()
            {
                UpdateInfo info = UpdateChecker.Check(UpdateChecker.DefaultTimeoutMs);
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    ApplyUpdateInfo(info, silent);
                }));
            });
            thread.IsBackground = true;
            thread.Name = "UpdateCheck";
            thread.Start();
        }

        private void ApplyUpdateInfo(UpdateInfo info, bool silent)
        {
            _lastUpdate = info;
            CheckUpdateButton.IsEnabled = true;
            UpdateStatusText.Text = info.Summarize();

            if (info.Succeeded && info.UpdateAvailable)
            {
                DownloadButton.IsEnabled = true;
                UpdateNotesBox.Visibility = Visibility.Visible;
                UpdateNotesBox.Text = "版本 " + info.LatestVersion +
                    (string.IsNullOrEmpty(info.PublishedAt) ? string.Empty : "（发布于 " + info.PublishedAt + "）") +
                    Environment.NewLine + Environment.NewLine + info.Notes;

                AppendLog("发现新版本：" + info.LatestVersion + "（当前 " + info.CurrentVersion + "）");

                if (_host != null)
                {
                    _host.ShowBalloon("发现新版本 " + info.LatestVersion,
                        "当前版本 " + info.CurrentVersion + "。打开「诊断与更新」页可以下载。");
                }
            }
            else if (!silent)
            {
                DownloadButton.IsEnabled = false;
                UpdateNotesBox.Visibility = Visibility.Collapsed;
                AppendLog(info.Summarize());
            }
        }

        private void OnCheckUpdateClick(object sender, RoutedEventArgs e)
        {
            CheckUpdate(false);
        }

        private void OnOpenReleases(object sender, RoutedEventArgs e)
        {
            UpdateChecker.TryOpenUrl(UpdateChecker.ReleasesPageUrl);
        }

        private void OnDownloadUpdate(object sender, RoutedEventArgs e)
        {
            string url = (_lastUpdate != null && !string.IsNullOrEmpty(_lastUpdate.DownloadUrl))
                ? _lastUpdate.DownloadUrl
                : UpdateChecker.ReleasesPageUrl;
            UpdateChecker.TryOpenUrl(url);
            AppendLog("已在浏览器中打开下载地址：" + url);
        }

        // ------------------------------------------------------------ 关闭与退出

        private void OnHideClick(object sender, RoutedEventArgs e)
        {
            Hide();
        }

        private void OnExitClick(object sender, RoutedEventArgs e)
        {
            if (_host != null)
            {
                _host.RequestExit(0);
            }
            else
            {
                Close();
            }
        }

        private void OnWindowClosing(object sender, CancelEventArgs e)
        {
            if (_exitRequested)
            {
                SaveConfig();
                return;
            }

            e.Cancel = true;
            SaveConfig();
            Hide();
            AppendLog("主窗口已隐藏到系统托盘，共享继续运行。");
        }

        /// <summary>退出前由 App 调用，确保会话停下来、钩子摘干净。</summary>
        public void ShutdownSession()
        {
            if (_session != null)
            {
                try
                {
                    _session.Log -= OnSessionLog;
                    _session.ConnectedChanged -= OnSessionConnected;
                    _session.LocationChanged -= OnSessionLocation;
                    _session.RemoteScreenKnown -= OnRemoteScreenKnown;
                    _session.Stop();
                }
                catch (Exception ex)
                {
                    Log.Warn("停止会话时出错：" + ex.Message);
                }
            }
        }
    }
}
