using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace WinKit.Translate.Services
{
    /// <summary>可选的本地 OCR 模型组合</summary>
    public enum OcrModelKind
    {
        /// <summary>PP-OCRv6 Small：多语言（简繁中文 / 英文 / 日文 + 46 种拉丁语系），精度与速度均衡（默认）</summary>
        V6Small = 0,

        /// <summary>PP-OCRv6 Tiny：体积最小、速度最快，适合低配机器</summary>
        V6Tiny = 1,
    }

    /// <summary>一套模型所需的四个路径（检测 / 方向分类 / 识别 / 字典）</summary>
    public readonly record struct OcrModelPaths(string Det, string Cls, string Rec, string Keys);

    /// <summary>
    /// 本地 OCR 模型清单与路径解析（ONNX Runtime 版）。
    ///
    /// 输出目录布局（构建期由 WinKit.csproj 从项目内 models\ 原样复制）：
    ///   WinKit.exe
    ///   models\
    ///     PP-OCRv6_det_small.onnx                          检测（Small）
    ///     PP-OCRv6_rec_small.onnx                          识别（Small）
    ///     PP-OCRv6_small_dict.txt                          上者对应字典
    ///     PP-OCRv6_det_tiny.onnx                           检测（Tiny）
    ///     PP-OCRv6_rec_tiny.onnx                           识别（Tiny）
    ///     PP-OCRv6_tiny_dict.txt                           上者对应字典
    ///     ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx   方向分类（v6 沿用 v5 分类器）
    ///
    /// 设计要点：PP-OCRv6 的 small / tiny 权重本身就是多语言的，
    /// 因此不再需要旧版那样按「中英文 / 英文」拆分模型档位。
    /// </summary>
    public static class OcrModelCatalog
    {
        /// <summary>模型根目录</summary>
        public static string ModelsDirectory { get; } =
            Path.Combine(AppContext.BaseDirectory, "models");

        private static string Dir(string name) => Path.Combine(ModelsDirectory, name);

        /// <summary>方向分类模型（两档共用；PP-OCRv6 不自带分类器，沿用 v5 的）</summary>
        private static string AngleClassifier => Dir("ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx");

        /// <summary>把配置里的字符串解析为模型枚举，无法识别时回落到默认值</summary>
        public static OcrModelKind Parse(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return OcrModelKind.V6Small;

            return name.Trim().ToLowerInvariant() switch
            {
                "v6tiny" or "v6_tiny" or "tiny" => OcrModelKind.V6Tiny,

                // 旧版配置里的 PP-OCRv5 中英文两档：v6 单模型即多语言，统一并入默认档
                "v5cn" or "v5_cn" or "v5chinese" or "chinese" => OcrModelKind.V6Small,
                "v5en" or "v5_en" or "v5english" or "english" => OcrModelKind.V6Small,

                _ => OcrModelKind.V6Small,
            };
        }

        /// <summary>枚举转配置字符串</summary>
        public static string ToConfigValue(OcrModelKind kind) => kind switch
        {
            OcrModelKind.V6Tiny => "V6_Tiny",
            _ => "V6_Small",
        };

        /// <summary>供界面展示的名称</summary>
        public static string DisplayName(OcrModelKind kind) => kind switch
        {
            OcrModelKind.V6Tiny => "PP-OCRv6 Tiny",
            _ => "PP-OCRv6 Small",
        };

        /// <summary>解析指定模型组合的四个路径</summary>
        public static OcrModelPaths Resolve(OcrModelKind kind) => kind switch
        {
            OcrModelKind.V6Tiny => new OcrModelPaths(
                Dir("PP-OCRv6_det_tiny.onnx"),
                AngleClassifier,
                Dir("PP-OCRv6_rec_tiny.onnx"),
                Dir("PP-OCRv6_tiny_dict.txt")),

            _ => new OcrModelPaths(
                Dir("PP-OCRv6_det_small.onnx"),
                AngleClassifier,
                Dir("PP-OCRv6_rec_small.onnx"),
                Dir("PP-OCRv6_small_dict.txt")),
        };

        /// <summary>
        /// 启动前自检：模型文件是否齐备。
        /// 失败时给出可直接照做的提示，而不是等到调用时抛出难以理解的异常。
        /// </summary>
        public static bool TryValidate(OcrModelKind kind, out string error)
        {
            if (!Directory.Exists(ModelsDirectory))
            {
                error = $"未找到本地 OCR 模型目录：{ModelsDirectory}";
                return false;
            }

            var paths = Resolve(kind);
            var missing = new List<string>();
            if (!File.Exists(paths.Det)) missing.Add(Path.GetFileName(paths.Det));
            if (!File.Exists(paths.Cls)) missing.Add(Path.GetFileName(paths.Cls));
            if (!File.Exists(paths.Rec)) missing.Add(Path.GetFileName(paths.Rec));
            if (!File.Exists(paths.Keys)) missing.Add(Path.GetFileName(paths.Keys));

            if (missing.Count > 0)
            {
                error = $"OCR 模型缺失：{string.Join("、", missing)}（目录 {ModelsDirectory}）";
                return false;
            }

            error = string.Empty;
            return true;
        }
    }
}
