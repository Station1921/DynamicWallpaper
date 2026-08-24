using System;
using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using DynamicWallpaper.Core;
using DynamicWallpaper.Desktop;
using DynamicWallpaper.Models;
using Image = System.Windows.Controls.Image;

namespace DynamicWallpaper.Providers
{
    /// <summary>
    /// 静态壁纸渐入过渡 Provider：用 WPF Image 控件渲染图片 + WPF 原生 opacity 动画渐入。
    ///
    /// 用途：动态壁纸 A → 静态壁纸 S 切换时，系统壁纸层（SPI/IDesktopWallpaper）本身无法
    /// 播放渐入动画；本 Provider 先把 S 渲染在 WPF 窗口里渐入覆盖 A（A 保持显示、
    /// 不做渐出），渐入完成后由调用方把系统壁纸设为 S 并销毁所有窗口，视觉上
    /// "A → S 无缝直切且 S 有渐入效果"，中间不露出任何旧静态壁纸残留。
    ///
    /// 复用 ImageProvider 的 RenderWindow + Image 控件方案（零 WebView2 依赖，秒切）。
    /// </summary>
    public class StaticFadeProvider : IWallpaperProvider
    {
        private RenderWindow? _window;
        private Image? _image;
        private string _path = "";

        /// <summary>图片渐入过渡时长（毫秒）。</summary>
        public int FadeMs { get; set; } = 300;

        /// <summary>壁纸适应方式：fill=铺满裁剪 / fit=完整显示 / center=原始居中。由 WallpaperManager 在切换时注入。</summary>
        public static string FitMode { get; set; } = "fill";

        /// <summary>把 FitMode 映射为 WPF Image Stretch / StretchDirection。</summary>
        private static (Stretch Stretch, StretchDirection Direction) BuildImageStretch()
        {
            return FitMode switch
            {
                "fit" => (Stretch.Uniform, StretchDirection.DownOnly),
                "center" => (Stretch.None, StretchDirection.Both),
                _ => (Stretch.UniformToFill, StretchDirection.Both)
            };
        }

        /// <summary>当前宿主窗口挂接到的 WorkerW 承载层（供 WallpaperManager 校验失效后重挂）。</summary>
        public IntPtr AttachedWorkerW { get; private set; } = IntPtr.Zero;

        public WallpaperType Type => WallpaperType.Image;
        public IntPtr Handle => _window == null ? IntPtr.Zero : new System.Windows.Interop.WindowInteropHelper(_window).EnsureHandle();

        /// <summary>
        /// 创建/复用渲染窗口与 Image 控件，加载目标图片。
        /// 复用层再次调用时仅更新 Image.Source 并重置 opacity=0（隐藏状态，不闪现旧图）。
        /// </summary>
        public void Show(string path, Rectangle bounds)
        {
            _path = path;

            if (_window == null)
            {
                // 首次创建窗口 + Image（每屏仅一次）
                _window = new RenderWindow();
                var (stretch, direction) = BuildImageStretch();
                _image = new Image
                {
                    Stretch = stretch,
                    StretchDirection = direction,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                    VerticalAlignment = System.Windows.VerticalAlignment.Center,
                    Opacity = 0, // 隐藏状态，渐入前不可见
                    Source = LoadImage(path, Math.Max(bounds.Width, bounds.Height))
                };
                _window.RootGrid.Children.Add(_image);
                _window.SetDeviceBounds(bounds);
            }
            else
            {
                // 复用层：更新图片源 + 重置 opacity=0（隐藏，不闪现旧图 C）
                _image!.Source = LoadImage(path, Math.Max(bounds.Width, bounds.Height));
                _image.Opacity = 0;
                _window.SetDeviceBounds(bounds);
            }
        }

        /// <summary>将渲染窗口挂接到 WorkerW 容器层。</summary>
        public void AttachTo(IntPtr workerw, Rectangle bounds)
        {
            if (_window == null) return;
            WorkerWInjector.Attach(Handle, workerw, bounds);
            AttachedWorkerW = workerw;
            // 必须调用 Show() 触发 WPF 渲染管线（仅 SetWindowPos SWP_SHOWWINDOW 不够，
            // WPF 窗口需 Show() 才开始渲染内容到 DirectX 表面，DWM 才能合成到桌面）。
            _window.Show();
            Win32.SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0,
                Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOZORDER
                | Win32.SWP_NOACTIVATE | Win32.SWP_SHOWWINDOW);
        }

        /// <summary>隐藏复用层（保留窗口+Image 供下次即时复用），不销毁。
        /// 用于切到动态壁纸时把静态层藏到桌面之下，下次切回静态可秒显。</summary>
        public void Hide()
        {
            if (_window == null) return;
            Win32.SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0,
                Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOZORDER
                | Win32.SWP_NOACTIVATE | Win32.SWP_HIDEWINDOW);
        }

        /// <summary>触发图片渐入：WPF 原生 opacity 动画（0→1），等待过渡完成。
        /// 调用方必须在 <see cref="AttachTo"/> 之后调用，确保窗口已显示（透明→渐入覆盖下层）。</summary>
        public async Task FadeInAsync()
        {
            if (_image == null) return;
            var tcs = new TaskCompletionSource<bool>();
            var sb = new System.Windows.Media.Animation.Storyboard();
            var anim = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(FadeMs))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            anim.Completed += (_, _) => tcs.TrySetResult(true);
            Storyboard.SetTarget(anim, _image);
            Storyboard.SetTargetProperty(anim, new PropertyPath(UIElement.OpacityProperty));
            sb.Children.Add(anim);
            sb.Begin();
            // 等待动画完成（不阻塞 UI 线程）
            await tcs.Task;
            await Task.Delay(80); // 额外余量确保 DWM 合成
        }

        /// <summary>运行时切换适应方式：立即更新已渲染图片的 Stretch。</summary>
        public void ApplyFitMode()
        {
            if (_image == null) return;
            var (stretch, direction) = BuildImageStretch();
            _image.Stretch = stretch;
            _image.StretchDirection = direction;
        }

        public void Play() { }
        public void Pause() { }
        public void SetMuted(bool muted) { }

        private static ImageSource? LoadImage(string path, int maxPixel)
        {
            try
            {
                if (!File.Exists(path)) return null;
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(path, UriKind.Absolute);
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                if (maxPixel > 0) bmp.DecodePixelWidth = maxPixel;
                bmp.EndInit();
                bmp.Freeze(); // 跨线程安全
                return bmp;
            }
            catch (Exception ex)
            {
                Logger.Log($"[StaticFadeProvider] 加载图片失败: {ex.Message}");
                return null;
            }
        }

        public void Dispose()
        {
            _window?.Close();
            _window = null;
            _image = null;
        }
    }
}
