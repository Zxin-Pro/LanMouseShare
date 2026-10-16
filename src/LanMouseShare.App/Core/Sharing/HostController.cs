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
    ///   边缘就卡住不动」。所以移动一律放行。
    ///
    ///   1.0.2 起 Remote 模式改成「**每处理一次移动就把光标钉回屏幕中心**」
    ///   （ParkHostCursor）+ 把箭头藏起来（InputInjector.HideCursor）。
    ///   旧版是「只在离边缘 8 像素内才搬回中心」，真机反馈暴露了两个后果：
    ///   主机屏幕上能看见一个箭头到处乱跑（用户原话「主机屏幕上还显示鼠标」），
    ///   以及靠临时搬回中心凑位移，导致副机光标走满一屏要付出大约两倍行程、
    ///   手感发飘。钉死在中心之后，每个事件算出的 (e.X - 中心) 恰好就是那次
    ///   的物理位移，一像素不丢也不再多出假位移。
    ///   SetCursorPos 自己产生的回声事件坐标正好等于停放点，算出来位移天然
    ///   为 0，会被忽略，不需要额外打标记。
    ///
    ///   配套的第二个修复是 ScreenLayout.EntryGuard：旧版从副机的 x=0 落脚，
    ///   手腕往回抖 4 像素就弹回主机，副机上根本没法用。
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

        /// <summary>主机光标停放点（屏幕正中心），Remote 期间每个事件都钉回这里。</summary>
        private int _parkX;
        private int _parkY;

        /// <summary>装钩子的线程 id：ShowCursor 对调用线程有亲和性，见 OnHookThread。</summary>
        private uint _hookThreadId;
        private bool _parkFailureLogged;

        private bool _active;
        private bool _firstMouseEventLogged;
        private int _edgeProbeLogged;
        private int _remoteProbeLogged;
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

        /// <summary>
        /// 把动作调度回 UI 线程的执行器（由界面层注入）。
        /// 断线兜底那条路径是从网络线程过来的，而 ShowCursor 维护的是
        /// **调用线程**的显示计数，在网络线程上调等于没调，用户的箭头
        /// 会一直回不来。不注入也能跑，只是那种情况下会就地执行。
        /// </summary>
        public Action<Action> UiDispatch { get; set; }

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

            // 记下装钩子的线程。低级钩子的回调就跑在这个线程上，
            // ShowCursor 这类有线程亲和性的调用必须回到它上面执行。
            _hookThreadId = NativeMethods.GetCurrentThreadId();
            UpdateParkPoint();

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

            // 停在 Remote 状态时被停掉（关程序、断线收尾）也必须把箭头还回去，
            // 否则用户的鼠标会凭空消失，只能注销才回来。
            if (_location == CursorLocation.Remote)
            {
                _location = CursorLocation.Local;
                RestoreHostCursor();
            }

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

        /// <summary>
        /// 收到第一个鼠标事件时记一行日志。
        ///
        /// 为什么要专门记这一行：用户报「鼠标划过去没反应」时，
        /// 必须先区分两种完全不同的故障 —— 「钩子没装上 / 一个事件都没收到」
        /// 还是「收到了但穿越判定没过」。没有这一行就只能靠猜。
        /// 只记一次，不刷屏。
        /// </summary>
        private void LogFirstMouseEvent(MouseHookEventArgs e)
        {
            if (_firstMouseEventLogged)
            {
                return;
            }

            _firstMouseEventLogged = true;
            LogMessage("鼠标钩子已开始接收事件（第一个事件："
                + (e.IsMove ? "移动" : "按键/滚轮")
                + " " + e.X + "," + e.Y
                + "）；主机屏幕 " + _layout.HostWidth + "x" + _layout.HostHeight
                + "，副机分辨率" + (_layout.RemoteKnown ? "已" : "未") + "知，边缘穿越"
                + (_layout.RemoteKnown ? "已启用" : "尚未启用（等副机上报）") + "。");
        }

        private void OnMouseEvent(object sender, MouseHookEventArgs e)
        {
            if (!_active || e.Injected)
            {
                return;
            }

            LogFirstMouseEvent(e);

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

            LogEdgeProbe(e, dx, dy);

            int remoteX;
            int remoteY;
            if (_layout.TryExitHost(e.X, e.Y, dx, dy, out remoteX, out remoteY))
            {
                EnterRemote(remoteX, remoteY, dx, dy);
            }
        }

        /// <summary>
        /// 光标贴近贴合边时把这次判定过程记进日志（最多 40 行）。
        ///
        /// 为什么要这条诊断：用户报「鼠标划到边缘过不去」时，光看日志分不清是
        /// 「事件根本没到」还是「到了但坐标/位移不满足条件」。有了这几行，
        /// 事件坐标、本次位移、屏幕尺寸、副机分辨率是否已知全都一目了然。
        /// 只在贴近边缘时才写，正常使用几乎不会产生噪音，且有硬上限。
        /// </summary>
        private void LogEdgeProbe(MouseHookEventArgs e, int dx, int dy)
        {
            if (_edgeProbeLogged >= 40)
            {
                return;
            }

            // 只看「贴近某一条屏幕边缘」的事件，屏幕中间区域完全不记，避免噪音
            bool nearEdge = e.X <= ScreenLayout.EdgeTolerance
                || e.X >= _layout.HostWidth - 1 - ScreenLayout.EdgeTolerance
                || e.Y <= ScreenLayout.EdgeTolerance
                || e.Y >= _layout.HostHeight - 1 - ScreenLayout.EdgeTolerance;

            if (!nearEdge)
            {
                return;
            }

            _edgeProbeLogged++;
            LogMessage("[边缘诊断] 光标 " + e.X + "," + e.Y
                + "（主机屏幕 " + _layout.HostWidth + "x" + _layout.HostHeight + "）"
                + "，本次位移 " + dx + "," + dy
                + "，副机分辨率" + (_layout.RemoteKnown ? "已知" : "未知") + "。");
        }

        private void HandleRemoteMouse(MouseHookEventArgs e)
        {
            if (e.IsMove)
            {
                int dx;
                int dy;
                ComputeDelta(e, out dx, out dy);

                // 位移为 0 只有两种可能：用户真的没动，或者这是上一轮
                // SetCursorPos 把光标钉回停放点之后系统补发的回声事件。
                // 两种都不需要做任何事：回声的坐标正好等于停放点，
                // 算出来天然是 0，所以这里连「注入标记」都不用打。
                if (dx == 0 && dy == 0)
                {
                    return;
                }

                _virtualX += _layout.ScaleDelta(dx, _layout.HostWidth, _layout.RemoteWidth);
                _virtualY += _layout.ScaleDelta(dy, _layout.HostHeight, _layout.RemoteHeight);

                LogRemoteProbe(e, dx, dy);

                int hostX;
                int hostY;
                if (_layout.TryExitRemote(_virtualX, _virtualY, out hostX, out hostY))
                {
                    ExitRemote(hostX, hostY, "光标从副机边缘划回本机");
                    e.Handled = true;
                    return;
                }

                SendRemoteMove();

                // 每处理完一次移动立刻把主机光标钉回停放点。它固定在中心，
                // 既不会在主机屏幕上乱跑，也不会被系统夹到边缘把位移吃掉。
                ParkHostCursor();

                // 注意：这里**故意不设 Handled**，让移动事件继续传给系统。
                // 一旦吞掉，光标坐标就不再变化，下一个事件算出来的位移
                // 永远是 0，副机光标会直接卡死 —— 本项目最容易踩的坑。
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

        /// <summary>
        /// Remote 模式下把前若干次移动的关键数字记进日志（最多 40 行）。
        ///
        /// 用途：用户报「副机上鼠标不动 / 划不回来」时，这几行能立刻区分是
        /// 「事件根本没到钩子」「位移被算成了 0」还是「虚拟坐标没越过回程门槛」，
        /// 否则只能靠猜。有硬上限，正常使用不会刷屏。
        /// </summary>
        private void LogRemoteProbe(MouseHookEventArgs e, int dx, int dy)
        {
            if (_remoteProbeLogged >= 40)
            {
                return;
            }

            _remoteProbeLogged++;
            LogMessage("[副机诊断] 主机光标 " + e.X + "," + e.Y
                + "，本次位移 " + dx + "," + dy
                + "，虚拟坐标 " + _virtualX + "," + _virtualY
                + "（副机 " + _layout.RemoteWidth + "x" + _layout.RemoteHeight
                + "，回程门槛 " + (-ScreenLayout.DefaultReturnGuard) + "）");
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
        /// 停放点 = 主机屏幕正中心。
        /// 选中心是因为它到四条边的距离都最远：用户朝任意方向甩出一个
        /// 大幅位移时可用行程最大（半个屏），被系统夹在边缘而丢掉位移的
        /// 概率最低。
        /// </summary>
        private void UpdateParkPoint()
        {
            int width = _layout.HostWidth > 0 ? _layout.HostWidth : VirtualScreen.PrimaryWidth;
            int height = _layout.HostHeight > 0 ? _layout.HostHeight : VirtualScreen.PrimaryHeight;
            _parkX = width / 2;
            _parkY = height / 2;
        }

        /// <summary>
        /// 把主机光标钉回停放点，并把基准坐标同步过去。
        ///
        /// 为什么必须同步基准（最容易写错的一行）：下一个事件算的是
        /// e.X - _lastHostX。如果不更新，算出来就是「停放点到刚才那个位置」
        /// 的整段距离，副机光标会瞬间弹飞出去；同步成停放点之后，
        /// 算出来的正好是这之后发生的那一小段真实物理位移。
        ///
        /// 为什么不能改成「吞掉移动事件」：见 HandleRemoteMouse 里的说明。
        /// </summary>
        private void ParkHostCursor()
        {
            if (!NativeMethods.SetCursorPos(_parkX, _parkY) && !_parkFailureLogged)
            {
                _parkFailureLogged = true;
                LogMessage("警告：把主机光标钉回中心失败（可能有程序在用 ClipCursor 限制光标范围），"
                           + "副机光标可能出现跳变。");
            }

            _lastHostX = _parkX;
            _lastHostY = _parkY;
        }

        /// <summary>
        /// 把 HideCursor 扣掉的显示计数还回去。
        /// 每扣一层就必须还一层：Windows 的 ShowCursor 是引用计数语义，
        /// 少还一次，用户的鼠标箭头就永远回不来了（只能注销或重启）。
        /// </summary>
        private void RestoreHostCursor()
        {
            if (InputInjector.HiddenCursorCalls == 0)
            {
                return;
            }

            int restored = 0;
            OnHookThread(delegate
            {
                restored = InputInjector.ShowCursor();
            });

            if (restored > 0)
            {
                LogMessage("主机光标已恢复显示（还回了 " + restored + " 层显示计数）");
            }
        }

        /// <summary>
        /// 保证动作跑在「装钩子那个线程」上。
        /// 需要它的原因是 ShowCursor 的显示计数是**按线程**记的：
        /// 在 A 线程扣掉，就必须在 A 线程还回去；跑到别的线程上还，
        /// 那个线程的计数被调到 0 一下又加回去，实际等于什么都没做。
        /// 断线兜底这条路径是从网络线程过来的，所以必须绕这一道。
        /// </summary>
        private void OnHookThread(Action action)
        {
            if (action == null)
            {
                return;
            }

            if (NativeMethods.GetCurrentThreadId() == _hookThreadId)
            {
                action();
                return;
            }

            Action<Action> dispatch = UiDispatch;
            if (dispatch == null)
            {
                // 界面层没注入调度器也照样要工作，只是可能还不到那一层计数
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    LogMessage("警告：恢复主机光标时出错 —— " + ex.Message);
                }

                return;
            }

            try
            {
                dispatch(action);
            }
            catch (Exception ex)
            {
                LogMessage("警告：把光标恢复动作调度到界面线程失败 —— " + ex.Message);
            }
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

            // 立刻把主机光标钉到停放点：进边缘那一刻它正贴在屏幕边上，
            // 那里没有位移可读，必须先搬回中心才能继续算。
            UpdateParkPoint();
            ParkHostCursor();

            // 藏箭头。Remote 期间主机光标虽然在中心不动，但它毕竟还画着；
            // 用户的原话就是「主机的鼠标划过去 主机屏幕上还显示鼠标」。
            int hides = InputInjector.HideCursor();

            SendRemoteMove();
            LogMessage("鼠标已切换到副电脑（虚拟坐标 " + _virtualX + "," + _virtualY
                       + "，主机光标钉在 " + _parkX + "," + _parkY + " 并隐藏，扣了 " + hides + " 层显示计数）");
            RaiseLocation();
        }

        private void ExitRemote(int hostX, int hostY, string reason)
        {
            _location = CursorLocation.Local;
            _crossings++;

            // 先还箭头再搬光标。顺序反了的话用户会看见箭头在旧位置闪一下。
            RestoreHostCursor();

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
