using System;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using WinKit.Translate.Models;

namespace WinKit.Translate.Services
{
    /// <summary>
    /// OCR 识别引擎抽象接口。
    ///
    /// 全部实现均为本地离线引擎：直接接收位图，不做任何网络请求，
    /// 因此接口里没有 API Key、语言代码之类的在线服务概念
    /// （识别语种由加载哪一套模型决定，属于引擎构造期的配置）。
    /// </summary>
    public interface IOcrProvider : IDisposable
    {
        /// <summary>引擎显示名称（含当前模型）</summary>
        string Name { get; }

        /// <summary>引擎是否已完成初始化，可立即识别</summary>
        bool IsReady { get; }

        /// <summary>
        /// 预热引擎。本地推理引擎的初始化需要数秒（加载 ~390MB 原生库与模型），
        /// 应在后台提前触发，避免用户第一次按下热键时长时间等待。
        /// </summary>
        Task WarmUpAsync(CancellationToken ct = default);

        /// <summary>
        /// 识别位图中的文字。调用方负责释放传入的位图。
        /// </summary>
        Task<OcrResult> RecognizeAsync(Bitmap image, CancellationToken ct = default);
    }
}
