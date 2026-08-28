using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace DynamicWallpaper.Core
{
    /// <summary>
    /// 程序根目录统一解析。
    ///
    /// 背景：程序以单文件自包含方式发布（PublishSingleFile）时，运行时会把应用
    /// 解压到 C 盘临时目录：C:\Users\&lt;user&gt;\AppData\Local\Temp\.net\DynamicWallpaper\&lt;hash&gt;=\，
    /// 此时 AppContext.BaseDirectory 指向该临时解压目录，导致壁纸下载（Wallpapers）、
    /// config.json、app.log、hovercache 等全部落到 C 盘临时目录。
    ///
    /// 方案：优先使用 Environment.ProcessPath 所在目录作为程序根目录——单文件发布
    /// 场景下该值就是 exe 的真实路径（例如 publish\DynamicWallpaper.exe 所在目录），
    /// 与 AppContext.BaseDirectory 指向的临时解压目录不同。所有下载/配置/日志/缓存
    /// 路径统一基于 RootDirectory，从而固定在 exe 所在根目录。
    /// </summary>
    public static class AppPaths
    {
        /// <summary>程序根目录：exe 真实所在目录（不含尾随反斜杠）。</summary>
        public static string RootDirectory { get; } = ResolveRootDirectory();

        private static string ResolveRootDirectory()
        {
            try
            {
                var processPath = Environment.ProcessPath;
                if (!string.IsNullOrWhiteSpace(processPath))
                {
                    var dir = Path.GetDirectoryName(processPath);
                    if (!string.IsNullOrWhiteSpace(dir))
                        return dir;
                }
            }
            catch
            {
                // 解析失败时兜底到 AppContext.BaseDirectory
            }
            return AppContext.BaseDirectory;
        }

        /// <summary>会话级缓存：本次进程内找到的 ffmpeg 路径直接复用，避免重复扫描 PATH / 重复解压。</summary>
        private static string? _cachedFfmpeg;

        /// <summary>
        /// 获取可用的 ffmpeg 路径，优先级（共用优先，避免无谓释放文件）：
        ///   1) 程序目录已有 ffmpeg.exe → 编码器预检通过则直接使用，不释放；
        ///   2) 扫描 PATH 与本机常见安装位置（winget / ProgramFiles / C:\ffmpeg 等）→
        ///      找到即直接调用，不做任何释放（毫秒级纯文件检查）；
        ///   3) 全盘深度受限扫描（固定磁盘，最大深度 5）→ 找到直接调用，不释放；
        ///   4) 以上都没有 → 才从内嵌资源解压到程序目录兜底。
        /// 仅在需要转码时由调用方触发（平时浏览库 / 设壁纸完全不调用，零开销）。
        /// 外部候选与程序目录已有版本都会做编码器预检（要求含 libx264 + HEVC 解码 + x64），
        /// 过滤剪映精简版等缺编码器/32 位的版本，避免选中后转码失败再走兜底。
        /// </summary>
        public static string? EnsureFfmpeg()
        {
            if (_cachedFfmpeg != null) return _cachedFfmpeg;

            // 1) 程序目录已有 → 预检通过直接用
            string local = Path.Combine(RootDirectory, "ffmpeg.exe");
            if (File.Exists(local))
            {
                if (IsUsableFfmpeg(local))
                {
                    Logger.Log("[AppPaths] 使用程序目录 ffmpeg: " + local);
                    return _cachedFfmpeg = local;
                }
                Logger.Log("[AppPaths] 程序目录 ffmpeg 不可用（缺编码器或 32 位），继续查找…");
            }

            // 2) 扫描 PATH + 常见安装位置 → 找到预检通过则直接调用，不释放
            string? found = FindExternalFfmpeg();
            if (found != null)
            {
                Logger.Log("[AppPaths] 使用本机已有 ffmpeg: " + found);
                return _cachedFfmpeg = found;
            }

            // 3) 全盘深度受限扫描固定磁盘（兼容老版"先扫描电脑全盘"设计）
            Logger.Log("[AppPaths] PATH/常见位置未找到可用 ffmpeg，开始全盘快速扫描…");
            found = ScanAllFixedDrivesForFfmpeg();
            if (found != null)
            {
                Logger.Log("[AppPaths] 全盘扫描找到可用 ffmpeg: " + found);
                return _cachedFfmpeg = found;
            }

            // 4) 都没有 → 才解压内嵌资源兜底
            Logger.Log("[AppPaths] 全盘扫描未找到 ffmpeg，准备释放内嵌资源…");
            try
            {
                using var stream = System.Reflection.Assembly.GetExecutingAssembly()
                    .GetManifestResourceStream("ffmpeg.exe");
                if (stream == null)
                {
                    Logger.Log("[AppPaths] 内嵌 ffmpeg 资源缺失");
                    return null;
                }
                using var fs = new FileStream(local, FileMode.Create, FileAccess.Write);
                stream.CopyTo(fs);
                Logger.Log("[AppPaths] 已从内嵌资源解压 ffmpeg 到: " + local);
                return _cachedFfmpeg = local;
            }
            catch (Exception ex)
            {
                Logger.Log("[AppPaths] ffmpeg 解压失败: " + ex.Message);
                return null;
            }
        }

        /// <summary>强制从内嵌资源解压 ffmpeg 到程序目录（覆盖已有），并刷新会话缓存。
        /// 供「外部 ffmpeg 缺编码器导致转码失败」时回退调用；解压失败返回 null。</summary>
        public static string? ForceEmbeddedFfmpeg()
        {
            string local = Path.Combine(RootDirectory, "ffmpeg.exe");
            try
            {
                using var stream = System.Reflection.Assembly.GetExecutingAssembly()
                    .GetManifestResourceStream("ffmpeg.exe");
                if (stream == null)
                {
                    Logger.Log("[AppPaths] 内嵌 ffmpeg 资源缺失，无法兜底");
                    return null;
                }
                using var fs = new FileStream(local, FileMode.Create, FileAccess.Write);
                stream.CopyTo(fs);
                Logger.Log("[AppPaths] 已强制从内嵌资源解压 ffmpeg 兜底: " + local);
                return _cachedFfmpeg = local;
            }
            catch (Exception ex)
            {
                Logger.Log("[AppPaths] 强制解压 ffmpeg 失败: " + ex.Message);
                return null;
            }
        }

        /// <summary>扫描 PATH 与常见安装位置，返回第一个通过编码器预检的 ffmpeg.exe；无则返回 null。
        /// 纯本地文件检查（File.Exists），毫秒级；仅对命中的候选做一次预检。</summary>
        private static string? FindExternalFfmpeg()
        {
            var candidates = new List<string>(16);

            // PATH 环境变量中的各个目录
            string? pathEnv = Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrEmpty(pathEnv))
            {
                foreach (var dir in pathEnv.Split(';', StringSplitOptions.RemoveEmptyEntries))
                {
                    var trimmed = dir.Trim();
                    if (trimmed.Length == 0) continue;
                    try
                    {
                        var p = Path.Combine(trimmed, "ffmpeg.exe");
                        if (File.Exists(p)) candidates.Add(p);
                    }
                    catch { /* 非法路径跳过 */ }
                }
            }

            // 常见安装位置
            var roots = new List<string?>(8)
            {
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                @"C:\ffmpeg",
                @"C:\tools\ffmpeg"
            };
            foreach (var root in roots)
            {
                if (string.IsNullOrEmpty(root)) continue;
                try
                {
                    // winget 包：%LOCALAPPDATA%\Microsoft\WinGet\Packages\*\ffmpeg.exe（或 bin\ 下）
                    if (root == Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))
                    {
                        var wingetDir = Path.Combine(root, "Microsoft", "WinGet", "Packages");
                        if (Directory.Exists(wingetDir))
                        {
                            foreach (var sub in Directory.EnumerateDirectories(wingetDir))
                            {
                                AddIfExists(candidates, Path.Combine(sub, "ffmpeg.exe"));
                                AddIfExists(candidates, Path.Combine(sub, "bin", "ffmpeg.exe"));
                            }
                        }
                    }
                    AddIfExists(candidates, Path.Combine(root, "ffmpeg.exe"));
                    AddIfExists(candidates, Path.Combine(root, "bin", "ffmpeg.exe"));
                    AddIfExists(candidates, Path.Combine(root, "ffmpeg", "bin", "ffmpeg.exe"));
                }
                catch { /* 权限/非法路径跳过 */ }
            }

            foreach (var c in candidates)
            {
                if (IsUsableFfmpeg(c))
                {
                    Logger.Log("[AppPaths] PATH/常见位置候选通过预检: " + c);
                    return c;
                }
                Logger.Log("[AppPaths] 候选不可用（缺编码器/32位），跳过: " + c);
            }
            return null;
        }

        private static void AddIfExists(List<string> list, string path)
        {
            try { if (File.Exists(path)) list.Add(path); } catch { }
        }

        /// <summary>深度受限的全盘扫描：遍历所有固定磁盘（C:\, D:\ 等），
        /// 查找名为 ffmpeg.exe 的可执行文件，最大递归深度 5（覆盖 Fluent-M3U8/mediago/PrismGrab 等常见安装深度）。
        /// 优先匹配根目录、bin 子目录、ffmpeg 子目录下的 ffmpeg.exe。
        /// 命中候选须通过编码器预检；遇到无权限目录自动跳过，不会长时间挂起。</summary>
        private static string? ScanAllFixedDrivesForFfmpeg()
        {
            try
            {
                foreach (var drive in DriveInfo.GetDrives())
                {
                    if (!drive.IsReady) continue;
                    if (drive.DriveType != DriveType.Fixed) continue; // 只扫本地固定盘

                    var root = drive.RootDirectory.FullName;
                    // 根目录直接检查
                    try
                    {
                        var rootExe = Path.Combine(root, "ffmpeg.exe");
                        if (File.Exists(rootExe) && IsUsableFfmpeg(rootExe)) return rootExe;
                    }
                    catch { }

                    // 深度受限 BFS
                    var queue = new Queue<(string path, int depth)>();
                    try
                    {
                        foreach (var dir in Directory.EnumerateDirectories(root))
                            queue.Enqueue((dir, 1));
                    }
                    catch { continue; }

                    while (queue.Count > 0)
                    {
                        var (dir, depth) = queue.Dequeue();
                        try
                        {
                            // 优先检查当前目录及常见子目录
                            var exe = Path.Combine(dir, "ffmpeg.exe");
                            if (File.Exists(exe) && IsUsableFfmpeg(exe)) return exe;
                            exe = Path.Combine(dir, "bin", "ffmpeg.exe");
                            if (File.Exists(exe) && IsUsableFfmpeg(exe)) return exe;

                            if (depth < 5)
                            {
                                foreach (var sub in Directory.EnumerateDirectories(dir))
                                {
                                    var name = Path.GetFileName(sub).ToLowerInvariant();
                                    // 跳过明显不需要的目录，减少扫描量
                                    if (name is "windows" or "programdata" or "inetpub" or "$recycle.bin"
                                        or "system volume information") continue;
                                    queue.Enqueue((sub, depth + 1));
                                }
                            }
                        }
                        catch { /* 无权限/路径过长等跳过 */ }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log("[AppPaths] 全盘扫描 ffmpeg 异常: " + ex.Message);
            }
            return null;
        }

        /// <summary>判断 ffmpeg 是否可用：要求 x64 架构、含 libx264 编码器与 HEVC(h265) 解码器。
        /// 运行 `-hide_banner -encoders -decoders` 读取输出，超时 3 秒内未完成则视为不可用。
        /// 过滤剪映精简版（缺 libx264）与 32 位版本（HEVC 转码大分辨率易 malloc 失败）。</summary>
        private static bool IsUsableFfmpeg(string path)
        {
            try
            {
                if (!File.Exists(path)) return false;

                // 架构检查：读 PE 头 Machine 字段，0x8664 = x64
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    if (fs.Length < 0x40) return false;
                    fs.Seek(0x3C, SeekOrigin.Begin);
                    var peOff = new byte[4];
                    if (fs.Read(peOff, 0, 4) != 4) return false;
                    int pe = BitConverter.ToInt32(peOff, 0);
                    if (pe + 4 + 2 > fs.Length) return false;
                    fs.Seek(pe + 4, SeekOrigin.Begin);
                    var machine = new byte[2];
                    if (fs.Read(machine, 0, 2) != 2) return false;
                    if (BitConverter.ToUInt16(machine, 0) != 0x8664) return false;
                }

                // 编码器/解码器检查
                var psi = new ProcessStartInfo(path, "-hide_banner -encoders -decoders")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using (var p = Process.Start(psi))
                {
                    if (p == null) return false;
                    string output = p.StandardOutput.ReadToEnd() + "\n" + p.StandardError.ReadToEnd();
                    if (!p.WaitForExit(3000))
                    {
                        try { p.Kill(); } catch { }
                        return false;
                    }
                    return output.Contains("libx264") && output.Contains("hevc");
                }
            }
            catch
            {
                return false;
            }
        }
    }
}
