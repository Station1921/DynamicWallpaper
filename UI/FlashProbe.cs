using System;
using System.Runtime.InteropServices;
using System.Threading;
using DynamicWallpaper.Core;

namespace DynamicWallpaper
{
    /// <summary>切换瞬间的屏幕闪屏取证（r16）。
    ///
    /// 用户反复描述"点击设置壁纸、松开鼠标一瞬间闪一下，看不清闪的是什么"。日志能记录
    /// 程序做了什么，却回答不了"屏幕上到底闪出了什么内容"。本探针在切换开始后的 1.5s 内，
    /// 每约 16ms 把整屏缩成 48x30 采样一次，只在与上一帧差异超过阈值时记一行：
    /// 时间偏移 + 平均色 + 亮度 + 变化量。据此可一眼判断闪的是：
    ///   ① 全屏整体变亮/偏紫 → 流光过渡层（SwitchOverlay）；
    ///   ② 整体换成了另一张画面（出现桌面图标）→ 桌面重组、露出系统壁纸；
    ///   ③ 只有局部区域变化 → 应用窗口内部（卡片/状态栏/悬停预览）。
    /// 若全程没有一行"画面变化"，则说明程序侧没有产生全屏可见变化。
    ///
    /// 注意：屏幕 DC 的 BitBlt 取的是 DWM 合成结果，极少数硬件叠加层（MPO）内容可能取不到。
    /// 全程 try/catch，任何失败只记一行并静默退出，绝不影响壁纸功能。</summary>
    internal static class FlashProbe
    {
        private const int SampleW = 48;
        private const int SampleH = 30;
        private const int IntervalMs = 16;
        private const int DurationMs = 1500;
        /// <summary>平均色（三通道绝对值之和，0~765）变化阈值：超过才记一行。</summary>
        private const double ColorDeltaThreshold = 6.0;
        /// <summary>最多记多少行（视频壁纸持续运动时防刷屏）。</summary>
        private const int MaxLines = 40;

        private static int _running;
        private static int _seq;

        /// <summary>开始一次取证（非阻塞）。tag 用于在日志里标注本次切换的壁纸名。</summary>
        public static void Watch(string tag)
        {
            // 同一时刻只跑一个（多屏恢复、连点会连续调用）
            if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return;
            try
            {
                int id = Interlocked.Increment(ref _seq);
                var t = new Thread(() => Run(id, tag)) { IsBackground = true, Priority = ThreadPriority.BelowNormal };
                t.Start();
            }
            catch
            {
                Interlocked.Exchange(ref _running, 0);
            }
        }

        private static void Run(int id, string tag)
        {
            IntPtr screenDc = IntPtr.Zero, memDc = IntPtr.Zero, dib = IntPtr.Zero, oldBmp = IntPtr.Zero;
            var start = DateTime.UtcNow;
            try
            {
                int sw = GetSystemMetrics(0), sh = GetSystemMetrics(1);
                if (sw <= 0 || sh <= 0) return;
                screenDc = GetDC(IntPtr.Zero);
                if (screenDc == IntPtr.Zero) return;
                memDc = CreateCompatibleDC(screenDc);
                if (memDc == IntPtr.Zero) return;

                var bmi = new BmiHeader
                {
                    biSize = Marshal.SizeOf<BmiHeader>(),
                    biWidth = SampleW,
                    biHeight = -SampleH, // 负高度 = 自上而下行序（bits[0] 即左上角像素）
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = 0
                };
                dib = CreateDIBSection(memDc, ref bmi, 0, out IntPtr bits, IntPtr.Zero, 0);
                if (dib == IntPtr.Zero || bits == IntPtr.Zero) return;
                oldBmp = SelectObject(memDc, dib);
                SetStretchBltMode(memDc, 4 /* HALFTONE */);

                int n = SampleW * SampleH;
                var buf = new byte[n * 4];
                var cur = new byte[n * 3];
                var prev = new byte[n * 3];

                int frame = 0, lines = 0;
                double maxColorDelta = 0; int maxColorAt = -1;
                double maxPixDelta = 0; int maxPixAt = -1;

                Logger.Log($"[闪屏取证#{id}] 开始（{tag}）：{DurationMs}ms 内每 ~{IntervalMs}ms 采样整屏，仅记录明显变化的帧");

                while (true)
                {
                    int t0 = (int)(DateTime.UtcNow - start).TotalMilliseconds;
                    if (t0 > DurationMs) break;
                    frame++;
                    if (!StretchBlt(memDc, 0, 0, SampleW, SampleH, screenDc, 0, 0, sw, sh, 0x00CC0020 /* SRCCOPY */))
                        break;
                    Marshal.Copy(bits, buf, 0, buf.Length);

                    long sr = 0, sg = 0, sb = 0;
                    long pixSum = 0;
                    for (int p = 0, q = 0; p < n; p++, q += 4)
                    {
                        int b = buf[q], g = buf[q + 1], r = buf[q + 2];
                        cur[p * 3] = (byte)r; cur[p * 3 + 1] = (byte)g; cur[p * 3 + 2] = (byte)b;
                        sr += r; sg += g; sb += b;
                        if (frame > 1)
                            pixSum += Math.Abs(r - prev[p * 3]) + Math.Abs(g - prev[p * 3 + 1]) + Math.Abs(b - prev[p * 3 + 2]);
                    }

                    int mr = (int)(sr / n), mg = (int)(sg / n), mb = (int)(sb / n);
                    double pixAvg = frame > 1 ? pixSum / (double)(n * 3) : 0;
                    double colorDelta = 0;
                    if (frame > 1)
                    {
                        colorDelta = Math.Abs(mr - prevMeanR) + Math.Abs(mg - prevMeanG) + Math.Abs(mb - prevMeanB);
                        if (colorDelta > maxColorDelta) { maxColorDelta = colorDelta; maxColorAt = t0; }
                        if (pixAvg > maxPixDelta) { maxPixDelta = pixAvg; maxPixAt = t0; }
                    }

                    if (frame == 1)
                    {
                        Logger.Log($"[闪屏取证#{id}] t=+0ms 基准帧 均色=#{mr:X2}{mg:X2}{mb:X2} 亮度={(mr + mg + mb) / 3}");
                        lines++;
                    }
                    else if (colorDelta >= ColorDeltaThreshold && lines < MaxLines)
                    {
                        Logger.Log($"[闪屏取证#{id}] t=+{t0}ms ★全屏画面变化 均色=#{mr:X2}{mg:X2}{mb:X2} 亮度={(mr + mg + mb) / 3} 均色差={colorDelta:0.0} 像素均差={pixAvg:0.0}");
                        lines++;
                    }

                    prevMeanR = mr; prevMeanG = mg; prevMeanB = mb;
                    Array.Copy(cur, prev, cur.Length);

                    int spent = (int)(DateTime.UtcNow - start).TotalMilliseconds - t0;
                    int sleep = IntervalMs - spent;
                    if (sleep > 0) Thread.Sleep(sleep);
                }

                Logger.Log($"[闪屏取证#{id}] 结束：共 {frame} 帧，记录 {lines} 行；最大均色差={maxColorDelta:0.0}" +
                           (maxColorAt >= 0 ? $"@t+{maxColorAt}ms" : "") +
                           $" 最大像素均差={maxPixDelta:0.0}" +
                           (maxPixAt >= 0 ? $"@t+{maxPixAt}ms" : "") +
                           (lines <= 1 ? "（全程只有基准帧 → 屏幕上没有发生整体画面变化）" : ""));
            }
            catch (Exception ex)
            {
                try { Logger.Log($"[闪屏取证#{id}] 记录失败（不影响壁纸功能）: {ex.Message}"); } catch { }
            }
            finally
            {
                try { if (oldBmp != IntPtr.Zero && memDc != IntPtr.Zero) SelectObject(memDc, oldBmp); } catch { }
                try { if (dib != IntPtr.Zero) DeleteObject(dib); } catch { }
                try { if (memDc != IntPtr.Zero) DeleteDC(memDc); } catch { }
                try { if (screenDc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screenDc); } catch { }
                Interlocked.Exchange(ref _running, 0);
            }
        }

        private static int prevMeanR, prevMeanG, prevMeanB;

        [StructLayout(LayoutKind.Sequential)]
        private struct BmiHeader
        {
            public int biSize;
            public int biWidth;
            public int biHeight;
            public short biPlanes;
            public short biBitCount;
            public int biCompression;
            public int biSizeImage;
            public int biXPelsPerMeter;
            public int biYPelsPerMeter;
            public int biClrUsed;
            public int biClrImportant;
        }

        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("user32.dll")] private static extern int GetSystemMetrics(int nIndex);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr ho);
        [DllImport("gdi32.dll")] private static extern int SetStretchBltMode(IntPtr hdc, int mode);
        [DllImport("gdi32.dll")]
        private static extern bool StretchBlt(IntPtr hdcDest, int xDest, int yDest, int wDest, int hDest,
            IntPtr hdcSrc, int xSrc, int ySrc, int wSrc, int hSrc, int rop);
        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BmiHeader pbmi, uint usage,
            out IntPtr ppvBits, IntPtr hSection, uint offset);
    }
}
