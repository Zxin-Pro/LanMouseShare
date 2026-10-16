using System;
using System.Runtime.InteropServices;

namespace LanMouseShare.Core.Interop
{
    /// <summary>进程当前的 DPI 感知级别。</summary>
    public enum DpiAwarenessMode
    {
        /// <summary>未设置（所有回退都失败，系统按 DPI 虚拟化处理）。</summary>
        NotSet = 0,
        /// <summary>系统级 DPI 感知，Windows Vista / Windows 7 可用的最高级别。</summary>
        SystemAware = 1,
        /// <summary>每显示器 DPI 感知，Windows 8.1 起。</summary>
        PerMonitorAware = 2,
        /// <summary>每显示器 DPI 感知 V2，Windows 10 1703 起。</summary>
        PerMonitorAwareV2 = 3
    }

    /// <summary>
    /// DPI 感知：清单静态声明 + 运行时三级回退 + 回读校验。
    ///
    /// 硬性约束（项目要求第 7 条）——运行时按下面的顺序回退：
    ///   1. Windows 10 1703+  -> user32!SetProcessDpiAwarenessContext(PER_MONITOR_AWARE_V2)
    ///   2. Windows 8.1 及以上 -> shcore!SetProcessDpiAwareness(PROCESS_PER_MONITOR_DPI_AWARE)
    ///   3. Windows 7 / Vista  -> user32!SetProcessDPIAware()
    ///
    /// 实测到的关键事实（对照实验产物在 C:\Users\zjh19\DSH\_dpi_probe）：
    ///   * 「无清单」或「只有 supportedOS」的进程，初始感知是 0（DPI_UNAWARE）；
    ///   * 但 WPF 程序即使清单里完全不声明 DPI，进程启动时也已经是 1（系统级），
    ///     此时两个更高级别的 API 全部返回 E_ACCESSDENIED(0x80070005 / Win32=5)；
    ///   * 感知级别在进程启动时一旦被设定就「锁死」，运行时 API 无法再提高；
    ///   * 更要命的是 SetProcessDPIAware() 在这种被锁死的情况下依然返回 true，
    ///     只信返回值会把「实际是每显示器 V2」误报成「系统级」。
    ///
    /// 因此本类做三件事：
    ///   1. 真实执行三级回退（满足硬性约束，覆盖「清单缺失 / 被外部装载器忽略」的场景）；
    ///   2. 每次 API 声称成功之后都**回读**一次真实级别，回读优先于返回值，报告不说假话；
    ///   3. 回读用三级手段，从最精确的开始：
    ///        GetThreadDpiAwarenessContext（Win10 1607+，能区分 V1/V2）
    ///        -> GetProcessDpiAwareness（Win8.1+，只能区分 未感知/系统级/每显示器）
    ///        -> IsProcessDPIAware（Vista+，只能区分 未感知/系统级）
    /// </summary>
    public static class DpiAwareness
    {
        private const int ErrorAccessDenied = 5;

        // DPI 感知上下文伪值（只用于「传入」参数）。
        // 实测教训：GetThreadDpiAwarenessContext 回读出来的是真正的句柄
        // （Win11 26200 上实测 34 / 24592），不是这些伪值，
        // 因此比较回读结果必须走 AreDpiAwarenessContextsEqual。
        private static readonly IntPtr CtxUnaware = new IntPtr(-1);
        private static readonly IntPtr CtxSystemAware = new IntPtr(-2);
        private static readonly IntPtr CtxPerMonitorAware = new IntPtr(-3);
        private static readonly IntPtr CtxPerMonitorAwareV2 = new IntPtr(-4);

        private static DpiAwarenessMode _appliedMode = DpiAwarenessMode.NotSet;
        private static string _appliedApi = "尚未调用";
        private static string _fallbackLog = string.Empty;
        private static string _initialAwareness = "尚未查询";
        private static int _initialAwarenessCode = -1;
        private static bool _attempted;

        /// <summary>最终生效的 DPI 感知级别。</summary>
        public static DpiAwarenessMode AppliedMode
        {
            get { return _appliedMode; }
        }

        /// <summary>最终生效的来源说明（API 名，或「清单声明 + 回读」）。</summary>
        public static string AppliedApi
        {
            get { return _appliedApi; }
        }

        /// <summary>回退过程中记录的原因链。</summary>
        public static string FallbackLog
        {
            get { return _fallbackLog; }
        }

        /// <summary>本进程调用任何 DPI API 之前的初始感知状态（可读文本）。</summary>
        public static string InitialAwareness
        {
            get { return _initialAwareness; }
        }

        /// <summary>本进程调用任何 DPI API 之前的初始感知状态编码（-1 表示读不到）。</summary>
        public static int InitialAwarenessCode
        {
            get { return _initialAwarenessCode; }
        }

        /// <summary>是否已经尝试过设置（进程生命周期内只尝试一次）。</summary>
        public static bool HasAttempted
        {
            get { return _attempted; }
        }

        /// <summary>
        /// 按 Win10 1703+ -> Win8.1+ -> Win7/Vista 的顺序设置 DPI 感知。
        /// 返回是否最终处于「已感知」状态。
        /// </summary>
        public static bool TryEnableBestAvailable()
        {
            if (_attempted)
            {
                return _appliedMode != DpiAwarenessMode.NotSet;
            }
            _attempted = true;
            _fallbackLog = string.Empty;
            _initialAwarenessCode = ReadBackCode();
            _initialAwareness = FormatAwareness(_initialAwarenessCode);

            // ---------- 第一优先级：Windows 10 1703+ ----------
            try
            {
                if (NativeMethods.SetProcessDpiAwarenessContext(NativeMethods.DpiAwarenessContextPerMonitorAwareV2))
                {
                    return Settle("user32!SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2)",
                                  DpiAwarenessMode.PerMonitorAwareV2);
                }
                AppendFallback("SetProcessDpiAwarenessContext(PER_MONITOR_AWARE_V2) 返回 false，Win32=" +
                               DescribeWin32Error(Marshal.GetLastWin32Error()));
            }
            catch (EntryPointNotFoundException)
            {
                AppendFallback("user32!SetProcessDpiAwarenessContext 入口不存在（系统低于 Windows 10 1703）");
            }
            catch (DllNotFoundException)
            {
                AppendFallback("user32.dll 加载失败");
            }

            // ---------- 第二优先级：Windows 8.1+ ----------
            try
            {
                int hr = NativeMethods.SetProcessDpiAwareness(NativeMethods.ProcessPerMonitorDpiAware);
                if (hr == NativeMethods.SOk)
                {
                    return Settle("shcore!SetProcessDpiAwareness(PROCESS_PER_MONITOR_DPI_AWARE)",
                                  DpiAwarenessMode.PerMonitorAware);
                }
                AppendFallback("SetProcessDpiAwareness(PER_MONITOR) 返回 HRESULT=0x" + hr.ToString("X8") +
                               DescribeHResult(hr));
            }
            catch (EntryPointNotFoundException)
            {
                AppendFallback("shcore!SetProcessDpiAwareness 入口不存在");
            }
            catch (DllNotFoundException)
            {
                AppendFallback("shcore.dll 不存在（系统低于 Windows 8.1，这正是 Win7 的情况）");
            }

            // ---------- 第三优先级：Windows 7 / Vista ----------
            try
            {
                if (NativeMethods.SetProcessDPIAware())
                {
                    return Settle("user32!SetProcessDPIAware()", DpiAwarenessMode.SystemAware);
                }
                AppendFallback("SetProcessDPIAware 返回 false，Win32=" +
                               DescribeWin32Error(Marshal.GetLastWin32Error()));
            }
            catch (EntryPointNotFoundException)
            {
                AppendFallback("user32!SetProcessDPIAware 入口不存在");
            }
            catch (DllNotFoundException)
            {
                AppendFallback("user32.dll 加载失败");
            }

            // ---------- 三级都失败 ----------
            return SettleByReadBack();
        }

        /// <summary>供界面与日志显示的一行描述。</summary>
        public static string Describe()
        {
            switch (_appliedMode)
            {
                case DpiAwarenessMode.PerMonitorAwareV2:
                    return "每显示器 V2（PerMonitorV2，Win10 1703+）· " + _appliedApi;
                case DpiAwarenessMode.PerMonitorAware:
                    return "每显示器（PerMonitor，Win8.1+）· " + _appliedApi;
                case DpiAwarenessMode.SystemAware:
                    return "系统级（SystemAware，Win7/Vista）· " + _appliedApi;
                default:
                    return "未设置（未感知 DPI，坐标会被系统缩放虚拟化）";
            }
        }

        /// <summary>回退原因链（单行）。</summary>
        public static string DescribeFallback()
        {
            if (_fallbackLog.Length == 0)
            {
                return "（命中）";
            }
            return _fallbackLog;
        }

        /// <summary>
        /// 某个 API 声称成功之后收尾：先回读真实级别，回读结果优先于返回值。
        /// 这样即使 SetProcessDPIAware() 在被锁死的情况下谎报 true，
        /// 也不会把「实际每显示器 V2」误报成「系统级」。
        /// </summary>
        private static bool Settle(string apiName, DpiAwarenessMode claimedMode)
        {
            int actual = ReadBackCode();

            // 级别只升不降：进程启动时的感知级别（通常来自 app.manifest）
            // 不会被一个谎报成功的低级别 API「降级」。
            int effective = actual;
            if (effective < _initialAwarenessCode)
            {
                effective = _initialAwarenessCode;
            }

            if (effective >= 0)
            {
                _appliedMode = ModeFromCode(effective);

                if (_initialAwarenessCode >= 1 && effective <= _initialAwarenessCode)
                {
                    // 启动时已感知、且本次调用并没有把级别抬上去 —— 真正生效的是清单。
                    _appliedApi = "由 app.manifest 在进程启动时锁定（" + apiName +
                                  " 返回成功但未提升级别，回读 = " + FormatAwareness(effective) + "）";
                }
                else
                {
                    _appliedApi = apiName + "（回读确认 = " + FormatAwareness(effective) + "）";
                }

                return _appliedMode != DpiAwarenessMode.NotSet;
            }

            _appliedMode = claimedMode;
            _appliedApi = apiName + "（回读不可用，按 API 返回值）";
            return true;
        }

        /// <summary>
        /// 三级 API 全部失败时收尾。
        /// E_ACCESSDENIED 说明「感知级别已在进程启动时被 app.manifest 锁定」，
        /// 此时真实级别是清单声明的级别，而不是「未设置」。
        /// </summary>
        private static bool SettleByReadBack()
        {
            int actual = ReadBackCode();
            if (actual < 0)
            {
                actual = _initialAwarenessCode;
            }

            if (actual >= 1)
            {
                _appliedMode = ModeFromCode(actual);
                _appliedApi = "由 app.manifest 在进程启动时锁定（回读 = " + FormatAwareness(actual) + "）";
                AppendFallback("运行时三级 API 均被拒绝，但回读确认级别已由清单生效");
                return true;
            }
            if (actual == 0)
            {
                _appliedMode = DpiAwarenessMode.NotSet;
                _appliedApi = "三级回退全部失败，且回读确认进程未感知 DPI";
                return false;
            }

            AppendFallback("无法回读真实感知级别，按 app.manifest 的 dpiAware 声明推断");
            _appliedMode = DpiAwarenessMode.SystemAware;
            _appliedApi = "按 app.manifest 的 dpiAware 声明推断（回读不可用）";
            return true;
        }

        /// <summary>
        /// 回读当前进程/线程的真实 DPI 感知级别，精确优先：
        ///   1. GetThreadDpiAwarenessContext（Win10 1607+，能区分 0/1/2/3）
        ///   2. GetProcessDpiAwareness     （Win8.1+，能区分 0/1/2）
        ///   3. IsProcessDPIAware          （Vista+，只能区分 0/1）
        /// 全部不可用时返回 -1。
        /// </summary>
        private static int ReadBackCode()
        {
            try
            {
                IntPtr ctx = NativeMethods.GetThreadDpiAwarenessContext();
                // 实测教训：本函数返回的是真正的句柄（Win11 26200 上实测 34 / 24592），
                // 不是 -1/-2/-3/-4 这类伪值，所以必须用 AreDpiAwarenessContextsEqual
                // 比较；直接比数值永远是错的（会一路落到 GetProcessDpiAwareness，
                // 从而分不清 PerMonitor V1 与 V2）。
                if (NativeMethods.AreDpiAwarenessContextsEqual(ctx, CtxPerMonitorAwareV2)) { return 3; }
                if (NativeMethods.AreDpiAwarenessContextsEqual(ctx, CtxPerMonitorAware)) { return 2; }
                if (NativeMethods.AreDpiAwarenessContextsEqual(ctx, CtxSystemAware)) { return 1; }
                if (NativeMethods.AreDpiAwarenessContextsEqual(ctx, CtxUnaware)) { return 0; }
            }
            catch (EntryPointNotFoundException)
            {
            }
            catch (DllNotFoundException)
            {
            }

            try
            {
                int awareness;
                int hr = NativeMethods.GetProcessDpiAwareness(NativeMethods.GetCurrentProcess(), out awareness);
                if (hr == NativeMethods.SOk && awareness >= 0 && awareness <= 2)
                {
                    return awareness;
                }
            }
            catch (EntryPointNotFoundException)
            {
            }
            catch (DllNotFoundException)
            {
            }

            try
            {
                return NativeMethods.IsProcessDPIAware() ? 1 : 0;
            }
            catch (EntryPointNotFoundException)
            {
            }
            catch (DllNotFoundException)
            {
            }

            return -1;
        }

        private static DpiAwarenessMode ModeFromCode(int code)
        {
            switch (code)
            {
                case 3: return DpiAwarenessMode.PerMonitorAwareV2;
                case 2: return DpiAwarenessMode.PerMonitorAware;
                case 1: return DpiAwarenessMode.SystemAware;
                default: return DpiAwarenessMode.NotSet;
            }
        }

        private static string FormatAwareness(int code)
        {
            switch (code)
            {
                case 0:
                    return "0 = DPI_UNAWARE（未感知）";
                case 1:
                    return "1 = SYSTEM_AWARE（系统级）";
                case 2:
                    return "2 = PER_MONITOR_AWARE（每显示器）";
                case 3:
                    return "3 = PER_MONITOR_AWARE_V2（每显示器 V2）";
                default:
                    return "无法回读（shcore 与 user32 的回读入口都不可用）";
            }
        }

        private static string DescribeWin32Error(int code)
        {
            if (code == ErrorAccessDenied)
            {
                return "5(ERROR_ACCESS_DENIED：感知级别已被提前锁定)";
            }
            return code.ToString();
        }

        private static string DescribeHResult(int hr)
        {
            // 0x80070005 = E_ACCESSDENIED
            if ((uint)hr == 0x80070005u)
            {
                return "（E_ACCESSDENIED：感知级别已被提前锁定）";
            }
            return string.Empty;
        }

        private static void AppendFallback(string text)
        {
            if (_fallbackLog.Length > 0)
            {
                _fallbackLog += " -> ";
            }
            _fallbackLog += text;
        }
    }
}
