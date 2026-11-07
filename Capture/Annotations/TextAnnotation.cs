using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace WinKit.Capture.Annotations
{
    /// <summary>
    /// 文字标注 —— 拖动阶段用亮绿 1px 虚线框示意（与马赛克同套路），
    /// 松手后由 EditorWindow 弹出就地输入框与样式条，确定后才生成真正的
    /// TextBlock 元素挂到画布，因此 ComposeImage 永远不会把光标或样式条截进图片。
    ///
    /// 与 RectAnnotation 的差别：不支持旋转；缩放只改框宽（文字自动换行、字号不变）；
    /// 字号由编辑期样式条单独给定，不复用 IAnnotation.Thickness。
    /// 选中态自带虚线外框 + 8 个白色控制点，与截屏选区层视觉语言一致。
    /// </summary>
    public sealed class TextAnnotation : IAnnotation
    {
        public const double MinWidth = 24;
        public const double MinHeight = 16;

        private static readonly Color SelectionGreen = Color.FromRgb(0x34, 0xC7, 0x59);

        private Rectangle? _dashPreview;
        private Grid? _element;
        private TextBlock? _textBlock;
        private Grid? _selectionLayer;

        private string _text = string.Empty;
        private Color _color;
        private double _thickness;      // 兼容接口；字号另存 _fontSize
        private double _fontSize = 20;
        private Point _start;
        private Point _end;

        public TextAnnotation(Color color, double thickness)
        {
            _color = color;
            _thickness = thickness;
        }

        public Color Color
        {
            get => _color;
            set
            {
                _color = value;
                if (_textBlock != null) _textBlock.Foreground = new SolidColorBrush(value);
            }
        }

        /// <summary>线宽对文字无意义：只存不用，字号由样式条单独控制。</summary>
        public double Thickness
        {
            get => _thickness;
            set => _thickness = value;
        }

        public double Angle { get => 0; set { } }
        public bool SupportsResize => true;
        public bool SupportsRotation => false;

        public FrameworkElement Visual => _element!;

        public Rect Bounds
        {
            get
            {
                if (_element == null) return Rect.Empty;
                return new Rect(Canvas.GetLeft(_element), Canvas.GetTop(_element),
                                _element.Width, _element.Height);
            }
        }

        // ══════════════════════════════════════════════
        //  拖拽阶段：虚线框
        // ══════════════════════════════════════════════

        public FrameworkElement CreatePreview()
        {
            var stroke = new SolidColorBrush(SelectionGreen);
            stroke.Freeze();
            _dashPreview = new Rectangle
            {
                Stroke = stroke,
                StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 4, 3 },
                IsHitTestVisible = false,
                Tag = this,
            };
            return _dashPreview;
        }

        public void Update(FrameworkElement el, Point start, Point current)
        {
            _start = start;
            _end = current;
            var rect = Normalize(start, current);
            Canvas.SetLeft(el, rect.X);
            Canvas.SetTop(el, rect.Y);
            el.Width = rect.Width;
            el.Height = rect.Height;
        }

        public bool Commit(FrameworkElement el) => el.Width >= MinWidth && el.Height >= MinHeight;

        /// <summary>拖框结束时的归一化矩形（宿主拿它摆放输入框与样式条）。</summary>
        public Rect DraftBounds => Normalize(_start, _end);

        // ══════════════════════════════════════════════
        //  提交阶段：虚线框 → TextBlock
        // ══════════════════════════════════════════════

        /// <summary>由宿主在用户确定后调用；调用后 Visual 才可用，才能交给 AddCommitted。</summary>
        public void SetContent(string text, Color color, double fontSize, Rect rect)
        {
            _text = text;
            _color = color;
            _fontSize = fontSize;

            _textBlock = new TextBlock
            {
                Text = text,
                FontSize = fontSize,
                Foreground = new SolidColorBrush(color),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(3, 1, 3, 1),
                IsHitTestVisible = false,
            };

            _selectionLayer = BuildSelectionLayer();

            _element = new Grid
            {
                Width = Math.Max(MinWidth, rect.Width),
                Height = Math.Max(MinHeight, rect.Height),
                Background = Brushes.Transparent,
                IsHitTestVisible = true,
                Tag = this,
            };
            _element.Children.Add(_textBlock);
            _element.Children.Add(_selectionLayer);

            Canvas.SetLeft(_element, rect.X);
            Canvas.SetTop(_element, rect.Y);
        }

        /// <summary>构建选中装饰：1 圈绿色虚线 + 8 个白底绿边控制点（默认隐藏）。</summary>
        private static Grid BuildSelectionLayer()
        {
            var layer = new Grid
            {
                Visibility = Visibility.Hidden,
                IsHitTestVisible = false,
            };

            var frame = new Rectangle
            {
                Stroke = new SolidColorBrush(SelectionGreen),
                StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 4, 3 },
                IsHitTestVisible = false,
            };
            layer.Children.Add(frame);

            var dots = new (HorizontalAlignment h, VerticalAlignment v)[]
            {
                (HorizontalAlignment.Left,   VerticalAlignment.Top),
                (HorizontalAlignment.Center, VerticalAlignment.Top),
                (HorizontalAlignment.Right,  VerticalAlignment.Top),
                (HorizontalAlignment.Right,  VerticalAlignment.Center),
                (HorizontalAlignment.Right,  VerticalAlignment.Bottom),
                (HorizontalAlignment.Center, VerticalAlignment.Bottom),
                (HorizontalAlignment.Left,   VerticalAlignment.Bottom),
                (HorizontalAlignment.Left,   VerticalAlignment.Center),
            };
            foreach (var (h, v) in dots)
            {
                layer.Children.Add(new Rectangle
                {
                    Width = 8,
                    Height = 8,
                    Fill = Brushes.White,
                    Stroke = new SolidColorBrush(SelectionGreen),
                    StrokeThickness = 1,
                    Margin = new Thickness(-4),
                    HorizontalAlignment = h,
                    VerticalAlignment = v,
                    IsHitTestVisible = false,
                });
            }
            return layer;
        }

        // ══════════════════════════════════════════════
        //  选中 / 命中 / 变换
        // ══════════════════════════════════════════════

        public bool HitTest(Point p) => Bounds.Contains(p);

        public void SetSelected(bool selected)
        {
            if (_selectionLayer != null)
                _selectionLayer.Visibility = selected ? Visibility.Visible : Visibility.Hidden;
        }

        public void Translate(double dx, double dy)
        {
            if (_element == null) return;
            Canvas.SetLeft(_element, Canvas.GetLeft(_element) + dx);
            Canvas.SetTop(_element, Canvas.GetTop(_element) + dy);
        }

        /// <summary>只改框宽：左半控点固定右边界，右半控点固定左边界，上下控点忽略。</summary>
        public void Resize(int handle, Point p)
        {
            if (_element == null) return;
            double x = Canvas.GetLeft(_element);
            double w = _element.Width;
            double right = x + w;

            switch (handle)
            {
                case 0: case 6: case 7:                 // 左侧：右边界不动
                    x = Math.Min(p.X, right - MinWidth);
                    w = right - x;
                    break;
                case 2: case 3: case 4:                 // 右侧：左边界不动
                    w = Math.Max(MinWidth, p.X - x);
                    break;
                case 1: case 5:                         // 上下中点：高度不变
                    break;
            }

            Canvas.SetLeft(_element, x);
            _element.Width = w;
        }

        public void Rotate(double deltaRad) { }

        public object Snapshot()
        {
            if (_element == null) return default!;
            return (Canvas.GetLeft(_element), Canvas.GetTop(_element),
                    _element.Width, _element.Height, _color, _fontSize, _text);
        }

        public void Restore(object state)
        {
            if (_element == null || _textBlock == null) return;
            var (x, y, w, h, color, fontSize, text) =
                ((double, double, double, double, Color, double, string))state;
            Canvas.SetLeft(_element, x);
            Canvas.SetTop(_element, y);
            _element.Width = w;
            _element.Height = h;
            _color = color;
            _fontSize = fontSize;
            _text = text;
            _textBlock.FontSize = fontSize;
            _textBlock.Foreground = new SolidColorBrush(color);
            _textBlock.Text = text;
        }

        private static Rect Normalize(Point a, Point b) => new(
            Math.Min(a.X, b.X),
            Math.Min(a.Y, b.Y),
            Math.Abs(a.X - b.X),
            Math.Abs(a.Y - b.Y));
    }
}
