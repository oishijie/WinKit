using System;
using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WinKit.Clipboard.Models
{
    /// <summary>
    /// 将图片文件路径转换为 ImageSource（供列表缩略图绑定）。
    /// 使用 CacheOption.OnLoad 立即加载并释放文件句柄，避免锁定。
    /// </summary>
    public class ImagePathToSourceConverter : IValueConverter
    {
        public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var path = value as string;
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return null;

            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad; // 立即加载，释放文件句柄
                bitmap.UriSource = new Uri(path, UriKind.Absolute);
                bitmap.DecodePixelWidth = 160; // 缩略图不需要原尺寸
                bitmap.EndInit();
                bitmap.Freeze(); // 跨线程安全
                return bitmap;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ImageThumb 加载失败: {ex.Message}");
                return null;
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
