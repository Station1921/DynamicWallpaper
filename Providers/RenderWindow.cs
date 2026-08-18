using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Brushes = System.Windows.Media.Brushes;

namespace DynamicWallpaper.Providers
{
    /// <summary>
    /// 无边框、不出现在任务栏的渲染宿主窗口，渲染内容挂接到此窗口后整体塞进 WorkerW。
    /// </summary>
    public class RenderWindow : Window
    {
        public Grid RootGrid { get; }

        // 目标屏幕矩形（设备像素）。WPF 的 Window.Width/Height 是逻辑单位（1/96 英寸），
        // 必须按当前 DPI 缩放比例换算后赋值，否则在 HiDPI 下窗口会被 WPF 放大，
        // 随后被 WorkerWInjector 用 SetWindowPos 硬裁回屏幕物理尺寸，导致只显示左上角，
        // 宽屏视频出现“文字偏右/靠左播放”的现象。
        private Rectangle _deviceBounds;

        public RenderWindow()
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            // 透明背景：内容（视频/图片）未铺满或正在卸载时，露出的不是黑块，
            // 而是下方透过来的系统静态壁纸，避免“解除时黑屏闪一下”。
            Background = Brushes.Transparent;
            RootGrid = new Grid();
            Content = RootGrid;
            Width = 1920;
            Height = 1080;

            // 窗口 DPI 变化时（如被拖到另一台 DPI 不同的显示器）重新按新比例换算尺寸
            DpiChanged += (_, _) => ApplyBounds();

            // 辅助措施：尝试把该窗口的 DPI 感知上下文设为 UNAWARE。
            // 若成功，WPF 逻辑单位与设备像素 1:1，可进一步避免缩放误差；
            // 失败也不影响，因为 ApplyBounds 已按实际 DPI 比例做了换算。
            SourceInitialized += (_, _) =>
            {
                try
                {
                    var hwnd = new WindowInteropHelper(this).Handle;
                    if (hwnd != IntPtr.Zero)
                        SetWindowDpiAwarenessContext(hwnd, DpiAwarenessContextUnaware);
                }
                catch { /* 个别环境不支持时忽略 */ }
            };
        }

        /// <summary>
        /// 按屏幕设备像素设置窗口位置与大小（内部自动转换为 WPF 逻辑单位）。
        /// </summary>
        public void SetDeviceBounds(Rectangle bounds)
        {
            _deviceBounds = bounds;
            // 确保 HWND 与 PresentationSource 已创建，才能读到 WPF 当前 DPI 比例
            new WindowInteropHelper(this).EnsureHandle();
            ApplyBounds();
        }

        private void ApplyBounds()
        {
            if (_deviceBounds.IsEmpty) return;

            var scale = GetDpiScale();
            Left = _deviceBounds.Left / scale.X;
            Top = _deviceBounds.Top / scale.Y;
            Width = _deviceBounds.Width / scale.X;
            Height = _deviceBounds.Height / scale.Y;
        }

        /// <summary>获取 WPF 当前用于该窗口的逻辑单位→设备像素的缩放比例。</summary>
        private Vector GetDpiScale()
        {
            var source = PresentationSource.FromVisual(this);
            if (source?.CompositionTarget != null)
            {
                var m = source.CompositionTarget.TransformToDevice;
                return new Vector(m.M11, m.M22);
            }
            return new Vector(1, 1);
        }

        // DPI_AWARENESS_CONTEXT_UNAWARE = (HANDLE)-1
        private static readonly IntPtr DpiAwarenessContextUnaware = new IntPtr(-1);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowDpiAwarenessContext(IntPtr hwnd, IntPtr dpiContext);
    }
}
