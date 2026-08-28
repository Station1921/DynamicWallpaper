using System;
using System.IO;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Windows.Interop;
using System.Windows.Threading;
using DynamicWallpaper.Desktop;
using DynamicWallpaper.Models;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace DynamicWallpaper.Providers
{
    /// <summary>
    /// 网页 / 3D 壁纸：基于 WebView2（Chromium），支持 HTML/CSS/Canvas/WebGL 动画。
    /// 通过反射由主程序延迟加载；本程序集需与 DynamicWallpaper.exe 放在同一目录，
    /// 且目标机已安装 WebView2 运行环境（Win10/11 通常自带）。
    ///
    /// 网络/代理恢复：若远程壁纸因无代理而加载失败，开启代理后会通过
    /// NetworkChange 事件自动重试一次，无需手动重设。
    /// </summary>
    public class WebProvider : IWallpaperProvider
    {
        private RenderWindow? _window;
        private WebView2? _web;
        private string _path = "";
        private bool _isRemote;
        private bool _isM3u8;
        private bool _lastNavFailed = true;   // 初始按“可能失败”处理，便于首次恢复
        private System.Threading.Timer? _retryTimer;

        /// <summary>hls.js 内嵌资源（一次性读取并缓存），用于远程 m3u8 流式播放。</summary>
        private static readonly object _hlsLock = new();
        private static string? _hlsJsCache;

        private static string GetHlsJs()
        {
            if (_hlsJsCache != null) return _hlsJsCache;
            lock (_hlsLock)
            {
                if (_hlsJsCache != null) return _hlsJsCache;
                try
                {
                    var asm = typeof(WebProvider).Assembly;
                    using var s = asm.GetManifestResourceStream("hls.min.js");
                    if (s != null)
                    {
                        using var r = new StreamReader(s);
                        _hlsJsCache = r.ReadToEnd();
                        return _hlsJsCache;
                    }
                }
                catch { /* 读取失败兜底为空，避免反复重试 */ }
                _hlsJsCache = "";
                return _hlsJsCache;
            }
        }

        /// <summary>判断是否为远程 m3u8 流（HLS），需走 hls.js 封装而非直接导航。</summary>
        private static bool IsM3u8(string path)
        {
            if (!IsRemote(path)) return false;
            var p = path.Split('?')[0];
            return p.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>构造 hls.js 播放页：内联 hls.min.js，把 m3u8 地址安全地作为 JS 字符串字面量注入。</summary>
        private string BuildHlsHtml()
        {
            var urlLiteral = JsonSerializer.Serialize(_path); // 自动转义，避免地址中的引号/特殊字符破坏脚本
            var hls = GetHlsJs();
            // 整个 HTML 为单个插值逐字字符串：字面花括号必须用 {{ }} 转义，仅 {hls}/{urlLiteral} 为插值。
            return
$@"<html><head><meta charset=""utf-8""><style>html,body{{margin:0;padding:0;overflow:hidden;background:#000}}video{{position:fixed;inset:0;width:100vw;height:100vh;object-fit:cover;background:#000}}</style></head><body><video id=""v"" autoplay muted loop playsinline></video><script>{hls}</script><script>(function(){{var v=document.getElementById('v');var url={urlLiteral};function play(){{v.play().catch(function(){{}});}}if(window.Hls&&Hls.isSupported()){{var h=new Hls({{liveSyncDurationCount:3,lowLatencyMode:true}});h.loadSource(url);h.attachMedia(v);h.on(Hls.Events.MANIFEST_PARSED,play);h.on(Hls.Events.ERROR,function(e,d){{if(d&&d.fatal){{try{{h.destroy();}}catch(_){{}}}}}});}}else if(v.canPlayType('application/vnd.apple.mpegurl')){{v.src=url;v.addEventListener('loadedmetadata',play);}}else{{console.log('HLS not supported');}}}})();</script></body></html>";
        }

        public WallpaperType Type => WallpaperType.Web;
        public IntPtr Handle => _window == null ? IntPtr.Zero : new WindowInteropHelper(_window).EnsureHandle();

        public void Show(string path, Rectangle bounds)
        {
            _path = path;
            _isRemote = IsRemote(path);
            _isM3u8 = IsM3u8(path);
            _window = new RenderWindow();
            _web = new WebView2 { Margin = new System.Windows.Thickness(0) };
            _window.RootGrid.Children.Add(_web);
            _window.Left = bounds.Left;
            _window.Top = bounds.Top;
            _window.Width = bounds.Width;
            _window.Height = bounds.Height;
            _window.Show();

            _web.CoreWebView2InitializationCompleted += OnCoreInit;
            // 远程 m3u8：先用 about:blank 触发 WebView2 初始化，待 Core 就绪后再注入 hls.js 播放页；
            // 其它远程/本地网页仍走原 Source 导航。
            _web.Source = _isM3u8 ? new Uri("about:blank") : ToUri(path);

            if (_isRemote)
            {
                // 监听系统网络/代理变化：恢复后自动重试加载
                NetworkChange.NetworkAddressChanged += OnNetworkChanged;
                NetworkChange.NetworkAvailabilityChanged += OnAvailabilityChanged;
                _retryTimer = new System.Threading.Timer(_ => ReloadOnUiThread(), null, Timeout.Infinite, Timeout.Infinite);
            }
        }

        private void OnCoreInit(object? sender, CoreWebView2InitializationCompletedEventArgs e)
        {
            if (_web?.CoreWebView2 != null)
            {
                _web.CoreWebView2.NavigationCompleted += OnNavCompleted;
                // 远程 m3u8：Core 就绪后用 hls.js 封装页流式播放（hls.js 自带错误恢复/重连）。
                if (_isM3u8)
                {
                    try { _web.CoreWebView2.NavigateToString(BuildHlsHtml()); }
                    catch { /* 注入失败忽略，网络恢复时会重试 */ }
                }
            }
        }

        private void OnNavCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            _lastNavFailed = !e.IsSuccess;
        }

        private static bool IsRemote(string path)
        {
            return Uri.TryCreate(path, UriKind.Absolute, out var u) &&
                   (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);
        }

        private static Uri ToUri(string path)
        {
            if (IsRemote(path)) return new Uri(path);
            // 本地 html 文件：WebView2 要求 file:/// 形式
            return new Uri("file:///" + path.Replace('\\', '/'));
        }

        private void OnNetworkChanged(object? sender, EventArgs e) => ScheduleRetry();
        private void OnAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e) => ScheduleRetry();

        private void ScheduleRetry()
        {
            // 网络事件可能连发，去抖后统一在 2 秒后重试
            _retryTimer?.Change(2000, Timeout.Infinite);
        }

        private void ReloadOnUiThread()
        {
            var web = _web;
            if (web == null) return;
            // NetworkChange 回调在后台线程，必须回到 UI 线程操作 WebView2
            web.Dispatcher.Invoke(() =>
            {
                if (web == null || !_isRemote || !_lastNavFailed) return;
                try
                {
                    if (web.CoreWebView2 != null)
                    {
                        if (_isM3u8)
                            web.CoreWebView2.NavigateToString(BuildHlsHtml()); // 重新注入 hls.js 播放页
                        else
                            web.CoreWebView2.Reload();
                    }
                    else web.Source = ToUri(_path);
                }
                catch { /* 忽略单次刷新失败，下次网络变化再试 */ }
            });
        }

        public void AttachTo(IntPtr workerw, Rectangle bounds) =>
            WorkerWInjector.Attach(Handle, workerw, bounds);

        public void Play() { }
        public void Pause() { }
        public void SetMuted(bool muted) { }

        public void Dispose()
        {
            try
            {
                if (_isRemote)
                {
                    NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
                    NetworkChange.NetworkAvailabilityChanged -= OnAvailabilityChanged;
                }
                _retryTimer?.Dispose();
                _retryTimer = null;
            }
            catch { }
            try { _web?.Dispose(); } catch { }
            _window?.Close();
            _window = null;
        }
    }
}
