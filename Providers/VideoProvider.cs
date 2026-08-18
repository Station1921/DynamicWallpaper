using System;
using System.Drawing;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using DynamicWallpaper.Desktop;
using DynamicWallpaper.Models;

namespace DynamicWallpaper.Providers
{
    /// <summary>
    /// 视频壁纸：基于 WPF 内置 MediaElement（系统自带解码，零外部依赖，轻量）。
    /// 如需更广的编解码支持，可替换为 libVLC 实现（见 README）。
    /// </summary>
    public class VideoProvider : IWallpaperProvider
    {
        /// <summary>性能模式：降低视频缩放质量以减少 GPU 占用。</summary>
        public static bool LowQualityScaling { get; set; }

        private RenderWindow? _window;
        private MediaElement? _media;
        private string _path = "";

        public WallpaperType Type => WallpaperType.Video;
        public IntPtr Handle => _window == null ? IntPtr.Zero : new WindowInteropHelper(_window).EnsureHandle();

        public void Show(string path, Rectangle bounds)
        {
            _path = path;
            _window = new RenderWindow();
            _media = new MediaElement
            {
                Source = new Uri(path, UriKind.Absolute),
                LoadedBehavior = MediaState.Play,
                UnloadedBehavior = MediaState.Manual,
                Stretch = Stretch.UniformToFill,
                StretchDirection = StretchDirection.Both,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = System.Windows.VerticalAlignment.Center,
                IsMuted = true,
                Volume = 0
            };
            if (LowQualityScaling)
                RenderOptions.SetBitmapScalingMode(_media, BitmapScalingMode.LowQuality);
            // 循环播放
            _media.MediaEnded += (_, _) =>
            {
                if (_media != null) { _media.Position = TimeSpan.Zero; _media.Play(); }
            };
            _window.RootGrid.Children.Add(_media);
            _window.SetDeviceBounds(bounds);
            _window.Show();
        }

        public void AttachTo(IntPtr workerw, Rectangle bounds)
        {
            WorkerWInjector.Attach(Handle, workerw, bounds);
        }

        public void Play()
        {
            if (_media != null) { _media.Position = TimeSpan.Zero; _media.Play(); }
        }

        public void Pause() => _media?.Pause();

        public void SetMuted(bool muted)
        {
            if (_media == null) return;
            _media.IsMuted = muted;
            _media.Volume = muted ? 0 : 1;
        }

        public void Dispose()
        {
            try { _media?.Stop(); } catch { }
            _window?.Close();
            _window = null;
        }
    }
}
