using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using WinForms = System.Windows.Forms;

namespace WinKit.Common
{
    /// <summary>
    /// 右下角通知卡片的调度器：定位、堆叠、上限、全屏免打扰。
    /// 所有方法必须在 UI 线程调用（同步引擎事件本身来自后台线程，
    /// App 侧已经 Dispatcher 切过一次）。
    /// </summary>
    public sealed class ToastService
    {
        /// <summary>同时最多显示几条，超出的先关最旧的一条</summary>
        private const int MaxVisible = 3;

        /// <summary>卡片距屏幕右/下边缘的距离（DIP）</summary>
        private const int EdgeGap = 16;

        /// <summary>卡片之间的视觉间距（DIP）</summary>
        private const int CardGap = 8;

        /// <summary>ToastWindow 里根 Border 的外边距，用于把窗口外框换算成视觉内容框</summary>
        private const double SideMargin = 12;

        private readonly List<ToastWindow> _visible = new();

        /// <summary>总开关：关掉后一律走不到卡片，退回托盘气泡</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>默认停留秒数（鼠标悬停会暂停计时）</summary>
        public int TimeoutSeconds { get; set; } = 6;

        /// <summary>按钮动作的落地实现，由 App 注入（拿得到剪贴板与资源管理器）</summary>
        public Action<SyncToastPayload, ToastAction>? ActionHandler { get; set; }

        /// <summary>弹一张卡片。非 UI 线程调用会被忽略，调用方负责切线程。</summary>
        public void Show(SyncToastPayload payload)
        {
            if (payload == null || !Enabled) return;
            if (!CanDisturb()) return; // 全屏游戏 / PPT 放映 / 勿扰时段：静默，不打扰

            while (_visible.Count >= MaxVisible)
            {
                var oldest = _visible[0];
                _visible.RemoveAt(0);
                try { oldest.Close(); } catch { /* 已关闭则忽略 */ }
            }

            var toast = new ToastWindow(payload, TimeoutSeconds);
            toast.ActionInvoked += (p, a) => ActionHandler?.Invoke(p, a);
            toast.Dismissed += OnDismissed;
            _visible.Add(toast);

            // SizeToContent 的窗口要 Show 之后才知道 ActualHeight，直接 Show 会先在左上角闪一帧。
            // 先透明显示量好尺寸、定位，再显形。
            toast.Opacity = 0;
            toast.Show();
            toast.UpdateLayout();
            Relayout();
            toast.Opacity = 1;
        }

        /// <summary>关掉全部卡片（例如关闭同步总开关时）</summary>
        public void DismissAll()
        {
            var snapshot = _visible.ToArray();
            _visible.Clear();
            foreach (var w in snapshot)
            {
                try { w.Close(); } catch { /* 忽略 */ }
            }
        }

        private void OnDismissed(ToastWindow window)
        {
            if (_visible.Remove(window)) Relayout();
        }

        // ══════════════════════════════════════════════
        //  定位与堆叠
        // ══════════════════════════════════════════════

        /// <summary>
        /// 从屏幕底部往上排：最后加入的那张落在最底部，已有的抬升。
        /// 屏幕取鼠标所在的那块（多屏时跟着视线走），并对 DPI 做换算 ——
        /// Screen.WorkingArea 是物理像素，WPF 的 Left/Top 是 DIP。
        /// </summary>
        private void Relayout()
        {
            if (_visible.Count == 0) return;

            try
            {
                var screen = WinForms.Screen.FromPoint(WinForms.Cursor.Position);
                var area = screen.WorkingArea;
                var dpi = VisualTreeHelper.GetDpi(_visible[_visible.Count - 1]);

                double rightDip = area.Right / dpi.DpiScaleX;
                double bottomDip = area.Bottom / dpi.DpiScaleY;

                double offset = EdgeGap;
                for (int i = _visible.Count - 1; i >= 0; i--)
                {
                    var w = _visible[i];
                    double visW = Math.Max(0, w.ActualWidth - SideMargin * 2);
                    double visH = Math.Max(0, w.ActualHeight - SideMargin * 2);

                    w.Left = rightDip - EdgeGap - visW - SideMargin;
                    w.Top = bottomDip - offset - visH - SideMargin;

                    offset += visH + CardGap;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ToastService.Relayout: {ex.Message}");
            }
        }

        // ══════════════════════════════════════════════
        //  全屏检测
        // ══════════════════════════════════════════════

        [DllImport("shell32.dll")]
        private static extern int SHQueryUserNotificationState(out int state);

        /// <summary>
        /// 只在「允许通知」状态下弹卡片。
        /// 锁屏、全屏游戏、D3D 独占、PPT 放映、勿扰时段一律返回 false，
        /// 内容照常写剪贴板，只是不弹窗。
        /// </summary>
        private static bool CanDisturb()
        {
            try
            {
                if (SHQueryUserNotificationState(out int state) != 0) return true; // 查询失败不拦
                return state == 5; // QUNS_ACCEPTS_NOTIFICATIONS
            }
            catch
            {
                return true; // 老系统调用失败时按可打扰处理，宁可弹也别吞消息
            }
        }
    }
}
