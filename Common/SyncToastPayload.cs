using System;

namespace WinKit.Common
{
    /// <summary>通知卡片的类型（决定图标配色与按钮集）</summary>
    public enum SyncToastKind
    {
        Text,
        Image,
        File,
        Group
    }

    /// <summary>卡片上可执行的本地动作</summary>
    public enum ToastAction
    {
        /// <summary>把内容重新写回系统剪贴板</summary>
        Copy,
        /// <summary>复制一份到系统「下载」文件夹并在资源管理器中选中</summary>
        SaveToDownloads,
        /// <summary>用默认程序打开文件</summary>
        Open,
        /// <summary>在资源管理器中定位文件或文件夹</summary>
        OpenFolder,
        /// <summary>唤出 WinKit 剪贴板面板看全文</summary>
        OpenClipboardPanel
    }

    /// <summary>
    /// 同步通知的结构化载荷。
    /// 事件签名从纯字符串升级成它，是为了让卡片上的按钮能真正操作内容
    /// （字符串里拿不到文件路径，按钮点了也没东西可动）。
    /// </summary>
    public sealed class SyncToastPayload
    {
        /// <summary>内容类型，决定图标与按钮集</summary>
        public SyncToastKind Kind { get; init; }

        /// <summary>true = 本地推出去；false = 远端拉进来</summary>
        public bool IsPush { get; init; }

        /// <summary>主标题，如「收到远端文件」</summary>
        public string Title { get; init; } = string.Empty;

        /// <summary>副标题，文件名 / 尺寸 / 文本预览</summary>
        public string Subtitle { get; init; } = string.Empty;

        /// <summary>文本内容（Kind = Text 时有值）</summary>
        public string? Text { get; init; }

        /// <summary>本地文件路径（图片 / 文件 / 多文件组的解包目录）</summary>
        public string? FilePath { get; init; }

        /// <summary>图片原始字节（用于卡片缩略图）</summary>
        public byte[]? ImageBytes { get; init; }
    }
}
