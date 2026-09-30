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
        /// <summary>上次构建屏幕表时的拓扑签名，见 ScreensSignature()。</summary>
        private string _lastScreenSig = "";
        /// <summary>串行化所有变更屏幕壁纸状态的操作（设置/清除/停止），防止启动自动恢复
        /// 与手动"设为壁纸"并发执行导致 Provider 被交叉 Dispose、渲染窗口被误关。</summary>
        private readonly SemaphoreSlim _screenOpLock = new(1, 1);

        /// <summary>当前进行中的屏幕操作取消源。新操作（切换/解除/退出）在排队等锁前先取消它，
        /// 使正在无限等待加载（网络壁纸加载慢）的旧操作立即回滚并释放锁，实现"手动打断加载"；
        /// 用户不主动切换/解除/退出时，旧操作会一直等加载完成，不再自动回退。</summary>
        private CancellationTokenSource? _opCts;
        private readonly object _opCtsLock = new();

        /// <summary>虚拟桌面检测（注册表 CurrentVirtualDesktop 轮询）。Win10/Win11 恒可用。</summary>
        private readonly VirtualDesktop? _vd;
        /// <summary>当前所处虚拟桌面 GUID（字符串键用于 DesktopAssignments）。未检测到为 Guid.Empty。</summary>
        private Guid _currentDesktop = Guid.Empty;

        private bool _userPaused;

        /// <summary>正在执行中的设置/清除/停止操作计数（含排队等待锁的）。
        /// 轮播据此避让：用户手动操作进行中时，轮播本轮直接跳过，避免打断手动切换。</summary>
        private int _opActive;
        /// <summary>是否有壁纸操作正在执行（供轮播避让判断）。</summary>
        public bool IsBusy => System.Threading.Volatile.Read(ref _opActive) > 0;

        /// <summary>壁纸轮播服务：按间隔自动切换已设置壁纸的屏幕（复用 SetWallpaperAsync）。</summary>
        private readonly WallpaperCarousel _carousel;

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

        /// <summary>远程直链 Content-Type 探测结果缓存（true=视频/音频流，false=图片）。</summary>
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> RemoteTypeProbeCache = new();

        /// <summary>判断远程直链是否带可识别的图片扩展名。无扩展名的直链（网易等 CDN）
        /// 无法凭 URL 区分图片/视频，必须按 Content-Type 探测。</summary>
        private static bool HasImageExtension(string path)
        {
            try
            {
                return Path.GetExtension(path)?.ToLowerInvariant() is ".jpg" or ".jpeg" or ".png" or ".bmp" or ".webp";
            }
            catch { return false; }
        }

        /// <summary>HEAD 探测远程直链 Content-Type（4s 超时，结果缓存）。
        /// 返回 true=视频/音频流、false=图片、null=探测失败或类型不明（调用方保持原处理）。</summary>
        private static async Task<bool?> ProbeRemoteIsVideoAsync(string url)
        {
            if (RemoteTypeProbeCache.TryGetValue(url, out var cached)) return cached;
            try
            {
                using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(4) };
                try { http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36"); } catch { }
                using var resp = await http.SendAsync(
                    new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Head, url));
                var ct = resp.Content?.Headers.ContentType?.MediaType ?? "";
                bool? result;
                if (ct.StartsWith("video/", StringComparison.OrdinalIgnoreCase) ||
                    ct.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)) result = true;
                else if (ct.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) result = false;
                else result = null;
                if (result != null) RemoteTypeProbeCache[url] = result.Value;
                Logger.Log($"[WallpaperManager] 远程直链类型探测: {ct} → {(result == true ? "视频流" : result == false ? "图片" : "未知")}");
                return result;
            }
            catch (Exception ex)
            {
                Logger.Log($"[WallpaperManager] 远程直链类型探测失败（保持原处理）: {ex.Message}");
                return null;
            }
        }

        /// <summary>程序启动前系统原本的静态壁纸路径，解除桌面时恢复。</summary>
        private string _originalWallpaper = "";

        public WallpaperManager(Config config)
        {
            _config = config;
            _vd = new VirtualDesktop();
            BuildScreens();
            ApplyPerformanceMode();
            _fs = new FullscreenMonitor();
            _fs.FullscreenChanged += _ => ApplyPlayState();
            _power = new PowerManager();
            _power.BatteryChanged += _ => ApplyPlayState();
            _watchdog.Elapsed += WatchdogTick;
            _carousel = new WallpaperCarousel(
                _config,
                GetCarouselScreens,
                CarouselApplyAsync,
                ShouldSkipCarousel);
        }

        /// <summary>收集当前「已设置壁纸」的屏幕（轮播只在这些屏之间切换）。</summary>
        private IReadOnlyList<WallpaperCarousel.CarouselScreen> GetCarouselScreens()
        {
            try
            {
                var list = _states.Values
                    .Where(s => s.Provider != null || s.IsStaticImage)
                    .Select(s => new WallpaperCarousel.CarouselScreen { Index = s.Index, CurrentPath = s.LastPath })
                    .ToList();
                Logger.Log($"[Carousel] 收集轮播屏幕：{list.Count}/{_states.Count} 屏，路径=" +
                           string.Join(" | ", list.Select(s => $"#{s.Index}:{Path.GetFileName(s.CurrentPath ?? "")}")));
                return list;
            }
            catch
            {
                // 看门狗重建屏幕表期间枚举竞争：本轮放弃，下个周期再来
                return new List<WallpaperCarousel.CarouselScreen>();
            }
        }

        /// <summary>轮播执行的切换：手动操作进行中则回避；否则走标准设置流程（叠化过渡 + 持久化）。
        /// 注意：轮播定时器回调在线程池线程上，而所有 Provider（WPF 窗口 / WebView2 COM 对象）
        /// 都在 UI 线程创建——跨线程直接访问 WebView2 会抛 "Unable to cast COM object ...
        /// ICoreWebView2Controller"（轮播到点不切换的根因）。因此整体调度回 UI 线程执行，
        /// 与手动"设为壁纸"点击（本就在 UI 线程）走完全相同的执行环境。</summary>
        private async Task CarouselApplyAsync(string path, WallpaperType type, int screenIndex)
        {
            if (IsBusy)
            {
                Logger.Log($"[Carousel] 跳过切换（忙 opActive={System.Threading.Volatile.Read(ref _opActive)}）：{Path.GetFileName(path)}");
                return;
            }
            var disp = System.Windows.Application.Current?.Dispatcher;
            if (disp != null && !disp.CheckAccess())
                await disp.InvokeAsync(() => SetWallpaperAsync(path, type, screenIndex, save: true)).Task.Unwrap();
            else
                await SetWallpaperAsync(path, type, screenIndex, save: true);
        }

        /// <summary>轮播避让条件：仅用户主动暂停 / 全屏应用（游戏/视频）置于前台时避让，
        /// 不打扰用户。电池不再避让——轮播切换（多为静态图）开销极小，用户明确期望幻灯片按时切换。</summary>
        private bool ShouldSkipCarousel() =>
            _userPaused
            || (_config.PauseOnFullscreen && _fs.Peek());

        /// <summary>轮播设置变更后由设置窗口调用：按新配置重启/停止轮播定时器。</summary>
        public void ApplyCarouselSettings() => _carousel.ApplySettings();

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
            _lastScreenSig = ScreensSignature();
        }

        /// <summary>当前显示器拓扑签名（数量+各屏矩形+主屏标记），用于检测"数量不变但布局变化"
        /// 的情况：笔记本外接 HDMI 后设主屏、改复制/扩展、分辨率变化等，屏幕数量可能不变。</summary>
        private string ScreensSignature()
        {
            try
            {
                return string.Join("|", ScreenManager.GetScreens()
                    .Select(s => $"{s.DeviceName}:{s.Bounds.X},{s.Bounds.Y},{s.Bounds.Width},{s.Bounds.Height}:{s.IsPrimary}"));
            }
            catch { return ""; }
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

            // 虚拟桌面检测：订阅切换事件并启动轮询（注册表方案，Win10/Win11 恒可用）。
            // 放在恢复分配之前：恢复使用的计划来源取决于"是否按虚拟桌面分别记忆"。
            if (_vd != null && _vd.Available)
            {
                _vd.DesktopChanged += OnVirtualDesktopChanged;
                _vd.Start();
                _currentDesktop = _vd.CurrentDesktopId;
            }

            // 恢复已保存的每屏分配。
            // 注意：多屏恢复必须逐屏顺序 await，绝不能并发发起——SetWallpaperAsync 排队等锁时
            // 会取消"上一个进行中的操作"（手动打断加载机制），并发恢复会互相取消，
            // 导致只有最后一屏恢复成功、其余屏看起来"壁纸自己解除"。
            if (_config.Assignments != null)
            {
                // 按虚拟桌面分别记忆：恢复"当前桌面"自己的分配；当前桌面无记录则回退全局 Assignments。
                List<ScreenAssignment>? bucket = null;
                bool useDesktop = _vd != null && _vd.Available && _config.PerDesktopEnabled
                                  && _currentDesktop != Guid.Empty
                                  && _config.DesktopAssignments.TryGetValue(_currentDesktop.ToString(), out bucket)
                                  && (bucket?.Count ?? 0) > 0;
                List<ScreenAssignment> source = useDesktop ? bucket! : _config.Assignments;
                var plan = BuildRestorePlan(source);
                Logger.Log($"[WallpaperManager] {(useDesktop ? $"按虚拟桌面恢复（桌面 {_currentDesktop}）" : "全局恢复")}，{plan.Count} 屏");
                if (plan.Count > 0)
                {
                    // 开机自启等桌面就绪（锁屏消失、DefView 可见）再恢复，避免锁屏/桌面未就绪时
                    // 过早挂壁纸把图标层藏起来、点击穿透失效。等待在后台线程进行，真正的恢复
                    // 仍回到 UI 线程执行（各 Provider 在 UI 线程创建）。
                    _ = Task.Run(async () =>
                    {
                        await WaitForDesktopReadyAsync();
                        var disp = System.Windows.Application.Current?.Dispatcher;
                        if (disp != null)
                            await disp.InvokeAsync(async () =>
                            {
                                await ApplyRestorePlanAsync(plan);
                                _config.Save();
                            });
                        else
                        {
                            await ApplyRestorePlanAsync(plan);
                            _config.Save();
                        }
                    });
                }
                else
                {
                    _config.Save();
                }
            }

            // 启动壁纸轮播（按配置决定是否开启）
            _carousel.ApplySettings();
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

        /// <summary>把已保存的每屏分配构建为开机恢复计划，处理拓扑变化导致的屏幕索引错位：
        /// 若某分配保存的屏幕索引超出当前屏幕范围（例：“仅在2显示”时屏2变成 index 0，而保存的是
        /// 双屏时的 index 1），则把该分配重映射到当前最小未使用的可用屏，避免“重启后壁纸完全不显示”。
        /// 单屏场景下越界分配只会映射到 index 0，结果正确。已映射到某屏的索引不再被复用，避免重复。</summary>
        private List<(string Path, WallpaperType Type, int Index)> BuildRestorePlan(List<ScreenAssignment> src)
        {
            var used = new HashSet<int>();
            var result = new List<(string Path, WallpaperType Type, int Index)>();
            if (src == null) return result;
            int maxIndex = ScreenManager.Count - 1;
            foreach (var a in src)
            {
                if (!IsRemoteUrl(a.Path) && !File.Exists(a.Path)) continue;
                int idx = a.Index;
                if (!_states.ContainsKey(idx))
                {
                    // 越界：取当前最小未使用的可用屏索引（拓扑变化后屏号重排）
                    idx = -1;
                    for (int i = 0; i <= maxIndex; i++)
                    {
                        if (_states.ContainsKey(i) && !used.Contains(i)) { idx = i; break; }
                    }
                    if (idx < 0) continue; // 无可用屏
                }
                if (used.Contains(idx)) continue; // 该屏已被占用，跳过重复分配
                used.Add(idx);
                result.Add((a.Path, a.Type, idx));
            }
            return result;
        }

        /// <summary>判断当前前台是否为 Windows 锁屏/登录界面（LockApp / LogonUI）。
        /// 开机自启时锁屏可能仍在前台，桌面外壳未就绪，过早挂壁纸会把图标层藏起来、点击穿透失效。
        /// 用于延迟启动恢复，等用户进入桌面后再应用。</summary>
        private static bool IsLockScreenActive()
        {
            try
            {
                IntPtr fg = Win32.GetForegroundWindow();
                if (fg == IntPtr.Zero) return false;
                Win32.GetWindowThreadProcessId(fg, out uint pid);
                if (pid == 0) return false;
                using var p = Process.GetProcessById((int)pid);
                var n = p.ProcessName.ToLowerInvariant();
                return n == "lockapp" || n == "logonui";
            }
            catch { return false; }
        }

        /// <summary>等待桌面真正可交互后再恢复壁纸：锁屏已消失且图标层(DefView)可见。
        /// 避免开机自启在锁屏/桌面未就绪时过早挂壁纸（图标被隐藏、右击失效的根因之一）。
        /// 最多等待约 15 秒，超时则不再等待、照常恢复（宁可晚显示也不卡死在错误的 Z 序上）。</summary>
        private async Task WaitForDesktopReadyAsync()
        {
            for (int i = 0; i < 30; i++)
            {
                bool shellReady = false;
                try
                {
                    IntPtr defView = WorkerWInjector.FindTopLevelDefView();
                    shellReady = defView != IntPtr.Zero && Win32.IsWindowVisible(defView);
                }
                catch { }
                if (!IsLockScreenActive() && shellReady)
                {
                    Logger.Log("[WallpaperManager] 桌面已就绪，开始恢复壁纸");
                    return;
                }
                await Task.Delay(500);
            }
            Logger.Log("[WallpaperManager] 等待桌面就绪超时，照常恢复壁纸");
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
            Providers.WebProvider.FitMode = fit;

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
                case Providers.WebProvider web: web.ApplyFitMode(); break;
            }
        }

        /// <summary>读取指定壁纸路径的旋转角度（0/90/180/270）；未设置返回 0。</summary>
        public int GetRotation(string path)
        {
            if (string.IsNullOrEmpty(path)) return 0;
            return _config.WallpaperRotations.TryGetValue(path.ToLowerInvariant(), out var v) ? v : 0;
        }

        /// <summary>清除指定壁纸的旋转记录（从壁纸库移除时调用）。否则重新添加同名壁纸时
        /// 旧旋转角度残留生效，与新设置互相冲突导致旋转混乱；同时清理系统 API 降级路径的旋转副本。</summary>
        public void ClearRotation(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            if (_config.WallpaperRotations.Remove(path.ToLowerInvariant()))
            {
                _config.Save();
                Logger.Log($"[WallpaperManager] 已清除旋转记录：{Path.GetFileName(path)}");
            }
            try
            {
                var dir = Path.Combine(AppPaths.RootDirectory, "RotatedCache");
                if (!Directory.Exists(dir)) return;
                var stem = Path.GetFileNameWithoutExtension(path);
                foreach (var r in new[] { 90, 180, 270 })
                    foreach (var ext in new[] { ".jpg", ".png" })
                    {
                        var f = Path.Combine(dir, $"{stem}_rot{r}{ext}");
                        if (File.Exists(f)) File.Delete(f);
                    }
            }
            catch (Exception ex)
            {
                Logger.Log($"[WallpaperManager] 清理旋转副本失败: {ex.Message}");
            }
        }

        /// <summary>把旋转角度写入指定 Provider 实例（不立即重绘，供创建时一次性注入）。</summary>
        private static void SetProviderRotation(IWallpaperProvider provider, int deg)
        {
            switch (provider)
            {
                case Providers.ImageProvider img: img.Rotation = deg; break;
                case Providers.GifProvider gif: gif.Rotation = deg; break;
                case Providers.VideoProvider vid: vid.Rotation = deg; break;
                case Providers.StaticFadeProvider fade: fade.Rotation = deg; break;
                case Providers.WebProvider web: web.Rotation = deg; break;
            }
        }

        /// <summary>把旋转角度写入 Provider 实例并立即重绘已渲染内容（须在 UI 线程调用）。</summary>
        private static void ApplyRotationTo(IWallpaperProvider provider, int deg)
        {
            Logger.Log($"[WallpaperManager] ApplyRotationTo 类型={provider.GetType().Name} 角度={deg}°");
            SetProviderRotation(provider, deg);
            switch (provider)
            {
                case Providers.ImageProvider img: img.ApplyRotation(); break;
                case Providers.GifProvider gif: gif.ApplyRotation(); break;
                case Providers.VideoProvider vid: vid.ApplyRotation(); break;
                case Providers.StaticFadeProvider fade: fade.ApplyRotation(); break;
                case Providers.WebProvider web: web.ApplyRotation(); break;
            }
        }

        /// <summary>旋转指定壁纸（按路径）。delta 为相对角度：+90=顺时针90°、-90=逆时针90°、0=不旋转（重置）。
        /// 累加后取模 360（>=360 自动归零）；立即作用于正在显示该壁纸的屏幕，并持久化（下次设置/轮播时自动生效）。
        /// 返回实际生效的屏幕数（0 表示该壁纸当前未在任意屏幕显示，旋转仅记录、下次显示时生效）。</summary>
        public int RotateWallpaper(string path, int delta)
        {
            if (string.IsNullOrEmpty(path)) return 0;
            var key = path.ToLowerInvariant();
            int cur = _config.WallpaperRotations.TryGetValue(key, out var v) ? v : 0;
            int newRot = delta == 0 ? 0 : (((cur + delta) % 360) + 360) % 360;
            _config.WallpaperRotations[key] = newRot;

            int applied = 0;
            int totalScreens = _states.Count;
            foreach (var st in _states.Values)
            {
                if (string.IsNullOrEmpty(st.LastPath) ||
                    !string.Equals(st.LastPath, path, StringComparison.OrdinalIgnoreCase)) continue;
                var provider = st.Provider;
                Logger.Log($"[WallpaperManager] 旋转命中屏{st.Index}：Provider={(provider?.GetType().Name ?? "null")} IsStaticImage={st.IsStaticImage}");
                if (provider == null)
                {
                    // 系统 API 降级路径：当前屏是系统静态壁纸（无 Provider 窗口层）。
                    // 右键旋转需直接按新角度重设旋转后的壁纸，并沿用设置里的适应方式（Fill/Fit/Center）。
                    if (st.IsStaticImage)
                    {
                        try
                        {
                            var rotPath = CreateRotatedImage(st.LastPath, newRot) ?? st.LastPath;
                            SetSystemWallpaper(rotPath);
                            applied++;
                        }
                        catch (Exception ex)
                        {
                            Logger.Log($"[WallpaperManager] 应用旋转(系统API)失败: {ex.Message}");
                        }
                    }
                    continue;
                }
                try
                {
                    if (System.Windows.Application.Current?.Dispatcher != null)
                        System.Windows.Application.Current.Dispatcher.InvokeAsync(() => ApplyRotationTo(provider, newRot));
                    else
                        ApplyRotationTo(provider, newRot);
                    applied++;
                }
                catch (Exception ex)
                {
                    Logger.Log($"[WallpaperManager] 应用旋转失败: {ex.Message}");
                }
            }
            _config.Save();
            Logger.Log($"[WallpaperManager] 旋转壁纸 {Path.GetFileName(path)} {cur}°->{newRot}°（delta={delta}，已作用屏幕数={applied}/{totalScreens}）");
            return applied;
        }

        /// <summary>旋转某个幻灯片文件夹当前正在显示的壁纸（按已设置屏幕的当前路径分别旋转）。
        /// 返回实际命中并旋转的屏幕数。</summary>
        public int RotateWallpaperByFolder(string folder, int delta)
        {
            if (string.IsNullOrEmpty(folder)) return 0;
            // 归一化：去掉结尾分隔符并大小写不敏感比较，避免幻灯片文件夹路径带/不带尾斜杠
            // 或大小写差异导致 Path.GetDirectoryName(st.LastPath) 与 folder 不匹配、整轮旋转 0 命中。
            var normFolder = folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Trim();
            int hit = 0;
            foreach (var st in _states.Values)
            {
                if (string.IsNullOrEmpty(st.LastPath)) continue;
                var dir = Path.GetDirectoryName(st.LastPath)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Trim();
                if (!string.Equals(dir, normFolder, StringComparison.OrdinalIgnoreCase)) continue;
                RotateWallpaper(st.LastPath, delta);
                hit++;
            }
            Logger.Log($"[WallpaperManager] 旋转幻灯片文件夹壁纸：{folder}（归一化={normFolder}，命中屏幕数={hit}）");
            return hit;
        }

        /// <summary>为系统 API 直设路径生成旋转后的临时图片（仅降级路径使用；WebView2 路径用 CSS 旋转）。
        /// 旋转副本写入程序目录 RotatedCache，失败返回 null（回退原图）。</summary>
        private static string? CreateRotatedImage(string path, int rotation)
        {
            try
            {
                var r = ((rotation % 360) + 360) % 360;
                if (r == 0 || !File.Exists(path)) return null;
                using var fs = File.OpenRead(path);
                using var img = System.Drawing.Image.FromStream(fs);
                img.RotateFlip(r == 90
                    ? System.Drawing.RotateFlipType.Rotate90FlipNone
                    : r == 180
                        ? System.Drawing.RotateFlipType.Rotate180FlipNone
                        : System.Drawing.RotateFlipType.Rotate270FlipNone);
                var dir = Path.Combine(AppPaths.RootDirectory, "RotatedCache");
                Directory.CreateDirectory(dir);
                bool isPng = string.Equals(Path.GetExtension(path), ".png", StringComparison.OrdinalIgnoreCase);
                var outPath = Path.Combine(dir,
                    $"{Path.GetFileNameWithoutExtension(path)}_rot{r}{(isPng ? ".png" : ".jpg")}");
                using var outFs = File.Create(outPath);
                img.Save(outFs, isPng
                    ? System.Drawing.Imaging.ImageFormat.Png
                    : System.Drawing.Imaging.ImageFormat.Jpeg);
                return outPath;
            }
            catch (Exception ex)
            {
                Logger.Log($"[WallpaperManager] 生成旋转图片失败: {ex.Message}");
                return null;
            }
        }

        /// <summary>开始一次屏幕操作：先打断上一个进行中的操作（使其尽快释放 _screenOpLock，
        /// 实现手动打断加载），再排队等锁；拿锁后返回本次操作的取消令牌。若排队期间又被更新的
        /// 操作打断（令牌已取消），调用方应在拿到锁后立即检查并退出。</summary>
        private async Task<CancellationToken> BeginScreenOperationAsync()
        {
            var cts = new CancellationTokenSource();
            lock (_opCtsLock)
            {
                try { _opCts?.Cancel(); } catch { }
                try { _opCts?.Dispose(); } catch { }
                _opCts = cts;
            }
            // 排队留痕：锁被占用说明上一操作（如启动恢复）尚未完成，"点击无反应"时据此排查
            if (_screenOpLock.CurrentCount == 0)
                Logger.Log("[WallpaperManager] 操作排队等待串行锁（上一操作仍在执行）");
            await _screenOpLock.WaitAsync();
            return cts.Token;
        }

        /// <summary>按计划逐屏顺序恢复壁纸（await 串行，前一个完成才发起下一个）。
        /// 多屏恢复绝不能并发发起：SetWallpaperAsync 排队等锁时会取消"上一个进行中的操作"
        /// （手动打断加载机制），并发恢复会互相取消，导致只有最后一屏恢复成功、
        /// 其余屏看起来"壁纸自己解除"（系统调换屏幕位置/改主屏后壁纸丢失的根因）。</summary>
        private async Task ApplyRestorePlanAsync(List<(string Path, WallpaperType Type, int Index)> plan)
        {
            foreach (var (path, type, index) in plan)
            {
                try
                {
                    await SetWallpaperAsync(path, type, index, save: false);
                }
                catch (Exception ex)
                {
                    Logger.Log($"[WallpaperManager] 恢复屏幕 {index} 壁纸失败: {ex.Message}");
                }
            }
        }

        /// <summary>将指定内容设为某屏壁纸。screenIndex 默认 0（主屏）。
        /// 整个流程在 _screenOpLock 串行锁内执行，保证与自动恢复/清除/停止互斥，
        /// 同一时刻同一屏幕只有一个设置任务在创建/销毁 Provider 与渲染窗口。
        /// status 为可选切换状态回调（正在切换/已应用/失败原因），供 UI 状态栏反馈。</summary>
        public async Task SetWallpaperAsync(string path, WallpaperType type, int screenIndex = 0, bool save = true, Action<string>? status = null)
        {
            System.Threading.Interlocked.Increment(ref _opActive);
            // 闪屏取证（r16）：从"点击设为壁纸"这一刻开始记录屏幕变化，
            // 把"看不清闪的是什么"变成日志里的一行行数据（仅明显变化的帧才记），
            // 用于区分：流光过渡层 / 桌面重组露底 / 应用窗口内部局部变化。
            FlashProbe.Watch($"{Path.GetFileName(path)} 屏{screenIndex}");
            // 切换过渡流光：会话开始（点击立即显示；任何类型的切换——含静态图原地换图——
            // 都会播完一段完整的淡入/停留/淡出，见 SwitchOverlay）
            if (_states.TryGetValue(screenIndex, out var stEarly))
                SwitchOverlay.Begin(screenIndex, stEarly.Bounds);
            System.Threading.CancellationToken opToken;
            try { opToken = await BeginScreenOperationAsync(); }
            catch { SwitchOverlay.End(screenIndex); throw; }
            try
            {
                if (opToken.IsCancellationRequested) return; // 排队期间已被更新的操作打断
                if (!_states.TryGetValue(screenIndex, out var st)) return;

                status?.Invoke("正在切换：" + Path.GetFileName(path));

                // 同步壁纸适应方式到各 Provider（静态属性，Provider 创建时读取）
                SyncFitMode();
                // 旋转角度按壁纸路径独立注入：在各 Provider 创建前用 GetRotation 写入实例（见下方各分支 Show 之前）。

                // 静态图片：直接用系统 API（IDesktopWallpaper）设置桌面壁纸。
                // 若当前屏幕正在播放动态壁纸 A，切换顺序必须"先设系统壁纸 S、后销毁 A"：
                // 旧流程先销毁 A 会露出系统上一张静态壁纸（残留 C），再设 S，形成 A→C→S 的
                // 中间停留；新流程 S 先落到底层（被 A 盖住、用户无感知），A 不做渐出、
                // 通过 WebView2 过渡窗口把 S 渐入覆盖 A，最后统一清理，形成 A→S 无缝直切。
                // 无扩展名的远程直链（网易等 CDN）凭 URL 无法区分图片/视频；若实为视频流
                // 却按静态图渲染会建 <img> → 左上角裂图图标 + 10s 超时黑屏回退。
                // HEAD 探测 Content-Type，视频流直接改走视频壁纸分支（结果缓存，只探一次）。
                if (type == WallpaperType.Image && IsRemoteUrl(path) && !HasImageExtension(path))
                {
                    var probeVideo = await ProbeRemoteIsVideoAsync(path);
                    if (probeVideo == true)
                    {
                        Logger.Log($"[WallpaperManager] 远程直链实为视频流，改走视频壁纸分支: {path}");
                        type = WallpaperType.Video;
                    }
                }

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

                        // 当前屏若有窗口层壁纸在运行，先异步销毁（系统壁纸层直接承载静态图，无需叠化）；
                        // 不 await：WebView2 Controller.Close() 可能长时间阻塞，同步等待会拖住切换。
                        if (prevProvider != null)
                        {
                            System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                            {
                                try { prevProvider.Dispose(); }
                                catch (Exception ex) { Logger.Log($"[WallpaperManager] 旧壁纸 Dispose 异常: {ex.Message}"); }
                            }, System.Windows.Threading.DispatcherPriority.Background);
                        }
                        // 系统 API 不支持旋转，需要旋转时先生成旋转副本再设置
                        var setPath = CreateRotatedImage(path, GetRotation(path)) ?? path;
                        SetSystemWallpaper(setPath);
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
                        SetProviderRotation(vpImage, GetRotation(path));
                        bool navOk = await vpImage.NavigateImageAsync(path);
                        // 超时从 6s 收紧到 2s：本地图片（含 8MB 大图）加载都是毫秒级，2s 内不就绪
                        // 基本可判定本路径失败。实测 15/15 次都是等满 6s 后转冷启动——用户感受就是
                        // “静态切静态很慢”，白等 6s 是主因。navOk=false 时短路，不再空等。
                        // retryImageOnError：跨目录换图时虚拟主机映射（异步 IPC）尚未生效即请求
                        // → 404 → onerror，实测 100% 复现；onerror 现在会被立即检测到并自动重试
                        // 换图脚本（最多 3 次 × 300ms），成功则毫秒级完成，失败也快速转冷启动。
                        bool ready = navOk && await vpImage.WaitVideoReadyAsync(TimeSpan.FromSeconds(2), retryImageOnError: true);
                        if (ready)
                        {
                            st.LastPath = path;
                            st.LastType = type;
                            st.IsStaticImage = true;
                            if (save) PersistAssignments();
                            // 原地换图成功（零窗口重建、零闪动路径）——此前成功是静默的，
                            // 出问题时无法区分"走了快速路径且正常"与"根本没走快速路径"。
                            Logger.Log($"[WallpaperManager] 静态原地换图成功（无窗口重建）：{Path.GetFileName(path)} 旋转={GetRotation(path)}°");
                            status?.Invoke("已应用：" + Path.GetFileName(path));
                            return;
                        }
                        // 快速路径未就绪不再直接放弃（否则表现为"设置不成功、保持原壁纸"）：
                        // 打诊断（图片元素是否存在/是否加载完/onerror 结果/当前地址）后继续走下方冷启动路径
                        // （销毁重建静态层），保证一定能切过去。
                        var diag = await vpImage.DiagnoseImageAsync();
                        Logger.Log($"[WallpaperManager] 静态图快速切换未就绪（navOk={navOk}），转冷启动重建静态层。图片状态: {diag}");
                    }

                    // 静态壁纸即时显示：由 WebView2 静态层（VideoProvider 图片模式，虚拟主机映射
                    // 本地图片，绕开 data URI 2MB 上限）承载。历史 WPF/GDI 窗口层在 Win11 raised
                    // desktop 下不被 DWM 合成（Attach 成功但完全不显示）；WebView2 走
                    // DirectComposition 独立通道（视频已验证可被 DWM 真实合成到桌面），图片同通道，
                    // 图片加载完成即显示，无系统 IDesktopWallpaper 异步生效的秒级延迟。
                    IntPtr staticChildHwnd = await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        var p = new VideoProvider();
                        SetProviderRotation(p, GetRotation(path));
                        // forceImage：静态壁纸分支的冷启动必须按图片模式渲染。无扩展名的远程图片直链
                        // IsImageFile 判 false 会误建 <video>（图片渲染不出、旋转作用在 v 元素上错乱）。
                        p.Show(path, st.Bounds, forceImage: true);
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
                        // 拿不到 WorkerW 时销毁刚创建的新窗口，还原旧状态，避免残留（异步销毁不阻塞）
                        System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                        {
                            try { st.Provider?.Dispose(); } catch { }
                        }, System.Windows.Threading.DispatcherPriority.Background);
                        st.Provider = prevProvider;
                        st.IsStaticImage = prevIsStaticImage;
                        throw new InvalidOperationException("无法获取桌面 WorkerW 层，请尝试重启资源管理器或系统。");
                    }

                    // UI 线程挂接并显示：挂接前先把新层置为全透明——图片未就绪前不可见
                    // （露出旧壁纸），否则加载慢/失败的 10s 内桌面被黑屏新层盖住，回退前
                    // 一直黑屏且叠加旧层内容已可能被快速路径改写，表现为"黑屏卡死"。
                    await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        st.WorkerW = staticWorkerW;
                        if (st.Provider != null)
                        {
                            try { Win32.SetLayeredWindowAttributes(st.Provider.Handle, 0, 0, Win32.LWA_ALPHA); } catch { }
                            st.Provider.AttachTo(st.WorkerW, st.Bounds);
                        }
                    });

                    // 等图片真正加载完成（窗口透明露出旧壁纸层）。
                    // 10s 含 Controller 创建重试（0x8007139F 资源竞争）+ 导航 + 图片加载全过程。
                    if (st.Provider is VideoProvider vpStatic)
                    {
                        bool imgReady = await vpStatic.WaitVideoReadyAsync(TimeSpan.FromSeconds(10));
                        if (!imgReady)
                        {
                            // 图片未就绪：销毁新静态层（全程透明，桌面无黑屏过程），恢复旧壁纸。
                            Logger.Log("[WallpaperManager] 静态层图片未就绪，回退恢复原壁纸");
                            // 快速路径可能已把旧静态层 img 的 src 原地换成本次失败地址
                            // （回退后残留"左上角裂图图标"），把旧层内容恢复为原壁纸路径。
                            if (prevProvider is VideoProvider prevImg && prevImg.IsImageMode &&
                                !string.IsNullOrEmpty(st.LastPath))
                            {
                                var prevPath = st.LastPath;
                                _ = System.Windows.Application.Current.Dispatcher.InvokeAsync(async () =>
                                {
                                    try
                                    {
                                        // 回退重放换图同样要按原壁纸路径注入旋转角度（r14）：
                                        // 该 Provider 随后被复用时 Rotation 若残留新壁纸的角度，
                                        // 恢复出的旧壁纸会带错误旋转/布局。
                                        SetProviderRotation(prevImg, GetRotation(prevPath));
                                        await prevImg.NavigateImageAsync(prevPath);
                                    }
                                    catch (Exception ex) { Logger.Log($"[WallpaperManager] 回退恢复旧静态层失败: {ex.Message}"); }
                                });
                            }
                            System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                            {
                                try { st.Provider?.Dispose(); } catch { }
                            }, System.Windows.Threading.DispatcherPriority.Background);
                            st.Provider = prevProvider;
                            st.IsStaticImage = prevIsStaticImage;
                            status?.Invoke("切换失败：静态图加载未就绪（已保持原壁纸）");
                            return;
                        }
                        // 就绪：把此前挂在父客户区之外的新静态层移入正确位置显示（旧壁纸在此之前一直可见）。
                        // 原实现用窗口级 alpha 0→255 渐入，依赖 WS_EX_LAYERED；去掉分层后 alpha 调用无效，
                        // 窗口会一直留在屏外 → 壁纸不显示，因此必须改为显式移入。
                        // 移入前校验状态身份：等待期间状态若被重建（拓扑变化/快速连切），
                        // st.Bounds 已是新拓扑尺寸而新窗口是旧尺寸——落位会造成"壁纸偏移左上角"。
                        if (st.Provider != vpStatic)
                        {
                            Logger.Log("[WallpaperManager] 静态层就绪前状态已重建，销毁孤儿静态层并回滚");
                            var orphanStatic = vpStatic;
                            System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                            {
                                try { orphanStatic.Dispose(); } catch { }
                            }, System.Windows.Threading.DispatcherPriority.Background);
                            st.Provider = prevProvider;
                            st.IsStaticImage = prevIsStaticImage;
                            status?.Invoke("切换被打断（显示设置变化），已保持原壁纸");
                            return;
                        }
                        WorkerWInjector.ShowWallpaperWindow(vpStatic.Handle, st.WorkerW, st.Bounds);
                        // r17：移入后持续复核（父窗口归属/分层样式/Z 序沉在图标层之下/落位/渲染比例），
                        // 异常即自愈——一次性设置会被系统异步副作用破坏，破坏后就是"图标被覆盖、
                        // 右键不能用、壁纸偏移左上角"。身份守卫保证切换后作废。
                        SchedulePlacementGuard(st, vpStatic, "静态层");

                        // 移入后延时复核（fire-and-forget）：“壁纸偏移左上角”为间歇性残留状态，
                        // 移入瞬间的落位校验（ShowWallpaperWindow 内）抓不到“移入后被再次移动”。
                        // 2s 后比对实际矩形与期望矩形（父客户区换算），不符只记日志定位真凶，不自动改
                        // （此刻若再 SetWindowPos 会与身份守卫/后续切换产生新的竞态）。
                        var rvChild = vpStatic.Handle;
                        var rvBounds = st.Bounds;
                        _ = Task.Run(async () =>
                        {
                            await Task.Delay(2000);
                            try
                            {
                                if (!Win32.IsWindow(rvChild)) return;
                                if (!Win32.GetWindowRect(rvChild, out var rr)) return;
                                int rw = rr.Width, rh = rr.Height;
                                // GetWindowRect 返回**屏幕坐标**，期望值就是屏幕矩形 rvBounds。
                                // （r17 修正：此前拿父客户区坐标 ex/ey 与屏幕坐标比较——父客户区原点
                                // 不等于屏幕原点时会误报"偏差"，把正常的落位记成异常。）
                                bool off = Math.Abs(rr.Left - rvBounds.X) > 2 || Math.Abs(rr.Top - rvBounds.Y) > 2 ||
                                           Math.Abs(rw - rvBounds.Width) > 2 || Math.Abs(rh - rvBounds.Height) > 2;
                                if (off)
                                    Logger.Log($"[WallpaperManager] 移入2秒后复核偏差：期望 screen=({rvBounds.X},{rvBounds.Y}) size={rvBounds.Width}x{rvBounds.Height}，实际 pos=({rr.Left},{rr.Top}) size={rw}x{rh}（窗口在移入后被其他来源移动）");
                                // WebView2 内部视图子窗口矩形（Chrome_WidgetWin_1 / 渲染层）：
                                // 窗口几何全对而壁纸内容仍偏移时，偏移必发生在 WebView2 内部合成层——
                                // 把视图层矩形打出来即可定位是"宿主窗口偏"还是"Chromium 视图偏"。
                                var sb = new System.Text.StringBuilder();
                                IntPtr c = IntPtr.Zero;
                                int views = 0;
                                while ((c = Win32.FindWindowEx(rvChild, c, null, null)) != IntPtr.Zero && views < 8)
                                {
                                    views++;
                                    if (Win32.GetWindowRect(c, out var cr))
                                        sb.Append($"{Win32.GetClassName(c)}=({cr.Left},{cr.Top},{cr.Width}x{cr.Height}) ");
                                }
                                Logger.Log($"[WallpaperManager] 移入2秒后视图层: 宿主=({rr.Left},{rr.Top},{rw}x{rh}) 子窗口[{views}]: {sb}");
                            }
                            catch { }
                        });
                    }
                    // Win11 raised desktop（Progman 承载）下，WebView2 静态层作为 WS_EX_LAYERED 子窗口
                    // 挂到 Progman 后 DWM 不会自动合成，必须强制一次 DWM 合成才显示（与视频同因同解）。
                    // 仅 raised desktop 触发；经典 WorkerW 路径由 DWM 自动合成，无需此操作。
                    if (WorkerWInjector.IsRaisedDesktop() && st.Provider is VideoProvider vpStaticForced)
                    {
                        var cHwnd = vpStaticForced.Handle;
                        var cWorker = st.WorkerW;
                        var cBounds = st.Bounds;
                        _ = System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                        {
                            if (st.Provider == vpStaticForced && cHwnd != IntPtr.Zero && cWorker != IntPtr.Zero)
                                WorkerWInjector.ForceDwmComposition(cHwnd, cWorker, cBounds);
                        });
                    }
                    if (st.Provider == null) return;

                    // 销毁旧壁纸（动态 A 或旧静态层），静态 WebView2 层已就绪，无残留。
                    // 异步销毁不等待：WebView2 销毁可能长时间阻塞，同步等待会让切换无响应。
                    //
                    // r15：销毁前多留 250ms —— 新层此刻刚被 SetWindowPos 移入，DWM 需要 1~3 帧
                    // 才会把它真正合成到桌面；若旧层在这几帧内就被销毁，画面会出现"新旧皆无"的
                    // 空档，露出底层（系统壁纸）→ 用户看到的"切壁纸闪一下"。旧层此时位于新层
                    // 之下（新层 move in 时置于 Z 序顶部），多留一会儿完全不可见、无副作用。
                    // 动态路径的 CrossfadeAsync 本身有 300ms 交接期，此处补齐静态路径的差额。
                    if (prevProvider != null)
                    {
                        var staleProvider = prevProvider;
                        _ = Task.Run(async () =>
                        {
                            try { await Task.Delay(250); } catch { }
                            try
                            {
                                System.Windows.Application.Current?.Dispatcher.InvokeAsync(() =>
                                {
                                    try { staleProvider.Dispose(); }
                                    catch (Exception ex) { Logger.Log($"[WallpaperManager] 旧壁纸 Dispose 异常: {ex.Message}"); }
                                }, System.Windows.Threading.DispatcherPriority.Background);
                            }
                            catch { /* 程序退出中：Dispatcher 已关闭，交给进程回收 */ }
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
                    Logger.Log($"[WallpaperManager] 已设静态壁纸 屏{screenIndex}：path={path} Provider={st.Provider?.GetType().Name} IsStaticImage={st.IsStaticImage}");
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
                        SetProviderRotation(provider, GetRotation(path));
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
                    // 拿不到 WorkerW 时销毁刚创建的新窗口，还原旧状态，避免残留（异步销毁不阻塞）
                    System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        try { st.Provider?.Dispose(); } catch { }
                    }, System.Windows.Threading.DispatcherPriority.Background);
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
                    var provider = st.Provider;
                    var videoProvider = provider as VideoProvider;
                    // Win11 raised desktop（Progman 承载）下，所有窗口型 Provider（视频/静态图均经
                    // VideoProvider，网页等同样走 WebView2 窗口层）作为 WS_EX_LAYERED 子窗口挂到
                    // Progman 后，DWM 不会自动合成其内容到桌面，必须显式触发一次
                    // 「摘出→置顶→归位→图标层重绘」强制合成才能显示
                    // （表现：Attach 成功、程序报已应用，但桌面仍是静态壁纸/黑屏）。
                    // 经典 WorkerW 路径（非 raised）下 Lively 同款结构由 DWM 自动合成，仅旧
                    // WPF MediaElement（NeedsForcedComposition=true）需要后续强制合成。
                    bool raisedDesktop = WorkerWInjector.IsRaisedDesktop();
                    bool needsForce = raisedDesktop || (videoProvider != null && videoProvider.NeedsForcedComposition);
                    if (!needsForce) return;

                    var dispatcher = System.Windows.Application.Current.Dispatcher;
                    var deadline = DateTime.UtcNow.AddSeconds(12);
                    // raised desktop 且 Provider 非 VideoProvider（如网页层）时无 HasVideoContent 就绪
                    // 信号，固定等待 3s 让 WebView2 渲染出首帧再强制合成。
                    var fixedWaitUntil = DateTime.UtcNow.AddSeconds(raisedDesktop && videoProvider == null ? 3 : 0);
                    while (DateTime.UtcNow < deadline)
                    {
                        bool ready = false;
                        if (videoProvider != null && st.Provider == videoProvider)
                            ready = await dispatcher.InvokeAsync(() => videoProvider.HasVideoContent());
                        if (ready) break;
                        if (raisedDesktop && videoProvider == null && DateTime.UtcNow >= fixedWaitUntil) break;
                        await Task.Delay(500);
                    }
                    await dispatcher.InvokeAsync(() =>
                    {
                        // 期间若已切换/清理壁纸（Provider 不再是同一个），跳过合成避免误操作
                        if (st.Provider != provider) return;
                        var hwnd = st.Provider?.Handle ?? IntPtr.Zero;
                        if (hwnd != IntPtr.Zero && st.WorkerW != IntPtr.Zero)
                        {
                            WorkerWInjector.ForceDwmComposition(hwnd, st.WorkerW, st.Bounds);
                        }
                    });
                    // 【注意】此前这里在 raised desktop 下会再等 1500ms 补一次 ForceDwmComposition。
                    // 该二次合成会再跑一次 SetParent/SetWindowPos，造成切换后第二次重排/闪烁，
                    // 属老版本闪屏回归的一部分，已移除。非分层子窗口挂到背景 WorkerW 后 DWM 常规
                    // 合成、首帧本就可见，无需二次补救。
                });

                // 叠化过渡：统一走 CrossfadeAsync（动态→动态：新旧窗口 alpha 叠化；
                // 静态→动态：旧状态是 WebView2 静态层，同样支持窗口级 alpha 叠化，
                // 静态层淡出 + 视频层淡入，无系统壁纸层残留问题——静态图已由窗口层承载，
                // 系统壁纸层保持原壁纸，不再参与切换）。
                bool switched = await CrossfadeAsync(st, oldProvider, childHwnd, opToken);
                if (!switched)
                {
                    // 新壁纸未就绪（网络加载挂起、或等待被新操作打断）：CrossfadeAsync 已异步销毁新窗口，
                    // 这里回滚状态字段，旧壁纸继续显示，不写配置。
                    st.Provider = oldProvider;
                    st.IsStaticImage = oldIsStaticImage;
                    st.LastPath = oldPath;
                    st.LastType = oldType;
                    st.WorkerW = oldWorkerW;
                    status?.Invoke(opToken.IsCancellationRequested
                        ? "已取消切换（新壁纸加载被打断）"
                        : "切换失败：壁纸加载未就绪（已保持原壁纸）");
                    return;
                }

                if (save) PersistAssignments();
                status?.Invoke("已应用：" + Path.GetFileName(path));
            }
            finally
            {
                SwitchOverlay.End(screenIndex);
                System.Threading.Interlocked.Decrement(ref _opActive);
                _screenOpLock.Release();
                RaiseStateChanged();
            }
        }

        /// <summary>新壁纸淡入 + 旧壁纸淡出（300ms 窗口级 alpha 叠化）。叠化结束后销毁旧 Provider。
        /// 返回 true 表示新壁纸已成功显示；false 表示未就绪已回滚（旧壁纸保持显示，新窗口已销毁），
        /// 调用方应恢复旧壁纸状态字段。等待就绪为无限等待（网络加载慢时一直等），但可由 opToken
        /// 打断——用户切换/解除/退出时新操作先取消本令牌，本方法立即回滚释放锁，绝不阻塞新操作。</summary>
        private async Task<bool> CrossfadeAsync(ScreenState st, IWallpaperProvider? oldProvider, IntPtr newHwnd, CancellationToken opToken)
        {
            // 新窗口先置透明，避免挂载瞬间闪出
            try { Win32.SetLayeredWindowAttributes(newHwnd, 0, 0, Win32.LWA_ALPHA); } catch { }

            // 等新壁纸内容真正就绪（视频首帧解码 / WebView2 初始化并注入 hls 页）：
            // 统一走 IWallpaperProvider.WaitReadyAsync，覆盖 VideoProvider 与 WebProvider，
            // 就绪前新窗口保持透明（露出旧壁纸，视觉无变化）；就绪后再叠化，
            // 避免"叠化期间新窗口空白、被直接显示成白屏/黑屏"的闪动。
            // 无限等待 + opToken 取消：网络壁纸加载慢时一直等（不自动回退），
            // 用户点其他壁纸/解除/退出时新操作取消 opToken → 立即回滚释放锁。
            var provider = st.Provider;
            if (provider != null)
            {
                try { await provider.WaitReadyAsync(Timeout.InfiniteTimeSpan, opToken); }
                catch (OperationCanceledException)
                {
                    // 被新操作打断（用户切换/解除/退出）：销毁未就绪的新窗口（异步，不阻塞），
                    // 返回 false 让调用方回滚状态字段并释放锁，新操作随即拿到锁继续执行。
                    Logger.Log("[Crossfade] 等待壁纸就绪被新操作打断，取消本次切换");
                    var newProvider = provider;
                    System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        try { newProvider?.Dispose(); }
                        catch (Exception ex) { Logger.Log($"[WallpaperManager] 被打断新壁纸 Dispose 异常: {ex.Message}"); }
                    }, System.Windows.Threading.DispatcherPriority.Background);
                    return false;
                }
                catch (Exception ex) { Logger.Log($"[Crossfade] 等待就绪异常（继续显示）: {ex.Message}"); }
            }
            if (st.Provider == null || st.Provider != provider)
            {
                // 等待期间壁纸状态被清除/重建（显示器拓扑变化、快速连切）：
                // 此时的 st.Bounds 可能已是【新拓扑的尺寸】，而新窗口是按【旧 Bounds】创建的——
                // 若继续落位，旧尺寸窗口会摆进新桌面 → 表现为"壁纸偏移到桌面左上角、
                // 切换别的壁纸再切回来又正常"（日志/截图实证）。因此这里必须中止：
                // 销毁孤儿化的新窗口，返回 false 让调用方回滚。
                Logger.Log("[Crossfade] 等待期间壁纸状态已重建/清除，中止本次切换（防旧尺寸窗口落位）");
                var orphaned = provider;
                System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    try { orphaned?.Dispose(); }
                    catch (Exception ex) { Logger.Log($"[WallpaperManager] 孤儿新壁纸 Dispose 异常: {ex.Message}"); }
                }, System.Windows.Threading.DispatcherPriority.Background);
                return false;
            }

            // 就绪检查：内容真正可显示才叠化。未就绪（网络壁纸加载挂起、签名过期、网络不可达等）
            // 时回滚——销毁未就绪的新窗口（异步，不阻塞），旧壁纸继续显示，锁立即释放，
            // 后续切换/解除/退出不再被卡住。就绪的判定必须回 UI 线程（HasVideoContent 有 WPF 亲和）。
            bool ready = false;
            try
            {
                ready = await System.Windows.Application.Current.Dispatcher.InvokeAsync(() => IsContentReady(provider));
            }
            catch (Exception ex) { Logger.Log($"[Crossfade] 就绪检查异常: {ex.Message}"); }

            if (!ready)
            {
                var newProvider = provider;
                Logger.Log("[Crossfade] 新壁纸未就绪，回滚保持旧壁纸显示");
                System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    try { newProvider?.Dispose(); }
                    catch (Exception ex) { Logger.Log($"[WallpaperManager] 未就绪新壁纸 Dispose 异常: {ex.Message}"); }
                }, System.Windows.Threading.DispatcherPriority.Background);
                return false;
            }

            // 【内容已就绪 + 状态未变】把此前挂在父客户区之外的新窗口移入正确位置显示。
            // 此刻旧壁纸仍完好可见 → 新旧瞬间交接，不会闪出空白帧/底层系统壁纸。
            // 这是去掉 WS_EX_LAYERED 之后，老版本“就绪前保持透明（露出旧壁纸）”的等价实现：
            // 原实现靠 SetLayeredWindowAttributes(alpha=0) 隐藏，非分层窗口上该调用无效，
            // 因此必须在就绪这一刻显式移入，否则窗口会一直留在屏外（壁纸不显示）。
            // 就绪检查的 await 期间状态仍可能被重建（拓扑变化/快速连切），移入前最后校验一次：
            if (st.Provider != provider)
            {
                Logger.Log("[Crossfade] 就绪检查期间壁纸状态已重建，中止移入（防旧尺寸窗口落位）");
                var orphaned2 = provider;
                System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    try { orphaned2?.Dispose(); }
                    catch (Exception ex) { Logger.Log($"[WallpaperManager] 孤儿新壁纸 Dispose 异常: {ex.Message}"); }
                }, System.Windows.Threading.DispatcherPriority.Background);
                return false;
            }
            WorkerWInjector.ShowWallpaperWindow(newHwnd, st.WorkerW, st.Bounds);
            // r17：移入后持续复核（同静态层），自愈"图标被覆盖/右键失效/壁纸偏移"。
            SchedulePlacementGuard(st, provider, "动态层");

            // 旧 Provider 的 Handle 必须在 UI 线程获取（WPF 窗口/WindowInteropHelper 有线程亲和性），
            // 后台渐变线程直接访问 oldProvider.Handle 会抛“调用线程无法访问此对象”。
            IntPtr oldHwnd = IntPtr.Zero;
            if (oldProvider != null)
            {
                try
                {
                    oldHwnd = await System.Windows.Application.Current.Dispatcher.InvokeAsync(() => oldProvider.Handle);
                }
                catch (Exception ex)
                {
                    Logger.Log($"[Crossfade] 获取旧壁纸句柄异常: {ex.Message}");
                }
            }

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
                        if (oldHwnd != IntPtr.Zero)
                            Win32.SetLayeredWindowAttributes(oldHwnd, 0, (byte)oldAlpha, Win32.LWA_ALPHA);
                        await Task.Delay(FadeStepMs);
                    }
                }
                finally
                {
                    // 叠化结束（正常完成或中途被中断），销毁旧 Provider，避免窗口泄漏。
                    // 旧 Provider 销毁必须在 UI 线程（WPF RenderWindow / WebView2 Controller
                    // 归属创建线程），但绝不能 await 等待完成：WebView2 Controller.Close()
                    // （尤其网络壁纸还在加载/拉流时）会同步阻塞数百毫秒到数秒，
                    // 一旦 await，切换流程（持有 _screenOpLock）与后续任何操作都会被拖住，
                    // 表现为"切换后僵持、托盘强退无效"。改为 fire-and-forget 低优先级调度，
                    // 切换立即返回，销毁在后台悄悄完成。
                    if (oldProvider != null)
                    {
                        System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                        {
                            try { oldProvider.Dispose(); }
                            catch (Exception ex) { Logger.Log($"[WallpaperManager] 旧壁纸 Dispose 异常: {ex.Message}"); }
                        }, System.Windows.Threading.DispatcherPriority.Background);
                    }
                }
            });
            return true;
        }

        /// <summary>判断 Provider 内容是否已就绪（在 UI 线程调用）。</summary>
        private bool IsContentReady(IWallpaperProvider? provider)
        {
            switch (provider)
            {
                case VideoProvider vp: return vp.HasVideoContent();
                case WebProvider wp: return wp.IsContentReady;
                default: return provider != null;
            }
        }

        /// <summary>清空某一屏的壁纸（恢复为系统静态壁纸）。</summary>
        public async Task ClearScreenAsync(int screenIndex)
        {
            System.Threading.Interlocked.Increment(ref _opActive);
            var opToken = await BeginScreenOperationAsync();
            try
            {
                if (opToken.IsCancellationRequested) return; // 排队期间已被更新的操作打断
                if (_states.TryGetValue(screenIndex, out var st))
                    await CleanupScreenAsync(st, restoreWallpaper: true);
                PersistAfterClear();
            }
            finally
            {
                System.Threading.Interlocked.Decrement(ref _opActive);
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
            System.Threading.Interlocked.Increment(ref _opActive);
            var opToken = await BeginScreenOperationAsync();
            try
            {
                if (opToken.IsCancellationRequested) return; // 排队期间已被更新的操作打断
                var tasks = _states.Values.Select(st => CleanupScreenAsync(st, restoreWallpaper)).ToArray();
                await Task.WhenAll(tasks);
                if (persistState) PersistAfterClear();
            }
            finally
            {
                System.Threading.Interlocked.Decrement(ref _opActive);
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

            // 1. 在 UI 线程摘除 Provider 引用；Dispose 改为低优先级异步执行——
            // WebView2 Controller.Close()（尤其 m3u8 在线流）会同步阻塞数百毫秒到数秒，
            // 若在此 await，解除壁纸/切换壁纸都会被拖住；先摘引用让界面立即响应，
            // 销毁动作在 UI 空闲时（ApplicationIdle）再执行，不抢占用户操作。
            IntPtr ownHwnd = IntPtr.Zero;
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                var provider = st.Provider;
                ownHwnd = provider?.Handle ?? IntPtr.Zero;
                st.Provider = null;
                st.LastPath = "";
                st.IsStaticImage = false;
                if (provider != null)
                {
                    System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        try { provider.Dispose(); }
                        catch (Exception ex) { Logger.Log($"[WallpaperManager] Provider.Dispose 异常: {ex.Message}"); }
                    }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                }
            });

            // 复用静态层可能并非当前 st.Provider（动态屏时隐藏保活），上面未销毁则在此销毁
            if (reusableStatic != null && !layerIsActiveProvider)
            {
                await System.Windows.Application.Current.Dispatcher.InvokeAsync(() => reusableStatic.Dispose());
            }

            // 记录承载层并复位状态后，在 UI 线程空闲时兜底摘除本屏残留子窗口（Provider.Dispose
            // 通常已把窗口正常关掉，这里对已不存在的句柄是 no-op；仅清理销毁失败留下的孤儿窗口）。
            // 多屏扩展下所有屏共享同一个缓存的 Progman 承载层，绝不能后台线程 DetachChildren
            // 整个承载层——那会把其他屏幕仍在播放的壁纸窗口一起拆掉，WebView2 Dispose 随之挂死 UI。

            // 2. 在后台线程执行 Win32 清理与系统壁纸恢复（避免 SendMessageTimeout / SPI 阻塞 UI）
            await Task.Run(() =>
            {
                // 仅记录承载层并复位状态，不再批量拆子窗口；本屏子窗口由下方 UI 线程兜底摘除。
                // 注意：【不要销毁系统 WorkerW 本身】——那样会迫使 Windows 重建桌面，
                // 出现“黑屏闪一下再恢复”的现象。我们只是把自己的渲染窗口撤走，
                // 原本压在注入层之下的系统静态壁纸会自然透出来，无需任何刷新。
                st.WorkerW = IntPtr.Zero;

                // 解除壁纸【不做图标层 SW_HIDE→SW_SHOW 刷新】——那是整个桌面（图标+壁纸）
                // 闪一下的直接原因（用户可见的"解除时闪动"）。非分层壁纸窗口撤走后，DWM
                // 常规合成自然透出下层内容；下方 RestoreSystemWallpaper(forceRepaint) 会
                // 通过 SPI/IDesktopWallpaper 触发系统级重绘，无需（也不能）再闪图标层。
                // if (restoreWallpaper) WorkerWInjector.RefreshDesktop();

                // 解除/退出时必须把系统原壁纸再设一遍，强制桌面重绘：
                // 即使注册表里的 Wallpaper 值看起来已经是原壁纸，DWM 仍可能因为窗口层残留
                // 而保持黑屏；IDesktopWallpaper/SPI 重新设置会触发系统刷新，确保原壁纸透出。
                // 切换同屏壁纸时 restoreWallpaper=false，不会走到这里，不会导致闪屏。
                if (restoreWallpaper && (wasStaticImage || _states.Values.All(s => s.Provider == null && !s.IsStaticImage)))
                    RestoreSystemWallpaper(forceRepaint: true);
            });

            // 3. UI 线程空闲时兜底摘除本屏残留子窗口（Provider.Dispose 通常已把窗口正常关掉，
            // 这里对已不存在的句柄是 no-op；仅清理跨线程销毁失败留下的孤儿窗口）。
            if (ownHwnd != IntPtr.Zero)
            {
                await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    try { WorkerWInjector.DetachChildWindow(ownHwnd); }
                    catch (Exception ex) { Logger.Log($"[WallpaperManager] 摘除子窗口异常: {ex.Message}"); }
                }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            }
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
                // 按设置里的适应方式映射系统壁纸位置（Fill/Fit/Center），保证旋转后的壁纸
                // 在系统 API 降级路径下也遵循该适应方式，而非系统默认 Fill。
                int position = FitToDesktopPosition(_config.WallpaperFit);
                Win32.SetDesktopWallpaper(path, position);
                Logger.Log($"[WallpaperManager] 已设置静态壁纸（适应方式={_config.WallpaperFit}→position={position}）: {path}");
            }
            catch (Exception ex)
            {
                Logger.Log($"[WallpaperManager] 设置静态壁纸失败: {ex.Message}");
                // 新 API 异常时回退到 SPI：先写注册表 WallpaperStyle/TileWallpaper 再触发系统重绘
                try
                {
                    var fit = string.IsNullOrWhiteSpace(_config.WallpaperFit) ? "fill" : _config.WallpaperFit.Trim().ToLowerInvariant();
                    var (style, tile) = fit switch
                    {
                        "center" => ("0", "0"),
                        "fit" => ("6", "0"),
                        _ => ("10", "0")
                    };
                    Registry.SetValue(@"HKEY_CURRENT_USER\Control Panel\Desktop", "WallpaperStyle", style);
                    Registry.SetValue(@"HKEY_CURRENT_USER\Control Panel\Desktop", "TileWallpaper", tile);
                    Win32.SystemParametersInfo(Win32.SPI_SETDESKWALLPAPER, 0, path, Win32.SPIF_UPDATEINIFILE | Win32.SPIF_SENDCHANGE);
                    Logger.Log($"[WallpaperManager] 已通过 SPI 回退设置静态壁纸（适应方式={fit}）: {path}");
                }
                catch (Exception ex2)
                {
                    Logger.Log($"[WallpaperManager] SPI 回退设置静态壁纸也失败: {ex2.Message}");
                }
            }
        }

        /// <summary>把 App 适应方式映射为 IDesktopWallpaper 位置枚举：center→0 / fit→3 / fill(或未知)→4。</summary>
        private static int FitToDesktopPosition(string? fit)
        {
            return (string.IsNullOrWhiteSpace(fit) ? "fill" : fit.Trim().ToLowerInvariant()) switch
            {
                "center" => 0,
                "fit" => 3,
                _ => 4
            };
        }

        /// <summary>把系统桌面恢复为程序启动前的静态壁纸。</summary>
        /// <param name="forceRepaint">为 true 时不再因注册表已是原壁纸而跳过，强制重新设置以触发桌面重绘。</param>
        private void RestoreSystemWallpaper(bool forceRepaint = false)
        {
            try
            {
                // 读取"当前桌面此刻的系统壁纸"（每块虚拟桌面各有自己的系统壁纸）。
                // 关键：分桌面场景下绝不能用启动时记录的单一 _originalWallpaper 做重绘目标——
                // 那会把别的桌面的壁纸强写到本桌面（"桌面2解除后变成桌面1壁纸"的根因）。
                // 读当前值 = 只触发一次重绘、不改变壁纸语义，各桌面保持各自的系统壁纸。
                string liveCurrent = "";
                try
                {
                    liveCurrent = Registry.GetValue(@"HKEY_CURRENT_USER\Control Panel\Desktop", "Wallpaper", "") as string ?? "";
                }
                catch { liveCurrent = ""; }

                // 记录值缺失时才回退到启动记录（首次运行/注册表读取失败）
                if (string.IsNullOrEmpty(_originalWallpaper) || !File.Exists(_originalWallpaper))
                    ReadOriginalWallpaper();

                string target = (!string.IsNullOrEmpty(liveCurrent) && File.Exists(liveCurrent))
                    ? liveCurrent
                    : _originalWallpaper;

                if (!string.IsNullOrEmpty(target) && File.Exists(target))
                {
                    // 非强制时：当前已是该壁纸则无需重设，避免重复设置触发系统异步重绘/残留闪烁。
                    if (!forceRepaint && string.Equals(liveCurrent, target, StringComparison.OrdinalIgnoreCase))
                    {
                        Logger.Log("[WallpaperManager] 系统壁纸已是当前壁纸，跳过恢复");
                        return;
                    }

                    Win32.SetDesktopWallpaper(target);
                    Logger.Log($"[WallpaperManager] 已重绘系统壁纸(force={forceRepaint}): {target}");
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

        /// <summary>移入显示后的持续复核（r17）：+300ms / +1200ms / +3000ms 三档复核壁纸窗口的
        /// 挂载关系（父窗口归属 / 分层样式 / Z 序沉在图标层之下 / 落位矩形）与渲染比例
        /// （页面视口×devicePixelRatio = 宿主客户区像素），发现问题立即自愈并记日志。
        /// 挂载关系是切换时一次性设置的，之后会被系统异步副作用破坏（SetParent 回挂把子窗口
        /// 顶到 Z 序顶部、explorer 重建 SHELLDLL_DefView、桌面重绘、另一份副本互抢承载层），
        /// 破坏后的现象正是用户报的"桌面图标被覆盖、右键不能用、壁纸偏移只占左上角一块"。
        /// 身份守卫：期间若已切到别的壁纸/状态被重建，本次复核立即作废——否则会把已停放在屏外
        /// 的旧窗口重新落位，闪出旧壁纸。幂等、后台执行、绝不影响切换时序。</summary>
        private static void SchedulePlacementGuard(ScreenState stRef, IWallpaperProvider provider, string tag)
        {
            var child = provider.Handle;
            if (child == IntPtr.Zero) return;
            _ = Task.Run(async () =>
            {
                int waited = 0;
                foreach (int gap in new[] { 300, 900, 1800 })
                {
                    try { await Task.Delay(gap); } catch { }
                    waited += gap;
                    try
                    {
                        if (!ReferenceEquals(stRef.Provider, provider)) return;
                        if (!Win32.IsWindow(child)) return;
                        WorkerWInjector.EnsurePlacement(child, stRef.WorkerW, stRef.Bounds, $"{tag}+{waited}ms");
                        if (provider is VideoProvider vp)
                            await vp.VerifyRenderScaleAsync($"{tag}+{waited}ms");
                    }
                    catch { /* 复核绝不影响壁纸显示 */ }
                }
            });
        }

        /// <summary>把当前各屏壁纸分配持久化：全局 Assignments 始终更新（关闭按桌面记忆时回退使用）；
        /// 开启按桌面记忆时，额外把当前桌面（_currentDesktop）自己的分配写入 DesktopAssignments 对应键，
        /// 使得切换虚拟桌面后各屏壁纸能按桌面独立恢复。</summary>
        private void PersistAssignments()
        {
            var list = _states.Values
                .Where(s => (s.Provider != null || s.IsStaticImage) && !string.IsNullOrEmpty(s.LastPath))
                .Select(s => new ScreenAssignment { Index = s.Index, Path = s.LastPath, Type = s.LastType })
                .ToList();
            _config.Assignments = list;
            if (_config.PerDesktopEnabled && _vd != null && _currentDesktop != Guid.Empty && list.Count > 0)
                _config.DesktopAssignments[_currentDesktop.ToString()] = list;
            _config.Save();
        }

        /// <summary>清除操作（解除某屏/解除全部/退出前停止）后的持久化：
        /// 先更新全局分配；若清完所有屏幕后已无任何分配，则**连带清空所有虚拟桌面的分配桶**——
        /// 否则切回之前设过壁纸的桌面会"自己又冒出来"（用户反馈：桌面2解除后回桌面1还有壁纸，
        /// 必须再解除一遍）。用户预期"解除"就是彻底解除。仅当所有屏都空时才清，单屏解除不影响其他桌面。</summary>
        private void PersistAfterClear()
        {
            PersistAssignments();
            if (_config.Assignments == null || _config.Assignments.Count == 0)
            {
                if (_config.DesktopAssignments.Count > 0)
                {
                    _config.DesktopAssignments.Clear();
                    _config.Save();
                    Logger.Log("[WallpaperManager] 已清空所有虚拟桌面的壁纸分配（彻底解除）");
                }
            }
        }

        /// <summary>虚拟桌面切换事件入口（在 VDM 轮询线程上触发）：整体调度到 UI 线程，
        /// 与所有 Provider/WebView2 操作同一执行环境，并避免与看门狗并发写配置字典。</summary>
        private void OnVirtualDesktopChanged(Guid newId)
        {
            var disp = System.Windows.Application.Current?.Dispatcher;
            if (disp != null && !disp.CheckAccess())
                disp.BeginInvoke(() => OnVirtualDesktopChangedUi(newId));
            else
                OnVirtualDesktopChangedUi(newId);
        }

        /// <summary>切换到新虚拟桌面：先记住"上一个桌面"的当前实时壁纸，再按新桌面的分配热替换各屏。
        /// 新桌面某屏无记录则保留当前屏内容（继承），避免未自定义桌面出现空白/黑屏。</summary>
        private void OnVirtualDesktopChangedUi(Guid newId)
        {
            if (!_config.PerDesktopEnabled || _vd == null) return;

            // 0. 切换前先失效承载层缓存：Win11 各虚拟桌面共享同一 Progman（缓存可复用），但 Win10 的
            // 各虚拟桌面可能是各自独立的 Progman/WorkerW，复用旧句柄会把壁纸挂到已不在前台的桌面上
            // （"Win10 分桌面不生效"的根因）。失效后由下一次 AcquireWorkerW 重新定位当前桌面的承载层。
            try { WorkerWInjector.InvalidateCache(); } catch { }

            // 1. 离开上一桌面：把当前实时状态快照进它的分配桶（离开即记住）
            if (_currentDesktop != Guid.Empty)
            {
                var prevList = _states.Values
                    .Where(s => (s.Provider != null || s.IsStaticImage) && !string.IsNullOrEmpty(s.LastPath))
                    .Select(s => new ScreenAssignment { Index = s.Index, Path = s.LastPath, Type = s.LastType })
                    .ToList();
                if (prevList.Count > 0) _config.DesktopAssignments[_currentDesktop.ToString()] = prevList;
                else _config.DesktopAssignments.Remove(_currentDesktop.ToString());
            }
            _currentDesktop = newId;

            // 2. 加载新桌面的分配
            var key = newId.ToString();
            var bucket = _config.DesktopAssignments.TryGetValue(key, out var b) ? b : null;
            var plan = (bucket ?? new System.Collections.Generic.List<ScreenAssignment>())
                .Where(a => _states.ContainsKey(a.Index) && (IsRemoteUrl(a.Path) || File.Exists(a.Path)))
                .Select(a => (a.Path, a.Type, a.Index))
                .ToList();
            if (plan.Count > 0)
            {
                _ = ApplyRestorePlanAsync(plan); // save:false，不回写（新桌面分配已持久化）
            }
            else
            {
                // 目标桌面没有已保存壁纸：撤走本程序的覆盖层，露出该桌面自己的系统壁纸。
                // 若沿用旧的"保持当前屏幕内容"，会把上一个桌面的壁纸串到本桌面（用户反馈的串桌面根因）。
                Logger.Log($"[WallpaperManager] 虚拟桌面 {newId} 无已保存壁纸，撤走覆盖层显示系统壁纸");
                _ = Task.Run(async () =>
                {
                    try { await StopAsync(restoreWallpaper: true, persistState: false); }
                    catch (Exception ex) { Logger.Log($"[WallpaperManager] 撤走覆盖层失败: {ex.Message}"); }
                });
            }
            _config.Save();
        }

        /// <summary>设置项"按虚拟桌面分别记忆壁纸"开关切换时由设置窗口调用：
        /// 开启→记录当前桌面并把当前实时状态写入该桌面桶；关闭→把当前桌面桶并入全局 Assignments（回退使用）。</summary>
        public void ApplyPerDesktopSettings()
        {
            if (_vd == null) return;
            if (_config.PerDesktopEnabled)
            {
                _currentDesktop = _vd.CurrentDesktopId;
                if (_currentDesktop != Guid.Empty) PersistAssignments();
            }
            else
            {
                if (_currentDesktop != Guid.Empty &&
                    _config.DesktopAssignments.TryGetValue(_currentDesktop.ToString(), out var b) && b.Count > 0)
                {
                    _config.Assignments = b;
                    _config.Save();
                }
            }
        }

        private void WatchdogTick(object? sender, EventArgs e)
        {
            // 定时器在线程池线程上触发，而 Provider/WebView2 是 UI 线程对象：整体调度回 UI 线程
            var disp = System.Windows.Application.Current?.Dispatcher;
            if (disp != null && !disp.CheckAccess())
            {
                disp.BeginInvoke(() => WatchdogTick(sender, e));
                return;
            }

            // 锁屏/登录界面期间不重建或重挂壁纸（外壳尚未就绪，过早操作会藏起图标层、点击穿透失效）
            if (IsLockScreenActive())
            {
                Logger.Log("[Watchdog] 锁屏期间跳过拓扑检查");
                return;
            }

            // 显示器热插拔 / 拓扑变化：数量变化，或数量不变但矩形/主屏/设备变化
            //（笔记本外接 HDMI 设主屏、复制↔扩展、改分辨率等都会命中后者）。
            string sig;
            try { sig = ScreensSignature(); } catch { return; }
            if (ScreenManager.Count != _states.Count || sig != _lastScreenSig)
            {
                Logger.Log($"[Watchdog] 显示器拓扑变化，重建屏幕表并恢复壁纸：{_lastScreenSig} → {sig}");
                // 拓扑变化后 explorer 会重建/隐藏桌面 WorkerW 结构，旧承载层句柄多半已失效或
                // 被隐藏（复用会导致壁纸不显示、坐标错位）。无条件失效缓存，强制重新探测。
                try { WorkerWInjector.InvalidateCache(); } catch { }
                // 重建前先销毁旧 Provider/静态层窗口，避免泄漏（旧窗口挂在 Progman 上
                // 会以旧 bounds 继续解码播放，遮挡/干扰新窗口）
                foreach (var st in _states.Values)
                {
                    try { st.Provider?.Dispose(); } catch { }
                    st.Provider = null;
                    st.IsStaticImage = false;
                }
                lock (_staticLock)
                {
                    foreach (var sp in _reusableStatic.Values)
                    {
                        try { sp.Dispose(); } catch { }
                    }
                    _reusableStatic.Clear();
                }
                var saved = _states.Values
                    .Where(s => s.Provider != null || s.IsStaticImage || !string.IsNullOrEmpty(s.LastPath))
                    .ToDictionary(s => s.Index, s => (s.LastPath, s.LastType));
                BuildScreens();
                // “应用到所有屏幕”意图（DefaultScreen=-1）：拓扑变化（接/拔 HDMI、扩展↔复制、
                // 仅主屏/仅副屏）后，任何“当前已连接但尚无本屏分配”的屏幕也要用任一已保存壁纸补齐，
                // 否则新接入的副屏会一直空白（仅按旧索引恢复时漏掉它）。
                bool applyAll = _config.DefaultScreen == -1;
                // 恢复计划先收集、再逐屏顺序执行（同 Start() 恢复：并发发起会互相取消，
                // 只有最后一屏能活——系统里调换屏幕位置后主屏壁纸"自己解除"的根因）。
                var restorePlan = new List<(string Path, WallpaperType Type, int Index)>();
                foreach (var st in _states.Values)
                {
                    if (saved.TryGetValue(st.Index, out var a) &&
                        (IsRemoteUrl(a.LastPath ?? "") || File.Exists(a.LastPath)))
                    {
                        restorePlan.Add((a.LastPath!, a.LastType, st.Index));
                    }
                    else if (applyAll && saved.Count > 0)
                    {
                        var any = saved.Values.First();
                        if (IsRemoteUrl(any.LastPath ?? "") || File.Exists(any.LastPath))
                            restorePlan.Add((any.LastPath!, any.LastType, st.Index));
                    }
                }
                if (restorePlan.Count > 0)
                    _ = ApplyRestorePlanAsync(restorePlan);
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
