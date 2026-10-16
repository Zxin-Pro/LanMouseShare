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
