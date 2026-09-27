using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using PaddleOCRSharp;
using WinKit.Translate.Models;

namespace WinKit.Translate.Services
{
    /// <summary>
    /// 基于 PaddleOCR 的本地离线识别引擎（PP-OCRv5 / v6 模型 + CPU 推理）。
    ///
    /// 三个必须照顾到的现实约束：
    ///   1. 初始化极慢——冷启动约 3~7 秒，因此引擎常驻、懒加载，并由外部提前预热；
    ///   2. 原生引擎非线程安全——所有推理调用用信号量串行化；
    ///   3. 原生调用是阻塞的——统一放到线程池执行，绝不占用 UI 线程。
    /// </summary>
    public sealed class PaddleOcrProvider : IOcrProvider
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

        /// <summary>PaddleOCRdllPath 是进程级静态设置，只需设置一次</summary>
        private static int _dllPathInitialized;

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

        /// <summary>原生推理实例常驻时约占两三百 MB，闲置一段时间后主动释放并压缩工作集</summary>
        private Timer? _idleTimer;

        private readonly SemaphoreSlim _gate = new(1, 1);

        private PaddleOCREngine? _engine;
        private OcrEngineOptions _options;
        private string? _initError;
        private volatile bool _ready;
        /// <summary>原生 MKL（mklml.dll）是否已随引擎加载进进程。仅当它为 true 时调用 MKL_Free_Buffers 才有意义。</summary>
        private bool _mklResident;
        /// <summary>实际加载成功的模型（可能因首选模型缺失而回退到其它模型）。null 表示尚未确定。</summary>
        private OcrModelKind? _effectiveModel;
        /// <summary>已尝试但初始化失败的模型，避免后续重复踩坑</summary>
        private readonly HashSet<OcrModelKind> _failedModels = new();
        private bool _disposed;

        [DllImport("psapi.dll", SetLastError = true)]
        private static extern bool EmptyWorkingSet(IntPtr hProcess);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern IntPtr LoadLibrary(string lpFileName);

        [DllImport("mklml.dll")]
        private static extern int MKL_Free_Buffers();

        public PaddleOcrProvider(OcrEngineOptions options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));

            // 屏蔽 PaddlePaddle 原生 C++ 日志（ReduceMean 等推理期噪声），对标 SnapFind 的做法
            try { Environment.SetEnvironmentVariable("GLOG_minloglevel", "3"); } catch (Exception ex) { Debug.WriteLine($"PaddleOcrProvider: GLOG 设置失败 - {ex.Message}"); }

            // 闲置自动瘦身：超时后卸载原生推理实例并强制回收工作集。
            // 关闭时（IdleSlimSeconds<=0）不创建计时器，引擎始终保持常驻。
            if (_options.IdleSlimEnabled)
                _idleTimer = new Timer(OnIdleTimer, null, Timeout.Infinite, Timeout.Infinite);
        }

        private OcrModelKind EffectiveModel => _effectiveModel ?? _options.Model;

        public string Name => $"PaddleOCR · {OcrModelCatalog.DisplayName(EffectiveModel)}";

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

        public async Task<OcrResult> RecognizeAsync(Bitmap image, CancellationToken ct = default)
        {
            if (_disposed)
                return OcrResult.Fail("OCR 引擎已释放");

            if (image == null || image.Width < 4 || image.Height < 4)
                return OcrResult.Fail("选区过小，无法识别");

            try
            {
                await _gate.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return OcrResult.Fail("识别已取消");
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

                // 只有影响原生引擎构造的参数变了才真正重建（一次要数秒）；
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
                ? $"PaddleOcrProvider: 引擎参数已变更 ({OcrModelCatalog.DisplayName(oldModel)} → {OcrModelCatalog.DisplayName(options.Model)})，引擎已重置，等待后台预热"
                : "PaddleOcrProvider: 仅前后处理参数变更，引擎保持不变");
        }

        /// <summary>
        /// 主动请求尽快压缩内存（如结果窗关闭后立即瘦身）。仅当自动瘦身开启时有效。
        /// 触发后会在极短延迟内卸载原生推理实例并回收工作集——对标 SnapFind 的"关窗即瘦身"。
        /// </summary>
        public void RequestSlimNow()
        {
            if (_disposed || !_options.IdleSlimEnabled || _idleTimer == null) return;
            try { _idleTimer.Change(0, Timeout.Infinite); } catch (Exception ex) { Debug.WriteLine($"PaddleOcrProvider.RequestSlimNow: {ex.Message}"); }
        }

        // ══════════════════════════════════════════════
        //  闲置内存瘦身（对标 SnapFind 的"5 秒自动瘦身"）
        // ══════════════════════════════════════════════

        /// <summary>引擎卸载后重置闲置计时器（前提：瘦身功能开启且尚未释放）</summary>
        private void ScheduleSlim()
        {
            if (_disposed || _idleTimer == null) return;
            try { _idleTimer.Change(_options.IdleSlimDelayMs, Timeout.Infinite); } catch (Exception ex) { Debug.WriteLine($"PaddleOcrProvider.ScheduleSlim: {ex.Message}"); }
        }

        /// <summary>取消待触发的瘦身（即将开始一次识别/预热时调用）</summary>
        private void CancelSlim()
        {
            if (_idleTimer == null) return;
            try { _idleTimer.Change(Timeout.Infinite, Timeout.Infinite); } catch (Exception ex) { Debug.WriteLine($"PaddleOcrProvider.CancelSlim: {ex.Message}"); }
        }

        /// <summary>
        /// 根据当前配置应用瘦身策略：开启则确保计时器存在并重新计
        /// 时；关闭则取消计时器（此后引擎一直常驻）。
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

                DisposeEngine();               // 释放原生推理实例（释放其占用的非托管内存）
                TrimWorkingSet();              // 强制 OS 把工作集页换出，回落到接近 10MB
                Debug.WriteLine("PaddleOcrProvider: 闲置超时，已卸载推理实例并压缩工作集");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"PaddleOcrProvider: 瘦身异常 - {ex.Message}");
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>
        /// 调用 EmptyWorkingSet 迫使系统把本进程的工作集压缩到最小。
        /// 三步顺序对标 SnapFind 的 OptimizeMemory：
        ///   1) GC.Collect ×2 —— 释放托管侧已无引用的推理缓冲；
        ///   2) MKL_Free_Buffers —— 释放 Intel MKL 的线程本地 scratch buffer
        ///      （Paddle 推理时分配，DisposeEngine 与 EmptyWorkingSet 都释放不掉）；
        ///   3) EmptyWorkingSet —— 强制 OS 把工作集页换出，回落到接近 10MB。
        /// 原生 DLL 仍留在地址空间（无法卸载），但常驻占用的非托管推理内存
        /// 已在 DisposeEngine 中释放，配合此调用后任务管理器中的"内存"可落到 10MB 级别。
        /// </summary>
        private void TrimWorkingSet()
        {
            try
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();

                FreeMklBuffers();

                // 伪句柄 (-1) 不需要关闭；EmptyWorkingSet 需要 PROCESS_SET_QUOTA，
                // 当前进程伪句柄自带该权限
                EmptyWorkingSet(GetCurrentProcess());
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"PaddleOcrProvider: 压缩工作集失败 - {ex.Message}");
            }
        }

        /// <summary>
        /// 释放 Intel MKL 在推理期间分配的线程本地 scratch buffer。
        /// 这些 buffer 不会被 DisposeEngine 释放，EmptyWorkingSet 也压不掉，
        /// 必须显式调用 MKL_Free_Buffers（对标 SnapFind 的瘦身链路）。
        /// </summary>
        private void FreeMklBuffers()
        {
            try
            {
                if (!_mklResident) return;   // 引擎从未加载则无需（也无从）释放
                var path = Path.Combine(OcrModelCatalog.LibsDirectory, "mklml.dll");
                if (File.Exists(path))
                    LoadLibrary(path);        // 确保已加载，DllImport 才能按名解析符号
                MKL_Free_Buffers();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"PaddleOcrProvider: MKL_Free_Buffers 失败 - {ex.Message}");
            }
        }

        // ══════════════════════════════════════════════
        //  内部实现（均在信号量保护下的线程池线程执行）
        // ══════════════════════════════════════════════

        private OcrResult RecognizeCore(Bitmap image)
        {
            var engine = EnsureEngine();
            if (engine == null)
                return OcrResult.Fail(_initError ?? "OCR 引擎不可用");

            Bitmap? prepared = null;
            try
            {
                var sw = Stopwatch.StartNew();
                prepared = Prepare(image, out bool inverted, out double preprocessScale);
                var raw = engine.DetectText(prepared);
                sw.Stop();

                if (raw?.TextBlocks == null || raw.TextBlocks.Count == 0)
                    return OcrResult.Fail("未识别到文字");

                // 注意：不能直接用 raw.Text —— 它把所有文本框无分隔地拼在一起。
                // 传入原图尺寸与预处理缩放系数，让版面层把引擎坐标换算回原图坐标系，
                // 下游据此即可直接在截图上作画（对照高亮 / 译文叠图 / 按块复制）。
                var layout = OcrTextLayout.Compose(
                    raw.TextBlocks, image.Width, image.Height, preprocessScale);

                if (string.IsNullOrWhiteSpace(layout.Text))
                    return OcrResult.Fail("未识别到文字");

                return OcrResult.Ok(layout.Text, layout.Lines, sw.ElapsedMilliseconds,
                                    layout.Blocks.Count, inverted, layout.Blocks);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"PaddleOcrProvider: 识别失败 - {ex}");
                return OcrResult.Fail($"识别失败: {ex.Message}");
            }
            finally
            {
                if (!ReferenceEquals(prepared, image))
                    prepared?.Dispose();
            }
        }

        /// <summary>
        /// 创建（或复用）原生引擎。
        /// 首选配置的模型；若其文件缺失或初始化失败，则按回退顺序自动切换到其它可用模型，
        /// 避免因为某个模型目录被误删而导致整个 OCR 不可用（对标 SnapFind 的模型缺失回退）。
        /// 回退选择会缓存到 <see cref="_effectiveModel"/>，全部失败时给出明确的缺失清单，
        /// 而不是每次识别都重试数秒。
        /// </summary>
        private PaddleOCREngine? EnsureEngine()
        {
            if (_engine != null) return _engine;
            if (_initError != null) return null;

            // 一旦确定了实际可用模型，直接复用，不再每次重新探测
            OcrModelKind candidate = _effectiveModel ?? _options.Model;

            foreach (var kind in FallbackOrder(candidate))
            {
                if (_failedModels.Contains(kind)) continue;

                if (!OcrModelCatalog.TryValidate(kind, out var validationError))
                {
                    // 文件缺失：记录并跳过（若它是当前首选，提示会更明显）
                    Debug.WriteLine($"PaddleOcrProvider: 模型 {OcrModelCatalog.DisplayName(kind)} 文件缺失 - {validationError}");
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
                    _mklResident = true;   // 引擎初始化会拉起 mklml.dll，后续瘦身可释放其缓冲
                    Debug.WriteLine($"PaddleOcrProvider: 引擎初始化完成 ({OcrModelCatalog.DisplayName(kind)}) 耗时 {sw.ElapsedMilliseconds} ms{(kind != _options.Model ? "（已回退）" : "")}");
                    return _engine;
                }
                catch (Exception ex)
                {
                    _failedModels.Add(kind);
                    Debug.WriteLine($"PaddleOcrProvider: 模型 {OcrModelCatalog.DisplayName(kind)} 初始化失败 - {ex.Message}");
                    // 继续尝试下一个候选模型
                }
            }

            // 所有候选模型都不可用
            _initError = BuildUnavailableMessage(candidate);
            Debug.WriteLine($"PaddleOcrProvider: {_initError}");
            return null;
        }

        /// <summary>按指定模型构造原生推理引擎（调用方需保证该模型已通过 TryValidate）</summary>
        private PaddleOCREngine BuildEngine(OcrModelKind kind)
        {
            // 必须在实例化引擎之前指定原生库目录（进程级只需设置一次）
            if (Interlocked.Exchange(ref _dllPathInitialized, 1) == 0)
                EngineBase.PaddleOCRdllPath = OcrModelCatalog.LibsDirectory;

            var paths = OcrModelCatalog.Resolve(kind);
            var config = new OCRModelConfig(paths.Det, paths.Cls, paths.Rec, paths.Keys);

            var parameter = new OCRParameter
            {
                use_gpu = false,
                enable_mkldnn = _options.EnableMkldnn,
                cpu_math_library_num_threads = _options.EffectiveThreads,

                det = true,
                rec = true,

                // 截图内的文字基本都是正向的，关掉方向分类可省一次推理
                cls = _options.EnableAngleClassification,
                use_angle_cls = _options.EnableAngleClassification,

                // 兜底层：超大截图检测侧最长边上限，对标 SnapFind 的 max_side_len=960
                max_side_len = MaxSideLen,
            };

            var engine = new PaddleOCREngine(config, parameter);

            // 单字容易被检测成菱形框，矫正为正矩形可提升识别率（仅对水平文字有效）
            try { engine.EnableDetUseRect(true); } catch { /* 老版本原生库可能不支持 */ }

            return engine;
        }

        /// <summary>
        /// 模型回退优先级：首选模型排在最前，其余按「通用性 / 体积」排序。
        /// 这样首选缺失时优先回退到最全能的 V6_Small，再退而求其次。
        /// </summary>
        private static IEnumerable<OcrModelKind> FallbackOrder(OcrModelKind preferred)
        {
            var order = new List<OcrModelKind>
            {
                preferred,
                OcrModelKind.V6Small,
                OcrModelKind.V6Tiny,
                OcrModelKind.V5Chinese,
                OcrModelKind.V5English,
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
        /// 预处理：统一为 24 位色，对过小的选区做放大，必要时对深色背景反色。
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

            // 即便不放大也复制一份 24bpp 位图：屏幕截图带 Alpha 通道，
            // 统一成 3 通道可避免原生侧对透明度的处理差异
            var target = new Bitmap(width, height, PixelFormat.Format24bppRgb);
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
                Debug.WriteLine($"PaddleOcrProvider: 背景极性检测失败，按浅色处理 - {ex.Message}");
                return false;
            }
        }

        private void DisposeEngine()
        {
            if (_engine == null) return;
            try
            {
                // PaddleOCREngine 提供 Dispose() 但并未实现 IDisposable，只能直接调用
                _engine.Dispose();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"PaddleOcrProvider: 释放引擎异常 - {ex.Message}");
            }
            finally
            {
                _engine = null;
                _ready = false;   // 引擎已卸载，下次识别需重新加载
                _mklResident = false;
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
                Debug.WriteLine($"PaddleOcrProvider.Dispose(timer): {ex.Message}");
            }

            try
            {
                _gate.Wait(TimeSpan.FromSeconds(10));
                DisposeEngine();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"PaddleOcrProvider: Dispose 异常 - {ex.Message}");
            }
            finally
            {
                try { _gate.Release(); } catch (Exception ex) { Debug.WriteLine($"PaddleOcrProvider.Dispose(gate): {ex.Message}"); }
                _gate.Dispose();
            }
        }
    }
}
