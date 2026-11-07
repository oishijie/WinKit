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
        private bool _animating;
        private DateTime _lastInside = DateTime.MinValue;
        private DateTime _hoverStart = DateTime.MinValue;

        public RackWindow()
        {
            InitializeComponent();
            SetupWindow();
            _tick.Tick += Tick;
            _tick.Start();
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
        }

        /// <summary>清空晾衣架。</summary>
        public void Clear()
        {
            _items.Clear();
            _cards.Clear();
            CardCanvas.Children.Clear();
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
            var start = new Point(LineMarginX, LineY);
            var end = new Point(LineMarginX + w, LineY);
            var ctrl1 = new Point(LineMarginX + w * 0.3, LineY + Sag * 2);
            var ctrl2 = new Point(LineMarginX + w * 0.7, LineY + Sag * 2);

            var seg = new BezierSegment(start, ctrl1, ctrl2, end, true);
            var fig = new PathFigure(start, new[] { seg }, false);
            RopePath.Data = new PathGeometry(new[] { fig });
        }

        // ── 展开 / 收起 ─────────────────────────

        private void Expand()
        {
            if (_expanded && !_animating) return;
            _expanded = true;
            Height = LineY + Sag * 2 + 120; // 展开高度：绳子 + 卡片
            Topmost = true;
        }

        private void CollapseToTop()
        {
            _expanded = false;
            Height = HotZoneHeight;
        }

        // ── 自动收起：轮询 + 时间戳 ─────────────────────────

        private void Tick(object? sender, EventArgs e)
        {
            if (_animating) return;

            var pos = GetCursorPos();
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
                    }
                }
            }
            else
            {
                _hoverStart = DateTime.MinValue;
                if (_expanded && (DateTime.Now - _lastInside).TotalMilliseconds >= AutoHideMs)
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

        private static Point GetCursorPos()
        {
            // 用 WinForms 的静态鼠标位置（WinKit 混合 WPF + WinForms，可直接用）
            var p = System.Windows.Forms.Control.MousePosition;
            return new Point(p.X, p.Y);
        }
    }
}
