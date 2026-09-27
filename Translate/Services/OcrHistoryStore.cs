using System;
using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.Data.Sqlite;
using WinKit.Common;
using WinKit.Translate.Models;

namespace WinKit.Translate.Services
{
    /// <summary>
    /// OCR 识别历史的持久化。
    ///
    /// 与剪贴板历史共用同一个数据库文件（%APPDATA%/WinKit/clipboard.db），
    /// 只是另起一张表 —— 两处都是「历史记录」，放进同一个库便于统一备份和清理，
    /// 也省掉再开一个数据库连接池。
    ///
    /// 并发说明：SQLite 在 WAL 模式下支持多连接并发读写，而 ClipboardManager 持有
    /// 一条常驻连接。因此这里**另开一条自己的连接**并设置 busy_timeout，
    /// 避免两边互相锁死；本类内部再用一把锁保证自身调用的串行化
    /// （SqliteConnection 不是线程安全的）。
    /// </summary>
    public sealed class OcrHistoryStore : IDisposable
    {
        /// <summary>遇到写锁时最长等待多久（毫秒），与剪贴板那边共用同一个库，留足余量</summary>
        private const int BusyTimeoutMs = 3000;

        private readonly object _lock = new();
        private SqliteConnection? _connection;
        private bool _disposed;

        public OcrHistoryStore()
        {
            // 构造即建库建表：识别历史是低频写入，先建好可以让首次写入不承担建表开销
            EnsureConnection();
        }

        /// <summary>数据表是否可用（建库失败时返回 false，调用方据此静默降级，不影响识别主流程）</summary>
        public bool IsAvailable
        {
            get { lock (_lock) { return EnsureConnection() != null; } }
        }

        // ══════════════════════════════════════════════
        //  连接与建表
        // ══════════════════════════════════════════════

        private SqliteConnection? EnsureConnection()
        {
            if (_disposed) return null;
            if (_connection != null) return _connection;

            try
            {
                AppPaths.EnsureDirectories();

                var conn = new SqliteConnection($"Data Source={AppPaths.Database}");
                conn.Open();

                // WAL 是库级设置，已由 ClipboardManager 设过；这里幂等再设一次以防它尚未初始化。
                // journal_mode 会返回结果行，必须单独执行，不能和别的语句拼在一起。
                using (var pragma = conn.CreateCommand())
                {
                    pragma.CommandText = "PRAGMA journal_mode=WAL";
                    pragma.ExecuteNonQuery();
                }

                using (var timeout = conn.CreateCommand())
                {
                    timeout.CommandText = $"PRAGMA busy_timeout={BusyTimeoutMs}";
                    timeout.ExecuteNonQuery();
                }

                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"
                        CREATE TABLE IF NOT EXISTS ocr_history (
                            id            TEXT PRIMARY KEY,
                            created_at    TEXT    NOT NULL,
                            text          TEXT    NOT NULL,
                            char_count    INTEGER NOT NULL DEFAULT 0,
                            block_count   INTEGER NOT NULL DEFAULT 0,
                            elapsed_ms    INTEGER NOT NULL DEFAULT 0,
                            model         TEXT    NOT NULL DEFAULT '',
                            auto_inverted INTEGER NOT NULL DEFAULT 0
                        );
                        CREATE INDEX IF NOT EXISTS idx_ocr_created ON ocr_history(created_at DESC);
                    ";
                    cmd.ExecuteNonQuery();
                }

                _connection = conn;
                return conn;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"OcrHistoryStore: 初始化失败，识别历史将不可用 - {ex.Message}");
                return null;
            }
        }

        // ══════════════════════════════════════════════
        //  写入
        // ══════════════════════════════════════════════

        /// <summary>写入一条记录，并按 <paramref name="maxItems"/> 淘汰最旧的超出部分</summary>
        public bool Add(OcrHistoryItem item, int maxItems)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.Text)) return false;

            lock (_lock)
            {
                var conn = EnsureConnection();
                if (conn == null) return false;

                try
                {
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = @"
                            INSERT OR REPLACE INTO ocr_history
                                (id, created_at, text, char_count, block_count, elapsed_ms, model, auto_inverted)
                            VALUES
                                ($id, $at, $text, $cc, $bc, $ms, $model, $inv)";
                        cmd.Parameters.AddWithValue("$id", item.Id);
                        // 用 ISO 8601 往返格式存字符串：字典序即时间序，排序不必额外解析
                        cmd.Parameters.AddWithValue("$at", item.CreatedAt.ToString("o"));
                        cmd.Parameters.AddWithValue("$text", item.Text);
                        cmd.Parameters.AddWithValue("$cc", item.CharCount);
                        cmd.Parameters.AddWithValue("$bc", item.BlockCount);
                        cmd.Parameters.AddWithValue("$ms", item.ElapsedMs);
                        cmd.Parameters.AddWithValue("$model", item.Model ?? "");
                        cmd.Parameters.AddWithValue("$inv", item.AutoInverted ? 1 : 0);
                        cmd.ExecuteNonQuery();
                    }

                    TrimToCore(conn, maxItems);
                    return true;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"OcrHistoryStore.Add 失败: {ex.Message}");
                    return false;
                }
            }
        }

        // ══════════════════════════════════════════════
        //  查询
        // ══════════════════════════════════════════════

        /// <summary>按时间倒序取最近若干条</summary>
        public List<OcrHistoryItem> Query(int limit)
        {
            var list = new List<OcrHistoryItem>();
            if (limit <= 0) return list;

            lock (_lock)
            {
                var conn = EnsureConnection();
                if (conn == null) return list;

                try
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = @"
                        SELECT id, created_at, text, char_count, block_count, elapsed_ms, model, auto_inverted
                        FROM ocr_history
                        ORDER BY created_at DESC
                        LIMIT $limit";
                    cmd.Parameters.AddWithValue("$limit", limit);

                    using var reader = cmd.ExecuteReader();
                    while (reader.Read()) list.Add(ReadRow(reader));
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"OcrHistoryStore.Query 失败: {ex.Message}");
                }
            }

            return list;
        }

        /// <summary>当前记录条数</summary>
        public int Count()
        {
            lock (_lock)
            {
                var conn = EnsureConnection();
                if (conn == null) return 0;

                try
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "SELECT COUNT(*) FROM ocr_history";
                    var result = cmd.ExecuteScalar();
                    return result is long n ? (int)n : 0;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"OcrHistoryStore.Count 失败: {ex.Message}");
                    return 0;
                }
            }
        }

        // ══════════════════════════════════════════════
        //  删除
        // ══════════════════════════════════════════════

        /// <summary>删除单条</summary>
        public bool Remove(string id)
        {
            if (string.IsNullOrEmpty(id)) return false;

            lock (_lock)
            {
                var conn = EnsureConnection();
                if (conn == null) return false;

                try
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "DELETE FROM ocr_history WHERE id = $id";
                    cmd.Parameters.AddWithValue("$id", id);
                    return cmd.ExecuteNonQuery() > 0;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"OcrHistoryStore.Remove 失败: {ex.Message}");
                    return false;
                }
            }
        }

        /// <summary>清空全部</summary>
        public bool Clear()
        {
            lock (_lock)
            {
                var conn = EnsureConnection();
                if (conn == null) return false;

                try
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "DELETE FROM ocr_history";
                    cmd.ExecuteNonQuery();
                    return true;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"OcrHistoryStore.Clear 失败: {ex.Message}");
                    return false;
                }
            }
        }

        /// <summary>按上限裁剪（设置里调小上限后立即生效）</summary>
        public void TrimTo(int maxItems)
        {
            lock (_lock)
            {
                var conn = EnsureConnection();
                if (conn == null) return;

                try { TrimToCore(conn, maxItems); }
                catch (Exception ex) { Debug.WriteLine($"OcrHistoryStore.TrimTo 失败: {ex.Message}"); }
            }
        }

        /// <summary>裁剪实现（调用方必须已持锁）</summary>
        private static void TrimToCore(SqliteConnection conn, int maxItems)
        {
            if (maxItems <= 0) return;

            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                DELETE FROM ocr_history
                WHERE id NOT IN (
                    SELECT id FROM ocr_history ORDER BY created_at DESC LIMIT $max
                )";
            cmd.Parameters.AddWithValue("$max", maxItems);
            cmd.ExecuteNonQuery();
        }

        private static OcrHistoryItem ReadRow(SqliteDataReader reader)
        {
            var item = new OcrHistoryItem
            {
                Id = reader.GetString(0),
                CreatedAt = DateTime.TryParse(reader.GetString(1), out var at) ? at : DateTime.Now,
                Text = reader.GetString(2),
                CharCount = reader.GetInt32(3),
                BlockCount = reader.GetInt32(4),
                ElapsedMs = reader.GetInt64(5),
                Model = reader.IsDBNull(6) ? "" : reader.GetString(6),
                AutoInverted = !reader.IsDBNull(7) && reader.GetInt32(7) != 0,
            };

            // 老记录可能没写 char_count，用实际文本长度兜底，界面别显示成 0 字
            if (item.CharCount == 0 && item.Text.Length > 0) item.CharCount = item.Text.Length;

            return item;
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;

                try { _connection?.Close(); }
                catch (Exception ex) { Debug.WriteLine($"OcrHistoryStore.Dispose(close): {ex.Message}"); }

                try { _connection?.Dispose(); }
                catch (Exception ex) { Debug.WriteLine($"OcrHistoryStore.Dispose: {ex.Message}"); }

                _connection = null;
            }
        }
    }
}
