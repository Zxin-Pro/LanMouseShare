using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace LanMouseShare.Setup
{
    internal static class Program
    {
        private const int ExitOk = 0;
        private const int ExitFailed = 1;
        private const int ExitCrashed = 2;

        /// <summary>
        /// 静默模式下把自己挂到父进程的控制台，
        /// 这样 --silent 在 cmd 里能直接看到输出。
        /// </summary>
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AttachConsole(int dwProcessId);

        private const int AttachParentProcess = -1;

        [STAThread]
        private static int Main(string[] args)
        {
            SetupOptions options;
            try
            {
                options = SetupOptions.Parse(args);
            }
            catch (Exception ex)
            {
                Console.WriteLine("命令行解析失败：" + ex.Message);
                return ExitCrashed;
            }

            if (ContainsHelp(args))
            {
                PrintUsage();
                return ExitOk;
            }

            if (options.Silent)
            {
                return RunSilent(options);
            }

            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                using (var form = new InstallerForm(options))
                {
                    Application.Run(form);
                    return form.ExitCode;
                }
            }
            catch (Exception ex)
            {
                ShowFatal(ex);
                return ExitCrashed;
            }
        }

        private static int RunSilent(SetupOptions options)
        {
            AttachToParentConsole();

            // 本程序是 GUI 子系统（WinExe），cmd 启动它之后不会等它，
            // "setup.exe --silent > out.txt" 里的 out.txt 永远是空的。
            // 所以静默模式一律把输出再写一份到日志文件，脚本读这个文件就够了。
            var logPath = string.IsNullOrEmpty(options.LogPath) ? SetupEngine.DefaultSetupLogPath : options.LogPath;
            var log = TryOpenLog(logPath);

            try
            {
                WriteLine(log, "LanMouseShare 安装程序 " + SetupEngine.ProductVersion + " —— " + options.Describe());
                WriteLine(log, "日志文件：" + (log == null ? "（打不开：" + logPath + "）" : logPath));

                var report = new Action<string>(m => WriteLine(log, m));
                var result = options.Uninstall
                    ? SetupEngine.Uninstall(options, report, null)
                    : SetupEngine.Install(options, report, null);

                WriteLine(log, result.Ok ? "[OK]" : "[失败]");
                WriteLine(log, result.Message);

                if (result.Ok && !options.Uninstall && !options.NoLaunch)
                {
                    SetupEngine.LaunchApp(result.ExecutablePath);
                }

                return result.Ok ? ExitOk : ExitFailed;
            }
            catch (Exception ex)
            {
                WriteLine(log, "[异常] " + ex);
                return ExitCrashed;
            }
            finally
            {
                if (log != null)
                {
                    try { log.Flush(); log.Dispose(); }
                    catch (Exception) { }
                }
            }
        }

        /// <summary>日志写不进去也不该让安装失败，所以整段吞异常。</summary>
        private static StreamWriter TryOpenLog(string path)
        {
            try
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) { Directory.CreateDirectory(dir); }

                var writer = new StreamWriter(path, false, new UTF8Encoding(true));
                writer.AutoFlush = true;
                return writer;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static void WriteLine(StreamWriter log, string message)
        {
            try { Console.WriteLine(message); }
            catch (Exception) { }

            if (log == null) { return; }

            try { log.WriteLine(DateTime.Now.ToString("HH:mm:ss") + "  " + message); }
            catch (Exception) { }
        }

        private static void AttachToParentConsole()
        {
            try
            {
                if (!AttachConsole(AttachParentProcess)) { return; }
                Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
                Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
            }
            catch (Exception)
            {
            }
        }

        private static bool ContainsHelp(string[] args)
        {
            if (args == null) { return false; }
            foreach (var a in args)
            {
                if (string.IsNullOrEmpty(a)) { continue; }
                var t = a.Trim();
                if (t.Equals("--help", StringComparison.OrdinalIgnoreCase) ||
                    t.Equals("-h", StringComparison.OrdinalIgnoreCase) ||
                    t.Equals("/?", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        private static void PrintUsage()
        {
            AttachToParentConsole();
            Console.WriteLine("LanMouseShare 安装程序 " + SetupEngine.ProductVersion);
            Console.WriteLine();
            Console.WriteLine("  LanMouseShare-setup.exe                        图形界面安装");
            Console.WriteLine("  LanMouseShare-setup.exe --silent               静默安装");
            Console.WriteLine("  LanMouseShare-setup.exe --nolaunch             装完不自动启动");
            Console.WriteLine("  LanMouseShare-setup.exe --dir=D:盘路径         装到指定目录");
            Console.WriteLine("  LanMouseShare-setup.exe --uninstall            卸载（保留配置）");
            Console.WriteLine("  LanMouseShare-setup.exe --uninstall --purge    卸载并删除配置与日志");
            Console.WriteLine("  LanMouseShare-setup.exe --log=D:\\setup.log    静默模式把输出写到这个文件");
            Console.WriteLine("  LanMouseShare-setup.exe --help                 显示这段说明");
            Console.WriteLine();
            Console.WriteLine("默认安装目录：" + SetupEngine.DefaultTargetDirectory);
            Console.WriteLine("配置与日志：  " + SetupEngine.UserDataDirectory);
            Console.WriteLine("静默模式日志：" + SetupEngine.DefaultSetupLogPath);
            Console.WriteLine();
            Console.WriteLine("提示：本程序是图形程序，在 cmd 里用 > 重定向是拿不到输出的。");
            Console.WriteLine("      要脚本化安装，读上面那个日志文件，或者用 start /wait 等它跑完。");
        }

        private static void ShowFatal(Exception ex)
        {
            try
            {
                MessageBox.Show(
                    "安装程序遇到未处理的错误：" + Environment.NewLine + Environment.NewLine +
                    ex.Message + Environment.NewLine + Environment.NewLine +
                    "可以到 " + SetupEngine.ReleasesUrl + " 重新下载，或用 --silent 命令行方式运行看看详细输出。",
                    "LanMouseShare 安装程序",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            catch (Exception)
            {
            }
        }
    }
}
