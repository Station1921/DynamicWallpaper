using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using DynamicWallpaper.Core;

namespace DynamicWallpaper.Providers
{
    /// <summary>在线壁纸下载记录（持久化到本地 JSON）。</summary>
    public class DownloadHistory
    {
        private static readonly string FilePath =
            Path.Combine(OnlineWallpaperCrawler.OnlineSaveDirectory, "downloads.json");

        private static readonly object _lock = new();

        public List<DownloadRecord> Records { get; set; } = new();

        public static DownloadHistory Load()
        {
            lock (_lock)
            {
                try
                {
                    if (File.Exists(FilePath))
                    {
                        var json = File.ReadAllText(FilePath);
                        var hist = JsonSerializer.Deserialize<DownloadHistory>(json);
                        if (hist != null) return hist;
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"[下载记录] 读取失败: {ex.Message}");
                }
                return new DownloadHistory();
            }
        }

        public void Save()
        {
            lock (_lock)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                    File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
                }
                catch (Exception ex)
                {
                    Logger.Log($"[下载记录] 保存失败: {ex.Message}");
                }
            }
        }

        public DownloadRecord? Find(string detailUrl)
        {
            if (string.IsNullOrWhiteSpace(detailUrl)) return null;
            return Records.FirstOrDefault(r =>
                !string.IsNullOrEmpty(r.DetailUrl) &&
                r.DetailUrl.Equals(detailUrl, StringComparison.OrdinalIgnoreCase));
        }

        public string? TryGetExistingPath(string detailUrl)
        {
            var rec = Find(detailUrl);
            if (rec == null) return null;
            if (File.Exists(rec.LocalPath)) return rec.LocalPath;
            return null;
        }

        public void Upsert(string detailUrl, string localPath, string title, string source)
        {
            if (string.IsNullOrWhiteSpace(detailUrl)) return;

            var rec = Find(detailUrl);
            if (rec == null)
            {
                rec = new DownloadRecord { DetailUrl = detailUrl };
                Records.Add(rec);
            }
            rec.LocalPath = localPath;
            rec.Title = title;
            rec.Source = source;
            rec.DownloadedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            Save();
        }

        /// <summary>清理记录中文件已不存在的条目（可选，启动时调用）。</summary>
        public void PruneMissingFiles()
        {
            Records.RemoveAll(r => !File.Exists(r.LocalPath));
            Save();
        }
    }

    public class DownloadRecord
    {
        public string DetailUrl { get; set; } = "";
        public string LocalPath { get; set; } = "";
        public string Title { get; set; } = "";
        public string Source { get; set; } = "";
        public long DownloadedAt { get; set; }
    }
}
