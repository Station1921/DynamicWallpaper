using System;
using System.IO;

namespace DynamicWallpaper.Core
{
    /// <summary>
    /// 极简日志：写入程序根目录 app.log（exe 所在目录），
    /// 便于排查下载失败、注入异常等问题。失败不影响主流程。
    /// </summary>
    internal static class Logger
    {
        private static readonly string LogPath = Path.Combine(AppPaths.RootDirectory, "app.log");

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
            // 记录完整异常（含堆栈），便于定位白屏等 UI 线程异常的真实来源
            Log($"[{context}] {ex}");
            if (ex.InnerException != null)
                Log($"[{context}] Inner: {ex.InnerException}");
        }

        public static void Clear()
        {
            try { if (File.Exists(LogPath)) File.WriteAllText(LogPath, ""); } catch { /* ignore */ }
        }
    }
}
