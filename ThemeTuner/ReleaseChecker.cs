using System;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace TxTools.ThemeTuner
{
    internal static class ReleaseChecker
    {
        internal const string ProjectUrl = "https://github.com/ShuiYinMaster/PDPS_TxTools";
        internal static string BuildLabel
        {
            get
            {
                string release = typeof(ReleaseChecker).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
                return string.IsNullOrWhiteSpace(release) ? "本地构建 · 程序集 " + typeof(ReleaseChecker).Assembly.GetName().Version : release;
            }
        }

        internal static bool TryVersion(string tag, out Version version)
        {
            version = null;
            var match = Regex.Match(tag ?? "", @"^(?:TxTools[_-]?|v)?(\d+\.\d+\.\d+(?:\.\d+)?)(?:\+[\w.-]+)?$", RegexOptions.IgnoreCase);
            if (!match.Success || !Version.TryParse(match.Groups[1].Value, out var parsed)) return false;
            version = new Version(parsed.Major, parsed.Minor, parsed.Build, Math.Max(0, parsed.Revision));
            return true;
        }

        // Called only by the explicit check button. No startup hook or background polling.
        internal static async Task<string> CheckAsync(CancellationToken cancellation, HttpMessageHandler handler = null)
        {
            using (var client = new HttpClient(handler ?? new HttpClientHandler()) { Timeout = TimeSpan.FromSeconds(12), MaxResponseContentBufferSize = 2 * 1024 * 1024 })
            {
                client.DefaultRequestHeaders.UserAgent.ParseAdd("TxTools-ManualUpdateCheck/1.0");
                client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
                using (var response = await client.GetAsync("https://api.github.com/repos/ShuiYinMaster/PDPS_TxTools/releases/latest", cancellation))
                {
                    if (response.StatusCode == HttpStatusCode.NotFound) return "未找到公开稳定发行版，可打开 GitHub 发行版页面核对。";
                    if ((int)response.StatusCode == 403 || (int)response.StatusCode == 429) return "GitHub 暂时限制了请求，请稍后重试或打开发行版页面。";
                    response.EnsureSuccessStatusCode();
                    var release = JObject.Parse(await response.Content.ReadAsStringAsync());
                    if ((bool?)release["draft"] == true || (bool?)release["prerelease"] == true) return "未找到稳定发行版。";
                    string tag = (string)release["tag_name"];
                    if (string.IsNullOrWhiteSpace(tag)) throw new FormatException("发行版缺少版本标签。");
                    string local = typeof(ReleaseChecker).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
                    string verdict = "当前构建没有可比较的正式版本号，请查看发行说明核对。";
                    if (TryVersion(local, out var current) && TryVersion(tag, out var latest))
                        verdict = latest > current ? "有新版本，可在发行版页面查看更新。" : latest == current ? "当前已是最新稳定版。" : "当前构建版本高于最新稳定版。";
                    string date = DateTimeOffset.TryParse((string)release["published_at"], out var published)
                        ? " · " + published.ToOffset(TimeSpan.FromHours(8)).ToString("yyyy-MM-dd") : "";
                    return "最新稳定版：" + tag + date + "\n" + verdict;
                }
            }
        }
    }
}
