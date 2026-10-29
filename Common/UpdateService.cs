using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WinKit.Common
{
    /// <summary>
    /// 单个更新源的探测结果。
    /// </summary>
    public class UpdateSourceProbe
    {
        public string Name { get; set; } = "";
        public bool Success { get; set; }
        public string? Tag { get; set; }          // 如 "v2.3.0"
        public string? DownloadUrl { get; set; }  // 安装包直链（可能为空，延迟到下载时拼接）
        public double LatencyMs { get; set; }     // 探测耗时（毫秒）
    }

    /// <summary>
    /// 一次更新检查的汇总结果。
    /// </summary>
    public class UpdateCheckResult
    {
        public bool Probed { get; set; }              // 至少一个源探测成功
        public bool HasUpdate { get; set; }           // 远端版本 > 本地版本
        public string? LatestVersion { get; set; }    // 选中的远端版本 tag
        public string? CurrentVersion { get; set; }
        public string? SourceName { get; set; }       // 选中源的名称
        public string? DownloadUrl { get; set; }      // 选中源的安装包直链
        public double LatencyMs { get; set; }
    }

    /// <summary>
    /// 自动更新服务 — 多源并发探测版本 + 下载前镜像测速选最快源。
    ///
    /// 版本探测（2026-10-29 实测匿名可达的两条路）：
    ///  ① Gitee  GET api/v5/releases/latest（匿名 200，0.45s）→ tag_name + assets 直链；
    ///  ② GitHub HEAD releases/latest 302 重定向（绕开匿名 API 403）→ Location 解析 tag。
    ///  ⚠️ CNB 不能做版本探测：其 release API 与 git/raw 文件接口**匿名一律 401**（公共仓库也一样），
    ///     网页 `/-/raw/` 返回 SPA 的 "Page not found" HTML（HTTP 200 但非 JSON）。因此 CNB 只作**下载镜像**。
    ///
    /// 下载选源：三个镜像（Gitee / GitHub / CNB）用 Range 请求并发测 TTFB，取最快可用的那个下载。
    /// 版本选源规则：只认探测成功的源，取版本最高者（避免「最快的源未同步发版」误判无更新），并列取延迟最低。
    /// </summary>
    public class UpdateService
    {
        // ── 各源地址（发版时同步维护，见 AGENTS.md「发版约定」）────────────
        private const string GiteeApiUrl = "https://gitee.com/api/v5/repos/worhllo/WinKit/releases/latest";
        private const string GiteeDownloadBase = "https://gitee.com/worhllo/WinKit/releases/download/{0}/WinKit-{0}-win-x64.zip";
        private const string GitHubLatestUrl = "https://github.com/oishijie/WinKit/releases/latest";
        private const string GitHubDownloadFmt = "https://github.com/oishijie/WinKit/releases/download/{0}/WinKit-{0}-win-x64.zip";
        private const string CnbDownloadFmt = "https://cnb.cool/wordoi.rlc/winkit-customer/-/releases/download/{0}/WinKit-{0}-win-x64.zip";

        private static readonly TimeSpan ProbeTimeout = TimeSpan.FromMilliseconds(1500);
        private static readonly TimeSpan MirrorTimeout = TimeSpan.FromMilliseconds(2500);

        private readonly HttpClient _http;

        /// <summary>状态消息（检查中 / 已是最新 / 失败等），由 App 转成气泡或设置页文案</summary>
        public event Action<string>? Message;

        public UpdateService()
        {
            _http = new HttpClient();
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("WinKit-Updater");
            // GitHub 302 需要手动读 Location，全局关自动重定向
            _http.DefaultRequestHeaders.ExpectContinue = false;
        }

        /// <summary>本机当前版本（csproj 的 Version 属性 → 程序集版本）</summary>
        public static string CurrentVersion
        {
            get
            {
                var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
                return v == null ? "0.0.0" : v.ToString(3);
            }
        }

        /// <summary>
        /// 多源并发探测并汇总结果。网络异常静默，全失败时 Probed=false。
        /// </summary>
        public async Task<UpdateCheckResult> CheckAsync()
        {
            var gitee = ProbeGiteeAsync();
            var github = ProbeGitHubAsync();

            var probes = new[] { await gitee, await github };
            var ok = new List<UpdateSourceProbe>();
            foreach (var p in probes)
            {
                if (p.Success && !string.IsNullOrEmpty(p.Tag)) ok.Add(p);
            }

            var result = new UpdateCheckResult { CurrentVersion = CurrentVersion };
            if (ok.Count == 0)
            {
                Message?.Invoke("更新检查失败：各源均不可达（检查网络）");
                return result;
            }

            // 选源：版本最高优先，并列取延迟最低
            UpdateSourceProbe best = ok[0];
            foreach (var p in ok)
            {
                if (CompareTag(p.Tag!, best.Tag!) > 0 ||
                    (CompareTag(p.Tag!, best.Tag!) == 0 && p.LatencyMs < best.LatencyMs))
                {
                    best = p;
                }
            }

            result.Probed = true;
            result.LatestVersion = best.Tag;
            result.SourceName = best.Name;
            result.DownloadUrl = best.DownloadUrl;
            result.LatencyMs = best.LatencyMs;
            result.HasUpdate = CompareTag(best.Tag!, "v" + CurrentVersion) > 0;
            return result;
        }

        /// <summary>tag 比较：v2.3.0 → Version(2,3,0)，解析失败视为更小</summary>
        private static int CompareTag(string a, string b)
        {
            var va = ParseVersion(a);
            var vb = ParseVersion(b);
            return va.CompareTo(vb);
        }

        private static Version ParseVersion(string tag)
        {
            var s = tag.TrimStart('v', 'V');
            return Version.TryParse(s, out var v) ? v : new Version(0, 0, 0);
        }

        /// <summary>下载安装包到 TEMP，返回文件全路径；失败返回 null。progress 为 (已收字节, 总字节)。</summary>
        public async Task<string?> DownloadAsync(string tag, string downloadUrl, Action<long, long>? progress = null)
        {
            try
            {
                // 下载前对三个镜像并发测速（Range 请求取 TTFB），选最快可用的；全失败回退调用方给的地址
                var url = await PickFastestMirrorAsync(tag, downloadUrl).ConfigureAwait(false);
                var fileName = $"WinKit-{tag}-win-x64.zip";
                var target = Path.Combine(Path.GetTempPath(), fileName);

                using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                if (!resp.IsSuccessStatusCode) return null;

                var total = resp.Content.Headers.ContentLength ?? -1;
                await using var src = await resp.Content.ReadAsStreamAsync();
                await using var dst = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

                var buffer = new byte[81920];
                long received = 0;
                int read;
                while ((read = await src.ReadAsync(buffer)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, read));
                    received += read;
                    progress?.Invoke(received, total);
                }
                return received > 0 ? target : null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"UpdateService.DownloadAsync: {ex.Message}");
                return null;
            }
        }

        // ── 版本探测实现 ──────────────────────────────────────────────

        /// <summary>Gitee：api/v5 匿名 200，响应含 tag_name 与 assets[].browser_download_url</summary>
        private async Task<UpdateSourceProbe> ProbeGiteeAsync()
        {
            var probe = new UpdateSourceProbe { Name = "Gitee" };
            var sw = Stopwatch.StartNew();
            try
            {
                using var cts = new CancellationTokenSource(ProbeTimeout);
                var json = await _http.GetStringAsync(GiteeApiUrl, cts.Token);
                using var doc = JsonDocument.Parse(json);
                probe.Tag = doc.RootElement.GetProperty("tag_name").GetString();
                if (doc.RootElement.TryGetProperty("assets", out var assets) && assets.GetArrayLength() > 0)
                {
                    probe.DownloadUrl = assets[0].GetProperty("browser_download_url").GetString();
                }
                probe.Success = !string.IsNullOrEmpty(probe.Tag);
            }
            catch { probe.Success = false; }
            probe.LatencyMs = sw.Elapsed.TotalMilliseconds;
            return probe;
        }

        /// <summary>GitHub：匿名 API 403，改用 releases/latest 的 302 重定向解析 tag（HEAD 不下载）</summary>
        private async Task<UpdateSourceProbe> ProbeGitHubAsync()
        {
            var probe = new UpdateSourceProbe { Name = "GitHub" };
            var sw = Stopwatch.StartNew();
            try
            {
                using var cts = new CancellationTokenSource(ProbeTimeout);
                using var req = new HttpRequestMessage(HttpMethod.Head, GitHubLatestUrl);
                using var resp = await _http.SendAsync(req, cts.Token);
                if ((int)resp.StatusCode == 302 || (int)resp.StatusCode == 301)
                {
                    var loc = resp.Headers.Location?.ToString() ?? "";
                    // https://github.com/oishijie/WinKit/releases/tag/v2.3.0
                    var idx = loc.LastIndexOf("/tag/", StringComparison.Ordinal);
                    if (idx >= 0)
                    {
                        probe.Tag = loc[(idx + 5)..];
                        probe.Success = true;
                    }
                }
            }
            catch { probe.Success = false; }
            probe.LatencyMs = sw.Elapsed.TotalMilliseconds;
            return probe;
        }

        /// <summary>
        /// 三个下载镜像并发测速（Range 请求 0-0 只取 1 字节，测到首字节即算通），返回最快的可用 URL。
        /// 全部失败时回退 preferred（调用方传入的探测源直链），再回退 Gitee 固定命名。
        /// </summary>
        private async Task<string> PickFastestMirrorAsync(string tag, string? preferred)
        {
            var candidates = new List<string>();
            if (!string.IsNullOrEmpty(preferred)) candidates.Add(preferred);
            candidates.Add(string.Format(GiteeDownloadBase, tag));
            candidates.Add(string.Format(CnbDownloadFmt, tag));
            candidates.Add(string.Format(GitHubDownloadFmt, tag));

            var tasks = candidates.Distinct().Select(async url =>
            {
                var sw = Stopwatch.StartNew();
                try
                {
                    using var cts = new CancellationTokenSource(MirrorTimeout);
                    using var req = new HttpRequestMessage(HttpMethod.Get, url);
                    req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
                    using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                    var code = (int)resp.StatusCode;
                    if (code == 200 || code == 206)
                        return (Url: url, Ok: true, Ms: sw.Elapsed.TotalMilliseconds);
                }
                catch { /* 该镜像不可用 */ }
                return (Url: url, Ok: false, Ms: double.MaxValue);
            }).ToArray();

            var results = await Task.WhenAll(tasks).ConfigureAwait(false);
            var best = results.Where(r => r.Ok).OrderBy(r => r.Ms).FirstOrDefault();
            if (best.Ok && !string.IsNullOrEmpty(best.Url))
            {
                Debug.WriteLine($"UpdateService 镜像测速最优：{best.Url} ({best.Ms:F0}ms)");
                return best.Url;
            }
            return preferred ?? string.Format(GiteeDownloadBase, tag);
        }
    }
}
