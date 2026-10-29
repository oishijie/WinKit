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

        /// <summary>
        /// 全部文本框的平均置信度（0~1）。无块时为 0。
        ///
        /// 这是「这次识别有多可信」的单一量化指标：
        ///   · ≥0.90 通常意味着文字清晰、结果可直接使用；
        ///   · 0.70~0.90 属于常见范围（截图含抗锯齿小字、混排时略低）；
        ///   · &lt;0.70 建议提示用户核对，往往对应模糊图、极端小字或深浅混排背景。
        /// </summary>
        public double AverageConfidence { get; set; }

        /// <summary>
        /// 本次是否启用了「小字增强」（放大后二次识别）。
        /// 触发条件是首轮检出框偏小；状态栏据此提示，便于用户理解耗时为何偏长。
        /// </summary>
        public bool SmallTextBoosted { get; set; }

        /// <summary>
        /// 被置信度阈值过滤掉的候选框数量。
        /// 典型来源是图标、边框、噪点被误检成文字；数字偏大说明画面纹理复杂。
        /// </summary>
        public int FilteredBlocks { get; set; }

        public static OcrResult Ok(string text) =>
            new() { Text = text, Success = true };

        public static OcrResult Ok(
            string text,
            IReadOnlyList<string> lines,
            long elapsedMs,
            int blockCount,
            bool autoInverted = false,
            IReadOnlyList<OcrTextBlock>? blocks = null,
            double averageConfidence = 0,
            bool smallTextBoosted = false,
            int filteredBlocks = 0) =>
            new()
            {
                Text = text,
                Lines = lines,
                Success = true,
                ElapsedMs = elapsedMs,
                BlockCount = blockCount,
                AutoInverted = autoInverted,
                Blocks = blocks ?? Array.Empty<OcrTextBlock>(),
                AverageConfidence = averageConfidence,
                SmallTextBoosted = smallTextBoosted,
                FilteredBlocks = filteredBlocks,
            };

        public static OcrResult Fail(string error) =>
            new() { Success = false, Error = error };
    }
}
