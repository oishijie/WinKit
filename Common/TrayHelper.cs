using System;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using SWF = System.Windows.Forms;
using WinKit.Common;

namespace WinKit.Common
{
    /// <summary>
    /// 临时用于获取焦点的隐形辅助窗口，以实现 WPF ContextMenu 完美的失焦自动关闭机制
    /// </summary>
    internal class MenuHostWindow : Window
    {
        public MenuHostWindow()
        {
            Width = 0;
            Height = 0;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = System.Windows.Media.Brushes.Transparent;
            ShowInTaskbar = false;
            Topmost = true;
            // 移到可视范围之外
            Left = -10000;
            Top = -10000;
        }
    }

    /// <summary>
    /// 统一的系统托盘服务 — 基于 WPF ContextMenu 全自研，彻底匹配系统风格并完美支持失焦隐藏。
    /// 菜单保持精简：只保留最高频入口（待办显隐、剪贴板历史、设置、退出），
    /// 其余开关与参数统一收敛到设置中心，避免托盘菜单过长。
    /// </summary>
    public class TrayHelper : IDisposable
    {
        private readonly SWF.NotifyIcon _icon;
        private readonly Window _todoWindow;
        private readonly Window _pasteWindow;
        private readonly System.Windows.Application _app;
        private readonly SettingsManager _settingsManager;

        // 隐形焦点宿主窗口
        private readonly MenuHostWindow _menuHostWindow;

        // 设置中心（含「关于」分区，复用单一窗体实例）
        private readonly SettingsWindow _settingsWindow;

        // WPF ContextMenu 容器
        private readonly ContextMenu _contextMenu;

        // ── 精简后的菜单项 ─────────────────────────────
        private readonly MenuItem _itemCapture;    // 截图
        private readonly MenuItem _itemTodoSwitch; // 显示 / 隐藏待办（文字随窗口状态动态切换）
        private readonly MenuItem _itemPaste;      // 剪贴板历史
        private readonly MenuItem _itemSettings;   // 设置
        private readonly MenuItem _itemExit;       // 退出

        // 双击/单击计时器
        private readonly SWF.Timer _clickTimer;

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        public TrayHelper(System.Windows.Application app, SettingsManager settingsManager, Window todoWindow, Window pasteWindow, SettingsWindow settingsWindow)
        {
            _app = app;
            _settingsManager = settingsManager;
            _todoWindow = todoWindow;
            _pasteWindow = pasteWindow;
            _settingsWindow = settingsWindow ?? throw new ArgumentNullException(nameof(settingsWindow));

            // ── 1. 创建隐形焦点宿主窗口 ───────────────
            _menuHostWindow = new MenuHostWindow();
            _menuHostWindow.Show();
            _menuHostWindow.Hide(); // 触发句柄创建并在后台待命

            // ── 2. 创建 WPF ContextMenu ───────────────────────
            _contextMenu = new ContextMenu();

            // ── 3. 截图 ──────────────────────────────────────
            _itemCapture = new MenuItem { Header = "截图" };
            _itemCapture.Click += (s, e) => StartCapture();

            // ── 3.5 显示 / 隐藏待办 ──────────────────────────
            _itemTodoSwitch = new MenuItem { Header = "显示待办" };
            _itemTodoSwitch.Click += (s, e) => ToggleTodoWindow();

            // ── 4. 剪贴板历史 ────────────────────────────────
            _itemPaste = new MenuItem { Header = "剪贴板历史" };
            _itemPaste.Click += (s, e) => ShowPasteWindow();

            // ── 5. 设置 ──────────────────────────────────────
            _itemSettings = new MenuItem { Header = "设置" };
            _itemSettings.Click += (s, e) => _settingsWindow.ShowSection();

            // ── 6. 退出 ──────────────────────────────────────
            _itemExit = new MenuItem { Header = "退出" };
            _itemExit.Click += (s, e) => ShutdownApp();

            // ── 7. 上下文菜单组装 ──────────────────────────────
            _contextMenu.Items.Add(_itemCapture);
            _contextMenu.Items.Add(_itemTodoSwitch);
            _contextMenu.Items.Add(_itemPaste);
            _contextMenu.Items.Add(new Separator());
            _contextMenu.Items.Add(_itemSettings);
            _contextMenu.Items.Add(new Separator());
            _contextMenu.Items.Add(_itemExit);

            // ── 8. 事件监听联动：失焦自动隐藏菜单 ──────────────────
            _menuHostWindow.Deactivated += (s, e) =>
            {
                // 当隐形宿主窗口失去焦点时，主动收回菜单
                _contextMenu.IsOpen = false;
                _menuHostWindow.Hide();
            };

            _contextMenu.Closed += (s, e) =>
            {
                // 菜单完全闭合时隐藏辅助窗口
                _menuHostWindow.Hide();
            };

            // ── 9. 创建托盘图标 ────────────────────────────────
            var asm = Assembly.GetExecutingAssembly();
            var iconStream = asm.GetManifestResourceStream("WinKit.PTD.ico");
            int smallWidth = (int)SystemParameters.SmallIconWidth;
            int smallHeight = (int)SystemParameters.SmallIconHeight;
            int targetWidth = smallWidth <= 16 ? 32 : (smallWidth <= 24 ? 32 : 48);
            int targetHeight = smallHeight <= 16 ? 32 : (smallHeight <= 24 ? 32 : 48);
            var trayIcon = iconStream != null ? new Icon(iconStream, new System.Drawing.Size(targetWidth, targetHeight)) : SystemIcons.Application;
            var version = asm.GetName().Version?.ToString(3) ?? "1.0.0";

            _icon = new SWF.NotifyIcon
            {
                Icon = trayIcon,
                Text = $"WinKit v{version}",
                Visible = true
            };

            // ── 10. 单击判定定时器 (处理左键单击/双击) ─────────────
            _clickTimer = new SWF.Timer();
            _clickTimer.Interval = 200;
            _clickTimer.Tick += (s, e) =>
            {
                _clickTimer.Stop();
                _todoWindow.Dispatcher.Invoke(() =>
                {
                    if (_todoWindow.IsVisible)
                    {
                        _todoWindow.Hide();
                    }
                    else
                    {
                        _todoWindow.Show();
                        _todoWindow.Activate();
                    }
                });
            };

            _icon.MouseClick += (s, e) =>
            {
                if (e.Button == SWF.MouseButtons.Left)
                {
                    _clickTimer.Start();
                }
                else if (e.Button == SWF.MouseButtons.Right)
                {
                    // 右键点击：弹出 WPF 自研 ContextMenu 菜单
                    _todoWindow.Dispatcher.Invoke(() =>
                    {
                        SyncMenuStates();

                        POINT mousePos;
                        GetCursorPos(out mousePos);

                        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(_todoWindow);

                        // 强行展现隐形宿主窗口并激活到系统前台，确保失焦响应在任何全局环境下都绝对生效
                        _menuHostWindow.Show();
                        _menuHostWindow.Activate();
                        var hwnd = new WindowInteropHelper(_menuHostWindow).Handle;
                        SetForegroundWindow(hwnd);

                        // 弹出菜单，绑定到隐形宿主上
                        _contextMenu.PlacementTarget = _menuHostWindow;
                        _contextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.AbsolutePoint;
                        _contextMenu.HorizontalOffset = mousePos.X / dpi.DpiScaleX;
                        _contextMenu.VerticalOffset = mousePos.Y / dpi.DpiScaleY - 2;

                        _contextMenu.IsOpen = true;
                    });
                }
            };

            _icon.MouseDoubleClick += (s, e) =>
            {
                if (e.Button == SWF.MouseButtons.Left)
                {
                    _clickTimer.Stop();
                    _todoWindow.Dispatcher.Invoke(() =>
                    {
                        if (_todoWindow.IsVisible) _todoWindow.Hide();
                        else { _todoWindow.Show(); _todoWindow.Activate(); }
                    });
                }
            };

            SyncMenuStates();

            // 初始化应用已保存的不透明度
            ApplyOpacity(_settingsManager.Settings.WindowOpacity);

            // 订阅配置变更：设置窗「改一项存一项」时不透明度即时热更新到主窗口背板
            _settingsManager.SettingsChanged += OnSettingsChanged;
        }

        /// <summary>配置变更时即时把不透明度应用到两个主窗口背板（设置窗拖动滑块时实时生效）</summary>
        private void OnSettingsChanged(object? sender, AppSettings settings)
        {
            ApplyOpacity(settings.WindowOpacity);
        }

        /// <summary>同步菜单项状态：待办显隐文字、剪贴板入口可用性</summary>
        private void SyncMenuStates()
        {
            _itemTodoSwitch.Header = _todoWindow.IsVisible ? "隐藏待办" : "显示待办";
            _itemPaste.IsEnabled = _settingsManager.Settings.PasteEnableMonitoring;
        }

        // ── 不透明度操作 ────────────────────────────────────────
        private void ApplyOpacity(int opacity)
        {
            // 计算 Alpha 通道值 (0-255) 并转换为 #AARRGGBB 格式
            int alpha = (int)Math.Round(opacity / 100.0 * 255);
            string hex = $"#{alpha:X2}FFFFFF";
            var color = (System.Windows.Media.Color)
                System.Windows.Media.ColorConverter.ConvertFromString(hex);
            var brush = new System.Windows.Media.SolidColorBrush(color);

            // 直接设置两个主窗口的 RootBorder.Background
            _todoWindow.Dispatcher.Invoke(() =>
            {
                var border = ((System.Windows.FrameworkElement)_todoWindow.Content)
                             as System.Windows.Controls.Border
                             ?? _todoWindow.FindName("RootBorder") as System.Windows.Controls.Border;
                if (border != null) border.Background = brush;
            });
            _pasteWindow.Dispatcher.Invoke(() =>
            {
                var border = _pasteWindow.FindName("RootBorder") as System.Windows.Controls.Border;
                if (border != null) border.Background = brush;
            });
        }

        /// <summary>
        /// 走截图流程。先稍等一下再截屏 —— 托盘菜单的浮层此刻还没收起，
        /// 立刻截会把菜单本身截进底图。
        /// </summary>
        private async void StartCapture()
        {
            var capture = (_app as App)?.CaptureModule;
            if (capture == null) return;

            await Task.Delay(150);
            await capture.CaptureInteractiveAsync();
        }

        /// <summary>切换待办窗口的显示 / 隐藏</summary>
        /// <summary>
        /// 切换待办窗口显隐（内部会同步菜单文字）。
        /// 由托盘菜单「显示待办」与待办唤出键共用 —— 两条入口指向同一个实现，行为不会跑偏。
        /// </summary>
        public void ToggleTodoWindow()
        {
            _todoWindow.Dispatcher.Invoke(() =>
            {
                if (_todoWindow.IsVisible)
                {
                    _todoWindow.Hide();
                }
                else
                {
                    _todoWindow.Show();
                    _todoWindow.Activate();
                }
                SyncMenuStates();
            });
        }

        private void ShowPasteWindow()
        {
            _pasteWindow.Dispatcher.Invoke(() =>
            {
                ((Clipboard.MainWindow)_pasteWindow).ShowAtMouse();
            });
        }

        private void ShutdownApp()
        {
            _todoWindow.Dispatcher.Invoke(() => _todoWindow.Close());
            _pasteWindow.Dispatcher.Invoke(() => _pasteWindow.Close());
            _settingsWindow.Dispatcher.Invoke(() => _settingsWindow.Hide());
            _menuHostWindow.Dispatcher.Invoke(() => _menuHostWindow.Close());
            _app.Shutdown();
        }

        public void Dispose()
        {
            _clickTimer.Dispose();
            _icon.Dispose();
            try { _settingsManager.SettingsChanged -= OnSettingsChanged; } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"TrayHelper.Dispose(unsub): {ex.Message}"); }
            _menuHostWindow.Dispatcher.Invoke(() => _menuHostWindow.Close());
        }
    }
}
