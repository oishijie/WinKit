using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
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
    ///   · 模型是官方 PP-OCRv6 权重导出的 ONNX，与 Paddle 版**同源**，识别结果一致；
    ///   · 方向分类沿用 v5 分类器（PP-OCRv6 不自带分类模型）。
    ///
    /// 三个必须照顾到的现实约束（与原实现一致）：
    ///   1. 初始化较慢——冷启动约 1~3 秒，因此引擎常驻、懒加载，并由外部提前预热；
    ///   2. 原生引擎非线程安全——所有推理调用用信号量串行化；
    ///   3. 原生调用是阻塞的——统一放到线程池执行，绝不占用 UI 线程。
    /// </summary>
    public sealed class RapidOcrProvider : IOcrProvider
    {
        /// <summary>识别文本高度低于该值时先放大，PP-OCR 识别头按 48px 行高训练</summary>
        private const int SmallRegionHeightThreshold = 48;
        private const int PreferredSmallRegionHeight = 96;
        private const double MaxUpscale = 4.0;
        private const int MaxUpscaledSide = 4000;

        /// <summary>超大截图兜底：检测阶段最长边上限（对标 SnapFind 的 max_side_len=960）</summary>
        private const int MaxSideLen = 960;

        /// <summary>
        /// 整图最长边超过该值（如 4K 整屏 / 超长网页截图）时，额外等比缩小到 <see cref="MaxSideLen"/>，
        /// 防止识别阶段在超大位图上内存飙升或卡死。常规 1080p / 2K 选区不受影响。
        /// </summary>
        private const int LargeImageSideThreshold = 3000;

        /// <summary>背景极性检测用的缩略图长边（缩小过程即区域平均，成本与原图幅无关）</summary>
        private const int PolarityThumbSize = 32;

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

            try
            {
                await _gate.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return ModelOcrResult.Fail("识别已取消");
            }

            try
            {
                return await Task.Run(() => RecognizeCore(image), CancellationToken.None).ConfigureAwait(false);
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
                // 瘦身策略、深色自动反色这类前后处理参数就地生效，不付出重建代价。
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

        private ModelOcrResult RecognizeCore(Bitmap image)
        {
            var engine = EnsureEngine();
            if (engine == null)
                return ModelOcrResult.Fail(_initError ?? "OCR 引擎不可用");

            Bitmap? prepared = null;
            SKBitmap? skImage = null;
            try
            {
                var sw = Stopwatch.StartNew();
                prepared = Prepare(image, out bool inverted, out double preprocessScale);
                skImage = ToSkBitmap(prepared);

                // v6 必须配 PPOCRv6 预设：Default 预设的 1024 长边上限 + 50px 白边
                // 是为 v5 调的，会让 v6 检测器分辨率不足，小图上漏框或出乱码。
                var detectOptions = RapidOcrOptions.PPOCRv6 with
                {
                    DoAngle = _options.EnableAngleClassification,
                };

                var raw = engine.Detect(skImage, detectOptions);
                sw.Stop();

                if (raw?.TextBlocks == null || raw.TextBlocks.Length == 0)
                    return ModelOcrResult.Fail("未识别到文字");

                // 引擎回传的是缩放后位图空间里的四点框，统一折算为矩形包围盒，
                // 再由版面层按 preprocessScale 换算回原图坐标系。
                var blocks = ToBlocks(raw.TextBlocks);
                if (blocks.Count == 0)
                    return ModelOcrResult.Fail("未识别到文字");

                var layout = OcrTextLayout.Compose(
                    blocks, image.Width, image.Height, preprocessScale);

                if (string.IsNullOrWhiteSpace(layout.Text))
                    return ModelOcrResult.Fail("未识别到文字");

                return ModelOcrResult.Ok(layout.Text, layout.Lines, sw.ElapsedMilliseconds,
                                         layout.Blocks.Count, inverted, layout.Blocks);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"RapidOcrProvider: 识别失败 - {ex}");
                return ModelOcrResult.Fail($"识别失败: {ex.Message}");
            }
            finally
            {
                skImage?.Dispose();
                if (!ReferenceEquals(prepared, image))
                    prepared?.Dispose();
            }
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
        /// 预处理：统一为 32 位色，对过小的选区做放大，必要时对深色背景反色。
        /// 屏幕正文常见 12~16px 行高，远低于识别模型 48px 的训练尺度，
        /// 直接送入会明显掉字，先放大再识别性价比很高。
        /// </summary>
        /// <param name="inverted">输出：本次是否因检出深色背景而反色</param>
        /// <param name="scale">
        /// 输出：原图 → 送入引擎的位图的缩放系数。
        /// 引擎回传的文本框坐标位于缩放后的空间，需按此系数换算回原图
        /// （见 <c>OcrTextLayout.Compose</c> 的坐标反映射）。
        /// </param>
        private Bitmap Prepare(Bitmap source, out bool inverted, out double scale)
        {
            scale = 1.0;
            if (source.Height < SmallRegionHeightThreshold)
            {
                scale = Math.Min(MaxUpscale, (double)PreferredSmallRegionHeight / source.Height);

                var longest = Math.Max(source.Width, source.Height) * scale;
                if (longest > MaxUpscaledSide)
                    scale = MaxUpscaledSide / (double)Math.Max(source.Width, source.Height);
            }

            // 超大截图兜底：整图最长边超过阈值（如 4K 整屏 / 超长网页截图）时，
            // 等比缩小到 MaxSideLen，避免识别阶段在超大位图上内存飙升或卡死。
            // 常规 1080p / 2K 选区不受影响，保持原生分辨率以保证小字识别率。
            var longestSide = Math.Max(source.Width, source.Height) * scale;
            if (longestSide > LargeImageSideThreshold)
                scale *= (double)MaxSideLen / longestSide;

            int width = Math.Max(1, (int)Math.Round(source.Width * scale));
            int height = Math.Max(1, (int)Math.Round(source.Height * scale));

            // PP-OCR 识别头按「白底黑字」训练，深色主题截图（浅字深底）直接送入会明显掉字，
            // 先反色可把它拉回训练分布。检测放在缩略图上做，与被缩放与否互不干扰。
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
        /// 判断截图是否为深色背景。
        ///
        /// 整图等比缩到长边 32 的缩略图，再只取缩略图外圈求平均亮度：
        /// 截图中央通常是内容（文字 / 图片），四周边带才真正代表背景底色。
        /// 采样量恒定在千像素以内，耗时与原始图幅无关。
        /// </summary>
        private static bool IsDarkBackground(Bitmap source)
        {
            try
            {
                double s = (double)PolarityThumbSize / Math.Max(source.Width, source.Height);
                int tw = Math.Max(1, (int)Math.Round(source.Width * s));
                int th = Math.Max(1, (int)Math.Round(source.Height * s));

                using var thumb = new Bitmap(tw, th, PixelFormat.Format24bppRgb);
                using (var g = Graphics.FromImage(thumb))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.Clear(Color.White);
                    g.DrawImage(source, new Rectangle(0, 0, tw, th));
                }

                int border = Math.Max(1, Math.Min(tw, th) / 8);
                double sum = 0;
                int count = 0;

                for (int y = 0; y < th; y++)
                {
                    bool edgeRow = y < border || y >= th - border;

                    for (int x = 0; x < tw; x++)
                    {
                        if (!edgeRow && x >= border && x < tw - border)
                            continue;   // 中央内容区不参与背景判定

                        var c = thumb.GetPixel(x, y);
                        sum += 0.299 * c.R + 0.587 * c.G + 0.114 * c.B;
                        count++;
                    }
                }

                if (count == 0) return false;
                return sum / count < DarkBackgroundLumaThreshold;
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
        /// RapidOcrNet 的文本框 → 项目通用文本框。
        /// 引擎给的是四点多边形（顺时针，源位图像素坐标），这里折算成外接矩形：
        /// 项目下游只需矩形即可在截图上作画（对照高亮 / 译文叠图 / 按块复制）。
        /// </summary>
        private static List<OcrTextBlock> ToBlocks(IReadOnlyList<TextBlock> source)
        {
            var list = new List<OcrTextBlock>();
            if (source == null) return list;

            foreach (var b in source)
            {
                if (b == null || string.IsNullOrWhiteSpace(b.Text)) continue;

                var points = b.BoxPoints;
                if (points == null || points.Length == 0) continue;

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
                });
            }

            return list;
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
