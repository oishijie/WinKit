using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace WinKit.Translate.Services
{
    /// <summary>可选的本地 OCR 模型组合</summary>
    public enum OcrModelKind
    {
        /// <summary>PP-OCRv6 Small：中英混排，精度与速度均衡（默认）</summary>
        V6Small = 0,

        /// <summary>PP-OCRv6 Tiny：体积最小、速度最快，适合低配机器</summary>
        V6Tiny = 1,

        /// <summary>PP-OCRv5 Mobile 中英文模型</summary>
        V5Chinese = 2,

        /// <summary>PP-OCRv5 Mobile 英文/数字专用模型</summary>
        V5English = 3,
    }

    /// <summary>一套模型所需的四个路径</summary>
    public readonly record struct OcrModelPaths(string Det, string Cls, string Rec, string Keys);

    /// <summary>
    /// 本地 OCR 模型清单与运行时目录解析。
    ///
    /// 输出目录布局（构建期由 WinKit.csproj 生成，参考 SnapFind 便携版）：
    ///   WinKit.exe
    ///   libs\             原生推理库
    ///   libs\inference\   PP-OCR 模型与字典
    /// </summary>
    public static class OcrModelCatalog
    {
        /// <summary>原生推理库目录（PaddleOCR.dll 及其依赖）</summary>
        public static string LibsDirectory { get; } =
            Path.Combine(AppContext.BaseDirectory, "libs");

        /// <summary>模型目录</summary>
        public static string InferenceDirectory { get; } =
            Path.Combine(LibsDirectory, "inference");

        /// <summary>中英文通用字典</summary>
        private static string ChineseKeys => Path.Combine(InferenceDirectory, "ppocr_keys.txt");

        /// <summary>英文/数字字典</summary>
        private static string EnglishKeys => Path.Combine(InferenceDirectory, "en_dict.txt");

        /// <summary>角度分类模型（四种组合共用）</summary>
        private static string AngleClassifier => Path.Combine(InferenceDirectory, "PP-OCRv5_mobile_cls_infer");

        /// <summary>校验原生运行时是否齐全时必须存在的关键文件</summary>
        private static readonly string[] RequiredNativeFiles =
        {
            "PaddleOCR.dll",
            "paddle_inference.dll",
            "opencv_world470.dll",
            "mkldnn.dll",
        };

        /// <summary>把配置里的字符串解析为模型枚举，无法识别时回落到默认值</summary>
        public static OcrModelKind Parse(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return OcrModelKind.V6Small;

            return name.Trim().ToLowerInvariant() switch
            {
                "v6small" or "v6_small" or "small" => OcrModelKind.V6Small,
                "v6tiny" or "v6_tiny" or "tiny" => OcrModelKind.V6Tiny,
                "v5cn" or "v5_cn" or "v5chinese" or "chinese" => OcrModelKind.V5Chinese,
                "v5en" or "v5_en" or "v5english" or "english" => OcrModelKind.V5English,
                _ => OcrModelKind.V6Small,
            };
        }

        /// <summary>枚举转配置字符串</summary>
        public static string ToConfigValue(OcrModelKind kind) => kind switch
        {
            OcrModelKind.V6Tiny => "V6_Tiny",
            OcrModelKind.V5Chinese => "V5_CN",
            OcrModelKind.V5English => "V5_EN",
            _ => "V6_Small",
        };

        /// <summary>供界面展示的名称</summary>
        public static string DisplayName(OcrModelKind kind) => kind switch
        {
            OcrModelKind.V6Tiny => "PP-OCRv6 Tiny",
            OcrModelKind.V5Chinese => "PP-OCRv5 中英文",
            OcrModelKind.V5English => "PP-OCRv5 英文",
            _ => "PP-OCRv6 Small",
        };

        /// <summary>解析指定模型组合的四个路径</summary>
        public static OcrModelPaths Resolve(OcrModelKind kind)
        {
            string Dir(string name) => Path.Combine(InferenceDirectory, name);

            return kind switch
            {
                OcrModelKind.V6Tiny => new OcrModelPaths(
                    Dir("PP-OCRv6_tiny_det_infer"),
                    AngleClassifier,
                    Dir("PP-OCRv6_tiny_rec_infer"),
                    ChineseKeys),

                OcrModelKind.V5Chinese => new OcrModelPaths(
                    Dir("PP-OCRv5_mobile_det_infer"),
                    AngleClassifier,
                    Dir("PP-OCRv5_mobile_rec_infer"),
                    ChineseKeys),

                OcrModelKind.V5English => new OcrModelPaths(
                    Dir("PP-OCRv5_mobile_det_infer"),
                    AngleClassifier,
                    Dir("en_PP-OCRv5_mobile_rec_infer"),
                    EnglishKeys),

                _ => new OcrModelPaths(
                    Dir("PP-OCRv6_small_det_infer"),
                    AngleClassifier,
                    Dir("PP-OCRv6_small_rec_infer"),
                    ChineseKeys),
            };
        }

        /// <summary>
        /// 启动前自检：原生库与模型文件是否齐备。
        /// 失败时给出可直接照做的提示，而不是等到调用时抛出难以理解的 DllNotFoundException。
        /// </summary>
        public static bool TryValidate(OcrModelKind kind, out string error)
        {
            if (!Directory.Exists(LibsDirectory))
            {
                error = $"未找到本地 OCR 运行时目录：{LibsDirectory}";
                return false;
            }

            var missingNative = RequiredNativeFiles
                .Where(f => !File.Exists(Path.Combine(LibsDirectory, f)))
                .ToList();

            if (missingNative.Count > 0)
            {
                error = $"本地 OCR 原生库缺失：{string.Join("、", missingNative)}（目录 {LibsDirectory}）";
                return false;
            }

            var paths = Resolve(kind);
            var missingModel = new List<string>();
            if (!Directory.Exists(paths.Det)) missingModel.Add(Path.GetFileName(paths.Det));
            if (!Directory.Exists(paths.Cls)) missingModel.Add(Path.GetFileName(paths.Cls));
            if (!Directory.Exists(paths.Rec)) missingModel.Add(Path.GetFileName(paths.Rec));
            if (!File.Exists(paths.Keys)) missingModel.Add(Path.GetFileName(paths.Keys));

            if (missingModel.Count > 0)
            {
                error = $"OCR 模型缺失：{string.Join("、", missingModel)}（目录 {InferenceDirectory}）";
                return false;
            }

            error = string.Empty;
            return true;
        }
    }
}
