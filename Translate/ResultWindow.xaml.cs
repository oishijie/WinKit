using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

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

        public ResultWindow()
        {
            InitializeComponent();
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
                TargetLabel.Text = targetLabel;
                TargetBox.Text = message;
                SourceBox.Text = string.Empty;
                SourceSection.Visibility = Visibility.Collapsed;
                Separator.Visibility = Visibility.Collapsed;
                StatusText.Text = "";
                CopyBtn.IsEnabled = false;
                _currentSource = string.Empty;
                _currentActionText = string.Empty;
                ActionPanel.Visibility = Visibility.Collapsed;
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
                TargetLabel.Text = targetLabel;
                TargetBox.Text = string.IsNullOrEmpty(target) ? (success ? "（无内容）" : "失败") : target;
                _currentTarget = target ?? string.Empty;
                _currentSource = source ?? string.Empty;
                _currentActionText = actionText ?? source ?? string.Empty;

                if (!string.IsNullOrEmpty(source))
                {
                    SourceBox.Text = source;
                    SourceSection.Visibility = Visibility.Visible;
                    Separator.Visibility = Visibility.Visible;
                }
                else
                {
                    SourceSection.Visibility = Visibility.Collapsed;
                    Separator.Visibility = Visibility.Collapsed;
                }

                // 有可操作的识别文本才显示翻译 / 搜索
                ActionPanel.Visibility = string.IsNullOrEmpty(_currentActionText)
                    ? Visibility.Collapsed
                    : Visibility.Visible;

                StatusText.Text = status ?? "";
                CopyBtn.IsEnabled = success && !string.IsNullOrEmpty(target);
            });
        }

        /// <summary>仅更新状态栏文案（如「已打开浏览器搜索」）</summary>
        public void SetStatus(string status)
        {
            Dispatcher.Invoke(() => StatusText.Text = status);
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 1) DragMove();
        }

        private void CloseBtn_Click(object sender, RoutedEventArgs e)
        {
            // 显式隐藏时重置钉住状态，下次弹出恢复默认行为
            _isPinned = false;
            PinBtn.Content = "📌";
            PinBtn.ToolTip = "钉住窗口";
            Hide();
        }

        private void Window_Deactivated(object sender, EventArgs e)
        {
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
    }
}
