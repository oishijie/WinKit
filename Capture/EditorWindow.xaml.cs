using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using SWF = System.Windows.Forms;
using WPoint = System.Windows.Point;

namespace WinKit.Capture
{
    /// <summary>
    /// 截图编辑器 — 底图 + 标注层（Canvas）+ 工具栏。
    ///
    /// 标注采用「元素即记录」模型：每次操作往 OverlayCanvas 里加一批 WPF 元素，
    /// 这批元素原样进撤销栈，撤销/重做就是整批加回或移除，无需序列化中间状态。
    ///
    /// 坐标约定：位图按物理像素采集，而 WPF 布局走逻辑单位，两者差一个 DPI 缩放系数。
    /// 图像按 1/scale 的逻辑尺寸布局，于是屏幕上正好 1:1；反向换算即可得到像素坐标。
    /// </summary>
    public partial class EditorWindow : Window
    {
        private enum ToolKind { Rect, Arrow, Pen, Mosaic }

        private readonly BitmapSource _image;
        private readonly System.Drawing.Rectangle _region;
        private readonly bool _copiedToClipboard;

        private double _scaleX = 1.0;
        private double _scaleY = 1.0;

        private ToolKind _tool = ToolKind.Rect;

        // 绘制中的临时元素（同时也是最终元素，见类注释）
        private bool _drawing;
        private WPoint _startPoint;
        private Rectangle? _rectPreview;
        private System.Windows.Shapes.Path? _arrowPreview;
        private Polyline? _penPreview;

        // 撤销 / 重做：一个条目 = 一次操作加入的一批元素
        private readonly Stack<List<UIElement>> _undoStack = new();
        private readonly Stack<List<UIElement>> _redoStack = new();

        /// <summary>标注主色：红色，在绝大多数截图上都有足够对比度</summary>
        private static readonly Brush StrokeBrush = CreateFrozen(Color.FromRgb(0xFF, 0x3B, 0x30));
        private const double StrokeThickness = 2.5;
        /// <summary>马赛克块边长（物理像素）</summary>
        private const int MosaicBlock = 8;

        private static SolidColorBrush CreateFrozen(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        /// <summary>
        /// <paramref name="image"/> 为已截取的画面；<paramref name="region"/> 为它在屏幕上的物理像素位置，
        /// 仅用于把编辑器窗口摆到选区附近。
        /// </summary>
        public EditorWindow(BitmapSource image, System.Drawing.Rectangle region, bool copiedToClipboard)
        {
            _image = image ?? throw new ArgumentNullException(nameof(image));
            _region = region;
            _copiedToClipboard = copiedToClipboard;

            InitializeComponent();
            BaseImage.Source = _image;
            Opacity = 0;   // 尺寸与位置要在 Loaded 里算完，先藏起来免得闪出一个空壳窗口
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            _scaleX = dpi.DpiScaleX <= 0 ? 1.0 : dpi.DpiScaleX;
            _scaleY = dpi.DpiScaleY <= 0 ? 1.0 : dpi.DpiScaleY;

            // 物理像素 → 逻辑单位，保证在屏幕上按原始尺寸 1:1 呈现
            double w = _image.PixelWidth / _scaleX;
            double h = _image.PixelHeight / _scaleY;

            ImageHost.Width = w;
            ImageHost.Height = h;
            OverlayCanvas.Width = w;
            OverlayCanvas.Height = h;

            UpdateLayout();
            PositionNearRegion();

            SetStatus(_copiedToClipboard ? "已复制到剪贴板" : "选区已就绪");
            UpdateUndoRedoState();

            Opacity = 1;
            Activate();
            Focus();
        }

        /// <summary>把窗口摆在选区所在屏幕的工作区内，优先落于选区下方</summary>
        private void PositionNearRegion()
        {
            double winW = ActualWidth > 0 ? ActualWidth : Width;
            double winH = ActualHeight > 0 ? ActualHeight : Height;
            if (double.IsNaN(winW) || double.IsNaN(winH)) return;

            var wa = SWF.Screen.FromRectangle(_region).WorkingArea;
            double waLeft = wa.Left / _scaleX, waTop = wa.Top / _scaleY;
            double waRight = wa.Right / _scaleX, waBottom = wa.Bottom / _scaleY;

            double left = _region.X / _scaleX;
            double top = _region.Y / _scaleY;

            // 先试选区下方，放不下再翻到上方
            if (top + winH + 8 <= waBottom) top += _region.Height / _scaleY + 8;
            else if (top - winH - 8 >= waTop) top -= winH + 8;

            if (left + winW > waRight) left = waRight - winW;
            if (left < waLeft) left = waLeft;
            if (top + winH > waBottom) top = waBottom - winH;
            if (top < waTop) top = waTop;

            Left = left;
            Top = top;
        }

        // ══════════════════════════════════════════════
        //  工具栏
        // ══════════════════════════════════════════════

        private void ToolBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 1) DragMove();
        }

        private void Tool_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is RadioButton rb && rb.Tag is string tag
                && Enum.TryParse<ToolKind>(tag, out var kind))
            {
                _tool = kind;
            }
        }

        private void UndoBtn_Click(object sender, RoutedEventArgs e) => Undo();

        private void RedoBtn_Click(object sender, RoutedEventArgs e) => Redo();

        private void CopyBtn_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                System.Windows.Clipboard.SetImage(ComposeImage());
                SetStatus("已复制到剪贴板");
            }
            catch (Exception ex)
            {
                SetStatus($"复制失败：{ex.Message}");
            }
        }

        private void SaveBtn_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                FileName = $"截图_{DateTime.Now:yyyyMMdd_HHmmss}.png",
                Filter = "PNG 图片|*.png",
                DefaultExt = ".png",
                AddExtension = true,
            };

            if (dlg.ShowDialog(this) != true) return;

            try
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(ComposeImage()));
                using var fs = File.Create(dlg.FileName);
                encoder.Save(fs);
                SetStatus($"已保存：{System.IO.Path.GetFileName(dlg.FileName)}");
            }
            catch (Exception ex)
            {
                SetStatus($"保存失败：{ex.Message}");
            }
        }

        private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;

            if (e.Key == Key.Escape)
            {
                Close();
                e.Handled = true;
            }
            else if (ctrl && e.Key == Key.Z)
            {
                Undo();
                e.Handled = true;
            }
            else if (ctrl && e.Key == Key.Y)
            {
                Redo();
                e.Handled = true;
            }
        }

        // ══════════════════════════════════════════════
        //  标注绘制
        // ══════════════════════════════════════════════

        private void Canvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;

            _drawing = true;
            _startPoint = e.GetPosition(OverlayCanvas);
            OverlayCanvas.CaptureMouse();

            switch (_tool)
            {
                case ToolKind.Rect:
                    _rectPreview = new Rectangle
                    {
                        Stroke = StrokeBrush,
                        StrokeThickness = StrokeThickness,
                        Fill = CreateFrozen(Color.FromArgb(0x22, 0xFF, 0x3B, 0x30)),
                        IsHitTestVisible = false,
                    };
                    OverlayCanvas.Children.Add(_rectPreview);
                    break;

                case ToolKind.Arrow:
                    _arrowPreview = new System.Windows.Shapes.Path
                    {
                        Stroke = StrokeBrush,
                        StrokeThickness = StrokeThickness,
                        StrokeLineJoin = PenLineJoin.Round,
                        StrokeStartLineCap = PenLineCap.Round,
                        StrokeEndLineCap = PenLineCap.Round,
                        IsHitTestVisible = false,
                    };
                    OverlayCanvas.Children.Add(_arrowPreview);
                    break;

                case ToolKind.Pen:
                    _penPreview = new Polyline
                    {
                        Stroke = StrokeBrush,
                        StrokeThickness = StrokeThickness,
                        StrokeLineJoin = PenLineJoin.Round,
                        StrokeStartLineCap = PenLineCap.Round,
                        StrokeEndLineCap = PenLineCap.Round,
                        IsHitTestVisible = false,
                    };
                    _penPreview.Points.Add(_startPoint);
                    OverlayCanvas.Children.Add(_penPreview);
                    break;

                case ToolKind.Mosaic:
                    // 拖动期间先用虚线框示意，松手才落马赛克
                    _rectPreview = new Rectangle
                    {
                        Stroke = StrokeBrush,
                        StrokeThickness = 1.5,
                        StrokeDashArray = new DoubleCollection { 4, 3 },
                        IsHitTestVisible = false,
                    };
                    OverlayCanvas.Children.Add(_rectPreview);
                    break;
            }

            e.Handled = true;
        }

        private void Canvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_drawing) return;

            var p = e.GetPosition(OverlayCanvas);
            var rect = Normalize(_startPoint, p);

            switch (_tool)
            {
                case ToolKind.Rect:
                case ToolKind.Mosaic:
                    if (_rectPreview != null) ApplyRect(_rectPreview, rect);
                    break;

                case ToolKind.Arrow:
                    if (_arrowPreview != null) _arrowPreview.Data = BuildArrowGeometry(_startPoint, p);
                    break;

                case ToolKind.Pen:
                    _penPreview?.Points.Add(p);
                    break;
            }

            e.Handled = true;
        }

        private void Canvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_drawing) return;
            _drawing = false;
            OverlayCanvas.ReleaseMouseCapture();

            var end = e.GetPosition(OverlayCanvas);
            var rect = Normalize(_startPoint, end);

            switch (_tool)
            {
                case ToolKind.Rect:
                    if (_rectPreview != null)
                    {
                        if (rect.Width >= 3 && rect.Height >= 3) Commit(_rectPreview);
                        else OverlayCanvas.Children.Remove(_rectPreview);
                        _rectPreview = null;
                    }
                    break;

                case ToolKind.Arrow:
                    if (_arrowPreview != null)
                    {
                        double len = (end - _startPoint).Length;
                        if (len >= 4) Commit(_arrowPreview);
                        else OverlayCanvas.Children.Remove(_arrowPreview);
                        _arrowPreview = null;
                    }
                    break;

                case ToolKind.Pen:
                    if (_penPreview != null)
                    {
                        if (_penPreview.Points.Count >= 2) Commit(_penPreview);
                        else OverlayCanvas.Children.Remove(_penPreview);
                        _penPreview = null;
                    }
                    break;

                case ToolKind.Mosaic:
                    if (_rectPreview != null)
                    {
                        OverlayCanvas.Children.Remove(_rectPreview);
                        _rectPreview = null;

                        if (rect.Width >= 3 && rect.Height >= 3)
                        {
                            var mosaic = CreateMosaic(rect);
                            if (mosaic != null)
                            {
                                OverlayCanvas.Children.Add(mosaic);
                                Commit(mosaic);
                            }
                        }
                    }
                    break;
            }

            e.Handled = true;
        }

        private static void ApplyRect(Rectangle el, Rect rect)
        {
            Canvas.SetLeft(el, rect.X);
            Canvas.SetTop(el, rect.Y);
            el.Width = rect.Width;
            el.Height = rect.Height;
        }

        private static Rect Normalize(WPoint a, WPoint b) => new(
            Math.Min(a.X, b.X),
            Math.Min(a.Y, b.Y),
            Math.Abs(a.X - b.X),
            Math.Abs(a.Y - b.Y));

        /// <summary>由起点终点算出「主干 + 两撇箭头」的几何</summary>
        private static Geometry BuildArrowGeometry(WPoint start, WPoint end)
        {
            var group = new GeometryGroup();
            group.Children.Add(new LineGeometry(start, end));

            double angle = Math.Atan2(end.Y - start.Y, end.X - start.X);
            const double headLen = 14.0;
            const double spread = Math.PI / 7;   // 约 25.7°

            var wing1 = new WPoint(
                end.X - headLen * Math.Cos(angle - spread),
                end.Y - headLen * Math.Sin(angle - spread));
            var wing2 = new WPoint(
                end.X - headLen * Math.Cos(angle + spread),
                end.Y - headLen * Math.Sin(angle + spread));

            group.Children.Add(new LineGeometry(end, wing1));
            group.Children.Add(new LineGeometry(end, wing2));
            return group;
        }

        /// <summary>
        /// 生成马赛克贴片：从底图裁出该区域 → 缩到极小 → 交给 Image 以最近邻放大。
        /// 只做一次缩小变换，放大阶段的插值由 Image 元素按 NearestNeighbor 控制，出来的块才是硬的。
        /// </summary>
        private UIElement? CreateMosaic(Rect rect)
        {
            int px = (int)Math.Round(rect.X * _scaleX);
            int py = (int)Math.Round(rect.Y * _scaleY);
            int pw = (int)Math.Round(rect.Width * _scaleX);
            int ph = (int)Math.Round(rect.Height * _scaleY);

            px = Math.Max(0, Math.Min(px, _image.PixelWidth - 1));
            py = Math.Max(0, Math.Min(py, _image.PixelHeight - 1));
            pw = Math.Max(1, Math.Min(pw, _image.PixelWidth - px));
            ph = Math.Max(1, Math.Min(ph, _image.PixelHeight - py));

            if (pw <= 0 || ph <= 0) return null;

            try
            {
                var cropped = new CroppedBitmap(_image, new Int32Rect(px, py, pw, ph));

                double sx = Math.Min(1.0, (double)MosaicBlock / pw);
                double sy = Math.Min(1.0, (double)MosaicBlock / ph);

                var small = new TransformedBitmap(cropped, new ScaleTransform(sx, sy));
                small.Freeze();

                var img = new Image
                {
                    Source = small,
                    Width = rect.Width,
                    Height = rect.Height,
                    Stretch = Stretch.Fill,
                    IsHitTestVisible = false,
                };
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.NearestNeighbor);

                Canvas.SetLeft(img, rect.X);
                Canvas.SetTop(img, rect.Y);
                return img;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"马赛克生成失败: {ex.Message}");
                return null;
            }
        }

        // ══════════════════════════════════════════════
        //  撤销 / 重做
        // ══════════════════════════════════════════════

        private void Commit(UIElement element)
        {
            _undoStack.Push(new List<UIElement> { element });
            _redoStack.Clear();
            UpdateUndoRedoState();
        }

        private void Undo()
        {
            if (_undoStack.Count == 0) return;
            var batch = _undoStack.Pop();
            foreach (var el in batch) OverlayCanvas.Children.Remove(el);
            _redoStack.Push(batch);
            UpdateUndoRedoState();
        }

        private void Redo()
        {
            if (_redoStack.Count == 0) return;
            var batch = _redoStack.Pop();
            foreach (var el in batch) OverlayCanvas.Children.Add(el);
            _undoStack.Push(batch);
            UpdateUndoRedoState();
        }

        private void UpdateUndoRedoState()
        {
            UndoBtn.IsEnabled = _undoStack.Count > 0;
            RedoBtn.IsEnabled = _redoStack.Count > 0;
        }

        // ══════════════════════════════════════════════
        //  导出
        // ══════════════════════════════════════════════

        /// <summary>
        /// 把底图与标注合成成一张位图，按物理像素输出（不受窗口显示缩放影响）。
        /// </summary>
        private BitmapSource ComposeImage()
        {
            var dpi = VisualTreeHelper.GetDpi(ImageHost);
            int pw = (int)Math.Round(ImageHost.ActualWidth * dpi.DpiScaleX);
            int ph = (int)Math.Round(ImageHost.ActualHeight * dpi.DpiScaleY);

            if (pw <= 0 || ph <= 0) return _image;

            var rtb = new RenderTargetBitmap(
                pw, ph, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
            rtb.Render(ImageHost);
            rtb.Freeze();
            return rtb;
        }

        private void SetStatus(string text) => StatusText.Text = text;
    }
}
