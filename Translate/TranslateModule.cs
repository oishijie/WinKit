using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
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

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

        private const byte VK_CONTROL = 0x11;
        private const byte VK_C = 0x43;
        private const byte VK_MENU = 0x12;   // Alt
        private const byte VK_SHIFT = 0x10;
        private const uint KEYEVENTF_KEYUP = 0x0002;

        private readonly SettingsManager _settingsManager;
        private ITranslator _translator;
        private TranslatorOptions _translatorOptions;
        private readonly IOcrProvider _ocr;
        private HotkeyService _hotkey;

        private ResultWindow? _resultWindow;
        private bool _busy;
        private bool _enabled;
        private bool _disposed;

        public bool IsEnabled => _enabled;

        /// <summary>截图快捷键触发后完成的截图（供剪贴板模块订阅存入历史）</summary>
        public event EventHandler<System.Windows.Media.Imaging.BitmapSource>? ScreenshotCaptured;

        /// <summary>当前 OCR 引擎显示名（含实际加载的模型）</summary>
        public string OcrEngineName => _ocr.Name;

        /// <summary>OCR 引擎是否已完成初始化，可立即识别</summary>
        public bool OcrIsReady => _ocr.IsReady;

        /// <summary>初始化失败原因（未失败为 null）</summary>
        public string? OcrInitializationError =>
            (_ocr is PaddleOcrProvider paddle) ? paddle.InitializationError : null;

        /// <summary>因首选模型缺失而回退到其它模型时的提示文案（无回退为 null）</summary>
        public string? OcrFallbackNotice =>
            (_ocr is PaddleOcrProvider paddle) ? paddle.FallbackNotice : null;

        public TranslateModule(SettingsManager settingsManager)
        {
            _settingsManager = settingsManager;
            _translatorOptions = TranslatorOptions.FromSettings(settingsManager.Settings);
            _translator = CreateTranslator(_translatorOptions);
            _ocr = new PaddleOcrProvider(OcrEngineOptions.FromSettings(settingsManager.Settings));
            _hotkey = new HotkeyService();

            // 配置变更后同步引擎参数（内部会判断是否真的需要重建）
            _settingsManager.SettingsChanged += OnSettingsChanged;
        }

        /// <summary>
        /// 后台预热本地 OCR 引擎。冷启动需要数秒，提前加载可让首次热键即时响应。
        /// </summary>
        public Task WarmUpOcrAsync() => _ocr.WarmUpAsync();

        private void OnSettingsChanged(object? sender, AppSettings settings)
        {
            if (_ocr is PaddleOcrProvider paddle)
            {
                paddle.Reconfigure(OcrEngineOptions.FromSettings(settings));

                // 配置变更后立即在后台预热引擎，避免首次识别时冷启动等数秒
                // （WarmUpAsync 内部判断 IsReady，已就绪时立即返回，开销可忽略）
                _ = Task.Run(async () =>
                {
                    try { await paddle.WarmUpAsync(); }
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

            // 快捷键变更时重新注册（先注销旧的再注册新的）
            if (_enabled)
            {
                Stop();
                Start();
            }
        }

        /// <summary>获取当前快捷键描述（供托盘菜单 / 设置中心动态显示）</summary>
        public string[] GetHotkeyDescriptions()
        {
            var s = _settingsManager.Settings;
            return new[]
            {
                $"截图翻译    {HotkeyConfig.Format(s.HotkeyScreenshotTranslate.VK, s.HotkeyScreenshotTranslate.Modifiers)}",
                $"划词翻译    {HotkeyConfig.Format(s.HotkeySelectionTranslate.VK, s.HotkeySelectionTranslate.Modifiers)}",
                $"文字识别    {HotkeyConfig.Format(s.HotkeyOcrOnly.VK, s.HotkeyOcrOnly.Modifiers)}",
                $"静默OCR     {HotkeyConfig.Format(s.HotkeySilentOcr.VK, s.HotkeySilentOcr.Modifiers)}",
                $"截图到剪贴板  {HotkeyConfig.Format(s.HotkeyScreenshot.VK, s.HotkeyScreenshot.Modifiers)}",
            };
        }

        /// <summary>按配置创建一个翻译器实例</summary>
        private static ITranslator CreateTranslator(TranslatorOptions o) =>
            o.Provider == "openai"
                ? new OpenAITranslator(o.OpenAIApiKey, o.OpenAIBaseUrl, o.OpenAIModel, o.SkipCertValidation)
                : new GoogleTranslator();

        /// <summary>
        /// 翻译器构造期参数。record 提供值相等语义：配置保存后可据此判断
        /// "翻译引擎相关配置是否真的变化"，无关改动（如 OCR 线程数）不触发重建。
        /// 注意：不含源/目标语言，因为翻译时实时读取最新设置。
        /// </summary>
        private sealed record TranslatorOptions(string Provider, string OpenAIApiKey, string OpenAIBaseUrl, string OpenAIModel, bool SkipCertValidation)
        {
            public static TranslatorOptions FromSettings(AppSettings s) => new(
                (s.TranslateProvider ?? "google").ToLowerInvariant(),
                s.OpenAIApiKey ?? "",
                s.OpenAIBaseUrl ?? "",
                s.OpenAIModel ?? "",
                s.OpenAISkipCertValidation);
        }

        /// <summary>根据配置启用/停用模块热键</summary>
        public void Start()
        {
            if (_enabled) return;
            var s = _settingsManager.Settings;
            if (!s.TranslateEnable) return;

            // 每次启用都重建 HotkeyService（旧的可能在 Stop 时被 Dispose）
            _hotkey.Dispose();
            _hotkey = new HotkeyService();

            // 从配置读取快捷键（用户可在设置中心自定义，AppSettings 属性初始化器保证非 null）
            var hkScreenshot = s.HotkeyScreenshotTranslate;
            var hkSelection = s.HotkeySelectionTranslate;
            var hkOcrOnly = s.HotkeyOcrOnly;
            var hkSilent = s.HotkeySilentOcr;

            // 截图翻译（默认 Alt+S）
            _hotkey.Register((uint)hkScreenshot.VK, hkScreenshot.Modifiers, () => RunAsync(ScreenshotTranslateAsync));
            // 划词翻译（默认 Alt+D）
            _hotkey.Register((uint)hkSelection.VK, hkSelection.Modifiers, () => RunAsync(SelectionTranslateAsync));
            // 本地 OCR 仅识别（默认 Alt+Shift+S）
            _hotkey.Register((uint)hkOcrOnly.VK, hkOcrOnly.Modifiers, () => RunAsync(OcrOnlyAsync));
            // 静默 OCR（默认 Alt+Shift+F）
            _hotkey.Register((uint)hkSilent.VK, hkSilent.Modifiers, () => RunAsync(SilentOcrAsync));

            // 截图到剪贴板历史（默认 Alt+A）
            var hkClipScreenshot = s.HotkeyScreenshot;
            _hotkey.Register((uint)hkClipScreenshot.VK, hkClipScreenshot.Modifiers, () => RunAsync(ScreenshotToClipboardAsync));

            _enabled = true;
        }

        public void Stop()
        {
            if (!_enabled) return;
            _hotkey.Dispose();
            _enabled = false;
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
        private async Task ScreenshotTranslateAsync()
        {
            var region = await CaptureSelectionAsync().ConfigureAwait(true);
            if (region == null) return; // 用户取消

            var window = EnsureResultWindow();
            window.SetLoading("截图翻译", "译文", OcrLoadingHint());
            window.ShowAtMouse();

            var ocr = await OcrAsync(region.Value).ConfigureAwait(true);
            if (!ocr.Success || string.IsNullOrWhiteSpace(ocr.Text))
            {
                window.SetResult("截图翻译", "译文", "", status: ocr.Error ?? "未识别到文字", success: false);
                return;
            }

            window.SetLoading("截图翻译", "译文", "翻译中…");
            var t = await TranslateAsync(ocr.Text).ConfigureAwait(true);
            if (!t.Success)
            {
                window.SetResult("截图翻译", "译文", "", source: ocr.Text, status: t.Error, success: false);
                return;
            }

            window.SetResult("截图翻译", "译文", t.Target,
                source: ocr.Text, actionText: ocr.Text, status: $"{OcrStatus(ocr)} · {_translator.Name}");
        }

        /// <summary>Alt+D 划词翻译：模拟 Ctrl+C → 翻译 → 结果窗</summary>
        private async Task SelectionTranslateAsync()
        {
            var window = EnsureResultWindow();
            window.SetLoading("划词翻译", "译文", "正在获取选中文本…");
            window.ShowAtMouse();

            var selected = await GetSelectionTextAsync().ConfigureAwait(true);
            if (string.IsNullOrWhiteSpace(selected))
            {
                window.SetResult("划词翻译", "译文", "", status: "未获取到选中文本", success: false);
                return;
            }

            window.SetLoading("划词翻译", "译文", "翻译中…");
            var t = await TranslateAsync(selected).ConfigureAwait(true);
            if (!t.Success)
            {
                window.SetResult("划词翻译", "译文", "", source: selected, status: t.Error, success: false);
                return;
            }

            window.SetResult("划词翻译", "译文", t.Target, source: selected, actionText: selected, status: _translator.Name);
        }

        /// <summary>Alt+Shift+S OCR 识别：截图 → OCR → 结果窗（不翻译）</summary>
        private async Task OcrOnlyAsync()
        {
            var region = await CaptureSelectionAsync().ConfigureAwait(true);
            if (region == null) return;

            var window = EnsureResultWindow();
            window.SetLoading("文字识别", "识别结果", OcrLoadingHint());
            window.ShowAtMouse();

            var ocr = await OcrAsync(region.Value).ConfigureAwait(true);
            if (!ocr.Success || string.IsNullOrWhiteSpace(ocr.Text))
            {
                window.SetResult("文字识别", "识别结果", "", status: ocr.Error ?? "未识别到文字", success: false);
                return;
            }

            window.SetResult("文字识别", "识别结果", ocr.Text, actionText: ocr.Text, status: OcrStatus(ocr));
        }

        /// <summary>Alt+Shift+F 静默 OCR：截图 → OCR → 自动复制到剪贴板（不弹窗）</summary>
        private async Task SilentOcrAsync()
        {
            var region = await CaptureSelectionAsync().ConfigureAwait(true);
            if (region == null) return;

            var ocr = await OcrAsync(region.Value).ConfigureAwait(true);
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
        
        /// <summary>
        /// 弹出截图选区，返回选中区域的 BitmapSource（供剪贴板等外部模块调用）。
        /// 用户取消返回 null。
        /// </summary>
        public async Task<System.Windows.Media.Imaging.BitmapSource?> CaptureScreenshotAsync()
        {
            var region = await CaptureSelectionAsync().ConfigureAwait(true);
            if (region == null) return null;
        
            using var bitmap = ScreenshotService.CaptureRegion(region.Value);
            if (bitmap == null) return null;
        
            // System.Drawing.Bitmap → BitmapSource
            var handle = bitmap.GetHbitmap();
            try
            {
                var source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                    handle, System.IntPtr.Zero,
                    System.Windows.Int32Rect.Empty,
                    System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
                source.Freeze(); // 跨线程安全
                return source;
            }
            finally
            {
                DeleteObject(handle);
            }
        }

        /// <summary>截图快捷键触发：截屏 → 触发 ScreenshotCaptured 事件</summary>
        private async Task ScreenshotToClipboardAsync()
        {
            var bitmap = await CaptureScreenshotAsync();
            if (bitmap != null)
                ScreenshotCaptured?.Invoke(this, bitmap);
        }

        private async Task<Models.OcrResult> OcrAsync(System.Drawing.Rectangle region)
        {
            using var bitmap = ScreenshotService.CaptureRegion(region);
            if (bitmap == null)
                return Models.OcrResult.Fail("截图区域无效");

            return await _ocr.RecognizeAsync(bitmap, CancellationToken.None).ConfigureAwait(true);
        }

        /// <summary>识别中的提示语：引擎尚未就绪时说明正在加载模型，避免用户以为卡死</summary>
        private string OcrLoadingHint() =>
            _ocr.IsReady ? "识别中…" : "正在加载本地 OCR 模型（约需数秒）…";

        /// <summary>识别成功后的状态栏文案（含模型回退提示）</summary>
        private string OcrStatus(Models.OcrResult ocr)
        {
            var text = $"{_ocr.Name} · {ocr.BlockCount} 块 · {ocr.ElapsedMs} ms";
            if (_ocr is PaddleOcrProvider p && p.FallbackNotice != null)
                text += $" · {p.FallbackNotice}";
            return text;
        }

        private async Task<Models.TranslationResult> TranslateAsync(string text)
        {
            var s = _settingsManager.Settings;
            try
            {
                var target = await _translator.TranslateAsync(text, s.TranslateSourceLang, s.TranslateTargetLang, CancellationToken.None)
                    .ConfigureAwait(true);
                return Models.TranslationResult.Ok(text, target);
            }
            catch (Exception ex)
            {
                return Models.TranslationResult.Fail(text, ex.Message);
            }
        }

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

            var t = await TranslateAsync(e.Text).ConfigureAwait(true);
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
            if (sender is ResultWindow w && !w.IsVisible && _ocr is PaddleOcrProvider paddle)
                paddle.RequestSlimNow();
        }

        /// <summary>统一的异步入口，串行化与异常兜底</summary>
        private async void RunAsync(Func<Task> task)
        {
            if (_busy) return;
            _busy = true;
            try
            {
                await task().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Translate 模块异常: {ex}");
                try
                {
                    var w = EnsureResultWindow();
                    w.SetResult("翻译", "译文", "", status: ex.Message, success: false);
                    w.ShowAtMouse();
                }
                catch (Exception ex2) { System.Diagnostics.Debug.WriteLine($"RunAsync 错误展示失败: {ex2.Message}"); }
            }
            finally
            {
                _busy = false;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _settingsManager.SettingsChanged -= OnSettingsChanged; } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"TranslateModule.Dispose(unsub): {ex.Message}"); }
            try { _hotkey.Dispose(); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"TranslateModule.Dispose(hotkey): {ex.Message}"); }
            try { _ocr.Dispose(); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"TranslateModule.Dispose(ocr): {ex.Message}"); }
        }
    }
}
