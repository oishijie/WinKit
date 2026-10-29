using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
        private SyncEngine? _syncEngine;
        /// <summary>同步总开关关闭时的显式推送专用引擎（不跑轮询循环，仅右键「同步到手机」用）</summary>
        private SyncEngine? _pushOnlyEngine;
        private UpdateService? _updateService;
        private CancellationTokenSource? _updateCts;
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

        /// <summary>待办历史（回收站）窗口：惰性创建、复用单实例</summary>
        private Todo.HistoryWindow? _historyWindow;

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
                // 右键「同步到手机」触发：把文件路径写进通道文件，唤醒主实例后退出
                var clipFiles = ParseClipSendArgs(e.Args);
                if (clipFiles.Length > 0)
                {
                    try { File.AppendAllLines(ShellMenuHelper.ClipSendFilePath, clipFiles); }
                    catch { /* 通道文件写失败时忽略，主实例无从得知 */ }
                }

                // 已有实例在运行 → 唤醒它的主窗口（带到前台）后退出本实例
                SignalExistingInstance();
                Shutdown();
                return;
            }

            // 1. 初始化统一配置管理器
            _settingsManager = new SettingsManager();

            // 1.5 右键菜单开关开着 → 刷新 exe 路径（程序挪位置后自动修正，无需重新开关）
            if (_settingsManager.Settings.SyncShellMenu)
            {
                var exePath = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exePath)) ShellMenuHelper.Install(exePath);
            }

            // 右键直达（无实例运行时首次启动）：留待托盘与引擎就绪后处理
            var selfClipFiles = ParseClipSendArgs(e.Args);

            // 2. 初始化剪贴板监听和数据中心（常驻后台）
            _clipboardService = new ClipboardService();
            _clipboardManager = new ClipboardManager(_settingsManager);

            _clipboardService.TextChanged += OnClipboardTextChanged;
            _clipboardService.ImageChanged += OnClipboardImageChanged;
            _clipboardService.FileChanged += OnClipboardFileChanged;
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
            _trayHelper.TodoHistoryRequested = ShowTodoHistory;

            // 4.5 初始化剪贴板同步引擎（SyncClipboard 协议，默认关闭）。
            //     放在托盘之后：推送 / 拉取成功的气泡提示需要托盘图标。
            if (_settingsManager.Settings.SyncEnabled)
            {
                StartSyncEngine();
            }

            // 4.6 无实例运行时右键直达的文件：托盘与引擎已就绪，立即推送
            if (selfClipFiles.Length > 0)
            {
                ProcessClipSendFiles(selfClipFiles);
            }

            // 4.7 自动更新：启动后台每日静默检查（手动检查走设置页按钮，共用 RunUpdateFlowAsync）。
            //     放在托盘之后：气泡提示需要托盘图标。
            if (_settingsManager.Settings.UpdateAutoCheck)
            {
                StartUpdateLoop();
            }

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
                    // 右键「同步到手机」：通道文件里有待推文件 → 只推送，不弹主窗口
                    var pending = TakeClipSendFiles();
                    if (pending.Length > 0)
                    {
                        ProcessClipSendFiles(pending);
                        return;
                    }

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

        // ── 右键「同步到手机」参数通道 ──────────────────

        /// <summary>解析 /clipsend 命令行参数（shell 对每个文件起一次进程，每次带一个路径）</summary>
        private static string[] ParseClipSendArgs(string[] args)
        {
            var files = new System.Collections.Generic.List<string>();
            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], "/clipsend", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 < args.Length) files.Add(args[++i]);
                }
            }
            return files.Where(f => !string.IsNullOrWhiteSpace(f) && File.Exists(f)).ToArray();
        }

        /// <summary>取出并清空通道文件里的待推文件路径（多选时多个临时实例追加，主实例一次性合并）</summary>
        private static string[] TakeClipSendFiles()
        {
            try
            {
                var path = ShellMenuHelper.ClipSendFilePath;
                if (!File.Exists(path)) return Array.Empty<string>();

                var lines = File.ReadAllLines(path);
                File.Delete(path);
                return lines
                    .Select(l => l.Trim())
                    .Where(l => l.Length > 0 && File.Exists(l))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        /// <summary>处理右键推送：第一个文件显式推送，其余仅入历史（一期单文件推送）</summary>
        private void ProcessClipSendFiles(string[] files)
        {
            if (files.Length == 0 || _clipboardManager == null || _settingsManager == null) return;

            try
            {
                if (files.Length == 1)
                {
                    ObtainPushEngine().PushFileNow(files[0]);
                }
                else
                {
                    // 多选打包为官方 Group 类型（zip），多文件一步到位
                    ObtainPushEngine().PushGroupNow(files);
                    _trayHelper?.ShowSyncTip($"已推送文件组：{files.Length} 项");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"WinKit 右键推送失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 取推送引擎：同步开关开着复用现有引擎；关闭时用推送专用实例（不 Start 轮询循环）。
        /// </summary>
        private SyncEngine ObtainPushEngine()
        {
            if (_syncEngine != null) return _syncEngine;

            if (_pushOnlyEngine == null)
            {
                _pushOnlyEngine = new SyncEngine(_clipboardManager!, _settingsManager!);
                _pushOnlyEngine.SyncSucceeded += OnSyncSucceeded;
                _pushOnlyEngine.SyncSkipped += OnSyncSucceeded;
            }
            return _pushOnlyEngine;
        }

        private void OnClipboardTextChanged(object? sender, string text)
        {
            _clipboardManager?.AddTextItem(text);
        }

        private void OnClipboardImageChanged(object? sender, System.Windows.Media.Imaging.BitmapSource image)
        {
            _clipboardManager?.AddImageItem(image);
        }

        private void OnClipboardFileChanged(object? sender, string[] files)
        {
            // 多文件（Group）一期不做同步，历史里先记录第一个文件
            if (files.Length > 0)
                _clipboardManager?.AddFileItem(files[0]);
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

        /// <summary>打开「待办历史」（回收站）窗口：惰性创建、复用单实例</summary>
        private void ShowTodoHistory()
        {
            if (_todoWindow == null) return;

            if (_historyWindow == null || !_historyWindow.IsLoaded)
            {
                _historyWindow = new Todo.HistoryWindow(_todoWindow);
            }
            _historyWindow.Show();
            _historyWindow.Activate();
        }

        /// <summary>开启/关闭翻译 & OCR 模块及其全局热键（供托盘调用）</summary>
        public void ToggleTranslateFeature(bool enable)
        {
            _translateModule?.SetEnabled(enable);
        }

        /// <summary>
        /// 启动自动更新的后台循环：每小时醒来，距上次检查超过 24 小时才真正探测。
        /// 手动检查（设置页按钮）走 CheckForUpdateNow，不受此循环影响。
        /// </summary>
        public void StartUpdateLoop()
        {
            if (_updateCts != null) return; // 已在跑
            _updateCts = new CancellationTokenSource();
            var ct = _updateCts.Token;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), ct); // 避开启动高峰
                    while (!ct.IsCancellationRequested)
                    {
                        var last = _settingsManager!.Settings.LastUpdateCheckAt;
                        if (!last.HasValue || DateTime.Now - last.Value > TimeSpan.FromHours(24))
                        {
                            await RunUpdateFlowAsync(manual: false);
                        }
                        await Task.Delay(TimeSpan.FromHours(1), ct);
                    }
                }
                catch (OperationCanceledException) { }
                catch { /* 循环异常静默，下轮重试 */ }
            }, ct);
        }

        /// <summary>关闭/开启自动检查开关时调用（设置页）</summary>
        public void ToggleUpdateLoop(bool enable)
        {
            if (enable) StartUpdateLoop();
            else { _updateCts?.Cancel(); _updateCts = null; }
        }

        /// <summary>
        /// 完整更新流程：三源探测 → 版本判断 →（按开关）后台下载 → 气泡反馈。
        /// manual=true 时结果无论有无更新都气泡告知；静默模式只在有更新或下载完成时提示。
        /// </summary>
        public async Task RunUpdateFlowAsync(bool manual)
        {
            _updateService ??= new UpdateService();
            var r = await _updateService.CheckAsync();

            _settingsManager!.Settings.LastUpdateCheckAt = DateTime.Now;
            _settingsManager.SaveSettings(_settingsManager.Settings);

            if (!r.Probed)
            {
                if (manual) _trayHelper?.ShowTip("WinKit 更新", "检查失败：三个源均不可达");
                return;
            }

            if (!r.HasUpdate)
            {
                if (manual) _trayHelper?.ShowTip("WinKit 更新", $"已是最新版本 v{UpdateService.CurrentVersion}");
                return;
            }

            if (!_settingsManager.Settings.UpdateAutoDownload)
            {
                _trayHelper?.ShowTip("WinKit 更新",
                    $"发现新版本 {r.LatestVersion}（{r.SourceName} 源）\n请到 设置 → 通用 → 软件更新 下载");
                return;
            }

            _trayHelper?.ShowTip("WinKit 更新", $"发现新版本 {r.LatestVersion}（{r.SourceName} 源），后台下载中…");
            var file = await _updateService.DownloadAsync(r.LatestVersion!, r.DownloadUrl!);
            if (file != null)
            {
                _trayHelper?.ShowTip("WinKit 更新", $"新版本已下载完成\n{file}\n关闭 WinKit 后解压覆盖即可升级");
                try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{file}\"")); }
                catch { }
            }
            else
            {
                _trayHelper?.ShowTip("WinKit 更新", $"新版本 {r.LatestVersion} 下载失败，可稍后在设置里重试");
            }
        }

        /// <summary>同步引擎状态文本（设置页「同步」分区展示）</summary>
        public string GetSyncStatusText()
        {
            var e = _syncEngine;
            if (e == null) return "未运行（托盘「剪贴板同步」总开关未开启）";
            if (e.LastError != null)
                return $"异常（连续 {e.ConsecutiveFailures} 轮）：{e.LastError} · 退避轮询 {e.EffectiveIntervalMs / 1000.0:F0}s";
            return e.LastSuccessAt.HasValue
                ? $"正常 · 上次成功 {e.LastSuccessAt.Value:HH:mm:ss} · 轮询 {e.EffectiveIntervalMs / 1000}s"
                : "运行中 · 等待首轮完成";
        }

        /// <summary>设置页「立即检查」：唤醒一轮推拉（Nudge），结果反映在状态行与气泡</summary>
        public void SyncCheckNow()
        {
            if (_syncEngine == null)
            {
                _trayHelper?.ShowTip("WinKit 同步", "同步总开关未开启（托盘右键「剪贴板同步」）");
                return;
            }
            _syncEngine.Nudge();
            _trayHelper?.ShowTip("WinKit 同步", "已触发一轮同步，几秒后状态行刷新");
        }

        /// <summary>
        /// 启动剪贴板同步引擎（已启动则忽略），并把同步成功事件接到托盘气泡。
        /// </summary>
        public void StartSyncEngine()
        {
            if (_syncEngine != null) return;
            if (_clipboardManager == null || _settingsManager == null) return;

            _syncEngine = new SyncEngine(_clipboardManager, _settingsManager);
            _syncEngine.SyncSucceeded += OnSyncSucceeded;
            _syncEngine.SyncSkipped += OnSyncSucceeded; // 守门提示同样走气泡
            _syncEngine.Start();
        }

        /// <summary>
        /// 连接参数（URL / 账号 / 密码）变更后重启同步引擎，使 Basic Auth 等配置即时生效。
        /// 未开启同步时为空操作。
        /// </summary>
        public void RestartSyncEngine()
        {
            if (_syncEngine == null) return;
            _syncEngine.Dispose();
            _syncEngine = null;
            StartSyncEngine();
        }

        /// <summary>
        /// 开启/关闭剪贴板同步（供托盘调用，含配置持久化）。
        /// </summary>
        public void ToggleSyncFeature(bool enable)
        {
            if (_settingsManager == null) return;
            var settings = _settingsManager.Settings;
            settings.SyncEnabled = enable;
            _settingsManager.SaveSettings(settings);

            if (enable)
            {
                StartSyncEngine();
            }
            else
            {
                _syncEngine?.Dispose();
                _syncEngine = null;
            }
        }

        /// <summary>同步成功 → 托盘气泡提示（事件来自后台线程，切回 UI 线程）</summary>
        private void OnSyncSucceeded(string message)
        {
            Dispatcher.Invoke(() =>
            {
                _trayHelper?.ShowSyncTip(message);
            });
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
            _updateCts?.Cancel();
            _updateCts?.Dispose();
            _syncEngine?.Dispose();
            _pushOnlyEngine?.Dispose();
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
