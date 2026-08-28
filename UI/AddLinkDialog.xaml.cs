using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using MessageBox = System.Windows.MessageBox;
using DynamicWallpaper.Core;
using DynamicWallpaper.Models;

namespace DynamicWallpaper.UI
{
    public partial class AddLinkDialog : Window
    {
        /// <summary>下载完成后的本地路径；为空表示未成功。</summary>
        public string? DownloadedPath { get; private set; }

        /// <summary>用户是否勾选“添加后直接设为壁纸”。</summary>
        public bool ApplyAfter { get; private set; }

        /// <summary>是否使用在线直播模式（不下载）。</summary>
        public bool IsOnline { get; private set; }

        /// <summary>在线模式下的媒体直链 URL。</summary>
        public string? OnlineUrl { get; private set; }

        /// <summary>在线模式下的壁纸类型（图片/视频）。</summary>
        public WallpaperType OnlineType { get; private set; }

        // 下载的壁纸保存到程序根目录 Wallpapers 子目录，不写入系统用户目录（C 盘）。
        private static readonly string SaveDir = Path.Combine(AppPaths.RootDirectory, "Wallpapers");

        private static readonly HashSet<string> KnownExt = new(StringComparer.OrdinalIgnoreCase)
        {
            ".mp4", ".webm", ".mkv", ".avi", ".mov", ".wmv", ".m4v", ".mpg", ".mpeg", ".flv", ".ts",
            ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp", ".html", ".htm"
        };

        private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".mp4", ".webm", ".mkv", ".avi", ".mov", ".wmv", ".m4v", ".mpg", ".mpeg", ".flv", ".ts"
        };

        private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp" };

        private static readonly Dictionary<string, string> ContentTypeExt = new(StringComparer.OrdinalIgnoreCase)
        {
            { "video/mp4", ".mp4" }, { "video/webm", ".webm" }, { "video/x-matroska", ".mkv" },
            { "video/quicktime", ".mov" }, { "video/x-msvideo", ".avi" }, { "video/x-flv", ".flv" },
            { "image/jpeg", ".jpg" }, { "image/png", ".png" }, { "image/gif", ".gif" },
            { "image/webp", ".webp" }, { "image/bmp", ".bmp" }, { "text/html", ".html" }
        };

        private static readonly HttpClient _client = new(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All
        })
        {
            Timeout = TimeSpan.FromMinutes(15)
        };

        public AddLinkDialog()
        {
            InitializeComponent();
            ProgressBarBorder.Visibility = Visibility.Collapsed;
            this.KeyDown += (s, e) =>
            {
                if (e.Key == System.Windows.Input.Key.Escape)
                {
                    DialogResult = false;
                    Close();
                }
            };
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            try
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                if (hwnd != IntPtr.Zero)
                    DynamicWallpaper.Desktop.Win32.ApplyDwmDarkChrome(hwnd);
            }
            catch { /* DWM 属性不支持的旧系统上静默忽略 */ }
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            UrlBox.Focus();
            UpdateButtonText();
        }

        private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ChangedButton == System.Windows.Input.MouseButton.Left)
                DragMove();
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void OnlineChk_Changed(object sender, RoutedEventArgs e)
        {
            UpdateButtonText();
        }

        private void UpdateButtonText()
        {
            // XAML 加载 OnlineChk 时会立即触发 Checked 事件，此时 DownloadBtn 可能尚未初始化，必须做空引用保护。
            if (DownloadBtn == null || OnlineChk == null) return;
            DownloadBtn.Content = OnlineChk.IsChecked == true ? "添加" : "下载并添加";
        }

        private static string CleanPathForExt(string url)
        {
            try { return new Uri(url).AbsolutePath; }
            catch { return url; }
        }

        private async void Download_Click(object sender, RoutedEventArgs e)
        {
            var text = UrlBox.Text.Trim();
            if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                MessageBox.Show("请输入有效的 http/https 链接。\n\n注意：必须是【媒体直链】（通常以 .mp4 / .webm / .jpg 等结尾），不是网页地址。", "链接无效", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 在线直播模式：不下载，直接根据扩展名识别类型并返回
            if (OnlineChk.IsChecked == true)
            {
                var extension = Path.GetExtension(CleanPathForExt(text));
                var isImage = ImageExtensions.Contains(extension);
                var isM3u8 = extension.Equals(".m3u8", StringComparison.OrdinalIgnoreCase);
                if (!isImage && !VideoExtensions.Contains(extension) && !isM3u8)
                {
                    // 扩展名无法识别时默认按视频流播尝试
                    Logger.Log($"在线直播：扩展名未识别（{extension}），按视频流播尝试：{text}");
                }
                // m3u8 走 Web 类型，由 WebProvider 用内嵌 hls.js 流式播放；其余按图片/视频处理
                OnlineType = isImage ? WallpaperType.Image : (isM3u8 ? WallpaperType.Web : WallpaperType.Video);
                OnlineUrl = text;
                IsOnline = true;
                ApplyAfter = ApplyChk.IsChecked == true;
                StatusText.Text = "已添加在线直播源";
                Logger.Log($"在线直播源已添加：{text}（{OnlineType}）");
                DialogResult = true;
                Close();
                return;
            }

            // 下载模式
            DownloadBtn.IsEnabled = false;
            CancelBtn.IsEnabled = false;
            ProgressBar.Value = 0;
            ProgressBarBorder.Visibility = Visibility.Visible;
            StatusText.Text = "正在连接…";
            Logger.Log($"下载开始：{text}");

            try
            {
                var req = new HttpRequestMessage(HttpMethod.Get, uri);
                req.Headers.UserAgent.Add(new ProductInfoHeaderValue("DynamicWallpaper", "1.0"));
                req.Headers.Referrer = new Uri($"{uri.Scheme}://{uri.Host}");
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));

                using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
                Logger.Log($"响应：{(int)resp.StatusCode} {resp.ReasonPhrase} | contentType={resp.Content.Headers.ContentType?.MediaType} | length={resp.Content.Headers.ContentLength}");

                if (!resp.IsSuccessStatusCode)
                    throw new Exception($"服务器返回 {(int)resp.StatusCode} {resp.StatusCode}（{resp.ReasonPhrase}）。\n多为防盗链或需要登录；请确认是可直接访问的媒体直链。");

                var contentType = resp.Content.Headers.ContentType?.MediaType ?? "";
                if (contentType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase))
                    throw new Exception("这个链接返回的是【网页】而不是视频/图片文件。\n视频直链通常形如 https://.../xxx.mp4。\n请在浏览器打开该页 → F12 → 网络(Network) → 筛选 Media → 复制真正的 .mp4 链接。");

                var name = Path.GetFileName(uri.LocalPath);
                var ext = Path.GetExtension(name);
                if (string.IsNullOrEmpty(name) || !KnownExt.Contains(ext))
                {
                    var mapped = (contentType != "" && ContentTypeExt.TryGetValue(contentType, out var m)) ? m
                                 : (string.IsNullOrEmpty(ext) ? ".mp4" : ext);
                    var baseName = string.IsNullOrEmpty(Path.GetFileNameWithoutExtension(name)) ? "wallpaper" : Path.GetFileNameWithoutExtension(name);
                    name = baseName + mapped;
                }

                Directory.CreateDirectory(SaveDir);
                var dest = Path.Combine(SaveDir, Sanitize(name));

                var total = resp.Content.Headers.ContentLength;
                await using var src = await resp.Content.ReadAsStreamAsync();
                await using var dst = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);

                var buffer = new byte[81920];
                long read = 0;
                int n;
                while ((n = await src.ReadAsync(buffer)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, n));
                    read += n;
                    if (total.HasValue && total.Value > 0)
                        ProgressBar.Value = Math.Min(100, (int)(read * 100 / total.Value));
                    StatusText.Text = total.HasValue
                        ? $"下载中… {ProgressBar.Value}%"
                        : $"下载中… {read / 1024 / 1024} MB";
                }
                await dst.FlushAsync();

                if (IsHtmlContent(dest))
                {
                    try { File.Delete(dest); } catch { }
                    throw new Exception("下载完成，但文件内容其实是网页(HTML)，并非真实视频/图片。\n请复制真正的 .mp4 媒体直链（F12 → 网络 → 筛选 Media）。");
                }

                DownloadedPath = dest;
                ApplyAfter = ApplyChk.IsChecked == true;
                ProgressBarBorder.Visibility = Visibility.Collapsed;
                StatusText.Text = "完成";
                Logger.Log($"下载成功：{dest}");
                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                Logger.Log("下载失败", ex);
                ProgressBarBorder.Visibility = Visibility.Collapsed;
                StatusText.Text = "失败：" + ex.Message;
                MessageBox.Show("下载失败：" + ex.Message + "\n\n（详细日志已记录到 app.log，可发给我排查）", "下载失败", MessageBoxButton.OK, MessageBoxImage.Error);
                DownloadBtn.IsEnabled = true;
                CancelBtn.IsEnabled = true;
            }
        }

        /// <summary>下载后二次校验：某些站点 Content-Type 不准，文件实际是 HTML。</summary>
        private static bool IsHtmlContent(string path)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 512);
                var buf = new byte[512];
                int n = fs.Read(buf, 0, buf.Length);
                var head = System.Text.Encoding.ASCII.GetString(buf, 0, n).TrimStart();
                return head.StartsWith("<!doctype", StringComparison.OrdinalIgnoreCase)
                    || head.StartsWith("<html", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static string Sanitize(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name.Length > 200 ? name[..200] : name;
        }
    }
}
