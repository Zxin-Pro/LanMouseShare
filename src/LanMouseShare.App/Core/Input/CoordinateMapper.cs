using System;

namespace LanMouseShare.Core.Input
{
    /// <summary>
    /// 物理像素 <-> 0..65535 归一化坐标（第三阶段：坐标映射代理）。
    ///
    /// 为什么用归一化坐标而不是直接传像素：
    ///   两台机器的分辨率可以完全不同（1920x1080 对 2560x1440）。
    ///   主机只需要知道「鼠标在屏幕宽度的百分之几」，副机自己换算成
    ///   自己的像素 —— 这样主机根本不需要知道副机的分辨率，
    ///   换显示器、改分辨率都不用改代码。
    ///
    /// 换算公式故意与 Windows 自身的实现对齐：
    ///   Windows 把 dx 还原成像素时算的是 (dx * cxScreen) >> 16，
    ///   所以这里正向用 pixel * 65536 / width，反向用 nx * width / 65536，
    ///   两者能精确往返，不会因为四舍五入在边缘差 1 像素。
    /// </summary>
    public static class CoordinateMapper
    {
        public const int NormalizedMax = 65535;

        /// <summary>像素 -> 归一化（0..65535）。</summary>
        public static int PixelsToNormalized(int pixel, int extent)
        {
            if (extent <= 1)
            {
                return 0;
            }

            long value = ((long)pixel * 65536L) / extent;
            if (value < 0)
            {
                value = 0;
            }
            else if (value > NormalizedMax)
            {
                value = NormalizedMax;
            }

            return (int)value;
        }

        /// <summary>归一化 -> 像素（0..extent-1）。</summary>
        public static int NormalizedToPixels(int normalized, int extent)
        {
            if (extent <= 1)
            {
                return 0;
            }

            if (normalized < 0)
            {
                normalized = 0;
            }
            else if (normalized > NormalizedMax)
            {
                normalized = NormalizedMax;
            }

            long value = ((long)normalized * extent) / 65536L;
            if (value > extent - 1)
            {
                value = extent - 1;
            }

            return (int)value;
        }

        public static void PixelsToNormalized(int x, int y, int width, int height, out int nx, out int ny)
        {
            nx = PixelsToNormalized(x, width);
            ny = PixelsToNormalized(y, height);
        }

        public static void NormalizedToPixels(int nx, int ny, int width, int height, out int x, out int y)
        {
            x = NormalizedToPixels(nx, width);
            y = NormalizedToPixels(ny, height);
        }
    }
}
