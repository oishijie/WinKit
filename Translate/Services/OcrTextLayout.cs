using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using PaddleOCRSharp;

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
    ///   4. 行与行之间用换行符连接。
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

        /// <summary>把文本框集合重建为带换行的纯文本，同时输出逐行结果</summary>
        public static (string Text, IReadOnlyList<string> Lines) Compose(IEnumerable<TextBlock>? blocks)
        {
            var fragments = ToFragments(blocks);
            if (fragments.Count == 0)
                return (string.Empty, Array.Empty<string>());

            var lines = GroupIntoLines(fragments)
                .Select(BuildLine)
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .ToList();

            return (string.Join(Environment.NewLine, lines), lines);
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
    }
}
