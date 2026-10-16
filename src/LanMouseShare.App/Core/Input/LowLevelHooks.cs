using System;
using System.Runtime.InteropServices;
using LanMouseShare.Core.Interop;

namespace LanMouseShare.Core.Input
{
    /// <summary>低级鼠标钩子的事件参数。</summary>
    public sealed class MouseHookEventArgs : EventArgs
    {
        /// <summary>原始窗口消息（WmMousemove 等）。</summary>
        public int Message { get; set; }

        /// <summary>光标屏幕坐标（物理像素）。</summary>
        public int X { get; set; }

        public int Y { get; set; }

        /// <summary>滚轮增量（以 120 为一格）；按键事件里是 XBUTTON 号。</summary>
        public int MouseData { get; set; }

        /// <summary>是否由本程序注入（防死循环依据）。</summary>
        public bool Injected { get; set; }

        /// <summary>订阅者把它设为 true 表示「吞掉这个事件」。</summary>
        public bool Handled { get; set; }

        public bool IsMove
        {
            get { return Message == NativeMethods.WmMousemove; }
        }

        public bool IsWheel
        {
            get { return Message == NativeMethods.WmMousewheel || Message == NativeMethods.WmMousehwheel; }
        }
    }

    /// <summary>低级键盘钩子的事件参数。</summary>
    public sealed class KeyboardHookEventArgs : EventArgs
    {
        public int Message { get; set; }

        /// <summary>虚拟键码。</summary>
        public int VirtualKey { get; set; }

        /// <summary>硬件扫描码。</summary>
        public int ScanCode { get; set; }

        /// <summary>是否扩展键（方向键、右 Ctrl 等）。</summary>
        public bool Extended { get; set; }

        /// <summary>是否抬起。</summary>
        public bool Up { get; set; }

        public bool Injected { get; set; }

        public bool Handled { get; set; }

        /// <summary>系统键（Alt 组合会走 WM_SYSKEYDOWN）。</summary>
        public bool IsSystem
        {
            get
            {
                return Message == NativeMethods.WmSyskeydown || Message == NativeMethods.WmSyskeyup;
            }
        }
    }

    /// <summary>
    /// WH_MOUSE_LL / WH_KEYBOARD_LL 的托管封装（第四阶段：鼠标钩子 / 键盘钩子 /
    /// 防死循环三个代理的落点）。
    ///
    /// 三条铁律：
    /// 1) 必须在有消息泵的线程安装 —— 这里就是 WPF 的 UI 线程；
    /// 2) 回调里只做内存判断，绝不碰网络和磁盘。系统给的回调预算是 300ms，
    ///    超时会被静默摘钩，而且不报任何错，极难排查；
    /// 3) 靠 dwExtraInfo 签名识别自己注入的事件（不是靠时间窗），
    ///    这样绝不会把自己发出去的事件又吃回来形成死循环。
    /// </summary>
    public sealed class LowLevelHooks : IDisposable
    {
        // 委托实例必须用字段保存住，否则会被 GC 回收 -> 钩子回调变成野指针直接崩溃
        private readonly NativeMethods.LowLevelMouseProc _mouseProc;
        private readonly NativeMethods.LowLevelKeyboardProc _keyboardProc;

        private IntPtr _mouseHook = IntPtr.Zero;
        private IntPtr _keyboardHook = IntPtr.Zero;
        private bool _disposed;

        public event EventHandler<MouseHookEventArgs> MouseEvent;
        public event EventHandler<KeyboardHookEventArgs> KeyboardEvent;

        /// <summary>被吞掉的事件数（诊断用，不参与逻辑）。</summary>
        public long SuppressedCount;

        /// <summary>被识别为「自己注入」而放行的事件数（防死循环生效的证据）。</summary>
        public long IgnoredInjectedCount;

        public bool MouseHookInstalled
        {
            get { return _mouseHook != IntPtr.Zero; }
        }

        public bool KeyboardHookInstalled
        {
            get { return _keyboardHook != IntPtr.Zero; }
        }

        public LowLevelHooks()
        {
            _mouseProc = MouseCallback;
            _keyboardProc = KeyboardCallback;
        }

        /// <summary>安装鼠标钩子。返回是否成功。</summary>
        public bool InstallMouseHook(out string error)
        {
            error = null;
            if (_mouseHook != IntPtr.Zero)
            {
                return true;
            }

            IntPtr hModule = NativeMethods.GetModuleHandle(null);
            _mouseHook = NativeMethods.SetWindowsHookEx(
                NativeMethods.WhMouseLl, _mouseProc, hModule, 0);

            if (_mouseHook == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                error = "SetWindowsHookEx(WH_MOUSE_LL) 失败，Win32=" + err
                        + "（" + new System.ComponentModel.Win32Exception(err).Message + "）";
                return false;
            }

            return true;
        }

        /// <summary>安装键盘钩子。</summary>
        public bool InstallKeyboardHook(out string error)
        {
            error = null;
            if (_keyboardHook != IntPtr.Zero)
            {
                return true;
            }

            IntPtr hModule = NativeMethods.GetModuleHandle(null);
            _keyboardHook = NativeMethods.SetWindowsHookEx(
                NativeMethods.WhKeyboardLl, _keyboardProc, hModule, 0);

            if (_keyboardHook == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                error = "SetWindowsHookEx(WH_KEYBOARD_LL) 失败，Win32=" + err
                        + "（" + new System.ComponentModel.Win32Exception(err).Message + "）";
                return false;
            }

            return true;
        }

        public bool UninstallMouseHook()
        {
            if (_mouseHook == IntPtr.Zero)
            {
                return true;
            }

            bool ok = NativeMethods.UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
            return ok;
        }

        public bool UninstallKeyboardHook()
        {
            if (_keyboardHook == IntPtr.Zero)
            {
                return true;
            }

            bool ok = NativeMethods.UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = IntPtr.Zero;
            return ok;
        }

        public void UninstallAll()
        {
            UninstallMouseHook();
            UninstallKeyboardHook();
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            UninstallAll();
        }

        // ------------------------------------------------------------ 回调

        private IntPtr MouseCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode < NativeMethods.HcAction)
            {
                return NativeMethods.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
            }

            try
            {
                int message = wParam.ToInt32();
                NativeMethods.MSLLHOOKSTRUCT data =
                    (NativeMethods.MSLLHOOKSTRUCT)Marshal.PtrToStructure(
                        lParam, typeof(NativeMethods.MSLLHOOKSTRUCT));

                bool injected = IsInjected(data.dwExtraInfo, data.flags);

                EventHandler<MouseHookEventArgs> handler = MouseEvent;
                if (handler != null)
                {
                    MouseHookEventArgs args = new MouseHookEventArgs();
                    args.Message = message;
                    args.X = data.pt.x;
                    args.Y = data.pt.y;
                    args.MouseData = unchecked((int)data.mouseData);
                    args.Injected = injected;
                    args.Handled = false;

                    handler(this, args);

                    if (args.Handled)
                    {
                        SuppressedCount++;
                        // 非 0 表示吞掉：不再往下传给钩子链，也不再投递给目标窗口
                        return new IntPtr(1);
                    }
                }

                if (injected)
                {
                    IgnoredInjectedCount++;
                }
            }
            catch
            {
                // 回调里绝不允许异常逃出去：一旦逃出，钩子链会被破坏，
                // 严重时整个鼠标就卡死。宁可丢掉这一个事件。
            }

            return NativeMethods.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
        }

        private IntPtr KeyboardCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode < NativeMethods.HcAction)
            {
                return NativeMethods.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
            }

            try
            {
                int message = wParam.ToInt32();
                NativeMethods.KBDLLHOOKSTRUCT data =
                    (NativeMethods.KBDLLHOOKSTRUCT)Marshal.PtrToStructure(
                        lParam, typeof(NativeMethods.KBDLLHOOKSTRUCT));

                bool injected = IsInjected(data.dwExtraInfo, data.flags);

                EventHandler<KeyboardHookEventArgs> handler = KeyboardEvent;
                if (handler != null)
                {
                    KeyboardHookEventArgs args = new KeyboardHookEventArgs();
                    args.Message = message;
                    args.VirtualKey = unchecked((int)data.vkCode);
                    args.ScanCode = unchecked((int)data.scanCode);
                    args.Extended = (data.flags & NativeMethods.LlkhfExtended) != 0;
                    args.Up = (data.flags & NativeMethods.LlkhfUp) != 0;
                    args.Injected = injected;
                    args.Handled = false;

                    handler(this, args);

                    if (args.Handled)
                    {
                        SuppressedCount++;
                        return new IntPtr(1);
                    }
                }

                if (injected)
                {
                    IgnoredInjectedCount++;
                }
            }
            catch
            {
            }

            return NativeMethods.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
        }

        /// <summary>
        /// 防死循环判定：只要带我们的注入签名，或者带 LLKHF_INJECTED 位，
        /// 就认为不是真人操作。
        ///
        /// 这里**故意**不比对时间窗：时间窗法在高速移动时会误伤真人输入，
        /// 而签名是精确的、零误判的。
        /// 另外注意：别的程序（例如远程桌面、自动化脚本）注入的事件
        /// 也会带 LLKHF_INJECTED，我们同样放行 —— 不去拦截别的程序，
        /// 避免互相打架。
        /// </summary>
        private static bool IsInjected(IntPtr extraInfo, uint flags)
        {
            if (extraInfo == new IntPtr(InputInjector.InjectionSignature))
            {
                return true;
            }

            if ((flags & NativeMethods.LlkhfInjected) != 0)
            {
                return true;
            }

            if ((flags & NativeMethods.LlkhfLowerIlInjected) != 0)
            {
                return true;
            }

            return false;
        }
    }
}
