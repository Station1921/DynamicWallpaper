using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using DynamicWallpaper.Core;

namespace DynamicWallpaper
{
    /// <summary>
    /// 切换过渡「流光」层：壁纸切换期间在屏幕上显示一道循环扫过的斜向流光，
    /// 提示"切换中"（网络壁纸加载时间较长，纯黑屏等待容易被误认为卡死）。
    ///
    /// - 透明 + 点击穿透（WS_EX_TRANSPARENT）+ 不激活不抢焦点（WS_EX_NOACTIVATE），
    ///   不影响桌面/应用任何交互；Topmost 仅在切换的数秒内存在。
    /// - 点击"设为壁纸"即显示（不等下载/加载），140ms 淡入；最短可见 420ms 避免快切一闪而过；
    ///   切换完成后 200ms 淡出。
    /// - 按屏幕索引管理会话：SetWallpaperAsync 进入时 Begin，finally 中 End。
    /// </summary>
    public static class SwitchOverlay
    {
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_LAYERED = 0x00080000;
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int WS_EX_TOOLWINDOW = 0x00000080;

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        private sealed class Session
        {
            public System.Threading.CancellationTokenSource? DelayCts;
            public Window? Window;
            public DateTime ShownAtUtc;
        }

        private static readonly object Gate = new();
        private static readonly Dictionary<int, Session> Sessions = new();

        /// <summary>进入切换会话时立即显示流光（点击后马上有反馈）。</summary>
        private const int FadeInMs = 140;
        /// <summary>最短可见时长：极快切换也至少停留这么久，避免一闪而过的抖动感。</summary>
        private const int MinVisibleMs = 420;

        /// <summary>开始一次切换过渡会话：立即显示流光（点击"设为壁纸"就响应）。</summary>
        public static void Begin(int screenIndex, Rectangle bounds)
        {
            var disp = System.Windows.Application.Current?.Dispatcher;
            if (disp == null) return;
            if (!disp.CheckAccess()) { disp.BeginInvoke(() => Begin(screenIndex, bounds)); return; }

            lock (Gate)
            {
                // 同屏已有会话（上一场尚未结束）：复用其窗口，不重置延迟
                if (Sessions.TryGetValue(screenIndex, out var existing))
                {
                    try { existing.DelayCts?.Cancel(); } catch { }
                    existing.DelayCts = new System.Threading.CancellationTokenSource();
                    return;
                }
                var cts = new System.Threading.CancellationTokenSource();
                Sessions[screenIndex] = new Session { DelayCts = cts };
                // Send（最高）优先级：即使 UI 线程正在忙于建窗口/初始化 WebView2 的普通优先级队列，
                // 也优先把流光挂上屏幕，避免"切换完了才看到动画"
                _ = disp.InvokeAsync(() => ShowCore(screenIndex, bounds, cts.Token),
                    System.Windows.Threading.DispatcherPriority.Send);
            }
        }

        /// <summary>结束会话：取消尚未显示的流光；已显示的按最短可见时长停留后淡出关闭。</summary>
        public static void End(int screenIndex)
        {
            var disp = System.Windows.Application.Current?.Dispatcher;
            if (disp == null) return;
            if (!disp.CheckAccess()) { disp.BeginInvoke(() => End(screenIndex)); return; }

            Session? s;
            lock (Gate)
            {
                if (!Sessions.TryGetValue(screenIndex, out s)) return;
                Sessions.Remove(screenIndex);
            }
            try { s.DelayCts?.Cancel(); s.DelayCts?.Dispose(); } catch { }
            var w = s.Window;
            if (w == null) return;

            int elapsed = (int)(DateTime.UtcNow - s.ShownAtUtc).TotalMilliseconds;
            int wait = Math.Max(0, MinVisibleMs - elapsed);
            _ = disp.InvokeAsync(async () =>
            {
                try { if (wait > 0) await Task.Delay(wait); } catch { }
                try
                {
                    var fade = new DoubleAnimation(w.Opacity, 0, TimeSpan.FromMilliseconds(200))
                    {
                        EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                    };
                    fade.Completed += (_, _) => { try { w.Close(); } catch { } };
                    w.BeginAnimation(UIElement.OpacityProperty, fade);
                }
                catch { try { w.Close(); } catch { } }
            });
        }

        private static void ShowCore(int screenIndex, Rectangle bounds, System.Threading.CancellationToken ct)
        {
            lock (Gate)
            {
                // End 已先到（切换极快结束）：不再显示
                if (ct.IsCancellationRequested || !Sessions.TryGetValue(screenIndex, out var s) || s.DelayCts == null || s.Window != null)
                    return;
                try
                {
                    var w = BuildWindow(bounds);
                    w.Opacity = 0;
                    s.Window = w;
                    w.Show();
                    s.ShownAtUtc = DateTime.UtcNow;
                    var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(FadeInMs))
                    {
                        EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                    };
                    w.BeginAnimation(UIElement.OpacityProperty, fadeIn);
                }
                catch (Exception ex)
                {
                    Logger.Log($"[SwitchOverlay] 流光层显示失败: {ex.Message}");
                    s.Window = null;
                }
            }
        }

        private static Window BuildWindow(Rectangle bounds)
        {
            var w = new Window
            {
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                AllowsTransparency = true,
                Background = System.Windows.Media.Brushes.Transparent,
                Topmost = true,
                ShowInTaskbar = false,
                ShowActivated = false,
                Left = bounds.Left,
                Top = bounds.Top,
                Width = bounds.Width,
                Height = bounds.Height,
                Content = BuildVisual(bounds.Width, bounds.Height),
            };
            w.SourceInitialized += (_, _) =>
            {
                try
                {
                    var hwnd = new WindowInteropHelper(w).Handle;
                    int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
                    // 点击穿透 + 不激活 + 工具窗口（不出现在 Alt-Tab）
                    SetWindowLong(hwnd, GWL_EXSTYLE,
                        ex | WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
                }
                catch { /* 样式设置失败仅影响穿透，不影响显示 */ }
            };
            return w;
        }

        /// <summary>
        /// 屏幕边缘「内发光彩虹流光」（沿四边循环流动 + 整体呼吸明暗）
        /// + 中央弥散光晕（仿网络壁纸卡片的弥散质感）。
        /// </summary>
        private static FrameworkElement BuildVisual(double screenW, double screenH)
        {
            var grid = new Grid { ClipToBounds = true, IsHitTestVisible = false };

            // ── 中央弥散光晕：两团低透明度彩色光斑，缓慢脉动 ──
            double baseSize = Math.Max(screenW, screenH) * 0.85;
            grid.Children.Add(CreateDiffuseGlow(baseSize, System.Windows.Media.Color.FromArgb(0x44, 0x9C, 0x5C, 0xFF), 0, 0, 2400));
            grid.Children.Add(CreateDiffuseGlow(baseSize * 0.62, System.Windows.Media.Color.FromArgb(0x38, 0x40, 0x8A, 0xFF), -screenW * 0.10, screenH * 0.08, 2900));

            // ── 边缘内发光：四条向内渐隐的彩虹光带，沿边缘循环流动 ──
            double thick = Math.Max(120, Math.Min(screenW, screenH) * 0.22);
            var period = TimeSpan.FromMilliseconds(2600);
            grid.Children.Add(CreateEdge("top", thick, period));
            grid.Children.Add(CreateEdge("right", thick, period));
            grid.Children.Add(CreateEdge("bottom", thick, period));
            grid.Children.Add(CreateEdge("left", thick, period));

            // 呼吸：整体透明度缓慢起伏
            var breathe = new DoubleAnimation(0.62, 1.0, TimeSpan.FromMilliseconds(1700))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            };
            grid.BeginAnimation(UIElement.OpacityProperty, breathe);
            return grid;
        }

        /// <summary>中央弥散光斑：径向渐变（中心有色→边缘透明）+ 缓慢缩放脉动。</summary>
        private static FrameworkElement CreateDiffuseGlow(double size, System.Windows.Media.Color color,
            double offsetX, double offsetY, int pulseMs)
        {
            var scale = new ScaleTransform(1, 1);
            var ellipse = new System.Windows.Shapes.Ellipse
            {
                Width = size,
                Height = size,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = System.Windows.VerticalAlignment.Center,
                RenderTransform = new TransformGroup
                {
                    Children = { scale, new TranslateTransform(offsetX, offsetY) }
                },
                Fill = new RadialGradientBrush
                {
                    GradientStops =
                    {
                        new GradientStop(color, 0.0),
                        new GradientStop(System.Windows.Media.Color.FromArgb(0x00, color.R, color.G, color.B), 1.0),
                    }
                },
            };
            var pulse = new DoubleAnimation(1.0, 1.12, TimeSpan.FromMilliseconds(pulseMs))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, pulse);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, pulse);
            return ellipse;
        }

        // 彩虹循环色（低不透明度 + 多段渐隐，呈弥散光感而非硬色带）
        private static readonly System.Windows.Media.Color[] RainbowColors =
        {
            System.Windows.Media.Color.FromArgb(0x70, 0xFF, 0x5A, 0x76), // 红/粉
            System.Windows.Media.Color.FromArgb(0x70, 0xFF, 0xA4, 0x4E), // 橙
            System.Windows.Media.Color.FromArgb(0x70, 0xFF, 0xE3, 0x66), // 黄
            System.Windows.Media.Color.FromArgb(0x70, 0x56, 0xE1, 0x93), // 绿
            System.Windows.Media.Color.FromArgb(0x70, 0x3A, 0xDB, 0xE9), // 青
            System.Windows.Media.Color.FromArgb(0x70, 0x50, 0x92, 0xFF), // 蓝
            System.Windows.Media.Color.FromArgb(0x70, 0xB5, 0x63, 0xFF), // 紫
        };

        /// <summary>
        /// 单边内发光光带：彩虹渐变沿边缘流动（top/right 正向、bottom/left 反向 → 环绕方向一致），
        /// 内侧用 OpacityMask 渐隐，只在屏幕边缘发亮。
        /// </summary>
        private static FrameworkElement CreateEdge(string side, double thickness, TimeSpan period)
        {
            bool horizontalEdge = side == "top" || side == "bottom";
            bool forward = side == "top" || side == "right";

            var rect = new System.Windows.Shapes.Rectangle();
            if (horizontalEdge)
            {
                rect.Height = thickness;
                rect.VerticalAlignment = side == "top"
                    ? System.Windows.VerticalAlignment.Top
                    : System.Windows.VerticalAlignment.Bottom;
            }
            else
            {
                rect.Width = thickness;
                rect.HorizontalAlignment = side == "left"
                    ? System.Windows.HorizontalAlignment.Left
                    : System.Windows.HorizontalAlignment.Right;
            }

            var brush = new LinearGradientBrush
            {
                StartPoint = horizontalEdge ? new System.Windows.Point(0, 0.5) : new System.Windows.Point(0.5, 0),
                EndPoint = horizontalEdge ? new System.Windows.Point(1, 0.5) : new System.Windows.Point(0.5, 1),
                SpreadMethod = GradientSpreadMethod.Repeat,
            };
            // 一个完整彩虹周期（7 色均分），配合 Repeat + 平移一整圈实现无缝循环
            for (int i = 0; i <= 7; i++)
            {
                brush.GradientStops.Add(new GradientStop(RainbowColors[i % 7], i / 7.0));
            }
            var tt = new TranslateTransform(0, 0);
            brush.RelativeTransform = tt;
            var flow = new DoubleAnimation(0, 1.0, period)
            {
                RepeatBehavior = RepeatBehavior.Forever,
            };
            if (!forward)
            {
                flow.From = 1.0;
                flow.To = 0;
            }
            if (horizontalEdge) tt.BeginAnimation(TranslateTransform.XProperty, flow);
            else tt.BeginAnimation(TranslateTransform.YProperty, flow);
            rect.Fill = brush;

            // 内侧渐隐：非线性多段衰减（贴边最亮 → 快速变暗 → 长尾柔和），避免线性渐变看起来像硬色带
            rect.OpacityMask = CreateEdgeFadeMask(side);
            return rect;
        }

        /// <summary>边缘光的渐隐遮罩：alpha 沿"边缘→屏幕中心"多段非线性衰减。</summary>
        private static System.Windows.Media.Brush CreateEdgeFadeMask(string side)
        {
            double angle = side switch
            {
                "top" => 90.0,     // 渐变起点在屏幕边缘一侧
                "bottom" => 270.0,
                "left" => 0.0,
                _ => 180.0,
            };
            byte[] alphas = { 0xE0, 0x96, 0x46, 0x16, 0x00 };
            var brush = new LinearGradientBrush(
                System.Windows.Media.Color.FromArgb(0xFF, 0, 0, 0),
                System.Windows.Media.Color.FromArgb(0x00, 0, 0, 0), angle);
            brush.GradientStops.Clear();
            for (int i = 0; i < alphas.Length; i++)
                brush.GradientStops.Add(new GradientStop(
                    System.Windows.Media.Color.FromArgb(alphas[i], 0, 0, 0),
                    i / (double)(alphas.Length - 1)));
            return brush;
        }
    }
}
