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

        /// <summary>
        /// 进入副机时的落脚内缩像素数（防抖的第二个核心参数，1.0.2 增加）。
        ///
        /// 为什么必须有它（真机反馈修出来的）：如果落脚点就是副机的 x=0，
        /// 用户越过边缘之后手腕只要往回抖几像素（落在 ReturnGuard 以内），
        /// 立刻就被判成回程，表现是「刚划过去就弹回来，副机上根本没法用」。
        /// 落脚时先内缩这么多像素，回程就必须真的走完这一整段，
        /// 手抖再也触发不了切换；而靠近边缘的像素依然全部可达，
        /// 因为继续往回走仍然能走到 x=0，只是要走过这一段守卫带。
        /// </summary>
        public const int EntryGuard = 24;

        /// <summary>
        /// 贴合边的判定容差（像素）。
        ///
        /// 为什么不能要求光标「精确压在第 HostWidth-1 个像素上」：
        /// 绝对坐标注入（SendInput + MOUSEEVENTF_ABSOLUTE）的归一化是
        /// x = dx * 屏幕宽 / 65536，落点向下取整 —— 在 1920 宽的屏上最大只能
        /// 落到 1918，**永远够不到 1919**。某些鼠标驱动、远程桌面、虚拟机
        /// 也会把光标停在离边缘 1~2 像素的地方。要求精确贴边的直接后果就是
        /// 「用户怎么划都过不去」。容差 2 像素肉眼察觉不到，判定却稳得多。
        /// </summary>
        public const int EdgeTolerance = 2;

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
                    if (hostX < HostWidth - 1 - EdgeTolerance || dx <= 0)
                    {
                        return false;
                    }

                    // 从副机左边缘稍稍内缩落脚，理由见 EntryGuard 的说明
                    remoteX = Clamp(EntryGuard, 0, RemoteWidth - 1);
                    remoteY = Clamp(hostY - OffsetPixels, 0, RemoteHeight - 1);
                    return true;

                case ScreenSide.Left:
                    if (hostX > EdgeTolerance || dx >= 0)
                    {
                        return false;
                    }

                    remoteX = Clamp(RemoteWidth - 1 - EntryGuard, 0, RemoteWidth - 1);
                    remoteY = Clamp(hostY - OffsetPixels, 0, RemoteHeight - 1);
                    return true;

                case ScreenSide.Above:
                    if (hostY > EdgeTolerance || dy >= 0)
                    {
                        return false;
                    }

                    remoteX = Clamp(hostX - OffsetPixels, 0, RemoteWidth - 1);
                    remoteY = Clamp(RemoteHeight - 1 - EntryGuard, 0, RemoteHeight - 1);
                    return true;

                default: // Below
                    if (hostY < HostHeight - 1 - EdgeTolerance || dy <= 0)
                    {
                        return false;
                    }

                    remoteX = Clamp(hostX - OffsetPixels, 0, RemoteWidth - 1);
                    remoteY = Clamp(EntryGuard, 0, RemoteHeight - 1);
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
        /// 把虚拟副机坐标夹在一个**有界**的范围里。这是防「光标跑飞」的最后一道闸，
        /// 在每次累加完位移、做回程判定之前调用。
        ///
        /// 为什么必须夹：_virtualX/_virtualY 是累加出来的，只要有一次基准脱节
        /// 就会一路涨上去。而不夹的话，CoordinateMapper 内置的钳位会把超界值
        /// 饱和成 0 或 65535 —— 副机看到的就是「鼠标一划，光标窜到屏幕最上面
        /// 或最下面」，而且之后要往反方向挪很久才追得回来，手感上就是
        /// 「光标卡死在边上，副机根本没法操作」。
        ///
        /// 上下界**按屏幕相对位置分开定**：朝着主机的那条边是接口边，必须允许它
        /// 溢出一点（回程判定要求越过 ReturnGuard 像素），其余三条边都是外侧边，
        /// 一律夹在副机屏幕内。四条正好对应 TryExitRemote 里四个分支的阈值：
        ///   副机在右侧：左边是接口边，可到 -(ReturnGuard + 64)；右边夹在 RemoteWidth - 1
        ///   副机在左侧：右边是接口边，可到 RemoteWidth - 1 + ReturnGuard + 64；左边夹在 0
        ///   副机在下方：上边是接口边，可到 -(ReturnGuard + 64)；下边夹在 RemoteHeight - 1
        ///   副机在上方：下边是接口边，可到 RemoteHeight - 1 + ReturnGuard + 64；上边夹在 0
        ///
        /// 注意与 ClampRemote 的分工：ClampRemote 是「发送给副机时」用的窄夹紧（严格
        /// 夹在屏幕内），本方法是「做回程判定前」用的宽夹紧（允许接口边溢出）。
        /// 两者都存在是刻意的，不要合并。
        /// </summary>
        public void ClampVirtualToRemote(ref int remoteX, ref int remoteY)
        {
            int guard = ReturnGuard > 0 ? ReturnGuard : DefaultReturnGuard;
            int slack = guard + 64;

            int xLow = 0;
            int xHigh = RemoteWidth > 0 ? RemoteWidth - 1 : 4096;
            int yLow = 0;
            int yHigh = RemoteHeight > 0 ? RemoteHeight - 1 : 4096;

            switch (Side)
            {
                case ScreenSide.Right:
                    xLow = -slack;
                    break;

                case ScreenSide.Left:
                    xHigh += slack;
                    break;

                case ScreenSide.Above:
                    yHigh += slack;
                    break;

                default: // Below
                    yLow = -slack;
                    break;
            }

            remoteX = Clamp(remoteX, xLow, xHigh);
            remoteY = Clamp(remoteY, yLow, yHigh);
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
