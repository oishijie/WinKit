using System;
using System.Collections.Specialized;
using System.Windows.Media.Imaging;
using WinKit.Clipboard.Models;
using WinKit.Clipboard.Services;
using WinKit.Common;

namespace WinKit.Rack
{
    /// <summary>
    /// 晾衣架模块 —— 屏幕顶端的置顶横杆。
    ///
    /// 语义对齐 SnowAir-Rack：模块是被动接收内容的容器，**只有截图会自动上绳**
    /// （Plugin.cpp 的 OnCapture）。左信封＝剪贴板合并池、右篮子＝待办合并池，
    /// 两边都只是「池」，内容按指纹 / 标题合并去重；用户点「上绳」才生成卡片。
    /// 早期版本把未完成待办和最近剪贴板逐条挂到绳子上的 LoadFromSources() 已删除。
    ///
    /// 唯一的例外是用户主动打开「复制文本自动上绳」设置，此时才监听
    /// ClipboardManager 的新增文本并直接 HangText。
    /// </summary>
    public class RackModule : IDisposable
    {
        private readonly SettingsManager _settingsManager;
        private readonly ClipboardManager? _clipboardManager;
        private RackWindow? _window;
        private bool _disposed;

        /// <summary>晾衣架是否处于展开状态（托盘菜单据此显示「收起 / 展开」）。
        /// 不能用 Window.IsVisible：自动收起后窗口仍以 8px 热区存在，那时对外也算「已收起」。</summary>
        public bool IsActive => _window?.IsExpanded == true;

        public RackModule(SettingsManager settingsManager, ClipboardManager? clipboardManager = null)
        {
            _settingsManager = settingsManager ?? throw new ArgumentNullException(nameof(settingsManager));
            _clipboardManager = clipboardManager;
            _settingsManager.SettingsChanged += OnSettingsChanged;
            if (_clipboardManager != null) _clipboardManager.ItemsChanged += OnClipboardItemsChanged;
        }

        /// <summary>显示 / 隐藏晾衣架。首次显示时才创建窗口并恢复已落盘的卡片。</summary>
        public void Toggle()
        {
            EnsureWindow();
            if (_window!.IsExpanded)
            {
                _window.Dismiss();
                return;
            }
            _window.Reveal();
        }

        /// <summary>挂一张图片（截图 / 贴图）。</summary>
        public void HangImage(BitmapSource image)
        {
            if (image == null) return;
            HangCore(RackItem.ImageItem(image));
        }

        /// <summary>挂一段文本（信封面板「上绳」/ 手动调用）。</summary>
        public void HangText(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            HangCore(RackItem.FromClipboardText(text));
        }

        /// <summary>挂一条待办。</summary>
        public void HangTodo(string title, bool isDone)
        {
            if (string.IsNullOrEmpty(title)) return;
            HangCore(RackItem.TodoItem(title, isDone));
        }

        public void HangNote(string text, string? color = null) =>
            HangCore(RackItem.NoteItem(text, color));

        public void HangLink(string url) => HangCore(RackItem.LinkItem(url));

        public void HangColor(string hex) => HangCore(RackItem.ColorItem(hex));

        public void HangFile(string path) => HangCore(RackItem.FileItem(path));

        /// <summary>手动挂载的统一入口：挂完立刻展开显示，卡片不会被自动收起带走。</summary>
        private void HangCore(RackItem item)
        {
            EnsureWindow();
            _window!.Hang(item);
            _window.Reveal();
        }

        /// <summary>设置页「清空晾衣架」：只清绳上的卡片，两个合并池的数据不动。</summary>
        public void ClearAll()
        {
            _window?.Clear();
            RackItemStore.ClearAll();
        }

        /// <summary>
        /// 默认关。开启后每次复制新文本都会生成一张卡；关闭时文本只合并进信封。
        /// 事件可能来自后台加载线程，统一切回 UI 线程再动窗口。
        /// </summary>
        private void OnClipboardItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (_disposed || !_settingsManager.Settings.RackAutoHangText) return;
            if (e.Action != NotifyCollectionChangedAction.Add || e.NewItems == null) return;

            var app = System.Windows.Application.Current;
            if (app == null) return;
            if (!app.Dispatcher.CheckAccess())
            {
                app.Dispatcher.BeginInvoke(new Action(() => OnClipboardItemsChanged(sender, e)));
                return;
            }

            foreach (var obj in e.NewItems)
            {
                if (obj is ClipboardItem { Type: ClipboardItemType.Text } item &&
                    !string.IsNullOrWhiteSpace(item.Content))
                    HangText(item.Content!);
            }
        }

        private void EnsureWindow()
        {
            if (_window != null) return;
            _window = new RackWindow();
            _window.SetClipboardSource(_clipboardManager);
            _window.ApplySettings(_settingsManager.Settings);
            _window.LoadPersisted();
        }

        private void OnSettingsChanged(object? sender, AppSettings settings)
        {
            if (_disposed) return;
            // 设置页改一项就即时生效；窗口尚未创建时不用管，EnsureWindow 会读最新配置。
            _window?.ApplySettings(settings);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _settingsManager.SettingsChanged -= OnSettingsChanged;
            if (_clipboardManager != null) _clipboardManager.ItemsChanged -= OnClipboardItemsChanged;
            try { _window?.Close(); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"RackModule.Dispose: {ex.Message}"); }
        }
    }
}
