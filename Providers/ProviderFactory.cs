using System;
using System.Collections.Generic;
using System.IO;
using DynamicWallpaper.Models;

namespace DynamicWallpaper.Providers
{
    public static class ProviderFactory
    {
        public static IWallpaperProvider Create(WallpaperType type) =>
            type switch
            {
                WallpaperType.Video => new VideoProvider(),
                WallpaperType.Gif => new GifProvider(),
                _ => new ImageProvider()
            };

        public static WallpaperType DetectType(string path)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (WebExtensions.Contains(ext)) return WallpaperType.Web;
            // GIF 使用专门的 GifProvider 自绘循环，避免 MediaElement 对 GIF 循环不稳定。
            if (ext == ".gif") return WallpaperType.Gif;
            return VideoExtensions.Contains(ext) ? WallpaperType.Video : WallpaperType.Image;
        }

        private static readonly HashSet<string> VideoExtensions = new()
        {
            ".mp4", ".webm", ".mkv", ".avi", ".mov", ".wmv",
            ".m4v", ".mpg", ".mpeg", ".flv", ".ts"
        };

        private static readonly HashSet<string> WebExtensions = new()
        {
            ".html", ".htm", ".xhtml"
        };
    }
}
