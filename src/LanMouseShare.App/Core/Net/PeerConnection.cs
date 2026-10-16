using System;
using System.Threading;

namespace LanMouseShare.Core.Net
{
    public enum PeerRole
    {
        /// <summary>接鼠标键盘的那台，监听端口等副机连进来。</summary>
        Host = 0,

        /// <summary>不接鼠标键盘的那台，主动连接主机。</summary>
        Client = 1
    }

    /// <summary>
    /// 一条对等连接 = 握手 + 认证 + 心跳 + 断线自动重连
    /// （第二阶段：握手认证代理 + 心跳重连代理）。
    ///
    /// 握手时序：
    ///   Client 连接成功
    ///     Host   -- hello(version, nonce) -->  Client
    ///     Client -- auth(mac)             -->  Host
    ///     Host   -- auth_result(accepted) -->  Client
    ///   认证通过后才允许透明转发 mouse_*/key 消息。
    ///
    /// 心跳：每 1 秒 ping；连续 5 秒没收到任何帧就判定断线。
    ///   注意「没收到任何帧」而不是「没收到 pong」—— 有真实输入流动时
    ///   就算 pong 丢了也不该误判断线。
    ///
    /// 重连：指数退避 1s -> 2s -> 4s -> 8s -> 15s（封顶），成功后清零。
    /// </summary>
    public sealed class PeerConnection : IDisposable
    {
        public const string ProtocolVersion = "LMS/1";

        private const int HeartbeatIntervalMs = 1000;
        private const int TimeoutMs = 5000;
        private const int HandshakeTimeoutMs = 5000;
        private const int BackoffStartMs = 1000;
        private const int BackoffMaxMs = 15000;

        private readonly object _sync = new object();
        private readonly TcpTransport _transport = new TcpTransport();

        private PeerRole _role = PeerRole.Host;
        private string _host = "127.0.0.1";
        private int _port = 24800;
        private string _key = string.Empty;

        private Timer _timer;
        private long _lastReceivedMs;
        private long _connectedAtMs;
        private long _nextReconnectAtMs;

        private bool _authenticated;
        private bool _running;
        private bool _autoReconnect;
        private bool _disposed;
        private string _pendingNonce;
        private int _backoffMs = BackoffStartMs;
        private int _reconnectAttempts;

        /// <summary>认证通过后的业务帧（mouse_* / key / screen_info / notice）。</summary>
        public event Action<Frame> FrameReceived;

        /// <summary>状态文本变化，用于界面和日志。</summary>
        public event Action<string> StatusChanged;

        /// <summary>认证通过。</summary>
        public event Action<string> Authenticated;

        /// <summary>
        /// 链路可用性变化（true=认证通过可用，false=断开/掉线）。
        /// 主机端必须订阅它：掉线时要把鼠标立刻收回本机，否则用户的
        /// 光标会被留在副机上而本机收不到任何事件，整台机器等于失控。
        /// </summary>
        public event Action<bool> ConnectedChanged;

        public bool IsAuthenticated { get { return _authenticated; } }

        public bool IsConnected { get { return _transport.IsConnected; } }

        public PeerRole Role { get { return _role; } }

        public string Key { get { return _key; } }

        /// <summary>本次连接收到的坏帧字节数（诊断用）。</summary>
        public int DroppedBytes { get { return _transport.DroppedBytes; } }

        public PeerConnection()
        {
            _transport.FrameReceived += OnTransportFrame;
            _transport.StateChanged += OnTransportState;
            _transport.Failed += OnTransportFailed;
        }

        public void Configure(PeerRole role, string host, int port, string key)
        {
            lock (_sync)
            {
                _role = role;
                _host = host == null ? "127.0.0.1" : host;
                _port = port;
                _key = key == null ? string.Empty : key;
            }
        }

        /// <summary>启动（主机开始监听 / 副机开始连接），并开启心跳定时器。</summary>
        public void Start(bool autoReconnect)
        {
            lock (_sync)
            {
                if (_disposed)
                {
                    throw new ObjectDisposedException("PeerConnection");
                }

                _autoReconnect = autoReconnect;
                _running = true;
                _reconnectAttempts = 0;
                _backoffMs = BackoffStartMs;
                _nextReconnectAtMs = 0;

                if (_timer == null)
                {
                    _timer = new Timer(OnHeartbeat, null, HeartbeatIntervalMs, HeartbeatIntervalMs);
                }

                OpenTransportLocked();
            }
        }

        public void Stop()
        {
            lock (_sync)
            {
                _running = false;
                _autoReconnect = false;
            }

            Timer t = _timer;
            if (t != null)
            {
                t.Dispose();
                _timer = null;
            }

            _transport.Close();
            RaiseStatus("已停止");
        }

        // ------------------------------------------------------------ 建立连接

        private void OpenTransportLocked()
        {
            _authenticated = false;
            _pendingNonce = null;
            _connectedAtMs = Frame.NowMs();
            _lastReceivedMs = _connectedAtMs;

            if (_role == PeerRole.Host)
            {
                _transport.StartListen(_port);
            }
            else
            {
                bool ok = _transport.Connect(_host, _port, 3000);
                if (!ok && _running)
                {
                    ScheduleReconnect();
                }
            }
        }

        // ------------------------------------------------------------ 心跳

        private void OnHeartbeat(object state)
        {
            if (_disposed || !_running)
            {
                return;
            }

            long now = Frame.NowMs();

            if (!_transport.IsConnected)
            {
                // 主机角色：监听套接字建好之后就该一直等着，AcceptLoop 自己会接下一个对端。
                // 早期版本这里每秒都会调一次 Reconnect()，而 StartListen 内部第一件事就是 Close()，
                // 结果是「每秒把 24800 端口关闭再打开」：副机恰好撞上关闭窗口就被拒，
                // 而且 WSACancelBlockingCall 会在日志里刷出一堆看起来像故障的假异常。
                if (_role == PeerRole.Host && _transport.IsListening)
                {
                    return;
                }

                if (_autoReconnect && now >= _nextReconnectAtMs)
                {
                    Reconnect();
                }

                return;
            }

            if (!_authenticated && now - _connectedAtMs > HandshakeTimeoutMs)
            {
                RaiseStatus("握手超时（" + HandshakeTimeoutMs + " ms 内未完成认证），断开重连");
                _transport.Close();
                ScheduleReconnect();
                return;
            }

            if (now - _lastReceivedMs > TimeoutMs)
            {
                RaiseStatus("心跳超时（" + TimeoutMs + " ms 未收到任何数据），断开重连");
                _transport.Close();
                ScheduleReconnect();
                return;
            }

            Frame ping = new Frame();
            ping.type = Msg.Ping;
            _transport.Send(ping);
        }

        private void ScheduleReconnect()
        {
            lock (_sync)
            {
                if (!_running || !_autoReconnect)
                {
                    return;
                }

                _authenticated = false;
                _reconnectAttempts++;
                _nextReconnectAtMs = Frame.NowMs() + _backoffMs;
                RaiseStatus("将在 " + (_backoffMs / 1000.0).ToString("0.#") + " 秒后重连（第 "
                            + _reconnectAttempts + " 次）");

                if (_backoffMs < BackoffMaxMs)
                {
                    _backoffMs = Math.Min(_backoffMs * 2, BackoffMaxMs);
                }
            }
        }

        private void Reconnect()
        {
            lock (_sync)
            {
                if (!_running || !_autoReconnect || _disposed)
                {
                    return;
                }

                RaiseStatus("正在重连……");
                OpenTransportLocked();
            }
        }

        // ------------------------------------------------------------ 帧分发

        private void OnTransportFrame(Frame frame)
        {
            _lastReceivedMs = Frame.NowMs();

            switch (frame.type)
            {
                case Msg.Hello:
                    if (_role == PeerRole.Client)
                    {
                        ReplyAuth(frame);
                    }
                    break;

                case Msg.Auth:
                    if (_role == PeerRole.Host)
                    {
                        VerifyAuth(frame);
                    }
                    break;

                case Msg.AuthResult:
                    if (_role == PeerRole.Client)
                    {
                        HandleAuthResult(frame);
                    }
                    break;

                case Msg.Ping:
                    {
                        Frame pong = new Frame();
                        pong.type = Msg.Pong;
                        _transport.Send(pong);
                    }
                    break;

                case Msg.Pong:
                    // 心跳应答只用来刷新 _lastReceivedMs，不往上抛。
                    break;

                case Msg.Bye:
                    RaiseStatus("对端主动断开");
                    _transport.Close();
                    break;

                default:
                    Dispatch(frame);
                    break;
            }
        }

        /// <summary>把已认证的业务帧交给上层；未认证时一律丢弃（安全边界）。</summary>
        private void Dispatch(Frame frame)
        {
            if (!_authenticated)
            {
                return;
            }

            Action<Frame> handler = FrameReceived;
            if (handler != null)
            {
                handler(frame);
            }
        }

        // ------------------------------------------------------------ 握手

        private void SendHello()
        {
            _pendingNonce = PairingKey.CreateNonce();

            Frame hello = new Frame();
            hello.type = Msg.Hello;
            hello.version = ProtocolVersion;
            hello.nonce = _pendingNonce;
            _transport.Send(hello);
            RaiseStatus("已发出握手挑战，等待副机应答");
        }

        private void ReplyAuth(Frame hello)
        {
            string nonce = hello.nonce;
            if (string.IsNullOrEmpty(nonce))
            {
                RaiseStatus("握手包缺少 nonce，断开");
                _transport.Close();
                return;
            }

            Frame auth = new Frame();
            auth.type = Msg.Auth;
            auth.version = ProtocolVersion;
            auth.mac = PairingKey.ComputeMac(_key, ProtocolVersion, nonce);
            _transport.Send(auth);
            RaiseStatus("已提交认证应答，等待主机确认");
        }

        private void VerifyAuth(Frame auth)
        {
            bool ok = PairingKey.Verify(_key, ProtocolVersion, _pendingNonce, auth.mac);

            Frame result = new Frame();
            result.type = Msg.AuthResult;
            result.accepted = ok;
            result.version = ProtocolVersion;
            result.note = ok ? "认证通过" : "连接密钥不匹配";

            if (!ok)
            {
                // 认证失败只回绝并断开，不做任何重试 —— 避免被当成爆破点。
                RaiseStatus("认证失败：连接密钥不匹配，已拒绝");
                _transport.Send(result);
                _authenticated = false;
                _transport.Close();
                _running = false;
                _autoReconnect = false;
                return;
            }

            _authenticated = true;
            _backoffMs = BackoffStartMs;
            _transport.Send(result);
            RaiseStatus("认证通过，链路就绪");
            RaiseAuthenticated(_transport.IsConnected ? "主机（监听方）" : string.Empty);
        }

        private void HandleAuthResult(Frame result)
        {
            if (!result.accepted)
            {
                RaiseStatus("主机拒绝：连接密钥不匹配");
                _running = false;
                _autoReconnect = false;
                _transport.Close();
                return;
            }

            _authenticated = true;
            _backoffMs = BackoffStartMs;
            RaiseStatus("认证通过，链路就绪");
            RaiseAuthenticated("副机（连接方）");
        }

        // ------------------------------------------------------------ 状态回调

        private void OnTransportState(string text)
        {
            RaiseStatus(text);

            if (text != null && text.IndexOf("已断开", StringComparison.Ordinal) >= 0)
            {
                _authenticated = false;
                RaiseConnected(false);

                // 只有副机才需要主动重连。主机角色下 AcceptLoop 已经回到 Accept 状态继续等，
                // 这里再排一次重连只会把好好的监听套接字拆掉重建，反而制造连接失败窗口。
                if (_role == PeerRole.Client)
                {
                    ScheduleReconnect();
                }
            }
            else if (text != null && text.IndexOf("对端已接入", StringComparison.Ordinal) >= 0)
            {
                // 主机侧：连上后立刻发出挑战（稍等 50ms 让对端进入读循环）。
                ThreadPool.QueueUserWorkItem(delegate(object state)
                {
                    Thread.Sleep(50);
                    if (_transport.IsConnected)
                    {
                        SendHello();
                    }
                });
            }
        }

        private void OnTransportFailed(Exception ex)
        {
            RaiseStatus("网络异常：" + ex.Message);
        }

        private void RaiseStatus(string text)
        {
            Action<string> handler = StatusChanged;
            if (handler != null)
            {
                handler(text);
            }
        }

        private void RaiseAuthenticated(string peer)
        {
            Action<string> handler = Authenticated;
            if (handler != null)
            {
                handler(peer);
            }

            RaiseConnected(true);
        }

        private void RaiseConnected(bool connected)
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

        // ------------------------------------------------------------ 发送业务帧

        public bool SendAuthenticated(Frame frame)
        {
            if (!_authenticated)
            {
                return false;
            }

            return _transport.Send(frame);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _running = false;
            _autoReconnect = false;

            Timer t = _timer;
            if (t != null)
            {
                t.Dispose();
                _timer = null;
            }

            _transport.Dispose();
        }
    }
}
