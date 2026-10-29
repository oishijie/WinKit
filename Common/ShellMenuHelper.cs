using System;
using Microsoft.Win32;

namespace WinKit.Common
{
    /// <summary>
    /// 资源管理器右键「同步到手机（WinKit）」菜单安装器。
    /// 纯 HKCU 注册表实现，无需管理员权限，零 shell DLL。
    /// command 形如："C:\...\WinKit.exe" /clipsend "%1" —— 点击后由 WinKit
    /// 把文件复制进剪贴板并立即推送（多选时 shell 会对每个文件各起一次进程，路径在主实例合并）。
    /// </summary>
    public static class ShellMenuHelper
    {
        private const string MenuKeyPath = @"Software\Classes\*\shell\WinKitSync";
        private const string MenuText = "同步到手机（WinKit）";
        private const string ArgPrefix = "/clipsend";

        /// <summary>安装 / 刷新菜单（写入 exe 当前路径；exe 挪位置后重装一次即可）</summary>
        public static void Install(string exePath)
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(MenuKeyPath);
                if (key == null) return;
                key.SetValue("MUIVerb", MenuText);
                key.SetValue("Icon", exePath);
                using var command = key.CreateSubKey("command");
                command?.SetValue(null, $"\"{exePath}\" {ArgPrefix} \"%1\"");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ShellMenuHelper.Install: {ex.Message}");
            }
        }

        /// <summary>卸载菜单（不存在时静默）</summary>
        public static void Uninstall()
        {
            try
            {
                Registry.CurrentUser.DeleteSubKeyTree(MenuKeyPath, throwOnMissingSubKey: false);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ShellMenuHelper.Uninstall: {ex.Message}");
            }
        }

        /// <summary>是否已安装（仅检查键存在，不校验路径是否最新）</summary>
        public static bool IsInstalled()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(MenuKeyPath);
                return key != null;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>clipsend 临时文件的路径：第二实例把文件路径追加到这里，主实例读取后删除</summary>
        public static string ClipSendFilePath => System.IO.Path.Combine(AppPaths.AppData, "clipsend.txt");
    }
}
