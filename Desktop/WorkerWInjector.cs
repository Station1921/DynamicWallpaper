using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading;
using DynamicWallpaper.Core;
using DynamicWallpaper.Providers;

namespace DynamicWallpaper.Desktop
{
    /// <summary>
    /// 把渲染窗口挂到 Windows 桌面壁纸层。
    ///
    /// 兼容策略（Win10 → Win11 25H2）：
    ///   1. 给 Progman 发送 0x052C，让它生成一个“孤儿 WorkerW”（即壁纸承载层）。
    ///   2. Lively 方式（优先，Win11 24H2/25H2 有效）：枚举顶层窗口，找子窗口 class 为
    ///      SHELLDLL_DefView 的窗口，再 FindWindowEx 取其后继的“活动 WorkerW”。
    ///   3. 经典路径(Win10)：找到“包含 SHELLDLL_DefView 的 WorkerW”，其 Z 序之后
    ///      的那个 WorkerW 即为 0x052C 生成的孤儿壁纸层。
    ///   4. Win11 路径：SHELLDLL_DefView 是 Progman 的直接子窗口，壁纸层是它的兄弟 WorkerW
    ///      （仅接受 0x052C 发送后新增的孤儿 WorkerW，快照 diff 作为回退）。
    ///   5. Win11 24H2/25H2 raised desktop 核心：Progman 带 WS_EX_NOREDIRECTIONBITMAP 时，
    ///      真正承载桌面的是 Progman 本身，壁纸窗口必须 SetParent 到 Progman、
    ///      设置 WS_EX_LAYERED 并 SetLayeredWindowAttributes(alpha=255)（DWM 只合成
    ///      WS_EX_LAYERED 子窗口到桌面）；传统 WorkerW 注入在此模式下失效。
    ///   6. 兜底：直接挂到“图标 WorkerW”内部、DefView 下方；再不行挂 Progman。
    ///
    /// 注意：本类只把【自己的渲染窗口】挂到壁纸层，【绝不销毁系统 WorkerW】，
    /// 因此解除壁纸时只是撤走自己的窗口，原静态壁纸自然透出，不会出现黑屏闪烁。
    /// </summary>
    public static class WorkerWInjector
    {
        /// <summary>承载层缓存：承载层（Progman/孤儿 WorkerW）是全局桌面对象，跨屏、跨次切换不变；
        /// 首次探测后缓存复用，仅 explorer 重启失效时才重新探测。Win10 经典路径每次切换都重发 0x052C
        /// 并轮询定位孤儿 WorkerW（~1-3s），是切换延迟主因，缓存后降至 ~100-300ms。</summary>
        private static IntPtr _cachedLayer = IntPtr.Zero;
        private static bool _cacheValid = false;
        private static readonly object _cacheLock = new object();

        /// <summary>资源管理器重启后承载层句柄失效，调用此方法使缓存失效、下次切换重新探测。</summary>
        public static void InvalidateCache()
        {
            lock (_cacheLock)
            {
                _cachedLayer = IntPtr.Zero;
                _cacheValid = false;
            }
            Logger.Log("[WorkerW] 承载层缓存已失效，下次切换将重新探测");
        }

        /// <summary>获取/创建桌面壁纸承载窗口。</summary>
        public static IntPtr AcquireWorkerW(Rectangle screenBounds)
        {
            // 缓存复用：承载层是全局桌面对象，跨屏/跨次不变；仅 explorer 重启失效时才重探。
            lock (_cacheLock)
            {
                if (_cacheValid && _cachedLayer != IntPtr.Zero && IsValid(_cachedLayer))
                {
                    Logger.Log($"[WorkerW] 复用缓存承载层: 0x{_cachedLayer.ToInt64():X}");
                    return _cachedLayer;
                }
            }

            Logger.Log($"[WorkerW] 开始获取桌面壁纸承载层，目标屏幕: {screenBounds}");

            var progman = Win32.FindWindow("Progman", null);
            Logger.Log($"[WorkerW] Progman=0x{progman.ToInt64():X}");
            if (progman == IntPtr.Zero)
            {
                Logger.Log("[WorkerW] 未找到 Progman，获取失败");
                return IntPtr.Zero;
            }

            // Win11 24H2/25H2 raised desktop：0x052C 不再生成孤儿 WorkerW，轮询 3 秒必然空转。
            // 直接走方案B（FindDefViewWorkerW 退化返回 Progman），跳过 0x052C 与轮询，设置即时响应。
            if (IsRaisedDesktop())
            {
                var w = FindDefViewWorkerW();
                IntPtr target = w != IntPtr.Zero ? w : progman;
                Logger.Log($"[WorkerW] raised desktop 模式，跳过 0x052C/轮询，直接使用承载层: 0x{target.ToInt64():X}");
                return CacheAndReturn(target);
            }

            // 经典做法：给 Progman 发送 0x052C，生成孤儿 WorkerW（壁纸层）。
            // 发送前先记录 Progman 现有子窗口快照（方案A）：Win11 24H2/25H2 上 0x052C 可能
            // 不再生成孤儿 WorkerW，只有“发送后新增”的 WorkerW 才是真正的壁纸承载层；
            // 若误选系统自带的壁纸层 WorkerW（发送前已存在），DWM 不会合成其子窗口到桌面。
            var preSpawnChildren = new HashSet<IntPtr>(EnumChildrenZOrder(progman));
            Logger.Log($"[WorkerW] 发送 0x052C 前 Progman 子窗口数: {preSpawnChildren.Count}");

            Win32.SendMessageTimeout(progman, Win32.WM_SPAWN_WORKER,
                IntPtr.Zero, IntPtr.Zero, Win32.SMTO_NORMAL, 1000, out _);

            // 轮询最多 3 秒，等待孤儿 WorkerW 生成并定位。
            // 定位优先级（Lively 已验证，Win11 24H2/25H2 上有效）：
            //   1) FindActivityWorkerW —— Lively 方式：0x052C 触发后枚举顶层窗口，找子窗口 class
            //      为 SHELLDLL_DefView 的窗口，再 FindWindowEx(IntPtr.Zero, hwnd, "WorkerW", null)
            //      取其后继“活动 WorkerW”（真正承载桌面的那个）。Win11 24H2/25H2 上 DefView 已
            //      改为 Progman 直接子窗口、顶层无含 DefView 的 WorkerW 时会返回 0，走后续回退。
            //   2) FindClassicOrphan —— Win10 经典路径（原逻辑保留为回退）。
            //   3) FindWin11Sibling —— Win11 快照 diff 路径（保留为回退）：只接受 0x052C 发送后
            //      新增的孤儿 WorkerW，避免误选系统自带壁纸层（DWM 不合成其子窗口）。
            IntPtr workerw = IntPtr.Zero;
            for (int i = 0; i < 30; i++)
            {
                workerw = FindActivityWorkerW();
                if (workerw == IntPtr.Zero)
                    workerw = FindClassicOrphan();
                if (workerw == IntPtr.Zero)
                    workerw = FindWin11Sibling(progman, preSpawnChildren);

                if (workerw != IntPtr.Zero)
                {
                    Logger.Log($"[WorkerW] 找到壁纸承载层: 0x{workerw.ToInt64():X}（第{i}次轮询）");
                    return CacheAndReturn(workerw);
                }
                Thread.Sleep(100);
            }

            // 轮询结束仍未找到孤儿 WorkerW：
            // - Win11 24H2/25H2：0x052C 不再生成孤儿 WorkerW，进入方案B——把渲染窗口挂到 Progman
            //   并置于 SHELLDLL_DefView 下方（图标层之下），由 Attach 中的 Z 序逻辑完成；
            // - Win10：0x052C 偶发失败时保留原有兜底顺序（图标 WorkerW 内部 → Progman）。
            Logger.Log("[WorkerW] 未找到孤儿 WorkerW，进入替代路径（方案B/兜底）");

            // 兜底 A：含 DefView 的图标 WorkerW，挂到其内部、DefView 下方。
            // Win11 24H2/25H2 上不存在“图标 WorkerW”，FindDefViewWorkerW 会退化返回 Progman，
            // Attach 仍会定位 Progman 下的 DefView 并把子窗口置于其下方（即方案B 的挂载效果）。
            workerw = FindDefViewWorkerW();
            if (workerw != IntPtr.Zero)
            {
                Logger.Log($"[WorkerW] 方案B/兜底-挂到图标WorkerW内部或Progman: 0x{workerw.ToInt64():X}");
                return CacheAndReturn(workerw);
            }

            // 兜底 B：Progman
            Logger.Log($"[WorkerW] 兜底-使用 Progman: 0x{progman.ToInt64():X}");
            return CacheAndReturn(progman);
        }

        /// <summary>记录并缓存承载层后返回（供 AcquireWorkerW 各分支复用）。</summary>
        private static IntPtr CacheAndReturn(IntPtr h)
        {
            if (h != IntPtr.Zero)
            {
                lock (_cacheLock)
                {
                    _cachedLayer = h;
                    _cacheValid = true;
                }
                Logger.Log($"[WorkerW] 承载层已缓存: 0x{h.ToInt64():X}");
            }
            return h;
        }

        /// <summary>将渲染子窗口挂接到父窗口，并铺满指定屏幕区域。</summary>
        public static void Attach(IntPtr childHwnd, IntPtr parentHwnd, Rectangle bounds)
        {
            if (childHwnd == IntPtr.Zero || parentHwnd == IntPtr.Zero)
            {
                Logger.Log($"[WorkerW] Attach 失败：child=0x{childHwnd.ToInt64():X}, parent=0x{parentHwnd.ToInt64():X}");
                return;
            }

            Logger.Log($"[WorkerW] 开始 Attach: child=0x{childHwnd.ToInt64():X} -> parent=0x{parentHwnd.ToInt64():X}, bounds={bounds}");

            // —— Win11 24H2+ raised desktop 检测（Lively 兼容核心）——
            // Win11 24H2/25H2 起 Progman 带 WS_EX_NOREDIRECTIONBITMAP，进入 raised desktop 架构：
            // 桌面壁纸层不再由传统孤儿 WorkerW 承载，而是 DWM 直接合成 Progman 的
            // WS_EX_LAYERED 子窗口。此模式下壁纸窗口必须挂到 Progman（而非 WorkerW），
            // 否则真实屏幕永远显示静态壁纸。
            bool raisedDesktop = IsRaisedDesktop();
            if (raisedDesktop)
            {
                var progman = Win32.FindWindow("Progman", null);
                if (progman != IntPtr.Zero && parentHwnd != progman)
                {
                    Logger.Log($"[WorkerW] 检测到 Win11 raised desktop（Progman 带 WS_EX_NOREDIRECTIONBITMAP），父窗口切换为 Progman: 0x{progman.ToInt64():X}");
                    parentHwnd = progman;
                }
            }

            // 注意：此处不再调用 DetachChildren(parentHwnd)。
            // 动态壁纸 A→B 无缝切换时，旧壁纸 A 的宿主窗口仍存活（尚未 Dispose），
            // 若在此强行 DestroyWindow，会导致 A 的 WebView2 Controller 未 Close，
            // 同一 CoreWebView2Environment 上 B 的 CreateCoreWebView2ControllerAsync 失败
            // （0x8007139F），B 透明、桌面显示系统原壁纸。
            // 旧壁纸窗口的清理统一由 WallpaperManager 的 CleanupScreenAsync / Provider.Dispose 负责。

            // 确保父窗口可见
            if (!Win32.IsWindowVisible(parentHwnd))
            {
                Logger.Log($"[WorkerW] parent 不可见，强制 ShowWindow: 0x{parentHwnd.ToInt64():X}");
                Win32.ShowWindow(parentHwnd, Win32.SW_SHOW);
            }

            Win32.SetParent(childHwnd, parentHwnd);
            Logger.Log("[WorkerW] SetParent 完成");

            // 去掉标题栏/边框/任务栏条目；WPF 无边框窗口默认带 WS_POPUP，必须一并清除——
            // WS_POPUP 与 WS_CHILD 互斥，Win11 24H2/25H2 raised desktop 模式下 DWM
            // 只合成 WS_CHILD（且非 WS_POPUP）的子窗口到桌面壁纸层。
            int style = Win32.GetWindowLong(childHwnd, Win32.GWL_STYLE);
            style = (style | Win32.WS_CHILD | Win32.WS_VISIBLE)
                    & ~Win32.WS_POPUP
                    & ~Win32.WS_CAPTION & ~Win32.WS_THICKFRAME
                    & ~Win32.WS_SYSMENU & ~Win32.WS_MINIMIZEBOX & ~Win32.WS_MAXIMIZEBOX;
            Win32.SetWindowLong(childHwnd, Win32.GWL_STYLE, style);
            Win32.SetWindowPos(childHwnd, IntPtr.Zero, 0, 0, 0, 0,
                Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOZORDER | Win32.SWP_FRAMECHANGED);

            int exStyle = Win32.GetWindowLong(childHwnd, Win32.GWL_EXSTYLE);
            if (raisedDesktop)
            {
                // Win11 24H2+ raised desktop 分支：DWM 仅合成 WS_EX_LAYERED 子窗口到桌面壁纸层。
                // WPF 窗口（ImageProvider/GifProvider）：WPF 内部管理 Layered 窗口渲染，
                // 外部设置 WS_EX_LAYERED 会导致渲染冲突（窗口不显示）。WPF 通过 AllowTransparency
                // 属性在 SourceInitialized 时自行设置 WS_EX_LAYERED，外部无需也不应干预。
                if (RenderWindow.IsWpfWindow(childHwnd))
                {
                    Logger.Log("[WorkerW] 窗口样式已设置（raised desktop + WPF 窗口：跳过外部 WS_EX_LAYERED，由 WPF AllowTransparency 内部管理）");
                }
                else
                {
                    // VideoProvider/WebProvider 原生 Win32 窗口：直接设置 WS_EX_LAYERED
                    // + SetLayeredWindowAttributes(alpha=255) 置满不透明，否则 alpha=0 完全透明。
                    exStyle = (exStyle | Win32.WS_EX_LAYERED | Win32.WS_EX_NOACTIVATE | Win32.WS_EX_TOOLWINDOW)
                              & ~Win32.WS_EX_APPWINDOW;
                    Win32.SetWindowLong(childHwnd, Win32.GWL_EXSTYLE, exStyle);
                    Win32.SetLayeredWindowAttributes(childHwnd, 0, 255, Win32.LWA_ALPHA);
                    Logger.Log("[WorkerW] 窗口样式已设置（raised desktop：WS_EX_LAYERED + alpha=255）");
                }
            }
            else
            {
                // 传统模式（Win10 经典 WorkerW / 非 raised）：绝不能保留 WS_EX_LAYERED。
                // DWM 不合成 WorkerW 下 Layered 子窗口的 alpha 修改——窗口初始 alpha=0 后
                // 永远透明，直到用户点击任意窗口引发前台变化才显现（"点一下才播放"根因）；
                // WPF MediaElement 的视频内容走 DirectX，Layered 窗口同样不被屏幕合成。
                // 非 WPF 原生窗口（VideoProvider 等）即使创建时带了 layered 也在此剥除。
                if (!RenderWindow.IsWpfWindow(childHwnd))
                    exStyle &= ~Win32.WS_EX_LAYERED;
                exStyle = (exStyle | Win32.WS_EX_NOACTIVATE | Win32.WS_EX_TOOLWINDOW)
                          & ~Win32.WS_EX_APPWINDOW;
                Win32.SetWindowLong(childHwnd, Win32.GWL_EXSTYLE, exStyle);
                Logger.Log("[WorkerW] 窗口样式已设置（传统模式：非 Layered）");
            }

            // 若父窗口是图标 WorkerW（含 SHELLDLL_DefView），需把子窗口置于 DefView 下方（图标后方）。
            // 否则（孤儿壁纸层）置于顶层，确保盖在系统静态壁纸背景之上。
            // Win11 24H2/25H2 上发送 0x052C 后 Progman 的 SHELLDLL_DefView 可能短暂消失/重建，
            // 只查一次会偶发返回 0 而落到 HWND_TOP 置顶、盖住桌面图标，因此这里轮询等待其出现。
            IntPtr insertAfter = Win32.HWND_TOP;
            IntPtr defView = IntPtr.Zero;
            // 仅当父窗口可能包含 DefView 时才轮询等待（Progman / 含 DefView 的图标 WorkerW）；
            // Win10 经典路径父窗口是孤儿 WorkerW（不含 DefView），轮询必然空转满 30 次（~3s），
            // 直接跳过、保持 HWND_TOP 即可——这是 Win10 切换延迟的另一主因。
            bool parentMayGainDefView =
                Win32.GetClassName(parentHwnd) != "WorkerW"
                || Win32.HasShellDefViewDescendant(parentHwnd);
            if (parentMayGainDefView)
            {
                for (int attempt = 0; attempt < 30; attempt++)
                {
                    defView = FindShellDefViewUnderParent(parentHwnd);
                    if (defView != IntPtr.Zero) break;
                    if (attempt < 29)
                    {
                        Logger.Log($"[WorkerW] 等待 DefView 出现（第{attempt + 1}次/30，100ms）");
                        Thread.Sleep(100);
                    }
                }
            }
            else
            {
                Logger.Log("[WorkerW] 父窗口为孤儿 WorkerW（Win10/经典路径，不含 DefView），跳过 DefView 轮询");
            }

            if (defView != IntPtr.Zero)
            {
                insertAfter = defView;
                Logger.Log($"[WorkerW] 父窗口包含 DefView，将 child 置于 DefView 下方: 0x{defView.ToInt64():X}");
            }
            else if (Win32.GetClassName(parentHwnd) == "WorkerW")
            {
                // Win10/经典路径：父窗口是孤儿 WorkerW（不含 DefView），原行为是置于顶层盖过
                // 静态壁纸背景，与桌面图标层（DefView 在顶层 WorkerW 内部）无冲突，保持 HWND_TOP。
                insertAfter = Win32.HWND_TOP;
                Logger.Log("[WorkerW] DefView 未出现且父窗口为 WorkerW（Win10/经典路径），保持 HWND_TOP");
            }
            else
            {
                // Win11 方案B（挂 Progman）：DefView 一直未出现时绝不能置顶（会盖住桌面图标）。
                // 若 Progman 子窗口里存在系统壁纸 WorkerW（不含 DefView 的壁纸层），把 child 插到它
                // 上方——SetWindowPos 的 hWndInsertAfter 是“置于该窗口之后（下方）”，所以取该 WorkerW
                // 的 Z 序前一个兄弟窗口，使 child 落在图标层（DefView）与壁纸层之间。
                var children = EnumChildrenZOrder(parentHwnd);
                int workerIdx = -1;
                for (int i = children.Count - 1; i >= 0; i--)
                {
                    if (Win32.GetClassName(children[i]) == "WorkerW"
                        && !Win32.HasShellDefViewDescendant(children[i]))
                    {
                        workerIdx = i;
                        break;
                    }
                }
                if (workerIdx > 0)
                {
                    insertAfter = children[workerIdx - 1];
                    Logger.Log($"[WorkerW] DefView 未出现，将 child 插到系统壁纸 WorkerW 上方（insertAfter=0x{insertAfter.ToInt64():X}），避免盖住桌面图标");
                }
                else
                {
                    // 无法可靠调整（壁纸 WorkerW 不存在或已是 Z 序最顶）：不置顶，把 child 沉到底部，
                    // 至少保证不盖住桌面图标，并打日志警告。
                    Logger.Log("[WorkerW] DefView 未出现且无法可靠定位壁纸层，child 置于 Z 序底部（HWND_BOTTOM），避免盖住桌面图标");
                    Win32.SetWindowPos(childHwnd, Win32.HWND_BOTTOM,
                        0, 0, bounds.Width, bounds.Height,
                        Win32.SWP_NOACTIVATE | Win32.SWP_NOOWNERZORDER | Win32.SWP_SHOWWINDOW);
                    Win32.SetWindowPos(parentHwnd, IntPtr.Zero, 0, 0, 0, 0,
                        Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOZORDER | Win32.SWP_FRAMECHANGED | Win32.SWP_SHOWWINDOW);
                    Logger.Log("[WorkerW] Attach 完成（降级：底部）");
                    return;
                }
            }

            Win32.SetWindowPos(childHwnd, insertAfter,
                0, 0, bounds.Width, bounds.Height,
                Win32.SWP_NOACTIVATE | Win32.SWP_NOOWNERZORDER | Win32.SWP_SHOWWINDOW);

            // 注：此处不再执行 HWND_TOP 置顶——实验证明子窗口状态下 HWND_TOP 无法触发
            // DWM 系统级合成（PrintWindow 能抓到视频帧、真实桌面却始终显示静态壁纸）。
            // 且 Attach 阶段视频尚未开始渲染，触发也无内容可合成。
            // 真正的强制合成由 ForceDwmComposition 在视频开始渲染后执行
            // （摘出→HWND_TOPMOST→归位，见 WallpaperManager.SetWallpaperAsync）。

            // 强制刷新父窗口，促使 DWM 立即合成
            Win32.SetWindowPos(parentHwnd, IntPtr.Zero, 0, 0, 0, 0,
                Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOZORDER | Win32.SWP_FRAMECHANGED | Win32.SWP_SHOWWINDOW);

            Logger.Log("[WorkerW] Attach 完成");
        }

        /// <summary>
        /// 强制 DWM 重新合成渲染窗口到桌面壁纸层。
        /// 根因：Win11 24H2/25H2 下 WPF MediaElement 刚 Attach 到 Progman 时视频尚未渲染，
        /// DWM 不合成空窗口，桌面始终显示底层静态壁纸（PrintWindow 能抓到视频帧、屏幕却不显示）。
        /// 实验验证：子窗口状态下 SetWindowPos(HWND_TOPMOST) 无效（子窗口无法成为系统置顶），
        /// 必须先把窗口摘出（SetParent 到桌面）使其成为顶层窗口、系统级置顶触发 DWM 合成后再归位；
        /// 归位后 DWM 合成状态保持，视频持续可见。
        /// 调用时机：视频开始播放（Play 之后延迟 2~3 秒）再调用，确保已有内容可合成。
        /// </summary>
        public static void ForceDwmComposition(IntPtr childHwnd, IntPtr parentHwnd, Rectangle bounds)
        {
            if (childHwnd == IntPtr.Zero || parentHwnd == IntPtr.Zero)
            {
                Logger.Log($"[WorkerW] 强制 DWM 合成跳过：child=0x{childHwnd.ToInt64():X}, parent=0x{parentHwnd.ToInt64():X}");
                return;
            }

            Logger.Log($"[WorkerW] 强制 DWM 合成开始: child=0x{childHwnd.ToInt64():X}, parent=0x{parentHwnd.ToInt64():X}");

            // 1. 摘出为顶层窗口（父=桌面），子窗口状态下置顶无效
            Win32.SetParent(childHwnd, IntPtr.Zero);
            // 2. 系统级置顶，强制 DWM 重新合成窗口内容
            Win32.SetWindowPos(childHwnd, Win32.HWND_TOPMOST,
                bounds.X, bounds.Y, bounds.Width, bounds.Height,
                Win32.SWP_NOACTIVATE | Win32.SWP_SHOWWINDOW);

            // 3. 归位回挂载父窗口
            Win32.SetParent(childHwnd, parentHwnd);
            // 4. 定位 Z 序：有 DefView 则置于其下方（图标层之下、壁纸层之上），否则沉底
            IntPtr insertAfter = Win32.HWND_BOTTOM;
            IntPtr defView = FindShellDefViewUnderParent(parentHwnd);
            if (defView != IntPtr.Zero)
                insertAfter = defView;
            Win32.SetWindowPos(childHwnd, insertAfter,
                0, 0, bounds.Width, bounds.Height,
                Win32.SWP_NOACTIVATE | Win32.SWP_NOOWNERZORDER | Win32.SWP_SHOWWINDOW);

            // 4.1 归位后强制校验 Z 序：SetParent 回挂时 Windows 会把子窗口置到父窗口 Z 序顶部，
            // 若上面的 SetWindowPos 未生效，渲染窗口会盖住桌面图标（DefView）。必须枚举确认
            // child 确实排在 DefView 之后（下方）；若否，则反复纠正直至成功或达到重试上限。
            int fixAttempts = 0;
            while (defView != IntPtr.Zero && fixAttempts < 5 && !IsAfterChild(parentHwnd, childHwnd, defView))
            {
                fixAttempts++;
                Logger.Log($"[WorkerW] Z 序校验失败（第{fixAttempts}次），重新将 child 置于 DefView 下方: 0x{defView.ToInt64():X}");
                Win32.SetWindowPos(childHwnd, defView,
                    0, 0, bounds.Width, bounds.Height,
                    Win32.SWP_NOACTIVATE | Win32.SWP_NOOWNERZORDER | Win32.SWP_SHOWWINDOW);
                Thread.Sleep(50);
            }
            if (defView != IntPtr.Zero)
                Logger.Log($"[WorkerW] Z 序校验通过（child 位于 DefView 下方）: child=0x{childHwnd.ToInt64():X}, defView=0x{defView.ToInt64():X}");
            else
                Logger.Log($"[WorkerW] Z 序校验跳过（未找到 DefView），child 已沉底不遮挡图标");

            // 5. 强制图标层重绘（Win11 25H2 下壁纸不显示时的关键收尾手段）。
            //    原理：25H2 上手动"关闭再开启显示桌面图标"（桌面右键→查看→显示桌面图标）
            //    会触发 DWM 重新合成桌面壁纸层与图标层；这里对 SHELLDLL_DefView 执行
            //    ShowWindow(SW_HIDE) → ShowWindow(SW_SHOWNORMAL)，即程序内等价操作，
            //    确保视频就绪后 DWM 真正把壁纸子窗口合成到桌面。
            //    注意：仅做显示开关，不销毁、不改样式，图标层会瞬闪后恢复，不影响使用。
            IntPtr defViewForRefresh = defView != IntPtr.Zero ? defView : FindTopLevelDefView();
            if (defViewForRefresh != IntPtr.Zero)
            {
                Logger.Log($"[WorkerW] 触发图标层重绘: DefView=0x{defViewForRefresh.ToInt64():X}（SW_HIDE→SW_SHOWNORMAL）");
                Win32.ShowWindow(defViewForRefresh, Win32.SW_HIDE);
                Win32.ShowWindow(defViewForRefresh, Win32.SW_SHOWNORMAL);
            }
            else
            {
                Logger.Log("[WorkerW] 未找到 SHELLDLL_DefView，跳过图标层重绘");
            }

            Logger.Log("[WorkerW] 强制 DWM 合成完成（摘出→置顶→归位→图标层重绘）");
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
            if (!IsOrphanWorkerW(workerw)) return;
            try { Win32.DestroyWindow(workerw); }
            catch { /* ignore */ }
        }

        #region 定位策略

        /// <summary>公开检测：当前桌面是否为 Win11 24H2+ raised desktop 模式。
        /// 供 VideoProvider 在创建窗口时决定是否使用 WS_EX_LAYERED
        /// （Win10/传统模式必须非 Layered，Win11 raised 必须 Layered）。</summary>
        public static bool IsRaisedDesktopMode()
        {
            try { return IsRaisedDesktop(); }
            catch { return false; }
        }

        /// <summary>检测当前桌面是否为 Win11 24H2+ raised desktop 模式。
        /// 判据：Progman 带 WS_EX_NOREDIRECTIONBITMAP 扩展样式（Lively 同款判据）。
        /// raised desktop 模式下真正承载桌面的是 Progman（DWM 直接合成其 WS_EX_LAYERED
        /// 子窗口），传统孤儿 WorkerW 注入失效，必须走 raised desktop 兼容分支。</summary>
        private static bool IsRaisedDesktop()
        {
            IntPtr progman = Win32.FindWindow("Progman", null);
            if (progman == IntPtr.Zero) return false;
            IntPtr exStyle = Win32.GetWindowLongPtr(progman, Win32.GWL_EXSTYLE);
            bool raised = (exStyle.ToInt64() & Win32.WS_EX_NOREDIRECTIONBITMAP) != 0;
            if (raised)
                Logger.Log($"[WorkerW] 检测到 raised desktop（Progman exStyle=0x{exStyle.ToInt64():X} 含 WS_EX_NOREDIRECTIONBITMAP）");
            return raised;
        }

        /// <summary>Lively 方式定位“活动 WorkerW”（Win11 24H2/25H2 兼容，优先采用）：
        /// 1) EnumWindows 枚举所有顶层窗口；
        /// 2) 对每个顶层窗口 FindWindowEx(hwnd, 0, "SHELLDLL_DefView", null) 查找子窗口
        ///    class 为 SHELLDLL_DefView 的窗口（即桌面图标宿主）；
        /// 3) 找到后，再 FindWindowEx(IntPtr.Zero, hwnd, "WorkerW", null) 取其 Z 序后继的
        ///    “活动 WorkerW”——即真正承载桌面壁纸的那个 WorkerW。
        /// Win11 24H2/25H2 上 DefView 已改为 Progman 直接子窗口、顶层不存在含 DefView 的
        /// WorkerW，本方法返回 0，由 AcquireWorkerW 继续走经典/快照 diff 回退。</summary>
        private static IntPtr FindActivityWorkerW()
        {
            IntPtr result = IntPtr.Zero;
            Win32.EnumWindows((hwnd, _) =>
            {
                IntPtr defView = Win32.FindWindowEx(hwnd, IntPtr.Zero, "SHELLDLL_DefView", null);
                if (defView == IntPtr.Zero) return true; // 该顶层窗口不含 DefView 子窗口，跳过

                // 含 DefView 的窗口（活动 WorkerW / Progman）之后的下一个 WorkerW 即承载桌面壁纸的层
                IntPtr workerw = Win32.FindWindowEx(IntPtr.Zero, hwnd, "WorkerW", null);
                if (workerw != IntPtr.Zero)
                {
                    result = workerw;
                    return false; // 找到，停止枚举
                }
                return true;
            }, IntPtr.Zero);
            return result;
        }

        /// <summary>枚举顶层窗口，返回子窗口 class 为 SHELLDLL_DefView 的 DefView 窗口。
        /// 用于图标层重绘：当挂载父窗口本身不含 DefView（如传统模式挂孤儿 WorkerW）时，
        /// 从顶层窗口中定位真正显示桌面图标的 DefView 实例。</summary>
        private static IntPtr FindTopLevelDefView()
        {
            IntPtr found = IntPtr.Zero;
            Win32.EnumWindows((hwnd, _) =>
            {
                IntPtr def = Win32.FindWindowEx(hwnd, IntPtr.Zero, "SHELLDLL_DefView", null);
                if (def != IntPtr.Zero) { found = def; return false; }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        /// <summary>Win10 经典路径：包含 SHELLDLL_DefView 的 WorkerW 之后那个孤儿 WorkerW。
        /// 用 HasShellDefViewDescendant 做“含 DefView 后代”判断，兼容 DefView 直接子窗口或嵌套在容器内的结构。</summary>
        private static IntPtr FindClassicOrphan()
        {
            IntPtr result = IntPtr.Zero;
            Win32.EnumWindows((hwnd, _) =>
            {
                if (Win32.GetClassName(hwnd) != "WorkerW") return true;
                if (!Win32.HasShellDefViewDescendant(hwnd)) return true;

                // 图标 WorkerW 之后的那个 WorkerW 即 0x052C 生成的孤儿壁纸层
                IntPtr orphan = Win32.FindWindowEx(IntPtr.Zero, hwnd, "WorkerW", null);
                if (orphan != IntPtr.Zero && !Win32.HasShellDefViewDescendant(orphan))
                {
                    result = orphan;
                    return false; // 找到，停止枚举
                }
                return true;
            }, IntPtr.Zero);
            return result;
        }

        /// <summary>Win11 路径：SHELLDLL_DefView 是 Progman 的直接子窗口，壁纸层是 Progman 下“不含 DefView 的 WorkerW”。
        /// 其中 0x052C 生成的孤儿壁纸层位于 Z 序最底层（children 列表最后一项），必须选它，
        /// 而不是 DefView 之后第一个（那个是系统自己的壁纸层，DWM 不会合成子窗口到桌面）。
        /// <para>方案A：preSpawnChildren 为发送 0x052C 前的 Progman 子窗口快照，只有“发送后新增”的
        /// WorkerW 才是 0x052C 生成的孤儿；Win11 24H2/25H2 上 0x052C 不再生成新窗口，因此
        /// 该路径会返回 0，由调用方走方案B（挂 Progman、置于 DefView 下方）。</para></summary>
        private static IntPtr FindWin11Sibling(IntPtr progman, ISet<IntPtr> preSpawnChildren)
        {
            var children = EnumChildrenZOrder(progman);
            int defIdx = children.FindIndex(h => Win32.GetClassName(h) == "SHELLDLL_DefView");

            // 诊断：记录 Progman 全部子窗口结构与候选 WorkerW（isNew 表示 0x052C 发送后新增）
            for (int i = 0; i < children.Count; i++)
            {
                var h = children[i];
                string cls = Win32.GetClassName(h);
                bool hasShell = Win32.HasShellDefViewDescendant(h);
                bool isNew = !preSpawnChildren.Contains(h);
                Logger.Log($"[WorkerW] FindWin11Sibling 子窗口[{i}] 0x{h.ToInt64():X} class={cls} hasShell={hasShell} isNew={isNew}");
            }

            var candidates = new List<(int idx, IntPtr hwnd)>();
            for (int i = 0; i < children.Count; i++)
            {
                var h = children[i];
                // 仅接受 0x052C 发送后【新增】的 WorkerW：系统自带的壁纸层 WorkerW 在发送前就存在，
                // DWM 不会合成其子窗口到桌面；只有发送后新增的孤儿 WorkerW 才是真正的壁纸承载层。
                if (Win32.GetClassName(h) == "WorkerW"
                    && !Win32.HasShellDefViewDescendant(h)
                    && !preSpawnChildren.Contains(h))
                {
                    candidates.Add((i, h));
                }
            }

            if (candidates.Count == 0) return IntPtr.Zero;

            // 优先选 Z 序最底层（列表最后）的新增 WorkerW —— 即 0x052C 生成的孤儿壁纸层。
            var chosen = candidates[candidates.Count - 1];
            Logger.Log($"[WorkerW] FindWin11Sibling: DefView索引={defIdx}, 新增候选WorkerW数={candidates.Count}, 选用最后一项 idx={chosen.idx} 0x{chosen.hwnd.ToInt64():X}");
            return chosen.hwnd;
        }

        /// <summary>兜底：返回包含 SHELLDLL_DefView 的 WorkerW（挂到其内部、DefView 下方）。
        /// Win11 24H2/25H2 桌面架构重构后不存在“图标 WorkerW”（DefView 是 Progman 直接子窗口），
        /// 此时退化返回 Progman——Attach 会定位 Progman 下的 DefView 并把子窗口置于其下方，
        /// 与方案B（挂 Progman、置于 DefView 下方）效果一致。</summary>
        private static IntPtr FindDefViewWorkerW()
        {
            IntPtr result = IntPtr.Zero;
            Win32.EnumWindows((hwnd, _) =>
            {
                if (Win32.GetClassName(hwnd) != "WorkerW") return true;
                IntPtr defView = Win32.FindWindowEx(hwnd, IntPtr.Zero, "SHELLDLL_DefView", null);
                if (defView != IntPtr.Zero) { result = hwnd; return false; }
                return true;
            }, IntPtr.Zero);
            if (result != IntPtr.Zero) return result;

            // Win11 24H2/25H2：没有“图标 WorkerW”，退化返回 Progman（方案B 的挂载目标）。
            IntPtr progman = Win32.FindWindow("Progman", null);
            if (progman != IntPtr.Zero)
            {
                Logger.Log($"[WorkerW] FindDefViewWorkerW 未找到图标 WorkerW（Win11 结构），退化使用 Progman: 0x{progman.ToInt64():X}");
                return progman;
            }
            return IntPtr.Zero;
        }

        /// <summary>在父窗口下查找 SHELLDLL_DefView。</summary>
        private static IntPtr FindShellDefViewUnderParent(IntPtr parent)
        {
            if (parent == IntPtr.Zero) return IntPtr.Zero;
            IntPtr def = Win32.FindWindowEx(parent, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (def != IntPtr.Zero) return def;

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

        private static bool IsOrphanWorkerW(IntPtr hWnd)
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

        /// <summary>判断 child 在 parent 的 Z 序中是否位于 anchor 之后（下方）。
        /// 枚举父窗口全部子窗口，child 的索引大于 anchor 的索引即视为在其下方。</summary>
        private static bool IsAfterChild(IntPtr parent, IntPtr child, IntPtr anchor)
        {
            if (parent == IntPtr.Zero || child == IntPtr.Zero || anchor == IntPtr.Zero) return false;
            var children = EnumChildrenZOrder(parent);
            int childIdx = children.IndexOf(child);
            int anchorIdx = children.IndexOf(anchor);
            return childIdx > anchorIdx;
        }

        #endregion
    }
}
