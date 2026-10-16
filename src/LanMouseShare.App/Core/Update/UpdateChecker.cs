using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using LanMouseShare.Core.Diagnostics;
using LanMouseShare.Core.Json;

namespace LanMouseShare.Core.Update
{
    /// <summary>一次检查更新的结果。</summary>
    public sealed class UpdateInfo
    {
        public bool Succeeded { get; set; }
        public bool UpdateAvailable { get; set; }
        public string CurrentVersion { get; set; }
        public string LatestVersion { get; set; }
        public string ReleaseName { get; set; }
        public string ReleaseUrl { get; set; }
        public string DownloadUrl { get; set; }
        public string PublishedAt { get; set; }
        public string Notes { get; set; }
        public string Error { get; set; }
        public bool RateLimited { get; set; }

        public static UpdateInfo Failure(string error)
        {
            UpdateInfo info = new UpdateInfo();
            info.Succeeded = false;
            info.Error = error;
            return info;
        }

        /// <summary>给界面用的一句话总结。</summary>
        public string Summarize()
        {
            if (!Succeeded)
            {
                return "检查更新失败：" + (Error ?? "未知原因");
            }

            if (!UpdateAvailable)
            {
                return "已是最新版本（" + CurrentVersion + "）";
            }

            return "发现新版本 " + LatestVersion + "（当前 " + CurrentVersion + "）";
        }
    }

    /// <summary>
    /// 检查更新（升级代理产出，第六阶段附加需求）。
    ///
    /// 实现要点：
    ///   * 数据源用 GitHub Releases 的公开 REST 接口，不需要任何鉴权；
    ///   * 只读 /releases/latest，未登录时每小时 60 次配额，启动时查一次完全够用；
    ///   * 必须显式打开 TLS 1.2：Win7 的 .NET 默认还在 TLS 1.0，会被 GitHub 直接拒绝；
    ///   * 整个检查过程不抛异常，失败也要返回人能看懂的原因（不联网是常态，不是错误）。
    /// </summary>
    public static class UpdateChecker
    {
        public const string Owner = "Zxin-Pro";
        public const string Repo = "LanMouseShare";

        /// <summary>默认超时：8 秒。检查更新绝不能拖慢启动。</summary>
        public const int DefaultTimeoutMs = 8000;

        public static string ReleasesPageUrl
        {
            get { return "https://github.com/" + Owner + "/" + Repo + "/releases"; }
        }

        public static string LatestReleaseApiUrl
        {
            get { return "https://api.github.com/repos/" + Owner + "/" + Repo + "/releases/latest"; }
        }

        /// <summary>当前程序集版本，例如 0.6.0.0。</summary>
        public static Version GetCurrentVersion()
        {
            try
            {
                Assembly assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
                if (assembly != null)
                {
                    Version version = assembly.GetName().Version;
                    if (version != null)
                    {
                        return version;
                    }
                }
            }
            catch (Exception)
            {
            }

            return new Version(0, 0, 0, 0);
        }

        public static string GetCurrentVersionText()
        {
            Version version = GetCurrentVersion();
            return version.Major.ToString(CultureInfo.InvariantCulture) + "." +
                   version.Minor.ToString(CultureInfo.InvariantCulture) + "." +
                   version.Build.ToString(CultureInfo.InvariantCulture);
        }

        public static UpdateInfo Check()
        {
            return Check(DefaultTimeoutMs);
        }

        public static UpdateInfo Check(int timeoutMs)
        {
            try
            {
                // Win7 必须显式开 TLS 1.2，否则 GitHub 直接断连
                try
                {
                    ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                }
                catch (Exception)
                {
                }

                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(LatestReleaseApiUrl);
                request.Method = "GET";
                request.UserAgent = "LanMouseShare/" + GetCurrentVersionText() + " (Windows; .NET " +
                                    Environment.Version.ToString() + ")";
                request.Accept = "application/vnd.github+json";
                request.Timeout = timeoutMs;
                request.ReadWriteTimeout = timeoutMs;
                request.AllowAutoRedirect = true;
                request.Proxy = WebRequest.DefaultWebProxy;
                if (request.Proxy != null)
                {
                    request.Proxy.Credentials = CredentialCache.DefaultCredentials;
                }

                string body;
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                using (Stream stream = response.GetResponseStream())
                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                {
                    body = reader.ReadToEnd();
                }

                return ParseRelease(body);
            }
            catch (WebException ex)
            {
                HttpWebResponse response = ex.Response as HttpWebResponse;
                if (response != null)
                {
                    int status = (int)response.StatusCode;
                    if (status == 404)
                    {
                        return UpdateInfo.Failure("仓库还没有发布任何 Release（HTTP 404）");
                    }
                    if (status == 403)
                    {
                        UpdateInfo info = UpdateInfo.Failure("GitHub 接口访问受限（HTTP 403），可能已超出每小时 60 次的匿名配额，稍后再试");
                        info.RateLimited = true;
                        return info;
                    }

                    return UpdateInfo.Failure("GitHub 返回 HTTP " + status.ToString(CultureInfo.InvariantCulture));
                }

                return UpdateInfo.Failure("网络不可达：" + ex.Message);
            }
            catch (Exception ex)
            {
                return UpdateInfo.Failure(ex.GetType().Name + "：" + ex.Message);
            }
        }

        private static UpdateInfo ParseRelease(string body)
        {
            string parseError;
            Dictionary<string, object> map = Json.Json.AsObject(Json.Json.TryParse(body, out parseError));
            if (map == null)
            {
                return UpdateInfo.Failure("无法解析 GitHub 返回的数据：" + (parseError ?? "不是 JSON 对象"));
            }

            UpdateInfo info = new UpdateInfo();
            info.Succeeded = true;
            info.CurrentVersion = GetCurrentVersionText();
            info.ReleaseName = Json.Json.GetString(map, "name", null);
            info.ReleaseUrl = Json.Json.GetString(map, "html_url", ReleasesPageUrl);
            info.PublishedAt = Json.Json.GetString(map, "published_at", null);
            info.Notes = Json.Json.GetString(map, "body", null);

            string tag = Json.Json.GetString(map, "tag_name", null);
            if (string.IsNullOrEmpty(tag))
            {
                tag = info.ReleaseName;
            }

            info.LatestVersion = NormalizeVersionText(tag);
            info.DownloadUrl = FindZipAsset(map);
            if (string.IsNullOrEmpty(info.DownloadUrl))
            {
                info.DownloadUrl = info.ReleaseUrl;
            }

            if (string.IsNullOrEmpty(info.Notes))
            {
                info.Notes = "（该 Release 没有写说明）";
            }
            else if (info.Notes.Length > 1500)
            {
                info.Notes = info.Notes.Substring(0, 1500) + Environment.NewLine + "……（说明过长，已截断，完整内容见 Release 页面）";
            }

            info.UpdateAvailable = CompareVersions(info.LatestVersion, info.CurrentVersion) > 0;
            return info;
        }

        /// <summary>在 assets 里找第一个 .zip 附件的下载地址。</summary>
        private static string FindZipAsset(Dictionary<string, object> map)
        {
            object raw;
            if (!map.TryGetValue("assets", out raw))
            {
                return null;
            }

            List<object> assets = raw as List<object>;
            if (assets == null)
            {
                return null;
            }

            string fallback = null;
            for (int i = 0; i < assets.Count; i++)
            {
                Dictionary<string, object> asset = Json.Json.AsObject(assets[i]);
                if (asset == null)
                {
                    continue;
                }

                string url = Json.Json.GetString(asset, "browser_download_url", null);
                string name = Json.Json.GetString(asset, "name", string.Empty);
                if (string.IsNullOrEmpty(url))
                {
                    continue;
                }

                if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    return url;
                }

                if (fallback == null)
                {
                    fallback = url;
                }
            }

            return fallback;
        }

        /// <summary>把 "v0.6.0" / "0.6" / "release-1.2.3" 之类的标签规整成 x.y.z。</summary>
        public static string NormalizeVersionText(string tag)
        {
            if (string.IsNullOrEmpty(tag))
            {
                return "0.0.0";
            }

            StringBuilder sb = new StringBuilder();
            bool started = false;
            for (int i = 0; i < tag.Length; i++)
            {
                char c = tag[i];
                if (c >= '0' && c <= '9')
                {
                    started = true;
                    sb.Append(c);
                }
                else if (c == '.' && started)
                {
                    sb.Append(c);
                }
                else if (started)
                {
                    break;
                }
            }

            string text = sb.ToString().Trim('.');
            return string.IsNullOrEmpty(text) ? "0.0.0" : text;
        }

        /// <summary>a &gt; b 返回正数。任一侧解析不了就返回 0（当作相同，宁可不升级也不误报）。</summary>
        public static int CompareVersions(string a, string b)
        {
            Version va;
            Version vb;
            if (!TryParseVersion(a, out va) || !TryParseVersion(b, out vb))
            {
                return 0;
            }

            return va.CompareTo(vb);
        }

        private static bool TryParseVersion(string text, out Version version)
        {
            version = null;
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            string normalized = NormalizeVersionText(text);
            if (Version.TryParse(normalized, out version))
            {
                return true;
            }

            // "0.6" 这类补成 "0.6.0"
            if (Version.TryParse(normalized + ".0", out version))
            {
                return true;
            }

            return false;
        }

        /// <summary>用默认浏览器打开 URL。失败只记日志，不打扰用户。</summary>
        public static bool TryOpenUrl(string url)
        {
            if (string.IsNullOrEmpty(url))
            {
                return false;
            }

            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn("打开链接失败（" + url + "）：" + ex.Message);
                return false;
            }
        }
    }
}
