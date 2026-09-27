using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using WinKit.Capture;
using WinKit.Common;
using WinKit.Translate.Services;

namespace WinKit.Translate
{
    /// <summary>
    /// 翻译 / OCR 模块编排器 — 对标 STranslate 的主翻译链路。
    /// 串联 截图 → 本地 OCR → 翻译 → 结果展示 / 静默复制 四条链路，
    /// 并通过 HotkeyService 接管 Alt+S / Alt+D / Alt+Shift+S / Alt+Shift+F。
    ///
    /// OCR 全程在本机完成（PaddleOCR CPU 推理），不产生任何网络请求。
    /// </summary>
    public class TranslateModule : IDisposable
    {
        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        private const byte VK_CONTROL = 0x11;
        private const byte VK_C = 0x43;
        private const byte VK_MENU = 0x12;   // Alt
        private const byte VK_SHIFT = 0x10;
        private const uint KEYEVENTF_KEYUP = 0x0002;

        private readonly SettingsManager _settingsManager;
        private ITranslator _translator;
        private TranslatorOptions _translatorOptions;
        private readonly IOcrProvider _ocr;
        private readonly OcrHistoryStore _history;
        private HotkeyService _hotkey;

        private ResultWindow? _resultWindow;
        /// <summary>
        /// 当前正在跑的任务的取消源。新触发会取消它 —— 连按热键的意图就是「重来一次」，
        /// 旧版遇到正在运行就静默 return，用户会觉得程序卡住了。
        /// </summary>
        private CancellationTokenSource? _runCts;
        private bool _enabled;
        private bool _disposed;
        /// <summary>当前已注册热键的快照，供设置变更时判断"快捷键是否真变"、避免无关改动触发整组重注册</summary>
        private HotkeySnapshot? _appliedHotkeys;
        /// <summary>热键是否因设置中心正在录制而被临时挂起（挂起期间不再响应配置变更重注册）</summary>
        private bool _hotkeysSuspended;

        /// <summary>热键标识 → 触发的链路。与 <see cref="HotkeyCatalog"/> 的 Id 一一对应。</summary>
        private readonly Dictionary<string, Func<CancellationToken, Task>> _hotkeyActions;

        public bool IsEnabled => _enabled;

        /// <summary>
        /// 热键注册失败（组合已被其它程序抢占、或与系统保留键冲突）。
        /// 参数为热键显示名。UI 层据此提示用户「改了这个键但没生效」——
        /// 旧版忽略返回值，用户会以为设置没保存。
        /// </summary>
        public event EventHandler<string>? HotkeyRegistrationFailed;

        /// <summary>当前 OCR 引擎显示名（含实际加载的模型）</summary>
        public string OcrEngineName => _ocr.Name;

        /// <summary>识别历史存储（供设置中心的「识别历史」卡片读写）</summary>
        public OcrHistoryStore History => _history;

        /// <summary>OCR 引擎是否已完成初始化，可立即识别</summary>
        public bool OcrIsReady => _ocr.IsReady;

        /// <summary>初始化失败原因（未失败为 null）</summary>
        public string? OcrInitializationError =>
            (_ocr is RapidOcrProvider paddle) ? paddle.InitializationError : null;

        /// <summary>因首选模型缺失而回退到其它模型时的提示文案（无回退为 null）</summary>
        public string? OcrFallbackNotice =>
            (_ocr is RapidOcrProvider paddle) ? paddle.FallbackNotice : null;

        public TranslateModule(SettingsManager settingsManager)
        {
            _settingsManager = settingsManager;
            _translatorOptions = TranslatorOptions.FromSettings(settingsManager.Settings);
            _translator = CreateTranslator(_translatorOptions);
            _ocr = new RapidOcrProvider(OcrEngineOptions.FromSettings(settingsManager.Settings));
            _history = new OcrHistoryStore();
            _hotkey = new HotkeyService();

            _hotkeyActions = new Dictionary<string, Func<CancellationToken, Task>>
            {
                ["screenshotTranslate"] = ScreenshotTranslateAsync,
                ["selectionTranslate"] = SelectionTranslateAsync,
                ["ocrOnly"] = OcrOnlyAsync,
                ["silentOcr"] = SilentOcrAsync,
            };

            // 配置变更后同步引擎参数（内部会判断是否真的需要重建）
            _settingsManager.SettingsChanged += OnSettingsChanged;
        }

        /// <summary>
        /// 后台预热本地 OCR 引擎。冷启动需要数秒，提前加载可让首次热键即时响应。
        /// </summary>
        public Task WarmUpOcrAsync() => _ocr.WarmUpAsync();

        private void OnSettingsChanged(object? sender, AppSettings settings)
        {
            if (_ocr is RapidOcrProvider paddle)
            {
                // Reconfigure 内部走 WaitAsync 而非同步 Wait，绝不阻塞 UI 线程；
                // 重置完成后在后台预热新引擎，避免首次识别时冷启动等数秒。
                // （WarmUpAsync 内部判断 IsReady，已就绪时立即返回，开销可忽略）
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await paddle.ReconfigureAsync(OcrEngineOptions.FromSettings(settings)).ConfigureAwait(false);
                        await paddle.WarmUpAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"OCR 热切换预热失败: {ex.Message}"); }
                });
            }

            // 仅当翻译引擎相关配置（provider / key / url / model）真的变化时，才重建翻译器，
            // 避免为无关设置（如 OCR 线程数）付出重新 new 的代价。
            var newOpts = TranslatorOptions.FromSettings(settings);
            if (!newOpts.Equals(_translatorOptions))
            {
                _translatorOptions = newOpts;
                _translator = CreateTranslator(newOpts);
            }

            // 仅当快捷键字段真的变化时才重新注册整组热键，
            // 避免无关设置改动（如 OCR 线程数、翻译引擎）引发不必要的注销/注册抖动。
            // 录制期间热键已整体注销，此处直接跳过——由 ResumeHotkeys 统一按最新配置注册。
            if (_hotkeysSuspended) return;

            if (_enabled)
            {
                var newKeys = HotkeySnapshot.FromSettings(settings);
                if (!newKeys.Equals(_appliedHotkeys))
                {
                    Stop();
                    Start();
                }
            }
        }

        /// <summary>按配置创建一个翻译器实例</summary>
        private static ITranslator CreateTranslator(TranslatorOptions o) => o.Provider switch
        {
            "openai" or "deepseek" => new OpenAITranslator(o.OpenAIApiKey, o.OpenAIBaseUrl, o.OpenAIModel, o.SkipCertValidation),
            _ => new GoogleTranslator(),
        };

        /// <summary>
        /// 翻译器构造期参数。record 提供值相等语义：配置保存后可据此判断
        /// "翻译引擎相关配置是否真的变化"，无关改动（如 OCR 线程数）不触发重建。
        /// 注意：不含源/目标语言，因为翻译时实时读取最新设置。
        /// </summary>
        private sealed record TranslatorOptions(
            string Provider, string OpenAIApiKey, string OpenAIBaseUrl, string OpenAIModel, bool SkipCertValidation)
        {
            public static TranslatorOptions FromSettings(AppSettings s) => new(
                (s.TranslateProvider ?? "deepseek").ToLowerInvariant(),
                s.OpenAIApiKey ?? "",
                s.OpenAIBaseUrl ?? "",
                s.OpenAIModel ?? "",
                s.OpenAISkipCertValidation);
        }

        /// <summary>
        /// 当前已注册的全部快捷键的不可变快照。record 提供值相等语义：
        /// 设置变更时据此判断"快捷键是否真的变化"，只有变化才触发整组热键重注册，
        /// 避免改 OCR 线程数、翻译引擎等无关设置时的注销/注册抖动。
        /// </summary>
        private sealed record HotkeySnapshot(
            (int VK, int Modifiers) ScreenshotTranslate,
            (int VK, int Modifiers) SelectionTranslate,
            (int VK, int Modifiers) OcrOnly,
            (int VK, int Modifiers) SilentOcr)
        {
            public static HotkeySnapshot FromSettings(AppSettings s) => new(
                Snapshot(s.HotkeyScreenshotTranslate),
                Snapshot(s.HotkeySelectionTranslate),
                Snapshot(s.HotkeyOcrOnly),
                Snapshot(s.HotkeySilentOcr));

            /// <summary>配置文件里若把热键写成 null 也能安全取值</summary>
            private static (int VK, int Modifiers) Snapshot(HotkeyConfig? c) => (c?.VK ?? 0, c?.Modifiers ?? 0);
        }

        /// <summary>根据配置启用/停用模块热键</summary>
        public void Start()
        {
            if (_enabled) return;
            if (!_settingsManager.Settings.TranslateEnable) return;

            _enabled = true;
            RegisterHotkeys();
        }

        /// <summary>按当前配置注册本模块的全部热键（清单由 <see cref="HotkeyCatalog"/> 驱动）</summary>
        private void RegisterHotkeys()
        {
            _hotkeysSuspended = false;

            // 每次注册都重建 HotkeyService（旧的可能已被 Stop / Suspend 注销）
            _hotkey.Dispose();
            _hotkey = new HotkeyService();

            foreach (var descriptor in HotkeyCatalog.All)
            {
                if (descriptor.Owner != HotkeyOwner.Translate) continue;
                if (!_hotkeyActions.TryGetValue(descriptor.Id, out var action)) continue;

                var hk = descriptor.Get(_settingsManager.Settings);
                string label = descriptor.Label;   // 闭包捕获，提示文案要独立于循环变量

                if (!_hotkey.Register((uint)hk.VK, hk.Modifiers, () => RunAsync(action)))
                {
                    // 注册失败（多为组合已被其它程序占用）不再静默：上报给 UI 层提示用户
                    System.Diagnostics.Debug.WriteLine($"TranslateModule: 热键注册失败 {label}");
                    HotkeyRegistrationFailed?.Invoke(this, label);
                }
            }

            // 截图（Alt+A）已移交 CaptureModule 独立接管，本模块不再注册

            // 记录当前已注册的热键，供设置变更时判断是否需要重注册
            _appliedHotkeys = HotkeySnapshot.FromSettings(_settingsManager.Settings);
        }

        public void Stop()
        {
            if (!_enabled) return;
            _hotkey.Dispose();
            _enabled = false;
        }

        /// <summary>
        /// 临时注销本模块全部热键 —— 设置中心进入快捷键录制时由 App 层调用。
        ///
        /// 必须挂起的原因：录制时若用户按下「当前已生效的组合」（如 Alt+S），
        /// Windows 会把 WM_HOTKEY 直接发给注册者，设置窗收不到这个键；
        /// 同时弹出的翻译/截图窗口会让设置窗失焦，进而触发 OnDeactivated 取消录制。
        /// 表现就是「快捷键改不了」。
        /// </summary>
        public void SuspendHotkeys()
        {
            if (_hotkeysSuspended) return;
            _hotkeysSuspended = true;
            _hotkey.Dispose();
        }

        /// <summary>录制结束，按最新配置恢复热键</summary>
        public void ResumeHotkeys()
        {
            if (!_hotkeysSuspended) return;
            _hotkeysSuspended = false;
            if (_enabled) RegisterHotkeys();
        }

        /// <summary>切换启用状态（供托盘调用）</summary>
        public void SetEnabled(bool enable)
        {
            if (enable == _enabled) return;
            if (enable)
            {
                // 重新创建 HotkeyService（旧的已被 Dispose）
                Start();
            }
            else
            {
                Stop();
            }
        }

        // ══════════════════════════════════════════════
        //  四条主链路
        // ══════════════════════════════════════════════

        /// <summary>Alt+S 截图翻译：截图 → OCR → 翻译 → 结果窗</summary>
        private async Task ScreenshotTranslateAsync(CancellationToken ct)
        {
            var region = await CaptureSelectionAsync().ConfigureAwait(true);
            if (region == null || ct.IsCancellationRequested) return; // 用户取消

            var window = EnsureResultWindow();
            window.SetLoading("截图翻译", "译文", OcrLoadingHint());
            window.ShowAtMouse();

            var ocr = await OcrAsync(region.Value, ct).ConfigureAwait(true);
            if (ct.IsCancellationRequested) return;   // 期间用户又触发了一次，本次结果作废
            if (!ocr.Success || string.IsNullOrWhiteSpace(ocr.Text))
            {
                window.SetResult("截图翻译", "译文", "", status: ocr.Error ?? "未识别到文字", success: false);
                return;
            }

            window.SetLoading("截图翻译", "译文", "翻译中…");
            var t = await TranslateAsync(ocr.Text, ct).ConfigureAwait(true);
            if (ct.IsCancellationRequested) return;
            if (!t.Success)
            {
                window.SetResult("截图翻译", "译文", "", source: ocr.Text, status: t.Error, success: false);
                return;
            }

            window.SetResult("截图翻译", "译文", t.Target,
                source: ocr.Text, actionText: ocr.Text, status: $"{OcrStatus(ocr)} · {_translator.Name}");
        }

        /// <summary>Alt+D 划词翻译：模拟 Ctrl+C → 翻译 → 结果窗</summary>
        private async Task SelectionTranslateAsync(CancellationToken ct)
        {
            var window = EnsureResultWindow();
            window.SetLoading("划词翻译", "译文", "正在获取选中文本…");
            window.ShowAtMouse();

            var selected = await GetSelectionTextAsync().ConfigureAwait(true);
            if (ct.IsCancellationRequested) return;
            if (string.IsNullOrWhiteSpace(selected))
            {
                window.SetResult("划词翻译", "译文", "", status: "未获取到选中文本", success: false);
                return;
            }

            window.SetLoading("划词翻译", "译文", "翻译中…");
            var t = await TranslateAsync(selected, ct).ConfigureAwait(true);
            if (ct.IsCancellationRequested) return;
            if (!t.Success)
            {
                window.SetResult("划词翻译", "译文", "", source: selected, status: t.Error, success: false);
                return;
            }

            window.SetResult("划词翻译", "译文", t.Target, source: selected, actionText: selected, status: _translator.Name);
        }

        /// <summary>Alt+Shift+S OCR 识别：截图 → OCR → 结果窗（不翻译）</summary>
        private async Task OcrOnlyAsync(CancellationToken ct)
        {
            var region = await CaptureSelectionAsync().ConfigureAwait(true);
            if (region == null || ct.IsCancellationRequested) return;

            var window = EnsureResultWindow();
            window.SetLoading("文字识别", "识别结果", OcrLoadingHint());
            window.ShowAtMouse();

            var ocr = await OcrAsync(region.Value, ct).ConfigureAwait(true);
            if (ct.IsCancellationRequested) return;
            if (!ocr.Success || string.IsNullOrWhiteSpace(ocr.Text))
            {
                window.SetResult("文字识别", "识别结果", "", status: ocr.Error ?? "未识别到文字", success: false);
                return;
            }

            window.SetResult("文字识别", "识别结果", ocr.Text, actionText: ocr.Text, status: OcrStatus(ocr));
        }

        /// <summary>Alt+Shift+F 静默 OCR：截图 → OCR → 自动复制到剪贴板（不弹窗）</summary>
        private async Task SilentOcrAsync(CancellationToken ct)
        {
            var region = await CaptureSelectionAsync().ConfigureAwait(true);
            if (region == null || ct.IsCancellationRequested) return;

            var ocr = await OcrAsync(region.Value, ct).ConfigureAwait(true);
            if (ct.IsCancellationRequested) return;
            if (!ocr.Success || string.IsNullOrWhiteSpace(ocr.Text))
            {
                System.Diagnostics.Debug.WriteLine($"静默 OCR 失败: {ocr.Error}");
                return;
            }

            try
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    System.Windows.Clipboard.SetText(ocr.Text);
                });
                System.Diagnostics.Debug.WriteLine($"静默 OCR 已复制 {ocr.Text.Length} 字符到剪贴板");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"静默 OCR 复制失败: {ex.Message}");
            }
        }

        // ══════════════════════════════════════════════
        //  基础原子操作
        // ══════════════════════════════════════════════

        /// <summary>弹出截图选区窗口，返回物理像素矩形（取消为 null）</summary>
        private Task<System.Drawing.Rectangle?> CaptureSelectionAsync()
        {
            // ScreenshotWindow 内部 TCS 为单一可信源（Complete 用 TrySetResult，Closed 兗底亦然），
            // 这里直接返回其 Task，无需再包一层外层 TCS。
            // 截底图前 ScreenshotWindow 会自动隐藏所有可见的应用窗口（ResultWindow、
            // Clipboard、TodoList 等），避免 CopyFromScreen 把应用自身 UI 截进底图。
            return Application.Current.Dispatcher.Invoke(() =>
            {
                var shot = new ScreenshotWindow();
                return shot.SelectRegionAsync();
            });
        }
        
        private async Task<Models.OcrResult> OcrAsync(System.Drawing.Rectangle region, CancellationToken ct)
        {
            using var bitmap = ScreenshotService.CaptureRegion(region);
            if (bitmap == null)
                return Models.OcrResult.Fail("截图区域无效");

            // 传入真实 token：原生推理本身不可中断，但「排队等信号量」这一段能立刻取消，
            // 长识别跑完后也会由调用方按 token 丢弃结果，不会用过期结果覆盖界面。
            var result = await _ocr.RecognizeAsync(bitmap, ct).ConfigureAwait(true);
            if (ct.IsCancellationRequested)
                return Models.OcrResult.Fail("识别已取消");

            RecordHistory(result);
            return result;
        }

        /// <summary>
        /// 把成功的识别结果落进历史。
        /// 三条链路（截图翻译 / 文字识别 / 静默 OCR）都经过 <see cref="OcrAsync"/>，
        /// 因此在这里记录一次即可全覆盖；写库失败只记日志，绝不影响识别主流程。
        /// </summary>
        private void RecordHistory(Models.OcrResult result)
        {
            if (!result.Success || string.IsNullOrWhiteSpace(result.Text)) return;

            try
            {
                _history.Add(new Models.OcrHistoryItem
                {
                    Text = result.Text,
                    CharCount = result.Text.Length,
                    BlockCount = result.BlockCount,
                    ElapsedMs = result.ElapsedMs,
                    Model = _ocr.Name,
                    AutoInverted = result.AutoInverted,
                }, _settingsManager.Settings.OcrHistoryMaxItems);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"写入识别历史失败: {ex.Message}");
            }
        }

        /// <summary>识别中的提示语：引擎尚未就绪时说明正在加载模型，避免用户以为卡死</summary>
        private string OcrLoadingHint() =>
            _ocr.IsReady ? "识别中…" : "正在加载本地 OCR 模型（约需数秒）…";

        /// <summary>识别成功后的状态栏文案（含模型回退与深色反色提示）</summary>
        private string OcrStatus(Models.OcrResult ocr)
        {
            var text = $"{_ocr.Name} · {ocr.BlockCount} 块 · {ocr.ElapsedMs} ms";
            if (ocr.AutoInverted)
                text += " · 已反色";
            if (_ocr is RapidOcrProvider p && p.FallbackNotice != null)
                text += $" · {p.FallbackNotice}";
            return text;
        }

        private async Task<Models.TranslationResult> TranslateAsync(string text, CancellationToken ct)
        {
            var s = _settingsManager.Settings;
            try
            {
                var (translation, detected) = await _translator.TranslateAsync(
                    text, s.TranslateSourceLang, s.TranslateTargetLang, ct)
                    .ConfigureAwait(true);

                // 智能跳过：检测到的源语言与目标语言相同时，跳过翻译直接返回原文
                // （典型场景：OCR 截到英文但目标语言是 zh-CN，或截到中文但目标是 en）
                if (detected != null && IsSameLang(detected, s.TranslateTargetLang))
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"翻译跳过：检测到源语言 {detected} = 目标语言 {s.TranslateTargetLang}");
                    return Models.TranslationResult.Ok(text, text, detected);
                }

                return Models.TranslationResult.Ok(text, translation, detected);
            }
            catch (Exception ex)
            {
                return Models.TranslationResult.Fail(text, ex.Message);
            }
        }

        /// <summary>
        /// 归一化语言代码，用于比较源/目标语言是否相同。
        /// Google API 可能返回 "zh" 而非 "zh-CN"，需统一处理。
        /// </summary>
        private static string NormalizeLang(string? lang)
        {
            if (string.IsNullOrEmpty(lang)) return "";
            var l = lang.ToLowerInvariant().Trim();
            // Google 返回 "zh" 表示中文（不区分简繁），统一映射到 "zh"
            if (l == "zh" || l == "zh-cn" || l == "zh-tw") return "zh";
            return l;
        }

        /// <summary>检测到的源语言是否与目标语言相同（跳过翻译）</summary>
        private static bool IsSameLang(string detected, string target)
            => NormalizeLang(detected) == NormalizeLang(target);

        // ══════════════════════════════════════════════
        //  结果窗「翻译 / 搜索」按钮回调（SnapFind 式就地操作）
        // ══════════════════════════════════════════════

        /// <summary>点击「翻译」：就地翻译识别出的文字，结果仍显示在同一结果窗</summary>
        private async void OnTranslateRequested(object? sender, ResultWindow.TextRequestedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(e.Text)) return;
            var window = (sender as ResultWindow) ?? _resultWindow;
            if (window == null) return;

            var title = window.CurrentTitle;
            window.SetLoading(title, "译文", "翻译中…");

            // 结果窗按钮是用户主动点击的，不参与热键那套「新触发取消旧触发」，
            // 因此这里给一个不会被取消的 token
            var t = await TranslateAsync(e.Text, CancellationToken.None).ConfigureAwait(true);
            if (t.Success)
                window.SetResult(title, "译文", t.Target,
                    source: e.Text, actionText: e.Text, status: _translator.Name);
            else
                window.SetResult(title, "译文", "",
                    source: e.Text, actionText: e.Text, status: t.Error, success: false);
        }

        /// <summary>点击「搜索」：用默认浏览器打开搜索页</summary>
        private void OnSearchRequested(object? sender, ResultWindow.TextRequestedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(e.Text)) return;
            var window = (sender as ResultWindow) ?? _resultWindow;

            var url = BuildSearchUrl(e.Text);
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                window?.SetStatus("已打开浏览器搜索");
            }
            catch (Exception ex)
            {
                window?.SetStatus($"打开搜索失败: {ex.Message}");
            }
        }

        /// <summary>根据设置拼出搜索 URL（bing / google / baidu，默认 bing）</summary>
        private string BuildSearchUrl(string text)
        {
            var engine = (_settingsManager.Settings.OcrSearchEngine ?? "bing").ToLowerInvariant();
            var q = Uri.EscapeDataString(text);
            return engine switch
            {
                "google" => $"https://www.google.com/search?q={q}",
                "baidu" => $"https://www.baidu.com/s?wd={q}",
                _ => $"https://www.bing.com/search?q={q}",
            };
        }

        /// <summary>模拟 Ctrl+C 复制当前选中内容并读取剪贴板</summary>
        private async Task<string?> GetSelectionTextAsync()
        {
            string before = string.Empty;
            Application.Current.Dispatcher.Invoke(() =>
            {
                try { before = System.Windows.Clipboard.GetText(); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"GetSelectionText(before): {ex.Message}"); }
            });

            // 划词翻译由 Alt+D 触发，此时 Alt 可能仍被按住，先释放 Alt/Shift
            // 否则模拟的 Ctrl+C 会变成 Ctrl+Alt+C 导致复制失败
            keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            keybd_event(VK_SHIFT, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            await Task.Delay(30).ConfigureAwait(true);

            keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);
            keybd_event(VK_C, 0, 0, UIntPtr.Zero);
            keybd_event(VK_C, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);

            // 轮询剪贴板，等待新内容
            for (int i = 0; i < 20; i++)
            {
                await Task.Delay(30).ConfigureAwait(true);
                string current = string.Empty;
                Application.Current.Dispatcher.Invoke(() =>
                {
                    try { current = System.Windows.Clipboard.GetText(); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"GetSelectionText(poll): {ex.Message}"); }
                });
                if (!string.IsNullOrEmpty(current) && current != before)
                    return current;
            }
            return null;
        }

        private ResultWindow EnsureResultWindow()
        {
            var window = _resultWindow;
            if (window != null && window.IsLoaded) return window;

            // 返回 Invoke 的结果而非读回字段，编译器才能确定非空
            return Application.Current.Dispatcher.Invoke(() =>
            {
                _resultWindow = new ResultWindow();
                // 结果窗右下角「翻译 / 搜索」按钮 → 回传识别文本由模块处理
                _resultWindow.TranslateRequested += OnTranslateRequested;
                _resultWindow.SearchRequested += OnSearchRequested;
                // 关窗即瘦身：结果窗隐藏后主动压缩 OCR 引擎内存（对标 SnapFind）
                _resultWindow.IsVisibleChanged += OnResultWindowVisibilityChanged;
                return _resultWindow;
            });
        }

        /// <summary>结果窗隐藏后立即触发 OCR 引擎瘦身（卸载原生推理实例并压缩工作集）</summary>
        private void OnResultWindowVisibilityChanged(object? sender, DependencyPropertyChangedEventArgs e)
        {
            if (sender is ResultWindow w && !w.IsVisible && _ocr is RapidOcrProvider paddle)
                paddle.RequestSlimNow();
        }

        /// <summary>
        /// 统一的异步入口：可取消 + 异常兜底。
        ///
        /// 并发语义与旧版相反 —— 旧版遇到「已有任务在跑」直接 return，
        /// 用户连按两次热键时第二次毫无反应，会以为程序卡死或热键坏了。
        /// 现在改为「新请求取消旧请求」，因为连按热键的真实意图就是重来一次。
        /// </summary>
        private async void RunAsync(Func<CancellationToken, Task> task)
        {
            // 取消上一次：旧 token 立即失效，其后续每一步的界面写入都会被拦下。
            // 这里只 Cancel 不 Dispose —— 旧任务仍可能正持有该 token 等信号量，
            // 提前释放会让它在 Register 时撞上 ObjectDisposedException；
            // 释放交给它自己的 finally 处理（Dispose 幂等，不必担心重复）。
            var previous = _runCts;
            if (previous != null)
            {
                try { previous.Cancel(); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"取消上次任务失败: {ex.Message}"); }
            }

            var cts = new CancellationTokenSource();
            _runCts = cts;

            try
            {
                await task(cts.Token).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                // 被后一次触发顶掉，属正常流程，不弹错误
                System.Diagnostics.Debug.WriteLine("Translate 模块：本次任务已被新的触发取代");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Translate 模块异常: {ex}");
                if (!cts.IsCancellationRequested)
                {
                    try
                    {
                        var w = EnsureResultWindow();
                        w.SetResult("翻译", "译文", "", status: ex.Message, success: false);
                        w.ShowAtMouse();
                    }
                    catch (Exception ex2) { System.Diagnostics.Debug.WriteLine($"RunAsync 错误展示失败: {ex2.Message}"); }
                }
            }
            finally
            {
                // 只有「自己仍是最新一次」时才复位，避免被顶掉的旧任务清掉新任务的状态
                if (ReferenceEquals(_runCts, cts))
                {
                    _runCts = null;
                }
                try { cts.Dispose(); } catch { /* 已释放则忽略 */ }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            // 正在跑的识别/翻译任务随之取消，避免退出时还有回调往已释放的窗口写数据
            try { _runCts?.Cancel(); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"TranslateModule.Dispose(cts): {ex.Message}"); }
            try { _settingsManager.SettingsChanged -= OnSettingsChanged; } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"TranslateModule.Dispose(unsub): {ex.Message}"); }
            try { _hotkey.Dispose(); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"TranslateModule.Dispose(hotkey): {ex.Message}"); }
            try { _ocr.Dispose(); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"TranslateModule.Dispose(ocr): {ex.Message}"); }
            try { _history.Dispose(); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"TranslateModule.Dispose(history): {ex.Message}"); }
        }
    }
}
