using System;
using System.IO;
using System.Reflection;
using DynamicWallpaper.Models;

namespace DynamicWallpaper.Providers
{
    /// <summary>
    /// 延迟加载可选网页壁纸模块（DynamicWallpaper.Web.dll）。
    /// 该模块依赖 WebView2（需联网还原 NuGet 包），故与主程序解耦：
    /// 缺失时主程序仍可正常编译与运行，仅网页壁纸功能不可用。
    /// </summary>
    public static class WebProviderLoader
    {
        private static readonly Type? _type;

        static WebProviderLoader()
        {
            try
            {
                string dll = Path.Combine(AppContext.BaseDirectory, "DynamicWallpaper.Web.dll");
                if (File.Exists(dll))
                {
                    var asm = Assembly.LoadFrom(dll);
                    _type = asm.GetType("DynamicWallpaper.Providers.WebProvider");
                }
            }
            catch
            {
                _type = null;
            }
        }

        public static bool Available => _type != null;

        public static IWallpaperProvider? Create()
        {
            if (_type == null) return null;
            try
            {
                return (IWallpaperProvider?)Activator.CreateInstance(_type);
            }
            catch
            {
                return null;
            }
        }
    }
}
