using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using WinKit.Common;
using WinKit.Clipboard.Models;

namespace WinKit.Clipboard.Services
{
    /// <summary>
    /// SyncClipboard 协议同步引擎（对接官方服务器 / SyncClipboardCfServer 等兼容服务端）
    /// - 双轮询环：先拉后推，间隔可配（默认 5s，对齐官方客户端探活节奏）
    /// - 防回环：推/拉各自记录内容 hash；拉取产生的历史条目以 Id 守门，杜绝图片重编码导致的误推回
    /// - hash 官方格式：文本 SHA256(UTF8)；图片/文件 SHA256("文件名|" + 大写hex(内容SHA256))，均大写十六进制
    /// - 图片/文件为两步上传：先 PUT file/{dataName} 再 PUT SyncClipboard.json 换指针
    /// - 类型开关 / 大小上限 / 文件黑名单均可配（AppSettings）
    /// </summary>
    public class SyncEngine : IDisposable
    {
        private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

        private readonly HttpClient _http;
        private readonly ClipboardManager _clipboardManager;
        private readonly SettingsManager _settingsManager;
        private readonly Dispatcher _dispatcher;
        private readonly SHA256 _sha256 = SHA256.Create();

        /// <summary>同步成功通知（推送 / 拉取各一发，后台线程触发，订阅方自行切线程；受通知开关过滤）</summary>
        public event Action<string>? SyncSucceeded;

        /// <summary>内容被守门规则跳过时的提示（超上限 / 黑名单 / 不支持的类型），始终触发</summary>
        public event Action<string>? SyncSkipped;

        // 防回环标记
        private string? _lastPushedHash;      // 最近一次推送 / 拉取回写的官方 hash（文本、文件有效）
        private string? _lastPulledHash;      // 最近一次拉取的官方 hash
        private string? _lastPushedContentHash; // 图片/文件防重推：与文件名无关的内容 hash
        private string? _lastRemoteHash;      // 远端当前 hash（拉取侧去重）
        private Guid _lastPulledItemId;       // 拉取产生的历史条目（图片重编码后字节变化，hash 守不住，用 Id）

        private CancellationTokenSource? _cts;

        // ── 同步状态可视化（设置页「同步」分区展示）─────────────────
        /// <summary>上次一轮推拉全部正常的时间</summary>
        public DateTime? LastSuccessAt { get; private set; }

        /// <summary>最近一次失败的摘要（null = 当前无错误）</summary>
        public string? LastError { get; private set; }

        /// <summary>连续失败轮数（成功即清零；≥3 触发退避）</summary>
        public int ConsecutiveFailures { get; private set; }

        /// <summary>当前实际轮询间隔（退避后可能大于配置值，供设置页展示）</summary>
        public int EffectiveIntervalMs => Math.Min(
            (int)(Math.Max(1000, _settingsManager.Settings.SyncIntervalMs) * BackoffMultiplier),
            60_000);

        /// <summary>退避倍率：0 失败 = 1x；连续失败 ≥3 次翻倍，封顶 60s（防止服务器异常时空转打请求）</summary>
        private double BackoffMultiplier => ConsecutiveFailures switch
        {
            0 => 1.0,
            1 => 1.0,
            2 => 2.0,
            _ => Math.Min(Math.Pow(2, ConsecutiveFailures - 1), 12.0) // 4x, 8x, 12x 封顶
        };

        /// <summary>Nudge 唤醒源：右键推送等显式动作后立即触发一轮循环，不等轮询间隔</summary>
        private TaskCompletionSource<bool>? _nudgeTcs;

        public SyncEngine(ClipboardManager clipboardManager, SettingsManager settingsManager)
        {
            _clipboardManager = clipboardManager;
            _settingsManager = settingsManager;
            _dispatcher = System.Windows.Application.Current.Dispatcher;

            var settings = settingsManager.Settings;
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            var raw = $"{settings.SyncUser}:{settings.SyncPassword}";
            _http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(raw)));
        }

        public void Start()
        {
            if (_cts != null) return;
            _cts = new CancellationTokenSource();
            _ = LoopAsync(_cts.Token);
        }

        private async Task LoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                // Nudge 唤醒源每轮重建：Task.WhenAny 与 Delay 竞争，被 Set 即立即进入下一轮
                _nudgeTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var nudge = _nudgeTcs.Task;

                // 每轮读取间隔，设置页改完即时生效；连续失败后指数退避（封顶 60s）
                int interval = (int)(Math.Max(1000, _settingsManager.Settings.SyncIntervalMs) * BackoffMultiplier);
                interval = Math.Min(interval, 60_000);
                try
                {
                    await PullAsync(ct).ConfigureAwait(false);
                    await PushAsync(ct).ConfigureAwait(false);
                    ConsecutiveFailures = 0;
                    LastError = null;
                    LastSuccessAt = DateTime.Now;
                }
                catch (Exception ex)
                {
                    // 网络 / 服务器异常：记账 + 退避，下一轮重试（不弹窗）
                    ConsecutiveFailures++;
                    LastError = ex.Message;
                }

                try
                {
                    var delay = Task.Delay(interval, ct);
                    await Task.WhenAny(delay, nudge).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }
            }
        }

        /// <summary>立即触发一轮推 / 拉（右键推送后调用，省掉最多一个轮询间隔的等待）</summary>
        public void Nudge()
        {
            _nudgeTcs?.TrySetResult(true);
        }

        // ══════════════════════════════════════════════════
        //  拉取：远端 → 本地
        // ══════════════════════════════════════════════════

        private async Task PullAsync(CancellationToken ct)
        {
            var s = _settingsManager.Settings;
            using var resp = await _http.GetAsync(BuildUrl("SyncClipboard.json"), ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return; // 404 = 远端为空，交给上传环

            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var profile = JsonSerializer.Deserialize<SyncClipboardProfile>(json, JsonOpts);
            if (profile == null || string.IsNullOrEmpty(profile.Hash)) return;
            if (profile.Hash == _lastRemoteHash) return; // 远端无变化

            switch (profile.Type)
            {
                case "Text":
                    if (!s.SyncText) { _lastRemoteHash = profile.Hash; return; }
                    await PullTextAsync(profile, ct).ConfigureAwait(false);
                    break;

                case "Image":
                    if (!s.SyncImage) { _lastRemoteHash = profile.Hash; return; }
                    await PullImageAsync(profile, ct).ConfigureAwait(false);
                    break;

                case "File":
                    if (!s.SyncFile) { _lastRemoteHash = profile.Hash; return; }
                    await PullFileAsync(profile, ct).ConfigureAwait(false);
                    break;

                case "Group":
                    if (!s.SyncFile) { _lastRemoteHash = profile.Hash; return; }
                    await PullGroupAsync(profile, ct).ConfigureAwait(false);
                    break;

                default:
                    _lastRemoteHash = profile.Hash;
                    break;
            }
        }

        private async Task PullTextAsync(SyncClipboardProfile profile, CancellationToken ct)
        {
            var text = profile.Text ?? string.Empty;
            if (text.Length == 0) { _lastRemoteHash = profile.Hash; return; }

            var official = ComputeTextHash(text);
            if (official == _lastPulledHash || official == _lastPushedHash)
            {
                _lastRemoteHash = profile.Hash; // 自己推过的 / 已拉过的
                return;
            }

            await _dispatcher.InvokeAsync(() =>
            {
                try
                {
                    if (System.Windows.Clipboard.GetText() != text)
                        System.Windows.Clipboard.SetText(text);
                }
                catch { /* 剪贴板被占用时忽略 */ }
                _clipboardManager.AddTextItem(text);
                _lastPulledItemId = _clipboardManager.Items.Count > 0
                    ? _clipboardManager.Items[0].Id : Guid.Empty;
            }).Task.ConfigureAwait(false);

            _lastPulledHash = official;
            _lastPushedHash = official; // 拉取的内容不再被上传环推回
            _lastRemoteHash = profile.Hash;

            if (_settingsManager.Settings.SyncNotifyPull)
                SyncSucceeded?.Invoke($"收到远端：{Preview(text)}");
        }

        private async Task PullImageAsync(SyncClipboardProfile profile, CancellationToken ct)
        {
            var dataName = profile.DataName;
            if (string.IsNullOrEmpty(dataName)) { _lastRemoteHash = profile.Hash; return; }

            var s = _settingsManager.Settings;
            if (profile.Size > s.SyncImageMaxMB * 1024L * 1024L)
            {
                SyncSkipped?.Invoke($"远端图片（{FormatSize(profile.Size ?? 0)}）超出本地上限，跳过拉取");
                _lastRemoteHash = profile.Hash;
                return;
            }

            byte[] bytes;
            try
            {
                bytes = await _http.GetByteArrayAsync(BuildUrl($"file/{Uri.EscapeDataString(dataName)}"), ct)
                    .ConfigureAwait(false);
            }
            catch
            {
                return; // 下载失败不记标记，下轮重试
            }

            // transferDataHash 校验（官方规范：内容 SHA256 大写 hex）
            if (!string.IsNullOrEmpty(profile.TransferDataHash) &&
                !profile.TransferDataHash.Equals(ComputeContentHash(bytes), StringComparison.OrdinalIgnoreCase))
            {
                _lastRemoteHash = profile.Hash; // 数据不完整 / 损坏，放弃本轮
                return;
            }

            int w = 0, h = 0;
            var ext = Path.GetExtension(dataName);
            await _dispatcher.InvokeAsync(() =>
            {
                try
                {
                    var frame = System.Windows.Media.Imaging.BitmapFrame.Create(
                        new MemoryStream(bytes),
                        System.Windows.Media.Imaging.BitmapCreateOptions.None,
                        System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
                    w = frame.PixelWidth;
                    h = frame.PixelHeight;

                    // 原始字节入库（保留 jpg/png 原格式，避免 BitmapSource→PNG 二次编码损失）
                    _clipboardManager.AddImageItem(bytes, ext);

                    // 剪贴板双格式：位图（即时粘贴显示）+ 原始文件（可粘贴出原文件）
                    var imagePath = _clipboardManager.Items.Count > 0 ? _clipboardManager.Items[0].ImagePath : null;
                    if (imagePath != null && File.Exists(imagePath))
                    {
                        var data = new System.Windows.DataObject();
                        data.SetImage(frame);
                        data.SetFileDropList(new StringCollection { imagePath });
                        System.Windows.Clipboard.SetDataObject(data);
                    }
                    else
                    {
                        System.Windows.Clipboard.SetImage(frame);
                    }

                    if (_clipboardManager.Items.Count > 0)
                        _lastPulledItemId = _clipboardManager.Items[0].Id;
                }
                catch { /* 剪贴板被占用 / 解码失败时忽略 */ }
            }).Task.ConfigureAwait(false);

            _lastPulledHash = profile.Hash;
            _lastPushedHash = profile.Hash;
            _lastPushedContentHash = ComputeContentHash(bytes);
            _lastRemoteHash = profile.Hash;

            if (s.SyncNotifyPull)
                SyncSucceeded?.Invoke($"收到远端图片 ({w}×{h}, {FormatSize(bytes.Length)})");
        }

        private async Task PullFileAsync(SyncClipboardProfile profile, CancellationToken ct)
        {
            var dataName = profile.DataName;
            if (string.IsNullOrEmpty(dataName)) { _lastRemoteHash = profile.Hash; return; }

            var s = _settingsManager.Settings;
            if (profile.Size > s.SyncFileMaxMB * 1024L * 1024L)
            {
                SyncSkipped?.Invoke($"远端文件（{FormatSize(profile.Size ?? 0)}）超出本地上限，跳过拉取");
                _lastRemoteHash = profile.Hash;
                return;
            }

            byte[] bytes;
            try
            {
                bytes = await _http.GetByteArrayAsync(BuildUrl($"file/{Uri.EscapeDataString(dataName)}"), ct)
                    .ConfigureAwait(false);
            }
            catch
            {
                return; // 下轮重试
            }

            if (!string.IsNullOrEmpty(profile.TransferDataHash) &&
                !profile.TransferDataHash.Equals(ComputeContentHash(bytes), StringComparison.OrdinalIgnoreCase))
            {
                _lastRemoteHash = profile.Hash;
                return;
            }

            var localPath = Path.Combine(AppPaths.SyncDir, SanitizeFileName(dataName));
            try
            {
                await File.WriteAllBytesAsync(localPath, bytes, ct).ConfigureAwait(false);
            }
            catch
            {
                return; // 磁盘写入失败，下轮重试
            }

            await _dispatcher.InvokeAsync(() =>
            {
                try
                {
                    var list = new StringCollection { localPath };
                    System.Windows.Clipboard.SetFileDropList(list);
                    _clipboardManager.AddFileItem(localPath);
                    if (_clipboardManager.Items.Count > 0)
                        _lastPulledItemId = _clipboardManager.Items[0].Id;
                }
                catch { /* 剪贴板被占用时忽略 */ }
            }).Task.ConfigureAwait(false);

            _lastPulledHash = profile.Hash;
            _lastPushedHash = profile.Hash; // 文件字节不变，官方 hash 一致，天然防推回
            _lastPushedContentHash = ComputeContentHash(bytes);
            _lastRemoteHash = profile.Hash;

            if (s.SyncNotifyPull)
                SyncSucceeded?.Invoke($"收到远端文件：{dataName} ({FormatSize(bytes.Length)})");
        }

        // ══════════════════════════════════════════════════
        //  推送：本地 → 远端（只推 Items[0]，当前剪贴板最新内容）
        // ══════════════════════════════════════════════════

        private async Task PushAsync(CancellationToken ct)
        {
            var s = _settingsManager.Settings;

            // UI 线程取最新项快照，避免与集合并发冲突
            var top = await _dispatcher.InvokeAsync(() =>
            {
                var items = _clipboardManager.Items;
                if (items.Count == 0) return null;
                var i = items[0];
                return new TopItem(i.Id, i.Type, i.Content, i.ImagePath, i.FilePath);
            }).Task.ConfigureAwait(false);

            if (top == null) return;
            if (top.Id == _lastPulledItemId) return; // 刚拉取的内容不推回

            switch (top.Type)
            {
                case ClipboardItemType.Text:
                    if (s.SyncText && !string.IsNullOrEmpty(top.Content))
                        await PushTextAsync(top.Content, ct).ConfigureAwait(false);
                    break;

                case ClipboardItemType.Image:
                    // 自动推送受开关控制（默认关）：复制仅入历史，右键菜单显式推送见 PushFileNowAsync
                    if (s.SyncAutoPushImage && !string.IsNullOrEmpty(top.ImagePath))
                        await PushImageAsync(top.ImagePath, explicitPush: false, ct).ConfigureAwait(false);
                    break;

                case ClipboardItemType.File:
                    if (s.SyncAutoPushFile && !string.IsNullOrEmpty(top.FilePath))
                        await PushFileAsync(top.FilePath, explicitPush: false, ct).ConfigureAwait(false);
                    break;
            }
        }

        /// <summary>
        /// 显式推送本地文件（右键「同步到手机」入口）。
        /// 显式意图优先：无视自动推送 / 接收开关、黑名单放行；仅大小上限仍拦截（物理限制）。
        /// 同时把文件写入系统剪贴板与历史，并 Nudge 立即推送。
        /// </summary>
        public void PushFileNow(string filePath)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return;

            // 剪贴板与历史操作需 UI 线程
            _dispatcher.InvokeAsync(() =>
            {
                try
                {
                    var list = new StringCollection { filePath };
                    System.Windows.Clipboard.SetFileDropList(list);
                }
                catch { /* 剪贴板被占用时忽略 */ }
                _clipboardManager.AddFileItem(filePath);
            });

            // 显式推送在后台执行，完成后 Nudge 立即换指针
            _ = Task.Run(async () =>
            {
                try
                {
                    await PushFileAsync(filePath, explicitPush: true, CancellationToken.None).ConfigureAwait(false);
                    Nudge();
                }
                catch { /* 静默 */ }
            });
        }

        private sealed record TopItem(Guid Id, ClipboardItemType Type, string? Content, string? ImagePath, string? FilePath);

        private async Task PushTextAsync(string text, CancellationToken ct)
        {
            var hash = ComputeTextHash(text);
            if (hash == _lastPushedHash || hash == _lastPulledHash) return; // 无新内容

            var profile = new SyncClipboardProfile
            {
                Type = "Text",
                Text = text,
                HasData = false,
                Hash = hash
            };
            if (await PutProfileAsync(profile, ct).ConfigureAwait(false))
            {
                _lastPushedHash = hash;
                _lastPushedContentHash = hash;
                if (_settingsManager.Settings.SyncNotifyPush)
                    SyncSucceeded?.Invoke($"已推送：{Preview(text)}");
            }
        }

        private async Task PushImageAsync(string imagePath, bool explicitPush, CancellationToken ct)
        {
            long length;
            byte[] bytes;
            string contentHash;
            try
            {
                var fi = new FileInfo(imagePath);
                length = fi.Length;
                if (length > _settingsManager.Settings.SyncImageMaxMB * 1024L * 1024L)
                {
                    SyncSkipped?.Invoke($"图片（{FormatSize(length)}）超出推送上限，已跳过");
                    _lastPushedContentHash = $"skip|{imagePath}|{length}"; // 避免每轮重复提示
                    return;
                }
                bytes = await File.ReadAllBytesAsync(imagePath, ct).ConfigureAwait(false);
                contentHash = ComputeContentHash(bytes);
            }
            catch { return; }

            if (!explicitPush && _lastPushedContentHash == contentHash) return; // 这张图已推过

            // 对齐官方命名 Image_时间戳.png；显式推送磁盘图片时保留原扩展名（手机端按内容解码）
            var ext = explicitPush ? Path.GetExtension(imagePath).ToLowerInvariant() : ".png";
            if (ext.Length == 0 || ext.Length > 5) ext = ".png";
            var dataName = $"Image_{DateTime.Now:yyyyMMddHHmmssfff}{ext}";
            var hash = ComputeOfficialFileHash(dataName, contentHash);
            if (!explicitPush && hash == _lastPulledHash) return;

            if (!await UploadDataAsync(dataName, bytes, ct).ConfigureAwait(false)) return;

            var profile = new SyncClipboardProfile
            {
                Type = "Image",
                Text = dataName,
                HasData = true,
                DataName = dataName,
                TransferDataHash = contentHash,
                Hash = hash,
                Size = length
            };
            if (await PutProfileAsync(profile, ct).ConfigureAwait(false))
            {
                _lastPushedHash = hash;
                _lastPushedContentHash = contentHash;
                if (_settingsManager.Settings.SyncNotifyPush)
                    SyncSucceeded?.Invoke($"已推送图片 ({FormatSize(length)})");
            }
        }

        private async Task PushFileAsync(string filePath, bool explicitPush, CancellationToken ct)
        {
            var name = Path.GetFileName(filePath);
            var s = _settingsManager.Settings;

            // 文件黑名单（对齐官方上传黑名单）；显式推送（右键）放行——用户主动点了就是要传
            if (!explicitPush)
            {
                var ext = Path.GetExtension(name).TrimStart('.');
                if (!string.IsNullOrEmpty(ext) && ParseBlacklist(s.SyncFileBlacklist).Contains(ext))
                {
                    SyncSkipped?.Invoke($"文件 {name} 命中黑名单（.{ext}），已跳过推送");
                    return;
                }
            }

            long length;
            string contentHash;
            try
            {
                var fi = new FileInfo(filePath);
                length = fi.Length;
                if (length > s.SyncFileMaxMB * 1024L * 1024L)
                {
                    SyncSkipped?.Invoke($"文件（{FormatSize(length)}）超出推送上限，已跳过");
                    _lastPushedContentHash = $"skip|{filePath}|{length}";
                    return;
                }
                contentHash = await ComputeContentHashFromFileAsync(filePath, ct).ConfigureAwait(false);
            }
            catch { return; }

            if (!explicitPush)
            {
                if (_lastPushedContentHash == contentHash) return; // 已推过
                if (contentHash == _lastPulledHash) return;
            }

            // 流式上传，不整读大文件
            try
            {
                using var fs = File.OpenRead(filePath);
                using var content = new StreamContent(fs);
                using var resp = await _http.PutAsync(
                    BuildUrl($"file/{Uri.EscapeDataString(name)}"), content, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) return;
            }
            catch { return; }

            var hash = ComputeOfficialFileHash(name, contentHash);
            var profile = new SyncClipboardProfile
            {
                Type = "File",
                Text = name,
                HasData = true,
                DataName = name,
                TransferDataHash = contentHash,
                Hash = hash,
                Size = length
            };
            if (await PutProfileAsync(profile, ct).ConfigureAwait(false))
            {
                _lastPushedHash = hash;
                _lastPushedContentHash = contentHash;
                if (s.SyncNotifyPush)
                    SyncSucceeded?.Invoke($"已推送文件：{name} ({FormatSize(length)})");
            }
        }

        // ══════════════════════════════════════════════════
        //  Group 多文件组（官方 zip 打包语义，协议逐字对齐 GroupProfile.cs）
        //  hash = 条目清单增量 SHA256：条目按 entryName 的 UTF-8 字节升序排序，
        //  文件行 "F|{entry}|{length}|{大写hex(内容SHA256)}\0"、目录行 "D|{entry}\0" 逐条 AppendData。
        //  transferDataHash = zip 文件内容的 SHA256（大写 hex）；dataName = File_{时间戳}.zip。
        // ══════════════════════════════════════════════════

        /// <summary>
        /// 显式推送多文件（右键多选入口）：打包 zip 为官方 Group 类型。
        /// 显式意图优先，仅总大小上限拦截；单文件自动降级为 File 类型。
        /// </summary>
        public void PushGroupNow(IReadOnlyList<string> files)
        {
            var valid = files.Where(File.Exists).ToList();
            if (valid.Count == 0) return;
            if (valid.Count == 1) { PushFileNow(valid[0]); return; }

            _dispatcher.InvokeAsync(() =>
            {
                try
                {
                    var list = new StringCollection();
                    foreach (var f in valid) list.Add(f);
                    System.Windows.Clipboard.SetFileDropList(list);
                }
                catch { /* 剪贴板被占用时忽略 */ }
                foreach (var f in valid) _clipboardManager.AddFileItem(f);
            });

            _ = Task.Run(async () =>
            {
                try
                {
                    await PushGroupAsync(valid, CancellationToken.None).ConfigureAwait(false);
                    Nudge();
                }
                catch { /* 静默 */ }
            });
        }

        private async Task PushGroupAsync(List<string> files, CancellationToken ct)
        {
            long total = 0;
            foreach (var f in files)
            {
                try { total += new FileInfo(f).Length; }
                catch { return; }
            }
            var maxBytes = _settingsManager.Settings.SyncFileMaxMB * 1024L * 1024L;
            if (total > maxBytes)
            {
                SyncSkipped?.Invoke($"多文件组（{FormatSize(total)}）超出推送上限，已跳过");
                return;
            }

            var dataName = $"File_{DateTime.Now:yyyyMMddHHmmssfff}.zip";
            var zipPath = Path.Combine(Path.GetTempPath(), dataName);
            string groupHash, transferHash;
            try
            {
                (groupHash, transferHash) = await BuildGroupZipAsync(files, zipPath, ct).ConfigureAwait(false);
            }
            catch { return; }

            try
            {
                await using var fs = File.OpenRead(zipPath);
                using var content = new StreamContent(fs);
                using var resp = await _http.PutAsync(
                    BuildUrl($"file/{Uri.EscapeDataString(dataName)}"), content, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) return;
            }
            catch { return; }
            finally
            {
                try { File.Delete(zipPath); } catch { }
            }

            var profile = new SyncClipboardProfile
            {
                Type = "Group",
                Text = string.Join("\r\n", files.Select(Path.GetFileName)),
                HasData = true,
                DataName = dataName,
                TransferDataHash = transferHash,
                Hash = groupHash,
                Size = total
            };
            if (await PutProfileAsync(profile, ct).ConfigureAwait(false))
            {
                _lastPushedHash = groupHash;
                _lastPushedContentHash = transferHash;
                if (_settingsManager.Settings.SyncNotifyPush)
                    SyncSucceeded?.Invoke($"已推送文件组（{files.Count} 项，{FormatSize(total)}）");
            }
        }

        private sealed record GroupEntryItem(string EntryName, bool IsDir, long Length, string ContentHash);

        /// <summary>打包 zip（官方结构：文件条目名 = 文件名，目录条目名 = dirName/ 前缀）并算清单 hash</summary>
        private async Task<(string GroupHash, string TransferHash)> BuildGroupZipAsync(
            List<string> files, string zipPath, CancellationToken ct)
        {
            var entries = new List<GroupEntryItem>();
            await using (var fs = new FileStream(zipPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: false, Encoding.UTF8))
            {
                foreach (var path in files)
                {
                    ct.ThrowIfCancellationRequested();
                    if (Directory.Exists(path))
                    {
                        var dirName = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
                        zip.CreateEntry(dirName + "/", CompressionLevel.Optimal);
                        entries.Add(new GroupEntryItem(dirName + "/", true, 0, ""));
                        foreach (var sub in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                        {
                            var entryName = dirName + "/" + Path.GetRelativePath(path, sub).Replace('\\', '/');
                            zip.CreateEntryFromFile(sub, entryName, CompressionLevel.Optimal);
                            entries.Add(new GroupEntryItem(entryName, false, new FileInfo(sub).Length,
                                Convert.ToHexString(await SHA256.HashDataAsync(File.OpenRead(sub), ct).ConfigureAwait(false))));
                        }
                    }
                    else
                    {
                        var entryName = Path.GetFileName(path);
                        zip.CreateEntryFromFile(path, entryName, CompressionLevel.Optimal);
                        entries.Add(new GroupEntryItem(entryName, false, new FileInfo(path).Length,
                            await ComputeContentHashFromFileAsync(path, ct).ConfigureAwait(false)));
                    }
                }
            }

            var transferHash = await ComputeContentHashFromFileAsync(zipPath, ct).ConfigureAwait(false);
            return (ComputeGroupHash(entries), transferHash);
        }

        /// <summary>条目清单 hash（对齐官方 CalculateEntriesHashAsync：UTF-8 字节序排序 + 逐条 AppendData）</summary>
        private static string ComputeGroupHash(List<GroupEntryItem> entries)
        {
            var ordered = entries
                .Select(e => (Item: e, Key: Encoding.UTF8.GetBytes(e.EntryName)))
                .OrderBy(k => k.Key, ByteArrayComparer.Instance)
                .ToList();

            using var incremental = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach (var (item, _) in ordered)
            {
                var line = item.IsDir
                    ? $"D|{item.EntryName}\0"
                    : $"F|{item.EntryName}|{item.Length}|{item.ContentHash.ToUpperInvariant()}\0";
                incremental.AppendData(Encoding.UTF8.GetBytes(line));
            }
            return Convert.ToHexString(incremental.GetHashAndReset());
        }

        /// <summary>字节数组逐位比较（对齐官方 ByteArrayComparer，用于条目名排序）</summary>
        private sealed class ByteArrayComparer : IComparer<byte[]>
        {
            public static readonly ByteArrayComparer Instance = new();
            public int Compare(byte[]? x, byte[]? y)
            {
                if (x == null || y == null) return (x?.Length ?? 0).CompareTo(y?.Length ?? 0);
                int len = Math.Min(x.Length, y.Length);
                for (int i = 0; i < len; i++)
                    if (x[i] != y[i]) return x[i].CompareTo(y[i]);
                return x.Length.CompareTo(y.Length);
            }
        }

        /// <summary>拉取远端多文件组：下载 zip → 校验 → 解包 → FileDropList 写回剪贴板 + 逐文件入历史</summary>
        private async Task PullGroupAsync(SyncClipboardProfile profile, CancellationToken ct)
        {
            var dataName = profile.DataName;
            if (string.IsNullOrEmpty(dataName)) { _lastRemoteHash = profile.Hash; return; }

            var s = _settingsManager.Settings;
            if (profile.Size > s.SyncFileMaxMB * 1024L * 1024L)
            {
                SyncSkipped?.Invoke($"远端文件组（{FormatSize(profile.Size ?? 0)}）超出本地上限，跳过拉取");
                _lastRemoteHash = profile.Hash;
                return;
            }

            byte[] bytes;
            try
            {
                bytes = await _http.GetByteArrayAsync(BuildUrl($"file/{Uri.EscapeDataString(dataName)}"), ct)
                    .ConfigureAwait(false);
            }
            catch { return; } // 下轮重试

            if (!string.IsNullOrEmpty(profile.TransferDataHash) &&
                !profile.TransferDataHash.Equals(ComputeContentHash(bytes), StringComparison.OrdinalIgnoreCase))
            {
                _lastRemoteHash = profile.Hash;
                return;
            }

            var extractDir = Path.Combine(AppPaths.SyncDir,
                $"Group_{DateTime.Now:yyyyMMddHHmmssfff}");
            try
            {
                using var ms = new MemoryStream(bytes);
                using var zip = new ZipArchive(ms, ZipArchiveMode.Read, leaveOpen: false, Encoding.UTF8);
                Directory.CreateDirectory(extractDir);
                zip.ExtractToDirectory(extractDir, overwriteFiles: true);
            }
            catch
            {
                return; // 解包失败下轮重试
            }

            // 顶层条目写回 FileDropList；其中文件逐个入历史（目录仅进剪贴板）
            List<string> topEntries;
            List<string> fileEntries;
            try
            {
                topEntries = zipTopEntries(bytes);
                fileEntries = topEntries.Where(File.Exists).ToList();
            }
            catch { topEntries = fileEntries = new List<string>(); }

            static List<string> zipTopEntries(byte[] raw)
            {
                using var ms = new MemoryStream(raw);
                using var zip = new ZipArchive(ms, ZipArchiveMode.Read, leaveOpen: false, Encoding.UTF8);
                return zip.Entries
                    .Select(e => e.FullName.Replace('\\', '/').Split('/')[0])
                    .Where(x => x.Length > 0)
                    .Distinct()
                    .ToList();
            }

            await _dispatcher.InvokeAsync(() =>
            {
                try
                {
                    var list = new StringCollection();
                    foreach (var name in topEntries)
                        list.Add(Path.Combine(extractDir, name));
                    if (list.Count > 0)
                        System.Windows.Clipboard.SetFileDropList(list);
                    foreach (var f in fileEntries)
                        _clipboardManager.AddFileItem(f);
                    if (_clipboardManager.Items.Count > 0)
                        _lastPulledItemId = _clipboardManager.Items[0].Id;
                }
                catch { /* 剪贴板被占用时忽略 */ }
            }).Task.ConfigureAwait(false);

            _lastPulledHash = profile.Hash;
            _lastPushedHash = profile.Hash;
            _lastPushedContentHash = ComputeContentHash(bytes);
            _lastRemoteHash = profile.Hash;

            if (s.SyncNotifyPull)
                SyncSucceeded?.Invoke($"收到远端文件组（{topEntries.Count} 项，{FormatSize(bytes.Length)}）");
        }

        // ══════════════════════════════════════════════════
        //  HTTP 基础操作
        // ══════════════════════════════════════════════════

        private async Task<bool> PutProfileAsync(SyncClipboardProfile profile, CancellationToken ct)
        {
            try
            {
                var body = new StringContent(
                    JsonSerializer.Serialize(profile, JsonOpts),
                    Encoding.UTF8,
                    "application/json");
                using var resp = await _http.PutAsync(BuildUrl("SyncClipboard.json"), body, ct).ConfigureAwait(false);
                return resp.IsSuccessStatusCode;
            }
            catch { return false; }
        }

        private async Task<bool> UploadDataAsync(string dataName, byte[] bytes, CancellationToken ct)
        {
            try
            {
                using var content = new ByteArrayContent(bytes);
                using var resp = await _http.PutAsync(
                    BuildUrl($"file/{Uri.EscapeDataString(dataName)}"), content, ct).ConfigureAwait(false);
                return resp.IsSuccessStatusCode;
            }
            catch { return false; }
        }

        private string BuildUrl(string path)
        {
            return _settingsManager.Settings.SyncUrl.TrimEnd('/') + "/" + path;
        }

        // ══════════════════════════════════════════════════
        //  官方 hash 规则（全部大写十六进制）
        // ══════════════════════════════════════════════════

        /// <summary>文本：SHA256(UTF8) → 大写 hex</summary>
        private string ComputeTextHash(string text)
        {
            var bytes = _sha256.ComputeHash(Encoding.UTF8.GetBytes(text));
            return Convert.ToHexString(bytes);
        }

        /// <summary>内容 SHA256 → 大写 hex（即官方 transferDataHash）</summary>
        private string ComputeContentHash(byte[] data)
        {
            lock (_sha256)
            {
                return Convert.ToHexString(_sha256.ComputeHash(data));
            }
        }

        /// <summary>文件内容流式 SHA256 → 大写 hex（大文件不整读内存）</summary>
        private async Task<string> ComputeContentHashFromFileAsync(string path, CancellationToken ct)
        {
            await using var fs = File.OpenRead(path);
            using var sha = SHA256.Create();
            var hash = await sha.ComputeHashAsync(fs, ct).ConfigureAwait(false);
            return Convert.ToHexString(hash);
        }

        /// <summary>
        /// 图片 / 文件的官方 hash：
        /// SHA256( UTF8( 文件名 + "|" + 大写hex(内容SHA256) ) ) → 大写 hex
        /// </summary>
        private string ComputeOfficialFileHash(string fileName, string contentHashUpper)
        {
            var combined = Encoding.UTF8.GetBytes($"{fileName}|{contentHashUpper}");
            lock (_sha256)
            {
                return Convert.ToHexString(_sha256.ComputeHash(combined));
            }
        }

        // ══════════════════════════════════════════════════
        //  工具
        // ══════════════════════════════════════════════════

        private HashSet<string> ParseBlacklist(string raw)
        {
            return raw
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(x => x.TrimStart('.'))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>清洗远端文件名，去掉路径分隔符与 Windows 非法字符</summary>
        private static string SanitizeFileName(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            name = name.Replace('/', '_').Replace('\\', '_');
            return name.Length == 0 ? "file" : name;
        }

        private static string FormatSize(long bytesCount)
        {
            return bytesCount >= 1024 * 1024
                ? $"{bytesCount / 1024.0 / 1024.0:F1}MB"
                : $"{Math.Max(1, bytesCount / 1024)}KB";
        }

        /// <summary>气泡提示用的内容预览（单行、最长 30 字）</summary>
        private static string Preview(string text)
        {
            text = text.Trim().Replace("\r", " ").Replace("\n", " ");
            return text.Length <= 30 ? text : text.Substring(0, 30) + "…";
        }

        public void Dispose()
        {
            // 只 Cancel，不立即 Dispose CTS：正在进行的 HTTP 请求仍持有该 token
            _cts?.Cancel();
            _cts = null;
            _sha256.Dispose();
            _http.Dispose();
        }
    }

    /// <summary>
    /// SyncClipboard.json 协议报文（键名对齐官方 ProfileDto，camelCase）
    /// </summary>
    public class SyncClipboardProfile
    {
        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("hash")]
        public string? Hash { get; set; }

        [JsonPropertyName("text")]
        public string? Text { get; set; }

        [JsonPropertyName("hasData")]
        public bool HasData { get; set; }

        [JsonPropertyName("dataName")]
        public string? DataName { get; set; }

        [JsonPropertyName("transferDataHash")]
        public string? TransferDataHash { get; set; }

        [JsonPropertyName("size")]
        public long? Size { get; set; }
    }
}
