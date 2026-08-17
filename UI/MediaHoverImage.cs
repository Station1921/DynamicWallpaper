using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Authentication;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DynamicWallpaper.Core;

using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Image = System.Windows.Controls.Image;

namespace DynamicWallpaper.UI
{
    /// <summary>
    /// 支持鼠标悬停时播放视频（MP4/WebM），移开时停止并显示封面图。
    /// 用于 3gbizhi 动态视频壁纸列表的悬停预览。
    /// 改进：封面保留在最上层直到视频真正准备好，避免黑屏/闪屏；
    ///       并加入短延迟，防止鼠标快速掠过时频繁启停。
    /// 健壮性：封面支持本地文件与远程地址两种来源——远程封面会先下载到内存流再解码，
    ///         规避 WPF 直接加载远程 webp 时卡在 1×1 导致灰底的问题；
    ///         悬停视频若远程打开失败，则回退到本地缓存文件，保证动效始终可用。
    /// </summary>
    public class MediaHoverImage : Grid
    {
        private static readonly HttpClient Http;

        static MediaHoverImage()
        {
            var handler = new HttpClientHandler
            {
                AllowAutoRedirect = true,
                MaxAutomaticRedirections = 10,
                AutomaticDecompression = DecompressionMethods.All,
                SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                ServerCertificateCustomValidationCallback = (msg, cert, chain, err) => true
            };
            Http = new HttpClient(handler);
            Http.DefaultRequestHeaders.Add("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/115.0.0.0 Safari/537.36");
            Http.DefaultRequestHeaders.Add("Accept", "image/webp,image/avif,image/*,*/*;q=0.8");
            Http.DefaultRequestHeaders.Add("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8");
            Http.Timeout = TimeSpan.FromSeconds(30);
        }

        private readonly Image _poster;
        private readonly MediaElement _media;
        private readonly DispatcherTimer _enterTimer;

        private bool _isMouseOver;
        private bool _mediaOpened;
        private string? _currentPosterUri;
        private string? _currentMediaUri;
        private string? _localMediaPath;

        public static readonly DependencyProperty SourceUriProperty =
            DependencyProperty.Register(nameof(SourceUri), typeof(string), typeof(MediaHoverImage),
                new PropertyMetadata(null, OnSourceUriChanged));

        public string? SourceUri
        {
            get => (string?)GetValue(SourceUriProperty);
            set => SetValue(SourceUriProperty, value);
        }

        public static readonly DependencyProperty HoverSourceUriProperty =
            DependencyProperty.Register(nameof(HoverSourceUri), typeof(string), typeof(MediaHoverImage),
                new PropertyMetadata(null, OnHoverSourceUriChanged));

        public string? HoverSourceUri
        {
            get => (string?)GetValue(HoverSourceUriProperty);
            set => SetValue(HoverSourceUriProperty, value);
        }

        /// <summary>本地文件可直接用本地路径作为 SourceUri；也可用此属性直接绑定 ImageSource 作为封面。</summary>
        public static readonly DependencyProperty PosterSourceProperty =
            DependencyProperty.Register(nameof(PosterSource), typeof(ImageSource), typeof(MediaHoverImage),
                new PropertyMetadata(null, OnPosterSourceChanged));

        public ImageSource? PosterSource
        {
            get => (ImageSource?)GetValue(PosterSourceProperty);
            set => SetValue(PosterSourceProperty, value);
        }

        public MediaHoverImage()
        {
            _poster = new Image
            {
                Stretch = Stretch.UniformToFill,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = System.Windows.VerticalAlignment.Center
            };

            _media = new MediaElement
            {
                Stretch = Stretch.UniformToFill,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = System.Windows.VerticalAlignment.Center,
                LoadedBehavior = MediaState.Manual,
                UnloadedBehavior = MediaState.Manual,
                Volume = 0,
                IsMuted = true,
                Visibility = Visibility.Collapsed
            };
            _media.MediaOpened += (_, _) =>
            {
                _mediaOpened = true;
                if (_isMouseOver && _media.Visibility == Visibility.Visible)
                {
                    // 视频已准备好，再隐藏封面，避免黑屏/闪屏
                    _poster.Visibility = Visibility.Collapsed;
                }
            };
            _media.MediaEnded += (_, _) =>
            {
                if (_media.Source != null)
                {
                    _media.Position = TimeSpan.Zero;
                    _media.Play();
                }
            };
            _media.MediaFailed += (_, args) =>
            {
                // 远程视频打开失败（图床 hotlink/Referer 限制）时，尝试回退本地缓存
                if (_currentMediaUri != null && Uri.TryCreate(_currentMediaUri, UriKind.Absolute, out var u) &&
                    (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps))
                {
                    _ = EnsureLocalMediaAsync(_currentMediaUri, retry: true);
                }
                else
                {
                    Logger.Log($"[MediaHoverImage] 视频打开失败: {args.ErrorException?.Message}");
                }
            };

            // 注意：保持 _poster 在视觉树中的上层，这样视频解码完成前仍显示封面，避免闪黑
            Children.Add(_media);
            Children.Add(_poster);

            _enterTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(180), DispatcherPriority.Normal, OnEnterTimerTick, Dispatcher);
            _enterTimer.Stop();

            MouseEnter += OnMouseEnter;
            MouseLeave += OnMouseLeave;
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            // TabControl 切换会导致控件被 Unload/Reload，但 DP 值不会变，
            // 因此重新应用当前绑定的 URI，避免切回后缩略图空白、悬停无动画。
            SetPoster(SourceUri, PosterSource);

            // MediaElement 在 Unload/Reload 后若 Source 不变，可能不会重新触发 MediaOpened，
            // 导致切回页签后悬停无法播放。强制先清空 Source 再重设，确保媒体重新加载。
            _media.Source = null;
            SetMediaSource(HoverSourceUri);
        }

        private static void OnSourceUriChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is MediaHoverImage img) img.SetPoster(e.NewValue as string, img.PosterSource);
        }

        private static void OnPosterSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is MediaHoverImage img) img.SetPoster(img.SourceUri, e.NewValue as ImageSource);
        }

        private static void OnHoverSourceUriChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is MediaHoverImage img) img.SetMediaSource(e.NewValue as string);
        }

        private void SetPoster(string? uri, ImageSource? source)
        {
            _currentPosterUri = uri;
            try
            {
                if (source != null)
                {
                    _poster.Source = source;
                    return;
                }
                if (string.IsNullOrWhiteSpace(uri))
                {
                    _poster.Source = null;
                    return;
                }
                // 远程封面：先下载到内存流再解码，规避 WPF 直接加载远程 webp 卡 1×1 的已知问题
                if (Uri.TryCreate(uri, UriKind.Absolute, out var u) &&
                    (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps))
                {
                    _ = LoadRemotePosterAsync(uri);
                    return;
                }
                // 本地文件：直接解码
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(uri, UriKind.Absolute);
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                if (!bmp.IsFrozen) bmp.Freeze();
                _poster.Source = bmp;
            }
            catch
            {
                _poster.Source = null;
            }
        }

        private async Task LoadRemotePosterAsync(string uri)
        {
            try
            {
                var bytes = await DownloadBytesAsync(uri, CancellationToken.None);
                if (bytes == null || bytes.Length == 0) return;
                var bmp = new BitmapImage();
                using var ms = new MemoryStream(bytes);
                bmp.BeginInit();
                bmp.StreamSource = ms;
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                if (!bmp.IsFrozen) bmp.Freeze();
                // 避免竞态：仅当封面 URI 未变时才应用；用 BeginInvoke 避免在非 UI 线程回封时死锁
                Dispatcher.BeginInvoke((Action)(() =>
                {
                    if (_currentPosterUri == uri)
                        _poster.Source = bmp;
                }));
            }
            catch
            {
                // 远程封面最终失败则保持灰底（极少数情况），不抛出异常
            }
        }

        private void SetMediaSource(string? uri)
        {
            try
            {
                _mediaOpened = false;
                _localMediaPath = null;
                _currentMediaUri = uri;
                if (string.IsNullOrWhiteSpace(uri))
                {
                    _media.Source = null;
                    return;
                }
                if (Uri.TryCreate(uri, UriKind.Absolute, out var absoluteUri))
                {
                    _media.Source = absoluteUri;
                    return;
                }
                // 支持本地绝对路径（如 C:\Users\...\video.mp4）
                var full = System.IO.Path.GetFullPath(uri);
                _media.Source = new Uri(full, UriKind.Absolute);
            }
            catch
            {
                _media.Source = null;
            }
        }

        /// <summary>
        /// 确保悬停视频有可用源：优先用远程直链（多数情况下可直接播放）；
        /// 若远程失败且 retry=true，则把视频下载到本地缓存后用本地文件播放，保证动效可用。
        /// </summary>
        private async Task EnsureLocalMediaAsync(string uri, bool retry)
        {
            try
            {
                var local = await DownloadToCacheAsync(uri, CancellationToken.None);
                if (local == null) return;
                _localMediaPath = local;
                Dispatcher.BeginInvoke((Action)(() =>
                {
                    if (_currentMediaUri != uri) return;
                    _media.Source = new Uri(local, UriKind.Absolute);
                    if (_isMouseOver) { _media.Visibility = Visibility.Visible; _media.Play(); }
                }));
            }
            catch
            {
                // 忽略：回退失败不影响封面显示
            }
        }

        private void OnMouseEnter(object sender, MouseEventArgs e)
        {
            if (_media.Source == null && _localMediaPath == null) return;
            _isMouseOver = true;
            _enterTimer.Stop();
            _enterTimer.Start();
        }

        private void OnMouseLeave(object sender, MouseEventArgs e)
        {
            _isMouseOver = false;
            _enterTimer.Stop();
            _media.Stop();
            _media.Visibility = Visibility.Collapsed;
            _poster.Visibility = Visibility.Visible;
        }

        private void OnEnterTimerTick(object? sender, EventArgs e)
        {
            _enterTimer.Stop();
            if (!_isMouseOver) return;
            if (_media.Source == null)
            {
                // 源为空（例如本地路径未就绪），尝试用远程/本地缓存兜底
                if (!string.IsNullOrWhiteSpace(_currentMediaUri))
                    _ = EnsureLocalMediaAsync(_currentMediaUri, retry: false);
                return;
            }

            // 开始播放，但封面仍保持可见（在最上层），直到 MediaOpened 触发
            _media.Visibility = Visibility.Visible;
            _media.Position = TimeSpan.Zero;
            _media.Play();

            // 如果已经打开过（之前悬停过且仍在可视树中），可以直接隐藏封面
            if (_mediaOpened)
                _poster.Visibility = Visibility.Collapsed;
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            // 只停止播放和定时器，不要清空 Source。
            // TabControl 切换会反复 Unload/Reload 控件，清空 Source 后 DP 值未变，
            // 会导致切回页签时缩略图空白且悬停无动画。
            _enterTimer.Stop();
            _media.Stop();
            _isMouseOver = false;
            _mediaOpened = false;
        }

        #region 远程下载辅助（带 Referer，规避 3gbizhi 图床 hotlink 限制）

        private static async Task<byte[]?> DownloadBytesAsync(string url, CancellationToken ct)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                if (url.Contains("3gbizhi.com", StringComparison.OrdinalIgnoreCase))
                    req.Headers.Referrer = new Uri("https://desk.3gbizhi.com/");
                using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                if ((int)resp.StatusCode is 307 or 308)
                {
                    var loc = resp.Headers.Location;
                    if (loc != null)
                    {
                        var next = loc.IsAbsoluteUri ? loc.ToString() : new Uri(new Uri(url), loc).ToString();
                        return await DownloadBytesAsync(next, ct);
                    }
                }
                resp.EnsureSuccessStatusCode();
                return await resp.Content.ReadAsByteArrayAsync(ct);
            }
            catch
            {
                return null;
            }
        }

        private static async Task<string?> DownloadToCacheAsync(string url, CancellationToken ct)
        {
            try
            {
                var bytes = await DownloadBytesAsync(url, ct);
                if (bytes == null || bytes.Length == 0) return null;
                var dir = Path.Combine(AppContext.BaseDirectory, "Wallpapers", "hovercache");
                Directory.CreateDirectory(dir);
                var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(url))).Substring(0, 16).ToLowerInvariant();
                var ext = Path.GetExtension(new Uri(url).AbsolutePath);
                if (string.IsNullOrEmpty(ext) || ext.Length > 5) ext = ".mp4";
                var path = Path.Combine(dir, hash + ext);
                if (!File.Exists(path) || new FileInfo(path).Length == 0)
                    await File.WriteAllBytesAsync(path, bytes, ct);
                return path;
            }
            catch
            {
                return null;
            }
        }

        #endregion
    }
}
