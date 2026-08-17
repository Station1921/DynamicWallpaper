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

            if (!Win32.GetWindowRect(fg, out Win32.RECT r)) return false;

            foreach (var screen in Screen.AllScreens)
            {
                var b = screen.Bounds;
                if (r.Left <= b.Left + 2 && r.Top <= b.Top + 2 &&
                    r.Right >= b.Right - 2 && r.Bottom >= b.Bottom - 2)
                {
                    return true;
                }
            }
            return false;
        }
    }
}
