using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace WinKit.Capture.Annotations
{
    /// <summary>箭头标注 —— 主干 + 两撇箭头，圆角端点。支持旋转。</summary>
    public sealed class ArrowAnnotation : IAnnotation
    {
        private System.Windows.Shapes.Path? _element;
        private Color _color;
        private double _thickness;
        private Point _start;
        private Point _end;
        private double _angle;

        public ArrowAnnotation(Color color, double thickness)
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

        public bool SupportsResize => false;
        public bool SupportsRotation => true;

        public Rect Bounds
        {
            get
            {
                // 箭头的轴对齐包围盒（含旋转）
                if (_element == null) return Rect.Empty;
                double minX = Math.Min(_start.X, _end.X);
                double maxX = Math.Max(_start.X, _end.X);
                double minY = Math.Min(_start.Y, _end.Y);
                double maxY = Math.Max(_start.Y, _end.Y);
                var r = new Rect(minX, minY, maxX - minX, maxY - minY);
                if (Math.Abs(_angle) < 1e-6) return r;
                double cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
                double cos = Math.Cos(_angle), sin = Math.Sin(_angle);
                var corners = new[]
                {
                    Rotate(r.Left, r.Top, cx, cy, cos, sin),
                    Rotate(r.Right, r.Top, cx, cy, cos, sin),
                    Rotate(r.Right, r.Bottom, cx, cy, cos, sin),
                    Rotate(r.Left, r.Bottom, cx, cy, cos, sin),
                };
                double bx0 = double.MaxValue, by0 = double.MaxValue;
                double bx1 = double.MinValue, by1 = double.MinValue;
                foreach (var c in corners)
                {
                    bx0 = Math.Min(bx0, c.X); bx1 = Math.Max(bx1, c.X);
                    by0 = Math.Min(by0, c.Y); by1 = Math.Max(by1, c.Y);
                }
                return new Rect(bx0, by0, bx1 - bx0, by1 - by0);
            }
        }

        public FrameworkElement Visual => _element!;

        public FrameworkElement CreatePreview()
        {
            var path = new System.Windows.Shapes.Path
            {
                StrokeThickness = _thickness,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                IsHitTestVisible = true,
                Tag = this,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new RotateTransform(_angle),
            };
            ApplyBrush(path);
            _element = path;
            return path;
        }

        public void Update(FrameworkElement el, Point start, Point current)
        {
            _start = start;
            _end = current;
            ((System.Windows.Shapes.Path)el).Data = BuildArrowGeometry(start, current);
        }

        public bool Commit(FrameworkElement el)
        {
            return (_end - _start).Length >= 4;
        }

        public bool HitTest(Point p)
        {
            if (_element?.Data == null) return false;
            // 旋转后把点反变换回未旋转坐标，再做点到线段距离判定
            var local = InverseRotate(p);
            return DistanceToSegment(local, _start, _end) <= _thickness + 4;
        }

        public void SetSelected(bool selected)
        {
            if (_element == null) return;
            _element.StrokeThickness = selected ? _thickness + 1.5 : _thickness;
        }

        public void Translate(double dx, double dy)
        {
            _start = new Point(_start.X + dx, _start.Y + dy);
            _end = new Point(_end.X + dx, _end.Y + dy);
            if (_element != null) _element.Data = BuildArrowGeometry(_start, _end);
        }

        public void Resize(int handle, Point p)
        {
            // 箭头不支持缩放（能力协商 SuppportsResize=false，不会被调用）
        }

        public void Rotate(double deltaRad) => Angle = _angle + deltaRad;

        public object Snapshot() => (_start, _end, _color, _thickness, _angle);

        public void Restore(object state)
        {
            var (start, end, color, thickness, angle) = ((Point, Point, Color, double, double))state;
            _start = start;
            _end = end;
            _color = color;
            _thickness = thickness;
            _angle = angle;
            if (_element != null)
            {
                ApplyBrush(_element);
                _element.Data = BuildArrowGeometry(_start, _end);
                _element.RenderTransform = new RotateTransform(angle);
            }
        }

        /// <summary>把点从世界坐标反旋转到箭头的未旋转坐标（绕起点终点中点）。</summary>
        private Point InverseRotate(Point p)
        {
            if (Math.Abs(_angle) < 1e-6) return p;
            double cx = (_start.X + _end.X) / 2, cy = (_start.Y + _end.Y) / 2;
            double cos = Math.Cos(-_angle), sin = Math.Sin(-_angle);
            double dx = p.X - cx, dy = p.Y - cy;
            return new Point(cx + dx * cos - dy * sin, cy + dx * sin + dy * cos);
        }

        private static Point Rotate(double x, double y, double cx, double cy, double cos, double sin)
            => new(cx + (x - cx) * cos - (y - cy) * sin, cy + (x - cx) * sin + (y - cy) * cos);

        private void ApplyBrush(System.Windows.Shapes.Path path)
        {
            var stroke = new SolidColorBrush(_color);
            stroke.Freeze();
            path.Stroke = stroke;
        }

        private static Geometry BuildArrowGeometry(Point start, Point end)
        {
            var group = new GeometryGroup();
            group.Children.Add(new LineGeometry(start, end));

            double angle = Math.Atan2(end.Y - start.Y, end.X - start.X);
            const double headLen = 14.0;
            const double spread = Math.PI / 7;

            var wing1 = new Point(
                end.X - headLen * Math.Cos(angle - spread),
                end.Y - headLen * Math.Sin(angle - spread));
            var wing2 = new Point(
                end.X - headLen * Math.Cos(angle + spread),
                end.Y - headLen * Math.Sin(angle + spread));

            group.Children.Add(new LineGeometry(end, wing1));
            group.Children.Add(new LineGeometry(end, wing2));
            return group;
        }

        private static double DistanceToSegment(Point p, Point a, Point b)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y;
            double lenSq = dx * dx + dy * dy;
            if (lenSq == 0) return (p - a).Length;
            double t = Math.Max(0, Math.Min(1, ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / lenSq));
            var proj = new Point(a.X + t * dx, a.Y + t * dy);
            return (p - proj).Length;
        }
    }
}
