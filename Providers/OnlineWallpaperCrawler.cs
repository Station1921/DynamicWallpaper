using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DynamicWallpaper.Core;
using DynamicWallpaper.Models;

namespace DynamicWallpaper.Providers
{
    /// <summary>通用分类信息（netbian / 3gbizhi 共用）。</summary>
    public class CategoryInfo
    {
        public string Name { get; set; } = "";
        public string Slug { get; set; } = "";

        /// <summary>ComboBox 选中项（SelectionBoxItem）直接显示 Name，避免自定义模板显示类型全名。</summary>
        public override string ToString() => Name;
    }

    /// <summary>
    /// 在线壁纸站点爬虫。零外部依赖，仅使用 .NET 内置 HttpClient + 正则解析。
    /// 仅供个人本地使用，请遵守各站点服务条款与版权规定。
    /// </summary>
    public static class OnlineWallpaperCrawler
    {
        private static readonly HttpClient Client;

        static OnlineWallpaperCrawler()
        {
            // .NET 8 默认不支持 GBK/GB2312，注册 CodePages 提供程序以正确解码中文壁纸站页面。
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

            var handler = new HttpClientHandler
            {
                AllowAutoRedirect = true,
                MaxAutomaticRedirections = 10,
                AutomaticDecompression = DecompressionMethods.All,
                SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                ServerCertificateCustomValidationCallback = (msg, cert, chain, err) => true
            };
            Client = new HttpClient(handler);

            Client.DefaultRequestHeaders.Add("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/115.0.0.0 Safari/537.36");
            Client.DefaultRequestHeaders.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8");
            Client.DefaultRequestHeaders.Add("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8");
            Client.DefaultRequestHeaders.Add("Accept-Encoding", "gzip, deflate, br");
            Client.DefaultRequestHeaders.Add("Cache-Control", "max-age=0");
            Client.DefaultRequestHeaders.Add("Upgrade-Insecure-Requests", "1");
            Client.DefaultRequestHeaders.Add("Sec-Fetch-Dest", "document");
            Client.DefaultRequestHeaders.Add("Sec-Fetch-Mode", "navigate");
            Client.DefaultRequestHeaders.Add("Sec-Fetch-Site", "none");
            Client.DefaultRequestHeaders.Add("Sec-Fetch-User", "?1");
            Client.Timeout = TimeSpan.FromSeconds(45);
        }

        /// <summary>在线壁纸自动下载保存目录：程序 exe 根目录旁的 Wallpapers 文件夹。</summary>
        public static string OnlineSaveDirectory
        {
            get
            {
                // 统一走 AppPaths.RootDirectory：单文件发布时 AppContext.BaseDirectory
                // 指向 C 盘临时解压目录，而 AppPaths.RootDirectory 始终为 exe 真实所在目录。
                return Path.Combine(AppPaths.RootDirectory, "Wallpapers");
            }
        }

        private static string ThumbCacheDir => Path.Combine(OnlineSaveDirectory, "thumbs");

        public static IReadOnlyList<CategoryInfo> NetbianCategories { get; } = new List<CategoryInfo>
        {
            new() { Name = "最新", Slug = "" },
            new() { Name = "4K壁纸", Slug = "4k" },
            new() { Name = "风景", Slug = "fengjing" },
            new() { Name = "美女", Slug = "meinv" },
            new() { Name = "动漫", Slug = "dongman" },
            new() { Name = "游戏", Slug = "youxi" },
            new() { Name = "影视", Slug = "yingshi" },
            new() { Name = "明星", Slug = "mingxing" },
            new() { Name = "汽车", Slug = "qiche" },
            new() { Name = "动物", Slug = "dongwu" },
            new() { Name = "植物", Slug = "zhiwu" },
            new() { Name = "美食", Slug = "meishi" },
            new() { Name = "节日", Slug = "jieri" },
            new() { Name = "简约", Slug = "jianyue" },
            new() { Name = "日历", Slug = "rili" }
        };

        /// <summary>3gbizhi.com（3G壁纸）的分类，默认 deskMV（美女壁纸）。</summary>
        public static IReadOnlyList<CategoryInfo> GBizhiCategories { get; } = new List<CategoryInfo>
        {
            new() { Name = "美女壁纸", Slug = "deskMV" },
            new() { Name = "风景壁纸", Slug = "deskFJ" },
            new() { Name = "动漫壁纸", Slug = "deskDM" },
            new() { Name = "明星壁纸", Slug = "deskMX" },
            new() { Name = "汽车壁纸", Slug = "deskQC" },
            new() { Name = "影视壁纸", Slug = "deskYS" },
            new() { Name = "游戏壁纸", Slug = "deskYX" },
            new() { Name = "植物壁纸", Slug = "deskZW" },
            new() { Name = "动物壁纸", Slug = "deskDW" },
            new() { Name = "节日壁纸", Slug = "deskJR" },
            new() { Name = "简约壁纸", Slug = "deskjy" },
            new() { Name = "唯美壁纸", Slug = "deskwm" },
            new() { Name = "车模壁纸", Slug = "deskCM" },
            new() { Name = "创意壁纸", Slug = "deskCY" },
            new() { Name = "动态壁纸", Slug = "deskDT" },
            new() { Name = "可爱壁纸", Slug = "deskKA" },
            new() { Name = "精美壁纸", Slug = "deskJM" },
            new() { Name = "体育壁纸", Slug = "deskTY" }
        };

        /// <summary>Wallhaven（wallhaven.cc）的分类，与站点顶部筛选栏一致。
        /// Slug 存 API 参数串 "categories|purity"：categories 三位标记（第1位 general、第2位 anime、第3位 people，1 启用 0 禁用）；
        /// purity 三位标记（第1位 sfw、第2位 sketchy、第3位 nsfw，1 启用 0 禁用）。</summary>
        public static IReadOnlyList<CategoryInfo> WallhavenCategories { get; } = new List<CategoryInfo>
        {
            new() { Name = "全部", Slug = "111|100" },
            new() { Name = "General", Slug = "100|100" },
            new() { Name = "Anime", Slug = "010|100" },
            new() { Name = "People", Slug = "001|100" },
            new() { Name = "SFW", Slug = "111|100" },
            new() { Name = "Sketchy", Slug = "111|010" }
        };

        /// <summary>最近一次 wallhaven API 调用返回的末页页号，用于 LoadGBizhiAsync 判断是否还有更多内容。
        /// 初始化时设为 int.MaxValue 表示"未知"，调用后设为实际值。API 失败时保持原值不变。</summary>
        public static int WallhavenLastPage { get; set; } = int.MaxValue;

        /// <summary>Wallhaven（wallhaven.cc）的分辨率筛选选项，与站点 Resolution 下拉一致。
        /// Value 为 wallhaven API 的 resolutions 参数值（如 1920x1080），空串表示不限。</summary>
        public static IReadOnlyList<CategoryInfo> WallhavenResolutions { get; } = new List<CategoryInfo>
        {
            new() { Name = "不限", Slug = "" },
            // Ultrawide（21:9）
            new() { Name = "2560x1080", Slug = "2560x1080" },
            new() { Name = "3440x1440", Slug = "3440x1440" },
            new() { Name = "3840x1600", Slug = "3840x1600" },
            new() { Name = "5120x2160", Slug = "5120x2160" },
            // 16:9
            new() { Name = "1280x720", Slug = "1280x720" },
            new() { Name = "1366x768", Slug = "1366x768" },
            new() { Name = "1600x900", Slug = "1600x900" },
            new() { Name = "1920x1080", Slug = "1920x1080" },
            new() { Name = "2560x1440", Slug = "2560x1440" },
            new() { Name = "3840x2160", Slug = "3840x2160" },
            new() { Name = "5120x2880", Slug = "5120x2880" },
            new() { Name = "7680x4320", Slug = "7680x4320" },
            // 16:10
            new() { Name = "1280x800", Slug = "1280x800" },
            new() { Name = "1440x900", Slug = "1440x900" },
            new() { Name = "1600x1000", Slug = "1600x1000" },
            new() { Name = "1920x1200", Slug = "1920x1200" },
            new() { Name = "2560x1600", Slug = "2560x1600" },
            new() { Name = "3840x2400", Slug = "3840x2400" },
            // 4:3
            new() { Name = "1280x960", Slug = "1280x960" },
            new() { Name = "1600x1200", Slug = "1600x1200" },
            new() { Name = "1920x1440", Slug = "1920x1440" },
            new() { Name = "2560x1920", Slug = "2560x1920" },
            new() { Name = "3840x2880", Slug = "3840x2880" },
            // 5:4
            new() { Name = "1280x1024", Slug = "1280x1024" },
            new() { Name = "1600x1280", Slug = "1600x1280" },
            new() { Name = "1920x1536", Slug = "1920x1536" },
            new() { Name = "2560x2048", Slug = "2560x2048" }
        };

        public static Task<List<OnlineWallpaperItem>> FetchAsync(string source, int page = 1, string? categorySlug = null, CancellationToken ct = default, string? wallhavenResolution = null)
        {
            return source.ToLowerInvariant() switch
            {
                "netbian" => FetchNetbianAsync(page, categorySlug, ct),
                "netbian-dongtai" => FetchNetbianAsync(page, "dongtai", ct, sourceLabel: "netbian-dongtai"),
                "3gbizhi" => FetchGBizhiAsync(page, categorySlug, ct),
                "3gbizhi-dt" => Fetch3gDTAsync(page, ct),
                "zhutix" => FetchZhutixAsync(page, ct),
                "wallhaven" => FetchWallhavenAsync(page, categorySlug, wallhavenResolution, ct),
                // "livewallpapers4free" => FetchLw4fAsync(page, ct), // 海外站点网络可达性差，当前默认源禁用
                _ => throw new ArgumentException($"未知在线壁纸来源：{source}")
            };
        }

        /// <summary>抓取 netbian.com 列表页。</summary>
        private static async Task<List<OnlineWallpaperItem>> FetchNetbianAsync(int page, string? categorySlug, CancellationToken ct, string sourceLabel = "netbian")
        {
            string url;
            var slug = categorySlug?.Trim('/') ?? "";
            var basePath = string.IsNullOrEmpty(slug) ? "/" : $"/{slug}/";
            if (page <= 1)
                url = $"https://www.netbian.com{basePath}";
            else
                url = $"https://www.netbian.com{basePath}index_{page}.htm";

            Logger.Log($"[爬虫] 开始抓取 netbian: {url}");
            var html = await GetStringWithEncodingAsync(url, ct);

            var list = new List<OnlineWallpaperItem>();
            // 匹配：<a href="/desk/12345.htm" ...><img src="..." alt="..."></a>
            var rx = new Regex(
                @"<a\s+href\s*=\s*""/desk/(\d+)\.htm""[^>]*>[\s\S]*?<img\s+src\s*=\s*""([^""]+)""[^>]*alt\s*=\s*""([^""]*)""[^>]*>",
                RegexOptions.IgnoreCase | RegexOptions.Compiled);

            foreach (Match m in rx.Matches(html))
            {
                var thumbUrl = MakeAbsolute(m.Groups[2].Value.Trim(), "https://www.netbian.com");
                var item = new OnlineWallpaperItem
                {
                    Title = WebUtility.HtmlDecode(m.Groups[3].Value.Trim()),
                    ThumbnailUrl = thumbUrl,
                    DetailUrl = $"https://www.netbian.com/desk/{m.Groups[1].Value}.htm",
                    Source = sourceLabel
                };

                // netbian 动态壁纸列表的缩略图是单帧静态小 GIF；
                // 可通过去掉 "small" 前缀与末尾 10 位时间戳推导出真实高清动画 GIF，用于悬停预览。
                if (sourceLabel == "netbian-dongtai")
                {
                    var preview = TryDeriveNetbianFullGif(thumbUrl);
                    if (!string.IsNullOrEmpty(preview))
                        item.PreviewUrl = preview;
                }

                list.Add(item);
            }

            Logger.Log($"[爬虫] {sourceLabel} 解析到 {list.Count} 条");
            return list;
        }

        /// <summary>
        /// 从 netbian 动态壁纸列表缩略图推导出高清原 GIF 地址。
        /// 缩略图格式：.../small{base}{10位时间戳}.gif，对应原图：.../{base}.gif
        /// </summary>
        private static string? TryDeriveNetbianFullGif(string thumbUrl)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(thumbUrl)) return null;
                var uri = new Uri(thumbUrl);
                var name = Path.GetFileName(uri.AbsolutePath);
                if (!name.StartsWith("small", StringComparison.OrdinalIgnoreCase)) return null;
                if (!name.EndsWith(".gif", StringComparison.OrdinalIgnoreCase)) return null;

                // 去掉 "small" 前缀和 ".gif" 后缀
                var body = name.Substring(5, name.Length - 9);
                if (body.Length <= 10) return null;

                // 末尾 10 位应为时间戳数字
                var ts = body.Substring(body.Length - 10);
                if (!ts.All(char.IsDigit)) return null;

                var baseName = body.Substring(0, body.Length - 10);
                if (string.IsNullOrEmpty(baseName)) return null;

                var fullName = baseName + ".gif";
                return new Uri(uri, fullName).ToString();
            }
            catch
            {
                return null;
            }
        }

        /// <summary>抓取 desk.3gbizhi.com 列表页（默认 deskMV 美女壁纸，可指定分类 slug）。</summary>
        private static async Task<List<OnlineWallpaperItem>> FetchGBizhiAsync(int page, string? categorySlug, CancellationToken ct)
        {
            var slug = string.IsNullOrWhiteSpace(categorySlug) ? "deskMV" : categorySlug.Trim('/');
            var baseUrl = $"https://desk.3gbizhi.com/{slug}/";
            var url = page <= 1 ? baseUrl + "index_1.html" : baseUrl + $"index_{page}.html";

            Logger.Log($"[爬虫] 开始抓取 3gbizhi: {url}");
            var html = await GetStringWithEncodingAsync(url, ct);

            var list = new List<OnlineWallpaperItem>();
            // 列表条目：<a href="https://desk.3gbizhi.com/{slug}/NNNN.html" ...> ... <img ... lay-src="缩略图" alt="标题" .../>
            var rx = new Regex(
                @"<a\s+href\s*=\s*""(https://desk\.3gbizhi\.com/[^""]+?/\d+\.html)""[^>]*>[\s\S]*?lay-src\s*=\s*""([^""]+)""[^>]*?alt\s*=\s*""([^""]*)""",
                RegexOptions.IgnoreCase | RegexOptions.Compiled);

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match m in rx.Matches(html))
            {
                var detail = m.Groups[1].Value.Trim();
                if (!seen.Add(detail)) continue;

                var thumb = m.Groups[2].Value.Trim();
                var title = WebUtility.HtmlDecode(m.Groups[3].Value.Trim());
                var local = await DownloadThumbAsync(thumb, ct);

                list.Add(new OnlineWallpaperItem
                {
                    Title = title,
                    ThumbnailUrl = local ?? thumb,
                    DetailUrl = detail,
                    Source = "3gbizhi"
                });
            }

            Logger.Log($"[爬虫] 3gbizhi 解析到 {list.Count} 条");
            return list;
        }

        /// <summary>抓取 desk.3gbizhi.com 动态视频壁纸列表（deskDT）。列表直接提供 MP4 直链。</summary>
        private static async Task<List<OnlineWallpaperItem>> Fetch3gDTAsync(int page, CancellationToken ct)
        {
            var url = $"https://desk.3gbizhi.com/deskDT/index_{page}.html";
            Logger.Log($"[爬虫] 开始抓取 3gbizhi 动态壁纸: {url}");
            var html = await GetStringWithEncodingAsync(url, ct);

            var list = new List<OnlineWallpaperItem>();
            // 每个视频壁纸是一个 <li class="box_black video-item">，内部有 <video poster="..."><source src="...mp4"></video>
            var itemRx = new Regex(@"<li class=""box_black video-item"">([\s\S]*?)</li>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
            var detailRx = new Regex(@"href=""(https://desk\.3gbizhi\.com/deskDT/\d+\.html)""", RegexOptions.IgnoreCase | RegexOptions.Compiled);
            var posterRx = new Regex(@"poster=""([^""]+)""", RegexOptions.IgnoreCase | RegexOptions.Compiled);
            var mp4Rx = new Regex(@"<source src=""([^""]+\.mp4)""", RegexOptions.IgnoreCase | RegexOptions.Compiled);
            var titleRx = new Regex(@"<div class=""text"">([^<]+)</div>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match item in itemRx.Matches(html))
            {
                var block = item.Groups[1].Value;
                var detailM = detailRx.Match(block);
                if (!detailM.Success) continue;
                var detail = detailM.Groups[1].Value.Trim();
                if (!seen.Add(detail)) continue;

                var posterM = posterRx.Match(block);
                var mp4M = mp4Rx.Match(block);
                var titleM = titleRx.Match(block);

                var title = titleM.Success ? WebUtility.HtmlDecode(titleM.Groups[1].Value.Trim()) : "动态壁纸";
                var poster = posterM.Success ? posterM.Groups[1].Value.Trim() : "";
                var mp4 = mp4M.Success ? mp4M.Groups[1].Value.Trim() : "";

                var localPoster = await DownloadThumbAsync(poster, ct);

                list.Add(new OnlineWallpaperItem
                {
                    Title = title,
                    ThumbnailUrl = localPoster ?? poster,
                    DetailUrl = detail,
                    Source = "3gbizhi-dt",
                    DownloadUrl = mp4,
                    PreviewUrl = mp4
                });
            }

            Logger.Log($"[爬虫] 3gbizhi-dt 解析到 {list.Count} 条");
            return list;
        }

        /// <summary>
        /// 抓取 zhutix.com 动态壁纸列表（https://zhutix.com/animated/）。
        /// 该站列表缩略图只是 400x225 的预览 GIF，高清视频需要进入详情页提取 iframe（优酷）或网盘链接。
        /// </summary>
        private static async Task<List<OnlineWallpaperItem>> FetchZhutixAsync(int page, CancellationToken ct)
        {
            var url = page <= 1 ? "https://zhutix.com/animated/" : $"https://zhutix.com/animated/page/{page}/";

            Logger.Log($"[爬虫] 开始抓取 zhutix: {url}");
            var html = await GetStringWithEncodingAsync(url, ct);

            var list = new List<OnlineWallpaperItem>();

            // 每条列表项是一个 <li class="post-list-item ..."> 块
            var liRx = new Regex(@"<li\s+class=""post-list-item[^""]*""[\s\S]*?</li>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
            var imgTagRx = new Regex(@"<img\b[^>]*class=""post-thumb[^""]*""[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
            var gifRx = new Regex(@"(?:data-src|src)=""([^""]+\.gif)""", RegexOptions.IgnoreCase | RegexOptions.Compiled);
            var titleRx = new Regex(@"class=""imglist-char shu""[^>]*>([^<]+)</a>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
            // 详情页链接在 thumb-link 的 <a> 上，而不是 post-thumb 上
            var detailRx = new Regex(@"<a\b[^>]*class=""thumb-link""[^>]*href=""([^""]+)""", RegexOptions.IgnoreCase | RegexOptions.Compiled);

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match li in liRx.Matches(html))
            {
                var block = li.Value;

                // 详情页链接
                var detailM = detailRx.Match(block);
                if (!detailM.Success) continue;
                var detailUrl = detailM.Groups[1].Value.Trim();
                detailUrl = MakeAbsolute(detailUrl, "https://zhutix.com/animated/");
                if (!seen.Add(detailUrl)) continue;

                // 标题
                var titleM = titleRx.Match(block);
                var title = titleM.Success ? WebUtility.HtmlDecode(titleM.Groups[1].Value.Trim()) : "动态壁纸";

                // 列表缩略图（小 GIF，仅用于网格预览）
                string thumb = "";
                var imgTag = imgTagRx.Match(block);
                if (imgTag.Success)
                {
                    var gifM = gifRx.Match(imgTag.Value);
                    if (gifM.Success)
                        thumb = gifM.Groups[1].Value.Trim();
                }
                if (string.IsNullOrEmpty(thumb))
                    thumb = detailUrl; // 兜底

                var localThumb = await DownloadThumbAsync(thumb, ct);

                list.Add(new OnlineWallpaperItem
                {
                    Title = title,
                    ThumbnailUrl = localThumb ?? thumb,
                    DetailUrl = detailUrl,
                    Source = "zhutix",
                    // 列表里的 GIF 即站点提供的原图直链（约 400x225，站点本身只提供此分辨率），
                    // 直接用它作为下载地址，避免拿到被 OSS 二次缩放过的更小缩略图。
                    DownloadUrl = thumb
                });
            }

            Logger.Log($"[爬虫] zhutix 解析到 {list.Count} 条");
            return list;
        }

        /// <summary>
        /// 抓取 wallhaven.cc 壁纸列表（对应站内 toplist 页面）。
        /// 优先调用官方 API（无需解析 HTML），失败时回退解析 toplist 页面。
        /// 排序固定 sorting=date_added&amp;order=desc，与 wallhaven 网站手动筛选默认排序一致。
        /// 原 toplist 仅收录排行榜壁纸，内容范围小于全站最新，导致筛选结果比网站少。
        /// </summary>
        private static async Task<List<OnlineWallpaperItem>> FetchWallhavenAsync(int page, string? categorySlug, string? resolutionFilter, CancellationToken ct)
        {
            var (categories, purity) = ParseWallhavenSlug(categorySlug);
            // 分辨率筛选：空串/空白表示不限；API 的 resolutions 参数格式如 1920x1080（x 小写）
            var resQuery = string.IsNullOrWhiteSpace(resolutionFilter) ? "" : $"&resolutions={Uri.EscapeDataString(resolutionFilter.Trim())}";
            var apiUrl = $"https://wallhaven.cc/api/v1/search?categories={categories}&purity={purity}&sorting=date_added&order=desc&page={page}{resQuery}";
            Logger.Log($"[爬虫] 开始抓取 wallhaven API: {apiUrl}");

            var list = new List<OnlineWallpaperItem>();
            try
            {
                var json = await GetStringWithEncodingAsync(apiUrl, ct);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("meta", out var meta) && meta.TryGetProperty("last_page", out var lp))
                    WallhavenLastPage = lp.GetInt32();
                if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                {
                    foreach (var el in data.EnumerateArray())
                    {
                        var id = el.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
                        var path = el.TryGetProperty("path", out var pathEl) ? pathEl.GetString() ?? "" : "";
                        var detail = el.TryGetProperty("url", out var urlEl) ? urlEl.GetString() ?? "" : "";
                        var resolution = el.TryGetProperty("resolution", out var resEl) ? resEl.GetString() ?? "" : "";
                        var category = el.TryGetProperty("category", out var catEl) ? catEl.GetString() ?? "" : "";
                        var purityTag = el.TryGetProperty("purity", out var purEl) ? purEl.GetString() ?? "" : "";

                        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(path)) continue;

                        // 列表缩略图优先 thumbs.large，其次 thumbs.original / thumbnail 兜底
                        var thumb = "";
                        if (el.TryGetProperty("thumbs", out var thumbs) && thumbs.ValueKind == JsonValueKind.Object)
                        {
                            if (thumbs.TryGetProperty("large", out var largeEl)) thumb = largeEl.GetString() ?? "";
                            if (string.IsNullOrEmpty(thumb) && thumbs.TryGetProperty("original", out var origEl)) thumb = origEl.GetString() ?? "";
                        }
                        if (string.IsNullOrEmpty(thumb) && el.TryGetProperty("thumbnail", out var thEl))
                            thumb = thEl.GetString() ?? "";

                        var localThumb = await DownloadThumbAsync(thumb, ct);
                        var title = string.IsNullOrEmpty(resolution) ? $"wallhaven-{id}" : $"wallhaven-{id} {resolution}";
                        if (!string.IsNullOrEmpty(category) && !string.IsNullOrEmpty(purityTag))
                            title = $"{title} [{category}/{purityTag}]";

                        list.Add(new OnlineWallpaperItem
                        {
                            Title = title,
                            ThumbnailUrl = localThumb ?? thumb,
                            DetailUrl = string.IsNullOrEmpty(detail) ? $"https://wallhaven.cc/w/{id}" : detail,
                            Source = "wallhaven",
                            // API 返回的 path 即原图直链（w.wallhaven.cc/full/...），下载时无需再进详情页
                            DownloadUrl = path,
                            Resolution = resolution
                        });
                    }
                }
                Logger.Log($"[爬虫] wallhaven API 解析到 {list.Count} 条");
                return list;
            }
            catch (Exception ex)
            {
                Logger.Log($"[爬虫] wallhaven API 失败，回退解析页面: {ex.Message}");
            }

            // 回退：解析 https://wallhaven.cc/toplist?page={page} 列表页
            var htmlUrl = $"https://wallhaven.cc/toplist?page={page}";
            Logger.Log($"[爬虫] 回退抓取 wallhaven 列表页: {htmlUrl}");
            var html = await GetStringWithEncodingAsync(htmlUrl, ct);

            // 每个条目是一个 <figure class="thumb ..."> 块，含 preview 详情链接与缩略图 img
            var figureRx = new Regex(@"<figure[^>]*class=""thumb[^""]*""[^>]*>([\s\S]*?)</figure>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
            var detailRx = new Regex(@"<a[^>]*class=""preview""[^>]*href=""(https://wallhaven\.cc/w/[^""]+)""", RegexOptions.IgnoreCase | RegexOptions.Compiled);
            var imgRx = new Regex(@"<img[^>]*src=""([^""]+)""", RegexOptions.IgnoreCase | RegexOptions.Compiled);

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match fig in figureRx.Matches(html))
            {
                var block = fig.Groups[1].Value;
                var detailM = detailRx.Match(block);
                if (!detailM.Success) continue;
                var detail = detailM.Groups[1].Value.Trim();
                if (!seen.Add(detail)) continue;

                var id = detail.TrimEnd('/').Split('/').LastOrDefault() ?? "";
                var imgM = imgRx.Match(block);
                var thumb = imgM.Success ? imgM.Groups[1].Value.Trim() : "";
                var localThumb = await DownloadThumbAsync(thumb, ct);

                list.Add(new OnlineWallpaperItem
                {
                    Title = string.IsNullOrEmpty(id) ? "wallhaven" : $"wallhaven-{id}",
                    ThumbnailUrl = localThumb ?? thumb,
                    DetailUrl = detail,
                    Source = "wallhaven",
                    // 列表页拿不到原图直链，下载时回退详情页解析
                    DownloadUrl = null
                });
            }

            Logger.Log($"[爬虫] wallhaven 列表页解析到 {list.Count} 条");
            return list;
        }

        /// <summary>解析 wallhaven 分类 Slug（"categories|purity"），缺省返回默认全部 SFW（111|100）。</summary>
        private static (string Categories, string Purity) ParseWallhavenSlug(string? categorySlug)
        {
            var slug = string.IsNullOrWhiteSpace(categorySlug) ? "" : categorySlug.Trim();
            var parts = slug.Split('|', StringSplitOptions.RemoveEmptyEntries);
            var categories = parts.Length > 0 && parts[0].Length == 3 ? parts[0] : "111";
            var purity = parts.Length > 1 && parts[1].Length == 3 ? parts[1] : "100";
            return (categories, purity);
        }

        /// <summary>
        /// 抓取 livewallpapers4free.com 动态壁纸列表。
        /// 列表页提供缩略图与详情页链接；先立即返回缩略图列表让 UI 可显示，
        /// 再后台并发获取详情页的 480p 预览视频和 /download/{id}/ 高清下载直链。
        /// 优先使用 4K 下载，其次 HD，避免默认下载过大的 8K 文件。
        /// </summary>
        private static async Task<List<OnlineWallpaperItem>> FetchLw4fAsync(int page, CancellationToken ct)
        {
            var url = page <= 1 ? "https://livewallpapers4free.com/" : $"https://livewallpapers4free.com/page/{page}/";
            Logger.Log($"[爬虫] 开始抓取 livewallpapers4free: {url}");
            var html = await GetStringWithEncodingAsync(url, ct);

            var list = new List<OnlineWallpaperItem>();
            var postRx = new Regex(@"<div id=""post-\d+""[^>]*>([\s\S]*?)</div>\s*(?=<div id=""post-\d+""|<div class=""pagination""|<footer|</body>)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
            var detailRx = new Regex(@"<a class=""thumbnail-link"" href=""([^""]+)""", RegexOptions.IgnoreCase | RegexOptions.Compiled);
            var thumbRx = new Regex(@"<img[^>]+src=""([^""]+)""[^>]*alt=""([^""]*)""", RegexOptions.IgnoreCase | RegexOptions.Compiled);
            var srcsetRx = new Regex(@"srcset=""([^""]+)""", RegexOptions.IgnoreCase | RegexOptions.Compiled);

            foreach (Match post in postRx.Matches(html))
            {
                var block = post.Groups[1].Value;
                var detailM = detailRx.Match(block);
                if (!detailM.Success) continue;
                var detail = detailM.Groups[1].Value.Trim();

                var thumbM = thumbRx.Match(block);
                var thumb = thumbM.Success ? thumbM.Groups[1].Value.Trim() : "";
                var title = thumbM.Success ? WebUtility.HtmlDecode(thumbM.Groups[2].Value.Trim()) : "Live Wallpaper";

                // 取 srcset 中最大尺寸缩略图，列表显示更清晰
                var srcsetM = srcsetRx.Match(block);
                if (srcsetM.Success)
                {
                    var candidates = new List<(string Url, int Width)>();
                    foreach (var entry in srcsetM.Groups[1].Value.Split(','))
                    {
                        var parts = entry.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length < 2) continue;
                        var sizeSpec = parts[1].Trim();
                        if (!sizeSpec.EndsWith("w", StringComparison.OrdinalIgnoreCase)) continue;
                        if (!int.TryParse(sizeSpec.TrimEnd('w', 'W'), out var width)) continue;
                        candidates.Add((parts[0], width));
                    }
                    if (candidates.Count > 0)
                        thumb = candidates.OrderByDescending(x => x.Width).First().Url;
                }

                var localThumb = await DownloadThumbAsync(thumb, ct);
                list.Add(new OnlineWallpaperItem
                {
                    Title = title,
                    ThumbnailUrl = localThumb ?? thumb,
                    DetailUrl = detail,
                    Source = "livewallpapers4free"
                });
            }

            Logger.Log($"[爬虫] livewallpapers4free 解析到 {list.Count} 条，后台获取详情页...");

            // 后台并发获取详情页：缩略图已显示，避免用户长时间看到空白；限制并发 3 个，降低被限流风险。
            _ = Task.Run(async () =>
            {
                using var sem = new SemaphoreSlim(3);
                var tasks = list.Select(async item =>
                {
                    await sem.WaitAsync(ct);
                    try
                    {
                        var detailHtml = await GetStringWithEncodingAsync(item.DetailUrl, ct);

                        var sourceM = Regex.Match(detailHtml, @"<source src=""([^""]+\.mp4)[^""]*""[^>]*type=""video/mp4""", RegexOptions.IgnoreCase);
                        if (sourceM.Success)
                            item.PreviewUrl = sourceM.Groups[1].Value.Trim();

                        var downloadLinks = Regex.Matches(detailHtml,
                            @"<a[^>]+href=""(https://livewallpapers4free\.com/download/(\d+)/)""[^>]*>([^<]+)</a>",
                            RegexOptions.IgnoreCase)
                            .Cast<Match>()
                            .Select(m => new
                            {
                                Url = m.Groups[1].Value.Trim(),
                                Text = WebUtility.HtmlDecode(m.Groups[3].Value.Trim())
                            })
                            .ToList();

                        // 优先 4K，其次 HD/2K，最后 8K（避免默认下载过大文件）
                        var chosen = downloadLinks.FirstOrDefault(x => x.Text.Contains("4k", StringComparison.OrdinalIgnoreCase))
                            ?? downloadLinks.FirstOrDefault(x => x.Text.Contains("HD", StringComparison.OrdinalIgnoreCase) || x.Text.Contains("2k", StringComparison.OrdinalIgnoreCase))
                            ?? downloadLinks.FirstOrDefault();
                        if (chosen != null)
                            item.DownloadUrl = chosen.Url;
                    }
                    catch (OperationCanceledException)
                    {
                        // 用户切换页签或取消，静默退出
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"[爬虫] lw4f 详情页失败 ({item.DetailUrl}): {ex.Message}");
                    }
                    finally
                    {
                        sem.Release();
                    }
                }).ToArray();
                await Task.WhenAll(tasks);
                Logger.Log("[爬虫] livewallpapers4free 详情页获取完成");
            }, ct);

            return list;
        }

        /// <summary>
        /// 下载指定在线壁纸到本地。返回保存后的完整路径；
        /// 若返回 null，表示该条目需要在外部浏览器中手动完成下载。
        /// </summary>
        public static async Task<string?> DownloadAsync(OnlineWallpaperItem item, DownloadHistory? history = null, CancellationToken ct = default)
        {
            var saveDir = Path.Combine(OnlineSaveDirectory, item.Source);
            Directory.CreateDirectory(saveDir);

            string? path = null;
            if (item.Source == "netbian" || item.Source == "netbian-dongtai")
                path = await DownloadNetbianAsync(item, saveDir, ct);
            else if (item.Source == "3gbizhi")
                path = await DownloadGBizhiAsync(item, saveDir, ct);
            else if (item.Source == "3gbizhi-dt")
                path = await Download3gDTAsync(item, saveDir, ct);
            else if (item.Source == "zhutix")
                path = await DownloadZhutixAsync(item, saveDir, ct);
            else if (item.Source == "wallhaven")
                path = await DownloadWallhavenAsync(item, saveDir, ct);

            if (!string.IsNullOrEmpty(path))
            {
                // 持久化下载记录，用于避免重复下载及删除后重下定位原 URL
                (history ?? DownloadHistory.Load()).Upsert(item.DetailUrl, path, item.Title, item.Source);
            }
            return path;
        }

        private static async Task<string?> DownloadNetbianAsync(OnlineWallpaperItem item, string saveDir, CancellationToken ct)
        {
            Logger.Log($"[爬虫] netbian 获取详情: {item.DetailUrl}");
            var detail = await GetStringWithEncodingAsync(item.DetailUrl, ct);

            // 详情页大图在 <div class="pic"> 里的 <img src="...">
            var rx = new Regex(
                @"<div\s+class\s*=\s*""pic""[^>]*>[\s\S]*?<img\s+src\s*=\s*""([^""]+)""",
                RegexOptions.IgnoreCase | RegexOptions.Compiled);
            var m = rx.Match(detail);
            if (!m.Success)
            {
                Logger.Log("[爬虫] netbian 未在详情页找到大图");
                return null;
            }

            var imgUrl = MakeAbsolute(m.Groups[1].Value.Trim(), "https://www.netbian.com");
            Logger.Log($"[爬虫] netbian 大图 URL: {imgUrl}");
            return await SaveFileAsync(imgUrl, saveDir, item.Title, ct);
        }

        private static async Task<string?> DownloadGBizhiAsync(OnlineWallpaperItem item, string saveDir, CancellationToken ct)
        {
            Logger.Log($"[爬虫] 3gbizhi 获取详情: {item.DetailUrl}");
            var detail = await GetStringWithEncodingAsync(item.DetailUrl, ct);

            // 提取详情页标注的原图尺寸（如 3840×2160），用于在 UI 提示用户：
            // 站点免费预览为 uploadmark webp（通常为 1280×720），高清原图需登录积分。
            var resMatch = Regex.Match(detail, @"<div class=""txt-left"">尺寸：(\d+×\d+)</div>", RegexOptions.IgnoreCase);
            if (resMatch.Success)
                item.Resolution = resMatch.Groups[1].Value.Trim();
            else
            {
                var sizeMatch = Regex.Match(detail, @"<span class=""sizes"">[^\d]*(\d+×\d+)", RegexOptions.IgnoreCase);
                if (sizeMatch.Success) item.Resolution = sizeMatch.Groups[1].Value.Trim();
            }

            // 详情页主图：<img id="contpic" src="/uploads/...png">
            var tagMatch = Regex.Match(detail, @"<img\b[^>]*\bid=""contpic""[^>]*>", RegexOptions.IgnoreCase);
            if (!tagMatch.Success)
            {
                Logger.Log("[爬虫] 3gbizhi 未找到 contpic 主图");
                return null;
            }
            var srcMatch = Regex.Match(tagMatch.Value, @"src\s*=\s*""([^""]+)""", RegexOptions.IgnoreCase);
            if (!srcMatch.Success) return null;

            var imgUrl = MakeAbsolute(srcMatch.Groups[1].Value.Trim(), "https://desk.3gbizhi.com");
            Logger.Log($"[爬虫] 3gbizhi 大图 URL: {imgUrl}，原图尺寸：{item.Resolution ?? "未知"}");
            return await SaveFileAsync(imgUrl, saveDir, item.Title, ct);
        }

        /// <summary>
        /// 3gbizhi 动态视频壁纸：列表页的 MP4 是低码率预览版。
        /// 下载时强制进入详情页重新解析 &lt;video id="contpic"&gt; 中的 MP4，
        /// 该版本文件更大、画质更好（实测同一壁纸详情版约为列表版的 1.5~2 倍）。
        /// </summary>
        private static async Task<string?> Download3gDTAsync(OnlineWallpaperItem item, string saveDir, CancellationToken ct)
        {
            Logger.Log("[爬虫] 3gbizhi-dt 解析详情页获取高清 MP4");
            var detail = await GetStringWithEncodingAsync(item.DetailUrl, ct);

            // 提取详情页标注的“原图尺寸”（如 3840×2160），用于下载后如实提示用户：
            // 免费下载的是 1280×720 预览版，4K 原图需登录积分。
            var resM = Regex.Match(detail, @"原图尺寸\s*(\d+[×x]\d+)", RegexOptions.IgnoreCase);
            if (!resM.Success) resM = Regex.Match(detail, @"<div class=""bz_size_show""[^>]*>([\d×x]+)", RegexOptions.IgnoreCase);
            if (!resM.Success) resM = Regex.Match(detail, @"尺寸[：:]\s*(\d+[×x]\d+)", RegexOptions.IgnoreCase);
            if (resM.Success) item.Resolution = resM.Groups[1].Value.Trim().Replace('x', '×');

            var m = Regex.Match(detail, @"<video[^>]+id=""contpic""[\s\S]*?<source src=""([^""]+\.mp4)""", RegexOptions.IgnoreCase);
            if (!m.Success)
                m = Regex.Match(detail, @"<source src=""([^""]+\.mp4)""", RegexOptions.IgnoreCase);
            if (!m.Success)
            {
                Logger.Log("[爬虫] 3gbizhi-dt 详情页未找到 MP4");
                return null;
            }
            var mp4Url = m.Groups[1].Value.Trim();
            mp4Url = MakeAbsolute(mp4Url, "https://desk.3gbizhi.com");
            Logger.Log($"[爬虫] 3gbizhi-dt 下载高清 MP4: {mp4Url}");
            return await SaveFileAsync(mp4Url, saveDir, item.Title, ct);
        }

        /// <summary>
        /// zhutix 动态壁纸：站点本身只提供 400x225 左右的 GIF 原图（无 mp4/高清直链，
        /// 详情页仅有优酷 iframe 或网盘）。这里直接下载该 GIF 原图作为可循环的动画壁纸。
        /// 若条目未带直链，则回退到详情页解析主图 GIF。
        /// </summary>
        private static async Task<string?> DownloadZhutixAsync(OnlineWallpaperItem item, string saveDir, CancellationToken ct)
        {
            string? mediaUrl = item.DownloadUrl;

            if (string.IsNullOrWhiteSpace(mediaUrl))
            {
                // 兜底：抓取详情页提取主图 GIF
                Logger.Log("[爬虫] zhutix 无直链，回退解析详情页");
                var detail = await GetStringWithEncodingAsync(item.DetailUrl, ct);
                var gifM = Regex.Match(detail,
                    @"(?i)(?:data-src|src)\s*=\s*[""']([^""']+\.gif)[""']",
                    RegexOptions.IgnoreCase | RegexOptions.Compiled);
                if (gifM.Success) mediaUrl = gifM.Groups[1].Value.Trim();
            }

            if (string.IsNullOrWhiteSpace(mediaUrl))
            {
                Logger.Log("[爬虫] zhutix 未找到可下载的 GIF 原图");
                return null;
            }

            mediaUrl = MakeAbsolute(mediaUrl, "https://zhutix.com/");
            Logger.Log($"[爬虫] zhutix 下载 GIF 原图: {mediaUrl}");
            return await SaveFileAsync(mediaUrl, saveDir, item.Title, ct);
        }

        /// <summary>
        /// livewallpapers4free 动态壁纸下载：详情页已解析出 /download/{id}/ 直链，
        /// 直接下载高清 MP4（优先 4K，其次 HD）。
        /// </summary>
        private static async Task<string?> DownloadLw4fAsync(OnlineWallpaperItem item, string saveDir, CancellationToken ct)
        {
            var downloadUrl = item.DownloadUrl;
            if (string.IsNullOrWhiteSpace(downloadUrl))
            {
                Logger.Log("[爬虫] lw4f 无下载直链，回退解析详情页");
                var detail = await GetStringWithEncodingAsync(item.DetailUrl, ct);
                var links = Regex.Matches(detail,
                    @"<a[^>]+href=""(https://livewallpapers4free\.com/download/(\d+)/)""[^>]*>([^<]+)</a>",
                    RegexOptions.IgnoreCase)
                    .Cast<Match>()
                    .Select(m => new
                    {
                        Url = m.Groups[1].Value.Trim(),
                        Text = WebUtility.HtmlDecode(m.Groups[3].Value.Trim())
                    })
                    .ToList();
                var chosen = links.FirstOrDefault(x => x.Text.Contains("4k", StringComparison.OrdinalIgnoreCase))
                    ?? links.FirstOrDefault(x => x.Text.Contains("HD", StringComparison.OrdinalIgnoreCase) || x.Text.Contains("2k", StringComparison.OrdinalIgnoreCase))
                    ?? links.FirstOrDefault();
                if (chosen == null)
                {
                    Logger.Log("[爬虫] lw4f 详情页未找到下载链接");
                    return null;
                }
                downloadUrl = chosen.Url;
            }

            Logger.Log($"[爬虫] lw4f 下载 MP4: {downloadUrl}");
            return await SaveFileAsync(downloadUrl, saveDir, item.Title, ct, ".mp4");
        }

        /// <summary>
        /// wallhaven 壁纸下载：优先使用 API 返回的 path 原图直链（w.wallhaven.cc/full/...），
        /// 无需再进详情页；列表页回退场景（DownloadUrl 为空）才解析详情页主图。
        /// 注意 wallhaven 单张下载有速率限制，失败时记录日志并返回 null 即可。
        /// </summary>
        private static async Task<string?> DownloadWallhavenAsync(OnlineWallpaperItem item, string saveDir, CancellationToken ct)
        {
            var url = item.DownloadUrl;

            // 1) 优先直链下载
            if (!string.IsNullOrWhiteSpace(url))
            {
                Logger.Log($"[爬虫] wallhaven 下载原图: {url}");
                var path = await SaveFileAsync(url, saveDir, item.Title, ct);
                if (!string.IsNullOrEmpty(path)) return path;
                // 直链失败（常见于 CDN 限流），继续走详情页回退
                Logger.Log("[爬虫] wallhaven 直链下载失败，尝试详情页回退");
            }

            // 2) 直链缺失或失败：进详情页解析 id="wallpaper" 原图再试一次。
            //    详情页与直链走同一 CDN，但 URL 路径不同，可绕过部分按路径限流的限制。
            try
            {
                Logger.Log($"[爬虫] wallhaven 回退解析详情页: {item.DetailUrl}");
                var detail = await GetStringWithEncodingAsync(item.DetailUrl, ct);
                var m = Regex.Match(detail, @"<img[^>]*\bid=""wallpaper""[^>]*src=""([^""]+)""", RegexOptions.IgnoreCase);
                if (!m.Success)
                    m = Regex.Match(detail, @"id=""wallpaper""[^>]*src=""([^""]+)""", RegexOptions.IgnoreCase);
                if (!m.Success)
                {
                    Logger.Log("[爬虫] wallhaven 详情页未找到原图");
                    return null;
                }
                var detailUrl = MakeAbsolute(m.Groups[1].Value.Trim(), "https://wallhaven.cc/");
                // 与已失败的直链完全相同则不再重复尝试
                if (!string.IsNullOrWhiteSpace(url) && string.Equals(detailUrl, url, StringComparison.OrdinalIgnoreCase))
                {
                    Logger.Log("[爬虫] wallhaven 详情页原图与直链一致，放弃重试");
                    return null;
                }
                Logger.Log($"[爬虫] wallhaven 回退下载详情页原图: {detailUrl}");
                return await SaveFileAsync(detailUrl, saveDir, item.Title, ct);
            }
            catch (Exception ex)
            {
                Logger.Log($"[爬虫] wallhaven 详情页回退失败: {ex.Message}");
                return null;
            }
        }

        /// <summary>把缩略图下载到本地缓存（规避图床 hotlink 限制），失败返回 null（回退远程 URL）。</summary>
        private static async Task<string?> DownloadThumbAsync(string url, CancellationToken ct)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(url)) return null;
                Directory.CreateDirectory(ThumbCacheDir);

                var uri = new Uri(url);
                var ext = Path.GetExtension(uri.AbsolutePath);
                if (string.IsNullOrEmpty(ext) || ext.Length > 5) ext = ".webp";

                var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url))).Substring(0, 16).ToLowerInvariant();
                var path = Path.Combine(ThumbCacheDir, hash + ext);

                if (File.Exists(path) && new FileInfo(path).Length > 0)
                    return path;

                // 3gbizhi 图床偶发限流/临时拦截，重试一次以提升缩略图本地化成功率，
                // 避免回退到远程 webp（WPF 直接加载远程 webp 易卡 1×1 导致灰底）。
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    try
                    {
                        var (bytes, _) = await GetBytesWithRedirectAsync(url, ct);
                        if (bytes.Length == 0) continue;
                        await File.WriteAllBytesAsync(path, bytes, ct);
                        return path;
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"[爬虫] 缩略图下载失败(第{attempt + 1}次): {ex.Message}");
                        if (attempt == 0) await Task.Delay(400, ct);
                    }
                }
                return null;
            }
            catch (Exception ex)
            {
                Logger.Log($"[爬虫] 缩略图下载失败(回退远程): {ex.Message}");
                return null;
            }
        }

        private static async Task<string> GetStringWithEncodingAsync(string url, CancellationToken ct)
        {
            var (bytes, headerCharset) = await GetBytesWithRedirectAsync(url, ct);

            // 优先使用 HTTP 响应头里声明的字符集
            string? name = null;
            if (!string.IsNullOrWhiteSpace(headerCharset))
                name = headerCharset.Trim().Trim('"', '\'').ToLowerInvariant();

            // 否则用 Latin1（ISO-8859-1）探测前 4KB 中的 meta charset。
            // Latin1 是 1:1 字节映射，不会破坏 GBK 中文字节，因此即使 meta 标签前
            // 已经有中文内容，被探测后再按 GBK 解码整段字节仍能得到正确标题。
            if (string.IsNullOrWhiteSpace(name))
            {
                var probeLength = Math.Min(bytes.Length, 4096);
                var probe = Encoding.Latin1.GetString(bytes, 0, probeLength);
                // 修复原正则无法匹配 <meta charset="gbk"/> 的引号问题
                var match = Regex.Match(probe, @"charset\s*=\s*[""']?([^""'\s>]+)[""']?", RegexOptions.IgnoreCase);
                name = match.Success ? match.Groups[1].Value.Trim('"', '\'', ' ').ToLowerInvariant() : null;

                // 兜底：常见站点已知编码
                if (string.IsNullOrWhiteSpace(name))
                {
                    if (url.Contains("netbian.com", StringComparison.OrdinalIgnoreCase))
                        name = "gbk";
                    else if (url.Contains("3gbizhi.com", StringComparison.OrdinalIgnoreCase))
                        name = "utf-8";
                    else
                        name = "utf-8";
                }
            }

            try
            {
                if (name.Contains("gbk") || name.Contains("gb2312"))
                    return Encoding.GetEncoding("gbk").GetString(bytes);
                if (name.Contains("utf-8") || name.Contains("utf8"))
                    return Encoding.UTF8.GetString(bytes);
                return Encoding.GetEncoding(name).GetString(bytes);
            }
            catch
            {
                return Encoding.UTF8.GetString(bytes);
            }
        }

        /// <summary>下载字节并返回 HTTP 响应声明的字符集（若存在），用于正确解码文本页面。</summary>
        private static async Task<(byte[] bytes, string? charset)> GetBytesWithRedirectAsync(string url, CancellationToken ct)
        {
            // 网络抖动：wallhaven 等海外站点经代理访问时 TLS 握手会被间歇性重置（SSL EOF），
            // 一次失败直接透传会让用户看到"下载失败"，这里对连接类异常自动重试 3 次（间隔递增）。
            Exception? lastError = null;
            HttpResponseMessage? lastResp = null;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                if (attempt > 0)
                {
                    Logger.Log($"[爬虫] 请求失败，第 {attempt + 1} 次重试: {url} -> {lastError?.Message}");
                    await Task.Delay(600 * attempt, ct);
                }
                try
                {
                    // HttpClient 的自动重定向对 307/308 在某些情况下不保留方法/header，这里做手动兜底。
                    for (int i = 0; i < 10; i++)
                    {
                        using var req = new HttpRequestMessage(HttpMethod.Get, url);
                        // 3gbizhi 图床对 Referer 较敏感，带上报头规避 hotlink 限制
                        if (url.Contains("3gbizhi.com", StringComparison.OrdinalIgnoreCase))
                            req.Headers.Referrer = new Uri("https://desk.3gbizhi.com/");
                        // zhutix 图床（dl.zhutix.net）同理
                        if (url.Contains("zhutix", StringComparison.OrdinalIgnoreCase))
                            req.Headers.Referrer = new Uri("https://zhutix.com/");
                        // wallhaven 原图/缩略图 CDN（w.wallhaven.cc）对 Referer 敏感
                        if (url.Contains("wallhaven.cc", StringComparison.OrdinalIgnoreCase))
                            req.Headers.Referrer = new Uri("https://wallhaven.cc/");

                        using var resp = await Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                        lastResp = resp;

                        if ((int)resp.StatusCode is 307 or 308)
                        {
                            var location = resp.Headers.Location;
                            if (location == null) throw new HttpRequestException($"服务器返回 {(int)resp.StatusCode} 但没有 Location 头");
                            url = location.IsAbsoluteUri ? location.ToString() : new Uri(new Uri(url), location).ToString();
                            Logger.Log($"[爬虫] 跟随 {(int)resp.StatusCode} 重定向到: {url}");
                            continue;
                        }

                        resp.EnsureSuccessStatusCode();
                        var charset = resp.Content.Headers.ContentType?.CharSet;
                        var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
                        return (bytes, charset);
                    }
                    throw new HttpRequestException("重定向次数过多");
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (HttpRequestException hex) when (hex.StatusCode is (System.Net.HttpStatusCode)429 or System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.TooManyRequests)
                {
                    // wallhaven 等站点对单 IP 有严格速率限制（429/403），需按 Retry-After 退避后重试，
                    // 否则短间隔重试只会持续被限流，导致下载/列表"假失败"。
                    lastError = hex;
                    if (attempt < 2)
                    {
                        int waitSec = 5;
                        var retryAfter = lastResp?.Headers?.RetryAfter;
                        if (retryAfter != null)
                        {
                            if (retryAfter.Delta.HasValue) waitSec = (int)Math.Ceiling(retryAfter.Delta.Value.TotalSeconds);
                            else if (retryAfter.Date.HasValue) waitSec = (int)Math.Max(0, (retryAfter.Date.Value - DateTimeOffset.UtcNow).TotalSeconds);
                        }
                        Logger.Log($"[爬虫] 速率限制({(int)hex.StatusCode})，等待 {waitSec}s 后重试: {url}");
                        await Task.Delay(waitSec * 1000, ct);
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex;
                }
            }
            throw lastError ?? new HttpRequestException("请求失败");
        }

        private static async Task<string?> SaveFileAsync(string url, string saveDir, string title, CancellationToken ct, string? forceExt = null)
        {
            try
            {
                var uri = new Uri(url);
                var ext = forceExt ?? Path.GetExtension(uri.AbsolutePath);
                if (string.IsNullOrEmpty(ext)) ext = ".jpg";

                var safeTitle = SanitizeFileName(title);
                Logger.Log($"[爬虫] 文件名清理: 原始='{title}' -> 安全='{safeTitle}'");

                // 避免同一标题重复 GUID 导致文件名冲突：先尝试无/短 GUID，若存在再加
                var baseName = Path.Combine(saveDir, $"{safeTitle}_{Guid.NewGuid().ToString("N")[..8]}{ext}");
                var path = baseName;
                int suffix = 1;
                while (File.Exists(path))
                {
                    path = Path.Combine(saveDir, $"{safeTitle}_{Guid.NewGuid().ToString("N")[..8]}_{suffix++}{ext}");
                }

                Logger.Log($"[爬虫] 开始下载: {url}");

                var (bytes, _) = await GetBytesWithRedirectAsync(url, ct);
                await File.WriteAllBytesAsync(path, bytes, ct);

                Logger.Log($"[爬虫] 已保存: {path} ({bytes.Length} bytes)");
                return path;
            }
            catch (Exception ex)
            {
                Logger.Log($"[爬虫] 下载失败: {ex.Message}");
                return null;
            }
        }

        /// <summary>清理标题中的非法/乱码字符，使其可作为文件名。</summary>
        private static string SanitizeFileName(string title)
        {
            if (string.IsNullOrWhiteSpace(title)) return "wallpaper";

            // 有些站点把中文标题做了 URL 编码（如 %E7%BE%8E%E5%A5%B3）
            title = WebUtility.UrlDecode(title);
            // HTML 实体（如 &amp; &quot;）
            title = WebUtility.HtmlDecode(title);

            var invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(title.Length);
            foreach (char c in title)
            {
                // 替换非法文件名字符、控制字符、Unicode 替代字符（�）以及非 BMP 乱码占位
                if (invalid.Contains(c) || char.IsControl(c) || c == '\uFFFD' || c == '\uFFFE' || c == '\uFFFF')
                    sb.Append('_');
                else
                    sb.Append(c);
            }

            var safe = sb.ToString().Trim();
            // 把连续下划线合并为单个，避免标题被乱码字符占满后只剩一排下划线
            safe = Regex.Replace(safe, @"_+", "_").Trim('_');
            if (string.IsNullOrWhiteSpace(safe)) safe = "wallpaper";
            if (safe.Length > 40) safe = safe[..40];
            return safe;
        }

        private static string MakeAbsolute(string url, string baseUrl)
        {
            url = url.Trim();
            if (url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return url;
            if (url.StartsWith("//")) return "https:" + url;
            var baseUri = new Uri(baseUrl);
            return new Uri(baseUri, url).ToString();
        }
    }
}
