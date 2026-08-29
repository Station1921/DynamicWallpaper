using System;
using System.IO;
using System.Linq;

namespace DynamicWallpaper.Core
{
    /// <summary>
    /// 本地缓存管理：仅管理「可安全删除、删除后自动重建」的缓存目录，
    /// 不影响用户实际下载的壁纸（Wallpapers/&lt;来源&gt;/ 下的 mp4/gif 等）。
    ///
    /// 缓存目录：
    ///   - Wallpapers/thumbs     在线壁纸列表缩略图缓存（OnlineWallpaperCrawler.DownloadThumbAsync 写入）
    ///   - Wallpapers/hovercache 悬停预览视频兜底缓存（MediaHoverImage.DownloadToCacheAsync 写入）
    /// </summary>
    public static class CacheManager
    {
        /// <summary>所有缓存根目录（相对 exe 根目录）。</summary>
        private static string[] CacheDirs =>
            new[]
            {
                Path.Combine(AppPaths.RootDirectory, "Wallpapers", "thumbs"),
                Path.Combine(AppPaths.RootDirectory, "Wallpapers", "hovercache")
            };

        /// <summary>统计缓存占用：返回 (文件数, 总字节数)。目录不存在时返回 (0,0)。</summary>
        public static (int Count, long Bytes) GetStats()
        {
            int count = 0;
            long bytes = 0;
            foreach (var dir in CacheDirs)
            {
                if (!Directory.Exists(dir)) continue;
                try
                {
                    foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                    {
                        try
                        {
                            var info = new FileInfo(file);
                            if (info.Exists)
                            {
                                count++;
                                bytes += info.Length;
                            }
                        }
                        catch { /* 跳过无法访问的文件 */ }
                    }
                }
                catch { /* 跳过无法枚举的目录 */ }
            }
            return (count, bytes);
        }

        /// <summary>清除全部缓存文件（保留目录结构），返回 (删除文件数, 释放字节数)。
        /// 单个文件被占用（如正在显示的缩略图）时跳过，不中断整体清理。</summary>
        public static (int Count, long Bytes) ClearAll()
        {
            return ClearWhere(_ => true);
        }

        /// <summary>清除早于指定保留时长的缓存文件（用于周期性自动清理）。
        /// 以文件最后写入时间为准（UTC）。返回 (删除文件数, 释放字节数)。</summary>
        public static (int Count, long Bytes) ClearOlderThan(TimeSpan maxAge)
        {
            var cutoff = DateTime.UtcNow - maxAge;
            return ClearWhere(info => info.LastWriteTimeUtc < cutoff);
        }

        private static (int Count, long Bytes) ClearWhere(Func<FileInfo, bool> predicate)
        {
            int count = 0;
            long bytes = 0;
            foreach (var dir in CacheDirs)
            {
                if (!Directory.Exists(dir)) continue;
                try
                {
                    foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                    {
                        try
                        {
                            var info = new FileInfo(file);
                            if (!info.Exists || !predicate(info)) continue;
                            long size = info.Length;
                            info.Delete();
                            count++;
                            bytes += size;
                        }
                        catch { /* 文件被占用/无权限：跳过，不中断整体清理 */ }
                    }
                }
                catch { /* 跳过无法枚举的目录 */ }
            }
            return (count, bytes);
        }

        /// <summary>
        /// 按配置执行周期性自动清理：仅当 Config.AutoCleanCache=true 时，
        /// 删除超过 CacheRetentionDays 天的缓存文件。无操作或异常均安全返回。
        /// </summary>
        public static void RunScheduledCleanup()
        {
            try
            {
                var cfg = Config.Load();
                if (!cfg.AutoCleanCache) return;
                int days = Math.Max(1, cfg.CacheRetentionDays);
                var (count, bytes) = ClearOlderThan(TimeSpan.FromDays(days));
                if (count > 0)
                    Logger.Log($"[Cache] 自动清理 {days} 天前缓存：删除 {count} 个文件，释放 {FormatSize(bytes)}");
                else
                    Logger.Log($"[Cache] 自动清理检查完成：无超过 {days} 天的缓存文件");
            }
            catch (Exception ex)
            {
                Logger.Log($"[Cache] 自动清理失败: {ex.Message}");
            }
        }

        /// <summary>把字节数格式化为带单位的可读字符串。</summary>
        public static string FormatSize(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
            return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
        }
    }
}
