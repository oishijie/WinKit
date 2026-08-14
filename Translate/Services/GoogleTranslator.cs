using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WinKit.Translate.Services
{
    /// <summary>
    /// Google 免费翻译引擎 — 使用 gtx 端点，无需 API Key，开箱即用。
    /// 参考自 STranslate 的 GoogleTranslator 实现，简化为零配置版本。
    /// </summary>
    public class GoogleTranslator : ITranslator
    {
        private const string Endpoint = "https://translate.googleapis.com/translate_a/single";

        // 复用单例 HttpClient，避免每次请求创建新实例
        private static readonly HttpClient _client = new()
        {
            Timeout = TimeSpan.FromSeconds(15)
        };

        public string Name => "Google";

        public async Task<string> TranslateAsync(string text, string sourceLang, string targetLang, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;

            var query = Uri.EscapeDataString(text);
            var sl = string.IsNullOrEmpty(sourceLang) ? "auto" : sourceLang;
            var url = $"{Endpoint}?client=gtx&sl={sl}&tl={targetLang}&dt=t&q={query}";

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            // 模拟浏览器请求头，提高稳定性
            req.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)");

            using var resp = await _client.SendAsync(req, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();

            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return ParseGoogleResponse(json);
        }

        /// <summary>
        /// 解析 Google gtx 返回的嵌套数组 JSON：
        /// [[["译文","原文",...],...], ..., ["检测到的源语言"]]
        /// </summary>
        private static string ParseGoogleResponse(string json)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Array)
                return string.Empty;

            var first = root[0];
            if (first.ValueKind != JsonValueKind.Array)
                return string.Empty;

            var sb = new System.Text.StringBuilder();
            foreach (var seg in first.EnumerateArray())
            {
                // seg[0] 是译文片段
                if (seg.ValueKind == JsonValueKind.Array && seg.GetArrayLength() > 0)
                {
                    var piece = seg[0].GetString();
                    if (!string.IsNullOrEmpty(piece))
                        sb.Append(piece);
                }
            }
            return sb.ToString();
        }
    }
}
