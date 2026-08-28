using System;
using DynamicWallpaper.Core;
using DynamicWallpaper.Models;

namespace DynamicWallpaper.Providers
{
    /// <summary>
    /// 网页 / 3D 壁纸提供器（WebProvider）现已编译进主程序集，直接可用，无需外置 DLL。
    /// 保留本类以隔离「是否可用」的判断与实例化，并兜底构造异常。
    /// 远程 m3u8 流由 WebProvider 用内嵌的 hls.min.js 经 WebView2 流式播放（路线 A）。
    /// </summary>
    public static class WebProviderLoader
    {
        // WebProvider 直接位于主程序集，编译期即确定可用
        public static bool Available => true;

        public static IWallpaperProvider? Create()
        {
            try
            {
                return (IWallpaperProvider?)Activator.CreateInstance(typeof(WebProvider));
            }
            catch
            {
                return null;
            }
        }
    }
}
