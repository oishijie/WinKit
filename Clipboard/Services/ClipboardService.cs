using System;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace WinKit.Clipboard.Services
{
    /// <summary>
    /// 剪切板监控服务 — 监控文本、图片和文件变更，后台低占用
    /// </summary>
    public class ClipboardService : IDisposable
    {
        private DispatcherTimer? _timer;
        private string? _lastTextContent;
        private string? _lastImageHash;
        private string? _lastFileList;
        private bool _isMonitoring;
        private int _pollInterval = 800; // ms

        public event EventHandler<string>? TextChanged;
        public event EventHandler<BitmapSource>? ImageChanged;

        /// <summary>复制文件（FileDropList）时触发，参数为文件完整路径数组（单文件场景）</summary>
        public event EventHandler<string[]>? FileChanged;

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
                        _lastFileList = null;
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
                            _lastFileList = null;
                            ImageChanged?.Invoke(this, image);
                        }
                    }
                    return;
                }

                // 最后检查文件（FileDropList）
                if (System.Windows.Clipboard.ContainsFileDropList())
                {
                    var files = System.Windows.Clipboard.GetFileDropList();
                    if (files != null && files.Count > 0)
                    {
                        // 用"数量+首文件+末文件"做轻量去重，避免字符串拼接开销
                        var key = $"{files.Count}|{files[0]}|{files[files.Count - 1]}";
                        if (key != _lastFileList)
                        {
                            _lastFileList = key;
                            _lastTextContent = null;
                            _lastImageHash = null;
                            var arr = new string[files.Count];
                            files.CopyTo(arr, 0);
                            FileChanged?.Invoke(this, arr);
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
