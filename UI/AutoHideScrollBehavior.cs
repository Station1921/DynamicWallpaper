using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DynamicWallpaper.UI
{
    /// <summary>
    /// 让 ScrollViewer 的滚动条默认隐藏，仅在滚动时浮现，静止一段时间后淡出。
    /// 用法：在 ScrollViewer 上设置 local:AutoHideScrollBehavior.AutoHide="True"。
    /// </summary>
    public static class AutoHideScrollBehavior
    {
        public static readonly DependencyProperty AutoHideProperty =
            DependencyProperty.RegisterAttached(
                "AutoHide", typeof(bool), typeof(AutoHideScrollBehavior),
                new PropertyMetadata(false, OnAutoHideChanged));

        public static bool GetAutoHide(DependencyObject obj) => (bool)obj.GetValue(AutoHideProperty);
        public static void SetAutoHide(DependencyObject obj, bool value) => obj.SetValue(AutoHideProperty, value);

        private static void OnAutoHideChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is ScrollViewer sv && (bool)e.NewValue)
            {
                sv.Loaded += (_, __) => Attach(sv);
            }
        }

        private static void Attach(ScrollViewer sv)
        {
            // 静止 1 秒后淡出
            var timer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(1000)
            };
            timer.Tick += (_, __) =>
            {
                timer.Stop();
                Fade(sv, 0);
            };

            sv.ScrollChanged += (_, __) =>
            {
                // 滚动即浮现（快速淡入）
                Fade(sv, 1);
                timer.Stop();
                timer.Start();
            };

            // 初次加载先隐藏
            sv.Dispatcher.BeginInvoke(new Action(() => Fade(sv, 0)),
                System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private static void Fade(ScrollViewer sv, double to)
        {
            var vsb = FindScrollBar(sv, System.Windows.Controls.Orientation.Vertical);
            var hsb = FindScrollBar(sv, System.Windows.Controls.Orientation.Horizontal);
            var anim = new DoubleAnimation(to, TimeSpan.FromMilliseconds(to > 0 ? 150 : 400));
            if (vsb != null) vsb.BeginAnimation(UIElement.OpacityProperty, anim);
            if (hsb != null) hsb.BeginAnimation(UIElement.OpacityProperty, anim);
        }

        private static System.Windows.Controls.Primitives.ScrollBar FindScrollBar(DependencyObject parent, System.Windows.Controls.Orientation orientation)
        {
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is System.Windows.Controls.Primitives.ScrollBar sb && sb.Orientation == orientation)
                    return sb;
                var result = FindScrollBar(child, orientation);
                if (result != null) return result;
            }
            return null;
        }
    }
}
