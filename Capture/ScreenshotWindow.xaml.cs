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

namespace WinKit.Capture
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
        /// <summary>截底图前隐藏的应用窗口，选区完成后恢复</summary>
        private readonly System.Collections.Generic.List<Window> _hiddenWindows = new();
        /// <summary>Complete 正在执行标志，防止 Show() 恢复 Topmost 窗口时抢焦点触发 Deactivated → Cancel() 重入崩溃</summary>
        private bool _completing;
        /// <summary>窗口是否成功激活过。未激活成功前失焦不取消 —— 否则通知弹窗、输入法浮窗等
        /// 抢一下焦点就会让选区窗口"一闪而过"</summary>
        private bool _activatedOnce;

        private double _dpiScaleX = 1.0;
        private double _dpiScaleY = 1.0;
        private double _virtLeftPhys; // 虚拟屏幕原点物理像素 X
        private double _virtTopPhys;  // 虚拟屏幕原点物理像素 Y
        private int _virtWPhys = 1;   // 虚拟屏幕物理像素宽
        private int _virtHPhys = 1;   // 虚拟屏幕物理像素高

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);

        private const int SM_XVIRTUALSCREEN = 76;
        private const int SM_YVIRTUALSCREEN = 77;
        private const int SM_CXVIRTUALSCREEN = 78;
        private const int SM_CYVIRTUALSCREEN = 79;

        public ScreenshotWindow()
        {
            InitializeComponent();
            Opacity = 0; // 捕获底图前保持完全透明
            Loaded += OnLoaded;
            Activated += (s, e) => _activatedOnce = true;
            Deactivated += (s, e) =>
            {
                // 只在成功激活过之后才允许"失焦即取消"；
                // 否则焦点竞争（通知/输入法浮窗/他窗弹窗）会让选区窗口一闪而过
                if (_activatedOnce && !_completing) Cancel();
            };
            // 兜底：任何关闭路径（含 Alt+F4）都确保完成 Task 并恢复被隐藏的窗口
            Closed += (s, e) =>
            {
                foreach (var w in _hiddenWindows) { try { w.Show(); } catch { } }
                _hiddenWindows.Clear();
                _tcs?.TrySetResult(null);
            };
        }

        /// <summary>
        /// 显示选区窗口并等待用户框选，返回物理像素矩形；取消返回 null
        /// </summary>
        public Task<SDRectangle?> SelectRegionAsync()
        {
            _tcs = new TaskCompletionSource<SDRectangle?>();
            try
            {
                Show();
                Activate();
            }
            catch (Exception ex)
            {
                // 显示失败也必须完成 Task，否则热键入口的 await 永久挂起、
                // CaptureModule._busy 永远为 true，截图热键从此无反应（只能重启）
                System.Diagnostics.Debug.WriteLine($"ScreenshotWindow: 显示选区窗口失败 ({ex.Message})");
                _tcs.TrySetResult(null);
                try { Close(); } catch { }
            }
            return _tcs.Task;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            // 整个加载流程兜底：底图捕获抛异常（锁屏/安全桌面/显卡切换瞬间等）
            // 若不接住，Task 永不完成 → CaptureModule._busy 永久为 true → 截图热键失效到重启
            try
            {
                // 覆盖整个虚拟屏幕（多显示器）
                Left = SystemParameters.VirtualScreenLeft;
                Top = SystemParameters.VirtualScreenTop;
                Width = SystemParameters.VirtualScreenWidth;
                Height = SystemParameters.VirtualScreenHeight;

                var m = PresentationSource.FromVisual(this).CompositionTarget.TransformToDevice;
                _dpiScaleX = m.M11;
                _dpiScaleY = m.M22;

                // 虚拟屏幕原点与尺寸取物理像素真值（GetSystemMetrics 不受进程 DPI 虚拟化影响）。
                // 之前用 VirtualScreenLeft(DIP) × 主屏 DPI 推算，在负坐标副屏 / 混合 DPI 下会算错，
                // 导致 CopyFromScreen 截偏区域。
                _virtLeftPhys = GetSystemMetrics(SM_XVIRTUALSCREEN);
                _virtTopPhys = GetSystemMetrics(SM_YVIRTUALSCREEN);
                _virtWPhys = Math.Max(1, GetSystemMetrics(SM_CXVIRTUALSCREEN));
                _virtHPhys = Math.Max(1, GetSystemMetrics(SM_CYVIRTUALSCREEN));

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
                // 隐藏所有可见的应用窗口，避免 CopyFromScreen 把应用自身 UI 截进底图
                // （ResultWindow/Clipboard/TodoList 等浮动面板，尤其是 Topmost 窗口）
                foreach (Window w in Application.Current.Windows)
                {
                    if (w != null && w != this && w.IsVisible)
                    {
                        _hiddenWindows.Add(w);
                        w.Hide();
                    }
                }

                BgImage.Source = CaptureFullScreen();

                // 注意：不在这里恢复窗口！
                // 如果立即恢复，Topmost 窗口（如 ResultWindow）会盖在 ScreenshotWindow 上面，
                // 导致用户框选时看到应用自身的 UI 文字，被 OCR 误识别。
                // 窗口将在 Complete()（选完/取消/关闭）时统一恢复。

                // 显现遮罩
                Opacity = 1;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ScreenshotWindow: 加载失败 ({ex.Message})");
                Cancel();
                return;
            }

            RootCanvas.MouseLeftButtonDown += OnMouseLeftButtonDown;
            RootCanvas.MouseMove += OnMouseMove;
            RootCanvas.MouseLeftButtonUp += OnMouseLeftButtonUp;

            // 确保拿到键盘焦点：否则 Esc（KeyDown 需焦点）收不到，选区窗口关不掉
            Activate();
            Focus();
        }

        private BitmapSource CaptureFullScreen()
        {
            // 物理像素真值来自 GetSystemMetrics，不再用 DIP × 主屏 DPI 推算
            int w = _virtWPhys;
            int h = _virtHPhys;
            if (w <= 0 || h <= 0) w = h = 1;

            using var bmp = new Bitmap(w, h, SDPixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(bmp);
            g.CopyFromScreen((int)_virtLeftPhys, (int)_virtTopPhys,
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
            // 防止 Complete 内部 Close/Show 触发 Deactivated 事件重入
            if (_completing) return;
            Complete(null);
        }

        private void Complete(SDRectangle? result)
        {
            if (_tcs == null || _completing) return;
            _completing = true;
            RootCanvas.ReleaseMouseCapture();

            // 先完成 Task 并关闭选区窗口，再恢复被隐藏的应用窗口。
            // 顺序很重要：如果先 Show() 恢复 Topmost 窗口（如 TodoList 置顶），
            // 它会抢走焦点，触发 ScreenshotWindow.Deactivated → Cancel() → Complete() 重入崩溃。
            _tcs.TrySetResult(result);
            _tcs = null;
            Close();

            // 选区窗口已关闭，安全恢复所有被隐藏的应用窗口
            foreach (var w in _hiddenWindows)
            {
                try { w.Show(); } catch { }
            }
            _hiddenWindows.Clear();
        }
    }
}
