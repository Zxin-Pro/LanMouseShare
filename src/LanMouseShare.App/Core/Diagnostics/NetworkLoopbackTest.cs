using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using LanMouseShare.Core.Input;
using LanMouseShare.Core.Interop;
using LanMouseShare.Core.Layout;
using LanMouseShare.Core.Net;

namespace LanMouseShare.Core.Diagnostics
{
    /// <summary>
    /// 回环联调自检（「测试代理」在第七阶段的核心产出）。
    ///
    /// 为什么要有它：真正跨两台电脑手工验收一次，需要两台机器、两台显示器、一双手。
    /// 而这个测试在**同一台机器**上用回环地址把「握手 → 认证 → 屏幕信息 → 各种输入帧」
    /// 整条链路跑一遍，一次运行 5 秒内出结论。
    ///
    /// 它**不装钩子、不拦截输入**。唯一会碰真实机器的地方是 CheckInjection：
    /// 它故意真的搬一下光标，用来验证「副机能否被操作」这条命脉，跑完立刻还原原位。
    /// </summary>
    public static class NetworkLoopbackTest
    {
        private const string GoodKey = "lms-loopback-test-key";
        private const string BadKey = "lms-wrong-key";

        /// <summary>收集对端发来的帧，事件回调在后台线程，必须加锁。</summary>
        private sealed class Recorder
        {
            private readonly object _gate = new object();
            private readonly List<Frame> _frames = new List<Frame>();

            public int AuthAccepted;
            public int AuthRejected;
            public int StatusMessages;

            public void OnFrame(Frame frame)
            {
                if (frame == null)
                {
                    return;
                }
                lock (_gate)
                {
                    _frames.Add(frame);
                }
            }

            public void OnStatus(string text)
            {
                Interlocked.Increment(ref StatusMessages);
            }

            public List<Frame> Snapshot()
            {
                lock (_gate)
                {
                    return new List<Frame>(_frames);
                }
            }

            public Frame Find(string type)
            {
                lock (_gate)
                {
                    for (int i = 0; i < _frames.Count; i++)
                    {
                        if (string.Equals(_frames[i].type, type, StringComparison.Ordinal))
                        {
                            return _frames[i];
                        }
                    }
                }
                return null;
            }

            public bool WaitFor(string type, int timeoutMs)
            {
                DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
                while (DateTime.UtcNow < deadline)
                {
                    if (Find(type) != null)
                    {
                        return true;
                    }
                    Thread.Sleep(10);
                }
                return Find(type) != null;
            }
        }

        /// <summary>跑完整套回环联调，返回可读报告（最后一行是 NETTEST RESULT）。</summary>
        public static string Run(int timeoutMs)
        {
            if (timeoutMs < 2000)
            {
                timeoutMs = 2000;
            }

            List<string> lines = new List<string>();
            int passed = 0;
            int total = 0;

            Action<string, bool, string> check = delegate (string name, bool ok, string detail)
            {
                total++;
                if (ok)
                {
                    passed++;
                }
                lines.Add("  [" + (ok ? "PASS" : "FAIL") + "] " + name + " —— " + detail);
            };

            lines.Add("回环联调开始（超时基准 " + timeoutMs + " 毫秒）");

            // ---------------------------------------------------------- 坐标映射
            CheckCoordinateMapper(check);

            // ---------------------------------------------------------- 协议编解码
            CheckProtocolCodec(check);

            // -------------------------------------------------- 副机侧输入注入（动真实光标）
            // 这一项会真的搬动光标，跑完会还原，所以放在网络部分之前，避免干扰后续判断。
            CheckInjection(check);

            // ---------------------------------------------------------- 正确密钥链路
            int port = FindFreePort();
            check("能找到空闲的本地端口", port > 0, "端口 " + port);

            PeerConnection host = null;
            PeerConnection client = null;
            PeerConnection badHost = null;
            PeerConnection badClient = null;

            try
            {
                Recorder hostSide = new Recorder();
                Recorder clientSide = new Recorder();

                host = new PeerConnection();
                host.FrameReceived += hostSide.OnFrame;
                host.StatusChanged += hostSide.OnStatus;
                host.Authenticated += delegate (string text) { Interlocked.Increment(ref hostSide.AuthAccepted); };
                host.Configure(PeerRole.Host, "127.0.0.1", port, GoodKey);
                host.Start(false);

                client = new PeerConnection();
                client.FrameReceived += clientSide.OnFrame;
                client.StatusChanged += clientSide.OnStatus;
                client.Authenticated += delegate (string text) { Interlocked.Increment(ref clientSide.AuthAccepted); };
                client.Configure(PeerRole.Client, "127.0.0.1", port, GoodKey);
                client.Start(false);

                bool connected = WaitUntil(delegate { return host.IsConnected && client.IsConnected; }, timeoutMs);
                check("主机与副机建立 TCP 连接", connected,
                    "主机 IsConnected=" + host.IsConnected + "，副机 IsConnected=" + client.IsConnected);

                bool authed = WaitUntil(delegate { return host.IsAuthenticated && client.IsAuthenticated; }, timeoutMs);
                check("双方完成密钥认证", authed,
                    "主机 IsAuthenticated=" + host.IsAuthenticated + "，副机 IsAuthenticated=" + client.IsAuthenticated +
                    "，认证回调次数 " + hostSide.AuthAccepted + "/" + clientSide.AuthAccepted);

                if (authed)
                {
                    // 副机 -> 主机：屏幕信息
                    Frame screenInfo = new Frame();
                    screenInfo.type = Msg.ScreenInfo;
                    screenInfo.screenWidth = 1920;
                    screenInfo.screenHeight = 1080;
                    client.SendAuthenticated(screenInfo);

                    bool gotScreen = hostSide.WaitFor(Msg.ScreenInfo, timeoutMs);
                    Frame received = hostSide.Find(Msg.ScreenInfo);
                    check("screen_info 帧送达主机",
                        gotScreen && received != null && received.screenWidth == 1920 && received.screenHeight == 1080,
                        gotScreen ? (received.screenWidth + "×" + received.screenHeight) : "未收到");

                    // 副机 -> 主机：鼠标移动（归一化坐标必须逐位不变）
                    Frame move = new Frame();
                    move.type = Msg.MouseMove;
                    move.nx = 32768;
                    move.ny = 16384;
                    client.SendAuthenticated(move);

                    bool gotMove = hostSide.WaitFor(Msg.MouseMove, timeoutMs);
                    Frame moveBack = hostSide.Find(Msg.MouseMove);
                    check("mouse_move 帧的归一化坐标逐位不变",
                        gotMove && moveBack != null && moveBack.nx == 32768 && moveBack.ny == 16384,
                        gotMove ? ("nx=" + moveBack.nx + " ny=" + moveBack.ny) : "未收到");

                    // 副机 -> 主机：鼠标按键
                    Frame button = new Frame();
                    button.type = Msg.MouseButton;
                    button.button = "left";
                    button.down = true;
                    client.SendAuthenticated(button);

                    bool gotButton = hostSide.WaitFor(Msg.MouseButton, timeoutMs);
                    Frame buttonBack = hostSide.Find(Msg.MouseButton);
                    check("mouse_button 帧的按键与按下状态正确",
                        gotButton && buttonBack != null && buttonBack.button == "left" && buttonBack.down,
                        gotButton ? (buttonBack.button + " down=" + buttonBack.down) : "未收到");

                    // 副机 -> 主机：滚轮（含水平滚轮标志）
                    Frame wheel = new Frame();
                    wheel.type = Msg.MouseWheel;
                    wheel.wheel = -120;
                    wheel.horizontal = true;
                    client.SendAuthenticated(wheel);

                    bool gotWheel = hostSide.WaitFor(Msg.MouseWheel, timeoutMs);
                    Frame wheelBack = hostSide.Find(Msg.MouseWheel);
                    check("mouse_wheel 帧的滚动量与水平标志正确",
                        gotWheel && wheelBack != null && wheelBack.wheel == -120 && wheelBack.horizontal,
                        gotWheel ? ("wheel=" + wheelBack.wheel + " horizontal=" + wheelBack.horizontal) : "未收到");

                    // 副机 -> 主机：键盘
                    Frame key = new Frame();
                    key.type = Msg.Key;
                    key.vk = 0x41;
                    key.scan = 0x1E;
                    key.extended = false;
                    key.down = true;
                    client.SendAuthenticated(key);

                    bool gotKey = hostSide.WaitFor(Msg.Key, timeoutMs);
                    Frame keyBack = hostSide.Find(Msg.Key);
                    check("key 帧的虚拟键码、扫描码、按下状态正确",
                        gotKey && keyBack != null && keyBack.vk == 0x41 && keyBack.scan == 0x1E && keyBack.down,
                        gotKey ? ("vk=0x" + keyBack.vk.ToString("X") + " scan=0x" + keyBack.scan.ToString("X") + " down=" + keyBack.down) : "未收到");

                    // 主机 -> 副机：反向也要通
                    Frame back = new Frame();
                    back.type = Msg.MouseMove;
                    back.nx = 1111;
                    back.ny = 2222;
                    host.SendAuthenticated(back);

                    bool gotBack = clientSide.WaitFor(Msg.MouseMove, timeoutMs);
                    Frame backFrame = clientSide.Find(Msg.MouseMove);
                    check("主机 -> 副机方向同样通畅", gotBack && backFrame != null && backFrame.nx == 1111,
                        gotBack ? ("nx=" + backFrame.nx + " ny=" + backFrame.ny) : "未收到");

                    // ------------------------------------------------------ 发送泵
                    CheckFramePump(check, client, hostSide, timeoutMs);

                    // ------------------------------------------------------ 心跳与断线
                    CheckHeartbeat(check, host, client, timeoutMs);
                }
            }
            catch (Exception ex)
            {
                check("回环联调未抛出异常", false, ex.GetType().Name + "：" + ex.Message);
            }
            finally
            {
                SafeStop(host);
                SafeStop(client);

                // ------------------------------------------------------ 错误密钥
                int badPort = FindFreePort();
                try
                {
                    Recorder badRecorder = new Recorder();

                    badHost = new PeerConnection();
                    badHost.Configure(PeerRole.Host, "127.0.0.1", badPort, GoodKey);
                    badHost.Start(false);

                    badClient = new PeerConnection();
                    badClient.FrameReceived += badRecorder.OnFrame;
                    badClient.Authenticated += delegate (string text) { Interlocked.Increment(ref badRecorder.AuthAccepted); };
                    badClient.Configure(PeerRole.Client, "127.0.0.1", badPort, BadKey);
                    badClient.Start(false);

                    int wait = Math.Min(3000, timeoutMs);
                    bool wronglyAccepted = WaitUntil(delegate { return badClient.IsAuthenticated || badRecorder.AuthAccepted > 0; }, wait);
                    check("错误密钥的连接被拒绝", !wronglyAccepted,
                        wronglyAccepted ? "严重问题：错误密钥竟然通过认证" : "在 " + wait + " 毫秒内未通过认证，符合预期");
                }
                catch (Exception ex)
                {
                    check("错误密钥的连接被拒绝", false, "测试本身出错：" + ex.Message);
                }
                finally
                {
                    SafeStop(badHost);
                    SafeStop(badClient);
                }
            }

            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < lines.Count; i++)
            {
                sb.AppendLine(lines[i]);
            }
            sb.AppendLine("NETTEST RESULT: " + (passed == total ? "PASS" : "FAIL") + " (" + passed + "/" + total + ")");
            return sb.ToString();
        }

        /// <summary>
        /// 心跳保活与断线检测。合并成一个方法，因为两者用的是同一个超时窗口。
        ///
        /// 关键事实（写断言前必须知道）：PeerConnection 在 OnTransportFrame 里对 Msg.Ping
        /// 自动回 Msg.Pong，对 Msg.Pong 只刷新 _lastReceivedMs —— 两者都**不会**冒泡给
        /// FrameReceived 事件。所以不能用「收到 ping 帧」来断言心跳，那永远会失败。
        /// 正确的观测方式：自己发的 ping 不会刷新自己的 _lastReceivedMs，只有对方回的 pong
        /// 才会；所以只要连续静默超过 TimeoutMs(5000 毫秒) 还不断线，就证明回执链路是通的。
        /// </summary>
        private static void CheckHeartbeat(Action<string, bool, string> check, PeerConnection host,
            PeerConnection client, int timeoutMs)
        {
            try
            {
                const int quietMs = 6000;
                Thread.Sleep(quietMs);
                bool alive = host.IsConnected && client.IsConnected;
                check("心跳保活：静默 " + quietMs + " 毫秒（超过 5000 毫秒超时窗口）后双方仍然连接",
                    alive,
                    "主机 IsConnected=" + host.IsConnected + "，副机 IsConnected=" + client.IsConnected +
                    (alive ? "（ping / pong 双向都在工作）" : "（pong 回执或超时判定有问题）"));

                // 断线检测：副机崩溃或网线拔掉时，主机必须自己察觉到 —— 否则光标永远留在
                // 副机上，本机等于彻底失控。这是整个项目里最不能出错的一条。
                client.Stop();
                int wait = timeoutMs + 4000;
                bool noticed = WaitUntil(delegate { return !host.IsConnected; }, wait);
                check("断线检测：副机断开后主机在 " + wait + " 毫秒内察觉到",
                    noticed,
                    noticed ? "主机已判定断开" : "主机仍然 IsConnected=True（断线检测失效）");
            }
            catch (Exception ex)
            {
                check("心跳与断线检测", false, ex.GetType().Name + "：" + ex.Message);
            }
        }

        // ------------------------------------------------------------ 各个子测试

        private static void CheckCoordinateMapper(Action<string, bool, string> check)
        {
            try
            {
                int nx = CoordinateMapper.PixelsToNormalized(500, 1920);
                int ny = CoordinateMapper.PixelsToNormalized(300, 1080);
                int backX = CoordinateMapper.NormalizedToPixels(nx, 1920);
                int backY = CoordinateMapper.NormalizedToPixels(ny, 1080);

                check("像素 -> 归一化 -> 像素 往返误差不超过 1 像素",
                    Math.Abs(backX - 500) <= 1 && Math.Abs(backY - 300) <= 1,
                    "500->" + nx + "->" + backX + "，300->" + ny + "->" + backY);

                int minX = CoordinateMapper.NormalizedToPixels(0, 1920);
                int maxX = CoordinateMapper.NormalizedToPixels(CoordinateMapper.NormalizedMax, 1920);
                check("归一化边界映射到屏幕两端",
                    minX == 0 && maxX >= 1919,
                    "0 -> " + minX + "，" + CoordinateMapper.NormalizedMax + " -> " + maxX);
            }
            catch (Exception ex)
            {
                check("坐标映射可用", false, ex.GetType().Name + "：" + ex.Message);
            }
        }

        private static void CheckProtocolCodec(Action<string, bool, string> check)
        {
            try
            {
                ProtocolCodec codec = new ProtocolCodec();
                Frame frame = new Frame();
                frame.type = Msg.MouseMove;
                frame.nx = 12345;
                frame.ny = 54321;

                byte[] encoded = codec.Encode(frame);
                int dropped;
                List<Frame> decoded = codec.Append(encoded, 0, encoded.Length, out dropped);

                check("JSON 行协议编码 -> 解码往返正确",
                    decoded.Count == 1 && decoded[0].nx == 12345 && decoded[0].ny == 54321 && dropped == 0,
                    "编码 " + encoded.Length + " 字节，解出 " + decoded.Count + " 帧，丢弃 " + dropped + " 字节");

                // 粘包：两帧拼在一起要能一次解出两帧
                ProtocolCodec codec2 = new ProtocolCodec();
                byte[] a = codec2.Encode(frame);
                byte[] b = codec2.Encode(frame);
                byte[] joined = new byte[a.Length + b.Length];
                Buffer.BlockCopy(a, 0, joined, 0, a.Length);
                Buffer.BlockCopy(b, 0, joined, a.Length, b.Length);
                int dropped2;
                List<Frame> two = codec2.Append(joined, 0, joined.Length, out dropped2);
                check("粘包（两帧一次到达）能完整解出两帧", two.Count == 2 && dropped2 == 0,
                    "解出 " + two.Count + " 帧，丢弃 " + dropped2 + " 字节");

                // 乱码不能让解析器抛异常
                ProtocolCodec codec3 = new ProtocolCodec();
                byte[] garbage = Encoding.UTF8.GetBytes("这不是 JSON\r\n{\"type\":\"mouse_move\"\r\n\u0000\u0001\u0002\r\n");
                int dropped3;
                List<Frame> fromGarbage = codec3.Append(garbage, 0, garbage.Length, out dropped3);
                check("遇到乱码不抛异常、不崩溃（丢弃并继续）",
                    fromGarbage != null && dropped3 >= 0,
                    "从乱码中解出 " + fromGarbage.Count + " 帧，丢弃 " + dropped3 + " 字节");
            }
            catch (Exception ex)
            {
                check("协议编解码健壮性", false, ex.GetType().Name + "：" + ex.Message);
            }
        }

        private static void CheckFramePump(Action<string, bool, string> check, PeerConnection client,
            Recorder hostSide, int timeoutMs)
        {
            try
            {
                FramePump pump = new FramePump(client);
                pump.Start();

                const int count = 200;
                for (int i = 0; i < count; i++)
                {
                    Frame f = new Frame();
                    f.type = Msg.MouseMove;
                    f.nx = i;
                    f.ny = i;
                    pump.Post(f);
                }

                // 最后一帧必须完整到达：合并可以丢中间态，绝不能丢最终位置。
                Frame last = new Frame();
                last.type = Msg.MouseMove;
                last.nx = 12345;
                last.ny = 54321;
                pump.Post(last);

                bool delivered = WaitUntil(delegate
                {
                    Frame got = hostSide.Find(Msg.MouseMove);
                    // 找到 nx==12345 的那一帧才算数
                    List<Frame> all = hostSide.Snapshot();
                    for (int i = 0; i < all.Count; i++)
                    {
                        if (all[i].type == Msg.MouseMove && all[i].nx == 12345 && all[i].ny == 54321)
                        {
                            return true;
                        }
                    }
                    return false;
                }, timeoutMs);

                check("发送泵合并后仍能保证最终位置送达", delivered,
                    delivered ? "最后一个位置 12345,54321 已送达" : "最终位置丢失（合并逻辑有 bug）");

                long accounted = pump.SentCount + pump.CoalescedCount + pump.DroppedCount + pump.FailedCount;
                check("发送泵计数守恒（发出+合并+丢弃+失败 == 提交数）",
                    accounted == count + 1,
                    "提交 " + (count + 1) + "，发出 " + pump.SentCount + "，合并 " + pump.CoalescedCount +
                    "，丢弃 " + pump.DroppedCount + "，失败 " + pump.FailedCount +
                    (string.IsNullOrEmpty(pump.LastError) ? string.Empty : "，最后错误：" + pump.LastError));

                pump.Dispose();
            }
            catch (Exception ex)
            {
                check("发送泵可用", false, ex.GetType().Name + "：" + ex.Message);
            }
        }

        // ------------------------------------------------------------ 工具

        private static bool WaitUntil(Func<bool> condition, int timeoutMs)
        {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    if (condition())
                    {
                        return true;
                    }
                }
                catch (Exception)
                {
                }
                Thread.Sleep(20);
            }
            try
            {
                return condition();
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// 副机侧注入实测：真的调 SendInput 把光标搬过去，再用 GetCursorPos 核对落点。
        ///
        /// 为什么非要有这一项：1.0.4 之前整个自检体系都只验「帧发出去了、帧收到了、坐标数值没变」，
        /// 从来没有任何一条断言验过「副机上的光标到底动了没有」。用户的反馈
        /// 「根本不能发在副屏操作」恰好落在这个真空里 —— 帧是对的，注入可能是错的。
        ///
        /// 这一项会把用户的光标搬走两下（约 120 毫秒），跑完立刻还原原位置。
        /// 落点判据取 ±2 像素：绝对坐标注入本身允许 1 像素的取整差，
        /// 再考虑「鼠标指针精度增强」在绝对定位下不生效，2 像素足够宽松又足够严格。
        /// </summary>
        private static void CheckInjection(Action<string, bool, string> check)
        {
            int width = VirtualScreen.PrimaryWidth > 0 ? VirtualScreen.PrimaryWidth : 1920;
            int height = VirtualScreen.PrimaryHeight > 0 ? VirtualScreen.PrimaryHeight : 1080;

            NativeMethods.POINT original;
            bool haveOriginal = NativeMethods.GetCursorPos(out original);

            try
            {
                // (1) 屏幕正中
                int midX = width / 2;
                int midY = height / 2;
                int nx = CoordinateMapper.PixelsToNormalized(midX, width);
                int ny = CoordinateMapper.PixelsToNormalized(midY, height);
                bool sent = InputInjector.MoveAbsolute(nx, ny);
                Thread.Sleep(80);

                NativeMethods.POINT landed;
                bool read = NativeMethods.GetCursorPos(out landed);
                int wantX = CoordinateMapper.NormalizedToPixels(nx, width);
                int wantY = CoordinateMapper.NormalizedToPixels(ny, height);
                int errX = Math.Abs(landed.x - wantX);
                int errY = Math.Abs(landed.y - wantY);

                check("副机注入：绝对坐标真的把光标搬到了目标位置",
                    sent && read && errX <= 2 && errY <= 2,
                    "发送 " + sent.ToString() + "，目标 (" + wantX + "," + wantY + ")，实测 ("
                    + landed.x + "," + landed.y + ")，误差 " + errX + "/" + errY + " 像素（屏幕 "
                    + width + "x" + height + "）");

                // (2) 跨屏进入副机时的落脚点：左边缘内缩 EntryGuard 像素
                int entryX = ScreenLayout.EntryGuard;
                int entryY = height / 2;
                int ex = CoordinateMapper.PixelsToNormalized(entryX, width);
                int ey = CoordinateMapper.PixelsToNormalized(entryY, height);
                InputInjector.MoveAbsolute(ex, ey);
                Thread.Sleep(80);

                NativeMethods.POINT landed2;
                NativeMethods.GetCursorPos(out landed2);
                int errX2 = Math.Abs(landed2.x - entryX);
                int errY2 = Math.Abs(landed2.y - entryY);

                check("副机注入：跨屏落脚点（边缘内缩 " + ScreenLayout.EntryGuard + " 像素）可达",
                    errX2 <= 2 && errY2 <= 2,
                    "目标 (" + entryX + "," + entryY + ")，实测 (" + landed2.x + "," + landed2.y
                    + ")，误差 " + errX2 + "/" + errY2 + " 像素");
            }
            catch (Exception ex)
            {
                check("副机注入：绝对坐标真的把光标搬到了目标位置", false, "异常 —— " + ex.Message);
            }
            finally
            {
                // 无论成败都必须把用户的光标放回去
                if (haveOriginal)
                {
                    NativeMethods.SetCursorPos(original.x, original.y);
                }
            }
        }

        private static int FindFreePort()
        {
            TcpListener listener = null;
            try
            {
                listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                IPEndPoint endpoint = listener.LocalEndpoint as IPEndPoint;
                return endpoint == null ? 0 : endpoint.Port;
            }
            catch (Exception ex)
            {
                Log.Warn("探测空闲端口失败：" + ex.Message);
                return 0;
            }
            finally
            {
                if (listener != null)
                {
                    try
                    {
                        listener.Stop();
                    }
                    catch (Exception)
                    {
                    }
                }
            }
        }

        private static void SafeStop(PeerConnection connection)
        {
            if (connection == null)
            {
                return;
            }
            try
            {
                connection.Stop();
                connection.Dispose();
            }
            catch (Exception ex)
            {
                Log.Warn("回环测试清理连接时出错：" + ex.Message);
            }
        }
    }
}
