using System;
using System.Collections.Generic;
using LanMouseShare.Core.Input;
using LanMouseShare.Core.Net;

namespace LanMouseShare.Core.Sharing
{
    /// <summary>
    /// 副机端（不接鼠标键盘的那台）：第三、五阶段在副机侧的落点。
    ///
    /// 副机的职责比主机简单得多，而且**副机不需要装任何钩子**：
    ///   光标该去哪、什么时候划回主机，全部由主机算好，副机只负责
    ///   把收到的归一化坐标用 SendInput 还原成真实光标位置。
    ///   这么做的好处在多显示器/不同分辨率下会立刻体现出来 ——
    ///   副机完全不需要知道主机长什么样。
    ///
    /// 唯一需要自己管的是「按下状态」：链路一旦断掉，必须把还在
    /// 按下状态的按键和鼠标键补发抬起事件，否则副机上会出现
    /// 「一直按住 Ctrl」这种粘键，用户只能重启程序。
    /// </summary>
    public sealed class ClientController : IDisposable
    {
        private readonly PeerConnection _peer;
        private readonly object _sync = new object();
        private readonly HashSet<MouseButtonKind> _downButtons = new HashSet<MouseButtonKind>();
        private readonly HashSet<int> _downKeys = new HashSet<int>();
        private readonly Queue<Frame> _playback = new Queue<Frame>();

        private bool _active;
        private long _appliedFrames;
        private long _failedFrames;
        private int _lastX = -1;
        private int _lastY = -1;

        public ClientController(PeerConnection peer)
        {
            if (peer == null)
            {
                throw new ArgumentNullException("peer");
            }

            _peer = peer;
        }

        public bool Active
        {
            get { return _active; }
        }

        public long AppliedFrames
        {
            get { return _appliedFrames; }
        }

        public long FailedFrames
        {
            get { return _failedFrames; }
        }

        /// <summary>最近一次被还原到的副机像素坐标（界面显示用）。</summary>
        public int LastX
        {
            get { return _lastX; }
        }

        public int LastY
        {
            get { return _lastY; }
        }

        public event Action<string> Log;
        public event Action<string> NoticeReceived;

        public bool Start(out string error)
        {
            error = null;
            if (_active)
            {
                return true;
            }

            _peer.FrameReceived += OnFrame;
            _peer.Authenticated += OnAuthenticated;
            _peer.ConnectedChanged += OnConnectedChanged;
            _active = true;
            LogMessage("副机模式已启动，等待主机连接（端口 " + _peer.GetType().Name + "）");
            return true;
        }

        public void Stop()
        {
            if (!_active)
            {
                return;
            }

            _active = false;
            _peer.FrameReceived -= OnFrame;
            _peer.Authenticated -= OnAuthenticated;
            _peer.ConnectedChanged -= OnConnectedChanged;
            ReleaseAll();
            LogMessage("副机模式已停止");
        }

        public void Dispose()
        {
            Stop();
        }

        // ------------------------------------------------------------ 事件

        private void OnAuthenticated(string peer)
        {
            SendScreenInfo();
            LogMessage("与主机认证通过（" + peer + "），已上报本机分辨率");
        }

        private void SendScreenInfo()
        {
            Frame frame = new Frame();
            frame.type = Msg.ScreenInfo;
            frame.screenWidth = VirtualScreen.PrimaryWidth;
            frame.screenHeight = VirtualScreen.PrimaryHeight;
            _peer.SendAuthenticated(frame);
        }

        private void OnConnectedChanged(bool connected)
        {
            if (connected)
            {
                return;
            }

            // 断线兜底：把所有「按着没放」的东西松开
            ReleaseAll();
        }

        private void OnFrame(Frame frame)
        {
            if (!_active || frame == null)
            {
                return;
            }

            bool ok;
            switch (frame.type)
            {
                case Msg.MouseMove:
                    ok = ApplyMove(frame);
                    break;
                case Msg.MouseButton:
                    ok = ApplyButton(frame);
                    break;
                case Msg.MouseWheel:
                    ok = ApplyWheel(frame);
                    break;
                case Msg.Key:
                    ok = ApplyKey(frame);
                    break;
                case Msg.Notice:
                    RaiseNotice(frame.note);
                    return;
                case Msg.ScreenInfo:
                    // 主机也会报自己的分辨率，副机目前不需要，但记下来便于排查
                    LogMessage("主机分辨率 " + frame.screenWidth + "x" + frame.screenHeight);
                    return;
                default:
                    return;
            }

            if (ok)
            {
                _appliedFrames++;
            }
            else
            {
                _failedFrames++;
                LogMessage("注入失败：" + InputInjector.LastFailure);
            }
        }

        private bool ApplyMove(Frame frame)
        {
            int x;
            int y;
            CoordinateMapper.NormalizedToPixels(
                frame.nx, frame.ny,
                VirtualScreen.PrimaryWidth, VirtualScreen.PrimaryHeight,
                out x, out y);

            _lastX = x;
            _lastY = y;
            return InputInjector.MoveAbsolute(frame.nx, frame.ny);
        }

        private bool ApplyButton(Frame frame)
        {
            MouseButtonKind kind;
            if (!MouseButtonKindExtensions.TryParse(frame.button, out kind))
            {
                return false;
            }

            lock (_sync)
            {
                if (frame.down)
                {
                    _downButtons.Add(kind);
                }
                else
                {
                    _downButtons.Remove(kind);
                }
            }

            return InputInjector.Button(kind, frame.down);
        }

        private bool ApplyWheel(Frame frame)
        {
            return frame.horizontal
                ? InputInjector.HorizontalWheel(frame.wheel)
                : InputInjector.Wheel(frame.wheel);
        }

        private bool ApplyKey(Frame frame)
        {
            lock (_sync)
            {
                if (frame.down)
                {
                    _downKeys.Add(frame.vk);
                }
                else
                {
                    _downKeys.Remove(frame.vk);
                }
            }

            return InputInjector.Key(frame.vk, frame.scan, frame.extended, frame.down);
        }

        /// <summary>把残留的按下状态全部松开。</summary>
        private void ReleaseAll()
        {
            List<MouseButtonKind> buttons;
            List<int> keys;

            lock (_sync)
            {
                if (_downButtons.Count == 0 && _downKeys.Count == 0)
                {
                    return;
                }

                buttons = new List<MouseButtonKind>(_downButtons);
                keys = new List<int>(_downKeys);
                _downButtons.Clear();
                _downKeys.Clear();
            }

            foreach (MouseButtonKind kind in buttons)
            {
                InputInjector.Button(kind, false);
            }

            foreach (int vk in keys)
            {
                InputInjector.Key(vk, InputInjector.ScanCodeFromVirtualKey(vk),
                    InputInjector.IsExtendedKey(vk), false);
            }

            LogMessage("已补发抬起事件：" + buttons.Count + " 个鼠标键、" + keys.Count + " 个按键");
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

        private void RaiseNotice(string text)
        {
            Action<string> handler = NoticeReceived;
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
