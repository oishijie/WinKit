using System;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace WinKit.Clipboard.Services
{
    /// <summary>
    /// 剪切板监控服务 — 监控文本和图片变更，后台低占用
    /// </summary>
    public class ClipboardService : IDisposable
    {
        private DispatcherTimer? _timer;
        private string? _lastTextContent;
        private string? _lastImageHash;
        private bool _isMonitoring;
        private int _pollInterval = 800; // ms

        public event EventHandler<string>? TextChanged;
        public event EventHandler<BitmapSource>? ImageChanged;

        public void StartMonitoring()
        {
            if (_isMonitoring) return;
            _isMonitoring = true;

            _timer = new DispatcherTimer(
                TimeSpan.FromMilliseconds(_pollInterval),
                DispatcherPriority.Background, // 低优先级，不阻塞 UI 线程
                OnTimerTick,
                Dispatcher.CurrentDispatcher);
            _timer.Start();
        }

        public void StopMonitoring()
        {
            _isMonitoring = false;
            _timer?.Stop();
            _timer = null;
        }

        private void OnTimerTick(object? sender, EventArgs e)
        {
            try
            {
                // 优先检查文本
                if (System.Windows.Clipboard.ContainsText())
                {
                    var text = System.Windows.Clipboard.GetText();
                    if (!string.IsNullOrEmpty(text) && text != _lastTextContent)
                    {
                        _lastTextContent = text;
                        _lastImageHash = null; // 新文本覆盖旧图片记录
                        TextChanged?.Invoke(this, text);
                    }
                    return;
                }

                // 再检查图片
                if (System.Windows.Clipboard.ContainsImage())
                {
                    var image = System.Windows.Clipboard.GetImage();
                    if (image != null)
                    {
                        // 用像素尺寸做快速去重（避免每次 tick 都编码整个位图）
                        var hash = $"{image.PixelWidth}x{image.PixelHeight}";
                        if (hash != _lastImageHash)
                        {
                            _lastImageHash = hash;
                            _lastTextContent = null; // 新图片覆盖旧文本记录
                            ImageChanged?.Invoke(this, image);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // 剪切板可能被独占，静默忽略
                System.Diagnostics.Debug.WriteLine($"ClipboardService: {ex.Message}");
            }
        }

        public void Dispose()
        {
            StopMonitoring();
        }
    }
}
