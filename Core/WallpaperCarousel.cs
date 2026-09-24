using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DynamicWallpaper.Models;
using DynamicWallpaper.Providers;

namespace DynamicWallpaper.Core
{
    /// <summary>
    /// 壁纸轮播：按配置间隔，把「已设置壁纸的屏幕」自动切换到用户所选文件夹里的下一张图片/视频。
    /// 设计要点：
    ///  - 只轮播已有壁纸的屏幕（不会给空屏强加壁纸），每屏独立推进；
    ///  - 切换复用 WallpaperManager.SetWallpaperAsync（自带 300ms 叠化过渡 + 配置持久化）；
    ///  - 顺序模式：每屏按文件夹文件顺序循环；随机模式：每屏一个不重复抽签袋，抽空后重洗；
    ///  - 每个轮播周期实时重新扫描文件夹，新增/删除的图片会立即生效；
    ///  - 全屏/用户暂停/手动操作进行中时自动避让，不打断用户。
    /// </summary>
    public class WallpaperCarousel
    {
        /// <summary>参与轮播的一块屏幕：屏索引 + 当前显示的壁纸路径。</summary>
        public sealed class CarouselScreen
        {
            public int Index { get; init; }
            public string CurrentPath { get; init; } = "";
        }

        private readonly Config _config;
        private readonly Func<IReadOnlyList<CarouselScreen>> _getScreens;
        private readonly Func<string, WallpaperType, int, Task> _apply;
        private readonly Func<bool> _shouldSkip;

        private readonly System.Timers.Timer _timer;
        private readonly object _gate = new();
        private readonly Dictionary<int, int> _cursor = new();        // 顺序模式：每屏游标（未直接使用索引推进，保留以备扩展）
        private readonly Dictionary<int, List<string>> _bags = new(); // 随机模式：每屏不重复抽签袋
        private readonly Random _rng = new();
        private bool _rotating;

        public WallpaperCarousel(
            Config config,
            Func<IReadOnlyList<CarouselScreen>> getScreens,
            Func<string, WallpaperType, int, Task> apply,
            Func<bool> shouldSkip)
        {
            _config = config;
            _getScreens = getScreens;
            _apply = apply;
            _shouldSkip = shouldSkip;
            _timer = new System.Timers.Timer { AutoReset = true, Enabled = false };
            _timer.Elapsed += async (_, _) => await TickAsync();
        }

        /// <summary>按最新配置启动/停止定时器（程序启动与设置变更时调用）。
        /// 先 Stop 再 Start 确保即使定时器已在运行（仅改了间隔）也以“当前时刻 + 新间隔”重新计时，
        /// 避免 Interval 已在运行时修改却不重新计时的坑——否则“先设壁纸、后改间隔为 1 分钟”会依旧按旧间隔触发。</summary>
        public void ApplySettings()
        {
            lock (_gate)
            {
                _cursor.Clear();
                _bags.Clear();
                _timer.Stop();
                if (_config.CarouselEnabled && _config.CarouselIntervalMinutes > 0)
                {
                    double ms = TimeSpan.FromMinutes(Math.Min(_config.CarouselIntervalMinutes, 1440)).TotalMilliseconds;
                    _timer.Interval = ms;
                    _timer.Start();
                    Logger.Log($"[Carousel] 已启用：间隔 {_config.CarouselIntervalMinutes} 分钟（{ms}ms），顺序={_config.CarouselOrder}，文件夹数={_config.CarouselFolders.Count}");
                }
                else
                {
                    Logger.Log($"[Carousel] 已停用（CarouselEnabled={_config.CarouselEnabled}，CarouselIntervalMinutes={_config.CarouselIntervalMinutes}）");
                }
            }
        }

        private async Task TickAsync()
        {
            lock (_gate)
            {
                if (_rotating) return; // 上一轮尚未完成（网络壁纸加载慢等），本轮跳过
                _rotating = true;
            }
            Logger.Log($"[Carousel] >>> 定时器触发 Tick（userPaused={_shouldSkip()}）");
            try
            {
                // 全屏 / 用户暂停 时避让：不打扰游戏，也不做无谓切换
                if (_shouldSkip())
                {
                    Logger.Log("[Carousel] 本轮跳过（用户暂停或全屏前台应用）");
                    return;
                }

                var screens = _getScreens();
                if (screens.Count == 0)
                {
                    Logger.Log("[Carousel] 本轮无已设置壁纸的屏幕，跳过");
                    return;
                }

                var library = SnapshotSources();
                if (library.Count == 0)
                {
                    Logger.Log("[Carousel] 轮播源为空（文件夹无图片/视频），跳过");
                    return;
                }

                Logger.Log($"[Carousel] 触发切换：屏幕数={screens.Count}，源数={library.Count}");

                foreach (var sc in screens)
                {
                    if (_shouldSkip()) return;
                    var next = PickNext(sc.Index, sc.CurrentPath, library);
                    if (next == null) continue;
                    try
                    {
                        Logger.Log($"[Carousel] 屏{sc.Index} 轮播切换 -> {Path.GetFileName(next)}");
                        await _apply(next, ProviderFactory.DetectType(next), sc.Index);
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"[Carousel] 屏{sc.Index} 切换失败: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[Carousel] 轮播异常: {ex.Message}");
            }
            finally
            {
                lock (_gate) { _rotating = false; }
            }
        }

        /// <summary>支持的轮播文件扩展名（图片 + GIF + 常见视频）。</summary>
        private static readonly string[] _supportedExts =
        {
            ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp",
            ".mp4", ".webm", ".mov", ".mkv", ".avi"
        };

        /// <summary>文件夹快照：扫描用户所选文件夹（不含子文件夹）里的图片/视频，并实时去重。
        /// 每个轮播周期重新扫描，因此文件夹中新增或删除的文件会立即生效。</summary>
        private List<string> SnapshotSources()
        {
            var result = new List<string>();
            foreach (var folder in _config.CarouselFolders)
            {
                if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) continue;
                try
                {
                    foreach (var file in Directory.EnumerateFiles(folder))
                    {
                        var ext = Path.GetExtension(file);
                        if (ext.Length > 0 && Array.Exists(_supportedExts,
                                e => string.Equals(e, ext, StringComparison.OrdinalIgnoreCase)))
                            result.Add(file);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"[Carousel] 扫描文件夹失败 {folder}: {ex.Message}");
                }
            }
            return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>为指定屏选出下一张壁纸（不与当前重复），无可用项返回 null。</summary>
        private string? PickNext(int screen, string current, List<string> library)
        {
            var candidates = library
                .Where(p => !string.Equals(p, current, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (candidates.Count == 0) return null;

            if (string.Equals(_config.CarouselOrder, "random", StringComparison.OrdinalIgnoreCase))
            {
                if (!_bags.TryGetValue(screen, out var bag) || bag.Count == 0)
                {
                    bag = candidates.OrderBy(_ => _rng.Next()).ToList();
                    _bags[screen] = bag;
                }
                // 抽签袋里的项可能已从库移除/成为当前壁纸：逐个弹出直到有效
                while (bag.Count > 0)
                {
                    var p = bag[^1];
                    bag.RemoveAt(bag.Count - 1);
                    if (candidates.Contains(p)) return p;
                }
                // 全部失效则重洗一次（candidates 非空，必然可得）
                bag = candidates.OrderBy(_ => _rng.Next()).ToList();
                _bags[screen] = bag;
                return bag[^1];
            }
            else
            {
                // 顺序模式：从「当前壁纸在库中的位置」的下一项开始，找第一个与当前不同的
                int start = library.FindIndex(p => string.Equals(p, current, StringComparison.OrdinalIgnoreCase));
                int idx = start < 0 ? 0 : start + 1;
                for (int i = 0; i < library.Count; i++)
                {
                    var p = library[(idx + i) % library.Count];
                    if (!string.Equals(p, current, StringComparison.OrdinalIgnoreCase)) return p;
                }
                return null;
            }
        }
    }
}
