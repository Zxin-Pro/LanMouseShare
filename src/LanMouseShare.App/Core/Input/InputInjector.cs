using System;
using System.Runtime.InteropServices;
using LanMouseShare.Core.Interop;

namespace LanMouseShare.Core.Input
{
    /// <summary>
    /// 输入注入（第三阶段：鼠标移动模拟 / 鼠标按键代理 / 滚轮模拟代理
    /// 与第五阶段键盘模拟代理共用同一个出口）。
    ///
    /// 三个必须记住的点：
    /// 1) cbSize 用 Marshal.SizeOf(typeof(INPUT)) 现算，x86=28 / x64=40，
    ///    这是 AnyCPU 下唯一正确的做法；
    /// 2) 每个事件都打上 dwExtraInfo 签名，本机的低级钩子据此忽略自己
    ///    注入的事件，这是防死循环的核心（见第四阶段）；
    /// 3) SendInput 返回的是成功注入的事件数，不是布尔值。被 UIPI 拦
    ///    （对高权限窗口注入）或处于 UAC 安全桌面/锁屏时会失败，
    ///    这不是 bug，属于已知限制。
    /// </summary>
    public static class InputInjector
    {
        /// <summary>
        /// 注入签名 'LMSH' = 0x4C4D5348。
        /// 选它是因为这个值几乎不可能被别的软件占用，
        /// 而且落在 32 位可表示范围内，x86 上 (IntPtr) 转换不会溢出。
        /// </summary>
        public const long InjectionSignature = 0x4C4D5348L;

        private static readonly int InputSize = Marshal.SizeOf(typeof(NativeMethods.INPUT));

        /// <summary>最近一次注入失败的原因（发给网络层做日志用）。</summary>
        [ThreadStatic]
        private static string _lastFailure;

        public static string LastFailure
        {
            get { return _lastFailure; }
        }

        public static int InputStructSize
        {
            get { return InputSize; }
        }

        // ------------------------------------------------------------ 鼠标移动

        /// <summary>
        /// 绝对移动。nx/ny 是 0..65535 归一化坐标。
        /// 不带 MOUSEEVENTF_VIRTUALDESK，所以归一化空间是**主显示器**，
        /// 与第一阶段「先做单显示器 MVP」的约定一致。
        /// MOUSEEVENTF_MOVE_NOCOALESCE 是 Vista+ 的标志，能防止系统把
        /// 快速移动合并掉，让副机的指针轨迹更跟手。
        /// </summary>
        public static bool MoveAbsolute(int nx, int ny)
        {
            NativeMethods.INPUT input = default(NativeMethods.INPUT);
            input.type = NativeMethods.InputMouse;
            input.u.mi.dx = nx;
            input.u.mi.dy = ny;
            input.u.mi.dwFlags = NativeMethods.MouseeventfMove
                                  | NativeMethods.MouseeventfAbsolute
                                  | NativeMethods.MouseeventfMoveNocoalesce;
            input.u.mi.dwExtraInfo = new IntPtr(InjectionSignature);
            return Send(input);
        }

        /// <summary>相对移动（给未来的「锁定指针」和调试用）。</summary>
        public static bool MoveRelative(int dx, int dy)
        {
            NativeMethods.INPUT input = default(NativeMethods.INPUT);
            input.type = NativeMethods.InputMouse;
            input.u.mi.dx = dx;
            input.u.mi.dy = dy;
            input.u.mi.dwFlags = NativeMethods.MouseeventfMove;
            input.u.mi.dwExtraInfo = new IntPtr(InjectionSignature);
            return Send(input);
        }

        // ------------------------------------------------------------ 鼠标按键

        public static bool Button(MouseButtonKind kind, bool down)
        {
            uint flag;
            int data = 0;

            switch (kind)
            {
                case MouseButtonKind.Right:
                    flag = down ? NativeMethods.MouseeventfRightdown : NativeMethods.MouseeventfRightup;
                    break;
                case MouseButtonKind.Middle:
                    flag = down ? NativeMethods.MouseeventfMiddledown : NativeMethods.MouseeventfMiddleup;
                    break;
                case MouseButtonKind.X1:
                    flag = down ? NativeMethods.MouseeventfXdown : NativeMethods.MouseeventfXup;
                    data = 1; // XBUTTON1
                    break;
                case MouseButtonKind.X2:
                    flag = down ? NativeMethods.MouseeventfXdown : NativeMethods.MouseeventfXup;
                    data = 2; // XBUTTON2
                    break;
                default:
                    flag = down ? NativeMethods.MouseeventfLeftdown : NativeMethods.MouseeventfLeftup;
                    break;
            }

            NativeMethods.INPUT input = default(NativeMethods.INPUT);
            input.type = NativeMethods.InputMouse;
            input.u.mi.mouseData = data;
            input.u.mi.dwFlags = flag;
            input.u.mi.dwExtraInfo = new IntPtr(InjectionSignature);
            return Send(input);
        }

        // ------------------------------------------------------------ 滚轮

        /// <summary>垂直滚轮。delta 以 120 为一格（WHEEL_DELTA）。</summary>
        public static bool Wheel(int delta)
        {
            NativeMethods.INPUT input = default(NativeMethods.INPUT);
            input.type = NativeMethods.InputMouse;
            input.u.mi.mouseData = delta;
            input.u.mi.dwFlags = NativeMethods.MouseeventfWheel;
            input.u.mi.dwExtraInfo = new IntPtr(InjectionSignature);
            return Send(input);
        }

        /// <summary>水平滚轮（某些鼠标的侧向滚轮）。</summary>
        public static bool HorizontalWheel(int delta)
        {
            NativeMethods.INPUT input = default(NativeMethods.INPUT);
            input.type = NativeMethods.InputMouse;
            input.u.mi.mouseData = delta;
            input.u.mi.dwFlags = NativeMethods.MouseeventfHwheel;
            input.u.mi.dwExtraInfo = new IntPtr(InjectionSignature);
            return Send(input);
        }

        // ------------------------------------------------------------ 键盘

        /// <summary>
        /// 键盘事件。优先用扫描码（KEYEVENTF_SCANCODE）：
        /// 很多游戏用 DirectInput 读扫描码，只发虚拟键码会「按了没反应」。
        /// 扫描码拿不到时退回虚拟键码，保证永远不丢按键。
        /// </summary>
        public static bool Key(int vk, int scan, bool extended, bool down)
        {
            uint flags = down ? 0u : NativeMethods.KeyeventfKeyup;
            if (extended)
            {
                flags |= NativeMethods.KeyeventfExtendedkey;
            }

            NativeMethods.INPUT input = default(NativeMethods.INPUT);
            input.type = NativeMethods.InputKeyboard;
            input.u.ki.time = 0;
            input.u.ki.dwExtraInfo = new IntPtr(InjectionSignature);

            int effectiveScan = scan > 0 ? scan : ScanCodeFromVirtualKey(vk);
            if (effectiveScan > 0)
            {
                input.u.ki.wScan = (ushort)effectiveScan;
                input.u.ki.wVk = 0;
                flags |= NativeMethods.KeyeventfScancode;
            }
            else
            {
                input.u.ki.wVk = (ushort)vk;
                input.u.ki.wScan = 0;
            }

            input.u.ki.dwFlags = flags;
            return Send(input);
        }

        /// <summary>虚拟键码 -> 扫描码（失败返回 0）。</summary>
        public static int ScanCodeFromVirtualKey(int vk)
        {
            if (vk <= 0)
            {
                return 0;
            }

            uint scan = NativeMethods.MapVirtualKey((uint)vk, NativeMethods.MapvkVkToVsc);
            return scan > 0 ? (int)scan : 0;
        }

        /// <summary>
        /// 这些键在底层事件里带 KEYEVENTF_EXTENDEDKEY 位。
        /// 漏掉会导致方向键变成数字小键盘、右 Ctrl 变成左 Ctrl，
        /// 是键盘模拟最经典的一类 bug。
        /// </summary>
        public static bool IsExtendedKey(int vk)
        {
            switch (vk)
            {
                case 0x21: // VK_PRIOR   PageUp
                case 0x22: // VK_NEXT    PageDown
                case 0x23: // VK_END
                case 0x24: // VK_HOME
                case 0x25: // VK_LEFT
                case 0x26: // VK_UP
                case 0x27: // VK_RIGHT
                case 0x28: // VK_DOWN
                case 0x2D: // VK_INSERT
                case 0x2E: // VK_DELETE
                case 0x6F: // VK_DIVIDE  （小键盘 /）
                case 0x90: // VK_NUMLOCK
                case 0xA3: // VK_RCONTROL
                case 0xA5: // VK_RMENU   （右 Alt）
                case 0x5B: // VK_LWIN
                case 0x5C: // VK_RWIN
                case 0x5D: // VK_APPS
                    return true;
                default:
                    return false;
            }
        }

        // ------------------------------------------------------------ 光标可见性

        /// <summary>
        /// 光标显示计数被我们扣掉了几层（ShowCursor 是引用计数语义）。
        /// 主机控制副机期间必须把自己的箭头藏起来，否则用户会在主机屏幕上
        /// 看见一个到处乱跑的鼠标（1.0.2 的真机反馈就是这么报的）。
        /// </summary>
        private static int _cursorHides;

        public static int HiddenCursorCalls
        {
            get { return _cursorHides; }
        }

        /// <summary>
        /// 把本机光标藏起来。ShowCursor 维护的是显示计数，别的程序可能已经
        /// 把它加回去过，所以要在循环里一路调到返回负值为止。
        /// 返回当前累计扣掉的层数（恢复时要原样还回去）。
        /// </summary>
        public static int HideCursor()
        {
            int guard = 0;
            int level;
            do
            {
                level = NativeMethods.ShowCursor(false);
                _cursorHides++;
            }
            while (level >= 0 && ++guard < 32);

            return _cursorHides;
        }

        /// <summary>把 HideCursor 扣掉的光标原样还回去。重复调用是安全的。</summary>
        public static int ShowCursor()
        {
            int calls = _cursorHides;
            for (int i = 0; i < calls; i++)
            {
                NativeMethods.ShowCursor(true);
            }

            _cursorHides = 0;
            return calls;
        }

        /// <summary>
        /// 非破坏性地读出 Windows 内部的光标**显示计数**。
        ///
        /// 做法：先 ShowCursor(true) 拿到新值（= C+1），紧跟着 ShowCursor(false)
        /// 把它原样还原，净变化为 0 —— 所以调用它不会影响光标状态。
        ///
        /// **1.0.5 更正**：1.0.5 之前这里写着「GetCursorInfo 不反映这个计数，不能当证据」。
        /// 那条结论是错的，错在把「压别的窗口上测」当成了推翻依据。提权重测（lms-cursor2.ps1）：
        ///
        ///   A.光标压在我们自己的窗口上，count=0  → flags=0x1 showing=True
        ///   B.光标压在我们自己的窗口上，count=-3 → flags=0x0 showing=False  ☆计数确实生效
        ///   C.光标压在别的窗口上，    count=-3 → flags=0x1 showing=True
        ///
        /// 也就是说 GetCursorInfo 的 CURSOR_SHOWING **忠实反映「此刻画不画」**，
        /// 只不过它的前提是「光标得压在我们自己线程的窗口上」—— 这正是 1.0.5 加罩窗的原因，
        /// 见 Core\Host\CursorShieldWindow.cs。
        /// </summary>
        public static int DisplayCount
        {
            get
            {
                int up = NativeMethods.ShowCursor(true);
                NativeMethods.ShowCursor(false);
                return up - 1;
            }
        }

        /// <summary>
        /// 光标此刻是否真的画着（GetCursorInfo 口径）。
        ///
        /// 1.0.5 起这就是「藏成功了没有」的**首选判据**（理由见 DisplayCount 的注释）：
        /// 前提是光标得压在我们自己线程的窗口上 —— 正常情况下由 CursorShieldWindow
        /// 那块全屏罩窗保证，所以自检里会先把罩窗盖上再断言。
        /// 查不到时保守返回「可见」，不谎报成功。
        /// cbSize 必须现算：x86 = 20、x64 = 24。
        /// </summary>
        public static bool IsCursorVisible()
        {
            NativeMethods.CURSORINFO info = default(NativeMethods.CURSORINFO);
            info.cbSize = Marshal.SizeOf(typeof(NativeMethods.CURSORINFO));
            if (!NativeMethods.GetCursorInfo(ref info))
            {
                // 查不到就保守地当成「可见」，不要谎报成功
                return true;
            }

            return (info.flags & NativeMethods.CursorShowing) != 0;
        }

        // ------------------------------------------------------------ 内部

        private static bool Send(NativeMethods.INPUT input)
        {
            NativeMethods.INPUT[] buffer = new NativeMethods.INPUT[1];
            buffer[0] = input;

            uint sent = NativeMethods.SendInput(1, buffer, InputSize);
            if (sent == 1)
            {
                _lastFailure = null;
                return true;
            }

            int err = Marshal.GetLastWin32Error();
            _lastFailure = "SendInput 只注入了 " + sent + "/1 个事件，Win32=" + err
                           + "（" + new System.ComponentModel.Win32Exception(err).Message
                           + "；UAC 安全桌面、锁屏或对更高权限窗口注入时属已知限制）";
            return false;
        }
    }
}
