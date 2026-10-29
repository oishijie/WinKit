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

        /// <summary>启动时是否自动显示待办窗口（默认关闭，仅驻留托盘）</summary>
        public bool TodoShowOnStartup { get; set; } = false;

        /// <summary>待办回收站保留天数（超期自动清理；默认 60 天）</summary>
        public int RecycleBinRetentionDays { get; set; } = 60;

        // ── 不透明度设置 ────────────────────────────────
        public int WindowOpacity { get; set; } = 80; // 80% 不透明度（默认）

        // ── Clipboard 剪贴板设置 ────────────────────────
        public int PasteMaxItems { get; set; } = 100;
        public bool PasteEnableTextDeduplication { get; set; } = true;
        public bool PasteEnableMonitoring { get; set; } = true;

        // ── 剪贴板图片缓存清理（自上游 v2.11 移植）──────
        /// <summary>图片缓存保留天数，超期自动清理（默认 15 天）</summary>
        public int ClipboardImageRetentionDays { get; set; } = 15;

        /// <summary>图片缓存容量上限（MB），超出后折半淘汰最旧的一半（默认 100MB）</summary>
        public int ClipboardImageMaxStorageMB { get; set; } = 100;

        // ── Clipboard 剪贴板同步（SyncClipboard 协议）────
        /// <summary>是否启用剪贴板同步（默认关闭）</summary>
        public bool SyncEnabled { get; set; } = false;

        /// <summary>SyncClipboard 兼容服务器地址，如 https://your-server.example.com（不以 / 结尾）</summary>
        public string SyncUrl { get; set; } = string.Empty;

        /// <summary>服务器用户名（HTTP Basic 认证）</summary>
        public string SyncUser { get; set; } = string.Empty;

        /// <summary>服务器密码（HTTP Basic 认证）</summary>
        public string SyncPassword { get; set; } = string.Empty;

        /// <summary>轮询间隔毫秒（默认 5000，最低 1000；对齐官方客户端探活节奏）</summary>
        public int SyncIntervalMs { get; set; } = 5000;

        /// <summary>同步文本类型（默认开）</summary>
        public bool SyncText { get; set; } = true;

        /// <summary>接收图片（远端 → 本地拉取；默认开）。本地图片是否自动推送见 SyncAutoPushImage</summary>
        public bool SyncImage { get; set; } = true;

        /// <summary>接收文件（远端 → 本地拉取；默认关）。本地文件是否自动推送见 SyncAutoPushFile</summary>
        public bool SyncFile { get; set; } = false;

        /// <summary>复制图片后自动推送（默认关：复制仅入历史，可用右键菜单显式推送）</summary>
        public bool SyncAutoPushImage { get; set; } = false;

        /// <summary>复制文件后自动推送（默认关：复制仅入历史，可用右键菜单显式推送）</summary>
        public bool SyncAutoPushFile { get; set; } = false;

        /// <summary>资源管理器右键「同步到手机」菜单（HKCU 注册表；默认关）</summary>
        public bool SyncShellMenu { get; set; } = false;

        /// <summary>图片推送大小上限（MB），超出跳过推送并气泡提示（默认 20）</summary>
        public int SyncImageMaxMB { get; set; } = 20;

        /// <summary>文件推送大小上限（MB），对齐 Cloudflare Workers 默认上限（默认 48）</summary>
        public int SyncFileMaxMB { get; set; } = 48;

        /// <summary>文件黑名单扩展名，逗号分隔（如 exe,dll,msi），命中则不上传（对齐官方上传黑名单；空 = 不过滤）</summary>
        public string SyncFileBlacklist { get; set; } = string.Empty;

        /// <summary>推送成功时气泡提示（默认开）</summary>
        public bool SyncNotifyPush { get; set; } = true;

        /// <summary>拉取成功时气泡提示（默认开）</summary>
        public bool SyncNotifyPull { get; set; } = true;

        /// <summary>
        /// 用右下角操作卡片替代托盘气泡（默认开）。
        /// 卡片带「复制 / 存到下载 / 打开」按钮；关掉则退回传统气泡，只是没有按钮。
        /// </summary>
        public bool SyncToastEnabled { get; set; } = true;

        /// <summary>卡片停留秒数（默认 6；鼠标悬停期间暂停计时）</summary>
        public int SyncToastSeconds { get; set; } = 6;

        // ── Update 自动更新设置 ─────────────────────────
        /// <summary>自动检查更新：启动后每日静默检查一次，多源择优（默认开）</summary>
        public bool UpdateAutoCheck { get; set; } = true;

        /// <summary>自动下载更新包：发现新版本即后台下载，关 = 仅气泡提示版本号（默认关）</summary>
        public bool UpdateAutoDownload { get; set; } = false;

        /// <summary>上次自动检查时间（每日一次的判定依据）</summary>
        public DateTime? LastUpdateCheckAt { get; set; }

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
        /// 是否启用文字方向分类（0° / 180° 判定）。
        ///
        /// 默认**开启**。分类模型仅 1MB、开销只占全流程 3~5%，
        /// 而关掉的代价是倒置文字（截图时画面翻转、拍屏照片）直接出乱码 ——
        /// 典型的「省了一点时间，丢了一大片正确率」。
        /// </summary>
        public bool OcrEnableAngleClassification { get; set; } = true;

        /// <summary>
        /// 深色截图自动反色（默认开启）。
        /// PP-OCR 识别头按「白底黑字」训练，深色主题（终端 / IDE / 暗色网页）截图是浅字深底，
        /// 直接送入会明显掉字。开启后先检测背景极性，判为深色则整图反色再识别。
        /// 属预处理，改动不影响已加载的引擎（不触发重建）。
        /// </summary>
        public bool OcrAutoInvertDark { get; set; } = true;

        /// <summary>
        /// 识别结果置信度过滤阈值（0~1，默认 0.5，与 RapidOCR 官方 text_score 默认值一致）。
        ///
        /// 引擎对每行都会回传逐字符置信度均值，低于该值的行视为「图标 / 边框 / 噪点被误检」，
        /// 直接丢弃。调低会保留更多可疑文本（可能夹乱码），调高会更干净但可能误删真实内容。
        /// 属识别后处理，改动不触发引擎重建。
        /// </summary>
        public double OcrTextScoreThreshold { get; set; } = 0.5;

        /// <summary>
        /// 小字增强（默认开启）：首轮检出框偏小时，按目标行高整体放大后再识别一遍，
        /// 两轮之间按「字符总数 + 平均置信度」择优采用。
        ///
        /// 解决的是屏幕截图的典型难题 —— PP-OCR 识别头按 48px 行高训练，
        /// 而桌面正文常常只有 12~16px。代价是最多跑两遍推理（仅在检出小字时触发）。
        /// 属识别前处理，改动不触发引擎重建。
        /// </summary>
        public bool OcrSmallTextBoost { get; set; } = true;

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
