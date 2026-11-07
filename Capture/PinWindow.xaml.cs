using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace WinKit.Capture
{
    /// <summary>
    /// 贴图窗口 —— 把截图钉在桌面当参考图。
    ///
    /// 基础版能力：置顶无边框、拖动移动、滚轮等比缩放、双击/Esc/按钮关闭。
    /// 缩放实现：改 PinImage 的 Width/Height（原始像素 × scale），
    /// 窗口 SizeToContent=WidthAndHeight 自动跟随，避免 ScaleTransform 裁切/留白。
    /// </summary>
    public partial class PinWindow : Window
    {
        private readonly double _origWidth;
        private readonly double _origHeight;
        private double _scale = 1.0;

        public PinWindow(BitmapSource image)
        {
            if (image == null) throw new ArgumentNullException(nameof(image));

            InitializeComponent();

            _origWidth = image.PixelWidth;
            _origHeight = image.PixelHeight;

            PinImage.Source = image;
            PinImage.Width = _origWidth;
            PinImage.Height = _origHeight;

            // 初始位置：屏幕左上角附近，留出拖拽空间
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = SystemParameters.WorkArea.Left + 32;
            Top = SystemParameters.WorkArea.Top + 32;
        }

        private void Root_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // 双击关闭；单击拖动
            if (e.ClickCount == 2)
            {
                Close();
                return;
            }
            if (e.ChangedButton == MouseButton.Left)
                DragMove();
        }

        private void Root_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            // 滚轮缩放：上滚放大、下滚缩小，倍率钳制在 0.2~4 之间
            double factor = e.Delta > 0 ? 1.1 : 1.0 / 1.1;
            _scale = Math.Clamp(_scale * factor, 0.2, 4.0);

            PinImage.Width = _origWidth * _scale;
            PinImage.Height = _origHeight * _scale;

            e.Handled = true;
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                Close();
                e.Handled = true;
            }
        }

        private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();
    }
}
