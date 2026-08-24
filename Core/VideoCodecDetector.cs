using System;
using System.Collections.Generic;
using System.IO;

namespace DynamicWallpaper.Core
{
    /// <summary>
    /// MP4 视频编码检测：解析 stsd 样本描述，判断视频轨编码是否为 HEVC/H.265（hvc1/hev1）。
    /// 用于导入壁纸时提示转码——部分系统/显卡无法硬件解码 HEVC，直接播放会黑屏。
    /// </summary>
    public static class VideoCodecDetector
    {
        /// <summary>视觉（视频）编码四字符码。解析 stsd 时只认这些，避免误把音频轨（mp4a 等）当视频编码。</summary>
        private static readonly HashSet<string> VisualCodecs = new()
        {
            "avc1", "avc3", "hvc1", "hev1", "mp4v",
            "vp08", "vp09", "av01", "dvhe", "dvh1", "encv"
        };

        /// <summary>分块流式扫描全文查找 stsd，解析返回视频编码四字符码；非 MP4 容器或解析失败返回 null。
        /// 注意：不能只读文件头/尾固定大小——当 mdat 大于 8MB 且位于头部、moov 在文件尾部时，
        /// 头部扫描会被超大的 mdat 截断（boxEnd 越界 break），尾部扫描又恰好从 mdat 数据中间开始
        /// （把随机数据当 box 头解析，极易 break），两种路径都会漏判。因此改为全文分块扫描 stsd 标记，
        /// 对每个命中做 box 合理性校验后解析，兼容 moov 在任意位置的容器。</summary>
        public static string? DetectVideoCodec(string path)
        {
            try
            {
                using var fs = File.OpenRead(path);
                if (fs.Length < 12) return null;

                const int chunkSize = 1 << 20; // 1MB
                const int overlap = 8;         // 相邻块重叠 8 字节，保证 stsd 跨块边界不被漏掉
                byte[] buf = new byte[chunkSize + overlap];
                long fileLen = fs.Length;
                long readStart = 0;

                while (readStart < fileLen)
                {
                    int toRead = (int)Math.Min(chunkSize, fileLen - readStart);
                    int total = toRead + (readStart + toRead < fileLen ? overlap : 0);
                    fs.Position = readStart;
                    int got = fs.Read(buf, 0, total);
                    if (got <= 0) break;

                    for (int i = 0; i + 4 <= got; i++)
                    {
                        if (buf[i] == (byte)'s' && buf[i + 1] == (byte)'t'
                         && buf[i + 2] == (byte)'s' && buf[i + 3] == (byte)'d')
                        {
                            long typeAbs = readStart + i;
                            long sizeAbs = typeAbs - 4;
                            if (sizeAbs < 0) continue;

                            int headLen = (int)Math.Min(128, fileLen - sizeAbs);
                            byte[] hdr = new byte[headLen];
                            fs.Position = sizeAbs;
                            if (fs.Read(hdr, 0, headLen) < 16) continue;

                            uint boxSize = ReadU32(hdr, 0);
                            // 合理性校验：stsd 至少含 size(4)+type(4)+fullbox(4)+entry_count(4)+一个 entry(8)
                            if (boxSize < 24 || boxSize > 512 * 1024 * 1024) continue;
                            if (sizeAbs + boxSize > fileLen) continue;

                            // contentStart=8 指向 fullbox（跳过 size+type），boxEnd 取 box 实际大小与缓冲区的较小者
                            string? codec = ParseStsd(hdr, 8, (int)Math.Min(boxSize, (uint)headLen));
                            if (codec != null) return codec;
                        }
                    }

                    if (readStart + chunkSize >= fileLen) break;
                    readStart += chunkSize - overlap;
                }
                return null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>编码是否为 HEVC/H.265（hvc1/hev1）。</summary>
        public static bool IsHevc(string? codec) => codec is "hvc1" or "hev1";

        /// <summary>遍历 MP4 box 树，查找所有 stsd 并解析其中的视频样本描述编码。
        /// 返回第一个视频编码；找不到返回 null。用显式栈迭代，避免嵌套 box 过深。</summary>
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
                        // 64-bit largesize
                        if (pos + 16 > e) break;
                        ulong large = ReadU64(d, pos + 8);
                        boxEnd = (int)((ulong)pos + large);
                        pos += 16;
                    }
                    else if (size == 0)
                    {
                        // 延伸到父 box 末尾
                        boxEnd = e;
                        pos += 8;
                    }
                    else
                    {
                        boxEnd = pos + (int)size;
                        pos += 8;
                    }

                    if (boxEnd < pos || boxEnd > e) break; // 异常 box（空 box 时 boxEnd==pos 合法，跳过即可），中止该分支

                    if (type == "stsd")
                    {
                        string? codec = ParseStsd(d, pos, boxEnd);
                        if (codec != null) return codec;
                    }
                    else if (type is "moov" or "trak" or "mdia" or "minf" or "stbl" or "edts" or "dinf")
                    {
                        stack.Push((pos, boxEnd));
                    }
                    // 其它 box 跳过

                    pos = boxEnd;
                }
            }
            return null;
        }

        /// <summary>解析 stsd 内容：fullbox(version+flags) + entry_count + sample entries。
        /// 每个 entry 头部即 (size + type)，type 为编码四字符码。返回首个视频编码。</summary>
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
            // 仅接受可打印 ASCII（4CC 均为字母/数字），避免把随机二进制当类型名
            bool printable = c0 >= 32 && c0 < 127 && c1 >= 32 && c1 < 127
                          && c2 >= 32 && c2 < 127 && c3 >= 32 && c3 < 127;
            return printable ? new string(new[] { c0, c1, c2, c3 }) : "";
        }
    }
}
