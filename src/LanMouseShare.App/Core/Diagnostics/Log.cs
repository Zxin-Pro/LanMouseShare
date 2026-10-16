using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace LanMouseShare.Core.Diagnostics
{
    /// <summary>
    /// 轻量文本日志（对应「日志代理」的第一阶段产出）。
    ///
    /// 硬性约定：
    ///   * 只记录连接状态、错误、屏幕切换、DPI、权限等事件；
    ///   * **绝不记录具体按键内容**（不落盘任何按键码、字符、剪贴板内容）；
    ///   * 按天分文件，单文件超过 2 MB 自动滚动编号；
    ///   * 所有写入都在锁内完成，且任何写入失败都被吞掉
    ///     —— 日志永远不能让主流程崩溃。
    /// </summary>
    public static class Log
    {
        private const long MaxFileBytes = 2L * 1024L * 1024L;

        /// <summary>不带 BOM 的 UTF-8：日志文件追加时不会反复写入 BOM。</summary>
        private static readonly UTF8Encoding LogEncoding = new UTF8Encoding(false);

        private static readonly object Gate = new object();

        private static string _directory;
        private static string _currentFile;
        private static int _rollIndex;

        /// <summary>当前正在写入的日志文件；未初始化时为 null。</summary>
        public static string CurrentFile
        {
            get
            {
                lock (Gate)
                {
                    return _currentFile;
                }
            }
        }

        public static void Initialize(string directory)
        {
            lock (Gate)
            {
                _directory = directory;
                _rollIndex = 0;
                _currentFile = null;
                try
                {
                    if (!string.IsNullOrEmpty(_directory) && !Directory.Exists(_directory))
                    {
                        Directory.CreateDirectory(_directory);
                    }
                    _currentFile = BuildPath();
                }
                catch (Exception)
                {
                    _currentFile = null;
                }
            }
        }

        public static void Info(string message)
        {
            Write("INFO ", message, null);
        }

        public static void Warn(string message)
        {
            Write("WARN ", message, null);
        }

        public static void Error(string message, Exception ex)
        {
            Write("ERROR", message, ex);
        }

        private static string BuildPath()
        {
            string name = "LanMouseShare-" +
                          DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            if (_rollIndex > 0)
            {
                name += "-" + _rollIndex.ToString(CultureInfo.InvariantCulture);
            }
            return Path.Combine(_directory, name + ".log");
        }

        private static void Write(string level, string message, Exception ex)
        {
            lock (Gate)
            {
                if (_currentFile == null)
                {
                    return;
                }
                try
                {
                    RollIfNeeded();

                    StringBuilder sb = new StringBuilder(256);
                    sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture));
                    sb.Append(" [").Append(level).Append("] [T");
                    sb.Append(Thread.CurrentThread.ManagedThreadId.ToString(CultureInfo.InvariantCulture));
                    sb.Append("] ");
                    sb.Append(message);
                    if (ex != null)
                    {
                        sb.Append(" | ").Append(ex.GetType().FullName).Append(": ").Append(ex.Message);
                        if (!string.IsNullOrEmpty(ex.StackTrace))
                        {
                            sb.Append(" | stack: ").Append(ex.StackTrace);
                        }
                    }
                    sb.Append(Environment.NewLine);

                    File.AppendAllText(_currentFile, sb.ToString(), LogEncoding);
                }
                catch (Exception)
                {
                    // 日志写入失败不能影响主流程。
                }
            }
        }

        private static void RollIfNeeded()
        {
            try
            {
                if (!File.Exists(_currentFile))
                {
                    return;
                }
                FileInfo info = new FileInfo(_currentFile);
                if (info.Length < MaxFileBytes)
                {
                    return;
                }
                _rollIndex++;
                _currentFile = BuildPath();
            }
            catch (Exception)
            {
                // 滚动失败时继续写原文件。
            }
        }
    }
}
