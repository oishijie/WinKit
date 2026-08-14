using System;
using System.Security.Cryptography;
using System.Text;

namespace WinKit.Common
{
    /// <summary>
    /// 基于 Windows DPAPI 的敏感字符串加密/解密工具。
    /// 加密绑定到当前 Windows 用户，同机同用户才能解密，
    /// 无需管理密钥，适合本地桌面应用存储 API Key 等凭据。
    /// </summary>
    internal static class SecureStringHelper
    {
        /// <summary>
        /// 加密明文字符串，返回 Base64 密文。
        /// 空字符串返回空字符串（不加密空值）。
        /// </summary>
        public static string Encrypt(string plainText)
        {
            if (string.IsNullOrEmpty(plainText)) return plainText;

            try
            {
                var plainBytes = Encoding.UTF8.GetBytes(plainText);
                var encryptedBytes = ProtectedData.Protect(plainBytes, null, DataProtectionScope.CurrentUser);
                return Convert.ToBase64String(encryptedBytes);
            }
            catch (PlatformNotSupportedException)
            {
                // 非 Windows 平台不支持 DPAPI，降级为 Base64（仅防误读，非真正加密）
                return "PLAIN:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(plainText));
            }
        }

        /// <summary>
        /// 解密由 <see cref="Encrypt"/> 产生的密文。
        /// 若解密失败（如数据已损坏或非 DPAPI 格式），返回 null。
        /// </summary>
        public static string? Decrypt(string encryptedText)
        {
            if (string.IsNullOrEmpty(encryptedText)) return encryptedText;

            try
            {
                // 兼容非 Windows 降级格式
                if (encryptedText.StartsWith("PLAIN:"))
                {
                    var bytes = Convert.FromBase64String(encryptedText.Substring(6));
                    return Encoding.UTF8.GetString(bytes);
                }

                var encryptedBytes = Convert.FromBase64String(encryptedText);
                var plainBytes = ProtectedData.Unprotect(encryptedBytes, null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(plainBytes);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 判断字符串是否已经是加密格式（Base64 或 PLAIN: 前缀）。
        /// 用于迁移检测：如果 Load 出来的值不是加密格式，说明是旧版明文，需要加密后重新保存。
        /// </summary>
        public static bool IsEncrypted(string value)
        {
            if (string.IsNullOrEmpty(value)) return true; // 空值无需加密
            if (value.StartsWith("PLAIN:")) return true;
            // DPAPI 加密后是 Base64，尝试解码看是否合法
            try
            {
                var bytes = Convert.FromBase64String(value);
                // Base64 合法不代表一定是 DPAPI 密文，但至少不是明文 API Key 的典型格式
                // API Key 通常以 "sk-" 等前缀开头，不会是纯 Base64
                return bytes.Length > 0;
            }
            catch
            {
                return false; // 非 Base64 → 一定是明文
            }
        }
    }
}
