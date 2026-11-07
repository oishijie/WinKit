using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace WinKit.Rack
{
    /// <summary>
    /// 晾衣架卡片：照片卡 / 类型方卡两套模板。
    /// 本类只负责渲染与右键菜单；拖动重排、拖出、点击复制由 RackWindow 统一挂载事件，
    /// 这样布局信息集中在窗口里，不会出现「卡片算一套坐标、窗口算另一套」的分裂。
    /// </summary>
    public partial class RackCard : UserControl
    {
        public RackItem Item { get; }

        public event EventHandler? RemoveRequested;
        public event EventHandler? LockRequested;

        private RackTheme _theme = RackTheme.All[0];

        /// <summary>插件自带 letter.png 信纸（288x312），静态缓存，不每张卡重复读盘。</summary>
        private static readonly BitmapSource? LetterBitmap = LoadLetter();

        private static BitmapSource? LoadLetter()
        {
            try
            {
                var file = RackTheme.AssetPath("letter.png");
                if (!File.Exists(file)) return null;
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(file, UriKind.Absolute);
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch
            {
                return null;
            }
        }

        public RackCard(RackItem item)
        {
            InitializeComponent();
            Item = item;
            ApplyTheme(_theme);
            Render();
        }

        /// <summary>切换主题（4 套）。类型色不受主题影响，始终按 CardType 走。</summary>
        public void ApplyTheme(RackTheme theme)
        {
            _theme = theme;
            PhotoOuter.Background = RackTheme.Brush(theme.CardBack);
            PhotoOuter.BorderBrush = RackTheme.Brush(theme.CardBorder);
            SquareBody.Background = RackTheme.Brush(theme.CardBack);
            SquareBody.BorderBrush = RackTheme.Brush(theme.CardBorder);
            OuterRing.BorderBrush = RackTheme.Brush(theme.IsDark ? "#33FFFFFF" : "#22000000");
            LetterBg.Source = LetterBitmap;
            TextContent.Foreground = RackTheme.Brush(theme.TextColor);
            TypeLabel.Foreground = RackTheme.Brush(theme.MutedText);
            ClipBody.Background = RackTheme.Brush(theme.HangerColor);
            Render();
        }

        /// <summary>设置页「显示夹子」开关。</summary>
        public void SetClipVisible(bool visible) =>
            ClipBody.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

        public void Render()
        {
            bool photo = Item.CardType.IsPhoto();
            PhotoOuter.Visibility = photo ? Visibility.Visible : Visibility.Collapsed;
            SquareCard.Visibility = photo ? Visibility.Collapsed : Visibility.Visible;
            LockBadge.Visibility = Item.Locked ? Visibility.Visible : Visibility.Collapsed;
            MenuLock.Header = Item.Locked ? "解锁卡片" : "锁定卡片";
            ToolTip = Item.CardType.DisplayName() + " · " + ShortText();

            if (photo)
            {
                LetterBg.Visibility = Visibility.Collapsed;
                ImgPreview.Source = Item.Image;
                return;
            }

            // 浅色主题下文本 / 便签 / 待办卡用官方 letter.png 信纸作底；
            // 信纸自带两条灰色占位线，内容整体下移避开，文字换成深色保证可读。
            bool useLetter = !_theme.IsDark && LetterBitmap != null &&
                             Item.CardType is RackItemType.Text or RackItemType.Note or RackItemType.Todo;
            LetterBg.Visibility = useLetter ? Visibility.Visible : Visibility.Collapsed;
            SquareBody.Background = useLetter ? Brushes.Transparent : RackTheme.Brush(_theme.CardBack);
            TextContent.Foreground = RackTheme.Brush(useLetter ? "#FF3A3226" : _theme.TextColor);
            TypeLabel.Foreground = RackTheme.Brush(useLetter ? "#FF8C8478" : _theme.MutedText);
            ContentStack.Margin = useLetter ? new Thickness(9, 26, 9, 4) : new Thickness(9, 7, 9, 4);

            var color = RackTheme.TypeColor(Item.CardType);
            TypeBar.Background = RackTheme.Brush(color ?? "#00000000");
            TypeLabel.Text = Item.CardType.DisplayName();
            TypeBar.Visibility = color == null ? Visibility.Collapsed : Visibility.Visible;

            TextContent.Text = ShortText();
            TextContent.TextDecorations = Item.IsDone ? TextDecorations.Strikethrough : null;
            DoneMark.Visibility = Item.CardType == RackItemType.Todo && Item.IsDone
                ? Visibility.Visible : Visibility.Collapsed;

            if (Item.CardType == RackItemType.Color && !string.IsNullOrWhiteSpace(Item.ColorHex))
            {
                ColorSwatch.Visibility = Visibility.Visible;
                ColorSwatch.Background = RackTheme.Brush(Item.ColorHex!);
            }
            else if (Item.CardType == RackItemType.Note && !string.IsNullOrWhiteSpace(Item.NoteColor))
            {
                // 便签：色头跟随 noteColor，底色留主题白（不做整块染色，保持和官方一致）
                TypeBar.Background = RackTheme.Brush(Item.NoteColor!);
                ColorSwatch.Visibility = Visibility.Collapsed;
            }
            else
            {
                ColorSwatch.Visibility = Visibility.Collapsed;
            }
        }

        private string ShortText()
        {
            var s = Item.DisplayText;
            if (string.IsNullOrWhiteSpace(s)) return Item.CardType.DisplayName();
            s = s.Replace("\r", " ").Replace("\n", " ").Trim();
            return s.Length > 90 ? s.Substring(0, 90) + "…" : s;
        }

        /// <summary>
        /// 重复内容只晃一下原卡（对齐插件「同正文不新增卡、只晃原卡」的反馈），
        /// 让用户知道「这条已经在绳上了」，而不是以为点了没反应。
        /// </summary>
        public void Shake()
        {
            if (RenderTransform is not TranslateTransform t)
            {
                t = new TranslateTransform();
                RenderTransform = t;
                RenderTransformOrigin = new Point(0.5, 0.5);
            }

            var anim = new DoubleAnimationUsingKeyFrames
            {
                Duration = TimeSpan.FromMilliseconds(340),
                FillBehavior = FillBehavior.Stop,
            };
            anim.KeyFrames.Add(new LinearDoubleKeyFrame(-6, KeyTime.FromPercent(0.0)));
            anim.KeyFrames.Add(new LinearDoubleKeyFrame(6, KeyTime.FromPercent(0.25)));
            anim.KeyFrames.Add(new LinearDoubleKeyFrame(-4, KeyTime.FromPercent(0.5)));
            anim.KeyFrames.Add(new LinearDoubleKeyFrame(4, KeyTime.FromPercent(0.75)));
            anim.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(1.0)));
            t.BeginAnimation(TranslateTransform.XProperty, anim);
        }

        /// <summary>点击复制（RackWindow 在没有发生拖动时调用）。</summary>
        public void CopyToClipboard()
        {
            try
            {
                if (Item.CardType == RackItemType.Image && Item.Image != null)
                    System.Windows.Clipboard.SetImage(Item.Image);
                else if (!string.IsNullOrEmpty(Item.DisplayText))
                    System.Windows.Clipboard.SetText(Item.DisplayText);
            }
            catch
            {
                // 剪贴板被其它进程占用时静默失败，不能让托盘进程崩掉
            }
        }

        /// <summary>纵向拖出：一律 COPY，绝不 MOVE（避免搬走宿主数据）。</summary>
        public void DragOut()
        {
            try
            {
                DataObject data;
                if (Item.CardType == RackItemType.Image && Item.Image != null)
                {
                    data = new DataObject();
                    data.SetImage(Item.Image);
                }
                else
                {
                    data = new DataObject(DataFormats.UnicodeText, Item.DisplayText ?? "");
                    if (!string.IsNullOrWhiteSpace(Item.FilePath) &&
                        System.IO.File.Exists(Item.FilePath))
                    {
                        var files = new System.Collections.Specialized.StringCollection { Item.FilePath };
                        data.SetFileDropList(files);
                    }
                }
                System.Windows.DragDrop.DoDragDrop(this, data, DragDropEffects.Copy);
            }
            catch
            {
                // 拖拽目标拒绝 / 剪贴板异常都忽略
            }
        }

        private void MenuCopy_Click(object sender, RoutedEventArgs e) => CopyToClipboard();

        private void MenuLock_Click(object sender, RoutedEventArgs e) => LockRequested?.Invoke(this, EventArgs.Empty);

        private void MenuRemove_Click(object sender, RoutedEventArgs e) => RemoveRequested?.Invoke(this, EventArgs.Empty);
    }
}
