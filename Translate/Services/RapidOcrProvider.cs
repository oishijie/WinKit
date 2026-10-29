using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using RapidOcrNet;
using SkiaSharp;
using WinKit.Translate.Models;
using ModelOcrResult = WinKit.Translate.Models.OcrResult;

namespace WinKit.Translate.Services
{
    /// <summary>
    /// 基于 ONNX Runtime 的本地离线识别引擎（PP-OCRv6 模型 + CPU 推理）。
    ///
    /// 与旧的 Paddle 方案相比，核心收益是**体积**：
    ///   · 原生运行时从约 386MB（paddle_inference + mklml + opencv_world + mkldnn）
    ///     降到约 13MB（onnxruntime.dll）；
    ///   · 模型是官方 PP-OCRv6 权重导出的 ONNX，与 Paddle 版**同源**；
    ///   · 方向分类沿用 v5 分类器（PP-OCRv6 不自带分类模型）。
    ///
    /// 三个必须照顾到的现实约束（与原实现一致）：
    ///   1. 初始化较慢——冷启动约 1~3 秒，因此引擎常驻、懒加载，并由外部提前预热；
    ///   2. 原生引擎非线程安全——所有推理调用用信号量串行化；
    ///   3. 原生调用是阻塞的——统一放到线程池执行，绝不占用 UI 线程。
    ///
    /// v2.4 的四处识别质量改造（对应 RapidOCR 官方调参实践）：
    ///   A. **大图缩放上限 960 → 2048**：原值会让 4K 整屏被压到 0.25 倍，
    ///      12px 正文变 4px 直接不可读；RapidOCR 官方默认 max_side_len 是 2000。
    ///   B. **小字增强（两遍制）**：首轮检出框偏小时，按目标行高整体放大后再识别一遍，
    ///      两轮之间按「字符总数 + 平均置信度」择优。解决「整图够高、但字很小」的盲点。
    ///   C. **置信度过滤**：引擎本来就回传逐字符置信度，之前被整段丢弃。
    ///      现在低于阈值的块直接丢弃（滤掉图标/边框误检），并把均值带进结果。
    ///   D. **真取消**：改用 DetectAsync，调用方的 CancellationToken 会一路传进
    ///      ONNX RunOptions，每个 kernel 检查一次 —— 旧实现跑完才丢弃结果，白烧 CPU。
    /// </summary>
    public sealed class RapidOcrProvider : IOcrProvider
    {
        /// <summary>识别文本高度低于该值时先放大，PP-OCR 识别头按 48px 行高训练</summary>
        private const int SmallRegionHeightThreshold = 48;
        private const int PreferredSmallRegionHeight = 96;
        private const double MaxUpscale = 4.0;
        private const int MaxUpscaledSide = 4000;

        /// <summary>
        /// 超大截图兜底：检测阶段最长边上限。
        ///
        /// 对齐 RapidOCR 官方默认 max_side_len=2000 并向上取到 2048（32 的整数倍，便于张量对齐）。
        /// 旧值是 960 —— 那是缩略图预检的量级，拿来直接识别 4K 整屏会把 12px 正文压到 4px，
        /// 等于主动放弃识别率。代价只是超大图慢一点：
        /// 常规 1080p / 2K 选区根本走不到这个分支（见 <see cref="LargeImageSideThreshold"/>）。
        /// </summary>
        private const int MaxSideLen = 2048;

        /// <summary>
        /// 整图最长边超过该值（如 4K 整屏 / 超长网页截图）时，等比缩小到 <see cref="MaxSideLen"/>，
        /// 防止识别阶段在超大位图上内存飙升或卡死。常规 1080p / 2K 选区不受影响。
        /// </summary>
        private const int LargeImageSideThreshold = 3000;

        /// <summary>
        /// PP-OCRv6 预设（<c>RapidOcrOptions.PPOCRv6</c>，等价于 PythonCompat）的短边自适应目标。
        /// 引擎内部会把短边拉到 736 —— 估算「引擎实际看到的字有多大」时必须把它算进去，
        /// 否则会低估小字程度，白白放过该增强的图。
        /// </summary>
        private const int EngineShortSideTarget = 736;

        /// <summary>首轮结果的中位行高（换算到引擎实际尺度）低于该值时触发小字增强</summary>
        private const double BoostTriggerMedianHeight = 32.0;

        /// <summary>小字增强的目标行高，略低于识别头 48px 训练尺度，避免过度放大带来的插值模糊</summary>
        private const double BoostTargetMedianHeight = 44.0;

        private const double MinBoost = 1.4;
        private const double MaxBoost = 2.5;

        /// <summary>增强后长边硬上限：再大就会被引擎的缩放逻辑缩回去，白跑一遍</summary>
        private const int MaxBoostedSide = 2600;

        /// <summary>样本太少时中位行高不可信，不做增强判断</summary>
        private const int BoostMinBlockCount = 3;

        /// <summary>背景极性检测用的缩略图长边（缩小过程即区域平均，成本与原图幅无关）</summary>
        private const int PolarityThumbSize = 48;

        /// <summary>极性判定网格：3×3 共 9 格，逐格投票</summary>
        private const int PolarityGridSize = 3;

        /// <summary>
        /// 背景平均亮度低于该值即判定为深色。
        /// 常见深色 UI 底色（#1E1E1E / #252526 / #0D1117）亮度都在 40 以下，
        /// 浅色底色（#FFFFFF / #F5F5F5）则在 240 以上，取 110 有充裕的安全余量。
        /// </summary>
        private const double DarkBackgroundLumaThreshold = 110.0;

        /// <summary>
        /// 反色矩阵：RGB 三通道取负，再由第五行常量项补 +255，alpha 原样保留。
        /// 只读共享 —— <c>Prepare</c> 在信号量保护下串行执行，不存在并发写入。
        /// </summary>
        private static readonly ColorMatrix InvertColorMatrix = new(new[]
        {
            new[] { -1f, 0f, 0f, 0f, 0f },
            new[] { 0f, -1f, 0f, 0f, 0f },
            new[] { 0f, 0f, -1f, 0f, 0f },
            new[] { 0f, 0f, 0f, 1f, 0f },
            new[] { 1f, 1f, 1f, 0f, 1f },
        });

        /// <summary>推理实例常驻时会占用可观内存，闲置一段时间后主动释放并压缩工作集</summary>
        private Timer? _idleTimer;

        private readonly SemaphoreSlim _gate = new(1, 1);

        private RapidOcr? _engine;
        private OcrEngineOptions _options;
        private string? _initError;
        private volatile bool _ready;

        /// <summary>实际加载成功的模型（可能因首选模型缺失而回退到其它模型）。null 表示尚未确定。</summary>
        private OcrModelKind? _effectiveModel;

        /// <summary>已尝试但初始化失败的模型，避免后续重复踩坑</summary>
        private readonly HashSet<OcrModelKind> _failedModels = new();

        private bool _disposed;

        [DllImport("psapi.dll", SetLastError = true)]
        private static extern bool EmptyWorkingSet(IntPtr hProcess);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        public RapidOcrProvider(OcrEngineOptions options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));

            // 闲置自动瘦身：超时后卸载推理实例并强制回收工作集。
            // 关闭时（IdleSlimSeconds<=0）不创建计时器，引擎始终保持常驻。
            if (_options.IdleSlimEnabled)
                _idleTimer = new Timer(OnIdleTimer, null, Timeout.Infinite, Timeout.Infinite);
        }

        private OcrModelKind EffectiveModel => _effectiveModel ?? _options.Model;

        public string Name => $"RapidOCR · {OcrModelCatalog.DisplayName(EffectiveModel)}";

        /// <summary>若因首选模型缺失而回退到其它模型，返回给用户的提示文案；否则为 null</summary>
        public string? FallbackNotice
        {
            get
            {
                if (_effectiveModel == null || _effectiveModel.Value == _options.Model)
                    return null;
                return $"{OcrModelCatalog.DisplayName(_options.Model)} 缺失，已回退到 {OcrModelCatalog.DisplayName(_effectiveModel.Value)}";
            }
        }

        public bool IsReady => _ready;

        /// <summary>初始化失败时的原因，未失败为 null</summary>
        public string? InitializationError => _initError;

        // ══════════════════════════════════════════════
        //  对外能力
        // ══════════════════════════════════════════════

        public async Task WarmUpAsync(CancellationToken ct = default)
        {
            if (_ready || _disposed) return;

            try
            {
                await _gate.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                await Task.Run(() => EnsureEngine(), CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
                ScheduleSlim();   // 预热完即开始计时，闲置后自动瘦身
            }
        }

        public async Task<ModelOcrResult> RecognizeAsync(Bitmap image, CancellationToken ct = default)
        {
            if (_disposed)
                return ModelOcrResult.Fail("OCR 引擎已释放");

            if (image == null || image.Width < 4 || image.Height < 4)
                return ModelOcrResult.Fail("选区过小，无法识别");

            // 声明「即将使用引擎」：取消任何待触发的闲置瘦身。
            // 旧版只有关闭瘦身开关时才会走到 CancelSlim，此处注释里写的
            // 「即将开始一次识别时调用」实际上从未被调用 —— 于是长识别一旦
            // 撞上闲置计时器，识别刚结束引擎就被卸载，下次要重新加载模型。
            CancelSlim();

            try
            {
                await _gate.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 没真正跑起来也要恢复闲置计时，否则计时器会一直停在已取消状态，
                // 引擎从此再也不会被瘦身。
                ScheduleSlim();
                return ModelOcrResult.Fail("识别已取消");
            }

            try
            {
                // 原生推理是同步阻塞的，统一扔线程池；ct 会一路传到 ONNX RunOptions，
                // 中途取消时 DetectAsync 抛 OperationCanceledException，而不是白跑到底。
                return await Task.Run(() => RecognizeCoreAsync(image, ct), CancellationToken.None)
                                 .ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
                ScheduleSlim();   // 每次识别结束都重置闲置计时
            }
        }

        /// <summary>
        /// 配置变更时调用。仅当会影响引擎构造的参数真的变化时才销毁重建，
        /// 否则保持现有引擎（重建一次要数秒）。
        /// 异步等待信号量，绝不阻塞调用线程（配置变更在 UI 线程触发，
        /// 若此时正有识别在跑，同步 Wait 会冻结界面数秒，故此处用 WaitAsync）。
        /// </summary>
        public async Task ReconfigureAsync(OcrEngineOptions options, CancellationToken ct = default)
        {
            if (options == null || _disposed) return;
            if (options == _options) return;   // record 值相等：参数没变就什么都不做

            bool needRebuild = !options.SameEngineConfig(_options);
            var oldModel = _effectiveModel ?? _options.Model;

            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                _options = options;

                // 只有影响引擎构造的参数变了才真正重建（一次要数秒）；
                // 瘦身策略、深色反色、置信度阈值、小字增强这些前后处理参数就地生效。
                if (needRebuild)
                {
                    _failedModels.Clear();
                    _effectiveModel = null;
                    DisposeEngine();
                    _initError = null;
                    _ready = false;
                }
            }
            finally
            {
                _gate.Release();
                ApplyIdleSlimPolicy();
            }

            Debug.WriteLine(needRebuild
                ? $"RapidOcrProvider: 引擎参数已变更 ({OcrModelCatalog.DisplayName(oldModel)} → {OcrModelCatalog.DisplayName(options.Model)})，引擎已重置，等待后台预热"
                : "RapidOcrProvider: 仅前后处理参数变更，引擎保持不变");
        }

        /// <summary>
        /// 主动请求尽快压缩内存（如结果窗关闭后立即瘦身）。仅当自动瘦身开启时有效。
        /// 触发后会在极短延迟内卸载推理实例并回收工作集。
        /// </summary>
        public void RequestSlimNow()
        {
            if (_disposed || !_options.IdleSlimEnabled || _idleTimer == null) return;
            try { _idleTimer.Change(0, Timeout.Infinite); } catch (Exception ex) { Debug.WriteLine($"RapidOcrProvider.RequestSlimNow: {ex.Message}"); }
        }

        // ══════════════════════════════════════════════
        //  闲置内存瘦身
        // ══════════════════════════════════════════════

        /// <summary>引擎卸载后重置闲置计时器（前提：瘦身功能开启且尚未释放）</summary>
        private void ScheduleSlim()
        {
            if (_disposed || _idleTimer == null) return;
            try { _idleTimer.Change(_options.IdleSlimDelayMs, Timeout.Infinite); } catch (Exception ex) { Debug.WriteLine($"RapidOcrProvider.ScheduleSlim: {ex.Message}"); }
        }

        /// <summary>取消待触发的瘦身（即将开始一次识别/预热时调用）</summary>
        private void CancelSlim()
        {
            if (_idleTimer == null) return;
            try { _idleTimer.Change(Timeout.Infinite, Timeout.Infinite); } catch (Exception ex) { Debug.WriteLine($"RapidOcrProvider.CancelSlim: {ex.Message}"); }
        }

        /// <summary>
        /// 根据当前配置应用瘦身策略：开启则确保计时器存在并重新计时；关闭则取消计时器（此后引擎一直常驻）。
        /// </summary>
        private void ApplyIdleSlimPolicy()
        {
            if (_disposed) return;

            if (!_options.IdleSlimEnabled)
            {
                CancelSlim();
                return;
            }

            // 配置从"关闭"热切换到"开启"时按需创建计时器
            if (_idleTimer == null)
            {
                try { _idleTimer = new Timer(OnIdleTimer, null, Timeout.Infinite, Timeout.Infinite); }
                catch { return; }
            }

            ScheduleSlim();
        }

        private void OnIdleTimer(object? state)
        {
            // 计时器回调跑在线程池，这里异步执行并隔离异常
            _ = SlimAsync();
        }

        private async Task SlimAsync()
        {
            if (_disposed) return;
            try
            {
                await _gate.WaitAsync().ConfigureAwait(false);
            }
            catch
            {
                return;
            }

            try
            {
                if (_engine == null) return;   // 已经瘦身过，无需重复

                DisposeEngine();               // 释放推理实例（返回其占用的非托管内存）
                TrimWorkingSet();              // 强制 OS 把工作集页换出
                Debug.WriteLine("RapidOcrProvider: 闲置超时，已卸载推理实例并压缩工作集");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"RapidOcrProvider: 瘦身异常 - {ex.Message}");
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>
        /// 调用 EmptyWorkingSet 迫使系统把本进程的工作集压缩到最小。
        /// ONNX Runtime 的推理缓冲多数在 Dispose 时释放，但内存池（arena）会保留，
        /// 因此仍需 GC + EmptyWorkingSet 组合把常驻内存压下去。
        /// </summary>
        private void TrimWorkingSet()
        {
            try
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();

                // 伪句柄 (-1) 不需要关闭；EmptyWorkingSet 需要 PROCESS_SET_QUOTA，
                // 当前进程伪句柄自带该权限
                EmptyWorkingSet(GetCurrentProcess());
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"RapidOcrProvider: 压缩工作集失败 - {ex.Message}");
            }
        }

        // ══════════════════════════════════════════════
        //  内部实现（均在信号量保护下的线程池线程执行）
        // ══════════════════════════════════════════════

        private async Task<ModelOcrResult> RecognizeCoreAsync(Bitmap image, CancellationToken ct)
        {
            var engine = EnsureEngine();
            if (engine == null)
                return ModelOcrResult.Fail(_initError ?? "OCR 引擎不可用");

            try
            {
                var sw = Stopwatch.StartNew();

                // v6 必须配 PPOCRv6 预设：Default 预设的 1024 长边上限 + 50px 白边
                // 是为 v5 调的，会让 v6 检测器分辨率不足，小图上漏框或出乱码。
                //
                // TextScore 置 0 让引擎全量回传，过滤改由本层做：
                // 这样才能统计「滤掉了多少块」并给每块标注置信度。
                // 引擎内部静默丢弃的话，用户根本不知道漏了什么。
                var detectOptions = RapidOcrOptions.PPOCRv6 with
                {
                    DoAngle = _options.EnableAngleClassification,
                    TextScore = 0f,
                };

                // ── 第一轮 ──
                var first = await RunPassAsync(engine, image, detectOptions, 1.0, ct).ConfigureAwait(false);
                if (first == null)
                    return ModelOcrResult.Fail("未识别到文字");

                var blocks = first.Value.Blocks;
                double scale = first.Value.Scale;
                bool inverted = first.Value.Inverted;
                int filtered = first.Value.FilteredBlocks;
                bool boosted = false;

                // ── 第二轮：小字增强 ──
                if (_options.SmallTextBoost)
                {
                    double boost = ComputeBoostFactor(blocks, image, scale);
                    if (boost > 1.0)
                    {
                        ct.ThrowIfCancellationRequested();

                        var second = await RunPassAsync(engine, image, detectOptions, boost, ct).ConfigureAwait(false);
                        if (second != null && IsBetterResult(second.Value.Blocks, blocks))
                        {
                            blocks = second.Value.Blocks;
                            scale = second.Value.Scale;
                            inverted = second.Value.Inverted;
                            // 只报告「最终采用那一轮」的过滤数，两轮累加会误导
                            filtered = second.Value.FilteredBlocks;
                            boosted = true;
                        }
                    }
                }

                ct.ThrowIfCancellationRequested();

                var layout = OcrTextLayout.Compose(blocks, image.Width, image.Height, scale);
                if (string.IsNullOrWhiteSpace(layout.Text))
                    return ModelOcrResult.Fail("未识别到文字");

                double avgConf = layout.Blocks.Count > 0
                    ? layout.Blocks.Average(b => (double)b.Confidence)
                    : 0;

                sw.Stop();

                Debug.WriteLine(
                    $"RapidOcrProvider: {layout.Blocks.Count} 块 / 置信 {avgConf:F3} / {sw.ElapsedMilliseconds} ms" +
                    (boosted ? "（已小字增强）" : "") + (inverted ? "（已反色）" : ""));

                return ModelOcrResult.Ok(
                    layout.Text,
                    layout.Lines,
                    sw.ElapsedMilliseconds,
                    layout.Blocks.Count,
                    inverted,
                    layout.Blocks,
                    avgConf,
                    boosted,
                    filtered);
            }
            catch (OperationCanceledException)
            {
                Debug.WriteLine("RapidOcrProvider: 识别被取消");
                return ModelOcrResult.Fail("识别已取消");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"RapidOcrProvider: 识别失败 - {ex}");
                return ModelOcrResult.Fail($"识别失败: {ex.Message}");
            }
        }

        /// <summary>一轮「预处理 → 推理 → 转块」的结果</summary>
        private readonly record struct PassResult(
            List<OcrTextBlock> Blocks,
            double Scale,
            bool Inverted,
            int FilteredBlocks);

        /// <summary>
        /// 跑一轮完整识别。<paramref name="boost"/> 为额外放大倍率（1.0 表示不做小字增强）。
        /// 返回 null 表示这一轮没识别到任何可用文字。
        /// </summary>
        private async Task<PassResult?> RunPassAsync(
            RapidOcr engine,
            Bitmap image,
            RapidOcrOptions options,
            double boost,
            CancellationToken ct)
        {
            Bitmap? prepared = null;
            SKBitmap? skImage = null;
            try
            {
                prepared = Prepare(image, boost, out bool inverted, out double scale);
                skImage = ToSkBitmap(prepared);

                // DetectAsync 的取消是逐 kernel 生效的（RunOptions.terminate），
                // 连 detection 这个最长的一段也能被打断 —— 同步 Detect 做不到。
                var raw = await engine.DetectAsync(skImage, options, null, ct).ConfigureAwait(false);

                if (raw?.TextBlocks == null || raw.TextBlocks.Length == 0)
                    return null;

                var (blocks, filtered) = ToBlocks(raw.TextBlocks, _options.EffectiveTextScore);
                if (blocks.Count == 0)
                    return null;

                return new PassResult(blocks, scale, inverted, filtered);
            }
            finally
            {
                skImage?.Dispose();
                if (!ReferenceEquals(prepared, image))
                    prepared?.Dispose();
            }
        }

        /// <summary>
        /// 估算需要多大的额外放大倍率来救小字。返回 1.0 表示不需要增强。
        ///
        /// 判据不是「整图高度」而是「检出框的中位高度」——这正是旧实现的盲点：
        /// 一个 900px 高的代码块整图够大，但里面正文只有 12px，
        /// 按整图判断永远不会触发放大，小字就这样原样喂给了按 48px 训练的识别头。
        /// </summary>
        private double ComputeBoostFactor(List<OcrTextBlock> blocks, Bitmap source, double scale)
        {
            if (blocks.Count < BoostMinBlockCount) return 1.0;

            double medianH = MedianHeight(blocks);
            if (medianH <= 0) return 1.0;

            // 换算到「引擎实际看到的尺度」：PPOCRv6 预设会把短边拉到 736。
            // 漏算这一步会低估小字程度，该增强的图被放过。
            int prepW = Math.Max(1, (int)Math.Round(source.Width * scale));
            int prepH = Math.Max(1, (int)Math.Round(source.Height * scale));
            int shortSide = Math.Min(prepW, prepH);
            double engineScale = shortSide > 0 && shortSide < EngineShortSideTarget
                ? (double)EngineShortSideTarget / shortSide
                : 1.0;

            double effectiveH = medianH * engineScale;
            if (effectiveH >= BoostTriggerMedianHeight) return 1.0;

            double factor = Math.Clamp(
                BoostTargetMedianHeight / effectiveH,
                MinBoost,
                MaxBoost);

            // 放大后若超硬上限，只放大到上限为止（再多也会被引擎缩回去）
            double maxSide = Math.Max(source.Width, source.Height);
            if (maxSide <= 0) return 1.0;

            double cap = (double)MaxBoostedSide / (maxSide * scale);
            if (factor > cap) factor = cap;

            return factor > MinBoost ? factor : 1.0;
        }

        /// <summary>检出框高度的中位数</summary>
        private static double MedianHeight(List<OcrTextBlock> blocks)
        {
            var heights = blocks
                .Select(b => (double)b.Height)
                .Where(h => h > 0)
                .OrderBy(h => h)
                .ToList();

            if (heights.Count == 0) return 0;

            int mid = heights.Count / 2;
            return heights.Count % 2 == 1
                ? heights[mid]
                : (heights[mid - 1] + heights[mid]) / 2.0;
        }

        /// <summary>
        /// 判断增强后的结果是否比首轮更好。
        ///
        /// 两个条件同时满足才采用：
        ///   1. 认出的字符总数没有变少 —— 放大后检测退化（框变少/变碎）时直接否掉；
        ///   2. 平均置信度没有明显下降（容忍 0.05 抖动）。
        /// 只看块数不够：放大后框可能变多但都是碎片，置信度会掉。
        /// </summary>
        private static bool IsBetterResult(List<OcrTextBlock> candidate, List<OcrTextBlock> baseline)
        {
            if (candidate.Count == 0) return false;

            int candChars = candidate.Sum(b => b.Text.Length);
            int baseChars = baseline.Sum(b => b.Text.Length);
            if (candChars < baseChars) return false;

            double candConf = candidate.Average(b => (double)b.Confidence);
            double baseConf = baseline.Average(b => (double)b.Confidence);
            return candConf + 0.05 >= baseConf;
        }

        /// <summary>
        /// 创建（或复用）推理引擎。
        /// 首选配置的模型；若其文件缺失或初始化失败，则按回退顺序自动切换到其它可用模型，
        /// 避免因为某个模型文件被误删而导致整个 OCR 不可用。
        /// 回退选择会缓存到 <see cref="_effectiveModel"/>，全部失败时给出明确的缺失清单。
        /// </summary>
        private RapidOcr? EnsureEngine()
        {
            if (_engine != null) return _engine;
            if (_initError != null) return null;

            OcrModelKind candidate = _effectiveModel ?? _options.Model;

            foreach (var kind in FallbackOrder(candidate))
            {
                if (_failedModels.Contains(kind)) continue;

                if (!OcrModelCatalog.TryValidate(kind, out var validationError))
                {
                    Debug.WriteLine($"RapidOcrProvider: 模型 {OcrModelCatalog.DisplayName(kind)} 文件缺失 - {validationError}");
                    _failedModels.Add(kind);
                    continue;
                }

                try
                {
                    var sw = Stopwatch.StartNew();
                    var engine = BuildEngine(kind);
                    sw.Stop();

                    _engine = engine;
                    _ready = true;
                    _effectiveModel = kind;
                    Debug.WriteLine($"RapidOcrProvider: 引擎初始化完成 ({OcrModelCatalog.DisplayName(kind)}) 耗时 {sw.ElapsedMilliseconds} ms{(kind != _options.Model ? "（已回退）" : "")}");
                    return _engine;
                }
                catch (Exception ex)
                {
                    _failedModels.Add(kind);
                    Debug.WriteLine($"RapidOcrProvider: 模型 {OcrModelCatalog.DisplayName(kind)} 初始化失败 - {ex.Message}");
                    // 继续尝试下一个候选模型
                }
            }

            // 所有候选模型都不可用
            _initError = BuildUnavailableMessage(candidate);
            Debug.WriteLine($"RapidOcrProvider: {_initError}");
            return null;
        }

        /// <summary>按指定模型构造推理引擎（调用方需保证该模型已通过 TryValidate）</summary>
        private RapidOcr BuildEngine(OcrModelKind kind)
        {
            var paths = OcrModelCatalog.Resolve(kind);
            var engine = new RapidOcr();

            int threads = _options.EffectiveThreads;
            if (threads > 0)
            {
                // SessionOptions 在 InitModels 内部被读取后即无用处，按官方用法用 using 释放
                using var sessionOptions = RapidOcr.GetDefaultSessionOptions(threads);
                engine.InitModels(paths.Det, paths.Cls, paths.Rec, paths.Keys, sessionOptions);
            }
            else
            {
                engine.InitModels(paths.Det, paths.Cls, paths.Rec, paths.Keys);
            }

            return engine;
        }

        /// <summary>
        /// 模型回退优先级：首选模型排在最前，其余按「通用性 / 体积」排序。
        /// 首选缺失时优先回退到最全能的 V6_Small。
        /// </summary>
        private static IEnumerable<OcrModelKind> FallbackOrder(OcrModelKind preferred)
        {
            var order = new List<OcrModelKind>
            {
                preferred,
                OcrModelKind.V6Small,
                OcrModelKind.V6Tiny,
            };

            // 去重但保持顺序
            var seen = new HashSet<OcrModelKind>();
            foreach (var k in order)
            {
                if (seen.Add(k)) yield return k;
            }
        }

        /// <summary>所有候选模型都不可用时，列出各自缺失情况，方便用户对照修复</summary>
        private static string BuildUnavailableMessage(OcrModelKind preferred)
        {
            var lines = new List<string>();
            foreach (var kind in FallbackOrder(preferred))
            {
                if (OcrModelCatalog.TryValidate(kind, out var err))
                    lines.Add($"{OcrModelCatalog.DisplayName(kind)}：可用");
                else
                    lines.Add($"{OcrModelCatalog.DisplayName(kind)}：{err}");
            }
            return "本地 OCR 模型不可用：\n" + string.Join("\n", lines);
        }

        /// <summary>
        /// 预处理：统一为 32 位色，对过小的选区做放大，必要时对深色背景反色，
        /// 并叠加小字增强要求的额外放大倍率。
        /// </summary>
        /// <param name="source">原始位图</param>
        /// <param name="boost">
        /// 额外放大倍率（1.0 = 不增强）。第二轮识别专用：
        /// 首轮发现字太小，就按目标行高把整图放大再来一遍。
        /// </param>
        /// <param name="inverted">输出：本次是否因检出深色背景而反色</param>
        /// <param name="scale">
        /// 输出：原图 → 送入引擎的位图的缩放系数。
        /// 引擎回传的文本框坐标位于缩放后的空间，需按此系数换算回原图
        /// （见 <c>OcrTextLayout.Compose</c> 的坐标反映射）。
        /// </param>
        private Bitmap Prepare(Bitmap source, double boost, out bool inverted, out double scale)
        {
            scale = 1.0;

            // ① 极小选区放大：整图还没一行字高时，直接放大到识别头舒服的尺度
            if (source.Height < SmallRegionHeightThreshold)
            {
                scale = Math.Min(MaxUpscale, (double)PreferredSmallRegionHeight / source.Height);

                var longest = Math.Max(source.Width, source.Height) * scale;
                if (longest > MaxUpscaledSide)
                    scale = MaxUpscaledSide / (double)Math.Max(source.Width, source.Height);
            }

            // ② 超大截图兜底：整图最长边超过阈值（4K 整屏 / 超长网页截图）时缩到 MaxSideLen，
            //    避免识别阶段在超大位图上内存飙升或卡死。
            //    注意这条只对 >3000px 生效，常规 1080p / 2K 选区保持原生分辨率。
            var longestSide = Math.Max(source.Width, source.Height) * scale;
            if (longestSide > LargeImageSideThreshold)
                scale *= (double)MaxSideLen / longestSide;

            // ③ 小字增强：第一轮结果不理想时按倍率再放大一次，但绝不突破硬上限
            if (boost > 1.0)
            {
                double boosted = scale * boost;
                double maxSide = Math.Max(source.Width, source.Height);
                if (maxSide > 0 && maxSide * boosted > MaxBoostedSide)
                    boosted = (double)MaxBoostedSide / maxSide;

                if (boosted > scale) scale = boosted;
            }

            int width = Math.Max(1, (int)Math.Round(source.Width * scale));
            int height = Math.Max(1, (int)Math.Round(source.Height * scale));

            // PP-OCR 识别头按「白底黑字」训练，深色主题截图（浅字深底）直接送入会明显掉字，
            // 先反色可把它拉回训练分布。
            inverted = _options.AutoInvertDark && IsDarkBackground(source);

            // 统一成 32bpp：SKBitmap 侧按 Bgra8888 / Opaque 直接按行拷贝像素，
            // 是 24bpp 与 32bpp 之间唯一无需二次转换的匹配格式。
            var target = new Bitmap(width, height, PixelFormat.Format32bppRgb);
            try
            {
                using var g = Graphics.FromImage(target);
                g.InterpolationMode = scale > 1.0 ? InterpolationMode.HighQualityBicubic : InterpolationMode.HighQualityBilinear;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.SmoothingMode = SmoothingMode.HighQuality;
                g.Clear(Color.White);   // 源图若有透明像素，落到白底上（反色与否都成立）

                var dest = new Rectangle(0, 0, width, height);
                if (inverted)
                {
                    using var attrs = new ImageAttributes();
                    attrs.SetColorMatrix(InvertColorMatrix);
                    g.DrawImage(source, dest, 0, 0, source.Width, source.Height, GraphicsUnit.Pixel, attrs);
                }
                else
                {
                    g.DrawImage(source, dest);
                }

                return target;
            }
            catch
            {
                target.Dispose();
                throw;
            }
        }

        /// <summary>
        /// 判断截图是否为深色背景（3×3 网格投票）。
        ///
        /// 旧实现只看整图外圈的平均亮度，遇到「深色窗口里嵌浅色对话框 / 浅色网页里嵌深色终端」
        /// 这类深浅混排的截图会整图判错极性 —— 一次整图反色把正常的那一半直接毁掉。
        /// 改为把图缩到 48px 长边后切成 3×3 共 9 格，逐格算平均亮度，
        /// 只有 ≥2/3 的格子都偏暗才整图反色；混排时保守按浅色（不反色）处理 ——
        /// 宁可让深色那一半掉字，也不能让浅色那一半变乱码。
        ///
        /// 采样量恒定（48×48 以内），耗时与原图幅无关。
        /// </summary>
        private static bool IsDarkBackground(Bitmap source)
        {
            try
            {
                double s = (double)PolarityThumbSize / Math.Max(source.Width, source.Height);
                int tw = Math.Max(PolarityGridSize, (int)Math.Round(source.Width * s));
                int th = Math.Max(PolarityGridSize, (int)Math.Round(source.Height * s));

                using var thumb = new Bitmap(tw, th, PixelFormat.Format24bppRgb);
                using (var g = Graphics.FromImage(thumb))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.Clear(Color.White);
                    g.DrawImage(source, new Rectangle(0, 0, tw, th));
                }

                // 用 LockBits 一次拷出，避免 GetPixel 逐点跨越托管/非托管边界
                var rect = new Rectangle(0, 0, tw, th);
                var data = thumb.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
                byte[] buffer;
                int stride;
                try
                {
                    stride = data.Stride;
                    buffer = new byte[stride * th];
                    Marshal.Copy(data.Scan0, buffer, 0, buffer.Length);
                }
                finally
                {
                    thumb.UnlockBits(data);
                }

                int darkCells = 0;
                for (int gy = 0; gy < PolarityGridSize; gy++)
                {
                    int y0 = gy * th / PolarityGridSize;
                    int y1 = (gy + 1) * th / PolarityGridSize;

                    for (int gx = 0; gx < PolarityGridSize; gx++)
                    {
                        int x0 = gx * tw / PolarityGridSize;
                        int x1 = (gx + 1) * tw / PolarityGridSize;

                        double sum = 0;
                        int count = 0;

                        for (int y = y0; y < y1; y++)
                        {
                            int row = y * stride;
                            for (int x = x0; x < x1; x++)
                            {
                                int i = row + x * 3;
                                if (i + 2 >= buffer.Length) continue;

                                double b = buffer[i];
                                double g2 = buffer[i + 1];
                                double r = buffer[i + 2];
                                sum += 0.299 * r + 0.587 * g2 + 0.114 * b;
                                count++;
                            }
                        }

                        if (count > 0 && sum / count < DarkBackgroundLumaThreshold)
                            darkCells++;
                    }
                }

                int totalCells = PolarityGridSize * PolarityGridSize;
                return darkCells * 3 >= totalCells * 2;   // ≥ 2/3 的格子偏暗
            }
            catch (Exception ex)
            {
                // 判定失败不应影响识别主流程，按浅色（不反色）处理
                Debug.WriteLine($"RapidOcrProvider: 背景极性检测失败，按浅色处理 - {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// System.Drawing.Bitmap → SkiaSharp.SKBitmap。
        /// ONNX 侧（RapidOcrNet）只吃 SKBitmap，这里按行做原始像素搬运：
        /// 走 PNG/BMP 编解码会白白搭上一次压缩与解压，对整屏截图是几十毫秒的浪费。
        /// </summary>
        private static SKBitmap ToSkBitmap(Bitmap source)
        {
            var info = new SKImageInfo(source.Width, source.Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
            var sk = new SKBitmap(info);

            var rect = new Rectangle(0, 0, source.Width, source.Height);
            var data = source.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
            try
            {
                int rowBytes = source.Width * 4;
                var buffer = new byte[rowBytes];
                IntPtr dst = sk.GetPixels();
                int dstStride = sk.RowBytes;

                for (int y = 0; y < source.Height; y++)
                {
                    Marshal.Copy(data.Scan0 + y * data.Stride, buffer, 0, rowBytes);
                    Marshal.Copy(buffer, 0, dst + y * dstStride, rowBytes);
                }
            }
            finally
            {
                source.UnlockBits(data);
            }

            return sk;
        }

        /// <summary>
        /// RapidOcrNet 的文本框 → 项目通用文本框（带置信度）。
        ///
        /// 引擎给的是四点多边形（顺时针，源位图像素坐标），这里折算成外接矩形：
        /// 项目下游只需矩形即可在截图上作画（对照高亮 / 译文叠图 / 按块复制）。
        ///
        /// <paramref name="minScore"/> 之下的块直接丢弃 —— 那基本都是图标、边框、
        /// 页面纹理被误检成"文字"，留着只会污染识别结果、拉低整段译文质量。
        /// 被丢弃的数量回传给调用方，用于状态栏 / 诊断展示。
        /// </summary>
        private static (List<OcrTextBlock> Blocks, int Filtered) ToBlocks(
            IReadOnlyList<TextBlock> source,
            double minScore)
        {
            var list = new List<OcrTextBlock>();
            int filtered = 0;
            if (source == null) return (list, filtered);

            foreach (var b in source)
            {
                if (b == null || string.IsNullOrWhiteSpace(b.Text)) continue;

                var points = b.BoxPoints;
                if (points == null || points.Length == 0) continue;

                // 逐字符置信度均值。引擎未回传时按 1.0 处理（不因缺数据误杀）。
                double confidence = 1.0;
                var scores = b.CharScores;
                if (scores is { Length: > 0 })
                {
                    try { confidence = scores.Average(x => (double)x); }
                    catch { confidence = 1.0; }
                }

                if (confidence < minScore)
                {
                    filtered++;
                    continue;
                }

                float minX = float.MaxValue, minY = float.MaxValue;
                float maxX = float.MinValue, maxY = float.MinValue;
                foreach (var p in points)
                {
                    if (p.X < minX) minX = p.X;
                    if (p.Y < minY) minY = p.Y;
                    if (p.X > maxX) maxX = p.X;
                    if (p.Y > maxY) maxY = p.Y;
                }

                if (maxX <= minX || maxY <= minY) continue;

                list.Add(new OcrTextBlock
                {
                    Text = b.Text.Trim(),
                    Left = (int)Math.Floor(minX),
                    Top = (int)Math.Floor(minY),
                    Right = (int)Math.Ceiling(maxX),
                    Bottom = (int)Math.Ceiling(maxY),
                    Confidence = (float)confidence,
                });
            }

            return (list, filtered);
        }

        private void DisposeEngine()
        {
            if (_engine == null) return;
            try
            {
                _engine.Dispose();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"RapidOcrProvider: 释放引擎异常 - {ex.Message}");
            }
            finally
            {
                _engine = null;
                _ready = false;   // 引擎已卸载，下次识别需重新加载
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _ready = false;

            try
            {
                _idleTimer?.Dispose();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"RapidOcrProvider.Dispose(timer): {ex.Message}");
            }

            try
            {
                _gate.Wait(TimeSpan.FromSeconds(10));
                DisposeEngine();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"RapidOcrProvider: Dispose 异常 - {ex.Message}");
            }
            finally
            {
                try { _gate.Release(); } catch (Exception ex) { Debug.WriteLine($"RapidOcrProvider.Dispose(gate): {ex.Message}"); }
                _gate.Dispose();
            }
        }
    }
}
