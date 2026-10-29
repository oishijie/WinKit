using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace WinKit.Common
{
    /// <summary>
    /// 窗口背板（RootBorder 背景）统一着色。
    ///
    /// 背景色格式为 <c>#AARRGGBB</c>，只由 Alpha 通道随「窗口不透明度」变化，
    /// RGB 恒为白 —— 这是这套毛玻璃观感的基础：靠半透明白叠在下层内容上，
    /// 而不是靠模糊（本机 Win10 22H2 无 Mica / 亚克力；WPF 分层窗口
    /// 又与 DWM BlurBehind 冲突，见 AGENTS.md）。
    ///
    /// 存在意义：不透明度此前只由 <c>TrayHelper.ApplyOpacity</c> 就地实现，
    /// 作用域硬编码在两个主窗口上，结果窗 / 历史窗等一概不跟随，用户拖滑块
    /// 时看的是最常开的结果窗，自然觉得「没生效」。收敛到这里之后，
    /// 任何窗口只要背板叫 <c>RootBorder</c> 就能一键接入。
    /// </summary>
    public static class WindowBackdrop
    {
        /// <summary>不透明度下限：低于 20% 文字基本无法辨认，不再往下放。</summary>
        public const int MinPercent = 20;

        /// <summary>不透明度上限。</summary>
        public const int MaxPercent = 100;

        /// <summary>
        /// 由百分比（0-100）构造背板画刷。
        /// 例：80 → <c>#CCFFFFFF</c>；40 → <c>#66FFFFFF</c>。
        /// </summary>
        public static SolidColorBrush FromPercent(int percent)
        {
            int clamped = Math.Clamp(percent, MinPercent, MaxPercent);
            byte alpha = (byte)Math.Round(clamped / 100.0 * 255);
            return new SolidColorBrush(Color.FromArgb(alpha, 0xFF, 0xFF, 0xFF));
        }

        /// <summary>
        /// 把不透明度应用到窗口的背板。
        /// 优先取 <see cref="Window.Content"/> 本身（本项目的窗口都以 Border 直接作为内容），
        /// 回退到按名字查找，两者都拿不到就静默跳过 —— 着色失败不应影响窗口功能。
        /// </summary>
        public static bool Apply(Window window, int percent)
        {
            if (window == null) return false;

            var border = window.Content as Border
                         ?? window.FindName("RootBorder") as Border;
            return Apply(border, percent);
        }

        /// <summary>把不透明度应用到指定 Border</summary>
        public static bool Apply(Border? border, int percent)
        {
            if (border == null) return false;
            border.Background = FromPercent(percent);
            return true;
        }
    }
}
