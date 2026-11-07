using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace WinKit.Rack
{
    /// <summary>
    /// 晾衣架主窗口 —— 屏幕顶端一根下垂的绳子，卡片挂在绳子上。
    ///
    /// 核心机制（借鉴 SnowAir-Rack）：
    ///   1. 绳子用三次贝塞尔曲线绘制，中间下垂（sag）；
    ///   2. 卡片沿曲线参数化定位：中心 x 归一化为 t，挂点高度 = 4*sag*t*(1-t)；
    ///   3. 自动收起用 DispatcherTimer 轮询鼠标位置 + 时间戳阈值（而非 MouseLeave 事件）。
    /// </summary>
    public partial class RackWindow : Window
    {
        private readonly List<RackItem> _items = new();
        private readonly List<RackCard> _cards = new();

        // 绳子参数
        private const double Sag = 24.0;        // 绳子下垂深度（像素）
        private const double LineY = 20.0;       // 绳子两端的基线高度（从窗口顶算）
        private const double LineMarginX = 40.0; // 绳子两端距屏幕边缘的留白

        // 自动收起参数（借鉴 SnowAir-Rack 的轮询+时间戳+阈值设计）
        private const double HoverDelayMs = 600.0;   // 顶部热区停留多久才展开
        private const double AutoHideMs = 1200.0;    // 离开后多久收起
        private const double HotZoneHeight = 8.0;    // 收起态顶部热区高度

        private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(50) };
        private bool _expanded;
        /// <summary>
        /// 自动收起的「武装」开关：只有鼠标真正进过展开后的窗口，离开时才允许收起。
        /// 程序侧挂卡片时鼠标通常还在编辑器 / 别的应用里，若也走自动收起，
        /// 卡片会在 AutoHideMs 后一闪消失 —— 用户看到的就是「挂了没反应」。
        /// </summary>
        private bool _autoHideArmed;
        private DateTime _lastInside = DateTime.MinValue;
        private DateTime _hoverStart = DateTime.MinValue;

        /// <summary>逻辑上的展开状态。自动收起后窗口仍在（8px 热区），但对外算「已收起」。</summary>
        public bool IsExpanded => _expanded;

        public RackWindow()
        {
            InitializeComponent();
            SetupWindow();
            // 卡片刚加入时 ActualWidth 还是 0，等窗口真正布局完再按实际尺寸重排一次
            SizeChanged += (_, _) => ReLayout();
            // 模块 Dispose 时 Close 窗口，顺手停掉 50ms 轮询，别让计时器吊着窗口
            Closed += (_, _) => _tick.Stop();
            _tick.Tick += Tick;
            _tick.Start();
            UpdateEmptyHint();
        }

        private void SetupWindow()
        {
            var wa = SystemParameters.WorkArea;
            Left = wa.Left;
            Top = wa.Top;
            Width = wa.Width;
            Height = HotZoneHeight;   // 初始收起态
            CollapseToTop();
        }

        // ── 挂载 / 移除卡片 ─────────────────────────────

        /// <summary>挂一个条目到绳子上。</summary>
        public void Hang(RackItem item)
        {
            _items.Add(item);
            var card = new RackCard(item);
            _cards.Add(card);
            CardCanvas.Children.Add(card);
            card.MouseLeftButtonUp += (s, e) => { card.CopyToClipboard(); e.Handled = true; };
            ReLayout();
            Expand();
            UpdateEmptyHint();
        }

        /// <summary>清空晾衣架。</summary>
        public void Clear()
        {
            _items.Clear();
            _cards.Clear();
            CardCanvas.Children.Clear();
            UpdateEmptyHint();
        }

        // ── 布局：沿曲线挂载卡片 ─────────────────────────

        /// <summary>
        /// 重新布局：每张卡水平中心归一化为 t，代入贝塞尔曲线求挂点高度。
        /// 卡片按 OffsetX 顺序排列。
        /// </summary>
        private void ReLayout()
        {
            double ropeWidth = Math.Max(1, ActualWidth - LineMarginX * 2);
            double lineStart = LineMarginX;

            // 按 OffsetX 排序
            var ordered = new List<(RackItem item, RackCard card)>();
            for (int i = 0; i < _items.Count; i++)
                ordered.Add((_items[i], _cards[i]));
            ordered.Sort((a, b) => a.item.OffsetX.CompareTo(b.item.OffsetX));

            double x = lineStart;
            foreach (var (item, card) in ordered)
            {
                double cardW = card.ActualWidth > 0 ? card.ActualWidth : 120;
                double cx = x + item.OffsetX + cardW / 2;

                // 沿曲线定位：t 归一化，挂点高度用贝塞尔曲线方程 4*sag*t*(1-t)
                double t = Math.Clamp((cx - lineStart) / ropeWidth, 0, 1);
                double pinTop = LineY + 4 * Sag * t * (1 - t);

                Canvas.SetLeft(card, x + item.OffsetX);
                Canvas.SetTop(card, pinTop);

                x += cardW + 12; // 卡片宽度 + 间距
            }

            DrawRope();
        }

        /// <summary>绘制绳子：三次贝塞尔，中间下垂 Sag。</summary>
        private void DrawRope()
        {
            double w = Math.Max(1, ActualWidth - LineMarginX * 2);
            // 收起态窗口只有 8px 高，把绳子压成贴顶的一条细线，至少让用户看见它还在
            double baseY = _expanded ? LineY : HotZoneHeight / 2;
            double sag = _expanded ? Sag * 2 : 0;
            var start = new Point(LineMarginX, baseY);
            var end = new Point(LineMarginX + w, baseY);
            var ctrl1 = new Point(LineMarginX + w * 0.3, baseY + sag);
            var ctrl2 = new Point(LineMarginX + w * 0.7, baseY + sag);

            RopePath.StrokeThickness = _expanded ? 2.5 : 2.0;
            var seg = new BezierSegment(ctrl1, ctrl2, end, true);
            var fig = new PathFigure(start, new[] { seg }, false);
            RopePath.Data = new PathGeometry(new[] { fig });
        }

        // ── 展开 / 收起 ─────────────────────────

        /// <summary>
        /// 显式打开晾衣架：显示窗口、展开到完整高度，并解除自动收起武装。
        /// 托盘菜单与 HangXxx 共用这条路径，保证任何入口打开后都能立刻看到内容。
        /// </summary>
        public void Reveal()
        {
            if (!IsVisible) Show();
            _autoHideArmed = false;      // 程序触发的显示不因鼠标不在顶部而自动收起
            _hoverStart = DateTime.MinValue;
            Expand();
            ReLayout();
            UpdateEmptyHint();
        }

        /// <summary>托盘菜单收起：先回到 8px 热区再隐藏，保证下次点击能重新展开。</summary>
        public void Dismiss()
        {
            CollapseToTop();
            Hide();
        }

        private void Expand()
        {
            _expanded = true;
            Height = LineY + Sag * 2 + 120; // 展开高度：绳子 + 卡片
            Topmost = true;
            // 展开态让空白区域点击穿透，避免全屏宽的置顶窗口挡住下层应用
            Background = null;
            DrawRope();
        }

        private void CollapseToTop()
        {
            _expanded = false;
            _autoHideArmed = false;
            Height = HotZoneHeight;
            // 收起态同样不让窗口吞掉屏幕顶部的点击；热区由 Tick 的坐标轮询负责
            Background = null;
            DrawRope();
        }

        private void UpdateEmptyHint()
        {
            EmptyHint.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        // ── 自动收起：轮询 + 时间戳 ─────────────────────────

        private void Tick(object? sender, EventArgs e)
        {
            var pos = GetCursorPosInDip();
            bool inside = CursorInside(pos);

            if (inside)
            {
                _lastInside = DateTime.Now;
                if (!_expanded)
                {
                    // 顶部热区停留超过 HoverDelayMs 才展开
                    if (_hoverStart == DateTime.MinValue) _hoverStart = DateTime.Now;
                    if ((DateTime.Now - _hoverStart).TotalMilliseconds >= HoverDelayMs)
                    {
                        Expand();
                        _hoverStart = DateTime.MinValue;
                        _autoHideArmed = true;   // 悬停展开：鼠标已在窗口内，离开后允许自动收起
                    }
                }
                else
                {
                    // 鼠标进过展开窗口，之后离开才启动 AutoHide 计时
                    _autoHideArmed = true;
                }
            }
            else
            {
                _hoverStart = DateTime.MinValue;
                if (_expanded && _autoHideArmed &&
                    (DateTime.Now - _lastInside).TotalMilliseconds >= AutoHideMs)
                {
                    CollapseToTop();
                }
            }
        }

        private bool CursorInside(Point cursor)
        {
            if (!IsVisible) return false;
            // 展开态：整个窗口矩形；收起态：只顶部热区
            double hotHeight = _expanded ? Height : HotZoneHeight;
            return cursor.X >= Left && cursor.X <= Left + Width &&
                   cursor.Y >= Top && cursor.Y <= Top + hotHeight;
        }

        /// <summary>
        /// 取鼠标位置并换算成 WPF DIP。Control.MousePosition 是物理像素，
        /// 而 Left / Top / Width / Height 是 DIP；125% / 150% 缩放下直接混用
        /// 会让顶部热区永远命中不了（悬停展开也就永远不会触发）。
        /// </summary>
        private Point GetCursorPosInDip()
        {
            var p = System.Windows.Forms.Control.MousePosition;
            var target = PresentationSource.FromVisual(this)?.CompositionTarget;
            if (target != null)
                return target.TransformFromDevice.Transform(new Point(p.X, p.Y));
            return new Point(p.X, p.Y);
        }
    }
}
