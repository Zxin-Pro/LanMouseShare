using System;

namespace LanMouseShare.Setup
{
    /// <summary>
    /// 安装程序的命令行开关。
    ///
    ///   LanMouseShare-setup.exe                          图形界面安装
    ///   LanMouseShare-setup.exe --silent                 静默安装（不弹窗，只给退出码）
    ///   LanMouseShare-setup.exe --nolaunch               只安装，不自动启动
    ///   LanMouseShare-setup.exe --dir=D:\LMS             装到指定目录
    ///   LanMouseShare-setup.exe --log=D:\setup.log       静默模式把输出写到这个文件
    ///   LanMouseShare-setup.exe --uninstall              卸载
    ///   LanMouseShare-setup.exe --uninstall --purge      连配置和日志一起删掉
    /// </summary>
    internal sealed class SetupOptions
    {
        /// <summary>静默模式：不显示任何界面，结果只体现在退出码上。</summary>
        public bool Silent;

        /// <summary>卸载模式。</summary>
        public bool Uninstall;

        /// <summary>安装完成后不自动启动程序。</summary>
        public bool NoLaunch;

        /// <summary>卸载时连 %LOCALAPPDATA%\LanMouseShare 里的配置和日志一起删。</summary>
        public bool Purge;

        /// <summary>自定义安装目录；为空表示用 DefaultTargetDirectory。</summary>
        public string TargetDirectory;

        /// <summary>
        /// 静默模式的日志文件；为空表示用 SetupEngine.DefaultSetupLogPath。
        ///
        /// 为什么需要它：本程序是 GUI 子系统（WinExe），cmd 启动它之后不会等它，
        /// 所以 "setup.exe --silent &gt; out.txt" 里的 out.txt 永远是空的。
        /// 静默模式一律把输出再写一份到日志文件，脚本读这个文件就够了。
        /// </summary>
        public string LogPath;

        public static SetupOptions Parse(string[] args)
        {
            var o = new SetupOptions();
            if (args == null) { return o; }

            foreach (var raw in args)
            {
                if (string.IsNullOrEmpty(raw)) { continue; }
                var a = raw.Trim();

                if (a.Equals("--silent", StringComparison.OrdinalIgnoreCase) ||
                    a.Equals("-s", StringComparison.OrdinalIgnoreCase) ||
                    a.Equals("/s", StringComparison.OrdinalIgnoreCase))
                {
                    o.Silent = true;
                    continue;
                }
                if (a.Equals("--uninstall", StringComparison.OrdinalIgnoreCase) ||
                    a.Equals("-u", StringComparison.OrdinalIgnoreCase))
                {
                    o.Uninstall = true;
                    continue;
                }
                if (a.Equals("--nolaunch", StringComparison.OrdinalIgnoreCase) ||
                    a.Equals("--no-launch", StringComparison.OrdinalIgnoreCase))
                {
                    o.NoLaunch = true;
                    continue;
                }
                if (a.Equals("--purge", StringComparison.OrdinalIgnoreCase))
                {
                    o.Purge = true;
                    continue;
                }
                if (a.StartsWith("--dir=", StringComparison.OrdinalIgnoreCase))
                {
                    var v = a.Substring(6).Trim().Trim('"');
                    if (v.Length > 0) { o.TargetDirectory = v; }
                    continue;
                }
                if (a.StartsWith("--log=", StringComparison.OrdinalIgnoreCase))
                {
                    var v = a.Substring(6).Trim().Trim('"');
                    if (v.Length > 0) { o.LogPath = v; }
                    continue;
                }
            }

            // 卸载模式下「不自动启动」是废话，直接置位，省得下面到处判断。
            if (o.Uninstall) { o.NoLaunch = true; }

            return o;
        }

        public string Describe()
        {
            return string.Format("模式={0}，静默={1}，启动程序={2}，目录={3}，日志={4}",
                Uninstall ? "卸载" : "安装",
                Silent ? "是" : "否",
                NoLaunch ? "否" : "是",
                string.IsNullOrEmpty(TargetDirectory) ? "（默认）" : TargetDirectory,
                string.IsNullOrEmpty(LogPath) ? "（默认）" : LogPath);
        }
    }
}
