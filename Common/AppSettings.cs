using System;
using System.Text.Json.Serialization;

namespace WinKit.Common
{
    /// <summary>
    /// 热键配置模型
    /// </summary>
    public class HotkeyConfig
    {
        /// <summary>Win32 虚拟键码</summary>
        public int VK { get; set; }

        /// <summary>修饰键组合（1=Alt, 2=Shift, 4=Ctrl, 8=Win）</summary>
        public int Modifiers { get; set; }

        public HotkeyConfig() { }

        public HotkeyConfig(int vk, int modifiers)
        {
            VK = vk;
            Modifiers = modifiers;
        }

        /// <summary>
        /// 格式化为可读字符串
        /// </summary>
        public static string Format(int vk, int modifiers)
        {
            var parts = new System.Collections.Generic.List<string>();
            if ((modifiers & 8) != 0) parts.Add("Win");
            if ((modifiers & 4) != 0) parts.Add("Ctrl");
            if ((modifiers & 1) != 0) parts.Add("Alt");
            if ((modifiers & 2) != 0) parts.Add("Shift");

            var key = System.Windows.Input.KeyInterop.KeyFromVirtualKey(vk);
            var keyStr = key.ToString();
            if (keyStr.Length == 1 && char.IsLetter(keyStr[0]))
                keyStr = keyStr.ToUpper();

            parts.Add(keyStr);
            return string.Join(" + ", parts);
        }
    }

    /// <summary>
    /// WinKit 统一配置模型类
    /// </summary>
    public class AppSettings
    {
        // ── TodoList 待办设置 ──────────────────────────
        public bool TodoIsPinned { get; set; } = false;
        public bool TodoIsPassThrough { get; set; } = false;

        // ── 不透明度设置 ────────────────────────────────
        public int WindowOpacity { get; set; } = 80; // 80% 不透明度（默认）

        // ── Clipboard 剪贴板设置 ────────────────────────
        public int PasteMaxItems { get; set; } = 100;
        public bool PasteEnableTextDeduplication { get; set; } = true;
        public bool PasteEnableMonitoring { get; set; } = true;

        // ── Translate 翻译 & OCR 设置 ───────────────────
        /// <summary>是否启用翻译/OCR 模块及其全局热键</summary>
        public bool TranslateEnable { get; set; } = true;

        /// <summary>翻译目标语言 (Google 语言代码，如 zh-CN / en / ja)</summary>
        public string TranslateTargetLang { get; set; } = "zh-CN";

        /// <summary>翻译源语言 (auto 表示自动检测)</summary>
        public string TranslateSourceLang { get; set; } = "auto";

        /// <summary>
        /// 翻译引擎：
        ///   deepseek — 国内大模型（DeepSeek 等 OpenAI 兼容，预填国内可达端点，需填 Key）【默认】
        ///   baidu    — 百度翻译开放平台（APP ID + 密钥，国内直连免代理）
        ///   openai   — 通用 OpenAI 兼容接口（自定义 Base URL，需填 Key）
        ///   google   — Google 免费端点（国内需代理，默认不再使用）
        /// ITranslator 接口已为 DeepL / Azure 等预留接入位。
        /// </summary>
        public string TranslateProvider { get; set; } = "deepseek";

        /// <summary>OpenAI 兼容接口的 API Key（仅 deepseek / openai 引擎使用，本地通过 DPAPI 加密存储，内存中为明文）</summary>
        public string OpenAIApiKey { get; set; } = "";

        /// <summary>OpenAI 兼容接口的 Base URL。deepseek 默认为 https://api.deepseek.com/v1；openai 默认为 https://api.openai.com/v1；留空则用对应预设值</summary>
        public string OpenAIBaseUrl { get; set; } = "https://api.deepseek.com/v1";

        /// <summary>OpenAI 大模型名，如 deepseek-chat / gpt-4o-mini / 自建兼容模型（仅 deepseek / openai 引擎使用）</summary>
        public string OpenAIModel { get; set; } = "deepseek-chat";

        /// <summary>OpenAI 接口是否跳过 TLS 证书校验（用于自签/内网/反代端点；默认关闭更安全）</summary>
        public bool OpenAISkipCertValidation { get; set; } = false;

        /// <summary>百度翻译 APP ID（仅 TranslateProvider=baidu 时使用，本地明文存储）</summary>
        public string BaiduAppId { get; set; } = "";

        /// <summary>百度翻译 SecretKey（仅 TranslateProvider=baidu 时使用，本地通过 DPAPI 加密存储，内存中为明文）</summary>
        public string BaiduApiKey { get; set; } = "";

        // ── 本地 OCR 引擎设置 ───────────────────────────
        // 识别全程在本机完成，不依赖任何在线服务，也无需 API Key。

        /// <summary>
        /// 使用的本地模型：V6_Small (默认，均衡) / V6_Tiny (最快) / V5_CN / V5_EN
        /// </summary>
        public string OcrModel { get; set; } = "V6_Small";

        /// <summary>
        /// 是否启用文字方向分类。截图场景文字基本为正向，
        /// 关闭可省一次推理；识别竖排或旋转文本时再打开。
        /// </summary>
        public bool OcrEnableAngleClassification { get; set; } = false;

        /// <summary>是否启用 MKL-DNN 加速（CPU 推理，默认开启）</summary>
        public bool OcrEnableMkldnn { get; set; } = true;

        /// <summary>CPU 推理线程数，0 表示按机器核数自动选择</summary>
        public int OcrCpuThreads { get; set; } = 0;

        /// <summary>
        /// 是否在应用启动后台预加载 OCR 引擎。
        /// 引擎冷启动需数秒，预热后首次按热键即可秒回。
        /// </summary>
        public bool OcrPreloadOnStartup { get; set; } = true;

        /// <summary>
        /// 闲置多久后自动卸载原生推理实例并压缩工作集（秒）。
        /// 引擎常驻时约占两三百 MB，闲置后主动释放可回落到接近 10MB。
        /// 设为 0 表示关闭自动瘦身（始终保持引擎常驻，首帧零延迟但更占内存）。
        /// </summary>
        public int OcrIdleSlimSeconds { get; set; } = 5;

        /// <summary>
        /// 识别结果「搜索」按钮使用的搜索引擎：bing / google / baidu（默认 bing）。
        /// 点击搜索时会用默认浏览器打开对应搜索页。
        /// </summary>
        public string OcrSearchEngine { get; set; } = "bing";

        // ── 快捷键配置 ─────────────────────────────────

        /// <summary>截图翻译快捷键（默认 Alt+S）</summary>
        public HotkeyConfig HotkeyScreenshotTranslate { get; set; } = new(0x53, 1);

        /// <summary>划词翻译快捷键（默认 Alt+D）</summary>
        public HotkeyConfig HotkeySelectionTranslate { get; set; } = new(0x44, 1);

        /// <summary>纯 OCR 识别快捷键（默认 Alt+Shift+S）</summary>
        public HotkeyConfig HotkeyOcrOnly { get; set; } = new(0x53, 3);

        /// <summary>静默 OCR 快捷键（默认 Alt+Shift+F）</summary>
        public HotkeyConfig HotkeySilentOcr { get; set; } = new(0x46, 3);

        /// <summary>截图到剪贴板历史快捷键（默认 Alt+A）</summary>
        public HotkeyConfig HotkeyScreenshot { get; set; } = new(0x41, 1);
    }
}
