using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using WinKit.Common;

namespace WinKit.Clipboard.Services
{
    /// <summary>
    /// 全局低级键盘钩子 —— 接管「钩子型热键」，典型就是剪贴板唤出键（默认 Win+V）。
    ///
    /// 为什么不用 RegisterHotKey：
    ///   含 Win 键的组合（Win+V / Win+D …）被系统保留，RegisterHotKey 注册必然失败，
    ///   只能走 WH_KEYBOARD_LL 这条路；而钩子还有个额外好处 —— 它能「吞掉」按键，
    ///   使系统自带的剪贴板面板不再弹出，这正是本类存在的意义。
    ///
    /// 三条必须遵守的约束（都是踩过的坑）：
    ///
    ///   1) 钩子回调必须在极短时间内返回。Windows 对低级钩子设有 LowLevelHooksTimeout
    ///      （默认 300ms），超时后会**静默移除钩子且不作任何通知** —— 此后热键彻底失效，
    ///      直到重新安装。因此回调内只做「判定 + 投递」，任何窗口操作（Show / Activate /
    ///      首次创建 HWND）一律丢给 UI 线程异步执行，绝不在回调里同步等待。
    ///      旧版在回调里同步 Dispatcher.Invoke 去显示剪贴板窗口，而窗口首次显示要创建 HWND、
    ///      加载 WPF 资源，冷启动路径轻松超过 300ms —— 表现就是「Win+V 有概率失灵，
    ///      而且一旦失灵就永久失灵，重启程序才恢复」。
    ///
    ///   2) 回调内异常绝不能逃逸。异常穿过 P/Invoke 边界同样会导致钩子被移除。
    ///
    ///   3) 修饰键状态由钩子自己维护，而不是在主键按下时现查 GetAsyncKeyState ——
    ///      后者在低级钩子回调中存在时序不准的问题。只在安装钩子那一刻用
    ///      GetAsyncKeyState 校正一次初始状态，以覆盖「装钩时用户已经按住修饰键」的情况。
    /// </summary>
    public sealed class KeyboardHookService : IDisposable
    {
        /// <summary>
        /// 一条钩子型热键绑定。
        /// Modifiers 的位编码必须与 <see cref="HotkeyConfig.Modifiers"/> 保持一致
        /// （1=Alt、2=Shift、4=Ctrl、8=Win），这样设置中心录出来的组合能直接拿来用。
        ///
        /// 回调带上命中的 (VK, Modifiers)：设置中心录制时要把按下的组合原样收下，
        /// 只看「哪个回调被触发」是拿不到具体键的。
        /// </summary>
        public readonly record struct HookBinding(int VK, int Modifiers, Action<int, int> Callback);

        // ── Win32 常量 ──────────────────────────────────────────
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP = 0x0101;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_SYSKEYUP = 0x0105;

        private const int VK_SHIFT = 0x10;
        private const int VK_CONTROL = 0x11;
        private const int VK_MENU = 0x12;
        private const int VK_LWIN = 0x5B;
        private const int VK_RWIN = 0x5C;
        private const int VK_LSHIFT = 0xA0;
        private const int VK_RSHIFT = 0xA1;
        private const int VK_LCONTROL = 0xA2;
        private const int VK_RCONTROL = 0xA3;
        private const int VK_LMENU = 0xA4;
        private const int VK_RMENU = 0xA5;

        /// <summary>KBDLLHOOKSTRUCT.flags 的「由程序注入」标志位</summary>
        private const int LLKHF_INJECTED = 0x10;

        // 修饰键位（与 HotkeyConfig.Modifiers 同构）
        private const int ModAlt = 1;
        private const int ModShift = 2;
        private const int ModCtrl = 4;
        private const int ModWin = 8;

        [StructLayout(LayoutKind.Sequential)]
        private struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetModuleHandle(IntPtr lpModuleName);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        /// <summary>需要在初始化时校正状态的修饰键虚拟键码</summary>
        private static readonly int[] ModifierVks =
        {
            VK_LSHIFT, VK_RSHIFT, VK_LCONTROL, VK_RCONTROL,
            VK_LMENU, VK_RMENU, VK_LWIN, VK_RWIN,
        };

        /// <summary>热键日志超过该体积时整份重写，避免无限增长</summary>
        private const long LogMaxBytes = 256 * 1024;

        private readonly Dispatcher _dispatcher;
        /// <summary>强引用钩子委托 —— 被 GC 回收会让原生侧回调悬空，是低级钩子的经典崩溃点</summary>
        private readonly LowLevelKeyboardProc _proc;
        private readonly List<HookBinding> _bindings = new();

        /// <summary>修饰键按下状态：vk → 是否按下。仅由钩子回调线程写读，无需加锁</summary>
        private readonly Dictionary<int, bool> _modDown = new();

        private IntPtr _hookID = IntPtr.Zero;
        private bool _disposed;

        public KeyboardHookService()
        {
            // 在 UI 线程构造 → 钩子回调也跑在 UI 线程，因此投递用同一个 Dispatcher
            _dispatcher = Dispatcher.CurrentDispatcher;
            _proc = HookCallback;
        }

        /// <summary>钩子当前是否已安装</summary>
        public bool IsInstalled => _hookID != IntPtr.Zero;

        /// <summary>当前生效的绑定数量（便于外部判断「是否需要装钩」）</summary>
        public int BindingCount => _bindings.Count;

        /// <summary>
        /// 录制模式：设置中心正在录快捷键时置 true。
        ///
        /// 为什么需要它：绑定表里只有**当前已生效**的组合。用户想把唤出键改成
        /// 一个还没生效的新 Win 组合（比如 Win+Alt+V）时，绑定表当然匹配不上，
        /// 按键会直接落到系统手里，设置窗根本收不到。
        /// 因此录制模式下放宽为「只要修饰键含 Win 就截获并转发」，
        /// 这样任意 Win 组合都能被录下来。不含 Win 的组合不受影响，照常传给设置窗。
        ///
        /// ⚠️ 用完务必复位，否则会把用户的 Win+E / Win+D 之类系统快捷键一并吞掉。
        /// </summary>
        public bool RecordingMode { get; set; }

        /// <summary>录制模式下截获到的按键（vk, modifiers），已在 UI 线程触发</summary>
        public event Action<int, int>? RecordingInputCaptured;

        /// <summary>
        /// 设置（整体替换）全部绑定。传空集合或全部 VK 为 0 表示停止拦截。
        /// 由调用方决定绑哪些键 —— 本类不认识「剪贴板」这个概念，只负责把按键翻译成回调。
        /// </summary>
        public void SetBindings(IReadOnlyList<HookBinding>? bindings)
        {
            _bindings.Clear();

            if (bindings != null)
            {
                foreach (var b in bindings)
                {
                    if (b.VK != 0 && b.Callback != null) _bindings.Add(b);
                }
            }

            if (_bindings.Count == 0)
            {
                Uninstall();
                return;
            }

            Install();
        }

        /// <summary>
        /// 确保钩子已装上（绑定存在但上次安装失败时重试）。
        /// 供外部在明确时机调用 —— 本类刻意不做心跳重装，那会在重装窗口期造成漏键或重复触发。
        /// </summary>
        public void EnsureInstalled()
        {
            if (_disposed || _bindings.Count == 0) return;
            if (_hookID == IntPtr.Zero) Install();
        }

        /// <summary>
        /// 强制重装钩子。留作自愈入口：若钩子曾被系统因超时移除（旧版本的典型故障），
        /// 调一次即可恢复，无需重启程序。
        /// </summary>
        public void Reinstall()
        {
            if (_disposed || _bindings.Count == 0) return;
            Uninstall();
            Install();
            Log("钩子已重装");
        }

        // ══════════════════════════════════════════════
        //  安装 / 卸载
        // ══════════════════════════════════════════════

        private void Install()
        {
            if (_disposed || _hookID != IntPtr.Zero) return;

            SyncModifierState();
            _hookID = SetHook();

            if (_hookID == IntPtr.Zero)
            {
                // 装钩失败是可诊断的问题，不像「超时被移除」那样无声无息，务必留痕
                Log($"安装钩子失败，Win32 错误码 {Marshal.GetLastWin32Error()}");
            }
            else
            {
                Log($"钩子已安装 → {DescribeBindings()}");
            }
        }

        private void Uninstall()
        {
            if (_hookID == IntPtr.Zero) return;

            try
            {
                UnhookWindowsHookEx(_hookID);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"KeyboardHookService: 卸载钩子异常 - {ex.Message}");
            }
            finally
            {
                _hookID = IntPtr.Zero;
                Log("钩子已卸载");
            }
        }

        private IntPtr SetHook()
        {
            // 低级钩子不需要注入 DLL，hMod 传模块句柄或 NULL 均可。
            // 取模块句柄只是为了兼容性更好；取不到时退回 NULL，不因此放弃安装。
            IntPtr hMod = IntPtr.Zero;
            try
            {
                using var curProcess = Process.GetCurrentProcess();
                using var curModule = curProcess.MainModule;
                if (curModule?.ModuleName is string name && !string.IsNullOrEmpty(name))
                {
                    var handle = GetModuleHandle(IntPtr.Zero);
                    if (handle != IntPtr.Zero) hMod = handle;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"KeyboardHookService: 获取模块句柄失败，改用 NULL - {ex.Message}");
            }

            return SetWindowsHookEx(WH_KEYBOARD_LL, _proc, hMod, 0);
        }

        /// <summary>用实时键态校正修饰键状态，覆盖「装钩瞬间用户已按住修饰键」的情况</summary>
        private void SyncModifierState()
        {
            _modDown.Clear();
            foreach (int vk in ModifierVks)
            {
                try
                {
                    if ((GetAsyncKeyState(vk) & 0x8000) != 0) _modDown[vk] = true;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"KeyboardHookService: 校正修饰键状态失败({vk}) - {ex.Message}");
                }
            }
        }

        // ══════════════════════════════════════════════
        //  钩子回调（必须极快返回）
        // ══════════════════════════════════════════════

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (nCode >= 0)
                {
                    var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                    int message = wParam.ToInt32();

                    bool isDown = message == WM_KEYDOWN || message == WM_SYSKEYDOWN;
                    bool isUp = message == WM_KEYUP || message == WM_SYSKEYUP;

                    if (isDown || isUp)
                    {
                        int vk = (int)data.vkCode;

                        if (IsModifierVk(vk))
                        {
                            if (isDown) _modDown[vk] = true;
                            else _modDown.Remove(vk);
                        }
                        else if (isDown && (data.flags & LLKHF_INJECTED) == 0)
                        {
                            if (RecordingMode)
                            {
                                // 录制模式：含 Win 的组合一律截获转发（用户要录的可能是尚未生效的新组合，
                                // 绑定表里没有它）；不含 Win 的组合放行，交给设置窗自己处理。
                                int recordingMods = CurrentModifiers();
                                if ((recordingMods & ModWin) != 0)
                                {
                                    SuppressStartMenu();
                                    var captured = RecordingInputCaptured;
                                    int capturedVk = vk;
                                    int capturedMods = recordingMods;
                                    if (captured != null) Post(() => captured(capturedVk, capturedMods));
                                    return (IntPtr)1;
                                }
                            }
                            else if (TryTrigger(vk))
                            {
                                // 命中绑定则吞掉按键（返回 1 阻止其继续传递，系统剪贴板因而不会弹出）
                                return (IntPtr)1;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // 绝不向外抛：异常穿过 P/Invoke 边界会让系统连带移除本钩子
                Debug.WriteLine($"KeyboardHookService: 回调异常 - {ex.Message}");
            }

            return CallNextHookEx(_hookID, nCode, wParam, lParam);
        }

        /// <summary>命中绑定则投递回调并返回 true（调用方据此吞掉按键）</summary>
        private bool TryTrigger(int vk)
        {
            int mods = CurrentModifiers();

            Action<int, int>? hit = null;
            for (int i = 0; i < _bindings.Count; i++)
            {
                var b = _bindings[i];
                // 精确匹配：Win+V 不应在 Ctrl+Win+V 时被触发
                if (b.VK == vk && b.Modifiers == mods)
                {
                    hit = b.Callback;
                    break;
                }
            }

            if (hit == null) return false;

            SuppressStartMenu();

            // 关键：这里只投递，不同步执行。
            // 目标回调可能显示窗口（首次还要创建 HWND），同步做会拖爆 300ms 的钩子超时预算。
            var callback = hit;
            Post(() => callback(vk, mods));
            return true;
        }

        /// <summary>
        /// 抑制「松开 Win 键弹出开始菜单」。
        /// 主键被我们吞掉后，系统认为 Win 键单独按下过；注入一次无意义的击键可让系统
        /// 认为 Win 键已用于组合键，从而跳过开始菜单。注入的按键带 INJECTED 标志，
        /// 会被本回调直接忽略，不会递归。
        /// </summary>
        private static void SuppressStartMenu()
        {
            try
            {
                keybd_event(0xFF, 0, 0, UIntPtr.Zero);
                keybd_event(0xFF, 0, 2, UIntPtr.Zero);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"KeyboardHookService: 抑制开始菜单失败 - {ex.Message}");
            }
        }

        /// <summary>把回调投递到 UI 线程异步执行（不阻塞钩子链）</summary>
        private void Post(Action callback)
        {
            try
            {
                if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished) return;
                _dispatcher.BeginInvoke(DispatcherPriority.Normal, callback);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"KeyboardHookService: 投递回调失败 - {ex.Message}");
            }
        }

        // ══════════════════════════════════════════════
        //  修饰键状态
        // ══════════════════════════════════════════════

        /// <summary>把已按下的修饰键合并成 HotkeyConfig.Modifiers 形式的位掩码</summary>
        private int CurrentModifiers()
        {
            int m = 0;
            foreach (var kv in _modDown)
            {
                if (kv.Value) m |= ModifierBit(kv.Key);
            }
            return m;
        }

        private static bool IsModifierVk(int vk) => ModifierBit(vk) != 0;

        /// <summary>虚拟键码 → 修饰键位。注意左右键与「通用」修饰键都要映射到同一位</summary>
        private static int ModifierBit(int vk) => vk switch
        {
            VK_SHIFT or VK_LSHIFT or VK_RSHIFT => ModShift,
            VK_CONTROL or VK_LCONTROL or VK_RCONTROL => ModCtrl,
            VK_MENU or VK_LMENU or VK_RMENU => ModAlt,
            VK_LWIN or VK_RWIN => ModWin,
            _ => 0,
        };

        // ══════════════════════════════════════════════
        //  诊断日志
        // ══════════════════════════════════════════════

        private string DescribeBindings()
        {
            var parts = new List<string>(_bindings.Count);
            foreach (var b in _bindings)
            {
                try { parts.Add(HotkeyConfig.Format(b.VK, b.Modifiers)); }
                catch { parts.Add($"VK={b.VK}/Mod={b.Modifiers}"); }
            }
            return string.Join("  |  ", parts);
        }

        /// <summary>
        /// 记录装钩 / 卸钩事件。只在安装与卸载这两个低频时机落盘 ——
        /// 绝不能放进回调主路径，那里做文件 IO 会直接吃掉 300ms 超时预算。
        /// </summary>
        private static void Log(string message)
        {
            try
            {
                var path = AppPaths.HotKeyLog;
                if (File.Exists(path) && new FileInfo(path).Length > LogMaxBytes)
                    File.Delete(path);

                File.AppendAllText(path,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [hook] {message}{Environment.NewLine}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"KeyboardHookService: 写日志失败 - {ex.Message}");
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            Uninstall();
            _bindings.Clear();
            _modDown.Clear();
        }
    }
}
