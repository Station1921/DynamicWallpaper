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
    /// - 延迟 600ms 才出现：本地壁纸切换通常 1s 内完成，不闪流光；
    ///   只有网络壁纸等慢切换才会看到。
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
        }

        private static readonly object Gate = new();
        private static readonly Dictionary<int, Session> Sessions = new();

        /// <summary>开始一次切换过渡会话。600ms 后仍未结束才显示流光（快速切换无感）。</summary>
        public static void Begin(int screenIndex, Rectangle bounds)
        {
            var disp = System.Windows.Application.Current?.Dispatcher;
            if (disp == null) return;
            if (!disp.CheckAccess()) { disp.BeginInvoke(() => Begin(screenIndex, bounds)); return; }

            lock (Gate)
            {
                // 同屏已有会话（上一场尚未结束）：复用其窗口，仅重置延迟
                if (Sessions.TryGetValue(screenIndex, out var existing))
                {
                    try { existing.DelayCts?.Cancel(); } catch { }
                    existing.DelayCts = new System.Threading.CancellationTokenSource();
                    return;
                }
                var cts = new System.Threading.CancellationTokenSource();
                Sessions[screenIndex] = new Session { DelayCts = cts };
                _ = disp.InvokeAsync(async () =>
                {
                    try { await Task.Delay(600, cts.Token); }
                    catch (OperationCanceledException) { return; }
                    ShowCore(screenIndex, bounds, cts.Token);
                });
            }
        }

        /// <summary>结束会话：取消延迟显示；已显示的流光淡出后关闭。</summary>
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
            try
            {
                var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(220))
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                };
                fade.Completed += (_, _) => { try { w.Close(); } catch { } };
                w.BeginAnimation(UIElement.OpacityProperty, fade);
            }
            catch { try { w.Close(); } catch { } }
        }

        private static void ShowCore(int screenIndex, Rectangle bounds, System.Threading.CancellationToken ct)
        {
            lock (Gate)
            {
                // End 已先到（快速切换）：不再显示
                if (ct.IsCancellationRequested || !Sessions.TryGetValue(screenIndex, out var s) || s.DelayCts == null || s.Window != null)
                    return;
                try
                {
                    s.Window = BuildWindow(bounds);
                    s.Window.Show();
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

        /// <summary>两道不同速度/亮度、反向倾斜的流光，扫过全屏（超出部分被 Grid 裁剪）。</summary>
        private static FrameworkElement BuildVisual(double screenW, double screenH)
        {
            double diag = Math.Sqrt(screenW * screenW + screenH * screenH);

            var grid = new Grid { ClipToBounds = true, IsHitTestVisible = false };

            var band = CreateStreak(diag, Math.Max(140, diag * 0.16), -16, 0x5A, 0x90, TimeSpan.FromMilliseconds(1700), 0.0);
            var thin = CreateStreak(diag, Math.Max(60, diag * 0.06), -16, 0x40, 0xFF, TimeSpan.FromMilliseconds(1700), -120);
            grid.Children.Add(band);
            grid.Children.Add(thin);
            return grid;
        }

        private static FrameworkElement CreateStreak(double diag, double thickness, double angle,
            byte midAlpha, byte coreAlpha, TimeSpan period, double phaseOffset)
        {
            var rect = new System.Windows.Shapes.Rectangle
            {
                Width = diag,
                Height = thickness,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = System.Windows.VerticalAlignment.Center,
                RenderTransformOrigin = new System.Windows.Point(0.5, 0.5),
                RenderTransform = new TransformGroup
                {
                    Children =
                    {
                        new RotateTransform(angle),
                        new TranslateTransform(phaseOffset, 0),
                    }
                },
            };
            var brush = new LinearGradientBrush
            {
                StartPoint = new System.Windows.Point(0, 0.5),
                EndPoint = new System.Windows.Point(1, 0.5),
                GradientStops =
                {
                    new GradientStop(System.Windows.Media.Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 0.0),
                    new GradientStop(System.Windows.Media.Color.FromArgb(midAlpha, 0xE8, 0xF2, 0xFF), 0.40),
                    new GradientStop(System.Windows.Media.Color.FromArgb(coreAlpha, 0xFF, 0xFF, 0xFF), 0.50),
                    new GradientStop(System.Windows.Media.Color.FromArgb(midAlpha, 0xE8, 0xF2, 0xFF), 0.60),
                    new GradientStop(System.Windows.Media.Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 1.0),
                }
            };
            brush.Freeze();
            rect.Fill = brush;

            if (((TransformGroup)rect.RenderTransform).Children[1] is TranslateTransform tt)
            {
                var anim = new DoubleAnimation(-diag, diag, period)
                {
                    RepeatBehavior = RepeatBehavior.Forever,
                };
                tt.BeginAnimation(TranslateTransform.XProperty, anim);
            }
            return rect;
        }
    }
}
