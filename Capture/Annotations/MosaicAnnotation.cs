using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace WinKit.Capture.Annotations
{
    /// <summary>
    /// 马赛克标注 —— 拖动期间用虚线框示意，松手才从底图裁块打码。
    /// 依赖底图（BitmapSource）与 DPI 缩放系数做物理像素换算，构造时传入。
    /// </summary>
    public sealed class MosaicAnnotation : IAnnotation
    {
        private const int MosaicBlock = 8;

        private readonly BitmapSource _image;
        private readonly double _scaleX;
        private readonly double _scaleY;

        private Rectangle? _dashPreview;   // 拖动中的虚线框
        private Image? _mosaicImage;       // 松手后生成的马赛克贴片
        private Color _color;
        private double _thickness;
        private Point _start;
        private Point _end;

        public MosaicAnnotation(BitmapSource image, double scaleX, double scaleY,
                                Color color, double thickness)
        {
            _image = image;
            _scaleX = scaleX;
            _scaleY = scaleY;
            _color = color;
            _thickness = thickness;
        }

        public Color Color
        {
            get => _color;
            set => _color = value;
        }

        public double Thickness
        {
            get => _thickness;
            set => _thickness = value;
        }

        public double Angle { get => 0; set { } }
        public bool SupportsResize => false;
        public bool SupportsRotation => false;

        public Rect Bounds
        {
            get
            {
                if (_mosaicImage == null) return Rect.Empty;
                return new Rect(
                    Canvas.GetLeft(_mosaicImage), Canvas.GetTop(_mosaicImage),
                    _mosaicImage.Width, _mosaicImage.Height);
            }
        }

        public void Resize(int handle, Point p) { }
        public void Rotate(double deltaRad) { }

        public FrameworkElement Visual => _mosaicImage!;

        public FrameworkElement CreatePreview()
        {
            _dashPreview = new Rectangle
            {
                Stroke = new SolidColorBrush(_color),
                StrokeThickness = 1.5,
                StrokeDashArray = new DoubleCollection { 4, 3 },
                IsHitTestVisible = true,
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

        public bool Commit(FrameworkElement el)
        {
            // 松手后由 EditorWindow 负责：移除虚线框、生成马赛克贴片并调用 SetMosaicImage
            return el.Width >= 3 && el.Height >= 3;
        }

        /// <summary>松手后由宿主调用，把虚线框替换成真正的马赛克贴片。太小则返回 false。</summary>
        public bool BuildMosaic()
        {
            var rect = Normalize(_start, _end);
            if (rect.Width < 3 || rect.Height < 3) return false;
            var img = CreateMosaicImage(rect);
            if (img == null) return false;
            _mosaicImage = img;
            return true;
        }

        public bool HitTest(Point p)
        {
            if (_mosaicImage == null) return false;
            var x = Canvas.GetLeft(_mosaicImage);
            var y = Canvas.GetTop(_mosaicImage);
            return p.X >= x && p.X <= x + _mosaicImage.Width &&
                   p.Y >= y && p.Y <= y + _mosaicImage.Height;
        }

        public void SetSelected(bool selected)
        {
            // 马赛克无描边，选中态暂不加粗；留待后续加外框
        }

        public void Translate(double dx, double dy)
        {
            if (_mosaicImage == null) return;
            Canvas.SetLeft(_mosaicImage, Canvas.GetLeft(_mosaicImage) + dx);
            Canvas.SetTop(_mosaicImage, Canvas.GetTop(_mosaicImage) + dy);
        }

        public object Snapshot()
        {
            if (_mosaicImage == null) return default!;
            return (Canvas.GetLeft(_mosaicImage), Canvas.GetTop(_mosaicImage),
                    _mosaicImage.Width, _mosaicImage.Height);
        }

        public void Restore(object state)
        {
            if (_mosaicImage == null) return;
            var (x, y, w, h) = ((double, double, double, double))state;
            Canvas.SetLeft(_mosaicImage, x);
            Canvas.SetTop(_mosaicImage, y);
            _mosaicImage.Width = w;
            _mosaicImage.Height = h;
        }

        /// <summary>从底图裁出该区域 → 缩到极小 → 最近邻放大成硬块。</summary>
        private Image? CreateMosaicImage(Rect rect)
        {
            int px = (int)Math.Round(rect.X * _scaleX);
            int py = (int)Math.Round(rect.Y * _scaleY);
            int pw = (int)Math.Round(rect.Width * _scaleX);
            int ph = (int)Math.Round(rect.Height * _scaleY);

            px = Math.Max(0, Math.Min(px, _image.PixelWidth - 1));
            py = Math.Max(0, Math.Min(py, _image.PixelHeight - 1));
            pw = Math.Max(1, Math.Min(pw, _image.PixelWidth - px));
            ph = Math.Max(1, Math.Min(ph, _image.PixelHeight - py));

            if (pw <= 0 || ph <= 0) return null;

            try
            {
                var cropped = new CroppedBitmap(_image, new Int32Rect(px, py, pw, ph));

                double sx = Math.Min(1.0, (double)MosaicBlock / pw);
                double sy = Math.Min(1.0, (double)MosaicBlock / ph);

                var small = new TransformedBitmap(cropped, new ScaleTransform(sx, sy));
                small.Freeze();

                var img = new Image
                {
                    Source = small,
                    Width = rect.Width,
                    Height = rect.Height,
                    Stretch = Stretch.Fill,
                    IsHitTestVisible = true,
                    Tag = this,
                };
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.NearestNeighbor);

                Canvas.SetLeft(img, rect.X);
                Canvas.SetTop(img, rect.Y);
                return img;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"马赛克生成失败: {ex.Message}");
                return null;
            }
        }

        private static Rect Normalize(Point a, Point b) => new(
            Math.Min(a.X, b.X),
            Math.Min(a.Y, b.Y),
            Math.Abs(a.X - b.X),
            Math.Abs(a.Y - b.Y));
    }
}
