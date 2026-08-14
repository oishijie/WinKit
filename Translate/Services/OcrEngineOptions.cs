using System;
using WinKit.Common;

namespace WinKit.Translate.Services
{
    /// <summary>
    /// 本地 OCR 引擎的构造期参数。
    /// 设计成 record 以获得值相等语义：配置保存后可据此判断"是否真的需要重建引擎"，
    /// 避免为无关设置（比如翻译目标语言）付出数秒的重新初始化代价。
    /// </summary>
    public sealed record OcrEngineOptions(
        OcrModelKind Model,
        bool EnableAngleClassification,
        bool EnableMkldnn,
        int CpuThreads,
        int IdleSlimSeconds)
    {
        /// <summary>线程数上限：再高收益递减，且会和前台应用抢 CPU</summary>
        private const int MaxThreads = 8;

        /// <summary>闲置瘦身下限：低于该值视为关闭（始终常驻）</summary>
        public const int IdleSlimDisabled = 0;

        /// <summary>把 0/负数解析为自动线程数</summary>
        public int EffectiveThreads =>
            CpuThreads > 0
                ? Math.Min(CpuThreads, MaxThreads)
                : Math.Max(1, Math.Min(Environment.ProcessorCount, MaxThreads));

        /// <summary>闲置自动瘦身是否开启（IdleSlimSeconds <= 0 视为关闭）</summary>
        public bool IdleSlimEnabled => IdleSlimSeconds > 0;

        /// <summary>闲置多少毫秒后触发瘦身（关闭时为 0）</summary>
        public int IdleSlimDelayMs => IdleSlimEnabled ? IdleSlimSeconds * 1000 : 0;

        public static OcrEngineOptions FromSettings(AppSettings s) => new(
            OcrModelCatalog.Parse(s.OcrModel),
            s.OcrEnableAngleClassification,
            s.OcrEnableMkldnn,
            s.OcrCpuThreads,
            s.OcrIdleSlimSeconds);
    }
}
