using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using DynamicWallpaper.Desktop;

namespace DynamicWallpaper.Core
{
    /// <summary>
    /// 检测前台是否有全屏应用（如游戏/视频），用于自动暂停壁纸以释放资源。
    /// </summary>
    public class FullscreenMonitor
    {
        public event Action<bool>? FullscreenChanged;

        private bool _isFullscreen;
        public bool IsFullscreen => _isFullscreen;

        private readonly System.Timers.Timer _timer = new(1000);

        public FullscreenMonitor() => _timer.Elapsed += Tick;

        public void Start() => _timer.Start();
        public void Stop() => _timer.Stop();

        /// <summary>立即执行一次全屏检测（不触发状态变更事件），返回当前前台窗口是否全屏。
        /// 供 ApplyPlayState 在决定暂停/播放时使用，避免依赖定时器缓存的滞后状态。</summary>
        public bool Peek() => Detect();

        private void Tick(object? sender, EventArgs e)
        {
            bool fs = Detect();
            if (fs != _isFullscreen)
            {
                _isFullscreen = fs;
                FullscreenChanged?.Invoke(fs);
            }
        }

        private bool Detect()
        {
            IntPtr fg = Win32.GetForegroundWindow();
            if (fg == IntPtr.Zero) return false;

            Win32.GetWindowThreadProcessId(fg, out uint pid);
            if (pid == Process.GetCurrentProcess().Id) return false; // 忽略自身

            // 忽略资源管理器（explorer）：其桌面层 / 任务切换 / 任务视图等系统窗口
            // 覆盖整个屏幕是常态（实测 rect=0,0,2560,1600 全屏无边框），若参与判定会
            // 误报"全屏"→ 壁纸刚设置就被暂停，点击其他窗口前台变化后才恢复。
            try
            {
                using var fp = Process.GetProcessById((int)pid);
                if (fp.ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase)) return false;
            }
            catch { /* 进程已退出等，忽略 */ }

            if (!Win32.GetWindowRect(fg, out Win32.RECT r)) return false;

            // 无边框/无尺寸边框样式的窗口（如播放器无边框全屏、游戏独占窗口化）：
            // 这类窗口 GetWindowRect 可能与屏幕边界偏差数像素，放宽到 8px 容差。
            int style = 0;
            try { style = Win32.GetWindowLong(fg, Win32.GWL_STYLE); } catch { }

            foreach (var screen in Screen.AllScreens)
            {
                var b = screen.Bounds;

                // 真全屏：窗口与屏幕边界基本重合（容差 8px，覆盖 DWM 阴影/边框导致的
                // rect 偏差——此前 2px 容差会让带边框的全屏播放器漏检，表现为不暂停）。
                if (r.Left <= b.Left + 8 && r.Top <= b.Top + 8 &&
                    r.Right >= b.Right - 8 && r.Bottom >= b.Bottom - 8)
                {
                    LogFullscreen(fg, pid, r, style, "full8px");
                    return true;
                }

                // 无边框窗口（无标题栏/无尺寸边框）：只有覆盖接近整个屏幕（97%+，任务栏
                // 隐藏/游戏独占全屏形态）才视为全屏。此前 85% 阈值过宽——普通无边框应用
                // （如 Qt 自绘标题栏的聊天窗口）最大化后覆盖约 93% 也会被误判成"全屏播放器"，
                // 导致壁纸设置后无故暂停、点击其他窗口才恢复。
                bool noCaption = (style & Win32.WS_CAPTION) == 0;
                bool noThickFrame = (style & Win32.WS_THICKFRAME) == 0;
                long overlapW = Math.Max(0, Math.Min(r.Right, b.Right) - Math.Max(r.Left, b.Left));
                long overlapH = Math.Max(0, Math.Min(r.Bottom, b.Bottom) - Math.Max(r.Top, b.Top));
                long screenArea = (long)b.Width * b.Height;
                if (noCaption && noThickFrame && screenArea > 0 &&
                    overlapW * overlapH * 100 >= screenArea * 97)
                {
                    LogFullscreen(fg, pid, r, style, "noborder97");
                    return true;
                }
            }
            return false;
        }

        private void LogFullscreen(IntPtr hwnd, uint pid, Win32.RECT r, int style, string reason)
        {
            try
            {
                string title = "";
                var p = Process.GetProcessById((int)pid);
                string name = p.ProcessName;
                try { title = p.MainWindowTitle; } catch { }
                Logger.Log($"[FullscreenMonitor] 判定全屏({reason}) PID={pid} 进程={name} 标题={title} rect={r.Left},{r.Top},{r.Right},{r.Bottom} style=0x{style:X}");
            }
            catch { /* 进程已退出等，忽略 */ }
        }
    }
}
