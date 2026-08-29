using System;
using System.Drawing;
using System.IO;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Net.Http;
using System.Net;
using System.Windows.Threading;
using DynamicWallpaper.Core;
using DynamicWallpaper.Desktop;
using DynamicWallpaper.Models;
using Microsoft.Web.WebView2.Core;

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
        #region Win32（原生层窗口：WS_EX_LAYERED 必须在创建时携带，动态设置无效；
        /// 与 VideoProvider 相同的「原生挂载窗口 + WebView2」结构才能被 DWM 真实合成到 Win11 壁纸层）

        private const int WS_EX_LAYERED = 0x00080000;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int WS_EX_TOOLWINDOW = 0x00000080;

        private const int ERROR_CLASS_ALREADY_EXISTS = 1410;

        private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        // static readonly 保活：防止委托被 GC 回收导致 WndProc 失效（AccessViolation）。
        private static readonly WndProcDelegate WndProcHandler = (h, m, w, l) => DefWindowProc(h, m, w, l);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct WNDCLASS
        {
            public uint style;
            public IntPtr lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            public string? lpszMenuName;
            public string? lpszClassName;
        }

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern ushort RegisterClass(ref WNDCLASS lpWndClass);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr CreateWindowEx(int dwExStyle, string lpClassName, string lpWindowName,
            int dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

        [DllImport("user32.dll")]
        private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr GetModuleHandle(string? name);

        private static readonly IntPtr HInstance = GetModuleHandle(null);
        private static readonly object ClassLock = new();
        private static int _classRegistered;

        private static void EnsureWindowClass()
        {
            if (Volatile.Read(ref _classRegistered) != 0) return;
            lock (ClassLock)
            {
                if (_classRegistered != 0) return;
                var wc = new WNDCLASS
                {
                    style = 0,
                    lpfnWndProc = Marshal.GetFunctionPointerForDelegate(WndProcHandler),
                    hInstance = HInstance,
                    lpszClassName = "DynamicWallpaperWebHost"
                };
                if (RegisterClass(ref wc) == 0 && Marshal.GetLastWin32Error() != ERROR_CLASS_ALREADY_EXISTS)
                    Logger.Log($"[WebProvider] 注册窗口类失败: {Marshal.GetLastWin32Error()}");
                else
                    _classRegistered = 1;
            }
        }

        #endregion

        private IntPtr _hwnd;
        private CoreWebView2Controller? _controller;
        private CoreWebView2? _core;
        /// <summary>创建 WebView2 Controller 的 UI 线程 Dispatcher。WebView2 COM 对象线程亲和，
        /// 跨线程调用 CoreWebView2/Controller 会抛 "Unable to cast COM object ... ICoreWebView2Controller"。</summary>
        private System.Windows.Threading.Dispatcher? _uiDispatcher;
        private string _path = "";
        private bool _isRemote;
        private bool _isM3u8;
        private bool _lastNavFailed = true;   // 初始按“可能失败”处理，便于首次恢复
        private System.Threading.Timer? _retryTimer;

        /// <summary>WebView2 Core 初始化完成信号（成功=已就绪且 CoreWebView2 非 null）。</summary>
        private readonly TaskCompletionSource<bool> _initTcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>页面导航完成信号（成功=hls 页/网页已加载，不再是空白 about:blank）。</summary>
        private readonly TaskCompletionSource<bool> _navTcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>m3u8 真正可播放信号：hls.js MANIFEST_PARSED 或 video loadedmetadata。
        /// 避免“页面已加载但视频未缓冲”导致黑屏/白屏被直接显示。</summary>
        private readonly TaskCompletionSource<bool> _contentTcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        // m3u8 流式代理（本地 HttpListener 按需转发远程流，规避 WebView2 透明源跨域拉流失败导致黑屏）
        private HlsProxy? _proxy;
        private bool _coreReady;
        private bool _navigatedLocal;

        /// <summary>壁纸适应方式：fill=铺满裁剪 / fit=完整显示 / center=原始居中。由 WallpaperManager 在切换时注入。</summary>
        public static string FitMode { get; set; } = "fill";

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
$@"<html><head><meta charset=""utf-8""><style>html,body{{margin:0;padding:0;overflow:hidden;background:#000}}video{{position:fixed;inset:0;width:100vw;height:100vh;object-fit:cover;background:#000}}</style></head><body><video id=""v"" autoplay muted loop playsinline></video><script>{hls}</script><script>(function(){{function showError(msg){{var p=document.createElement('div');p.style.cssText='position:fixed;inset:0;display:flex;align-items:center;justify-content:center;color:#fff;background:rgba(0,0,0,.85);font:13px sans-serif;text-align:center;padding:24px;z-index:9';p.textContent='HLS 加载失败：'+msg;document.body.appendChild(p);}}var v=document.getElementById('v');var url={urlLiteral};function play(){{v.play().catch(function(){{}});}}if(window.Hls&&Hls.isSupported()){{var h=new Hls({{liveSyncDurationCount:3,lowLatencyMode:true}});h.loadSource(url);h.attachMedia(v);h.on(Hls.Events.MANIFEST_PARSED,function(){{play();try{{if(window.chrome&&chrome.webview)chrome.webview.postMessage('hls:ready');}}catch(_){{}}}});h.on(Hls.Events.ERROR,function(e,d){{if(d&&d.fatal){{try{{h.destroy();}}catch(_){{}}showError((d.type||'')+' / '+(d.details||''));try{{if(window.chrome&&chrome.webview)chrome.webview.postMessage('hls:error');}}catch(_){{}}}}}});}}else if(v.canPlayType('application/vnd.apple.mpegurl')){{v.src=url;v.addEventListener('loadedmetadata',function(){{play();try{{if(window.chrome&&chrome.webview)chrome.webview.postMessage('video:ready');}}catch(_){{}}}});}}else{{showError('当前内核不支持 HLS（hls.js 未加载或 WebView2 环境异常）');try{{if(window.chrome&&chrome.webview)chrome.webview.postMessage('hls:error');}}catch(_){{}}}})();</script></body></html>";
        }

        public WallpaperType Type => WallpaperType.Web;
        public IntPtr Handle => _hwnd;

        /// <summary>内容是否已真正就绪（导航完成且 hls/页面内容可播放）。供 Crossfade 判断是否可叠化显示；
        /// 网络加载挂起时保持 false，WallpaperManager 会回滚保持旧壁纸而不僵持。</summary>
        public bool IsContentReady
        {
            get
            {
                if (_isM3u8)
                    return _contentTcs.Task.IsCompletedSuccessfully && _contentTcs.Task.Result;
                return _navTcs.Task.IsCompletedSuccessfully && _navTcs.Task.Result;
            }
        }

        public void Show(string path, Rectangle bounds)
        {
            _path = path;
            _isRemote = IsRemote(path);
            _isM3u8 = IsM3u8(path);

            // 原生 WS_EX_LAYERED 窗口（必须在创建时携带，动态设置无效）：WPF 窗口挂到
            // Win11 raised desktop 后 redirection surface 不被 DWM 合成，桌面永远无画面；
            // 原生层窗口 + CoreWebView2Controller 与 VideoProvider 相同的结构才能被 DWM 真实合成。
            EnsureWindowClass();
            _hwnd = CreateWindowEx(
                WS_EX_LAYERED | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW,
                "DynamicWallpaperWebHost", "DynamicWallpaper Web Host",
                0, bounds.X, bounds.Y, bounds.Width, bounds.Height,
                IntPtr.Zero, IntPtr.Zero, HInstance, IntPtr.Zero);
            if (_hwnd == IntPtr.Zero)
            {
                Logger.Log($"[WebProvider] CreateWindowEx 失败: {Marshal.GetLastWin32Error()}");
                return;
            }

            // Layered 窗口默认 alpha=0（完全透明），必须置满不透明 DWM 才合成内容
            Win32.SetLayeredWindowAttributes(_hwnd, 0, 255, Win32.LWA_ALPHA);
            // 记录创建线程的 Dispatcher：WebView2 COM 对象线程亲和，后续调用需调度回该线程。
            _uiDispatcher = System.Windows.Application.Current?.Dispatcher;

            // 不在这里显示窗口（避免 WorkerW 获取失败时窗口在顶层闪现）；
            // 窗口会在 AttachTo 成功挂接到 WorkerW 后再 SetWindowPos 显示。
            _ = InitWebView2Async(bounds);

            if (_isM3u8)
            {
                // 流式代理：本地 HttpListener 把 m3u8 流按需转发到远程（不再下载全部分段到本地）。
                // 浏览器能秒开而旧镜像方案"加载很长时间"的根因：HlsMirror 顺序下载 manifest 中
                // 全部分段（LIVE 流分段持续增长，永远下载不完），下载完才导航播放。
                // 代理方案只需一次 manifest 请求即可开始播放，LIVE/VOD 通吃，可持续播放。
                try
                {
                    _proxy = new HlsProxy(_path, GetHlsJs(), _muted);
                    Logger.Log($"[WebProvider] m3u8 流式代理已启动: {_proxy.BaseUrl}");
                }
                catch (Exception ex)
                {
                    Logger.Log($"[WebProvider] m3u8 流式代理启动失败: {ex.Message}");
                    _navTcs.TrySetResult(false);
                    _contentTcs.TrySetResult(false);
                }
            }

            if (_isRemote)
            {
                // 监听系统网络/代理变化：恢复后自动重试加载
                NetworkChange.NetworkAddressChanged += OnNetworkChanged;
                NetworkChange.NetworkAvailabilityChanged += OnAvailabilityChanged;
                _retryTimer = new System.Threading.Timer(_ => ReloadOnUiThread(), null, Timeout.Infinite, Timeout.Infinite);
            }
        }

        /// <summary>创建 CoreWebView2Controller 并绑定事件、导航（与 VideoProvider 相同的初始化路径，
        /// 含 0x8007139F 资源竞争重试）。Controller 创建成功后才置 _initTcs 完成。</summary>
        private async Task InitWebView2Async(Rectangle bounds)
        {
            try
            {
                var env = await VideoProvider.EnvironmentLazy.Value;
                if (_hwnd == IntPtr.Zero) return;

                // 创建 Controller 增加失败重试：动态壁纸 A→B 无缝切换瞬间，旧壁纸 A 的
                // WebView2 Controller 可能尚未完全释放，同一 Environment 上创建 Controller
                // 会短暂失败（0x8007139F）。6 次渐进退避，与 VideoProvider 保持一致。
                const int maxAttempts = 6;
                int[] backoffMs = { 300, 500, 800, 1200, 1800 };
                CoreWebView2Controller ctrl = null!;
                for (int attempt = 1; attempt <= maxAttempts; attempt++)
                {
                    try
                    {
                        ctrl = await env.CreateCoreWebView2ControllerAsync(_hwnd);
                        break;
                    }
                    catch (Exception ex)
                    {
                        if (attempt >= maxAttempts) throw;
                        int delay = backoffMs[attempt - 1];
                        Logger.Log($"[WebProvider] CreateCoreWebView2ControllerAsync 失败（第{attempt}次/共{maxAttempts}次），{delay}ms 后重试: {ex.Message}");
                        if (_hwnd == IntPtr.Zero) return;
                        await Task.Delay(delay);
                    }
                }

                ctrl.Bounds = new Rectangle(0, 0, bounds.Width, bounds.Height);
                // WebView2 默认页面背景为白色，页面/hls 尚未渲染时会出现“白屏”闪动；
                // 设为黑色与播放页黑场一致，叠化前保持透明不可见。
                ctrl.DefaultBackgroundColor = System.Drawing.Color.Black;
                ctrl.IsVisible = true;
                var core = ctrl.CoreWebView2;
                try { core.Settings.IsWebMessageEnabled = true; } catch { }
                core.NavigationCompleted += OnNavCompleted;
                core.WebMessageReceived += OnWebMessage;
                _controller = ctrl;
                _core = core;

                _initTcs.TrySetResult(true);

                if (_isM3u8)
                {
                    _coreReady = true;
                    TryNavigateM3u8(); // 流式代理已在 Show 阶段启动，Core 就绪后直接导航播放页
                }
                else
                {
                    // 其它网页/本地文件统一在 Core 就绪后导航
                    try { core.Navigate(_path); }
                    catch (Exception ex) { Logger.Log($"[WebProvider] 导航失败: {ex.Message}"); }
                }
                Logger.Log($"[WebProvider] WebView2 初始化完成: {_path}");
            }
            catch (Exception ex)
            {
                Logger.Log($"[WebProvider] WebView2 初始化失败: {ex.Message}");
                _initTcs.TrySetResult(false);
                _navTcs.TrySetResult(false);
                _contentTcs.TrySetResult(false);
            }
        }

        private void OnNavCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            _lastNavFailed = !e.IsSuccess;
            Logger.Log($"[WebProvider] 导航完成: 成功={e.IsSuccess}, 路径={_path}");
            _navTcs.TrySetResult(e.IsSuccess);
            // 非 m3u8 页面：导航完成即视为内容就绪；m3u8 需等待 hls.js 的 MANIFEST_PARSED/loadedmetadata
            if (!_isM3u8) _contentTcs.TrySetResult(e.IsSuccess);
            // 按最新静音状态重放一次（覆盖导航前 SetMuted 因 Controller 未就绪被丢弃的情况）
            RunJs($"var v=document.getElementById('v');if(v){{v.muted={_muted.ToString().ToLowerInvariant()};v.volume={(_muted ? 0 : 1)};}}");
        }

        private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                var msg = e.TryGetWebMessageAsString();
                Logger.Log($"[WebProvider] JS 消息: {msg}, 路径={_path}");
                if (msg == "hls:ready" || msg == "video:ready")
                    _contentTcs.TrySetResult(true);
                else if (msg == "hls:error" || msg == "video:error")
                    _contentTcs.TrySetResult(false);
            }
            catch (Exception ex)
            {
                Logger.Log($"[WebProvider] 解析 JS 消息异常: {ex.Message}");
            }
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
            var core = _core;
            if (core == null || !_isRemote || !_lastNavFailed) return;
            // NetworkChange 回调在后台线程，调度回创建线程操作 WebView2 COM 对象
            var disp = _uiDispatcher;
            if (disp == null || disp.CheckAccess())
            {
                DoReload(core);
                return;
            }
            disp.Invoke(() => DoReload(core));
        }

        private void DoReload(CoreWebView2 core)
        {
            try
            {
                if (_isM3u8)
                {
                    // 重新导航到流式代理播放页，让 hls.js 重新拉取最新 manifest（网络恢复/切换后刷新）
                    _navigatedLocal = false;
                    if (_proxy != null)
                    {
                        try { core.Navigate(_proxy.BaseUrl + "player.html"); } catch { }
                    }
                }
                else
                    core.Reload();
            }
            catch { /* 忽略单次刷新失败，下次网络变化再试 */ }
        }

        public void AttachTo(IntPtr workerw, Rectangle bounds)
        {
            if (_hwnd == IntPtr.Zero) return;
            WorkerWInjector.Attach(_hwnd, workerw, bounds);
            // 挂接成功后再显示窗口，避免 WorkerW 获取失败时窗口在顶层闪现
            Win32.SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0,
                Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOZORDER
                | Win32.SWP_NOACTIVATE | Win32.SWP_SHOWWINDOW);
        }

        public void Play() { }
        public void Pause() { }

        /// <summary>当前期望的静音状态（默认 true，与播放页 video 标签 autoplay muted 一致）。
        /// 流式代理生成 player.html 与导航完成后重放都按此应用；Controller 未创建时早期
        /// SetMuted 调用会被静默丢弃，因此必须缓存该字段，不能只发 JS。</summary>
        private volatile bool _muted = true;

        public void SetMuted(bool muted)
        {
            // 先缓存期望状态，再下推 JS；导航完成后 OnNavCompleted 会按 _muted 重放一次，
            // 覆盖 Controller 尚未创建（早期调用被丢弃）的情况。
            _muted = muted;
            RunJs($"var v=document.getElementById('v');if(v){{v.muted={muted.ToString().ToLowerInvariant()};v.volume={(muted ? 0 : 1)};}}");
        }

        /// <summary>在 WebView2 创建线程执行 JS（COM 对象线程亲和，跨线程抛异常）。
        /// Controller 未就绪时静默丢弃——静音状态已由 _muted 缓存，导航完成后会重放。</summary>
        private void RunJs(string script)
        {
            var core = _core;
            if (core == null) return;
            var disp = _uiDispatcher;
            if (disp == null || disp.CheckAccess())
            {
                try { core.ExecuteScriptAsync(script); } catch { }
                return;
            }
            disp.Invoke(() => { try { core.ExecuteScriptAsync(script); } catch { } });
        }

        /// <summary>运行时切换 m3u8 壁纸适应方式：立即更新播放页 video 的 object-fit / object-position CSS。
        /// 仅对已导航到本地代理播放页的 m3u8 生效；普通网页壁纸整页铺满、不适用。</summary>
        public void ApplyFitMode()
        {
            if (_core == null || _proxy == null || !_navigatedLocal) return;
            var fit = string.IsNullOrWhiteSpace(FitMode) ? "fill" : FitMode.Trim().ToLowerInvariant();
            if (fit is not ("fill" or "fit" or "center")) fit = "fill";
            var (objectFit, objectPosition, bg) = fit switch
            {
                "fit" => ("contain", "50% 50%", "#000"),
                "center" => ("none", "center center", "transparent"),
                _ => ("cover", "50% 50%", "#000")
            };
            RunJs($"var v=document.getElementById('v');if(v){{v.style.objectFit='{objectFit}';v.style.objectPosition='{objectPosition}';v.style.background='{bg}';}}");
        }

        /// <summary>实现 IWallpaperProvider.WaitReadyAsync：等待 WebView2 Core 初始化 + 页面导航完成 + 内容可播放，
        /// 再让 WallpaperManager 开始叠化。避免“叠化期间 WebView2 还是空白/未缓冲，被直接显示成白屏/黑屏”。
        /// 超时或失败不抛异常，交由调用方继续显示（此时窗口仍为透明，不会闪屏）。
        /// 注意：timeout 为 InfiniteTimeSpan 时无限等待（网络加载慢时一直等、不自动回退），
        /// 仅由 cancellationToken 打断——用户切换/解除/退出时 WallpaperManager 取消令牌，
        /// 此处抛 OperationCanceledException 交由调用方回滚释放锁，绝不阻塞新操作。</summary>
        public async Task WaitReadyAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (timeout != Timeout.InfiniteTimeSpan) cts.CancelAfter(timeout);
            try
            {
                // 先等 Core 初始化（WebView2 环境异常时 _initTcs 可能永不完成）
                await WaitTask(_initTcs.Task, cts.Token);
                // 再等页面导航完成（网络壁纸加载挂起时 NavigationCompleted 永不触发）
                await WaitTask(_navTcs.Task, cts.Token);
                // m3u8 最后等 hls.js 解析完 manifest / video 触发 ready 消息
                if (_isM3u8)
                {
                    var content = await Task.WhenAny(_contentTcs.Task, Task.Delay(Timeout.Infinite, cts.Token));
                    if (content != _contentTcs.Task)
                        Logger.Log("[WebProvider] m3u8 内容未在超时内就绪——可能原因：签名过期(403)/跨域被拦/网络不可达/hls.js 拉流失败。请检查链接是否有效，或查看桌面红字错误提示与 app.log。");
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // 外部取消（用户切换/解除/退出打断）：向上传播，WallpaperManager 回滚并释放锁
                throw;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Logger.Log($"[WebProvider] WaitReadyAsync 异常（继续显示）: {ex.Message}"); }
        }

        /// <summary>带取消等待：token 取消即抛 OperationCanceledException，避免无限挂起。</summary>
        private static async Task WaitTask(Task t, CancellationToken token)
        {
            var done = await Task.WhenAny(t, Task.Delay(Timeout.Infinite, token));
            if (done != t) throw new OperationCanceledException(token);
            await t; // 传播 TCS 中的异常
        }

        private void TryNavigateM3u8()
        {
            if (!_coreReady || _core == null || _proxy == null || _navigatedLocal) return;
            try
            {
                _navigatedLocal = true;
                _core.Navigate(_proxy.BaseUrl + "player.html");
                Logger.Log("[WebProvider] 已导航至 m3u8 流式代理播放页（同源，规避跨域黑屏）");
            }
            catch (Exception ex)
            {
                Logger.Log($"[WebProvider] m3u8 播放页导航失败: {ex.Message}");
            }
        }

                /// <summary>
        /// m3u8 流式代理：本地 HttpListener 把远程 manifest/分段/key 按需转发给 WebView2 页面。
        /// 页面与流同源（http://127.0.0.1:port），hls.js 拉流无跨域限制；
        /// 只做实时转发、不下载任何分段，加载只需一次 manifest 请求（秒开），
        /// LIVE 流每次 manifest 请求都转发远程最新内容，可持续播放。
        /// </summary>
        private sealed class HlsProxy : IDisposable
        {
            private HttpListener? _listener;
            private readonly string _remoteUrl;
            private readonly string _baseDir;
            private readonly string _origin;
            private readonly string _hlsJs;
            private readonly bool _muted;
            private readonly CancellationTokenSource _cts = new();
            public string BaseUrl { get; private set; } = "";

            public HlsProxy(string remoteUrl, string hlsJs, bool muted)
            {
                _remoteUrl = remoteUrl;
                var u = new Uri(remoteUrl);
                _baseDir = u.AbsoluteUri.Substring(0, u.AbsoluteUri.LastIndexOf('/') + 1);
                _origin = u.GetLeftPart(UriPartial.Authority);
                _hlsJs = hlsJs;
                _muted = muted;
                // 找空闲端口（HttpListener 监听 127.0.0.1 无需管理员权限）。
                // 关键：Start() 失败后该 HttpListener 实例的前缀会残留，导致同一实例在后续
                // 端口上 Start 也永远失败（表现为“端口均被占用”，m3u8 切 m3u8 回退）。
                // 因此每个端口都新建实例，失败即整体丢弃。
                for (int port = 18080; port < 19000; port++)
                {
                    try
                    {
                        var l = new HttpListener();
                        l.Prefixes.Add($"http://127.0.0.1:{port}/");
                        l.Start();
                        _listener = l;
                        BaseUrl = $"http://127.0.0.1:{port}/";
                        break;
                    }
                    catch { }
                }
                if (_listener == null || BaseUrl.Length == 0)
                    throw new InvalidOperationException("无法启动本地代理（端口均被占用）");
                _ = Task.Run(ListenLoopAsync);
            }

            private async Task ListenLoopAsync()
            {
                while (!_cts.IsCancellationRequested)
                {
                    HttpListenerContext ctx;
                    try { ctx = await _listener.GetContextAsync(); }
                    catch { break; }
                    _ = Task.Run(() => HandleAsync(ctx));
                }
            }

            private async Task HandleAsync(HttpListenerContext ctx)
            {
                try
                {
                    var path = ctx.Request.Url?.AbsolutePath ?? "/";
                    if (path.EndsWith("player.html", StringComparison.OrdinalIgnoreCase))
                    {
                        WriteBytes(ctx, Encoding.UTF8.GetBytes(BuildLocalPlayerHtml(_hlsJs, _muted)), "text/html; charset=utf-8");
                        return;
                    }
                    if (path.EndsWith("index.m3u8", StringComparison.OrdinalIgnoreCase))
                    {
                        // manifest 每次实时转发远程最新内容（LIVE 流持续播放的关键）
                        var manifest = await FetchAsync(_remoteUrl, ctx);
                        if (manifest == null) { Fail(ctx); return; }
                        WriteBytes(ctx, manifest, "application/vnd.apple.mpegurl");
                        return;
                    }
                    // 其余路径（分段 / key）：
                    // 相对分段经 hls.js 基于代理 URL 解析后是 /56186e-m0.ts 这类纯文件名路径，
                    // 必须拼回远程 baseDir 目录下；而 manifest 中 /hc104/m3u8/enc.key 这类
                    // 根绝对路径的 AES-128 密钥则需拼到远程 origin。
                    // HttpListener 的 AbsolutePath 恒以 / 开头，无法凭前缀区分二者，
                    // 因此先按 baseDir 尝试（覆盖绝大多数相对分段），404 再回退 origin（覆盖根绝对密钥/分段）。
                    var q = ctx.Request.Url?.Query ?? "";
                    var target = _baseDir + path.TrimStart('/') + q;
                    var seg = await FetchAsync(target, ctx);
                    if (seg == null)
                    {
                        target = _origin + path + q;
                        seg = await FetchAsync(target, ctx);
                    }
                    if (seg == null)
                    {
                        Logger.Log($"[HlsProxy] 转发失败: {path} (baseDir与origin均失败)");
                        Fail(ctx);
                        return;
                    }
                    WriteBytes(ctx, seg, "application/octet-stream");
                }
                catch { Fail(ctx); }
            }

            private static void Fail(HttpListenerContext ctx)
            {
                try { ctx.Response.StatusCode = 502; ctx.Response.Close(); } catch { }
            }

            private async Task<byte[]?> FetchAsync(string url, HttpListenerContext ctx)
            {
                try
                {
                    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
                    try { http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36"); } catch { }
                    using var req = new HttpRequestMessage(HttpMethod.Get, url);
                    var range = ctx.Request.Headers["Range"];
                    if (!string.IsNullOrEmpty(range)) req.Headers.TryAddWithoutValidation("Range", range);
                    using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, _cts.Token);
                    if (!resp.IsSuccessStatusCode) return null;
                    return await resp.Content.ReadAsByteArrayAsync();
                }
                catch { return null; }
            }

            private static void WriteBytes(HttpListenerContext ctx, byte[] data, string contentType)
            {
                try
                {
                    ctx.Response.ContentType = contentType;
                    ctx.Response.ContentLength64 = data.Length;
                    ctx.Response.OutputStream.Write(data, 0, data.Length);
                    ctx.Response.Close();
                }
                catch { try { ctx.Response.Close(); } catch { } }
            }

            private static string BuildLocalPlayerHtml(string hlsJs, bool muted)
            {
                // video 标签保留 autoplay muted（保证浏览器自动播放策略放行），
                // play() 时再按 MUTED 应用真实静音状态（muted=false 时取消静音并恢复音量）。
                var mutedLiteral = muted ? "true" : "false";
                // 诊断浮层 #st 默认隐藏（保留 DOM 与 postMessage 上报，供 app.log 排查），
                // 仅 #err 错误浮层在加载失败时可见；body 注入 data-fit 供脚本应用壁纸适应方式。
                var head = @"<html><head><meta charset=""utf-8""><style>html,body{margin:0;padding:0;overflow:hidden;background:#000}video{position:fixed;inset:0;width:100vw;height:100vh;object-fit:cover;background:#000}#st{display:none}#err{position:fixed;inset:0;display:flex;align-items:center;justify-content:center;color:#fff;background:rgba(0,0,0,.85);font:14px sans-serif;text-align:center;padding:24px;z-index:9}</style></head><body data-fit=""fill""><video id=""v"" autoplay muted loop playsinline></video><div id=""st""></div><script>";
                var js = @"(function(){
  var MUTED=" + mutedLiteral + @";
  function status(msg){ try{ if(window.chrome&&chrome.webview) chrome.webview.postMessage('status:'+msg); }catch(_){} }
  window.onerror=function(m,s,l,c,e){ status('JS错误: '+m); return false; };
  function showError(msg){ var p=document.getElementById('err'); if(!p){ p=document.createElement('div'); p.id='err'; document.body.appendChild(p); } p.textContent='HLS 加载失败：'+msg; }
  var v=document.getElementById('v');
  (function(){ var fit=(document.body&&document.body.dataset)?(document.body.dataset.fit||'fill'):'fill'; if(fit!=='fill'){ if(fit==='fit'){ v.style.objectFit='contain'; v.style.background='#000'; } else if(fit==='center'){ v.style.objectFit='none'; v.style.objectPosition='center center'; v.style.background='transparent'; } } })();
  function play(){ v.muted=MUTED; v.volume=MUTED?0:1; v.play().catch(function(){}); }
  var url='./index.m3u8';
  v.addEventListener('playing',function(){ status('video playing'); });
  v.addEventListener('canplay',function(){ status('canplay'); });
  v.addEventListener('stalled',function(){ status('video stalled'); });
  v.addEventListener('error',function(){ status('video error: '+(v.error?(''+v.error.code+':'+v.error.message):'unknown')); });
  status('init Hls='+(window.Hls?'yes':'no'));
  if(window.Hls&&Hls.isSupported()){
    status('loadSource...');
    var h=new Hls({liveSyncDurationCount:3,lowLatencyMode:true});
    h.loadSource(url);
    h.attachMedia(v);
    h.on(Hls.Events.MANIFEST_LOADED,function(e,data){ status('manifest loaded levels='+(data&&data.levels?data.levels.length:'?')); });
    h.on(Hls.Events.MEDIA_ATTACHED,function(){ status('media attached'); });
    h.on(Hls.Events.FRAG_LOADED,function(){ status('frag loaded'); });
    h.on(Hls.Events.MANIFEST_PARSED,function(e,data){ status('manifest parsed -> play'); play(); try{ if(window.chrome&&chrome.webview) chrome.webview.postMessage('hls:ready'); }catch(_){} });
    h.on(Hls.Events.ERROR,function(e,d){ status('ERR '+(d?(d.type+'/'+d.details+' fatal='+d.fatal):'?')); if(d&&d.fatal){ try{ h.destroy(); }catch(_){} showError((d.type||'')+' / '+(d.details||'')); try{ if(window.chrome&&chrome.webview) chrome.webview.postMessage('hls:error'); }catch(_){} } });
  } else if(v.canPlayType('application/vnd.apple.mpegurl')){
    status('native HLS path');
    v.src=url;
    v.addEventListener('loadedmetadata',function(){ play(); try{ if(window.chrome&&chrome.webview) chrome.webview.postMessage('video:ready'); }catch(_){} });
  } else {
    status('HLS unsupported');
    showError('当前内核不支持 HLS（hls.js 未加载或 WebView2 环境异常）');
    try{ if(window.chrome&&chrome.webview) chrome.webview.postMessage('hls:error'); }catch(_){}
  }
})();";
                var tail = @"</script></body></html>";
                // 把 WallpaperManager 注入的适应方式写进 body data-fit（head 默认 fill，这里按实际配置覆盖）
                var fit = string.IsNullOrWhiteSpace(FitMode) ? "fill" : FitMode.Trim().ToLowerInvariant();
                if (fit is not ("fill" or "fit" or "center")) fit = "fill";
                head = head.Replace("<body data-fit=\"fill\">", $"<body data-fit=\"{fit}\">");
                return head + hlsJs + js + tail;
            }

            public void Dispose()
            {
                try { _cts.Cancel(); } catch { }
                try { _listener.Stop(); } catch { }
                try { _listener.Close(); } catch { }
            }
        }

        public void Dispose()
        {
            // 释放 m3u8 流式代理（HttpListener 停止监听，中断挂起的转发请求）
            try { _proxy?.Dispose(); } catch { }
            _proxy = null;
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
            // 先停止当前导航/拉流，可显著减少 Controller.Close() 的阻塞时间（页面挂起时尤其明显）
            try { _core?.Stop(); } catch { }
            try { _controller?.Close(); } catch { }
            try { if (_core != null) { _core.NavigationCompleted -= OnNavCompleted; _core.WebMessageReceived -= OnWebMessage; } } catch { }
            if (_hwnd != IntPtr.Zero)
            {
                try { Win32.DestroyWindow(_hwnd); } catch { }
                _hwnd = IntPtr.Zero;
            }
            _controller = null;
            _core = null;
        }
    }
}
