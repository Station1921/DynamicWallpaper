using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using DynamicWallpaper.Core;
using DynamicWallpaper.Desktop;
using DynamicWallpaper.Models;
using Microsoft.Web.WebView2.Core;

namespace DynamicWallpaper.Providers
{
    /// <summary>
    /// 视频壁纸：基于 WebView2 + HTML5 video 渲染（Win11 25H2 raised desktop 兼容方案）。
    ///
    /// 背景：WPF MediaElement 的 DirectComposition 视频表面在 Layered 窗口下不被 DWM 合成
    /// （Win11 24H2/25H2 实测报 0xC00D109B / 桌面无变化）；实验已验证「创建时带
    /// WS_EX_LAYERED 的原生挂载窗口 + WebView2 渲染」可被 DWM 真实合成到桌面。
    ///
    /// 关键结论：WS_EX_LAYERED 必须在 CreateWindowEx 创建时携带（动态 SetWindowLong 无效）。
    /// </summary>
    public class VideoProvider : IWallpaperProvider
    {
        /// <summary>性能模式：降低视频缩放质量以减少 GPU 占用（WebView2 下无实际效果，仅保持接口兼容）。</summary>
        public static bool LowQualityScaling { get; set; }

        /// <summary>WebView2 视频链路不需要「摘出→置顶→归位」强制合成（Lively 同款结构不做此操作）。</summary>
        public bool NeedsForcedComposition => false;

        #region Win32

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

        #endregion

        /// <summary>WebView2 Environment 静态缓存（进程内共享，userDataFolder 显式指定避免默认目录冲突）。
        /// userDataFolder 固定到程序根目录 WebView2，避免单文件发布解压场景下落到 C 盘临时目录。
        /// public 供 WebProvider/StaticFadeProvider 复用同一实例，避免重复创建 Environment 及多个用户数据目录。</summary>
        public static readonly Lazy<Task<CoreWebView2Environment>> EnvironmentLazy = new(() =>
            CoreWebView2Environment.CreateAsync(
                null,
                Path.Combine(AppPaths.RootDirectory, "WebView2"),
                new CoreWebView2EnvironmentOptions
                {
                    // 允许无用户手势的自动播放（含 JS 解除静音后继续出声）：
                    // HTML 以 muted autoplay 启动保证必播，SetMuted(false) 再通过 JS 取消静音；
                    // 若无该策略，Chromium 会拦截"无手势的未静音播放"，表现为切换后无声，
                    // 需到设置里重新开关一次（触发一次用户手势）才恢复声音。
                    AdditionalBrowserArguments = "--autoplay-policy=no-user-gesture-required"
                }));

        /// <summary>程序启动时预热 WebView2 Environment，消除首次设置壁纸时创建浏览器进程的延迟。</summary>
        public static void Prewarm()
        {
            try { _ = EnvironmentLazy.Value; }
            catch { /* 预热失败不影响后续设置，正式初始化会重试 */ }
        }

        /// <summary>WebView2 环境可用性（进程内缓存一次检测结果）。
        /// 静态壁纸降级系统 API 的判定依据：WebView2 不可用（缺 WebView2Loader.dll /
        /// VC++ 运行库 / WebView2 Runtime）时，静态壁纸不再依赖窗口层，直接走系统 API，
        /// 保证 Win10 等环境即使动态壁纸不可用，静态壁纸也一定能设置。</summary>
        private static bool? _envAvailable;

        /// <summary>检测 WebView2 环境是否可用。首次调用创建 Environment（含失败缓存），
        /// 之后直接返回缓存结果——环境在进程生命周期内不会从不可用变为可用。</summary>
        public static async Task<bool> IsWebView2AvailableAsync()
        {
            if (_envAvailable.HasValue) return _envAvailable.Value;
            try
            {
                var env = await EnvironmentLazy.Value;
                _envAvailable = env != null;
            }
            catch (Exception ex)
            {
                _envAvailable = false;
                Logger.Log($"[VideoProvider] WebView2 环境不可用（后续静态壁纸将降级系统 API）: {DescribeWebView2Error(ex)}");
            }
            return _envAvailable.Value;
        }

        /// <summary>把 WebView2 初始化异常转成用户可操作的原因描述（状态栏/日志使用）。</summary>
        private static string DescribeWebView2Error(Exception ex)
        {
            string msg = ex.Message;
            if (ex is DllNotFoundException)
                return "缺少 WebView2Loader.dll 或其运行库（VC++ Redistributable），请确认程序目录完整或安装 VC++ 2015-2022 运行库";
            if (msg.Contains("WebView2 Runtime", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("Couldn't find a compatible WebView2 Runtime", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("0x8007139F", StringComparison.OrdinalIgnoreCase) && msg.Contains("runtime", StringComparison.OrdinalIgnoreCase))
                return "未安装 WebView2 Runtime，请安装 Microsoft Edge WebView2 Runtime";
            return msg;
        }

        /// <summary>壁纸适应方式：fill=铺满裁剪 / fit=完整显示 / center=原始居中。由 WallpaperManager 在切换时注入。</summary>
        public static string FitMode { get; set; } = "fill";

        /// <summary>把 FitMode 映射为 HTML video 的 object-fit / object-position CSS。</summary>
        private static string BuildVideoFitCss()
        {
            return FitMode switch
            {
                "fit" => "object-fit:contain;background:#000",
                "center" => "object-fit:none;object-position:center center;background:transparent",
                _ => "object-fit:cover;background:#000"
            };
        }

        private static readonly IntPtr HInstance = GetModuleHandle(null);
        private static readonly object ClassLock = new();
        private static int _classRegistered;

        private IntPtr _hwnd;
        private CoreWebView2Controller? _controller;
        /// <summary>创建 WebView2 Controller 的 UI 线程 Dispatcher。
        /// WebView2 COM 对象是线程亲和的——所有 CoreWebView2 / Controller 调用必须在
        /// 创建它的线程上执行。动切静快速路径（NavigateImageAsync / WaitVideoReadyAsync /
        /// RevertToVideoAsync）可能由非 UI 线程（看门狗/后台任务）触发，直接调用会抛
        /// "Unable to cast COM object ... ICoreWebView2Controller"，必须统一调度回创建线程。</summary>
        private System.Windows.Threading.Dispatcher? _uiDispatcher;
        private string _path = "";
        private Rectangle _bounds;
        private volatile bool _ready;
        private volatile bool _disposed;
        /// <summary>当前静音状态缓存：InitWebView2Async 构造 HTML 时按此写 video 的 muted 属性，
        /// 非静音时页面加载即出声，不依赖 JS 解除静音（规避初始化窗口期 ExecuteScript 偶发失败）。</summary>
        private volatile bool _muted = true;
        /// <summary>静态图片模式：HTML 渲染 &lt;img&gt; 而非 &lt;video&gt;。
        /// 静态壁纸由本 Provider（WebView2 虚拟主机映射本地图片）即时承载显示，
        /// 规避系统 IDesktopWallpaper 异步生效的秒级延迟；图片与视频同走
        /// DirectComposition 通道（视频已验证可被 DWM 合成到 raised desktop 桌面层）。</summary>
        private volatile bool _isImage;
        /// <summary>首次导航完成信号：ExecuteScriptAsync 在导航未完成时调用会抛
        /// "Specified cast is not valid"（COM 层无 document 上下文），执行 JS 前先等待导航完成。</summary>
        private TaskCompletionSource<bool>? _navTcs;

        public WallpaperType Type => WallpaperType.Video;
        public IntPtr Handle => _hwnd;

        /// <summary>当前是否为静态图片模式（HTML 含 img 元素，可原地换图）。</summary>
        public bool IsImageMode => _isImage;

        /// <summary>原地切换为静态图片，不重建 Controller 也不整页导航。
        /// 图片模式：修改现有 img 的 src，新图加载完成前旧图保持显示，就绪后无缝替换。
        /// 视频模式：动态创建 img 覆盖在 video 之上；加载期间 img 背景透明（video 继续播放，
        /// 无黑屏无残留），加载完成 onload 时暂停并隐藏 video（声音立即停、画面无缝直切），
        /// 加载失败 onerror 时移除 img 并恢复 video 播放（不黑屏、不卡死）。
        /// 注意：不能依赖 onload 里 document.body.innerHTML='' 替换 body——清空 body 会连带移除
        /// img 自身（重插有状态丢失风险），且加载中若带 #000 背景会盖住 video 造成黑屏假死。</summary>
        public async Task<bool> NavigateImageAsync(string path)
        {
            _path = path;
            var ctrl = _controller;
            if (ctrl == null || ctrl.CoreWebView2 == null)
            {
                // 冷启动兜底：Controller 尚未就绪时走完整 Show 流程（正常切换路径不会触发）
                Show(path, _bounds);
                _isImage = true;
                return true;
            }
            try
            {
                var dir = Path.GetDirectoryName(path);
                var isUrl = IsRemoteUrl(path);
                if (!string.IsNullOrEmpty(dir) && !isUrl)
                    await RunOnUiThreadAsync(() =>
                    {
                        // 图片模式与视频模式统一使用 dwallpaper.local：108 轮实测该主机名
                        // 对视频与图片均正常；曾尝试独立主机名 dwallpaper-img.local 用于图片，
                        // 实测图片无法加载（冷启动静态层"图片未就绪"），已回退统一主机名。
                        ctrl.CoreWebView2.SetVirtualHostNameToFolderMapping("dwallpaper.local", dir, CoreWebView2HostResourceAccessKind.Allow);
                        return Task.FromResult("");
                    });
                var src = JsEscape(BuildMediaSrc(path));
                var css = BuildVideoFitCss();
                // 图片加载完成后的背景（fit 需黑边；fill 无影响；center 透明）
                var bgOnLoad = FitMode == "center" ? "transparent" : "#000";
                await WaitNavAndRunAsync(
                    "var v=document.getElementById('v');var i=document.getElementById('i');" +
                    $"if(i){{i.src='{src}';}}" +
                    $"else{{var n=document.createElement('img');n.id='i';" +
                    // 背景先置透明：加载期间露出下方 video，绝不出现黑屏
                    $"n.style.cssText='position:fixed;inset:0;width:100vw;height:100vh;{css};background:transparent';" +
                    // onload：暂停并隐藏 video（立即停声），显示静态图，恢复按适应方式需要的背景
                    $"n.onload=function(){{window.__dwpImgState='ok';var vv=document.getElementById('v');if(vv){{vv.pause();vv.style.display='none';}}this.style.background='{bgOnLoad}';}};" +
                    // onerror：移除 img、恢复 video 显示与播放，失败不残留黑屏不卡死
                    "n.onerror=function(){window.__dwpImgState='err';var vv=document.getElementById('v');if(vv){vv.style.display='';vv.play();}this.remove();};" +
                    $"n.src='{src}';document.body.appendChild(n);}}");
                _isImage = true;
                return true;
            }
            catch (Exception ex)
            {
                Logger.Log($"[VideoProvider] NavigateImageAsync 失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>动切静失败回退：移除残留的 img 覆盖层，恢复 video 显示与播放（_isImage 复位）。
        /// 与 NavigateImageAsync 的 onerror 兜底互补，覆盖"img 一直挂起未触发 onload/onerror"的超时场景。</summary>
        public async Task RevertToVideoAsync()
        {
            _isImage = false;
            await WaitNavAndRunAsync(
                "var i=document.getElementById('i');if(i){i.remove();}" +
                "var v=document.getElementById('v');if(v){v.style.display='';v.play();}");
        }

        public void Show(string path, Rectangle bounds)
        {
            _path = path;
            _bounds = bounds;
            _isImage = IsImageFile(path);
            EnsureWindowClass();

            // WS_EX_LAYERED 必须在创建时携带：创建后再动态 SetWindowLong 设置无效
            // （WPF HwndSource 创建时设置也会被 WPF 内部丢弃，实测 exstyle 缺 0x80000 位）。
            _hwnd = CreateWindowEx(
                WS_EX_LAYERED | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW,
                "DynamicWallpaperVideoHost", "DynamicWallpaper Video Host",
                0, bounds.X, bounds.Y, bounds.Width, bounds.Height,
                IntPtr.Zero, IntPtr.Zero, HInstance, IntPtr.Zero);

            if (_hwnd == IntPtr.Zero)
            {
                Logger.Log($"[VideoProvider] CreateWindowEx 失败: {Marshal.GetLastWin32Error()}");
                return;
            }

            // Layered 窗口默认 alpha=0（完全透明），必须置满不透明 DWM 才合成内容
            Win32.SetLayeredWindowAttributes(_hwnd, 0, 255, Win32.LWA_ALPHA);

            // 不在这里显示窗口（避免 WorkerW 获取失败时窗口在顶层闪现）；
            // 窗口会在 AttachTo 成功挂接到 WorkerW 后再 SetWindowPos 显示。
            // fire-and-forget 启动异步初始化，异常在内部 try/catch，不抛出到 UI 线程。
            _ = InitWebView2Async(path, bounds);
        }

        public void AttachTo(IntPtr workerw, Rectangle bounds)
        {
            if (_hwnd == IntPtr.Zero) return;
            WorkerWInjector.Attach(_hwnd, workerw, bounds);
            // 成功挂接到桌面壁纸层后再显示（对应原 WPF 版 _window?.Show() 的语义）
            Win32.SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0,
                Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOZORDER
                | Win32.SWP_NOACTIVATE | Win32.SWP_SHOWWINDOW);
        }

        public void Play() { if (_isImage) return; RunJs("var v=document.getElementById('v');if(v){v.play();}"); }

        public void Pause() { if (_isImage) return; RunJs("var v=document.getElementById('v');if(v){v.pause();}"); }

        /// <summary>运行时切换适应方式：立即更新已渲染内容（video / img）的 object-fit CSS。</summary>
        public void ApplyFitMode()
        {
            var css = BuildVideoFitCss();
            string elem = _isImage ? "i" : "v";
            RunJs($"var {elem}=document.getElementById('{elem}');if({elem}){{{elem}.style.objectFit='{css.Split(';')[0].Split(':')[1].Trim()}';{elem}.style.objectPosition='{(FitMode == "center" ? "center center" : "50% 50%")}';}}");
        }

        public void SetMuted(bool muted)
        {
            if (_isImage) return; // 静态图片无声音概念
            // 必须先缓存期望状态：InitWebView2Async 构造 HTML 及 NavigationCompleted 后
            // 重放时都按此字段应用静音；若只发 JS 而不更新字段，Controller 尚未创建时
            // RunJs 会静默丢弃，HTML 里 video 将始终带 muted（默认 true）静音启动 → 永远无声。
            _muted = muted;
            RunJs($"var v=document.getElementById('v');if(v){{v.muted={muted.ToString().ToLowerInvariant()};v.volume={(muted ? 0 : 1)};}}");
        }

        /// <summary>WebView2 初始化是否已完成（可开始渲染）。</summary>
        public bool HasVideoContent() => _ready;

        /// <summary>等待视频/图片真正可播放（HTML5 video readyState &gt;= 3 且已播放；img 加载完成），
        /// 最多等待 timeout。调用方应在 AttachTo 后调用：就绪前窗口保持透明（露出旧壁纸），
        /// 就绪后再开始叠化过渡，避免"叠化期间新窗口空白、视频加载好后突然跳入"的闪动。
        /// 注意：InitWebView2Async 是异步的，创建 Controller 可能失败重试（0x8007139F 资源竞争），
        /// 因此必须等待 _controller / _navTcs 就绪后再轮询内容状态，否则会立即误判未就绪。</summary>
        public async Task<bool> WaitVideoReadyAsync(TimeSpan timeout)
        {
            // InfiniteTimeSpan 表示无限等待（由 WallpaperManager 用取消令牌打断），
            // 用于网络/慢速加载场景：用户不主动切换/解除/退出就持续等待，不做超时回退。
            var deadline = timeout == Timeout.InfiniteTimeSpan ? DateTime.MaxValue : DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (_disposed) return false;
                var ctrl = _controller;
                var tcs = _navTcs;
                if (ctrl == null || ctrl.CoreWebView2 == null || tcs == null || !tcs.Task.IsCompleted)
                {
                    // Controller / 导航信号尚未就绪（异步初始化中，可能含失败重试），继续等待
                    await Task.Delay(100);
                    continue;
                }
                if (!tcs.Task.Result) return false; // 导航失败，页面不会有内容
                try
                {
                    var script = _isImage
                        ? "var i=document.getElementById('i');(i&&i.complete&&i.naturalWidth>0)?'ok':'pending'"
                        : "var v=document.getElementById('v');(v&&v.readyState>=3&&!v.paused&&v.currentTime>0)?'ok':'pending'";
                    // ExecuteScriptAsync 必须回到 Controller 创建线程执行（跨线程抛 COM 异常）
                    var r = await RunOnUiThreadAsync(() => ctrl.CoreWebView2.ExecuteScriptAsync(script));
                    if (r != null && r.IndexOf("ok", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                }
                catch (Exception ex)
                {
                    // 单次轮询异常不直接判失败：导航刚完成窗口期 ExecuteScriptAsync 偶发
                    // COM 异常，继续轮询直到超时（避免"内容已就绪却误判失败"）。
                    Logger.Log($"[VideoProvider] WaitVideoReady 轮询异常（继续等待）: {ex.Message}");
                }
                await Task.Delay(100);
            }
            return false;
        }

        /// <summary>实现 IWallpaperProvider.WaitReadyAsync，供 WallpaperManager 统一等待内容就绪。</summary>
        public async Task WaitReadyAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            if (!await WaitVideoReadyAsync(timeout).WaitAsync(timeout, cancellationToken))
                throw new TimeoutException("视频/图片加载超时");
        }

        public void Dispose()
        {
            _disposed = true;
            try { _controller?.Close(); } catch { }
            _controller = null;
            if (_hwnd != IntPtr.Zero)
            {
                Win32.DestroyWindow(_hwnd);
                _hwnd = IntPtr.Zero;
            }
        }

        #region 私有

        /// <summary>判断是否为静态图片（静态壁纸走 &lt;img&gt; 渲染模式）。</summary>
        private static bool IsImageFile(string path)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            return ext is ".jpg" or ".jpeg" or ".png" or ".bmp" or ".gif" or ".webp";
        }

        /// <summary>判断路径是否为远程 http/https URL。</summary>
        private static bool IsRemoteUrl(string path)
        {
            return Uri.TryCreate(path, UriKind.Absolute, out var u) &&
                   (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);
        }

        /// <summary>生成 HTML/JS 中使用的媒体源地址。本地文件走 dwallpaper.local 虚拟主机，远程 URL 直接使用原链接。</summary>
        private static string BuildMediaSrc(string path)
        {
            if (IsRemoteUrl(path)) return path;
            return $"http://dwallpaper.local/{Uri.EscapeDataString(Path.GetFileName(path))}";
        }

        private static string HtmlEscape(string s)
        {
            return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
                    .Replace("\"", "&quot;").Replace("'", "&#39;");
        }

        private static string JsEscape(string s)
        {
            return s.Replace("\\", "\\\\").Replace("'", "\\'").Replace("\n", "\\n").Replace("\r", "\\r");
        }

        /// <summary>把 WebView2 COM 调用调度回 Controller 创建线程（UI 线程）执行。
        /// WebView2 COM 接口绑定创建线程，跨线程 QueryInterface 会抛
        /// "Unable to cast COM object ... ICoreWebView2Controller"；已在 UI 线程时直接执行。</summary>
        private async Task<string?> RunOnUiThreadAsync(Func<Task<string>> action)
        {
            var disp = _uiDispatcher;
            if (disp == null || disp.CheckAccess())
                return await action();
            return await disp.InvokeAsync(action).Task.Unwrap();
        }

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
                    lpszClassName = "DynamicWallpaperVideoHost"
                };
                if (RegisterClass(ref wc) == 0 && Marshal.GetLastWin32Error() != ERROR_CLASS_ALREADY_EXISTS)
                    Logger.Log($"[VideoProvider] 注册窗口类失败: {Marshal.GetLastWin32Error()}");
                else
                    _classRegistered = 1;
            }
        }

        private async Task InitWebView2Async(string path, Rectangle bounds)
        {
            try
            {
                // 记录创建线程的 Dispatcher：WebView2 COM 对象线程亲和，后续所有
                // CoreWebView2 调用（ExecuteScriptAsync / SetVirtualHostNameToFolderMapping）
                // 都必须调度回该线程执行，否则跨线程访问抛 COM 异常。
                _uiDispatcher = System.Windows.Application.Current?.Dispatcher;
                var env = await EnvironmentLazy.Value;
                if (_disposed || _hwnd == IntPtr.Zero) return;

                // 创建 Controller 增加失败重试：动态壁纸 A→B 无缝切换瞬间，旧壁纸 A 的
                // WebView2 Controller 可能尚未完全释放，同一 Environment 上创建 Controller
                // 会短暂失败（0x8007139F 组或资源的状态不正确）。
                // 6 次渐进退避（300→500→800→1200→1800ms），比固定 3×500ms 更可靠。
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
                        Logger.Log($"[VideoProvider] CreateCoreWebView2ControllerAsync 失败（第{attempt}次/共{maxAttempts}次），{delay}ms 后重试: {ex.Message}");
                        if (_disposed || _hwnd == IntPtr.Zero) return;
                        await Task.Delay(delay);
                    }
                }

                if (_disposed)
                {
                    try { ctrl.Close(); } catch { }
                    return;
                }

                ctrl.Bounds = new Rectangle(0, 0, bounds.Width, bounds.Height);
                // WebView2 默认页面背景是白色（不透明），视频解码渲染前窗口会合成出白底，
                // 切换叠化期间会"白屏闪过"。设为透明后，视频未就绪时窗口完全透明（露出旧壁纸）。
                ctrl.DefaultBackgroundColor = System.Drawing.Color.Transparent;
                ctrl.IsVisible = true;
                ctrl.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                ctrl.CoreWebView2.Settings.IsStatusBarEnabled = false;

                // 虚拟主机映射：本地文件通过 http://dwallpaper.local/ 加载（不受 CORS 限制）；
                // 远程 URL 直接使用原链接流播，不做文件夹映射。
                var isUrl = IsRemoteUrl(path);
                string? dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !isUrl)
                    ctrl.CoreWebView2.SetVirtualHostNameToFolderMapping("dwallpaper.local", dir, CoreWebView2HostResourceAccessKind.Allow);

                var mediaSrc = HtmlEscape(BuildMediaSrc(path));

                // video 始终带 muted 启动（保证 autoplay 必成功），再通过内嵌 script 在
                // loadedmetadata 时按 _muted 应用真实静音状态——非静音时取消静音继续播放。
                // 不能只依赖外部 SetMuted 的 RunJs：Controller 创建是异步的，早期调用会被丢弃，
                // 页面加载后没有任何机制再取消静音，表现为"未勾选静音却始终无声"。
                string html;
                if (_isImage)
                {
                    // 静态图片模式：本地文件走虚拟主机，远程 URL 直接加载。
                    // 窗口背景透明，图片加载完成前露出下层（旧动态层），就绪后无缝直切。
                    html = $"<html><head><style>html,body{{margin:0;padding:0;overflow:hidden}}img{{position:fixed;inset:0;width:100vw;height:100vh;{BuildVideoFitCss()}}}</style></head><body><img id='i' src='{mediaSrc}'></body></html>";
                }
                else
                {
                    string mutedJs = _muted ? "true" : "false";
                    // 注意：不注册 canplay 自动 play——Pause() 暂停后若触发 canplay 事件会被误恢复播放
                    // （全屏暂停失效的隐患之一）；播放由 autoplay + loadedmetadata 保证。
                    html = $"<html><head><style>html,body{{margin:0;padding:0;overflow:hidden}}video{{position:fixed;inset:0;width:100vw;height:100vh;{BuildVideoFitCss()}}}</style></head><body><video id='v' src='{mediaSrc}' autoplay muted loop playsinline></video><script>var v=document.getElementById('v');v.addEventListener('loadedmetadata',function(){{v.muted={mutedJs};v.volume={(_muted ? 0 : 1)};v.play();}});</script></body></html>";
                }

                // 导航完成信号：ExecuteScriptAsync 需在导航完成后调用（此前会抛 COM 异常），
                // WaitNavAndRunAsync / WaitVideoReadyAsync 都依赖它先等待导航完成。
                var navTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _navTcs = navTcs;
                ctrl.CoreWebView2.NavigationCompleted += (_, e) =>
                {
                    navTcs.TrySetResult(e.IsSuccess);
                    // 导航完成后按最新 _muted 重放一次静音状态：覆盖"SetMuted 在 Controller
                    // 创建前被 RunJs 丢弃"的残留窗口期，保证页面加载完成后静音状态与配置一致。
                    // 静态图片模式无静音/播放概念，跳过重放。
                    if (e.IsSuccess && !_isImage)
                    {
                        try
                        {
                            bool muted = _muted;
                            _ = ctrl.CoreWebView2.ExecuteScriptAsync(
                                $"var v=document.getElementById('v');if(v){{v.muted={muted.ToString().ToLowerInvariant()};v.volume={(muted ? 0 : 1)};v.play();}}");
                        }
                        catch { /* 导航成功瞬间执行偶发失败，内嵌 script 已兜底，可忽略 */ }
                    }
                };

                ctrl.CoreWebView2.NavigateToString(html);

                _controller = ctrl;
                _ready = true;
                Logger.Log($"[VideoProvider] WebView2 初始化完成: {path}");
            }
            catch (Exception ex)
            {
                Logger.Log($"[VideoProvider] WebView2 初始化失败: {DescribeWebView2Error(ex)}");
                // 初始化失败即视为 WebView2 环境不可用，后续静态壁纸降级系统 API
                _envAvailable = false;
            }
        }

        private void RunJs(string script)
        {
            if (_controller == null) return;
            _ = WaitNavAndRunAsync(script);
        }

        /// <summary>等待首次导航完成后再执行 JS，避免切换瞬间 ExecuteScriptAsync 抛
        /// "Specified cast is not valid"；5 秒超时兜底（导航异常时页面仍可能有内容）。
        /// _navTcs 可能在 Play()/SetMuted() 被调用时尚未由 InitWebView2Async 设置，
        /// 此处先轮询等待其就绪（最多 1s），再等待导航完成。</summary>
        private async Task WaitNavAndRunAsync(string script)
        {
            try
            {
                // _navTcs 可能为 null（InitWebView2Async 尚未执行到设置 _navTcs 的行），
                // 短暂轮询等待其就绪，避免在 null 状态下跳过导航等待直接执行 JS。
                var tcs = _navTcs;
                if (tcs == null)
                {
                    for (int i = 0; i < 10 && _navTcs == null && !_disposed; i++)
                        await Task.Delay(100);
                    tcs = _navTcs;
                }
                if (tcs != null)
                {
                    var done = await Task.WhenAny(tcs.Task, Task.Delay(5000));
                    if (done != tcs.Task) return; // 超时
                    if (!tcs.Task.Result) return; // 导航失败，不执行 JS
                }
                var ctrl = _controller;
                if (ctrl == null) return;
                try
                {
                    // ExecuteScriptAsync 必须回到 Controller 创建线程执行：
                    // 跨线程访问 WebView2 COM 对象会抛 "Unable to cast COM object ... ICoreWebView2Controller"
                    await RunOnUiThreadAsync(() => ctrl.CoreWebView2.ExecuteScriptAsync(script));
                }
                catch (Exception ex)
                {
                    // 导航刚完成但 WebView2 内部尚未完全就绪时，ExecuteScriptAsync 偶发
                    // "Specified cast is not valid"；600ms 后重试一次，仍失败才记日志。
                    Logger.Log($"[VideoProvider] JS 执行失败（重试前）: {ex.Message}");
                    try
                    {
                        await Task.Delay(600);
                        await RunOnUiThreadAsync(() => ctrl.CoreWebView2.ExecuteScriptAsync(script));
                    }
                    catch (Exception ex2)
                    {
                        Logger.Log($"[VideoProvider] JS 执行失败（重试后仍失败）: {ex2.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[VideoProvider] JS 执行失败: {ex.Message}");
            }
        }

        #endregion
    }
}
