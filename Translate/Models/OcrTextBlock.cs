using System;

namespace WinKit.Translate.Models
{
    /// <summary>
    /// 单个 OCR 文本框：识别文字 + 它在**原图坐标系**中的像素包围盒。
    ///
    /// 坐标系约定（重要）：
    ///   所有 Left/Top/Right/Bottom 都是相对「传给 <c>IOcrProvider.RecognizeAsync</c> 的那张位图」
    ///   的像素值，原点在左上角。识别前引擎会对位图做缩放（小图放大、超大图缩小），
    ///   引擎回传的坐标位于缩放后的空间，<c>OcrTextLayout</c> 会按预处理比例换算回原图，
    ///   因此这里拿到的是**可直接用于在原图上作画**的坐标，无需再关心预处理细节。
    /// </summary>
    public class OcrTextBlock
    {
        /// <summary>该框识别出的文字（已去除首尾空白）</summary>
        public string Text { get; set; } = string.Empty;

        /// <summary>包围盒左边界（原图像素）</summary>
        public int Left { get; set; }

        /// <summary>包围盒上边界（原图像素）</summary>
        public int Top { get; set; }

        /// <summary>包围盒右边界（原图像素）</summary>
        public int Right { get; set; }

        /// <summary>包围盒下边界（原图像素）</summary>
        public int Bottom { get; set; }

        /// <summary>
        /// 所属版面行序号（自 0 起，自上而下）。同一行的多个文本框共享同一个值，
        /// 可据此把碎片拼回整行 —— 这是「原文译文逐行对照高亮」的关键。
        ///
        /// 注意：版面行由几何位置聚合而来（见 <c>OcrTextLayout.GroupIntoLines</c>），
        /// 与 <see cref="OcrResult.Lines"/> 不保证一一对应 —— 后者额外做过段落合并
        /// （把 OCR 硬换行并回连续段落），会改变行数。
        /// </summary>
        public int LineIndex { get; set; } = -1;

        /// <summary>包围盒宽度（像素）</summary>
        public int Width => Math.Max(0, Right - Left);

        /// <summary>包围盒高度（像素）</summary>
        public int Height => Math.Max(0, Bottom - Top);

        /// <summary>包围盒水平中心</summary>
        public double CenterX => (Left + Right) / 2.0;

        /// <summary>包围盒垂直中心</summary>
        public double CenterY => (Top + Bottom) / 2.0;

        /// <summary>调试/导出用的紧凑表示：<c>文本 [x,y,w,h]</c></summary>
        public override string ToString() => $"{Text} [{Left},{Top},{Width}x{Height}]";
    }
}
