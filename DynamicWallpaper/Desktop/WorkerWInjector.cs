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
    /// 负责把渲染窗口挂接到 Windows 桌面的 WorkerW 容器层（图标之后、静态壁纸之前）。
    ///
    /// 关键点：桌面窗口树里通常“同时”存在两个 WorkerW：
    ///   1) 显示【静态系统壁纸】的底层 WorkerW（在桌面图标 SHELLDLL_DefView 之下）；
    ///   2) 由 Progman 的 0x052C 消息触发生成的“空” WorkerW（在 SHELLDLL_DefView 之上），
    ///      它就是用来承载动态壁纸的注入层。
    /// 若把动态壁纸挂到第 1 个（底层）WorkerW，会被静态壁纸完全盖住 → 表现为“设置了没反应”。
    /// 因此本类始终优先定位“位于桌面图标【上方】”的那个 WorkerW。
    ///
    /// Win11 与 Win10 的桌面树差异：
    ///   - Win10：SHELLDLL_DefView 通常是 Progman 的直接子窗口；
    ///   - Win11：SHELLDLL_DefView 经常嵌套在一个 WorkerW 内部，该 WorkerW 再作为 Progman 的子窗口。
    /// 因此不能简单地用 GetWindow(defView, GW_HWNDPREV)（它只返回同父窗口的兄弟），
    /// 而应以“SHELLDLL_DefView 所在的容器”为基准，在其父窗口的所有子窗口中按 Z 序找上方的 WorkerW。
    /// </summary>
    public static class WorkerWInjector
    {
        /// <summary>
        /// 获取/创建与指定屏幕对应的“注入层” WorkerW 句柄（即桌面图标上方的那个）。
        /// </summary>
        public static IntPtr AcquireWorkerW(Rectangle screenBounds)
        {
            Logger.Log($"[WorkerW] 开始获取 WorkerW（注入层），目标屏幕: {screenBounds}");

            // 1) 优先复用已存在的注入层，避免每次都重新触发 0x052C 造成 WorkerW 堆积
            var existing = FindInjectionWorkerW();
            if (existing != IntPtr.Zero)
            {
                Logger.Log($"[WorkerW] 复用已有注入层 WorkerW: 0x{existing.ToInt64():X}（aboveIcons={IsAboveDefView(existing)}）");
                return existing;
            }

            var progman = Win32.FindWindow("Progman", null);
            Logger.Log($"[WorkerW] Progman=0x{progman.ToInt64():X}");

            // 2) 向 Progman 发 0x052C 强制在桌面图标上方生成/暴露注入层 WorkerW
            //    参数 0xD 是 Wallpaper Engine / Lively 等主流实现使用的标准值。
            if (progman != IntPtr.Zero)
            {
                Win32.SendMessageTimeout(progman, Win32.WM_SPAWN_WORKER,
                    new IntPtr(0xD), IntPtr.Zero,
                    Win32.SMTO_NORMAL, 1000, out _);
                Logger.Log("[WorkerW] 已发送 0x052C (wParam=0xD) 触发注入层 WorkerW 生成");
            }
            else
            {
                Logger.Log("[WorkerW] 警告：未找到 Progman");
            }

            // 3) 轮询最多 8 秒（Win11 上 WorkerW 是异步生成的，100ms 不够）
            for (int i = 0; i < 80; i++)
            {
                Thread.Sleep(100);

                // 策略 A（主）：桌面图标容器上方的 WorkerW —— 即正确的注入层
                var w = FindInjectionWorkerW();
                if (w != IntPtr.Zero)
                {
                    Logger.Log($"[WorkerW] 通过图标上方定位找到注入层: 0x{w.ToInt64():X}（第{i}次轮询）");
                    return w;
                }

                // 策略 B：Progman 的直接子窗口里、位于图标容器上方的 WorkerW（Win11 常见结构）
                w = FindWorkerWAboveDefViewContainer(progman);
                if (w != IntPtr.Zero)
                {
                    Logger.Log($"[WorkerW] 通过 Progman 子窗口定位到图标上方 WorkerW: 0x{w.ToInt64():X}（第{i}次轮询）");
                    return w;
                }

                // 策略 C：按屏幕矩形匹配所有 WorkerW（不依赖层级，兼容各种变体）
                w = FindWorkerWByRect(screenBounds);
                if (w != IntPtr.Zero)
                {
                    Logger.Log($"[WorkerW] 通过矩形匹配找到: 0x{w.ToInt64():X}（第{i}次轮询）");
                    return w;
                }

                // 策略 D（最后兜底）：任意孤儿 WorkerW（含隐藏），用于诊断
                w = FindAnyOrphanWorkerWAllowHidden();
                if (w != IntPtr.Zero)
                {
                    Logger.Log($"[WorkerW] 通过任意孤儿 WorkerW 找到(兜底): 0x{w.ToInt64():X}（第{i}次轮询），将强制显示");
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
            if (childHwnd == IntPtr.Zero || workerwHwnd == IntPtr.Zero)
            {
                Logger.Log($"[WorkerW] Attach 失败：child=0x{childHwnd.ToInt64():X}, workerw=0x{workerwHwnd.ToInt64():X}");
                return;
            }

            Logger.Log($"[WorkerW] 开始 Attach: child=0x{childHwnd.ToInt64():X} -> workerw=0x{workerwHwnd.ToInt64():X}, bounds={bounds}");

            // 先清理该 WorkerW 上本程序旧的渲染窗口，防止叠加成“两张壁纸”
            DetachChildren(workerwHwnd);

            // 确保 WorkerW 本身可见，否则子窗口无法显示出来
            if (!Win32.IsWindowVisible(workerwHwnd))
            {
                Logger.Log($"[WorkerW] Attach 前 WorkerW 不可见，强制 ShowWindow: 0x{workerwHwnd.ToInt64():X}");
                Win32.ShowWindow(workerwHwnd, Win32.SW_SHOW);
            }

            Win32.SetParent(childHwnd, workerwHwnd);
            Logger.Log($"[WorkerW] SetParent 完成");

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

            Logger.Log($"[WorkerW] Attach 完成，子窗口已置于 WorkerW 内");
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

        /// <summary>
        /// 定位“注入层” WorkerW：找到桌面图标 SHELLDLL_DefView 所在的容器，
        /// 在其父窗口的所有子窗口中按 Z 序找位于该容器【上方】且不含图标的 WorkerW。
        /// </summary>
        private static IntPtr FindInjectionWorkerW()
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

            // Win11：SHELLDLL_DefView 嵌套在 WorkerW 内，返回这个 WorkerW
            if (Win32.GetClassName(parent) == "WorkerW")
                return parent;

            // Win10：SHELLDLL_DefView 的直接父窗口是 Progman，返回 DefView 本身
            return defView;
        }

        /// <summary>判断某 WorkerW 是否位于桌面图标（SHELLDLL_DefView）上方（即正确的注入层）。</summary>
        private static bool IsAboveDefView(IntPtr workerw)
        {
            var container = FindDefViewContainer();
            if (container == IntPtr.Zero || workerw == IntPtr.Zero) return false;

            var parent = Win32.GetAncestor(container, Win32.GA_PARENT);
            if (parent == IntPtr.Zero) return false;

            var siblings = EnumChildrenZOrder(parent);
            int idx = siblings.IndexOf(container);
            if (idx <= 0) return false;

            for (int i = 0; i < idx; i++)
                if (siblings[i] == workerw) return true;

            return false;
        }

        /// <summary>
        /// 在 Progman 的直接子窗口中查找位于图标容器上方的 WorkerW（Win11 常见结构）。
        /// </summary>
        private static IntPtr FindWorkerWAboveDefViewContainer(IntPtr progman)
        {
            if (progman == IntPtr.Zero) return IntPtr.Zero;

            var container = FindDefViewContainer();
            if (container == IntPtr.Zero) return IntPtr.Zero;

            var siblings = EnumChildrenZOrder(progman);
            int idx = siblings.IndexOf(container);
            if (idx <= 0) return IntPtr.Zero;

            for (int i = 0; i < idx; i++)
            {
                var h = siblings[i];
                if (Win32.GetClassName(h) == "WorkerW" && !Win32.HasShellDefViewDescendant(h))
                    return h;
            }
            return IntPtr.Zero;
        }

        /// <summary>
        /// 按屏幕矩形中心距离匹配所有 WorkerW。
        /// 仅作为兜底，优先使用 FindInjectionWorkerW。
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

        private static IntPtr FindAnyOrphanWorkerWAllowHidden()
        {
            IntPtr found = IntPtr.Zero;
            Win32.EnumWindows((hwnd, _) =>
            {
                if (IsOrphanWorkerWAllowHidden(hwnd))
                {
                    found = hwnd;
                    return false;
                }
                return true;
            }, IntPtr.Zero);
            return found;
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

        /// <summary>查找桌面图标窗口 SHELLDLL_DefView（全局枚举，兼容其嵌套层级差异）。</summary>
        private static IntPtr FindShellDefView()
        {
            // 先查各顶层窗口的直接子窗口（常规结构下 SHELLDLL_DefView 是 Progman 的直接子窗口）
            IntPtr found = IntPtr.Zero;
            Win32.EnumWindows((hwnd, _) =>
            {
                IntPtr def = Win32.FindWindowEx(hwnd, IntPtr.Zero, "SHELLDLL_DefView", null);
                if (def != IntPtr.Zero) { found = def; return false; }
                return true;
            }, IntPtr.Zero);
            if (found != IntPtr.Zero) return found;

            // 兜底：深度遍历各顶层窗口的子窗口
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

        /// <summary>判断是否为可见的、可用于壁纸的 WorkerW。</summary>
        private static bool IsOrphanWorkerW(IntPtr hWnd)
        {
            if (Win32.GetClassName(hWnd) != "WorkerW") return false;
            if (!Win32.IsWindowVisible(hWnd)) return false;
            if (Win32.HasShellDefViewDescendant(hWnd)) return false;
            return true;
        }

        /// <summary>判断是否为 WorkerW 且不含桌面图标（允许隐藏，仅作为最后兜底）。</summary>
        private static bool IsOrphanWorkerWAllowHidden(IntPtr hWnd)
        {
            if (Win32.GetClassName(hWnd) != "WorkerW") return false;
            if (Win32.HasShellDefViewDescendant(hWnd)) return false;
            return true;
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
                bool above = IsAboveDefView(hwnd);
                Logger.Log($"  WorkerW 0x{hwnd.ToInt64():X}: rect=({r.Left},{r.Top},{r.Right},{r.Bottom}), visible={visible}, hasShell={hasShell}, aboveIcons={above}");
                return true;
            }, IntPtr.Zero);
        }
    }
}
