using System;
using System.Collections.Generic;

namespace WinKit.Translate.Models
{
    /// <summary>
    /// OCR 识别结果
    /// </summary>
    public class OcrResult
    {
        /// <summary>识别出的文本（已按版面还原换行）</summary>
        public string Text { get; set; } = string.Empty;

        /// <summary>逐行文本，便于界面按行展示（已做段落合并，行数可能少于版面行数）</summary>
        public IReadOnlyList<string> Lines { get; set; } = Array.Empty<string>();

        /// <summary>
        /// 识别出的全部文本框，含各自在**原图坐标系**中的像素包围盒。
        ///
        /// 这是位置相关能力的共同地基 —— 原文译文逐行对照高亮、把译文叠回截图、
        /// 点击某一块单独复制、带坐标导出等，都依赖这里的坐标。
        /// 按阅读顺序（自上而下、行内自左向右）排列。
        /// </summary>
        public IReadOnlyList<OcrTextBlock> Blocks { get; set; } = Array.Empty<OcrTextBlock>();

        /// <summary>识别是否成功</summary>
        public bool Success { get; set; }

        /// <summary>失败时的错误信息</summary>
        public string? Error { get; set; }

        /// <summary>本次识别耗时（毫秒）</summary>
        public long ElapsedMs { get; set; }

        /// <summary>识别出的文本框数量</summary>
        public int BlockCount { get; set; }

        /// <summary>
        /// 本次识别前是否因检出深色背景而自动反色。
        /// 供状态栏展示，便于用户确认该预处理是否按预期生效。
        /// </summary>
        public bool AutoInverted { get; set; }

        public static OcrResult Ok(string text) =>
            new() { Text = text, Success = true };

        public static OcrResult Ok(string text, IReadOnlyList<string> lines, long elapsedMs, int blockCount,
                                   bool autoInverted = false, IReadOnlyList<OcrTextBlock>? blocks = null) =>
            new()
            {
                Text = text,
                Lines = lines,
                Success = true,
                ElapsedMs = elapsedMs,
                BlockCount = blockCount,
                AutoInverted = autoInverted,
                Blocks = blocks ?? Array.Empty<OcrTextBlock>(),
            };

        public static OcrResult Fail(string error) =>
            new() { Success = false, Error = error };
    }
}
