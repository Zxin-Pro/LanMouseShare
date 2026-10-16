using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace LanMouseShare.Core.Net
{
    /// <summary>
    /// 连通性探测代理。
    ///
    /// 为什么需要它：副机连不上主机时，日志里只会出现一句「连接超时」或者干脆什么都没有 ——
    /// 而「超时」背后的原因至少有四种（主机防火墙没放行、主机没点开始共享、
    /// 两台机器不在同一网段、IP 填成了虚拟网卡），每种的处理办法完全不同。
    /// 这里把原始 Socket 错误翻译成能直接照着做的中文结论。
    /// </summary>
    public static class ConnectivityProbe
    {
        public const int DefaultTimeoutMs = 3000;

        public static string Probe(string host, int port)
        {
            return Probe(host, port, DefaultTimeoutMs);
        }

        public static string Probe(string host, int port, int timeoutMs)
        {
            if (host == null)
            {
                host = string.Empty;
            }

            host = host.Trim();

            if (host.Length == 0)
            {
                return "✘ 没有填主机 IP。副机上必须填写**主机**（接鼠标键盘那台）的局域网地址，不能留空。";
            }

            IPAddress address;
            if (!TryResolve(host, out address))
            {
                return "✘ 解析不出地址「" + host + "」。IP 很可能敲错了，或者填的是主机名但这台机器解析不了。"
                       + Environment.NewLine + Environment.NewLine + LocalAddressHint();
            }

            // 先做一些「不用发包就能看出来」的判断，能省掉用户大量猜测。
            string localNote = DescribeLocalMatch(address);

            Stopwatch watch = Stopwatch.StartNew();
            Socket socket = null;
            try
            {
                socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                IAsyncResult ar = socket.BeginConnect(address, port, null, null);

                if (!ar.AsyncWaitHandle.WaitOne(timeoutMs, false))
                {
                    return "✘ 连接 " + address.ToString() + ":" + port.ToString() + " 超时（"
                           + (timeoutMs / 1000.0).ToString("0.#") + " 秒内没有回应）。" + Environment.NewLine
                           + Environment.NewLine
                           + "超时通常意味着数据包被**静默丢弃**了，按可能性从高到低查：" + Environment.NewLine
                           + "  1. 主机（接鼠标键盘那台）的防火墙没有放行 TCP " + port.ToString() + "。" + Environment.NewLine
                           + "     在主机的本程序里点「放行防火墙」，或者手工执行：" + Environment.NewLine
                           + "     netsh advfirewall firewall add rule name=\"LanMouseShare\" dir=in action=allow protocol=TCP localport=" + port.ToString()
                           + Environment.NewLine
                           + "  2. 主机上的本程序**没有**处于运行状态，或者没有点「开始共享」——" + Environment.NewLine
                           + "     但它既然没监听，正常应该回「拒绝」而不是超时，所以这条可能性反而低。" + Environment.NewLine
                           + "  3. 两台电脑不在同一个局域网 / 不是同一个路由器。" + Environment.NewLine
                           + "  4. 主机的 IP 填错了。"
                           + localNote
                           + Environment.NewLine + Environment.NewLine + LocalAddressHint();
                }

                socket.EndConnect(ar);
                watch.Stop();

                string ok = "✔ 能和 " + address.ToString() + ":" + port.ToString() + " 建立 TCP 连接（耗时 "
                            + watch.ElapsedMilliseconds.ToString() + " 毫秒）。" + Environment.NewLine
                            + "网络这一层是通的。如果程序里还是显示未连接，请对照两边日志里「连接密钥」是否完全一致。";

                return ok + localNote + Environment.NewLine + Environment.NewLine + LocalAddressHint();
            }
            catch (SocketException ex)
            {
                string verdict = Classify(ex, address, port);
                return verdict + localNote + Environment.NewLine + Environment.NewLine + LocalAddressHint();
            }
            catch (Exception ex)
            {
                return "✘ 连接失败：" + ex.Message + localNote;
            }
            finally
            {
                if (socket != null)
                {
                    try { socket.Close(); }
                    catch (Exception) { }
                }
            }
        }

        /// <summary>本机该用哪个地址被别人填 —— 直接抄，别自己去 ipconfig 里找。</summary>
        public static string LocalAddressHint()
        {
            string preferred = NetworkInfo.GetPreferredIPv4();
            string all = NetworkInfo.DescribeAll();

            if (string.IsNullOrEmpty(preferred))
            {
                return "本机没有探测到可用的局域网 IPv4 地址（只有回环或者网线没插）。";
            }

            return "继续查之前先确认地址填对了：本机应该用 " + preferred + " 这个地址（本机全部地址：" + all + "）。"
                   + Environment.NewLine
                   + "标着「虚拟网卡」的那些（VMware / VirtualBox / VPN / ZeroTier 之类）"
                   + "别的电脑是连不上的，千万不要填。";
        }

        private static string Classify(SocketException ex, IPAddress address, int port)
        {
            switch (ex.SocketErrorCode)
            {
                case SocketError.ConnectionRefused:
                    return "✘ " + address.ToString() + ":" + port.ToString() + " 明确**拒绝**了连接。" + Environment.NewLine
                           + "这说明 IP 和网络都是通的（包送到了，对方也回了 RST），问题在主机上：" + Environment.NewLine
                           + "  · 主机上的本程序没有在运行；" + Environment.NewLine
                           + "  · 或者程序在运行但没点「开始共享」；" + Environment.NewLine
                           + "  · 或者主机用了别的端口（检查两边的端口号是否一致）。";

                case SocketError.HostNotFound:
                case SocketError.NoData:
                    return "✘ 找不到主机 " + address.ToString() + "。这个 IP 不存在，或者本机 DNS 解析不了。";

                case SocketError.NetworkUnreachable:
                    return "✘ 网络不可达。" + Environment.NewLine
                           + "两台电脑很可能不在同一个局域网：连的不是同一个路由器，"
                           + "或者一边连了 WiFi 另一边连了别的网段。";

                case SocketError.HostUnreachable:
                    return "✘ 主机不可达（" + address.ToString() + "）。" + Environment.NewLine
                           + "IP 段对得上但对方没响应：主机的 IP 可能已经变了（DHCP 续租换了地址），"
                           + "或者对方机器关机 / 睡眠了。";

                case SocketError.TimedOut:
                    return "✘ 连接超时。最可能是主机防火墙没有放行 TCP " + port.ToString() + "。";

                case SocketError.AccessDenied:
                    return "✘ 访问被拒绝：本机自己的防火墙或安全软件挡住了出站连接（" + ex.Message + "）。";

                default:
                    return "✘ 连接失败：" + ex.Message + "（SocketError=" + ex.SocketErrorCode.ToString() + "）。";
            }
        }

        private static string DescribeLocalMatch(IPAddress address)
        {
            List<NetworkInfo.AddressEntry> local = NetworkInfo.GetLocalIPv4();
            string text = address.ToString();

            for (int i = 0; i < local.Count; i++)
            {
                if (!string.Equals(local[i].Address, text, StringComparison.Ordinal))
                {
                    continue;
                }

                if (local[i].IsVirtual)
                {
                    return Environment.NewLine
                           + "⚠ 注意：" + text + " 是**本机虚拟网卡**（" + local[i].Adapter + "）的地址。"
                           + "你正在连自己，而且这个地址别的电脑多半连不上。";
                }

                return Environment.NewLine
                       + "⚠ 注意：" + text + " 就是**本机自己**的地址（" + local[i].Adapter + "）。"
                       + "副机应该填主机的地址，不是自己的。";
            }

            return string.Empty;
        }

        private static bool TryResolve(string host, out IPAddress address)
        {
            address = null;

            if (IPAddress.TryParse(host, out address))
            {
                return true;
            }

            try
            {
                IPAddress[] all = Dns.GetHostAddresses(host);
                for (int i = 0; i < all.Length; i++)
                {
                    if (all[i].AddressFamily == AddressFamily.InterNetwork)
                    {
                        address = all[i];
                        return true;
                    }
                }

                if (all.Length > 0)
                {
                    address = all[0];
                    return true;
                }
            }
            catch (Exception)
            {
            }

            return false;
        }
    }
}
