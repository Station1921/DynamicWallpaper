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

        /// <summary>“内容就绪前不可见”用的屏外坐标：把壁纸子窗口临时放到父窗口客户区之外。
        /// 窗口仍是 WS_VISIBLE（WebView2/视频解码不会被挂起、就绪判定不受影响），但被父窗口
        /// 裁剪 → 肉眼完全不可见。这样切换 A→B 时旧壁纸 A 全程可见，新窗口就绪后才移入正确位置，
        /// 不会闪出新窗口的空白帧或底层系统壁纸（= 老版本“就绪前沿 alpha 保持透明”的等价实现，
        /// 因为去掉 WS_EX_LAYERED 后 alpha 已失效）。</summary>
        private const int OffscreenX = -32000;
        private const int OffscreenY = -32000;

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
                // 缓存复用硬条件：句柄有效【且可见】。显示拓扑切换后旧 WorkerW 常被 explorer
                // 隐藏/废弃（此时其矩形也不再是桌面矩形，坐标换算会错位），必须重新探测。
                if (_cacheValid && _cachedLayer != IntPtr.Zero && IsValid(_cachedLayer)
                    && Win32.IsWindowVisible(_cachedLayer))
                {
                    Logger.Log($"[WorkerW] 复用缓存承载层: 0x{_cachedLayer.ToInt64():X}");
                    return _cachedLayer;
                }
                if (_cacheValid && _cachedLayer != IntPtr.Zero && IsValid(_cachedLayer)
                    && !Win32.IsWindowVisible(_cachedLayer))
                    Logger.Log($"[WorkerW] 缓存承载层 0x{_cachedLayer.ToInt64():X} 已不可见（拓扑变化），重新探测");
            }

            Logger.Log($"[WorkerW] 开始获取桌面壁纸承载层，目标屏幕: {screenBounds}");

            var progman = Win32.FindWindow("Progman", null);
            Logger.Log($"[WorkerW] Progman=0x{progman.ToInt64():X}");
            if (progman == IntPtr.Zero)
            {
                Logger.Log("[WorkerW] 未找到 Progman，获取失败");
                return IntPtr.Zero;
            }

            // Win11 24H2/25H2 raised desktop：Progman 带 WS_EX_NOREDIRECTIONBITMAP，真正承载桌面的是 Progman。
            // 关键：壁纸必须挂到 Progman 之下的“背景 WorkerW”（系统静态壁纸层，位于 DefView 之下），
            // 而不是挂到 Progman 本身。挂到 Progman 再 Z 序塞到 DefView 之下在 raised desktop 下不可靠
            // （分层子窗口仍会被 DWM 合成到图标层之上 → 盖住图标、吃右键），这正是此前回归的根因。
            // 挂到背景 WorkerW 后，壁纸作为其子窗口天然位于图标层之后，无需 WS_EX_TRANSPARENT。
            if (IsRaisedDesktop())
            {
                // 优先复用缓存中的“有效背景 WorkerW”：避免每次重新探测撞上 explorer 重建桌面
                // 结构的窗口期（此间 Progman 下可能瞬时没有背景 WorkerW）而误退化到挂 Progman。
                // 非分层窗口挂 Progman 在 raised desktop 下**不被 DWM 合成**，表现为
                // “仅在N显示不显示壁纸 / 闪现一下即消失”。
                lock (_cacheLock)
                {
                    if (_cacheValid && _cachedLayer != IntPtr.Zero && IsValid(_cachedLayer)
                        && Win32.GetClassName(_cachedLayer) == "WorkerW"
                        && Win32.IsWindowVisible(_cachedLayer))
                    {
                        Logger.Log($"[WorkerW] raised desktop 复用缓存背景 WorkerW: 0x{_cachedLayer.ToInt64():X}");
                        return _cachedLayer;
                    }
                }

                var bg = FindBackgroundWorkerW(progman);
                if (bg != IntPtr.Zero)
                {
                    Logger.Log($"[WorkerW] raised desktop 模式，承载层=背景 WorkerW: 0x{bg.ToInt64():X}（跳过 0x052C/轮询）");
                    return CacheAndReturn(bg);
                }

                // 探测失败：回退到上一次缓存的 WorkerW（即便当前 IsValid 暂为假，也优先它，
                // 避免挂到“必然不可见”的 Progman；句柄真死了也只是 SetParent 失败，无副作用）。
                lock (_cacheLock)
                {
                    if (_cachedLayer != IntPtr.Zero && Win32.GetClassName(_cachedLayer) == "WorkerW"
                        && Win32.IsWindowVisible(_cachedLayer))
                    {
                        Logger.Log($"[WorkerW] 探测失败，回退到上次背景 WorkerW: 0x{_cachedLayer.ToInt64():X}（不挂 Progman）");
                        return _cachedLayer;
                    }
                }

                // 最终兜底：Progman（raised desktop 下可能不可见）。**不缓存 Progman**，
                // 以便下一次调用重新探测背景 WorkerW，避免把不可见承载层固化进缓存。
                Logger.Log($"[WorkerW] raised desktop 未找到背景 WorkerW，兜底 Progman: 0x{progman.ToInt64():X}（不缓存，下次重探）");
                return progman;
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
            // 是否需要在子窗口上保留 WS_EX_LAYERED：
            //   - 挂到 Progman 之下的“背景 WorkerW”（普通 WorkerW，位于 DefView 之下）时，
            //     子窗口是普通子窗口，DWM 会按常规合成沉在图标层之后，【绝不能带 WS_EX_LAYERED】
            //     —— 分层窗口会被 DWM 独立合成到图标层(DefView)之上，导致盖住图标、吃掉右键
            //     （这正是此前“运行中重设壁纸就盖图标/右键失效”的根因）。
            //   - 仅当找不到背景 WorkerW、退化为直接挂 Progman（noreirectionbitmap 父窗口）时，
            //     才保留 WS_EX_LAYERED 让 DWM 合成内容（可见性优先，此为罕见退化路径）。
            bool useLayered = false;
            if (raisedDesktop)
            {
                if (Win32.GetClassName(parentHwnd) == "WorkerW")
                {
                    // 承载层已经是 WorkerW（AcquireWorkerW 已定位好），不再二次探测：
                    // ① 二次探测要再耗 0~2s（切换延迟）；② 一旦这次探测失败，就会把父窗口从
                    // “可见的 WorkerW”改写成“raised desktop 下不可见的 Progman”，直接导致壁纸不显示。
                    Logger.Log($"[WorkerW] 父窗口已是 WorkerW: 0x{parentHwnd.ToInt64():X}（跳过二次探测，直接挂载）");
                }
                else
                {
                    var progman = Win32.FindWindow("Progman", null);
                    if (progman != IntPtr.Zero)
                    {
                        var bg = FindBackgroundWorkerW(progman);
                        if (bg != IntPtr.Zero && bg != parentHwnd)
                        {
                            Logger.Log($"[WorkerW] 检测到 Win11 raised desktop，父窗口切换为 WorkerW: 0x{bg.ToInt64():X}（子窗口非分层，沉到图标层之后）");
                            parentHwnd = bg;
                        }
                        else if (bg == IntPtr.Zero)
                        {
                            // 极少数情况（本次日志中 1920×1080 显示模式就是这种）确实找不到任何 WorkerW，
                            // 只能退回 Progman。绝不再用 WS_EX_LAYERED —— 分层窗口会被 DWM 独立合成到
                            // 图标层之上，盖住图标、吃掉右键。
                            Logger.Log("[WorkerW] raised desktop 未找到任何 WorkerW，退回 Progman（非分层，可能不可见）");
                        }
                    }
                }
            }

            // 注意：此处不再调用 DetachChildren(parentHwnd)。
            // 动态壁纸 A→B 无缝切换时，旧壁纸 A 的宿主窗口仍存活（尚未 Dispose），
            // 若在此强行 DestroyWindow，会导致 A 的 WebView2 Controller 未 Close，
            // 同一 CoreWebView2Environment 上 B 的 CreateCoreWebView2ControllerAsync 失败
            // （0x8007139F），B 透明、桌面显示系统原壁纸。
            // 旧壁纸窗口的清理统一由 WallpaperManager 的 CleanupScreenAsync / Provider.Dispose 负责。

            // 【绝不 ShowWindow 父 WorkerW】——explorer 的隐藏占位 WorkerW 被强行显示后，
            // 就是一个没有内容的空白顶层窗口（用户看到的"白块"，Alt+Tab 可见可关闭，
            // 且它是 explorer 的窗口，本程序退出后依然存在）。正确做法是只在探测阶段
            // 接受"可见"的 WorkerW（见 ProbeWorkerWLayer 的 IsWindowVisible 过滤）。
            if (!Win32.IsWindowVisible(parentHwnd))
                Logger.Log($"[WorkerW] 警告：父承载层 0x{parentHwnd.ToInt64():X} 当前不可见（不改其可见性，白块即由此前的强制 ShowWindow 引起）");

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
            if (useLayered)
            {
                // 退化路径（直接挂 Progman / noreirectionbitmap 父窗口）：必须设置 WS_EX_LAYERED 让 DWM 合成内容。
                // 设完 Layered 后必须 SetLayeredWindowAttributes(alpha=255) 置满不透明，否则默认 alpha=0 透明不显示。
                exStyle = (exStyle | Win32.WS_EX_LAYERED | Win32.WS_EX_NOACTIVATE | Win32.WS_EX_TOOLWINDOW)
                          & ~Win32.WS_EX_APPWINDOW;
                Win32.SetWindowLong(childHwnd, Win32.GWL_EXSTYLE, exStyle);
                Win32.SetLayeredWindowAttributes(childHwnd, 0, 255, Win32.LWA_ALPHA);
                Logger.Log("[WorkerW] 窗口样式已设置（退化路径：直接挂 Progman，WS_EX_LAYERED + alpha=255）");
            }
            else
            {
                // 主路径：普通子窗口（挂到背景 WorkerW 或传统 WorkerW），【绝不设置 WS_EX_LAYERED】。
                // WS_EX_LAYERED 会让 DWM 把分层窗口独立合成到图标层(DefView)之上 → 盖住图标、吃掉右键。
                // 普通子窗口由 DWM 按常规合成，自然沉在图标层之后（图标常显、右键落到桌面）。
                // 这里额外显式清掉 WS_EX_LAYERED 位：即便窗口被复用/样式残留，也保证绝无分层样式。
                exStyle = (exStyle | Win32.WS_EX_NOACTIVATE | Win32.WS_EX_TOOLWINDOW)
                          & ~Win32.WS_EX_APPWINDOW
                          & ~Win32.WS_EX_LAYERED;
                Win32.SetWindowLong(childHwnd, Win32.GWL_EXSTYLE, exStyle);
                Logger.Log("[WorkerW] 窗口样式已设置（主路径：非 Layered 普通子窗口，沉到图标层之后）");
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
                    ToParentClient(parentHwnd, bounds, out int cx, out int cy);
                    Win32.SetWindowPos(childHwnd, Win32.HWND_BOTTOM,
                        cx, cy, bounds.Width, bounds.Height,
                        Win32.SWP_NOACTIVATE | Win32.SWP_NOOWNERZORDER | Win32.SWP_SHOWWINDOW);
                    Win32.SetWindowPos(parentHwnd, IntPtr.Zero, 0, 0, 0, 0,
                        Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOZORDER | Win32.SWP_FRAMECHANGED | Win32.SWP_SHOWWINDOW);
                    Logger.Log("[WorkerW] Attach 完成（降级：底部）");
                    return;
                }
            }

            // 关键：x/y 必须是屏幕在“虚拟桌面”中的真实坐标换算成父窗口客户区坐标，绝不能写死 0,0。
            // 多屏扩展模式下主屏为 (0,0)、第二屏通常为 (3440,0) 或 (-1920,0) 等偏移；若写死 0,0，
            // 副屏壁纸窗口会被错误地压到主屏左上角（与/或盖住主屏壁纸），副屏永远空白。
            // 子窗口 SetParent 到 Progman 后，坐标相对 Progman 客户区。Progman 客户区覆盖整个虚拟桌面，
            // 其客户区原点 = 虚拟桌面左上角（外接屏在主屏左侧/上方时该原点 ≠ (0,0)，如 (-1920,0)），
            // 因此必须用“屏幕虚拟坐标 − 父客户区原点”换算，否则整块壁纸会偏移一块屏
            // （表现为：主屏壁纸跑到副屏且按主屏分辨率外溢、设副屏壁纸则完全跑到屏幕外）。
            ToParentClient(parentHwnd, bounds, out int px, out int py);
            Win32.SetWindowPos(childHwnd, insertAfter,
                px, py, bounds.Width, bounds.Height,
                Win32.SWP_NOACTIVATE | Win32.SWP_NOOWNERZORDER | Win32.SWP_SHOWWINDOW);

            // Z 序/尺寸已按上面的 SetWindowPos 定好（insertAfter 关系保留），
            // 但【此刻内容还没就绪】——立刻把它移到父客户区之外，让它肉眼不可见。
            // 就绪后由 WallpaperManager 调 ShowWallpaperWindow 移回上面的位置。
            // 目的：切换 A→B 时不会闪出新窗口的空白帧 / 底层系统壁纸（旧壁纸 A 全程可见）。
            // SWP_NOZORDER：保持 Z 序不变，只挪位置。
            Win32.SetWindowPos(childHwnd, IntPtr.Zero, OffscreenX, OffscreenY,
                bounds.Width, bounds.Height,
                Win32.SWP_NOACTIVATE | Win32.SWP_NOZORDER | Win32.SWP_SHOWWINDOW);
            Logger.Log("[WorkerW] 窗口已挂好但内容未就绪，先移至屏外（就绪后再移入，切换不闪）");

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
        /// 内容就绪后把壁纸子窗口移入正确位置并显示。
        /// Attach 结束时会把还未就绪的窗口先移到父客户区之外（避免切换闪出空白帧/系统壁纸），
        /// 由本方法在“内容真正可显示”之后把它移回来——这是去掉 WS_EX_LAYERED 之后，
        /// 老版本“就绪前保持透明”行为的等价实现。
        /// 幂等：重复调用无副作用。
        /// </summary>
        public static void ShowWallpaperWindow(IntPtr childHwnd, IntPtr parentHwnd, Rectangle bounds)
        {
            if (childHwnd == IntPtr.Zero || parentHwnd == IntPtr.Zero) return;
            if (!Win32.IsWindow(childHwnd)) return;
            try
            {
                // Z 序：父窗口含 DefView 时置于 DefView 之下（图标层之后）；
                // 父窗口为纯 WorkerW（经典壁纸层）时保持置顶；其余（Progman 兜底）沉底，避免盖图标。
                IntPtr defView = FindShellDefViewUnderParent(parentHwnd);
                IntPtr insertAfter = defView != IntPtr.Zero
                    ? defView
                    : (Win32.GetClassName(parentHwnd) == "WorkerW" ? Win32.HWND_TOP : Win32.HWND_BOTTOM);
                ToParentClient(parentHwnd, bounds, out int px, out int py);
                Win32.SetWindowPos(childHwnd, insertAfter,
                    px, py, bounds.Width, bounds.Height,
                    Win32.SWP_NOACTIVATE | Win32.SWP_NOOWNERZORDER | Win32.SWP_SHOWWINDOW);

                // 落位校验 + 自愈：SetWindowPos 返回 true 不代表最终落位正确（DPI 换算、
                // 父窗口客户区在移入瞬间变化、以及未知来源的再次移动都可能让实际矩形偏离）。
                // “壁纸偏移到桌面左上角/尺寸缩小”复现时无任何日志证据，这里把期望矩形与
                // 实际矩形比对，不符则立即重设并记录——既当场自愈，又为下次复现留下确凿证据。
                // r17 修正：GetWindowRect 返回**屏幕坐标**，期望值就用屏幕矩形 bounds 比对；
                // 此前拿父客户区坐标 (px,py) 比屏幕坐标，父客户区原点 ≠ 屏幕原点时会误判。
                if (Win32.GetWindowRect(childHwnd, out var wr))
                {
                    int aw = wr.Width, ah = wr.Height;
                    bool offPos = Math.Abs(wr.Left - bounds.X) > 2 || Math.Abs(wr.Top - bounds.Y) > 2;
                    bool offSize = Math.Abs(aw - bounds.Width) > 2 || Math.Abs(ah - bounds.Height) > 2;
                    if (offPos || offSize)
                    {
                        Logger.Log($"[WorkerW] 移入落位校验失败：期望 screen=({bounds.X},{bounds.Y}) size={bounds.Width}x{bounds.Height}（parent-client=({px},{py})），实际 screen=({wr.Left},{wr.Top}) size={aw}x{ah}，立即纠正");
                        Win32.SetWindowPos(childHwnd, insertAfter,
                            px, py, bounds.Width, bounds.Height,
                            Win32.SWP_NOACTIVATE | Win32.SWP_NOOWNERZORDER | Win32.SWP_SHOWWINDOW);
                        if (Win32.GetWindowRect(childHwnd, out var wr2))
                            Logger.Log($"[WorkerW] 纠正后实际 pos=({wr2.Left},{wr2.Top}) size={wr2.Width}x{wr2.Height}");
                    }
                }
                Logger.Log($"[WorkerW] 内容就绪，壁纸窗口移入显示: child=0x{childHwnd.ToInt64():X} parent=0x{parentHwnd.ToInt64():X} pos=({px},{py}) size={bounds.Width}x{bounds.Height} insertAfter=0x{insertAfter.ToInt64():X}");
            }
            catch (Exception ex)
            {
                Logger.Log($"[WorkerW] 壁纸窗口移入显示失败: {ex.Message}");
            }
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

            // 子窗口是否为分层窗口：仅“退化路径（直接挂 Progman）”才会带 WS_EX_LAYERED。
            // 分层子窗口被 DWM 独立合成，需要“摘出→置顶→归位”强制 DWM 重合成；
            // 普通（非分层）子窗口由 DWM 常规合成、自然沉在图标层之后，只需沉底 + 图标层刷新，
            // 绝不能走 TOPMOST 闪现（会瞬间盖住图标 / 吃掉右键）。
            // 注意：必须用 GetWindowLongPtr（返回 IntPtr）取扩展样式，GetWindowLong 返回 int 没有 ToInt64()。
            bool childLayered = (Win32.GetWindowLongPtr(childHwnd, Win32.GWL_EXSTYLE).ToInt64() & Win32.WS_EX_LAYERED) != 0;

            if (!childLayered)
            {
                // —— 主路径（挂到背景 WorkerW / 传统 WorkerW 的普通子窗口）——
                // 【幂等空转】非分层子窗口由 DWM 常规合成，Attach 已完成挂接与 Z 序安排，
                // 就绪后的"移入显示"由 ShowWallpaperWindow 【唯一】负责。
                // 此前这里会再做一次 SetWindowPos（带 SWP_SHOWWINDOW 的重定位 + 沉底），
                // 与 ShowWallpaperWindow 的"升到 TOP"形成竞争：顺序反了会把已可见的新窗口
                // 沉到旧壁纸层之下 → 旧壁纸重新露出 → 旧窗口销毁后再闪回来——这正是
                // "切换闪旧壁纸 / 快速抖动、时好时坏"的根因（日志实证两种顺序都会出现）。
                // 因此这里只做安全归位检查：已挂好则什么都不动。
                if (Win32.GetParent(childHwnd) == parentHwnd)
                {
                    Logger.Log("[WorkerW] 强制 DWM 合成跳过（非分层子窗口已挂好，DWM 常规合成；移入显示由 ShowWallpaperWindow 唯一负责）");
                    return;
                }
                Win32.SetParent(childHwnd, parentHwnd);
                Logger.Log("[WorkerW] 强制 DWM 合成完成（非分层：仅归位 SetParent，不挪位置不改 Z 序）");
                return;
            }

            // —— 退化路径（分层子窗口，直接挂 Progman）——
            // 1. 摘出为顶层窗口（父=桌面），子窗口状态下置顶无效
            Win32.SetParent(childHwnd, IntPtr.Zero);
            // 2. 系统级置顶，强制 DWM 重新合成窗口内容
            Win32.SetWindowPos(childHwnd, Win32.HWND_TOPMOST,
                bounds.X, bounds.Y, bounds.Width, bounds.Height,
                Win32.SWP_NOACTIVATE | Win32.SWP_SHOWWINDOW);

            // 3. 归位回挂载父窗口
            Win32.SetParent(childHwnd, parentHwnd);
            // 归位回挂载父窗口：坐标必须换算成父客户区坐标（与 Attach 一致），否则副屏壁纸会被
            // 重置回错误位置而再次空白/串屏。
            IntPtr insertAfter = Win32.HWND_BOTTOM;
            IntPtr defView = FindShellDefViewUnderParent(parentHwnd);
            if (defView != IntPtr.Zero)
                insertAfter = defView;
            ToParentClient(parentHwnd, bounds, out int px, out int py);
            Win32.SetWindowPos(childHwnd, insertAfter,
                px, py, bounds.Width, bounds.Height,
                Win32.SWP_NOACTIVATE | Win32.SWP_NOOWNERZORDER | Win32.SWP_SHOWWINDOW);

            // 4.1 归位后强制校验 Z 序：SetParent 回挂时 Windows 会把子窗口置到父窗口 Z 序顶部，
            // 若上面的 SetWindowPos 未生效，渲染窗口会盖住桌面图标（DefView）。必须枚举确认
            // child 确实排在 DefView 之后（下方）；若否，则反复纠正直至成功或达到重试上限。
            int fixAttempts = 0;
            while (defView != IntPtr.Zero && fixAttempts < 5 && !IsAfterChild(parentHwnd, childHwnd, defView))
            {
                fixAttempts++;
                Logger.Log($"[WorkerW] Z 序校验失败（第{fixAttempts}次），重新将 child 置于 DefView 下方: 0x{defView.ToInt64():X}");
                // SWP_NOMOVE | SWP_NOSIZE：只调整 Z 序，绝不改动位置/尺寸。
                // 此前这里写死 (0,0,w,h)，多屏共享承载层时会把本屏窗口挪到父客户区原点
                // （= 虚拟桌面左上角，通常是左侧副屏），造成两块屏的壁纸叠在同一块屏上。
                Win32.SetWindowPos(childHwnd, defView,
                    0, 0, 0, 0,
                    Win32.SWP_NOMOVE | Win32.SWP_NOSIZE |
                    Win32.SWP_NOACTIVATE | Win32.SWP_NOOWNERZORDER | Win32.SWP_SHOWWINDOW);
                Thread.Sleep(50);
            }
            if (defView != IntPtr.Zero)
                Logger.Log($"[WorkerW] Z 序校验通过（child 位于 DefView 下方）: child=0x{childHwnd.ToInt64():X}, defView=0x{defView.ToInt64():X}");
            else
                Logger.Log($"[WorkerW] Z 序校验跳过（未找到 DefView），child 已沉底不遮挡图标");

            // 5. 【不再做图标层重绘】——对 DefView 做 SW_HIDE→SW_SHOW 会让整个桌面（图标层 +
            //    壁纸层）重绘一次，表现为"切换壁纸闪一下、桌面图标也闪一下"，是老版本已修复的
            //    闪屏回归。此退化路径本就极少走到（raised desktop 下基本都走非分层主路径），
            //    去掉后归位即完成，无闪烁。
            Logger.Log("[WorkerW] 强制 DWM 合成完成（摘出→置顶→归位，不做图标层重绘）");
        }

        private static readonly object _iconRefreshLock = new object();

        /// <summary>复核并自愈壁纸子窗口的挂载状态（r17）。切换时一次性设置好的挂载关系会被系统的
        /// 异步副作用破坏——SetParent 回挂时 Windows 把子窗口置到父窗口 Z 序顶部、explorer 重建
        /// SHELLDLL_DefView、桌面重绘、另一份程序副本互抢同一承载层——破坏后的现象正是用户报的：
        /// ① 桌面图标被壁纸覆盖、右键落到壁纸窗口上（child 跑到 DefView 之上，或窗口被重新带上
        ///    WS_EX_LAYERED 被 DWM 独立合成到图标层之上）；② 壁纸偏移/只占左上角一块（child 被挪位）。
        /// 本方法幂等、代价极低，异常只记日志，绝不影响壁纸显示。
        /// 返回 true 表示复核通过（或已自愈成功），false 表示发现问题并做了纠正。</summary>
        public static bool EnsurePlacement(IntPtr childHwnd, IntPtr parentHwnd, Rectangle bounds, string tag)
        {
            if (childHwnd == IntPtr.Zero || parentHwnd == IntPtr.Zero) return true;
            try
            {
                if (!Win32.IsWindow(childHwnd) || !Win32.IsWindow(parentHwnd)) return true;

                // 1) 父窗口归属：child 必须是承载层的子窗口。若被摘出（父=桌面/0），它会变成顶层窗口
                //    盖住整个桌面（图标被覆盖、右键被吃）。
                var curParent = Win32.GetParent(childHwnd);
                if (curParent != parentHwnd)
                {
                    Logger.Log($"[WorkerW] 复核({tag})：child 父窗口不符（当前=0x{curParent.ToInt64():X} 期望=0x{parentHwnd.ToInt64():X}）→ 重新挂回承载层");
                    Win32.SetParent(childHwnd, parentHwnd);
                }

                // 2) 分层样式：分层（WS_EX_LAYERED）子窗口会被 DWM 独立合成到图标层(DefView)之上，
                //    无论 Z 序如何排布都会盖住图标、吃掉右键。这里作为硬保证：发现就清除。
                long ex = Win32.GetWindowLongPtr(childHwnd, Win32.GWL_EXSTYLE).ToInt64();
                if ((ex & Win32.WS_EX_LAYERED) != 0)
                {
                    Logger.Log($"[WorkerW] 复核({tag})：child 被带上 WS_EX_LAYERED（会盖住图标/吃掉右键）→ 清除");
                    Win32.SetWindowLong(childHwnd, Win32.GWL_EXSTYLE, (int)(ex & ~(long)Win32.WS_EX_LAYERED));
                    Win32.SetWindowPos(childHwnd, IntPtr.Zero, 0, 0, 0, 0,
                        Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOZORDER | Win32.SWP_FRAMECHANGED);
                }

                // 3) Z 序：child 必须沉在 DefView（图标层）之下。若跑到上方，图标会被覆盖、右键失效。
                IntPtr defView = FindShellDefViewUnderParent(parentHwnd);
                if (defView != IntPtr.Zero && !IsAfterChild(parentHwnd, childHwnd, defView))
                {
                    Logger.Log($"[WorkerW] 复核({tag})：child 位于图标层之上（图标会被覆盖、右键被吃）→ 沉到 DefView 下方");
                    Win32.SetWindowPos(childHwnd, defView, 0, 0, 0, 0,
                        Win32.SWP_NOMOVE | Win32.SWP_NOSIZE |
                        Win32.SWP_NOACTIVATE | Win32.SWP_NOOWNERZORDER | Win32.SWP_SHOWWINDOW);
                    Thread.Sleep(40);
                    if (!IsAfterChild(parentHwnd, childHwnd, defView))
                    {
                        Logger.Log($"[WorkerW] 复核({tag})：沉到 DefView 下方仍未生效 → 直接沉到 Z 序底部（HWND_BOTTOM）");
                        Win32.SetWindowPos(childHwnd, Win32.HWND_BOTTOM, 0, 0, 0, 0,
                            Win32.SWP_NOMOVE | Win32.SWP_NOSIZE |
                            Win32.SWP_NOACTIVATE | Win32.SWP_NOOWNERZORDER | Win32.SWP_SHOWWINDOW);
                    }
                }

                // 4) 落位矩形：GetWindowRect 返回屏幕坐标，期望值就是屏幕矩形 bounds。
                if (Win32.GetWindowRect(childHwnd, out var wr))
                {
                    bool off = Math.Abs(wr.Left - bounds.X) > 2 || Math.Abs(wr.Top - bounds.Y) > 2 ||
                               Math.Abs(wr.Width - bounds.Width) > 2 || Math.Abs(wr.Height - bounds.Height) > 2;
                    if (off)
                    {
                        ToParentClient(parentHwnd, bounds, out int px, out int py);
                        Logger.Log($"[WorkerW] 复核({tag})：落位偏差（实际 pos=({wr.Left},{wr.Top}) size={wr.Width}x{wr.Height}，期望 screen=({bounds.X},{bounds.Y}) {bounds.Width}x{bounds.Height} / parent-client=({px},{py})）→ 重新落位");
                        Win32.SetWindowPos(childHwnd, IntPtr.Zero, px, py, bounds.Width, bounds.Height,
                            Win32.SWP_NOACTIVATE | Win32.SWP_NOZORDER | Win32.SWP_SHOWWINDOW);
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                Logger.Log($"[WorkerW] 复核({tag}) 异常: {ex.Message}");
                return true;
            }
        }

        /// <summary>
        /// 安全触发图标层（SHELLDLL_DefView）重绘。
        /// ⚠️ DefView 是**所有屏幕 + 所有虚拟桌面共享**的同一个窗口，绝不能把它留在隐藏态，
        /// 否则全部桌面的桌面图标都会消失，直到下次重绘（"设置第二个桌面壁纸后所有桌面都看不到
        /// 图标、解除才恢复"的根因）。
        /// 原实现 SW_HIDE→SW_SHOWNORMAL 在快速连续切换虚拟桌面（多次 ForceDwmComposition 并发）
        /// 时会交错执行，两次 hide 配一次 show 就可能把 DefView 卡在隐藏态。
        /// 这里：① 全局加锁把重绘串行化；② hide 后短暂让出消息处理，避免 show 被合并；
        /// ③ 显式用 SW_SHOW（不改尺寸/位置/激活）；④ 校验可见性并自愈重试。
        /// </summary>
        private static void SafeIconLayerRefresh(IntPtr defView, string logTag)
        {
            if (defView == IntPtr.Zero)
            {
                Logger.Log("[WorkerW] 未找到 SHELLDLL_DefView，跳过重绘");
                return;
            }
            lock (_iconRefreshLock)
            {
                try
                {
                    Logger.Log($"[WorkerW] {logTag}: DefView=0x{defView.ToInt64():X}（SW_HIDE→SW_SHOW）");
                    Win32.ShowWindow(defView, Win32.SW_HIDE);
                    System.Threading.Thread.Sleep(16); // 让 hide 先被处理，避免与 show 合并
                    Win32.ShowWindow(defView, Win32.SW_SHOW);
                    // 自愈：若因并发/系统异步副作用仍不可见，则重试直至可见
                    for (int i = 0; i < 6 && !Win32.IsWindowVisible(defView); i++)
                    {
                        System.Threading.Thread.Sleep(20);
                        Win32.ShowWindow(defView, Win32.SW_SHOW);
                    }
                    if (!Win32.IsWindowVisible(defView))
                        Logger.Log("[WorkerW] 警告：DefView 重绘后仍不可见（桌面图标可能被隐藏）");
                }
                catch (Exception ex)
                {
                    Logger.Log($"[WorkerW] 图标层重绘异常: {ex.Message}");
                }
            }
        }

        public static bool IsValid(IntPtr hWnd) => hWnd != IntPtr.Zero && Win32.IsWindow(hWnd);

        /// <summary>强制桌面图标层/壁纸层重绘：对 SHELLDLL_DefView 执行 SW_HIDE→SW_SHOWNORMAL。
        /// 解除壁纸（尤其 WebProvider 这种从未走 ForceDwmComposition 的层）后，DWM 合成状态可能
        /// 停留导致桌面黑屏（系统壁纸虽已记录为原壁纸、恢复逻辑正确跳过，却未重绘）。此刷新
        /// 触发 DWM 重新合成底层静态壁纸，使其透出。等效于手动"显示桌面图标"开关。</summary>
        public static void RefreshDesktop()
        {
            try
            {
                IntPtr defView = FindTopLevelDefView();
                if (defView != IntPtr.Zero)
                    SafeIconLayerRefresh(defView, "触发桌面刷新");
                else
                    Logger.Log("[WorkerW] 未找到 SHELLDLL_DefView，跳过桌面刷新");
            }
            catch (Exception ex)
            {
                Logger.Log($"[WorkerW] 桌面刷新异常: {ex.Message}");
            }
        }

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

        /// <summary>仅摘除并销毁本屏自己的子窗口。多屏扩展下所有屏共享同一个缓存的 Progman
        /// 承载层，解除某一屏时绝不能 DetachChildren 整个承载层——否则会把其他屏幕仍在
        /// 播放的壁纸窗口一起跨线程拆掉，WebView2 随后 Dispose 会挂死 UI。</summary>
        public static void DetachChildWindow(IntPtr child)
        {
            if (child == IntPtr.Zero || !Win32.IsWindow(child)) return;
            Win32.GetWindowThreadProcessId(child, out uint pid);
            if ((int)pid != Process.GetCurrentProcess().Id) return;
            try
            {
                Win32.SetParent(child, IntPtr.Zero);
                Win32.DestroyWindow(child);
            }
            catch { /* ignore */ }
        }

        /// <summary>把屏幕虚拟桌面坐标换算成父窗口客户区坐标。
        /// 子窗口 SetParent 后，SetWindowPos 的 x/y 相对父窗口客户区原点；Progman 覆盖整个
        /// 虚拟桌面，其客户区原点 = 虚拟桌面左上角。外接屏位于主屏左侧/上方时该原点为负
        /// （如 (-1920,0)），直接用虚拟坐标会整体偏移一块屏。</summary>
        public static void ToParentClient(IntPtr parent, System.Drawing.Rectangle bounds, out int x, out int y)
        {
            var pt = new Win32.POINT(0, 0);
            if (!Win32.ClientToScreen(parent, ref pt))
            { pt.X = 0; pt.Y = 0; }
            x = bounds.X - pt.X;
            y = bounds.Y - pt.Y;
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

        /// <summary>检测当前桌面是否为 Win11 24H2+ raised desktop 模式。
        /// 判据：Progman 带 WS_EX_NOREDIRECTIONBITMAP 扩展样式（Lively 同款判据）。
        /// raised desktop 模式下真正承载桌面的是 Progman（DWM 直接合成其 WS_EX_LAYERED
        /// 子窗口），传统孤儿 WorkerW 注入失效，必须走 raised desktop 兼容分支。
        /// 公开供 WallpaperManager 判定是否需要为窗口型 Provider 强制触发一次 DWM 合成。</summary>
        public static bool IsRaisedDesktop()
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
        public static IntPtr FindTopLevelDefView()
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

        /// <summary>
        /// 定位 Progman 之下的“背景 WorkerW”（系统静态壁纸所在的层）。
        /// 这是 raised desktop（Win11 24H2+）下壁纸应挂载的层：它位于图标层 DefView 之下，
        /// 把壁纸作为它的子窗口，壁纸自然沉到图标层后面——图标常显、右键自然落到桌面，
        /// 无需 WS_EX_TRANSPARENT（该样式会让分层窗口画到 DefView 之上、盖住图标，是上一轮回归的根因）。
        /// 同款思路即 Wallpaper Engine / Lively 在 Win11 上的做法。
        /// 选取 Z 序最底（最靠后）的不含 DefView 的 WorkerW，即系统背景壁纸层。
        /// 找不到时返回 IntPtr.Zero，调用方退化为挂 Progman（旧行为）。
        /// </summary>
        private static IntPtr FindBackgroundWorkerW(IntPtr progman)
        {
            if (progman == IntPtr.Zero) return IntPtr.Zero;
            // 轮询重试：explorer 刚启动 / 显示拓扑刚变化（如切到“仅在N显示”）时桌面窗口结构会被重建，
            // 期间可能瞬时没有可用的 WorkerW。这里轮询 + 逐级兜底 + 最后的 0x052C 生成，
            // 尽最大可能拿到一个“可见且不盖图标”的承载层，而不是退回 Progman（raised desktop 下不可见）。
            bool spawned = false;
            for (int attempt = 0; attempt < 24; attempt++)
            {
                IntPtr hit = ProbeWorkerWLayer(progman, attempt);
                if (hit != IntPtr.Zero) return hit;

                // 所有策略都没命中：尝试经典的 0x052C，让 explorer 生成一个孤儿 WorkerW（壁纸层）。
                if (!spawned)
                {
                    spawned = true;
                    Logger.Log("[WorkerW] 未找到任何 WorkerW，尝试向 Progman 发送 0x052C 生成壁纸层");
                    try
                    {
                        Win32.SendMessageTimeout(progman, Win32.WM_SPAWN_WORKER,
                            IntPtr.Zero, IntPtr.Zero, Win32.SMTO_NORMAL, 1000, out _);
                    }
                    catch (Exception ex) { Logger.Log($"[WorkerW] 发送 0x052C 异常: {ex.Message}"); }
                }

                if (attempt < 23)
                    Thread.Sleep(80);
            }

            DumpDesktopStructure(progman);
            Logger.Log("[WorkerW] FindBackgroundWorkerW 最终未找到任何可用 WorkerW，退回 Progman（raised desktop 下可能不可见）");
            return IntPtr.Zero;
        }

        /// <summary>
        /// 一次完整的承载层探测（按优先级逐级兜底）。命中返回句柄，全部落空返回 IntPtr.Zero。
        /// ① Progman 直接子窗口里“不含 DefView 的 WorkerW”（取 Z 序最底 = 系统背景壁纸层，首选）；
        /// ② Progman 子树里“不含 DefView 的 WorkerW”（少数拓扑下不是直接子窗口）；
        /// ③ 顶层窗口里“不含 DefView 的 WorkerW”（部分拓扑下背景壁纸层挂在顶层而非 Progman 下）；
        /// ④ “含 DefView 的 WorkerW”（经典结构 Progman→WorkerW→DefView）——挂其内部、DefView 之下，
        ///    同样落在图标层之后，而且是可见的（WorkerW 是普通窗口，子窗口由 DWM 常规合成；
        ///    直接挂 Progman 在 raised desktop 下会被 DefView 完全挡住 → 不显示）。
        /// </summary>
        private static IntPtr ProbeWorkerWLayer(IntPtr progman, int attempt)
        {
            // 所有候选必须 IsWindowVisible：explorer 在 Win11 上维护着若干【隐藏占位 WorkerW】，
            // 挂到隐藏窗口上壁纸必然不可见（仅在N切换后的典型故障）；此前顶层兜底把它们当
            // 背景层选中，正是"仅2不显示壁纸"的直接原因。可见性是承载层有效的硬条件。
            // ① Progman 直接子窗口，取 Z 序最底（列表由顶到底，逐个覆盖）
            var children = EnumChildrenZOrder(progman);
            IntPtr best = IntPtr.Zero;
            foreach (var h in children)
            {
                if (Win32.GetClassName(h) == "WorkerW" && !Win32.HasShellDefViewDescendant(h)
                    && Win32.IsWindowVisible(h))
                    best = h;
            }
            if (best != IntPtr.Zero)
            {
                Logger.Log(attempt == 0
                    ? $"[WorkerW] FindBackgroundWorkerW 选用背景 WorkerW: 0x{best.ToInt64():X}"
                    : $"[WorkerW] FindBackgroundWorkerW 第{attempt + 1}次轮询命中背景 WorkerW: 0x{best.ToInt64():X}");
                return best;
            }

            // ② Progman 子树递归
            IntPtr nested = FindWorkerWWithoutDefView(progman);
            if (nested != IntPtr.Zero)
            {
                Logger.Log($"[WorkerW] FindBackgroundWorkerW 递归命中背景 WorkerW: 0x{nested.ToInt64():X}");
                return nested;
            }

            // ③ 顶层窗口
            IntPtr top = FindTopLevelWorkerWWithoutDefView();
            if (top != IntPtr.Zero)
            {
                Logger.Log($"[WorkerW] FindBackgroundWorkerW 顶层命中背景 WorkerW: 0x{top.ToInt64():X}");
                return top;
            }

            // ④ 含 DefView 的 WorkerW（经典结构兜底）。
            //    只在轮询一段时间（~0.6s）后仍没找到“纯背景 WorkerW”时才启用：
            //    优先给 explorer 重建桌面结构留出时间，避免过早接受次优承载层。
            if (attempt >= 8)
            {
                IntPtr iconWorker = FindWorkerWContainingDefView(progman);
                if (iconWorker != IntPtr.Zero)
                {
                    Logger.Log($"[WorkerW] FindBackgroundWorkerW 未找到纯背景 WorkerW，改用“含 DefView 的 WorkerW”: 0x{iconWorker.ToInt64():X}（挂其内部、DefView 之下）");
                    return iconWorker;
                }
            }

            return IntPtr.Zero;
        }

        /// <summary>顶层窗口中 Z 序最底的一个"可见、不含 DefView 的 WorkerW"（背景壁纸层）。</summary>
        private static IntPtr FindTopLevelWorkerWWithoutDefView()
        {
            IntPtr found = IntPtr.Zero;
            Win32.EnumWindows((hwnd, _) =>
            {
                if (Win32.GetClassName(hwnd) != "WorkerW") return true;
                if (Win32.HasShellDefViewDescendant(hwnd)) return true;
                if (!Win32.IsWindowVisible(hwnd)) return true; // 隐藏占位 WorkerW 绝不可用
                found = hwnd; // 继续枚举，取最后一个（Z 序最底）
                return true;
            }, IntPtr.Zero);
            return found;
        }

        /// <summary>“含 SHELLDLL_DefView 的 WorkerW”（经典结构 Progman→WorkerW→DefView）：
        /// 先看 Progman 直接子窗口，再看顶层窗口；返回 Z 序最底的一个。</summary>
        private static IntPtr FindWorkerWContainingDefView(IntPtr progman)
        {
            IntPtr found = IntPtr.Zero;
            foreach (var h in EnumChildrenZOrder(progman))
            {
                if (Win32.GetClassName(h) == "WorkerW" && Win32.HasShellDefViewDescendant(h)
                    && Win32.IsWindowVisible(h))
                    found = h;
            }
            if (found != IntPtr.Zero) return found;
            Win32.EnumWindows((hwnd, _) =>
            {
                if (Win32.GetClassName(hwnd) != "WorkerW") return true;
                if (!Win32.HasShellDefViewDescendant(hwnd)) return true;
                if (!Win32.IsWindowVisible(hwnd)) return true; // 隐藏占位 WorkerW 绝不可用
                found = hwnd;
                return true;
            }, IntPtr.Zero);
            return found;
        }

        /// <summary>探测全部失败时转储桌面窗口结构（只在失败路径调用，便于定位“为什么找不到承载层”）。</summary>
        private static void DumpDesktopStructure(IntPtr progman)
        {
            try
            {
                Logger.Log($"[WorkerW] === 桌面结构转储（诊断）Progman=0x{progman.ToInt64():X} exStyle=0x{Win32.GetWindowLongPtr(progman, Win32.GWL_EXSTYLE).ToInt64():X} ===");
                var children = EnumChildrenZOrder(progman);
                Logger.Log($"[WorkerW] Progman 直接子窗口 {children.Count} 个（Z 序由顶到底）:");
                for (int i = 0; i < children.Count; i++)
                {
                    var h = children[i];
                    Logger.Log($"[WorkerW]   [{i}] 0x{h.ToInt64():X} class={Win32.GetClassName(h)} hasDefView={Win32.HasShellDefViewDescendant(h)} visible={Win32.IsWindowVisible(h)} exStyle=0x{Win32.GetWindowLongPtr(h, Win32.GWL_EXSTYLE).ToInt64():X}");
                }
                int cnt = 0;
                Win32.EnumWindows((hwnd, _) =>
                {
                    string cls = Win32.GetClassName(hwnd);
                    if (cls == "WorkerW" || cls == "Progman")
                    {
                        cnt++;
                        Logger.Log($"[WorkerW]   顶层 0x{hwnd.ToInt64():X} class={cls} hasDefView={Win32.HasShellDefViewDescendant(hwnd)} visible={Win32.IsWindowVisible(hwnd)} exStyle=0x{Win32.GetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE).ToInt64():X}");
                    }
                    return true;
                }, IntPtr.Zero);
                Logger.Log($"[WorkerW] 顶层 WorkerW/Progman 共 {cnt} 个（诊断结束）");
            }
            catch (Exception ex)
            {
                Logger.Log($"[WorkerW] 桌面结构转储失败: {ex.Message}");
            }
        }

        /// <summary>递归在 root 子树中查找第一个“不含 SHELLDLL_DefView 后代”的 WorkerW（背景壁纸层）。</summary>
        private static IntPtr FindWorkerWWithoutDefView(IntPtr root)
        {
            IntPtr found = IntPtr.Zero;
            Win32.EnumChildWindows(root, (child, _) =>
            {
                if (found != IntPtr.Zero) return false;
                if (Win32.GetClassName(child) == "WorkerW" && !Win32.HasShellDefViewDescendant(child)
                    && Win32.IsWindowVisible(child)) // 隐藏占位 WorkerW 绝不可用
                {
                    found = child;
                    return false;
                }
                return true;
            }, IntPtr.Zero);
            return found;
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
