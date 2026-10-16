using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace LanMouseShare.Setup
{
    /// <summary>
    /// 安装 / 卸载的全部实际动作都在这里。
    /// 界面（InstallerForm）和静默模式（Program）都只调用本类，
    /// 所以 --silent 与图形界面走的是同一条代码路径，不存在「只有界面能装」的情况。
    /// </summary>
    internal static class SetupEngine
    {
        public const string ProductName = "LanMouseShare";
        public const string ProductTitle = "LanMouseShare · 局域网键鼠共享";
        public const string Publisher = "Zxin-Pro";
        public const string ProjectUrl = "https://github.com/Zxin-Pro/LanMouseShare";
        public const string ReleasesUrl = "https://github.com/Zxin-Pro/LanMouseShare/releases/latest";
        public const string DotNet48Url = "https://dotnet.microsoft.com/download/dotnet-framework/net48";

        /// <summary>要关闭的同名进程（不含 .exe）。</summary>
        public const string ProcessName = "LanMouseShare";

        /// <summary>主程序文件名，与 LanMouseShare.App 的 AssemblyName 一致。</summary>
        public const string ExecutableName = "LanMouseShare.exe";

        /// <summary>安装目录里那份卸载程序的文件名。</summary>
        public const string UninstallerName = "卸载 LanMouseShare.exe";

        /// <summary>桌面 / 开始菜单快捷方式的显示名。</summary>
        public const string ShortcutName = "LanMouseShare";

        /// <summary>卸载信息注册表项（当前用户，卸载列表里能看到）。</summary>
        public const string UninstallRegistryPath =
            @"Software\Microsoft\Windows\CurrentVersion\Uninstall\LanMouseShare";

        /// <summary>用户配置与日志目录，由 LanMouseShare.App 的 AppPaths 使用。</summary>
        public static string UserDataDirectory
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "LanMouseShare");
            }
        }

        public static string DefaultTargetDirectory
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Programs",
                    "LanMouseShare");
            }
        }

        /// <summary>
        /// 静默模式的默认日志文件。
        ///
        /// 放在 %TEMP% 而不是 %LOCALAPPDATA%\LanMouseShare：卸载加 --purge 时要把那个目录整个删掉，
        /// 而我们此刻正开着日志文件的句柄，删不干净。
        /// </summary>
        public static string DefaultSetupLogPath
        {
            get { return Path.Combine(Path.GetTempPath(), "LanMouseShareSetup.log"); }
        }

        public static string ProductVersion
        {
            get
            {
                try
                {
                    var asm = Assembly.GetExecutingAssembly();
                    var attr = (AssemblyFileVersionAttribute)Attribute.GetCustomAttribute(
                        asm, typeof(AssemblyFileVersionAttribute));
                    if (attr != null && !string.IsNullOrEmpty(attr.Version)) { return attr.Version; }
                    return asm.GetName().Version.ToString();
                }
                catch (Exception)
                {
                    return "0.0.0.0";
                }
            }
        }

        public static string CurrentExecutablePath
        {
            get
            {
                try
                {
                    return Process.GetCurrentProcess().MainModule.FileName;
                }
                catch (Exception)
                {
                    try { return Assembly.GetExecutingAssembly().Location; }
                    catch (Exception) { return string.Empty; }
                }
            }
        }

        // ==================================================================
        //  内嵌载荷
        // ==================================================================

        /// <summary>
        /// 找到内嵌的 payload.zip 资源名。
        /// 不写死字符串，是因为嵌入资源名会随 RootNamespace 和文件名变化，
        /// 写死一旦对不上就变成一个「装完是空的」的哑巴安装包。
        /// </summary>
        private static string FindPayloadResourceName()
        {
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                foreach (var name in asm.GetManifestResourceNames())
                {
                    if (name.EndsWith("payload.zip", StringComparison.OrdinalIgnoreCase))
                    {
                        return name;
                    }
                }
            }
            catch (Exception)
            {
            }
            return null;
        }

        public static bool HasPayload
        {
            get { return FindPayloadResourceName() != null; }
        }

        /// <summary>读出内嵌 zip 的文件数与总字节数，用于界面展示。</summary>
        public static void GetPayloadSummary(out int fileCount, out long totalBytes)
        {
            fileCount = 0;
            totalBytes = 0;
            var res = FindPayloadResourceName();
            if (res == null) { return; }

            try
            {
                using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(res))
                using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
                {
                    foreach (var entry in zip.Entries)
                    {
                        if (string.IsNullOrEmpty(entry.Name)) { continue; }
                        fileCount++;
                        totalBytes += entry.Length;
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        // ==================================================================
        //  运行环境检查
        // ==================================================================

        /// <summary>
        /// 检测 .NET Framework 4.8 是否就绪。
        /// 判据是注册表 Release 值（4.8 的起始版本号是 528040），
        /// 这比读文件版本可靠 —— 系统目录里的 mscorlib 版本号在装了更高版本后不会变。
        /// </summary>
        public static bool IsDotNet48Installed(out string detail)
        {
            const int Release48 = 528040;   // .NET Framework 4.8
            const int Release481 = 533320;  // .NET Framework 4.8.1

            try
            {
                using (var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32))
                using (var key = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full"))
                {
                    if (key == null)
                    {
                        detail = "未找到 .NET Framework 4.x 的安装记录，需要先安装 4.8。";
                        return false;
                    }

                    var versionText = key.GetValue("Version") as string;
                    if (string.IsNullOrEmpty(versionText)) { versionText = "4.x"; }

                    var releaseObject = key.GetValue("Release");
                    var release = 0;
                    if (releaseObject is int)
                    {
                        release = (int)releaseObject;
                    }
                    else if (releaseObject != null)
                    {
                        int.TryParse(releaseObject.ToString(), out release);
                    }

                    if (release >= Release481)
                    {
                        detail = "已就绪：.NET Framework " + versionText + "（Release " + release + "，高于 4.8）。";
                        return true;
                    }
                    if (release >= Release48)
                    {
                        detail = "已就绪：.NET Framework " + versionText + "（Release " + release + "）。";
                        return true;
                    }

                    detail = "当前是 .NET Framework " + versionText + "（Release " + release +
                             "），低于 4.8 要求的 " + Release48 + "，需要先升级。";
                    return false;
                }
            }
            catch (Exception ex)
            {
                detail = "读取 .NET Framework 版本失败：" + ex.Message;
                return false;
            }
        }

        // ==================================================================
        //  安装
        // ==================================================================

        public static InstallResult Install(SetupOptions options, Action<string> report, Action<int> progress)
        {
            var result = new InstallResult();

            var target = options != null && !string.IsNullOrWhiteSpace(options.TargetDirectory)
                ? options.TargetDirectory
                : DefaultTargetDirectory;

            try
            {
                target = Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch (Exception ex)
            {
                return InstallResult.Fail("安装路径不合法：" + ex.Message);
            }

            result.TargetDirectory = target;

            var resourceName = FindPayloadResourceName();
            if (resourceName == null)
            {
                return InstallResult.Fail(
                    "这个安装程序的内部载荷（payload.zip）不见了，文件不完整。请重新到 " + ReleasesUrl + " 下载。");
            }

            // ---------- 1. 目录 ----------
            Report(report, "准备安装目录：" + target);
            Progress(progress, 3);
            try
            {
                Directory.CreateDirectory(target);
            }
            catch (Exception ex)
            {
                return InstallResult.Fail("无法创建安装目录 " + target + "：" + ex.Message);
            }

            // ---------- 2. 关掉正在跑的旧版本 ----------
            StopRunningApp(report);
            Progress(progress, 6);

            // ---------- 3. 解压 ----------
            Report(report, "正在解压程序文件…");
            var fileCount = 0;
            long totalBytes = 0;

            try
            {
                using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName))
                using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
                {
                    var total = zip.Entries.Count;
                    if (total <= 0)
                    {
                        return InstallResult.Fail("安装包内部是空的，请重新下载。");
                    }

                    var index = 0;
                    foreach (var entry in zip.Entries)
                    {
                        index++;

                        // 目录项（以 / 结尾）没有文件名，跳过即可，解压文件时会把父目录建出来。
                        if (string.IsNullOrEmpty(entry.Name)) { continue; }

                        var relative = entry.FullName
                            .Replace('/', Path.DirectorySeparatorChar)
                            .Replace('\\', Path.DirectorySeparatorChar);

                        // 防目录穿越：zip 里若出现 ..\..\Windows 这种条目，一律丢弃。
                        if (relative.IndexOf("..", StringComparison.Ordinal) >= 0) { continue; }

                        var destination = Path.GetFullPath(Path.Combine(target, relative));
                        if (!IsInside(target, destination)) { continue; }

                        var parent = Path.GetDirectoryName(destination);
                        if (!string.IsNullOrEmpty(parent)) { Directory.CreateDirectory(parent); }

                        entry.ExtractToFile(destination, true);
                        fileCount++;
                        totalBytes += entry.Length;

                        Progress(progress, 6 + (int)(index * 78.0 / total));
                    }
                }
            }
            catch (Exception ex)
            {
                return InstallResult.Fail("解压失败：" + ex.Message + "（安装目录 " + target + "）");
            }

            result.FileCount = fileCount;
            result.TotalBytes = totalBytes;

            var exePath = Path.Combine(target, ExecutableName);
            result.ExecutablePath = exePath;

            if (!File.Exists(exePath))
            {
                return InstallResult.Fail("解压完成，但没有找到 " + ExecutableName + "，安装包可能是坏的。");
            }

            Report(report, "已解压 " + fileCount + " 个文件（" + result.SizeText + "）。");
            Progress(progress, 85);

            // ---------- 4. 快捷方式 ----------
            Report(report, "正在创建快捷方式…");
            var warnings = new StringBuilder();
            foreach (var link in ShortcutPaths())
            {
                var error = CreateShortcut(link, exePath, target, ShortcutName);
                if (error != null)
                {
                    warnings.Append("[").Append(link).Append("] ").Append(error).Append(" ");
                    Report(report, "创建快捷方式失败：" + link + " —— " + error);
                }
                else
                {
                    Report(report, "已创建快捷方式：" + link);
                }
            }
            result.ShortcutWarning = warnings.ToString().Trim();
            Progress(progress, 92);

            // ---------- 5. 把卸载程序放到安装目录 ----------
            Report(report, "正在放置卸载程序…");
            var uninstallerPath = Path.Combine(target, UninstallerName);
            try
            {
                var self = CurrentExecutablePath;
                if (!string.IsNullOrEmpty(self) &&
                    !string.Equals(self, uninstallerPath, StringComparison.OrdinalIgnoreCase))
                {
                    File.Copy(self, uninstallerPath, true);
                }
            }
            catch (Exception ex)
            {
                Report(report, "复制卸载程序失败（不影响使用）：" + ex.Message);
            }
            Progress(progress, 96);

            // ---------- 6. 注册卸载信息 ----------
            RegisterUninstallEntry(target, exePath, uninstallerPath, totalBytes, report);
            Progress(progress, 100);

            result.Ok = true;
            result.Message = "安装完成，共 " + fileCount + " 个文件（" + result.SizeText + "）。";
            Report(report, result.Message);
            return result;
        }

        // ==================================================================
        //  卸载
        // ==================================================================

        public static InstallResult Uninstall(SetupOptions options, Action<string> report, Action<int> progress)
        {
            var result = new InstallResult();
            Report(report, "开始卸载…");
            Progress(progress, 3);

            StopRunningApp(report);
            Progress(progress, 12);

            var dir = ResolveInstalledDirectory();
            result.TargetDirectory = dir;

            // ---------- 1. 快捷方式 ----------
            var removedLinks = 0;
            foreach (var link in ShortcutPaths())
            {
                try
                {
                    if (File.Exists(link))
                    {
                        File.Delete(link);
                        removedLinks++;
                        Report(report, "已删除快捷方式：" + link);
                    }
                }
                catch (Exception ex)
                {
                    Report(report, "删除快捷方式失败：" + link + " —— " + ex.Message);
                }
            }
            Report(report, "共删除 " + removedLinks + " 个快捷方式。");
            Progress(progress, 35);

            // 开始菜单文件夹若是空的，一并删掉，别留一个空文件夹在开始菜单里。
            try
            {
                var folder = StartMenuFolder;
                if (Directory.Exists(folder) && Directory.GetFiles(folder).Length == 0 && Directory.GetDirectories(folder).Length == 0)
                {
                    Directory.Delete(folder);
                    Report(report, "已删除开始菜单文件夹：" + folder);
                }
            }
            catch (Exception)
            {
            }
            Progress(progress, 50);

            // ---------- 2. 注册表 ----------
            RemoveUninstallEntry(report);
            Progress(progress, 62);

            // ---------- 3. 配置与日志（可选） ----------
            if (options != null && options.Purge)
            {
                try
                {
                    var userData = UserDataDirectory;
                    if (Directory.Exists(userData))
                    {
                        Directory.Delete(userData, true);
                        Report(report, "已删除配置与日志：" + userData);
                    }
                }
                catch (Exception ex)
                {
                    Report(report, "删除配置目录失败：" + ex.Message);
                }
            }
            else
            {
                Report(report, "已保留配置与日志：" + UserDataDirectory + "（要一起删就加 --purge）");
            }
            Progress(progress, 75);

            // ---------- 4. 程序文件 ----------
            var self = CurrentExecutablePath;
            if (!string.IsNullOrEmpty(self) && IsInside(dir, self))
            {
                // 卸载程序自己就在安装目录里，Windows 不允许删除正在运行的文件。
                // 交给一个后台 cmd：等本进程退出后再删目录。
                ScheduleDirectoryRemoval(dir);
                Report(report, "程序文件会在本程序退出后自动删除。");
            }
            else
            {
                try
                {
                    if (Directory.Exists(dir))
                    {
                        Directory.Delete(dir, true);
                        Report(report, "已删除程序目录：" + dir);
                    }
                    else
                    {
                        Report(report, "程序目录不存在，跳过：" + dir);
                    }
                }
                catch (Exception ex)
                {
                    Report(report, "删除程序目录失败：" + dir + " —— " + ex.Message);
                }
            }
            Progress(progress, 100);

            result.Ok = true;
            result.Message = "卸载完成。";
            Report(report, result.Message);
            return result;
        }

        /// <summary>安装目录：优先读卸载注册表项，读不到就退回默认路径。</summary>
        public static string ResolveInstalledDirectory()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(UninstallRegistryPath))
                {
                    if (key != null)
                    {
                        var location = key.GetValue("InstallLocation") as string;
                        if (!string.IsNullOrEmpty(location)) { return location; }
                    }
                }
            }
            catch (Exception)
            {
            }
            return DefaultTargetDirectory;
        }

        public static bool IsInstalled
        {
            get
            {
                try
                {
                    return File.Exists(Path.Combine(ResolveInstalledDirectory(), ExecutableName));
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }

        // ==================================================================
        //  快捷方式 / 注册表 / 进程 这些杂活
        // ==================================================================

        public static string DesktopShortcutPath
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                    ShortcutName + ".lnk");
            }
        }

        public static string StartMenuFolder
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                    ShortcutName);
            }
        }

        public static string StartMenuShortcutPath
        {
            get { return Path.Combine(StartMenuFolder, ShortcutName + ".lnk"); }
        }

        public static string[] ShortcutPaths()
        {
            return new[] { DesktopShortcutPath, StartMenuShortcutPath };
        }

        /// <summary>
        /// 用 WScript.Shell 建 .lnk。
        /// 不用自己手写 .lnk 二进制格式 —— 那个格式有 CLSID、LinkInfo、
        /// 可选字段一堆坑，写错了快捷方式就是白的。
        /// 返回 null 表示成功，否则返回失败原因。
        /// </summary>
        public static string CreateShortcut(string linkPath, string targetPath, string workingDirectory, string description)
        {
            try
            {
                var parent = Path.GetDirectoryName(linkPath);
                if (!string.IsNullOrEmpty(parent)) { Directory.CreateDirectory(parent); }

                var shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null)
                {
                    return "系统里没有注册 WScript.Shell，无法创建快捷方式。";
                }

                dynamic shell = Activator.CreateInstance(shellType);
                dynamic link = shell.CreateShortcut(linkPath);
                link.TargetPath = targetPath;
                link.WorkingDirectory = workingDirectory;
                link.Description = description;
                link.IconLocation = targetPath + ",0";
                link.WindowStyle = 1;
                link.Save();
                return null;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        private static void RegisterUninstallEntry(string target, string exePath, string uninstallerPath, long totalBytes, Action<string> report)
        {
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(UninstallRegistryPath))
                {
                    if (key == null)
                    {
                        Report(report, "写入卸载注册表项失败：CreateSubKey 返回 null。");
                        return;
                    }

                    var uninstallCommand = "\"" + uninstallerPath + "\" --uninstall";

                    key.SetValue("DisplayName", ProductTitle);
                    key.SetValue("DisplayVersion", ProductVersion);
                    key.SetValue("Publisher", Publisher);
                    key.SetValue("InstallLocation", target);
                    key.SetValue("DisplayIcon", exePath + ",0");
                    key.SetValue("UninstallString", uninstallCommand);
                    key.SetValue("QuietUninstallString", uninstallCommand + " --silent");
                    key.SetValue("URLInfoAbout", ProjectUrl);
                    key.SetValue("URLUpdateInfo", ReleasesUrl);
                    key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
                    key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                    key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                    if (totalBytes > 0)
                    {
                        key.SetValue("EstimatedSize", (int)(totalBytes / 1024), RegistryValueKind.DWord);
                    }
                }
                Report(report, "已在「程序和功能」里登记卸载项。");
            }
            catch (Exception ex)
            {
                Report(report, "写入卸载注册表项失败（不影响使用）：" + ex.Message);
            }
        }

        private static void RemoveUninstallEntry(Action<string> report)
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(UninstallRegistryPath))
                {
                    if (key == null)
                    {
                        Report(report, "卸载注册表项本来就不存在，跳过。");
                        return;
                    }
                }
                Registry.CurrentUser.DeleteSubKeyTree(UninstallRegistryPath, false);
                Report(report, "已从「程序和功能」里移除登记。");
            }
            catch (Exception ex)
            {
                Report(report, "删除卸载注册表项失败：" + ex.Message);
            }
        }

        /// <summary>
        /// 关闭正在运行的 LanMouseShare。
        /// 安装程序是 requireAdministrator，所以这里的 Kill 不会吃到 Access is denied
        /// （主程序同样是 requireAdministrator，普通权限的进程杀不掉它）。
        /// </summary>
        public static void StopRunningApp(Action<string> report)
        {
            Process[] list;
            try
            {
                list = Process.GetProcessesByName(ProcessName);
            }
            catch (Exception)
            {
                return;
            }

            if (list == null || list.Length == 0) { return; }

            foreach (var p in list)
            {
                Report(report, "正在关闭已运行的 " + ProcessName + "（PID " + p.Id + "）…");
                try { p.CloseMainWindow(); }
                catch (Exception) { }
            }

            Thread.Sleep(700);

            foreach (var p in list)
            {
                try
                {
                    if (!p.HasExited)
                    {
                        p.Kill();
                        p.WaitForExit(3000);
                    }
                }
                catch (Exception) { }
                finally
                {
                    try { p.Dispose(); } catch (Exception) { }
                }
            }
        }

        public static bool LaunchApp(string exePath)
        {
            try
            {
                if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath)) { return false; }
                var psi = new ProcessStartInfo(exePath);
                psi.WorkingDirectory = Path.GetDirectoryName(exePath);
                psi.UseShellExecute = true;
                Process.Start(psi);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static bool OpenUrl(string url)
        {
            try
            {
                var psi = new ProcessStartInfo(url);
                psi.UseShellExecute = true;
                Process.Start(psi);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// 把自己藏到一个后台 cmd 里，等本进程退出后删掉整个安装目录。
        /// ping 在这里只是当 sleep 用 —— timeout 命令在 stdin 被重定向时会直接失败。
        /// </summary>
        private static void ScheduleDirectoryRemoval(string dir)
        {
            try
            {
                var quoted = "\"" + dir.TrimEnd(Path.DirectorySeparatorChar) + "\"";
                var command = "/C ping 127.0.0.1 -n 3 > nul & rmdir /S /Q " + quoted +
                              " & if exist " + quoted + " (ping 127.0.0.1 -n 4 > nul & rmdir /S /Q " + quoted + ")";

                var psi = new ProcessStartInfo("cmd.exe", command);
                psi.CreateNoWindow = true;
                psi.UseShellExecute = false;
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                Process.Start(psi);
            }
            catch (Exception)
            {
            }
        }

        /// <summary>child 是否位于 parent 目录之内（大小写不敏感，用于挡住目录穿越）。</summary>
        public static bool IsInside(string parent, string child)
        {
            if (string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(child)) { return false; }

            var p = Path.GetFullPath(parent)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            var c = Path.GetFullPath(child);

            return c.StartsWith(p, StringComparison.OrdinalIgnoreCase);
        }

        private static void Report(Action<string> report, string message)
        {
            if (report == null || message == null) { return; }
            try { report(message); }
            catch (Exception) { }
        }

        private static void Progress(Action<int> progress, int percent)
        {
            if (progress == null) { return; }
            try { progress(percent); }
            catch (Exception) { }
        }
    }
}
