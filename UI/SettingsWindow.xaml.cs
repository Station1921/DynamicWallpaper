using System.Windows;
using System.Windows.Controls;
using DynamicWallpaper.Core;

namespace DynamicWallpaper.UI
{
    public partial class SettingsWindow : Window
    {
        private readonly Config _config;
        private readonly WallpaperManager _manager;

        public SettingsWindow(Config config, WallpaperManager manager)
        {
            // 必须在 InitializeComponent 之前赋值：XAML 中 TextBox 初始 Text 赋值会触发
            // TextChanged，事件处理器会访问 _config，未赋值即 NullReferenceException。
            _config = config;
            _manager = manager;
            InitializeComponent();

            MuteBox.IsChecked = _config.Mute;
            FsBox.IsChecked = _config.PauseOnFullscreen;
            BatBox.IsChecked = _config.PauseOnBattery;
            PerfBox.IsChecked = _config.PerformanceMode;
            StartBox.IsChecked = _config.RunOnStartup;
            TrayBox.IsChecked = _config.CloseToTray;

            switch (_config.WallpaperFit)
            {
                case "fit": FitFit.IsChecked = true; break;
                case "center": FitCenter.IsChecked = true; break;
                default: FitFill.IsChecked = true; break;
            }

            // 缓存管理：初始化开关与保留天数，并刷新缓存占用显示
            AutoCleanBox.IsChecked = _config.AutoCleanCache;
            RetentionDaysBox.Text = _config.CacheRetentionDays.ToString();
            RefreshCacheInfo();
        }

        /// <summary>刷新「当前缓存占用」说明文字。</summary>
        private void RefreshCacheInfo()
        {
            var (count, bytes) = CacheManager.GetStats();
            CacheInfo.Text = $"当前缓存占用：{CacheManager.FormatSize(bytes)}（{count} 个文件）。" +
                             "清除只删除缩略图与悬停预览缓存，不影响已下载的壁纸。";
        }

        private void ClearCache_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var (count, bytes) = CacheManager.ClearAll();
                CacheStatus.Text = $"已清除 {count} 个文件，释放 {CacheManager.FormatSize(bytes)}。";
                RefreshCacheInfo();
            }
            catch (Exception ex)
            {
                CacheStatus.Text = "清除缓存时出错：" + ex.Message;
            }
        }

        private void AutoClean_Changed(object sender, RoutedEventArgs e)
        {
            _config.AutoCleanCache = AutoCleanBox.IsChecked == true;
            if (int.TryParse(RetentionDaysBox.Text, out var d) && d > 0)
                _config.CacheRetentionDays = d;
            _config.Save();
        }

        private void RetentionDays_Changed(object sender, TextChangedEventArgs e)
        {
            if (_config == null) return; // 双保险：初始化早期事件
            if (int.TryParse(RetentionDaysBox.Text, out var d) && d > 0)
            {
                _config.CacheRetentionDays = d;
                _config.Save();
            }
        }

        /// <summary>限制保留天数输入框仅接受数字（含粘贴/IME 输入）。</summary>
        private void NumberOnly_Preview(object sender, System.Windows.Input.TextCompositionEventArgs e)
        {
            foreach (var c in e.Text)
            {
                if (!char.IsDigit(c))
                {
                    e.Handled = true;
                    break;
                }
            }
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            try
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                if (hwnd != IntPtr.Zero)
                    DynamicWallpaper.Desktop.Win32.ApplyDwmDarkChrome(hwnd);
            }
            catch { /* DWM 属性不支持的旧系统上静默忽略 */ }
        }

        private void Mute_Changed(object sender, RoutedEventArgs e) => _manager.SetMute(MuteBox.IsChecked == true);
        private void Fs_Changed(object sender, RoutedEventArgs e) => _manager.SetPauseOnFullscreen(FsBox.IsChecked == true);
        private void Bat_Changed(object sender, RoutedEventArgs e) => _manager.SetPauseOnBattery(BatBox.IsChecked == true);
        private void Perf_Changed(object sender, RoutedEventArgs e) => _manager.SetPerformanceMode(PerfBox.IsChecked == true);
        private void Start_Changed(object sender, RoutedEventArgs e)
        {
            _config.RunOnStartup = StartBox.IsChecked == true;
            _config.Save();
            Logger.Log($"[Settings] 开机自启开关：{_config.RunOnStartup}，注册表实际状态：{_config.IsStartupRegistered()}");
        }

        private void Tray_Changed(object sender, RoutedEventArgs e)
        {
            _config.CloseToTray = TrayBox.IsChecked == true;
            _config.Save();
        }

        private void Fit_Changed(object sender, RoutedEventArgs e)
        {
            string fit = FitFill.IsChecked == true ? "fill"
                : FitFit.IsChecked == true ? "fit"
                : "center";
            _config.WallpaperFit = fit;
            _config.Save();
            _manager.SyncFitMode();
        }
    }
}
