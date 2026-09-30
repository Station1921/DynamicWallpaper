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
    /// - 【r18 定稿时序】点击那一刻立即显示（"立即响应"是硬要求，r16 的 320ms 延迟门
    ///   会让动画在切换完成后才播，已被用户否掉），但保证一次【完整】的过渡：
    ///   淡入 140ms 迅速稳定到全不透明 → 保持 ≥360ms（自淡入完成起算，End 早到也不打断淡入）
    ///   → 260ms 淡出。任何切换（含静态图原地换图）都播完整段，全程约 500~760ms。
    ///   r16 前"松手一瞬间闪一下"的机制正是：淡入尚未走完 End 就到了，过渡层只能到达
    ///   半透明便开始淡出——半亮的层一闪而过，观感就是"闪"而不是"过渡"。
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
            public Window? Window;
            public DateTime StartedAtUtc;
            public DateTime ShownAtUtc;
            /// <summary>淡入动画真正开始（首帧渲染完成）的时刻；未开始则为 default。</summary>
            public DateTime FadeStartUtc;
            /// <summary>会话已结束、淡出流程已启动：此后绝不能再开始淡入（否则淡入会覆盖
            /// 正在进行的淡出动画，Completed 不再触发 → 过渡层永不关闭）。</summary>
            public bool Closing;
        }

        private static readonly object Gate = new();
        private static readonly Dictionary<int, Session> Sessions = new();

        /// <summary>淡入时长（r18：480→140ms）。过渡层必须"迅速而确定地出现"：
        /// 淡入过慢 → 半透明的层在屏幕上停留过久，观感从"正在切换"退化为"闪了一下"。</summary>
        private const int FadeInMs = 140;
        /// <summary>淡出时长。</summary>
        private const int FadeOutMs = 260;
        /// <summary>最短可见时长（自【淡入完成】起算）。保证任何一次切换——哪怕 100ms 就
        /// 完成的本地静态图原地换图——都有一段完整、可感知的过渡，而不是一闪而过。</summary>
        private const int MinVisibleMs = 360;

        /// <summary>开始一次切换过渡会话：**点击那一刻立即显示**（不延迟、不等就绪）。
        /// r16 曾加 320ms 延迟门以避免"松手一瞬间闪一下"，但那让本地快速切换的动画
        /// "切换都完成了才开始播"，失去意义——已被用户明确否掉。
        /// r18 改为：立即出现（一次完整过渡）+ 不打断淡入（见 End），两个诉求同时满足。</summary>
        public static void Begin(int screenIndex, Rectangle bounds)
        {
            var disp = System.Windows.Application.Current?.Dispatcher;
            if (disp == null) return;
            if (!disp.CheckAccess()) { disp.BeginInvoke(() => Begin(screenIndex, bounds)); return; }

            lock (Gate)
            {
                // 同屏已有会话（上一场尚未结束）：沿用，不重复建窗、不重置计时
                if (Sessions.ContainsKey(screenIndex)) return;
                Sessions[screenIndex] = new Session { StartedAtUtc = DateTime.UtcNow };
            }

            // Send（最高）优先级挂窗口：即使 UI 线程正忙于建窗口/初始化 WebView2，
            // 也优先把过渡层铺上屏幕——"点下去立即有反馈"。
            try
            {
                _ = disp.InvokeAsync(() => ShowCore(screenIndex, bounds),
                    System.Windows.Threading.DispatcherPriority.Send);
            }
            catch { /* 程序退出中：Dispatcher 已关闭 */ }
        }

        /// <summary>结束会话：**绝不打断淡入**——最短可见时长自"淡入完成"起算，
        /// End 早到（本地快速切换 100~300ms 即完成）时也要等淡入走完再停留、再淡出，
        /// 保证屏幕上出现的永远是一段完整过渡，而不是"半透明地闪一下"。
        /// 【r20】End 若抢在首帧淡入之前到达（静切静原地换图仅 50~90ms，ContentRendered
        /// 还没来得及触发），必须**先强制启动淡入**再计时收尾——否则过渡层全程 Opacity=0
        /// 不可见，用户看到的就是"静切静没有流光动画"（r18 的 Closing 守卫会拦住
        /// ContentRendered 的淡入，窗口静默挂 500ms 后透明关闭）。
        /// 若窗口尚未建出（同一帧内开始即结束）则直接取消，屏幕零变化。</summary>
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
                s.Closing = true;   // 关掉"首帧到达后开始淡入"的门，防止覆盖淡出动画
            }

            int elapsed = (int)(DateTime.UtcNow - s.StartedAtUtc).TotalMilliseconds;
            var w = s.Window;
            if (w == null)
            {
                Logger.Log($"[SwitchOverlay] 屏{screenIndex} 未显示过渡层：切换在 {elapsed}ms 内完成且窗口尚未建出（屏幕零变化）");
                return;
            }

            _ = disp.InvokeAsync(async () =>
            {
                // r20：淡入尚未开始（End 抢在 ContentRendered 之前到达）→ 立即强制启动淡入。
                // Closing 守卫只拦 ContentRendered/fallback 那条路径，不拦这里；
                // 且 FadeStartUtc 置位后 ContentRendered 的 StartFade 也不会重复淡入。
                if (s.FadeStartUtc == default)
                {
                    s.FadeStartUtc = DateTime.UtcNow;
                    Logger.Log($"[SwitchOverlay] 屏{screenIndex} End 抢在首帧渲染前到达（切换仅 {elapsed}ms），强制启动淡入：过渡层将完整可见");
                    try
                    {
                        var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(FadeInMs))
                        {
                            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                        };
                        w.BeginAnimation(UIElement.OpacityProperty, fadeIn);
                    }
                    catch { }
                }
                // 基准时刻取"淡入真正开始"的时刻：保证 淡入140ms + 停留360ms 完整播完再淡出
                int shown = (int)(DateTime.UtcNow - s.FadeStartUtc).TotalMilliseconds;
                int wait = Math.Max(0, FadeInMs + MinVisibleMs - shown);
                Logger.Log($"[SwitchOverlay] 屏{screenIndex} 过渡层收尾：切换共 {elapsed}ms，淡入起已 {shown}ms，再停留 {wait}ms 后 {FadeOutMs}ms 淡出（总时长约 {elapsed + wait + FadeOutMs}ms）");
                try { if (wait > 0) await Task.Delay(wait); } catch { }
                try
                {
                    var fade = new DoubleAnimation(w.Opacity, 0, TimeSpan.FromMilliseconds(FadeOutMs))
                    {
                        EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                    };
                    fade.Completed += (_, _) => { try { w.Close(); } catch { } };
                    w.BeginAnimation(UIElement.OpacityProperty, fade);
                }
                catch { try { w.Close(); } catch { } }
            });
        }

        private static void ShowCore(int screenIndex, Rectangle bounds)
        {
            lock (Gate)
            {
                // 会话已结束（同一帧内开始即结束）或本会话窗口已建出 → 不显示
                if (!Sessions.TryGetValue(screenIndex, out var s) || s.Window != null)
                    return;
                try
                {
                    var w = BuildWindow(bounds);
                    s.Window = w;
                    // 【两步显示】① 先以 Opacity=0 把窗口挂上（此时完全不可见）；
                    // ② 等首帧真正渲染完成（ContentRendered）再开始淡入。
                    // 透明分层窗口"刚 Show 出来"的那一帧若被 DWM 提前合成，屏幕上会闪过
                    // 一块未初始化的不透明内容——"松手一瞬间闪一下"的另一可能来源。
                    w.Opacity = 0;
                    s.ShownAtUtc = DateTime.UtcNow;
                    bool fadeStarted = false;
                    void StartFade()
                    {
                        if (fadeStarted) return;
                        fadeStarted = true;
                        lock (Gate)
                        {
                            // 会话已结束（End 已启动淡出）或窗口已换 → 不淡入；
                            // 否则淡入动画会覆盖正在进行的淡出，过渡层将永远留在屏幕上。
                            if (s.Closing || !ReferenceEquals(s.Window, w)) return;
                            s.FadeStartUtc = DateTime.UtcNow;
                        }
                        try
                        {
                            var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(FadeInMs))
                            {
                                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                            };
                            w.BeginAnimation(UIElement.OpacityProperty, fadeIn);
                        }
                        catch { }
                    }
                    w.ContentRendered += (_, _) => StartFade();
                    w.Show();
                    // 兜底：极端情况下 ContentRendered 不触发也必须淡入，否则流光永不出现
                    var fallback = new System.Windows.Threading.DispatcherTimer(
                        System.Windows.Threading.DispatcherPriority.Background)
                    {
                        Interval = TimeSpan.FromMilliseconds(220)
                    };
                    fallback.Tick += (_, _) => { try { fallback.Stop(); } catch { } StartFade(); };
                    fallback.Start();
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

        /// <summary>边缘光的渐隐遮罩：alpha 沿"边缘→屏幕中心"多段非线性衰减。
        /// 显式 StartPoint/EndPoint（offset 0 固定在屏幕边缘一侧）——
        /// 不用角度构造函数：实测 180°/270° 时起点落在屏幕内侧，右/下两条光带会变成硬边。</summary>
        private static System.Windows.Media.Brush CreateEdgeFadeMask(string side)
        {
            System.Windows.Point start, end;
            switch (side)
            {
                case "top": start = new System.Windows.Point(0, 0); end = new System.Windows.Point(0, 1); break;    // 贴上边缘亮 → 向下淡出
                case "bottom": start = new System.Windows.Point(0, 1); end = new System.Windows.Point(0, 0); break; // 贴下边缘亮 → 向上淡出
                case "left": start = new System.Windows.Point(0, 0); end = new System.Windows.Point(1, 0); break;   // 贴左边缘亮 → 向右淡出
                default: start = new System.Windows.Point(1, 0); end = new System.Windows.Point(0, 0); break;       // 贴右边缘亮 → 向左淡出
            }
            byte[] alphas = { 0xE0, 0x96, 0x46, 0x16, 0x00 };
            var brush = new LinearGradientBrush { StartPoint = start, EndPoint = end };
            for (int i = 0; i < alphas.Length; i++)
                brush.GradientStops.Add(new GradientStop(
                    System.Windows.Media.Color.FromArgb(alphas[i], 0, 0, 0),
                    i / (double)(alphas.Length - 1)));
            return brush;
        }
    }
}
