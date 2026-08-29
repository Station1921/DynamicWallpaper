using System;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
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

        /// <summary>
        /// 等待渲染内容真正就绪（如视频首帧解码完成、WebView2 初始化并注入页面完成）。
        /// 默认实现直接返回已完成；需要等待的 Provider 可覆盖实现。
        /// </summary>
        Task WaitReadyAsync(TimeSpan timeout, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
