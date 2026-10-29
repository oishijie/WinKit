using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
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

                // 每轮读取间隔，设置页改完即时生效
                int interval = Math.Max(1000, _settingsManager.Settings.SyncIntervalMs);
                try
                {
                    await PullAsync(ct).ConfigureAwait(false);
                    await PushAsync(ct).ConfigureAwait(false);
                }
                catch
                {
                    // 网络 / 服务器异常静默，下一轮重试
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
                    // 官方多文件组（zip 打包），一期不做
                    SyncSkipped?.Invoke("远端为多文件组，暂不支持拉取");
                    _lastRemoteHash = profile.Hash;
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
                    System.Windows.Clipboard.SetImage(frame);
                    _clipboardManager.AddImageItem(frame);
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
