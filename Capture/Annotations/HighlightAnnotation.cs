using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace WinKit.Capture.Annotations
{
    /// <summary>
    /// 聚焦高亮 —— 拖出一个矩形，框内保持原样、框外压暗 55%。
    ///
    /// 实现：Visual 是覆盖整张画布的 Grid，里面一条 Path 的 Data 为
    /// 「全画布矩形 Exclude 高亮矩形」的组合几何，填充 #8C000000。
    /// 因此遮罩天然会被 ComposeImage 渲染进导出图，不需要额外合成步骤。
    ///
    /// Visual 整体 IsHitTestVisible=False：命中不靠 WPF 路由，而是由
    /// AnnotationController.TrySelect 遍历 Children 找 Tag 后调 HitTest。
    /// HitTest 只认矩形边框附近 6px 的带 —— 否则整块画布都能点中它，
    /// 用户就再也无法在框内画新标注了。
    /// </summary>
    public sealed class HighlightAnnotation : IAnnotation
    {
        private static readonly Color MaskColor = Color.FromArgb(0x8C, 0x00, 0x00, 0x00); // 55% 黑
        private static readonly Color SelectionGreen = Color.FromRgb(0x34, 0xC7, 0x59);

        private readonly double _canvasW;
        private readonly double _canvasH;

        private Grid? _element;
        private System.Windows.Shapes.Path? _mask;
        private System.Windows.Shapes.Rectangle? _selection;
        private Rect _rect;
        private Color _color;
        private double _thickness;

        public HighlightAnnotation(double canvasW, double canvasH, Color color, double thickness)
        {
            _canvasW = canvasW;
            _canvasH = canvasH;
            _color = color;
            _thickness = thickness;
            BuildVisual();
        }

        public Color Color
        {
            get => _color;
            set
            {
                _color = value;
                if (_selection != null) _selection.Stroke = new SolidColorBrush(value);
            }
        }

        /// <summary>线宽对遮罩无意义，只存不用。</summary>
        public double Thickness
        {
            get => _thickness;
            set => _thickness = value;
        }

        public double Angle { get => 0; set { } }
        public bool SupportsResize => true;
        public bool SupportsRotation => false;

        public FrameworkElement Visual => _element!;
        public Rect Bounds => _rect;

        private void BuildVisual()
        {
            _mask = new System.Windows.Shapes.Path
            {
                Fill = new SolidColorBrush(MaskColor),
                IsHitTestVisible = false,
            };

            _selection = new System.Windows.Shapes.Rectangle
            {
                Stroke = new SolidColorBrush(SelectionGreen),
                StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 4, 3 },
                Visibility = Visibility.Hidden,
                IsHitTestVisible = false,
            };

            _element = new Grid
            {
                Width = _canvasW,
                Height = _canvasH,
                Background = Brushes.Transparent,
                IsHitTestVisible = false,
                Tag = this,
            };
            _element.Children.Add(_mask);
            _element.Children.Add(_selection);
            Canvas.SetLeft(_element, 0);
            Canvas.SetTop(_element, 0);
            Rebuild();
        }

        /// <summary>按当前高亮矩形重建「挖洞」几何与选中框位置。</summary>
        private void Rebuild()
        {
            if (_mask == null || _selection == null) return;

            var full = new RectangleGeometry(new Rect(0, 0, _canvasW, _canvasH));
            if (_rect.Width >= 1 && _rect.Height >= 1)
            {
                var hole = new RectangleGeometry(_rect);
                _mask.Data = Geometry.Combine(full, hole, GeometryCombineMode.Exclude, null);
            }
            else
            {
                _mask.Data = full;
            }

            Canvas.SetLeft(_selection, _rect.X);
            Canvas.SetTop(_selection, _rect.Y);
            _selection.Width = Math.Max(0, _rect.Width);
            _selection.Height = Math.Max(0, _rect.Height);
        }

        public FrameworkElement CreatePreview() => Visual;

        public void Update(FrameworkElement el, Point start, Point current)
        {
            _rect = Normalize(start, current);
            Rebuild();
        }

        public bool Commit(FrameworkElement el) => _rect.Width >= 3 && _rect.Height >= 3;

        /// <summary>只命中矩形边框附近 6px 的带，框内留给新标注。</summary>
        public bool HitTest(Point p)
        {
            const double band = 6;
            bool outer = p.X >= _rect.X - band && p.X <= _rect.Right + band &&
                         p.Y >= _rect.Y - band && p.Y <= _rect.Bottom + band;
            bool inner = p.X > _rect.X + band && p.X < _rect.Right - band &&
                         p.Y > _rect.Y + band && p.Y < _rect.Bottom - band;
            return outer && !inner;
        }

        public void SetSelected(bool selected)
        {
            if (_selection != null)
                _selection.Visibility = selected ? Visibility.Visible : Visibility.Hidden;
        }

        public void Translate(double dx, double dy)
        {
            _rect = new Rect(_rect.X + dx, _rect.Y + dy, _rect.Width, _rect.Height);
            Rebuild();
        }

        public void Resize(int handle, Point p)
        {
            double l = _rect.Left, t = _rect.Top, r = _rect.Right, b = _rect.Bottom;
            switch (handle)
            {
                case 0: l = p.X; t = p.Y; break;
                case 1: t = p.Y; break;
                case 2: r = p.X; t = p.Y; break;
                case 3: r = p.X; break;
                case 4: r = p.X; b = p.Y; break;
                case 5: b = p.Y; break;
                case 6: l = p.X; b = p.Y; break;
                case 7: l = p.X; break;
            }
            _rect = new Rect(Math.Min(l, r), Math.Min(t, b), Math.Abs(r - l), Math.Abs(b - t));
            Rebuild();
        }

        public void Rotate(double deltaRad) { }

        public object Snapshot() => (_rect, _color);

        public void Restore(object state)
        {
            var (rect, color) = ((Rect, Color))state;
            _rect = rect;
            _color = color;
            if (_selection != null) _selection.Stroke = new SolidColorBrush(color);
            Rebuild();
        }

        private static Rect Normalize(Point a, Point b) => new(
            Math.Min(a.X, b.X),
            Math.Min(a.Y, b.Y),
            Math.Abs(a.X - b.X),
            Math.Abs(a.Y - b.Y));
    }
}
