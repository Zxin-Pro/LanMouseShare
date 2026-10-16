using System;
using System.Collections.Generic;
using LanMouseShare.Core.Input;
using LanMouseShare.Core.Interop;
using LanMouseShare.Core.Layout;
using LanMouseShare.Core.Net;

namespace LanMouseShare.Core.Sharing
{
    /// <summary>
    /// 主机端（接鼠标键盘的那台）的总调度：第四、五阶段的合流点。
    ///
    /// 状态机只有两个状态，切换条件非常明确：
    ///   Local  --(光标顶到贴合边且位移朝外)-->  Remote
    ///   Remote --(虚拟副机坐标越过边缘 ReturnGuard 像素 / Ctrl+Alt+Home / 断线)--> Local
    ///
    /// 核心取舍（这里踩过坑，后来者不要改回去）：
    ///   Remote 模式下**只吞按键和滚轮，不吞鼠标移动**。
    ///   因为低级钩子只能靠「前后两次坐标相减」算位移，一旦把移动也吞掉、
    ///   光标被系统钉在屏幕边缘，位移就永远是 0，表现是「鼠标划到副机
    ///   边缘就卡住不动」。所以移动一律放行，同时用 SetCursorPos 把光标
    ///   在接近边缘时搬回屏幕中心，保证位移永远有得读。用户此刻正在看
    ///   另一块屏幕，这个搬动看不见。
    ///
    /// 钩子回调里只做内存判断和入队（微秒级）。任何耗时操作都会让
    /// 回调超过 300ms 预算而被系统静默摘钩，那是本项目最难查的一类故障。
    /// </summary>
    public sealed class HostController : IDisposable
    {
        private const int VkControl = 0x11;
        private const int VkMenu = 0x12;
        private const int VkHome = 0x24;
        private const int VkLControl = 0xA2;
        private const int VkRControl = 0xA3;
        private const int VkLMenu = 0xA4;
        private const int VkRMenu = 0xA5;

        private readonly PeerConnection _peer;
        private readonly FramePump _pump;
        private readonly LowLevelHooks _hooks;

        private readonly object _sync = new object();
        private readonly HashSet<int> _pressedKeys = new HashSet<int>();

        private ScreenLayout _layout;
        private CursorLocation _location = CursorLocation.Local;
        private int _virtualX;
        private int _virtualY;
        private int _lastHostX = int.MinValue;
        private int _lastHostY = int.MinValue;
        private bool _active;
        private bool _keyboardEnabled = true;
        private bool _ctrlDown;
        private bool _altDown;
        private long _crossings;

        public HostController(PeerConnection peer, FramePump pump, LowLevelHooks hooks)
        {
            if (peer == null) throw new ArgumentNullException("peer");
            if (pump == null) throw new ArgumentNullException("pump");
            if (hooks == null) throw new ArgumentNullException("hooks");

            _peer = peer;
            _pump = pump;
            _hooks = hooks;
            _layout = new ScreenLayout();
        }

        /// <summary>屏幕布局（第四阶段：屏幕布局代理的输出被这里消费）。</summary>
        public ScreenLayout Layout
        {
            get { return _layout; }
            set
            {
                if (value != null)
                {
                    _layout = value;
                }
            }
        }

        /// <summary>是否共享键盘（第五阶段开关）。关掉时按键不再被吞，直接留在本机。</summary>
        public bool KeyboardSharingEnabled
        {
            get { return _keyboardEnabled; }
            set { _keyboardEnabled = value; }
        }

        public bool Active
        {
            get { return _active; }
        }

        public CursorLocation Location
        {
            get { return _location; }
        }

        /// <summary>当前虚拟副机坐标（未夹紧），诊断用。</summary>
        public int VirtualRemoteX
        {
            get { return _virtualX; }
        }

        public int VirtualRemoteY
        {
            get { return _virtualY; }
        }

        public long Crossings
        {
            get { return _crossings; }
        }

        public event Action<CursorLocation> LocationChanged;
        public event Action<string> Log;

        // ------------------------------------------------------------ 生命周期

        public bool Start(out string error)
        {
            error = null;
            if (_active)
            {
                return true;
            }

            if (!_hooks.InstallMouseHook(out error))
            {
                return false;
            }

            string keyError;
            if (!_hooks.InstallKeyboardHook(out keyError))
            {
                // 键盘钩子失败不致命：鼠标共享仍然可用，如实报告即可
                LogMessage("键盘钩子安装失败，键盘共享不可用：" + keyError);
            }

            _hooks.MouseEvent += OnMouseEvent;
            _hooks.KeyboardEvent += OnKeyboardEvent;
            _peer.ConnectedChanged += OnPeerConnectedChanged;

            _active = true;
            _location = CursorLocation.Local;
            _lastHostX = int.MinValue;
            _lastHostY = int.MinValue;
            LogMessage("主机模式已启动：" + _layout.Describe());
            return true;
        }

        public void Stop()
        {
            if (!_active)
            {
                return;
            }

            _active = false;
            _hooks.MouseEvent -= OnMouseEvent;
            _hooks.KeyboardEvent -= OnKeyboardEvent;
            _peer.ConnectedChanged -= OnPeerConnectedChanged;

            // 停机前一定要把「留在副机上的按下状态」松开，
            // 否则副机上会出现「一直按住 Ctrl」这种粘键，只能重启副机程序。
            ReleaseRemoteHold();

            _hooks.UninstallAll();
            LogMessage("主机模式已停止");
        }

        public void Dispose()
        {
            Stop();
        }

        // ------------------------------------------------------------ 副机信息

        /// <summary>副机分辨率到达（来自 screen_info 帧）。</summary>
        public void UpdateRemoteScreen(int width, int height)
        {
            if (width <= 0 || height <= 0)
            {
                return;
            }

            bool first = !_layout.RemoteKnown;
            _layout.RemoteWidth = width;
            _layout.RemoteHeight = height;

            if (first)
            {
                LogMessage("已知副机分辨率 " + width + "x" + height + "，边缘穿越已启用；" + _layout.Describe());
            }
        }

        // ------------------------------------------------------------ 鼠标

        private void OnMouseEvent(object sender, MouseHookEventArgs e)
        {
            if (!_active || e.Injected)
            {
                return;
            }

            if (_location == CursorLocation.Local)
            {
                HandleLocalMouse(e);
            }
            else
            {
                HandleRemoteMouse(e);
            }
        }

        private void HandleLocalMouse(MouseHookEventArgs e)
        {
            if (!e.IsMove)
            {
                // 本机模式：按键和滚轮完全放行，不做任何干预
                UpdateAnchor(e);
                return;
            }

            int dx;
            int dy;
            ComputeDelta(e, out dx, out dy);

            if (!_layout.RemoteKnown || (dx == 0 && dy == 0))
            {
                return;
            }

            int remoteX;
            int remoteY;
            if (_layout.TryExitHost(e.X, e.Y, dx, dy, out remoteX, out remoteY))
            {
                EnterRemote(remoteX, remoteY, dx, dy);
            }
        }

        private void HandleRemoteMouse(MouseHookEventArgs e)
        {
            if (e.IsMove)
            {
                int dx;
                int dy;
                ComputeDelta(e, out dx, out dy);

                if (dx != 0 || dy != 0)
                {
                    int rdx = _layout.ScaleDelta(dx, _layout.HostWidth, _layout.RemoteWidth);
                    int rdy = _layout.ScaleDelta(dy, _layout.HostHeight, _layout.RemoteHeight);

                    _virtualX += rdx;
                    _virtualY += rdy;

                    int hostX;
                    int hostY;
                    if (_layout.TryExitRemote(_virtualX, _virtualY, out hostX, out hostY))
                    {
                        ExitRemote(hostX, hostY, "光标从副机边缘划回本机");
                        e.Handled = true;
                        return;
                    }

                    SendRemoteMove();
                    KeepHostCursorMovable();
                }

                // 注意：这里**故意不设 Handled**，让移动事件继续传给系统，
                // 这样光标坐标才会更新，位移才读得出来。
                return;
            }

            // 按键与滚轮：吞掉，转给副机
            e.Handled = true;

            if (e.IsWheel)
            {
                Frame wheel = new Frame();
                wheel.type = Msg.MouseWheel;
                wheel.wheel = e.MouseData;
                wheel.horizontal = e.Message == NativeMethods.WmMousehwheel;
                _pump.Post(wheel);
                return;
            }

            string button;
            bool down;
            if (!TryMapButton(e.Message, out button, out down))
            {
                return;
            }

            Frame frame = new Frame();
            frame.type = Msg.MouseButton;
            frame.button = button;
            frame.down = down;
            frame.nx = CoordinateMapper.PixelsToNormalized(_virtualX, _layout.RemoteWidth);
            frame.ny = CoordinateMapper.PixelsToNormalized(_virtualY, _layout.RemoteHeight);
            _pump.Post(frame);
        }

        private static bool TryMapButton(int message, out string button, out bool down)
        {
            button = null;
            down = false;

            switch (message)
            {
                case NativeMethods.WmLbuttondown: button = "left"; down = true; return true;
                case NativeMethods.WmLbuttonup: button = "left"; down = false; return true;
                case NativeMethods.WmRbuttondown: button = "right"; down = true; return true;
                case NativeMethods.WmRbuttonup: button = "right"; down = false; return true;
                case NativeMethods.WmMbuttondown: button = "middle"; down = true; return true;
                case NativeMethods.WmMbuttonup: button = "middle"; down = false; return true;
                case NativeMethods.WmXbuttondown: button = "x1"; down = true; return true;
                case NativeMethods.WmXbuttonup: button = "x1"; down = false; return true;
                default: return false;
            }
        }

        private void ComputeDelta(MouseHookEventArgs e, out int dx, out int dy)
        {
            if (_lastHostX == int.MinValue || _lastHostY == int.MinValue)
            {
                dx = 0;
                dy = 0;
            }
            else
            {
                dx = e.X - _lastHostX;
                dy = e.Y - _lastHostY;
            }

            _lastHostX = e.X;
            _lastHostY = e.Y;
        }

        private void UpdateAnchor(MouseHookEventArgs e)
        {
            _lastHostX = e.X;
            _lastHostY = e.Y;
        }

        /// <summary>
        /// 光标接近屏幕边缘时把它搬回屏幕中心。
        /// 这是第四阶段「防抖代理」之外最关键的一处工程细节：
        /// 光标一旦被系统夹在边缘，坐标不再变化，位移永远是 0，
        /// 副机上的光标就会停住不动。
        /// 搬动之后必须**同步更新基准坐标**，否则下一次算出来的位移
        /// 会是「中心到边缘」这么一个巨大的假值，副机光标会瞬间弹飞。
        /// </summary>
        private void KeepHostCursorMovable()
        {
            if (!_layout.NeedsRecenter(_lastHostX, _lastHostY))
            {
                return;
            }

            int cx = _layout.HostWidth / 2;
            int cy = _layout.HostHeight / 2;

            if (Math.Abs(_lastHostX - cx) <= 1 && Math.Abs(_lastHostY - cy) <= 1)
            {
                return;
            }

            NativeMethods.SetCursorPos(cx, cy);
            _lastHostX = cx;
            _lastHostY = cy;
        }

        // ------------------------------------------------------------ 状态切换

        private void EnterRemote(int remoteX, int remoteY, int dx, int dy)
        {
            _virtualX = remoteX;
            _virtualY = remoteY;

            // 进入时的朝向位移要算进去，否则第一次移动会有「1 像素丢失」的感觉
            _virtualX += _layout.ScaleDelta(dx, _layout.HostWidth, _layout.RemoteWidth);
            _virtualY += _layout.ScaleDelta(dy, _layout.HostHeight, _layout.RemoteHeight);

            _location = CursorLocation.Remote;
            _crossings++;

            // 立刻把主机光标搬到中心：此时它正贴在边缘，没有位移可读
            int cx = _layout.HostWidth / 2;
            int cy = _layout.HostHeight / 2;
            NativeMethods.SetCursorPos(cx, cy);
            _lastHostX = cx;
            _lastHostY = cy;

            SendRemoteMove();
            LogMessage("鼠标已切换到副电脑（虚拟坐标 " + _virtualX + "," + _virtualY + "）");
            RaiseLocation();
        }

        private void ExitRemote(int hostX, int hostY, string reason)
        {
            _location = CursorLocation.Local;
            _crossings++;

            // 回程时把光标停放在距边缘 ReturnGuard 像素处：
            // 必须再真实移动这么多像素才能再次穿越，这是防抖的最后一道闸。
            NativeMethods.SetCursorPos(hostX, hostY);
            _lastHostX = hostX;
            _lastHostY = hostY;

            ReleaseRemoteHold();
            LogMessage("鼠标已返回本机（" + reason + "）");
            RaiseLocation();
        }

        /// <summary>强制返回本机（Ctrl+Alt+Home 或断线兜底）。</summary>
        public void ForceReturnLocal(string reason)
        {
            if (_location != CursorLocation.Remote)
            {
                return;
            }

            int x;
            int y;
            switch (_layout.Side)
            {
                case ScreenSide.Left:
                    x = _layout.ReturnGuard;
                    y = _layout.HostHeight / 2;
                    break;
                case ScreenSide.Above:
                    x = _layout.HostWidth / 2;
                    y = _layout.ReturnGuard;
                    break;
                case ScreenSide.Below:
                    x = _layout.HostWidth / 2;
                    y = _layout.HostHeight - 1 - _layout.ReturnGuard;
                    break;
                default:
                    x = _layout.HostWidth - 1 - _layout.ReturnGuard;
                    y = _layout.HostHeight / 2;
                    break;
            }

            ExitRemote(x, y, reason);
        }

        /// <summary>把留在副机上的按下状态全部松开（防粘键）。</summary>
        private void ReleaseRemoteHold()
        {
            lock (_sync)
            {
                foreach (int vk in _pressedKeys)
                {
                    Frame up = new Frame();
                    up.type = Msg.Key;
                    up.vk = vk;
                    up.scan = InputInjector.ScanCodeFromVirtualKey(vk);
                    up.extended = InputInjector.IsExtendedKey(vk);
                    up.down = false;
                    _pump.Post(up);
                }

                if (_pressedKeys.Count > 0)
                {
                    LogMessage("已向副机补发 " + _pressedKeys.Count + " 个抬起事件，避免粘键");
                }

                _pressedKeys.Clear();
            }
        }

        private void RaiseLocation()
        {
            Action<CursorLocation> handler = LocationChanged;
            if (handler != null)
            {
                try
                {
                    handler(_location);
                }
                catch
                {
                }
            }
        }

        private void LogMessage(string text)
        {
            Action<string> handler = Log;
            if (handler != null)
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

        // ------------------------------------------------------------ 键盘

        private void OnKeyboardEvent(object sender, KeyboardHookEventArgs e)
        {
            if (!_active || e.Injected)
            {
                return;
            }

            // 修饰键状态必须在两种模式下都跟踪，否则从副机返回本机后
            // 热键判定会用错状态（比如以为 Ctrl 还按着）
            TrackModifiers(e);

            if (_location != CursorLocation.Remote)
            {
                return;
            }

            if (IsForceReturnHotkey(e))
            {
                e.Handled = true;
                if (!e.Up)
                {
                    ForceReturnLocal("按下 Ctrl+Alt+Home");
                }

                return;
            }

            if (!_keyboardEnabled)
            {
                // 关掉键盘共享时按键留在本机，不做吞掉 —— 静默吞键比不共享更让人困惑
                return;
            }

            e.Handled = true;

            lock (_sync)
            {
                if (e.Up)
                {
                    _pressedKeys.Remove(e.VirtualKey);
                }
                else
                {
                    _pressedKeys.Add(e.VirtualKey);
                }
            }

            Frame frame = new Frame();
            frame.type = Msg.Key;
            frame.vk = e.VirtualKey;
            frame.scan = e.ScanCode > 0 ? e.ScanCode : InputInjector.ScanCodeFromVirtualKey(e.VirtualKey);
            frame.extended = e.Extended;
            frame.down = !e.Up;
            _pump.Post(frame);
        }

        private void TrackModifiers(KeyboardHookEventArgs e)
        {
            switch (e.VirtualKey)
            {
                case VkControl:
                case VkLControl:
                case VkRControl:
                    _ctrlDown = !e.Up;
                    break;
                case VkMenu:
                case VkLMenu:
                case VkRMenu:
                    _altDown = !e.Up;
                    break;
            }
        }

        /// <summary>
        /// 可选热键「强制返回本机」：Ctrl+Alt+Home。
        /// 穿越不需要热键，这个只是「万一在副机上找不到北」的逃生舱。
        /// 注意它必须在这里判定：Remote 模式下按键被我们吞掉，
        /// 系统级的 RegisterHotKey 根本收不到。
        /// </summary>
        private bool IsForceReturnHotkey(KeyboardHookEventArgs e)
        {
            return e.VirtualKey == VkHome && _ctrlDown && _altDown;
        }

        // ------------------------------------------------------------ 发送

        private void SendRemoteMove()
        {
            int x = _virtualX;
            int y = _virtualY;
            _layout.ClampRemote(ref x, ref y);

            Frame frame = new Frame();
            frame.type = Msg.MouseMove;
            frame.nx = CoordinateMapper.PixelsToNormalized(x, _layout.RemoteWidth);
            frame.ny = CoordinateMapper.PixelsToNormalized(y, _layout.RemoteHeight);
            _pump.Post(frame);
        }

        /// <summary>
        /// 第三阶段要求的主机测试按钮：让副机光标移动到指定像素。
        /// 不依赖钩子和布局，用来单独验证「网络 + 注入」这条链路。
        /// </summary>
        public void SendTestMove(int pixelX, int pixelY)
        {
            Frame frame = new Frame();
            frame.type = Msg.MouseMove;
            frame.nx = CoordinateMapper.PixelsToNormalized(pixelX, _layout.RemoteWidth);
            frame.ny = CoordinateMapper.PixelsToNormalized(pixelY, _layout.RemoteHeight);
            _pump.Post(frame);
            LogMessage("已发送测试移动指令：副机光标 -> (" + pixelX + "," + pixelY + ")");
        }

        /// <summary>发一条提示给副机显示（例如「主机已断开」）。</summary>
        public void SendNotice(string text)
        {
            Frame frame = new Frame();
            frame.type = Msg.Notice;
            frame.note = text;
            _pump.Post(frame);
        }

        // ------------------------------------------------------------ 断线兜底

        private void OnPeerConnectedChanged(bool connected)
        {
            if (connected)
            {
                return;
            }

            // 掉线时必须立刻收回控制权，否则用户的鼠标被留在副机上，
            // 而本机又收不到事件，整台机器就「失控」了 —— 这是最糟糕的故障态。
            if (_location == CursorLocation.Remote)
            {
                ForceReturnLocal("与副机的连接已断开，自动收回鼠标");
            }
        }
    }
}
