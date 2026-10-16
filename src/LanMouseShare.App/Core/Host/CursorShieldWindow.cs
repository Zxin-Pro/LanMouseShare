using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using LanMouseShare.Core.Interop;

namespace LanMouseShare.Core.Host
{
    /// <summary>
    /// 「光标罩」——一块盖住整个主显示器、置顶、几乎完全透明的自家窗口。
    ///
    /// 为什么非要有它（这是 1.0.5 的核心修复，1.0.4 及以前的做法根本不成立）：
    ///
    /// ShowCursor 维护的显示计数是**按线程**记的，而且实测规则是——
    /// **光标当前压在哪个窗口上，就按那个窗口所属线程的计数决定画不画**。
    /// 提权实测（lms-p1/p2.ps1、lms-cursor2.ps1、lms-cursor3.ps1）：
    ///
    ///   A.over-our-window,     count=0   → GetCursorInfo flags=0x1 showing=True
    ///   B.over-our-window,     count=-3  → flags=0x0 showing=False   ☆藏住了
    ///   C.NOT-over-our-window, count=-3  → flags=0x1 showing=True    ☆负计数完全失效
    ///   D.全屏罩窗上任意位置,   count=-3  → flags=0x0 showing=False   ☆罩窗有效
    ///
    /// 也就是说：光在钩子线程上扣计数，只要光标压在别的进程的窗口上（桌面、
    /// 资源管理器、别的应用），那个负计数一点用都没有 —— 这正是用户反复报的
    /// 「主机的鼠标划过去 主机屏幕上还显示鼠标」。
    ///
    /// 罩窗把整屏盖住之后，光标无论跑到哪里都落在我们自己的窗口上，
    /// 于是「在我们 UI 线程上扣计数」就真能把箭头藏掉。
    ///
    /// 刻意**不加** WS_EX_TRANSPARENT：
    /// 点击穿透靠的是命中测试时跳过窗口，一旦跳过，光标又变成「压在别的窗口上」，
    /// 隐藏立刻失效（跟实验里的 C 行一个道理）。反过来，Remote 期间主机本机
    /// 的鼠标动作本来就要被低级钩子拦下转给副机，罩窗吃掉本机点击正是我们希望的行为。
    ///
    /// 不透明度取 2/255（约 0.8%）：足够让窗口「显示着」从而参与命中测试，
    /// 又完全看不出来（实测这个 alpha 下 WindowFromPoint 仍然返回罩窗自己）。
    /// </summary>
    internal sealed class CursorShieldWindow : Window
    {
        /// <summary>约 0.8% 不透明的黑，肉眼看不出来，但窗口确实在「显示」状态。</summary>
        private static readonly Color ShieldColor = Color.FromArgb(2, 0, 0, 0);

        private const int GwlExstyle = -20;
        private const int WsExNoactivate = 0x08000000;
        private const int WsExToolwindow = 0x00000080;
        private const int WsExLayered = 0x00080000;

        private bool _shown;

        public CursorShieldWindow()
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            AllowsTransparency = true;
            Focusable = false;
            IsHitTestVisible = true;
            Background = new SolidColorBrush(ShieldColor);
            WindowStartupLocation = WindowStartupLocation.Manual;
            Title = "LanMouseShare";
        }

        public bool IsShown
        {
            get { return _shown; }
        }

        /// <summary>盖住主显示器。必须在界面线程上调用。</summary>
        public void ShowShield()
        {
            if (_shown)
            {
                return;
            }

            int left = 0;
            int top = 0;
            int width = 0;
            int height = 0;
            try
            {
                left = Core.Input.VirtualScreen.VirtualLeft;
                top = Core.Input.VirtualScreen.VirtualTop;
                width = Core.Input.VirtualScreen.VirtualWidth;
                height = Core.Input.VirtualScreen.VirtualHeight;
            }
            catch (Exception)
            {
            }

            if (width <= 0 || height <= 0)
            {
                width = Core.Input.VirtualScreen.PrimaryWidth;
                height = Core.Input.VirtualScreen.PrimaryHeight;
                left = 0;
                top = 0;
            }

            Left = left;
            Top = top;
            Width = width;
            Height = height;

            // 先建句柄，才能在 Show 之前把扩展样式钉上（否则会抢焦点 / 进 Alt+Tab）
            IntPtr handle = new WindowInteropHelper(this).EnsureHandle();
            if (handle != IntPtr.Zero)
            {
                int ex = NativeMethods.GetWindowLong(handle, GwlExstyle);
                ex |= WsExNoactivate | WsExToolwindow | WsExLayered;
                NativeMethods.SetWindowLong(handle, GwlExstyle, ex);
            }

            Show();
            UpdateWindow(handle);
            _shown = true;
        }

        /// <summary>收起罩窗。必须在界面线程上调用。</summary>
        public void HideShield()
        {
            if (!_shown)
            {
                return;
            }

            _shown = false;
            try
            {
                Hide();
            }
            catch (Exception)
            {
            }
        }

        /// <summary>重按一遍置顶，防别的 TopMost 窗口爬到我们上面。</summary>
        public void ReassertTopmost()
        {
            if (!_shown)
            {
                return;
            }

            try
            {
                IntPtr handle = new WindowInteropHelper(this).Handle;
                if (handle != IntPtr.Zero)
                {
                    NativeMethods.SetWindowPos(handle, NativeMethods.HwndTopmost, 0, 0, 0, 0,
                        NativeMethods.SwpNomove | NativeMethods.SwpNosize | NativeMethods.SwpNoactivate);
                }
            }
            catch (Exception)
            {
            }
        }

        private static void UpdateWindow(IntPtr handle)
        {
            if (handle == IntPtr.Zero)
            {
                return;
            }

            try
            {
                NativeMethods.UpdateWindow(handle);
            }
            catch (Exception)
            {
            }
        }
    }
}
