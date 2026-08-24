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

        /// <summary>是否需要「摘出→置顶→归位→DefView 重绘」强制 DWM 合成：不需要。
        /// Win10 视频不显示的真正根因是窗口带了 WS_EX_LAYERED（传统模式下 DWM 不合成
        /// WorkerW 下 Layered 子窗口的 alpha 变化），现按桌面模式创建窗口后无需强制合成。
        /// 且「摘出→TOPMOST→归位」会让窗口短暂浮到所有窗口上方，本身也是视觉干扰源。</summary>
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
        /// --autoplay-policy=no-user-gesture-required：允许视频带声音自动播放（无需用户手势）。
        /// --disable-backgrounding-occluded-windows / --disable-renderer-backgrounding /
        /// --disable-background-timer-throttling：壁纸窗口常年被桌面图标层遮挡，Chromium 的
        /// 遮挡检测会判定页面"被遮挡"而节流渲染/暂停视频，必须禁用，否则视频不播或卡帧。
        /// --disable-features=CalculateNativeWinOcclusion：彻底关闭原生窗口遮挡计算，
        /// 双保险防止 Chromium 因壁纸窗口被完全遮挡而挂起渲染（表现为"点一下才播放"）。</summary>
        internal static readonly Lazy<Task<CoreWebView2Environment>> EnvironmentLazy = new(() =>
            CoreWebView2Environment.CreateAsync(
                null,
                Path.Combine(AppPaths.RootDirectory, "WebView2"),
                new CoreWebView2EnvironmentOptions("--autoplay-policy=no-user-gesture-required --disable-backgrounding-occluded-windows --disable-renderer-backgrounding --disable-background-timer-throttling --disable-features=CalculateNativeWinOcclusion")));

        /// <summary>程序启动时预热 WebView2 Environment，消除首次设置壁纸时创建浏览器进程的延迟。</summary>
        public static void Prewarm()
        {
            try { _ = EnvironmentLazy.Value; }
            catch { /* 预热失败不影响后续设置，正式初始化会重试 */ }
        }

        /// <summary>壁纸适应方式：fill=铺满裁剪 / fit=完整显示 / center=原始居中。由 WallpaperManager 在切换时注入。</summary>
        public static string FitMode { get; set; } = "fill";

        /// <summary>视频初始静音状态。由 WallpaperManager 在 Show() 前设置（_config.Mute）。
        /// HTML 根据此值决定 video 元素的初始 muted 属性——若 false 则不静音，
        /// 配合 --autoplay-policy=no-user-gesture-required 允许带声音自动播放。
        /// 之前 HTML 无条件 muted=true 再用 SetMuted 取消静音，WebView2 会阻止
        /// "先静音再取消静音"的播放策略，导致非静音模式下无声音。</summary>
        public static bool InitialMuted { get; set; } = true;

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
        private string _path = "";
        private volatile bool _ready;
        private volatile bool _disposed;
        /// <summary>首次导航完成信号：ExecuteScriptAsync 在导航未完成时调用会抛
        /// "Specified cast is not valid"（COM 层无 document 上下文），执行 JS 前先等待导航完成。</summary>
        private TaskCompletionSource<bool>? _navTcs;

        /// <summary>视频就绪信号（canplay/playing 时由页面脚本 postMessage 发出）。
        /// 不再用 ExecuteScriptAsync 轮询就绪状态：切换瞬间它常抛 E_NOINTERFACE
        /// （COM 接口尚未就绪），导致"等视频就绪"立即失败、视频未解码就切屏闪动。</summary>
        private TaskCompletionSource<bool>? _readyTcs;

        public WallpaperType Type => WallpaperType.Video;
        public IntPtr Handle => _hwnd;

        /// <summary>窗口是否为 Layered 模式（仅 Win11 raised desktop 使用）。
        /// Win10/传统模式必须用非 Layered 窗口：DWM 不会合成 WorkerW 下 Layered 子窗口的
        /// alpha 修改（窗口初始 alpha=0 后永远透明，点击任意窗口引发前台变化才显现，
        /// 即"点一下才播放"的根因）；非 Layered 子窗口与普通窗口一样被正常合成。
        /// Win11 24H2+ raised desktop 相反：DWM 仅合成 WS_EX_LAYERED 子窗口到桌面壁纸层。</summary>
        private bool _layered;

        public void Show(string path, Rectangle bounds)
        {
            _path = path;
            EnsureWindowClass();

            // 按桌面模式决定窗口扩展样式（关键：WS_EX_LAYERED 只允许 Win11 raised desktop 使用）
            _layered = WorkerWInjector.IsRaisedDesktopMode();
            int exStyle = _layered
                ? WS_EX_LAYERED | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW
                : WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;

            _hwnd = CreateWindowEx(
                exStyle, "DynamicWallpaperVideoHost", "DynamicWallpaper Video Host",
                0, bounds.X, bounds.Y, bounds.Width, bounds.Height,
                IntPtr.Zero, IntPtr.Zero, HInstance, IntPtr.Zero);

            if (_hwnd == IntPtr.Zero)
            {
                Logger.Log($"[VideoProvider] CreateWindowEx 失败: {Marshal.GetLastWin32Error()}");
                return;
            }

            // Layered 窗口（Win11 raised）创建即置 alpha=255 全不透明：不搞 alpha=0→255 动画，
            // 视频未就绪时靠 WebView2 透明背景（DefaultBackgroundColor=Transparent）露出旧壁纸，
            // 就绪后画面自然出现，无需任何 alpha 切换（alpha 变化在部分环境下不被重新合成）。
            if (_layered)
                Win32.SetLayeredWindowAttributes(_hwnd, 0, 255, Win32.LWA_ALPHA);

            // 窗口保持隐藏（创建时不带 WS_VISIBLE），等视频就绪后由 WallpaperManager
            // 调用 AttachTo 挂到桌面承载层并显示——挂载瞬间已有解码好的画面，直切无闪动。
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

        public void Play() => RunJs("var v=document.getElementById('v');if(v){v.play();}");

        public void Pause() => RunJs("var v=document.getElementById('v');if(v){v.pause();}");

        /// <summary>运行时切换适应方式：立即更新已渲染视频的 object-fit CSS。</summary>
        public void ApplyFitMode()
        {
            var css = BuildVideoFitCss();
            RunJs($"var v=document.getElementById('v');if(v){{v.style.objectFit='{css.Split(';')[0].Split(':')[1].Trim()}';v.style.objectPosition='{(FitMode == "center" ? "center center" : "50% 50%")}';}}");
        }

        public void SetMuted(bool muted)
        {
            RunJs($"var v=document.getElementById('v');if(v){{v.muted={muted.ToString().ToLowerInvariant()};v.volume={(muted ? 0 : 1)};}}");
        }

        /// <summary>WebView2 初始化是否已完成（可开始渲染）。</summary>
        public bool HasVideoContent() => _ready;

        /// <summary>等待视频真正可播放（HTML5 video canplay/playing 且已有分辨率）。
        /// 就绪信号由页面内脚本通过 postMessage 发出（WebMessageReceived 事件接收），
        /// 不依赖 ExecuteScriptAsync——它在切换瞬间常因 COM 接口未就绪抛 E_NOINTERFACE，
        /// 曾导致此等待立即失败、视频未解码就置不透明切屏闪动。</summary>
        public async Task<bool> WaitVideoReadyAsync(TimeSpan timeout)
        {
            var tcs = _readyTcs;
            if (tcs == null)
            {
                // 信号源尚未建立（初始化早期）：短暂等待其就绪
                for (int i = 0; i < 20 && _readyTcs == null && !_disposed; i++)
                    await Task.Delay(100);
                tcs = _readyTcs;
            }
            if (tcs == null) return _ready;
            var done = await Task.WhenAny(tcs.Task, Task.Delay(timeout));
            return done == tcs.Task;
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
                // Layered（Win11 raised）：透明背景——视频未就绪时窗口完全透明露出旧壁纸，
                // 就绪后画面自然浮现，无任何闪动。非 Layered（Win10 传统）：透明背景在
                // 非分层窗口上结果未定义（可能残留杂色），明确用黑色，就绪前短暂黑底。
                ctrl.DefaultBackgroundColor = _layered
                    ? System.Drawing.Color.Transparent
                    : System.Drawing.Color.Black;
                ctrl.IsVisible = true;
                ctrl.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                ctrl.CoreWebView2.Settings.IsStatusBarEnabled = false;

                // 虚拟主机映射：HTML 页面通过 http://dwallpaper.local/ 加载本地视频（不受 CORS 限制）
                string? dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                    ctrl.CoreWebView2.SetVirtualHostNameToFolderMapping("dwallpaper.local", dir, CoreWebView2HostResourceAccessKind.Allow);

                // 注意：video 元素的 muted 状态由 InitialMuted 决定（跟随用户配置）。
                // --autoplay-policy=no-user-gesture-required 允许带声音自动播放，
                // 但 WebView2 会阻止"先静音再取消静音"的播放，因此必须在 HTML 层面
                // 就设置正确的初始静音状态，而非后续通过 JS 取消静音。
                string videoTag = InitialMuted
                    ? $"<video id='v' src='http://dwallpaper.local/{Uri.EscapeDataString(Path.GetFileName(path))}' autoplay muted loop playsinline></video>"
                    : $"<video id='v' src='http://dwallpaper.local/{Uri.EscapeDataString(Path.GetFileName(path))}' autoplay loop playsinline></video>";
                // 就绪通知脚本：video 可播（canplay/playing 且有分辨率）时 postMessage 通知宿主，
                // WaitVideoReadyAsync 通过 WebMessageReceived 接收——不依赖 ExecuteScriptAsync 轮询。
                // r() 内的 v.play()：页面侧主动续播兜底——若自动播放被策略拦截（如 Environment
                // 参数未生效、浏览器进程复用旧参数），canplay 时从页面内部再次发起 play，
                // 配合宿主侧 Play()（UI 线程 JS）双保险，杜绝"设置后不播放"。
                string readyScript =
                    "<script>(function(){var v=document.getElementById('v');" +
                    "function r(){if(v&&v.readyState>=2&&v.videoWidth>0){try{v.play().catch(function(){});}catch(e){}try{chrome.webview.postMessage('video-ready');}catch(e){}}}" +
                    "v.addEventListener('canplay',r);v.addEventListener('playing',r);r();})();</script>";
                string html = $"<html><head><style>html,body{{margin:0;padding:0;overflow:hidden}}video{{position:fixed;inset:0;width:100vw;height:100vh;{BuildVideoFitCss()}}}</style></head><body>{videoTag}{readyScript}</body></html>";

                // 导航完成信号：ExecuteScriptAsync 需在导航完成后调用（此前会抛 COM 异常），
                // WaitNavAndRunAsync 依赖它先等待导航完成。
                var navTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _navTcs = navTcs;
                ctrl.CoreWebView2.NavigationCompleted += (_, e) => navTcs.TrySetResult(e.IsSuccess);

                // 视频就绪信号：接收页面脚本的 postMessage（见 readyScript）
                var readyTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _readyTcs = readyTcs;
                ctrl.CoreWebView2.WebMessageReceived += (_, e) =>
                {
                    try
                    {
                        // TryGetWebMessageAsString()：消息为 JSON 时返回 null，不抛异常
                        if (e.TryGetWebMessageAsString() == "video-ready")
                        {
                            if (!_ready) Logger.Log("[VideoProvider] 收到视频就绪通知 (video-ready)");
                            _ready = true;
                            readyTcs.TrySetResult(true);
                        }
                    }
                    catch { /* 其它异常忽略 */ }
                };

                ctrl.CoreWebView2.NavigateToString(html);

                _controller = ctrl;
                // 注：此处不再置 _ready=true——_ready 必须表示"视频真正解码可播"
                // （由页面 postMessage/video-ready 驱动）。导航刚发出就置 true 会让
                // WaitVideoReadyAsync 在信号源缺失的兜底路径上误判就绪，导致视频
                // 还没解码就置不透明、桌面闪现下层静态壁纸。
                Logger.Log($"[VideoProvider] WebView2 初始化完成: {path}");
            }
            catch (Exception ex)
            {
                Logger.Log($"[VideoProvider] WebView2 初始化失败: {ex}");
            }
        }

        /// <summary>在 UI 线程执行 WebView2 脚本。WebView2 的 COM 接口只允许在创建它的
        /// UI 线程访问：RunJs 由 Play()/Pause()/SetMuted() 触发，而 WallpaperManager 的
        /// 切换流程运行在线程池线程，await 续延后直接调用 ExecuteScriptAsync 必然抛
        /// "Unable to cast COM object ... E_NOINTERFACE"（日志里大量 JS 执行失败的根因）。
        /// 后果是 Play() 形同虚设——视频一旦被自动播放策略拦住，就再没有任何机制启动播放，
        /// 表现为"设置了动态壁纸但不播放"。封送回 UI 线程后 Play/SetMuted 真正生效。</summary>
        private static async Task RunScriptOnUiThread(CoreWebView2Controller ctrl, string script)
        {
            var app = System.Windows.Application.Current;
            if (app?.Dispatcher != null && !app.Dispatcher.CheckAccess())
                await await app.Dispatcher.InvokeAsync(() => ctrl.CoreWebView2.ExecuteScriptAsync(script));
            else
                await ctrl.CoreWebView2.ExecuteScriptAsync(script);
        }

        private void RunJs(string script)
        {
            if (_controller == null || _disposed) return;
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
                if (_disposed) return; // 等待期间窗口已被销毁，不再触碰 COM 对象（避免 E_NOINTERFACE 噪音日志）
                var ctrl = _controller;
                if (ctrl == null) return;
                try
                {
                    await RunScriptOnUiThread(ctrl, script);
                }
                catch (Exception ex)
                {
                    // 偶发失败：600ms 后重试一次，仍失败才记日志。
                    Logger.Log($"[VideoProvider] JS 执行失败（重试前）: {ex.Message}");
                    try
                    {
                        await Task.Delay(600);
                        if (_disposed || _controller == null) return;
                        await RunScriptOnUiThread(_controller, script);
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
