using System;
using System.Collections.Concurrent;
using System.Drawing;
using System.IO;
using System.Linq;
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

        private const uint WM_SIZE = 0x0005;
        private const uint WM_WINDOWPOSCHANGED = 0x0047;
        private const uint WM_DPICHANGED = 0x02E0;

        /// <summary>hwnd → Provider 映射。窗口过程是静态委托（无 this），需要靠它找回实例，
        /// 才能在窗口被 SetWindowPos 改变尺寸时同步 WebView2 控制器的 Bounds。</summary>
        private static readonly ConcurrentDictionary<IntPtr, VideoProvider> LiveHosts = new();

        // static readonly 保活：防止委托被 GC 回收导致 WndProc 失效（AccessViolation）。
        private static readonly WndProcDelegate WndProcHandler = WndProcImpl;

        /// <summary>宿主窗口过程：只在“尺寸/DPI 变化”时把 WebView2 控制器尺寸重新对齐到客户区，
        /// 其余消息原样透传给 DefWindowProc。
        ///
        /// 为什么必须做（“壁纸偏移到桌面左上角”的根因）：
        /// WebView2 控制器的可见尺寸 = Bounds × RasterizationScale（设备像素）。本程序把 Bounds 设为
        /// 屏幕像素尺寸且 RasterizationScale 保持默认 1.0。窗口一旦被重新 SetWindowPos 改变尺寸
        /// （Attach 挂载 / ShowWallpaperWindow 移入 / 拓扑变化后重排 / 跨屏移动），控制器 Bounds 不会
        /// 自动跟随 —— 内容仍按旧尺寸绘制并锚定在客户区左上角，即“壁纸只占左上角一块”。
        /// 另外 ShouldDetectMonitorScaleChanges 默认为 true：在缩放率不同的显示器之间切换
        /// （笔记本内屏 1920×1080 ↔ 外接 2560×1600）时 WebView2 会自动改 RasterizationScale
        /// （Bounds 不变则设备尺寸整体缩放）——同样会击穿尺寸对齐。
        /// 因此这里在每次 WM_SIZE / WM_WINDOWPOSCHANGED / WM_DPICHANGED 后按
        /// “客户区像素 ÷ RasterizationScale”回写 Bounds。窗口过程在创建线程（UI 线程）被调用，
        /// 可直接访问 COM 控制器，无需再调度。</summary>
        private static IntPtr WndProcImpl(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if ((msg == WM_SIZE || msg == WM_WINDOWPOSCHANGED || msg == WM_DPICHANGED)
                    && LiveHosts.TryGetValue(hWnd, out var p))
                {
                    p.SyncControllerBounds(msg == WM_DPICHANGED ? "DPI 变化" : "窗口尺寸变化");
                }
            }
            catch { /* 原生窗口过程内绝不抛出异常（会直接崩进程） */ }
            return DefWindowProc(hWnd, msg, wParam, lParam);
        }

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

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int left;
            public int top;
            public int right;
            public int bottom;
        }

        [DllImport("user32.dll")]
        private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

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
                    // 关闭 Chromium 的"原生窗口遮挡检测"（CalculateNativeWinOcclusion）：
                    // 壁纸宿主窗口在切换期间会被移到屏幕之外 / 被旧壁纸窗口盖住，Chromium 会把
                    // 这种状态判定为"被遮挡/不可见"从而降低渲染帧率甚至停止出帧——表现就是
                    // "内容明明已就绪，移入显示的瞬间却还是空白帧（闪一下）"以及"时好时坏"。
                    // 关掉该启发式后，无论窗口是否被遮挡都持续渲染，移入时内容必然已画好。
                    AdditionalBrowserArguments = "--autoplay-policy=no-user-gesture-required --disable-features=CalculateNativeWinOcclusion"
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

        /// <summary>壁纸旋转角度（实例属性，按壁纸路径独立）：0=不旋转，90=顺时针90°，180=180°，270=逆时针90°。由 WallpaperManager 在创建时按路径注入（仅图片模式生效）。</summary>
        public int Rotation { get; set; } = 0;

        /// <summary>把旋转映射为 img 的定位/尺寸/transform CSS（r15 收口）：
        /// 一律 inset:0 + margin:auto + 显式宽高居中（块布局机制，不依赖 left/top 百分比与
        /// translate 变换）；90°/270° 时宽高互换（宽=100vh、高=100vw）再以中心 rotate 铺满。
        ///
        /// 【r15 关键修复：每个角度都必须【显式声明 transform】】
        /// 0° 以前返回的字符串里没有 transform —— 这在 CSS 级联里等于"本样式未声明该属性"，
        /// 并不会覆盖样式表。而页面 &lt;style&gt; 里的 img{video} 规则会随页面【一直存活】，
        /// 原地换图（快速路径复用同一页面）换成"不旋转"的壁纸时，内联样式没有声明 transform，
        /// 建页时旧壁纸写进 &lt;style&gt; 的 rotate(270deg) 就穿透生效了：
        /// r15 日志实证 内联样式="position:fixed;inset:0;margin:auto;width:100vw;height:100vh;
        /// object-fit:cover"（无 transform）却算出 tf=matrix(0,-1,1,0,0,0)= rotate(-90°)，
        /// rect=(480,-480,1600x2560) —— 尺寸来自内联、旋转来自样式表，正是此前的"嵌合体 CSS"。
        /// 后果：切换时先闪现"上一张壁纸的旋转角度"的图，断言抓到异常→重套同样 CSS 无效
        /// （依旧没声明 transform）→ 转冷启动重建页面，才显示正确壁纸（用户看到的"闪动"）。
        /// 现在 0° 显式写 transform:none，内联样式对 transform 始终有声明权，
        /// 样式表残留的旋转再也无法穿透；同时页面 &lt;style&gt; 也不再携带任何旋转（见 PageBaseCss）。</summary>
        private string BuildImageRotationCss()
        {
            var r = ((Rotation % 360) + 360) % 360;
            if (r == 90 || r == 270)
                return $"inset:0;margin:auto;width:100vh;height:100vw;transform:rotate({r}deg);";
            if (r == 180)
                return "inset:0;margin:auto;width:100vw;height:100vh;transform:rotate(180deg);";
            return "inset:0;margin:auto;width:100vw;height:100vh;transform:none;";
        }

        /// <summary>页面基础样式（r15）：只给 img/video 一个"铺满视口"的几何基线，
        /// 【绝不含 transform / 旋转】。原因：页面 &lt;style&gt; 规则在整个页面生命周期内持续生效，
        /// 而内联样式只覆盖它声明过的属性——把建页那一刻的旋转写进 &lt;style&gt;，
        /// 之后原地换图换成别的旋转角度（尤其 0°）时就会"内联管尺寸、样式表管旋转"，
        /// 出现"闪现上一张壁纸旋转角度"的嵌合体渲染。旋转一律只由内联样式承担。</summary>
        private const string PageBaseCss =
            "html,body{margin:0;padding:0;overflow:hidden}" +
            "img,video{position:fixed;inset:0;margin:auto;width:100vw;height:100vh;object-fit:cover}";

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
        /// <summary>宿主窗口是否已完成挂载（AttachTo）。r17 起控制器必须等它置位后再创建——
        /// 此前 InitWebView2Async 与 AttachTo 并发：Attach 正在 SetParent/改窗口样式/落位时
        /// 创建控制器必然失败（日志实证：每一次冷启动都先报
        /// "CreateCoreWebView2ControllerAsync 失败（第1次）0x8007139F" 再退避 300ms 重试），
        /// 既白等 300ms（切换变慢），又让控制器拿到挂载中途的窗口/DPI 上下文
        /// （内容按错误比例渲染 → 只看到左上角一部分，即"壁纸偏移/中间裁切"）。</summary>
        private readonly TaskCompletionSource<bool> _attachTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>当前宿主窗口的承载层父窗口（复核/自愈用）。</summary>
        private IntPtr _attachedParent = IntPtr.Zero;
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
        /// <summary>最近一次原地换图的完整 JS（改 src + 重绑 onload/onerror + 重写 CSS）。
        /// WaitVideoReadyAsync 检测到 onerror 后按此脚本自动重试：虚拟主机映射变更
        /// （SetVirtualHostNameToFolderMapping → 浏览器进程）是异步 IPC，换图脚本紧随其后
        /// 执行时新映射可能尚未生效 → 404。重试时映射早已生效，一次即可自愈。</summary>
        private volatile string? _lastImageNavScript;
        /// <summary>当前页面已设置的虚拟主机映射目录（dwallpaper.local → ?）。
        /// 特殊值 <see cref="RootMappingMark"/> 表示映射到 Wallpapers 根目录（子目录通用）；
        /// null 表示未设置（纯远程 URL 页面）。快速路径据此判断"换图是否需要改映射"
        /// ——r9 实测对已加载页面改映射后新请求持续 404（重试无效），必须避免。</summary>
        private string? _mappedDir;
        /// <summary>_mappedDir 的特殊标记：映射到 Wallpapers 根目录，其下任意子目录换图都无需改映射。</summary>
        private const string RootMappingMark = "\u0001ROOT";
        /// <summary>图片渲染快照是否已记录（每个 Provider 只记一次，用于诊断"个别图片渲染异常"）。</summary>
        private int _renderSnapLogged;

        /// <summary>把 Wallpapers 根目录下的本地文件路径转成根映射下的相对 URL 路径
        /// （各段 URL 编码，形如 /netbian/xxx.jpg）；不在根下返回 null。</summary>
        private static string? TryGetWallpapersRelativePath(string path)
        {
            try
            {
                var root = Path.GetFullPath(Path.Combine(AppPaths.RootDirectory, "Wallpapers"))
                               .TrimEnd('\\', '/');
                var full = Path.GetFullPath(path).Replace('/', '\\');
                root = root.Replace('/', '\\');
                if (!full.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase)) return null;
                var rel = full.Substring(root.Length + 1);
                return "/" + string.Join("/", rel.Split('\\').Select(Uri.EscapeDataString));
            }
            catch { return null; }
        }

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
            // 只判 _controller 引用：CoreWebView2 属性是 COM 跨线程访问，非创建线程调用会抛
            // "Unable to cast COM object ... ICoreWebView2Controller"（Controller 创建成功后
            // CoreWebView2 必非 null，判 _controller 即可）。真正的 COM 调用都在 UI 线程执行。
            if (ctrl == null)
            {
                // 控制器尚未就绪（初始化中或失败）：不在这里另起一个窗口——Show() 会新建 hwnd 而旧窗口
                // 无人挂载、无人销毁，留下孤儿窗口。直接返回 false，交由调用方走冷启动路径重建静态层。
                Logger.Log("[VideoProvider] NavigateImageAsync 跳过：控制器尚未就绪，交由冷启动路径处理");
                return false;
            }
            try
            {
                var isUrl = IsRemoteUrl(path);
                // 媒体地址解析（r10 根治"跨目录静切静必失败"）：
                // r9 实测（15:10:21-22 三次重试全 404）证明：对已加载页面调用
                // SetVirtualHostNameToFolderMapping 改映射目录后，新请求【持续】404（非瞬态竞态，
                // 重试无效）——映射变更只对之后的导航生效。因此：
                // ① 页面创建时（InitWebView2Async）统一把 dwallpaper.local 映射到 Wallpapers 根目录
                //   （媒体在根下时），src 带 /子目录/ 文件名——之后任意子目录间换图【完全不再改映射】；
                // ② 媒体不在根下（外部文件）：页面已按其目录映射，若换图目标目录不同 → 必 404，
                //   直接返回 false 交冷启动（新页面导航前设映射，必定成功），不浪费重试时间。
                var relPath = isUrl ? null : TryGetWallpapersRelativePath(path);
                string mediaSrcRaw;
                if (isUrl)
                {
                    mediaSrcRaw = path;
                }
                else if (relPath != null)
                {
                    if (_mappedDir != RootMappingMark)
                    {
                        // 旧页面是外部目录映射（r10 之前创建的会话/兜底路径）：改映射必 404，转冷启动
                        Logger.Log("[VideoProvider] 快速路径跳过：页面为外部目录映射，换根目录图需重建页面（转冷启动）");
                        return false;
                    }
                    mediaSrcRaw = "http://dwallpaper.local" + relPath;
                }
                else
                {
                    var dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir))
                    {
                        var normDir = Path.GetFullPath(dir).TrimEnd('\\', '/');
                        if (_mappedDir != normDir)
                        {
                            Logger.Log($"[VideoProvider] 快速路径跳过：外部文件目录变化（{_mappedDir} → {normDir}），改映射必 404，转冷启动");
                            return false;
                        }
                    }
                    mediaSrcRaw = BuildMediaSrc(path);
                }
                var src = JsEscape(mediaSrcRaw);
                var css = BuildVideoFitCss();
                // 图片加载完成后的背景（fit 需黑边；fill 无影响；center 透明）
                var bgOnLoad = FitMode == "center" ? "transparent" : "#000";
                // 换图策略（r13）：【先把新图加载并绘制出来，再撤掉旧图】。
                // 旧实现在原 img 上原地改 src —— 改 src 的瞬间旧图立即从屏幕上消失，新图解码前
                // 页面是一片空白，桌面就会闪出底层（系统壁纸/空帧）：这正是"静切静闪一下"的来源。
                // 现在改为新建一个 img 叠在上面（旧图改 id 留在原地继续显示），新图加载完成、
                // 合成器真正出帧两帧之后才移除旧图——换图全过程屏幕上始终有内容，无空白帧。
                // 另：__dwpImgState 作为就绪判据（pending/ok/err），避免"旧图还完整显示"被
                // 误判为"新图已就绪"（旧图 complete=true 会让 naturalWidth 判据出现假阳性）。
                // 整段脚本存入 _lastImageNavScript，供 WaitVideoReadyAsync 在失败/悬挂时重发。
                // 叠化时长（r18）：原地换图以前是"双 rAF 后立即移除旧图"＝画面瞬间硬切，
                // 用户反馈"静切静没有切换动画"（视频↔静态走窗口级 300ms 叠化，所以"其他都有"）。
                // 现改为：新图就绪出帧后从 opacity 0 → 1 淡入覆盖旧图，淡入完成再撤旧图——
                // 屏幕内容全程连续（旧图在下层），且壁纸本身有了真正的过渡。
                const int swapFadeMs = 260;
                string setImgScript =
                    // 序号守卫：连续快速切换时以"最后一次"换图为准，避免先发起的加载晚完成时把新图清掉
                    "window.__dwpImgSeq=(window.__dwpImgSeq||0)+1;var seq=window.__dwpImgSeq;" +
                    "window.__dwpPainted=false;window.__dwpImgState='pending';" +
                    // 旧图先改 id 留在原地（不删、不清 src）：新图未画出来之前，屏幕上一直是旧图，
                    // 【换图全过程不出现任何空白帧】——这是静切静"闪一下"的直接来源。
                    "var cur=document.getElementById('i');if(cur)cur.id='i-old';" +
                    $"var n=document.createElement('img');n.id='i';" +
                    // 背景先置透明：加载期间露出下方旧图/ video，绝不出现黑屏；
                    // opacity:0 + transition：新图先以全透明"就位"（内容可正常解码出帧），就绪后再淡入
                    $"n.style.cssText='position:fixed;{BuildImageRotationCss()}{css};background:transparent;opacity:0;transition:opacity {swapFadeMs}ms linear';" +
                    // onload：新图解码完成后再等两帧（合成器真正出帧），然后淡入并撤旧图
                    $"n.onload=function(){{if(seq!==window.__dwpImgSeq)return;var self=this;window.__dwpImgState='ok';" +
                    $"var vv=document.getElementById('v');if(vv){{vv.pause();vv.style.display='none';}}self.style.background='{bgOnLoad}';" +
                    "requestAnimationFrame(function(){requestAnimationFrame(function(){" +
                    "if(seq!==window.__dwpImgSeq)return;" +
                    // 新图已出帧 → 开始淡入（旧图仍在下层，屏幕始终有内容）
                    "self.style.opacity='1';window.__dwpPainted=true;" +
                    // 淡入完成（或超时兜底）后移除旧图：此时旧图已被新图完全遮住，移除无视觉变化。
                    // 序号必须复核——中途若又换了图，self 已降级为 i-old，此时移除其他 img 会把
                    // 当前正在显示的新图删掉（黑屏）。
                    "var dropped=false;var drop=function(){if(dropped)return;dropped=true;" +
                    "if(seq!==window.__dwpImgSeq)return;var all=document.getElementsByTagName('img');" +
                    "for(var k=all.length-1;k>=0;k--){if(all[k]!==self){all[k].parentNode.removeChild(all[k]);}}};" +
                    $"self.addEventListener('transitionend',function(e){{if(e.propertyName==='opacity')drop();}});" +
                    $"setTimeout(drop,{swapFadeMs + 80});" +
                    "});});};" +
                    // onerror：撤掉失败的新图，把旧图 id 恢复成 'i' 继续显示（不残留裂图、不黑屏）；
                    // 旧页面里若有 video（动态层覆盖场景）一并恢复显示与播放
                    "n.onerror=function(){if(this.parentNode)this.parentNode.removeChild(this);" +
                    "if(seq!==window.__dwpImgSeq)return;window.__dwpImgState='err';" +
                    "var o=document.getElementById('i-old');if(o)o.id='i';" +
                    "var vv=document.getElementById('v');if(vv){vv.style.display='';vv.play();}};" +
                    $"n.src='{src}';document.body.appendChild(n);";
                _lastImageNavScript = setImgScript;
                bool ran = await WaitNavAndRunAsync(setImgScript);
                // 脚本没真正执行（导航信号超时 / COM 异常重试仍失败）时不能置 _isImage：
                // 否则页面里可能根本没有 img 元素，而 _isImage=true 会让后续每一次原地换图
                // 都因找不到 #i 而空等到超时（用户感受为“静态切换很慢”）。
                if (!ran) return false;
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
            // 恢复视频的旋转 CSS：图片覆盖期间若旋转角度有变，video 的内联样式仍是旧值
            ApplyRotation();
        }

        public void Show(string path, Rectangle bounds) => Show(path, bounds, null);

        public void Show(string path, Rectangle bounds, bool? forceImage = null)
        {
            _path = path;
            _bounds = bounds;
            // 默认按扩展名判定；无扩展名的远程图片直链会被误判为视频（<video> 无法渲染图片），
            // 调用方（静态壁纸冷启动/动切静兜底）可通过 forceImage:true 强制图片模式。
            _isImage = forceImage ?? IsImageFile(path);
            EnsureWindowClass();

            // 关键：创建为【普通（非分层）子窗口】——仅 WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW，不带 WS_EX_LAYERED。
            // 根因修复：此前带 WS_EX_LAYERED 时，分层窗口被 DWM 独立合成到图标层(DefView)之上，
            // 无论 Z 序如何排布都会盖住桌面图标、吃掉右键（即“运行中重设壁纸就盖图标”的根因）。
            // 普通子窗口挂到 Progman 之下的“背景 WorkerW”（位于 DefView 之下），由 DWM 常规合成，
            // 自然沉在图标层之后——图标常显、右键落到桌面。
            // 仅当 WorkerWInjector.Attach 退化到直接挂 Progman（noreirectionbitmap）时，才会在挂载时
            // 临时补上 WS_EX_LAYERED（那才是唯一的 layered 场景，且为罕见退化路径）。
            // 刻意不使用 WS_EX_TRANSPARENT（会让分层窗口画到 DefView 之上、盖图标）。
            _hwnd = CreateWindowEx(
                WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW,
                "DynamicWallpaperVideoHost", "DynamicWallpaper Video Host",
                0, bounds.X, bounds.Y, bounds.Width, bounds.Height,
                IntPtr.Zero, IntPtr.Zero, HInstance, IntPtr.Zero);

            if (_hwnd == IntPtr.Zero)
            {
                Logger.Log($"[VideoProvider] CreateWindowEx 失败: {Marshal.GetLastWin32Error()}");
                return;
            }

            // 注册到 hwnd→Provider 映射：窗口过程据此在窗口尺寸/DPI 变化时同步控制器 Bounds
            LiveHosts[_hwnd] = this;
            Logger.Log($"[VideoProvider] 创建宿主窗口: hwnd=0x{_hwnd.ToInt64():X} bounds={bounds.X},{bounds.Y},{bounds.Width},{bounds.Height} isImage={_isImage}");

            // 不在这里显示窗口（避免 WorkerW 获取失败时窗口在顶层闪现）；
            // 窗口会在 AttachTo 成功挂接到 WorkerW 后再 SetWindowPos 显示。
            // fire-and-forget 启动异步初始化，异常在内部 try/catch，不抛出到 UI 线程。
            _ = InitWebView2Async(path, bounds);
        }

        public void AttachTo(IntPtr workerw, Rectangle bounds)
        {
            if (_hwnd == IntPtr.Zero) return;
            WorkerWInjector.Attach(_hwnd, workerw, bounds);
            _attachedParent = workerw;
            // r17：挂载（SetParent / 改窗口样式 / 落位 / 屏外停放）完成后才允许创建 WebView2 控制器。
            // 见 _attachTcs 注释：两者并发时控制器创建必失败并退避 300ms，且拿到错误的窗口/DPI 上下文。
            _attachTcs.TrySetResult(true);
            // 【不再在此立即显示】Attach 已把窗口挂到壁纸层、并临时移到父客户区之外保持不可见。
            // 等 WallpaperManager 确认内容就绪后，再调 WorkerWInjector.ShowWallpaperWindow 移入正确位置。
            // 若在此就显示，切换 A→B 时会闪出新窗口的空白帧/底层系统壁纸（用户反馈的“切换闪一下”）。
        }

        /// <summary>把 WebView2 控制器尺寸重新对齐到宿主窗口客户区（客户区像素 ÷ RasterizationScale）。
        /// 见 WndProcImpl 注释：窗口被改尺寸或跨显示器 DPI 变化后，控制器 Bounds 若不回写，
        /// 内容会按旧尺寸绘制并锚在客户区左上角（“壁纸偏移到桌面左上角”）。幂等、代价极低。</summary>
        public void SyncControllerBounds(string reason, bool force = false)
        {
            var ctrl = _controller;
            if (ctrl == null || _hwnd == IntPtr.Zero || _disposed) return;
            try
            {
                if (!GetClientRect(_hwnd, out var rc)) return;
                int cw = rc.right - rc.left;
                int ch = rc.bottom - rc.top;
                if (cw <= 0 || ch <= 0) return;
                double scale = 1.0;
                try { var s = ctrl.RasterizationScale; if (s > 0.01) scale = s; } catch { }
                int w = Math.Max(1, (int)Math.Round(cw / scale));
                int h = Math.Max(1, (int)Math.Round(ch / scale));
                var cur = ctrl.Bounds;
                if (!force && cur.X == 0 && cur.Y == 0 && cur.Width == w && cur.Height == h) return;
                ctrl.Bounds = new Rectangle(0, 0, w, h);
                Logger.Log($"[VideoProvider] 控制器尺寸同步({reason}): 客户区={cw}x{ch}px scale={scale:0.##} → Bounds={w}x{h}（旧={cur.X},{cur.Y},{cur.Width},{cur.Height}）");
            }
            catch (Exception ex)
            {
                Logger.Log($"[VideoProvider] 控制器尺寸同步失败: {ex.Message}");
            }
        }

        /// <summary>渲染比例与几何复核（r17）：页面视口(DIP) × devicePixelRatio 必须等于宿主客户区像素。
        /// 不相等 = 控制器 Bounds 的 DIP/像素换算错（内容按错误比例放大 → 屏幕上只看到左上角一块，
        /// 即用户报的"偏移/中间裁切"）；这类错误【页面内布局断言看不见】——img 铺满的是那个过大的视口，
        /// getBoundingClientRect 与 innerWidth 完全自洽，只有把视口换算成像素与客户区对比才能发现。
        /// 发现不符即按客户区强制重写 Bounds 并复测。返回 true=一致（或无法判定）。</summary>
        public async Task<bool> VerifyRenderScaleAsync(string reason)
        {
            var ctrl = _controller;
            if (ctrl == null || _hwnd == IntPtr.Zero || _disposed) return true;
            try
            {
                if (!GetClientRect(_hwnd, out var rc)) return true;
                int cw = rc.right - rc.left, ch = rc.bottom - rc.top;
                if (cw <= 0 || ch <= 0) return true;
                string? pr = await RunOnUiThreadAsync(() => ctrl.CoreWebView2.ExecuteScriptAsync(
                    "(function(){return {iw:innerWidth,ih:innerHeight,dpr:devicePixelRatio};})()"));
                if (string.IsNullOrEmpty(pr)) return true;
                using var doc = System.Text.Json.JsonDocument.Parse(pr);
                var r = doc.RootElement;
                int iw = r.TryGetProperty("iw", out var e1) ? e1.GetInt32() : 0;
                int ih = r.TryGetProperty("ih", out var e2) ? e2.GetInt32() : 0;
                double dpr = r.TryGetProperty("dpr", out var e3) ? e3.GetDouble() : 0;
                if (iw <= 0 || ih <= 0 || dpr <= 0) return true;
                double pxW = iw * dpr, pxH = ih * dpr;
                if (Math.Abs(pxW - cw) <= 2 && Math.Abs(pxH - ch) <= 2) return true;

                Logger.Log($"[VideoProvider] 渲染比例不符({reason}): 页面视口={iw}x{ih} dpr={dpr:0.###} → {pxW:0}x{pxH:0}px，宿主客户区={cw}x{ch}px（内容会按错误比例渲染并锚在左上角）→ 按客户区强制重同步控制器尺寸");
                SyncControllerBounds($"渲染比例校准·{reason}", force: true);
                await Task.Delay(150);
                string? pr2 = await RunOnUiThreadAsync(() => ctrl.CoreWebView2.ExecuteScriptAsync(
                    "(function(){return innerWidth+'x'+innerHeight+'@'+devicePixelRatio;})()"));
                Logger.Log($"[VideoProvider] 渲染比例校准后({reason}): {pr2}（期望视口≈{(int)Math.Round(cw / dpr)}x{(int)Math.Round(ch / dpr)}）");
                return false;
            }
            catch (Exception ex)
            {
                Logger.Log($"[VideoProvider] 渲染比例复核异常({reason}): {ex.Message}");
                return true;
            }
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

        /// <summary>运行时切换旋转：立即按新角度重写 img / video 的定位/尺寸/transform CSS。
        /// 图片与视频统一处理：90°/270° 时宽高互换（宽=100vh、高=100vw）再以中心旋转，
        /// 180° 仅中心旋转，0° 恢复铺满。适应方式（object-fit）始终跟随设置里的 FitMode。</summary>
        public void ApplyRotation()
        {
            var rot = BuildImageRotationCss();
            var fit = BuildVideoFitCss();
            var bg = FitMode == "center" ? "transparent" : "#000";
            if (_isImage)
            {
                Logger.Log($"[VideoProvider] ApplyRotation 应用旋转：{Rotation}°（img 模式）");
                RunJs($"var i=document.getElementById('i');if(i){{i.style.cssText='position:fixed;{rot}{fit};background:{bg};';}}");
            }
            else
            {
                Logger.Log($"[VideoProvider] ApplyRotation 应用旋转：{Rotation}°（video 模式）");
                RunJs($"var v=document.getElementById('v');if(v){{v.style.cssText='position:fixed;{rot}{fit};';}}");
            }
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
        public async Task<bool> WaitVideoReadyAsync(TimeSpan timeout, bool retryImageOnError = false)
        {
            // InfiniteTimeSpan 表示无限等待（由 WallpaperManager 用取消令牌打断），
            // 用于网络/慢速加载场景：用户不主动切换/解除/退出就持续等待，不做超时回退。
            var deadline = timeout == Timeout.InfiniteTimeSpan ? DateTime.MaxValue : DateTime.UtcNow + timeout;
            var startedAt = DateTime.UtcNow;
            int imgRetries = 0;
            bool hungRetried = false;
            // 请求悬挂起点（r17：原按"轮询次数≥30 次"判定＝3s，本地图片毫秒级可加载，
            // 3s 无 onload/onerror 基本可判定请求被网络栈吞掉；改为按时间 800ms 判定，
            // 让"解除→立刻重设"这类悬挂场景提前自愈，而不是先空等满 3s）。
            DateTime? pendingSince = null;
            // 内容已解码（①）但尚未出帧（②）的起始时刻：用于"等到真正出帧，最多多等 400ms"的兜底
            DateTime? okSince = null;
            while (DateTime.UtcNow < deadline)
            {
                if (_disposed) return false;
                var ctrl = _controller;
                var tcs = _navTcs;
                // 注意：这里不能访问 ctrl.CoreWebView2——该属性是 COM 对象上的跨线程调用，
                // 在非创建线程（如轮播定时器线程）会抛 "Unable to cast COM object ...
                // ICoreWebView2Controller"。只判 _controller/_navTcs 引用（普通字段读取安全），
                // 真正的 CoreWebView2 访问统一在下方 RunOnUiThreadAsync 内（已调度回创建线程）。
                if (ctrl == null || tcs == null || !tcs.Task.IsCompleted)
                {
                    // Controller / 导航信号尚未就绪（异步初始化中，可能含失败重试），继续等待
                    await Task.Delay(100);
                    continue;
                }
                if (!tcs.Task.Result) return false; // 导航失败，页面不会有内容
                try
                {
                    // 就绪 = ① 内容已解码可显示；② 合成器已真正出帧（__dwpPainted）。
                    // ① 视频：readyState>=3（首帧可用）；图片：__dwpImgState==='ok'（原地换图路径
                    //   显式置位，防止"旧图还完整显示"造成假阳性），冷启动页（未置位）退回
                    //   complete && naturalWidth>0 判据。
                    // ② 只用 ① 判定会让窗口在"内容解码完但还没画到屏幕"的瞬间被移入显示 →
                    //   露出一帧空白（用户看到的"切换闪一下"，且时好时坏）。__dwpPainted 由页面在
                    //   requestVideoFrameCallback / playing（视频）或双 requestAnimationFrame（图片）
                    //   时置位——即真正提交给合成器之后。拿不到该信号时最多多等 400ms 便照常继续，
                    //   绝不因为信号缺失而卡住切换。
                    // 注意不能要求"正在播放"：开机自启时锁屏（LockApp）常被误判为全屏而暂停壁纸，
                    // 若就绪依赖 !paused，启动恢复会永远等不到就绪 → 串行锁被占死，
                    // 之后所有"设为壁纸/解除"全部排队卡死（表现：只有切换动画、壁纸不显示）。
                    var script = _isImage
                        ? "(function(){var st=window.__dwpImgState;var i=document.getElementById('i');" +
                          "var ok=(st==='ok')||(st===undefined&&i&&i.complete&&i.naturalWidth>0);" +
                          "return (ok?1:0)+'|'+(window.__dwpPainted?1:0);})()"
                        : "(function(){var v=document.getElementById('v');" +
                          "return ((v&&v.readyState>=3)?1:0)+'|'+(window.__dwpPainted?1:0);})()";
                    // ExecuteScriptAsync 必须回到 Controller 创建线程执行（跨线程抛 COM 异常）
                    var r = await RunOnUiThreadAsync(() => ctrl.CoreWebView2.ExecuteScriptAsync(script));
                    bool contentOk = ParseFlag(r, 0);
                    bool painted = ParseFlag(r, 1);
                    if (contentOk)
                    {
                        if (painted)
                        {
                            // 取证：记录"内容已解码 → 合成器真正出帧"的等待时长。若该值长期 >0，
                            // 说明此前"内容就绪即移入显示"确实移入过一个还没画出来的窗口（闪一下的根因）。
                            int gateMs = okSince.HasValue ? (int)(DateTime.UtcNow - okSince.Value).TotalMilliseconds : 0;
                            Logger.Log($"[VideoProvider] 壁纸就绪：内容已解码且合成器已出帧（出帧追加等待 {gateMs}ms）");
                            // 图片模式：就绪后再断言一次实际布局（窗口/控制器/视图层几何均已验证正确，
                            // 若仍偏移只可能出在页面内 img 的布局层——见 VerifyAndHealImageLayoutAsync）
                            if (_isImage && !await VerifyAndHealImageLayoutAsync(ctrl))
                            {
                                if (retryImageOnError)
                                {
                                    // 快速路径（原地换图）：自愈无效说明该页面的布局状态已坏，
                                    // 返回 false 让调用方走冷启动重建页面（实测冷启动渲染始终正确）
                                    Logger.Log("[VideoProvider] 快速换图布局自愈无效，转冷启动重建页面");
                                    return false;
                                }
                            }
                            return true;
                        }
                        // 内容已解码但尚未出帧：继续等（正常在 1~2 帧内到达），超 400ms 放行
                        okSince ??= DateTime.UtcNow;
                        if ((DateTime.UtcNow - okSince.Value).TotalMilliseconds >= 400)
                        {
                            Logger.Log("[VideoProvider] 内容已就绪但 400ms 内未收到出帧信号，按就绪继续（防卡死）");
                            if (_isImage && !await VerifyAndHealImageLayoutAsync(ctrl))
                            {
                                if (retryImageOnError)
                                {
                                    Logger.Log("[VideoProvider] 快速换图布局自愈无效（400ms 放行路径），转冷启动重建页面");
                                    return false;
                                }
                            }
                            return true;
                        }
                    }
                    else
                    {
                        okSince = null;
                    }

                    // 图片模式：onerror 已重绑（NavigateImageAsync），__dwpImgState='err' 表示
                    // 加载已明确失败——绝不能再干等到超时（静切静 2s 空等的元凶）。
                    // retryImageOnError=true（快速路径）：失败大概率是虚拟主机映射变更的 IPC
                    // 竞态（新目录未生效即 404），延迟 300ms 重跑换图脚本即可自愈，最多 3 次；
                    // 重试耗尽仍 err 则快速返回 false 转冷启动，不再占用剩余等待时间。
                    if (_isImage && !contentOk)
                    {
                        var st = await RunOnUiThreadAsync(() =>
                            ctrl.CoreWebView2.ExecuteScriptAsync("String(window.__dwpImgState||'')"));
                        if (st != null && st.IndexOf("err", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            if (!retryImageOnError || imgRetries >= 3)
                            {
                                Logger.Log($"[VideoProvider] 图片加载失败（onerror），重试={imgRetries}次，快速失败转后续路径");
                                return false;
                            }
                            imgRetries++;
                            pendingSince = null;
                            Logger.Log($"[VideoProvider] 图片加载失败（onerror，疑为虚拟主机映射竞态 404），300ms 后自动重试换图（第{imgRetries}/3次）");
                            await Task.Delay(300);
                            var retry = _lastImageNavScript;
                            if (string.IsNullOrEmpty(retry)) return false;
                            await RunOnUiThreadAsync(() => ctrl.CoreWebView2.ExecuteScriptAsync(retry));
                            continue;
                        }
                        // 请求悬挂检测（r17：按时间 800ms，原为轮询 30 次≈3s）：img 一直 pending
                        // （complete=false，onload/onerror 均未触发）持续超过 800ms ＝ 网络栈把请求吞了
                        // （实测场景：解除→立刻重设时首个请求无响应，表现为"解除后再设置失败一次"）。
                        // 重发一次换图脚本强制重新发起请求；图片是本地文件毫秒级可加载，
                        // 800ms 无响应基本可判定请求已丢失而非在加载。
                        if (!retryImageOnError)
                        {
                            pendingSince ??= DateTime.UtcNow;
                            if (!hungRetried && (DateTime.UtcNow - pendingSince.Value).TotalMilliseconds >= 800)
                            {
                                hungRetried = true;
                                var retry = _lastImageNavScript;
                                if (!string.IsNullOrEmpty(retry))
                                {
                                    Logger.Log($"[VideoProvider] 图片请求悬挂超 800ms（onload/onerror 均未触发），重发换图脚本强制重新请求");
                                    await RunOnUiThreadAsync(() => ctrl.CoreWebView2.ExecuteScriptAsync(retry));
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    // 单次轮询异常不直接判失败：导航刚完成窗口期 ExecuteScriptAsync 偶发
                    // COM 异常，继续轮询直到超时（避免"内容已就绪却误判失败"）。
                    Logger.Log($"[VideoProvider] WaitVideoReady 轮询异常（继续等待）: {ex.Message}");
                }
                // r17：前 1.2s 用 30ms 轮询（本地图片毫秒级就绪，原固定 100ms 会把"就绪→移入显示"
                // 平均推后约 50ms，静切静被感知为"慢半拍"）；之后回落到 100ms 省开销。
                await Task.Delay((DateTime.UtcNow - startedAt).TotalMilliseconds < 1200 ? 30 : 100);
            }
            return false;
        }

        /// <summary>解析就绪脚本返回的 "标志0|标志1" 结果（ExecuteScriptAsync 返回的是 JSON 字符串，
        /// 形如 "1|0"，带引号）。index=0 → 内容已解码；index=1 → 已真正出帧。</summary>
        private static bool ParseFlag(string? json, int index)
        {
            if (string.IsNullOrEmpty(json)) return false;
            var parts = json.Trim().Trim('"').Split('|');
            return parts.Length > index && parts[index].Trim() == "1";
        }

        /// <summary>图片就绪后的布局断言（r11）：核对 img 实际渲染矩形是否铺满视口。
        /// 背景：r9/r10 已把窗口层（ShowWallpaperWindow 落位校验）、控制器层（SyncControllerBounds）、
        /// 视图层（2s 复核枚举 Chrome_WidgetWin）三层几何全部验证正确，但"AI暗黑鎏金神性写真"
        /// 仍规律性偏移（仅此图，移除库重加依旧）——偏移只可能发生在页面内 img 的布局层。
        /// 就绪时采集 img 矩形 + naturalWidth/Height + 视口 + 计算样式：
        /// ① 布局正常 → 静默通过（零日志零开销）；
        /// ② 布局异常 → 记完整取证日志并重套完整 CSS 自愈（若偏移源是样式丢失/被重置则当场修复）；
        /// ③ 若自愈后仍异常（或矩形本身正确却仍视觉偏移），naturalW/H、rect、innerW/H、pos/fit
        ///    组合足以区分 EXIF 方向 / 样式未生效 / 合成器异常等具体机制，不再盲修。</summary>
        private async Task<bool> VerifyAndHealImageLayoutAsync(CoreWebView2Controller ctrl)
        {
            string? before = null;
            try
            {
                // 返回对象本体（不要 JSON.stringify——ExecuteScriptAsync 会对返回值再做一次 JSON 编码，
                // 对象直接返回得到的才是可直接解析的 JSON 文本）
                const string Probe =
                    "(function(){var i=document.getElementById('i');if(!i)return 'noimg';" +
                    "var r=i.getBoundingClientRect();var cs=getComputedStyle(i);" +
                    "return {nw:i.naturalWidth,nh:i.naturalHeight," +
                    "x:Math.round(r.left),y:Math.round(r.top),w:Math.round(r.width),h:Math.round(r.height)," +
                    "iw:innerWidth,ih:innerHeight,pos:cs.position,fit:cs.objectFit,dpr:devicePixelRatio," +
                    "sty:(i.getAttribute('style')||'').slice(0,300),lf:cs.left,tp:cs.top,tf:cs.transform.slice(0,60)};})()";
                before = await RunOnUiThreadAsync(() => ctrl.CoreWebView2.ExecuteScriptAsync(Probe));
                if (string.IsNullOrEmpty(before) || before.Contains("noimg")) return true;

                using var doc = System.Text.Json.JsonDocument.Parse(before);
                var root = doc.RootElement;
                int nw = root.TryGetProperty("nw", out var eNw) ? eNw.GetInt32() : 0;
                int nh = root.TryGetProperty("nh", out var eNh) ? eNh.GetInt32() : 0;
                int x = root.TryGetProperty("x", out var eX) ? eX.GetInt32() : 0;
                int y = root.TryGetProperty("y", out var eY) ? eY.GetInt32() : 0;
                int w = root.TryGetProperty("w", out var eW) ? eW.GetInt32() : 0;
                int h = root.TryGetProperty("h", out var eH) ? eH.GetInt32() : 0;
                int iw = root.TryGetProperty("iw", out var eIw) ? eIw.GetInt32() : 0;
                int ih = root.TryGetProperty("ih", out var eIh) ? eIh.GetInt32() : 0;
                string pos = root.TryGetProperty("pos", out var eP) ? (eP.GetString() ?? "") : "";
                string fit = root.TryGetProperty("fit", out var eF) ? (eF.GetString() ?? "") : "";
                double dpr = root.TryGetProperty("dpr", out var eD) ? eD.GetDouble() : 0;

                // 期望：img 盒铺满视口（90°/270° 时元素盒先按 100vh×100vw 布局再旋转，
                // getBoundingClientRect 返回变换后包围盒，仍应等于视口）。容差 2px。
                bool off = pos != "fixed" ||
                           Math.Abs(x) > 2 || Math.Abs(y) > 2 ||
                           Math.Abs(w - iw) > 2 || Math.Abs(h - ih) > 2;

                // 一次性渲染快照（r12）：无论布局是否正常都记录一条，用于判定"看到的方向/裁切"
                // 究竟来自①应用旋转（本类 Rotation）②图片 EXIF 方向（浏览器自动应用）
                // ③object-fit 裁切（FitMode）。三项数据合起来一眼定性，不再靠猜。
                if (Interlocked.Exchange(ref _renderSnapLogged, 1) == 0)
                    Logger.Log($"[VideoProvider] 图片渲染快照: 应用旋转={Rotation}° 适应={FitMode} " +
                               $"渲染尺寸={nw}x{nh} rect=({x},{y},{w}x{h}) 视口={iw}x{ih} dpr={dpr:0.##} " +
                               $"{DescribeSourceImage()} 布局判定={(off ? "异常" : "正常")}");

                if (!off) return true;

                // 取证：内联样式属性实值 + 计算样式关键项（r14）——直接看浏览器里真实生效的字符串，
                // 区分"程序写入的样式就错了"与"样式正确但浏览器计算异常"
                string sty = root.TryGetProperty("sty", out var eS) ? (eS.GetString() ?? "") : "";
                string lf = root.TryGetProperty("lf", out var eLf) ? (eLf.GetString() ?? "") : "";
                string tp = root.TryGetProperty("tp", out var eTp) ? (eTp.GetString() ?? "") : "";
                string tf = root.TryGetProperty("tf", out var eTf) ? (eTf.GetString() ?? "") : "";
                Logger.Log($"[VideoProvider] 图片布局异常(取证): naturalW/H={nw}x{nh} rect=({x},{y},{w}x{h}) 视口={iw}x{ih} pos={pos} fit={fit} dpr={dpr:0.##} left={lf} top={tp} tf={tf} 内联样式=\"{sty}\" → 重套CSS自愈");

                // 自愈：重套完整定位/尺寸/适应 CSS（与 ApplyRotation img 分支同一份）
                var rot = BuildImageRotationCss();
                var css = BuildVideoFitCss();
                var bg = FitMode == "center" ? "transparent" : "#000";
                await RunOnUiThreadAsync(() => ctrl.CoreWebView2.ExecuteScriptAsync(
                    $"var i=document.getElementById('i');if(i){{i.style.cssText='position:fixed;{rot}{css};background:{bg};';}}"));
                await Task.Delay(150);
                var after = await RunOnUiThreadAsync(() => ctrl.CoreWebView2.ExecuteScriptAsync(Probe));
                bool healed = false;
                try
                {
                    using var doc2 = System.Text.Json.JsonDocument.Parse(after ?? "null");
                    var r2 = doc2.RootElement;
                    int ax = r2.TryGetProperty("x", out var aX) ? aX.GetInt32() : -9999;
                    int ay = r2.TryGetProperty("y", out var aY) ? aY.GetInt32() : -9999;
                    int aw = r2.TryGetProperty("w", out var aW) ? aW.GetInt32() : -9999;
                    int ah = r2.TryGetProperty("h", out var aH) ? aH.GetInt32() : -9999;
                    string apos = r2.TryGetProperty("pos", out var aP) ? (aP.GetString() ?? "") : "";
                    healed = apos == "fixed" && Math.Abs(ax) <= 2 && Math.Abs(ay) <= 2 &&
                             Math.Abs(aw - iw) <= 2 && Math.Abs(ah - ih) <= 2;
                }
                catch { }
                Logger.Log($"[VideoProvider] 图片布局自愈后: {(healed ? "已恢复正常" : "仍异常")} {after}");
                return healed;
            }
            catch (Exception ex)
            {
                // 断言失败不影响就绪判定（图已加载、已显示），只丢证据
                Logger.Log($"[VideoProvider] 图片布局断言异常（继续）: {ex.Message} probe={before}");
                return true;
            }
        }

        /// <summary>读取磁盘上图片的存储像素尺寸与 EXIF 方向标签（System.Drawing 不应用 EXIF，
        /// 因此与浏览器渲染出的 naturalWidth/Height 对照即可判定方向是否被 EXIF 改写）。
        /// EXIF 0x0112 取值：1=正常 2=水平镜像 3=旋转180 4=垂直镜像 5=镜像+90CW 6=旋转90CW
        /// 7=镜像+270CW 8=旋转270CW（即逆时针90°）。</summary>
        private string DescribeSourceImage()
        {
            try
            {
                var p = _path;
                if (string.IsNullOrEmpty(p) || IsRemoteUrl(p)) return "存储=远程/无";
                if (!File.Exists(p)) return "存储=文件不存在";
                using var fs = File.OpenRead(p);          // 流式读取，避免 FromFile 锁住壁纸文件
                using var img = System.Drawing.Image.FromStream(fs);
                int exif = 0;
                try
                {
                    var pi = img.GetPropertyItem(0x0112);
                    if (pi?.Value != null && pi.Value.Length >= 2)
                        exif = BitConverter.ToUInt16(pi.Value, 0);
                }
                catch { /* 无 EXIF 属性项（PNG/已剥离/无标签） */ }
                string exifText = exif switch
                {
                    0 or 1 => "无(正常)",
                    3 => "3(旋转180°)",
                    6 => "6(顺时针90°)",
                    8 => "8(逆时针90°=270CW)",
                    _ => exif.ToString()
                };
                return $"存储={img.Width}x{img.Height} EXIF方向={exifText}";
            }
            catch (Exception ex)
            {
                return "存储读取失败:" + ex.Message;
            }
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
                LiveHosts.TryRemove(_hwnd, out _);
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

                // r17：等宿主窗口完成挂载再创建控制器。
                // 此前两者并发（构造里 fire-and-forget 本方法，AttachTo 由调用方另行触发），
                // Attach 正在 SetParent/SetWindowLong/SWP_FRAMECHANGED 时创建控制器必然失败：
                // 日志实证每次冷启动都是「第1次失败(0x8007139F) → 退避 300ms → 第2次成功」，
                // 白白多花 300ms（切换慢），且控制器是在窗口"挂载中途"的样式/DPI 上下文里建立的。
                // 3s 超时兜底：万一调用方没走 AttachTo（异常路径），仍按老流程继续，绝不卡死初始化。
                try
                {
                    if (!await _attachTcs.Task.WaitAsync(TimeSpan.FromSeconds(3)))
                        Logger.Log("[VideoProvider] 等待窗口挂载超时，按未挂载状态继续创建控制器");
                }
                catch (TimeoutException)
                {
                    Logger.Log("[VideoProvider] 等待窗口挂载超时(3s)，按未挂载状态继续创建控制器");
                }
                catch { }
                if (_disposed || _hwnd == IntPtr.Zero) return;

                // 创建 Controller 增加失败重试：动态壁纸 A→B 无缝切换瞬间，旧壁纸 A 的
                // WebView2 Controller 可能尚未完全释放，同一 Environment 上创建 Controller
                // 会短暂失败（0x8007139F 组或资源的状态不正确）。
                // 6 次渐进退避（300→500→800→1200→1800ms），比固定 3×500ms 更可靠。
                // r17：正常路径下上面的"等挂载"已消除这一失败；保留重试仅作最后兜底。
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

                // r17：控制器 Bounds 的单位是 DIP（与页面视口同单位），而 bounds 是屏幕像素。
                // 此前直接写 bounds.Width/Height（像素）当 DIP 用：在缩放率 ≠100% 的显示器上，
                // 若随后的 SyncControllerBounds("初始化完成") 那一刻 RasterizationScale 还报 1.0
                // （控制器刚建、尚未绑定到目标显示器），Bounds 就永久停在像素值 → 页面视口比客户区大
                // 缩放率倍 → 内容整体放大并锚在左上角，用户看到的就是"壁纸只显示左上角一块/中间裁切"。
                // 这里直接按 DIP 写入，从源头消除该换算错误（后续仍会按客户区复核）。
                double initScale = 1.0;
                try { var s = ctrl.RasterizationScale; if (s > 0.01) initScale = s; } catch { }
                ctrl.Bounds = new Rectangle(0, 0,
                    Math.Max(1, (int)Math.Round(bounds.Width / initScale)),
                    Math.Max(1, (int)Math.Round(bounds.Height / initScale)));
                // WebView2 默认页面背景是白色（不透明），视频解码渲染前窗口会合成出白底，
                // 切换叠化期间会"白屏闪过"。设为透明后，视频未就绪时窗口完全透明（露出旧壁纸）。
                ctrl.DefaultBackgroundColor = System.Drawing.Color.Transparent;
                ctrl.IsVisible = true;
                ctrl.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                ctrl.CoreWebView2.Settings.IsStatusBarEnabled = false;

                // 虚拟主机映射（r10）：本地文件通过 http://dwallpaper.local/ 加载（不受 CORS 限制）。
                // 【映射一律设为 Wallpapers 根目录】（媒体在根下时）——根映射覆盖所有子目录，
                // 之后快速路径跨子目录换图【完全不需要改映射】，根治"对已加载页面改映射 → 新请求
                // 持续 404"（r9 实测 3 次重试全部失败，证明该映射变更只对之后的导航生效）。
                // 媒体不在根下（外部文件）才退回按文件目录映射。
                var isUrl = IsRemoteUrl(path);
                _path = path;
                string mediaSrcRaw;
                if (isUrl)
                {
                    mediaSrcRaw = path; // 远程 URL 直接使用原链接流播，不做文件夹映射
                    _mappedDir = null;
                }
                else
                {
                    var relPath = TryGetWallpapersRelativePath(path);
                    if (relPath != null)
                    {
                        var wallRoot = Path.GetFullPath(Path.Combine(AppPaths.RootDirectory, "Wallpapers"));
                        ctrl.CoreWebView2.SetVirtualHostNameToFolderMapping(
                            "dwallpaper.local", wallRoot, CoreWebView2HostResourceAccessKind.Allow);
                        _mappedDir = RootMappingMark;
                        mediaSrcRaw = "http://dwallpaper.local" + relPath;
                    }
                    else
                    {
                        var dir = Path.GetDirectoryName(path) ?? "";
                        if (!string.IsNullOrEmpty(dir))
                            ctrl.CoreWebView2.SetVirtualHostNameToFolderMapping(
                                "dwallpaper.local", dir, CoreWebView2HostResourceAccessKind.Allow);
                        _mappedDir = string.IsNullOrEmpty(dir) ? null : Path.GetFullPath(dir).TrimEnd('\\', '/');
                        mediaSrcRaw = BuildMediaSrc(path);
                    }
                }

                var mediaSrc = HtmlEscape(mediaSrcRaw);

                // video 始终带 muted 启动（保证 autoplay 必成功），再通过内嵌 script 在
                // loadedmetadata 时按 _muted 应用真实静音状态——非静音时取消静音继续播放。
                // 不能只依赖外部 SetMuted 的 RunJs：Controller 创建是异步的，早期调用会被丢弃，
                // 页面加载后没有任何机制再取消静音，表现为"未勾选静音却始终无声"。
                string html;
                if (_isImage)
                {
                    // 静态图片模式：本地文件走虚拟主机，远程 URL 直接加载。
                    // 窗口背景透明，图片加载完成前露出下层（旧动态层），就绪后无缝直切。
                    // 内联 style 与 <style> 选择器双保险：偏移取证（r11 截图）显示个别图片的
                    // img 会以"自然尺寸顶左对齐"渲染（布局样式完全未生效的形态），
                    // 内联样式优先级最高，杜绝任何样式表解析/应用环节的意外。
                    // r15：<style> 只保留不含旋转的几何基线（PageBaseCss），旋转全部由内联承担——
                    // 否则原地换图换成 0° 壁纸时，建页时的旧旋转会从样式表穿透生效（见 BuildImageRotationCss）。
                    html = $"<html><head><style>{PageBaseCss}</style></head><body><img id='i' src='{mediaSrc}' style=\"position:fixed;{BuildImageRotationCss()}{BuildVideoFitCss()}\"><script>var i=document.getElementById('i');function w(){{requestAnimationFrame(function(){{requestAnimationFrame(function(){{window.__dwpPainted=true;}});}});}}if(i){{if(i.complete&&i.naturalWidth>0)w();else i.addEventListener('load',w);}}</script></body></html>";
                }
                else
                {
                    string mutedJs = _muted ? "true" : "false";
                    // 注意：不注册 canplay 自动 play——Pause() 暂停后若触发 canplay 事件会被误恢复播放
                    // （全屏暂停失效的隐患之一）；播放由 autoplay + loadedmetadata 保证。
                    // r15：旋转从 <style> 移到 video 的内联样式（与 img 分支一致），
                    // 保证样式表里不含任何旋转，杜绝"样式表旋转 + 内联尺寸"的嵌合体渲染。
                    html = $"<html><head><style>{PageBaseCss}</style></head><body><video id='v' src='{mediaSrc}' style=\"position:fixed;{BuildImageRotationCss()}{BuildVideoFitCss()}\" autoplay muted loop playsinline></video><script>var v=document.getElementById('v');v.addEventListener('loadedmetadata',function(){{v.muted={mutedJs};v.volume={(_muted ? 0 : 1)};v.play();}});function w(){{requestAnimationFrame(function(){{requestAnimationFrame(function(){{window.__dwpPainted=true;}});}});}}if(v.requestVideoFrameCallback){{try{{v.requestVideoFrameCallback(function(){{window.__dwpPainted=true;}});}}catch(e){{}}}}v.addEventListener('playing',w);</script></body></html>";
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
                // 跨显示器缩放率变化：WebView2 会自行改 RasterizationScale（ShouldDetectMonitorScaleChanges
                // 默认 true），必须回写 Bounds 才能继续铺满客户区，否则内容缩到左上角。
                try
                {
                    ctrl.RasterizationScaleChanged += (_, _) =>
                    {
                        var d = _uiDispatcher;
                        if (d == null || d.CheckAccess()) SyncControllerBounds("缩放率变化");
                        else { try { d.BeginInvoke(() => SyncControllerBounds("缩放率变化")); } catch { } }
                    };
                }
                catch { /* 事件订阅失败不影响主流程（WM_DPICHANGED 仍会兜底） */ }
                // 初始化期间窗口可能已被 Attach 改成别的尺寸（创建尺寸与当前客户区不一致），
                // 就绪后按客户区再对齐一次，杜绝“控制器尺寸 ≠ 窗口尺寸”的左上角偏移。
                SyncControllerBounds("初始化完成");
                // 记录控制器关键参数：窗口几何全对而内容仍偏移时，配合"移入2秒后视图层"日志
                // 判断是否 RasterizationScale 异常（内容整体放大/缩小导致裁剪或错位）。
                try
                {
                    var cb = ctrl.Bounds;
                    Logger.Log($"[VideoProvider] 控制器就绪: hwnd=0x{_hwnd.ToInt64():X} scale={ctrl.RasterizationScale:0.###} Bounds={cb.X},{cb.Y},{cb.Width}x{cb.Height} 映射={( _mappedDir == RootMappingMark ? "Wallpapers根" : (_mappedDir ?? "无"))}");
                }
                catch { }
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
        /// 此处先轮询等待其就绪（最多 1s），再等待导航完成。
        /// 返回 true 表示脚本**确实执行成功**；false 表示因未就绪/超时/导航失败/COM 异常而没执行
        /// ——调用方（原地换图快速路径）必须据此判断，否则会把“JS 没跑”误当成“换图已生效”而空等。</summary>
        private async Task<bool> WaitNavAndRunAsync(string script)
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
                    if (done != tcs.Task) return false; // 超时
                    if (!tcs.Task.Result) return false; // 导航失败，不执行 JS
                }
                var ctrl = _controller;
                if (ctrl == null) return false;
                try
                {
                    // ExecuteScriptAsync 必须回到 Controller 创建线程执行：
                    // 跨线程访问 WebView2 COM 对象会抛 "Unable to cast COM object ... ICoreWebView2Controller"
                    await RunOnUiThreadAsync(() => ctrl.CoreWebView2.ExecuteScriptAsync(script));
                    return true;
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
                        return true;
                    }
                    catch (Exception ex2)
                    {
                        Logger.Log($"[VideoProvider] JS 执行失败（重试后仍失败）: {ex2.Message}");
                        return false;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[VideoProvider] JS 执行失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>原地换图快速路径未就绪时的自诊断：把图片元素的真实状态（元素是否存在 / 是否加载完 /
        /// 自然尺寸 / onload-onerror 结果 / 当前地址 / 页面里有没有 video）一次性打出来，
        /// 用于一次定位“快速路径为什么一直不就绪”，而不是靠猜。
        /// 返回可直接拼进日志的一行文本。</summary>
        public async Task<string> DiagnoseImageAsync()
        {
            try
            {
                var ctrl = _controller;
                if (ctrl == null) return "controller=null（控制器尚未创建）";
                var script =
                    "var i=document.getElementById('i');" +
                    "JSON.stringify({hasImg:!!i," +
                    "complete:i?i.complete:null," +
                    "naturalW:i?i.naturalWidth:null," +
                    "state:(window.__dwpImgState||'(未触发)')," +
                    "src:i?String(i.currentSrc||i.src||'').slice(0,120):''," +
                    "hasVideo:!!document.getElementById('v')});";
                var r = await RunOnUiThreadAsync(() => ctrl.CoreWebView2.ExecuteScriptAsync(script));
                return $"isImage={_isImage} navOk={_navTcs?.Task.IsCompleted} {r}";
            }
            catch (Exception ex)
            {
                return "诊断失败: " + ex.Message;
            }
        }

        #endregion
    }
}
