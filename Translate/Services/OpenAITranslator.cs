using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WinKit.Translate.Services
{
    /// <summary>
    /// OpenAI 兼容大模型翻译引擎 — 实现 <see cref="ITranslator"/>。
    /// 通过 /chat/completions 端点，用 system prompt 约束为纯翻译输出。
    /// 注意：API Key 由调用方从配置传入，本类不负责持久化。
    /// </summary>
    public class OpenAITranslator : ITranslator
    {
        // 实例级 HttpClient（证书校验策略随配置不同而不同，无法共用静态实例）
        private readonly HttpClient _client;

        /// <summary>
        /// 构造 HttpClient：优先读取显式代理环境变量（如 Clash 的 HTTPS_PROXY/HTTP_PROXY），
        /// 否则回退到系统代理（HttpClient.DefaultProxy 已含 Windows WinINet / IE 设置）。
        /// skipCertValidation=true 时跳过 TLS 证书校验（用于自签/内网/反代端点，安全性降低）。
        /// </summary>
        private static HttpClient CreateClient(bool skipCertValidation)
        {
            IWebProxy? proxy = null;
            var env = Environment.GetEnvironmentVariable("HTTPS_PROXY")
                   ?? Environment.GetEnvironmentVariable("https_proxy")
                   ?? Environment.GetEnvironmentVariable("HTTP_PROXY")
                   ?? Environment.GetEnvironmentVariable("http_proxy");
            if (!string.IsNullOrWhiteSpace(env) && Uri.TryCreate(env, UriKind.Absolute, out var proxyUri))
            {
                proxy = new WebProxy(proxyUri);
            }

            var handler = new HttpClientHandler
            {
                UseProxy = true,
                Proxy = proxy ?? HttpClient.DefaultProxy,
            };

            // 跳过证书校验：接受所有证书（含无效/自签）。仅对可信的国内反代端点开启。
            if (skipCertValidation)
            {
                handler.ServerCertificateCustomValidationCallback =
                    (msg, cert, chain, errors) => true;
            }

            return new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(60)
            };
        }

        private readonly string _apiKey;
        private readonly string _baseUrl;
        private readonly string _model;

        public OpenAITranslator(string apiKey, string baseUrl, string model, bool skipCertValidation = false)
        {
            _apiKey = apiKey ?? "";
            _baseUrl = (baseUrl ?? "").TrimEnd('/');
            if (string.IsNullOrEmpty(_baseUrl)) _baseUrl = "https://api.openai.com/v1";
            _model = string.IsNullOrWhiteSpace(model) ? "gpt-4o-mini" : model;
            _client = CreateClient(skipCertValidation);
        }

        public string Name => $"OpenAI · {_model}";

        public async Task<string> TranslateAsync(string text, string sourceLang, string targetLang, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;

            if (string.IsNullOrWhiteSpace(_apiKey))
                throw new InvalidOperationException("未配置 OpenAI API Key，请在设置 → 翻译与 OCR 中填写");

            var targetName = LanguageName(targetLang);
            var sourcePart = string.IsNullOrEmpty(sourceLang) || sourceLang == "auto"
                ? "自动检测源语言"
                : $"源语言为 {LanguageName(sourceLang)}";

            var system = $"你是一个专业的翻译引擎。请将用户输入的文本翻译为{targetName}。" +
                         $"{sourcePart}。只输出译文本身，不要任何解释、前言、编号或引号包裹。";

            var payload = new
            {
                model = _model,
                messages = new object[]
                {
                    new { role = "system", content = system },
                    new { role = "user", content = text },
                },
                temperature = 0.2,
            };

            var json = JsonSerializer.Serialize(payload);
            var url = $"{_baseUrl}/chat/completions";

            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _apiKey);
            req.Content = new StringContent(json, Encoding.UTF8, "application/json");

            try
            {
                using var resp = await _client.SendAsync(req, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    var detail = await TryReadError(resp, ct).ConfigureAwait(false);
                    throw new InvalidOperationException(
                        $"OpenAI 返回错误 {((int)resp.StatusCode)} {resp.ReasonPhrase}" +
                        (string.IsNullOrEmpty(detail) ? "" : $"：{detail}"));
                }

                var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                return ParseOpenAIResponse(body);
            }
            catch (HttpRequestException ex)
            {
                // 把真正的根因（证书不受信任 / 连接被拒 / 被网络拦截等）透传出来
                var inner = ex.InnerException?.Message ?? ex.Message;
                var hint = ex.InnerException is System.Security.Authentication.AuthenticationException
                    ? "（TLS 握手失败：多为证书不受信任，或直连被网络层拦截。若用自签/内网端点需配置信任或走代理；若直连 api.openai.com 建议通过代理访问）"
                    : "（网络连接层失败：检查代理/防火墙/Base URL 是否可达）";
                throw new InvalidOperationException($"OpenAI 请求失败：{inner} {hint}".Trim());
            }
        }

        private static async Task<string?> TryReadError(HttpResponseMessage resp, CancellationToken ct)
        {
            try
            {
                var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("error", out var err) &&
                    err.TryGetProperty("message", out var em))
                {
                    return em.GetString();
                }
            }
            catch { /* 忽略解析失败 */ }
            return null;
        }

        private static string ParseOpenAIResponse(string json)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("choices", out var choices) &&
                choices.ValueKind == JsonValueKind.Array)
            {
                foreach (var choice in choices.EnumerateArray())
                {
                    if (choice.TryGetProperty("message", out var msg) &&
                        msg.TryGetProperty("content", out var content))
                    {
                        var text = content.GetString();
                        if (!string.IsNullOrWhiteSpace(text))
                            return text.Trim();
                    }
                }
            }

            if (root.TryGetProperty("error", out var err) &&
                err.TryGetProperty("message", out var em))
            {
                var msg = em.GetString();
                if (!string.IsNullOrEmpty(msg))
                    throw new InvalidOperationException($"OpenAI 返回错误: {msg}");
            }

            return string.Empty;
        }

        private static string LanguageName(string? code) => code?.ToLowerInvariant() switch
        {
            "zh-cn" => "简体中文",
            "zh-tw" => "繁體中文",
            "en" => "English",
            "ja" => "日本語",
            "ko" => "한국어",
            "fr" => "Français",
            "de" => "Deutsch",
            "ru" => "Русский",
            _ => code ?? "目标语言",
        };
    }
}
