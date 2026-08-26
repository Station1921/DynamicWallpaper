using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using MessageBox = System.Windows.MessageBox;
using DynamicWallpaper.Core;
using DynamicWallpaper.Models;
using DynamicWallpaper.Providers;
using DynamicWallpaper.UI;
using Microsoft.Win32;
using DataFormats = System.Windows.DataFormats;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using System.Drawing;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace DynamicWallpaper
{
    public partial class MainWindow : Window
    {
        private readonly Config _config;
        private readonly WallpaperManager _manager;
        private readonly DownloadHistory _downloadHistory;
        public ObservableCollection<WallpaperItem> Library { get; } = new();
        // 三个在线来源各自维护独立集合，切页签时互不清空，避免下载状态丢失/重复下载
        public ObservableCollection<OnlineWallpaperItem> NetbianWallpapers { get; } = new();
        public ObservableCollection<OnlineWallpaperItem> GBizhiWallpapers { get; } = new();
        // 动态壁纸页签集合（现使用 netbian 动态分类 dongtai，提供 1920x1080 GIF 高清动画）
        public ObservableCollection<OnlineWallpaperItem> DynamicWallpapers { get; } = new();
        public bool ForceExit { get; set; }
        private bool _exiting;

        private List<ScreenOption> _screenOptions = new();
        private CancellationTokenSource? _onlineCts;

        private string _currentNetbianCategory = "";
        private int _currentNetbianPage = 1;
        private string _currentGBizhiCategory = "111|100";
        private int _currentGBizhiPage = 1;
        // Wallhaven 分辨率筛选：空串表示不限（对应下拉"不限"项）
        private string _currentWallhavenResolution = "";
        // 下拉初始化期间（ItemsSource 填充/默认选中）抑制 SelectionChanged 误触发加载
        private bool _wallhavenResolutionReady;
        private int _currentDynamicPage = 1;
        // 各在线来源是否已首次加载（已加载则切回页签不再重复抓取）
        private bool _netbianLoaded;
        private bool _gBizhiLoaded;
        private bool _dynamicLoaded;
        // 瀑布流滚动加载互斥与尾页标记
        private bool _netbianLoading;
        private bool _gBizhiLoading;
        private bool _dynamicLoading;
        private bool _hasMoreNetbian = true;
        private bool _hasMoreGBizhi = true;
        private bool _hasMoreDynamic = true;
        // 分类复选 Chip 程序性批量设置选中态时抑制重复加载
        private bool _gBizhiChipUpdating;

        // 让卡片宽度随窗口宽度自适应，以消除右侧空白
        public static readonly DependencyProperty CardWidthProperty =
            DependencyProperty.Register(nameof(CardWidth), typeof(double), typeof(MainWindow), new PropertyMetadata(200.0));

        public double CardWidth
        {
            get => (double)GetValue(CardWidthProperty);
            set => SetValue(CardWidthProperty, value);
        }

        private const string Filter =
            "媒体文件|*.mp4;*.webm;*.mkv;*.avi;*.mov;*.wmv;*.m4v;*.mpg;*.mpeg;*.flv;*.ts;*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.webp|视频|*.mp4;*.webm;*.mkv;*.avi;*.mov;*.wmv;*.m4v;*.mpg;*.mpeg;*.flv;*.ts|图片|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.webp|所有文件|*.*";

        public MainWindow()
        {
            Logger.Log("[MainWindow] 构造开始");
            InitializeComponent();
            Logger.Log("[MainWindow] InitializeComponent 完成");

            // 窗口标题栏图标：从 EXE 自带的原生图标取（ApplicationIcon 已嵌入），
            // 不依赖 WPF pack 资源，避免资源未嵌入时构造抛异常导致进程启动即崩。
            try
            {
                var exePath = Process.GetCurrentProcess().MainModule?.FileName;
                if (exePath != null)
                {
                    using var sysIcon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
                    if (sysIcon != null)
                        Icon = Imaging.CreateBitmapSourceFromHIcon(
                            sysIcon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                }
            }
            catch { /* 图标加载失败不阻塞主流程 */ }

            _config = Config.Load();
            // 迁移：若已开启开机自启，确保注册表自启项带有 --silent 参数（旧版本不带，导致开机仍弹主界面）
            if (_config.RunOnStartup) _config.EnsureStartupRegistered();
            _manager = new WallpaperManager(_config);
            _downloadHistory = DownloadHistory.Load();
            _manager.Start();

            foreach (var p in _config.Library)
            {
                // 本地文件必须存在才加载；网络直链（http/https）直接加入库，不检查文件存在性
                if (File.Exists(p) || IsWebUrl(p))
                    Library.Add(new WallpaperItem(p, ProviderFactory.DetectType(p)));
            }

            InitScreenSelector();
            RefreshEmpty();
            RefreshActiveBadges();
            SetStatusText(StatusSummary());
            UpdateBottomBar();

            AllowDrop = true;
            DragOver += OnDragOver;
            Drop += OnDrop;
            Closing += OnClosing;

            InitNetbianCategories();
            MyWallpaperScroll.SizeChanged += GridScroll_SizeChanged;
            NetbianScroll.SizeChanged += GridScroll_SizeChanged;
            GBizhiScroll.SizeChanged += GridScroll_SizeChanged;
            DynamicScroll.SizeChanged += GridScroll_SizeChanged;
            Loaded += (_, _) => { Logger.Log("[MainWindow] Loaded 已触发"); UpdateCardWidth(NetbianScroll); };
            Logger.Log("[MainWindow] 构造完成");
        }

        /// <summary>窗口句柄创建后应用 DWM 外观：暗色标题栏 + 标题栏颜色与主体一致 + 标题文字白色
        /// + 窗口圆角。
        ///
        /// 注意：不再调用 SetWindowBackdrop（Acrylic/Mica backdrop）。根因：MainWindow 在静默
        /// 启动（--silent）时构造后不 Show，托盘双击才首次显示；此时 OnSourceInitialized 同步设置
        /// DWMWA_SYSTEMBACKDROP_TYPE=Acrylic 与 WPF 透明背景（Background=Transparent）存在合成冲突，
        /// DWM 会把窗口判定为"仅 Acrylic 材质"而不再合成 WPF 内容树，导致界面白屏（无文字无控件）。
        /// 窗口背景已改为不透明深色 #1B1B22（与 SettingsWindow 一致），内容由 WPF 自绘，不再依赖
        /// DWM backdrop，白屏路径被彻底消除。</summary>
        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd != IntPtr.Zero)
                {
                    DynamicWallpaper.Desktop.Win32.ApplyDwmDarkChrome(hwnd);
                    Logger.Log("[MainWindow] DWM 暗色外观已应用");
                }
            }
            catch { /* DWM 属性不支持的旧系统上静默忽略 */ }
        }

        private void GridScroll_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (sender is ScrollViewer sv) UpdateCardWidth(sv);
        }

        private void UpdateCardWidth(ScrollViewer sv)
        {
            double area = sv.ActualWidth - 4; // WrapPanel 左右 Margin 各 2
            if (area < 40) return;
            const double cardMargin = 12;     // 卡片左右 Margin 各 6
            const double desired = 200 + cardMargin;
            int cols = Math.Max(1, (int)Math.Floor(area / desired));
            double w = (area - cols * cardMargin) / cols;
            CardWidth = Math.Max(160, Math.Min(w, 360));
        }

        // ---------- 分类标签栏：鼠标滚轮横向滚动 ----------
        private void CategoryScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (sender is ScrollViewer sv)
            {
                if (e.Delta > 0)
                    sv.LineLeft();
                else
                    sv.LineRight();
                e.Handled = true;
            }
        }

        // ---------- 屏幕选择 ----------
        public class ScreenOption
        {
            public int Index { get; set; }
            public string Name { get; set; } = "";
            public override string ToString() => Name;
        }

        private void InitScreenSelector()
        {
            _screenOptions = new List<ScreenOption>();
            var screens = ScreenManager.GetScreens();
            if (screens.Count > 1)
                _screenOptions.Add(new ScreenOption { Index = -1, Name = "所有屏幕" });
            foreach (var s in screens)
                _screenOptions.Add(new ScreenOption { Index = s.Index, Name = s.DisplayName });

            bool multiScreen = screens.Count > 1;
            if (multiScreen)
            {
                ScreenCombo.ItemsSource = _screenOptions;
                ScreenCombo.DisplayMemberPath = "Name";
                var def = _screenOptions.FirstOrDefault(o => o.Index == _config.DefaultScreen) ?? _screenOptions[0];
                ScreenCombo.SelectedItem = def;
                ScreenCombo.Visibility = Visibility.Visible;
                ScreenText.Visibility = Visibility.Collapsed;
            }
            else
            {
                ScreenCombo.Visibility = Visibility.Collapsed;
                ScreenText.Text = screens.Count > 0 ? screens[0].DisplayName : "主屏";
                ScreenText.Visibility = Visibility.Visible;
            }
        }

        private int SelectedScreen() => (ScreenCombo.SelectedItem as ScreenOption)?.Index ?? 0;

        private string ScreenName(int index)
        {
            var s = ScreenManager.GetScreens().FirstOrDefault(x => x.Index == index);
            return s?.DisplayName ?? ("屏幕 " + index);
        }

        // ---------- 库管理 ----------
        /// <summary>本次会话已弹出过 HEVC 转码询问的原文件路径，避免重复弹窗。</summary>
        private readonly HashSet<string> _hevcPrompted = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>添加壁纸到库。MP4 视频在添加前先检测 HEVC 编码（方案 A）：
        /// 检测到 HEVC 时弹窗询问是否用程序目录下的 ffmpeg.exe 转码为 H.264，避免部分系统/显卡
        /// 无法解码导致壁纸黑屏；原文件始终保留，转码产物为同目录 "原名_h264.mp4"。</summary>
        private async Task AddPathAsync(string path, WallpaperType? type = null)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;

            path = await PrepareVideoAsync(path);
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
            if (Library.Any(i => i.Path.Equals(path, StringComparison.OrdinalIgnoreCase))) return;

            var t = type ?? ProviderFactory.DetectType(path);
            var item = new WallpaperItem(path, t);
            Library.Add(item);
            if (!_config.Library.Contains(path)) _config.Library.Add(path);
            _config.Save();
            RefreshEmpty();
        }

        /// <summary>HEVC 检测与转码（方案 A）。返回最终应加入库的路径：未检测到 HEVC、用户选择
        /// 不转码或转码失败时原样返回；转码成功返回转码产物路径。</summary>
        private async Task<string> PrepareVideoAsync(string path)
        {
            if (!path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)) return path;

            string? codec = VideoCodecDetector.DetectVideoCodec(path);
            if (!VideoCodecDetector.IsHevc(codec)) return path;

            string outPath = Path.Combine(
                Path.GetDirectoryName(path)!,
                Path.GetFileNameWithoutExtension(path) + "_h264.mp4");

            // 已有转码产物则直接复用，不再重复询问转码
            if (File.Exists(outPath)) return outPath;

            // 本次会话已询问过该文件则不再弹窗；未转码一律不添加
            if (!_hevcPrompted.Add(path)) return string.Empty;

            var choice = MessageBox.Show(
                $"检测到壁纸视频《{Path.GetFileName(path)}》为 HEVC（H.265）编码。\n" +
                "部分系统/显卡可能无法解码，直接播放会出现黑屏。\n\n" +
                "是否转换为 H.264 后添加？\n（未转码将不会添加到库中）",
                "HEVC 转码", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (choice != MessageBoxResult.Yes) return string.Empty;

            // ffmpeg 查找：程序目录 → PATH/常见位置 → 内嵌解压兜底（详见 AppPaths.EnsureFfmpeg）。
            // 平时浏览库/设壁纸完全不触发此查找，零开销；仅此处（HEVC 转码）才查一次。
            string? ffmpeg = AppPaths.EnsureFfmpeg();
            if (string.IsNullOrEmpty(ffmpeg))
            {
                MessageBox.Show("未找到 ffmpeg.exe，且内置 ffmpeg 解压失败。该视频未添加。",
                    "缺少 ffmpeg", MessageBoxButton.OK, MessageBoxImage.Warning);
                return string.Empty;
            }

            // 是否用的是「外部 ffmpeg」（非程序目录解压版）：外部版本可能缺 libx264/HEVC 编码器，
            // 一旦转码失败则回退到内嵌资源解压兜底并重试一次。
            string localFfmpeg = Path.Combine(AppPaths.RootDirectory, "ffmpeg.exe");
            bool usedExternal = !string.Equals(Path.GetFullPath(ffmpeg), Path.GetFullPath(localFfmpeg), StringComparison.OrdinalIgnoreCase);

            for (int attempt = 1; attempt <= 2; attempt++)
            {
                if (await RunTranscodeOnceAsync(ffmpeg, path, outPath))
                {
                    Logger.Log($"[HEVC转码] 完成: {path} -> {outPath}");
                    MessageBox.Show($"转码完成：{Path.GetFileName(outPath)}\n已按转码文件添加。",
                        "转码成功", MessageBoxButton.OK, MessageBoxImage.Information);
                    return outPath;
                }

                Logger.Log($"[HEVC转码] 第{attempt}次失败，ffmpeg={ffmpeg}");
                // 第一次失败且用的是外部 ffmpeg → 回退内嵌解压兜底并重试一次
                if (attempt == 1 && usedExternal)
                {
                    string? embedded = AppPaths.ForceEmbeddedFfmpeg();
                    if (!string.IsNullOrEmpty(embedded))
                    {
                        ffmpeg = embedded;
                        usedExternal = false;
                        continue;
                    }
                }
                break;
            }

            MessageBox.Show("转码失败，未添加该视频。", "转码失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            return string.Empty;
        }

        /// <summary>单次 HEVC 转码（libx264）。成功返回 true；启动失败/进程退出码非 0/产物缺失返回 false。</summary>
        private async Task<bool> RunTranscodeOnceAsync(string ffmpeg, string path, string outPath)
        {
            TranscodeProgressWindow? progressWin = null;
            try
            {
                var psi = new ProcessStartInfo(ffmpeg,
                    $"-y -nostats -progress pipe:1 -i \"{path}\" -c:v libx264 -crf 22 -preset medium -c:a copy \"{outPath}\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using var p = Process.Start(psi);
                if (p == null) return false;

                progressWin = new TranscodeProgressWindow(Path.GetFileName(path));
                progressWin.Show();
                progressWin.SetIndeterminate("正在启动 ffmpeg…");

                // 从 stderr 解析输入视频总时长，从 stdout 的 -progress 输出解析实时转码位置
                double durationSec = 0;
                var durationRegex = new Regex(@"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)");
                p.ErrorDataReceived += (_, e) =>
                {
                    if (string.IsNullOrEmpty(e.Data) || durationSec > 0) return;
                    var m = durationRegex.Match(e.Data);
                    if (m.Success)
                    {
                        durationSec = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * 3600
                                    + int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) * 60
                                    + double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
                    }
                };
                p.OutputDataReceived += (_, e) =>
                {
                    if (string.IsNullOrEmpty(e.Data)) return;

                    if (e.Data.StartsWith("out_time_us=", StringComparison.OrdinalIgnoreCase)
                        && long.TryParse(e.Data.AsSpan(12), out long us) && us >= 0 && durationSec > 0)
                    {
                        double sec = us / 1_000_000.0;
                        double pct = Math.Min(100.0, sec / durationSec * 100.0);
                        progressWin?.SetProgress(pct, $"{pct:F1}%  ({FormatTs(sec)} / {FormatTs(durationSec)})");
                    }
                };
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();

                await p.WaitForExitAsync();
                progressWin?.Close();
                progressWin = null;

                return p.ExitCode == 0 && File.Exists(outPath);
            }
            catch (Exception ex)
            {
                progressWin?.Close();
                Logger.Log($"[HEVC转码] 异常: {ex}");
                return false;
            }
        }

        /// <summary>把秒数格式化为 分:秒 或 时:分:秒。</summary>
        private static string FormatTs(double seconds)
        {
            var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
            return t.TotalHours >= 1
                ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
                : $"{t.Minutes:00}:{t.Seconds:00}";
        }

        private void RefreshEmpty()
        {
            EmptyHint.Visibility = Library.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (MyWallpaperCount != null) MyWallpaperCount.Text = $"共 {Library.Count} 个";
        }

        private async void Add_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog { Multiselect = true, Filter = Filter, Title = "选择壁纸文件" };
            if (dlg.ShowDialog() == true)
                foreach (var f in dlg.FileNames) await AddPathAsync(f);
        }

        /// <summary>判断路径是否为 http/https 网络直链。</summary>
        private static bool IsWebUrl(string path) =>
            Uri.TryCreate(path, UriKind.Absolute, out var u) &&
            (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);

        private async void AddLink_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new AddLinkDialog { Owner = this };
            if (dlg.ShowDialog() != true) return;

            // 在线直播模式：直接以 URL 作为路径创建壁纸项，无需下载
            if (dlg.IsOnline && !string.IsNullOrEmpty(dlg.OnlineUrl))
            {
                var url = dlg.OnlineUrl!;
                if (Library.Any(i => i.Path.Equals(url, StringComparison.OrdinalIgnoreCase))) return;

                var onlineItem = new WallpaperItem(url, dlg.OnlineType);
                Library.Add(onlineItem);
                if (!_config.Library.Contains(url)) _config.Library.Add(url);
                _config.Save();
                Logger.Log($"在线壁纸已加入库：{url}（{dlg.OnlineType}）");
                if (dlg.ApplyAfter)
                    await ApplyItemAsync(onlineItem, SelectedScreen());
                RefreshEmpty();
                RefreshActiveBadges();
                SetStatusText(StatusSummary());
                return;
            }

            // 下载模式
            if (string.IsNullOrEmpty(dlg.DownloadedPath)) return;
            var path = dlg.DownloadedPath!;
            await AddPathAsync(path);
            if (dlg.ApplyAfter && Library.FirstOrDefault(i => i.Path == path) is { } item)
                await ApplyItemAsync(item, SelectedScreen());
            RefreshActiveBadges();
            SetStatusText(StatusSummary());
        }

        // ---------- 应用壁纸 ----------
        /// <summary>状态栏默认文字颜色（浅灰，与 XAML 初始 Foreground=#C8C8D2 一致）。</summary>
        private static readonly System.Windows.Media.Brush DefaultStatusBrush =
            new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xC8, 0xC8, 0xD2));

        /// <summary>底部状态栏切换状态反馈（可能从后台线程回调，需调度回 UI 线程）。
        /// 按消息类型着色：正在切换=黄色，切换失败=红色，其余（已应用/当前状态）=默认浅灰。</summary>
        private void SetStatusSafe(string msg)
        {
            var brush = msg.StartsWith("正在切换：") ? System.Windows.Media.Brushes.Gold
                : msg.StartsWith("切换失败：") ? System.Windows.Media.Brushes.Red
                : DefaultStatusBrush;
            if (Dispatcher.CheckAccess()) SetStatusText(msg, brush);
            else Dispatcher.InvokeAsync(() => SetStatusText(msg, brush));
        }

        /// <summary>设置状态栏文本与颜色（须在 UI 线程调用）。</summary>
        private void SetStatusText(string msg, System.Windows.Media.Brush? brush = null)
        {
            StatusText.Text = msg;
            StatusText.Foreground = brush ?? DefaultStatusBrush;
        }

        private async Task ApplyItemAsync(WallpaperItem item, int targetScreen)
        {
            // 立即把该条目标记为“已应用”，让按钮（设为壁纸→解除）即时更新，
            // 不等待较慢的 WorkerW 注入 / 系统壁纸切换完成。后续 RefreshActiveBadges 会校正真实状态。
            item.IsActive = true;

            // 状态栏即时反馈"正在切换"，不等待 SetWallpaperAsync 内部排队（锁可能被占用）
            SetStatusSafe("正在切换：" + Path.GetFileName(item.Path));

            try
            {
                if (targetScreen < 0)
                {
                    var screens = ScreenManager.GetScreens();
                    foreach (var sc in screens)
                        await _manager.SetWallpaperAsync(item.Path, item.Type, sc.Index, status: SetStatusSafe);
                }
                else
                {
                    await _manager.SetWallpaperAsync(item.Path, item.Type, targetScreen, status: SetStatusSafe);
                }

                RefreshActiveBadges();
                SetStatusText(StatusSummary());
            }
            catch (Exception ex)
            {
                // 应用失败时回滚乐观标记，避免按钮状态与实际情况不符
                RefreshActiveBadges();
                SetStatusText(StatusSummary());
                SetStatusSafe("切换失败：" + ex.Message);
                MessageBox.Show("应用失败：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void SetWallpaper_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button btn) btn.IsEnabled = false;
            try
            {
                var item = (sender as FrameworkElement)?.DataContext as WallpaperItem;
                if (item != null) await ApplyItemAsync(item, SelectedScreen());
            }
            finally
            {
                if (sender is System.Windows.Controls.Button btn2) btn2.IsEnabled = true;
            }
        }

        private async void ClearWallpaper_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button btn) btn.IsEnabled = false;
            try
            {
                var item = (sender as FrameworkElement)?.DataContext as WallpaperItem;
                if (item != null) await ClearItemAsync(item);
            }
            finally
            {
                if (sender is System.Windows.Controls.Button btn2) btn2.IsEnabled = true;
            }
        }

        private async Task ClearItemAsync(WallpaperItem item)
        {
            // 先立即把该条目标记为“未应用”，让按钮（解除→设为壁纸）即时更新，
            // 不必等待较慢的系统壁纸恢复 / 配置保存完成（否则按钮会滞后几秒才变化）。
            item.IsActive = false;
            item.ActiveScreens = "";

            // Windows 路径不区分大小写，避免因为大小写不一致导致解除按钮找不到目标屏幕
            foreach (var idx in _manager.ActiveScreenIndices)
                if (string.Equals(_manager.GetActivePath(idx), item.Path, StringComparison.OrdinalIgnoreCase))
                    await _manager.ClearScreenAsync(idx);

            RefreshActiveBadges();
            SetStatusText(StatusSummary());
        }

        private void Card_RightClick(object sender, MouseButtonEventArgs e)
        {
            var border = sender as Border;
            var item = border?.DataContext as WallpaperItem;
            if (item == null) return;

            var menu = new ContextMenu { PlacementTarget = border, Placement = PlacementMode.MousePoint };
            var setMenu = new MenuItem { Header = "设为壁纸到" };
            foreach (var sc in ScreenManager.GetScreens())
            {
                int idx = sc.Index;
                var mi = new MenuItem { Header = sc.DisplayName };
                mi.Click += async (_, _) => await ApplyItemAsync(item, idx);
                setMenu.Items.Add(mi);
            }
            if (ScreenManager.GetScreens().Count > 1)
            {
                var miAll = new MenuItem { Header = "所有屏幕" };
                miAll.Click += async (_, _) => await ApplyItemAsync(item, -1);
                setMenu.Items.Add(miAll);
            }
            var openLocation = new MenuItem { Header = "打开壁纸位置" };
            openLocation.Click += (_, _) => OpenWallpaperLocation(item);

            var remove = new MenuItem { Header = "从库移除" };
            remove.Click += (_, _) => RemoveItem(item);

            var deleteLocal = new MenuItem { Header = "从本地删除壁纸" };
            deleteLocal.Click += (_, _) => DeleteWallpaperLocal(item);

            menu.Items.Add(setMenu);
            menu.Items.Add(openLocation);
            menu.Items.Add(remove);
            menu.Items.Add(deleteLocal);
            menu.IsOpen = true;
            e.Handled = true;
        }

        private async void RemoveItem(WallpaperItem item)
        {
            foreach (var idx in _manager.ActiveScreenIndices)
                if (_manager.GetActivePath(idx) == item.Path)
                    await _manager.ClearScreenAsync(idx);

            Library.Remove(item);
            if (_config.Library.Contains(item.Path)) _config.Library.Remove(item.Path);
            _config.Assignments.RemoveAll(a => a.Path == item.Path);
            _config.Save();

            RefreshEmpty();
            RefreshActiveBadges();
            SetStatusText(StatusSummary());
        }

        /// <summary>用资源管理器打开壁纸文件所在文件夹并选中该文件。</summary>
        private void OpenWallpaperLocation(WallpaperItem item)
        {
            if (string.IsNullOrEmpty(item.Path) || !File.Exists(item.Path))
            {
                MessageBox.Show("壁纸文件不存在：\n" + item.Path, "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{item.Path}\"",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                Logger.Log($"[MainWindow] 打开壁纸位置失败: {ex}");
                MessageBox.Show("打开壁纸位置失败：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>从本地删除壁纸：删除磁盘文件 + 从壁纸库移除卡片（区别于"从库移除"只删卡片不删文件）。
        /// 文件删除失败（如被占用）时提示且不移除卡片。</summary>
        private void DeleteWallpaperLocal(WallpaperItem item)
        {
            try
            {
                File.Delete(item.Path);
            }
            catch (Exception ex)
            {
                Logger.Log($"[MainWindow] 删除壁纸文件失败: {ex}");
                MessageBox.Show("删除壁纸文件失败（文件可能正被占用）：\n" + item.Path + "\n\n" + ex.Message,
                    "删除失败", MessageBoxButton.OK, MessageBoxImage.Error);
                return; // 文件未删除成功，不移除卡片
            }

            // 文件删除成功后，复用 RemoveItem 逻辑移除卡片（解除正在使用的屏幕壁纸、配置清理、刷新界面）
            RemoveItem(item);
        }

        // ---------- 状态展示 ----------
        private void RefreshActiveBadges()
        {
            var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var idx in _manager.ActiveScreenIndices)
            {
                var p = _manager.GetActivePath(idx);
                if (p == null) continue;
                if (!map.ContainsKey(p)) map[p] = new List<string>();
                map[p].Add(ScreenName(idx));
            }
            foreach (var it in Library)
            {
                // Windows 路径不区分大小写，避免不同来源路径格式不一致导致按钮状态不更新
                bool active = map.TryGetValue(it.Path, out var ls);
                it.IsActive = active;
                it.ActiveScreens = active ? string.Join(" · ", ls!) : "";
            }

            // 同步在线壁纸列表的应用状态（按钮"设为桌面 ↔ 解除壁纸"）。
            // 下载自动设为桌面 / 手动应用 / 解除 / 启动恢复等路径都汇聚到此统一校正，
            // 避免界面按钮与桌面实际状态不同步。
            SyncOnlineAppliedStates();
        }

        private void SyncOnlineAppliedStates()
        {
            foreach (var col in new[] { NetbianWallpapers, GBizhiWallpapers, DynamicWallpapers })
            {
                foreach (var oi in col)
                {
                    bool applied = !string.IsNullOrEmpty(oi.DownloadedPath) && IsPathApplied(oi.DownloadedPath);
                    oi.IsApplied = applied;
                }
            }
        }

        /// <summary>判断本地路径当前是否被应用为任意屏幕的桌面壁纸。</summary>
        private bool IsPathApplied(string? path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            foreach (var idx in _manager.ActiveScreenIndices)
                if (string.Equals(_manager.GetActivePath(idx), path, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        private string StatusSummary()
        {
            var active = _manager.ActiveScreenIndices;
            if (active.Count == 0) return "当前：无";
            var parts = active.Select(idx =>
            {
                var p = _manager.GetActivePath(idx);
                return $"{ScreenName(idx)}《{Path.GetFileName(p ?? "")}》";
            });
            return "已应用：" + string.Join(" ｜ ", parts);
        }

        // ---------- 其它交互 ----------
        private void Settings_Click(object sender, RoutedEventArgs e)
        {
            var w = new SettingsWindow(_config, _manager) { Owner = this };
            w.ShowDialog();
        }

        /// <summary>根据当前页签显示底部栏对应的状态文本与加载按钮，避免两个操作栏同时出现。</summary>
        private void UpdateBottomBar(string? header = null)
        {
            header ??= (MainTabs.SelectedItem as System.Windows.Controls.TabItem)?.Header as string;

            MyWallpaperCount.Visibility = header == "我的壁纸" ? Visibility.Visible : Visibility.Collapsed;

            NetbianStatus.Visibility = header == "静态壁纸" ? Visibility.Visible : Visibility.Collapsed;
            GBizhiStatus.Visibility = header == "Wallhaven" ? Visibility.Visible : Visibility.Collapsed;
            DynamicStatus.Visibility = header == "动态壁纸" ? Visibility.Visible : Visibility.Collapsed;
        }

        // ---------- 在线壁纸爬虫 ----------

        private void InitNetbianCategories()
        {
            NetbianCategoryPanel.Children.Clear();
            foreach (var cat in OnlineWallpaperCrawler.NetbianCategories)
            {
                var rb = new System.Windows.Controls.RadioButton
                {
                    Content = cat.Name,
                    Style = (Style)FindResource("OutlineChip"),
                    IsChecked = cat.Slug == _currentNetbianCategory,
                    Tag = cat.Slug
                };
                rb.Checked += NetbianCategory_Checked;
                NetbianCategoryPanel.Children.Add(rb);
            }
        }

        private async void NetbianCategory_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is not System.Windows.Controls.RadioButton rb || rb.Tag is not string slug) return;
            _currentNetbianCategory = slug;
            _currentNetbianPage = 1;
            await LoadNetbianAsync(true);
            NetbianScroll.ScrollToTop();
        }

        private async void NetbianScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (_netbianLoading || !_hasMoreNetbian) return;
            if (!IsNearBottom(e)) return;
            _currentNetbianPage++;
            await LoadNetbianAsync(false);
        }

        private bool IsNearBottom(ScrollChangedEventArgs e, double threshold = 120)
        {
            if (e.ExtentHeight <= 0) return false;
            return e.VerticalOffset + e.ViewportHeight >= e.ExtentHeight - threshold;
        }

        private async Task LoadNetbianAsync(bool reset)
        {
            if (_netbianLoading) return;
            _netbianLoading = true;
            _onlineCts?.Cancel();
            _onlineCts = new CancellationTokenSource();

            NetbianStatus.Visibility = Visibility.Visible;
            NetbianStatus.Text = "正在加载...";
            if (reset)
            {
                NetbianWallpapers.Clear();
                _hasMoreNetbian = true;
            }
            NetbianEmptyHint.Visibility = Visibility.Collapsed;

            try
            {
                var list = await OnlineWallpaperCrawler.FetchAsync("netbian", _currentNetbianPage, _currentNetbianCategory, _onlineCts.Token);
                foreach (var item in list)
                {
                    item.DownloadedPath = _downloadHistory.TryGetExistingPath(item.DetailUrl);
                    item.IsApplied = IsPathApplied(item.DownloadedPath);
                    NetbianWallpapers.Add(item);
                }

                _netbianLoaded = true;
                _hasMoreNetbian = list.Count > 0;
                NetbianStatus.Text = $"已加载 {NetbianWallpapers.Count} 条";
                NetbianEmptyHint.Visibility = NetbianWallpapers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                Logger.Log($"[netbian] 加载失败: {ex}");
                if (!_netbianLoaded)
                {
                    NetbianStatus.Text = "加载失败：" + ex.Message;
                    NetbianEmptyHint.Visibility = Visibility.Visible;
                }
            }
            finally
            {
                _netbianLoading = false;
            }
        }

        private async void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.Source is not System.Windows.Controls.TabControl tc || tc.SelectedItem is not System.Windows.Controls.TabItem item) return;
            var header = item.Header as string;
            UpdateBottomBar(header);

            if (header == "静态壁纸" && !_netbianLoaded)
            {
                await Dispatcher.BeginInvoke(async () =>
                {
                    _currentNetbianCategory = "";
                    _currentNetbianPage = 1;
                    InitNetbianCategories();
                    await LoadNetbianAsync(true);
                    NetbianScroll.ScrollToTop();
                }, System.Windows.Threading.DispatcherPriority.Background);
            }
            else if (header == "Wallhaven" && !_gBizhiLoaded)
            {
                await Dispatcher.BeginInvoke(async () =>
                {
                    _currentGBizhiCategory = "111|100";
                    _currentGBizhiPage = 1;
                    InitGBizhiCategories();
                    await LoadGBizhiAsync(true);
                    GBizhiScroll.ScrollToTop();
                }, System.Windows.Threading.DispatcherPriority.Background);
            }
            else if (header == "动态壁纸" && !_dynamicLoaded)
            {
                await Dispatcher.BeginInvoke(async () =>
                {
                    _currentDynamicPage = 1;
                    await LoadDynamicAsync(true);
                    DynamicScroll.ScrollToTop();
                }, System.Windows.Threading.DispatcherPriority.Background);
            }
        }

        private void InitGBizhiCategories()
        {
            GBizhiCategoryPanel.Children.Clear();

            // 内容分类（可复选，默认全选）：全部 / General / Anime / People
            AddGBizhiChip("全部", "content-all", true);
            AddGBizhiChip("General", "content-general", true);
            AddGBizhiChip("Anime", "content-anime", true);
            AddGBizhiChip("People", "content-people", true);

            // 分组分隔符
            GBizhiCategoryPanel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = "|",
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x3A, 0x3A, 0x45)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
                FontSize = 14
            });

            // 纯度（可复选，默认 SFW）：SFW / Sketchy
            AddGBizhiChip("SFW", "purity-sfw", true);
            AddGBizhiChip("Sketchy", "purity-sketchy", false);

            // 分辨率下拉：仅在未初始化时填充一次，避免重复触发 SelectionChanged
            if (!_wallhavenResolutionReady)
            {
                _wallhavenResolutionReady = false;
                WallhavenResolutionCombo.ItemsSource = OnlineWallpaperCrawler.WallhavenResolutions;
                WallhavenResolutionCombo.DisplayMemberPath = "Name";
                WallhavenResolutionCombo.SelectedValuePath = "Slug";
                WallhavenResolutionCombo.SelectedValue = _currentWallhavenResolution;
                _wallhavenResolutionReady = true;
            }
        }

        private void AddGBizhiChip(string name, string tag, bool isChecked)
        {
            var cb = new System.Windows.Controls.CheckBox
            {
                Content = name,
                Style = (Style)FindResource("OutlineChipCheck"),
                IsChecked = isChecked,
                Tag = tag
            };
            cb.Checked += GBizhiCategory_StateChanged;
            cb.Unchecked += GBizhiCategory_StateChanged;
            GBizhiCategoryPanel.Children.Add(cb);
        }

        private System.Windows.Controls.CheckBox? GetGBizhiChip(string tag)
        {
            foreach (var child in GBizhiCategoryPanel.Children)
                if (child is System.Windows.Controls.CheckBox cb && cb.Tag is string t && t == tag)
                    return cb;
            return null;
        }

        private bool IsGBizhiChipChecked(string tag) => GetGBizhiChip(tag)?.IsChecked == true;

        private void SetGBizhiChip(string tag, bool on)
        {
            var cb = GetGBizhiChip(tag);
            if (cb != null) cb.IsChecked = on;
        }

        private bool IsGBizhiContentAnyChecked()
            => IsGBizhiChipChecked("content-general") || IsGBizhiChipChecked("content-anime") || IsGBizhiChipChecked("content-people");

        private bool IsGBizhiContentAllChecked()
            => IsGBizhiChipChecked("content-general") && IsGBizhiChipChecked("content-anime") && IsGBizhiChipChecked("content-people");

        private bool IsGBizhiPurityAnyChecked()
            => IsGBizhiChipChecked("purity-sfw") || IsGBizhiChipChecked("purity-sketchy");

        /// <summary>按当前复选状态生成 wallhaven 分类 slug（"categories|purity"，如 111|100）。</summary>
        private string BuildGBizhiSlug()
        {
            int g = IsGBizhiChipChecked("content-general") ? 1 : 0;
            int a = IsGBizhiChipChecked("content-anime") ? 1 : 0;
            int p = IsGBizhiChipChecked("content-people") ? 1 : 0;
            int s = IsGBizhiChipChecked("purity-sfw") ? 1 : 0;
            int k = IsGBizhiChipChecked("purity-sketchy") ? 1 : 0;
            return $"{g}{a}{p}|{s}{k}0";
        }

        private async void GBizhiCategory_StateChanged(object sender, RoutedEventArgs e)
        {
            if (_gBizhiChipUpdating) return;
            if (sender is not System.Windows.Controls.CheckBox cb || cb.Tag is not string tag) return;

            // “全部”联动：勾选则全选内容分类；取消则回勾（不允许出现无内容分类的状态）
            if (tag == "content-all")
            {
                if (cb.IsChecked == true)
                {
                    _gBizhiChipUpdating = true;
                    SetGBizhiChip("content-general", true);
                    SetGBizhiChip("content-anime", true);
                    SetGBizhiChip("content-people", true);
                    _gBizhiChipUpdating = false;
                }
                else
                {
                    _gBizhiChipUpdating = true;
                    cb.IsChecked = true;
                    _gBizhiChipUpdating = false;
                    return;
                }
            }
            else if (tag.StartsWith("content-", StringComparison.Ordinal))
            {
                // 内容分类至少保留一个
                if (cb.IsChecked == false && !IsGBizhiContentAnyChecked())
                {
                    _gBizhiChipUpdating = true;
                    cb.IsChecked = true;
                    _gBizhiChipUpdating = false;
                    return;
                }
                // 三个内容分类全选时自动勾上“全部”，否则取消“全部”
                _gBizhiChipUpdating = true;
                SetGBizhiChip("content-all", IsGBizhiContentAllChecked());
                _gBizhiChipUpdating = false;
            }
            else if (tag.StartsWith("purity-", StringComparison.Ordinal))
            {
                // 纯度至少保留一个
                if (cb.IsChecked == false && !IsGBizhiPurityAnyChecked())
                {
                    _gBizhiChipUpdating = true;
                    cb.IsChecked = true;
                    _gBizhiChipUpdating = false;
                    return;
                }
            }

            var slug = BuildGBizhiSlug();
            if (slug == _currentGBizhiCategory) return;
            _currentGBizhiCategory = slug;
            _currentGBizhiPage = 1;
            await LoadGBizhiAsync(true);
            GBizhiScroll.ScrollToTop();
        }

        private async void WallhavenResolution_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (!_wallhavenResolutionReady) return;
            if (WallhavenResolutionCombo.SelectedValue is not string slug) return;
            _currentWallhavenResolution = slug;
            _currentGBizhiPage = 1;
            await LoadGBizhiAsync(true);
            GBizhiScroll.ScrollToTop();
        }

        private async void GBizhiScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (_gBizhiLoading || !_hasMoreGBizhi) return;
            if (!IsNearBottom(e)) return;
            _currentGBizhiPage++;
            await LoadGBizhiAsync(false);
        }

        private async Task LoadGBizhiAsync(bool reset)
        {
            if (_gBizhiLoading) return;
            _gBizhiLoading = true;
            _onlineCts?.Cancel();
            _onlineCts = new CancellationTokenSource();

            GBizhiStatus.Visibility = Visibility.Visible;
            GBizhiStatus.Text = "正在加载...";
            if (reset)
            {
                GBizhiWallpapers.Clear();
                _hasMoreGBizhi = true;
                // 重置静态末页标记，避免上一分类/分辨率残留的 last_page 干扰新筛选的翻页判断
                OnlineWallpaperCrawler.WallhavenLastPage = int.MaxValue;
            }
            GBizhiEmptyHint.Visibility = Visibility.Collapsed;

            try
            {
                // 循环续填：每次加载一页后，若视口仍未铺满且还有更多内容，自动加载下一页，
                // 避免依赖用户滚动事件（偶发不触发或内容不足一屏时滚动条无位移导致加载停止）
                while (true)
                {
                    var list = await OnlineWallpaperCrawler.FetchAsync("wallhaven", _currentGBizhiPage, _currentGBizhiCategory, _onlineCts.Token, _currentWallhavenResolution);
                    foreach (var item in list)
                    {
                        item.DownloadedPath = _downloadHistory.TryGetExistingPath(item.DetailUrl);
                        item.IsApplied = IsPathApplied(item.DownloadedPath);
                        GBizhiWallpapers.Add(item);
                    }

                    _gBizhiLoaded = true;
                    // 已到达 API 返回的 last_page 或返回空列表时停止加载
                    var lastPage = OnlineWallpaperCrawler.WallhavenLastPage;
                    _hasMoreGBizhi = lastPage == int.MaxValue
                        ? list.Count > 0   // 未解析到末页（异常/回退响应），按列表非空判断
                        : list.Count > 0 && _currentGBizhiPage < lastPage;

                    // 视口已铺满（或高度未知）时交给滚动事件继续；未铺满且还有更多则续载
                    double vh = GBizhiScroll.ViewportHeight;
                    if (!_hasMoreGBizhi) break;
                    if (vh > 0 && GBizhiScroll.ExtentHeight >= vh * 0.92) break;
                    _currentGBizhiPage++;
                }

                GBizhiStatus.Text = $"已加载 {GBizhiWallpapers.Count} 条";
                GBizhiEmptyHint.Visibility = GBizhiWallpapers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                Logger.Log($"[wallhaven] 加载失败: {ex}");
                if (!_gBizhiLoaded)
                {
                    GBizhiStatus.Text = "加载失败：" + ex.Message;
                    GBizhiEmptyHint.Visibility = Visibility.Visible;
                }
                else
                {
                    // 翻页失败（网络抖动/限流）：回退页码并提示，用户再次滚动即可重试
                    if (!reset && _currentGBizhiPage > 1) _currentGBizhiPage--;
                    GBizhiStatus.Text = "加载失败：" + ex.Message;
                }
            }
            finally
            {
                _gBizhiLoading = false;
            }
        }

        private async void DynamicScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (_dynamicLoading || !_hasMoreDynamic) return;
            if (!IsNearBottom(e)) return;
            _currentDynamicPage++;
            await LoadDynamicAsync(false);
        }

        private async Task LoadDynamicAsync(bool reset)
        {
            if (_dynamicLoading) return;
            _dynamicLoading = true;
            _onlineCts?.Cancel();
            _onlineCts = new CancellationTokenSource();

            DynamicStatus.Visibility = Visibility.Visible;
            DynamicStatus.Text = "正在加载...";
            if (reset)
            {
                DynamicWallpapers.Clear();
                _hasMoreDynamic = true;
            }
            DynamicEmptyHint.Visibility = Visibility.Collapsed;

            try
            {
                // 动态壁纸源使用 3gbizhi-dt（国内可访问），避免海外站点 SSL/TLS 被墙
                var list = await OnlineWallpaperCrawler.FetchAsync("3gbizhi-dt", _currentDynamicPage, null, _onlineCts.Token);
                foreach (var item in list)
                {
                    item.DownloadedPath = _downloadHistory.TryGetExistingPath(item.DetailUrl);
                    item.IsApplied = IsPathApplied(item.DownloadedPath);
                    DynamicWallpapers.Add(item);
                }

                _dynamicLoaded = true;
                _hasMoreDynamic = list.Count > 0;
                DynamicStatus.Text = $"已加载 {DynamicWallpapers.Count} 条";
                DynamicEmptyHint.Visibility = DynamicWallpapers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                Logger.Log($"[dynamic] 加载失败: {ex}");
                if (!_dynamicLoaded)
                {
                    DynamicStatus.Text = "加载失败：" + ex.Message;
                    DynamicEmptyHint.Visibility = Visibility.Visible;
                }
            }
            finally
            {
                _dynamicLoading = false;
            }
        }

        /// <summary>在线壁纸卡片"解除壁纸"：把对应已下载壁纸从桌面解除，按钮同步恢复为"设为桌面"。
        /// 状态回写由 RefreshActiveBadges → SyncOnlineAppliedStates 统一完成；同时清除右侧状态栏
        /// 残留的"已设为桌面"提示，保持按钮与状态栏提示一致。</summary>
        private async void OnlineClearWallpaper_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button btn) btn.IsEnabled = false;
            try
            {
                var item = (sender as FrameworkElement)?.DataContext as OnlineWallpaperItem;
                if (item == null || string.IsNullOrEmpty(item.DownloadedPath)) return;

                var libraryItem = Library.FirstOrDefault(i =>
                    string.Equals(i.Path, item.DownloadedPath, StringComparison.OrdinalIgnoreCase));
                if (libraryItem != null)
                {
                    await ClearItemAsync(libraryItem);
                }
                else
                {
                    // 库里没有对应条目（已被移除等）：按路径直接清除桌面应用
                    foreach (var idx in _manager.ActiveScreenIndices)
                        if (string.Equals(_manager.GetActivePath(idx), item.DownloadedPath, StringComparison.OrdinalIgnoreCase))
                            await _manager.ClearScreenAsync(idx);
                    RefreshActiveBadges();
                    SetStatusText(StatusSummary());
                }

                // 解除成功后同步更新对应来源的状态栏提示，避免右侧仍残留"已设为桌面"。
                // 来源映射与 OnlineDownload_Click 保持一致（3gbizhi→GBizhiStatus、3gbizhi-dt→DynamicStatus、其余→NetbianStatus）。
                // 该语句在解除失败（ClearItemAsync / ClearScreenAsync 抛异常）时不会执行，避免误报已解除。
                var status = item.Source switch
                {
                    "3gbizhi" => GBizhiStatus,
                    "3gbizhi-dt" => DynamicStatus,
                    "wallhaven" => GBizhiStatus,
                    _ => NetbianStatus
                };
                status.Text = "已解除壁纸";
            }
            finally
            {
                if (sender is System.Windows.Controls.Button btn2) btn2.IsEnabled = true;
            }
        }

        private async void OnlineDownload_Click(object sender, RoutedEventArgs e)
        {
            var item = (sender as FrameworkElement)?.DataContext as OnlineWallpaperItem;
            if (item == null) return;

            // 按条目来源更新对应页签的状态文本（各来源已使用独立集合）
            var status = item.Source switch
            {
                "3gbizhi" => GBizhiStatus,
                "3gbizhi-dt" => DynamicStatus,
                "wallhaven" => GBizhiStatus,
                _ => NetbianStatus
            };

            // 已下载且文件仍存在：直接设为桌面
            if (!string.IsNullOrEmpty(item.DownloadedPath) && File.Exists(item.DownloadedPath))
            {
                await ApplyDownloadedWallpaperAsync(item.DownloadedPath, status);
                return;
            }

            // 有下载记录但文件被用户删除，或全新下载：都走下载流程
            // （DownloadAsync 内部会按 DetailUrl 更新下载记录；再次点击则命中首个分支直接应用）
            bool wasDeleted = _downloadHistory.Find(item.DetailUrl) != null;
            status.Text = $"{(wasDeleted ? "文件已删除，重新下载" : "正在下载")}：{item.Title}";
            try
            {
                var path = await OnlineWallpaperCrawler.DownloadAsync(item, _downloadHistory);
                if (!string.IsNullOrEmpty(path))
                {
                    item.DownloadedPath = path;
                    await AddPathAsync(path, ProviderFactory.DetectType(path));
                    bool applied = await ApplyDownloadedWallpaperAsync(path, status);

                    // 仅应用成功时补充分辨率说明；应用失败时保留"设置未生效，请重试"，
                    // 避免状态栏与按钮（回滚为"设为桌面"）不一致。
                    if (!applied) return;

                    // 3gbizhi 静态壁纸：免费下载的是站点预览图（通常为 1280×720 webp），
                    // 详情页标注的 2K/4K 原图需要登录积分，提示用户避免误解为下载错误。
                    if (item.Source == "3gbizhi")
                    {
                        var resNote = string.IsNullOrWhiteSpace(item.Resolution) ? "" : $"，原图 {item.Resolution} 需登录积分";
                        status.Text = $"已设为桌面（当前下载为预览{resNote}）";
                    }
                    // 3gbizhi 动态壁纸：免费可下载的最高清就是详情页 1280×720 的 MP4；
                    // 站点标注的 2K/4K 原图需登录并消耗会员积分，应用无法绕过，如实告知用户。
                    else if (item.Source == "3gbizhi-dt")
                    {
                        var resNote = string.IsNullOrWhiteSpace(item.Resolution) ? "" : $"（原图 {item.Resolution}）";
                        status.Text = $"已设为桌面（当前为免费最高清 1280×720{resNote}，4K 需登录积分）";
                    }
                    // wallhaven 为原图直链下载（API 返回的 path），直接展示分辨率即可；
                    // 受站点速率限制，失败时 DownloadAsync 返回 null 并已记录日志。
                    else if (item.Source == "wallhaven")
                    {
                        var resNote = string.IsNullOrWhiteSpace(item.Resolution) ? "" : $"（{item.Resolution} 原图）";
                        status.Text = $"已设为桌面{resNote}";
                    }
                }
                else
                {
                    status.Text = "下载失败：无法解析资源地址";
                    MessageBox.Show("该资源自动下载失败，请稍后重试或换一张壁纸。", "下载失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[在线壁纸] 下载失败: {ex}");
                status.Text = "下载失败：" + ex.Message;
                MessageBox.Show("下载失败：" + ex.Message, "下载失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        /// <summary>把已下载的壁纸加入库（若未加入）并应用到当前选中的屏幕。返回是否真正应用成功，
        /// 供调用方决定是否写入"已设为桌面"提示，避免应用失败时状态栏与按钮（回滚为"设为桌面"）不一致。</summary>
        private async Task<bool> ApplyDownloadedWallpaperAsync(string path, TextBlock status)
        {
            var libraryItem = Library.FirstOrDefault(i => i.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
            if (libraryItem == null)
            {
                var type = ProviderFactory.DetectType(path);
                libraryItem = new WallpaperItem(path, type);
                Library.Add(libraryItem);
                if (!_config.Library.Contains(path)) _config.Library.Add(path);
                _config.Save();
                RefreshEmpty();
            }
            await ApplyItemAsync(libraryItem, SelectedScreen());

            // ApplyItemAsync 内部已通过 RefreshActiveBadges 校正真实状态：
            // 只有当前路径确实被应用为任意屏幕壁纸时，才提示"已设为桌面"。
            bool applied = IsPathApplied(path);
            status.Text = applied ? "已设为桌面" : "设置未生效，请重试";
            return applied;
        }

        /// <summary>
        /// 退出程序：先停止所有壁纸并恢复系统静态壁纸，再关闭进程。
        /// 用于“关闭主窗口（非最小化到托盘）”与“托盘退出”两条路径，
        /// 确保无论怎么退出，桌面壁纸一定会被解除。
        /// </summary>
        public async Task ExitAndCleanupAsync()
        {
            if (_exiting) return;
            _exiting = true;
            ForceExit = true;
            Hide();
            try
            {
                // restoreWallpaper=true：退出时必须把系统壁纸刷回原始静态图，
                // 否则 WorkerW 子窗口销毁后只会露出桌面背景（灰底/黑屏），不会自动恢复原壁纸。
                await _manager.StopAsync(restoreWallpaper: true);
            }
            catch (Exception ex)
            {
                Logger.Log($"[MainWindow] 退出时停止壁纸出错: {ex}");
            }
            System.Windows.Application.Current.Shutdown();
        }

        private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            if (_exiting)
            {
                // 已在退出清理流程中，直接放行，避免重复进入
                return;
            }

            if (_config.CloseToTray)
            {
                // 关闭主窗口时缩到托盘，壁纸继续运行
                e.Cancel = true;
                Hide();
                return;
            }

            // 用户未勾选最小化到托盘：取消本次关闭事件，走统一清理流程
            // （停止壁纸并恢复系统壁纸后关闭）。窗口立即隐藏，不再等待耗时清理。
            e.Cancel = true;
            _ = ExitAndCleanupAsync();
        }

        private void OnDragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
                e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }

        private async void OnDrop(object sender, DragEventArgs e)
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
                foreach (var f in files) await AddPathAsync(f);
            e.Handled = true;
        }
    }
}
