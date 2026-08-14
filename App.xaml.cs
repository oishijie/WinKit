using System;
using System.Windows;
using WinKit.Common;
using WinKit.Clipboard.Services;
using WinKit.Translate;

namespace WinKit
{
    public partial class App : System.Windows.Application
    {
        private SettingsManager? _settingsManager;
        private ClipboardService? _clipboardService;
        private ClipboardManager? _clipboardManager;
        private KeyboardHookService? _keyboardHookService;
        private TrayHelper? _trayHelper;
        private TranslateModule? _translateModule;
        private SettingsWindow? _settingsWindow;

        private Todo.MainWindow? _todoWindow;
        private Clipboard.MainWindow? _pasteWindow;

        public ClipboardManager? ClipboardManager => _clipboardManager;
        public SettingsManager? SettingsManager => _settingsManager;
        public TranslateModule? TranslateModule => _translateModule;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // 1. 初始化统一配置管理器
            _settingsManager = new SettingsManager();

            // 2. 初始化剪贴板监听和数据中心（常驻后台）
            _clipboardService = new ClipboardService();
            _clipboardManager = new ClipboardManager(_settingsManager);

            _clipboardService.TextChanged += OnClipboardTextChanged;
            _clipboardService.ImageChanged += OnClipboardImageChanged;
            if (_settingsManager.Settings.PasteEnableMonitoring)
            {
                _clipboardService.StartMonitoring();
            }

            // 3. 实例化两个功能窗口
            _todoWindow = new Todo.MainWindow(_settingsManager);
            _pasteWindow = new Clipboard.MainWindow(_clipboardManager, _settingsManager);
            _pasteWindow.ScreenshotRequested += OnPasteScreenshotRequested;

            // 3.5 设置中心窗口（常驻隐藏，复用以秒开）
            _settingsWindow = new SettingsWindow(_settingsManager);

            // 4. 初始化合并后的托盘服务
            _trayHelper = new TrayHelper(this, _settingsManager, _todoWindow, _pasteWindow, _settingsWindow);
            _todoWindow.SetTray(_trayHelper);
            _todoWindow.SetSettingsWindow(_settingsWindow);

            // 5. 开启低级键盘钩子，全局接管 Win + V 快捷键
            if (_settingsManager.Settings.PasteEnableMonitoring)
            {
                RegisterGlobalKeyboardHook();
            }

            // 6. 初始化翻译 / OCR 模块并注册全局热键 (Alt+S/D、Alt+Shift+S/F、Alt+A)
            _translateModule = new TranslateModule(_settingsManager);
            _translateModule.ScreenshotCaptured += OnScreenshotCaptured;
            _translateModule.Start();

            // 6.1 后台预热本地 OCR 引擎。
            //     加载 PaddleOCR 原生库与模型需要数秒，放到后台线程提前完成，
            //     用户第一次按下热键时就不用干等。失败不影响其余功能。
            if (_settingsManager.Settings.TranslateEnable && _settingsManager.Settings.OcrPreloadOnStartup)
            {
                _ = System.Threading.Tasks.Task.Run(async () =>
                {
                    try
                    {
                        await _translateModule.WarmUpOcrAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"OCR 预热失败: {ex.Message}");
                    }
                });
            }

            // 7. 默认展现 TodoList 待办主窗口
            _todoWindow.Show();
            _todoWindow.Activate();
        }

        private void OnClipboardTextChanged(object? sender, string text)
        {
            _clipboardManager?.AddTextItem(text);
        }

        private void OnClipboardImageChanged(object? sender, System.Windows.Media.Imaging.BitmapSource image)
        {
            _clipboardManager?.AddImageItem(image);
        }

        /// <summary>截图快捷键触发：将截图存入剪贴板历史</summary>
        private void OnScreenshotCaptured(object? sender, System.Windows.Media.Imaging.BitmapSource bitmap)
        {
            _clipboardManager?.AddImageItem(bitmap);
        }

        /// <summary>剪贴板面板截图按钮：截屏 → 存入剪贴板历史</summary>
        private async void OnPasteScreenshotRequested(object? sender, EventArgs e)
        {
            if (_translateModule == null || _clipboardManager == null) return;

            try
            {
                var bitmap = await _translateModule.CaptureScreenshotAsync();
                if (bitmap != null)
                {
                    _clipboardManager.AddImageItem(bitmap);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"截图存入剪贴板失败: {ex.Message}");
            }
        }

        public void ToggleClipboardFeature(bool enable)
        {
            if (enable)
            {
                _clipboardService?.StartMonitoring();
                RegisterGlobalKeyboardHook();
            }
            else
            {
                _clipboardService?.StopMonitoring();
                _keyboardHookService?.Dispose();
                _keyboardHookService = null;
                _pasteWindow?.Hide();
            }
        }

        private void RegisterGlobalKeyboardHook()
        {
            _keyboardHookService?.Dispose();
            _keyboardHookService = new KeyboardHookService(() =>
            {
                Dispatcher.Invoke(() =>
                {
                    if (_pasteWindow != null)
                    {
                        if (_pasteWindow.IsVisible)
                        {
                            _pasteWindow.Hide();
                        }
                        else
                        {
                            _pasteWindow.ShowAtMouse();
                        }
                    }
                });
            });
        }

        /// <summary>开启/关闭翻译 & OCR 模块及其全局热键（供托盘调用）</summary>
        public void ToggleTranslateFeature(bool enable)
        {
            _translateModule?.SetEnabled(enable);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            // 优雅释放所有非托管钩子和资源
            _translateModule?.Dispose();
            _keyboardHookService?.Dispose();
            _clipboardService?.Dispose();
            _trayHelper?.Dispose();
            _clipboardManager?.Dispose();
            _settingsManager?.Dispose();

            base.OnExit(e);
        }
    }
}
