using System.ComponentModel;
using System.IO;
using System.Windows.Media;
using DynamicWallpaper.Providers;

namespace DynamicWallpaper.Models
{
    /// <summary>
    /// 幻灯片页的「文件夹」卡片（类图库视图）：以文件夹内一张图作封面，
    /// 不展开文件夹里的每张图。文件夹可无限添加，占位「添加文件夹」卡始终在集合末尾。
    /// </summary>
    public class SlideshowFolderItem : INotifyPropertyChanged
    {
        private static readonly string[] _exts =
        {
            ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp",
            ".mp4", ".webm", ".mov", ".mkv", ".avi"
        };

        public string Folder { get; }

        public string Name =>
            Path.GetFileName(Folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        /// <summary>文件夹内媒体总数（图片 + 视频，不含子文件夹）。</summary>
        public int Count { get; }

        private ImageSource? _cover;
        public ImageSource? Cover
        {
            get => _cover;
            private set { _cover = value; OnPropertyChanged(nameof(Cover)); }
        }

        private bool _isActive;
        public bool IsActive
        {
            get => _isActive;
            set { _isActive = value; OnPropertyChanged(nameof(IsActive)); }
        }

        private string _activeScreens = "";
        public string ActiveScreens
        {
            get => _activeScreens;
            set { _activeScreens = value; OnPropertyChanged(nameof(ActiveScreens)); }
        }

        public SlideshowFolderItem(string folder)
        {
            Folder = folder;
            Count = ScanCount(folder);
            _ = LoadCoverAsync(folder);
        }

        private static int ScanCount(string folder)
        {
            int n = 0;
            try
            {
                foreach (var f in Directory.EnumerateFiles(folder))
                {
                    var ext = Path.GetExtension(f);
                    if (ext.Length > 0 && System.Array.Exists(_exts, e => string.Equals(e, ext, System.StringComparison.OrdinalIgnoreCase)))
                        n++;
                }
            }
            catch { /* 无权限或路径异常：数量为 0 */ }
            return n;
        }

        private async System.Threading.Tasks.Task LoadCoverAsync(string folder)
        {
            string? cover = null;
            try
            {
                foreach (var f in Directory.EnumerateFiles(folder))
                {
                    var ext = Path.GetExtension(f);
                    if (ext.Length == 0 || !System.Array.Exists(_exts, e => string.Equals(e, ext, System.StringComparison.OrdinalIgnoreCase)))
                        continue;
                    // 优先用图片作封面；视频也可作封面（ThumbnailHelper 会取首帧），但图片更稳
                    cover = f;
                    if (string.Equals(ext, ".jpg", System.StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(ext, ".jpeg", System.StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(ext, ".png", System.StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(ext, ".bmp", System.StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(ext, ".webp", System.StringComparison.OrdinalIgnoreCase))
                        break;
                }
            }
            catch { /* 扫描失败：无封面 */ }
            if (cover == null) return;
            var bmp = await ThumbnailHelper.GetThumbnailAsync(cover, ProviderFactory.DetectType(cover));
            if (bmp != null) Cover = bmp;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged(string name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
