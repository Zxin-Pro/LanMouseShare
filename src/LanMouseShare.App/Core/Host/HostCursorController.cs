using System;
using System.Windows.Threading;
using LanMouseShare.Core.Input;
using LanMouseShare.Core.Interop;

namespace LanMouseShare.Core.Host
{
    /// <summary>
    /// 主机的「光标管家」：把本机那个会到处乱跑的箭头彻底收拾掉。
    ///
    /// 1.0.5 之前主机进副机模式时做的是「把箭头钉到屏幕正中心 + 在钩子线程上扣显示计数」，
    /// 实测两头都不对：
    ///
    ///   1) 计数按线程记、而且只有「光标当前压倒的那个窗口所属线程」的计数才作数，
    ///      所以压在桌面/资源管理器上时，钩子线程的负计数完全无效（见 CursorShieldWindow 注释）。
    ///   2) 只钉住不移走遮挡，视觉上箭头照样画在屏幕中间，用户原话就是
    ///      「主机的鼠标划过去 主机屏幕上还显示鼠标」。
    ///
    /// 这个类按正确的顺序把三件事一次做完：
    ///     ① 把整屏盖上一块几乎全透明的自家罩窗（让光标必然落在我们窗口上）
    ///     ② 立刻把光标搬到停放点（罩窗正中央，标志位只搬一次，避免连搬引发视频游戏视角乱转）
    ///     ③ 在界面线程（＝罩窗所属线程）上把显示计数扣成负数
    ///
    /// 恢复时严格逆序：先还计数、再撤罩窗。
    ///
    /// 所有方法都要求跑在界面线程上；调用方负责用 UiDispatch 兜住（见 HostController）。
    /// </summary>
    internal sealed class HostCursorController
    {
        /// <summary>点一下标志位能盖住的最小边距（像素），拖动时留这个余量可避开视频游戏误触。</summary>
        private const int StubMargin = 2;

        private readonly Dispatcher _dispatcher;
        private CursorShieldWindow _shield;
        private bool _engaged;
        private int _parkX;
        private int _parkY;

        public HostCursorController(Dispatcher dispatcher)
        {
            if (dispatcher == null)
            {
                throw new ArgumentNullException("dispatcher");
            }

            _dispatcher = dispatcher;
        }

        public bool IsEngaged
        {
            get { return _engaged; }
        }

        public int ParkX
        {
            get { return _parkX; }
        }

        public int ParkY
        {
            get { return _parkY; }
        }

        public bool ShieldVisible
        {
            get { return _shield != null && _shield.IsShown; }
        }

        /// <summary>
        /// 程序退出前的兜底：无论当前处于什么状态，都要把用户的箭头还回去。
        /// 少还一次显示计数，用户的鼠标箭头就再也回不来了（只能注销或重启）。
        /// </summary>
        public void ReleaseAll()
        {
            if (!_engaged)
            {
                return;
            }

            _engaged = false;

            int restored = 0;
            try
            {
                restored = InputInjector.ShowCursor();
            }
            catch (Exception)
            {
            }

            CursorShieldWindow shield = _shield;
            _shield = null;
            if (shield != null)
            {
                try
                {
                    shield.HideShield();
                    shield.Close();
                }
                catch (Exception)
                {
                }
            }

        }

        /// <summary>
        /// 重新固定停放点。每个鼠标事件都调一次会引发视频游戏视角乱转，
        /// 所以只在「进入副机」「窗口尺寸变化」「回到本机」这几个时刻调。
        /// </summary>
        public void UpdateParkPoint()
        {
            int width = Core.Input.VirtualScreen.PrimaryWidth;
            int height = Core.Input.VirtualScreen.PrimaryHeight;
            _parkX = width > 0 ? width / 2 : 0;
            _parkY = height > 0 ? height / 2 : 0;
        }

        /// <summary>把光标搬到停放点。返回是否搬成功。</summary>
        public bool ParkCursor()
        {
            if (_parkX <= 0 && _parkY <= 0)
            {
                UpdateParkPoint();
            }

            return NativeMethods.SetCursorPos(_parkX, _parkY);
        }

        /// <summary>
        /// 进入副机模式：盖罩窗 → 搬光标 → 扣计数。
        /// 返回扣掉的显示计数层数；0 表示这次没藏成（调用方应记日志）。
        /// </summary>
        public int Engage()
        {
            if (_engaged)
            {
                return 0;
            }

            _engaged = true;

            if (_shield == null)
            {
                _shield = new CursorShieldWindow();
            }

            _shield.ShowShield();
            UpdateParkPoint();
            ParkCursor();
            return InputInjector.HideCursor();
        }

        /// <summary>离开副机模式：还计数 → 撤罩窗。返回还回去的层数。</summary>
        public int Disengage()
        {
            if (!_engaged)
            {
                return 0;
            }

            _engaged = false;

            int restored = InputInjector.ShowCursor();

            CursorShieldWindow shield = _shield;
            _shield = null;
            if (shield != null)
            {
                try
                {
                    shield.HideShield();
                    shield.Close();
                }
                catch (Exception)
                {
                }
            }

            return restored;
        }

        /// <summary>防别的置顶窗口爬到罩窗上面（例如用户自己开了个 Topmost 播放器）。</summary>
        public void Reassert()
        {
            CursorShieldWindow shield = _shield;
            if (shield != null && _engaged)
            {
                shield.ReassertTopmost();
            }
        }

        /// <summary>停放点中心那块「戳一下」矩形，供自检断言使用。</summary>
        public int[] ParkRect()
        {
            if (_parkX <= 0 && _parkY <= 0)
            {
                UpdateParkPoint();
            }

            return new int[]
            {
                _parkX - StubMargin,
                _parkY - StubMargin,
                _parkX + StubMargin,
                _parkY + StubMargin
            };
        }
    }
}
