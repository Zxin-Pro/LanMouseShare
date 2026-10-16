using System;

namespace LanMouseShare.Core.Net
{
    /// <summary>
    /// 协议消息类型（第二阶段：协议设计代理）。
    /// 一条消息 = 一行 UTF-8 JSON，以 '\n' 结尾。
    /// </summary>
    public static class Msg
    {
        /// <summary>服务端 -> 客户端：协议版本 + 随机挑战 nonce。</summary>
        public const string Hello = "hello";

        /// <summary>客户端 -> 服务端：HMAC-SHA256(密钥, 版本|nonce)。</summary>
        public const string Auth = "auth";

        /// <summary>服务端 -> 客户端：accepted = true/false。</summary>
        public const string AuthResult = "auth_result";

        /// <summary>双向：通告本机屏幕尺寸（物理像素）。</summary>
        public const string ScreenInfo = "screen_info";

        /// <summary>主机 -> 客户端：鼠标移动到归一化坐标。</summary>
        public const string MouseMove = "mouse_move";

        /// <summary>主机 -> 客户端：鼠标按键按下/抬起。</summary>
        public const string MouseButton = "mouse_button";

        /// <summary>主机 -> 客户端：滚轮。</summary>
        public const string MouseWheel = "mouse_wheel";

        /// <summary>主机 -> 客户端：键盘按键。</summary>
        public const string Key = "key";

        /// <summary>心跳。</summary>
        public const string Ping = "ping";

        /// <summary>心跳应答。</summary>
        public const string Pong = "pong";

        /// <summary>主动断开。</summary>
        public const string Bye = "bye";

        /// <summary>人类可读的通知（只用于界面显示，不参与逻辑）。</summary>
        public const string Notice = "notice";
    }

    /// <summary>
    /// 一条协议帧。公开字段名即 JSON 键名，保持短小以降低带宽与解析开销。
    ///
    /// 坐标约定：nx / ny 一律是 0..65535 的**归一化值**，
    /// 与 SendInput 的 MOUSEEVENTF_ABSOLUTE 完全一致，
    /// 这样跨不同分辨率的机器也不需要主机知道副机的分辨率。
    /// </summary>
    [Serializable]
    public sealed class Frame
    {
        public string type;
        public string version;
        public string nonce;
        public string mac;
        public bool accepted;
        public string note;
        public int screenWidth;
        public int screenHeight;
        public int nx;
        public int ny;
        public string button;
        public bool down;
        public int wheel;

        /// <summary>水平滚轮的标志位（仅 mouse_wheel 帧使用）。</summary>
        public bool horizontal;
        public int vk;
        public int scan;
        public bool extended;
        public long stamp;

        public static long NowMs()
        {
            return DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
        }

        public Frame Clone()
        {
            Frame f = new Frame();
            f.type = type; f.version = version; f.nonce = nonce; f.mac = mac;
            f.accepted = accepted; f.note = note;
            f.screenWidth = screenWidth; f.screenHeight = screenHeight;
            f.nx = nx; f.ny = ny;
            f.button = button; f.down = down; f.wheel = wheel;
            f.vk = vk; f.scan = scan; f.extended = extended; f.stamp = stamp;
            return f;
        }
    }
}
