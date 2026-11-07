using System;
using System.IO;
using System.Windows.Media;

namespace WinKit.Rack
{
    /// <summary>
    /// 晾衣架主题。4 套参数对齐 SnowAir-Rack 的 <c>rack.theme</c>：
    /// 石墨 / 纸白 / 赤陶 / 深海。素材文件名里的数字就是主题下标。
    /// </summary>
    public sealed class RackTheme
    {
        public string Key { get; init; } = "graphite";
        public string Name { get; init; } = "";
        public bool IsDark { get; init; }

        /// <summary>横杆颜色。</summary>
        public string RopeColor { get; init; } = "#8A6A4A";

        /// <summary>卡片底色 / 外圈 / 文字。</summary>
        public string CardBack { get; init; } = "#FFFFFF";
        public string CardBorder { get; init; } = "#33000000";
        public string TextColor { get; init; } = "#222222";
        public string MutedText { get; init; } = "#888888";

        /// <summary>夹子 / 挂钩颜色。</summary>
        public string HangerColor { get; init; } = "#C9A227";

        public string BasketAsset { get; init; } = "";
        public string EnvelopeBackAsset { get; init; } = "";
        public string EnvelopeFrontAsset { get; init; } = "";
        public string PreviewAsset { get; init; } = "";

        public static readonly RackTheme[] All =
        {
            new()
            {
                Key = "graphite", Name = "石墨", IsDark = true,
                RopeColor = "#C9BBA6", CardBack = "#2A2D31", CardBorder = "#33FFFFFF",
                TextColor = "#F2F2F2", MutedText = "#A8A8A8", HangerColor = "#D8B45A",
                BasketAsset = "basket_0.png",
                EnvelopeBackAsset = "envelope_0_back.png",
                EnvelopeFrontAsset = "envelope_0_front.png",
                PreviewAsset = "theme_0.png",
            },
            new()
            {
                Key = "paper", Name = "纸白", IsDark = false,
                RopeColor = "#9A7B55", CardBack = "#FFFCF5", CardBorder = "#33000000",
                TextColor = "#2B2721", MutedText = "#8C8478", HangerColor = "#C9A227",
                BasketAsset = "basket_1.png",
                EnvelopeBackAsset = "envelope_1_back.png",
                EnvelopeFrontAsset = "envelope_1_front.png",
                PreviewAsset = "theme_1.png",
            },
            new()
            {
                Key = "terracotta", Name = "赤陶", IsDark = false,
                RopeColor = "#B4603C", CardBack = "#FFF8F0", CardBorder = "#33B4603C",
                TextColor = "#4A2E22", MutedText = "#9B7B6B", HangerColor = "#B4603C",
                BasketAsset = "basket_2.png",
                EnvelopeBackAsset = "envelope_2_back.png",
                EnvelopeFrontAsset = "envelope_2_front.png",
                PreviewAsset = "theme_2.png",
            },
            new()
            {
                Key = "deepsea", Name = "深海", IsDark = true,
                RopeColor = "#7FBFC2", CardBack = "#194145", CardBorder = "#337FBFC2",
                TextColor = "#EAFBFB", MutedText = "#9FC9C9", HangerColor = "#7FBFC2",
                BasketAsset = "basket_3.png",
                EnvelopeBackAsset = "envelope_3_back.png",
                EnvelopeFrontAsset = "envelope_3_front.png",
                PreviewAsset = "theme_3.png",
            },
        };

        public static RackTheme Get(int index) =>
            All[index >= 0 && index < All.Length ? index : 0];

        public static int IndexOf(string? key)
        {
            for (int i = 0; i < All.Length; i++)
                if (string.Equals(All[i].Key, key, StringComparison.OrdinalIgnoreCase)) return i;
            return 0;
        }

        /// <summary>
        /// 7 条类型色带（对齐 SnowAir-Rack CardType.h）。图片类无色带 —— 返回 null。
        /// </summary>
        public static string? TypeColor(RackItemType type) => type switch
        {
            RackItemType.Text => "#AC7F5E",
            RackItemType.Link => "#1677FF",
            RackItemType.File => "#FF8D28",
            RackItemType.Video => "#FF383C",
            RackItemType.Music => "#CB30E0",
            RackItemType.Color => "#34C759",
            RackItemType.Note => "#F3CD5F",
            RackItemType.Todo => "#34C759",
            RackItemType.Svg => "#7A5AF8",
            RackItemType.Recording => "#FF6A3D",
            _ => null,                       // Image：照片卡不带色带
        };

        /// <summary>把 #RRGGBB / #AARRGGBB 变成冻结的画刷，XAML 里可直接赋给 Brush 属性。</summary>
        public static SolidColorBrush Brush(string hex)
        {
            try
            {
                var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
                b.Freeze();
                return b;
            }
            catch
            {
                var b = new SolidColorBrush(Colors.Transparent);
                b.Freeze();
                return b;
            }
        }

        /// <summary>
        /// 主题素材的绝对路径。素材以 Content 形式随输出目录分发（见 csproj），
        /// 用文件路径而不是 pack URI —— 主题切换只要换文件名，也便于第三方 NOTICE 随包保留。
        /// </summary>
        public static string AssetPath(string fileName) =>
            Path.Combine(AppContext.BaseDirectory, "Rack", "Assets", fileName);
    }
}
