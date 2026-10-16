using System;
using System.Diagnostics;
using System.IO;

namespace LanMouseShare.Core.Net
{
    /// <summary>
    /// Windows 防火墙放行助手（部署代理）。
    ///
    /// 为什么必须有这个东西：程序第一次监听端口时系统会弹一次「是否允许访问」的对话框，
    /// 但那一次弹窗很脆弱 —— 用户点错、被其它窗口盖住、或者策略把弹窗关掉了，
    /// 结果就是规则压根没建。此后主机照常监听、日志一切正常，
    /// 可副机发过来的 SYN 会被防火墙**静默丢弃**（不是拒绝！），副机只能干等到超时。
    /// 现象就是「两台电脑连不上」，而且两边日志都看不出毛病。
    ///
    /// 这里用 netsh advfirewall 而不是 PowerShell 或 COM：
    ///   * netsh 从 Vista / Server 2008 起就在，Win7 SP1 稳定可用；
    ///   * 不依赖 Microsoft.CSharp 的 dynamic 绑定，C# 5 编译器下不会出意外；
    ///   * 程序本来就 requireAdministrator，直接就能建规则。
    /// </summary>
    public static class FirewallHelper
    {
        /// <summary>程序级入站规则名（不绑定端口，改端口也不用重建）。</summary>
        public const string ProgramRuleName = "LanMouseShare (程序)";

        private const int NetshTimeoutMs = 15000;

        public static string RuleNameFor(int port)
        {
            return "LanMouseShare (TCP " + port.ToString() + ")";
        }

        /// <summary>指定端口的入站放行规则是否已经存在。</summary>
        public static bool RuleExists(int port)
        {
            int code;
            RunNetsh("advfirewall firewall show rule name=\"" + RuleNameFor(port) + "\"", out code);
            return code == 0;
        }

        /// <summary>程序级入站规则是否已经存在。</summary>
        public static bool ProgramRuleExists()
        {
            int code;
            RunNetsh("advfirewall firewall show rule name=\"" + ProgramRuleName + "\"", out code);
            return code == 0;
        }

        /// <summary>
        /// 确保「所有配置文件」下都放行 TCP 端口的入站连接。
        /// profile=any 是有意的：同一台笔记本在家里（专用）和公司（域）之间来回切换，
        /// 只放行单一配置文件的话换个网络就连不上，这种问题极难排查。
        /// </summary>
        public static bool EnsureRule(int port, out string error)
        {
            error = null;

            if (RuleExists(port))
            {
                return true;
            }

            string args = "advfirewall firewall add rule name=\"" + RuleNameFor(port)
                          + "\" dir=in action=allow protocol=TCP localport=" + port.ToString()
                          + " profile=any enable=yes";

            int code;
            string output = RunNetsh(args, out code);
            if (code != 0)
            {
                error = "创建防火墙规则失败（netsh 退出码 " + code.ToString() + "）：" + output;
                return false;
            }

            return true;
        }

        /// <summary>放行本程序自身（覆盖任意端口，将来端口可配置时不用再动防火墙）。</summary>
        public static bool EnsureProgramRule(out string error)
        {
            error = null;

            try
            {
                string exe = ProcessEnvironmentExePath();
                if (string.IsNullOrEmpty(exe))
                {
                    error = "拿不到本程序路径";
                    return false;
                }

                if (ProgramRuleExists())
                {
                    return true;
                }

                string args = "advfirewall firewall add rule name=\"" + ProgramRuleName
                              + "\" dir=in action=allow program=\"" + exe
                              + "\" profile=any enable=yes";

                int code;
                string output = RunNetsh(args, out code);
                if (code != 0)
                {
                    error = "创建程序规则失败（netsh 退出码 " + code.ToString() + "）：" + output;
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                error = "创建程序规则异常：" + ex.Message;
                return false;
            }
        }

        /// <summary>端口规则 + 程序规则一起放行，写给 UI 的一键按钮用。</summary>
        public static bool EnsureAll(int port, out string error)
        {
            if (!EnsureRule(port, out error))
            {
                return false;
            }

            string programError;
            if (!EnsureProgramRule(out programError))
            {
                // 程序规则失败不算致命：端口规则才是连通的关键，只把原因带回去。
                error = programError;
            }

            return true;
        }

        public static bool RemoveRule(int port, out string error)
        {
            error = null;

            if (!RuleExists(port))
            {
                return true;
            }

            int code;
            string output = RunNetsh("advfirewall firewall delete rule name=\"" + RuleNameFor(port) + "\"", out code);
            if (code != 0)
            {
                error = "删除防火墙规则失败（netsh 退出码 " + code.ToString() + "）：" + output;
                return false;
            }

            return true;
        }

        /// <summary>给诊断页用的一行人话。</summary>
        public static string Describe(int port)
        {
            string text = "防火墙放行（入站 TCP " + port.ToString() + "）：";
            text += RuleExists(port) ? "已放行" : "未放行";
            text += "；程序规则：";
            text += ProgramRuleExists() ? "已放行" : "未放行";
            return text;
        }

        private static string ProcessEnvironmentExePath()
        {
            try
            {
                System.Reflection.Assembly asm = System.Reflection.Assembly.GetEntryAssembly();
                if (asm != null)
                {
                    return asm.Location;
                }
            }
            catch (Exception)
            {
            }

            return null;
        }

        private static string RunNetsh(string arguments, out int exitCode)
        {
            exitCode = -1;
            string output = string.Empty;

            try
            {
                string netsh = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "netsh.exe");

                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = netsh;
                psi.Arguments = arguments;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;

                Process p = Process.Start(psi);
                if (p == null)
                {
                    return "无法启动 netsh.exe";
                }

                output = p.StandardOutput.ReadToEnd();
                string err = p.StandardError.ReadToEnd();

                if (!p.WaitForExit(NetshTimeoutMs))
                {
                    try { p.Kill(); }
                    catch (Exception) { }

                    return "netsh 超时未返回";
                }

                try { exitCode = p.ExitCode; }
                catch (Exception) { }

                p.Dispose();

                string text = (output == null ? string.Empty : output).Trim();
                if (!string.IsNullOrEmpty(err))
                {
                    text = text + " " + err.Trim();
                }

                return text;
            }
            catch (Exception ex)
            {
                return "调用 netsh 异常：" + ex.Message;
            }
        }
    }
}
