using System;

namespace LanMouseShare.Core.Input
{
    /// <summary>鼠标按键种类（第三阶段：鼠标按键代理）。</summary>
    public enum MouseButtonKind
    {
        Left = 0,
        Right = 1,
        Middle = 2,
        X1 = 3,
        X2 = 4
    }

    /// <summary>鼠标按键的工具方法。</summary>
    public static class MouseButtonKindExtensions
    {
        /// <summary>协议里的字符串名（保持稳定，不要随语言变化）。</summary>
        public static string ToProtocolName(this MouseButtonKind kind)
        {
            switch (kind)
            {
                case MouseButtonKind.Left: return "left";
                case MouseButtonKind.Right: return "right";
                case MouseButtonKind.Middle: return "middle";
                case MouseButtonKind.X1: return "x1";
                case MouseButtonKind.X2: return "x2";
                default: return "left";
            }
        }

        public static bool TryParse(string text, out MouseButtonKind kind)
        {
            kind = MouseButtonKind.Left;
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            switch (text.Trim().ToLowerInvariant())
            {
                case "left": kind = MouseButtonKind.Left; return true;
                case "right": kind = MouseButtonKind.Right; return true;
                case "middle": kind = MouseButtonKind.Middle; return true;
                case "x1": kind = MouseButtonKind.X1; return true;
                case "x2": kind = MouseButtonKind.X2; return true;
                default: return false;
            }
        }
    }
}
