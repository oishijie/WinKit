using System;
using System.Drawing;
using System.Drawing.Imaging;

namespace WinKit.Translate.Services
{
    /// <summary>
    /// 屏幕截图服务 — 按物理像素矩形区域捕获屏幕。
    ///
    /// 本地 OCR 直接吃位图，因此不再做 PNG 编解码往返：
    /// 截图后原样交给识别引擎，省掉一次编码和一次解码。
    /// </summary>
    public static class ScreenshotService
    {
        /// <summary>
        /// 捕获指定矩形区域 (物理像素坐标)。调用方负责释放返回的位图。
        /// 区域无效时返回 null。
        /// </summary>
        public static Bitmap? CaptureRegion(Rectangle region)
        {
            var normalized = Normalize(region);
            if (normalized.Width <= 0 || normalized.Height <= 0)
                return null;

            var bmp = new Bitmap(normalized.Width, normalized.Height, PixelFormat.Format32bppArgb);
            try
            {
                using var g = Graphics.FromImage(bmp);
                g.CopyFromScreen(normalized.X, normalized.Y, 0, 0, normalized.Size, CopyPixelOperation.SourceCopy);
                return bmp;
            }
            catch
            {
                bmp.Dispose();
                throw;
            }
        }

        /// <summary>规范化矩形，避免宽高为负</summary>
        private static Rectangle Normalize(Rectangle region) => new(
            region.Width < 0 ? region.X + region.Width : region.X,
            region.Height < 0 ? region.Y + region.Height : region.Y,
            Math.Abs(region.Width),
            Math.Abs(region.Height));
    }
}
