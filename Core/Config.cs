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

        /// <summary>是否启用周期性自动清理过期缓存（缩略图/悬停预览）。默认关闭，由用户在设置中开启。</summary>
        public bool AutoCleanCache { get; set; } = false;

        /// <summary>自动清理的保留天数：超过该天数的缓存文件会被删除。默认 30 天。</summary>
        public int CacheRetentionDays { get; set; } = 30;

        public List<string> Library { get; set; } = new();

        /// <summary>每屏壁纸分配（持久化，重启后自动恢复）。</summary>
        public List<ScreenAssignment> Assignments { get; set; } = new();

        /// <summary>“设为”按钮默认应用到的目标屏：0=主屏，1..n=对应屏幕，-1=所有屏幕。</summary>
        public int DefaultScreen { get; set; } = 0;

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
        private const string AppName = "DynamicWallpaper";
    }

    /// <summary>单屏壁纸分配记录（可序列化）。</summary>
    public class ScreenAssignment
    {
        public int Index { get; set; }
        public string Path { get; set; } = "";
        public WallpaperType Type { get; set; }
    }
}
