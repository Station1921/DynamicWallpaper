using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading;
using DynamicWallpaper.Core;

namespace DynamicWallpaper.Desktop
{
    /// <summary>
    /// 负责把渲染窗口挂接到 Windows 桌面壁纸层（在桌面图标之后、静态壁纸之前）。
    ///
    /// Windows 10/11 的桌面窗口树差异很大，本类采用“主策略 + 多个 fallback”的 robust 策略：
    ///
    /// 主策略（Win10 / 旧 Win11）：
    ///   向 Progman 发送 0x052C，会在桌面图标 SHELLDLL_DefView 上方生成一个空的 WorkerW，
    ///   把渲染窗口挂到该 WorkerW 内即可。
    ///
    /// Fallback A（部分 Win11）：
    ///   0x052C 没有生成新的 WorkerW，或者生成的 WorkerW 不在预期层级。
    ///   此时把渲染窗口直接挂到“包含 SHELLDLL_DefView 的那个 WorkerW”内部，并放在 DefView 下方，
    ///   这样视频就在桌面图标后面，静态壁纸前面。
    ///
    /// Fallback B（极端情况）：
    ///   把渲染窗口直接挂到 Progman，放在 DefView 容器下方。
    /// </summary>
    public static class WorkerWInjector
    {
        /// <summary>
        /// 获取/创建桌面壁纸承载窗口。返回的句柄可能是：
        ///   - 图标上方的注入层 WorkerW（最常见）
        ///   - 包含图标的 WorkerW 容器（fallback A）
        ///   - Progman（fallback B）
        /// 调用方应把此句柄作为 SetParent 的父窗口。
        /// </summary>
        public static IntPtr AcquireWorkerW(Rectangle screenBounds)
        {
            Logger.Log($"[WorkerW] 开始获取桌面壁纸承载层，目标屏幕: {screenBounds}");

            var progman = Win32.FindWindow("Progman", null);
            Logger.Log($"[WorkerW] Progman=0x{progman.ToInt64():X}");
            LogDesktopTree(progman);

            // 1) 先触发一次 0x052C（Wallpaper Engine / Lively 的标准参数 0xD）
            if (progman != IntPtr.Zero)
            {
                Win32.SendMessageTimeout(progman, Win32.WM_SPAWN_WORKER,
                    new IntPtr(0xD), IntPtr.Zero,
                    Win32.SMTO_NORMAL, 1000, out _);
                Logger.Log("[WorkerW] 已发送 0x052C (wParam=0xD)");
            }

            // 2) 轮询最多 8 秒
            for (int i = 0; i < 80; i++)
            {
                Thread.Sleep(100);

                // 策略 1：图标上方的标准注入层 WorkerW
                var w = FindWorkerWAboveDefView();
                if (w != IntPtr.Zero)
                {
                    Logger.Log($"[WorkerW] 策略1-图标上方注入层: 0x{w.ToInt64():X}（第{i}次轮询）");
                    return w;
                }

                // 策略 2：Win11 上 DefView 嵌套在 WorkerW 容器内，0x052C 又没产生新 WorkerW 时，
                // 把视频直接挂到该容器内部、DefView 下方。
                w = FindDefViewContainerWorkerW();
                if (w != IntPtr.Zero)
                {
                    Logger.Log($"[WorkerW] 策略2-DefView容器WorkerW: 0x{w.ToInt64():X}（第{i}次轮询），将挂到其内部DefView下方");
                    return w;
                }

                // 策略 3：任意匹配屏幕矩形的孤儿 WorkerW
                w = FindWorkerWByRect(screenBounds);
                if (w != IntPtr.Zero)
                {
                    Logger.Log($"[WorkerW] 策略3-矩形匹配WorkerW: 0x{w.ToInt64():X}（第{i}次轮询）");
                    return w;
                }
            }

            // 策略 4：直接挂 Progman
            if (progman != IntPtr.Zero)
            {
                Logger.Log($"[WorkerW] 策略4-兜底使用Progman: 0x{progman.ToInt64():X}");
                return progman;
            }

            Logger.Log("[WorkerW] 错误：所有策略均未找到可用桌面承载层");
            return IntPtr.Zero;
        }

        /// <summary>
        /// 将渲染子窗口挂接到父窗口，并铺满指定屏幕区域。
        /// 父窗口可能是：图标上方 WorkerW / DefView 容器 WorkerW / Progman。
        /// </summary>
        public static void Attach(IntPtr childHwnd, IntPtr parentHwnd, Rectangle bounds)
        {
            if (childHwnd == IntPtr.Zero || parentHwnd == IntPtr.Zero)
            {
                Logger.Log($"[WorkerW] Attach 失败：child=0x{childHwnd.ToInt64():X}, parent=0x{parentHwnd.ToInt64():X}");
                return;
            }

            Logger.Log($"[WorkerW] 开始 Attach: child=0x{childHwnd.ToInt64():X} -> parent=0x{parentHwnd.ToInt64():X}, bounds={bounds}");

            // 先清理父窗口中本程序旧的渲染窗口
            DetachChildren(parentHwnd);

            // 确保父窗口可见
            if (!Win32.IsWindowVisible(parentHwnd))
            {
                Logger.Log($"[WorkerW] parent 不可见，强制 ShowWindow: 0x{parentHwnd.ToInt64():X}");
                Win32.ShowWindow(parentHwnd, Win32.SW_SHOW);
            }

            Win32.SetParent(childHwnd, parentHwnd);
            Logger.Log("[WorkerW] SetParent 完成");

            // 去掉标题栏/边框/任务栏条目
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

            // 如果父窗口是 DefView 容器（含 SHELLDLL_DefView），需要把 child 放到 DefView 下方，
            // 否则 child 会盖住桌面图标。InsertAfter 用 DefView 句柄即可实现放到它后面。
            IntPtr insertAfter = Win32.HWND_BOTTOM;
            var defView = FindShellDefViewUnderParent(parentHwnd);
            if (defView != IntPtr.Zero)
            {
                insertAfter = defView;
                Logger.Log($"[WorkerW] 父窗口包含 DefView，将 child 置于 DefView 下方: 0x{defView.ToInt64():X}");
            }

            Win32.SetWindowPos(childHwnd, insertAfter,
                0, 0, bounds.Width, bounds.Height,
                Win32.SWP_NOACTIVATE | Win32.SWP_NOOWNERZORDER | Win32.SWP_SHOWWINDOW);

            // 强制刷新父窗口/桌面，避免 Win11 DWM 不立即合成
            Win32.SetWindowPos(parentHwnd, IntPtr.Zero, 0, 0, 0, 0,
                Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOZORDER | Win32.SWP_FRAMECHANGED | Win32.SWP_SHOWWINDOW);

            Logger.Log("[WorkerW] Attach 完成");
        }

        public static bool IsValid(IntPtr hWnd) => hWnd != IntPtr.Zero && Win32.IsWindow(hWnd);

        /// <summary>销毁由本程序注入到父窗口里的子窗口，避免旧壁纸残留。</summary>
        public static void DetachChildren(IntPtr parent)
        {
            if (parent == IntPtr.Zero) return;
            var currentPid = Process.GetCurrentProcess().Id;
            var children = new List<IntPtr>();
            Win32.EnumChildWindows(parent, (child, _) =>
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
            if (!IsOrphanWorkerWAllowHidden(workerw)) return;
            try { Win32.DestroyWindow(workerw); }
            catch { /* ignore */ }
        }

        #region 定位策略

        /// <summary>
        /// 策略1：找到桌面图标 SHELLDLL_DefView 所在容器，在其父窗口中按 Z 序找【上方】的不含图标 WorkerW。
        /// </summary>
        private static IntPtr FindWorkerWAboveDefView()
        {
            var container = FindDefViewContainer();
            if (container == IntPtr.Zero) return IntPtr.Zero;

            var parent = Win32.GetAncestor(container, Win32.GA_PARENT);
            if (parent == IntPtr.Zero) return IntPtr.Zero;

            var siblings = EnumChildrenZOrder(parent);
            int idx = siblings.IndexOf(container);
            if (idx <= 0) return IntPtr.Zero;

            // 从 Z 序顶部往容器走，第一个不含图标的 WorkerW 就是注入层
            for (int i = 0; i < idx; i++)
            {
                var h = siblings[i];
                if (Win32.GetClassName(h) == "WorkerW" && !Win32.HasShellDefViewDescendant(h))
                    return h;
            }
            return IntPtr.Zero;
        }

        /// <summary>
        /// 返回 SHELLDLL_DefView 所在的“容器”窗口（Win11 结构）：
        ///   即包含 SHELLDLL_DefView 的那个 WorkerW（其父为 Progman）。
        ///   Win10 返回 IntPtr.Zero，因为 Win10 的 DefView 直接挂在 Progman 下，不该作为父窗口。
        /// </summary>
        private static IntPtr FindDefViewContainerWorkerW()
        {
            var defView = FindShellDefView();
            if (defView == IntPtr.Zero) return IntPtr.Zero;

            var parent = Win32.GetAncestor(defView, Win32.GA_PARENT);
            if (parent == IntPtr.Zero) return IntPtr.Zero;

            // Win11：SHELLDLL_DefView 嵌套在 WorkerW 内
            if (Win32.GetClassName(parent) == "WorkerW")
                return parent;

            // Win10：DefView 直接挂在 Progman 下，不把它当容器返回
            return IntPtr.Zero;
        }

        /// <summary>
        /// 返回 SHELLDLL_DefView 所在的“容器”窗口：
        ///   - Win10：容器就是 SHELLDLL_DefView 本身（其父为 Progman）；
        ///   - Win11：容器是包含 SHELLDLL_DefView 的那个 WorkerW（其父为 Progman）。
        /// </summary>
        private static IntPtr FindDefViewContainer()
        {
            var defView = FindShellDefView();
            if (defView == IntPtr.Zero) return IntPtr.Zero;

            var parent = Win32.GetAncestor(defView, Win32.GA_PARENT);
            if (parent == IntPtr.Zero) return IntPtr.Zero;

            // Win11：SHELLDLL_DefView 嵌套在 WorkerW 内
            if (Win32.GetClassName(parent) == "WorkerW")
                return parent;

            // Win10：SHELLDLL_DefView 的直接父窗口是 Progman，返回 DefView 本身
            return defView;
        }

        /// <summary>
        /// 策略3：按屏幕矩形中心距离匹配所有 WorkerW。
        /// </summary>
        private static IntPtr FindWorkerWByRect(Rectangle screenBounds)
        {
            IntPtr bestVisible = IntPtr.Zero;
            double bestVisibleDist = double.MaxValue;
            IntPtr bestHidden = IntPtr.Zero;
            double bestHiddenDist = double.MaxValue;

            int cx = screenBounds.Left + screenBounds.Width / 2;
            int cy = screenBounds.Top + screenBounds.Height / 2;

            Win32.EnumWindows((hwnd, _) =>
            {
                if (Win32.GetClassName(hwnd) != "WorkerW") return true;
                if (Win32.HasShellDefViewDescendant(hwnd)) return true;
                if (!Win32.GetWindowRect(hwnd, out Win32.RECT r)) return true;

                int wcx = r.Left + r.Width / 2;
                int wcy = r.Top + r.Height / 2;
                bool inside = wcx >= screenBounds.Left && wcx <= screenBounds.Right &&
                              wcy >= screenBounds.Top && wcy <= screenBounds.Bottom;
                if (!inside) return true;

                double dist = Math.Pow(wcx - cx, 2) + Math.Pow(wcy - cy, 2);
                bool visible = Win32.IsWindowVisible(hwnd);
                if (visible && dist < bestVisibleDist)
                {
                    bestVisible = hwnd;
                    bestVisibleDist = dist;
                }
                else if (!visible && dist < bestHiddenDist)
                {
                    bestHidden = hwnd;
                    bestHiddenDist = dist;
                }
                return true;
            }, IntPtr.Zero);

            return bestVisible != IntPtr.Zero ? bestVisible : bestHidden;
        }

        /// <summary>
        /// 在父窗口下查找 SHELLDLL_DefView（用于 fallback 时决定 child 的 Z 序插入点）。
        /// </summary>
        private static IntPtr FindShellDefViewUnderParent(IntPtr parent)
        {
            if (parent == IntPtr.Zero) return IntPtr.Zero;
            // 先查直接子窗口
            IntPtr def = Win32.FindWindowEx(parent, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (def != IntPtr.Zero) return def;

            // 再深度遍历
            IntPtr found = IntPtr.Zero;
            Win32.EnumChildWindows(parent, (child, _) =>
            {
                if (Win32.GetClassName(child) == "SHELLDLL_DefView")
                {
                    found = child;
                    return false;
                }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        /// <summary>全局查找桌面图标窗口 SHELLDLL_DefView。</summary>
        private static IntPtr FindShellDefView()
        {
            IntPtr found = IntPtr.Zero;

            // 先查各顶层窗口的直接子窗口（Win10 常规结构）
            Win32.EnumWindows((hwnd, _) =>
            {
                IntPtr def = Win32.FindWindowEx(hwnd, IntPtr.Zero, "SHELLDLL_DefView", null);
                if (def != IntPtr.Zero) { found = def; return false; }
                return true;
            }, IntPtr.Zero);
            if (found != IntPtr.Zero) return found;

            // 兜底：深度遍历各顶层窗口的子窗口（Win11 嵌套结构）
            Win32.EnumWindows((hwnd, _) =>
            {
                bool inner = false;
                Win32.EnumChildWindows(hwnd, (child, _) =>
                {
                    if (Win32.GetClassName(child) == "SHELLDLL_DefView") { found = child; inner = true; return false; }
                    return true;
                }, IntPtr.Zero);
                return !inner;
            }, IntPtr.Zero);
            return found;
        }

        private static bool IsOrphanWorkerWAllowHidden(IntPtr hWnd)
        {
            if (Win32.GetClassName(hWnd) != "WorkerW") return false;
            if (Win32.HasShellDefViewDescendant(hWnd)) return false;
            return true;
        }

        /// <summary>枚举某窗口的子窗口，按 Z 序从高到低排列。</summary>
        private static List<IntPtr> EnumChildrenZOrder(IntPtr parent)
        {
            var list = new List<IntPtr>();
            IntPtr child = Win32.GetWindow(parent, Win32.GW_CHILD);
            int guard = 0;
            while (child != IntPtr.Zero && guard++ < 256)
            {
                list.Add(child);
                child = Win32.GetWindow(child, Win32.GW_HWNDNEXT);
            }
            return list;
        }

        #endregion

        #region 诊断日志

        private static void LogDesktopTree(IntPtr progman)
        {
            try
            {
                Logger.Log("[WorkerW] === 桌面窗口树快照 ===");
                if (progman == IntPtr.Zero)
                {
                    Logger.Log("[WorkerW] Progman 未找到");
                    return;
                }

                var siblings = EnumChildrenZOrder(progman);
                Logger.Log($"[WorkerW] Progman 子窗口数: {siblings.Count}");
                for (int i = 0; i < siblings.Count; i++)
                {
                    var h = siblings[i];
                    var cls = Win32.GetClassName(h);
                    Win32.GetWindowRect(h, out Win32.RECT r);
                    bool visible = Win32.IsWindowVisible(h);
                    bool hasShell = Win32.HasShellDefViewDescendant(h);
                    Logger.Log($"[WorkerW]   [{i}] 0x{h.ToInt64():X} class={cls} rect=({r.Left},{r.Top},{r.Width}x{r.Height}) visible={visible} hasShell={hasShell}");

                    if (cls == "WorkerW" || cls == "SHELLDLL_DefView")
                    {
                        // 再展开一层，便于看 Win11 嵌套
                        var children = EnumChildrenZOrder(h);
                        foreach (var c in children.Take(8))
                        {
                            var ccls = Win32.GetClassName(c);
                            Win32.GetWindowRect(c, out Win32.RECT cr);
                            Logger.Log($"[WorkerW]     -> 0x{c.ToInt64():X} class={ccls} rect=({cr.Left},{cr.Top},{cr.Width}x{cr.Height})");
                        }
                    }
                }
                Logger.Log("[WorkerW] === 快照结束 ===");
            }
            catch (Exception ex)
            {
                Logger.Log($"[WorkerW] 记录桌面树时出错: {ex.Message}");
            }
        }

        #endregion
    }
}
