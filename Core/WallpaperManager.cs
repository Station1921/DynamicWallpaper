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

        /// <summary>每屏复用的静态壁纸 WebView2 层已废弃：静态图片改走系统壁纸 API（IDesktopWallpaper），
        /// 由 Windows 自己渲染，Win10 / Win11（含 raised desktop）全兼容，不存在自绘窗口的黑屏/旧图问题。</summary>

        /// <summary>程序启动前系统原本的静态壁纸路径，解除桌面时恢复。</summary>
        private string _originalWallpaper = "";

        /// <summary>上次系统壁纸设置时间：explorer 需在进程内同步转码壁纸图片（Win11 高分屏
        /// 一张 2K JPG 转码约 0.5~1s），密集连续设置会把 explorer 压到"未响应"。
        /// 两次设置之间强制最小间隔，给转码喘息空间。</summary>
        private DateTime _lastSystemWallpaperSetUtc = DateTime.MinValue;
        private static readonly TimeSpan MinSystemWallpaperSetInterval = TimeSpan.FromMilliseconds(800);

        /// <summary>Windows 换壁纸时系统层自带的 C→B 交叉叠化动画时长（约 0.5~1s）。
        /// GetDesktopWallpaper 路径确认 ≠ 视觉叠化完成——路径 30~200ms 即返回，此时叠化刚
        /// 开始，立刻销毁视频层会露出"旧图 C 渐变到新图 B"的全过程。动→静必须等叠化播完
        /// 再撤视频层，这是"闪旧图"从未根除的真正原因。</summary>
        private const int WallpaperFadeGraceMs = 800;

        /// <summary>上次完成任意壁纸设置（动态或静态）的时间。全屏暂停在此后的一段时间内
        /// 暂不生效，防止壁纸软件自身最大化/临时 Shell 窗口被误判为全屏应用，导致刚设完
        /// 动态壁纸就被暂停。</summary>
        private DateTime _lastWallpaperSetUtc = DateTime.MinValue;
        private static readonly TimeSpan FullscreenPauseCooldown = TimeSpan.FromSeconds(3);

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

        public IReadOnlyList<int> ActiveScreenIndices =>
            _states.Values.Where(s => s.Provider != null || s.IsStaticImage).Select(s => s.Index).ToList();

        public string? GetActivePath(int index) =>
            _states.TryGetValue(index, out var s) && (s.Provider != null || s.IsStaticImage) ? s.LastPath : null;

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
                    if (_states.ContainsKey(a.Index) && File.Exists(a.Path))
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
            SyncFitModeProperties();

            // 静态图片壁纸由系统渲染：适应方式变化时需要重设一次系统壁纸才能生效
            foreach (var st in _states.Values)
            {
                if (st.IsStaticImage && !string.IsNullOrEmpty(st.LastPath) && File.Exists(st.LastPath))
                {
                    var p = st.LastPath;
                    var b = st.Bounds;
                    _ = Task.Run(() => { ApplyWallpaperFitStyle(); SetSystemWallpaper(p, b, synchronous: false); });
                }
            }

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

        /// <summary>仅同步各 Provider 的 FitMode 静态属性，绝不触碰系统壁纸。
        /// SetWallpaperAsync 内部使用：切换壁纸时此处若重设系统壁纸（fire-and-forget），
        /// 会与随后的新壁纸设置竞争，把旧图 A 再刷一遍——形成 A→C→B 闪现，
        /// 且同一张图转码两次，明显拖慢切换（实测每次静态切换日志出现两条交错的
        /// "已设置静态壁纸"，Win10/Win11 均复现）。</summary>
        private void SyncFitModeProperties()
        {
            var fit = string.IsNullOrWhiteSpace(_config.WallpaperFit) ? "fill" : _config.WallpaperFit.Trim().ToLowerInvariant();
            if (fit is not ("fill" or "fit" or "center")) fit = "fill";
            Providers.ImageProvider.FitMode = fit;
            Providers.GifProvider.FitMode = fit;
            Providers.VideoProvider.FitMode = fit;
        }

        private static void ApplyFitModeTo(IWallpaperProvider provider)
        {
            switch (provider)
            {
                case Providers.ImageProvider img: img.ApplyFitMode(); break;
                case Providers.GifProvider gif: gif.ApplyFitMode(); break;
                case Providers.VideoProvider vid: vid.ApplyFitMode(); break;
            }
        }

        /// <summary>将指定内容设为某屏壁纸。screenIndex 默认 0（主屏）。
        /// 整个流程在 _screenOpLock 串行锁内执行，保证与自动恢复/清除/停止互斥，
        /// 同一时刻同一屏幕只有一个设置任务在创建/销毁 Provider 与渲染窗口。</summary>
        public async Task SetWallpaperAsync(string path, WallpaperType type, int screenIndex = 0, bool save = true)
        {
            await _screenOpLock.WaitAsync();
            try
            {
                if (!_states.TryGetValue(screenIndex, out var st)) return;

                // 同步壁纸适应方式到各 Provider 静态属性。
                // 注意：这里不能调用完整 SyncFitMode()——它会把旧静态图再设一遍系统壁纸
                // （fire-and-forget），与新壁纸设置竞争导致 A-C-B 闪现 + 双重转码卡顿。
                SyncFitModeProperties();

                // 静态图片：直接用系统壁纸 API（IDesktopWallpaper）设置桌面壁纸，
                // Windows 自己渲染壁纸层，Win10 / Win11（含 raised desktop）全兼容。
                // 若当前屏幕正在播放动态壁纸 A，切换顺序必须"先设系统壁纸 S、后销毁 A"：
                // S 先在 A 下方落位（被 A 盖住、用户无感知），A 销毁后 S 直接露出，
                // 形成 A→S 无缝直切，绝不闪现上一张静态图 C。
                if (type == WallpaperType.Image)
                {
                    if (!File.Exists(path))
                        throw new InvalidOperationException("壁纸文件不存在：" + path);

                    var prevProvider = st.Provider;

                    // 1. 后台线程：设适应方式（注册表 WallpaperStyle）→ 设系统壁纸。
                    //    仅"动→静"需要等转码完成（销毁视频层之前，确保下层系统壁纸已是新图，
                    //    否则视频移走瞬间会露出旧图）；"静→静"没有任何层遮挡，Windows 自带
                    //    过渡直接 A→B，无需等待——等待反而是 Win11 静态切换"略卡"的来源
                    //    （Win11 转码普遍比 Win10 慢，常逼近超时上限）。
                    await Task.Run(() =>
                    {
                        ApplyWallpaperFitStyle();
                        // 动→静：必须确保新系统壁纸完全落位后再销毁上层视频，否则视频移走瞬间会
                        // 露出旧图。IDesktopWallpaper 异步生效、落位时间不可控（Win10 快、Win11 慢），
                        // 靠轮询 TranscodedWallpaper 时间戳在 Win11 上经常超时（日志里等 3 秒），
                        // 在 Win10 上又提前返回导致叠化中段旧图闪现。改用同步 SPI：调用阻塞到
                        // explorer 真正处理完壁纸，返回即可立即销毁视频层，A→B 直切且不卡 UI
                        //（在后台线程执行）。静→静没有视频层遮挡，用轻快的 IDesktopWallpaper。
                        if (prevProvider != null)
                            SetSystemWallpaper(path, st.Bounds, synchronous: true);
                        else
                            SetSystemWallpaper(path, st.Bounds, synchronous: false);
                    });

                    // 2. 系统壁纸 S 已就位，销毁旧动态层 A——A 移走后 S 直接露出，无中间残留
                    if (prevProvider != null)
                    {
                        // 关键：销毁视频前先强制 WorkerW 同步重绘。叠化动画是 DWM 混合两张
                        // 纹理完成的，WorkerW 自身从叠化开始到结束一直没重绘——它最后一次画的
                        // 还是旧图 C。若直接 DestroyWindow，WorkerW 被触发重绘前的 1~2 帧，
                        // DWM 合成的就是 WorkerW 的旧内容 = 闪一帧 C。此处趁视频还盖着屏幕，
                        // 先让 explorer 把新图 B 画进桌面层（用户看不到重绘过程），再撤视频。
                        const uint RDW_INVALIDATE = 0x0001, RDW_ERASE = 0x0004,
                                   RDW_ALLCHILDREN = 0x0080, RDW_UPDATENOW = 0x0100;
                        var ww = st.WorkerW;
                        if (ww != IntPtr.Zero && Win32.IsWindow(ww))
                        {
                            bool ok = Win32.RedrawWindow(ww, IntPtr.Zero, IntPtr.Zero,
                                RDW_INVALIDATE | RDW_ERASE | RDW_ALLCHILDREN | RDW_UPDATENOW);
                            Logger.Log($"[WallpaperManager] 销毁视频前强制 WorkerW 重绘: {ok}");
                        }

                        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                        {
                            try { prevProvider.Dispose(); }
                            catch (Exception ex) { Logger.Log($"[WallpaperManager] 旧壁纸 Dispose 异常: {ex.Message}"); }
                        });
                    }

                    st.Provider = null;
                    st.IsStaticImage = true;
                    st.LastPath = path;
                    st.LastType = type;
                    st.WorkerW = IntPtr.Zero;
                    _lastWallpaperSetUtc = DateTime.UtcNow;
                    if (save) PersistAssignments();
                    Logger.Log($"[WallpaperManager] 静态壁纸已应用（系统壁纸层）: {path}");
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
                // 设置视频初始静音状态（HTML 层面），配合 --autoplay-policy 允许带声音自动播放
                VideoProvider.InitialMuted = _config.Mute;
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

                // 4. 【挂载前】等视频真正解码可播（就绪信号由页面 postMessage 发出）。
                //    窗口此刻仍隐藏：旧壁纸（动态 A 或系统静态层）保持原样、无任何中间状态。
                //    就绪后再挂载+销毁旧层，实现严格 A→B 直切——这是"闪现上次静态图 C"的根治点。
                if (st.Provider is VideoProvider newVp0)
                {
                    bool videoReady = await newVp0.WaitVideoReadyAsync(TimeSpan.FromSeconds(6));
                    Logger.Log($"[WallpaperManager] 视频就绪等待结束: ready={videoReady}, path={path}");
                }

                // 6. 【就绪后】UI 线程挂载新窗口 + 销毁旧动态层，随后开始播放。
                //    新窗口挂上瞬间已有解码画面（Win11 Layered 全不透明；Win10 非 Layered 正常合成），
                //    旧层紧随其后销毁，中间不露出任何底层内容。
                //    注：不再做任何 alpha 0/255 过渡——Win10 传统模式 DWM 不合成 WorkerW 下
                //    Layered 子窗口的 alpha 修改，alpha 动画既无效又是"不显示"的根因。
                await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    if (st.Provider == null) return;
                    st.WorkerW = workerW;
                    st.Provider.AttachTo(st.WorkerW, st.Bounds);
                    st.Provider.SetMuted(_config.Mute);
                    _userPaused = false;

                    // 挂载成功后销毁旧动态层（必须在 UI 线程：WPF/WebView2 窗口归属创建线程）
                    if (oldProvider != null)
                    {
                        try { oldProvider.Dispose(); }
                        catch (Exception ex) { Logger.Log($"[WallpaperManager] 旧壁纸 Dispose 异常: {ex.Message}"); }
                    }
                });

                st.Provider.Play();
                // 新壁纸启动后立即应用暂停状态：若系统已处于全屏/电池状态，定时器事件不会触发，
                // 需在此主动暂停新 Provider，避免视频在前台全屏/电池下继续播放。
                ApplyPlayState();

                _lastWallpaperSetUtc = DateTime.UtcNow;

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

                if (save) PersistAssignments();
            }
            finally
            {
                _screenOpLock.Release();
            }
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
            }
        }

        public async Task StopAsync(bool restoreWallpaper = true)
        {
            await _screenOpLock.WaitAsync();
            try
            {
                var tasks = _states.Values.Select(st => CleanupScreenAsync(st, restoreWallpaper)).ToArray();
                await Task.WhenAll(tasks);
                PersistAssignments();
            }
            finally
            {
                _screenOpLock.Release();
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
            bool fs = _config.PauseOnFullscreen && _fs.Peek();
            bool battery = _config.PauseOnBattery && _power.IsOnBattery;

            // 全屏暂停冷却：刚完成壁纸设置后的短时间内，不因"前台全屏"而暂停。
            // 壁纸软件自身最大化、或被设置壁纸动作临时推上前台的 Shell/DefView 等窗口，
            // 常被全屏检测误判为"全屏应用"，导致刚设完动态壁纸立即被暂停（表现"设置后不播放"）。
            if (fs)
            {
                var sinceSet = DateTime.UtcNow - _lastWallpaperSetUtc;
                if (sinceSet >= TimeSpan.Zero && sinceSet < FullscreenPauseCooldown)
                {
                    Logger.Log($"[WallpaperManager] 前台全屏但距离壁纸设置仅 {sinceSet.TotalMilliseconds:F0}ms，暂不暂停（冷却期内）");
                    fs = false;
                }
            }

            bool shouldPause = _userPaused || fs || battery;
            if (shouldPause)
            {
                // 记录暂停原因，便于排查"视频不播放"类问题（全屏误判/电池误判一眼可辨）
                string reason = _userPaused ? "手动暂停"
                    : fs ? "前台全屏"
                    : "电池供电";
                Logger.Log($"[WallpaperManager] 自动暂停壁纸: 原因={reason}");
            }
            foreach (var st in _states.Values)
            {
                if (st.Provider == null) continue;
                if (shouldPause) st.Provider.Pause();
                else st.Provider.Play();
            }
        }

        private async Task CleanupScreenAsync(ScreenState st, bool restoreWallpaper)
        {
            // 记录清理前是否为静态图片模式（静态图片由系统壁纸层承载，解除时恢复系统原壁纸）
            bool wasStaticImage = st.IsStaticImage;

            // 1. 在 UI 线程释放 WPF Provider（关闭渲染窗口）；静态图片屏 Provider 为空，跳过
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

            // 复用静态层机制已废弃（静态图走系统壁纸 API），无需额外清理

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

        /// <summary>设置系统桌面壁纸。
        /// synchronous=false（静→静）：使用 IDesktopWallpaper，调用立即返回，切换轻快。
        /// synchronous=true（动→静）：先使用 IDesktopWallpaper 发起设置，然后轮询
        /// GetDesktopWallpaper() 返回路径是否与目标一致，确认落位后再销毁上层视频层；
        /// 若超时未确认，则回退到 SPI_SETDESKWALLPAPER 同步阻塞兜底。该等待在后台线程执行，不卡 UI。</summary>
        private void SetSystemWallpaper(string path, Rectangle bounds, bool synchronous)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    Logger.Log($"[WallpaperManager] 静态壁纸文件不存在: {path}");
                    return;
                }

                // 预缩放到屏幕尺寸：让 explorer 避开对大图原图的解码转码重活。
                string prepared = PrepareWallpaperImage(path, bounds);

                // 限流：快速连点时给 explorer 喘息，防止任务堆积导致"资源管理器未响应"。
                var since = DateTime.UtcNow - _lastSystemWallpaperSetUtc;
                if (since >= TimeSpan.Zero && since < MinSystemWallpaperSetInterval)
                    Thread.Sleep(MinSystemWallpaperSetInterval - since);
                _lastSystemWallpaperSetUtc = DateTime.UtcNow;

                var sw = Stopwatch.StartNew();
                if (synchronous)
                {
                    // 动→静：需要确认新系统壁纸落位后再销毁视频层，避免旧图闪现。
                    // 两步缺一不可：
                    // ① 轮询 GetDesktopWallpaper 路径确认 explorer 已受理新壁纸（30~200ms）；
                    // ② 路径确认后系统层还在播放 C→B 交叉叠化动画（0.5~1s），必须等动画播完
                    //    再撤视频层，否则用户看到的就是"旧图渐变到新图"的全过程。
                    Win32.SetDesktopWallpaper(prepared);
                    Logger.Log($"[WallpaperManager] 已发起静态壁纸设置(IDW): {path}");
                    bool confirmed = WaitForDesktopWallpaper(prepared, TimeSpan.FromMilliseconds(900));
                    Logger.Log($"[WallpaperManager] 静态壁纸落位确认: {confirmed}, 总耗时 {sw.ElapsedMilliseconds}ms");
                    if (!confirmed)
                    {
                        // 兜底：IDW 未在预期时间内落位，改用 SPI 强制同步等待
                        Win32.SystemParametersInfo(Win32.SPI_SETDESKWALLPAPER, 0, prepared,
                            Win32.SPIF_UPDATEINIFILE | Win32.SPIF_SENDCHANGE);
                        Logger.Log($"[WallpaperManager] 已同步设置静态壁纸(SPI 兜底): {path} 耗时 {sw.ElapsedMilliseconds}ms");
                    }
                    // 叠化余量：等系统 C→B 过渡动画播完再返回（返回后调用方才销毁视频层）。
                    // 在后台线程 Sleep，不卡 UI；状态栏"切换中…"提示覆盖这段等待。
                    Thread.Sleep(WallpaperFadeGraceMs);
                    Logger.Log($"[WallpaperManager] 叠化余量等待完成(+{WallpaperFadeGraceMs}ms), 可安全销毁视频层");
                }
                else
                {
                    // IDesktopWallpaper 异步：轻快，适合没有旧动态层遮挡的静→静切换
                    Win32.SetDesktopWallpaper(prepared);
                    Logger.Log($"[WallpaperManager] 已异步设置静态壁纸(IDW): {path} 耗时 {sw.ElapsedMilliseconds}ms");
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[WallpaperManager] 设置静态壁纸失败: {ex.Message}");
                // 主路径异常时 SPI 兜底
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

        /// <summary>轮询 IDesktopWallpaper.GetWallpaper，直到返回路径与预期一致或超时。
        /// 用于动→静切换时确认新壁纸已落位，避免提前销毁视频层导致闪现旧图。</summary>
        private static bool WaitForDesktopWallpaper(string expected, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    var current = Win32.GetDesktopWallpaper();
                    if (string.Equals(current, expected, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                catch
                {
                    // GetDesktopWallpaper 可能因 COM 忙而失败，忽略并继续轮询
                }
                Thread.Sleep(30);
            }
            return false;
        }

        /// <summary>把壁纸图片预缩放到屏幕尺寸（仅 fill 模式；fit/center 需保留原始尺寸语义）。
        /// 结果缓存到 %LOCALAPPDATA%\DynamicWallpaper\wpcache：同一张图重复切换时直接命中，
        /// 不再重复解码缩放。任何异常都回退返回原始路径（功能不受影响，只是慢一点）。</summary>
        private string PrepareWallpaperImage(string path, Rectangle bounds)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                if (bounds.Width <= 0 || bounds.Height <= 0) return path;
                var fit = string.IsNullOrWhiteSpace(_config.WallpaperFit) ? "fill" : _config.WallpaperFit.Trim().ToLowerInvariant();
                if (fit != "fill") return path; // fit/center 保持原图，由系统按样式处理

                string cacheDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "DynamicWallpaper", "wpcache");
                Directory.CreateDirectory(cacheDir);

                string key = $"{Path.GetFileNameWithoutExtension(path)}_{bounds.Width}x{bounds.Height}";
                string cacheFile = Path.Combine(cacheDir, key + ".jpg");
                if (File.Exists(cacheFile) && File.GetLastWriteTimeUtc(cacheFile) >= File.GetLastWriteTimeUtc(path))
                {
                    Logger.Log($"[WallpaperManager] 预缩放缓存命中: {cacheFile} ({sw.ElapsedMilliseconds}ms)");
                    return cacheFile; // 缓存命中：源文件未变
                }

                using var src = System.Drawing.Image.FromFile(path);
                // 已是屏幕尺寸（且非超大 PNG 之类）则直接用原图
                if (src.Width == bounds.Width && src.Height == bounds.Height)
                {
                    src.Dispose();
                    Logger.Log($"[WallpaperManager] 壁纸已是屏幕尺寸，无需缩放 ({sw.ElapsedMilliseconds}ms)");
                    return path;
                }

                // fill = cover：等比缩放到铺满屏幕后居中裁剪
                double scale = Math.Max((double)bounds.Width / src.Width, (double)bounds.Height / src.Height);
                int nw = Math.Max(1, (int)Math.Round(src.Width * scale));
                int nh = Math.Max(1, (int)Math.Round(src.Height * scale));
                using var bmp = new System.Drawing.Bitmap(bounds.Width, bounds.Height);
                using (var g = System.Drawing.Graphics.FromImage(bmp))
                {
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                    g.DrawImage(src, new Rectangle((bounds.Width - nw) / 2, (bounds.Height - nh) / 2, nw, nh));
                }
                src.Dispose();

                string tmp = cacheFile + ".tmp";
                bmp.Save(tmp, System.Drawing.Imaging.ImageFormat.Jpeg);
                File.Delete(cacheFile);
                File.Move(tmp, cacheFile);
                Logger.Log($"[WallpaperManager] 预缩放完成: {path} -> {bounds.Width}x{bounds.Height} ({sw.ElapsedMilliseconds}ms)");
                return cacheFile;
            }
            catch (Exception ex)
            {
                Logger.Log($"[WallpaperManager] 预缩放壁纸失败（使用原图）: {ex.Message} ({sw.ElapsedMilliseconds}ms)");
                return path;
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

        /// <summary>按配置的壁纸适应方式写注册表 WallpaperStyle（系统壁纸层渲染时读取）。
        /// 10=fill（铺满裁剪，默认）/ 6=fit（完整显示）/ 0=center（原始居中）。</summary>
        private void ApplyWallpaperFitStyle()
        {
            try
            {
                var fit = string.IsNullOrWhiteSpace(_config.WallpaperFit) ? "fill" : _config.WallpaperFit.Trim().ToLowerInvariant();
                if (fit is not ("fill" or "fit" or "center")) fit = "fill";
                int style = fit switch { "fit" => 6, "center" => 0, _ => 10 };
                Registry.SetValue(@"HKEY_CURRENT_USER\Control Panel\Desktop", "WallpaperStyle", style.ToString());
                Registry.SetValue(@"HKEY_CURRENT_USER\Control Panel\Desktop", "TileWallpaper", "0");
            }
            catch (Exception ex)
            {
                Logger.Log($"[WallpaperManager] 设置壁纸适应方式失败: {ex.Message}");
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
