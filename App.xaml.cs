using System;
using System.Threading;
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

        /// <summary>单实例互斥体。持有期间保证同一会话只有一个 WinKit 进程常驻</summary>
        private Mutex? _singleInstanceMutex;
        /// <summary>用于让第二个实例唤醒首个实例主窗口的命名事件</summary>
        private EventWaitHandle? _showExistingEvent;

        /// <summary>单实例互斥体与唤醒事件的命名空间键（带固定后缀，避免与其它同名程序冲突）</summary>
        private const string SingleInstanceMutexName = @"Local\WinKit_SingleInstance_9F2D4A6E";
        private const string ShowExistingEventName = @"Local\WinKit_ShowExisting_9F2D4A6E";

        public ClipboardManager? ClipboardManager => _clipboardManager;
        public SettingsManager? SettingsManager => _settingsManager;
        public TranslateModule? TranslateModule => _translateModule;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // 0. 单实例保护：同一会话内只允许一个 WinKit 进程常驻。
            //    否则双击/重复启动会跑出多个托盘图标 + 多份 ~300MB 的 OCR 引擎。
            if (!OwnSingleInstance())
            {
                // 已有实例在运行 → 唤醒它的主窗口（带到前台）后退出本实例
                SignalExistingInstance();
                Shutdown();
                return;
            }

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

            // 7. 启动不再自动弹出 TodoList 待办窗口。
            //    需要时在托盘菜单「TodoList → 显示」或双击托盘图标即可打开。
        }

        /// <summary>
        /// 尝试取得单实例所有权。
        /// 返回 true 表示本进程是首个实例（已持有互斥体，并创建"唤醒主窗口"事件监听）；
        /// 返回 false 表示已有实例在运行（互斥体已被占用，本进程应退出）。
        /// </summary>
        private bool OwnSingleInstance()
        {
            // initialOwner=true：若互斥体尚不存在则创建并立即归本进程所有。
            // createdNew=false 意味着同名互斥体已存在 → 已有实例在跑。
            _singleInstanceMutex = new Mutex(true, SingleInstanceMutexName, out bool createdNew);
            if (!createdNew)
            {
                _singleInstanceMutex.Dispose();
                _singleInstanceMutex = null;
                return false;
            }

            // 首个实例：创建可被后续实例打开的命名事件，并后台监听——被唤醒时把主窗口带到前台。
            _showExistingEvent = new EventWaitHandle(false, EventResetMode.ManualReset, ShowExistingEventName);
            _ = System.Threading.Tasks.Task.Factory.StartNew(
                WatchShowEvent, System.Threading.Tasks.TaskCreationOptions.LongRunning);
            return true;
        }

        /// <summary>
        /// 后台监听"唤醒主窗口"事件：第二个实例启动时会 Set 它，
        /// 这里把首个实例的 TodoList 主窗口显示并激活到前台，等价于"再点一次启动 = 打开窗口"。
        /// </summary>
        private void WatchShowEvent()
        {
            while (_showExistingEvent != null)
            {
                try { _showExistingEvent.WaitOne(); }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"WinKit 单实例监听异常: {ex.Message}");
                    return;
                }

                _showExistingEvent.Reset();
                Dispatcher.Invoke(() =>
                {
                    if (_todoWindow != null)
                    {
                        if (!_todoWindow.IsVisible) _todoWindow.Show();
                        _todoWindow.Activate();
                    }
                });
            }
        }

        /// <summary>向首个实例发出"唤醒主窗口"信号（事件可能尚未创建，忽略异常）</summary>
        private void SignalExistingInstance()
        {
            try
            {
                using var ev = EventWaitHandle.OpenExisting(ShowExistingEventName);
                ev.Set();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"WinKit 唤醒已有实例失败（已退出本实例）: {ex.Message}");
            }
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
            _singleInstanceMutex?.Dispose();
            _showExistingEvent?.Dispose();
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
