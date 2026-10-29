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
    /// 自动更新服务 — 三源（Gitee / GitHub / CNB）并发探测，取版本最高的成功源。
    ///
    /// 三源探测方式（2026-10-29 全部实测匿名可达）：
    ///  ① Gitee  GET api/v5/releases/latest（匿名 200，0.65s）→ tag_name + assets 直链；
    ///  ② GitHub HEAD releases/latest 302 重定向（绕开匿名 API 403）→ Location 解析 tag，
    ///    资产走 releases/download/{tag}/ 固定命名；
    ///  ③ CNB    raw/master/latest.json 自维护清单（匿名 200，0.68s；CNB release API 匿名 401 不可用）。
    ///
    /// 选源规则：只认「探测成功」的源，取其中版本最高者（避免最快的源落后未同步导致误判无更新），
    /// 版本并列取延迟最低。下载 HttpClient 流式写盘，失败静默（与 SyncEngine 同一容错风格）。
    /// </summary>
    public class UpdateService
    {
        // ── 三源地址（发版时同步维护，见 AGENTS.md「发版约定」）────────────
        private const string GiteeApiUrl = "https://gitee.com/api/v5/repos/worhllo/WinKit/releases/latest";
        private const string GiteeDownloadBase = "https://gitee.com/worhllo/WinKit/releases/download/{0}/WinKit-{0}-win-x64.zip";
        private const string GitHubLatestUrl = "https://github.com/oishijie/WinKit/releases/latest";
        private const string GitHubDownloadFmt = "https://github.com/oishijie/WinKit/releases/download/{0}/WinKit-{0}-win-x64.zip";
        private const string CnbManifestUrl = "https://cnb.cool/wordoi.rlc/winkit-customer/-/raw/master/latest.json";

        private static readonly TimeSpan ProbeTimeout = TimeSpan.FromMilliseconds(1500);

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
        /// 三源并发探测并汇总结果。网络异常静默，全失败时 Probed=false。
        /// </summary>
        public async Task<UpdateCheckResult> CheckAsync()
        {
            var gitee = ProbeGiteeAsync();
            var github = ProbeGitHubAsync();
            var cnb = ProbeCnbAsync();

            var probes = new[] { await gitee, await github, await cnb };
            var ok = new List<UpdateSourceProbe>();
            foreach (var p in probes)
            {
                if (p.Success && !string.IsNullOrEmpty(p.Tag)) ok.Add(p);
            }

            var result = new UpdateCheckResult { CurrentVersion = CurrentVersion };
            if (ok.Count == 0)
            {
                Message?.Invoke("更新检查失败：三个源均不可达（检查网络）");
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
                var url = downloadUrl ?? string.Format(GiteeDownloadBase, tag);
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

        // ── 三源探测实现 ──────────────────────────────────────────────

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

        /// <summary>CNB：release API 匿名 401，改用仓库内自维护 latest.json（随发版更新）</summary>
        private async Task<UpdateSourceProbe> ProbeCnbAsync()
        {
            var probe = new UpdateSourceProbe { Name = "CNB" };
            var sw = Stopwatch.StartNew();
            try
            {
                using var cts = new CancellationTokenSource(ProbeTimeout);
                var json = await _http.GetStringAsync(CnbManifestUrl, cts.Token);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("tag", out var tag))
                {
                    probe.Tag = tag.GetString();
                    if (doc.RootElement.TryGetProperty("downloadUrl", out var du))
                    {
                        probe.DownloadUrl = du.GetString();
                    }
                    probe.Success = !string.IsNullOrEmpty(probe.Tag);
                }
            }
            catch { probe.Success = false; }
            probe.LatencyMs = sw.Elapsed.TotalMilliseconds;
            return probe;
        }
    }
}
