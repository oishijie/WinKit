using System;
using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using WinKit.Translate.Services;

namespace WinKit.Common
{
    /// <summary>
    /// 统一设置中心 — 四个分区：通用 / 剪贴板 / 翻译与 OCR / 关于。
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
        private static readonly string[] HotkeySettingNames = {
            nameof(AppSettings.HotkeyScreenshotTranslate),
            nameof(AppSettings.HotkeySelectionTranslate),
            nameof(AppSettings.HotkeyOcrOnly),
            nameof(AppSettings.HotkeySilentOcr),
            nameof(AppSettings.HotkeyScreenshot),
        };
        private static readonly string[] HotkeyLabels = {
            "截图翻译", "划词翻译", "文字识别", "静默 OCR（识别后直接复制）", "截图到剪贴板",
        };

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
                    SectionTranslate => 3,
                    SectionAbout => 4,
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

        /// <summary>窗口失去焦点时取消录制</summary>
        protected override void OnDeactivated(EventArgs e)
        {
            CancelRecording();
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

                // ④ 翻译与 OCR
                TranslateToggle.IsChecked = s.TranslateEnable;
                SelectByTag(ProviderCombo, s.TranslateProvider, "deepseek");
                OpenAIApiKeyBox.Text = s.OpenAIApiKey ?? "";
                OpenAIBaseUrlBox.Text = s.OpenAIBaseUrl ?? "";
                OpenAIModelBox.Text = s.OpenAIModel ?? "";
                OpenAISkipCertToggle.IsChecked = s.OpenAISkipCertValidation;
                BaiduAppIdBox.Text = s.BaiduAppId ?? "";
                BaiduApiKeyBox.Text = s.BaiduApiKey ?? "";
                SyncOpenAICard(s.TranslateEnable);
                SyncBaiduCard(s.TranslateEnable);
                SelectByTag(TargetLangCombo, s.TranslateTargetLang, "zh-CN");
                SelectByTag(SourceLangCombo, s.TranslateSourceLang, "auto");
                SelectByTag(SearchEngineCombo, (s.OcrSearchEngine ?? "bing").ToLowerInvariant(), "bing");
                SelectModel(OcrModelCatalog.Parse(s.OcrModel));
                AngleClsToggle.IsChecked = s.OcrEnableAngleClassification;
                MkldnnToggle.IsChecked = s.OcrEnableMkldnn;
                ThreadsBox.Text = Clamp(s.OcrCpuThreads, 0, 32).ToString();
                ThreadsHintText.Text = $"0 = 按机器自动选择（本机 {Environment.ProcessorCount} 逻辑核，上限 8 线程）";
                SlimSecondsBox.Text = Clamp(s.OcrIdleSlimSeconds, 0, 300).ToString();
                UpdateSlimHint(Clamp(s.OcrIdleSlimSeconds, 0, 300));
                PreloadToggle.IsChecked = s.OcrPreloadOnStartup;
                TranslateCard.IsEnabled = s.TranslateEnable;
                OcrCard.IsEnabled = s.TranslateEnable;
                BaiduCard.IsEnabled = s.TranslateEnable;

                // ⑤ 关于
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
        
            var s = _settingsManager.Settings;
            var configs = new[]
            {
                s.HotkeyScreenshotTranslate,
                s.HotkeySelectionTranslate,
                s.HotkeyOcrOnly,
                s.HotkeySilentOcr,
                s.HotkeyScreenshot,
            };
        
            HotkeyPanel.Children.Clear();
            for (int i = 0; i < configs.Length; i++)
            {
                if (i > 0)
                {
                    HotkeyPanel.Children.Add(new Border
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
                    Text = HotkeyLabels[i],
                    FontSize = 13,
                    Foreground = new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(0x22, 0x22, 0x22)),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                Grid.SetColumn(label, 0);
        
                var cap = new Border
                {
                    Style = (Style)FindResource("KeyCap"),
                    Cursor = System.Windows.Input.Cursors.Hand,
                    ToolTip = "点击修改快捷键",
                    Child = new TextBlock
                    {
                        Text = HotkeyConfig.Format(configs[i].VK, configs[i].Modifiers),
                        FontSize = 11,
                        FontFamily = new System.Windows.Media.FontFamily("Consolas, Segoe UI"),
                        Foreground = new System.Windows.Media.SolidColorBrush(
                            System.Windows.Media.Color.FromRgb(0x44, 0x44, 0x44)),
                    },
                };
                int idx = i; // 捕获循环变量
                cap.MouseLeftButtonDown += (s, e) => StartHotkeyRecording(idx, cap);
                Grid.SetColumn(cap, 1);
        
                grid.Children.Add(label);
                grid.Children.Add(cap);
                HotkeyPanel.Children.Add(grid);
            }
        }
        
        // ══════════════════════════════════════════════
        //  快捷键录制
        // ══════════════════════════════════════════════
        
        private void StartHotkeyRecording(int index, Border cap)
        {
            // 取消之前的录制
            if (_recordingIndex >= 0 && _recordingCap != null)
                CancelRecording();
        
            _recordingIndex = index;
            _recordingCap = cap;
            _recordingModifiers = 0;
        
            // 更新 UI 为录制状态
            cap.Background = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromArgb(0x30, 0x00, 0x78, 0xD4));
            cap.BorderBrush = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x00, 0x78, 0xD4));
            cap.BorderThickness = new Thickness(1);
            ((TextBlock)cap.Child).Text = "按下新快捷键…";
            ((TextBlock)cap.Child).Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x00, 0x78, 0xD4));
        
            Focus();
        }
        
        private void CancelRecording()
        {
            if (_recordingIndex < 0 || _recordingCap == null) return;
            // 恢复显示
            var s = _settingsManager.Settings;
            var config = GetHotkeyConfigByIndex(s, _recordingIndex);
            ((TextBlock)_recordingCap.Child).Text = HotkeyConfig.Format(config.VK, config.Modifiers);
            ((TextBlock)_recordingCap.Child).Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x44, 0x44, 0x44));
            _recordingCap.Background = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromArgb(0x14, 0, 0, 0));
            _recordingCap.BorderBrush = null;
            _recordingCap.BorderThickness = new Thickness(0);
            _recordingIndex = -1;
            _recordingCap = null;
        }
        
        private static HotkeyConfig GetHotkeyConfigByIndex(AppSettings s, int index)
        {
            return index switch
            {
                0 => s.HotkeyScreenshotTranslate,
                1 => s.HotkeySelectionTranslate,
                2 => s.HotkeyOcrOnly,
                3 => s.HotkeySilentOcr,
                4 => s.HotkeyScreenshot,
                _ => new HotkeyConfig(),
            };
        }
        
        private void SetHotkeyConfigByIndex(AppSettings s, int index, HotkeyConfig config)
        {
            switch (index)
            {
                case 0: s.HotkeyScreenshotTranslate = config; break;
                case 1: s.HotkeySelectionTranslate = config; break;
                case 2: s.HotkeyOcrOnly = config; break;
                case 3: s.HotkeySilentOcr = config; break;
                case 4: s.HotkeyScreenshot = config; break;
            }
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
                        ((TextBlock)_recordingCap.Child).Text = string.Join(" + ", parts) + " + …";
                    }
                    e.Handled = true;
                    return;
                }
        
                // 主键 + 修饰键 → 提交
                if (_recordingModifiers != 0)
                {
                    int vk = System.Windows.Input.KeyInterop.VirtualKeyFromKey(key);
        
                    // 冲突检测：检查是否与其他快捷键重复
                    var s = _settingsManager.Settings;
                    for (int i = 0; i < 4; i++)
                    {
                        if (i == _recordingIndex) continue;
                        var other = GetHotkeyConfigByIndex(s, i);
                        if (other.VK == vk && other.Modifiers == _recordingModifiers)
                        {
                            System.Windows.MessageBox.Show(
                                $"该快捷键已被「{HotkeyLabels[i]}」使用，请选择其他组合。",
                                "快捷键冲突",
                                MessageBoxButton.OK,
                                MessageBoxImage.Warning);
                            CancelRecording();
                            e.Handled = true;
                            return;
                        }
                    }
        
                    // 保存新快捷键
                    var config = new HotkeyConfig(vk, _recordingModifiers);
                    SetHotkeyConfigByIndex(s, _recordingIndex, config);
                    BuildHotkeyList();
                    Save();
                    e.Handled = true;
                    return;
                }
        
                // 没有修饰键，忽略
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
            PageTranslate.Visibility = index == 3 ? Visibility.Visible : Visibility.Collapsed;
            PageAbout.Visibility = index == 4 ? Visibility.Visible : Visibility.Collapsed;

            if (index == 2) UpdateClipCount();
            if (index == 4) RefreshOcrStatus();
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
        //  ③ 翻译与 OCR
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
            BaiduCard.IsEnabled = enable;
            SyncOpenAICard(enable);
            SyncBaiduCard(enable);
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
            else if (tag == "baidu")
            {
                // 引导用户填写 APP ID
                BaiduAppIdBox.Focus();
            }

            Save();
            bool enabled = TranslateToggle.IsChecked == true;
            SyncOpenAICard(enabled);
            SyncBaiduCard(enabled);
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

        private void BaiduAppIdBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_suspend) return;
            _settingsManager.Settings.BaiduAppId = BaiduAppIdBox.Text ?? "";
            Save();
        }

        private void BaiduApiKeyBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_suspend) return;
            _settingsManager.Settings.BaiduApiKey = BaiduApiKeyBox.Text ?? "";
            Save();
        }

        /// <summary>引擎为国内大模型 / 通用 OpenAI 时显示配置卡片；同时受「翻译模块开关」约束</summary>
        private void SyncOpenAICard(bool moduleEnabled)
        {
            bool isLLM = SelectedTag(ProviderCombo) is "openai" or "deepseek";
            OpenAICard.Visibility = (isLLM && moduleEnabled) ? Visibility.Visible : Visibility.Collapsed;
            OpenAICard.IsEnabled = moduleEnabled;
        }

        /// <summary>引擎为百度翻译时显示配置卡片；同时受「翻译模块开关」约束</summary>
        private void SyncBaiduCard(bool moduleEnabled)
        {
            bool isBaidu = SelectedTag(ProviderCombo) == "baidu";
            BaiduCard.Visibility = (isBaidu && moduleEnabled) ? Visibility.Visible : Visibility.Collapsed;
            BaiduCard.IsEnabled = moduleEnabled;
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
        //  ④ 关于
        // ══════════════════════════════════════════════

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
                Process.Start(new ProcessStartInfo("https://github.com/worldoi/WinKit") { UseShellExecute = true });
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
