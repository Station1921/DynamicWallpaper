using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

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
        ///   1) 程序目录已有 ffmpeg.exe → 直接使用，不释放；
        ///   2) 扫描 PATH 与本机常见安装位置（winget / ProgramFiles / C:\ffmpeg 等）→
        ///      找到即直接调用，不做任何释放（毫秒级纯文件检查）；
        ///   3) 以上都没有 → 才从内嵌资源解压到程序目录兜底。
        /// 仅在需要转码时由调用方触发（平时浏览库 / 设壁纸完全不调用，零开销）。
        /// 不做编码器探测（libx264/HEVC），直接用；若某版本缺编码器导致转码失败，
        /// 由调用方回退到 <see cref="ForceEmbeddedFfmpeg"/> 解压内嵌兜底。
        /// </summary>
        public static string? EnsureFfmpeg()
        {
            if (_cachedFfmpeg != null) return _cachedFfmpeg;

            // 1) 程序目录已有 → 直接用
            string local = Path.Combine(RootDirectory, "ffmpeg.exe");
            if (File.Exists(local))
            {
                Logger.Log("[AppPaths] 使用程序目录 ffmpeg: " + local);
                return _cachedFfmpeg = local;
            }

            // 2) 扫描 PATH + 常见安装位置 → 找到直接调用，不释放
            string? found = FindExternalFfmpeg();
            if (found != null)
            {
                Logger.Log("[AppPaths] 使用本机已有 ffmpeg: " + found);
                return _cachedFfmpeg = found;
            }

            // 3) 全盘扫描所有逻辑盘符 → 找到直接调用，不释放（覆盖用户自装软件目录）
            string? foundAll = FindFfmpegOnAllDrives();
            if (foundAll != null)
            {
                Logger.Log("[AppPaths] 使用全盘扫描 ffmpeg: " + foundAll);
                return _cachedFfmpeg = foundAll;
            }

            // 4) 都没有 → 才解压内嵌资源兜底
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

        /// <summary>扫描 PATH 与常见安装位置，返回第一个存在的 ffmpeg.exe（不验证编码器）。
        /// 纯本地文件检查（File.Exists），毫秒级；无则返回 null。</summary>
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

            return candidates.Count > 0 ? candidates[0] : null;
        }

        /// <summary>全盘扫描所有逻辑盘符，递归枚举 ffmpeg.exe，并预检编码器能力。
        /// 并行枚举各盘符以缩短耗时；仅当 PATH/常见位置均未命中时才调用，结果会话级缓存。
        /// 未就绪/无权限/异常盘符自动跳过。全部无果返回 null。</summary>
        private static string? FindFfmpegOnAllDrives()
        {
            var candidates = new ConcurrentBag<string>();
            var drives = DriveInfo.GetDrives()
                .Where(d => d.IsReady && (d.DriveType == DriveType.Fixed || d.DriveType == DriveType.Removable))
                .Select(d => d.RootDirectory.FullName)
                .ToArray();
            if (drives.Length == 0) return null;

            Parallel.ForEach(drives, drive =>
            {
                try
                {
                    foreach (var p in SafeEnumerateFfmpeg(drive))
                    {
                        candidates.Add(p);
                    }
                }
                catch
                {
                    // 整盘级异常：跳过该盘继续
                }
            });

            // 预检编码器：优先选择含 libx264 + hevc 的完整版。
            // 剪映等精简版 ffmpeg 缺 libx264 会导致转码失败后被迫回退内嵌解压，故必须过滤。
            foreach (var c in candidates)
            {
                if (HasRequiredCodecs(c)) return c;
            }
            return null;
        }

        /// <summary>运行 ffmpeg -encoders -decoders 检查是否含 libx264 编码器与 hevc 解码器。
        /// 启动失败/超时/无权限视为不可用，返回 false。</summary>
        private static bool HasRequiredCodecs(string ffmpegPath)
        {
            try
            {
                var psi = new ProcessStartInfo(ffmpegPath, "-hide_banner -encoders -decoders")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using var proc = Process.Start(psi);
                if (proc == null) return false;
                string output = proc.StandardOutput.ReadToEnd() + proc.StandardError.ReadToEnd();
                if (!proc.WaitForExit(3000))
                {
                    try { proc.Kill(); } catch { /* ignore */ }
                    return false;
                }
                return output.Contains("libx264", StringComparison.OrdinalIgnoreCase)
                    && output.Contains("hevc", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>自根目录深度优先遍历，逐目录容错：无权限/超长路径等异常目录跳过，不中断整盘扫描。</summary>
        private static IEnumerable<string> SafeEnumerateFfmpeg(string root)
        {
            var stack = new Stack<string>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var dir = stack.Pop();
                foreach (var f in GetFilesSafe(dir)) yield return f;
                foreach (var sub in GetDirsSafe(dir)) stack.Push(sub);
            }
        }

        private static string[] GetFilesSafe(string dir)
        {
            try { return Directory.GetFiles(dir, "ffmpeg.exe", SearchOption.TopDirectoryOnly); }
            catch { return Array.Empty<string>(); }
        }

        private static string[] GetDirsSafe(string dir)
        {
            try { return Directory.GetDirectories(dir); }
            catch { return Array.Empty<string>(); }
        }

        private static void AddIfExists(List<string> list, string path)
        {
            try { if (File.Exists(path)) list.Add(path); } catch { }
        }
    }
}
