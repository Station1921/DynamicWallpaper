using System;
using System.Drawing;
using DynamicWallpaper.Desktop;
using DynamicWallpaper.Models;
using Microsoft.Web.WebView2.Wpf;

namespace DynamicWallpaper.Providers
{
    /// <summary>
    /// 网页 / 3D 壁纸：基于 WebView2（Chromium），支持 HTML/CSS/Canvas/WebGL 动画。
    /// 通过反射由主程序延迟加载；本程序集需与 DynamicWallpaper.exe 放在同一目录，
    /// 且目标机已安装 WebView2 运行环境（Win10/11 通常自带）。
    /// </summary>
    public class WebProvider : IWallpaperProvider
    {
        private RenderWindow? _window;
        private WebView2? _web;
        private string _path = "";

        public WallpaperType Type => WallpaperType.Web;
        public IntPtr Handle => _window == null ? IntPtr.Zero : new WindowInteropHelper(_window).EnsureHandle();

        public void Show(string path, Rectangle bounds)
        {
            _path = path;
            _window = new RenderWindow();
            _web = new WebView2 { Margin = new System.Windows.Thickness(0) };
            _window.RootGrid.Children.Add(_web);
            _window.Left = bounds.Left;
            _window.Top = bounds.Top;
            _window.Width = bounds.Width;
            _window.Height = bounds.Height;
            _window.Show();

            _web.Source = ToUri(path);
        }

        private static Uri ToUri(string path)
        {
            if (Uri.TryCreate(path, UriKind.Absolute, out var u) &&
                (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps))
            {
                return u;
            }
            // 本地 html 文件：WebView2 要求 file:/// 形式
            return new Uri("file:///" + path.Replace('\\', '/'));
        }

        public void AttachTo(IntPtr workerw, Rectangle bounds) =>
            WorkerWInjector.Attach(Handle, workerw, bounds);

        public void Play() { }
        public void Pause() { }
        public void SetMuted(bool muted) { }

        public void Dispose()
        {
            try { _web?.Dispose(); } catch { }
            _window?.Close();
            _window = null;
        }
    }
}
