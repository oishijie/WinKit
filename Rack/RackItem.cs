using System;
using System.Text.Json.Serialization;
using System.Windows.Media.Imaging;

namespace WinKit.Rack
{
    /// <summary>
    /// 晾衣架卡片类型。前 10 项对齐 SnowAir-Rack 的 CardType 枚举；
    /// <see cref="Todo"/> 是 WinKit 自己的数据源（todos.md）。
    ///
    /// WinKit 目前只有 7 种能产生真实内容：Image / Text / Note / Link / Color / File / Todo。
    /// 其余 4 种保留枚举与类型色定义，但没有数据源，**不会生成空卡**。
    /// </summary>
    public enum RackItemType
    {
        Image = 0,
        Svg = 1,
        Recording = 2,
        Note = 3,
        Text = 4,
        Link = 5,
        Color = 6,
        File = 7,
        Video = 8,
        Music = 9,
        Todo = 10,
    }

    /// <summary>类型扩展：显示名 / 是否照片卡 / 是否为「有数据源」的可落地类型。</summary>
    public static class RackItemTypeEx
    {
        public static string DisplayName(this RackItemType type) => type switch
        {
            RackItemType.Image => "图片",
            RackItemType.Svg => "矢量图",
            RackItemType.Recording => "录屏",
            RackItemType.Note => "便签",
            RackItemType.Text => "文本",
            RackItemType.Link => "链接",
            RackItemType.Color => "颜色",
            RackItemType.File => "文件",
            RackItemType.Video => "视频",
            RackItemType.Music => "音乐",
            RackItemType.Todo => "待办",
            _ => "卡片",
        };

        /// <summary>照片卡：白边 + 夹子；其余为类型色头方卡。</summary>
        public static bool IsPhoto(this RackItemType type) =>
            type is RackItemType.Image or RackItemType.Svg
                or RackItemType.Recording or RackItemType.Video;

        /// <summary>WinKit 能真正产生内容的 7 种。</summary>
        public static bool IsSupported(this RackItemType type) =>
            type is RackItemType.Image or RackItemType.Text or RackItemType.Note
                or RackItemType.Link or RackItemType.Color or RackItemType.File
                or RackItemType.Todo;
    }

    /// <summary>
    /// 晾衣架上的一个挂载条目 —— 同时是 <c>rack_items.json</c> 的一条记录。
    ///
    /// 字段结构对齐 SnowAir-Rack 插件（20 字段）：
    /// aspect 管卡片高度、offsetX 管横向重排、hash 管去重、locked 管固定、
    /// noteId/noteColor 管便签。位图不能进 JSON，按 hash 落到独立 PNG，
    /// <see cref="ImagePath"/> 只存文件名，运行时位图放在 <see cref="Image"/>（不序列化）。
    /// </summary>
    public sealed class RackItem
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public RackItemType CardType { get; set; } = RackItemType.Text;
        public string? Title { get; set; }
        public string? Text { get; set; }
        public string? ImagePath { get; set; }
        public double Aspect { get; set; } = 1.0;
        public double OffsetX { get; set; }
        public string? Hash { get; set; }
        public bool Locked { get; set; }
        public string? NoteId { get; set; }
        public string? NoteColor { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public double Width { get; set; } = 140;
        public double Height { get; set; } = 140;
        public string? Url { get; set; }
        public string? FilePath { get; set; }
        public string? ColorHex { get; set; }
        public bool IsDone { get; set; }
        public bool Pinned { get; set; }
        public int Order { get; set; }

        /// <summary>运行时位图。仅内存，不落 JSON；由 RackItemStore 从 ImagePath 还原。</summary>
        [JsonIgnore]
        public BitmapSource? Image { get; set; }

        [JsonIgnore]
        public RackItemType Type => CardType;   // 兼容旧读法，不落盘

        /// <summary>卡片上显示的主文本（标题优先，其次正文）。</summary>
        [JsonIgnore]
        public string DisplayText => !string.IsNullOrWhiteSpace(Title)
            ? Title!
            : (Text ?? "");

        // ── 兼容旧调用点的工厂 ────────────────────────────

        public static RackItem ImageItem(BitmapSource image) => new()
        {
            CardType = RackItemType.Image,
            Image = image,
            Aspect = image != null && image.Height > 0 ? image.Width / image.Height : 1.0,
        };

        public static RackItem TextItem(string text) => new()
        {
            CardType = RackItemType.Text,
            Text = text,
        };

        public static RackItem TodoItem(string title, bool isDone) => new()
        {
            CardType = RackItemType.Todo,
            Title = title,
            IsDone = isDone,
        };

        public static RackItem NoteItem(string text, string? color = null) => new()
        {
            CardType = RackItemType.Note,
            Text = text,
            NoteColor = color ?? "#F3CD5F",
        };

        public static RackItem LinkItem(string url) => new()
        {
            CardType = RackItemType.Link,
            Text = url,
            Url = url,
        };

        public static RackItem ColorItem(string hex) => new()
        {
            CardType = RackItemType.Color,
            ColorHex = hex,
            Text = hex,
        };

        public static RackItem FileItem(string path) => new()
        {
            CardType = RackItemType.File,
            FilePath = path,
            Title = System.IO.Path.GetFileName(path),
            Text = path,
        };

        /// <summary>
        /// 按文本形态选卡片类型 —— 原「点信封自动判型」的逻辑抽到这里，
        /// 供合并列表点「上绳」时复用：URL→链接、色值→颜色、路径→文件、多行→便签，其余→文本。
        /// </summary>
        public static RackItem FromClipboardText(string? raw)
        {
            var t = (raw ?? "").Trim();
            if (t.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                t.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return LinkItem(t);
            if (IsHexColor(t))
                return ColorItem(t.StartsWith("#") ? t : "#" + t);
            if (t.Length is > 0 and < 260 && System.IO.File.Exists(t.Trim('"')))
                return FileItem(t.Trim('"'));
            if (t.Contains('\n') || t.Contains('\r'))
                return NoteItem(t);
            return TextItem(t);
        }

        private static bool IsHexColor(string s)
        {
            var t = s.StartsWith("#") ? s.Substring(1) : s;
            if (t.Length != 6) return false;
            foreach (var c in t) if (!Uri.IsHexDigit(c)) return false;
            return true;
        }

        /// <summary>生成去重用的内容指纹：图片用路径/时间，文本用内容本身。</summary>
        public string ComputeFingerprint()
        {
            var raw = CardType switch
            {
                RackItemType.Image => "img:" + (Hash ?? ImagePath ?? Id),
                RackItemType.File => "file:" + (FilePath ?? Id),
                _ => (int)CardType + ":" + (Url ?? Text ?? Title ?? Id),
            };
            return raw;
        }
    }
}
