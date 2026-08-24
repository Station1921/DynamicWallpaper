using System;
using System.Drawing;
using DynamicWallpaper.Models;

namespace DynamicWallpaper.Providers
{
    /// <summary>
    /// 所有壁纸类型统一实现的接口，由 WallpaperManager 统一调度。
    /// 新增壁纸类型只需新增一个 IWallpaperProvider 实现（如未来的 Web/3D）。
    /// </summary>
    public interface IWallpaperProvider : IDisposable
    {
        WallpaperType Type { get; }

        /// <summary>渲染窗口句柄（用于挂接到 WorkerW）。</summary>
        IntPtr Handle { get; }

        /// <summary>创建并显示渲染窗口，加载指定内容。</summary>
        void Show(string path, Rectangle bounds);

        /// <summary>将渲染窗口挂接到 WorkerW 容器层。</summary>
        void AttachTo(IntPtr workerw, Rectangle bounds);

        void Play();
        void Pause();
        void SetMuted(bool muted);
    }
}
