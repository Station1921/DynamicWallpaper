using System.ComponentModel;

namespace DynamicWallpaper.Models
{
    /// <summary>在线壁纸站点上抓取到的壁纸条目（尚未下载到本地）。</summary>
    public class OnlineWallpaperItem : INotifyPropertyChanged
    {
        public string Title { get; set; } = "";
        public string ThumbnailUrl { get; set; } = "";

        private string? _previewUrl;
        /// <summary>鼠标悬停时播放的动画预览地址（例如 netbian 动态壁纸的高清 GIF、livewallpapers4free 的 480p MP4）。为空时按 ThumbnailUrl 处理。</summary>
        public string? PreviewUrl
        {
            get => _previewUrl;
            set { _previewUrl = value; OnPropertyChanged(nameof(PreviewUrl)); }
        }

        public string DetailUrl { get; set; } = "";
        public string Source { get; set; } = "";

        private string? _downloadUrl;
        /// <summary>该条目可直接下载的媒体直链（3gbizhi-dt 为 MP4、zhutix 为 GIF、livewallpapers4free 为 /download/{id}/）。为空时下载流程再回退到详情页解析。</summary>
        public string? DownloadUrl
        {
            get => _downloadUrl;
            set { _downloadUrl = value; OnPropertyChanged(nameof(DownloadUrl)); }
        }

        /// <summary>详情页标注的原始分辨率（如 3840×2160），用于在 UI 提示用户实际下载质量。</summary>
        public string? Resolution { get; set; }

        private string? _downloadedPath;
        /// <summary>已下载到本地的完整路径；非空表示已下载，可设为桌面。</summary>
        public string? DownloadedPath
        {
            get => _downloadedPath;
            set
            {
                _downloadedPath = value;
                OnPropertyChanged(nameof(DownloadedPath));
                OnPropertyChanged(nameof(IsDownloaded));
            }
        }

        public bool IsDownloaded => !string.IsNullOrEmpty(_downloadedPath);

        public override string ToString() => $"[{Source}] {Title}";

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged(string name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
