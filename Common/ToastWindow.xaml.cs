using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace WinKit.Common
{
    /// <summary>
    /// 收到 / 发出同步内容时，屏幕右下角弹出的操作卡片。
    /// 之所以不用 NotifyIcon.ShowBalloonTip：传统气球提示的结构里没有按钮位，
    /// 只有标题、正文、图标三样，做不出「复制 / 存到下载」这类操作。
    /// </summary>
    public partial class ToastWindow : Window
    {
        private readonly DispatcherTimer _timer;
        private readonly int _timeoutMs;
        private int _elapsedMs;
        private bool _paused;

        /// <summary>窗口关闭后触发，由 ToastService 重排剩余卡片</summary>
        public event Action<ToastWindow>? Dismissed;

        /// <summary>用户点了卡片上的按钮</summary>
        public event Action<SyncToastPayload, ToastAction>? ActionInvoked;

        /// <summary>本卡片承载的内容</summary>
        public SyncToastPayload Payload { get; }

        public ToastWindow(SyncToastPayload payload, int timeoutSeconds)
        {
            InitializeComponent();

            Payload = payload;
            _timeoutMs = Math.Max(2, timeoutSeconds) * 1000;

            RenderContent(payload);

            _timer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(200)
            };
            _timer.Tick += OnTick;

            Loaded += (_, _) => _timer.Start();
            Closed += (_, _) =>
            {
                _timer.Stop();
                Dismissed?.Invoke(this);
            };
        }

        // ══════════════════════════════════════════════
        //  内容渲染
        // ══════════════════════════════════════════════

        private void RenderContent(SyncToastPayload p)
        {
            TitleText.Text = p.Title;
            SubText.Text = p.Subtitle;

            switch (p.Kind)
            {
                case SyncToastKind.Text:
                    SetIcon("#185FA5", "#E6F1FB", "M2,3 H14 M2,7.5 H14 M2,12 H9");
                    break;
                case SyncToastKind.Image:
                    SetIcon("#0F6E56", "#E1F5EE", "M2,3.5 H14 V12.5 H2 Z M2.5,10.2 L5.5,7.2 L8.5,10.2 L11,8 L13.5,10.5");
                    break;
                case SyncToastKind.File:
                    SetIcon("#854F0B", "#FAEEDA", "M4,2 H10 L12.5,4.5 V14 H4 Z M10,2 V4.8 H12.5");
                    break;
                case SyncToastKind.Group:
                    SetIcon("#534AB7", "#EEEDFE", "M2,4.5 H9.5 V14 H2 Z M5.5,2 H14 V11.5");
                    break;
            }

            // 图片 / 文件 / 组带缩略图，文本不带
            if (p.ImageBytes is { Length: > 0 })
                TrySetThumbnail(p.ImageBytes);

            // 推送成功只报个信，不需要按钮；拉取才给操作
            if (p.IsPush)
            {
                ActionsRow.Visibility = Visibility.Collapsed;
                return;
            }

            switch (p.Kind)
            {
                case SyncToastKind.Text:
                    AddButton("复制", ToastAction.Copy, primary: true);
                    AddButton("看历史", ToastAction.OpenClipboardPanel, primary: false);
                    break;
                case SyncToastKind.Image:
                    AddButton("复制", ToastAction.Copy, primary: true);
                    AddButton("存到下载", ToastAction.SaveToDownloads, primary: false);
                    break;
                case SyncToastKind.File:
                    AddButton("打开", ToastAction.Open, primary: true);
                    AddButton("所在文件夹", ToastAction.OpenFolder, primary: false);
                    AddButton("存到下载", ToastAction.SaveToDownloads, primary: false);
                    break;
                case SyncToastKind.Group:
                    AddButton("打开文件夹", ToastAction.OpenFolder, primary: true);
                    AddButton("存到下载", ToastAction.SaveToDownloads, primary: false);
                    break;
            }

            // XAML 里 ActionsRow 默认 Collapsed（推送场景不占高度），
            // 这里必须显式展开，否则按钮加进了 ButtonPanel 也看不见。
            if (ButtonPanel.Children.Count > 0)
                ActionsRow.Visibility = Visibility.Visible;
        }

        private void SetIcon(string strokeHex, string backgroundHex, string geometry)
        {
            IconBox.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(backgroundHex));
            IconPath.Data = Geometry.Parse(geometry);
            IconPath.Stroke = new SolidColorBrush((Color)ColorConverter.ConvertFromString(strokeHex));
            IconPath.StrokeThickness = 1.4;
            IconPath.StrokeLineJoin = PenLineJoin.Round;
            IconPath.StrokeStartLineCap = PenLineCap.Round;
            IconPath.StrokeEndLineCap = PenLineCap.Round;
        }

        private void TrySetThumbnail(byte[] bytes)
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                // 缩略图只有 46×34，限制解码宽度，避免为一张小图把整张大图留在内存里
                bmp.DecodePixelWidth = 92;
                bmp.StreamSource = new MemoryStream(bytes);
                bmp.EndInit();
                bmp.Freeze();

                ThumbImage.Source = bmp;
                ThumbBox.Visibility = Visibility.Visible;
            }
            catch
            {
                // 解码失败就不显示缩略图，不影响卡片其余部分
                ThumbBox.Visibility = Visibility.Collapsed;
            }
        }

        private void AddButton(string text, ToastAction action, bool primary)
        {
            var btn = new Button
            {
                Content = text,
                Style = (Style)FindResource(primary ? "WinBtnPrimary" : "WinBtn"),
                Margin = new Thickness(0, 0, 6, 0)
            };
            btn.Click += (_, _) =>
            {
                try { ActionInvoked?.Invoke(Payload, action); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"ToastWindow 动作失败: {ex.Message}"); }
                Close();
            };
            ButtonPanel.Children.Add(btn);
        }

        // ══════════════════════════════════════════════
        //  自动关闭与悬停暂停
        // ══════════════════════════════════════════════

        private void OnTick(object? sender, EventArgs e)
        {
            if (_paused) return;
            _elapsedMs += 200;
            if (_elapsedMs >= _timeoutMs) Close();
        }

        private void Window_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
        {
            // 鼠标进来就停下，让人来得及点按钮
            _paused = true;
        }

        private void Window_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
        {
            _paused = false;
            _elapsedMs = 0; // 移开重新计时，避免刚移开就关掉
        }

        private void CloseGlyph_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            Close();
        }
    }
}
