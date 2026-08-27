using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;
using DynamicWallpaper.Desktop;
using DynamicWallpaper.Models;
using DynamicWallpaper.Providers;

namespace DynamicWallpaper.Core
{
    /// <summary>
    /// 调度总控：按屏幕管理各自的壁纸，并统一处理自动暂停（全屏、电池）与看门狗恢复。
    /// </summary>
    public class WallpaperManager
    {
        private class ScreenState
        {
            public int Index;
            public Rectangle Bounds;
            public IWallpaperProvider? Provider;
            public IntPtr WorkerW;
            public string LastPath = "";
            public WallpaperType LastType;
            /// <summary>静态图片走系统 API（SPI_SETDESKWALLPAPER）直接设置，不创建 WPF Provider。</summary>
            public bool IsStaticImage;
        }

        private readonly Config _config;
        private readonly Dictionary<int, ScreenState> _states = new();
        private readonly FullscreenMonitor _fs;
        private readonly PowerManager _power;
        private readonly System.Timers.Timer _watchdog = new(3000);
        /// <summary>串行化所有变更屏幕壁纸状态的操作（设置/清除/停止），防止启动自动恢复
        /// 与手动"设为壁纸"并发执行导致 Provider 被交叉 Dispose、渲染窗口被误关。</summary>
        private readonly SemaphoreSlim _screenOpLock = new(1, 1);
        private bool _userPaused;

        /// <summary>每屏复用的静态壁纸 WebView2 层（窗口+Controller 仅创建一次，切换静态图仅重新导航，
        /// 消除每次 dynamic→static 都重建 WebView2 的 ~500ms 延迟）。非静态屏时隐藏保活，下次即时复用。</summary>
        private readonly Dictionary<int, StaticFadeProvider> _reusableStatic = new();
        private readonly object _staticLock = new();

        /// <summary>壁纸切换叠化过渡时长。300ms：既有叠化观感又跟手（600ms 会让用户觉得切换拖沓）。</summary>
        private static readonly TimeSpan FadeDuration = TimeSpan.FromMilliseconds(300);
        private const int FadeStepMs = 30;

        /// <summary>判断路径是否为 http/https 远程 URL。</summary>
        private static bool IsRemoteUrl(string path) =>
            Uri.TryCreate(path, UriKind.Absolute, out var u) &&
            (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);

        /// <summary>程序启动前系统原本的静态壁纸路径，解除桌面时恢复。</summary>
        private string _originalWallpaper = "";

        public WallpaperManager(Config config)
        {
            _config = config;
            BuildScreens();
            ApplyPerformanceMode();
            _fs = new FullscreenMonitor();
            _fs.FullscreenChanged += _ => ApplyPlayState();
            _power = new PowerManager();
            _power.BatteryChanged += _ => ApplyPlayState();
            _watchdog.Elapsed += WatchdogTick;
        }

        public bool IsPaused => _userPaused;
        public int ScreenCount => _states.Count;
        public bool WebAvailable => WebProviderLoader.Available;

        /// <summary>任意屏幕壁纸设置/清除/停止完成后触发，供 UI 同步按钮状态。</summary>
        public event Action? StateChanged;

        public IReadOnlyList<int> ActiveScreenIndices =>
            _states.Values.Where(s => s.Provider != null || s.IsStaticImage).Select(s => s.Index).ToList();

        public string? GetActivePath(int index) =>
            _states.TryGetValue(index, out var s) && (s.Provider != null || s.IsStaticImage) ? s.LastPath : null;

        private void RaiseStateChanged() => StateChanged?.Invoke();

        private void BuildScreens()
        {
            _states.Clear();
            foreach (var sc in ScreenManager.GetScreens())
                _states[sc.Index] = new ScreenState { Index = sc.Index, Bounds = sc.Bounds };
        }

        public void Start()
        {
            // 在应用任何动态壁纸之前，先记录系统当前的静态壁纸，用于后续解除恢复
            ReadOriginalWallpaper();
            // 如果注册表当前为空，但配置里保存过上次记录的原壁纸，则使用配置里的备用值
            if (string.IsNullOrEmpty(_originalWallpaper) && !string.IsNullOrEmpty(_config.OriginalWallpaper))
            {
                _originalWallpaper = _config.OriginalWallpaper;
                Logger.Log($"[WallpaperManager] 使用配置备用原壁纸: {_originalWallpaper}");
            }

            _fs.Start();
            _watchdog.Start();

            // 预热 WebView2 Environment，加快首次设置视频壁纸的响应
            VideoProvider.Prewarm();

            // 恢复已保存的每屏分配
            if (_config.Assignments != null)
            {
                foreach (var a in _config.Assignments)
                {
                    // 远程 URL（http/https）没有本地文件，File.Exists 会误判不存在而跳过，
                    // 因此加 IsRemoteUrl 判定，保证在线壁纸也能在启动时自动恢复。
                    if (_states.ContainsKey(a.Index) && (IsRemoteUrl(a.Path) || File.Exists(a.Path)))
                        _ = SetWallpaperAsync(a.Path, a.Type, a.Index, save: false);
                }
                _config.Save();
            }
        }

        private void ReadOriginalWallpaper()
        {
            try
            {
                _originalWallpaper = Registry.GetValue(@"HKEY_CURRENT_USER\Control Panel\Desktop", "Wallpaper", "") as string ?? "";
                if (!string.IsNullOrEmpty(_originalWallpaper))
                {
                    _config.OriginalWallpaper = _originalWallpaper;
                    _config.Save();
                }
                Logger.Log($"[WallpaperManager] 记录原始系统壁纸: {_originalWallpaper}");
            }
            catch (Exception ex)
            {
                Logger.Log($"[WallpaperManager] 读取原系统壁纸失败: {ex.Message}");
            }
        }

        /// <summary>把配置中的壁纸适应方式同步到各 Provider 静态属性，并立即刷新当前已激活的壁纸。
        /// fill=铺满裁剪 / fit=完整显示 / center=原始居中。</summary>
        public void SyncFitMode()
        {
            var fit = string.IsNullOrWhiteSpace(_config.WallpaperFit) ? "fill" : _config.WallpaperFit.Trim().ToLowerInvariant();
            if (fit is not ("fill" or "fit" or "center")) fit = "fill";
            Providers.ImageProvider.FitMode = fit;
            Providers.GifProvider.FitMode = fit;
            Providers.VideoProvider.FitMode = fit;
            Providers.StaticFadeProvider.FitMode = fit;

            // 立即刷新当前正在渲染的壁纸（WPF Image 属性需在 UI 线程更新）
            foreach (var st in _states.Values)
            {
                if (st.Provider == null) continue;
                var provider = st.Provider;
                try
                {
                    if (System.Windows.Application.Current?.Dispatcher != null)
                        System.Windows.Application.Current.Dispatcher.InvokeAsync(() => ApplyFitModeTo(provider));
                    else
                        ApplyFitModeTo(provider);
                }
                catch (Exception ex)
                {
                    Logger.Log($"[WallpaperManager] 即时应用适应方式失败: {ex.Message}");
                }
            }
        }

        private static void ApplyFitModeTo(IWallpaperProvider provider)
        {
            switch (provider)
            {
                case Providers.ImageProvider img: img.ApplyFitMode(); break;
                case Providers.GifProvider gif: gif.ApplyFitMode(); break;
                case Providers.VideoProvider vid: vid.ApplyFitMode(); break;
                case Providers.StaticFadeProvider fade: fade.ApplyFitMode(); break;
            }
        }

        /// <summary>将指定内容设为某屏壁纸。screenIndex 默认 0（主屏）。
        /// 整个流程在 _screenOpLock 串行锁内执行，保证与自动恢复/清除/停止互斥，
        /// 同一时刻同一屏幕只有一个设置任务在创建/销毁 Provider 与渲染窗口。
        /// status 为可选切换状态回调（正在切换/已应用/失败原因），供 UI 状态栏反馈。</summary>
        public async Task SetWallpaperAsync(string path, WallpaperType type, int screenIndex = 0, bool save = true, Action<string>? status = null)
        {
            await _screenOpLock.WaitAsync();
            try
            {
                if (!_states.TryGetValue(screenIndex, out var st)) return;

                status?.Invoke("正在切换：" + Path.GetFileName(path));

                // 同步壁纸适应方式到各 Provider（静态属性，Provider 创建时读取）
                SyncFitMode();

                // 静态图片：直接用系统 API（IDesktopWallpaper）设置桌面壁纸。
                // 若当前屏幕正在播放动态壁纸 A，切换顺序必须"先设系统壁纸 S、后销毁 A"：
                // 旧流程先销毁 A 会露出系统上一张静态壁纸（残留 C），再设 S，形成 A→C→S 的
                // 中间停留；新流程 S 先落到底层（被 A 盖住、用户无感知），A 不做渐出、
                // 通过 WebView2 过渡窗口把 S 渐入覆盖 A，最后统一清理，形成 A→S 无缝直切。
                if (type == WallpaperType.Image)
                {
                    bool isRemoteUrl = IsRemoteUrl(path);

                    // 本地图片必须存在；远程 URL（http/https）直接走 WebView2 渲染，不检查文件存在性。
                    if (!isRemoteUrl && !File.Exists(path))
                        throw new InvalidOperationException("壁纸文件不存在：" + path);

                    var prevProvider = st.Provider;
                    var prevIsStaticImage = st.IsStaticImage;

                    // WebView2 不可用时（缺 WebView2Loader.dll / VC++ 运行库 / WebView2 Runtime）：
                    // 静态壁纸降级为系统 API 直设（IDesktopWallpaper/SPI），Win10 传统桌面完全可靠，
                    // 不依赖 WebView2 窗口层——保证动态壁纸失效的环境下静态壁纸仍可设置。
                    // 注意：系统 API 不支持远程 URL，因此在线壁纸必须依赖 WebView2。
                    if (!await VideoProvider.IsWebView2AvailableAsync())
                    {
                        if (isRemoteUrl)
                            throw new InvalidOperationException("在线壁纸需要 WebView2 运行库，请确认已安装 Microsoft Edge WebView2 Runtime。");

                        // 当前屏若有窗口层壁纸在运行，先销毁（系统壁纸层直接承载静态图，无需叠化）
                        if (prevProvider != null)
                        {
                            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                            {
                                try { prevProvider.Dispose(); }
                                catch (Exception ex) { Logger.Log($"[WallpaperManager] 旧壁纸 Dispose 异常: {ex.Message}"); }
                            });
                        }
                        SetSystemWallpaper(path);
                        st.Provider = null;
                        st.IsStaticImage = true;
                        st.LastPath = path;
                        st.LastType = type;
                        if (save) PersistAssignments();
                        status?.Invoke("已应用：" + Path.GetFileName(path));
                        return;
                    }

                    // 系统壁纸层不参与静态切换（恒为程序启动前的原壁纸）：静态壁纸完全由
                    // WebView2 窗口层承载。不再调用 SetSystemWallpaper——它把系统层设为静态图后，
                    // 切动态/解除时系统层会透出"上次设置的静态壁纸"（异步生效秒级延迟），
                    // 正是"切换跳静态图、解除先出旧静态图再恢复原壁纸"的根因。

                    // 快速路径：仅限"静态→静态"原地换图（当前屏已是图片模式 Provider）。
                    // 复用同一 Controller 改现有 img 的 src，新图加载完成前旧图保持显示，无缝替换，
                    // 该路径多轮实测稳定。
                    // 动态→静态【不再走原地切图】：在 WebView2 视频页内动态注入 img 覆盖不可靠
                    // （图片加载无法就绪、切不过去），已回退为下方冷启动方案（新建静态图片层）——
                    // 108 轮验证过可用，代价是冷启动 ~1s 延迟，但保证能切过去。
                    if (prevProvider is VideoProvider vpImage && vpImage.IsImageMode)
                    {
                        bool navOk = await vpImage.NavigateImageAsync(path);
                        bool ready = navOk && await vpImage.WaitVideoReadyAsync(TimeSpan.FromSeconds(6));
                        if (!ready)
                        {
                            Logger.Log("[WallpaperManager] 静态图切换未就绪，保持原壁纸状态");
                            status?.Invoke("切换失败：静态图加载未就绪（已保持原壁纸）");
                            return;
                        }
                        st.LastPath = path;
                        st.LastType = type;
                        st.IsStaticImage = true;
                        if (save) PersistAssignments();
                        status?.Invoke("已应用：" + Path.GetFileName(path));
                        return;
                    }

                    // 静态壁纸即时显示：由 WebView2 静态层（VideoProvider 图片模式，虚拟主机映射
                    // 本地图片，绕开 data URI 2MB 上限）承载。历史 WPF/GDI 窗口层在 Win11 raised
                    // desktop 下不被 DWM 合成（Attach 成功但完全不显示）；WebView2 走
                    // DirectComposition 独立通道（视频已验证可被 DWM 真实合成到桌面），图片同通道，
                    // 图片加载完成即显示，无系统 IDesktopWallpaper 异步生效的秒级延迟。
                    IntPtr staticChildHwnd = await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        var p = new VideoProvider();
                        p.Show(path, st.Bounds); // 图片模式：无声音概念，无需 SetMuted
                        st.Provider = p;
                        return p.Handle;
                    });

                    if (staticChildHwnd == IntPtr.Zero)
                    {
                        // 新 Provider 创建失败，还原旧状态
                        st.Provider = prevProvider;
                        st.IsStaticImage = prevIsStaticImage;
                        return;
                    }

                    // 后台线程获取 WorkerW（SendMessageTimeout 可能阻塞，不能放 UI 线程）
                    IntPtr staticWorkerW = await Task.Run(() => WorkerWInjector.AcquireWorkerW(st.Bounds));
                    if (staticWorkerW == IntPtr.Zero)
                    {
                        // 拿不到 WorkerW 时销毁刚创建的新窗口，还原旧状态，避免残留
                        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                        {
                            try { st.Provider?.Dispose(); } catch { }
                        });
                        st.Provider = prevProvider;
                        st.IsStaticImage = prevIsStaticImage;
                        throw new InvalidOperationException("无法获取桌面 WorkerW 层，请尝试重启资源管理器或系统。");
                    }

                    // UI 线程挂接并显示：img 未加载完成时窗口透明（露出旧动态层），
                    // 加载完成后即显示静态图，再销毁旧动态层 → A→S 无缝直切、无残留、无秒级等待。
                    await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        st.WorkerW = staticWorkerW;
                        st.Provider.AttachTo(st.WorkerW, st.Bounds);
                    });

                    // 等图片真正加载完成（窗口透明露出旧动态层）。
                    // 10s 含 Controller 创建重试（0x8007139F 资源竞争）+ 导航 + 图片加载全过程。
                    if (st.Provider is VideoProvider vpStatic)
                    {
                        bool imgReady = await vpStatic.WaitVideoReadyAsync(TimeSpan.FromSeconds(10));
                        if (!imgReady)
                        {
                            // 图片未就绪：销毁新静态层，恢复旧壁纸（旧视频窗口仍挂在承载层上），
                            // 避免"销毁旧层后新层空白"的黑屏/无壁纸状态。
                            Logger.Log("[WallpaperManager] 静态层图片未就绪，回退恢复原壁纸");
                            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                            {
                                try { st.Provider?.Dispose(); } catch { }
                            });
                            st.Provider = prevProvider;
                            st.IsStaticImage = prevIsStaticImage;
                            status?.Invoke("切换失败：静态图加载未就绪（已保持原壁纸）");
                            return;
                        }
                    }
                    if (st.Provider == null) return;

                    // 销毁旧壁纸（动态 A 或旧静态层），静态 WebView2 层已就绪，无残留
                    if (prevProvider != null)
                    {
                        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                        {
                            try { prevProvider.Dispose(); }
                            catch (Exception ex) { Logger.Log($"[WallpaperManager] 旧壁纸 Dispose 异常: {ex.Message}"); }
                        });
                    }

                    // 清掉旧的 WPF 静态复用层（新方案不再创建，仅清理历史遗留）
                    lock (_staticLock)
                    {
                        if (_reusableStatic.TryGetValue(screenIndex, out var sp))
                        {
                            _reusableStatic.Remove(screenIndex);
                            var toDispose = sp;
                            System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                            {
                                try { toDispose.Dispose(); } catch { }
                            });
                        }
                    }

                    st.IsStaticImage = true;
                    st.LastPath = path;
                    st.LastType = type;
                    if (save) PersistAssignments();
                    status?.Invoke("已应用：" + Path.GetFileName(path));
                    return;
                }

                // 动态壁纸：记录旧状态，不立即销毁旧壁纸（叠化过渡期间新旧并存）
                var oldProvider = st.Provider;
                var oldIsStaticImage = st.IsStaticImage;
                var oldWorkerW = st.WorkerW;
                var oldPath = st.LastPath;
                var oldType = st.LastType;
                st.IsStaticImage = false;

                // 2. 在 UI 线程创建 WPF 渲染窗口、加载内容并拿到窗口句柄。
                //    WPF 窗口/控件/MediaElement 都必须在创建它们的 UI 线程上访问。
                IntPtr childHwnd = await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    st.LastPath = path;
                    st.LastType = type;

                    IWallpaperProvider? provider = type == WallpaperType.Web
                        ? WebProviderLoader.Create()
                        : ProviderFactory.Create(type);

                    if (provider == null)
                    {
                        if (type == WallpaperType.Web)
                            throw new InvalidOperationException("网页壁纸模块不可用：请将 DynamicWallpaper.Web.dll 放到主程序目录下。");
                        return IntPtr.Zero;
                    }

                    st.Provider = provider;
                    try
                    {
                        // 先缓存静音状态（VideoProvider 构造 HTML 时按此写 video 的 muted 属性，
                        // 非静音时页面加载即出声），再创建渲染窗口；此刻 Controller 尚未创建，
                        // SetMuted 里的 JS 调用会自动跳过，仅把状态存入 provider。
                        provider.SetMuted(_config.Mute);
                        provider.Show(path, st.Bounds);
                    }
                    catch (Exception ex)
                    {
                        // 壁纸恢复路径上的 UI 线程异常（如文件缺失/损坏）就地吞掉并记日志，
                        // 避免抛到 DispatcherUnhandledException 干扰主窗口正常初始化/显示。
                        Logger.Log($"[WallpaperManager] 壁纸渲染窗口创建失败: {ex.Message}");
                        st.Provider = null;
                        try { provider.Dispose(); } catch { }
                        return IntPtr.Zero;
                    }
                    return provider.Handle; // EnsureHandle 必须在 UI 线程执行
                });

                if (childHwnd == IntPtr.Zero)
                {
                    // 新 Provider 创建失败，还原旧状态
                    st.Provider = oldProvider;
                    st.IsStaticImage = oldIsStaticImage;
                    st.LastPath = oldPath;
                    st.LastType = oldType;
                    st.WorkerW = oldWorkerW;
                    return;
                }

                // 3. 在后台线程获取 WorkerW（SendMessageTimeout 可能阻塞，不能放在 UI 线程）
                IntPtr workerW = await Task.Run(() => WorkerWInjector.AcquireWorkerW(st.Bounds));
                if (workerW == IntPtr.Zero)
                {
                    // 拿不到 WorkerW 时销毁刚创建的新窗口，还原旧状态，避免残留
                    await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        try { st.Provider?.Dispose(); } catch { }
                    });
                    st.Provider = oldProvider;
                    st.IsStaticImage = oldIsStaticImage;
                    st.LastPath = oldPath;
                    st.LastType = oldType;
                    st.WorkerW = oldWorkerW;
                    throw new InvalidOperationException("无法获取桌面 WorkerW 层，请尝试重启资源管理器或系统。");
                }

                // 4. 在 UI 线程完成挂接、静音、播放（这些操作会访问 WPF 窗口/控件）
                await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    if (st.Provider == null) return;
                    st.WorkerW = workerW;
                    st.Provider.AttachTo(st.WorkerW, st.Bounds);
                    st.Provider.SetMuted(_config.Mute);
                    st.Provider.Play();
                    _userPaused = false;
                });

                // 5. 等待 MediaElement 真正渲染出视频后，强制 DWM 合成一次。
                //    修复 Win11 24H2/25H2：Attach 完成时视频尚未开始渲染，DWM 不合成空窗口，
                //    桌面保持静态壁纸；延迟后执行"摘出→置顶→归位"触发合成，归位后状态保持。
                //    触发时机改为轮询等待视频内容真正加载完成（NaturalVideoWidth>0），
                //    比固定延迟更可靠：大文件解码慢时不会错过触发时机。
                _ = Task.Run(async () =>
                {
                    var videoProvider = st.Provider as VideoProvider;
                    // WebView2 视频链路不需要「摘出→置顶→归位」强制合成（Lively 同款结构不做此操作）；
                    // 只有旧 WPF MediaElement 视频（NeedsForcedComposition=true）才需要后续轮询 + 强制合成。
                    if (videoProvider != null && !videoProvider.NeedsForcedComposition) return;
                    var deadline = DateTime.UtcNow.AddSeconds(12);
                    while (DateTime.UtcNow < deadline)
                    {
                        bool ready = false;
                        if (st.Provider == videoProvider && videoProvider != null)
                            ready = await System.Windows.Application.Current.Dispatcher.InvokeAsync(() => videoProvider.HasVideoContent());
                        if (ready) break;
                        await Task.Delay(500);
                    }
                    await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        // 期间若已切换/清理壁纸（Provider 不再是同一个），跳过合成避免误操作
                        if (st.Provider != videoProvider) return;
                        var hwnd = st.Provider.Handle;
                        if (hwnd != IntPtr.Zero && st.WorkerW != IntPtr.Zero)
                        {
                            WorkerWInjector.ForceDwmComposition(hwnd, st.WorkerW, st.Bounds);
                        }
                    });
                });

                // 叠化过渡：统一走 CrossfadeAsync（动态→动态：新旧窗口 alpha 叠化；
                // 静态→动态：旧状态是 WebView2 静态层，同样支持窗口级 alpha 叠化，
                // 静态层淡出 + 视频层淡入，无系统壁纸层残留问题——静态图已由窗口层承载，
                // 系统壁纸层保持原壁纸，不再参与切换）。
                await CrossfadeAsync(st, oldProvider, childHwnd);

                if (save) PersistAssignments();
                status?.Invoke("已应用：" + Path.GetFileName(path));
            }
            finally
            {
                _screenOpLock.Release();
                RaiseStateChanged();
            }
        }

        /// <summary>新壁纸淡入 + 旧壁纸淡出（300ms 窗口级 alpha 叠化）。叠化结束后销毁旧 Provider。</summary>
        private async Task CrossfadeAsync(ScreenState st, IWallpaperProvider? oldProvider, IntPtr newHwnd)
        {
            // 新窗口先置透明，避免挂载瞬间闪出
            try { Win32.SetLayeredWindowAttributes(newHwnd, 0, 0, Win32.LWA_ALPHA); } catch { }

            // 等新视频真正可播（透明背景，未就绪时露出旧壁纸、视觉无变化）；
            // 就绪后再叠化，避免"叠化期间新窗口空白、视频加载好后突然跳入"的闪动。
            if (st.Provider is VideoProvider newVp)
                await newVp.WaitVideoReadyAsync(TimeSpan.FromSeconds(8));
            if (st.Provider == null) return; // 等待期间壁纸被清除/切换，中止过渡

            await Task.Run(async () =>
            {
                try
                {
                    int steps = (int)(FadeDuration.TotalMilliseconds / FadeStepMs);
                    for (int i = 1; i <= steps; i++)
                    {
                        if (st.Provider == null) break; // 壁纸已被清除/切换，中止渐变（旧 Provider 仍在 finally 中清理）
                        int newAlpha = 255 * i / steps;
                        int oldAlpha = 255 - newAlpha;
                        if (newHwnd != IntPtr.Zero)
                            Win32.SetLayeredWindowAttributes(newHwnd, 0, (byte)newAlpha, Win32.LWA_ALPHA);
                        if (oldProvider != null)
                        {
                            IntPtr oh = oldProvider.Handle;
                            if (oh != IntPtr.Zero)
                                Win32.SetLayeredWindowAttributes(oh, 0, (byte)oldAlpha, Win32.LWA_ALPHA);
                        }
                        await Task.Delay(FadeStepMs);
                    }
                }
                finally
                {
                    // 叠化结束（正常完成或中途被中断），销毁旧 Provider，避免窗口泄漏。
                    // 复核兜底：旧 Provider 销毁必须在 UI 线程（WPF RenderWindow / WebView2 Controller
                    // 归属创建线程），但必须用 Dispatcher.InvokeAsync 异步调度——不阻塞后台渐变线程；
                    // Background 优先级避免销毁动作抢占 UI 输入响应；try/catch 兜底，
                    // 防止 CoreWebView2Controller.Close()/DestroyWindow 长时间阻塞或异常中断切换流程。
                    if (oldProvider != null)
                    {
                        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                        {
                            try { oldProvider.Dispose(); }
                            catch (Exception ex) { Logger.Log($"[WallpaperManager] 旧壁纸 Dispose 异常: {ex.Message}"); }
                        }, System.Windows.Threading.DispatcherPriority.Background);
                    }
                }
            });
        }

        /// <summary>清空某一屏的壁纸（恢复为系统静态壁纸）。</summary>
        public async Task ClearScreenAsync(int screenIndex)
        {
            await _screenOpLock.WaitAsync();
            try
            {
                if (_states.TryGetValue(screenIndex, out var st))
                    await CleanupScreenAsync(st, restoreWallpaper: true);
                PersistAssignments();
            }
            finally
            {
                _screenOpLock.Release();
                RaiseStateChanged();
            }
        }

        /// <summary>
        /// 停止所有屏壁纸。restoreWallpaper=true 时把系统壁纸刷回原静态图（退出/解除时桌面不会黑屏）。
        /// persistState=true（默认）时把“当前运行态”回写到配置；退出程序时应传 false：
        /// 此时 CleanupScreenAsync 已把各屏 LastPath 清空，若再 Persist 会把用户已设置的壁纸分配覆盖成空，
        /// 导致下次启动无法自动恢复。用户主动“解除壁纸”走 ClearScreenAsync，自会更新配置。
        /// </summary>
        public async Task StopAsync(bool restoreWallpaper = true, bool persistState = true)
        {
            await _screenOpLock.WaitAsync();
            try
            {
                var tasks = _states.Values.Select(st => CleanupScreenAsync(st, restoreWallpaper)).ToArray();
                await Task.WhenAll(tasks);
                if (persistState) PersistAssignments();
            }
            finally
            {
                _screenOpLock.Release();
                RaiseStateChanged();
            }
        }

        public void TogglePause()
        {
            _userPaused = !_userPaused;
            ApplyPlayState();
        }

        public void SetMute(bool mute)
        {
            _config.Mute = mute;
            _config.Save();
            foreach (var st in _states.Values) st.Provider?.SetMuted(mute);
        }

        public void SetPauseOnFullscreen(bool value) { _config.PauseOnFullscreen = value; _config.Save(); ApplyPlayState(); }
        public void SetPauseOnBattery(bool value) { _config.PauseOnBattery = value; _config.Save(); ApplyPlayState(); }
        public void SetPerformanceMode(bool value)
        {
            _config.PerformanceMode = value;
            _config.Save();
            ApplyPerformanceMode();
        }

        private void ApplyPerformanceMode()
        {
            try
            {
                var self = Process.GetCurrentProcess();
                self.PriorityClass = _config.PerformanceMode ? ProcessPriorityClass.BelowNormal : ProcessPriorityClass.Normal;
                VideoProvider.LowQualityScaling = _config.PerformanceMode;
            }
            catch { /* 权限不足时忽略 */ }
        }

        private void ApplyPlayState()
        {
            bool shouldPause = _userPaused
                || (_config.PauseOnFullscreen && _fs.Peek())
                || (_config.PauseOnBattery && _power.IsOnBattery);
            foreach (var st in _states.Values)
            {
                if (st.Provider == null) continue;
                if (shouldPause) st.Provider.Pause();
                else st.Provider.Play();
            }
        }

        private async Task CleanupScreenAsync(ScreenState st, bool restoreWallpaper)
        {
            // 记录清理前是否为静态图片模式（静态图片由窗口层承载，解除时同样撤走窗口即可）
            bool wasStaticImage = st.IsStaticImage;

            // 取出本屏复用静态层引用（动态屏时它隐藏保活，需单独销毁）
            StaticFadeProvider? reusableStatic = null;
            bool layerIsActiveProvider = false;
            lock (_staticLock)
            {
                if (_reusableStatic.TryGetValue(st.Index, out var sp))
                {
                    reusableStatic = sp;
                    _reusableStatic.Remove(st.Index);
                    layerIsActiveProvider = ReferenceEquals(sp, st.Provider);
                }
            }

            // 1. 在 UI 线程释放 WPF Provider（关闭渲染窗口）
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (st.Provider != null)
                {
                    st.Provider.Dispose();
                    st.Provider = null;
                }
                st.LastPath = "";
                st.IsStaticImage = false;
            });

            // 复用静态层可能并非当前 st.Provider（动态屏时隐藏保活），上面未销毁则在此销毁
            if (reusableStatic != null && !layerIsActiveProvider)
            {
                await System.Windows.Application.Current.Dispatcher.InvokeAsync(() => reusableStatic.Dispose());
            }

            // 2. 在后台线程执行 Win32 清理与系统壁纸恢复（避免 SendMessageTimeout / SPI 阻塞 UI）
            await Task.Run(() =>
            {
                // 仅把本程序注入的子窗口从 WorkerW 上摘离/销毁。
                // 注意：【不要销毁系统 WorkerW 本身】——那样会迫使 Windows 重建桌面，
                // 出现“黑屏闪一下再恢复”的现象。我们只是把自己的渲染窗口撤走，
                // 原本压在注入层之下的系统静态壁纸会自然透出来，无需任何刷新。
                if (st.WorkerW != IntPtr.Zero)
                {
                    WorkerWInjector.DetachChildren(st.WorkerW);
                    st.WorkerW = IntPtr.Zero;
                }

                // 解除静态图片壁纸（用户点击"解除壁纸"）时必须恢复系统原壁纸；
                // 其它情况仅当所有屏都已清空时才恢复，避免切换同屏壁纸时黑屏闪烁。
                // 注：静态壁纸由窗口层承载后，系统壁纸层始终为程序启动前的原壁纸，
                // 此处 RestoreSystemWallpaper 仅把原壁纸再设一遍（无害，保证退出/解除后桌面正确）。
                if (restoreWallpaper && (wasStaticImage || _states.Values.All(s => s.Provider == null && !s.IsStaticImage)))
                    RestoreSystemWallpaper();
            });
        }

        /// <summary>通过官方桌面壁纸 API（IDesktopWallpaper）将指定图片设为系统桌面壁纸。
        /// 该 API 异步生效、调用立即返回；SPI_SETDESKWALLPAPER 会同步等待系统重绘（实测阻塞 2~3 秒），
        /// 在"解除壁纸"时会让按钮/壁纸迟迟无响应，因此这里不再使用 SPI。</summary>
        private void SetSystemWallpaper(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    Logger.Log($"[WallpaperManager] 静态壁纸文件不存在: {path}");
                    return;
                }
                Win32.SetDesktopWallpaper(path);
                Logger.Log($"[WallpaperManager] 已设置静态壁纸: {path}");
            }
            catch (Exception ex)
            {
                Logger.Log($"[WallpaperManager] 设置静态壁纸失败: {ex.Message}");
                // 新 API 异常时回退到 SPI，保证功能可用
                try
                {
                    Win32.SystemParametersInfo(Win32.SPI_SETDESKWALLPAPER, 0, path, Win32.SPIF_UPDATEINIFILE | Win32.SPIF_SENDCHANGE);
                    Logger.Log($"[WallpaperManager] 已通过 SPI 回退设置静态壁纸: {path}");
                }
                catch (Exception ex2)
                {
                    Logger.Log($"[WallpaperManager] SPI 回退设置静态壁纸也失败: {ex2.Message}");
                }
            }
        }

        /// <summary>把系统桌面恢复为程序启动前的静态壁纸。</summary>
        private void RestoreSystemWallpaper()
        {
            try
            {
                // 如果记录丢失或文件已不存在，再次从注册表读取当前静态壁纸
                if (string.IsNullOrEmpty(_originalWallpaper) || !File.Exists(_originalWallpaper))
                    ReadOriginalWallpaper();

                if (!string.IsNullOrEmpty(_originalWallpaper) && File.Exists(_originalWallpaper))
                {
                    // 系统壁纸层恒为启动前原壁纸（静态壁纸由窗口层承载），若当前已是原壁纸则
                    // 无需重设，避免重复设置触发系统异步重绘/残留闪烁（解除时的"先旧图后原壁纸"）。
                    try
                    {
                        string? current = Registry.GetValue(@"HKEY_CURRENT_USER\Control Panel\Desktop", "Wallpaper", "") as string;
                        if (string.Equals(current, _originalWallpaper, StringComparison.OrdinalIgnoreCase))
                        {
                            Logger.Log("[WallpaperManager] 系统壁纸已是原壁纸，跳过恢复");
                            return;
                        }
                    }
                    catch { /* 注册表读取失败时继续走设置流程 */ }

                    Win32.SetDesktopWallpaper(_originalWallpaper);
                    Logger.Log($"[WallpaperManager] 已恢复系统壁纸: {_originalWallpaper}");
                }
                else
                {
                    // 没有可恢复的图片壁纸时，设为空（系统默认纯色/背景色），至少比黑屏自然
                    Win32.SystemParametersInfo(Win32.SPI_SETDESKWALLPAPER, 0, "", Win32.SPIF_UPDATEINIFILE | Win32.SPIF_SENDCHANGE);
                    Logger.Log("[WallpaperManager] 无可恢复图片壁纸，已恢复为系统默认桌面");
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[WallpaperManager] 恢复系统壁纸失败: {ex.Message}");
            }
        }

        private void PersistAssignments()
        {
            _config.Assignments = _states.Values
                .Where(s => (s.Provider != null || s.IsStaticImage) && !string.IsNullOrEmpty(s.LastPath))
                .Select(s => new ScreenAssignment { Index = s.Index, Path = s.LastPath, Type = s.LastType })
                .ToList();
            _config.Save();
        }

        private void WatchdogTick(object? sender, EventArgs e)
        {
            // 显示器热插拔：屏幕数量变化时重建并按原分配恢复
            if (ScreenManager.Count != _states.Count)
            {
                var saved = _states.Values
                    .Where(s => s.Provider != null || s.IsStaticImage)
                    .ToDictionary(s => s.Index, s => (s.LastPath, s.LastType));
                BuildScreens();
                foreach (var kv in saved)
                {
                    if (_states.ContainsKey(kv.Key) && File.Exists(kv.Value.LastPath))
                        _ = SetWallpaperAsync(kv.Value.LastPath, kv.Value.LastType, kv.Key, save: false);
                }
                _config.Save();
                return;
            }

            // 资源管理器重启导致 WorkerW 销毁时，按屏重建。
            // 注意：SetWallpaperAsync 执行期间（AcquireWorkerW 轮询中）st.WorkerW 尚未赋值，
            // 若仅判断 IsValid(IntPtr.Zero) 会把"正在设置中"误判为失效，导致看门狗无限排队重建。
            // 因此仅当 WorkerW 曾经被设置过（非 IntPtr.Zero）且当前已失效时才触发重建。
            foreach (var st in _states.Values)
            {
                if (st.Provider == null || string.IsNullOrEmpty(st.LastPath)) continue;
                if (st.WorkerW != IntPtr.Zero && !WorkerWInjector.IsValid(st.WorkerW))
                {
                    // explorer 重启导致承载层失效：先让 WorkerWInjector 丢弃缓存，
                    // 下次 AcquireWorkerW 才会重新探测，而非复用已死的句柄。
                    WorkerWInjector.InvalidateCache();
                    _ = SetWallpaperAsync(st.LastPath, st.LastType, st.Index, save: false);
                }
            }
        }
    }
}
