using System;

namespace LanMouseShare.Setup
{
    /// <summary>安装 / 卸载的结果。</summary>
    internal sealed class InstallResult
    {
        public bool Ok;
        public string Message = string.Empty;

        /// <summary>安装目录。</summary>
        public string TargetDirectory = string.Empty;

        /// <summary>解压出来的主程序完整路径。</summary>
        public string ExecutablePath = string.Empty;

        public int FileCount;
        public long TotalBytes;

        /// <summary>创建快捷方式时遇到的非致命问题（桌面 / 开始菜单分条记录）。</summary>
        public string ShortcutWarning = string.Empty;

        public static InstallResult Fail(string message)
        {
            return new InstallResult { Ok = false, Message = message ?? "未知错误" };
        }

        public static InstallResult Success(string message)
        {
            return new InstallResult { Ok = true, Message = message ?? "完成" };
        }

        public string SizeText
        {
            get
            {
                if (TotalBytes <= 0) { return "0 字节"; }
                if (TotalBytes < 1024) { return TotalBytes + " 字节"; }
                if (TotalBytes < 1024 * 1024) { return (TotalBytes / 1024.0).ToString("0.0") + " KB"; }
                return (TotalBytes / 1024.0 / 1024.0).ToString("0.00") + " MB";
            }
        }
    }
}
