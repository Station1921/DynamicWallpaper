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

        /// <summary>壁纸适应方式：fill=铺满裁剪 / fit=完整显示 / center=原始居中。由 WallpaperManager 在切换时注入。</summary>
        public static string FitMode { get; set; } = "fill";

        /// <summary>壁纸旋转角度（实例属性，按壁纸路径独立）：0=不旋转，90=顺时针90°，180=180°，270=逆时针90°。由 WallpaperManager 在创建时按路径注入。</summary>
        public int Rotation { get; set; } = 0;

        private Rectangle _bounds;

        /// <summary>把 FitMode 映射为 WPF Image Stretch / StretchDirection。</summary>
        private static (Stretch Stretch, StretchDirection Direction) BuildImageStretch()
        {
            return FitMode switch
            {
                "fit" => (Stretch.Uniform, StretchDirection.DownOnly),
                "center" => (Stretch.None, StretchDirection.Both),
                _ => (Stretch.UniformToFill, StretchDirection.Both)
            };
        }

        public void Show(string path, Rectangle bounds)
        {
            _path = path;
            _bounds = bounds;
            _window = new RenderWindow();
            int maxPixel = Math.Max(bounds.Width, bounds.Height);
            var (stretch, direction) = BuildImageStretch();
            _image = new Image
            {
                Stretch = stretch,
                StretchDirection = direction,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = System.Windows.VerticalAlignment.Center,
                Source = LoadImage(path, maxPixel)
            };
            ApplyRotationLayout(_image, bounds);
            _window.RootGrid.Children.Add(_image);
            _window.SetDeviceBounds(bounds);
            // 不在这里 Show，避免 WorkerW 获取失败时窗口在顶层闪现；
            // 窗口会在 AttachTo 成功挂接到 WorkerW 后再显示。
        }

        public void AttachTo(IntPtr workerw, Rectangle bounds)
        {
            WorkerWInjector.Attach(Handle, workerw, bounds);
            _window?.Show(); // 成功挂接到桌面壁纸层后再显示
        }

        /// <summary>运行时切换适应方式：立即更新已渲染图片的 Stretch（须在 UI 线程调用）。</summary>
        public void ApplyFitMode()
        {
            if (_image == null) return;
            var (stretch, direction) = BuildImageStretch();
            _image.Stretch = stretch;
            _image.StretchDirection = direction;
        }

        /// <summary>按旋转角度布局：90°/270° 时先按宽高互换的尺寸布局，再由 LayoutTransform 旋转铺满屏幕；180° 仅旋转不互换尺寸。</summary>
        private void ApplyRotationLayout(Image img, Rectangle bounds)
        {
            var r = ((Rotation % 360) + 360) % 360;
            if (r == 90 || r == 270)
            {
                img.Width = bounds.Height;   // 旋转后宽高互换，先按互换尺寸布局
                img.Height = bounds.Width;
                img.LayoutTransform = new RotateTransform(r);
            }
            else if (r == 180)
            {
                img.Width = bounds.Width;    // 180° 仅旋转不互换尺寸，仍按屏幕尺寸铺满
                img.Height = bounds.Height;
                img.LayoutTransform = new RotateTransform(180);
            }
            else
            {
                // 0° 不旋转：必须按屏幕尺寸铺满（不能留 NaN 自然尺寸），否则从 90/270 设回不旋转时
                // 图片会缩回自然尺寸、不再铺满屏幕，表现为"偏移出屏幕"。
                img.Width = bounds.Width;
                img.Height = bounds.Height;
                img.LayoutTransform = null;
            }
        }

        /// <summary>运行时切换旋转：立即更新已渲染图片的旋转布局（须在 UI 线程调用）。</summary>
        public void ApplyRotation()
        {
            if (_image == null || _window == null) return;
            ApplyRotationLayout(_image, _bounds);
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
