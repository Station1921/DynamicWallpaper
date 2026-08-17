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

namespace DynamicWallpaper.UI
{
    public partial class AddLinkDialog : Window
    {
        /// <summary>下载完成后的本地路径；为空表示未成功。</summary>
        public string? DownloadedPath { get; private set; }

        /// <summary>用户是否勾选“下载后直接设为壁纸”。</summary>
        public bool ApplyAfter { get; private set; }

        private static readonly string SaveDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DynamicWallpaper", "Wallpapers");

        private static readonly HashSet<string> KnownExt = new(StringComparer.OrdinalIgnoreCase)
        {
            ".mp4", ".webm", ".mkv", ".avi", ".mov", ".wmv", ".m4v", ".mpg", ".mpeg", ".flv", ".ts",
            ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp", ".html", ".htm"
        };

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
        }

        private async void Download_Click(object sender, RoutedEventArgs e)
        {
            var url = UrlBox.Text.Trim();
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                MessageBox.Show("请输入有效的 http/https 链接。\n\n注意：必须是【媒体直链】（通常以 .mp4 / .webm / .jpg 等结尾），不是网页地址。", "链接无效", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            DownloadBtn.IsEnabled = false;
            CancelBtn.IsEnabled = false;
            ProgressBar.Value = 0;
            StatusText.Text = "正在连接…";
            Logger.Log($"下载开始：{url}");

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
                StatusText.Text = "完成";
                Logger.Log($"下载成功：{dest}");
                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                Logger.Log("下载失败", ex);
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

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
