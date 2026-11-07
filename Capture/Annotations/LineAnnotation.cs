using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;

namespace WinKit.Capture.Annotations
{
    /// <summary>
    /// 直线标注 —— 与 <see cref="ArrowAnnotation"/> 共用同一套
    /// 「点到线段距离命中 / 平移 / 旋转 / 快照」逻辑，几何里只少了末端两撇箭头。
    /// 描边色与线宽跟随主界面当前调色板和线宽三档。
    /// </summary>
    public sealed class LineAnnotation : IAnnotation
    {
        private System.Windows.Shapes.Path? _element;
        private Color _color;
        private double _thickness;
        private Point _start;
        private Point _end;
        private double _angle;

        public LineAnnotation(Color color, double thickness)
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

        public FrameworkElement Visual => _element!;

        public Rect Bounds
        {
            get
            {
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
            ((System.Windows.Shapes.Path)el).Data = new LineGeometry(start, current);
        }

        public bool Commit(FrameworkElement el) => (_end - _start).Length >= 4;

        public bool HitTest(Point p)
        {
            if (_element?.Data == null) return false;
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
            if (_element != null) _element.Data = new LineGeometry(_start, _end);
        }

        public void Resize(int handle, Point p) { }
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
                _element.StrokeThickness = thickness;
                _element.Data = new LineGeometry(_start, _end);
                _element.RenderTransform = new RotateTransform(angle);
            }
        }

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
