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

        /// <summary>逐行文本，便于界面按行展示</summary>
        public IReadOnlyList<string> Lines { get; set; } = Array.Empty<string>();

        /// <summary>识别是否成功</summary>
        public bool Success { get; set; }

        /// <summary>失败时的错误信息</summary>
        public string? Error { get; set; }

        /// <summary>本次识别耗时（毫秒）</summary>
        public long ElapsedMs { get; set; }

        /// <summary>识别出的文本框数量</summary>
        public int BlockCount { get; set; }

        public static OcrResult Ok(string text) =>
            new() { Text = text, Success = true };

        public static OcrResult Ok(string text, IReadOnlyList<string> lines, long elapsedMs, int blockCount) =>
            new()
            {
                Text = text,
                Lines = lines,
                Success = true,
                ElapsedMs = elapsedMs,
                BlockCount = blockCount,
            };

        public static OcrResult Fail(string error) =>
            new() { Success = false, Error = error };
    }
}
