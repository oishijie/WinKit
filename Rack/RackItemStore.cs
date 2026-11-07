using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media.Imaging;
using WinKit.Common;

namespace WinKit.Rack
{
    /// <summary>rack_items.json 的根结构，对齐 SnowAir-Rack 插件。</summary>
    public sealed class RackSnapshot
    {
        [JsonPropertyName("version")]
        public int Version { get; set; } = 1;

        [JsonPropertyName("items")]
        public List<RackItem> Items { get; set; } = new();
    }

    /// <summary>
    /// 晾衣架持久化：把卡片写进 <c>%APPDATA%/WinKit/rack_items.json</c>，
    /// 位图按内容 hash 落到 <c>rack_images/&lt;hash&gt;.png</c>。
    ///
    /// 这样一次补上 MVP 两个老问题：重启清空、卡片不能重排（offsetX 落盘）。
    /// 写盘一律「临时文件 + 原子替换」，中途失败只留 .tmp，不会把整份数据写坏。
    /// </summary>
    public static class RackItemStore
    {
        private const int SnapshotVersion = 1;

        private static readonly JsonSerializerOptions Options = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
        };

        /// <summary>图片卡上限（同时受设置里的卡片总数上限约束）。</summary>
        public const int ImageFileLimit = 200;

        public static List<RackItem> Load()
        {
            try
            {
                AppPaths.EnsureDirectories();
                if (!File.Exists(AppPaths.RackItems)) return new List<RackItem>();

                var json = File.ReadAllText(AppPaths.RackItems, Encoding.UTF8);
                var snap = JsonSerializer.Deserialize<RackSnapshot>(json, Options);
                if (snap?.Items == null) return new List<RackItem>();

                var items = snap.Items.Where(i => i != null).ToList();
                foreach (var item in items) RestoreImage(item);
                return items;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"RackItemStore.Load: {ex.Message}");
                return new List<RackItem>();
            }
        }

        public static void Save(IEnumerable<RackItem> items)
        {
            try
            {
                AppPaths.EnsureDirectories();
                var snap = new RackSnapshot
                {
                    Version = SnapshotVersion,
                    Items = items.ToList(),
                };
                var json = JsonSerializer.Serialize(snap, Options);
                var tmp = AppPaths.RackItems + ".tmp";
                File.WriteAllText(tmp, json, new UTF8Encoding(false));
                File.Move(tmp, AppPaths.RackItems, overwrite: true);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"RackItemStore.Save: {ex.Message}");
            }
        }

        /// <summary>
        /// 把运行时位图编码成 PNG 落盘，并回填 Hash / ImagePath。
        /// 用 PNG 字节流直接做 SHA256，既是内容指纹又免得再算 stride。
        /// </summary>
        public static void StoreImage(RackItem item)
        {
            if (item.Image == null) return;
            try
            {
                AppPaths.EnsureDirectories();
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(item.Image));
                using var ms = new MemoryStream();
                encoder.Save(ms);
                var bytes = ms.ToArray();

                var hash = Convert.ToHexString(SHA256.HashData(bytes))
                    .Substring(0, 16).ToLowerInvariant();
                var fileName = hash + ".png";
                var full = Path.Combine(AppPaths.RackImages, fileName);
                if (!File.Exists(full)) File.WriteAllBytes(full, bytes);

                item.Hash = hash;
                item.ImagePath = fileName;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"RackItemStore.StoreImage: {ex.Message}");
            }
        }

        /// <summary>从磁盘还原图片位图。缺文件 / 解码失败只跳过这一张，不影响其它卡片。</summary>
        public static void RestoreImage(RackItem item)
        {
            if (item.Image != null || string.IsNullOrWhiteSpace(item.ImagePath)) return;
            try
            {
                var path = Path.Combine(AppPaths.RackImages, Path.GetFileName(item.ImagePath));
                if (!File.Exists(path)) return;

                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(path, UriKind.Absolute);
                bmp.EndInit();
                bmp.Freeze();
                item.Image = bmp;
                if (item.Aspect <= 0 && bmp.Height > 0) item.Aspect = bmp.Width / bmp.Height;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"RackItemStore.RestoreImage: {ex.Message}");
            }
        }

        /// <summary>文本类条目的去重指纹（图片类用 StoreImage 的 hash）。</summary>
        public static void EnsureTextHash(RackItem item)
        {
            if (!string.IsNullOrEmpty(item.Hash) || item.Image != null) return;
            var raw = (int)item.CardType + ":" + (item.Url ?? item.Text ?? item.Title ?? item.Id);
            item.Hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)))
                .Substring(0, 16).ToLowerInvariant();
        }

        /// <summary>
        /// 清空绳上的卡片（设置页「清空晾衣架」按钮）。
        /// 只清孤儿图片，剪贴板 / 待办两个合并池的数据不在本文件里，不受影响。
        /// </summary>
        public static void ClearAll()
        {
            try
            {
                if (File.Exists(AppPaths.RackItems)) File.Delete(AppPaths.RackItems);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"RackItemStore.ClearAll: {ex.Message}");
            }
            PruneImages();
        }

        /// <summary>删掉 rack_images/ 里没有被任何卡片引用的 PNG。</summary>
        public static void PruneImages()
        {
            try
            {
                if (!Directory.Exists(AppPaths.RackImages)) return;

                var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                CollectUsedImages(AppPaths.RackItems, used);

                foreach (var f in Directory.GetFiles(AppPaths.RackImages, "*.png"))
                {
                    if (used.Contains(Path.GetFileName(f))) continue;
                    try { File.Delete(f); } catch { /* 被占用就留给下次 */ }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"RackItemStore.PruneImages: {ex.Message}");
            }
        }

        private static void CollectUsedImages(string jsonFile, HashSet<string> used)
        {
            if (!File.Exists(jsonFile)) return;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(jsonFile, Encoding.UTF8));
                if (!doc.RootElement.TryGetProperty("items", out var arr) ||
                    arr.ValueKind != JsonValueKind.Array) return;

                foreach (var el in arr.EnumerateArray())
                {
                    if (!el.TryGetProperty("ImagePath", out var p) ||
                        p.ValueKind != JsonValueKind.String) continue;
                    var name = p.GetString();
                    if (!string.IsNullOrWhiteSpace(name)) used.Add(Path.GetFileName(name!));
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"RackItemStore.CollectUsedImages: {ex.Message}");
            }
        }

        /// <summary>只读 JSON 数组长度，不还原位图（设置页显示卡片数用）。</summary>
        public static int CountFast()
        {
            try
            {
                if (!File.Exists(AppPaths.RackItems)) return 0;
                using var doc = JsonDocument.Parse(File.ReadAllText(AppPaths.RackItems, Encoding.UTF8));
                return doc.RootElement.TryGetProperty("items", out var arr) &&
                       arr.ValueKind == JsonValueKind.Array
                    ? arr.GetArrayLength()
                    : 0;
            }
            catch
            {
                return 0;
            }
        }
    }
}
