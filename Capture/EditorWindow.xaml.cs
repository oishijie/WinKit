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
using WinKit.Capture.Annotations;

namespace WinKit.Capture
{
    /// <summary>
    /// 截图编辑器 — 底图 + 标注层（Canvas）+ 工具栏。
    ///
    /// 标注采用「对象模型」：每个标注是一个 IAnnotation 对象，由 AnnotationController
    /// 统一管理「工具工厂 / 选中态 / 命中 / 命令化撤销」。选中后可移动、删除、改色。
    ///
    /// 坐标约定：位图按物理像素采集，而 WPF 布局走逻辑单位，两者差一个 DPI 缩放系数。
    /// 图像按 1/scale 的逻辑尺寸布局，于是屏幕上正好 1:1；反向换算即可得到像素坐标。
    /// </summary>
    public partial class EditorWindow : Window
    {
        private readonly BitmapSource _image;
        private readonly System.Drawing.Rectangle _region;
        private readonly bool _copiedToClipboard;

        private double _scaleX = 1.0;
        private double _scaleY = 1.0;

        private AnnotationController.ToolKind _tool = AnnotationController.ToolKind.Rect;

        // 标注控制器：工厂 + 选中态 + 命令化撤销
        private AnnotationController? _controller;

        // 当前绘制中的标注与临时预览元素
        private bool _drawing;
        private bool _draggingSelection;
        private WPoint _dragStartPoint;
        private IAnnotation? _activeAnnotation;
        private FrameworkElement? _activePreview;

        // 橡皮擦状态
        private bool _erasing;
        private readonly List<WPoint> _eraseTrack = new();

        // 缩放 / 旋转状态
        private bool _resizing;
        private int _resizeHandle;
        private bool _rotating;
        private WPoint _rotateLast;
        private WPoint _rotateCenter;

        /// <summary>标注主色：红色，在绝大多数截图上都有足够对比度</summary>
        private static readonly Color DefaultColor = Color.FromRgb(0xFF, 0x3B, 0x30);
        private const double DefaultThickness = 2.5;

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

            _controller = new AnnotationController(OverlayCanvas, DefaultColor, DefaultThickness);

            // 物理像素 → 逻辑单位，保证在屏幕上按原始尺寸 1:1 呈现
            double w = _image.PixelWidth / _scaleX;
            double h = _image.PixelHeight / _scaleY;

            ImageHost.Width = w;
            ImageHost.Height = h;
            OverlayCanvas.Width = w;
            OverlayCanvas.Height = h;

            // 窗口钳制到所在屏幕工作区：全屏/大区域截图时窗口不再溢出屏幕，
            // 超出部分由 ScrollViewer 滚动查看，工具栏始终可达。
            // （ComposeImage 渲染的是 ImageHost 完整 Visual，不受滚动视口影响，输出分辨率不变。）
            var wa = SWF.Screen.FromRectangle(_region).WorkingArea;
            MaxWidth = Math.Max(320, wa.Width / _scaleX);
            MaxHeight = Math.Max(240, wa.Height / _scaleY);

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
                && Enum.TryParse<AnnotationController.ToolKind>(tag, out var kind))
            {
                _tool = kind;
            }
        }

        private void Color_Checked(object sender, RoutedEventArgs e)
        {
            if (_controller == null) return;
            if (sender is RadioButton rb && rb.Tag is string hex)
            {
                try
                {
                    _controller.Color = (Color)ColorConverter.ConvertFromString(hex);
                }
                catch { /* 无效色值忽略 */ }
            }
        }

        private void Width_Checked(object sender, RoutedEventArgs e)
        {
            if (_controller == null) return;
            if (sender is RadioButton rb && rb.Tag is string w
                && double.TryParse(w, out var thickness))
            {
                _controller.Thickness = thickness;
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

        private void PinBtn_Click(object sender, RoutedEventArgs e)
        {
            // 钉图：把当前合成结果（底图 + 标注）钉到桌面，然后关闭编辑器
            var img = ComposeImage();
            var pin = new PinWindow(img);
            pin.Show();
            Close();
        }

        private void RackBtn_Click(object sender, RoutedEventArgs e)
        {
            // 挂衣架：把当前合成结果挂到晾衣架，然后关闭编辑器
            var img = ComposeImage();
            var app = (App)Application.Current;
            app.RackModule?.HangImage(img);
            Close();
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
                // 先取消选中；无选中时关闭窗口
                if (_controller != null && _controller.HasSelection)
                {
                    _controller.Deselect();
                    e.Handled = true;
                    return;
                }
                Close();
                e.Handled = true;
            }
            else if (e.Key == Key.Delete)
            {
                _controller?.DeleteSelected();
                UpdateUndoRedoState();
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
            if (_controller == null) return;

            var p = e.GetPosition(OverlayCanvas);

            // ── 橡皮擦工具：拖动擦除 ──
            if (_tool == AnnotationController.ToolKind.Eraser)
            {
                _erasing = true;
                _eraseTrack.Clear();
                _eraseTrack.Add(p);
                OverlayCanvas.CaptureMouse();
                e.Handled = true;
                return;
            }

            // ── 已有选中标注：先判断命中控点/旋转圈 ──
            if (_controller.Selected != null && TryHitHandle(p, out int handle, out bool rotate))
            {
                if (rotate && _controller.Selected.SupportsRotation)
                {
                    _rotating = true;
                    _rotateLast = p;
                    _rotateCenter = CenterOf(_controller.Selected.Bounds);
                    _controller.BeginRotate();
                }
                else if (!rotate && _controller.Selected.SupportsResize)
                {
                    _resizing = true;
                    _resizeHandle = handle;
                    _controller.BeginResize();
                }
                OverlayCanvas.CaptureMouse();
                e.Handled = true;
                return;
            }

            // ── 点选已存在的标注：进入拖动（移动）模式 ──
            if (_controller.TrySelect(p))
            {
                _draggingSelection = true;
                _dragStartPoint = p;
                _controller.BeginDrag(p);
                OverlayCanvas.CaptureMouse();
                e.Handled = true;
                return;
            }

            // ── 点空白：开始新标注（Draw 模式）──
            _controller.Deselect();
            _drawing = true;
            _dragStartPoint = p;
            _activeAnnotation = CreateAnnotation(_tool);
            _activePreview = _activeAnnotation.CreatePreview();
            OverlayCanvas.Children.Add(_activePreview);
            OverlayCanvas.CaptureMouse();

            e.Handled = true;
        }

        /// <summary>
        /// 判断点是否命中选中标注的控点或旋转圈。
        /// 返回 true 且 rotate=true 表示命中旋转圈；rotate=false 表示命中缩放柄（handle 0-7）。
        /// </summary>
        private bool TryHitHandle(Point p, out int handle, out bool rotate)
        {
            handle = -1;
            rotate = false;
            var sel = _controller?.Selected;
            if (sel == null) return false;

            var bounds = sel.Bounds;
            if (bounds.IsEmpty) return false;

            const double handleSize = 8.0;   // 控点直径
            const double hitBand = 6.0;      // 命中容差
            double half = handleSize / 2;

            // 只有支持缩放的形状才检查 8 个控点
            if (sel.SupportsResize)
            {
                var handles = new (Point p, int idx)[]
                {
                    (new Point(bounds.Left, bounds.Top), 0),
                    (new Point(bounds.Left + bounds.Width / 2, bounds.Top), 1),
                    (new Point(bounds.Right, bounds.Top), 2),
                    (new Point(bounds.Right, bounds.Top + bounds.Height / 2), 3),
                    (new Point(bounds.Right, bounds.Bottom), 4),
                    (new Point(bounds.Left + bounds.Width / 2, bounds.Bottom), 5),
                    (new Point(bounds.Left, bounds.Bottom), 6),
                    (new Point(bounds.Left, bounds.Top + bounds.Height / 2), 7),
                };

                foreach (var h in handles)
                {
                    if (Math.Abs(p.X - h.p.X) <= half + hitBand &&
                        Math.Abs(p.Y - h.p.Y) <= half + hitBand)
                    {
                        handle = h.idx;
                        return true;
                    }
                }
            }

            // 旋转圈：四角外 4-26px 一圈
            if (sel.SupportsRotation)
            {
                var corners = new[]
                {
                    new Point(bounds.Left, bounds.Top),
                    new Point(bounds.Right, bounds.Top),
                    new Point(bounds.Right, bounds.Bottom),
                    new Point(bounds.Left, bounds.Bottom),
                };
                foreach (var c in corners)
                {
                    double d = (p - c).Length;
                    if (d > 4 && d <= 26)
                    {
                        rotate = true;
                        return true;
                    }
                }
            }

            return false;
        }

        private static Point CenterOf(Rect r) => new(r.X + r.Width / 2, r.Y + r.Height / 2);

        /// <summary>
        /// 创建标注对象。马赛克需要底图与 DPI 系数，单独处理；
        /// 其余走控制器工厂（无需额外上下文）。
        /// </summary>
        private IAnnotation CreateAnnotation(AnnotationController.ToolKind tool)
        {
            if (tool == AnnotationController.ToolKind.Mosaic)
            {
                return new MosaicAnnotation(_image, _scaleX, _scaleY,
                    _controller!.Color, _controller.Thickness);
            }
            return _controller!.CreateAnnotation(tool);
        }

        private void Canvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (_controller == null) return;
            var p = e.GetPosition(OverlayCanvas);

            // 橡皮擦拖动：收集轨迹点
            if (_erasing)
            {
                _eraseTrack.Add(p);
                e.Handled = true;
                return;
            }

            // 缩放拖动
            if (_resizing)
            {
                _controller.ResizeSelected(_resizeHandle, p);
                e.Handled = true;
                return;
            }

            // 旋转拖动：绕中心计算角度增量
            if (_rotating)
            {
                double a1 = Math.Atan2(_rotateLast.Y - _rotateCenter.Y, _rotateLast.X - _rotateCenter.X);
                double a2 = Math.Atan2(p.Y - _rotateCenter.Y, p.X - _rotateCenter.X);
                _controller.RotateSelected(a2 - a1);
                _rotateLast = p;
                e.Handled = true;
                return;
            }

            // 拖动已选中的标注（移动）
            if (_draggingSelection)
            {
                _controller.Drag(p);
                e.Handled = true;
                return;
            }

            // 绘制新标注
            if (!_drawing || _activeAnnotation == null || _activePreview == null) return;
            _activeAnnotation.Update(_activePreview, _dragStartPoint, p);
            e.Handled = true;
        }

        /// <summary>右键取消当前正在进行的绘制（画笔拖错了不用 Ctrl+Z，直接右键丢弃）</summary>
        private void Canvas_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!_drawing) return;
            _drawing = false;
            OverlayCanvas.ReleaseMouseCapture();

            if (_activePreview != null)
            {
                OverlayCanvas.Children.Remove(_activePreview);
                _activePreview = null;
            }
            _activeAnnotation = null;

            e.Handled = true;
        }

        private void Canvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_controller == null) return;
            OverlayCanvas.ReleaseMouseCapture();

            // 结束橡皮擦：批量删除命中标注
            if (_erasing)
            {
                _erasing = false;
                if (_eraseTrack.Count > 0)
                {
                    _controller.EraseAlong(_eraseTrack);
                }
                _eraseTrack.Clear();
                UpdateUndoRedoState();
                e.Handled = true;
                return;
            }

            // 结束缩放
            if (_resizing)
            {
                _resizing = false;
                _controller.EndResize();
                UpdateUndoRedoState();
                e.Handled = true;
                return;
            }

            // 结束旋转
            if (_rotating)
            {
                _rotating = false;
                _controller.EndRotate();
                UpdateUndoRedoState();
                e.Handled = true;
                return;
            }

            // 结束拖动选中的标注
            if (_draggingSelection)
            {
                _draggingSelection = false;
                _controller.EndDrag();
                e.Handled = true;
                return;
            }

            // 结束绘制新标注
            if (!_drawing || _activeAnnotation == null || _activePreview == null) return;
            _drawing = false;

            var end = e.GetPosition(OverlayCanvas);

            // 画笔在 MouseMove 里已逐点追加，松手无需再 Update（否则会重复加终点）；
            // 其余形状用最终位置刷新一次，兜底「按下即松手」的边界。
            if (_activeAnnotation is not PenAnnotation)
            {
                _activeAnnotation.Update(_activePreview, _dragStartPoint, end);
            }

            // 马赛克特殊处理：先移除虚线框，再生成真正的马赛克贴片
            if (_activeAnnotation is MosaicAnnotation mosaic)
            {
                OverlayCanvas.Children.Remove(_activePreview);
                _activePreview = null;

                if (mosaic.BuildMosaic())
                {
                    OverlayCanvas.Children.Add(mosaic.Visual);
                    _controller.AddCommitted(mosaic);
                }
            }
            else if (_activeAnnotation.Commit(_activePreview))
            {
                // 提交成功：预览元素即最终元素，登记新增命令
                _controller.AddCommitted(_activeAnnotation);
            }
            else
            {
                // 太小/无效：丢弃预览元素
                OverlayCanvas.Children.Remove(_activePreview);
            }

            _activeAnnotation = null;
            _activePreview = null;
            UpdateUndoRedoState();
            e.Handled = true;
        }

        // ══════════════════════════════════════════════
        //  撤销 / 重做
        // ══════════════════════════════════════════════

        private void Undo()
        {
            _controller?.Undo();
            UpdateUndoRedoState();
        }

        private void Redo()
        {
            _controller?.Redo();
            UpdateUndoRedoState();
        }

        private void UpdateUndoRedoState()
        {
            if (_controller == null) return;
            UndoBtn.IsEnabled = _controller.CanUndo;
            RedoBtn.IsEnabled = _controller.CanRedo;
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
