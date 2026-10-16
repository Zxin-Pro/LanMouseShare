using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using LanMouseShare.Core.Config;
using LanMouseShare.Core.Diagnostics;
using LanMouseShare.Core.Infrastructure;
using LanMouseShare.Core.Input;
using LanMouseShare.Core.Interop;
using LanMouseShare.Core.Layout;
using LanMouseShare.Core.Net;
using LanMouseShare.Core.Sharing;
using LanMouseShare.Core.Tray;
using LanMouseShare.Core.Update;

namespace LanMouseShare
{
    /// <summary>
    /// 应用入口。装配顺序是整个程序最不能改的部分：
    ///   1. 在创建任何 HWND（哪怕一个隐藏窗口）之前完成 DPI 感知三级回退，
    ///      否则 Windows 会把该进程的 DPI 感知永久锁在旧级别；
    ///   2. 建配置目录与日志——后面所有模块都可能写日志；
    ///   3. 读配置（读失败也要能起来，用默认值）；
    ///   4. 建会话对象（此时还没有网络、没有钩子，只是装配）；
    ///   5. 建主窗口并注入配置/会话；
    ///   6. 建托盘；
    ///   7. 自检，可选自动开始共享与自动检查更新；
    ///   8. 全局异常兜底。
    /// </summary>
    public partial class App : Application
    {
        private TrayIconService _tray;
        private MainWindow _mainWindow;
        private AppConfig _config;

        /// <summary>
        /// 从磁盘读出来的那份配置（命令行覆盖之前的快照）。
        /// 它的唯一用途：命令行覆盖只该作用于本次进程，绝不能被顺手写回
        /// config.json —— 1.0.1 就踩过这个坑：用 --client --peer=本机IP 联调
        /// 一次，配置就被改成「角色=副机、自动开始=真、主机地址=本机」,
        /// 之后每次开程序都对着自己反复重连、刷满日志。
        /// </summary>
        private AppConfig _baselineConfig;

        /// <summary>被命令行覆盖过的字段名，保存前要把它们还原成基线值。</summary>
        private readonly List<string> _ephemeralFields = new List<string>();
        private SharingSession _session;
        private int _selfTestSeconds;
        private int _runForSeconds;
        private int _netTestTimeoutMs;
        private bool _netTestPassed;
        private bool _updateTest;
        private bool _updateTestPassed;
        private bool _shuttingDown;
        private bool _sessionRunning;

        protected override void OnStartup(StartupEventArgs e)
        {
            // 硬性约束：DPI 感知必须在进程创建任何 HWND 之前设置完毕，
            // 否则 Windows 会忽略之后的 DPI API 调用（清单里也不能声明 dpiAware）。
            DpiAwareness.TryEnableBestAvailable();

            AppPaths.EnsureCreated();
            Log.Initialize(AppPaths.LogDirectory);

            DispatcherUnhandledException += OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;

            // 关掉主窗口只是隐藏到托盘，进程要活到用户显式退出。
            ShutdownMode = ShutdownMode.OnExplicitShutdown;

            _selfTestSeconds = ParseSelfTestSeconds(e.Args);
            _runForSeconds = ParseRunForSeconds(e.Args);
            _netTestTimeoutMs = ParseNetTestTimeout(e.Args);
            _updateTest = HasFlag(e.Args, "--updatetest");

            if (_runForSeconds > 0)
            {
                Log.Info("已启用限时运行：" + _runForSeconds.ToString() + " 秒后自动退出（--runfor）。");
            }

            Log.Info("========== LanMouseShare 启动 ==========");
            Log.Info("产品版本：" + ProcessEnvironment.GetProductVersionText());
            Log.Info("操作系统：" + ProcessEnvironment.DescribeOperatingSystem());
            Log.Info("目标框架：" + ProcessEnvironment.GetTargetFrameworkName());
            Log.Info("运行架构：" + ProcessEnvironment.DescribeArchitecture());
            Log.Info("管理员权限：" + (ProcessEnvironment.IsElevated ? "是" : "否"));
            Log.Info("DPI 感知：" + DpiAwareness.Describe());
            Log.Info("DPI 设置前初始状态：" + DpiAwareness.InitialAwareness);
            Log.Info("DPI 回退链：" + DpiAwareness.DescribeFallback());
            Log.Info("配置目录：" + AppPaths.RootDirectory);
            Log.Info("日志文件：" + Log.CurrentFile);
            if (_selfTestSeconds > 0)
            {
                Log.Info("已启用自检模式，将在 " + _selfTestSeconds.ToString() + " 秒后自动退出。");
            }

            base.OnStartup(e);

            // 配置读不出来也要能用：Load 内部逐字段容错，最坏情况退回全部默认值。
            string warning;
            _config = AppConfig.Load(out warning);
            if (!string.IsNullOrEmpty(warning))
            {
                Log.Warn("配置载入警告：" + warning);
            }

            // 先记下磁盘上的原样，再让命令行覆盖内存里的这份。
            _baselineConfig = _config.Clone();

            ApplyCommandLineOverrides(e.Args);

            Log.Info("配置：" + _config.Describe());
            Log.Info("局域网地址：" + NetworkInfo.DescribeAll());

            if (_netTestTimeoutMs > 0)
            {
                // 回环联调：不装钩子、不注入输入、不动真实鼠标，只验证整条网络链路。
                Log.Info("已启用回环联调模式（超时基准 " + _netTestTimeoutMs + " 毫秒）。");
                string report = NetworkLoopbackTest.Run(_netTestTimeoutMs);
                Log.Info("回环联调明细：" + Environment.NewLine + report);
                _netTestPassed = report.IndexOf("NETTEST RESULT: PASS", StringComparison.Ordinal) >= 0;
            }

            if (_updateTest)
            {
                // 检查更新联调：真的去 GitHub 拉一次 Release 信息，验证 TLS / User-Agent /
                // 版本比较 / 下载链接提取这一整条链路。会联网，所以只在显式传参时才跑。
                Log.Info("已启用检查更新联调模式。");
                UpdateInfo info = UpdateChecker.Check(15000);
                Log.Info("检查更新明细：" + Environment.NewLine + info.Summarize());
                _updateTestPassed = info.Succeeded;
            }

            _session = new SharingSession();
            _session.Log += OnSessionLog;
            _session.ConnectedChanged += OnSessionConnected;
            _session.LocationChanged += OnSessionLocation;

            // 不用 StartupUri：手动创建窗口，装配顺序才可控。
            _mainWindow = new MainWindow();
            MainWindow = _mainWindow;
            _mainWindow.Attach(_config, _session, this);
            _mainWindow.Show();

            _tray = new TrayIconService();
            _tray.Initialize(_mainWindow, BuildTrayCallbacks());
            Log.Info("系统托盘：" + (_tray.IsAvailable ? "已创建" : "创建失败"));
            UpdateTrayTooltip();

            _mainWindow.SetSelfTestResult(RunSelfTest());

            if (_config.AutoStart)
            {
                Log.Info("配置要求启动时自动开始共享，正在以" + AppConfig.SideToChinese(_config.Side) + "布局启动……");
                _mainWindow.StartSharing();
                _sessionRunning = _session.IsRunning;
                UpdateTrayTooltip();
            }

            if (_config.CheckUpdateOnStart)
            {
                // 延后 3 秒：先把界面画出来，别让网络请求拖慢启动。
                DispatcherTimer updateTimer = new DispatcherTimer();
                updateTimer.Interval = TimeSpan.FromSeconds(3);
                updateTimer.Tick += OnDeferredUpdateTick;
                updateTimer.Start();
            }

            if ((_netTestTimeoutMs > 0 || _updateTest) && _selfTestSeconds <= 0)
            {
                DispatcherTimer netTimer = new DispatcherTimer();
                netTimer.Interval = TimeSpan.FromSeconds(1);
                netTimer.Tick += OnNetTestExitTick;
                netTimer.Start();
            }

            if (_selfTestSeconds > 0)
            {
                DispatcherTimer timer = new DispatcherTimer();
                timer.Interval = TimeSpan.FromSeconds(_selfTestSeconds);
                timer.Tick += OnSelfTestTick;
                timer.Start();
            }

            if (_runForSeconds > 0)
            {
                // 供脚本化联调使用：到点自己退，省得留下一个提权进程占着 exe 不放，
                // 下一次编译就会撞上 MSB3021「文件正由另一进程使用」。
                DispatcherTimer runTimer = new DispatcherTimer();
                runTimer.Interval = TimeSpan.FromSeconds(_runForSeconds);
                runTimer.Tick += OnRunForTick;
                runTimer.Start();
            }
        }

        private void OnRunForTick(object sender, EventArgs e)
        {
            DispatcherTimer timer = sender as DispatcherTimer;
            if (timer != null)
            {
                timer.Stop();
            }

            Log.Info("限时运行结束，准备退出。");
            RequestExit(0);
        }

        private TrayMenuCallbacks BuildTrayCallbacks()
        {
            TrayMenuCallbacks callbacks = new TrayMenuCallbacks();
            callbacks.ShowWindow = ShowMainWindow;
            callbacks.ExitApplication = ExitFromTray;
            callbacks.StartAsHost = delegate { StartFromTray(true); };
            callbacks.StartAsClient = delegate { StartFromTray(false); };
            callbacks.StopSharing = delegate { StopFromTray(); };
            callbacks.ForceReturn = delegate { ForceReturnFromTray(); };
            callbacks.CheckUpdate = delegate { CheckUpdateFromTray(); };
            callbacks.OpenConfigFolder = OpenConfigFolder;
            callbacks.IsRunning = delegate { return _session != null && _session.IsRunning; };
            callbacks.IsHost = delegate { return _session != null && _session.Role == SessionRole.Host; };
            return callbacks;
        }

        private void OnNetTestExitTick(object sender, EventArgs e)
        {
            DispatcherTimer timer = sender as DispatcherTimer;
            if (timer != null)
            {
                timer.Stop();
            }
            bool ok = true;
            if (_netTestTimeoutMs > 0)
            {
                ok = ok && _netTestPassed;
            }
            if (_updateTest)
            {
                ok = ok && _updateTestPassed;
            }
            RequestExit(ok ? 0 : 2);
        }

        private void OnDeferredUpdateTick(object sender, EventArgs e)
        {
            DispatcherTimer timer = sender as DispatcherTimer;
            if (timer != null)
            {
                timer.Stop();
            }
            if (_mainWindow != null)
            {
                _mainWindow.CheckUpdate(true);
            }
        }

        // ------------------------------------------------------------ 会话事件

        private void OnSessionLog(string text)
        {
            Log.Info("[会话] " + text);
        }

        private void OnSessionConnected(bool connected)
        {
            UpdateTrayTooltip();
            if (_tray == null)
            {
                return;
            }

            if (connected)
            {
                _tray.ShowBalloon("已连接", _session.Role == SessionRole.Host ? "副机已接入，可以开始跨屏。" : "已连接到主机。");
            }
            else
            {
                _tray.ShowBalloon("连接断开", _session.Role == SessionRole.Host ? "副机掉线，光标已收回本机。" : "主机掉线，正在自动重连。");
            }
        }

        private void OnSessionLocation(CursorLocation location)
        {
            UpdateTrayTooltip();
        }

        private void UpdateTrayTooltip()
        {
            if (_tray == null || !_tray.IsAvailable)
            {
                return;
            }

            if (_session == null || !_session.IsRunning)
            {
                _tray.UpdateTooltip("未启动");
                return;
            }

            string role = _session.Role == SessionRole.Host ? "主机" : "副机";
            string link = _session.Peer != null && _session.Peer.IsConnected ? "已连接" : "等待连接";
            string where = string.Empty;
            if (_session.Role == SessionRole.Host && _session.Host != null)
            {
                where = _session.Host.Location == CursorLocation.Remote ? " · 光标在副机" : " · 光标在本机";
            }
            _tray.UpdateTooltip(role + " · " + link + where);
        }

        // ------------------------------------------------------------ 托盘动作

        private void StartFromTray(bool host)
        {
            if (_mainWindow == null)
            {
                return;
            }
            _mainWindow.StartAsRole(host);
            UpdateTrayTooltip();
        }

        private void StopFromTray()
        {
            if (_mainWindow == null)
            {
                return;
            }
            _mainWindow.StopSharing();
            UpdateTrayTooltip();
        }

        private void ForceReturnFromTray()
        {
            if (_mainWindow == null)
            {
                return;
            }
            _mainWindow.ForceReturnLocal();
        }

        private void CheckUpdateFromTray()
        {
            ShowMainWindow();
            if (_mainWindow != null)
            {
                _mainWindow.CheckUpdate(false);
            }
        }

        private void OpenConfigFolder()
        {
            try
            {
                Process.Start("explorer.exe", "\"" + AppPaths.RootDirectory + "\"");
            }
            catch (Exception ex)
            {
                Log.Warn("打开配置目录失败：" + ex.Message);
            }
        }

        /// <summary>主窗口调用：弹托盘气泡。</summary>
        public void ShowBalloon(string title, string text)
        {
            if (_tray != null)
            {
                _tray.ShowBalloon(title, text);
            }
        }

        // ------------------------------------------------------------ 自检

        /// <summary>
        /// 全项目自检。结果同时用于界面显示、日志记录与自动化验收
        /// （smoke-test.ps1 靠日志里的 SELFTEST RESULT 行判断成败）。
        /// </summary>
        public SelfTestRunner RunSelfTest()
        {
            SelfTestRunner runner = new SelfTestRunner();

            runner.Check("以管理员身份运行", ProcessEnvironment.IsElevated,
                ProcessEnvironment.IsElevated ? "requireAdministrator 清单已生效" : "未提权：无法安装全局键鼠钩子");

            runner.Check("DPI 感知已设置",
                DpiAwareness.HasAttempted && DpiAwareness.AppliedMode != DpiAwarenessMode.NotSet,
                DpiAwareness.Describe());

            runner.Check("目标框架为 .NET Framework 4.8",
                ProcessEnvironment.GetTargetFrameworkName().IndexOf("v4.8", StringComparison.OrdinalIgnoreCase) >= 0,
                ProcessEnvironment.GetTargetFrameworkName());

            runner.Check("配置目录可用", System.IO.Directory.Exists(AppPaths.RootDirectory), AppPaths.RootDirectory);

            runner.Check("日志文件可写", Log.CurrentFile != null,
                Log.CurrentFile == null ? "日志初始化失败" : Log.CurrentFile);

            runner.Check("主窗口已创建并可见", _mainWindow != null && _mainWindow.IsVisible,
                _mainWindow == null ? "主窗口为 null" : _mainWindow.Title);

            runner.Check("系统托盘图标已创建", _tray != null && _tray.IsAvailable,
                (_tray != null && _tray.IsAvailable) ? "双击托盘图标可唤回窗口，右键有完整菜单" : "托盘初始化失败");

            runner.Check("进程架构判定可用", true, ProcessEnvironment.DescribeArchitecture());

            runner.Check("主显示器尺寸可用",
                VirtualScreen.PrimaryWidth > 0 && VirtualScreen.PrimaryHeight > 0,
                "主显示器 " + VirtualScreen.PrimaryWidth + "×" + VirtualScreen.PrimaryHeight +
                (VirtualScreen.IsMultiMonitor ? "（本机是多显示器，第一版只按主显示器计算）" : string.Empty));

            runner.Check("JSON 配置可读写", CheckConfigRoundTrip(),
                "配置文件 " + AppConfig.FilePath);

            runner.Check("局域网地址探测可用", CheckNetworkProbe(), NetworkInfo.DescribeAll());

            runner.Check("屏幕布局原点计算正确", CheckLayoutMath(),
                "右侧=" + LayoutOriginFor(ScreenSide.Right) + "，左侧=" + LayoutOriginFor(ScreenSide.Left) +
                "，下方=" + LayoutOriginFor(ScreenSide.Below) + "，上方=" + LayoutOriginFor(ScreenSide.Above));

            runner.Check("输入注入签名与结构体尺寸可用",
                InputInjector.InjectionSignature != 0 && InputInjector.InputStructSize > 0,
                "INPUT 结构体 " + InputInjector.InputStructSize + " 字节（" +
                (InputInjector.InputStructSize == 40 ? "64 位进程" : "32 位进程") + "），注入签名 0x" +
                InputInjector.InjectionSignature.ToString("X"));

            runner.Check("光标隐藏与恢复实测可用", CheckCursorHiding(),
                "隐藏后显示计数 " + _cursorCountHidden + "（文档语义：< 0 即不画）"
                + "，还原后回到 " + _cursorCountRestored + "（与隐藏前一致，没有漏还）");

            runner.Check("边缘落脚点带防抖内缩", CheckEntryGuard(),
                "EntryGuard = " + ScreenLayout.EntryGuard + " 像素，ReturnGuard = "
                + ScreenLayout.DefaultReturnGuard + " 像素");

            return runner;
        }

        private static int _cursorCountHidden;
        private static int _cursorCountRestored;

        /// <summary>
        /// 实测「把本机箭头藏起来」这件事真的做到。
        ///
        /// 为什么非要有这条断言：1.0.2 的核心改动就是主机控制副机期间隐藏箭头，
        /// 而 ShowCursor 是引用计数语义 ——「我调用过了」和「真的藏起来了」是两件事。
        ///
        /// 判据取 ShowCursor 的文档定义：显示计数小于 0 时系统不画光标。
        /// **刻意不用 GetCursorInfo**：提权实测证明它不反映这个计数（计数已经到 -4，
        /// 它照样报 CURSOR_SHOWING；连 SetSystemCursor 换成全透明光标它也不变），
        /// 拿它当判据只会得到一个永远失败的假警报。
        /// DisplayCount 本身是非破坏性的（一升一降，净变化 0）。
        /// </summary>
        private static bool CheckCursorHiding()
        {
            try
            {
                // 先收掉上一轮异常可能留下的脏状态，避免误判
                if (InputInjector.HiddenCursorCalls > 0)
                {
                    InputInjector.ShowCursor();
                }

                int before = InputInjector.DisplayCount;

                InputInjector.HideCursor();
                _cursorCountHidden = InputInjector.DisplayCount;

                InputInjector.ShowCursor();
                _cursorCountRestored = InputInjector.DisplayCount;

                // 两条都要成立：藏的时候计数确实压到负，还的时候确实回到原值。
                // 第二条防的是「藏了却没还干净」，那会让用户的箭头永远回不来。
                return _cursorCountHidden < 0 && _cursorCountRestored == before;
            }
            catch (Exception)
            {
                try
                {
                    // 断言失败也必须把箭头还回去，绝不能把用户的鼠标弄丢
                    InputInjector.ShowCursor();
                }
                catch (Exception)
                {
                }

                return false;
            }
        }

        /// <summary>
        /// 验证「划过去」这条路上最容易出错的三个数字。
        /// 1.0.1 的实测反馈是「划过去以后副机鼠标没法用」：根因就是旧代码
        /// 把落脚点放在副机边缘 x=0，而回程判定只要往回 4 像素就成立，
        /// 手腕一抖就弹回主机。EntryGuard 就是为这个加的。
        /// </summary>
        private static bool CheckEntryGuard()
        {
            ScreenLayout layout = new ScreenLayout();
            layout.HostWidth = 1920;
            layout.HostHeight = 1080;
            layout.RemoteWidth = 1920;
            layout.RemoteHeight = 1080;
            layout.Side = ScreenSide.Right;

            int remoteX;
            int remoteY;
            // 贴着主机右边缘，再往右走 5 像素 —— 这就是「划过边缘」那一瞬间
            if (!layout.TryExitHost(1919, 600, 5, 0, out remoteX, out remoteY))
            {
                return false;
            }

            // 落脚点必须离副机左边缘有一整段 EntryGuard，回程得真走完它
            if (remoteX != ScreenLayout.EntryGuard)
            {
                return false;
            }

            // 刚进去就判「划回来了」的话，副机上根本没法用
            int hostX;
            int hostY;
            if (layout.TryExitRemote(remoteX, remoteY, out hostX, out hostY))
            {
                return false;
            }

            // 反过来，真的走回副机边缘时又必须放行，否则就回不来了
            if (!layout.TryExitRemote(-5, remoteY, out hostX, out hostY))
            {
                return false;
            }

            // 回程落脚点要停在距主机边缘 ReturnGuard 处，防止立刻又穿过去
            if (hostX != 1919 - ScreenLayout.DefaultReturnGuard)
            {
                return false;
            }

            // 贴合边判定必须留 EdgeTolerance 的容差：绝对坐标注入、某些鼠标驱动、
            // 远程桌面都会把光标停在离边缘 1~2 像素处，要求精确压在第 1919 个
            // 像素上的结果是「用户怎么划都过不去」。
            if (!layout.TryExitHost(1920 - 1 - ScreenLayout.EdgeTolerance, 600, 5, 0, out remoteX, out remoteY))
            {
                return false;
            }

            // 但容差不能无限放宽：离边缘再远一格就必须判为「没过」
            if (layout.TryExitHost(1920 - 2 - ScreenLayout.EdgeTolerance, 600, 5, 0, out remoteX, out remoteY))
            {
                return false;
            }

            // 方向也必须对：在边缘上但往屏幕里走，绝不能穿越
            return !layout.TryExitHost(1919, 600, -5, 0, out remoteX, out remoteY);
        }

        private bool CheckConfigRoundTrip()
        {
            try
            {
                string error;
                if (_config == null)
                {
                    return false;
                }
                if (!_config.Save(out error))
                {
                    Log.Warn("自检写配置失败：" + error);
                    return false;
                }
                string warning;
                AppConfig reloaded = AppConfig.Load(out warning);
                return reloaded != null && reloaded.Port == _config.Port && reloaded.IsHost == _config.IsHost;
            }
            catch (Exception ex)
            {
                Log.Warn("自检配置往返失败：" + ex.Message);
                return false;
            }
        }

        private static bool CheckNetworkProbe()
        {
            try
            {
                List<NetworkInfo.AddressEntry> list = NetworkInfo.GetLocalIPv4();
                return list != null;
            }
            catch (Exception ex)
            {
                Log.Warn("自检探测局域网地址失败：" + ex.Message);
                return false;
            }
        }

        private static int LayoutOriginFor(ScreenSide side)
        {
            ScreenLayout probe = new ScreenLayout();
            probe.HostWidth = 1920;
            probe.HostHeight = 1080;
            probe.RemoteWidth = 1600;
            probe.RemoteHeight = 900;
            probe.OffsetPixels = 0;
            probe.Side = side;
            return probe.Side == ScreenSide.Above || probe.Side == ScreenSide.Below
                ? probe.RemoteOriginY
                : probe.RemoteOriginX;
        }

        private static bool CheckLayoutMath()
        {
            return LayoutOriginFor(ScreenSide.Right) == 1920
                && LayoutOriginFor(ScreenSide.Left) == -1600
                && LayoutOriginFor(ScreenSide.Below) == 1080
                && LayoutOriginFor(ScreenSide.Above) == -900;
        }

        // ------------------------------------------------------------ 退出

        /// <summary>从托盘菜单或界面按钮退出程序。</summary>
        public void ExitFromTray()
        {
            RequestExit(0);
        }

        public void RequestExit(int exitCode)
        {
            if (_shuttingDown)
            {
                return;
            }
            _shuttingDown = true;

            Log.Info("应用退出，退出码 " + exitCode.ToString());

            // 顺序不能反：先让窗口把配置写下去，再停会话（停会话会补发抬键），最后收托盘图标。
            try
            {
                if (_mainWindow != null)
                {
                    _mainWindow.PrepareForExit();
                    _mainWindow.ShutdownSession();
                }
            }
            catch (Exception ex)
            {
                Log.Warn("退出时清理会话失败：" + ex.Message);
            }

            try
            {
                if (_session != null)
                {
                    _session.Log -= OnSessionLog;
                    _session.ConnectedChanged -= OnSessionConnected;
                    _session.LocationChanged -= OnSessionLocation;
                    _session.Dispose();
                    _session = null;
                }
            }
            catch (Exception ex)
            {
                Log.Warn("退出时释放会话失败：" + ex.Message);
            }

            if (_tray != null)
            {
                _tray.Dispose();
                _tray = null;
            }

            Log.Info("========== LanMouseShare 退出 ==========");
            Shutdown(exitCode);
        }

        /// <summary>把主窗口从托盘唤回到前台。</summary>
        public void ShowMainWindow()
        {
            if (_mainWindow == null)
            {
                return;
            }
            if (!_mainWindow.IsVisible)
            {
                _mainWindow.Show();
            }
            if (_mainWindow.WindowState == WindowState.Minimized)
            {
                _mainWindow.WindowState = WindowState.Normal;
            }
            // Topmost 闪一下，保证从托盘唤回的窗口一定跑到前台。
            _mainWindow.Topmost = true;
            _mainWindow.Topmost = false;
            _mainWindow.Activate();
            _mainWindow.Focus();
        }

        // ------------------------------------------------------------ 自检模式

        private void OnSelfTestTick(object sender, EventArgs e)
        {
            DispatcherTimer timer = sender as DispatcherTimer;
            if (timer != null)
            {
                timer.Stop();
            }

            SelfTestRunner runner = RunSelfTest();
            Log.Info("自检明细：" + Environment.NewLine + runner.Details);
            Log.Info("SELFTEST RESULT: " + (runner.AllPassed ? "PASS" : "FAIL") + " (" +
                     runner.Passed.ToString() + "/" + runner.Total.ToString() + ")" +
                     (runner.AllPassed ? string.Empty : " 未通过项：" + runner.FailedNames));

            if (_mainWindow != null)
            {
                _mainWindow.SetSelfTestResult(runner);
            }
            RequestExit(runner.AllPassed ? 0 : 2);
        }

        /// <summary>解析 --nettest[=毫秒]，默认 5000 毫秒。</summary>
        /// <summary>判断命令行里有没有某个纯开关（形如 --xxx）。</summary>
        private static bool HasFlag(string[] args, string flag)
        {
            if (args == null)
            {
                return false;
            }
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] != null && string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 命令行覆盖配置。
        ///
        /// 存在的意义：两台电脑之间来回调参数（换角色、换 IP、换端口、换布局）时
        /// 不用反复去改 %LOCALAPPDATA% 下的 config.json。这些覆盖只作用于本次进程，
        /// 界面上不点保存就不会写回文件。
        ///
        ///   --host / --client        指定角色，并且要求启动后立刻开始共享
        ///   --peer=192.168.1.2       副机填的主机地址
        ///   --port=24800             端口
        ///   --key=lms-24800          连接密钥
        ///   --side=right|left|above|below  副机相对主机的方位
        /// </summary>
        private void ApplyCommandLineOverrides(string[] args)
        {
            if (HasFlag(args, "--host"))
            {
                _config.Role = "host";
                _config.AutoStart = true;
                MarkEphemeral("Role");
                MarkEphemeral("AutoStart");
                Log.Info("命令行指定角色：主机（--host），本次启动会自动开始共享。");
            }
            else if (HasFlag(args, "--client"))
            {
                _config.Role = "client";
                _config.AutoStart = true;
                MarkEphemeral("Role");
                MarkEphemeral("AutoStart");
                Log.Info("命令行指定角色：副机（--client），本次启动会自动开始共享。");
            }

            string peer = GetArgValue(args, "--peer");
            if (peer != null)
            {
                _config.PeerHost = peer;
                MarkEphemeral("PeerHost");
                Log.Info("命令行指定主机地址：" + peer + "（只用于本次启动，不会写进配置文件）");
            }

            string key = GetArgValue(args, "--key");
            if (key != null)
            {
                _config.PairingKey = key;
                MarkEphemeral("PairingKey");
                Log.Info("命令行指定连接密钥（只用于本次启动，不会写进配置文件）。");
            }

            string portText = GetArgValue(args, "--port");
            if (portText != null)
            {
                int port;
                if (int.TryParse(portText, out port) && port >= AppConfig.MinPort && port <= AppConfig.MaxPort)
                {
                    _config.Port = port;
                    MarkEphemeral("Port");
                    Log.Info("命令行指定端口：" + port.ToString() + "（只用于本次启动）");
                }
                else
                {
                    Log.Warn("命令行端口参数无效，已忽略：" + portText);
                }
            }

            string side = GetArgValue(args, "--side");
            if (side != null)
            {
                _config.Side = AppConfig.ParseSide(side);
                MarkEphemeral("Side");
                Log.Info("命令行指定屏幕方位：" + AppConfig.SideToChinese(_config.Side) + "（只用于本次启动）");
            }
        }

        private void MarkEphemeral(string fieldName)
        {
            if (fieldName != null && !_ephemeralFields.Contains(fieldName))
            {
                _ephemeralFields.Add(fieldName);
            }
        }

        /// <summary>
        /// 用户在界面上真的动了设置控件：从这一刻起命令行覆盖不再特殊，
        /// 界面上的值就是用户想要的，照常写回文件。
        /// </summary>
        public void ClearEphemeralOverrides()
        {
            if (_ephemeralFields.Count > 0)
            {
                _ephemeralFields.Clear();
            }
        }

        /// <summary>
        /// 拿到一份「可以安全写进磁盘」的配置副本。
        /// 被命令行覆盖过的字段会还原成磁盘上的原值，其余的照抄现场值。
        /// 刻意返回副本而不是改 live 那份：调用它的时机是「马上就要写文件」，
        /// 不能顺手把进程正在用的配置也改掉。
        /// </summary>
        public AppConfig ConfigForSave(AppConfig live)
        {
            if (live == null || _baselineConfig == null || _ephemeralFields.Count == 0)
            {
                return live;
            }

            AppConfig copy = live.Clone();
            for (int i = 0; i < _ephemeralFields.Count; i++)
            {
                switch (_ephemeralFields[i])
                {
                    case "Role":
                        copy.Role = _baselineConfig.Role;
                        break;
                    case "AutoStart":
                        copy.AutoStart = _baselineConfig.AutoStart;
                        break;
                    case "PeerHost":
                        copy.PeerHost = _baselineConfig.PeerHost;
                        break;
                    case "PairingKey":
                        copy.PairingKey = _baselineConfig.PairingKey;
                        break;
                    case "Port":
                        copy.Port = _baselineConfig.Port;
                        break;
                    case "Side":
                        copy.Side = _baselineConfig.Side;
                        break;
                }
            }

            return copy;
        }

        /// <summary>取 --name=value 形式的参数值；没写或者值空则返回 null。</summary>
        private static string GetArgValue(string[] args, string name)
        {
            if (args == null)
            {
                return null;
            }

            string prefix = name + "=";
            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                if (arg == null || !arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string value = arg.Substring(prefix.Length).Trim();
                return value.Length == 0 ? null : value;
            }

            return null;
        }

        /// <summary>解析 --runfor=秒，到时自动退出。0 表示不限时。</summary>
        private static int ParseRunForSeconds(string[] args)
        {
            string raw = GetArgValue(args, "--runfor");
            if (raw == null)
            {
                return 0;
            }

            int seconds;
            if (int.TryParse(raw, out seconds) && seconds >= 3 && seconds <= 86400)
            {
                return seconds;
            }

            return 0;
        }

        private static int ParseNetTestTimeout(string[] args)
        {
            if (args == null)
            {
                return 0;
            }
            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                if (arg == null)
                {
                    continue;
                }
                if (arg.StartsWith("--nettest=", StringComparison.OrdinalIgnoreCase) ||
                    arg.StartsWith("-nettest=", StringComparison.OrdinalIgnoreCase))
                {
                    int ms;
                    string raw = arg.Substring(arg.IndexOf('=') + 1);
                    if (int.TryParse(raw, out ms) && ms >= 500 && ms <= 60000)
                    {
                        return ms;
                    }
                }
                else if (string.Equals(arg, "--nettest", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(arg, "-nettest", StringComparison.OrdinalIgnoreCase))
                {
                    return 5000;
                }
            }
            return 0;
        }

        private static int ParseSelfTestSeconds(string[] args)
        {
            if (args == null)
            {
                return 0;
            }
            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                if (arg == null)
                {
                    continue;
                }
                if (arg.StartsWith("--selftest=", StringComparison.OrdinalIgnoreCase) ||
                    arg.StartsWith("-selftest=", StringComparison.OrdinalIgnoreCase))
                {
                    int seconds;
                    string raw = arg.Substring(arg.IndexOf('=') + 1);
                    if (int.TryParse(raw, out seconds) && seconds > 0 && seconds < 600)
                    {
                        return seconds;
                    }
                }
                else if (string.Equals(arg, "--selftest", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(arg, "-selftest", StringComparison.OrdinalIgnoreCase))
                {
                    return 5;
                }
            }
            return 0;
        }

        // ------------------------------------------------------------ 全局异常兜底

        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            Log.Error("界面线程未处理异常", e.Exception);
            SafeMessageBox("界面线程发生未处理的异常：" + Environment.NewLine + Environment.NewLine +
                           e.Exception.Message + Environment.NewLine + Environment.NewLine +
                           "程序将继续运行，详细信息见日志：" + Environment.NewLine + Log.CurrentFile);
            e.Handled = true;
        }

        private void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            Exception ex = e.ExceptionObject as Exception;
            if (ex != null)
            {
                Log.Error("后台线程未处理异常", ex);
            }
            else
            {
                Log.Error("后台线程未处理异常（非 Exception 对象）", null);
            }
        }

        private static void SafeMessageBox(string text)
        {
            try
            {
                MessageBox.Show(text, "LanMouseShare", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch (Exception)
            {
                // 连弹窗都失败时只能忽略，避免异常处理器自身再次抛出。
            }
        }
    }
}
