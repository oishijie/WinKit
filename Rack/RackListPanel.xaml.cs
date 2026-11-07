using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace WinKit.Rack
{
    /// <summary>面板当前展示的数据源：信封＝剪贴板合并池，篮子＝待办合并池。</summary>
    public enum RackPanelMode
    {
        None,
        Clipboard,
        Todo,
    }

    /// <summary>
    /// 合并池里的一行。内容与卡片是惰性关系：只有点「上绳」时才调用 Create 生成卡片，
    /// 所以复制 / 新增待办永远只进池，不会自动挂到绳子上。
    /// </summary>
    public sealed class RackListEntry
    {
        public string Title { get; init; } = "";
        public string Detail { get; init; } = "";
        public int Repeat { get; init; } = 1;
        public string? CopyText { get; init; }
        public string Category { get; init; } = "文本";
        public string Tag { get; init; } = "文字";
        public Guid? ClipboardId { get; init; }
        public int TodoIndex { get; init; } = -1;
        public bool IsDone { get; init; }
        public Func<RackItem>? Create { get; init; }
    }

    /// <summary>
    /// 信封 / 篮子共用的合并气泡，几何规格对齐 SnowAir-Rack 的 ClipboardBubble.cpp。
    /// 图标取自插件自带 iconfont.ttf 的私有区字形（0xE647 复制 / 0xE612 上绳 /
    /// 0xE65B 删除 / 0xE61D 完成 / 0xE66D 分类）；字体缺失时自动退回汉字，不崩。
    /// </summary>
    public partial class RackListPanel : UserControl
    {
        public event Action<RackListEntry>? HangRequested;
        public event Action<RackListEntry>? DeleteRequested;
        public event Action? NewNoteRequested;
        public event Action? CloseRequested;

        private static readonly FontFamily? IconFont = TryLoadIconFont();

        private List<RackListEntry> _entries = new();
        private string _search = "";
        private string _category = "全部";
        private bool _suppressRender;
        private double _anchorX = 26;

        private string _panelBg = "#FFFFFFFF";
        private string _cardBg = "#FFF5F4F4";
        private string _btnBg = "#FFF5F4F4";
        private string _fg = "#FF2B2B2B";
        private string _muted = "#FF8A8A8A";
        private string _border = "#33000000";

        public RackPanelMode Mode { get; private set; } = RackPanelMode.None;

        public RackListPanel()
        {
            InitializeComponent();
            UpdateArrow();
        }

        /// <summary>气泡箭头指向左信封或右篮子；篮子模式贴屏幕右侧展开。</summary>
        public void SetAnchor(bool nearLeft, double rightMargin)
        {
            if (nearLeft)
            {
                HorizontalAlignment = HorizontalAlignment.Left;
                Margin = new Thickness(126, 88, 0, 0);
                _anchorX = 26;
            }
            else
            {
                HorizontalAlignment = HorizontalAlignment.Right;
                Margin = new Thickness(0, 88, rightMargin, 0);
                _anchorX = Width - 26;
            }
            UpdateArrow();
        }

        private void UpdateArrow()
        {
            var fig = new PathFigure(
                new Point(_anchorX - 8, 9),
                new[]
                {
                    new LineSegment(new Point(_anchorX, 0), true),
                    new LineSegment(new Point(_anchorX + 8, 9), true),
                },
                true);
            Arrow.Data = new PathGeometry(new[] { fig });
        }

        /// <summary>主题切换时重绘（面板打开状态下设置页改主题也要跟着变）。</summary>
        public void ApplyTheme(RackTheme theme)
        {
            _panelBg = theme.IsDark ? "#FF262626" : "#FFFFFFFF";
            _cardBg = theme.IsDark ? "#FF1A1A1A" : "#FFF5F4F4";
            _btnBg = theme.IsDark ? "#FF3A3A3A" : "#FFF5F4F4";
            _fg = theme.IsDark ? "#FFF2F2F2" : "#FF2B2B2B";
            _muted = theme.IsDark ? "#FFA8A8A8" : "#FF8A8A8A";
            _border = theme.IsDark ? "#44FFFFFF" : "#33000000";

            Bubble.Background = RackTheme.Brush(_panelBg);
            Bubble.BorderBrush = RackTheme.Brush(_border);
            Arrow.Fill = RackTheme.Brush(_panelBg);
            Arrow.Stroke = RackTheme.Brush(_border);

            TitleText.Foreground = RackTheme.Brush(_fg);
            HintText.Foreground = RackTheme.Brush(_muted);
            SearchBox.Background = RackTheme.Brush(_cardBg);
            SearchBox.Foreground = RackTheme.Brush(_fg);
            SearchBox.CaretBrush = RackTheme.Brush(_fg);

            foreach (var b in new[] { CloseBtn, NewBtn, ChipBtn })
            {
                b.Background = RackTheme.Brush(_btnBg);
                b.Foreground = RackTheme.Brush(_fg);
            }
            if (IconFont != null)
            {
                ChipBtn.FontFamily = IconFont;
                ChipBtn.Content = char.ConvertFromUtf32(0xE66D);
                ChipBtn.FontSize = 14;
            }
            else
            {
                ChipBtn.Content = "类";
            }

            if (Mode != RackPanelMode.None) Render();
        }

        public void Show(IReadOnlyList<RackListEntry> entries, RackPanelMode mode, RackTheme theme)
        {
            bool changed = Mode != mode;
            if (changed)
            {
                _category = "全部";
                _search = "";
                _suppressRender = true;
                SearchBox.Text = "";
                _suppressRender = false;
            }

            _entries = entries.ToList();
            Mode = mode;

            bool clip = mode == RackPanelMode.Clipboard;
            TitleText.Text = clip
                ? $"剪贴板 · {_entries.Count} 条（已合并）"
                : $"待办 · {_entries.Count} 条（已合并）";
            HintText.Text = clip
                ? "复制过的内容按内容合并去重，不会自动上绳；点夹子图标或双击才挂。"
                : "待办按标题合并，可筛选未完成 / 已完成；点夹子图标或双击才上绳。";
            SearchBox.ToolTip = clip ? "搜索剪贴板" : "搜索待办";

            Visibility = Visibility.Visible;
            ApplyTheme(theme);
        }

        public void HidePanel()
        {
            Mode = RackPanelMode.None;
            Visibility = Visibility.Collapsed;
            EntryList.Children.Clear();
            ChipBar.Children.Clear();
        }

        private string[] Categories => Mode == RackPanelMode.Todo
            ? new[] { "全部", "未完成", "已完成" }
            : new[] { "全部", "文本", "图片", "文件", "链接", "颜色" };

        private void Render()
        {
            if (_suppressRender) return;

            ChipBar.Children.Clear();
            foreach (var cat in Categories)
            {
                bool selected = string.Equals(cat, _category, StringComparison.Ordinal);
                var chip = new Border
                {
                    Height = 28,
                    CornerRadius = new CornerRadius(8),
                    Background = RackTheme.Brush(selected ? "#FF34C759" : _cardBg),
                    Margin = new Thickness(0, 0, 6, 6),
                    Padding = new Thickness(10, 0, 10, 0),
                    Cursor = Cursors.Hand,
                    Child = new TextBlock
                    {
                        Text = cat,
                        FontSize = 11,
                        VerticalAlignment = VerticalAlignment.Center,
                        Foreground = RackTheme.Brush(selected ? "#FFFFFFFF" : _fg),
                    },
                };
                var name = cat;
                chip.MouseLeftButtonUp += (_, e) =>
                {
                    _category = name;
                    Render();
                    e.Handled = true;
                };
                ChipBar.Children.Add(chip);
            }

            EntryList.Children.Clear();
            var q = (_search ?? "").Trim();
            var shown = _entries.Where(e =>
                    (_category == "全部" || string.Equals(e.Category, _category, StringComparison.Ordinal)) &&
                    (q.Length == 0 ||
                     (e.Title?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false) ||
                     (e.CopyText?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)))
                .Take(40)
                .ToList();

            if (shown.Count == 0)
            {
                EntryList.Children.Add(new TextBlock
                {
                    Text = Mode == RackPanelMode.Clipboard
                        ? "还没有匹配的剪贴板内容 · 复制几段文字后这里会合并显示"
                        : "还没有匹配的待办 · 在待办窗口里加几条后这里会合并显示",
                    FontSize = 12,
                    Foreground = RackTheme.Brush(_muted),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(4, 18, 4, 18),
                });
                return;
            }

            foreach (var entry in shown) EntryList.Children.Add(BuildRow(entry));
        }

        private UIElement BuildRow(RackListEntry e)
        {
            var card = new Border
            {
                Background = RackTheme.Brush(_cardBg),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10),
                Margin = new Thickness(0, 0, 0, 8),
                Cursor = Cursors.Hand,
            };

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.Children.Add(new TextBlock
            {
                Text = e.Tag + " · " + e.Detail + (e.Repeat > 1 ? " · ×" + e.Repeat : ""),
                FontSize = 11,
                Foreground = RackTheme.Brush(_muted),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });

            var actions = new StackPanel { Orientation = Orientation.Horizontal };
            actions.Children.Add(IconButton(0xE647, "复", "复制到剪贴板", () => CopyEntry(e)));
            actions.Children.Add(IconButton(0xE612, "夹", "挂到绳子上", () => HangRequested?.Invoke(e)));
            if (Mode == RackPanelMode.Clipboard && e.ClipboardId.HasValue)
                actions.Children.Add(IconButton(0xE65B, "删", "从剪贴板删除", () => DeleteRequested?.Invoke(e)));
            Grid.SetColumn(actions, 1);
            head.Children.Add(actions);

            Grid.SetRow(head, 0);
            grid.Children.Add(head);

            var body = new TextBlock
            {
                Text = e.Title,
                FontSize = 12,
                Foreground = RackTheme.Brush(_fg),
                TextWrapping = TextWrapping.Wrap,
                MaxHeight = 40,
                Margin = new Thickness(0, 6, 0, 0),
            };
            Grid.SetRow(body, 1);
            grid.Children.Add(body);

            var tag = new Border
            {
                HorizontalAlignment = HorizontalAlignment.Left,
                CornerRadius = new CornerRadius(4),
                Background = RackTheme.Brush(_panelBg),
                Padding = new Thickness(7, 0, 7, 0),
                Height = 16,
                Margin = new Thickness(0, 6, 0, 0),
                Child = new TextBlock
                {
                    Text = e.Category,
                    FontSize = 11,
                    Foreground = RackTheme.Brush(_muted),
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };
            Grid.SetRow(tag, 2);
            grid.Children.Add(tag);

            card.Child = grid;
            card.MouseLeftButtonDown += (_, args) =>
            {
                if (args.ClickCount != 2) return;
                if (args.OriginalSource is DependencyObject d && FindParentButton(d) != null) return;
                HangRequested?.Invoke(e);
                args.Handled = true;
            };
            return card;
        }

        private Button IconButton(uint codepoint, string fallback, string tip, Action onClick)
        {
            var tb = new TextBlock
            {
                Text = IconFont != null ? char.ConvertFromUtf32((int)codepoint) : fallback,
                FontSize = IconFont != null ? 13 : 11,
                Foreground = RackTheme.Brush(_fg),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            if (IconFont != null) tb.FontFamily = IconFont;

            var b = new Button
            {
                Content = tb,
                Width = 20,
                Height = 20,
                Margin = new Thickness(4, 0, 0, 0),
                ToolTip = tip,
                Background = RackTheme.Brush(_btnBg),
                Style = (Style)FindResource("FlatIconButton"),
            };
            b.Click += (_, _) => onClick();
            return b;
        }

        private static void CopyEntry(RackListEntry e)
        {
            try
            {
                if (!string.IsNullOrEmpty(e.CopyText)) System.Windows.Clipboard.SetText(e.CopyText!);
            }
            catch
            {
                // 剪贴板被其它进程占用时静默失败
            }
        }

        private static Button? FindParentButton(DependencyObject? node)
        {
            while (node != null)
            {
                if (node is Button b) return b;
                node = VisualTreeHelper.GetParent(node);
            }
            return null;
        }

        /// <summary>
        /// 从输出目录加载插件自带图标字体。family name 是 "font_family"，
        /// 先验证目标字形确实存在，避免字体加载成功但全是豆腐块。
        /// </summary>
        private static FontFamily? TryLoadIconFont()
        {
            try
            {
                var file = RackTheme.AssetPath("iconfont.ttf");
                if (!File.Exists(file)) return null;
                var glyph = new GlyphTypeface(new Uri(file, UriKind.Absolute));
                if (!glyph.CharacterToGlyphMap.ContainsKey(0xE612)) return null;
                var dir = System.IO.Path.GetDirectoryName(file) + System.IO.Path.DirectorySeparatorChar;
                return new FontFamily(new Uri(dir, UriKind.Absolute), "./#font_family");
            }
            catch
            {
                return null;
            }
        }

        // ══════════════════════════════════════════════
        //  事件
        // ══════════════════════════════════════════════

        private void CloseBtn_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();

        private void NewBtn_Click(object sender, RoutedEventArgs e) => NewNoteRequested?.Invoke();

        private void ChipBtn_Click(object sender, RoutedEventArgs e) =>
            ChipBar.Visibility = ChipBar.Visibility == Visibility.Visible
                ? Visibility.Collapsed
                : Visibility.Visible;

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            _search = SearchBox.Text ?? "";
            if (Mode != RackPanelMode.None) Render();
        }
    }
}
