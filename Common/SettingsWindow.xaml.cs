using System;
using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using WinKit.Translate.Models;
using WinKit.Translate.Services;

namespace WinKit.Common
{
    /// <summary>
    /// 统一设置中心 — 六个分区：通用 / 待办 / 剪贴板 / 截图 / 翻译与 OCR / 关于。
    ///
    /// 交互约定（对标 SnapFind 控制中心，但更轻）：
    ///   · 改一项存一项、即时生效，没有「保存 / 取消」按钮；
    ///   · 所有写入都经 <see cref="SettingsManager.SaveSettings"/>，由 SettingsChanged 事件驱动各模块热更新；
    ///   · 仅「启动时后台预热」需要重启才生效，界面上已注明。
    /// </summary>
    public partial class SettingsWindow : Window
    {
        /// <summary>导航分区标识，供外部直接跳到某一页</summary>
        public const string SectionGeneral = "general";
        public const string SectionTodo = "todo";
        public const string SectionClipboard = "clipboard";
        public const string SectionCapture = "capture";
        public const string SectionTranslate = "translate";
        public const string SectionAbout = "about";

        private static readonly Regex DigitsOnly = new(@"^[0-9]+$", RegexOptions.Compiled);

        private readonly SettingsManager _settingsManager;

        /// <summary>初始化 / 回填控件期间为 true，避免把「填值」误当成「用户修改」写盘</summary>
        private bool _suspend;

        /// <summary>不透明度滑块的写盘节流器：拖动过程中最多每 200ms 落一次盘</summary>
        private readonly DispatcherTimer _opacitySaveTimer;

        // ── 快捷键录制状态 ──────────────────────────────
        private int _recordingIndex = -1;
        private Border? _recordingCap;
        private int _recordingModifiers;

        /// <summary>为 true 时忽略窗口失焦 —— 弹出的模态提示会短暂夺焦，不该因此取消录制</summary>
        private bool _suppressDeactivateCancel;

        /// <summary>
        /// 快捷键录制状态变化：true = 进入录制，false = 结束（提交或取消）。
        ///
        /// App 层据此挂起 / 恢复各模块的全局热键。必须这么做：录制时若用户按下
        /// 「当前已生效的组合」（Alt+S / Alt+A 等），Windows 会把 WM_HOTKEY 直接发给注册者，
        /// 设置窗根本收不到这个键；同时弹出的截图 / 翻译窗还会把本窗顶得失焦，
        /// 触发 OnDeactivated 取消录制。表现就是「快捷键怎么改都改不了」。
        /// </summary>
        public event EventHandler<bool>? HotkeyRecordingChanged;

        public SettingsWindow(SettingsManager settingsManager)
        {
            _settingsManager = settingsManager ?? throw new ArgumentNullException(nameof(settingsManager));

            // 构造期间 XAML 解析会触发 Slider 的强制（coercion）ValueChanged：
            // 此时计时器与部分控件尚未就绪，先挂起写盘并把计时器建好，避免误写配置 / 空引用崩溃。
            _suspend = true;

            _opacitySaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _opacitySaveTimer.Tick += (s, e) =>
            {
                _opacitySaveTimer.Stop();
                Save();
            };

            InitializeComponent();

            BuildModelCombo();
            BuildHotkeyList();
            LoadAll();
        }

        // ══════════════════════════════════════════════
        //  对外入口
        // ══════════════════════════════════════════════

        /// <summary>显示窗口并（可选）跳转到指定分区</summary>
        public void ShowSection(string? section = null)
        {
            LoadAll();

            if (!string.IsNullOrEmpty(section))
            {
                NavList.SelectedIndex = section switch
                {
                    SectionTodo => 1,
                    SectionClipboard => 2,
                    SectionCapture => 3,
                    SectionTranslate => 4,
                    SectionAbout => 5,
                    _ => 0,
                };
            }

            Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
        }

        /// <summary>关闭按钮/Alt+F4 只隐藏，保留窗口实例以便下次秒开</summary>
        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            e.Cancel = true;
            CancelRecording(); // 取消正在录制的快捷键
            Hide();
            base.OnClosing(e);
        }

        /// <summary>
        /// 窗口失去焦点时取消录制（录制中切走视为放弃）。
        /// 例外：弹出的模态提示会短暂夺焦，这时用 _suppressDeactivateCancel 挡掉。
        /// </summary>
        protected override void OnDeactivated(EventArgs e)
        {
            if (!_suppressDeactivateCancel) CancelRecording();
            base.OnDeactivated(e);
        }

        // ══════════════════════════════════════════════
        //  初始化：把 AppSettings 回填到控件
        // ══════════════════════════════════════════════

        private void LoadAll()
        {
            _suspend = true;
            try
            {
                var s = _settingsManager.Settings;

                // ① 通用
                OpacitySlider.Value = Clamp(s.WindowOpacity, 40, 100);
                OpacityValueText.Text = $"{(int)OpacitySlider.Value}%";
                AutoStartToggle.IsChecked = AutoStartHelper.IsAutoStartEnabled();
                DataDirText.Text = AppPaths.AppData;

                // ② 待办
                TodoPinToggle.IsChecked = s.TodoIsPinned;
                TodoPassthroughToggle.IsChecked = s.TodoIsPassThrough;

                // ③ 剪贴板
                ClipMonitorToggle.IsChecked = s.PasteEnableMonitoring;
                ClipMaxItemsBox.Text = Clamp(s.PasteMaxItems, 10, 500).ToString();
                ClipDedupToggle.IsChecked = s.PasteEnableTextDeduplication;
                UpdateClipCount();
                ClipDetailCard.IsEnabled = s.PasteEnableMonitoring;
                ClipHotkeyCard.IsEnabled = s.PasteEnableMonitoring;

                // ④ 截图
                CaptureAutoCopyToggle.IsChecked = s.CaptureAutoCopy;
                CaptureOpenEditorToggle.IsChecked = s.CaptureOpenEditor;

                // ⑤ 翻译与 OCR
                TranslateToggle.IsChecked = s.TranslateEnable;
                SelectByTag(ProviderCombo, s.TranslateProvider, "deepseek");
                OpenAIApiKeyBox.Text = s.OpenAIApiKey ?? "";
                OpenAIBaseUrlBox.Text = s.OpenAIBaseUrl ?? "";
                OpenAIModelBox.Text = s.OpenAIModel ?? "";
                OpenAISkipCertToggle.IsChecked = s.OpenAISkipCertValidation;
                SyncOpenAICard(s.TranslateEnable);
                SelectByTag(TargetLangCombo, s.TranslateTargetLang, "zh-CN");
                SelectByTag(SourceLangCombo, s.TranslateSourceLang, "auto");
                SelectByTag(SearchEngineCombo, (s.OcrSearchEngine ?? "bing").ToLowerInvariant(), "bing");
                OcrHistoryMaxBox.Text = Clamp(s.OcrHistoryMaxItems, 10, 1000).ToString();
                SelectModel(OcrModelCatalog.Parse(s.OcrModel));
                AngleClsToggle.IsChecked = s.OcrEnableAngleClassification;
                InvertDarkToggle.IsChecked = s.OcrAutoInvertDark;
                MkldnnToggle.IsChecked = s.OcrEnableMkldnn;
                ThreadsBox.Text = Clamp(s.OcrCpuThreads, 0, 32).ToString();
                ThreadsHintText.Text = $"0 = 按机器自动选择（本机 {Environment.ProcessorCount} 逻辑核，上限 8 线程）";
                SlimSecondsBox.Text = Clamp(s.OcrIdleSlimSeconds, 0, 300).ToString();
                UpdateSlimHint(Clamp(s.OcrIdleSlimSeconds, 0, 300));
                PreloadToggle.IsChecked = s.OcrPreloadOnStartup;
                TranslateCard.IsEnabled = s.TranslateEnable;
                OcrCard.IsEnabled = s.TranslateEnable;

                // ⑥ 关于
                var version = Assembly.GetExecutingAssembly().GetName().Version;
                VersionText.Text = version != null ? $"v{version.ToString(3)}" : "v1.0.0";
                RefreshOcrStatus();
            }
            finally
            {
                _suspend = false;
            }
        }

        /// <summary>动态构建模型下拉：缺失的模型标注「未安装」并禁选</summary>
        private void BuildModelCombo()
        {
            ModelCombo.Items.Clear();

            foreach (OcrModelKind kind in Enum.GetValues<OcrModelKind>())
            {
                bool available = OcrModelCatalog.TryValidate(kind, out _);
                var item = new ComboBoxItem
                {
                    Content = available
                        ? OcrModelCatalog.DisplayName(kind)
                        : $"{OcrModelCatalog.DisplayName(kind)}（未安装）",
                    Tag = kind,
                    IsEnabled = available,
                };
                ModelCombo.Items.Add(item);
            }

            ModelHintText.Text = "Small 均衡 · Tiny 最快 · v5 中英文/英文为旧版模型";
        }

        /// <summary>构建可编辑的热键清单（点击快捷键帽即可录制新组合）</summary>
        private void BuildHotkeyList()
        {
            // 如果正在录制，先取消
            _recordingIndex = -1;
            _recordingCap = null;
            _recordingModifiers = 0;

            // 分区归属由 HotkeyCatalog 决定：待办页 1 条、翻译与 OCR 页 4 条、截图页 1 条、剪贴板页 1 条
            BuildHotkeyRows(HotkeyCatalog.IndicesOf(HotkeyOwner.Todo), TodoHotkeyPanel);
            BuildHotkeyRows(HotkeyCatalog.IndicesOf(HotkeyOwner.Translate), HotkeyPanel);
            BuildHotkeyRows(HotkeyCatalog.IndicesOf(HotkeyOwner.Capture), CaptureHotkeyPanel);
            BuildHotkeyRows(HotkeyCatalog.IndicesOf(HotkeyOwner.Clipboard), ClipboardHotkeyPanel);
        }

        /// <summary>把指定下标的快捷键渲染成行追加到目标面板（下标沿用 HotkeyCatalog 的全局体系）</summary>
        private void BuildHotkeyRows(int[] indices, Panel panel)
        {
            var s = _settingsManager.Settings;

            panel.Children.Clear();
            for (int n = 0; n < indices.Length; n++)
            {
                int i = indices[n];
                var descriptor = HotkeyCatalog.All[i];
                var config = descriptor.Get(s);

                if (n > 0)
                {
                    panel.Children.Add(new Border
                    {
                        Height = 1,
                        Background = new System.Windows.Media.SolidColorBrush(
                            System.Windows.Media.Color.FromArgb(0x0F, 0, 0, 0)),
                        Margin = new Thickness(0, 8, 0, 8),
                    });
                }

                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var label = new TextBlock
                {
                    Text = descriptor.Label,
                    FontSize = 13,
                    Foreground = new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(0x22, 0x22, 0x22)),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                Grid.SetColumn(label, 0);

                // 键帽外观（文字 / 颜色 / 重复标红 / ToolTip）统一交给 ApplyCapAppearance，
                // 与录制取消后的恢复路径共用同一套判定，避免两处规则跑偏
                var cap = new Border
                {
                    Style = (Style)FindResource("KeyCap"),
                    Cursor = System.Windows.Input.Cursors.Hand,
                    Child = new TextBlock
                    {
                        FontSize = 11,
                        FontFamily = new System.Windows.Media.FontFamily("Consolas, Segoe UI"),
                    },
                };
                ApplyCapAppearance(cap, i, config);

                int idx = i; // 捕获循环变量
                cap.MouseLeftButtonDown += (_, e) =>
                {
                    StartHotkeyRecording(idx, cap);
                    e.Handled = true;
                };
                Grid.SetColumn(cap, 1);

                grid.Children.Add(label);
                grid.Children.Add(cap);
                panel.Children.Add(grid);
            }

            // 操作提示。「清空」是最不直观的一项 —— 不在界面上写明，用户根本不会知道
            // 录制时按 Delete 可以留空，只会以为「必须选一个组合」。
            if (indices.Length > 0)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = "点击键帽修改；录制中按 Delete 可清空（该功能将不再有全局热键）",
                    FontSize = 11,
                    Foreground = new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(0x88, 0x88, 0x88)),
                    TextWrapping = System.Windows.TextWrapping.Wrap,
                    Margin = new Thickness(0, 10, 0, 0),
                });
            }
        }
        
        // ══════════════════════════════════════════════
        //  快捷键录制
        // ══════════════════════════════════════════════
        
        private void StartHotkeyRecording(int index, Border cap)
        {
            // 已经有一条在录制时，只把上一个键帽的显示还原，不通知 App ——
            // 热键本来就处于挂起状态，没必要先恢复再挂起地抖一次。
            if (_recordingIndex >= 0 && _recordingCap != null)
            {
                int prev = _recordingIndex;
                ApplyCapAppearance(_recordingCap, prev, HotkeyCatalog.All[prev].Get(_settingsManager.Settings));
                _recordingIndex = -1;
                _recordingCap = null;
                _recordingModifiers = 0;
            }
            else
            {
                // 只在真正进入录制时挂起全局热键。顺序不能反 ——
                // 否则第一个按下的组合若正好是当前生效的热键，仍会被系统发给注册者。
                HotkeyRecordingChanged?.Invoke(this, true);
            }

            _recordingIndex = index;
            _recordingCap = cap;
            _recordingModifiers = 0;
        
            // 更新 UI 为录制状态
            cap.Background = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromArgb(0x30, 0x00, 0x78, 0xD4));
            cap.BorderBrush = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x00, 0x78, 0xD4));
            cap.BorderThickness = new Thickness(1);
            SetCapText(cap, "按下新快捷键…", 0x00, 0x78, 0xD4);
        
            cap.ToolTip = "按 Esc 取消；按 Delete 清空此快捷键；组合需至少含 Ctrl / Alt / Shift / Win 之一";

            // Focus() 只给窗口设逻辑焦点，键盘输入未必落在本窗；
            // 必须 Activate + Keyboard.Focus 才能确保收到按键。
            Activate();
            System.Windows.Input.Keyboard.Focus(this);
        }
        
        /// <summary>取消录制：恢复键帽外观，并让 App 层把全局热键装回去</summary>
        private void CancelRecording()
        {
            if (_recordingIndex < 0 || _recordingCap == null) return;

            int index = _recordingIndex;
            var cap = _recordingCap;

            EndHotkeyRecording();
            ApplyCapAppearance(cap, index, HotkeyCatalog.All[index].Get(_settingsManager.Settings));
        }
        
        /// <summary>结束录制状态并通知 App 层恢复全局热键（提交与取消共用）</summary>
        private void EndHotkeyRecording()
        {
            bool wasRecording = _recordingIndex >= 0;

            _recordingIndex = -1;
            _recordingCap = null;
            _recordingModifiers = 0;

            // 恢复热键时读的是当前配置 —— 提交路径已先把新组合写回配置，因此装回去的就是新键
            if (wasRecording) HotkeyRecordingChanged?.Invoke(this, false);
        }

        /// <summary>设置窗是否正处在快捷键录制状态（App 层据此把钩子截获的按键转进来）</summary>
        public bool IsRecordingHotkey => _recordingIndex >= 0;

        /// <summary>
        /// 从外部喂入一次按键组合并提交 —— 专供**钩子型热键**的录制使用。
        ///
        /// 为什么需要这条通道：含 Win 键的组合（Win+V 等）会被系统抢先处理，
        /// 根本到不了 WPF 的键盘事件，设置窗永远录不到；而全局钩子在系统之前就看到了这个键。
        /// 录制期间让钩子把 (VK, Modifiers) 原样转进来，用户才能把唤出键改回 Win 组合，
        /// 或给别的功能分配一个 Win 组合。
        /// </summary>
        public void FeedHotkeyInput(int vk, int modifiers)
        {
            if (_recordingIndex < 0 || vk == 0 || modifiers == 0) return;
            CommitHotkey(vk, modifiers);
        }

        /// <summary>
        /// 提交录制出的组合：查重 → 写配置 → 结束录制 → 落盘 → 重建清单。
        /// 键盘事件（OnPreviewKeyDown）与钩子转发（FeedHotkeyInput）两条路共用此实现。
        /// </summary>
        private void CommitHotkey(int vk, int modifiers)
        {
            if (_recordingIndex < 0) return;

            // 冲突检测：与清单里其它热键逐条比对（含截图页与剪贴板页那两条）。
            // 旧版循环写死 i < 4，截图热键被漏检，会出现两个动作抢同一组合。
            var s = _settingsManager.Settings;
            int dup = HotkeyCatalog.FindDuplicate(s, _recordingIndex, vk, modifiers);
            if (dup >= 0)
            {
                // 模态提示会短暂夺焦，用豁免标志挡一下，别让它顺手把录制取消掉
                _suppressDeactivateCancel = true;
                try
                {
                    System.Windows.MessageBox.Show(
                        $"该组合已被「{HotkeyCatalog.All[dup].Label}」占用，请换一个。",
                        "快捷键冲突",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
                finally { _suppressDeactivateCancel = false; }

                // 保持录制状态：用户可以直接改按别的组合，不必重新点一次键帽
                _recordingModifiers = 0;
                if (_recordingCap != null) SetCapText(_recordingCap, "按下新快捷键…", 0x00, 0x78, 0xD4);
                return;
            }

            // 保存新快捷键
            var config = new HotkeyConfig(vk, modifiers);
            int edited = _recordingIndex;
            HotkeyCatalog.All[edited].Set(s, config);

            // 顺序要紧：先结束录制（此时 App 会按刚写入的新配置把热键装回去），
            // 再落盘。Save 触发的 SettingsChanged 里模块会发现自己已按新键注册过，
            // 于是不会多来一次注销 / 注册。
            EndHotkeyRecording();
            Save();

            // 整表重建：改了 A 之后其它行的「重复标红」状态也要跟着刷新
            BuildHotkeyList();
        }

        /// <summary>统一改键帽文字与颜色（避免各处强转 TextBlock）</summary>
        private static void SetCapText(Border cap, string text, byte r, byte g, byte b)
        {
            if (cap.Child is not TextBlock tb) return;
            tb.Text = text;
            tb.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(r, g, b));
        }

        /// <summary>
        /// 按「非录制态」刷新键帽外观。三种状态用不同颜色区分：
        /// 正常（深灰）/ 未设置（浅灰，用户主动清空）/ 与其它热键撞车（红）。
        /// </summary>
        private void ApplyCapAppearance(Border cap, int index, HotkeyConfig config)
        {
            bool unset = config.VK == 0;
            int dup = unset
                ? -1 // 空值不参与查重（FindDuplicate 内部也有同样保护，这里省一次遍历）
                : HotkeyCatalog.FindDuplicate(_settingsManager.Settings, index, config.VK, config.Modifiers);

            byte r, g, b;
            if (dup >= 0) { r = 0xD0; g = 0x30; b = 0x2F; }      // 冲突：红
            else if (unset) { r = 0x99; g = 0x99; b = 0x99; }    // 未设置：浅灰
            else { r = 0x44; g = 0x44; b = 0x44; }               // 正常：深灰

            SetCapText(cap, HotkeyConfig.Format(config.VK, config.Modifiers), r, g, b);

            cap.Background = new System.Windows.Media.SolidColorBrush(dup >= 0
                ? System.Windows.Media.Color.FromArgb(0x1F, 0xD0, 0x30, 0x2F)
                : System.Windows.Media.Color.FromArgb(0x14, 0, 0, 0));
            cap.BorderBrush = null;
            cap.BorderThickness = new Thickness(0);
            cap.ToolTip = dup >= 0
                ? $"与「{HotkeyCatalog.All[dup].Label}」重复，两者只能生效一个，点击修改"
                : unset
                    ? "未设置快捷键，点击可设置；该功能当前没有全局热键"
                    : "点击修改快捷键";
        }
        
        /// <summary>录制模式下的按键处理：捕获修饰键 + 主键组合</summary>
        protected override void OnPreviewKeyDown(System.Windows.Input.KeyEventArgs e)
        {
            // 录制模式下处理快捷键输入
            if (_recordingIndex >= 0)
            {
                // 按住 Alt 时 WPF 把 e.Key 报告为 Key.System，实际键在 e.SystemKey
                var key = e.Key == Key.System ? e.SystemKey : e.Key;
        
                // Esc 取消录制
                if (key == Key.Escape)
                {
                    CancelRecording();
                    e.Handled = true;
                    return;
                }

                // Delete / Backspace 清空这条快捷键。
                // 「未设置」必须是可达的合法状态 —— 有些功能用户并不想占用全局组合
                // （截图只从托盘进、某个翻译链路不常用等），旧版录制流程要求
                // 「修饰键 + 主键」才提交，等于把留空这条路彻底堵死。
                if (key == Key.Delete || key == Key.Back)
                {
                    CommitHotkey(0, 0);
                    e.Handled = true;
                    return;
                }
        
                // 忽略单独的修饰键
                if (IsModifierKey(key))
                {
                    if (key == Key.LeftShift || key == Key.RightShift) _recordingModifiers |= 2;
                    else if (key == Key.LeftAlt || key == Key.RightAlt) _recordingModifiers |= 1;
                    else if (key == Key.LeftCtrl || key == Key.RightCtrl) _recordingModifiers |= 4;
                    else if (key == Key.LWin || key == Key.RWin) _recordingModifiers |= 8;
        
                    // 更新显示
                    if (_recordingCap != null)
                    {
                        var parts = new System.Collections.Generic.List<string>();
                        if ((_recordingModifiers & 8) != 0) parts.Add("Win");
                        if ((_recordingModifiers & 4) != 0) parts.Add("Ctrl");
                        if ((_recordingModifiers & 1) != 0) parts.Add("Alt");
                        if ((_recordingModifiers & 2) != 0) parts.Add("Shift");
                        SetCapText(_recordingCap, string.Join(" + ", parts) + " + …", 0x00, 0x78, 0xD4);
                    }
                    e.Handled = true;
                    return;
                }
        
                // 主键 + 修饰键 → 提交
                if (_recordingModifiers != 0)
                {
                    CommitHotkey(System.Windows.Input.KeyInterop.VirtualKeyFromKey(key), _recordingModifiers);
                    e.Handled = true;
                    return;
                }
        
                // 只按了主键、没搭修饰键：给明确提示。
                // 旧版在这里静默忽略，用户按了单键没反应，会以为「快捷键根本改不了」。
                if (_recordingCap != null)
                {
                    SetCapText(_recordingCap, "需搭配 Ctrl / Alt / Win", 0xD0, 0x30, 0x2F);
                    _recordingModifiers = 0; // 复位，等用户重新按完整组合
                }
                e.Handled = true;
                return;
            }
        
            // 非录制模式：Esc 关闭设置窗
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Hide();
                return;
            }
            base.OnPreviewKeyDown(e);
        }
        
        private static bool IsModifierKey(Key key)
        {
            return key == Key.LeftShift || key == Key.RightShift
                || key == Key.LeftAlt || key == Key.RightAlt
                || key == Key.LeftCtrl || key == Key.RightCtrl
                || key == Key.LWin || key == Key.RWin;
        }

        // ══════════════════════════════════════════════
        //  导航 & 标题栏
        // ══════════════════════════════════════════════

        private void NavList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (PageGeneral == null) return; // 模板尚未加载完成

            int index = NavList.SelectedIndex;
            PageGeneral.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
            PageTodo.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
            PageClipboard.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;
            PageCapture.Visibility = index == 3 ? Visibility.Visible : Visibility.Collapsed;
            PageTranslate.Visibility = index == 4 ? Visibility.Visible : Visibility.Collapsed;
            PageAbout.Visibility = index == 5 ? Visibility.Visible : Visibility.Collapsed;

            if (index == 2) UpdateClipCount();
            if (index == 4) RefreshOcrHistory();   // 识别历史按需重建，避免每次改设置都全量查询
            if (index == 5) RefreshOcrStatus();
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 1) DragMove();
        }

        private void CloseBtn_Click(object sender, RoutedEventArgs e) => Hide();

        // ══════════════════════════════════════════════
        //  ① 通用
        // ══════════════════════════════════════════════

        private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            int value = (int)Math.Round(e.NewValue);
            if (OpacityValueText != null) OpacityValueText.Text = $"{value}%";
            if (_suspend || _opacitySaveTimer == null || _settingsManager == null) return;

            _settingsManager.Settings.WindowOpacity = value;

            // 拖动时高频触发，节流写盘（SettingsChanged 会驱动 TrayHelper 实时刷新背板）
            _opacitySaveTimer.Stop();
            _opacitySaveTimer.Start();
        }

        private void AutoStartToggle_Click(object sender, RoutedEventArgs e)
        {
            if (_suspend) return;
            bool enable = AutoStartToggle.IsChecked == true;
            try
            {
                AutoStartHelper.SetAutoStart(enable);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"设置开机启动失败: {ex.Message}", "WinKit",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            // 以注册表实际状态为准回显，避免权限受限时显示与事实不符
            _suspend = true;
            AutoStartToggle.IsChecked = AutoStartHelper.IsAutoStartEnabled();
            _suspend = false;
        }

        private void OpenDataDir_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                AppPaths.EnsureDirectories();
                Process.Start(new ProcessStartInfo(AppPaths.AppData) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"无法打开目录: {ex.Message}", "WinKit",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // ══════════════════════════════════════════════
        //  ② 待办
        // ══════════════════════════════════════════════

        /// <summary>改一项存一项：置顶开关直接写配置，由待办窗口订阅的 SettingsChanged 实时同步</summary>
        private void TodoPinToggle_Click(object sender, RoutedEventArgs e)
        {
            if (_suspend) return;
            _settingsManager.Settings.TodoIsPinned = TodoPinToggle.IsChecked == true;
            Save();
        }

        /// <summary>改一项存一项：鼠标穿透开关直接写配置，由待办窗口实时同步</summary>
        private void TodoPassthroughToggle_Click(object sender, RoutedEventArgs e)
        {
            if (_suspend) return;
            _settingsManager.Settings.TodoIsPassThrough = TodoPassthroughToggle.IsChecked == true;
            Save();
        }

        // ══════════════════════════════════════════════
        //  ③ 剪贴板
        // ══════════════════════════════════════════════

        private void ClipMonitorToggle_Click(object sender, RoutedEventArgs e)
        {
            if (_suspend) return;
            bool enable = ClipMonitorToggle.IsChecked == true;
            _settingsManager.Settings.PasteEnableMonitoring = enable;
            Save();
            CurrentApp?.ToggleClipboardFeature(enable);
            ClipDetailCard.IsEnabled = enable;
            ClipHotkeyCard.IsEnabled = enable;
        }

        private void ClipDedupToggle_Click(object sender, RoutedEventArgs e)
        {
            if (_suspend) return;
            _settingsManager.Settings.PasteEnableTextDeduplication = ClipDedupToggle.IsChecked == true;
            Save();
        }

        private void ClipMaxItemsBox_Commit(object sender, RoutedEventArgs e)
        {
            if (_suspend) return;
            int value = CommitNumber(ClipMaxItemsBox, _settingsManager.Settings.PasteMaxItems, 10, 500);
            if (value == _settingsManager.Settings.PasteMaxItems) return;

            _settingsManager.Settings.PasteMaxItems = value;
            Save();

            // 立刻按新上限裁剪，而不是等下一次插入触发
            CurrentApp?.ClipboardManager?.CleanupOldData();
            UpdateClipCount();
        }

        private void ClipClear_Click(object sender, RoutedEventArgs e)
        {
            var answer = System.Windows.MessageBox.Show(
                "确定清空全部剪贴板历史吗？此操作不可撤销。", "WinKit",
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return;

            CurrentApp?.ClipboardManager?.ClearAll();
            UpdateClipCount();
        }

        private void UpdateClipCount()
        {
            int count = CurrentApp?.ClipboardManager?.Items.Count ?? 0;
            ClipCountText.Text = $"当前已保存 {count} 条记录";
        }

        // ══════════════════════════════════════════════
        //  ④ 截图
        // ══════════════════════════════════════════════

        private void CaptureAutoCopyToggle_Click(object sender, RoutedEventArgs e)
        {
            if (_suspend) return;
            _settingsManager.Settings.CaptureAutoCopy = CaptureAutoCopyToggle.IsChecked == true;
            Save();
        }

        private void CaptureOpenEditorToggle_Click(object sender, RoutedEventArgs e)
        {
            if (_suspend) return;
            _settingsManager.Settings.CaptureOpenEditor = CaptureOpenEditorToggle.IsChecked == true;
            Save();
        }

        // ══════════════════════════════════════════════
        //  ⑤ 翻译与 OCR
        // ══════════════════════════════════════════════

        private void TranslateToggle_Click(object sender, RoutedEventArgs e)
        {
            if (_suspend) return;
            bool enable = TranslateToggle.IsChecked == true;
            _settingsManager.Settings.TranslateEnable = enable;
            Save();
            CurrentApp?.ToggleTranslateFeature(enable);
            TranslateCard.IsEnabled = enable;
            OcrCard.IsEnabled = enable;
            OpenAICard.IsEnabled = enable;
            SyncOpenAICard(enable);
        }

        private void ProviderCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suspend) return;
            var tag = SelectedTag(ProviderCombo);
            if (tag == null) return;
            var s = _settingsManager.Settings;
            s.TranslateProvider = tag;

            // 切换到国内大模型 / 通用 OpenAI 时，自动填入对应预设端点与模型，
            // 避免从 Google 切过来后仍停在 api.openai.com（国内不可达）。
            // 仅在用户未手动填过（空）或仍是另一种预设值时覆盖，不破坏自定义地址。
            if (tag == "deepseek")
            {
                if (string.IsNullOrWhiteSpace(s.OpenAIBaseUrl) || s.OpenAIBaseUrl == "https://api.openai.com/v1")
                    s.OpenAIBaseUrl = "https://api.deepseek.com/v1";
                if (string.IsNullOrWhiteSpace(s.OpenAIModel) || s.OpenAIModel == "gpt-4o-mini")
                    s.OpenAIModel = "deepseek-chat";
                OpenAIBaseUrlBox.Text = s.OpenAIBaseUrl;
                OpenAIModelBox.Text = s.OpenAIModel;
            }
            else if (tag == "openai")
            {
                if (string.IsNullOrWhiteSpace(s.OpenAIBaseUrl) || s.OpenAIBaseUrl == "https://api.deepseek.com/v1")
                    s.OpenAIBaseUrl = "https://api.openai.com/v1";
                if (string.IsNullOrWhiteSpace(s.OpenAIModel) || s.OpenAIModel == "deepseek-chat")
                    s.OpenAIModel = "gpt-4o-mini";
                OpenAIBaseUrlBox.Text = s.OpenAIBaseUrl;
                OpenAIModelBox.Text = s.OpenAIModel;
            }
            Save();
            bool enabled = TranslateToggle.IsChecked == true;
            SyncOpenAICard(enabled);
        }

        private void OpenAIApiKeyBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_suspend) return;
            _settingsManager.Settings.OpenAIApiKey = OpenAIApiKeyBox.Text ?? "";
            Save();
        }

        private void OpenAIBaseUrlBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_suspend) return;
            _settingsManager.Settings.OpenAIBaseUrl = OpenAIBaseUrlBox.Text ?? "";
            Save();
        }

        private void OpenAIModelBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_suspend) return;
            _settingsManager.Settings.OpenAIModel = OpenAIModelBox.Text ?? "";
            Save();
        }

        private void OpenAISkipCertToggle_Click(object sender, RoutedEventArgs e)
        {
            if (_suspend) return;
            _settingsManager.Settings.OpenAISkipCertValidation = OpenAISkipCertToggle.IsChecked == true;
            Save();
        }

        /// <summary>引擎为国内大模型 / 通用 OpenAI 时显示配置卡片；同时受「翻译模块开关」约束</summary>
        private void SyncOpenAICard(bool moduleEnabled)
        {
            bool isLLM = SelectedTag(ProviderCombo) is "openai" or "deepseek";
            OpenAICard.Visibility = (isLLM && moduleEnabled) ? Visibility.Visible : Visibility.Collapsed;
            OpenAICard.IsEnabled = moduleEnabled;
        }

        private void TargetLangCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suspend) return;
            var tag = SelectedTag(TargetLangCombo);
            if (tag == null) return;
            _settingsManager.Settings.TranslateTargetLang = tag;
            Save();
        }

        private void SourceLangCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suspend) return;
            var tag = SelectedTag(SourceLangCombo);
            if (tag == null) return;
            _settingsManager.Settings.TranslateSourceLang = tag;
            Save();
        }

        private void SearchEngineCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suspend) return;
            var tag = SelectedTag(SearchEngineCombo);
            if (tag == null) return;
            _settingsManager.Settings.OcrSearchEngine = tag;
            Save();
        }

        private void ModelCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suspend) return;
            if (ModelCombo.SelectedItem is not ComboBoxItem { Tag: OcrModelKind kind }) return;

            _settingsManager.Settings.OcrModel = OcrModelCatalog.ToConfigValue(kind);
            Save();   // TranslateModule 会据此重建引擎（仅在引擎参数真变化时）
        }

        private void AngleClsToggle_Click(object sender, RoutedEventArgs e)
        {
            if (_suspend) return;
            _settingsManager.Settings.OcrEnableAngleClassification = AngleClsToggle.IsChecked == true;
            Save();
        }

        private void InvertDarkToggle_Click(object sender, RoutedEventArgs e)
        {
            if (_suspend) return;
            // 属预处理参数，不触发引擎重建（OcrEngineOptions.SameEngineConfig 已排除）
            _settingsManager.Settings.OcrAutoInvertDark = InvertDarkToggle.IsChecked == true;
            Save();
        }

        private void MkldnnToggle_Click(object sender, RoutedEventArgs e)
        {
            if (_suspend) return;
            _settingsManager.Settings.OcrEnableMkldnn = MkldnnToggle.IsChecked == true;
            Save();
        }

        private void PreloadToggle_Click(object sender, RoutedEventArgs e)
        {
            if (_suspend) return;
            _settingsManager.Settings.OcrPreloadOnStartup = PreloadToggle.IsChecked == true;
            Save();
        }

        private void ThreadsBox_Commit(object sender, RoutedEventArgs e)
        {
            if (_suspend) return;
            int value = CommitNumber(ThreadsBox, _settingsManager.Settings.OcrCpuThreads, 0, 32);
            if (value == _settingsManager.Settings.OcrCpuThreads) return;
            _settingsManager.Settings.OcrCpuThreads = value;
            Save();
        }

        private void SlimSecondsBox_Commit(object sender, RoutedEventArgs e)
        {
            if (_suspend) return;
            int value = CommitNumber(SlimSecondsBox, _settingsManager.Settings.OcrIdleSlimSeconds, 0, 300);
            UpdateSlimHint(value);
            if (value == _settingsManager.Settings.OcrIdleSlimSeconds) return;
            _settingsManager.Settings.OcrIdleSlimSeconds = value;
            Save();
        }

        private void UpdateSlimHint(int seconds)
        {
            SlimHintText.Text = seconds <= 0
                ? "0 = 关闭瘦身，引擎常驻（首帧最快，但长期占用两三百 MB）"
                : $"闲置 {seconds} 秒后卸载推理实例并压缩工作集，内存回落到 10MB 上下";
        }

        // ══════════════════════════════════════════════
        //  ⑥ 关于
        // ══════════════════════════════════════════════

        // ── 识别历史 ────────────────────────────────────

        /// <summary>设置窗最多铺多少条历史。超出部分仍在库里，只是不把界面撑爆</summary>
        private const int HistoryPreviewLimit = 20;

        /// <summary>重建识别历史列表</summary>
        private void RefreshOcrHistory()
        {
            if (OcrHistoryPanel == null) return;

            OcrHistoryPanel.Children.Clear();

            var store = CurrentApp?.TranslateModule?.History;
            if (store == null || !store.IsAvailable)
            {
                OcrHistoryCountText.Text = "识别历史不可用";
                OcrHistoryClearBtn.IsEnabled = false;
                return;
            }

            int total = store.Count();
            OcrHistoryClearBtn.IsEnabled = total > 0;
            OcrHistoryCountText.Text = total == 0
                ? "暂无记录 · 识别成功后自动记录"
                : (total > HistoryPreviewLimit
                    ? $"共 {total} 条，显示最近 {HistoryPreviewLimit} 条"
                    : $"共 {total} 条");

            if (total == 0) return;

            var items = store.Query(HistoryPreviewLimit);
            for (int i = 0; i < items.Count; i++)
            {
                if (i > 0)
                {
                    OcrHistoryPanel.Children.Add(new Border
                    {
                        Height = 1,
                        Background = new System.Windows.Media.SolidColorBrush(
                            System.Windows.Media.Color.FromArgb(0x0F, 0, 0, 0)),
                        Margin = new Thickness(0, 6, 0, 6),
                    });
                }

                OcrHistoryPanel.Children.Add(BuildHistoryRow(items[i]));
            }
        }

        /// <summary>构建一行历史：时间 + 摘要/元信息 + 复制 / 删除</summary>
        private UIElement BuildHistoryRow(OcrHistoryItem item)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var time = new TextBlock
            {
                Text = item.TimeText,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0),
                Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x88, 0x88, 0x88)),
            };
            Grid.SetColumn(time, 0);

            var body = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
            body.Children.Add(new TextBlock
            {
                Text = item.Preview,
                FontSize = 13,
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = item.Text,
                Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x22, 0x22, 0x22)),
            });
            body.Children.Add(new TextBlock
            {
                Text = item.MetaText,
                FontSize = 11,
                Margin = new Thickness(0, 2, 0, 0),
                Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x99, 0x99, 0x99)),
            });
            Grid.SetColumn(body, 1);

            var actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
            };

            var copyBtn = new Button
            {
                Content = "复制",
                Style = (Style)FindResource("MinorBtn"),
                Margin = new Thickness(0, 0, 6, 0),
            };
            string textToCopy = item.Text;
            copyBtn.Click += (s, e) =>
            {
                try
                {
                    System.Windows.Clipboard.SetText(textToCopy);
                    // 就地给个短反馈：按钮文字闪一下「已复制」，无需额外的状态栏
                    copyBtn.Content = "已复制";
                    var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.2) };
                    timer.Tick += (s2, e2) =>
                    {
                        timer.Stop();
                        copyBtn.Content = "复制";
                    };
                    timer.Start();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"复制识别历史失败: {ex.Message}");
                    copyBtn.Content = "失败";
                }
            };

            var delBtn = new Button
            {
                Content = "删除",
                Style = (Style)FindResource("MinorBtn"),
            };
            string id = item.Id;
            delBtn.Click += (s, e) =>
            {
                CurrentApp?.TranslateModule?.History.Remove(id);
                RefreshOcrHistory();
            };

            actions.Children.Add(copyBtn);
            actions.Children.Add(delBtn);
            Grid.SetColumn(actions, 2);

            grid.Children.Add(time);
            grid.Children.Add(body);
            grid.Children.Add(actions);

            return grid;
        }

        private void OcrHistoryMaxBox_Commit(object sender, RoutedEventArgs e)
        {
            if (_suspend) return;
            int value = CommitNumber(OcrHistoryMaxBox, _settingsManager.Settings.OcrHistoryMaxItems, 10, 1000);
            if (value == _settingsManager.Settings.OcrHistoryMaxItems) return;

            _settingsManager.Settings.OcrHistoryMaxItems = value;
            Save();

            // 调小上限后立即裁剪，而不是等下一次识别触发
            CurrentApp?.TranslateModule?.History.TrimTo(value);
            RefreshOcrHistory();
        }

        private void OcrHistoryClear_Click(object sender, RoutedEventArgs e)
        {
            var store = CurrentApp?.TranslateModule?.History;
            if (store == null) return;

            int count = store.Count();
            if (count == 0) return;

            var answer = System.Windows.MessageBox.Show(
                $"确定清空全部 {count} 条识别历史？此操作不可撤销。",
                "清空识别历史",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes) return;

            store.Clear();
            RefreshOcrHistory();
        }

        private void RefreshOcrStatus_Click(object sender, RoutedEventArgs e) => RefreshOcrStatus();

        private void RefreshOcrStatus()
        {
            var module = CurrentApp?.TranslateModule;
            if (module == null)
            {
                OcrStatusText.Text = "模块未启动";
                FallbackRow.Visibility = Visibility.Collapsed;
                FallbackText.Visibility = Visibility.Collapsed;
                return;
            }

            var error = module.OcrInitializationError;
            if (error != null)
            {
                OcrStatusText.Text = error;
            }
            else
            {
                OcrStatusText.Text = module.OcrIsReady
                    ? $"{module.OcrEngineName} · 已就绪"
                    : $"{module.OcrEngineName} · 未加载（首次识别或预热时载入）";
            }

            var notice = module.OcrFallbackNotice;
            bool hasNotice = !string.IsNullOrEmpty(notice);
            FallbackRow.Visibility = hasNotice ? Visibility.Visible : Visibility.Collapsed;
            FallbackText.Visibility = hasNotice ? Visibility.Visible : Visibility.Collapsed;
            FallbackText.Text = notice ?? string.Empty;
        }

        private void LinkText_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo("https://github.com/oishijie/WinKit") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"无法打开链接: {ex.Message}", "WinKit",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void OriginLinkText_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo("https://github.com/li5bo5/WinKit/releases") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"无法打开链接: {ex.Message}", "WinKit",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // ══════════════════════════════════════════════
        //  公共小工具
        // ══════════════════════════════════════════════

        private static App? CurrentApp => System.Windows.Application.Current as App;

        /// <summary>统一写盘入口：触发 SettingsChanged，由各模块自行热更新</summary>
        private void Save()
        {
            if (_suspend) return;
            _settingsManager.SaveSettings(_settingsManager.Settings);
        }

        private static int Clamp(int value, int min, int max) => value < min ? min : (value > max ? max : value);

        /// <summary>读取数字框内容并夹到区间；非法输入回退到原值并回写到界面</summary>
        private int CommitNumber(TextBox box, int fallback, int min, int max)
        {
            int value = int.TryParse(box.Text?.Trim(), out var parsed) ? Clamp(parsed, min, max) : fallback;
            var text = value.ToString();
            if (box.Text != text)
            {
                bool old = _suspend;
                _suspend = true;
                box.Text = text;
                _suspend = old;
            }
            return value;
        }

        private void NumberOnly_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            e.Handled = !DigitsOnly.IsMatch(e.Text);
        }

        /// <summary>数字框按 Enter 立即提交（把焦点移走以触发 LostFocus）</summary>
        private void NumBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            if (sender is TextBox box)
                box.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
        }

        private static void SelectByTag(ComboBox combo, string? tag, string fallbackTag)
        {
            foreach (var obj in combo.Items)
            {
                if (obj is ComboBoxItem item && string.Equals(item.Tag as string, tag, StringComparison.OrdinalIgnoreCase))
                {
                    combo.SelectedItem = item;
                    return;
                }
            }

            foreach (var obj in combo.Items)
            {
                if (obj is ComboBoxItem item && string.Equals(item.Tag as string, fallbackTag, StringComparison.OrdinalIgnoreCase))
                {
                    combo.SelectedItem = item;
                    return;
                }
            }
        }

        private static string? SelectedTag(ComboBox combo) =>
            (combo.SelectedItem as ComboBoxItem)?.Tag as string;

        private void SelectModel(OcrModelKind kind)
        {
            foreach (var obj in ModelCombo.Items)
            {
                if (obj is ComboBoxItem item && item.Tag is OcrModelKind k && k == kind)
                {
                    ModelCombo.SelectedItem = item;
                    return;
                }
            }
            if (ModelCombo.Items.Count > 0) ModelCombo.SelectedIndex = 0;
        }
    }
}
