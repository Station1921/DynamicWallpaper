using System;
using System.Drawing;
using System.Net.NetworkInformation;
using System.Threading;
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
        private bool _lastNavFailed = true;   // 初始按“可能失败”处理，便于首次恢复
        private Timer? _retryTimer;

        public WallpaperType Type => WallpaperType.Web;
        public IntPtr Handle => _window == null ? IntPtr.Zero : new WindowInteropHelper(_window).EnsureHandle();

        public void Show(string path, Rectangle bounds)
        {
            _path = path;
            _isRemote = IsRemote(path);
            _window = new RenderWindow();
            _web = new WebView2 { Margin = new System.Windows.Thickness(0) };
            _window.RootGrid.Children.Add(_web);
            _window.Left = bounds.Left;
            _window.Top = bounds.Top;
            _window.Width = bounds.Width;
            _window.Height = bounds.Height;
            _window.Show();

            _web.CoreWebView2InitializationCompleted += OnCoreInit;
            _web.Source = ToUri(path);

            if (_isRemote)
            {
                // 监听系统网络/代理变化：恢复后自动重试加载
                NetworkChange.NetworkAddressChanged += OnNetworkChanged;
                NetworkChange.NetworkAvailabilityChanged += OnAvailabilityChanged;
                _retryTimer = new Timer(_ => ReloadOnUiThread(), null, Timeout.Infinite, Timeout.Infinite);
            }
        }

        private void OnCoreInit(object? sender, CoreWebView2InitializationCompletedEventArgs e)
        {
            if (_web?.CoreWebView2 != null)
            {
                _web.CoreWebView2.NavigationCompleted += OnNavCompleted;
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
                    if (web.CoreWebView2 != null) web.CoreWebView2.Reload();
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
