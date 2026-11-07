using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace WinKit.Capture.Annotations
{
    /// <summary>
    /// 矩形标注 —— 描边 + 半透明填充，支持选中、拖动、8 控点缩放与旋转。
    ///
    /// 几何约定：位置/尺寸用 Canvas.SetLeft/Top + Width/Height 表达（未旋转的轴对齐矩形），
    /// 旋转单独用 RotateTransform（绕中心，RenderTransformOrigin=0.5,0.5）在渲染时叠加。
    /// 因此缩放只改 Left/Top/Width/Height（不涉及旋转），命中时先把点反旋转回未旋转坐标。
    /// </summary>
    public sealed class RectAnnotation : IAnnotation
    {
        private const byte FillAlpha = 0x22;

        private Rectangle? _element;
        private Color _color;
        private double _thickness;
        private double _angle;

        public RectAnnotation(Color color, double thickness)
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

                // 旋转后的轴对齐包围盒
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
            var rect = new Rectangle
            {
                StrokeThickness = _thickness,
                IsHitTestVisible = true,
                Tag = this,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new RotateTransform(_angle),
            };
            ApplyBrush(rect);
            _element = rect;
            return rect;
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
            // 把点反旋转回未旋转坐标
            double x = Canvas.GetLeft(_element);
            double y = Canvas.GetTop(_element);
            double cx = x + _element.Width / 2, cy = y + _element.Height / 2;
            double cos = Math.Cos(-_angle), sin = Math.Sin(-_angle);
            double dx = p.X - cx, dy = p.Y - cy;
            double lx = cx + dx * cos - dy * sin;
            double ly = cy + dx * sin + dy * cos;
            return lx >= x && lx <= x + _element.Width &&
                   ly >= y && ly <= y + _element.Height;
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

        /// <summary>
        /// 按控点缩放：handle 0-7（0/2/4/6 四角，1/3/5/7 四边中点）。
        /// 固定对边/对角锚点不动，改 Left/Top/Width/Height。
        /// p 是鼠标位置（世界坐标）；本方法先把 p 反旋转到未旋转坐标系再缩放。
        /// </summary>
        public void Resize(int handle, Point p)
        {
            if (_element == null) return;
            double x = Canvas.GetLeft(_element);
            double y = Canvas.GetTop(_element);
            double w = _element.Width;
            double h = _element.Height;

            // 反旋转：把世界坐标点变换回未旋转坐标系
            if (Math.Abs(_angle) > 1e-6)
            {
                double cx = x + w / 2, cy = y + h / 2;
                double cos = Math.Cos(-_angle), sin = Math.Sin(-_angle);
                double dx = p.X - cx, dy = p.Y - cy;
                p = new Point(cx + dx * cos - dy * sin, cy + dx * sin + dy * cos);
            }

            switch (handle)
            {
                case 0: // 左上角：右下角固定
                    w = x + w - p.X; h = y + h - p.Y;
                    x = p.X; y = p.Y;
                    break;
                case 1: // 上边：下边固定
                    h = y + h - p.Y; y = p.Y;
                    break;
                case 2: // 右上角：左下角固定
                    w = p.X - x; h = y + h - p.Y;
                    y = p.Y;
                    break;
                case 3: // 右边：左边固定
                    w = p.X - x;
                    break;
                case 4: // 右下角：左上角固定
                    w = p.X - x; h = p.Y - y;
                    break;
                case 5: // 下边：上边固定
                    h = p.Y - y;
                    break;
                case 6: // 左下角：右上角固定
                    w = x + w - p.X; h = p.Y - y;
                    x = p.X;
                    break;
                case 7: // 左边：右边固定
                    w = x + w - p.X; x = p.X;
                    break;
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

        private void ApplyBrush(Rectangle rect)
        {
            var stroke = new SolidColorBrush(_color);
            stroke.Freeze();
            rect.Stroke = stroke;

            var fill = new SolidColorBrush(Color.FromArgb(FillAlpha, _color.R, _color.G, _color.B));
            fill.Freeze();
            rect.Fill = fill;
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
