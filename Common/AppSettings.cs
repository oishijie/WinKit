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
        /// 格式化为可读字符串，如「Alt + Shift + S」
        /// </summary>
        public static string Format(int vk, int modifiers)
        {
            var parts = new System.Collections.Generic.List<string>();
            if ((modifiers & 8) != 0) parts.Add("Win");
            if ((modifiers & 4) != 0) parts.Add("Ctrl");
            if ((modifiers & 1) != 0) parts.Add("Alt");
            if ((modifiers & 2) != 0) parts.Add("Shift");

            parts.Add(KeyName(vk));
            return string.Join(" + ", parts);
        }

        /// <summary>
        /// 把虚拟键码转成用户看得懂的键名。
        ///
        /// WPF 的 Key.ToString() 会吐出 D0 / OemPlus / Prior 这类内部名 ——
        /// 用户录制 Ctrl+1，键帽上却显示「Ctrl + D0」，会以为录错了键。
        /// </summary>
        public static string KeyName(int vk)
        {
            var key = System.Windows.Input.KeyInterop.KeyFromVirtualKey(vk);

            switch (key)
            {
                case System.Windows.Input.Key.None: return "未设置";

                // 主键盘数字 0-9：D0–D9 → 0–9
                case >= System.Windows.Input.Key.D0 and <= System.Windows.Input.Key.D9:
                    return ((char)('0' + ((int)key - (int)System.Windows.Input.Key.D0))).ToString();

                // 小键盘数字
                case >= System.Windows.Input.Key.NumPad0 and <= System.Windows.Input.Key.NumPad9:
                    return "小键盘 " + ((int)key - (int)System.Windows.Input.Key.NumPad0);

                case System.Windows.Input.Key.OemPlus: return "+";
                case System.Windows.Input.Key.OemMinus: return "-";
                case System.Windows.Input.Key.OemComma: return ",";
                case System.Windows.Input.Key.OemPeriod: return ".";
                case System.Windows.Input.Key.OemQuestion: return "/";
                case System.Windows.Input.Key.OemSemicolon: return ";";
                case System.Windows.Input.Key.OemQuotes: return "'";
                case System.Windows.Input.Key.OemOpenBrackets: return "[";
                case System.Windows.Input.Key.OemCloseBrackets: return "]";
                case System.Windows.Input.Key.OemPipe: return "\\";
                case System.Windows.Input.Key.OemTilde: return "`";
                case System.Windows.Input.Key.OemBackslash: return "\\";

                case System.Windows.Input.Key.Space: return "空格";
                case System.Windows.Input.Key.Return: return "回车";
                case System.Windows.Input.Key.Back: return "退格";
                case System.Windows.Input.Key.Capital: return "CapsLock";
                case System.Windows.Input.Key.Prior: return "PageUp";
                case System.Windows.Input.Key.Next: return "PageDown";
                case System.Windows.Input.Key.PrintScreen: return "PrintScreen";
                case System.Windows.Input.Key.Escape: return "Esc";
            }

            var name = key.ToString();
            if (name.Length == 1 && char.IsLetter(name[0])) name = name.ToUpperInvariant();
            return name;
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

        /// <summary>
        /// 深色截图自动反色（默认开启）。
        /// PP-OCR 识别头按「白底黑字」训练，深色主题（终端 / IDE / 暗色网页）截图是浅字深底，
        /// 直接送入会明显掉字。开启后先检测背景极性，判为深色则整图反色再识别。
        /// 属预处理，改动不影响已加载的引擎（不触发重建）。
        /// </summary>
        public bool OcrAutoInvertDark { get; set; } = true;

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

        /// <summary>
        /// 识别历史保留条数（默认 100，范围 10–1000）。
        /// 识别成功的结果会连同耗时、模型、块数一并入库，便于回溯与再次复制。
        /// 设在「翻译与 OCR」分区里，超出上限后淘汰最旧的记录。
        /// </summary>
        public int OcrHistoryMaxItems { get; set; } = 100;

        // ── 快捷键配置 ─────────────────────────────────

        /// <summary>截图翻译快捷键（默认 Alt+S）</summary>
        public HotkeyConfig HotkeyScreenshotTranslate { get; set; } = new(0x53, 1);

        /// <summary>划词翻译快捷键（默认 Alt+D）</summary>
        public HotkeyConfig HotkeySelectionTranslate { get; set; } = new(0x44, 1);

        /// <summary>纯 OCR 识别快捷键（默认 Alt+Shift+S）</summary>
        public HotkeyConfig HotkeyOcrOnly { get; set; } = new(0x53, 3);

        /// <summary>静默 OCR 快捷键（默认 Alt+Shift+F）</summary>
        public HotkeyConfig HotkeySilentOcr { get; set; } = new(0x46, 3);

        /// <summary>截图快捷键（默认 Alt+A）——框选后进标注编辑器</summary>
        public HotkeyConfig HotkeyScreenshot { get; set; } = new(0x41, 1);

        /// <summary>
        /// 剪贴板面板唤出键（默认 Win+V，VK 0x56 / Modifiers 8）。
        ///
        /// 与上面几条的本质区别：这条走**低级键盘钩子**而不是 RegisterHotKey。
        /// 含 Win 键的组合被系统保留，RegisterHotKey 注册必然失败；
        /// 钩子还有个附带好处 —— 能吞掉按键，从而阻止系统自带的剪贴板面板弹出。
        /// 改成不含 Win 的组合后，Win+V 就归还给系统，本程序不再拦截。
        /// </summary>
        public HotkeyConfig HotkeyClipboardPanel { get; set; } = new(0x56, 8);

        /// <summary>
        /// 待办窗口唤出键（默认 Alt+T）——切换待办面板的显示 / 隐藏，与托盘菜单「显示待办」同一动作。
        /// 走普通 RegisterHotKey：不含 Win 键，不需要那套低级键盘钩子。
        /// </summary>
        public HotkeyConfig HotkeyTodoPanel { get; set; } = new(0x54, 1);

        // ── 截图模块行为 ───────────────────────────────
        // 截图独立成 Capture 模块，与翻译 / OCR 的开关互不影响。

        /// <summary>截图后是否自动把原图复制到系统剪贴板（默认开；编辑器内仍可再次复制标注结果）</summary>
        public bool CaptureAutoCopy { get; set; } = true;

        /// <summary>
        /// 截图后是否打开标注编辑器（默认开）。
        /// 关闭则只取图并复制，等同于「截图到剪贴板」；
        /// 若此项与 <see cref="CaptureAutoCopy"/> 同时关闭，截图将毫无产出，因此代码会强制至少复制一次。
        /// </summary>
        public bool CaptureOpenEditor { get; set; } = true;
    }
}
