using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Data.Sqlite;
using WinKit.Common;
using WinKit.Clipboard.Models;

namespace WinKit.Clipboard.Services
{
    /// <summary>
    /// 剪切板数据管理器 - 使用 SQLite 数据库存储并使用 Deflate 压缩文本
    /// </summary>
    public class ClipboardManager : IDisposable
    {
        private readonly string _dbPath;
        private readonly ObservableCollection<ClipboardItem> _items;
        private readonly SettingsManager _settingsManager;
        private SqliteConnection _connection = null!;
        private bool _isFullyLoaded = false;
        private int _insertsSinceCleanup = 0;

        private const int InitialLoadCount = 200;
        private const int BatchLoadCount = 500;
        private const int CleanupThrottle = 10; // 每 N 次插入执行一次清理

        public ReadOnlyObservableCollection<ClipboardItem> Items { get; }

        public event NotifyCollectionChangedEventHandler? ItemsChanged;

        public ClipboardManager(SettingsManager settingsManager)
        {
            _settingsManager = settingsManager;

            AppPaths.EnsureDirectories();
            _dbPath = AppPaths.Database;

            _items = new ObservableCollection<ClipboardItem>();
            _items.CollectionChanged += (s, e) => ItemsChanged?.Invoke(s, e);
            Items = new ReadOnlyObservableCollection<ClipboardItem>(_items);

            InitializeDatabase();
            LoadInitialData();

            // 后台分批加载剩余历史数据，避免阻塞 UI
            _ = LoadRemainingDataAsync();
        }

        private void InitializeDatabase()
        {
            _connection = new SqliteConnection($"Data Source={_dbPath}");
            _connection.Open();

            // WAL 模式：读写并发、写入性能更好，适合高频插入场景
            using (var pragma = _connection.CreateCommand())
            {
                pragma.CommandText = "PRAGMA journal_mode=WAL";
                pragma.ExecuteNonQuery();
            }

            var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS _meta (
                    key TEXT PRIMARY KEY,
                    value TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS clipboard_items (
                    id TEXT PRIMARY KEY,
                    content BLOB NOT NULL,
                    timestamp TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_timestamp ON clipboard_items(timestamp DESC);
            ";
            cmd.ExecuteNonQuery();

            // 迁移：为旧数据库添加 type 列（区分文本/图片）
            try
            {
                using var mig = _connection.CreateCommand();
                mig.CommandText = "ALTER TABLE clipboard_items ADD COLUMN type INTEGER NOT NULL DEFAULT 0";
                mig.ExecuteNonQuery();
            }
            catch { /* 列已存在则忽略 */ }

            // 迁移：为旧数据库添加 hash 列（图片内容哈希，用于去重）
            try
            {
                using var mig2 = _connection.CreateCommand();
                mig2.CommandText = "ALTER TABLE clipboard_items ADD COLUMN hash TEXT";
                mig2.ExecuteNonQuery();
            }
            catch { /* 列已存在则忽略 */ }
        }

        /// <summary>添加图片项：编码为 PNG 存盘，路径入库</summary>
        public void AddImageItem(System.Windows.Media.Imaging.BitmapSource image)
        {
            if (image == null || image.PixelWidth == 0) return;

            byte[] png;
            try
            {
                png = EncodeImageToPng(image);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ClipboardManager: 图片编码失败 - {ex.Message}");
                return;
            }

            if (png.Length == 0) return;

            // 内容级去重：本张图片的像素哈希与最新一项（图片）相同则不重复添加
            var hash = ComputeDataHash(png);
            if (_items.Count > 0 && _items[0].Type == ClipboardItemType.Image && _items[0].ContentHash == hash)
                return;

            var fileName = $"{Guid.NewGuid()}.png";
            var filePath = System.IO.Path.Combine(AppPaths.Images, fileName);

            try
            {
                File.WriteAllBytes(filePath, png);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ClipboardManager: 图片保存失败 - {ex.Message}");
                return;
            }

            var item = new ClipboardItem
            {
                Type = ClipboardItemType.Image,
                Content = fileName, // 相对路径
                ContentHash = hash,
                Timestamp = DateTime.Now
            };

            InsertItemToDb(item);
            _items.Insert(0, item);

            if (++_insertsSinceCleanup >= CleanupThrottle)
            {
                CleanupOldData();
                _insertsSinceCleanup = 0;
            }
        }

        public void AddTextItem(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;

            text = text.Trim();
            if (text.Length == 0) return;

            var settings = _settingsManager.Settings;

            // 哈希去重逻辑
            if (settings.PasteEnableTextDeduplication)
            {
                var hash = ComputeTextHash(text);
                var existingItem = _items.FirstOrDefault(item => item.Type == ClipboardItemType.Text && item.ContentHash == hash);
                
                if (existingItem != null)
                {
                    _items.Remove(existingItem);
                    var newItem = new ClipboardItem
                    {
                        Type = ClipboardItemType.Text,
                        Content = text,
                        ContentHash = hash,
                        Timestamp = DateTime.Now
                    };
                    ReplaceItemInDb(existingItem.Id, newItem);
                    _items.Insert(0, newItem);
                    
                    if (++_insertsSinceCleanup >= CleanupThrottle)
                    {
                        CleanupOldData();
                        _insertsSinceCleanup = 0;
                    }
                    return;
                }
            }
            else
            {
                // 简单去重：检查是否与最新项相同
                if (_items.Count > 0 && _items[0].Type == ClipboardItemType.Text && _items[0].Content == text)
                    return;
            }

            var item = new ClipboardItem
            {
                Type = ClipboardItemType.Text,
                Content = text,
                ContentHash = ComputeTextHash(text),
                Timestamp = DateTime.Now
            };

            InsertItemToDb(item);
            _items.Insert(0, item);

            if (++_insertsSinceCleanup >= CleanupThrottle)
            {
                CleanupOldData();
                _insertsSinceCleanup = 0;
            }
        }

        private static string ComputeTextHash(string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            return ComputeDataHash(bytes);
        }

        /// <summary>对原始字节计算 SHA256 内容哈希（图片去重 / 文本去重共用，线程安全：每次新建实例）</summary>
        private static string ComputeDataHash(byte[] data)
        {
            using var sha = SHA256.Create();
            return Convert.ToBase64String(sha.ComputeHash(data));
        }

        private void InsertItemToDb(ClipboardItem item)
        {
            var contentValue = item.Type == ClipboardItemType.Image
                ? (item.Content ?? string.Empty)  // 图片：存相对路径
                : (item.Content ?? string.Empty); // 文本：存文本本身
            var compressed = CompressContent(contentValue);
            var command = _connection.CreateCommand();
            command.CommandText = "INSERT INTO clipboard_items (id, content, timestamp, type, hash) VALUES (@id, @content, @timestamp, @type, @hash)";
            command.Parameters.AddWithValue("@id", item.Id.ToString());
            command.Parameters.AddWithValue("@content", compressed);
            command.Parameters.AddWithValue("@timestamp", item.Timestamp.ToString("O"));
            command.Parameters.AddWithValue("@type", (int)item.Type);
            command.Parameters.AddWithValue("@hash", item.ContentHash ?? string.Empty);
            command.ExecuteNonQuery();
        }

        public void RemoveItem(Guid id)
        {
            var item = _items.FirstOrDefault(i => i.Id == id);
            if (item != null)
            {
                _items.Remove(item);
                DeleteItemFromDb(id);
            }
        }

        private void DeleteItemFromDb(Guid id)
        {
            DeleteImageFile(id);
            var command = _connection.CreateCommand();
            command.CommandText = "DELETE FROM clipboard_items WHERE id = @id";
            command.Parameters.AddWithValue("@id", id.ToString());
            command.ExecuteNonQuery();
        }

        /// <summary>删除条目关联的图片文件（仅对 Image 类型有效）</summary>
        private void DeleteImageFile(Guid id)
        {
            var item = _items.FirstOrDefault(i => i.Id == id);
            if (item?.Type != ClipboardItemType.Image) return;
            try
            {
                var path = item.ImagePath;
                if (path != null && File.Exists(path))
                    File.Delete(path);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ClipboardManager: 删除图片文件失败 - {ex.Message}");
            }
        }

        private void ReplaceItemInDb(Guid oldId, ClipboardItem newItem)
        {
            DeleteImageFile(oldId);
            using var transaction = _connection.BeginTransaction();
            try
            {
                var deleteCmd = _connection.CreateCommand();
                deleteCmd.CommandText = "DELETE FROM clipboard_items WHERE id = @id";
                deleteCmd.Parameters.AddWithValue("@id", oldId.ToString());
                deleteCmd.ExecuteNonQuery();

                var compressed = CompressContent(newItem.Content ?? string.Empty);
                var insertCmd = _connection.CreateCommand();
                insertCmd.CommandText = "INSERT INTO clipboard_items (id, content, timestamp, type, hash) VALUES (@id, @content, @timestamp, @type, @hash)";
                insertCmd.Parameters.AddWithValue("@id", newItem.Id.ToString());
                insertCmd.Parameters.AddWithValue("@content", compressed);
                insertCmd.Parameters.AddWithValue("@timestamp", newItem.Timestamp.ToString("O"));
                insertCmd.Parameters.AddWithValue("@type", (int)newItem.Type);
                insertCmd.Parameters.AddWithValue("@hash", newItem.ContentHash ?? string.Empty);
                insertCmd.ExecuteNonQuery();

                transaction.Commit();
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }

        public void ClearAll()
        {
            _items.Clear();
            ClearAllFromDb();
        }

        private void ClearAllFromDb()
        {
            // 清理所有图片文件
            try
            {
                foreach (var item in _items.Where(i => i.Type == ClipboardItemType.Image))
                {
                    var path = item.ImagePath;
                    if (path != null && File.Exists(path))
                        File.Delete(path);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ClipboardManager: 清空图片文件失败 - {ex.Message}");
            }

            var command = _connection.CreateCommand();
            command.CommandText = "DELETE FROM clipboard_items";
            command.ExecuteNonQuery();
        }

        public IEnumerable<ClipboardItem> Search(string query)
        {
            if (string.IsNullOrWhiteSpace(query))
                return _items;

            query = query.ToLowerInvariant();
            return _items.Where(item => item.Type == ClipboardItemType.Text && item.Content?.ToLowerInvariant().Contains(query) == true);
        }

        public void CleanupOldData()
        {
            var settings = _settingsManager.Settings;
            int maxItems = settings.PasteMaxItems;

            var itemsToDelete = new List<ClipboardItem>();

            // 限制条目数
            while (_items.Count > maxItems)
            {
                var oldest = _items.LastOrDefault();
                if (oldest != null)
                {
                    _items.Remove(oldest);
                    itemsToDelete.Add(oldest);
                }
                else break;
            }

            if (itemsToDelete.Count > 0)
            {
                DeleteItemsBatchFromDb(itemsToDelete);
            }
        }

        private void DeleteItemsBatchFromDb(List<ClipboardItem> items)
        {
            // 删除关联的图片文件
            foreach (var item in items.Where(i => i.Type == ClipboardItemType.Image))
            {
                try
                {
                    var path = item.ImagePath;
                    if (path != null && File.Exists(path))
                        File.Delete(path);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"ClipboardManager: 批量删除图片失败 - {ex.Message}");
                }
            }

            using var transaction = _connection.BeginTransaction();
            try
            {
                foreach (var item in items)
                {
                    var command = _connection.CreateCommand();
                    command.CommandText = "DELETE FROM clipboard_items WHERE id = @id";
                    command.Parameters.AddWithValue("@id", item.Id.ToString());
                    command.ExecuteNonQuery();
                }
                transaction.Commit();
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }

        private void LoadInitialData()
        {
            try
            {
                var command = _connection.CreateCommand();
                command.CommandText = $"SELECT id, content, timestamp, type, hash FROM clipboard_items ORDER BY timestamp DESC LIMIT {InitialLoadCount}";

                using var reader = command.ExecuteReader();
                var loadedList = new List<ClipboardItem>();

                while (reader.Read())
                {
                    loadedList.Add(MakeItemFromRow(reader));
                }

                _items.Clear();
                foreach (var item in loadedList.OrderByDescending(i => i.Timestamp))
                {
                    _items.Add(item);
                }

                if (loadedList.Count < InitialLoadCount)
                {
                    _isFullyLoaded = true;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ClipboardManager: 初始数据加载失败 - {ex.Message}");
            }
        }

        private async Task LoadRemainingDataAsync()
        {
            if (_isFullyLoaded) return;

            try
            {
                int offset = InitialLoadCount;
                int totalLoaded = 0;

                // 后台加载使用独立连接（避免跨线程共享主连接），
                // 但只创建一次，循环内复用，不再每批 new 一个。
                await Task.Run(() =>
                {
                    using var bgConn = new SqliteConnection($"Data Source={_dbPath}");
                    bgConn.Open();

                    while (true)
                    {
                        var batchItems = new List<ClipboardItem>();
                        var command = bgConn.CreateCommand();
                        command.CommandText = $"SELECT id, content, timestamp, type, hash FROM clipboard_items ORDER BY timestamp DESC LIMIT {BatchLoadCount} OFFSET {offset}";

                        using var reader = command.ExecuteReader();
                        while (reader.Read())
                        {
                            batchItems.Add(MakeItemFromRow(reader));
                        }

                        if (batchItems.Count == 0) break;

                        // 回到 UI 线程更新 ObservableCollection
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            foreach (var item in batchItems)
                            {
                                _items.Add(item);
                            }
                        });

                        totalLoaded += batchItems.Count;
                        offset += BatchLoadCount;

                        if (batchItems.Count < BatchLoadCount) break;
                    }
                });

                _isFullyLoaded = true;
                System.Diagnostics.Debug.WriteLine($"ClipboardManager: 历史记录后台读取完成，共加载 {totalLoaded} 项");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ClipboardManager: 历史记录后台读取失败 - {ex.Message}");
            }
        }

        private static byte[] CompressContent(string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            using var outputStream = new MemoryStream();
            using (var deflateStream = new DeflateStream(outputStream, CompressionLevel.Fastest))
            {
                deflateStream.Write(bytes, 0, bytes.Length);
            }
            return outputStream.ToArray();
        }

        private static string DecompressContent(byte[] compressed)
        {
            using var inputStream = new MemoryStream(compressed);
            using var deflateStream = new DeflateStream(inputStream, CompressionMode.Decompress);
            using var outputStream = new MemoryStream();
            deflateStream.CopyTo(outputStream);
            return Encoding.UTF8.GetString(outputStream.ToArray());
        }

        /// <summary>从数据库行构造 ClipboardItem（自动区分文本/图片类型）</summary>
        private static ClipboardItem MakeItemFromRow(SqliteDataReader reader)
        {
            var id = Guid.Parse(reader.GetString(0));
            var content = DecompressContent((byte[])reader["content"]);
            var timestamp = DateTime.Parse(reader.GetString(2));
            var typeOrdinal = 0;
            try { typeOrdinal = reader.GetInt32(3); } catch { /* 旧行可能无 type 列 */ }
            var hash = string.Empty;
            try { hash = reader.GetString(4); } catch { /* 旧行可能无 hash 列 */ }

            // 优先使用持久化的内容哈希（图片为像素哈希、文本为内容哈希）；
            // 旧行无 hash 时回退按内容计算（图片回退为文件名哈希，去重能力较弱但行为不变）
            var contentHash = string.IsNullOrEmpty(hash) ? ComputeTextHash(content) : hash;

            return new ClipboardItem
            {
                Id = id,
                Type = (ClipboardItemType)typeOrdinal,
                Content = content,
                ContentHash = contentHash,
                Timestamp = timestamp
            };
        }

        /// <summary>将 BitmapSource 编码为 PNG 字节（超长边自动缩放到 1920px）</summary>
        private static byte[] EncodeImageToPng(System.Windows.Media.Imaging.BitmapSource source)
        {
            const int maxSide = 1920;
            double scale = 1.0;
            if (source.PixelWidth > maxSide || source.PixelHeight > maxSide)
            {
                scale = Math.Min((double)maxSide / source.PixelWidth, (double)maxSide / source.PixelHeight);
            }

            System.Windows.Media.Imaging.BitmapSource target = source;
            if (scale < 1.0)
            {
                int w = (int)(source.PixelWidth * scale);
                int h = (int)(source.PixelHeight * scale);
                target = new System.Windows.Media.Imaging.TransformedBitmap(source,
                    new System.Windows.Media.ScaleTransform(scale, scale));
            }

            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(target));

            using var ms = new MemoryStream();
            encoder.Save(ms);
            return ms.ToArray();
        }

        public void Dispose()
        {
            try
            {
                _connection?.Close();
                _connection?.Dispose();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ClipboardManager.Dispose: {ex.Message}");
            }
        }
    }
}
