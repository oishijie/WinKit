using System.Windows.Media.Imaging;

namespace WinKit.Rack
{
    /// <summary>晾衣架挂载条目的类型。</summary>
    public enum RackItemType
    {
        /// <summary>图片（截图 / 剪贴板图片）。</summary>
        Image,
        /// <summary>普通文本（剪贴板文本）。</summary>
        Text,
        /// <summary>待办条目。</summary>
        Todo,
    }

    /// <summary>
    /// 晾衣架上的一个挂载条目。
    ///
    /// MVP 版不做持久化（重启清空），所以这里只有运行时数据。
    /// OffsetX 是沿绳子的水平偏移（像素），供卡片横向排列/拖动使用。
    /// </summary>
    public sealed class RackItem
    {
        public RackItemType Type { get; }

        /// <summary>文本卡：正文；待办卡：标题。图片卡为 null。</summary>
        public string? Text { get; }

        /// <summary>图片卡：位图。文本/待办卡为 null。</summary>
        public BitmapSource? Image { get; }

        /// <summary>待办卡：是否已完成（决定勾选样式）。</summary>
        public bool IsDone { get; }

        /// <summary>沿绳子的水平偏移（像素），用于横向排列与拖动。</summary>
        public double OffsetX { get; set; }

        private RackItem(RackItemType type, string? text, BitmapSource? image, bool isDone)
        {
            Type = type;
            Text = text;
            Image = image;
            IsDone = isDone;
        }

        public static RackItem ImageItem(BitmapSource image) =>
            new(RackItemType.Image, null, image, false);

        public static RackItem TextItem(string text) =>
            new(RackItemType.Text, text, null, false);

        public static RackItem TodoItem(string title, bool isDone) =>
            new(RackItemType.Todo, title, null, isDone);
    }
}
