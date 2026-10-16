using System;
using System.Reflection;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace LanMouseShare.Core.Infrastructure
{
    /// <summary>
    /// 运行环境探测：系统版本、位数、管理员权限、目标框架。
    ///
    /// 注意：Windows 版本号只有在 app.manifest 里声明了 supportedOS 之后
    /// 才会返回真实值（否则一律谎报 6.2）。app.manifest 已经列出
    /// Win7 / Win8 / Win8.1 / Win10-Win11 的 GUID。
    /// </summary>
    public static class ProcessEnvironment
    {
        /// <summary>当前进程是否以管理员身份运行。</summary>
        public static bool IsElevated
        {
            get
            {
                try
                {
                    using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                    {
                        WindowsPrincipal principal = new WindowsPrincipal(identity);
                        return principal.IsInRole(WindowsBuiltInRole.Administrator);
                    }
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }

        public static bool Is64BitOperatingSystem
        {
            get
            {
                try
                {
                    return Environment.Is64BitOperatingSystem;
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }

        public static bool Is64BitProcess
        {
            get
            {
                try
                {
                    return Environment.Is64BitProcess;
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }

        /// <summary>面向用户的系统名称，明确区分 Win7 / Win10 / Win11。</summary>
        public static string DescribeOperatingSystem()
        {
            Version v = Environment.OSVersion.Version;
            string name;

            if (v.Major == 6 && v.Minor == 1)
            {
                name = "Windows 7";
            }
            else if (v.Major == 6 && v.Minor == 2)
            {
                name = "Windows 8";
            }
            else if (v.Major == 6 && v.Minor == 3)
            {
                name = "Windows 8.1";
            }
            else if (v.Major == 10)
            {
                // Windows 11 的起始内部版本号是 22000。
                name = (v.Build >= 22000) ? "Windows 11" : "Windows 10";
            }
            else
            {
                name = "未知 Windows 版本";
            }

            return name + "（内核 " + v.ToString() + "，" +
                   (Is64BitOperatingSystem ? "64 位" : "32 位") + "）";
        }

        /// <summary>进程与操作系统的位数组合，AnyCPU 下用于确认 SendInput 结构体布局。</summary>
        public static string DescribeArchitecture()
        {
            string processBits = Is64BitProcess ? "64 位" : "32 位";
            string osBits = Is64BitOperatingSystem ? "64 位" : "32 位";
            return "进程 " + processBits + " / 系统 " + osBits + "（AnyCPU 编译，运行时自适应）";
        }

        /// <summary>程序集版本号。</summary>
        public static string GetProductVersionText()
        {
            try
            {
                Assembly assembly = Assembly.GetExecutingAssembly();
                AssemblyName name = assembly.GetName();
                string version = (name.Version == null) ? "1.0.0.0" : name.Version.ToString();
                return "LanMouseShare " + version;
            }
            catch (Exception)
            {
                return "LanMouseShare（版本未知）";
            }
        }

        /// <summary>本程序集编译时使用的目标框架，例如 ".NETFramework,Version=v4.8"。</summary>
        public static string GetTargetFrameworkName()
        {
            try
            {
                Assembly assembly = Assembly.GetExecutingAssembly();
                object[] attributes = assembly.GetCustomAttributes(typeof(TargetFrameworkAttribute), false);
                if (attributes != null && attributes.Length > 0)
                {
                    TargetFrameworkAttribute attribute = attributes[0] as TargetFrameworkAttribute;
                    if (attribute != null)
                    {
                        return attribute.FrameworkName;
                    }
                }
            }
            catch (Exception)
            {
                // 忽略，走下面的兜底返回值。
            }
            return "未知（无法读取 TargetFrameworkAttribute）";
        }
    }
}
