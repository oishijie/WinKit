using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace WinKit.Capture.Annotations
{
    /// <summary>画笔标注 —— 自由折线，逐点追加。</summary>
    public sealed class PenAnnotation : IAnnotation
    {
        private Polyline? _element;
        private Color _color;
        private double _thickness;

        public PenAnnotation(Color color, double thickness)
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

        public double Angle { get => 0; set { } }
        public bool SupportsResize => false;
        public bool SupportsRotation => false;

        public Rect Bounds
        {
            get
            {
                if (_element == null || _element.Points.Count == 0) return Rect.Empty;
                double minX = double.MaxValue, minY = double.MaxValue;
                double maxX = double.MinValue, maxY = double.MinValue;
                foreach (var pt in _element.Points)
                {
                    minX = Math.Min(minX, pt.X); maxX = Math.Max(maxX, pt.X);
                    minY = Math.Min(minY, pt.Y); maxY = Math.Max(maxY, pt.Y);
                }
                return new Rect(minX, minY, maxX - minX, maxY - minY);
            }
        }

        public void Resize(int handle, Point p) { }
        public void Rotate(double deltaRad) { }

        public FrameworkElement Visual => _element!;

        public FrameworkElement CreatePreview()
        {
            var poly = new Polyline
            {
                StrokeThickness = _thickness,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                IsHitTestVisible = true,
                Tag = this,
            };
            ApplyBrush(poly);
            _element = poly;
            return poly;
        }

        public void Update(FrameworkElement el, Point start, Point current)
        {
            // 首次更新把起点加进去，之后逐点追加
            var poly = (Polyline)el;
            if (poly.Points.Count == 0) poly.Points.Add(start);
            poly.Points.Add(current);
        }

        public bool Commit(FrameworkElement el)
        {
            return ((Polyline)el).Points.Count >= 2;
        }

        public bool HitTest(Point p)
        {
            if (_element == null) return false;
            var pts = _element.Points;
            for (int i = 1; i < pts.Count; i++)
            {
                if (DistanceToSegment(p, pts[i - 1], pts[i]) <= _thickness + 4)
                    return true;
            }
            return false;
        }

        public void SetSelected(bool selected)
        {
            if (_element == null) return;
            _element.StrokeThickness = selected ? _thickness + 1.5 : _thickness;
        }

        public void Translate(double dx, double dy)
        {
            if (_element == null) return;
            for (int i = 0; i < _element.Points.Count; i++)
            {
                _element.Points[i] = new Point(_element.Points[i].X + dx, _element.Points[i].Y + dy);
            }
        }

        public object Snapshot()
        {
            if (_element == null) return default!;
            return (_element.Points.Clone(), _color, _thickness);
        }

        public void Restore(object state)
        {
            if (_element == null) return;
            var (points, color, thickness) = ((PointCollection, Color, double))state;
            _element.Points = points;
            _color = color;
            _thickness = thickness;
            ApplyBrush(_element);
        }

        private void ApplyBrush(Polyline poly)
        {
            var stroke = new SolidColorBrush(_color);
            stroke.Freeze();
            poly.Stroke = stroke;
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
