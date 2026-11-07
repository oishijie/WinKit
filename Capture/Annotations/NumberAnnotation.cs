using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace WinKit.Capture.Annotations
{
    /// <summary>
    /// 序号标注 —— 点一下放一个 28px 圆形徽标，编号按放置顺序自增。
    ///
    /// 与拖拽类标注不同，它不走「按下建预览 → Update → Commit」链路，
    /// 而是由 EditorWindow 在 MouseDown 里直接 AddCommitted：点哪放哪，
    /// 可以连续点出 1 / 2 / 3。删除、撤销、重做都**不重排已有编号**，
    /// 避免用户排好的顺序突然跳位。
    ///
    /// 固定尺寸、不支持旋转；选中时显示一圈亮绿 1px 虚线外框。
    /// </summary>
    public sealed class NumberAnnotation : IAnnotation
    {
        public const double Size = 28;

        private static readonly Color SelectionGreen = Color.FromRgb(0x34, 0xC7, 0x59);

        private readonly int _number;
        private readonly Point _origin;

        private Grid? _element;
        private Ellipse? _dot;
        private Rectangle? _selection;
        private Color _color;
        private double _thickness;

        /// <param name="number">自增编号，由 EditorWindow 的计数器给定</param>
        /// <param name="color">徽标底色，跟随当前调色板</param>
        /// <param name="center">点击位置（徽标中心）</param>
        public NumberAnnotation(int number, Color color, Point center)
        {
            _number = number;
            _color = color;
            _origin = new Point(center.X - Size / 2, center.Y - Size / 2);
            BuildVisual();
        }

        public Color Color
        {
            get => _color;
            set
            {
                _color = value;
                if (_dot != null) _dot.Fill = new SolidColorBrush(value);
            }
        }

        /// <summary>线宽对序号徽标无意义，只存不用。</summary>
        public double Thickness
        {
            get => _thickness;
            set => _thickness = value;
        }

        public double Angle { get => 0; set { } }
        public bool SupportsResize => false;
        public bool SupportsRotation => false;

        public FrameworkElement Visual => _element!;

        public Rect Bounds
        {
            get
            {
                if (_element == null) return Rect.Empty;
                return new Rect(Canvas.GetLeft(_element), Canvas.GetTop(_element), Size, Size);
            }
        }

        private void BuildVisual()
        {
            _dot = new Ellipse
            {
                Fill = new SolidColorBrush(_color),
                Stroke = Brushes.White,
                StrokeThickness = 1.5,
                Width = Size,
                Height = Size,
                IsHitTestVisible = false,
            };

            var label = new TextBlock
            {
                Text = _number.ToString(),
                Foreground = Brushes.White,
                FontSize = 15,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
            };

            _selection = new Rectangle
            {
                Stroke = new SolidColorBrush(SelectionGreen),
                StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 4, 3 },
                Margin = new Thickness(-4),
                Visibility = Visibility.Hidden,
                IsHitTestVisible = false,
            };

            _element = new Grid
            {
                Width = Size,
                Height = Size,
                Background = Brushes.Transparent,
                IsHitTestVisible = true,
                Tag = this,
            };
            _element.Children.Add(_dot);
            _element.Children.Add(label);
            _element.Children.Add(_selection);

            Canvas.SetLeft(_element, _origin.X);
            Canvas.SetTop(_element, _origin.Y);
        }

        // 序号是「点一下即提交」，下面三个方法只为了满足 IAnnotation 契约，
        // 正常链路不会被调用（EditorWindow 直接 AddCommitted）。
        public FrameworkElement CreatePreview() => Visual;
        public void Update(FrameworkElement el, Point start, Point current) { }
        public bool Commit(FrameworkElement el) => true;

        public bool HitTest(Point p) => Bounds.Contains(p);

        public void SetSelected(bool selected)
        {
            if (_selection != null)
                _selection.Visibility = selected ? Visibility.Visible : Visibility.Hidden;
        }

        public void Translate(double dx, double dy)
        {
            if (_element == null) return;
            Canvas.SetLeft(_element, Canvas.GetLeft(_element) + dx);
            Canvas.SetTop(_element, Canvas.GetTop(_element) + dy);
        }

        public void Resize(int handle, Point p) { }
        public void Rotate(double deltaRad) { }

        public object Snapshot()
        {
            if (_element == null) return default!;
            return (Canvas.GetLeft(_element), Canvas.GetTop(_element), _color);
        }

        public void Restore(object state)
        {
            if (_element == null || _dot == null) return;
            var (x, y, color) = ((double, double, Color))state;
            Canvas.SetLeft(_element, x);
            Canvas.SetTop(_element, y);
            _color = color;
            _dot.Fill = new SolidColorBrush(color);
        }
    }
}
