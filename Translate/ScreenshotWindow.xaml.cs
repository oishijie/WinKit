using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SDRectangle = System.Drawing.Rectangle;
using WPoint = System.Windows.Point;
using WSize = System.Windows.Size;
// System.Drawing.Imaging 与 System.Windows.Media 都有 PixelFormat；
// 且 System.Drawing 命名空间下的 Imaging 会遮蔽 System.Windows.Interop.Imaging 类，
// 故这两处一律走别名，避免 CS0104 / CS0103。
using SDPixelFormat = System.Drawing.Imaging.PixelFormat;
using InteropImaging = System.Windows.Interop.Imaging;

namespace WinKit.Translate
{
    /// <summary>
    /// 全屏截图选区悬浮窗 — 覆盖整个虚拟屏幕，拖拽框选识别区域，ESC 取消。
    /// 捕获时先将自身 Opacity 置 0 以避免把遮罩截进底图。
    /// </summary>
    public partial class ScreenshotWindow : Window
    {
        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

        private TaskCompletionSource<SDRectangle?>? _tcs;
        private bool _selecting;
        private WPoint _start;
        private readonly RectangleGeometry _hole = new RectangleGeometry();
        private readonly RectangleGeometry _full = new RectangleGeometry();

        /// <summary>
        /// 截底图前的回调 —— 调用方在此隐藏自己的窗口，
        /// 避免被 CopyFromScreen 截进底图导致 OCR 识别到应用自身 UI 文字。
        /// 返回的 Action 在底图捕获完成后调用，用于恢复窗口可见性。
        /// </summary>
        public Func<Action?>? PreCaptureCallback { get; set; }

        private double _dpiScaleX = 1.0;
        private double _dpiScaleY = 1.0;
        private double _virtLeftPhys; // 虚拟屏幕原点物理像素 X
        private double _virtTopPhys;  // 虚拟屏幕原点物理像素 Y

        public ScreenshotWindow()
        {
            InitializeComponent();
            Opacity = 0; // 捕获底图前保持完全透明
            Loaded += OnLoaded;
            Deactivated += (s, e) => Cancel();
            // 兜底：任何关闭路径（含 Alt+F4）都确保完成 Task，避免调用方永久挂起
            Closed += (s, e) => { _tcs?.TrySetResult(null); };
        }

        /// <summary>
        /// 显示选区窗口并等待用户框选，返回物理像素矩形；取消返回 null
        /// </summary>
        public Task<SDRectangle?> SelectRegionAsync()
        {
            _tcs = new TaskCompletionSource<SDRectangle?>();
            Show();
            Activate();
            return _tcs.Task;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            // 覆盖整个虚拟屏幕（多显示器）
            Left = SystemParameters.VirtualScreenLeft;
            Top = SystemParameters.VirtualScreenTop;
            Width = SystemParameters.VirtualScreenWidth;
            Height = SystemParameters.VirtualScreenHeight;

            var m = PresentationSource.FromVisual(this).CompositionTarget.TransformToDevice;
            _dpiScaleX = m.M11;
            _dpiScaleY = m.M22;
            _virtLeftPhys = SystemParameters.VirtualScreenLeft * _dpiScaleX;
            _virtTopPhys = SystemParameters.VirtualScreenTop * _dpiScaleY;

            RootCanvas.Width = Width;
            RootCanvas.Height = Height;

            Canvas.SetLeft(BgImage, 0);
            Canvas.SetTop(BgImage, 0);
            BgImage.Width = Width;
            BgImage.Height = Height;

            Canvas.SetLeft(DimMask, 0);
            Canvas.SetTop(DimMask, 0);
            DimMask.Width = Width;
            DimMask.Height = Height;

            // 镂空遮罩：全屏矩形 - 选区矩形 (EvenOdd)
            _full.Rect = new Rect(0, 0, Width, Height);
            var group = new GeometryGroup { FillRule = FillRule.EvenOdd };
            group.Children.Add(_full);
            group.Children.Add(_hole);
            DimMask.Data = group;

            // 居中顶部提示条
            HintBar.Measure(new WSize(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(HintBar, (Width - HintBar.DesiredSize.Width) / 2);
            Canvas.SetTop(HintBar, 18);

            // 捕获底图（此时 Opacity=0，自身不会被截入）
            // 先执行回调让调用方隐藏其他窗口（如 ResultWindow），避免被截进底图
            var postRestore = PreCaptureCallback?.Invoke();
            BgImage.Source = CaptureFullScreen();
            postRestore?.Invoke(); // 底图已捕获，立即恢复窗口

            // 显现遮罩
            Opacity = 1;

            RootCanvas.MouseLeftButtonDown += OnMouseLeftButtonDown;
            RootCanvas.MouseMove += OnMouseMove;
            RootCanvas.MouseLeftButtonUp += OnMouseLeftButtonUp;
        }

        private BitmapSource CaptureFullScreen()
        {
            int w = (int)Math.Round(SystemParameters.VirtualScreenWidth * _dpiScaleX);
            int h = (int)Math.Round(SystemParameters.VirtualScreenHeight * _dpiScaleY);
            if (w <= 0 || h <= 0) w = h = 1;

            using var bmp = new Bitmap(w, h, SDPixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(bmp);
            g.CopyFromScreen((int)Math.Round(_virtLeftPhys), (int)Math.Round(_virtTopPhys),
                             0, 0, bmp.Size, CopyPixelOperation.SourceCopy);

            IntPtr hBitmap = bmp.GetHbitmap();
            try
            {
                return InteropImaging.CreateBitmapSourceFromHBitmap(
                    hBitmap, IntPtr.Zero, Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
            }
            finally
            {
                DeleteObject(hBitmap);
            }
        }

        private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _selecting = true;
            _start = e.GetPosition(RootCanvas);
            SelectionBorder.Visibility = Visibility.Visible;
            SizeTip.Visibility = Visibility.Visible;
            RootCanvas.CaptureMouse();
            UpdateSelection(_start);
        }

        private void OnMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (!_selecting) return;
            UpdateSelection(e.GetPosition(RootCanvas));
        }

        private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_selecting) return;
            _selecting = false;
            RootCanvas.ReleaseMouseCapture();

            var current = e.GetPosition(RootCanvas);
            var rect = Normalize(_start, current);

            // 过小的选区视为取消
            if (rect.Width < 5 || rect.Height < 5)
            {
                Cancel();
                return;
            }

            var phys = ToPhysical(rect);
            Complete(phys);
        }

        private void UpdateSelection(WPoint current)
        {
            var rect = Normalize(_start, current);
            _hole.Rect = rect;

            Canvas.SetLeft(SelectionBorder, rect.X);
            Canvas.SetTop(SelectionBorder, rect.Y);
            SelectionBorder.Width = rect.Width;
            SelectionBorder.Height = rect.Height;

            SizeText.Text = $"{(int)rect.Width} × {(int)rect.Height}";
            SizeTip.Measure(new WSize(double.PositiveInfinity, double.PositiveInfinity));
            double tipX = rect.X;
            double tipY = rect.Y - SizeTip.DesiredSize.Height - 4;
            if (tipY < 0) tipY = rect.Y + 4;
            Canvas.SetLeft(SizeTip, tipX);
            Canvas.SetTop(SizeTip, tipY);
        }

        private static Rect Normalize(WPoint a, WPoint b)
        {
            double x = Math.Min(a.X, b.X);
            double y = Math.Min(a.Y, b.Y);
            double w = Math.Abs(a.X - b.X);
            double h = Math.Abs(a.Y - b.Y);
            return new Rect(x, y, w, h);
        }

        private SDRectangle ToPhysical(Rect rect)
        {
            int x = (int)Math.Round(_virtLeftPhys + rect.X * _dpiScaleX);
            int y = (int)Math.Round(_virtTopPhys + rect.Y * _dpiScaleY);
            int w = (int)Math.Round(rect.Width * _dpiScaleX);
            int h = (int)Math.Round(rect.Height * _dpiScaleY);
            return new SDRectangle(x, y, w, h);
        }

        private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                Cancel();
                e.Handled = true;
            }
        }

        private void Cancel()
        {
            Complete(null);
        }

        private void Complete(SDRectangle? result)
        {
            if (_tcs == null) return;
            RootCanvas.ReleaseMouseCapture();
            _tcs.TrySetResult(result);
            _tcs = null;
            Close();
        }
    }
}
