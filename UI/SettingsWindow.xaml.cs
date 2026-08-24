using System.Windows;
using DynamicWallpaper.Core;

namespace DynamicWallpaper.UI
{
    public partial class SettingsWindow : Window
    {
        private readonly Config _config;
        private readonly WallpaperManager _manager;

        public SettingsWindow(Config config, WallpaperManager manager)
        {
            InitializeComponent();
            _config = config;
            _manager = manager;

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
