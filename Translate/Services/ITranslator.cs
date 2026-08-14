using System.Threading;
using System.Threading.Tasks;

namespace WinKit.Translate.Services
{
    /// <summary>
    /// 翻译引擎抽象接口 — 对标 STranslate 的 ITranslator。
    /// 后续可接入 DeepL / OpenAI / Azure 等 Key 引擎实现该接口。
    /// </summary>
    public interface ITranslator
    {
        /// <summary>引擎名称</summary>
        string Name { get; }

        /// <summary>
        /// 执行翻译
        /// </summary>
        /// <param name="text">待翻译文本</param>
        /// <param name="sourceLang">源语言 (auto 表示自动检测)</param>
        /// <param name="targetLang">目标语言</param>
        /// <param name="ct">取消令牌</param>
        Task<string> TranslateAsync(string text, string sourceLang, string targetLang, CancellationToken ct);
    }
}
