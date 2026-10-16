using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace LanMouseShare.Core.Net
{
    /// <summary>
    /// 本机网络信息（网络基础代理产出）。
    /// 主机界面上要把「副机该填的地址」直接显示出来，省得用户去 ipconfig。
    /// 只挑 IPv4 私网地址，并且优先有线/无线物理网卡，过滤掉 VPN 虚拟网卡。
    /// </summary>
    public static class NetworkInfo
    {
        public sealed class AddressEntry
        {
            public string Address { get; set; }
            public string Adapter { get; set; }
            public bool IsVirtual { get; set; }
        }

        public static List<AddressEntry> GetLocalIPv4()
        {
            List<AddressEntry> result = new List<AddressEntry>();

            try
            {
                NetworkInterface[] adapters = NetworkInterface.GetAllNetworkInterfaces();
                foreach (NetworkInterface adapter in adapters)
                {
                    if (adapter.OperationalStatus != OperationalStatus.Up)
                    {
                        continue;
                    }

                    if (adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                        adapter.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
                    {
                        continue;
                    }

                    IPInterfaceProperties properties;
                    try
                    {
                        properties = adapter.GetIPProperties();
                    }
                    catch (Exception)
                    {
                        continue;
                    }

                    foreach (UnicastIPAddressInformation info in properties.UnicastAddresses)
                    {
                        if (info.Address == null || info.Address.AddressFamily != AddressFamily.InterNetwork)
                        {
                            continue;
                        }

                        string text = info.Address.ToString();
                        if (!IsPrivateIPv4(text))
                        {
                            continue;
                        }

                        AddressEntry entry = new AddressEntry();
                        entry.Address = text;
                        entry.Adapter = adapter.Name + "（" + adapter.Description + "）";
                        entry.IsVirtual = IsVirtualAdapter(adapter);
                        result.Add(entry);
                    }
                }
            }
            catch (Exception)
            {
                // 拿不到网卡信息不算致命错误：用户自己填 IP 也能用
            }

            // 虚拟网卡（VMware / Hyper-V / VPN）排在后面
            result.Sort(delegate (AddressEntry a, AddressEntry b)
            {
                if (a.IsVirtual != b.IsVirtual)
                {
                    return a.IsVirtual ? 1 : -1;
                }
                return string.CompareOrdinal(a.Address, b.Address);
            });

            return result;
        }

        /// <summary>最好用的那个 IPv4 私网地址；没有就返回 null。</summary>
        public static string GetPreferredIPv4()
        {
            List<AddressEntry> list = GetLocalIPv4();
            return list.Count > 0 ? list[0].Address : null;
        }

        public static bool IsPrivateIPv4(string text)
        {
            IPAddress ip;
            if (!IPAddress.TryParse(text, out ip))
            {
                return false;
            }

            byte[] bytes = ip.GetAddressBytes();
            if (bytes.Length != 4)
            {
                return false;
            }

            if (bytes[0] == 10) return true;
            if (bytes[0] == 192 && bytes[1] == 168) return true;
            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true;
            if (bytes[0] == 169 && bytes[1] == 254) return true; // 自动私有地址（没拿到 DHCP）
            return false;
        }

        private static bool IsVirtualAdapter(NetworkInterface adapter)
        {
            string text = ((adapter.Name ?? string.Empty) + " " + (adapter.Description ?? string.Empty)).ToLowerInvariant();
            string[] markers = new string[]
            {
                "vmware", "virtualbox", "hyper-v", "vethernet", "loopback", "tap-", "tun", "vpn",
                "openvpn", "wireguard", "zerotier", "tailscale", "docker", "bluetooth", "npcap", "wintun"
            };

            for (int i = 0; i < markers.Length; i++)
            {
                if (text.IndexOf(markers[i], StringComparison.Ordinal) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        public static string DescribeAll()
        {
            List<AddressEntry> list = GetLocalIPv4();
            if (list.Count == 0)
            {
                return "未检测到可用的 IPv4 局域网地址";
            }

            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < list.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append("；");
                }
                sb.Append(list[i].Address);
                if (list[i].IsVirtual)
                {
                    sb.Append("(虚拟网卡)");
                }
            }

            return sb.ToString();
        }
    }
}
