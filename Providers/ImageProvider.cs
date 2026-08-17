using System;
using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DynamicWallpaper.Desktop;
using System.Windows.Interop;
using Image = System.Windows.Controls.Image;
using DynamicWallpaper.Models;

namespace DynamicWallpaper.Providers
{
    /// <summary>
    /// 图片壁纸：直接铺满屏幕，静态展示（可作为后续幻灯片/动态图的基础）。
    /// </summary>
    public class ImageProvider : IWallpaperProvider
    {
        private RenderWindow? _window;
        private Image? _image;
        private string _path = "";

        public WallpaperType Type => WallpaperType.Image;
        public IntPtr Handle => _window == null ? IntPtr.Zero : new WindowInteropHelper(_window).EnsureHandle();

        public void Show(string path, Rectangle bounds)
        {
            _path = path;
            _window = new RenderWindow();
            int maxPixel = Math.Max(bounds.Width, bounds.Height);
            _image = new Image
            {
                Stretch = Stretch.UniformToFill,
                Source = LoadImage(path, maxPixel)
            };
            _window.RootGrid.Children.Add(_image);
            _window.Left = bounds.Left;
            _window.Top = bounds.Top;
            _window.Width = bounds.Width;
            _window.Height = bounds.Height;
            _window.Show();
        }

        public void AttachTo(IntPtr workerw, Rectangle bounds)
        {
            WorkerWInjector.Attach(Handle, workerw, bounds);
        }

        public void Play() { }
        public void Pause() { }
        public void SetMuted(bool muted) { }

        private static ImageSource? LoadImage(string path, int maxPixel)
        {
            try
            {
                if (!File.Exists(path)) return null;
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(path, UriKind.Absolute);
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                // 按屏幕长边解码，避免 4K/8K 大图占用过多显存
                if (maxPixel > 0) bmp.DecodePixelWidth = maxPixel;
                bmp.EndInit();
                return bmp;
            }
            catch { return null; }
        }

        public void Dispose()
        {
            _window?.Close();
            _window = null;
        }
    }
}
