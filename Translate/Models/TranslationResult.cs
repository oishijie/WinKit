using System;

namespace WinKit.Translate.Models
{
    /// <summary>
    /// 翻译结果
    /// </summary>
    public class TranslationResult
    {
        /// <summary>原文</summary>
        public string Source { get; set; } = string.Empty;

        /// <summary>译文</summary>
        public string Target { get; set; } = string.Empty;

        /// <summary>检测到的源语言</summary>
        public string? DetectedSourceLang { get; set; }

        /// <summary>翻译是否成功</summary>
        public bool Success { get; set; }

        /// <summary>失败时的错误信息</summary>
        public string? Error { get; set; }

        public static TranslationResult Ok(string source, string target, string? detected = null)
            => new() { Source = source, Target = target, DetectedSourceLang = detected, Success = true };

        public static TranslationResult Fail(string source, string error)
            => new() { Source = source, Success = false, Error = error };
    }
}
