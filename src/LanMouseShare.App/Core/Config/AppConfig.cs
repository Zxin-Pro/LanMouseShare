using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using LanMouseShare.Core.Infrastructure;
using LanMouseShare.Core.Json;
using LanMouseShare.Core.Layout;
using LanMouseShare.Core.Net;

namespace LanMouseShare.Core.Config
{
    /// <summary>
    /// 配置模型（配置管理代理产出，第六阶段）。
    ///
    /// 设计取舍：
    ///   * 所有字段都是简单类型，没有嵌套集合 —— 手写 JSON 映射比
    ///     引入序列化框架更省事，也更容易在文件被改坏时逐字段退回默认值；
    ///   * 任何一项读失败都只影响那一项，不会让整份配置作废；
    ///   * Save 用「先写临时文件再替换」，避免掉电时把配置写成半个文件。
    /// </summary>
    public sealed class AppConfig
    {
        public const string FileName = "config.json";
        public const int MinPort = 1024;
        public const int MaxPort = 65535;

        /// <summary>启动角色：host / client。</summary>
        public string Role { get; set; }

        public int Port { get; set; }

        /// <summary>连接密钥（握手认证用，两台机器必须一致）。</summary>
        public string PairingKey { get; set; }

        /// <summary>副机填主机 IP；主机忽略此项。</summary>
        public string PeerHost { get; set; }

        /// <summary>副机相对主机的位置。</summary>
        public ScreenSide Side { get; set; }

        /// <summary>沿贴合边的偏移像素。</summary>
        public int OffsetPixels { get; set; }

        /// <summary>回程守卫像素（防抖）。</summary>
        public int ReturnGuard { get; set; }

        public bool KeyboardEnabled { get; set; }

        public bool AutoReconnect { get; set; }

        public bool MinimizeToTray { get; set; }

        /// <summary>启动后自动开始（上次退出时正在共享就自动恢复）。</summary>
        public bool AutoStart { get; set; }

        public bool CheckUpdateOnStart { get; set; }

        /// <summary>上次看到的副机分辨率，用于在副机连上前也能预览布局。</summary>
        public int LastRemoteWidth { get; set; }

        public int LastRemoteHeight { get; set; }

        public AppConfig()
        {
            Role = "host";
            Port = 24800;
            PairingKey = "lms-24800";
            PeerHost = "192.168.1.2";
            Side = ScreenSide.Right;
            OffsetPixels = 0;
            ReturnGuard = ScreenLayout.DefaultReturnGuard;
            KeyboardEnabled = true;
            AutoReconnect = true;
            MinimizeToTray = true;
            AutoStart = false;
            CheckUpdateOnStart = true;
        }

        public static string FilePath
        {
            get { return Path.Combine(AppPaths.RootDirectory, FileName); }
        }

        public bool IsHost
        {
            get { return !string.Equals(Role, "client", StringComparison.OrdinalIgnoreCase); }
        }

        /// <summary>把配置同步进一个 ScreenLayout 实例。</summary>
        public ScreenLayout ToLayout()
        {
            ScreenLayout layout = new ScreenLayout();
            layout.Side = Side;
            layout.OffsetPixels = OffsetPixels;
            layout.ReturnGuard = ReturnGuard <= 0 ? ScreenLayout.DefaultReturnGuard : ReturnGuard;
            layout.RemoteWidth = LastRemoteWidth;
            layout.RemoteHeight = LastRemoteHeight;
            return layout;
        }

        /// <summary>从界面上的 ScreenLayout 反向同步回配置。</summary>
        public void FromLayout(ScreenLayout layout)
        {
            if (layout == null)
            {
                return;
            }

            Side = layout.Side;
            OffsetPixels = layout.OffsetPixels;
            ReturnGuard = layout.ReturnGuard;
            if (layout.RemoteWidth > 0)
            {
                LastRemoteWidth = layout.RemoteWidth;
            }
            if (layout.RemoteHeight > 0)
            {
                LastRemoteHeight = layout.RemoteHeight;
            }
        }

        public AppConfig Clone()
        {
            AppConfig copy = new AppConfig();
            copy.Role = Role;
            copy.Port = Port;
            copy.PairingKey = PairingKey;
            copy.PeerHost = PeerHost;
            copy.Side = Side;
            copy.OffsetPixels = OffsetPixels;
            copy.ReturnGuard = ReturnGuard;
            copy.KeyboardEnabled = KeyboardEnabled;
            copy.AutoReconnect = AutoReconnect;
            copy.MinimizeToTray = MinimizeToTray;
            copy.AutoStart = AutoStart;
            copy.CheckUpdateOnStart = CheckUpdateOnStart;
            copy.LastRemoteWidth = LastRemoteWidth;
            copy.LastRemoteHeight = LastRemoteHeight;
            return copy;
        }

        // ------------------------------------------------------------ 读写

        public static AppConfig Load()
        {
            // C# 5 里没有弃元（discard），out 参数必须是真变量。
            string ignored;
            return Load(out ignored);
        }

        public static AppConfig Load(out string warning)
        {
            warning = null;
            AppConfig config = new AppConfig();

            try
            {
                if (!File.Exists(FilePath))
                {
                    return config;
                }

                string text = File.ReadAllText(FilePath, Encoding.UTF8);
                string parseError;
                object parsed = Json.Json.TryParse(text, out parseError);
                Dictionary<string, object> map = Json.Json.AsObject(parsed);

                if (map == null)
                {
                    warning = "配置文件无法解析（" + (parseError ?? "不是 JSON 对象") + "），已改用默认设置：" + FilePath;
                    return config;
                }

                config.Role = Json.Json.GetString(map, "role", config.Role);
                config.Port = Json.Json.GetInt(map, "port", config.Port, MinPort, MaxPort);
                config.PairingKey = Json.Json.GetString(map, "pairingKey", config.PairingKey);
                config.PeerHost = Json.Json.GetString(map, "peerHost", config.PeerHost);
                config.Side = ParseSide(Json.Json.GetString(map, "side", "right"));
                config.OffsetPixels = Json.Json.GetInt(map, "offsetPixels", config.OffsetPixels, -10000, 10000);
                config.ReturnGuard = Json.Json.GetInt(map, "returnGuard", config.ReturnGuard, 0, 64);
                config.KeyboardEnabled = Json.Json.GetBool(map, "keyboardEnabled", config.KeyboardEnabled);
                config.AutoReconnect = Json.Json.GetBool(map, "autoReconnect", config.AutoReconnect);
                config.MinimizeToTray = Json.Json.GetBool(map, "minimizeToTray", config.MinimizeToTray);
                config.AutoStart = Json.Json.GetBool(map, "autoStart", config.AutoStart);
                config.CheckUpdateOnStart = Json.Json.GetBool(map, "checkUpdateOnStart", config.CheckUpdateOnStart);
                config.LastRemoteWidth = Json.Json.GetInt(map, "lastRemoteWidth", config.LastRemoteWidth, 0, 100000);
                config.LastRemoteHeight = Json.Json.GetInt(map, "lastRemoteHeight", config.LastRemoteHeight, 0, 100000);
            }
            catch (Exception ex)
            {
                warning = "读取配置失败（" + ex.Message + "），已改用默认设置";
            }

            return config;
        }

        public bool Save(out string error)
        {
            error = null;
            string temp = FilePath + ".tmp";

            try
            {
                AppPaths.EnsureCreated();

                Dictionary<string, object> map = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                map["_comment"] = "LanMouseShare 配置文件。程序退出时自动生成，可以手工编辑，但请先退出程序。";
                map["role"] = IsHost ? "host" : "client";
                map["port"] = Port;
                map["pairingKey"] = PairingKey;
                map["peerHost"] = PeerHost;
                map["side"] = SideToString(Side);
                map["offsetPixels"] = OffsetPixels;
                map["returnGuard"] = ReturnGuard;
                map["keyboardEnabled"] = KeyboardEnabled;
                map["autoReconnect"] = AutoReconnect;
                map["minimizeToTray"] = MinimizeToTray;
                map["autoStart"] = AutoStart;
                map["checkUpdateOnStart"] = CheckUpdateOnStart;
                map["lastRemoteWidth"] = LastRemoteWidth;
                map["lastRemoteHeight"] = LastRemoteHeight;

                // 先写临时文件再替换：掉电也不会留下半个 JSON
                File.WriteAllText(temp, Json.Json.Stringify(map), new UTF8Encoding(false));
                if (File.Exists(FilePath))
                {
                    File.Delete(FilePath);
                }
                File.Move(temp, FilePath);

                return true;
            }
            catch (Exception ex)
            {
                error = "保存配置失败：" + ex.Message;
                try
                {
                    if (File.Exists(temp))
                    {
                        File.Delete(temp);
                    }
                }
                catch (Exception)
                {
                }
                return false;
            }
        }

        public string Describe()
        {
            return (IsHost ? "主机" : "副机") + " · 端口 " + Port.ToString(CultureInfo.InvariantCulture) +
                   " · " + SideToChinese(Side) + " · 偏移 " + OffsetPixels.ToString(CultureInfo.InvariantCulture) + "px";
        }

        // ------------------------------------------------------------ 枚举互转

        public static ScreenSide ParseSide(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return ScreenSide.Right;
            }

            switch (text.Trim().ToLowerInvariant())
            {
                case "left": return ScreenSide.Left;
                case "above":
                case "top": return ScreenSide.Above;
                case "below":
                case "bottom": return ScreenSide.Below;
                default: return ScreenSide.Right;
            }
        }

        public static string SideToString(ScreenSide side)
        {
            switch (side)
            {
                case ScreenSide.Left: return "left";
                case ScreenSide.Above: return "above";
                case ScreenSide.Below: return "below";
                default: return "right";
            }
        }

        public static string SideToChinese(ScreenSide side)
        {
            switch (side)
            {
                case ScreenSide.Left: return "副机在左侧";
                case ScreenSide.Above: return "副机在上方";
                case ScreenSide.Below: return "副机在下方";
                default: return "副机在右侧";
            }
        }
    }
}
