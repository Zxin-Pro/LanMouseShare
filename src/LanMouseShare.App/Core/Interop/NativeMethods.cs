using System;
using System.Runtime.InteropServices;

namespace LanMouseShare.Core.Interop
{
    /// <summary>
    /// Win32 API 声明（第一阶段只包含 DPI 感知相关入口）。
    ///
    /// 后续阶段会在同级继续追加：
    ///   NativeMethods.Hooks.cs    —— SetWindowsHookEx / CallNextHookEx / UnhookWindowsHookEx（WH_MOUSE_LL、WH_KEYBOARD_LL）
    ///   NativeMethods.Input.cs    —— SendInput 与 INPUT/MOUSEINPUT/KEYBDINPUT 结构体的 32/64 位统一布局
    ///   NativeMethods.Windows.cs  —— 窗口枚举、GetSystemMetrics、MonitorFromPoint
    ///
    /// 说明：AnyCPU 下 DllImport 不需要 32/64 位分支；
    /// 真正需要注意位数差异的是 SendInput 的 INPUT 结构体（后续阶段处理）。
    /// </summary>
    internal static class NativeMethods
    {
        /// <summary>
        /// DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2（Windows 10 1703 起可用）。
        /// 该值是 -4，必须用 IntPtr 传递而不是 int。
        /// </summary>
        internal static readonly IntPtr DpiAwarenessContextPerMonitorAwareV2 = new IntPtr(-4);

        /// <summary>PROCESS_SYSTEM_DPI_AWARE（Windows Vista 起）。</summary>
        internal const int ProcessSystemDpiAware = 1;

        /// <summary>PROCESS_PER_MONITOR_DPI_AWARE（Windows 8.1 起）。</summary>
        internal const int ProcessPerMonitorDpiAware = 2;

        /// <summary>HRESULT 成功码，shcore!SetProcessDpiAwareness 返回 0 表示成功。</summary>
        internal const int SOk = 0;

        // ---- 第三优先级：Windows Vista / Windows 7 ----

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetProcessDPIAware();

        /// <summary>
        /// 查询本进程当前是否为（系统级）DPI 感知。
        /// Windows Vista / 7 都有这个导出，用来在没有 shcore.dll 的 Win7 上回读感知状态。
        /// </summary>
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsProcessDPIAware();

        // ---- 第一优先级：Windows 10 1703+ ----

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetProcessDpiAwarenessContext(IntPtr dpiAwarenessContext);

        // ---- 第二优先级：Windows 8.1+（shcore.dll 在 Win7 上不存在）----

        [DllImport("shcore.dll", SetLastError = true)]
        internal static extern int SetProcessDpiAwareness(int dpiAwareness);

        // ---- 诊断用：读取调用前的 DPI 感知状态 ----

        [DllImport("kernel32.dll")]
        internal static extern IntPtr GetCurrentProcess();

        [DllImport("shcore.dll", SetLastError = true)]
        internal static extern int GetProcessDpiAwareness(IntPtr hProcess, out int awareness);

        /// <summary>
        /// 读取当前线程的 DPI 感知上下文（Windows 10 1607 起）。
        /// 返回伪句柄，其数值直接对应级别：
        ///   -1 = DPI_AWARENESS_CONTEXT_UNAWARE
        ///   -2 = DPI_AWARENESS_CONTEXT_SYSTEM_AWARE
        ///   -3 = DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE
        ///   -4 = DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2
        /// 这是唯一能精确区分「每显示器 V1 / V2」的公开 API，
        /// 因此 DpiAwareness 优先用它回读真实级别。
        /// </summary>
        [DllImport("user32.dll", SetLastError = true)]
        internal static extern IntPtr GetThreadDpiAwarenessContext();

        /// <summary>
        /// 比较两个 DPI 感知上下文是否等价（Windows 10 1607 起）。
        /// 实测教训：GetThreadDpiAwarenessContext 返回的是真正的句柄
        /// （在 Win11 26200 上实测得到 34 / 24592 这种值），
        /// 不是 -1/-2/-3/-4 这种伪值，所以**必须用本函数比较**，
        /// 直接比较数值会得到错误结论。
        /// </summary>
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool AreDpiAwarenessContextsEqual(IntPtr contextA, IntPtr contextB);

        // ==================================================================
        //  输入模拟（SendInput）—— 第三阶段：鼠标模拟 / 按键 / 滚轮代理
        // ==================================================================

        /// <summary>
        /// 注入输入事件。
        ///
        /// 32 位 / 64 位差异在这里被自动处理掉：INPUT 联合体里含 IntPtr
        /// (dwExtraInfo)，在 x86 上 Marshal.SizeOf(typeof(INPUT)) = 28，
        /// 在 x64 上 = 40 —— 这正是 Windows 各自期望的大小，
        /// 所以**不要**手工算偏移，只要用 IntPtr 就会自动对齐。
        /// 传错 cbSize 是 SendInput 失败最常见的原因。
        ///
        /// 返回值是**成功注入的事件数**，不是布尔值：网络/线程繁忙或
        /// 被 UIPI 拦截时会小于 nInputs。调用方必须检查它。
        /// </summary>
        [DllImport("user32.dll", SetLastError = true)]
        internal static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        /// <summary>
        /// 取系统度量。SM_CXSCREEN(0) / SM_CYSCREEN(1) 是主显示器尺寸，
        /// SM_XVIRTUALSCREEN(76) / SM_YVIRTUALSCREEN(77) /
        /// SM_CXVIRTUALSCREEN(78) / SM_CYVIRTUALSCREEN(79) 是整个虚拟桌面，
        /// 供第二阶段之后的多显示器预留接口使用。
        /// </summary>
        [DllImport("user32.dll")]
        internal static extern int GetSystemMetrics(int nIndex);

        /// <summary>把虚拟键码翻译成扫描码 / 虚拟键码（配合 KEYEVENTF_SCANCODE 使用）。</summary>
        [DllImport("user32.dll")]
        internal static extern uint MapVirtualKey(uint uCode, uint uMapType);

        // ---------------------------------------------------------------- 常量

        internal const int SmCxScreen = 0;
        internal const int SmCyScreen = 1;
        internal const int SmXVirtualScreen = 76;
        internal const int SmYVirtualScreen = 77;
        internal const int SmCxVirtualScreen = 78;
        internal const int SmCyVirtualScreen = 79;

        internal const uint MouseeventfMove = 0x0001;
        internal const uint MouseeventfLeftdown = 0x0002;
        internal const uint MouseeventfLeftup = 0x0004;
        internal const uint MouseeventfRightdown = 0x0008;
        internal const uint MouseeventfRightup = 0x0010;
        internal const uint MouseeventfMiddledown = 0x0020;
        internal const uint MouseeventfMiddleup = 0x0040;
        internal const uint MouseeventfXdown = 0x0080;
        internal const uint MouseeventfXup = 0x0100;
        internal const uint MouseeventfWheel = 0x0800;
        internal const uint MouseeventfHwheel = 0x1000;
        internal const uint MouseeventfAbsolute = 0x8000;
        internal const uint MouseeventfVirtualdesk = 0x4000;
        internal const uint MouseeventfMoveNocoalesce = 0x2000;

        internal const uint KeyeventfExtendedkey = 0x0001;
        internal const uint KeyeventfKeyup = 0x0002;
        internal const uint KeyeventfUnicode = 0x0004;
        internal const uint KeyeventfScancode = 0x0008;

        internal const uint InputMouse = 0;
        internal const uint InputKeyboard = 1;
        internal const uint InputHardware = 2;

        internal const uint MapvkVkToVsc = 0;
        internal const uint MapvkVkToChar = 2;

        // ---------------------------------------------------------------- 结构体

        [StructLayout(LayoutKind.Sequential)]
        internal struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public int mouseData;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct HARDWAREINPUT
        {
            public uint uMsg;
            public ushort wParamL;
            public ushort wParamH;
        }

        [StructLayout(LayoutKind.Explicit)]
        internal struct InputUnion
        {
            [FieldOffset(0)]
            public MOUSEINPUT mi;

            [FieldOffset(0)]
            public KEYBDINPUT ki;

            [FieldOffset(0)]
            public HARDWAREINPUT hi;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct INPUT
        {
            public uint type;
            public InputUnion u;
        }

        // ==================================================================
        //  光标定位 —— 第四阶段「重新居中」策略的基础
        // ==================================================================

        [StructLayout(LayoutKind.Sequential)]
        internal struct POINT
        {
            public int x;
            public int y;
        }

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetCursorPos(int x, int y);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetCursorPos(out POINT lpPoint);

        // ---- 光标显示与可见性（1.0.2：主机控制副机时要把自己的箭头藏起来）----
        //
        // ShowCursor 维护的是一个**显示计数**：每调一次 false 减 1，每调一次
        // true 加 1，只有计数为负光标才真的不画。别的程序可能已经把它加回去过，
        // 所以只能一路调到返回负值为止 —— 这也是几乎所有远控/多屏软件的做法。

        /// <summary>光标显示计数 +1（bShow=true）或 -1（false），返回调用后的新计数。</summary>
        [DllImport("user32.dll")]
        internal static extern int ShowCursor(bool bShow);

        internal const int CursorShowing = 0x00000001;

        /// <summary>
        /// 注意 cbSize 必须用 Marshal.SizeOf 现算：x86 = 20、x64 = 24，
        /// 写死常量正是本项目在 AnyCPU 下反复强调要避免的错误。
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct CURSORINFO
        {
            public int cbSize;
            public int flags;
            public IntPtr hCursor;
            public POINT ptScreenPos;
        }

        /// <summary>
        /// 查询光标真实状态（flags 含 CursorShowing 表示此刻确实画着光标）。
        /// 用它来**验证**隐藏有没有生效，而不是假设调用过就一定生效。
        /// </summary>
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetCursorInfo(ref CURSORINFO pci);

        // ==================================================================
        //  低级钩子 —— 第四阶段：鼠标/键盘捕获代理
        // ==================================================================

        internal const int WhKeyboardLl = 13;
        internal const int WhMouseLl = 14;

        internal const int HcAction = 0;

        internal const int WmMousemove = 0x0200;
        internal const int WmLbuttondown = 0x0201;
        internal const int WmLbuttonup = 0x0202;
        internal const int WmRbuttondown = 0x0204;
        internal const int WmRbuttonup = 0x0205;
        internal const int WmMbuttondown = 0x0207;
        internal const int WmMbuttonup = 0x0208;
        internal const int WmMousewheel = 0x020A;
        internal const int WmMousehwheel = 0x020E;
        internal const int WmXbuttondown = 0x020B;
        internal const int WmXbuttonup = 0x020C;

        internal const int WmKeydown = 0x0100;
        internal const int WmKeyup = 0x0101;
        internal const int WmSyskeydown = 0x0104;
        internal const int WmSyskeyup = 0x0105;

        /// <summary>KBDLLHOOKSTRUCT.flags：该事件由 SendInput 之类注入（含模拟注入）。</summary>
        internal const uint LlkhfExtended = 0x01;

        /// <summary>KBDLLHOOKSTRUCT.flags：事件来自更低完整性级别的注入。</summary>
        internal const uint LlkhfLowerIlInjected = 0x02;

        /// <summary>KBDLLHOOKSTRUCT.flags：事件由 SendInput / keybd_event 注入。</summary>
        internal const uint LlkhfInjected = 0x10;

        /// <summary>KBDLLHOOKSTRUCT.flags：抬起事件。</summary>
        internal const uint LlkhfUp = 0x80;

        [StructLayout(LayoutKind.Sequential)]
        internal struct MSLLHOOKSTRUCT
        {
            public POINT pt;
            public uint mouseData;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        internal delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

        internal delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        /// <summary>
        /// 安装钩子。低级钩子（WH_MOUSE_LL / WH_KEYBOARD_LL）会被绑定到
        /// **调用它的那个线程**上，而且必须有消息泵才会被回调 ——
        /// 所以必须在 WPF 的 UI 线程安装，不要在裸后台线程安装。
        ///
        /// 另一个必须知道的坑：回调超过 LowLevelHooksTimeout（默认 300ms）
        /// 系统会**静默摘掉钩子**，不报错。所以回调里只做内存判断，
        /// 任何网络/磁盘操作都必须丢给队列由别的线程做。
        /// </summary>
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("kernel32.dll")]
        internal static extern uint GetCurrentThreadId();

        // ------------------------------------------------------------ 窗口置顶与光标遮挡（1.0.5）

        /// <summary>SetWindowPos 的 hWndInsertAfter 取值：HWND_TOPMOST。</summary>
        internal static readonly IntPtr HwndTopmost = new IntPtr(-1);

        internal const int SwpNosize = 0x0001;
        internal const int SwpNomove = 0x0002;
        internal const int SwpNoactivate = 0x0010;

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
            int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool UpdateWindow(IntPtr hWnd);

        /// <summary>
        /// AnyCPU 下 GetWindowLong / SetWindowLong **必须分位数**：
        /// 32 位导出的是 GetWindowLongW（返回 4 字节），64 位导出的是 GetWindowLongPtrW（返回 8 字节），
        /// 名字看着一样但签名不同，按一个声明去调，在另一种位数上就是栈错位崩溃。
        /// 这里统一包一层，调用方不用关心位数。
        /// </summary>
        internal static int GetWindowLong(IntPtr hWnd, int nIndex)
        {
            if (IntPtr.Size == 8)
            {
                return (int)GetWindowLongPtr64(hWnd, nIndex);
            }

            return GetWindowLong32(hWnd, nIndex);
        }

        internal static int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong)
        {
            if (IntPtr.Size == 8)
            {
                return (int)SetWindowLongPtr64(hWnd, nIndex, new IntPtr(dwNewLong));
            }

            return SetWindowLong32(hWnd, nIndex, dwNewLong);
        }

        [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
        private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
        private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
    }
}
