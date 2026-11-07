using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WinKit.Clipboard.Models;
using WinKit.Clipboard.Services;
using WinKit.Common;
using WinKit.Todo.Services;

namespace WinKit.Rack
{
    /// <summary>
    /// 晾衣架主窗口 —— 近水平横杆 + 左信封 + 右篮子（对齐 SnowAir-Rack 的形态与语义）。
    ///
    /// 保留已验证过的五条铁律（改动会直接踩回「点了没反应」「卡片一闪就没」）：
    ///   ① 收起态顶部 8px 热区；② 悬停 600ms 展开；③ 离开 1200ms 收起；
    ///   ④ 50ms 轮询 Win32 坐标而非 MouseLeave；⑤ GetCursorPosInDip 做 DPI 换算；
    ///   ⑥ _autoHideArmed：只有鼠标真正进过展开窗口，离开才允许自动收起。
    ///
    /// 行为语义（对齐 SnowAir-Rack 源码）：
    ///   · 只有截图会自动上绳（Plugin.cpp 的 OnCapture）；
    ///   · 复制的内容按内容合并去重进左信封气泡，用户点「上绳」才生成卡片；
    ///   · 右篮子是待办合并池，未完成 / 已完成按标题合并，点「上绳」才生成卡片；
    ///   · 文本与待办默认都不上绳，绳子上只有图片和用户主动挂的卡片；
    ///   · 同正文重复挂载只晃原卡、不新增卡；
    ///   · 信封 / 篮子各带剪贴板条数 / 待办条数角标。
    /// </summary>
    public partial class RackWindow : Window
    {
        private readonly List<RackItem> _items = new();
        private readonly List<RackCard> _cards = new();

        // ── 布局常量 ──────────────────────────────────
        private const double LineY = 34.0;            // 横杆基线（从窗口顶算）
        private const double Sag = 6.0;               // 近水平微垂（旧版贝塞尔是 24，太弯）
        private const double EdgePad = 132.0;         // 两端留给信封 / 篮子
        private const double CardGap = 14.0;
        private const double HotZoneHeight = 8.0;     // 收起态热区，铁律①
        private const double BaseExpandHeight = 250.0;
        private const double PanelExpandHeight = 660.0;  // 气泡打开时加高（88 + 550 + 余量）
        private const double PollIntervalMs = 50.0;   // 铁律④

        private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(PollIntervalMs) };
        private bool _expanded;
        private bool _autoHideArmed;                   // 铁律⑥
        private DateTime _lastInside = DateTime.MinValue;
        private DateTime _hoverStart = DateTime.MinValue;

        // ── 两个合并池的数据源 ──
        private ClipboardManager? _clipboardManager;
        private readonly MarkdownStorage _todoStorage = new();

        // ── 设置快照（由 RackModule 在配置变更时推过来）──
        private RackTheme _theme = RackTheme.All[0];
        private double _cardWidth = 132;
        private double _cardHeight = 132;
        private bool _showRope = true;
        private bool _showDecor = true;
        private bool _showClip = true;
        private bool _hoverExpand = true;
        private bool _autoHide = true;
        private bool _enableReorder = true;
        private bool _persist = true;
        private double _hoverDelayMs = 600;           // 铁律②
        private double _autoHideMs = 1200;            // 铁律③
        private int _maxItems = 24;

        // ── 绳上的金属夹子（跟卡片数量同步，压在卡片上沿）──
        private readonly List<Border> _pegs = new();

        // ── 拖动状态 ──────────────────────────────────
        private RackCard? _dragCard;
        private Point _dragStart;
        private double _dragOriginLeft;
        private bool _dragMoved;
        private bool _dragHorizontal;

        /// <summary>逻辑上的展开状态。自动收起后窗口仍在（8px 热区），但对外算「已收起」。</summary>
        public bool IsExpanded => _expanded;

        /// <summary>卡片数量。</summary>
        public int ItemCount => _items.Count;

        public RackWindow()
        {
            InitializeComponent();
            SetupWindow();
            // 卡片刚加入时 ActualWidth 还是 0，等窗口真正布局完再按实际尺寸重排一次
            SizeChanged += (_, _) => ReLayout();
            Closed += (_, _) =>
            {
                _tick.Stop();
                Persist();
                if (_clipboardManager != null) _clipboardManager.ItemsChanged -= OnClipboardItemsChanged;
            };

            ListPanel.HangRequested += Panel_HangRequested;
            ListPanel.DeleteRequested += Panel_DeleteRequested;
            ListPanel.NewNoteRequested += Panel_NewNoteRequested;
            ListPanel.CloseRequested += ClosePanel;

            _tick.Tick += Tick;
            _tick.Start();
            ApplyThemeVisual();
            UpdateEmptyHint();
            UpdateBadges();
        }

        private void SetupWindow()
        {
            var wa = SystemParameters.WorkArea;
            Left = wa.Left;
            Top = wa.Top;
            Width = wa.Width;
            Height = HotZoneHeight;
            CollapseToTop();
        }

        // ══════════════════════════════════════════════
        //  设置
        // ══════════════════════════════════════════════

        /// <summary>把设置页的 14 项推到窗口，立即生效（无需重启）。</summary>
        public void ApplySettings(AppSettings s)
        {
            _theme = RackTheme.Get(RackTheme.IndexOf(s.RackTheme));
            _cardWidth = s.RackCardScale switch
            {
                "small" => 100,
                "large" => 164,
                _ => 132,
            };
            _cardHeight = _cardWidth;
            _showRope = s.RackShowRope;
            _showDecor = s.RackShowDecor;
            _showClip = s.RackShowClip;
            _hoverExpand = s.RackHoverExpand;
            _autoHide = s.RackAutoHide;
            _enableReorder = s.RackEnableReorder;
            _persist = s.RackPersistItems;
            _hoverDelayMs = Math.Clamp(s.RackHoverDelayMs, 100, 3000);
            _autoHideMs = Math.Clamp(s.RackAutoHideMs, 300, 10000);
            _maxItems = Math.Clamp(s.RackMaxItems, 5, 60);

            ApplyThemeVisual();
            foreach (var card in _cards) card.SetClipVisible(_showClip);
            ReLayout();
        }

        private void ApplyThemeVisual()
        {
            RopePath.Stroke = RackTheme.Brush(_theme.RopeColor);
            EmptyHint.Background = Brushes.Transparent;
            EmptyHint.BorderBrush = Brushes.Transparent;
            EmptyHintText.Foreground = RackTheme.Brush(_theme.MutedText);

            EnvelopeBack.Source = LoadAsset(_theme.EnvelopeBackAsset);
            EnvelopeFront.Source = LoadAsset(_theme.EnvelopeFrontAsset);
            BasketImage.Source = LoadAsset(_theme.BasketAsset);
            ListPanel.ApplyTheme(_theme);

            foreach (var card in _cards) card.ApplyTheme(_theme);
        }

        private static BitmapSource? LoadAsset(string file)
        {
            if (string.IsNullOrEmpty(file)) return null;
            try
            {
                var path = RackTheme.AssetPath(file);
                if (!File.Exists(path)) return null;
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(path, UriKind.Absolute);
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch
            {
                return null;   // 分发漏素材时只隐藏装饰，不能让启动崩掉
            }
        }

        // ══════════════════════════════════════════════
        //  合并列表数据源：剪贴板文本 / 历史
        // ══════════════════════════════════════════════

        /// <summary>
        /// 绑定剪贴板历史作为信封气泡的数据源。文本只是被读进合并列表，
        /// 不会自动变成绳上的卡片；是否自动上绳由 RackModule 按设置决定。
        /// </summary>
        public void SetClipboardSource(ClipboardManager? manager)
        {
            if (_clipboardManager != null) _clipboardManager.ItemsChanged -= OnClipboardItemsChanged;
            _clipboardManager = manager;
            if (manager != null) manager.ItemsChanged += OnClipboardItemsChanged;
            UpdateBadges();
        }

        private void OnClipboardItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => OnClipboardItemsChanged(sender, e)));
                return;
            }
            UpdateBadges();
            if (ListPanel.Mode == RackPanelMode.Clipboard) OpenPanel(RackPanelMode.Clipboard);
        }

        /// <summary>
        /// 按内容指纹合并去重（对齐 ClipboardBubble.cpp 的 EntryContentKey）：
        /// 同一段内容复制 N 次只显示一行，用 ×N 表示重复次数。
        /// 图片 / 文件条目也进池，但同样只是列表项，不会自动上绳。
        /// </summary>
        private IReadOnlyList<RackListEntry> BuildClipboardEntries()
        {
            var src = _clipboardManager?.Items;
            if (src == null) return Array.Empty<RackListEntry>();

            var groups = src
                .Where(i => !string.IsNullOrWhiteSpace(i.Content))
                .GroupBy(KeyOf, StringComparer.Ordinal)
                .Select(g => new
                {
                    Latest = g.OrderByDescending(x => x.Timestamp).First(),
                    Count = g.Count(),
                })
                .OrderByDescending(g => g.Latest.Timestamp)
                .Take(30)
                .ToList();

            return groups.Select(g =>
            {
                var item = g.Latest;
                var text = item.Content ?? "";
                var card = RackItem.FromClipboardText(text);
                return new RackListEntry
                {
                    Title = item.Type switch
                    {
                        ClipboardItemType.Image => "图片 · " + Path.GetFileName(item.ImagePath ?? ""),
                        ClipboardItemType.File => "文件 · " + Path.GetFileName(text),
                        _ => ShortLine(text),
                    },
                    Detail = (g.Count > 1 ? "×" + g.Count + " · " : "") +
                             item.Timestamp.ToString("MM-dd HH:mm"),
                    Repeat = g.Count,
                    Category = CategoryOf(item, card),
                    Tag = TagOf(item, card),
                    CopyText = text,
                    ClipboardId = item.Id,
                    Create = () => CreateFromClipboard(item.Type, item.ImagePath, text),
                };
            }).ToList();
        }

        private static string KeyOf(ClipboardItem i) =>
            !string.IsNullOrWhiteSpace(i.ContentHash)
                ? "h:" + i.ContentHash
                : (int)i.Type + ":" + (i.Content ?? "").Trim();

        private static string CategoryOf(ClipboardItem i, RackItem card) => i.Type switch
        {
            ClipboardItemType.Image => "图片",
            ClipboardItemType.File => "文件",
            _ => card.CardType switch
            {
                RackItemType.Link => "链接",
                RackItemType.Color => "颜色",
                RackItemType.File => "文件",
                _ => "文本",
            },
        };

        private static string TagOf(ClipboardItem i, RackItem card) => i.Type switch
        {
            ClipboardItemType.Image => "图片",
            ClipboardItemType.File => "文件",
            _ => card.CardType.DisplayName(),
        };

        /// <summary>点「上绳」时才真正把池里的内容变成卡片。</summary>
        private static RackItem CreateFromClipboard(ClipboardItemType type, string? imagePath, string text)
        {
            if (type == ClipboardItemType.Image && !string.IsNullOrEmpty(imagePath) && File.Exists(imagePath))
            {
                try
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.UriSource = new Uri(imagePath, UriKind.Absolute);
                    bmp.EndInit();
                    bmp.Freeze();
                    return RackItem.ImageItem(bmp);
                }
                catch
                {
                    // 图片文件失效时退回文本卡，不能让「上绳」把托盘进程带崩
                }
            }
            return RackItem.FromClipboardText(text);
        }

        /// <summary>
        /// 待办合并池：todos.md 里的条目按标题合并，重复只显示一次并带 ×N。
        /// 未完成的排在前面，已完成折叠在「已完成」分类里。
        /// 只读不回写：待办主窗口有自己的内存列表，从晾衣架写盘会覆盖未保存的改动。
        /// </summary>
        private IReadOnlyList<RackListEntry> BuildTodoEntries()
        {
            List<WinKit.Todo.Models.TodoItem> todos;
            try { todos = _todoStorage.LoadTodos(); }
            catch { return Array.Empty<RackListEntry>(); }

            return todos
                .Select((t, i) => new { Item = t, Index = i })
                .GroupBy(x => (x.Item.Title ?? "").Trim(), StringComparer.Ordinal)
                .Select(g =>
                {
                    var first = g.OrderBy(x => x.Item.IsDone ? 1 : 0).ThenBy(x => x.Index).First();
                    bool done = first.Item.IsDone;
                    var title = (first.Item.Title ?? "").Trim();
                    return new RackListEntry
                    {
                        Title = ShortLine(title),
                        Detail = done ? "已完成" : "未完成",
                        Repeat = g.Count(),
                        Category = done ? "已完成" : "未完成",
                        Tag = "待办",
                        CopyText = title,
                        TodoIndex = first.Index,
                        IsDone = done,
                        Create = () => RackItem.TodoItem(title, done),
                    };
                })
                .OrderBy(e => e.IsDone ? 1 : 0)
                .Take(40)
                .ToList();
        }

        private static string ShortLine(string raw)
        {
            var s = (raw ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            if (s.Length == 0) return "（空白）";
            return s.Length > 46 ? s.Substring(0, 46) + "…" : s;
        }

        private void UpdateBadges()
        {
            int clip = BuildClipboardEntries().Count;
            int todos = BuildTodoEntries().Count;

            EnvelopeBadge.Visibility = clip > 0 ? Visibility.Visible : Visibility.Collapsed;
            BasketBadge.Visibility = todos > 0 ? Visibility.Visible : Visibility.Collapsed;
            EnvelopeBadgeText.Text = clip > 99 ? "99+" : clip.ToString();
            BasketBadgeText.Text = todos > 99 ? "99+" : todos.ToString();
        }

        // ══════════════════════════════════════════════
        //  信封 / 篮子：面板开关
        // ══════════════════════════════════════════════

        private void Envelope_Click(object sender, MouseButtonEventArgs e)
        {
            TogglePanel(RackPanelMode.Clipboard);
            e.Handled = true;
        }

        private void Basket_Click(object sender, MouseButtonEventArgs e)
        {
            TogglePanel(RackPanelMode.Todo);
            e.Handled = true;
        }

        private void TogglePanel(RackPanelMode mode)
        {
            if (ListPanel.Mode == mode) ClosePanel();
            else OpenPanel(mode);
        }

        private void OpenPanel(RackPanelMode mode)
        {
            bool clip = mode == RackPanelMode.Clipboard;
            ListPanel.SetAnchor(clip, 36);
            ListPanel.Show(clip ? BuildClipboardEntries() : BuildTodoEntries(), mode, _theme);

            if (!_expanded) { Reveal(); return; }
            ApplyExpandedHeight();
            UpdateEmptyHint();
            UpdateBadges();
        }

        private void ClosePanel()
        {
            if (ListPanel.Mode == RackPanelMode.None) return;
            ListPanel.HidePanel();
            if (_expanded) ApplyExpandedHeight();
            UpdateEmptyHint();
        }

        private void ApplyExpandedHeight()
        {
            double want = ListPanel.Mode == RackPanelMode.None ? BaseExpandHeight : PanelExpandHeight;
            double cap = Math.Max(BaseExpandHeight, SystemParameters.WorkArea.Height - 8);
            Height = Math.Min(want, cap);
        }

        private void Panel_HangRequested(RackListEntry entry)
        {
            var item = entry.Create?.Invoke();
            if (item != null) Hang(item);
        }

        /// <summary>气泡里的删除只作用于剪贴板记录，绳上的卡片不受影响。</summary>
        private void Panel_DeleteRequested(RackListEntry entry)
        {
            if (entry.ClipboardId is Guid id) _clipboardManager?.RemoveItem(id);
            UpdateBadges();
            if (ListPanel.Mode == RackPanelMode.Clipboard) OpenPanel(RackPanelMode.Clipboard);
        }

        /// <summary>气泡里的「+」：新建一张便签卡并挂上绳子。</summary>
        private void Panel_NewNoteRequested() => Hang(RackItem.NoteItem("新便签"));

        // ══════════════════════════════════════════════
        //  挂载 / 移除
        // ══════════════════════════════════════════════

        /// <summary>从 rack_items.json 恢复卡片（在 RackModule 首次创建窗口后调用一次）。</summary>
        public void LoadPersisted()
        {
            if (!_persist) return;
            foreach (var item in RackItemStore.Load().Take(_maxItems))
                AddCard(item);
            ReLayout();
            UpdateEmptyHint();
            UpdateBadges();
        }

        /// <summary>
        /// 挂一个条目。hash 去重（重复只晃原卡）+ 上限淘汰 + 落盘。
        /// 这是「上绳」的唯一入口：两个合并气泡、截图、外部模块都走这里。
        /// </summary>
        public void Hang(RackItem item)
        {
            if (item == null) return;

            RackItemStore.StoreImage(item);
            RackItemStore.EnsureTextHash(item);

            int dup = _items.FindIndex(i =>
                !string.IsNullOrEmpty(item.Hash) &&
                string.Equals(i.Hash, item.Hash, StringComparison.Ordinal));
            if (dup >= 0)
            {
                if (dup < _cards.Count) _cards[dup].Shake();   // 同正文不新增卡
                Reveal();
                return;
            }

            while (_items.Count >= _maxItems)
            {
                int victim = _items.FindIndex(i => !i.Locked);
                if (victim < 0) victim = 0;
                RemoveAt(victim, persist: false);
            }

            item.OffsetX = (_items.Count == 0 ? 0 : _items.Max(i => i.OffsetX) + 1000);
            item.Order = _items.Count;
            AddCard(item);
            Persist();
            ReLayout();
            Expand();
            UpdateEmptyHint();
            UpdateBadges();
        }

        private void AddCard(RackItem item)
        {
            var card = new RackCard(item);
            card.ApplyTheme(_theme);
            card.SetClipVisible(_showClip);
            card.RemoveRequested += (s, _) => RemoveCard((RackCard)s!);
            card.LockRequested += (s, _) => ToggleLock((RackCard)s!);
            card.MouseLeftButtonDown += Card_MouseLeftButtonDown;
            card.MouseMove += Card_MouseMove;
            card.MouseLeftButtonUp += Card_MouseLeftButtonUp;

            _items.Add(item);
            _cards.Add(card);
            CardCanvas.Children.Add(card);
        }

        private void RemoveCard(RackCard card)
        {
            int idx = _cards.IndexOf(card);
            if (idx < 0) return;
            RemoveAt(idx, persist: true);
            ReLayout();
            UpdateEmptyHint();
        }

        private void RemoveAt(int idx, bool persist)
        {
            if (idx < 0 || idx >= _cards.Count) return;
            var card = _cards[idx];
            CardCanvas.Children.Remove(card);
            _cards.RemoveAt(idx);
            _items.RemoveAt(idx);
            if (persist) Persist();     // 只从绳上取下，剪贴板 / 待办数据不动
        }

        private void ToggleLock(RackCard card)
        {
            card.Item.Locked = !card.Item.Locked;
            card.Render();
            Persist();
        }

        /// <summary>清空所有卡片（设置页按钮）。两个合并池的数据不动。</summary>
        public void Clear()
        {
            for (int i = _cards.Count - 1; i >= 0; i--) RemoveAt(i, persist: false);
            Persist();
            ReLayout();
            UpdateEmptyHint();
        }

        private void Persist()
        {
            if (_persist) RackItemStore.Save(_items);
        }

        // ══════════════════════════════════════════════
        //  拖动：横向重排 / 纵向拖出 / 点击复制
        // ══════════════════════════════════════════════

        private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left || sender is not RackCard card) return;
            if (card.Item.Locked) return;

            _dragCard = card;
            _dragStart = e.GetPosition(CardCanvas);
            _dragOriginLeft = Canvas.GetLeft(card);
            if (double.IsNaN(_dragOriginLeft)) _dragOriginLeft = 0;
            _dragMoved = false;
            _dragHorizontal = false;
            card.CaptureMouse();
            e.Handled = true;
        }

        private void Card_MouseMove(object sender, MouseEventArgs e)
        {
            if (_dragCard == null || sender != _dragCard || e.LeftButton != MouseButtonState.Pressed) return;
            var p = e.GetPosition(CardCanvas);
            double dx = p.X - _dragStart.X;
            double dy = p.Y - _dragStart.Y;

            if (!_dragMoved)
            {
                if (Math.Abs(dx) < 5 && Math.Abs(dy) < 5) return;
                _dragMoved = true;
                _dragHorizontal = _enableReorder && Math.Abs(dx) >= Math.Abs(dy);

                if (!_dragHorizontal)
                {
                    // 纵向：拖出到其它应用，一律 COPY
                    var outCard = _dragCard;
                    _dragCard = null;
                    outCard.ReleaseMouseCapture();
                    outCard.DragOut();
                    return;
                }
            }

            if (_dragHorizontal)
            {
                double maxLeft = Math.Max(0, ActualWidth - _dragCard.Width);
                Canvas.SetLeft(_dragCard, Math.Clamp(_dragOriginLeft + dx, 0, maxLeft));
                Panel.SetZIndex(_dragCard, 9);
            }
        }

        private void Card_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (sender is not RackCard card) return;

            if (_dragCard == card)
            {
                if (!_dragMoved) card.CopyToClipboard();
                else if (_dragHorizontal) CommitReorder();

                Panel.SetZIndex(card, 0);
                card.ReleaseMouseCapture();
                _dragCard = null;
            }
            e.Handled = true;
        }

        /// <summary>按拖动后的左右次序重排 OffsetX 并落盘。</summary>
        private void CommitReorder()
        {
            var ordered = _cards
                .Select(c => (Card: c, Left: Canvas.GetLeft(c)))
                .OrderBy(p => double.IsNaN(p.Left) ? 0 : p.Left)
                .ToList();

            for (int i = 0; i < ordered.Count; i++)
            {
                ordered[i].Card.Item.OffsetX = i * 1000;
                ordered[i].Card.Item.Order = i;
            }
            ReLayout();
            Persist();
        }

        // ══════════════════════════════════════════════
        //  布局
        // ══════════════════════════════════════════════

        private void ReLayout()
        {
            double w = Math.Max(1, ActualWidth);
            DrawRope(w);
            PositionDecor(w);

            var ordered = new List<(RackItem Item, RackCard Card)>();
            for (int i = 0; i < _items.Count; i++) ordered.Add((_items[i], _cards[i]));
            ordered = ordered.OrderBy(p => p.Item.OffsetX).ThenBy(p => p.Item.Order).ToList();

            double startX = EdgePad + 16;
            double available = Math.Max(120, w - startX - EdgePad - 16);
            double x = startX;
            var centers = new List<double>();

            foreach (var (item, card) in ordered)
            {
                double cw = CardWidthFor(item);
                double ch = CardHeightFor(item, cw);
                card.Width = cw;
                card.Height = ch;

                double centerX = x + cw / 2;
                double t = Math.Clamp((centerX - startX) / available, 0, 1);
                double pinY = _expanded
                    ? LineY + 4 * Sag * t * (1 - t) + 14
                    : 4;

                Canvas.SetLeft(card, x);
                Canvas.SetTop(card, pinY);
                Panel.SetZIndex(card, 0);
                centers.Add(centerX);
                x += cw + CardGap;
            }

            SyncPegs(centers);
        }

        /// <summary>金属夹子：一张卡片一个，压在卡片上沿，数量跟卡片同步。</summary>
        private static Border MakePeg() => new()
        {
            Width = 9,
            Height = 22,
            CornerRadius = new CornerRadius(4),
            Background = RackTheme.Brush("#FFDDE3E9"),
            BorderBrush = RackTheme.Brush("#FFA8B4BF"),
            BorderThickness = new Thickness(1),
            IsHitTestVisible = false,
            Child = new Border
            {
                Width = 1,
                Height = 14,
                Background = RackTheme.Brush("#FFB7C2CC"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };

        private void SyncPegs(IReadOnlyList<double> centers)
        {
            bool show = _showClip && _expanded;
            while (_pegs.Count < centers.Count)
            {
                var peg = MakePeg();
                _pegs.Add(peg);
                CardCanvas.Children.Add(peg);
            }
            while (_pegs.Count > centers.Count)
            {
                var peg = _pegs[_pegs.Count - 1];
                _pegs.RemoveAt(_pegs.Count - 1);
                CardCanvas.Children.Remove(peg);
            }
            for (int i = 0; i < _pegs.Count; i++)
            {
                var peg = _pegs[i];
                peg.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
                Canvas.SetLeft(peg, centers[i] - 4.5);
                Canvas.SetTop(peg, LineY - 8);
                Panel.SetZIndex(peg, 8);
            }
        }

        private double CardWidthFor(RackItem item)
        {
            if (!item.CardType.IsPhoto()) return _cardWidth;
            double aspect = item.Aspect > 0.1 ? item.Aspect : 1.0;
            return _cardWidth * Math.Clamp(aspect, 0.75, 1.45);
        }

        private double CardHeightFor(RackItem item, double width)
        {
            if (!item.CardType.IsPhoto()) return _cardHeight;
            double aspect = item.Aspect > 0.1 ? item.Aspect : 1.0;
            return Math.Clamp(width / aspect, 84, 176);
        }

        private void DrawRope(double w)
        {
            RopePath.Visibility = _showRope ? Visibility.Visible : Visibility.Collapsed;
            double y = _expanded ? LineY : HotZoneHeight / 2;
            double x1 = EdgePad - 18;
            double x2 = Math.Max(x1 + 10, w - EdgePad + 18);
            double sag = _expanded ? Sag * 2 : 0;
            var start = new Point(x1, y);
            var end = new Point(x2, y);
            var ctrl1 = new Point(x1 + (x2 - x1) * 0.3, y + sag);
            var ctrl2 = new Point(x1 + (x2 - x1) * 0.7, y + sag);
            RopePath.StrokeThickness = _expanded ? 2.5 : 2.0;
            RopePath.Data = new PathGeometry(new[]
            {
                new PathFigure(start, new[] { new BezierSegment(ctrl1, ctrl2, end, true) }, false)
            });
        }

        private void PositionDecor(double w)
        {
            bool show = _showDecor && _expanded;
            EnvelopeHost.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            BasketHost.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (!show) return;

            Canvas.SetLeft(EnvelopeHost, 6);
            Canvas.SetTop(EnvelopeHost, LineY - 22);
            Canvas.SetLeft(BasketHost, Math.Max(0, w - 118));
            Canvas.SetTop(BasketHost, LineY - 20);
        }

        // ══════════════════════════════════════════════
        //  展开 / 收起
        // ══════════════════════════════════════════════

        public void Reveal()
        {
            if (!IsVisible) Show();
            _autoHideArmed = false;      // 程序触发的显示不因鼠标不在顶部而自动收起
            _hoverStart = DateTime.MinValue;
            Expand();
            ReLayout();
            UpdateEmptyHint();
            UpdateBadges();
        }

        public void Dismiss()
        {
            CollapseToTop();
            Hide();
        }

        private void Expand()
        {
            _expanded = true;
            Topmost = true;
            Background = null;           // 空白区域点击穿透
            ApplyExpandedHeight();
            ReLayout();   // 展开后重算卡片位置并显示金属夹子
        }

        private void CollapseToTop()
        {
            _expanded = false;
            _autoHideArmed = false;
            ClosePanel();
            Height = HotZoneHeight;
            Background = null;
            ReLayout();   // 收起时把夹子一并隐藏
        }

        private void UpdateEmptyHint()
        {
            EmptyHint.Visibility = _items.Count == 0 && _expanded &&
                                   ListPanel.Mode == RackPanelMode.None
                ? Visibility.Visible : Visibility.Collapsed;
        }

        // ══════════════════════════════════════════════
        //  自动收起：轮询 + 时间戳（铁律②③④⑤⑥）
        // ══════════════════════════════════════════════

        private void Tick(object? sender, EventArgs e)
        {
            var pos = GetCursorPosInDip();
            bool inside = CursorInside(pos);

            if (inside)
            {
                _lastInside = DateTime.Now;
                if (!_expanded)
                {
                    if (!_hoverExpand) return;
                    if (_hoverStart == DateTime.MinValue) _hoverStart = DateTime.Now;
                    if ((DateTime.Now - _hoverStart).TotalMilliseconds >= _hoverDelayMs)
                    {
                        Expand();
                        _hoverStart = DateTime.MinValue;
                        _autoHideArmed = true;
                        ReLayout();
                        UpdateEmptyHint();
                        UpdateBadges();
                    }
                }
                else
                {
                    _autoHideArmed = true;
                }
            }
            else
            {
                _hoverStart = DateTime.MinValue;
                if (_expanded && _autoHide && _autoHideArmed &&
                    (DateTime.Now - _lastInside).TotalMilliseconds >= _autoHideMs)
                {
                    CollapseToTop();
                    UpdateEmptyHint();
                }
            }
        }

        private bool CursorInside(Point cursor)
        {
            if (!IsVisible) return false;
            double hotHeight = _expanded ? Height : HotZoneHeight;
            return cursor.X >= Left && cursor.X <= Left + Width &&
                   cursor.Y >= Top && cursor.Y <= Top + hotHeight;
        }

        /// <summary>
        /// 取鼠标位置并换算成 WPF DIP。Control.MousePosition 是物理像素，
        /// 而 Left / Top / Width / Height 是 DIP；125% / 150% 缩放下直接混用
        /// 会让顶部热区永远命中不了。
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
