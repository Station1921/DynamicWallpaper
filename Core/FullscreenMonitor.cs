using System;
using System.Diagnostics;
using System.Drawing;
using System.Text;
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

            // 不可见/最小化窗口不可能是真正占用屏幕的全屏应用
            if (!Win32.IsWindowVisible(fg)) return false;
            if (Win32.IsIconic(fg)) return false;

            Win32.GetWindowThreadProcessId(fg, out uint pid);
            if (pid == Process.GetCurrentProcess().Id) return false; // 忽略自身

            // 忽略系统 Shell / 桌面 / 任务栏 / 开始菜单等窗口
            string className = Win32.GetClassName(fg);
            if (IsShellWindowClass(className)) return false;

            if (!Win32.GetWindowRect(fg, out Win32.RECT r)) return false;

            foreach (var screen in Screen.AllScreens)
            {
                var b = screen.Bounds;
                if (r.Left <= b.Left + 2 && r.Top <= b.Top + 2 &&
                    r.Right >= b.Right - 2 && r.Bottom >= b.Bottom - 2)
                {
                    // 典型全屏应用/游戏是 WS_POPUP 且没有标题栏/可调边框；
                    // 普通应用最大化后仍有 WS_CAPTION + WS_THICKFRAME，不应视为全屏。
                    int style = Win32.GetWindowLong(fg, Win32.GWL_STYLE);
                    bool hasCaption = (style & Win32.WS_CAPTION) != 0;
                    bool hasThickFrame = (style & Win32.WS_THICKFRAME) != 0;
                    if (hasCaption && hasThickFrame)
                    {
                        Logger.Log($"[FullscreenMonitor] 前台窗口覆盖全屏但为普通最大化应用（有标题栏），不视为全屏: class={className}, pid={pid}");
                        return false;
                    }

                    string procName = GetProcessName(pid);
                    Logger.Log($"[FullscreenMonitor] 检测到前台全屏应用: class={className}, process={procName}, pid={pid}");
                    return true;
                }
            }
            return false;
        }

        private static bool IsShellWindowClass(string className)
        {
            // 这些窗口可能覆盖整个屏幕（如资源管理器桌面、任务栏、开始菜单、搜索、UWP 宿主等），
            // 但都不是需要暂停壁纸的"全屏应用"。
            return className is "Progman"
                or "WorkerW"
                or "Shell_TrayWnd"
                or "Shell_SecondaryTrayWnd"
                or "SHELLDLL_DefView"
                or "DV2ControlHost"
                or "Windows.UI.Core.CoreWindow"
                or "SearchBox"
                or "XamlExplorerHostIslandWindow"
                or "WindowsDashboard";
        }

        private static string GetProcessName(uint pid)
        {
            try
            {
                using var p = Process.GetProcessById((int)pid);
                return p.ProcessName;
            }
            catch { return ""; }
        }
    }
}
