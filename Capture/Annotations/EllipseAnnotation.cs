using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace WinKit.Capture.Annotations
{
    /// <summary>
    /// 椭圆标注 —— 与矩形同系：描边 + 半透明填充，支持选中、拖动、8 控点缩放与旋转。
    /// 几何约定与 RectAnnotation 完全一致（位置/尺寸 + RotateTransform 旋转）。
    /// </summary>
    public sealed class EllipseAnnotation : IAnnotation
    {
        private const byte FillAlpha = 0x22;

        private Ellipse? _element;
        private Color _color;
        private double _thickness;
        private double _angle;

        public EllipseAnnotation(Color color, double thickness)
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
                if (_element != null) ApplyBrush(_element);
            }
        }

        public double Thickness
        {
            get => _thickness;
            set
            {
                _thickness = value;
                if (_element != null) _element.StrokeThickness = value;
            }
        }

        public double Angle
        {
            get => _angle;
            set
            {
                _angle = value;
                if (_element != null) _element.RenderTransform = new RotateTransform(value);
            }
        }

        public bool SupportsResize => true;
        public bool SupportsRotation => true;

        public FrameworkElement Visual => _element!;

        public Rect Bounds
        {
            get
            {
                if (_element == null) return Rect.Empty;
                double x = Canvas.GetLeft(_element);
                double y = Canvas.GetTop(_element);
                double w = _element.Width;
                double h = _element.Height;
                if (Math.Abs(_angle) < 1e-6)
                    return new Rect(x, y, w, h);

                double cx = x + w / 2, cy = y + h / 2;
                double cos = Math.Cos(_angle), sin = Math.Sin(_angle);
                var corners = new[]
                {
                    Rotate(x, y, cx, cy, cos, sin),
                    Rotate(x + w, y, cx, cy, cos, sin),
                    Rotate(x + w, y + h, cx, cy, cos, sin),
                    Rotate(x, y + h, cx, cy, cos, sin),
                };
                double minX = double.MaxValue, minY = double.MaxValue;
                double maxX = double.MinValue, maxY = double.MinValue;
                foreach (var c in corners)
                {
                    minX = Math.Min(minX, c.X); maxX = Math.Max(maxX, c.X);
                    minY = Math.Min(minY, c.Y); maxY = Math.Max(maxY, c.Y);
                }
                return new Rect(minX, minY, maxX - minX, maxY - minY);
            }
        }

        public FrameworkElement CreatePreview()
        {
            var ell = new Ellipse
            {
                StrokeThickness = _thickness,
                IsHitTestVisible = true,
                Tag = this,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new RotateTransform(_angle),
            };
            ApplyBrush(ell);
            _element = ell;
            return ell;
        }

        public void Update(FrameworkElement el, Point start, Point current)
        {
            var rect = Normalize(start, current);
            Canvas.SetLeft(el, rect.X);
            Canvas.SetTop(el, rect.Y);
            el.Width = rect.Width;
            el.Height = rect.Height;
        }

        public bool Commit(FrameworkElement el) => el.Width >= 3 && el.Height >= 3;

        public bool HitTest(Point p)
        {
            if (_element == null) return false;
            double x = Canvas.GetLeft(_element);
            double y = Canvas.GetTop(_element);
            double cx = x + _element.Width / 2, cy = y + _element.Height / 2;
            double cos = Math.Cos(-_angle), sin = Math.Sin(-_angle);
            double dx = p.X - cx, dy = p.Y - cy;
            double lx = cx + dx * cos - dy * sin;
            double ly = cy + dx * sin + dy * cos;
            // 椭圆命中：归一化到单位圆判断
            double rx = _element.Width / 2, ry = _element.Height / 2;
            if (rx <= 0 || ry <= 0) return false;
            double nx = (lx - cx) / rx, ny = (ly - cy) / ry;
            return nx * nx + ny * ny <= 1.0;
        }

        public void SetSelected(bool selected)
        {
            if (_element == null) return;
            _element.StrokeThickness = selected ? _thickness + 1.5 : _thickness;
        }

        public void Translate(double dx, double dy)
        {
            if (_element == null) return;
            Canvas.SetLeft(_element, Canvas.GetLeft(_element) + dx);
            Canvas.SetTop(_element, Canvas.GetTop(_element) + dy);
        }

        public void Resize(int handle, Point p)
        {
            if (_element == null) return;
            double x = Canvas.GetLeft(_element);
            double y = Canvas.GetTop(_element);
            double w = _element.Width;
            double h = _element.Height;

            // 反旋转到未旋转坐标系
            if (Math.Abs(_angle) > 1e-6)
            {
                double cx = x + w / 2, cy = y + h / 2;
                double cos = Math.Cos(-_angle), sin = Math.Sin(-_angle);
                double dx = p.X - cx, dy = p.Y - cy;
                p = new Point(cx + dx * cos - dy * sin, cy + dx * sin + dy * cos);
            }

            switch (handle)
            {
                case 0: w = x + w - p.X; h = y + h - p.Y; x = p.X; y = p.Y; break;
                case 1: h = y + h - p.Y; y = p.Y; break;
                case 2: w = p.X - x; h = y + h - p.Y; y = p.Y; break;
                case 3: w = p.X - x; break;
                case 4: w = p.X - x; h = p.Y - y; break;
                case 5: h = p.Y - y; break;
                case 6: w = x + w - p.X; h = p.Y - y; x = p.X; break;
                case 7: w = x + w - p.X; x = p.X; break;
            }

            if (w < 3) w = 3;
            if (h < 3) h = 3;

            Canvas.SetLeft(_element, x);
            Canvas.SetTop(_element, y);
            _element.Width = w;
            _element.Height = h;
        }

        public void Rotate(double deltaRad) => Angle = _angle + deltaRad;

        public object Snapshot()
        {
            if (_element == null) return default!;
            return (
                Canvas.GetLeft(_element),
                Canvas.GetTop(_element),
                _element.Width,
                _element.Height,
                _color,
                _thickness,
                _angle
            );
        }

        public void Restore(object state)
        {
            if (_element == null) return;
            var (x, y, w, h, color, thickness, angle) =
                ((double, double, double, double, Color, double, double))state;
            Canvas.SetLeft(_element, x);
            Canvas.SetTop(_element, y);
            _element.Width = w;
            _element.Height = h;
            _color = color;
            _thickness = thickness;
            _angle = angle;
            _element.RenderTransform = new RotateTransform(angle);
            ApplyBrush(_element);
        }

        private void ApplyBrush(Ellipse ell)
        {
            var stroke = new SolidColorBrush(_color);
            stroke.Freeze();
            ell.Stroke = stroke;

            var fill = new SolidColorBrush(Color.FromArgb(FillAlpha, _color.R, _color.G, _color.B));
            fill.Freeze();
            ell.Fill = fill;
        }

        private static Point Rotate(double x, double y, double cx, double cy, double cos, double sin)
            => new(cx + (x - cx) * cos - (y - cy) * sin, cy + (x - cx) * sin + (y - cy) * cos);

        private static Rect Normalize(Point a, Point b) => new(
            Math.Min(a.X, b.X),
            Math.Min(a.Y, b.Y),
            Math.Abs(a.X - b.X),
            Math.Abs(a.Y - b.Y));
    }
}
