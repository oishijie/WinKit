using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using WinKit.Common;

namespace WinKit.Translate
{
    /// <summary>
    /// 翻译 / OCR 结果展示窗 — 延续 WinKit 毛玻璃半透明风格，
    /// 失焦自动隐藏，支持复制译文、拖拽移动。
    ///
    /// 识别成功后右下角提供「翻译 / 搜索」操作钮（对标 SnapFind 的交互范式）：
    /// 点击后通过事件把当前识别文本回传给模块层处理，避免窗体直接依赖业务逻辑。
    /// </summary>
    public partial class ResultWindow : Window
    {
        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

        /// <summary>操作按钮回传的文本内容</summary>
        public class TextRequestedEventArgs : EventArgs
        {
            public string Text { get; }
            public TextRequestedEventArgs(string text) => Text = text;
        }

        /// <summary>点击「翻译」时触发，参数为待翻译的识别文本</summary>
        public event EventHandler<TextRequestedEventArgs>? TranslateRequested;

        /// <summary>点击「搜索」时触发，参数为待搜索的识别文本</summary>
        public event EventHandler<TextRequestedEventArgs>? SearchRequested;

        private string _currentTarget = string.Empty;
        private string _currentSource = string.Empty;
        /// <summary>操作按钮实际作用的文本（优先用显式传入的 actionText，否则回退到原文）</summary>
        private string _currentActionText = string.Empty;
        /// <summary>是否钉住窗口（钉住后失焦不自动隐藏）</summary>
        private bool _isPinned = false;

        /// <summary>
        /// 识别进行中。为 true 时失焦不隐藏 —— 用户切到软件内其它页面、
        /// 或干脆切到别的程序（浏览器 / 编辑器 / 聊天窗），结果窗都留在原地，
        /// 进度与最终结果始终可见。链路走向：SetLoading 置 true → SetResult 置 false。
        /// </summary>
        private bool _busy;

        /// <summary>
        /// 识别中的兜底定时器。链路被「静默 OCR」顶掉时（那边不弹窗、不会调 SetResult），
        /// _busy 会一直为 true 导致窗口永不隐藏，故超过该时长强制复位。
        /// </summary>
        private DispatcherTimer? _busyGuard;

        /// <summary>识别完成后的短暂停留：让早已切走的用户仍能瞥见结果，到点再收起。</summary>
        private DispatcherTimer? _autoHide;

        // ── 右下角抓手拖动状态 ───────────────────────────────
        private bool _isResizing;
        private Point _resizeStart;
        private double _resizeStartW;
        private double _resizeStartH;

        /// <summary>识别进行中（供模块层判断此时不应触发引擎瘦身）</summary>
        public bool IsBusy => _busy;

        public ResultWindow()
        {
            InitializeComponent();
            // 用户主动点回窗口 = 正在看结果，此时不该再自动收起
            Activated += (s, e) => CancelAutoHide();
        }

        /// <summary>当前标题，供外部在就地翻译时沿用</summary>
        public string CurrentTitle => TitleText.Text;

        /// <summary>在鼠标附近弹出并激活</summary>
        public void ShowAtMouse()
        {
            GetCursorPos(out var mousePos);

            var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(mousePos.X, mousePos.Y));
            var area = screen.WorkingArea;

            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
            double sx = dpi.DpiScaleX, sy = dpi.DpiScaleY;

            double mouseX = mousePos.X / sx;
            double mouseY = mousePos.Y / sy;
            double workRight = area.Right / sx;
            double workBottom = area.Bottom / sy;
            double workLeft = area.Left / sx;
            double workTop = area.Top / sy;

            double targetLeft = mouseX + 12;
            double targetTop = mouseY + 12;

            if (targetLeft + Width > workRight) targetLeft = mouseX - Width - 12;
            if (targetLeft < workLeft) targetLeft = workLeft;
            if (targetTop + Height > workBottom) targetTop = mouseY - Height - 12;
            if (targetTop < workTop) targetTop = workTop;

            Left = targetLeft;
            Top = targetTop;

            Show();
            WindowState = WindowState.Normal;
            Activate();
        }

        /// <summary>进入加载状态</summary>
        public void SetLoading(string title, string targetLabel, string message)
        {
            Dispatcher.Invoke(() =>
            {
                TitleText.Text = title;
                ApplyTargetLabel(targetLabel);
                TargetBox.Text = message;
                SourceBox.Text = string.Empty;
                SourceSection.Visibility = Visibility.Collapsed;
                StatusText.Text = "";
                CopyBtn.IsEnabled = false;
                _currentSource = string.Empty;
                _currentActionText = string.Empty;
                ActionPanel.Visibility = Visibility.Collapsed;

                // 从这一刻到 SetResult 之间，失焦不再隐藏窗口
                BeginBusy();
            });
        }

        /// <summary>展示最终结果</summary>
        /// <param name="actionText">操作按钮（翻译/搜索）作用的文本；为空时回退到 source</param>
        public void SetResult(string title, string targetLabel, string target,
                              string? source = null, string? actionText = null,
                              string? status = null, bool success = true)
        {
            Dispatcher.Invoke(() =>
            {
                TitleText.Text = title;
                ApplyTargetLabel(targetLabel);
                TargetBox.Text = string.IsNullOrEmpty(target) ? (success ? "（无内容）" : "失败") : target;
                _currentTarget = target ?? string.Empty;
                _currentSource = source ?? string.Empty;
                _currentActionText = actionText ?? source ?? string.Empty;

                if (!string.IsNullOrEmpty(source))
                {
                    SourceBox.Text = source;
                    SourceSection.Visibility = Visibility.Visible;
                }
                else
                {
                    SourceSection.Visibility = Visibility.Collapsed;
                }

                // 有可操作的识别文本才显示翻译 / 搜索
                ActionPanel.Visibility = string.IsNullOrEmpty(_currentActionText)
                    ? Visibility.Collapsed
                    : Visibility.Visible;

                StatusText.Text = status ?? "";
                CopyBtn.IsEnabled = success && !string.IsNullOrEmpty(target);

                EndBusy();
            });
        }

        /// <summary>
        /// 设置结果区小标签。传空串表示不显示 —— 纯识别场景窗口标题已是
        /// 「文字识别」，卡片里再标一次「识别结果」属于重复信息。
        /// 翻译场景（截图翻译 / 划词翻译）仍保留「原文 / 译文」以区分两段内容。
        /// </summary>
        private void ApplyTargetLabel(string? targetLabel)
        {
            var hasLabel = !string.IsNullOrEmpty(targetLabel);
            TargetLabel.Text = targetLabel ?? string.Empty;
            TargetLabel.Visibility = hasLabel ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>进入识别中状态：失焦不隐藏，并挂上兜底复位</summary>
        private void BeginBusy()
        {
            _busy = true;
            CancelAutoHide();
            CancelBusyGuard();

            _busyGuard = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            _busyGuard.Tick += (s, e) =>
            {
                CancelBusyGuard();
                _busy = false;
            };
            _busyGuard.Start();
        }

        /// <summary>
        /// 退出识别中状态。若此时窗口已经失去焦点（用户在识别期间切到了别的程序），
        /// 说明用户没在看这个窗口 —— 结果仍停留一小段时间供其瞥见，到点自动收起，
        /// 避免置顶窗口一直占着别人的界面。
        /// </summary>
        private void EndBusy()
        {
            _busy = false;
            CancelBusyGuard();

            if (!IsActive && IsVisible && !_isPinned)
                StartAutoHide(TimeSpan.FromSeconds(3.5));
        }

        private void CancelBusyGuard()
        {
            if (_busyGuard == null) return;
            _busyGuard.Stop();
            _busyGuard = null;
        }

        private void StartAutoHide(TimeSpan delay)
        {
            CancelAutoHide();
            _autoHide = new DispatcherTimer { Interval = delay };
            _autoHide.Tick += (s, e) =>
            {
                CancelAutoHide();
                if (!_isPinned && !_busy && !IsActive) Hide();
            };
            _autoHide.Start();
        }

        private void CancelAutoHide()
        {
            if (_autoHide == null) return;
            _autoHide.Stop();
            _autoHide = null;
        }

        /// <summary>仅更新状态栏文案（如「已打开浏览器搜索」）</summary>
        public void SetStatus(string status)
        {
            Dispatcher.Invoke(() => StatusText.Text = status);
        }

        /// <summary>
        /// 应用「窗口不透明度」到背板。
        ///
        /// 结果窗是用户最常打开、也最常拿来验证透明度设置的窗口，但它的背板
        /// 一直是 XAML 里硬编码的 <c>#CCFFFFFF</c> —— 拖设置页滑块时纹丝不动，
        /// 这正是「透明度没生效」的最直接来源。现由模块层在创建窗口与设置变更时推送。
        /// </summary>
        public void SetBackdropOpacity(int percent)
        {
            Dispatcher.Invoke(() => WindowBackdrop.Apply(RootBorder, percent));
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 1) DragMove();
        }

        /// <summary>
        /// 标题栏按钮默认半透明（0.5），与 Todo 窗同一套「不抢注意力」的规则，
        /// 但不像 Todo 那样完全隐藏 —— 结果窗是弹出即用，按钮若不可见，
        /// 「钉住」这个功能就没人会发现。鼠标进入标题栏时提到不透明。
        /// </summary>
        private void TitleBar_MouseEnter(object sender, MouseEventArgs e) => TitleButtons.Opacity = 1;

        private void TitleBar_MouseLeave(object sender, MouseEventArgs e) =>
            TitleButtons.Opacity = _isPinned ? 1 : 0.5;

        private void CloseBtn_Click(object sender, RoutedEventArgs e)
        {
            // 显式隐藏时重置钉住状态，下次弹出恢复默认行为
            _isPinned = false;
            PinBtn.Content = "📌";
            PinBtn.ToolTip = "钉住窗口";
            TitleButtons.Opacity = 0.5;
            CancelAutoHide();
            Hide();
        }

        private void Window_Deactivated(object sender, EventArgs e)
        {
            // 识别进行中一律不隐藏 —— 无论是切到软件内其它页面，还是切到别的程序
            // （浏览器 / 编辑器 / 聊天窗），窗口都留在原地，进度和结果始终可见。
            if (_busy) return;
            if (!_isPinned) Hide();
        }

        private void PinBtn_Click(object sender, RoutedEventArgs e)
        {
            _isPinned = !_isPinned;
            if (_isPinned)
            {
                PinBtn.Content = "📍";
                PinBtn.ToolTip = "取消钉住";
            }
            else
            {
                PinBtn.Content = "📌";
                PinBtn.ToolTip = "钉住窗口";
            }
            // 钉住时按钮保持全亮，让「当前是钉住的」这个状态随时可见
            TitleButtons.Opacity = _isPinned ? 1 : 0.5;
        }

        /// <summary>编辑原文/结果时同步操作文本，保证翻译/搜索基于用户改过的版本</summary>
        private void SourceBox_TextChanged(object sender, TextChangedEventArgs e) =>
            _currentActionText = SourceBox.Text;

        private void TargetBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            // 仅在无独立原文区时，结果框才代表可操作的识别文本
            if (SourceSection.Visibility != Visibility.Visible)
                _currentActionText = TargetBox.Text;
        }

        private void CopyBtn_Click(object sender, RoutedEventArgs e) => CopyToClipboard();

        private void CopyToClipboard()
        {
            var text = TargetBox.Text;
            if (string.IsNullOrEmpty(text)) return;
            try
            {
                System.Windows.Clipboard.SetText(text);
                StatusText.Text = "已复制";
                CopyBtn.IsEnabled = false;
            }
            catch (Exception ex)
            {
                StatusText.Text = $"复制失败: {ex.Message}";
            }
        }

        private void TranslateBtn_Click(object sender, RoutedEventArgs e)
        {
            var text = ResolveActionText();
            if (!string.IsNullOrEmpty(text))
                TranslateRequested?.Invoke(this, new TextRequestedEventArgs(text));
        }

        private void SearchBtn_Click(object sender, RoutedEventArgs e)
        {
            var text = ResolveActionText();
            if (!string.IsNullOrEmpty(text))
                SearchRequested?.Invoke(this, new TextRequestedEventArgs(text));
        }

        /// <summary>翻译/搜索实际作用的文本（优先用户编辑过的原文，否则回退到识别文本）</summary>
        private string ResolveActionText() => _currentActionText ?? string.Empty;

        /// <summary>
        /// 键盘快捷键（在 PreviewKeyDown 隧道阶段处理，早于按钮对 Enter 的默认响应，
        /// 以便用 e.Handled 拦截，避免焦点落在按钮上时 Enter 误触发布局）：
        ///   Enter  = 搜索；Esc = 关闭；Ctrl+C = 复制并关闭。
        /// 中文输入法正在组字（composition）时，Enter/Esc 用于确认/取消候选词，必须放行给输入法。
        /// </summary>
        private void ResultWindow_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.ImeProcessedKey == Key.Enter || e.ImeProcessedKey == Key.Escape)
                return;

            if (e.Key == Key.Escape)
            {
                CancelAutoHide();
                Hide();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Enter)
            {
                var text = ResolveActionText();
                if (!string.IsNullOrEmpty(text))
                    SearchRequested?.Invoke(this, new TextRequestedEventArgs(text));
                e.Handled = true;
                return;
            }

            if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control)
            {
                CopyToClipboard();
                Hide();
                e.Handled = true;
                return;
            }
        }

        // ══════════════════════════════════════════════
        // 右下角抓手：ResizeMode 一直是 CanResize，但此前没有任何
        // 可视抓手，用户根本不知道窗口能拖大。实现与 Todo 窗一致。
        // ══════════════════════════════════════════════
        private void ResizeGrip_MouseEnter(object sender, MouseEventArgs e) => ResizeGripArea.Opacity = 1;

        private void ResizeGrip_MouseLeave(object sender, MouseEventArgs e) => ResizeGripArea.Opacity = 0;

        private void ResizeGrip_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _isResizing = true;
            _resizeStart = e.GetPosition(null);
            _resizeStartW = Width;
            _resizeStartH = Height;
            ((UIElement)sender).CaptureMouse();
            ((UIElement)sender).MouseMove += ResizeGrip_MouseMove;
            ((UIElement)sender).MouseLeftButtonUp += ResizeGrip_MouseLeftButtonUp;
            e.Handled = true;
        }

        private void ResizeGrip_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_isResizing) return;

            var pos = e.GetPosition(null);
            var delta = pos - _resizeStart;

            double newW = Math.Max(MinWidth, _resizeStartW + delta.X);
            double newH = Math.Max(MinHeight, _resizeStartH + delta.Y);

            // 不越出当前屏幕工作区，否则拖过边界后会跑到屏幕外
            var area = GetCurrentScreenWorkArea();
            if (Left + newW > area.Right) newW = area.Right - Left;
            if (Top + newH > area.Bottom) newH = area.Bottom - Top;

            Width = newW;
            Height = newH;
        }

        private void ResizeGrip_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _isResizing = false;
            ((UIElement)sender).ReleaseMouseCapture();
            ((UIElement)sender).MouseMove -= ResizeGrip_MouseMove;
            ((UIElement)sender).MouseLeftButtonUp -= ResizeGrip_MouseLeftButtonUp;
        }

        private Rect GetCurrentScreenWorkArea()
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var screen = System.Windows.Forms.Screen.FromHandle(hwnd);
            var area = screen.WorkingArea;

            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
            double sx = dpi.DpiScaleX, sy = dpi.DpiScaleY;

            return new Rect(area.Left / sx, area.Top / sy, area.Width / sx, area.Height / sy);
        }
    }
}
