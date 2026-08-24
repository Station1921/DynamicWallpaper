using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace DynamicWallpaper.UI
{
    /// <summary>
    /// 支持鼠标悬停时播放 GIF，移开时静止在第一帧的 Image 控件。
    /// 可额外指定 HoverSourceUri：列表缩略图为静态小图时，悬停再异步加载高清动画 GIF。
    /// 不依赖第三方库，仅使用 .NET 内置的 GifBitmapDecoder + DispatcherTimer。
    /// </summary>
    public class GifHoverImage : System.Windows.Controls.Image
    {
        private static readonly HttpClient s_client = new();

        private GifBitmapDecoder? _decoder;
        private DispatcherTimer? _timer;
        private int _frameIndex;
        private BitmapSource[]? _frames;
        private bool _isHovering;
        private string? _hoverUri;
        private CancellationTokenSource? _hoverCts;

        static GifHoverImage()
        {
            s_client.DefaultRequestHeaders.Add("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/115.0.0.0 Safari/537.36");
            DefaultStyleKeyProperty.OverrideMetadata(typeof(GifHoverImage), new FrameworkPropertyMetadata(typeof(System.Windows.Controls.Image)));
        }

        public static readonly DependencyProperty SourceUriProperty =
            DependencyProperty.Register(nameof(SourceUri), typeof(string), typeof(GifHoverImage),
                new PropertyMetadata(null, OnSourceUriChanged));

        public string? SourceUri
        {
            get => (string?)GetValue(SourceUriProperty);
            set => SetValue(SourceUriProperty, value);
        }

        public static readonly DependencyProperty HoverSourceUriProperty =
            DependencyProperty.Register(nameof(HoverSourceUri), typeof(string), typeof(GifHoverImage),
                new PropertyMetadata(null, OnHoverSourceUriChanged));

        public string? HoverSourceUri
        {
            get => (string?)GetValue(HoverSourceUriProperty);
            set => SetValue(HoverSourceUriProperty, value);
        }

        public GifHoverImage()
        {
            Unloaded += OnUnloaded;
            MouseEnter += OnMouseEnter;
            MouseLeave += OnMouseLeave;
        }

        private static async void OnSourceUriChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is GifHoverImage img)
                await img.LoadAsync(e.NewValue as string);
        }

        private static void OnHoverSourceUriChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is GifHoverImage img)
                img._hoverUri = e.NewValue as string;
        }

        private async Task LoadAsync(string? uri)
        {
            StopAnimation();
            _decoder = null;
            _frames = null;
            _frameIndex = 0;

            if (string.IsNullOrWhiteSpace(uri))
            {
                Source = null;
                return;
            }

            try
            {
                var data = await DownloadBytesAsync(uri, CancellationToken.None);
                if (data == null || data.Length == 0)
                {
                    FallbackToStatic(uri);
                    return;
                }

                using var stream = new MemoryStream(data);
                _decoder = new GifBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);

                if (_decoder.Frames.Count == 0)
                {
                    FallbackToStatic(uri);
                    return;
                }

                _frames = new BitmapSource[_decoder.Frames.Count];
                for (int i = 0; i < _decoder.Frames.Count; i++)
                {
                    var frame = _decoder.Frames[i];
                    if (!frame.IsFrozen) frame.Freeze();
                    _frames[i] = frame;
                }

                Source = _frames[0];

                if (_isHovering)
                    StartAnimation();
            }
            catch
            {
                FallbackToStatic(uri);
            }
        }

        /// <summary>悬停时异步加载高清动画 GIF；失败时保留原有静态缩略图。</summary>
        private async Task LoadHoverAsync(string? uri)
        {
            if (string.IsNullOrWhiteSpace(uri)) return;

            _hoverCts?.Cancel();
            _hoverCts = new CancellationTokenSource();
            var ct = _hoverCts.Token;

            try
            {
                var data = await DownloadBytesAsync(uri, ct);
                if (data == null || data.Length == 0 || ct.IsCancellationRequested) return;

                using var stream = new MemoryStream(data);
                var decoder = new GifBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                if (decoder.Frames.Count <= 1) return; // 不是动画，保留静态缩略图

                var frames = new BitmapSource[decoder.Frames.Count];
                for (int i = 0; i < decoder.Frames.Count; i++)
                {
                    var frame = decoder.Frames[i];
                    if (!frame.IsFrozen) frame.Freeze();
                    frames[i] = frame;
                }

                if (ct.IsCancellationRequested) return;

                StopAnimation();
                _decoder = decoder;
                _frames = frames;
                _frameIndex = 0;
                Source = _frames[0];

                if (_isHovering)
                    StartAnimation();
            }
            catch (OperationCanceledException) { }
            catch { /* 悬停加载失败时静默保留静态缩略图 */ }
        }

        private async Task<byte[]?> DownloadBytesAsync(string uri, CancellationToken ct)
        {
            if (File.Exists(uri))
                return await File.ReadAllBytesAsync(uri, ct);

            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            if (uri.Contains("zhutix", StringComparison.OrdinalIgnoreCase))
                request.Headers.Referrer = new Uri("https://zhutix.com/");
            if (uri.Contains("netbian.com", StringComparison.OrdinalIgnoreCase))
                request.Headers.Referrer = new Uri("https://www.netbian.com/");

            var response = await s_client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsByteArrayAsync(ct);
        }

        private void FallbackToStatic(string uri)
        {
            try
            {
                // 无法解析为 GIF 时回退为普通静态图
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(uri);
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                if (!bmp.IsFrozen) bmp.Freeze();
                Source = bmp;
            }
            catch
            {
                Source = null;
            }
        }

        private void OnMouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
        {
            _isHovering = true;
            if (_frames != null && _frames.Length > 1)
                StartAnimation();
            else if (!string.IsNullOrWhiteSpace(_hoverUri))
                _ = LoadHoverAsync(_hoverUri);
        }

        private void OnMouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
        {
            _isHovering = false;
            _hoverCts?.Cancel();
            StopAnimation();
        }

        private void StartAnimation()
        {
            if (_frames == null || _frames.Length <= 1) return;
            if (_timer != null) return;

            _frameIndex = 0;
            _timer = new DispatcherTimer(DispatcherPriority.Render);
            _timer.Interval = GetFrameDelay(0);
            _timer.Tick += OnTimerTick;
            _timer.Start();
        }

        private void StopAnimation()
        {
            _timer?.Stop();
            _timer = null;
            _frameIndex = 0;
            if (_frames != null && _frames.Length > 0)
                Source = _frames[0];
        }

        private void OnTimerTick(object? sender, EventArgs e)
        {
            if (_frames == null || _frames.Length == 0) return;

            _frameIndex = (_frameIndex + 1) % _frames.Length;
            Source = _frames[_frameIndex];

            if (_timer != null)
                _timer.Interval = GetFrameDelay(_frameIndex);
        }

        private TimeSpan GetFrameDelay(int index)
        {
            if (_decoder == null || _frames == null) return TimeSpan.FromMilliseconds(100);
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

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            _hoverCts?.Cancel();
            StopAnimation();
            _decoder = null;
            _frames = null;
        }
    }
}
