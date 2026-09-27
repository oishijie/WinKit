using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using PaddleOCRSharp;
using WinKit.Translate.Models;

namespace WinKit.Translate.Services
{
    /// <summary>
    /// 版面还原。
    ///
    /// PaddleOCR 返回的是一组彼此独立的文本框，而 <c>OCRResult.Text</c> 只是把它们
    /// 按检测顺序**无分隔地首尾相连**（实测："本地离线识别测试" + "WinKit Local OCR 2026"
    /// 会拼成一整行）。直接拿去翻译会丢掉全部换行语义、严重影响译文质量。
    ///
    /// 这里按文本框的几何位置重建阅读顺序：
    ///   1. 纵向重叠度足够的文本框归为同一行；
    ///   2. 行内按横坐标从左到右排序；
    ///   3. 行内相邻片段之间，中日韩字符直接相接，其余补空格；
    ///   4. 行与行之间用换行符连接；
    ///   5. 最后将段落内的硬换行合并为连续文本（修复 OCR 拆行）。
    /// </summary>
    internal static class OcrTextLayout
    {
        /// <summary>纵向重叠比例超过该阈值即认为属于同一行</summary>
        private const double SameLineOverlapRatio = 0.5;

        private sealed class Fragment
        {
            public string Text = string.Empty;
            public int Left, Top, Right, Bottom;
            public int Height => Math.Max(1, Bottom - Top);
            public double CenterY => (Top + Bottom) / 2.0;
        }

        /// <summary>
        /// 版面还原的完整产物：纯文本 + 段落化行列表 + 带原图坐标的文本框。
        /// </summary>
        internal sealed record OcrLayout(
            string Text,
            IReadOnlyList<string> Lines,
            IReadOnlyList<OcrTextBlock> Blocks);

        /// <summary>
        /// 把文本框集合重建为带换行的纯文本，同时输出逐行结果与带坐标的文本框。
        /// </summary>
        /// <param name="blocks">引擎原始文本框（坐标位于预处理后的位图空间）</param>
        /// <param name="sourceWidth">传入引擎的**原图**宽度，用于坐标反映射后的边界收敛</param>
        /// <param name="sourceHeight">传入引擎的**原图**高度，同上</param>
        /// <param name="scale">预处理时的缩放系数（原图 → 送入引擎的位图），默认 1 表示未缩放</param>
        public static OcrLayout Compose(
            IEnumerable<TextBlock>? blocks,
            int sourceWidth = 0,
            int sourceHeight = 0,
            double scale = 1.0)
        {
            var fragments = ToFragments(blocks);
            if (fragments.Count == 0)
                return new OcrLayout(string.Empty, Array.Empty<string>(), Array.Empty<OcrTextBlock>());

            var grouped = GroupIntoLines(fragments);

            var rawLines = grouped
                .Select(BuildLine)
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .ToList();

            // 段落合并：将 OCR 硬换行修复为连续段落
            var repaired = RepairLineBreaks(rawLines);

            // 按阅读顺序摊平文本框，并把坐标从「预处理后空间」换算回「原图空间」。
            // grouped 的行序即自上而下的阅读顺序，行内已按 Left 排好，逐行摊平天然有序。
            var mapped = new List<OcrTextBlock>(fragments.Count);
            for (int lineIndex = 0; lineIndex < grouped.Count; lineIndex++)
            {
                foreach (var f in grouped[lineIndex])
                {
                    mapped.Add(new OcrTextBlock
                    {
                        Text = f.Text,
                        Left = MapCoordinate(f.Left, scale, sourceWidth),
                        Top = MapCoordinate(f.Top, scale, sourceHeight),
                        Right = MapCoordinate(f.Right, scale, sourceWidth),
                        Bottom = MapCoordinate(f.Bottom, scale, sourceHeight),
                        LineIndex = lineIndex,
                    });
                }
            }

            return new OcrLayout(string.Join(Environment.NewLine, repaired), repaired, mapped);
        }

        /// <summary>
        /// 把预处理位图中的坐标换算回原图坐标：除以缩放系数，并收敛到原图范围内。
        ///
        /// 必须收敛的原因：放大时双线性插值会让边缘框略微溢出；
        /// 而 <c>EnableDetUseRect</c> 的矩形矫正也可能把框推到边界外。
        /// 越界坐标会让下游（在截图上作画）画出画面外的图形。
        /// </summary>
        private static int MapCoordinate(int value, double scale, int limit)
        {
            if (scale <= 0) scale = 1.0;

            int mapped = (int)Math.Round(value / scale);
            if (mapped < 0) return 0;
            if (limit > 0 && mapped > limit) return limit;
            return mapped;
        }

        // ── 1. 归一化为带包围盒的片段 ────────────────────────────

        private static List<Fragment> ToFragments(IEnumerable<TextBlock>? blocks)
        {
            var list = new List<Fragment>();
            if (blocks == null) return list;

            foreach (var b in blocks)
            {
                if (b == null || string.IsNullOrWhiteSpace(b.Text)) continue;

                var points = b.BoxPoints;
                if (points == null || points.Count == 0)
                {
                    // 没有坐标信息时按出现顺序单独成行，至少不会串行
                    list.Add(new Fragment { Text = b.Text.Trim(), Left = 0, Top = list.Count * 1000, Right = 1, Bottom = list.Count * 1000 + 1 });
                    continue;
                }

                list.Add(new Fragment
                {
                    Text = b.Text.Trim(),
                    Left = points.Min(p => p.X),
                    Top = points.Min(p => p.Y),
                    Right = points.Max(p => p.X),
                    Bottom = points.Max(p => p.Y),
                });
            }

            return list;
        }

        // ── 2. 纵向聚合成行 ──────────────────────────────────────

        private static List<List<Fragment>> GroupIntoLines(List<Fragment> fragments)
        {
            var ordered = fragments.OrderBy(f => f.CenterY).ThenBy(f => f.Left).ToList();
            var lines = new List<List<Fragment>>();

            List<Fragment>? current = null;
            int lineTop = 0, lineBottom = 0;

            foreach (var f in ordered)
            {
                if (current == null || !IsSameLine(lineTop, lineBottom, f))
                {
                    current = new List<Fragment>();
                    lines.Add(current);
                    lineTop = f.Top;
                    lineBottom = f.Bottom;
                }
                else
                {
                    // 并入当前行后扩展行包围盒，便于后续片段继续比对
                    lineTop = Math.Min(lineTop, f.Top);
                    lineBottom = Math.Max(lineBottom, f.Bottom);
                }

                current.Add(f);
            }

            foreach (var line in lines)
                line.Sort((a, b) => a.Left.CompareTo(b.Left));

            return lines;
        }

        /// <summary>用纵向重叠比例判断是否同一行，比单纯比较中心点更抗字号差异</summary>
        private static bool IsSameLine(int lineTop, int lineBottom, Fragment f)
        {
            int overlap = Math.Min(lineBottom, f.Bottom) - Math.Max(lineTop, f.Top);
            if (overlap <= 0) return false;

            int shorter = Math.Min(Math.Max(1, lineBottom - lineTop), f.Height);
            return (double)overlap / shorter >= SameLineOverlapRatio;
        }

        // ── 3. 行内拼接 ──────────────────────────────────────────

        private static string BuildLine(List<Fragment> line)
        {
            var sb = new StringBuilder();

            for (int i = 0; i < line.Count; i++)
            {
                var text = line[i].Text;
                if (text.Length == 0) continue;

                if (sb.Length > 0 && NeedsSpace(sb[sb.Length - 1], text[0]))
                    sb.Append(' ');

                sb.Append(text);
            }

            return sb.ToString().Trim();
        }

        /// <summary>中日韩字符之间不补空格，其余情况补一个空格以免单词粘连</summary>
        private static bool NeedsSpace(char left, char right)
        {
            if (char.IsWhiteSpace(left) || char.IsWhiteSpace(right)) return false;
            if (IsCjk(left) && IsCjk(right)) return false;
            return true;
        }

        private static bool IsCjk(char c) =>
            (c >= 0x2E80 && c <= 0x9FFF) ||   // 中日韩部首扩展 ~ 中日韩统一表意文字
            (c >= 0x3000 && c <= 0x303F) ||   // 中日韩符号与标点
            (c >= 0xAC00 && c <= 0xD7AF) ||   // 谚文音节
            (c >= 0xFF00 && c <= 0xFFEF);     // 全角字符

        // ── 4. 段落合并（修复 OCR 硬换行） ───────────────────────

        /// <summary>
        /// 将 OCR 产生的硬换行修复为连续段落。
        ///
        /// 规则：
        ///   1. 空行视为真实段落分隔，予以保留；
        ///   2. 当前行以句末标点（。！？.!?）结尾 → 视为段落结束，不合并；
        ///   3. 下一行以首行缩进开头 → 视为新段落，不合并；
        ///   4. 其余情况将下一行接到当前行末尾（中日韩直接连接，其余补空格）。
        /// </summary>
        private static List<string> RepairLineBreaks(List<string> rawLines)
        {
            if (rawLines.Count <= 1) return rawLines;

            var paragraphs = new List<string>();
            var sb = new StringBuilder();
            sb.Append(rawLines[0]);

            for (int i = 1; i < rawLines.Count; i++)
            {
                var prev = sb.ToString();
                var curr = rawLines[i];

                // 空行 → 段落分隔：flush 当前段落，保留空行
                if (string.IsNullOrWhiteSpace(curr))
                {
                    paragraphs.Add(prev.TrimEnd());
                    paragraphs.Add("");
                    sb.Clear();
                    continue;
                }

                // 上一行以句末标点结尾 → 段落结束
                if (EndsWithSentenceEnd(prev))
                {
                    paragraphs.Add(prev.TrimEnd());
                    sb.Clear();
                    sb.Append(curr);
                    continue;
                }

                // 下一行以首行缩进开头 → 新段落
                if (StartsWithIndent(curr))
                {
                    paragraphs.Add(prev.TrimEnd());
                    sb.Clear();
                    sb.Append(curr);
                    continue;
                }

                // 否则合并：中日韩直接连接，其余补空格
                if (sb.Length > 0 && curr.Length > 0)
                {
                    char lastChar = sb[sb.Length - 1];
                    char firstChar = curr[0];
                    if (!IsCjk(lastChar) || !IsCjk(firstChar))
                        sb.Append(' ');
                }
                sb.Append(curr);
            }

            // flush 最后一段
            if (sb.Length > 0)
                paragraphs.Add(sb.ToString().TrimEnd());

            // 清理连续空行
            return paragraphs
                .Where((p, idx) => !string.IsNullOrEmpty(p) || idx > 0 && !string.IsNullOrEmpty(paragraphs[idx - 1]))
                .ToList();
        }

        /// <summary>行尾是否为句末标点（。！？.!?）</summary>
        private static bool EndsWithSentenceEnd(string line)
        {
            if (string.IsNullOrEmpty(line)) return false;
            char last = line[line.Length - 1];
            return last == '。' || last == '！' || last == '？'
                || last == '.' || last == '!' || last == '?';
        }

        /// <summary>行首是否为段落缩进（全角空格 / 2+ 半角空格 / 全角空白开头）</summary>
        private static bool StartsWithIndent(string line)
        {
            if (string.IsNullOrEmpty(line)) return false;
            // 两个全角空格（中文常见首行缩进）
            if (line.Length >= 2 && line[0] == '\u3000' && line[1] == '\u3000')
                return true;
            // 2+ 个半角空格
            if (line.Length >= 2 && line[0] == ' ' && line[1] == ' ')
                return true;
            return false;
        }
    }
}
