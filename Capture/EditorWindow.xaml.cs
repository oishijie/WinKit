using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
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

        // 序号标注自增计数器：只增不减，删除 / 撤销都不重排已有编号
        private int _numberCounter;

        // 文字标注的就地编辑态：这些元素都只在编辑期间挂在 OverlayCanvas 上，
        // 提交/取消时立刻移除，因此 ComposeImage 永远截不到光标与样式条。
        private bool _textEditing;
        private TextAnnotation? _textEditingAnno;
        private Grid? _textEditHost;
        private TextBox? _textEditor;
        private Color _textColor = Color.FromRgb(0xFF, 0x3B, 0x30);
        private double _textFontSize = 20;
        private Rect _textRect;

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
            // 从 OCR / 翻译结果窗切回来时恢复置顶（弹出结果窗前会临时降一层）
            Activated += (s, e) => Topmost = true;
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

        /// <summary>工具名 → 子工具条里的中文提示</summary>
        private static readonly Dictionary<string, string> ToolLabels = new()
        {
            ["Rect"] = "矩形",
            ["Ellipse"] = "椭圆",
            ["Arrow"] = "箭头",
            ["Pen"] = "画笔",
            ["Mosaic"] = "马赛克",
            ["Eraser"] = "橡皮擦",
            ["Text"] = "文字",
            ["Number"] = "序号",
            ["Line"] = "直线",
            ["Highlight"] = "聚焦",
        };

        private void CapBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount != 1) return;
            // 点在图标按钮上不能拖：DragMove 会进入模态移动循环并吞掉 Click。
            // 只有把手和胶囊空白处才允许拖动窗口。
            if (IsInsideButton(e.OriginalSource as DependencyObject)) return;
            DragMove();
        }

        private static bool IsInsideButton(DependencyObject? source)
        {
            var d = source;
            while (d != null)
            {
                if (d is ButtonBase) return true;
                d = VisualTreeHelper.GetParent(d);
            }
            return false;
        }

        private void BtnMore_Click(object sender, RoutedEventArgs e)
        {
            SubToolBar.Visibility = SubToolBar.Visibility == Visibility.Visible
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        private void Tool_Checked(object sender, RoutedEventArgs e)
        {
            // 换工具前先把正在编辑的文字收掉，避免遗留一个悬挂的输入框
            if (_textEditing) FinishTextEdit(true);

            // Tag 已被 StreamGeometry 占用，工具名改放 CommandParameter
            if (sender is RadioButton rb && rb.CommandParameter is string tag
                && Enum.TryParse<AnnotationController.ToolKind>(tag, out var kind))
            {
                _tool = kind;
                if (CurrentToolText != null)
                {
                    CurrentToolText.Text = ToolLabels.TryGetValue(tag, out var label) ? label : tag;
                }
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
            if (sender is RadioButton rb && rb.CommandParameter is string w
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
                var img = ComposeImage();
                System.Windows.Clipboard.SetImage(img);
                SetStatus("已复制到剪贴板");

                // 设置「截图完成后自动挂载」时才额外挂一份；默认关，行为与旧版完全一致。
                var app = (App)Application.Current;
                if (app.SettingsManager?.Settings.RackAutoHangCapture == true)
                    app.RackModule?.HangImage(img);
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

        // ══════════════════════════════════════════════
        //  OCR / 翻译：复用主实例的引擎与结果窗
        // ══════════════════════════════════════════════

        private void OcrBtn_Click(object sender, RoutedEventArgs e) => RunEditorOcr(false);
        private void TranslateBtn_Click(object sender, RoutedEventArgs e) => RunEditorOcr(true);

        /// <summary>
        /// 把「底图 + 已有标注」合成图交给翻译模块做 OCR / 翻译。
        /// 链路完全跑在主实例上：不新建第二个 200~300MB 的 OCR 引擎，
        /// 也不新造结果界面（ResultWindow 的复制 / 翻译 / 搜索 / 钉图全部白拿）。
        /// 编辑器本身保持打开。
        /// </summary>
        private async void RunEditorOcr(bool translate)
        {
            var app = (App)Application.Current;
            var module = app.TranslateModule;
            if (module == null)
            {
                SetStatus("翻译模块未就绪");
                return;
            }
            if (translate && !module.IsTranslatorConfigured)
            {
                SetStatus("未配置翻译 API Key：设置中心 → 翻译与 OCR");
                return;
            }

            try
            {
                // ComposeImage 会顺带收尾文字编辑、去掉选中态，
                // 所以识别输入里不会混入虚线框、控制点或光标。
                var image = ComposeImage();

                // 结果窗与编辑器同为 Topmost；临时让编辑器退一层，
                // 保证 ShowAtMouse() + Activate() 能稳定把结果窗盖上来。
                // 用户点回编辑器时由构造函数里的 Activated 订阅恢复置顶。
                Topmost = false;
                SetStatus(translate ? "正在识别并翻译…" : "正在识别文字…");

                if (translate) await module.TranslateEditorImageAsync(image);
                else await module.RecognizeEditorImageAsync(image);

                SetStatus("识别完成，结果窗已弹出");
            }
            catch (Exception ex)
            {
                SetStatus($"识别失败：{ex.Message}");
            }
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            // 文字就地编辑期间把键盘让给输入框：
            // Esc 取消编辑，Delete / Ctrl+Z / Ctrl+Y 由 TextBox 自己处理，窗口快捷键一律不抢。
            if (_textEditing)
            {
                if (e.Key == Key.Escape)
                {
                    CancelTextEdit();
                    e.Handled = true;
                }
                return;
            }

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

            // 编辑期间：点在输入框 / 样式条内不打扰；点画布其它位置视为「提交并收尾」。
            // 这次点击只用于收尾，不再顺手画一个新标注。
            if (_textEditing)
            {
                var src = e.OriginalSource as DependencyObject;
                if (_textEditHost != null && IsDescendantOf(src, _textEditHost)) return;
                if (IsDescendantOf(src, TextStyleBar)) return;
                FinishTextEdit(true);
                return;
            }

            var p = e.GetPosition(OverlayCanvas);

            // ── 序号工具：点一下即放置，不走「预览 → 拖拽 → 提交」链路 ──
            // 这样连点可以连续放 1 / 2 / 3，也不会出现徽标先在 (0,0) 闪一帧。
            if (_tool == AnnotationController.ToolKind.Number)
            {
                _controller.Deselect();
                var num = new NumberAnnotation(++_numberCounter, _controller.Color, p);
                _controller.AddCommitted(num);
                UpdateUndoRedoState();
                e.Handled = true;
                return;
            }

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
            if (tool == AnnotationController.ToolKind.Highlight)
            {
                // 遮罩要覆盖整张画布，构造时就得知道画布的逻辑尺寸（OnLoaded 里已设好）
                return new HighlightAnnotation(OverlayCanvas.Width, OverlayCanvas.Height,
                    _controller!.Color, _controller.Thickness);
            }
            return _controller!.CreateAnnotation(tool);
        }

        private void Canvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (_controller == null) return;
            if (_textEditing) return;
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
            if (_textEditing) return;
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

            // 文字特殊处理：虚线框换成就地输入框，用户确定后才 AddCommitted。
            // 与马赛克同属「松手后由宿主接管」的两阶段标注。
            if (_activeAnnotation is TextAnnotation textAnno)
            {
                var rect = textAnno.DraftBounds;
                bool valid = rect.Width >= TextAnnotation.MinWidth &&
                             rect.Height >= TextAnnotation.MinHeight;

                OverlayCanvas.Children.Remove(_activePreview);
                _activePreview = null;
                _activeAnnotation = null;

                if (valid) BeginTextEdit(textAnno, rect);
                UpdateUndoRedoState();
                e.Handled = true;
                return;
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
            BtnUndo.IsEnabled = _controller.CanUndo;
            BtnRedo.IsEnabled = _controller.CanRedo;
        }

        // ══════════════════════════════════════════════
        //  文字标注：拖框 → 就地输入 → 提交
        // ══════════════════════════════════════════════

        private const double TextFontSizeMin = 10;
        private const double TextFontSizeMax = 72;
        private const double TextFontSizeStep = 2;

        /// <summary>把刚拖好的虚线框换成输入框，并把样式条浮到框上方。</summary>
        private void BeginTextEdit(TextAnnotation anno, Rect rect)
        {
            _textEditing = true;                 // 先立闸，避免后续事件重入
            _textEditingAnno = anno;
            _textRect = rect;
            _textFontSize = 20;
            _textColor = DefaultColor;

            TextColorRed.IsChecked = true;       // 同步样式条到默认红
            UpdateTextStyleUi();

            var frame = new Rectangle
            {
                Stroke = new SolidColorBrush(Color.FromRgb(0x34, 0xC7, 0x59)),
                StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 4, 3 },
                IsHitTestVisible = false,
            };

            _textEditor = new TextBox
            {
                Width = rect.Width,
                Height = rect.Height,
                FontSize = _textFontSize,
                Foreground = new SolidColorBrush(_textColor),
                Background = new SolidColorBrush(Color.FromArgb(0x30, 0, 0, 0)),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(3, 1, 3, 1),
                TextWrapping = TextWrapping.Wrap,
                AcceptsReturn = false,
                AcceptsTab = false,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            };
            _textEditor.SpellCheck.IsEnabled = false;
            _textEditor.PreviewKeyDown += TextEditor_PreviewKeyDown;
            _textEditor.LostKeyboardFocus += TextEditor_LostKeyboardFocus;

            _textEditHost = new Grid
            {
                Width = rect.Width,
                Height = rect.Height,
                IsHitTestVisible = true,
            };
            _textEditHost.Children.Add(frame);
            _textEditHost.Children.Add(_textEditor);
            Canvas.SetLeft(_textEditHost, rect.X);
            Canvas.SetTop(_textEditHost, rect.Y);
            OverlayCanvas.Children.Add(_textEditHost);

            // 先可见再 Measure（Collapsed 元素测量恒为 0），然后摆到框上方；放不下就落下方
            TextStyleBar.Visibility = Visibility.Visible;
            TextStyleBar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double barW = TextStyleBar.DesiredSize.Width;
            double barH = TextStyleBar.DesiredSize.Height;
            double barX = Math.Max(0, Math.Min(rect.X, OverlayCanvas.Width - barW));
            double barY = rect.Y - barH - 6;
            if (barY < 0) barY = rect.Y + rect.Height + 6;
            Canvas.SetLeft(TextStyleBar, barX);
            Canvas.SetTop(TextStyleBar, barY);

            SetStatus("文字标注：Enter 确定 · Esc 取消 · 点击别处提交");

            // 焦点要等元素真正进入可视树之后再给
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!_textEditing || _textEditor == null) return;
                _textEditor.Focus();
                Keyboard.Focus(_textEditor);
                _textEditor.CaretIndex = _textEditor.Text.Length;
            }), DispatcherPriority.Input);
        }

        /// <summary>
        /// 结束编辑。commit=false 或文本为空时直接丢弃，不进撤销栈；
        /// commit=true 且非空时把输入框换成 TextBlock 渲染，再登记一条「新增」撤销命令。
        /// </summary>
        private void FinishTextEdit(bool commit)
        {
            if (!_textEditing) return;
            _textEditing = false;                // 先关闸，避免移除元素触发的 LostFocus 重入

            string text = _textEditor?.Text?.Trim() ?? string.Empty;
            var anno = _textEditingAnno;
            DetachTextEditor();

            if (commit && anno != null && text.Length > 0)
            {
                anno.SetContent(text, _textColor, _textFontSize, _textRect);
                _controller?.AddCommitted(anno);
                // 提交后自动选中，直接露出虚线外框与 8 个控制点
                _controller?.TrySelect(new WPoint(_textRect.X + 2, _textRect.Y + 2));
                SetStatus("已添加文字标注");
            }
            else
            {
                SetStatus(commit ? "文字为空，已丢弃" : "已取消文字标注");
            }

            _textEditingAnno = null;
            UpdateUndoRedoState();
        }

        private void CancelTextEdit() => FinishTextEdit(false);

        /// <summary>移除输入框与样式条，解绑事件；调用后画布上只剩已提交的标注。</summary>
        private void DetachTextEditor()
        {
            TextStyleBar.Visibility = Visibility.Collapsed;

            if (_textEditor != null)
            {
                _textEditor.PreviewKeyDown -= TextEditor_PreviewKeyDown;
                _textEditor.LostKeyboardFocus -= TextEditor_LostKeyboardFocus;
                _textEditor = null;
            }
            if (_textEditHost != null)
            {
                OverlayCanvas.Children.Remove(_textEditHost);
                _textEditHost.Children.Clear();
                _textEditHost = null;
            }
        }

        private void TextColor_Checked(object sender, RoutedEventArgs e)
        {
            if (!_textEditing) return;
            if (sender is RadioButton rb && rb.Tag is string hex)
            {
                try
                {
                    _textColor = (Color)ColorConverter.ConvertFromString(hex);
                    if (_textEditor != null) _textEditor.Foreground = new SolidColorBrush(_textColor);
                }
                catch { /* 无效色值忽略 */ }
            }
        }

        private void TextSizeDown_Click(object sender, RoutedEventArgs e) => ChangeTextFontSize(-TextFontSizeStep);
        private void TextSizeUp_Click(object sender, RoutedEventArgs e) => ChangeTextFontSize(TextFontSizeStep);

        private void ChangeTextFontSize(double delta)
        {
            if (!_textEditing) return;
            double v = Math.Max(TextFontSizeMin, Math.Min(TextFontSizeMax, _textFontSize + delta));
            if (Math.Abs(v - _textFontSize) < 1e-6) return;
            _textFontSize = v;
            if (_textEditor != null) _textEditor.FontSize = v;
            UpdateTextStyleUi();
        }

        private void UpdateTextStyleUi()
        {
            TextSizeLabel.Text = ((int)_textFontSize).ToString();
            BtnTextSizeDown.IsEnabled = _textFontSize > TextFontSizeMin;
            BtnTextSizeUp.IsEnabled = _textFontSize < TextFontSizeMax;
        }

        private void TextOk_Click(object sender, RoutedEventArgs e) => FinishTextEdit(true);
        private void TextCancel_Click(object sender, RoutedEventArgs e) => CancelTextEdit();

        private void TextEditor_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                FinishTextEdit(true);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                CancelTextEdit();
                e.Handled = true;
            }
        }

        /// <summary>
        /// 焦点离开输入框即提交，但点样式条按钮时必须放行，
        /// 否则颜色 / 字号还没点下去编辑就被收掉了。
        /// </summary>
        private void TextEditor_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (!_textEditing) return;
            if (TextStyleBar.IsMouseOver) return;
            if (e.NewFocus is DependencyObject d && IsDescendantOf(d, TextStyleBar)) return;

            // 延迟到本轮输入处理结束，避免抢在工具条按钮 Click 之前收尾
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_textEditing && !TextStyleBar.IsMouseOver) FinishTextEdit(true);
            }), DispatcherPriority.Background);
        }

        private static bool IsDescendantOf(DependencyObject? child, DependencyObject parent)
        {
            var d = child;
            while (d != null)
            {
                if (ReferenceEquals(d, parent)) return true;
                d = VisualTreeHelper.GetParent(d);
            }
            return false;
        }

        // ══════════════════════════════════════════════
        //  导出
        // ══════════════════════════════════════════════

        /// <summary>
        /// 把底图与标注合成成一张位图，按物理像素输出（不受窗口显示缩放影响）。
        /// </summary>
        private BitmapSource ComposeImage()
        {
            // 兜底：编辑中途点了「完成 / 钉图 / 挂衣架 / 另存为」也要先收掉输入框与样式条；
            // 顺带去掉选中态，避免虚线框与 8 个控制点被截进导出图片。
            // 下面的 RenderTargetBitmap 渲染逻辑本身一行未改。
            if (_textEditing) FinishTextEdit(true);
            _controller?.Deselect();

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
