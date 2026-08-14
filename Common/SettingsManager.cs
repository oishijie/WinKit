using System;
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

        public SettingsManager()
        {
            AppPaths.EnsureDirectories();
            _settingsFile = AppPaths.Settings;
            _settings = LoadSettings();
        }

        /// <summary>
        /// 保存设置。敏感字段（API Key）在序列化前用 DPAPI 加密，
        /// 内存中的 _settings 始终保持明文，不影响调用方。
        /// </summary>
        public void SaveSettings(AppSettings newSettings)
        {
            _settings = newSettings;

            try
            {
                var options = new JsonSerializerOptions { WriteIndented = true };
                var json = JsonSerializer.Serialize(_settings, options);

                // 将 JSON 中的明文 API Key 替换为 DPAPI 密文
                using var doc = JsonDocument.Parse(json);
                using var stream = new MemoryStream();
                using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
                {
                    writer.WriteStartObject();
                    foreach (var prop in doc.RootElement.EnumerateObject())
                    {
                        if (prop.Name == "OpenAIApiKey")
                        {
                            var plain = prop.Value.GetString() ?? "";
                            writer.WriteString(prop.Name, SecureStringHelper.Encrypt(plain));
                        }
                        else
                        {
                            prop.Value.WriteTo(writer);
                        }
                    }
                    writer.WriteEndObject();
                }

                File.WriteAllText(_settingsFile, Encoding.UTF8.GetString(stream.ToArray()), Encoding.UTF8);

                SettingsChanged?.Invoke(this, _settings);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"SettingsManager: 保存设置失败 - {ex.Message}");
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
                        // 解密 API Key
                        if (!string.IsNullOrEmpty(settings.OpenAIApiKey))
                        {
                            var decrypted = SecureStringHelper.Decrypt(settings.OpenAIApiKey);
                            if (decrypted != null)
                            {
                                settings.OpenAIApiKey = decrypted;
                            }
                            else
                            {
                                // 解密失败 → 旧版明文，保留值并后台重新加密保存
                                System.Diagnostics.Debug.WriteLine("SettingsManager: API Key 为明文格式，将自动加密迁移");
                                _ = Task.Run(() =>
                                {
                                    try { SaveSettings(settings); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"SettingsManager: 明文迁移加密失败 - {ex.Message}"); }
                                });
                            }
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

        public void Dispose()
        {
        }
    }
}
