using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace DynamicWallpaper.Core
{
    /// <summary>
    /// 视频编码检测：判断视频轨是否为 HEVC/H.265，用于导入壁纸时提示转码
    /// （部分系统/显卡无法硬件解码 HEVC，直接播放会黑屏）。
    ///
    /// 检测策略（兼顾「MP4 零开销」与「任意容器可靠」）：
    ///   1) MP4 / M4V / MOV 等 ISO BMFF 容器：先用零开销的 stsd box 解析（不启动进程，毫秒级）。
    ///   2) 其它容器（MKV / AVI / TS / WMV / FLV / WEBM 等），或 box 解析失败时：
    ///      回退用程序已捆绑的 ffmpeg 探测首个视频流的编码（最可靠，覆盖所有封装格式）。
    /// </summary>
    public static class VideoCodecDetector
    {
        /// <summary>视觉（视频）编码四字符码。box 解析时只认这些，避免误把音频轨（mp4a 等）当视频编码。</summary>
        private static readonly HashSet<string> VisualCodecs = new()
        {
            "avc1", "avc3", "hvc1", "hev1", "mp4v",
            "vp08", "vp09", "av01", "dvhe", "dvh1", "encv"
        };

        /// <summary>检测视频编码。返回视频编码标识：MP4 系来自 box 解析的 4CC（hvc1/hev1/avc1…），
        /// 或来自 ffmpeg 的 codec_name（hevc/h264/av1…）；非视频/解析失败返回 null。</summary>
        public static async Task<string?> DetectVideoCodecAsync(string path)
        {
            // 快速路径：ISO BMFF 系容器用零开销的 box 解析
            string ext = Path.GetExtension(path);
            if (ext.Equals(".mp4", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".m4v", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".mov", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    // 放到线程池执行：避免大文件同步 IO（读取头部/尾部各 8MB）阻塞 UI 线程造成拖入卡顿
                    string? box = await Task.Run(() => DetectVideoCodecBox(path));
                    if (box != null) return box;
                }
                catch { /* 解析异常则交给 ffmpeg 兜底 */ }
            }

            // 兜底：任意容器用 ffmpeg 探测（最可靠）
            return await DetectViaFfmpegAsync(path);
        }

        /// <summary>编码是否为 HEVC/H.265（兼容 box 4CC 与 ffmpeg codec_name 两种命名）。</summary>
        public static bool IsHevc(string? codec)
        {
            if (string.IsNullOrWhiteSpace(codec)) return false;
            var c = codec.Trim().ToLowerInvariant();
            return c is "hvc1" or "hev1" or "hevc" or "h265";
        }

        // ---- ffmpeg 兜底探测 ----

        private static async Task<string?> DetectViaFfmpegAsync(string path)
        {
            try
            {
                string? ffmpeg = AppPaths.EnsureFfmpeg();
                if (string.IsNullOrEmpty(ffmpeg)) return null;

                var psi = new ProcessStartInfo
                {
                    FileName = ffmpeg,
                    Arguments = $"-hide_banner -i \"{path}\"",
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var p = Process.Start(psi);
                if (p == null) return null;

                string stderr = await p.StandardError.ReadToEndAsync();
                await p.WaitForExitAsync();

                // ffmpeg 会把流信息打到 stderr，形如：
                //   Stream #0:0: Video: hevc, yuv420p(tv, bt709), 3840x2160, ...
                //   Stream #0:0: Video: h264 (High), yuv420p, 1920x1080, ...
                foreach (var raw in stderr.Split('\n'))
                {
                    var line = raw.Trim();
                    int streamIdx = line.IndexOf("Stream", StringComparison.OrdinalIgnoreCase);
                    int videoIdx = line.IndexOf("Video:", StringComparison.OrdinalIgnoreCase);
                    if (streamIdx >= 0 && videoIdx >= 0 && videoIdx > streamIdx)
                    {
                        var after = line.Substring(videoIdx + "Video:".Length).Trim();
                        // 取编码名（逗号/空格前，可能带 profile 如 "hevc (Main 10)"）
                        int end = after.IndexOfAny(new[] { ',', ' ' });
                        var codec = (end > 0 ? after.Substring(0, end) : after).Trim().ToLowerInvariant();
                        if (!string.IsNullOrEmpty(codec)) return codec;
                    }
                }
            }
            catch { /* ffmpeg 不可用或探测失败 → 无法判定，返回 null */ }
            return null;
        }

        // ---- MP4/MOV box 快速解析（零开销，不启动进程） ----

        /// <summary>读取文件头部（faststart）与尾部（moov 后置）各最多 8MB，解析 stsd 返回视频编码四字符码；
        /// 非 MP4 系容器或解析失败返回 null。</summary>
        private static string? DetectVideoCodecBox(string path)
        {
            using var fs = File.OpenRead(path);
            if (fs.Length < 12) return null;

            const int chunk = 8 * 1024 * 1024;
            int headLen = (int)Math.Min(fs.Length, chunk);
            byte[] head = new byte[headLen];
            fs.Position = 0;
            fs.ReadExactly(head, 0, headLen);

            string? codec = FindVideoCodec(head, 0, head.Length);
            if (codec != null) return codec;

            // moov 位于文件尾部（未做 faststart）时，头部扫描找不到 stsd，回退读尾部
            if (fs.Length > headLen + 1024)
            {
                int tailLen = (int)Math.Min(fs.Length, chunk);
                byte[] tail = new byte[tailLen];
                fs.Position = fs.Length - tailLen;
                fs.ReadExactly(tail, 0, tailLen);
                // 尾部起点往往落在 mdat 中间（不是 box 边界），直接按 box 树遍历会解析失败；
                // 需先反向定位 moov box 的起始位置，再从该边界开始解析 stsd。
                return FindVideoCodecInTail(tail);
            }
            return null;
        }

        /// <summary>在文件尾部数据中反向查找 moov box，并从其起点解析 stsd。
        /// 尾部读取起点可能位于 mdat 等大 box 中间，按普通 box 遍历读到的 size/type 是随机数据，
        /// 因此先扫描 4CC "moov" 出现位置，向前 4 字节读取 size 验证边界，再进入 box 树解析。</summary>
        private static string? FindVideoCodecInTail(byte[] tail)
        {
            for (int i = tail.Length - 4; i >= 4; i--)
            {
                if (tail[i] == (byte)'m' && tail[i + 1] == (byte)'o'
                    && tail[i + 2] == (byte)'o' && tail[i + 3] == (byte)'v')
                {
                    int boxStart = i - 4;
                    uint size = ReadU32(tail, boxStart);
                    // size 合法（>= 8）且 box 完全落在 tail 内才进入解析；超大 moov 也允许解析到 tail 末尾
                    if (size >= 8 && boxStart + (long)size <= tail.Length)
                    {
                        var codec = FindVideoCodec(tail, boxStart, boxStart + (int)size);
                        if (codec != null) return codec;
                    }
                    // 该 "moov" 位置不合法（可能是 mdat 内的巧合字节），继续往前找
                }
            }
            return null;
        }

        /// <summary>遍历 MP4 box 树，查找所有 stsd 并解析其中的视频样本描述编码。返回第一个视频编码；找不到返回 null。</summary>
        private static string? FindVideoCodec(byte[] d, int start, int end)
        {
            var stack = new Stack<(int s, int e)>();
            stack.Push((start, end));

            while (stack.Count > 0)
            {
                var (s, e) = stack.Pop();
                int pos = s;
                while (pos + 8 <= e)
                {
                    uint size = ReadU32(d, pos);
                    string type = ReadType(d, pos + 4);
                    int boxEnd;
                    if (size == 1)
                    {
                        if (pos + 16 > e) break;
                        ulong large = ReadU64(d, pos + 8);
                        boxEnd = (int)((ulong)pos + large);
                        pos += 16;
                    }
                    else if (size == 0)
                    {
                        boxEnd = e;
                        pos += 8;
                    }
                    else
                    {
                        boxEnd = pos + (int)size;
                        pos += 8;
                    }

                    if (boxEnd < pos || boxEnd > e) break;

                    if (type == "stsd")
                    {
                        string? codec = ParseStsd(d, pos, boxEnd);
                        if (codec != null) return codec;
                    }
                    else if (type is "moov" or "trak" or "mdia" or "minf" or "stbl" or "edts" or "dinf")
                    {
                        stack.Push((pos, boxEnd));
                    }

                    pos = boxEnd;
                }
            }
            return null;
        }

        /// <summary>解析 stsd：返回首个视频编码四字符码。</summary>
        private static string? ParseStsd(byte[] d, int contentStart, int boxEnd)
        {
            int p = contentStart + 4; // 跳过 fullbox 的 version+flags
            if (p + 4 > boxEnd) return null;
            uint count = ReadU32(d, p);
            p += 4;
            for (uint i = 0; i < count && p + 8 <= boxEnd; i++)
            {
                uint entrySize = ReadU32(d, p);
                string entryType = ReadType(d, p + 4);
                if (entryType.Length == 4 && VisualCodecs.Contains(entryType))
                    return entryType;
                p += (int)entrySize;
            }
            return null;
        }

        private static uint ReadU32(byte[] d, int p) =>
            ((uint)d[p] << 24) | ((uint)d[p + 1] << 16) | ((uint)d[p + 2] << 8) | d[p + 3];

        private static ulong ReadU64(byte[] d, int p) =>
            ((ulong)ReadU32(d, p) << 32) | ReadU32(d, p + 4);

        private static string ReadType(byte[] d, int p)
        {
            if (p + 4 > d.Length) return "";
            char c0 = (char)d[p], c1 = (char)d[p + 1], c2 = (char)d[p + 2], c3 = (char)d[p + 3];
            bool printable = c0 >= 32 && c0 < 127 && c1 >= 32 && c1 < 127
                          && c2 >= 32 && c2 < 127 && c3 >= 32 && c3 < 127;
            return printable ? new string(new[] { c0, c1, c2, c3 }) : "";
        }
    }
}
