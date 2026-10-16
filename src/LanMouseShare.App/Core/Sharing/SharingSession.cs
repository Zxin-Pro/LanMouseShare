using System;
using LanMouseShare.Core.Input;
using LanMouseShare.Core.Layout;
using LanMouseShare.Core.Net;

namespace LanMouseShare.Core.Sharing
{
    /// <summary>一次共享会话的角色。</summary>
    public enum SessionRole
    {
        Host = 0,
        Client = 1
    }

    /// <summary>会话启动参数。</summary>
    public sealed class SessionOptions
    {
        public SessionRole Role { get; set; }

        /// <summary>主机填监听端口；副机填主机 IP。</summary>
        public string PeerHost { get; set; }

        public int Port { get; set; }

        public string PairingKey { get; set; }

        public bool AutoReconnect { get; set; }

        public bool KeyboardEnabled { get; set; }

        public ScreenLayout Layout { get; set; }

        public SessionOptions()
        {
            Port = 24800;
            AutoReconnect = true;
            KeyboardEnabled = true;
            Layout = new ScreenLayout();
        }
    }

    /// <summary>
    /// 会话编排（第七阶段：异常处理代理在此兜底）。
    /// 负责把 PeerConnection / FramePump / HostController / ClientController
    /// 按角色装配起来，并把所有事件汇总成一条日志流给界面用。
    /// 界面只跟这个类打交道，不直接碰网络和钩子。
    /// </summary>
    public sealed class SharingSession : IDisposable
    {
        private readonly LowLevelHooks _hooks = new LowLevelHooks();
        private PeerConnection _peer;
        private FramePump _pump;
        private HostController _host;
        private ClientController _client;
        private bool _running;

        public bool IsRunning
        {
            get { return _running; }
        }

        public SessionRole Role { get; private set; }

        /// <summary>
        /// 由宿主层（WPF 主窗口）注入的线程调度器，用来把核心层的动作送回 UI 线程。
        /// 核心层是纯 .NET 类库式的写法，不引用 WPF，所以只能这样把 Dispatcher 传进来。
        /// </summary>
        public Action<Action> UiDispatch { get; set; }

        /// <summary>
        /// 由宿主层（WPF 主窗口）注入的「主机光标管家」开关：
        /// 参数 true = 盖罩窗并藏箭头，false = 还计数并撤罩窗。
        /// 核心层不引用 WPF，建不了罩窗，所以只能由界面层实现后传进来。
        /// 完全不注入也能跑，只是那种情况下主机进副机模式时箭头藏不住。
        /// </summary>
        public Action<bool> HostCursorControl { get; set; }

        public HostController Host
        {
            get { return _host; }
        }

        public ClientController Client
        {
            get { return _client; }
        }

        public PeerConnection Peer
        {
            get { return _peer; }
        }

        public FramePump Pump
        {
            get { return _pump; }
        }

        public LowLevelHooks Hooks
        {
            get { return _hooks; }
        }

        public event Action<string> Log;
        public event Action<CursorLocation> LocationChanged;
        public event Action<bool> ConnectedChanged;

        public bool Start(SessionOptions options, out string error)
        {
            error = null;

            if (options == null)
            {
                error = "启动参数为空";
                return false;
            }

            if (_running)
            {
                Stop();
            }

            try
            {
                _peer = new PeerConnection();
                _peer.Configure(
                    options.Role == SessionRole.Host ? PeerRole.Host : PeerRole.Client,
                    options.PeerHost,
                    options.Port,
                    options.PairingKey);

                _peer.StatusChanged += OnPeerStatus;
                _peer.ConnectedChanged += OnPeerConnected;
                _peer.Authenticated += OnPeerAuthenticated;

                _pump = new FramePump(_peer);
                _pump.Start();

                Role = options.Role;

                if (options.Role == SessionRole.Host)
                {
                    _host = new HostController(_peer, _pump, _hooks);
                    // 宿主层（WPF）如果注入了线程调度器，就转交给主机控制器。
                    // 需要它的原因见 HostController.OnHookThread：ShowCursor 的
                    // 显示计数是按线程记的，断线兜底必须回到装钩子那条线程上还。
                    _host.UiDispatch = UiDispatch;
                    _host.HostCursorControl = HostCursorControl;
                    _host.Layout = options.Layout ?? new ScreenLayout();
                    _host.Layout.HostWidth = VirtualScreen.PrimaryWidth;
                    _host.Layout.HostHeight = VirtualScreen.PrimaryHeight;
                    _host.KeyboardSharingEnabled = options.KeyboardEnabled;
                    _host.Log += RaiseLog;
                    _host.LocationChanged += OnHostLocationChanged;

                    // 主机：先装钩子。钩子失败就没必要开网络了。
                    if (!_host.Start(out error))
                    {
                        CleanUp();
                        return false;
                    }

                    _peer.FrameReceived += OnHostFrame;
                }
                else
                {
                    _client = new ClientController(_peer);
                    _client.Log += RaiseLog;
                    _client.NoticeReceived += RaiseLog;
                    if (!_client.Start(out error))
                    {
                        CleanUp();
                        return false;
                    }
                }

                _peer.Start(options.AutoReconnect);
                _running = true;

                RaiseLog(options.Role == SessionRole.Host
                    ? "已作为【主机】启动，监听端口 " + options.Port
                    : "已作为【副机】启动，正在连接 " + options.PeerHost + ":" + options.Port);

                return true;
            }
            catch (Exception ex)
            {
                error = "启动失败：" + ex.Message;
                CleanUp();
                return false;
            }
        }

        public void Stop()
        {
            CleanUp();
            RaiseLog("会话已停止");
        }

        private void CleanUp()
        {
            _running = false;

            try
            {
                if (_host != null)
                {
                    _peer.FrameReceived -= OnHostFrame;
                    _host.Log -= RaiseLog;
                    _host.LocationChanged -= OnHostLocationChanged;
                    _host.Dispose();
                    _host = null;
                }

                if (_client != null)
                {
                    _client.Log -= RaiseLog;
                    _client.NoticeReceived -= RaiseLog;
                    _client.Dispose();
                    _client = null;
                }

                if (_pump != null)
                {
                    _pump.Dispose();
                    _pump = null;
                }

                if (_peer != null)
                {
                    _peer.StatusChanged -= OnPeerStatus;
                    _peer.ConnectedChanged -= OnPeerConnected;
                    _peer.Authenticated -= OnPeerAuthenticated;
                    _peer.Stop();
                    _peer.Dispose();
                    _peer = null;
                }

                // 钩子留在 LowLevelHooks 里统一管理：主机停用后必须确认全摘掉
                _hooks.UninstallAll();
            }
            catch (Exception ex)
            {
                RaiseLog("停止会话时出错：" + ex.Message);
            }
        }

        public void Dispose()
        {
            CleanUp();
            _hooks.Dispose();
        }

        /// <summary>主机测到副机分辨率后回调（界面更新用）。</summary>
        public event Action<int, int> RemoteScreenKnown;

        private void OnHostFrame(Frame frame)
        {
            if (frame == null)
            {
                return;
            }

            if (frame.type == Msg.ScreenInfo)
            {
                if (_host != null)
                {
                    _host.UpdateRemoteScreen(frame.screenWidth, frame.screenHeight);
                }

                Action<int, int> handler = RemoteScreenKnown;
                if (handler != null)
                {
                    try
                    {
                        handler(frame.screenWidth, frame.screenHeight);
                    }
                    catch
                    {
                    }
                }
            }
        }

        private void OnHostLocationChanged(CursorLocation location)
        {
            Action<CursorLocation> handler = LocationChanged;
            if (handler != null)
            {
                try
                {
                    handler(location);
                }
                catch
                {
                }
            }
        }

        private void OnPeerConnected(bool connected)
        {
            Action<bool> handler = ConnectedChanged;
            if (handler != null)
            {
                try
                {
                    handler(connected);
                }
                catch
                {
                }
            }
        }

        private void OnPeerAuthenticated(string peer)
        {
            if (Role == SessionRole.Host)
            {
                // 主机也把自己的分辨率报过去，副机侧日志里能看到，便于排查布局问题
                Frame frame = new Frame();
                frame.type = Msg.ScreenInfo;
                frame.screenWidth = VirtualScreen.PrimaryWidth;
                frame.screenHeight = VirtualScreen.PrimaryHeight;
                _peer.SendAuthenticated(frame);
                RaiseLog("认证通过（" + peer + "）：已把本机分辨率 " + frame.screenWidth + "x" + frame.screenHeight + " 发给副机");
            }
        }

        private void OnPeerStatus(string text)
        {
            RaiseLog(text);
        }

        private void RaiseLog(string text)
        {
            Action<string> handler = Log;
            if (handler != null && !string.IsNullOrEmpty(text))
            {
                try
                {
                    handler(text);
                }
                catch
                {
                }
            }
        }
    }
}
