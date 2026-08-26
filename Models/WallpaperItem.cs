using System.ComponentModel;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Media;
using DynamicWallpaper.Providers;

namespace DynamicWallpaper.Models
{
    public enum WallpaperType
    {
        Video,
        Image,
        Web,
        Gif
    }

    public class WallpaperItem : INotifyPropertyChanged
    {
        public string Path { get; }
        public string Name => System.IO.Path.GetFileName(Path);
        public WallpaperType Type { get; }
        public string TypeLabel => Type switch
        {
            WallpaperType.Video => "视频",
            WallpaperType.Gif => "GIF",
            WallpaperType.Web => "网页",
            _ => "图片"
        };

        public string MotionLabel => Type switch
        {
            WallpaperType.Image => "静态",
            _ => "动态"
        };

        /// <summary>是否为网络直链（URL 在线壁纸）。</summary>
        public bool IsOnlineUrl =>
            Uri.TryCreate(Path, UriKind.Absolute, out var u) &&
            (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);

        private bool _isActive;
        public bool IsActive
        {
            get => _isActive;
            set { _isActive = value; OnPropertyChanged(nameof(IsActive)); }
        }

        /// <summary>该壁纸当前在哪些屏幕生效（如 "主屏 · 屏幕2"），空字符串表示未应用。</summary>
        private string _activeScreens = "";
        public string ActiveScreens
        {
            get => _activeScreens;
            set { _activeScreens = value; OnPropertyChanged(nameof(ActiveScreens)); }
        }

        private ImageSource? _thumbnail;
        public ImageSource? Thumbnail
        {
            get => _thumbnail;
            private set { _thumbnail = value; OnPropertyChanged(nameof(Thumbnail)); }
        }

        public WallpaperItem(string path, WallpaperType type)
        {
            Path = path;
            Type = type;
            _ = LoadThumbnailAsync();
        }

        private async Task LoadThumbnailAsync()
        {
            if (Type == WallpaperType.Web) return;
            // 远程 URL 不加载本地缩略图，避免文件不存在导致异常
            if (Uri.TryCreate(Path, UriKind.Absolute, out var u) &&
                (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps))
                return;
            var bmp = await ThumbnailHelper.GetThumbnailAsync(Path, Type);
            if (bmp != null) Thumbnail = bmp;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged(string name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
