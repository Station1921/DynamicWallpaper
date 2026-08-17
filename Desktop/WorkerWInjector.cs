using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using DynamicWallpaper.Desktop;

namespace DynamicWallpaper.Desktop
{
    /// <summary>
    /// 负责把渲染窗口挂接到 Windows 桌面的 WorkerW 容器层（图标之后、静态壁纸之前）。
    /// </summary>
    public static class WorkerWInjector
    {
        /// <summary>
        /// 获取/创建与指定屏幕对应的“孤儿” WorkerW 窗口句柄。
        /// 孤儿 WorkerW 即不含 SHELLDLL_DefView（桌面图标）的那个，位于图标背后。
        /// </summary>
        public static IntPtr AcquireWorkerW(Rectangle screenBounds)
        {
            var existing = FindOrphanWorkerW(screenBounds);
            if (existing != IntPtr.Zero) return existing;

            // 触发 Windows 生成 WorkerW
            var progman = Win32.FindWindow("Progman", null);
            if (progman != IntPtr.Zero)
            {
                Win32.SendMessageTimeout(progman, Win32.WM_SPAWN_WORKER,
                    IntPtr.Zero, IntPtr.Zero,
                    Win32.SMTO_NORMAL, 1000, out _);
            }

            return FindOrphanWorkerW(screenBounds);
        }

        /// <summary>
        /// 将渲染子窗口挂接到 WorkerW，并铺满指定屏幕区域。
        /// </summary>
        public static void Attach(IntPtr childHwnd, IntPtr workerwHwnd, Rectangle bounds)
        {
            if (childHwnd == IntPtr.Zero || workerwHwnd == IntPtr.Zero) return;

            // 先清理该 WorkerW 上本程序旧的渲染窗口，防止叠加成“两张壁纸”
            DetachChildren(workerwHwnd);

            Win32.SetParent(childHwnd, workerwHwnd);

            // 去掉标题栏/边框/任务栏条目，避免抢焦点与 Alt+Tab 露出
            int style = Win32.GetWindowLong(childHwnd, Win32.GWL_STYLE);
            style = (style | Win32.WS_CHILD | Win32.WS_VISIBLE)
                    & ~Win32.WS_CAPTION & ~Win32.WS_THICKFRAME
                    & ~Win32.WS_SYSMENU & ~Win32.WS_MINIMIZEBOX & ~Win32.WS_MAXIMIZEBOX;
            Win32.SetWindowLong(childHwnd, Win32.GWL_STYLE, style);
            Win32.SetWindowPos(childHwnd, IntPtr.Zero, 0, 0, 0, 0,
                Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOZORDER | Win32.SWP_FRAMECHANGED);

            int exStyle = Win32.GetWindowLong(childHwnd, Win32.GWL_EXSTYLE);
            exStyle = (exStyle | Win32.WS_EX_NOACTIVATE | Win32.WS_EX_TOOLWINDOW)
                      & ~Win32.WS_EX_APPWINDOW;
            Win32.SetWindowLong(childHwnd, Win32.GWL_EXSTYLE, exStyle);

            Win32.SetWindowPos(childHwnd, Win32.HWND_BOTTOM,
                0, 0, bounds.Width, bounds.Height,
                Win32.SWP_NOACTIVATE | Win32.SWP_NOZORDER | Win32.SWP_NOOWNERZORDER | Win32.SWP_SHOWWINDOW);
        }

        public static bool IsValid(IntPtr hWnd) => hWnd != IntPtr.Zero && Win32.IsWindow(hWnd);

        /// <summary>销毁由本程序注入到 WorkerW 里的子窗口，避免旧壁纸残留造成“两张一样”。</summary>
        public static void DetachChildren(IntPtr workerw)
        {
            if (workerw == IntPtr.Zero) return;
            var currentPid = Process.GetCurrentProcess().Id;
            var children = new List<IntPtr>();
            Win32.EnumChildWindows(workerw, (child, _) =>
            {
                Win32.GetWindowThreadProcessId(child, out uint pid);
                if ((int)pid == currentPid)
                    children.Add(child);
                return true;
            }, IntPtr.Zero);

            foreach (var child in children)
            {
                try
                {
                    Win32.SetParent(child, IntPtr.Zero);
                    Win32.DestroyWindow(child);
                }
                catch { /* ignore */ }
            }
        }

        /// <summary>安全销毁一个孤儿 WorkerW 窗口（仅用于程序自己生成/占用的 WorkerW）。</summary>
        public static void DestroyWorkerW(IntPtr workerw)
        {
            if (workerw == IntPtr.Zero) return;
            if (!IsOrphanWorkerW(workerw)) return; // 安全措施：只销毁确认不带图标的孤儿
            try { Win32.DestroyWindow(workerw); }
            catch { /* ignore */ }
        }

        private static bool IsOrphanWorkerW(IntPtr hWnd)
        {
            if (Win32.GetClassName(hWnd) != "WorkerW") return false;
            if (!Win32.IsWindowVisible(hWnd)) return false;
            if (Win32.HasShellDefViewDescendant(hWnd)) return false;
            return true;
        }

        private static IntPtr FindOrphanWorkerW(Rectangle screenBounds)
        {
            var orphans = new List<IntPtr>();
            var currentPid = Process.GetCurrentProcess().Id;

            Win32.EnumWindows((hwnd, _) =>
            {
                if (!IsOrphanWorkerW(hwnd)) return true;
                orphans.Add(hwnd);
                return true;
            }, IntPtr.Zero);

            if (orphans.Count == 0) return IntPtr.Zero;

            // 优先：矩形完全相等且没有本进程子窗口（更干净）
            foreach (var h in orphans)
            {
                if (HasOurChild(h, currentPid)) continue;
                if (Win32.GetWindowRect(h, out Win32.RECT r) &&
                    r.Left == screenBounds.Left && r.Top == screenBounds.Top &&
                    r.Right == screenBounds.Right && r.Bottom == screenBounds.Bottom)
                {
                    return h;
                }
            }

            // 次优先：矩形完全相等（即使有我们旧的子窗口，Attach 时会清理）
            foreach (var h in orphans)
            {
                if (Win32.GetWindowRect(h, out Win32.RECT r) &&
                    r.Left == screenBounds.Left && r.Top == screenBounds.Top &&
                    r.Right == screenBounds.Right && r.Bottom == screenBounds.Bottom)
                {
                    return h;
                }
            }

            // 兜底：取中心点落在屏幕内、且与屏幕中心最近者（兼容 DPI 虚拟化导致的尺寸微差）
            int cx = screenBounds.Left + screenBounds.Width / 2;
            int cy = screenBounds.Top + screenBounds.Height / 2;
            IntPtr best = IntPtr.Zero;
            double bestDist = double.MaxValue;
            foreach (var h in orphans)
            {
                if (!Win32.GetWindowRect(h, out Win32.RECT r)) continue;
                int wcx = r.Left + (r.Right - r.Left) / 2;
                int wcy = r.Top + (r.Bottom - r.Top) / 2;
                bool inside = wcx >= screenBounds.Left && wcx <= screenBounds.Right &&
                              wcy >= screenBounds.Top && wcy <= screenBounds.Bottom;
                double dist = Math.Pow(wcx - cx, 2) + Math.Pow(wcy - cy, 2);
                if (inside && dist < bestDist) { best = h; bestDist = dist; }
            }
            if (best != IntPtr.Zero) return best;

            // 再兜底：返回第一个孤儿（通常即主屏）
            return orphans[0];
        }

        private static bool HasOurChild(IntPtr workerw, int currentPid)
        {
            bool found = false;
            Win32.EnumChildWindows(workerw, (child, _) =>
            {
                Win32.GetWindowThreadProcessId(child, out uint pid);
                if ((int)pid == currentPid)
                {
                    found = true;
                    return false;
                }
                return true;
            }, IntPtr.Zero);
            return found;
        }
    }
}
