using System;
using System.IO;

namespace DynamicWallpaper.Core
{
    /// <summary>
    /// 极简日志：写入 %LOCALAPPDATA%\DynamicWallpaper\app.log，
    /// 便于排查下载失败、注入异常等问题。失败不影响主流程。
    /// </summary>
    internal static class Logger
    {
        private static readonly string LogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DynamicWallpaper", "app.log");

        public static string LogFilePath => LogPath;

        public static void Log(string message)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}";
                File.AppendAllText(LogPath, line + Environment.NewLine);
            }
            catch { /* ignore */ }
        }

        public static void Log(string context, Exception ex)
        {
            Log($"[{context}] {ex.GetType().Name}: {ex.Message}");
            if (ex.InnerException != null)
                Log($"[{context}] Inner: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
        }

        public static void Clear()
        {
            try { if (File.Exists(LogPath)) File.WriteAllText(LogPath, ""); } catch { /* ignore */ }
        }
    }
}
