using System;

namespace LanMouseShare.Core.Layout
{
    /// <summary>副机相对主机的摆放位置。</summary>
    public enum ScreenSide
    {
        Right = 0,
        Left = 1,
        Above = 2,
        Below = 3
    }

    /// <summary>
    /// 屏幕布局与边缘几何（第四阶段：屏幕布局 / 边缘切换 / 防抖三个代理的落点）。
    ///
    /// 模型：把两台机器拼成一个「合成桌面」。
    /// 主机永远占 [0,0,HW,HH]，副机按 Side + Offset 贴在旁边：
    ///   Right -> 副机在 (HW, Offset) 处
    ///   Left  -> 副机在 (-RW, Offset) 处
    ///   Above -> 副机在 (Offset, -RH) 处
    ///   Below -> 副机在 (Offset, HH) 处
    /// Offset 是沿贴合边方向的偏移像素（上下摆放时就是左右偏移）。
    ///
    /// 防抖（第四阶段：防抖代理）用的是两个物理量，不是时间：
    ///   1) 穿越必须伴随**朝向边缘的真实位移**（dx/dy 的方向要对）。
    ///      光标停在边缘不动、或者被我们停放在边缘，绝不会误触发 ——
    ///      这比「N 毫秒内不重复切换」的时间窗可靠得多：时间窗在
    ///      用户快速来回划的时候会漏掉合法切换，而方向判定不会。
    ///   2) 回程守卫 ReturnGuard：从副机回到主机后，主机光标被停放在
    ///      距边缘 ReturnGuard 像素处，必须再真实移动这么多像素才能
    ///      再次穿越。副机那边同样要求「越过边缘 ReturnGuard 像素」。
    ///      这条彻底消灭了「在边缘反复横跳」。
    /// </summary>
    public sealed class ScreenLayout
    {
        /// <summary>回程守卫像素数（防抖核心参数）。</summary>
        public const int DefaultReturnGuard = 4;

        /// <summary>重新居中触发区：主机光标进入距边缘这么多像素内就搬回中心。</summary>
        public const int RecenterMargin = 8;

        public ScreenSide Side { get; set; }

        /// <summary>沿贴合边方向的偏移像素（可正可负）。</summary>
        public int OffsetPixels { get; set; }

        /// <summary>回程守卫像素。</summary>
        public int ReturnGuard { get; set; }

        public int HostWidth { get; set; }

        public int HostHeight { get; set; }

        /// <summary>副机分辨率；未知时为 0，此时不允许穿越。</summary>
        public int RemoteWidth { get; set; }

        public int RemoteHeight { get; set; }

        public ScreenLayout()
        {
            Side = ScreenSide.Right;
            OffsetPixels = 0;
            ReturnGuard = DefaultReturnGuard;
        }

        public bool RemoteKnown
        {
            get { return RemoteWidth > 0 && RemoteHeight > 0; }
        }

        public bool IsHorizontal
        {
            get { return Side == ScreenSide.Left || Side == ScreenSide.Right; }
        }

        public string Describe()
        {
            string side;
            switch (Side)
            {
                case ScreenSide.Left: side = "左侧"; break;
                case ScreenSide.Above: side = "上方"; break;
                case ScreenSide.Below: side = "下方"; break;
                default: side = "右侧"; break;
            }

            return "副机在主机" + side + "，偏移 " + OffsetPixels + " 像素";
        }

        /// <summary>副机左上角在合成桌面里的 X（多显示器预留时也用这套坐标）。</summary>
        public int RemoteOriginX
        {
            get
            {
                switch (Side)
                {
                    case ScreenSide.Left: return -RemoteWidth;
                    case ScreenSide.Above:
                    case ScreenSide.Below: return OffsetPixels;
                    default: return HostWidth;
                }
            }
        }

        public int RemoteOriginY
        {
            get
            {
                switch (Side)
                {
                    case ScreenSide.Above: return -RemoteHeight;
                    case ScreenSide.Left:
                    case ScreenSide.Right: return OffsetPixels;
                    default: return HostHeight;
                }
            }
        }

        // ------------------------------------------------------------ 主机 -> 副机

        /// <summary>
        /// 判断主机光标是否应当穿出到副机。
        /// hostX/hostY 为主机光标物理像素；dx/dy 为本次移动增量（主机像素）。
        /// 命中时输出副机上的初始光标位置（副机像素）。
        /// </summary>
        public bool TryExitHost(int hostX, int hostY, int dx, int dy,
            out int remoteX, out int remoteY)
        {
            remoteX = 0;
            remoteY = 0;

            if (!RemoteKnown)
            {
                // 还不知道副机分辨率就不穿越：否则光标会落到不存在的位置
                return false;
            }

            switch (Side)
            {
                case ScreenSide.Right:
                    if (hostX < HostWidth - 1 || dx <= 0)
                    {
                        return false;
                    }

                    remoteX = 0;                                  // 从此机的左边缘进入
                    remoteY = Clamp(hostY - OffsetPixels, 0, RemoteHeight - 1);
                    return true;

                case ScreenSide.Left:
                    if (hostX > 0 || dx >= 0)
                    {
                        return false;
                    }

                    remoteX = RemoteWidth - 1;                    // 从此机的右边缘进入
                    remoteY = Clamp(hostY - OffsetPixels, 0, RemoteHeight - 1);
                    return true;

                case ScreenSide.Above:
                    if (hostY > 0 || dy >= 0)
                    {
                        return false;
                    }

                    remoteX = Clamp(hostX - OffsetPixels, 0, RemoteWidth - 1);
                    remoteY = RemoteHeight - 1;                   // 从此机的下边缘进入
                    return true;

                default: // Below
                    if (hostY < HostHeight - 1 || dy <= 0)
                    {
                        return false;
                    }

                    remoteX = Clamp(hostX - OffsetPixels, 0, RemoteWidth - 1);
                    remoteY = 0;                                  // 从此机的上边缘进入
                    return true;
            }
        }

        // ------------------------------------------------------------ 副机 -> 主机

        /// <summary>
        /// 判断副机光标是否应当穿回主机。
        /// remoteX/remoteY 是**未夹紧**的虚拟坐标，允许为负或超界 ——
        /// 只有真正越过边缘 ReturnGuard 像素才算回程，这就是防抖守卫。
        /// 命中时输出主机光标应当停放的位置（已包含守卫内缩）。
        /// </summary>
        public bool TryExitRemote(int remoteX, int remoteY, out int hostX, out int hostY)
        {
            hostX = 0;
            hostY = 0;

            switch (Side)
            {
                case ScreenSide.Right:
                    if (remoteX > -ReturnGuard)
                    {
                        return false;
                    }

                    hostX = HostWidth - 1 - ReturnGuard;
                    hostY = Clamp(remoteY + OffsetPixels, 0, HostHeight - 1);
                    return true;

                case ScreenSide.Left:
                    if (remoteX < RemoteWidth - 1 + ReturnGuard)
                    {
                        return false;
                    }

                    hostX = ReturnGuard;
                    hostY = Clamp(remoteY + OffsetPixels, 0, HostHeight - 1);
                    return true;

                case ScreenSide.Above:
                    if (remoteY < RemoteHeight - 1 + ReturnGuard)
                    {
                        return false;
                    }

                    hostY = ReturnGuard;
                    hostX = Clamp(remoteX + OffsetPixels, 0, HostWidth - 1);
                    return true;

                default: // Below
                    if (remoteY > -ReturnGuard)
                    {
                        return false;
                    }

                    hostY = HostHeight - 1 - ReturnGuard;
                    hostX = Clamp(remoteX + OffsetPixels, 0, HostWidth - 1);
                    return true;
            }
        }

        /// <summary>
        /// 把虚拟副机坐标夹紧到副机屏幕内，用于发送。
        /// 注意：返回的是夹紧后的值，但调用方保留未夹紧的原值用于回程判定。
        /// </summary>
        public void ClampRemote(ref int remoteX, ref int remoteY)
        {
            remoteX = Clamp(remoteX, 0, RemoteWidth - 1);
            remoteY = Clamp(remoteY, 0, RemoteHeight - 1);
        }

        /// <summary>
        /// 主机光标是否需要「重新居中」。为什么需要它：
        /// 底层钩子只能拿到光标坐标，位移要靠前后两次坐标相减。光标一旦
        /// 顶到屏幕边缘，系统会把坐标夹在边缘上，位移就再也读不出来 ——
        /// 表现为「鼠标划到副机边缘就卡住不动了」。
        /// 解法是在光标接近边缘时把它搬回屏幕中心，让位移永远有得读。
        /// 因为此时用户正在看另一块屏幕，这个搬动在视觉上是不可见的。
        /// </summary>
        public bool NeedsRecenter(int hostX, int hostY)
        {
            return hostX <= RecenterMargin
                   || hostY <= RecenterMargin
                   || hostX >= HostWidth - 1 - RecenterMargin
                   || hostY >= HostHeight - 1 - RecenterMargin;
        }

        public static int Clamp(int value, int min, int max)
        {
            if (value < min)
            {
                return min;
            }

            return value > max ? max : value;
        }

        /// <summary>
        /// 主机像素位移 -> 副机像素位移（等比缩放）。
        /// 分辨率不同时，主机上划过整屏的距离 = 副机上划过整屏的距离，
        /// 手感才一致；如果 1:1 照搬，高分辨率副机上会显得「慢半拍」。
        /// </summary>
        public int ScaleDelta(int delta, int hostExtent, int remoteExtent)
        {
            if (hostExtent <= 0 || remoteExtent <= 0 || delta == 0)
            {
                return 0;
            }

            long scaled = (long)delta * remoteExtent / hostExtent;
            if (scaled == 0 && delta != 0)
            {
                // 保底：极小位移不要被整除抹成 0，否则慢速移动会「粘住」
                return delta > 0 ? 1 : -1;
            }

            return (int)scaled;
        }
    }
}
