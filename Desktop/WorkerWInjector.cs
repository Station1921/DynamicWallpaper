using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Threading;
using DynamicWallpaper.Core;
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
            Logger.Log($"[WorkerW] 开始获取 WorkerW，目标屏幕: {screenBounds}");

            // 1) 已有且仍然有效的 WorkerW 直接复用
            var existing = FindOrphanWorkerW(screenBounds);
            if (existing != IntPtr.Zero)
            {
                Logger.Log($"[WorkerW] 复用已有 WorkerW: 0x{existing.ToInt64():X}");
                return existing;
            }

            var progman = Win32.FindWindow("Progman", null);
            Logger.Log($"[WorkerW] Progman=0x{progman.ToInt64():X}");

            // 2) 向 Progman 发 0x052C 强制桌面重建/暴露 WorkerW。
            //    很多 Win10/Win11 上只有发了这条消息后壁纸层 WorkerW 才会出现。
            if (progman != IntPtr.Zero)
            {
                Win32.SendMessageTimeout(progman, Win32.WM_SPAWN_WORKER,
                    IntPtr.Zero, IntPtr.Zero,
                    Win32.SMTO_NORMAL, 1000, out _);
                Logger.Log("[WorkerW] 已发送 0x052C 触发 WorkerW 生成");
            }
            else
            {
                Logger.Log("[WorkerW] 警告：未找到 Progman");
            }

            // 3) 轮询最多 8 秒（Win11 上 WorkerW 是异步生成的，100ms 不够）。
            //    每次同时尝试多种定位策略，提高兼容性。
            for (int i = 0; i < 80; i++)
            {
                Thread.Sleep(100);

                // 策略 A：Progman 的直接子窗口里的 WorkerW（Win11 常见结构）。
                var w = FindWorkerWUnderProgman(progman);
                if (w != IntPtr.Zero)
                {
                    Logger.Log($"[WorkerW] 通过 Progman 子窗口找到: 0x{w.ToInt64():X}（第{i}次轮询）");
                    return w;
                }

                // 策略 B：经典桌面树定位。
                w = FindWallpaperWorkerWByDesktopTree();
                if (w != IntPtr.Zero)
                {
                    Logger.Log($"[WorkerW] 通过桌面树定位找到: 0x{w.ToInt64():X}（第{i}次轮询）");
                    return w;
                }

                // 策略 C：按屏幕矩形匹配所有 WorkerW（不依赖层级，兼容各种变体）。
                w = FindWorkerWByRect(screenBounds);
                if (w != IntPtr.Zero)
                {
                    Logger.Log($"[WorkerW] 通过矩形匹配找到: 0x{w.ToInt64():X}（第{i}次轮询）");
                    return w;
                }

                // 策略 D：任意不带图标的 WorkerW。
                w = FindAnyOrphanWorkerW();
                if (w != IntPtr.Zero)
                {
                    Logger.Log($"[WorkerW] 通过任意孤儿 WorkerW 找到: 0x{w.ToInt64():X}（第{i}次轮询）");
                    return w;
                }
            }

            Logger.Log("[WorkerW] 错误：所有策略均未找到 WorkerW");
            LogWorkerWState();
            return IntPtr.Zero;
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
            // 注意：不再要求 IsWindowVisible。某些 Win11 系统上 WorkerW 创建后暂时不可见，
            // 或 DWM 判定不可见，但它仍然可以承载壁纸。
            if (Win32.HasShellDefViewDescendant(hWnd)) return false;
            return true;
        }

        /// <summary>
        /// 在 Progman 的直接子窗口中查找 WorkerW（Win11 常见结构）。
        /// 返回不含 SHELLDLL_DefView 的那个 WorkerW（即壁纸层）。
        /// </summary>
        private static IntPtr FindWorkerWUnderProgman(IntPtr progman)
        {
            if (progman == IntPtr.Zero) return IntPtr.Zero;
            IntPtr child = IntPtr.Zero;
            while ((child = Win32.FindWindowEx(progman, child, "WorkerW", null)) != IntPtr.Zero)
            {
                if (!Win32.HasShellDefViewDescendant(child))
                    return child;
            }
            return IntPtr.Zero;
        }

        /// <summary>
        /// 经典 Win10/Win11 桌面树定位法：
        /// 枚举顶层窗口，找到包含 SHELLDLL_DefView（桌面图标容器）的窗口，
        /// 再取 Z 序在它下方的 WorkerW，即为壁纸层。
        /// </summary>
        private static IntPtr FindWallpaperWorkerWByDesktopTree()
        {
            IntPtr shellContainer = IntPtr.Zero;
            Win32.EnumWindows((hwnd, _) =>
            {
                if (Win32.HasShellDefViewDescendant(hwnd))
                {
                    shellContainer = hwnd;
                    return false; // 已找到，停止枚举
                }
                return true;
            }, IntPtr.Zero);

            if (shellContainer == IntPtr.Zero) return IntPtr.Zero;

            // 在 Z 序中位于 shellContainer 下方的 WorkerW 就是壁纸容器
            IntPtr candidate = Win32.FindWindowEx(IntPtr.Zero, shellContainer, "WorkerW", null);
            // 防止拿到仍然带图标的 WorkerW，继续往下找直到找到孤儿
            int guard = 0;
            while (candidate != IntPtr.Zero && !IsOrphanWorkerW(candidate) && guard++ < 16)
            {
                candidate = Win32.FindWindowEx(IntPtr.Zero, candidate, "WorkerW", null);
            }
            return IsOrphanWorkerW(candidate) ? candidate : IntPtr.Zero;
        }

        /// <summary>
        /// 按屏幕矩形中心距离匹配所有 WorkerW。
        /// 不依赖桌面树结构，兼容 Progman/顶层/子窗口各种变体。
        /// </summary>
        private static IntPtr FindWorkerWByRect(Rectangle screenBounds)
        {
            var candidates = new List<(IntPtr hwnd, double dist)>();
            Win32.EnumWindows((hwnd, _) =>
            {
                if (!IsOrphanWorkerW(hwnd)) return true;
                if (!Win32.GetWindowRect(hwnd, out Win32.RECT r)) return true;

                int wcx = r.Left + r.Width / 2;
                int wcy = r.Top + r.Height / 2;
                int cx = screenBounds.Left + screenBounds.Width / 2;
                int cy = screenBounds.Top + screenBounds.Height / 2;
                double dist = Math.Pow(wcx - cx, 2) + Math.Pow(wcy - cy, 2);
                candidates.Add((hwnd, dist));
                return true;
            }, IntPtr.Zero);

            if (candidates.Count == 0) return IntPtr.Zero;
            candidates.Sort((a, b) => a.dist.CompareTo(b.dist));
            return candidates[0].hwnd;
        }

        private static IntPtr FindOrphanWorkerW(Rectangle screenBounds)
        {
            var orphans = new List<IntPtr>();

            Win32.EnumWindows((hwnd, _) =>
            {
                if (!IsOrphanWorkerW(hwnd)) return true;
                orphans.Add(hwnd);
                return true;
            }, IntPtr.Zero);

            if (orphans.Count == 0) return IntPtr.Zero;

            // 优先：矩形完全相等
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
                int wcx = r.Left + r.Width / 2;
                int wcy = r.Top + r.Height / 2;
                bool inside = wcx >= screenBounds.Left && wcx <= screenBounds.Right &&
                              wcy >= screenBounds.Top && wcy <= screenBounds.Bottom;
                double dist = Math.Pow(wcx - cx, 2) + Math.Pow(wcy - cy, 2);
                if (inside && dist < bestDist) { best = h; bestDist = dist; }
            }
            if (best != IntPtr.Zero) return best;

            // 再兜底：返回第一个孤儿（通常即主屏）
            return orphans[0];
        }

        private static IntPtr FindAnyOrphanWorkerW()
        {
            IntPtr found = IntPtr.Zero;
            Win32.EnumWindows((hwnd, _) =>
            {
                if (IsOrphanWorkerW(hwnd))
                {
                    found = hwnd;
                    return false;
                }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        /// <summary>记录当前所有 WorkerW 的句柄、矩形、可见性、是否含图标，用于诊断。</summary>
        private static void LogWorkerWState()
        {
            Logger.Log("[WorkerW] 当前 WorkerW 状态快照:");
            Win32.EnumWindows((hwnd, _) =>
            {
                if (Win32.GetClassName(hwnd) != "WorkerW") return true;
                Win32.GetWindowRect(hwnd, out Win32.RECT r);
                bool visible = Win32.IsWindowVisible(hwnd);
                bool hasShell = Win32.HasShellDefViewDescendant(hwnd);
                Logger.Log($"  WorkerW 0x{hwnd.ToInt64():X}: rect=({r.Left},{r.Top},{r.Right},{r.Bottom}), visible={visible}, hasShell={hasShell}");
                return true;
            }, IntPtr.Zero);
        }
    }
}
