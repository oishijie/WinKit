using System;
using System.Collections.Generic;

namespace WinKit.Common
{
    /// <summary>热键归属的模块 —— 决定它在设置中心的哪个分区里出现。</summary>
    public enum HotkeyOwner
    {
        /// <summary>
        /// 出现在「待办」分区。
        /// 待办窗口由 App 直接持有（没有独立模块类），所以这条热键由 App 顶层装配
        /// —— 见 App.ApplyTopLevelHotkeys。
        /// </summary>
        Todo,

        /// <summary>出现在「翻译与 OCR」分区</summary>
        Translate,

        /// <summary>出现在「截图」分区</summary>
        Capture,

        /// <summary>出现在「剪贴板」分区</summary>
        Clipboard,
    }

    /// <summary>
    /// 热键的注册机制。
    ///
    /// 这个区分很要紧：两条路径的失败模式完全不同，排查时先看机制就能少走弯路。
    /// </summary>
    public enum HotkeyMechanism
    {
        /// <summary>
        /// 走 Win32 <c>RegisterHotKey</c>。适用于普通组合（Alt+S 等）。
        /// 由 <see cref="HotkeyService"/> 注册，失败原因是「已被其它程序占用」。
        /// </summary>
        RegisterHotKey,

        /// <summary>
        /// 走低级键盘钩子。适用于**含 Win 键的组合**（Win+V 默认剪贴板唤出键）——
        /// 这些键被系统保留，<c>RegisterHotKey</c> 注册必然失败。
        /// 由 <c>KeyboardHookService</c> 驱动，额外能力是能把按键吞掉，
        /// 从而阻止系统自带剪贴板面板弹出。
        /// 代价：钩子回调必须极快返回，否则会被系统静默移除（详见该类注释）。
        /// </summary>
        KeyboardHook,
    }

    /// <summary>
    /// 一条可配置全局热键的元数据：显示名 + 归属 + 配置读写器。
    ///
    /// 读写器用委托而不是反射或字符串属性名，既能被编译器检查，也不怕属性改名后漏改。
    /// </summary>
    public sealed class HotkeyDescriptor
    {
        private readonly Func<AppSettings, HotkeyConfig> _get;
        private readonly Action<AppSettings, HotkeyConfig> _set;

        public HotkeyDescriptor(
            string id,
            string label,
            string hint,
            HotkeyOwner owner,
            Func<AppSettings, HotkeyConfig> get,
            Action<AppSettings, HotkeyConfig> set,
            HotkeyMechanism mechanism = HotkeyMechanism.RegisterHotKey)
        {
            Id = id;
            Label = label;
            Hint = hint;
            Owner = owner;
            _get = get;
            _set = set;
            Mechanism = mechanism;
        }

        /// <summary>稳定标识（不随显示名变化，可用于日志定位。模块用它索引动作字典）</summary>
        public string Id { get; }

        /// <summary>界面显示名</summary>
        public string Label { get; }

        /// <summary>界面副标题，说明这条热键做什么</summary>
        public string Hint { get; }

        /// <summary>归属模块</summary>
        public HotkeyOwner Owner { get; }

        /// <summary>注册机制 —— 决定由哪个服务负责注册，见 <see cref="HotkeyMechanism"/></summary>
        public HotkeyMechanism Mechanism { get; }

        /// <summary>从配置里读出当前热键（属性初始化器保证非 null，这里再兜一次空）</summary>
        public HotkeyConfig Get(AppSettings settings) => _get(settings) ?? new HotkeyConfig();

        /// <summary>把新热键写回配置</summary>
        public void Set(AppSettings settings, HotkeyConfig config) => _set(settings, config);
    }

    /// <summary>
    /// 全局热键注册表 —— 全项目唯一的热键元数据来源。
    ///
    /// 历史教训：这些信息曾经散落在设置窗的六个地方（HotkeySettingNames / HotkeyLabels /
    /// GetHotkeyConfigByIndex / SetHotkeyConfigByIndex 以及两个下标数组），
    /// 新增一条热键要同步改六处，漏一处就是「改了快捷键不生效」或者「冲突检测漏检」。
    /// 现在只剩这一张表，UI、冲突检测、录制、模块注册全部从这里取。
    /// </summary>
    public static class HotkeyCatalog
    {
        /// <summary>
        /// 全部可配置热键。
        /// ⚠️ 数组顺序即设置窗使用的「全局下标」，新增请追加到末尾，不要插队改动既有顺序。
        /// </summary>
        public static readonly HotkeyDescriptor[] All =
        {
            new("screenshotTranslate", "截图翻译", "框选屏幕区域，识别文字并翻译", HotkeyOwner.Translate,
                s => s.HotkeyScreenshotTranslate, (s, c) => s.HotkeyScreenshotTranslate = c),

            new("selectionTranslate", "划词翻译", "选中文本后按下，直接取词翻译", HotkeyOwner.Translate,
                s => s.HotkeySelectionTranslate, (s, c) => s.HotkeySelectionTranslate = c),

            new("ocrOnly", "文字识别", "框选后只做 OCR，结果可复制或搜索", HotkeyOwner.Translate,
                s => s.HotkeyOcrOnly, (s, c) => s.HotkeyOcrOnly = c),

            new("silentOcr", "静默 OCR", "识别后不弹窗，直接写入剪贴板", HotkeyOwner.Translate,
                s => s.HotkeySilentOcr, (s, c) => s.HotkeySilentOcr = c),

            new("screenshot", "截图", "框选后进标注编辑器，可复制或另存为", HotkeyOwner.Capture,
                s => s.HotkeyScreenshot, (s, c) => s.HotkeyScreenshot = c),

            // 钩子型热键：默认 Win+V。含 Win 键的组合被系统保留，RegisterHotKey 注册不上，
            // 必须由 KeyboardHookService 走低级钩子接管；顺带把按键吞掉，系统剪贴板面板不再弹出。
            new("clipboardPanel", "唤出剪贴板面板", "改为别的组合会释放 Win+V，系统面板随之恢复", HotkeyOwner.Clipboard,
                s => s.HotkeyClipboardPanel, (s, c) => s.HotkeyClipboardPanel = c,
                HotkeyMechanism.KeyboardHook),

            // 待办唤出键：走普通 RegisterHotKey（不含 Win 键），与托盘菜单「显示待办」同一个动作。
            new("todoPanel", "显示待办", "切换待办窗口的显示 / 隐藏", HotkeyOwner.Todo,
                s => s.HotkeyTodoPanel, (s, c) => s.HotkeyTodoPanel = c),
        };

        /// <summary>可配置热键总数</summary>
        public static int Count => All.Length;

        /// <summary>取某个模块名下的全部全局下标</summary>
        public static int[] IndicesOf(HotkeyOwner owner)
        {
            var list = new List<int>();
            for (int i = 0; i < All.Length; i++)
            {
                if (All[i].Owner == owner) list.Add(i);
            }
            return list.ToArray();
        }

        /// <summary>
        /// 取全部走低级键盘钩子的热键。App 层据此装配钩子绑定 ——
        /// 新增一条钩子型热键同样只需在 <see cref="All"/> 里加一项。
        /// </summary>
        public static IEnumerable<HotkeyDescriptor> HookHotkeys()
        {
            foreach (var d in All)
            {
                if (d.Mechanism == HotkeyMechanism.KeyboardHook) yield return d;
            }
        }

        /// <summary>
        /// 查找与指定组合重复的其它热键，返回其全局下标；没有重复返回 -1。
        /// excludeIndex 用于在录制时排除自己。
        /// </summary>
        public static int FindDuplicate(AppSettings settings, int excludeIndex, int vk, int modifiers)
        {
            if (vk == 0) return -1; // 未设置的热键之间不算重复

            for (int i = 0; i < All.Length; i++)
            {
                if (i == excludeIndex) continue;

                var other = All[i].Get(settings);
                if (other.VK == vk && other.Modifiers == modifiers) return i;
            }
            return -1;
        }
    }
}
