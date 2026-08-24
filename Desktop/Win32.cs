using System;
using System.Runtime.InteropServices;
using System.Text;

namespace DynamicWallpaper.Desktop
{
    internal static class Win32
    {
        public const int WM_SPAWN_WORKER = 0x052C;
        public const int SMTO_NORMAL = 0x0000;
        public const int SMTO_ABORTIFHUNG = 0x0002;

        public const uint SWP_NOACTIVATE = 0x0010;
        public const uint SWP_NOZORDER = 0x0004;
        public const uint SWP_NOSIZE = 0x0001;
        public const uint SWP_NOMOVE = 0x0002;
        public const uint SWP_NOOWNERZORDER = 0x0200;
        public const uint SWP_SHOWWINDOW = 0x0040;
        public const uint SWP_HIDEWINDOW = 0x0080;
        public const uint SWP_FRAMECHANGED = 0x0020;

        public const int SPI_SETDESKWALLPAPER = 20;
        public const int SPIF_UPDATEINIFILE = 0x01;
        public const int SPIF_SENDCHANGE = 0x02;

        public static readonly IntPtr HWND_BOTTOM = new IntPtr(1);
        public static readonly IntPtr HWND_TOP = new IntPtr(0);
        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        public static readonly IntPtr HWND_BROADCAST = new IntPtr(0xFFFF);

        public const int GWL_STYLE = -16;
        public const int GWL_EXSTYLE = -20;

        public const int WS_CHILD = 0x40000000;
        public const int WS_POPUP = unchecked((int)0x80000000);
        public const int WS_VISIBLE = 0x10000000;
        public const int WS_CAPTION = 0x00C00000;
        public const int WS_THICKFRAME = 0x00040000;
        public const int WS_SYSMENU = 0x00080000;
        public const int WS_MINIMIZEBOX = 0x00020000;
        public const int WS_MAXIMIZEBOX = 0x00010000;

        public const int WS_EX_NOACTIVATE = 0x08000000;
        public const int WS_EX_TOOLWINDOW = 0x00000080;
        public const int WS_EX_APPWINDOW = 0x00040000;
        public const int WS_EX_LAYERED = 0x00080000;
        // Win11 24H2/25H2 raised desktop 模式标志：Progman 带此扩展样式时，桌面渲染走
        // "raised desktop" 架构（DWM 直接合成 Progman 的 WS_EX_LAYERED 子窗口），
        // 传统 WorkerW 注入失效。用于判断是否需要走 raised desktop 兼容分支。
        public const int WS_EX_NOREDIRECTIONBITMAP = 0x00200000;

        // SetLayeredWindowAttributes 的 flags（Win11 24H2/25H2 raised desktop 兼容方案需要）
        public const int LWA_COLORKEY = 0x00000001;
        public const int LWA_ALPHA = 0x00000002;

        public const int SW_HIDE = 0;
        public const int SW_SHOWNORMAL = 1;
        public const int SW_SHOW = 5;
        public const int SW_RESTORE = 9;

        public const int GW_OWNER = 4;
        public const int GW_CHILD = 5;
        public const int GW_HWNDNEXT = 2;
        public const int GW_HWNDPREV = 3;
        public const int GW_HWNDFIRST = 0;

        public const int GA_PARENT = 1;
        public const int GA_ROOT = 2;
        public const int GA_ROOTOWNER = 3;

        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr FindWindowEx(IntPtr hWndParent, IntPtr hWndChildAfter, string? lpszClass, string? lpszWindow);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        public static extern IntPtr SendMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        public static extern IntPtr SendMessageTimeout(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam,
            int fuFlags, int uTimeout, out IntPtr lpdwResult);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        // 64 位指针宽度的窗口长整型读取（Win11 24H2+ 判断 Progman 的 WS_EX_NOREDIRECTIONBITMAP
        // 扩展样式时需要；扩展样式位都在低 32 位，但用 GetWindowLongPtr 保持与平台指针宽度一致，
        // 避免 64 位下 GetWindowLong 截断语义差异）。EntryPoint 显式用 W 后缀的导出。
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
        public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetLayeredWindowAttributes(IntPtr hWnd, uint crKey, byte bAlpha, uint dwFlags);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr GetWindow(IntPtr hWnd, int uCmd);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr GetParent(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr GetAncestor(IntPtr hWnd, int gaFlags);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        public static extern int GetClassName(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool DestroyWindow(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        public static extern bool SystemParametersInfo(int uAction, int uParam, string lpvParam, int fuWinIni);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        // ===== IDesktopWallpaper（Windows 8+ 官方桌面壁纸 API）=====
        // 替代 SPI_SETDESKWALLPAPER：调用立即返回（异步生效），实测不再阻塞 2~3 秒。

        [ComImport, Guid("C2CF3110-460E-4fc1-B9D0-8A1C0C9CC4BD")]
        private class DesktopWallpaper { }

        [ComImport, Guid("B92B56A9-8B55-4E14-9A89-0199BBB6F93B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDesktopWallpaper
        {
            void SetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string monitorID, [MarshalAs(UnmanagedType.LPWStr)] string wallpaper);
            [return: MarshalAs(UnmanagedType.LPWStr)] string GetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string monitorID);
            [return: MarshalAs(UnmanagedType.LPWStr)] string GetMonitorDevicePathAt(uint monitorIndex);
            void GetMonitorDevicePathCount(out uint count);
            void GetMonitorRECT([MarshalAs(UnmanagedType.LPWStr)] string monitorID, out RECT rc);
            void SetBackgroundColor(uint color);
            uint GetBackgroundColor();
            void SetPosition([MarshalAs(UnmanagedType.LPWStr)] string monitorID, int position);
            int GetPosition([MarshalAs(UnmanagedType.LPWStr)] string monitorID);
            void SetSlideshow(IntPtr items);
            IntPtr GetSlideshow();
            void SetSlideshowOptions(int options, uint interval);
            void GetSlideshowOptions(out int options, out uint interval);
            void AdvanceSlideshow([MarshalAs(UnmanagedType.LPWStr)] string monitorID, int direction);
            void GetStatus(out int status);
            void Enable(bool enable);
        }

        /// <summary>通过 IDesktopWallpaper 设置系统壁纸（monitorID 传空串 = 应用到所有显示器）。
        /// 该 API 异步生效、调用立即返回，避免 SPI_SETDESKWALLPAPER 同步阻塞 UI/后台线程数秒。</summary>
        public static void SetDesktopWallpaper(string wallpaperPath)
        {
            var dw = (IDesktopWallpaper)new DesktopWallpaper();
            dw.SetWallpaper("", wallpaperPath);
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;

            public int Width => Right - Left;
            public int Height => Bottom - Top;
        }

        public static string GetClassName(IntPtr hWnd)
        {
            var sb = new StringBuilder(256);
            GetClassName(hWnd, sb, sb.Capacity);
            return sb.ToString();
        }

        public static bool HasShellDefViewDescendant(IntPtr hWnd)
        {
            bool found = false;
            IntPtr defView = FindWindowEx(hWnd, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (defView != IntPtr.Zero) return true;

            EnumChildWindows(hWnd, (child, _) =>
            {
                if (GetClassName(child) == "SHELLDLL_DefView")
                {
                    found = true;
                    return false;
                }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        #region DWM 窗口外观（暗色标题栏 / 圆角 / 液态玻璃）

        public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        public const int DWMWA_CAPTION_COLOR = 35;
        public const int DWMWA_TEXT_COLOR = 36;
        public const int DWMWA_SYSTEMBACKDROP_TYPE = 38;

        public const int DWMWCP_DEFAULT = 0;
        public const int DWMWCP_DONOTROUND = 1;
        public const int DWMWCP_ROUND = 2;
        public const int DWMWCP_ROUNDSMALL = 3;

        public const int DWMSBT_AUTO = 0;
        public const int DWMSBT_NONE = 1;
        public const int DWMSBT_MAINWINDOW = 2;      // Mica
        public const int DWMSBT_TRANSIENTWINDOW = 3; // Mica Alt
        public const int DWMSBT_TABBEDWINDOW = 4;    // Acrylic（液态玻璃）

        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        private static void SetDwmAttr(IntPtr hwnd, int attr, int value)
        {
            try { DwmSetWindowAttribute(hwnd, attr, ref value, sizeof(int)); } catch { }
        }

        /// <summary>应用 DWM 窗口外观：暗色标题栏（标题栏颜色与主体一致、标题文字白色）+ Win11 窗口圆角。</summary>
        public static void ApplyDwmDarkChrome(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;
            SetDwmAttr(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, 1);
            // COLORREF = 0x00BBGGRR：#1B1B22 → R=0x1B G=0x1B B=0x22 → 0x00221B1B
            SetDwmAttr(hwnd, DWMWA_CAPTION_COLOR, 0x00221B1B);
            SetDwmAttr(hwnd, DWMWA_TEXT_COLOR, 0x00FFFFFF);
            SetDwmAttr(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, DWMWCP_ROUND);
        }

        /// <summary>设置系统背景材质（液态玻璃）。优先 Acrylic(4)，失败自动回退 Mica Alt(3) → Mica(2)。
        /// 返回实际采用的 backdrop 类型；未生效返回 -1。</summary>
        public static int SetWindowBackdrop(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return -1;
            int[] candidates = { DWMSBT_TABBEDWINDOW, DWMSBT_TRANSIENTWINDOW, DWMSBT_MAINWINDOW };
            foreach (int candidate in candidates)
            {
                int value = candidate;
                int hr = DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref value, sizeof(int));
                if (hr == 0)
                {
                    // 部分系统对不支持的属性仍返回 S_OK，回读校验确保真实生效
                    int readBack = -1;
                    DwmGetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref readBack, sizeof(int));
                    if (readBack == candidate) return candidate;
                }
            }
            return -1;
        }

        #endregion
    }
}
