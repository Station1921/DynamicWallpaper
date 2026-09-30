using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using DynamicWallpaper.Models;
using Microsoft.Win32;

namespace DynamicWallpaper.Core
{
    public class Config
    {
        public bool Mute { get; set; } = true;
        public bool PauseOnFullscreen { get; set; } = true;
        public bool PauseOnBattery { get; set; } = true;
        public bool PerformanceMode { get; set; } = false;
        public bool RunOnStartup { get; set; } = false;
        public bool CloseToTray { get; set; } = true;

        /// <summary>壁纸适应方式：fill=铺满裁剪 / fit=完整显示 / center=原始居中。默认 fill（保持旧版行为）。</summary>
        public string WallpaperFit { get; set; } = "fill";

        /// <summary>每壁纸旋转角度（按文件完整路径小写做键）：0/90/180/270。右键菜单“旋转”时累加写入，
        /// 仅在对应壁纸被设置为桌面壁纸时生效（作用于正在显示该壁纸的屏幕）。默认无旋转。</summary>
        public Dictionary<string, int> WallpaperRotations { get; set; } = new();

        /// <summary>是否启用周期性自动清理过期缓存（缩略图/悬停预览）。默认关闭，由用户在设置中开启。</summary>
        public bool AutoCleanCache { get; set; } = false;

        /// <summary>自动清理的保留天数：超过该天数的缓存文件会被删除。默认 30 天。</summary>
        public int CacheRetentionDays { get; set; } = 30;

        /// <summary>是否启用壁纸轮播（按间隔自动切换「已设置壁纸的屏幕」）。默认关闭。</summary>
        public bool CarouselEnabled { get; set; } = false;

        /// <summary>轮播切换间隔（分钟），1~1440。默认 15 分钟。</summary>
        public int CarouselIntervalMinutes { get; set; } = 15;

        /// <summary>轮播顺序：sequential=按库顺序循环，random=随机不重复。默认顺序。</summary>
        public string CarouselOrder { get; set; } = "sequential";

        /// <summary>轮播图片来源文件夹列表：用户自选，轮播时自动播放这些文件夹里的图片/视频。默认空（需在设置中添加）。</summary>
        public List<string> CarouselFolders { get; set; } = new();

        public List<string> Library { get; set; } = new();

        /// <summary>每屏壁纸分配（持久化，重启后自动恢复）。</summary>
        public List<ScreenAssignment> Assignments { get; set; } = new();

        /// <summary>“设为”按钮默认应用到的目标屏：0=主屏，1..n=对应屏幕，-1=所有屏幕。</summary>
        public int DefaultScreen { get; set; } = 0;

        /// <summary>是否按虚拟桌面分别记忆/切换壁纸（Win+Tab 新建桌面各桌面独立壁纸）。默认关闭。</summary>
        public bool PerDesktopEnabled { get; set; } = false;

        /// <summary>每虚拟桌面的壁纸分配：键为桌面 GUID 字符串，值为该桌面各屏的分配列表。
        /// 仅在 PerDesktopEnabled=true 时使用；关闭时回退到全局 Assignments。</summary>
        public Dictionary<string, List<ScreenAssignment>> DesktopAssignments { get; set; } = new();

        /// <summary>程序启动前系统原本的静态壁纸路径（备用：当注册表值被清空时使用）。</summary>
        public string OriginalWallpaper { get; set; } = "";

        // 配置文件生成在程序根目录（exe 所在目录），不写入系统用户目录（C 盘）。
        private static readonly string FilePath =
            Path.Combine(AppPaths.RootDirectory, "config.json");

        public static Config Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var json = File.ReadAllText(FilePath);
                    var cfg = JsonSerializer.Deserialize<Config>(json);
                    if (cfg != null) return cfg;
                }
            }
            catch { }
            return new Config();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
                ApplyStartup();
            }
            catch { }
        }

        private void ApplyStartup()
        {
            try
            {
                // 进程内状态缓存：RunOnStartup 未翻转时跳过注册表操作。此前每次 Save()（设壁纸/
                // 解除/重排库等都会触发）都开注册表键+打一条"已是最新"日志，纯属浪费与刷屏；
                // 注册表语义不变：开启→写入（含 --silent），关闭→删除（不残留垃圾项）。
                // 首次调用必执行（进程启动后兜底同步一次真实注册表状态，含旧键迁移）。
                sbyte state = RunOnStartup ? (sbyte)1 : (sbyte)0;
                if (_lastAppliedStartup == state) return;
                _lastAppliedStartup = state;

                // 迁移旧固定键：若 "DynamicWallpaper" 指向本 exe，挪到按目录区分的新键并删除旧键，
                // 避免与另一份副本（稳定版/测试版）共用固定键互相顶替。指向其他 exe 时不动（由对方副本自行迁移）。
                try
                {
                    using var legacyKey = Registry.CurrentUser.OpenSubKey(RunKey, true);
                    if (legacyKey != null)
                    {
                        var curExe = Environment.ProcessPath;
                        if (string.IsNullOrWhiteSpace(curExe)) curExe = Process.GetCurrentProcess().MainModule?.FileName;
                        var legacyVal = legacyKey.GetValue(LegacyAppName) as string;
                        if (curExe != null && legacyVal != null && legacyVal.StartsWith($"\"{curExe}\"", StringComparison.OrdinalIgnoreCase))
                        {
                            legacyKey.SetValue(AppName, legacyVal, RegistryValueKind.String);
                            legacyKey.DeleteValue(LegacyAppName, false);
                            Logger.Log($"[Config] 迁移旧自启项到按目录区分的键：{AppName}");
                        }
                    }
                }
                catch (Exception ex) { Logger.Log($"[Config] 旧自启项迁移失败（忽略）: {ex.Message}"); }

                using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
                if (key == null)
                {
                    Logger.Log("[Config] 开机自启：无法打开注册表 Run 键");
                    return;
                }

                // 单文件发布时 Environment.ProcessPath 是 exe 真实路径；用它作为首选，失败再回落 MainModule。
                var exe = Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(exe))
                    exe = Process.GetCurrentProcess().MainModule?.FileName;

                if (RunOnStartup)
                {
                    if (string.IsNullOrWhiteSpace(exe))
                    {
                        Logger.Log("[Config] 开机自启：无法获取当前 exe 路径，未写入注册表");
                        return;
                    }

                    // 开机自启项追加 --silent 参数：程序以静默方式启动（仅驻留托盘、不弹出主界面），
                    // 但仍会构造 MainWindow 以恢复已保存的每屏壁纸。
                    var expected = $"\"{exe}\" --silent";
                    var current = key.GetValue(AppName) as string;
                    if (current != expected)
                    {
                        key.SetValue(AppName, expected, RegistryValueKind.String);
                        Logger.Log($"[Config] 开机自启已写入：{expected}");
                    }
                    else
                    {
                        Logger.Log("[Config] 开机自启：注册表项已是最新");
                    }
                }
                else
                {
                    key.DeleteValue(AppName, false);
                    Logger.Log("[Config] 开机自启已关闭：注册表项已删除");
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[Config] 开机自启注册表操作失败：{ex.Message}");
            }
        }

        /// <summary>检查注册表中是否已存在当前 exe 的开机自启项。</summary>
        public bool IsStartupRegistered()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
                if (key == null) return false;
                var value = key.GetValue(AppName) as string;
                if (string.IsNullOrWhiteSpace(value)) return false;
                var exe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
                return value.StartsWith($"\"{exe}\"", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 确保注册表中的开机自启项已包含 --silent 参数（用于旧版本已开启自启、但当时未带该参数的用户迁移）。
        /// 仅同步注册表，不写 config.json。
        /// </summary>
        public void EnsureStartupRegistered()
        {
            if (RunOnStartup) ApplyStartup();
        }

        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string LegacyAppName = "DynamicWallpaper";
        /// <summary>ApplyStartup 进程内状态缓存：1=已按"开启"应用，0=已按"关闭"应用，null=尚未应用（首次必执行）。</summary>
        private static sbyte? _lastAppliedStartup;
        /// <summary>自启注册表项名：按 exe 所在目录做稳定哈希，使稳定版与测试版各占独立键互不顶替
        /// （原固定名 "DynamicWallpaper" 会让两份副本互相覆盖/删除对方的开机自启项）。</summary>
        private static string AppName
        {
            get
            {
                var exe = Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(exe)) exe = Process.GetCurrentProcess().MainModule?.FileName;
                if (string.IsNullOrWhiteSpace(exe)) return LegacyAppName;
                using var sha = System.Security.Cryptography.SHA256.Create();
                var dir = Path.GetDirectoryName(exe) ?? exe;
                var hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(dir));
                return "DynamicWallpaper_" + BitConverter.ToString(hash, 0, 4).Replace("-", "").ToLowerInvariant();
            }
        }
    }

    /// <summary>单屏壁纸分配记录（可序列化）。</summary>
    public class ScreenAssignment
    {
        public int Index { get; set; }
        public string Path { get; set; } = "";
        public WallpaperType Type { get; set; }
    }
}
