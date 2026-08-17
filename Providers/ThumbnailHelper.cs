using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DynamicWallpaper.Core;
using DynamicWallpaper.Models;

namespace DynamicWallpaper.Providers
{
    /// <summary>
    /// 壁纸缩略图生成器：图片直接解码；视频先尝试 Windows Shell 缩略图，
    /// 失败则使用 WPF MediaPlayer 截取首帧；最后再回退到文件图标，
    /// 保证库中不会长期显示空白占位。
    /// </summary>
    internal static class ThumbnailHelper
    {
        public static async Task<ImageSource?> GetThumbnailAsync(string path, WallpaperType type)
        {
            if (!File.Exists(path)) return null;
            try
            {
                if (type == WallpaperType.Image || type == WallpaperType.Gif)
                    return await Task.Run(() => GetImageThumbnail(path));

                if (type == WallpaperType.Video)
                {
                    var bmp = await GetVideoThumbnailAsync(path);
                    if (bmp != null)
                    {
                        Logger.Log($"[缩略图] Shell 成功: {Path.GetFileName(path)}");
                        return bmp;
                    }

                    bmp = await GetMediaPlayerFrameAsync(path);
                    if (bmp != null)
                    {
                        Logger.Log($"[缩略图] MediaPlayer 成功: {Path.GetFileName(path)}");
                        return bmp;
                    }

                    bmp = await GetFileIconAsync(path);
                    if (bmp != null)
                        Logger.Log($"[缩略图] 回退到文件图标: {Path.GetFileName(path)}");
                    else
                        Logger.Log($"[缩略图] 全部失败: {Path.GetFileName(path)}");
                    return bmp;
                }

                return null;
            }
            catch (Exception ex)
            {
                Logger.Log("[缩略图] 异常", ex);
                return null;
            }
        }

        private static BitmapSource? GetImageThumbnail(string path)
        {
            try
            {
                // 使用文件流加载，避免某些特殊字符路径在 Uri 解析时失败。
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                var img = new BitmapImage();
                img.BeginInit();
                img.StreamSource = fs;
                img.DecodePixelWidth = 360;
                img.CacheOption = BitmapCacheOption.OnLoad;
                img.EndInit();
                img.Freeze();
                return img;
            }
            catch (Exception ex)
            {
                Logger.Log($"[缩略图] 图片加载失败 {Path.GetFileName(path)}: {ex.Message}");
                return null;
            }
        }

        /// <summary>在 STA 线程调用 Shell 缩略图 API。</summary>
        private static Task<BitmapSource?> GetVideoThumbnailAsync(string path)
        {
            var tcs = new TaskCompletionSource<BitmapSource?>();
            var thread = new Thread(() =>
            {
                BitmapSource? result = null;
                int hr = CoInitializeEx(IntPtr.Zero, 0x2 /* COINIT_APARTMENTTHREADED */);
                try
                {
                    result = ShellThumbnail.GetThumbnail(path, 360);
                }
                catch (Exception ex)
                {
                    Logger.Log($"[缩略图] Shell 异常 {Path.GetFileName(path)}: {ex.Message}");
                }
                finally
                {
                    if (hr >= 0) CoUninitialize();
                }
                tcs.TrySetResult(result);
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
            return tcs.Task;
        }

        /// <summary>
        /// 使用 WPF MediaPlayer 截取视频首帧。MediaPlayer 必须跑在带 Dispatcher 的 STA 线程，
        /// 否则 MediaOpened 事件不会被触发。这里单独起一个 STA 线程并跑 Dispatcher。
        /// </summary>
        private static Task<BitmapSource?> GetMediaPlayerFrameAsync(string path)
        {
            var tcs = new TaskCompletionSource<BitmapSource?>();
            var thread = new Thread(() =>
            {
                int hr = CoInitializeEx(IntPtr.Zero, 0x2);
                var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;

                dispatcher.BeginInvoke(new Action(async () =>
                {
                    BitmapSource? result = null;
                    MediaPlayer? player = null;
                    try
                    {
                        player = new MediaPlayer
                        {
                            Volume = 0,
                            ScrubbingEnabled = true
                        };

                        var opened = new TaskCompletionSource<bool>();
                        var failed = false;
                        player.MediaOpened += (_, _) => opened.TrySetResult(true);
                        player.MediaFailed += (_, e) =>
                        {
                            failed = true;
                            opened.TrySetException(e.ErrorException ?? new InvalidOperationException("MediaPlayer 打开失败"));
                        };

                        player.Open(new Uri(path, UriKind.Absolute));

                        // 等待打开，最多 8 秒
                        await Task.WhenAny(opened.Task, Task.Delay(8000));
                        if (!opened.Task.IsCompleted || !opened.Task.Result)
                        {
                            Logger.Log($"[缩略图] MediaPlayer 打开超时或失败: {Path.GetFileName(path)}");
                            player.Close();
                            tcs.TrySetResult(null);
                            return;
                        }

                        if (failed || player.NaturalVideoWidth == 0)
                        {
                            Logger.Log($"[缩略图] MediaPlayer 非视频或失败: {Path.GetFileName(path)}");
                            player.Close();
                            tcs.TrySetResult(null);
                            return;
                        }

                        // 关键：先真正播放一小段，让解码管线产出可见帧，再暂停定格。
                        // 仅靠 Open+Position 在后台线程上往往画不出内容（黑帧/空帧），Play 后才能截到真实画面。
                        player.Play();
                        await Task.Delay(400);
                        try { player.Pause(); } catch { /* 忽略 */ }
                        await Task.Delay(60);

                        int w = player.NaturalVideoWidth;
                        int h = player.NaturalVideoHeight;
                        double ratio = w / (double)h;
                        int targetW = 360;
                        int targetH = Math.Max(1, (int)(targetW / ratio));

                        var rtb = new RenderTargetBitmap(targetW, targetH, 96, 96, PixelFormats.Pbgra32);
                        var dv = new DrawingVisual();
                        using (var ctx = dv.RenderOpen())
                        {
                            ctx.DrawVideo(player, new Rect(0, 0, targetW, targetH));
                        }
                        rtb.Render(dv);
                        rtb.Freeze();
                        result = rtb;

                        player.Close();
                        Logger.Log($"[缩略图] MediaPlayer 截帧成功: {Path.GetFileName(path)} ({w}x{h})");
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"[缩略图] MediaPlayer 异常 {Path.GetFileName(path)}: {ex.Message}");
                    }
                    finally
                    {
                        tcs.TrySetResult(result);
                        dispatcher.BeginInvokeShutdown(System.Windows.Threading.DispatcherPriority.Background);
                    }
                }));

                Dispatcher.Run();
                if (hr >= 0) CoUninitialize();
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
            return tcs.Task;
        }

        /// <summary>回退方案：提取文件关联图标。</summary>
        private static Task<BitmapSource?> GetFileIconAsync(string path)
        {
            return Task.Run(() =>
            {
                try
                {
                    using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
                    if (icon == null) return null;

                    using var bmp = icon.ToBitmap();
                    var hBitmap = bmp.GetHbitmap();
                    try
                    {
                        var src = Imaging.CreateBitmapSourceFromHBitmap(
                            hBitmap, IntPtr.Zero, Int32Rect.Empty,
                            BitmapSizeOptions.FromEmptyOptions());
                        src.Freeze();
                        return (BitmapSource?)src;
                    }
                    finally
                    {
                        DeleteObject(hBitmap);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"[缩略图] 图标回退失败 {Path.GetFileName(path)}: {ex.Message}");
                    return null;
                }
            });
        }

        [DllImport("ole32.dll", PreserveSig = true)]
        private static extern int CoInitializeEx(IntPtr pvReserved, int dwCoInit);

        [DllImport("ole32.dll", PreserveSig = true)]
        private static extern void CoUninitialize();

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);
    }
}
