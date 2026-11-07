using System;
using System.Linq;
using System.Windows.Media.Imaging;
using WinKit.Clipboard.Models;
using WinKit.Clipboard.Services;
using WinKit.Common;
using WinKit.Todo.Services;

namespace WinKit.Rack
{
    /// <summary>
    /// 晾衣架模块 —— 屏幕顶端的置顶绳子，把图片/文本/待办挂上去。
    ///
    /// 设计（借鉴 SnowAir-Rack）：模块是「被动接收」内容的容器，
    /// 由截图/剪贴板/待办等外部主动 Hang 内容进来，而不是自己轮询拉取。
    /// 数据源复用：待办 = MarkdownStorage，剪贴板 = ClipboardManager，截图 = BitmapSource。
    ///
    /// MVP 版不做持久化，重启后晾衣架清空。
    /// </summary>
    public class RackModule : IDisposable
    {
        private readonly SettingsManager _settingsManager;
        private readonly ClipboardManager? _clipboardManager;
        private RackWindow? _window;
        private bool _disposed;

        public RackModule(SettingsManager settingsManager, ClipboardManager? clipboardManager = null)
        {
            _settingsManager = settingsManager ?? throw new ArgumentNullException(nameof(settingsManager));
            _clipboardManager = clipboardManager;
        }

        /// <summary>显示 / 隐藏晾衣架。</summary>
        public void Toggle()
        {
            EnsureWindow();
            if (_window!.IsVisible) _window.Hide();
            else _window.Show();
        }

        /// <summary>挂一张图片（截图 / 贴图）到绳子上。</summary>
        public void HangImage(BitmapSource image)
        {
            if (image == null) return;
            EnsureWindow();
            _window!.Hang(RackItem.ImageItem(image));
            _window.Show();
        }

        /// <summary>挂一段文本到绳子上。</summary>
        public void HangText(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            EnsureWindow();
            _window!.Hang(RackItem.TextItem(text));
            _window.Show();
        }

        /// <summary>挂一条待办到绳子上。</summary>
        public void HangTodo(string title, bool isDone)
        {
            if (string.IsNullOrEmpty(title)) return;
            EnsureWindow();
            _window!.Hang(RackItem.TodoItem(title, isDone));
            _window.Show();
        }

        /// <summary>
        /// 从数据源批量加载内容挂到绳子上：待办（未完成）+ 剪贴板历史（文本 + 图片，各取最近若干条）。
        /// 由 App 启动时或用户手动触发调用。
        /// </summary>
        public void LoadFromSources(int maxClipboardItems = 5)
        {
            EnsureWindow();
            _window!.Clear();

            // 待办：未完成的条目
            try
            {
                var todos = new MarkdownStorage().LoadTodos();
                foreach (var t in todos.Where(x => !x.IsDone).Take(8))
                    _window.Hang(RackItem.TodoItem(t.Title, false));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Rack 加载待办失败: {ex.Message}");
            }

            // 剪贴板：文本 + 图片（最近的若干条）
            if (_clipboardManager != null)
            {
                try
                {
                    var texts = _clipboardManager.Items
                        .Where(i => i.Type == ClipboardItemType.Text && !string.IsNullOrEmpty(i.Content))
                        .Take(maxClipboardItems);
                    foreach (var t in texts)
                        _window.Hang(RackItem.TextItem(t.Content!));

                    var images = _clipboardManager.Items
                        .Where(i => i.Type == ClipboardItemType.Image)
                        .Take(maxClipboardItems);
                    foreach (var img in images)
                    {
                        var path = img.ImagePath;
                        if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) continue;
                        try
                        {
                            var bmp = new BitmapImage(new Uri(path));
                            bmp.Freeze();
                            _window.Hang(RackItem.ImageItem(bmp));
                        }
                        catch { /* 单个图片解码失败跳过 */ }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Rack 加载剪贴板失败: {ex.Message}");
                }
            }
        }

        private void EnsureWindow()
        {
            if (_window != null) return;
            _window = new RackWindow();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _window?.Close(); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"RackModule.Dispose: {ex.Message}"); }
        }
    }
}
