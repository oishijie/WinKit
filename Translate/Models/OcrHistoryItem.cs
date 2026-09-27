using System;

namespace WinKit.Translate.Models
{
    /// <summary>
    /// 一条 OCR 识别历史。
    ///
    /// 只存文本与元信息，不存截图 —— 图片占地太大，而且用户真正想回溯的是「刚才识别出了什么」。
    /// </summary>
    public class OcrHistoryItem
    {
        /// <summary>唯一标识（GUID 字符串）</summary>
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        /// <summary>识别完成时间（本地时间）</summary>
        public DateTime CreatedAt { get; set; } = DateTime.Now;

        /// <summary>识别出的完整文本（已按版面还原换行）</summary>
        public string Text { get; set; } = string.Empty;

        /// <summary>字符数</summary>
        public int CharCount { get; set; }

        /// <summary>文本框数量</summary>
        public int BlockCount { get; set; }

        /// <summary>识别耗时（毫秒）</summary>
        public long ElapsedMs { get; set; }

        /// <summary>产生这条记录的引擎名（含实际加载的模型）</summary>
        public string Model { get; set; } = string.Empty;

        /// <summary>识别前是否因深色背景做过自动反色</summary>
        public bool AutoInverted { get; set; }

        // ── 界面派生属性 ────────────────────────────────

        /// <summary>单行摘要：把换行压成空格并截断，供列表展示</summary>
        public string Preview
        {
            get
            {
                if (string.IsNullOrEmpty(Text)) return "(空)";

                var flat = Text.Replace("\r", " ").Replace("\n", " ").Trim();
                return flat.Length <= 80 ? flat : flat[..80] + "…";
            }
        }

        /// <summary>时间戳文案</summary>
        public string TimeText => CreatedAt.ToString("MM-dd HH:mm");

        /// <summary>元信息文案，如「128 字 · 6 块 · 320 ms」</summary>
        public string MetaText
        {
            get
            {
                var text = $"{CharCount} 字 · {BlockCount} 块 · {ElapsedMs} ms";
                return AutoInverted ? text + " · 已反色" : text;
            }
        }
    }
}
