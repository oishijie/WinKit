using System;
using System.Threading;
using System.Windows;
using WinKit.Capture;
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
        /// <summary>已装配到钩子上的唤出键快照，用于判断设置变更后是否真需要重装</summary>
        private HotkeyConfig? _appliedClipboardHotkey;
        /// <summary>上面那份快照对应的「剪贴板监控」开关状态</summary>
        private bool _appliedClipboardEnabled;

        /// <summary>
        /// 顶层热键服务 —— 装配不属于 Translate / Capture 模块、也不走键盘钩子的热键（目前是待办唤出键）。
        /// 单独一份实例，避免与各模块的热键生命周期互相干扰。
        /// </summary>
        private HotkeyService? _topLevelHotkeys;
        /// <summary>已装配的顶层热键配置签名，用于判断设置变更后是否真需要重装</summary>
        private string? _appliedTopLevelSignature;

        private TrayHelper? _trayHelper;
        private TranslateModule? _translateModule;
        private CaptureModule? _captureModule;
        private SettingsWindow? _settingsWindow;

        /// <summary>
        /// 录制快捷键期间收集到的「注册失败」热键显示名，录制结束后一次性提示。
        /// 旧版忽略 Register 的返回值，用户改成被别的软件占用的组合后毫无反馈，只感觉「改了没用」。
        /// </summary>
        private readonly System.Collections.Generic.List<string> _failedHotkeys = new();

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
        public CaptureModule? CaptureModule => _captureModule;

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

            // 3.5 设置中心窗口（常驻隐藏，复用以秒开）
            _settingsWindow = new SettingsWindow(_settingsManager);

            // 3.6 设置中心进入快捷键录制时，临时挂起各模块的全局热键。
            //     否则用户按下「当前已生效的组合」时，Windows 会把按键直接发给注册者，
            //     设置窗收不到；弹出的截图 / 翻译窗还会把设置窗顶得失焦，连录制都被取消。
            _settingsWindow.HotkeyRecordingChanged += OnHotkeyRecordingChanged;

            // 4. 初始化合并后的托盘服务
            _trayHelper = new TrayHelper(this, _settingsManager, _todoWindow, _pasteWindow, _settingsWindow);
            _todoWindow.SetSettingsWindow(_settingsWindow);

            // 5. 装配低级键盘钩子，接管剪贴板唤出键（默认 Win+V）。
            //    含 Win 的组合注册不上 RegisterHotKey，只能走钩子；钩子顺带吞掉按键，
            //    使系统自带的剪贴板面板不再弹出。
            ApplyClipboardHotkey();

            // 5.1 热键配置变更时重装（剪贴板唤出键 + 顶层热键）。
            //     注意这里读的是最新配置，改动一次设置最多重装一次（两者内部都有快照比较）。
            _settingsManager.SettingsChanged += OnSettingsChangedForHotkeys;

            // 5.2 装配顶层热键 —— 待办唤出键（默认 Alt+T）。
            //     必须放在 TrayHelper 之后：热键动作直接复用托盘菜单那套 ToggleTodoWindow，不另写一份。
            ApplyTopLevelHotkeys();

            // 6. 初始化翻译 / OCR 模块并注册全局热键 (Alt+S/D、Alt+Shift+S/F)
            _translateModule = new TranslateModule(_settingsManager);
            _translateModule.Start();

            // 6.1 初始化截图模块并注册截图热键（默认 Alt+A）。
            //     独立于翻译与剪贴板：翻译模块被关掉也照样能截图。
            _captureModule = new CaptureModule(_settingsManager);
            _captureModule.Start();

            // 6.1.1 订阅注册失败上报。放在 Start 之后是有意的：
            //       启动阶段若有键被别的软件占用，静默记日志即可，不打扰用户；
            //       用户在设置里主动改键时才会收到提示。
            _translateModule.HotkeyRegistrationFailed += OnHotkeyRegistrationFailed;
            _captureModule.HotkeyRegistrationFailed += OnHotkeyRegistrationFailed;

            // 6.2 后台预热本地 OCR 引擎。
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
            //    需要时点托盘菜单「显示待办」、双击托盘图标，或按截图/翻译热键即可唤起。
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

        public void ToggleClipboardFeature(bool enable)
        {
            if (enable)
            {
                _clipboardService?.StartMonitoring();
            }
            else
            {
                _clipboardService?.StopMonitoring();
                _pasteWindow?.Hide();
            }

            // 钩子随开关启停：启用时装回唤出键，停用时卸载（把 Win+V 归还给系统）。
            // 刻意不销毁服务实例 —— 保留它便于再次启用时快速装回。
            ApplyClipboardHotkey();
        }

        /// <summary>设置变更：剪贴板唤出键 / 顶层热键可能变了，需要重装</summary>
        private void OnSettingsChangedForHotkeys(object? sender, AppSettings settings)
        {
            // 录制期间跳过：那一刻按键正走钩子转发通道，重装会把这次按击吃掉；
            // 录制结束时会统一按最新配置装回去（见 OnHotkeyRecordingChanged）。
            if (_settingsWindow?.IsRecordingHotkey == true) return;
            ApplyClipboardHotkey();
            ApplyTopLevelHotkeys();
        }

        /// <summary>
        /// 按当前配置装配低级键盘钩子（剪贴板唤出键，默认 Win+V）。
        ///
        /// 为什么这条热键不走 HotkeyService：含 Win 键的组合被系统保留，
        /// RegisterHotKey 注册必然失败；而且我们需要「吞掉按键」这个额外能力，
        /// 才能阻止系统自带的剪贴板面板弹出。
        ///
        /// 配置没变化时直接返回，避免改无关设置（如 OCR 线程数）也把钩子装卸一次。
        /// </summary>
        private void ApplyClipboardHotkey()
        {
            if (_settingsManager == null) return;

            var settings = _settingsManager.Settings;
            var hk = settings.HotkeyClipboardPanel ?? new HotkeyConfig();
            bool enabled = settings.PasteEnableMonitoring;

            if (_appliedClipboardHotkey != null
                && _appliedClipboardEnabled == enabled
                && _appliedClipboardHotkey.VK == hk.VK
                && _appliedClipboardHotkey.Modifiers == hk.Modifiers)
            {
                return;
            }

            _appliedClipboardHotkey = new HotkeyConfig(hk.VK, hk.Modifiers);
            _appliedClipboardEnabled = enabled;

            // 剪贴板监控关闭时整条链路停用，连带把 Win+V 归还给系统
            if (!enabled)
            {
                _keyboardHookService?.SetBindings(null);
                return;
            }

            if (_keyboardHookService == null)
            {
                _keyboardHookService = new KeyboardHookService();
                // 录制期间钩子截获到的 Win 组合 → 转交设置窗提交。
                // 这条通道是必要的：含 Win 的组合到不了 WPF 键盘事件，只有钩子看得见。
                // （事件已由服务投递到 UI 线程，这里可以直接操作窗口）
                _keyboardHookService.RecordingInputCaptured += (vk, mods) =>
                    _settingsWindow?.FeedHotkeyInput(vk, mods);
            }

            var bindings = new System.Collections.Generic.List<KeyboardHookService.HookBinding>();
            foreach (var descriptor in HotkeyCatalog.HookHotkeys())
            {
                var config = descriptor.Get(settings);
                if (config.VK == 0) continue;

                string id = descriptor.Id;   // 闭包捕获：循环变量会被后续迭代改写
                int vk = config.VK;
                int mods = config.Modifiers;
                bindings.Add(new KeyboardHookService.HookBinding(
                    vk, mods, (pressedVk, pressedMods) => OnHookHotkey(id, pressedVk, pressedMods)));
            }

            _keyboardHookService.SetBindings(bindings);
        }

        /// <summary>
        /// 装配「顶层热键」—— 不属于 Translate / Capture 模块、也不走键盘钩子的那些（目前是待办唤出键）。
        ///
        /// 为什么放在 App 而不是某个模块：待办窗口由 App 直接持有，没有自己的模块类。
        /// HotkeyCatalog 是单一数据源，App 作为顶层装配者兜住「没被模块认领」的条目 ——
        /// 将来再加这类热键，只需在 catalog 里加一项 + 在 ResolveTopLevelAction 里接一个动作。
        /// </summary>
        private void ApplyTopLevelHotkeys()
        {
            if (_settingsManager == null) return;

            var settings = _settingsManager.Settings;

            // 先收集本轮要注册的条目并拼出「配置签名」——
            // 签名没变就整体跳过，避免改无关设置（OCR 线程数等）也把热键重装一次。
            var pending = new System.Collections.Generic.List<(string Label, HotkeyConfig Cfg, Action Run)>();
            var signature = new System.Text.StringBuilder();

            foreach (var descriptor in HotkeyCatalog.All)
            {
                // 已被模块认领的（各自持有 HotkeyService）与走钩子的（Clipboard）都不归这里管
                if (descriptor.Owner == HotkeyOwner.Translate) continue;
                if (descriptor.Owner == HotkeyOwner.Capture) continue;
                if (descriptor.Mechanism != HotkeyMechanism.RegisterHotKey) continue;

                var action = ResolveTopLevelAction(descriptor.Id);
                if (action == null) continue;

                var cfg = descriptor.Get(settings);
                signature.Append(descriptor.Id).Append('=')
                         .Append(cfg.VK).Append(',').Append(cfg.Modifiers).Append(';');
                pending.Add((descriptor.Label, cfg, action));
            }

            string sig = signature.ToString();
            if (_appliedTopLevelSignature == sig) return;
            _appliedTopLevelSignature = sig;

            // HotkeyService 没有「单条注销」的 API，只能整体 Dispose，所以每次重装都换一个新实例
            _topLevelHotkeys?.Dispose();
            _topLevelHotkeys = new HotkeyService();

            foreach (var item in pending)
            {
                // VK == 0（用户把这条热键清空了）由 Register 内部按「无需注册」放行，不会误报失败
                if (!_topLevelHotkeys.Register((uint)item.Cfg.VK, item.Cfg.Modifiers, item.Run))
                {
                    System.Diagnostics.Debug.WriteLine($"App: 顶层热键注册失败 {item.Label}");
                    OnHotkeyRegistrationFailed(this, item.Label);
                }
            }
        }

        /// <summary>顶层热键 Id → 动作。新增顶层热键时在这里接一条。</summary>
        private Action? ResolveTopLevelAction(string id) => id switch
        {
            // 与托盘菜单「显示待办」共用同一个实现（内部还会同步菜单文字），行为不会跑偏
            "todoPanel" => () => _trayHelper?.ToggleTodoWindow(),
            _ => null,
        };

        /// <summary>
        /// 钩子型热键命中。已在 UI 线程（服务内部做了投递），可以直接操作窗口。
        ///
        /// 唯一的分支值得留意：若设置中心正在录制快捷键，就把按键原样转给它 ——
        /// 含 Win 的组合到不了 WPF 的键盘事件，只有钩子看得见，不转发用户就永远录不进 Win 组合。
        /// </summary>
        private void OnHookHotkey(string id, int pressedVk, int pressedMods)
        {
            if (_settingsWindow != null && _settingsWindow.IsRecordingHotkey)
            {
                _settingsWindow.FeedHotkeyInput(pressedVk, pressedMods);
                return;
            }

            if (id == "clipboardPanel") TogglePastePanel();
        }

        /// <summary>切换剪贴板面板的显示 / 隐藏</summary>
        private void TogglePastePanel()
        {
            if (_pasteWindow == null) return;

            if (_pasteWindow.IsVisible) _pasteWindow.Hide();
            else _pasteWindow.ShowAtMouse();
        }

        /// <summary>开启/关闭翻译 & OCR 模块及其全局热键（供托盘调用）</summary>
        public void ToggleTranslateFeature(bool enable)
        {
            _translateModule?.SetEnabled(enable);
        }

        // ══════════════════════════════════════════════
        //  快捷键录制期间的热键挂起 / 恢复
        // ══════════════════════════════════════════════

        /// <summary>
        /// 设置中心的快捷键录制开关。
        /// 进入录制 → 注销全部全局热键（否则按键会被系统先发给注册者，录制收不到键）；
        /// 退出录制 → 按最新配置装回去，装不上的那些明确告知用户。
        /// </summary>
        private void OnHotkeyRecordingChanged(object? sender, bool recording)
        {
            if (recording)
            {
                _failedHotkeys.Clear();
                // 钩子**不挂起**，而是切到录制模式：含 Win 的组合到不了 WPF 键盘事件，
                // 只有钩子看得见 —— 由它截获转发给设置窗（见 OnHookHotkey 与 RecordingInputCaptured）。
                // 挂起反而会让 Win+V 落到系统手里、弹出系统剪贴板并顶掉设置窗焦点。
                if (_keyboardHookService != null) _keyboardHookService.RecordingMode = true;

                _translateModule?.SuspendHotkeys();
                _captureModule?.SuspendHotkeys();

                // 顶层热键同样要注销 —— 否则用户按下待办唤出键时，按键被系统直接发给注册者，
                // 待办窗口弹出还会顶掉设置窗焦点，录制当场中断（与模块热键同一个坑）。
                _topLevelHotkeys?.Dispose();
                _appliedTopLevelSignature = null;
                return;
            }

            // 退出录制模式，恢复正常的「只匹配已生效绑定」行为
            if (_keyboardHookService != null) _keyboardHookService.RecordingMode = false;

            // 恢复时读的是当前配置，因此装回去的已经是用户刚录制的新组合
            _translateModule?.ResumeHotkeys();
            _captureModule?.ResumeHotkeys();
            // 钩子型热键走的是另一条注册通道，同样要按新配置重新装配。
            // 但录制期间钩子没停过，所以这里需要显式重装 —— 钩子内部持有的是旧键。
            ApplyClipboardHotkey();
            // 顶层热键在录制期间被主动注销，这里按最新配置装回
            ApplyTopLevelHotkeys();

            if (_failedHotkeys.Count == 0) return;

            // 注册是同步完成的，这里取完即可；提示延到下一帧，避免和键帽重建抢同一帧
            string failed = string.Join("\n  · ", _failedHotkeys);
            _failedHotkeys.Clear();

            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() =>
            {
                MessageBox.Show(
                    $"以下快捷键没能注册成功，多半已被其它程序占用：\n\n  · {failed}\n\n" +
                    "请换一个组合，或先关掉占用它的软件再设置。",
                    "快捷键未生效",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }));
        }

        /// <summary>热键注册失败：先记账，等录制结束再由 OnHotkeyRecordingChanged 统一提示</summary>
        private void OnHotkeyRegistrationFailed(object? sender, string label)
        {
            if (!_failedHotkeys.Contains(label)) _failedHotkeys.Add(label);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            // 优雅释放所有非托管钩子和资源
            _singleInstanceMutex?.Dispose();
            _showExistingEvent?.Dispose();
            _translateModule?.Dispose();
            _captureModule?.Dispose();
            _keyboardHookService?.Dispose();
            _topLevelHotkeys?.Dispose();
            _clipboardService?.Dispose();
            _trayHelper?.Dispose();
            _clipboardManager?.Dispose();
            _settingsManager?.Dispose();

            base.OnExit(e);
        }
    }
}
