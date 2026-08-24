using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using DynamicWallpaper.Core;

namespace DynamicWallpaper.Providers
{
    /// <summary>
    /// 通过 Windows Shell（IShellItemImageFactory）提取文件缩略图，
    /// 可拿到资源管理器为视频生成的“首帧”缩略图。失败返回 null。
    /// </summary>
    internal static class ShellThumbnail
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct SIZE
        {
            public int cx;
            public int cy;
        }

        [Flags]
        private enum SIIGBF : uint
        {
            ResizeToFit = 0x00000000,
            BiggerSizeOk = 0x00000001,
            MemoryOnly = 0x00000002,
            IconOnly = 0x00000004,
            ThumbnailOnly = 0x00000008,
            InCacheOnly = 0x00000010,
            CropToSquare = 0x00000020,
            WideThumbnails = 0x00000040,
            IconAndThumbnail = 0x00000080,
            ScaleUp = 0x00000100
        }

        private static readonly Guid IID_IShellItemImageFactory = new("bcc18b79-ba16-442f-80c4-8a59c30c463b");
        private static readonly Guid IID_IShellItem = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");
        private static readonly Guid BHID_SFUIObject = new("3981e225-f559-11d3-8e3a-00c04f6837d5");

        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
        private interface IShellItemImageFactory
        {
            [PreserveSig]
            int GetImage(SIZE size, SIIGBF flags, out IntPtr phbm);
        }

        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
        private interface IShellItem
        {
            [PreserveSig]
            int BindToHandler(IntPtr pbc, [MarshalAs(UnmanagedType.LPStruct)] Guid bhid,
                              [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
                              [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
        private static extern int SHCreateItemFromParsingName(
            [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
            IntPtr pbc,
            [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out IShellItem ppv);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

        public static BitmapSource? GetThumbnail(string path, int size)
        {
            try
            {
                int hr = SHCreateItemFromParsingName(path, IntPtr.Zero, IID_IShellItem, out var item);
                if (hr != 0 || item == null)
                {
                    Logger.Log($"[ShellThumbnail] SHCreateItemFromParsingName 失败 hr=0x{hr:X8}: {path}");
                    return null;
                }

                hr = item.BindToHandler(IntPtr.Zero, BHID_SFUIObject, IID_IShellItemImageFactory, out var factory);
                if (hr != 0 || factory == null)
                {
                    Logger.Log($"[ShellThumbnail] BindToHandler 失败 hr=0x{hr:X8}: {path}");
                    return null;
                }

                var sz = new SIZE { cx = size, cy = size };
                // 优先尝试缩略图缓存；BiggerSizeOk 允许返回更大尺寸以提高成功率
                hr = factory.GetImage(sz, SIIGBF.ThumbnailOnly | SIIGBF.BiggerSizeOk, out var hBitmap);
                if (hr != 0 || hBitmap == IntPtr.Zero)
                {
                    // 某些格式/系统上 ThumbnailOnly 会失败，改用 ResizeToFit + BiggerSizeOk
                    hr = factory.GetImage(sz, SIIGBF.ResizeToFit | SIIGBF.BiggerSizeOk, out hBitmap);
                    if (hr != 0 || hBitmap == IntPtr.Zero)
                    {
                        Logger.Log($"[ShellThumbnail] GetImage 失败 hr=0x{hr:X8}: {path}");
                        return null;
                    }
                }

                try
                {
                    var src = Imaging.CreateBitmapSourceFromHBitmap(
                        hBitmap, IntPtr.Zero, Int32Rect.Empty,
                        BitmapSizeOptions.FromEmptyOptions());
                    src.Freeze();
                    return src;
                }
                finally
                {
                    DeleteObject(hBitmap);
                }
            }
            catch
            {
                return null;
            }
        }
    }
}
