using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace WinKit.Common
{
    /// <summary>
    /// 统一配置管理器
    /// </summary>
    public class SettingsManager : IDisposable
    {
        private readonly string _settingsFile;
        private AppSettings _settings;

        public AppSettings Settings => _settings;

        public event EventHandler<AppSettings>? SettingsChanged;

        /// <summary>
        /// 需要 DPAPI 加密落盘的敏感字段名。加密与解密共用同一份清单，
        /// 新增敏感配置时只改这里，避免保存/加载两边不同步。
        /// </summary>
        private static readonly HashSet<string> SensitiveFields = new(StringComparer.Ordinal)
        {
            "OpenAIApiKey",   // OpenAI 兼容接口的 API Key
            "SyncPassword"    // 剪贴板同步服务器密码（HTTP Basic）
        };

        public SettingsManager()
        {
            AppPaths.EnsureDirectories();
            _settingsFile = AppPaths.Settings;
            _settings = LoadSettings();
        }

        /// <summary>
        /// 保存设置。敏感字段（见 <see cref="SensitiveFields"/>）在序列化前用 DPAPI 加密，
        /// 内存中的 _settings 始终保持明文，不影响调用方。
        /// </summary>
        public void SaveSettings(AppSettings newSettings)
        {
            _settings = newSettings;

            try
            {
                var options = new JsonSerializerOptions { WriteIndented = true };
                var json = JsonSerializer.Serialize(_settings, options);

                // 将 JSON 中的明文敏感字段替换为 DPAPI 密文
                using var doc = JsonDocument.Parse(json);
                using var stream = new MemoryStream();
                using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
                {
                    writer.WriteStartObject();
                    foreach (var prop in doc.RootElement.EnumerateObject())
                    {
                        // 敏感字段（API Key / 同步密码）序列化前用 DPAPI 加密落盘
                        if (SensitiveFields.Contains(prop.Name))
                        {
                            var plain = prop.Value.GetString() ?? "";
                            writer.WriteString(prop.Name, SecureStringHelper.Encrypt(plain));
                        }
                        else
                        {
                            // JsonElement.WriteTo 只写「值」、不写「键」。
                            // 漏掉 WritePropertyName 会让 writer 停在「等待属性名」的状态下收到
                            // 一个 StartObject，抛 InvalidOperationException("Cannot write a JSON
                            // value within an object without a property name") —— 表现即「所有设置
                            // 都存不进去」。上面敏感字段那个带 name 的重载没有这个坑。
                            writer.WritePropertyName(prop.Name);
                            prop.Value.WriteTo(writer);
                        }
                    }
                    writer.WriteEndObject();
                }

                // 先写临时文件再替换：中途失败只会留下 .tmp，不会把用户整份配置写成半截 JSON
                var temp = _settingsFile + ".tmp";
                File.WriteAllText(temp, Encoding.UTF8.GetString(stream.ToArray()), Encoding.UTF8);
                File.Move(temp, _settingsFile, overwrite: true);
            }
            catch (Exception ex)
            {
                // 写盘失败不再静默：Debug.WriteLine 在 Release 构建下被编译器整体剔除，
                // 此前出问题时磁盘上没有任何痕迹可查。
                LogSaveError(ex);
            }

            // 变更通知放在 try 之外 —— 配置生效不该取决于磁盘是否写成功。
            // 旧实现把 Invoke 放在 try 内：一旦序列化或写盘抛异常，
            // 「内存里已经改了」与「界面上毫无反应」就同时成立，表现即设置失灵。
            try
            {
                SettingsChanged?.Invoke(this, _settings);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"SettingsManager: 设置变更通知失败 - {ex.Message}");
            }
        }

        /// <summary>
        /// 把保存失败落到日志文件（<c>%APPDATA%/WinKit/settings-error.log</c>）。
        /// 只在异常时写，超过 64KB 自动重开一份，避免长期运行无限堆积。
        /// </summary>
        private static void LogSaveError(Exception ex)
        {
            try
            {
                var path = Path.Combine(AppPaths.AppData, "settings-error.log");
                var info = new FileInfo(path);
                if (info.Exists && info.Length > 64 * 1024) File.Delete(path);

                File.AppendAllText(path,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}{Environment.NewLine}{Environment.NewLine}",
                    Encoding.UTF8);
            }
            catch
            {
                // 记日志本身失败只能放弃，绝不能反过来影响主流程
            }
        }

        /// <summary>
        /// 加载设置。敏感字段从 DPAPI 密文解密回明文；
        /// 若解密失败（旧版明文迁移），保留原值并触发后台重新加密保存。
        /// </summary>
        private AppSettings LoadSettings()
        {
            try
            {
                if (File.Exists(_settingsFile))
                {
                    var json = File.ReadAllText(_settingsFile, Encoding.UTF8);
                    var settings = JsonSerializer.Deserialize<AppSettings>(json);
                    if (settings != null)
                    {
                        // 解密敏感字段（API Key / 同步密码）
                        // 任一字段解不开 → 视为旧版明文，保留原值并后台重新加密保存
                        var needMigration = false;
                        needMigration |= TryDecryptField(settings.OpenAIApiKey, v => settings.OpenAIApiKey = v);
                        needMigration |= TryDecryptField(settings.SyncPassword, v => settings.SyncPassword = v);

                        if (needMigration)
                        {
                            System.Diagnostics.Debug.WriteLine("SettingsManager: 检测到明文敏感字段，将自动加密迁移");
                            _ = Task.Run(() =>
                            {
                                try { SaveSettings(settings); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"SettingsManager: 明文迁移加密失败 - {ex.Message}"); }
                            });
                        }

                        return settings;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"SettingsManager: 加载设置失败 - {ex.Message}");
            }

            return new AppSettings();
        }

        /// <summary>
        /// 尝试把落盘的密文解密为明文，并通过 <paramref name="assign"/> 写回。
        /// 返回 true 表示该值是旧版明文（解不开），需要调用方重新加密落盘。
        /// 空值不处理，返回 false。
        /// </summary>
        private static bool TryDecryptField(string? storedValue, Action<string> assign)
        {
            if (string.IsNullOrEmpty(storedValue)) return false;

            var decrypted = SecureStringHelper.Decrypt(storedValue);
            if (decrypted != null)
            {
                assign(decrypted);
                return false;
            }

            return true;   // 解不开 → 旧版明文，保留原值待重加密
        }

        public void Dispose()
        {
        }
    }
}
