using System;
using System.Security.Cryptography;
using System.Text;

namespace LanMouseShare.Core.Net
{
    /// <summary>
    /// 握手密钥工具（第二阶段：握手认证代理）。
    ///
    /// 设计：不做「把密钥明文发过去」这种蠢事，而是挑战-应答：
    ///   1. 服务端发 hello，带一个 16 字节随机 nonce；
    ///   2. 客户端回 auth，带 HMAC-SHA256(key, 协议版本 + "|" + nonce)；
    ///   3. 服务端用本地密钥重算一遍，做**固定时间比较**。
    /// 密钥本身永不上网，抓包也拿不到；nonce 每次随机，重放攻击也无效。
    /// </summary>
    public static class PairingKey
    {
        /// <summary>密钥的十六进制长度（16 字节 = 32 个 hex 字符）。</summary>
        public const int KeyHexLength = 32;

        public static string CreateKey()
        {
            return RandomHex(16);
        }

        public static string CreateNonce()
        {
            return RandomHex(16);
        }

        public static string ComputeMac(string key, string protocolVersion, string nonce)
        {
            byte[] keyBytes = Encoding.UTF8.GetBytes(key == null ? string.Empty : key);
            string material = (protocolVersion == null ? string.Empty : protocolVersion)
                              + "|" + (nonce == null ? string.Empty : nonce);
            byte[] message = Encoding.UTF8.GetBytes(material);

            using (HMACSHA256 hmac = new HMACSHA256(keyBytes))
            {
                return ToHex(hmac.ComputeHash(message));
            }
        }

        public static bool Verify(string key, string protocolVersion, string nonce, string mac)
        {
            if (string.IsNullOrEmpty(mac))
            {
                return false;
            }

            string expected = ComputeMac(key, protocolVersion, nonce);
            return FixedTimeEquals(expected, mac);
        }

        /// <summary>十六进制密钥是否看起来合法（长度 32 且全部是 hex 字符）。</summary>
        public static bool LooksValid(string key)
        {
            if (string.IsNullOrEmpty(key) || key.Length != KeyHexLength)
            {
                return false;
            }

            for (int i = 0; i < key.Length; i++)
            {
                char c = key[i];
                bool isHex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!isHex)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// 固定时间比较：不提前 return，避免通过响应时间逐字节猜出 MAC。
        /// 注意不要用 string.Equals 或 SequenceEqual 的短路版本。
        /// </summary>
        private static bool FixedTimeEquals(string a, string b)
        {
            if (a == null || b == null || a.Length != b.Length)
            {
                return false;
            }

            int diff = 0;
            for (int i = 0; i < a.Length; i++)
            {
                diff |= (a[i] ^ b[i]);
            }

            return diff == 0;
        }

        private static string RandomHex(int byteCount)
        {
            byte[] raw = new byte[byteCount];
            using (RandomNumberGenerator rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(raw);
            }

            return ToHex(raw);
        }

        private static string ToHex(byte[] bytes)
        {
            StringBuilder sb = new StringBuilder(bytes.Length * 2);
            for (int i = 0; i < bytes.Length; i++)
            {
                sb.Append(bytes[i].ToString("x2"));
            }

            return sb.ToString();
        }
    }
}
