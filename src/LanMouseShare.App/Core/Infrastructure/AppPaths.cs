using System;
using System.IO;

namespace LanMouseShare.Core.Infrastructure
{
    /// <summary>
    /// 统一路径管理。
    /// 配置与日志放在 %LOCALAPPDATA%\LanMouseShare 下，
    /// 而不是程序目录 —— 程序装在 Program Files 时普通用户没有写权限。
    /// </summary>
    public static class AppPaths
    {
        public const string ProductFolderName = "LanMouseShare";

        private static string _rootDirectory;
        private static string _logDirectory;
        private static string _configurationFile;

        public static string RootDirectory
        {
            get
            {
                EnsureComputed();
                return _rootDirectory;
            }
        }

        public static string LogDirectory
        {
            get
            {
                EnsureComputed();
                return _logDirectory;
            }
        }

        public static string ConfigurationFile
        {
            get
            {
                EnsureComputed();
                return _configurationFile;
            }
        }

        public static void EnsureCreated()
        {
            EnsureDirectory(RootDirectory);
            EnsureDirectory(LogDirectory);
        }

        public static void EnsureDirectory(string path)
        {
            if (!string.IsNullOrEmpty(path) && !Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }
        }

        private static void EnsureComputed()
        {
            if (_rootDirectory != null)
            {
                return;
            }

            string baseDirectory = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(baseDirectory))
            {
                baseDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            }
            if (string.IsNullOrEmpty(baseDirectory))
            {
                baseDirectory = Path.GetTempPath();
            }

            _rootDirectory = Path.Combine(baseDirectory, ProductFolderName);
            _logDirectory = Path.Combine(_rootDirectory, "logs");
            _configurationFile = Path.Combine(_rootDirectory, "settings.json");
        }
    }
}
