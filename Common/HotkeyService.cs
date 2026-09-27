using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace WinKit.Common
{
    /// <summary>
    /// 全局热键服务 — 基于 Win32 RegisterHotKey，通过隐藏 HwndSource 接收 WM_HOTKEY。
    ///
    /// 通用组件：TranslateModule 用它接管 Alt+S / Alt+D / Alt+Shift+S / Alt+Shift+F，
    /// CaptureModule 用它接管截图键 Alt+A。各模块各持一个实例，互不干扰。
    /// （WinKit 原有 Win+V 仍由 Clipboard 的低级键盘钩子处理。）
    /// </summary>
    public class HotkeyService : IDisposable
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        private const int WM_HOTKEY = 0x0312;
        private const uint MOD_ALT = 0x0001;
        private const uint MOD_CONTROL = 0x0002;
        private const uint MOD_SHIFT = 0x0004;
        private const uint MOD_WIN = 0x0008;
        private const uint MOD_NOREPEAT = 0x4000;

        public const uint VK_S = 0x53;
        public const uint VK_D = 0x44;
        public const uint VK_F = 0x46;

        private readonly HwndSource _source;
        private readonly Dictionary<int, Action> _actions = new();
        private readonly List<int> _registered = new();
        private int _nextId;

        /// <summary>注册成功的热键数量</summary>
        public int RegisteredCount => _registered.Count;

        public HotkeyService()
        {
            var parameters = new HwndSourceParameters("WinKitTranslateHotkeyHost")
            {
                Width = 0,
                Height = 0,
                PositionX = 0,
                PositionY = 0,
                WindowStyle = 0,
                ExtendedWindowStyle = 0
            };
            _source = new HwndSource(parameters);
            _source.AddHook(WndProc);
        }

        /// <summary>
        /// 注册组合热键。modifiers 使用 HotkeyConfig 约定：1=Alt, 2=Shift, 4=Ctrl, 8=Win。
        /// </summary>
        /// <returns>
        /// 是否注册成功（失败通常因热键被占用）。
        /// 特例：<paramref name="vk"/> == 0 表示用户在设置里把这条热键**清空**了 ——
        /// 这时直接返回 true 且什么都不注册。返回 false 会让上层的
        /// HotkeyRegistrationFailed 误报「热键注册失败」，把「用户主动留空」说成故障。
        /// </returns>
        public bool Register(uint vk, int modifiers, Action action)
        {
            if (vk == 0) return true; // 未设置（已清空）—— 无需注册，也不算失败

            uint mods = MOD_NOREPEAT;
            if ((modifiers & 1) != 0) mods |= MOD_ALT;
            if ((modifiers & 2) != 0) mods |= MOD_SHIFT;
            if ((modifiers & 4) != 0) mods |= MOD_CONTROL;
            if ((modifiers & 8) != 0) mods |= MOD_WIN;

            if (mods == MOD_NOREPEAT) return false; // 至少需要一个修饰键

            int id = _nextId++;
            if (RegisterHotKey(_source.Handle, id, mods, vk))
            {
                _actions[id] = action;
                _registered.Add(id);
                return true;
            }
            return false;
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_HOTKEY)
            {
                int id = wParam.ToInt32();
                if (_actions.TryGetValue(id, out var action))
                {
                    action();
                    handled = true;
                }
            }
            return IntPtr.Zero;
        }

        public void Dispose()
        {
            foreach (var id in _registered)
            {
                try { UnregisterHotKey(_source.Handle, id); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"HotkeyService.Dispose(unreg): {ex.Message}"); }
            }
            _registered.Clear();
            _actions.Clear();
            try { _source?.Dispose(); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"HotkeyService.Dispose(source): {ex.Message}"); }
        }
    }
}
