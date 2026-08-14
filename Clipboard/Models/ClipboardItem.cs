using System;
using System.IO;

namespace WinKit.Clipboard.Models
{
    /// <summary>
    /// 剪贴板项目类型
    /// </summary>
    public enum ClipboardItemType
    {
        Text,
        Image
    }

    /// <summary>
    /// 剪贴板项目实体
    /// </summary>
    public class ClipboardItem
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public ClipboardItemType Type { get; set; }
        public string? Content { get; set; }
        public string? ContentHash { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.Now;

        /// <summary>是否为图片类型（供 XAML DataTrigger 绑定）</summary>
        public bool IsImage => Type == ClipboardItemType.Image;

        /// <summary>
        /// 图片文件的绝对路径（仅 Type=Image 时有效）
        /// </summary>
        public string? ImagePath
        {
            get
            {
                if (Type != ClipboardItemType.Image || string.IsNullOrEmpty(Content))
                    return null;
                // Content 存储的是相对路径，拼接为绝对路径
                return Path.IsPathRooted(Content)
                    ? Content
                    : Path.Combine(Common.AppPaths.Images, Content);
            }
        }

        /// <summary>
        /// 获取显示文本（用于列表展示）
        /// </summary>
        public string DisplayText
        {
            get
            {
                if (Type == ClipboardItemType.Image)
                {
                    var exists = ImagePath != null && File.Exists(ImagePath);
                    return exists ? "🖼 图片" : "🖼 图片（已失效）";
                }

                var text = Content ?? string.Empty;
                // 去除首尾多余空白字符
                text = text.Trim();
                if (text.Length > 100)
                {
                    text = text.Substring(0, 100) + "...";
                }
                return text.Replace("\r", " ").Replace("\n", " ");
            }
        }

        /// <summary>
        /// 获取估计内存大小
        /// </summary>
        public long EstimatedSize
        {
            get
            {
                if (Type == ClipboardItemType.Image && ImagePath != null && File.Exists(ImagePath))
                {
                    try { return new FileInfo(ImagePath).Length; }
                    catch { return 0; }
                }
                return Content?.Length * 2 ?? 0; // UTF-16 characters
            }
        }
    }
}
