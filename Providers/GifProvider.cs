using System;
using System.IO;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DynamicWallpaper.Core;
using DynamicWallpaper.Desktop;
using DynamicWallpaper.Models;

using DrawingRectangle = System.Drawing.Rectangle;
using Image = System.Windows.Controls.Image;

namespace DynamicWallpaper.Providers
{
    /// <summary>
    /// GIF 动画壁纸：基于 GifBitmapDecoder + Image + DispatcherTimer 自绘循环播放。
    /// 避免 MediaElement 对 GIF 支持不稳定、只播放一次不循环的问题。
    /// </summary>
    public class GifProvider : IWallpaperProvider
    {
        private RenderWindow? _window;
        private Image? _image;
        private GifBitmapDecoder? _decoder;
        private BitmapSource[]? _frames;
        private TimeSpan[]? _delays;
        private DispatcherTimer? _timer;
        private int _frameIndex;

        public WallpaperType Type => WallpaperType.Gif;
        public IntPtr Handle => _window == null ? IntPtr.Zero : new WindowInteropHelper(_window).EnsureHandle();

        public void Show(string path, DrawingRectangle bounds)
        {
            _window = new RenderWindow();
            _image = new Image
            {
                Stretch = Stretch.UniformToFill,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = System.Windows.VerticalAlignment.Center
            };
            _window.RootGrid.Children.Add(_image);
            _window.Left = bounds.Left;
            _window.Top = bounds.Top;
            _window.Width = bounds.Width;
            _window.Height = bounds.Height;
            _window.Show();

            LoadGif(path);
        }

        private void LoadGif(string path)
        {
            try
            {
                using var stream = File.OpenRead(path);
                _decoder = new GifBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                if (_decoder.Frames.Count == 0) return;

                _frames = new BitmapSource[_decoder.Frames.Count];
                _delays = new TimeSpan[_decoder.Frames.Count];
                for (int i = 0; i < _decoder.Frames.Count; i++)
                {
                    var frame = _decoder.Frames[i];
                    if (!frame.IsFrozen) frame.Freeze();
                    _frames[i] = frame;
                    _delays[i] = GetFrameDelay(i);
                }

                if (_image != null && _frames.Length > 0)
                    _image.Source = _frames[0];
            }
            catch (Exception ex)
            {
                Logger.Log($"[GifProvider] 加载 GIF 失败: {ex.Message}");
            }
        }

        private TimeSpan GetFrameDelay(int index)
        {
            if (_decoder == null) return TimeSpan.FromMilliseconds(100);
            try
            {
                if (_decoder.Frames[index].Metadata is BitmapMetadata meta)
                {
                    var delay = meta.GetQuery("/grctlext/Delay");
                    if (delay is ushort d && d > 0)
                        return TimeSpan.FromMilliseconds(d * 10);
                }
            }
            catch { }
            return TimeSpan.FromMilliseconds(100);
        }

        public void AttachTo(IntPtr workerw, DrawingRectangle bounds)
        {
            WorkerWInjector.Attach(Handle, workerw, bounds);
        }

        public void Play()
        {
            if (_frames == null || _frames.Length <= 1) return;
            StopTimer();
            _frameIndex = 0;
            _timer = new DispatcherTimer(DispatcherPriority.Render);
            _timer.Interval = _delays![0];
            _timer.Tick += OnTick;
            _timer.Start();
        }

        public void Pause()
        {
            StopTimer();
        }

        private void StopTimer()
        {
            _timer?.Stop();
            _timer = null;
        }

        private void OnTick(object? sender, EventArgs e)
        {
            if (_frames == null || _frames.Length == 0 || _image == null) return;
            _frameIndex = (_frameIndex + 1) % _frames.Length;
            _image.Source = _frames[_frameIndex];
            if (_timer != null)
                _timer.Interval = _delays![_frameIndex];
        }

        public void SetMuted(bool muted)
        {
            // GIF 无声音
        }

        public void Dispose()
        {
            StopTimer();
            _decoder = null;
            _frames = null;
            _delays = null;
            _window?.Close();
            _window = null;
        }
    }
}
