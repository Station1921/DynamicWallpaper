using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
        }

        private readonly Config _config;
        private readonly Dictionary<int, ScreenState> _states = new();
        private readonly FullscreenMonitor _fs;
        private readonly PowerManager _power;
        private readonly System.Timers.Timer _watchdog = new(3000);
        private bool _userPaused;

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

        public IReadOnlyList<int> ActiveScreenIndices =>
            _states.Values.Where(s => s.Provider != null).Select(s => s.Index).ToList();

        public string? GetActivePath(int index) =>
            _states.TryGetValue(index, out var s) && s.Provider != null ? s.LastPath : null;

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

        /// <summary>将指定内容设为某屏壁纸。screenIndex 默认 0（主屏）。</summary>
        public async Task SetWallpaperAsync(string path, WallpaperType type, int screenIndex = 0, bool save = true)
        {
            if (!_states.TryGetValue(screenIndex, out var st)) return;

            // 1. 先清理旧壁纸（WorkerW 清理在后台线程，避免 UI 卡死）
            await CleanupScreenAsync(st, restoreWallpaper: false);

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
                provider.Show(path, st.Bounds);
                return provider.Handle; // EnsureHandle 必须在 UI 线程执行
            });

            if (childHwnd == IntPtr.Zero) return;

            // 3. 在后台线程获取 WorkerW（SendMessageTimeout 可能阻塞，不能放在 UI 线程）
            IntPtr workerW = await Task.Run(() => WorkerWInjector.AcquireWorkerW(st.Bounds));
            if (workerW == IntPtr.Zero)
            {
                // 拿不到 WorkerW 时清理已创建的窗口，避免残留
                await CleanupScreenAsync(st, restoreWallpaper: false);
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

            if (save) PersistAssignments();
        }

        /// <summary>清空某一屏的壁纸（恢复为系统静态壁纸）。</summary>
        public async Task ClearScreenAsync(int screenIndex)
        {
            if (_states.TryGetValue(screenIndex, out var st))
                await CleanupScreenAsync(st, restoreWallpaper: true);
            PersistAssignments();
        }

        public async Task StopAsync(bool restoreWallpaper = true)
        {
            var tasks = _states.Values.Select(st => CleanupScreenAsync(st, restoreWallpaper)).ToArray();
            await Task.WhenAll(tasks);
            PersistAssignments();
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
                || (_config.PauseOnFullscreen && _fs.IsFullscreen)
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
            // 1. 在 UI 线程释放 WPF Provider（关闭渲染窗口）
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (st.Provider != null)
                {
                    st.Provider.Dispose();
                    st.Provider = null;
                }
                st.LastPath = "";
            });

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

                // 只有真正清空最后一屏壁纸时才恢复系统静态壁纸；
                // 切换同屏壁纸时不需要恢复，避免黑屏闪烁。
                if (restoreWallpaper && _states.Values.All(s => s.Provider == null))
                    RestoreSystemWallpaper();
            });
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
                    Win32.SystemParametersInfo(Win32.SPI_SETDESKWALLPAPER, 0, _originalWallpaper, Win32.SPIF_UPDATEINIFILE | Win32.SPIF_SENDCHANGE);
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
                .Where(s => s.Provider != null && !string.IsNullOrEmpty(s.LastPath))
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
                    .Where(s => s.Provider != null)
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

            // 资源管理器重启导致 WorkerW 销毁时，按屏重建
            foreach (var st in _states.Values)
            {
                if (st.Provider == null || string.IsNullOrEmpty(st.LastPath)) continue;
                if (!WorkerWInjector.IsValid(st.WorkerW))
                    _ = SetWallpaperAsync(st.LastPath, st.LastType, st.Index, save: false);
            }
        }
    }
}
