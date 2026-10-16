using System;
using LanMouseShare.Core.Interop;

namespace LanMouseShare.Core.Input
{
    /// <summary>
    /// 屏幕度量（第三阶段：坐标映射代理的前置）。
    ///
    /// 注意 DPI：进程已声明为每显示器 DPI 感知（PerMonitorV2），
    /// 所以 GetSystemMetrics 返回的是**物理像素**，与 SendInput 的
    /// 绝对坐标归一化空间一致，不需要再做 DPI 换算。
    /// 如果哪天有人去掉 app.manifest 里的 dpiAware 声明，这里就会开始出错。
    /// </summary>
    public static class VirtualScreen
    {
        public static int PrimaryWidth
        {
            get
            {
                int w = NativeMethods.GetSystemMetrics(NativeMethods.SmCxScreen);
                return w > 0 ? w : 1920;
            }
        }

        public static int PrimaryHeight
        {
            get
            {
                int h = NativeMethods.GetSystemMetrics(NativeMethods.SmCyScreen);
                return h > 0 ? h : 1080;
            }
        }

        public static int VirtualLeft
        {
            get { return NativeMethods.GetSystemMetrics(NativeMethods.SmXVirtualScreen); }
        }

        public static int VirtualTop
        {
            get { return NativeMethods.GetSystemMetrics(NativeMethods.SmYVirtualScreen); }
        }

        public static int VirtualWidth
        {
            get
            {
                int w = NativeMethods.GetSystemMetrics(NativeMethods.SmCxVirtualScreen);
                return w > 0 ? w : PrimaryWidth;
            }
        }

        public static int VirtualHeight
        {
            get
            {
                int h = NativeMethods.GetSystemMetrics(NativeMethods.SmCyVirtualScreen);
                return h > 0 ? h : PrimaryHeight;
            }
        }

        public static bool IsMultiMonitor
        {
            get { return VirtualWidth > PrimaryWidth || VirtualHeight > PrimaryHeight; }
        }

        public static string Describe()
        {
            return PrimaryWidth + "x" + PrimaryHeight
                   + (IsMultiMonitor
                       ? "（虚拟桌面 " + VirtualWidth + "x" + VirtualHeight + "，多显示器预留）"
                       : "（单显示器）");
        }
    }
}
