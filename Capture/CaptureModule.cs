using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using WinKit.Common;

namespace WinKit.Capture
{
    /// <summary>
    /// 截图模块 — 独立于剪贴板与翻译之外的第三方功能模块。
    ///
    /// 主链路：全局热键 / 托盘入口 → 框选区域 → 自动把原图复制到系统剪贴板 → 打开标注编辑器。
    /// 「先复制」是刻意设计：即便用户标注完直接关窗，原图也已经在剪贴板里，不会空手而归。
    ///
    /// 与 Clipboard / Translate 之间没有任何直接依赖：Translate 只在做 OCR 时借用
    /// <see cref="ScreenshotWindow"/> 取一张图，本模块则自带截图热键，互不影响启停。
    /// </summary>
    public class CaptureModule : IDisposable
    {
        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

        private readonly SettingsManager _settingsManager;
        private HotkeyService _hotkey;

        private bool _busy;
        private bool _disposed;
        /// <summary>热键是否因设置中心正在录制而被临时挂起</summary>
        private bool _hotkeysSuspended;

        /// <summary>已注册的截图热键快照，用于判断设置变更后是否真需要重注册</summary>
        private HotkeyConfig? _appliedHotkey;

        /// <summary>热键注册失败（组合被其它程序占用等）。参数为热键显示名，供 UI 提示。</summary>
        public event EventHandler<string>? HotkeyRegistrationFailed;

        public CaptureModule(SettingsManager settingsManager)
        {
            _settingsManager = settingsManager ?? throw new ArgumentNullException(nameof(settingsManager));
            _hotkey = new HotkeyService();
            _settingsManager.SettingsChanged += OnSettingsChanged;
        }

        /// <summary>
        /// 注册截图热键（默认 Alt+A）。注册失败通常是被其它程序占用，会上报给 UI 层提示。
        /// </summary>
        public void Start()
        {
            _hotkeysSuspended = false;

            _hotkey.Dispose();
            _hotkey = new HotkeyService();

            // 只接管 HotkeyCatalog 里属于 Capture 的条目，显示名与设置中心保持同一来源
            foreach (var descriptor in HotkeyCatalog.All)
            {
                if (descriptor.Owner != HotkeyOwner.Capture) continue;

                var hk = descriptor.Get(_settingsManager.Settings);
                string label = descriptor.Label;

                if (!_hotkey.Register((uint)hk.VK, hk.Modifiers, () => RunAsync(CaptureInteractiveAsync)))
                {
                    System.Diagnostics.Debug.WriteLine($"CaptureModule: 热键注册失败 {label}");
                    HotkeyRegistrationFailed?.Invoke(this, label);
                }

                _appliedHotkey = hk;
                break;
            }
        }

        public void Stop() => _hotkey.Dispose();

        /// <summary>
        /// 临时注销截图热键 —— 设置中心进入快捷键录制时由 App 层调用。
        ///
        /// 必须挂起的原因：录制时若用户按下「当前已生效的组合」（如 Alt+A），
        /// Windows 会把 WM_HOTKEY 直接发给本模块，设置窗收不到这个键，
        /// 同时弹出的框选窗口会让设置窗失焦、录制被取消。表现就是「快捷键改不了」。
        /// </summary>
        public void SuspendHotkeys()
        {
            if (_hotkeysSuspended) return;
            _hotkeysSuspended = true;
            _hotkey.Dispose();
        }

        /// <summary>录制结束，按最新配置恢复热键</summary>
        public void ResumeHotkeys()
        {
            if (!_hotkeysSuspended) return;
            Start();
        }

        private void OnSettingsChanged(object? sender, AppSettings settings)
        {
            // 录制期间热键已整体注销，此处跳过——由 ResumeHotkeys 统一按最新配置注册
            if (_hotkeysSuspended) return;

            // 只有截图热键真的变了才重注册，避免改无关设置时热键抖动
            var hk = settings.HotkeyScreenshot;
            if (_appliedHotkey == null || hk == null
                || hk.VK != _appliedHotkey.VK || hk.Modifiers != _appliedHotkey.Modifiers)
            {
                Start();
            }
        }

        /// <summary>截图主流程：框选 →（按配置）复制原图到剪贴板 →（按配置）打开标注编辑器</summary>
        public async Task CaptureInteractiveAsync()
        {
            var region = await SelectRegionAsync().ConfigureAwait(true);
            if (region == null) return;   // 用户取消

            BitmapSource bitmap;
            using (var bmp = ScreenshotService.CaptureRegion(region.Value))
            {
                if (bmp == null) return;
                bitmap = ToBitmapSource(bmp);
            }

            var settings = _settingsManager.Settings;

            // 不进编辑器时若也不复制，这次截图等于白截 —— 因此强制至少复制一次
            bool needCopy = settings.CaptureAutoCopy || !settings.CaptureOpenEditor;
            bool copied = needCopy && CopyToClipboard(bitmap);

            if (settings.CaptureOpenEditor)
                ShowEditor(bitmap, region.Value, copied);
        }

        /// <summary>弹出全屏选区窗口，返回物理像素矩形（取消为 null）</summary>
        private static Task<System.Drawing.Rectangle?> SelectRegionAsync()
        {
            return Application.Current.Dispatcher.Invoke(() =>
            {
                var shot = new ScreenshotWindow();
                return shot.SelectRegionAsync();
            });
        }

        private static void ShowEditor(BitmapSource image, System.Drawing.Rectangle region, bool copied)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                var editor = new EditorWindow(image, region, copied);
                editor.Show();
            });
        }

        /// <summary>把位图写入系统剪贴板；失败（剪贴板被占用等）返回 false，不影响后续编辑</summary>
        private static bool CopyToClipboard(BitmapSource image)
        {
            try
            {
                Application.Current.Dispatcher.Invoke(() => System.Windows.Clipboard.SetImage(image));
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"截图复制到剪贴板失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>System.Drawing.Bitmap → 冻结的 BitmapSource（可跨线程安全使用）</summary>
        private static BitmapSource ToBitmapSource(System.Drawing.Bitmap bitmap)
        {
            var handle = bitmap.GetHbitmap();
            try
            {
                var source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                    handle, IntPtr.Zero, Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                return source;
            }
            finally
            {
                DeleteObject(handle);
            }
        }

        /// <summary>统一的异步入口，串行化并兜底异常（热键回调是 Action，必须吞掉异常）</summary>
        private async void RunAsync(Func<Task> task)
        {
            if (_busy) return;
            _busy = true;
            try
            {
                await task().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"截图模块异常: {ex}");
            }
            finally
            {
                _busy = false;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _settingsManager.SettingsChanged -= OnSettingsChanged; } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"CaptureModule.Dispose(unsub): {ex.Message}"); }
            try { _hotkey.Dispose(); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"CaptureModule.Dispose(hotkey): {ex.Message}"); }
        }
    }
}
