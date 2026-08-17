using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
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

        private List<ScreenOption> _screenOptions = new();
        private CancellationTokenSource? _onlineCts;

        private string _currentNetbianCategory = "";
        private int _currentNetbianPage = 1;
        private string _currentGBizhiCategory = "deskMV";
        private int _currentGBizhiPage = 1;
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
            InitializeComponent();

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
            _manager = new WallpaperManager(_config);
            _downloadHistory = DownloadHistory.Load();
            _manager.Start();

            foreach (var p in _config.Library)
                if (File.Exists(p)) Library.Add(new WallpaperItem(p, ProviderFactory.DetectType(p)));

            InitScreenSelector();
            RefreshEmpty();
            RefreshActiveBadges();
            StatusText.Text = StatusSummary();
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
            Loaded += (_, _) => UpdateCardWidth(NetbianScroll);
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
        private void AddPath(string path, WallpaperType? type = null)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
            if (Library.Any(i => i.Path.Equals(path, StringComparison.OrdinalIgnoreCase))) return;

            var t = type ?? ProviderFactory.DetectType(path);
            var item = new WallpaperItem(path, t);
            Library.Add(item);
            if (!_config.Library.Contains(path)) _config.Library.Add(path);
            _config.Save();
            RefreshEmpty();
        }

        private void RefreshEmpty()
        {
            EmptyHint.Visibility = Library.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (MyWallpaperCount != null) MyWallpaperCount.Text = $"共 {Library.Count} 个";
        }

        private void Add_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog { Multiselect = true, Filter = Filter, Title = "选择壁纸文件" };
            if (dlg.ShowDialog() == true)
                foreach (var f in dlg.FileNames) AddPath(f);
        }

        private async void AddLink_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new AddLinkDialog { Owner = this };
            if (dlg.ShowDialog() == true && !string.IsNullOrEmpty(dlg.DownloadedPath))
            {
                var path = dlg.DownloadedPath!;
                AddPath(path);
                if (dlg.ApplyAfter && Library.FirstOrDefault(i => i.Path == path) is { } item)
                    await ApplyItemAsync(item, SelectedScreen());
                RefreshActiveBadges();
                StatusText.Text = StatusSummary();
            }
        }

        // ---------- 应用壁纸 ----------
        private async Task ApplyItemAsync(WallpaperItem item, int targetScreen)
        {
            // 立即把该条目标记为“已应用”，让按钮（设为壁纸→解除）即时更新，
            // 不等待较慢的 WorkerW 注入 / 系统壁纸切换完成。后续 RefreshActiveBadges 会校正真实状态。
            item.IsActive = true;

            try
            {
                if (targetScreen < 0)
                {
                    var screens = ScreenManager.GetScreens();
                    foreach (var sc in screens)
                        await _manager.SetWallpaperAsync(item.Path, item.Type, sc.Index);
                }
                else
                {
                    await _manager.SetWallpaperAsync(item.Path, item.Type, targetScreen);
                }

                RefreshActiveBadges();
                StatusText.Text = StatusSummary();
            }
            catch (Exception ex)
            {
                // 应用失败时回滚乐观标记，避免按钮状态与实际情况不符
                RefreshActiveBadges();
                StatusText.Text = StatusSummary();
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
            StatusText.Text = StatusSummary();
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
            var remove = new MenuItem { Header = "从库移除" };
            remove.Click += (_, _) => RemoveItem(item);

            menu.Items.Add(setMenu);
            menu.Items.Add(remove);
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
            StatusText.Text = StatusSummary();
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
            GBizhiStatus.Visibility = header == "3G壁纸" ? Visibility.Visible : Visibility.Collapsed;
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
            else if (header == "3G壁纸" && !_gBizhiLoaded)
            {
                await Dispatcher.BeginInvoke(async () =>
                {
                    _currentGBizhiCategory = "deskMV";
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
            foreach (var cat in OnlineWallpaperCrawler.GBizhiCategories)
            {
                var rb = new System.Windows.Controls.RadioButton
                {
                    Content = cat.Name,
                    Style = (Style)FindResource("OutlineChip"),
                    IsChecked = cat.Slug == _currentGBizhiCategory,
                    Tag = cat.Slug
                };
                rb.Checked += GBizhiCategory_Checked;
                GBizhiCategoryPanel.Children.Add(rb);
            }
        }

        private async void GBizhiCategory_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is not System.Windows.Controls.RadioButton rb || rb.Tag is not string slug) return;
            _currentGBizhiCategory = slug;
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
            }
            GBizhiEmptyHint.Visibility = Visibility.Collapsed;

            try
            {
                var list = await OnlineWallpaperCrawler.FetchAsync("3gbizhi", _currentGBizhiPage, _currentGBizhiCategory, _onlineCts.Token);
                foreach (var item in list)
                {
                    item.DownloadedPath = _downloadHistory.TryGetExistingPath(item.DetailUrl);
                    GBizhiWallpapers.Add(item);
                }

                _gBizhiLoaded = true;
                _hasMoreGBizhi = list.Count > 0;
                GBizhiStatus.Text = $"已加载 {GBizhiWallpapers.Count} 条";
                GBizhiEmptyHint.Visibility = GBizhiWallpapers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                Logger.Log($"[3gbizhi] 加载失败: {ex}");
                if (!_gBizhiLoaded)
                {
                    GBizhiStatus.Text = "加载失败：" + ex.Message;
                    GBizhiEmptyHint.Visibility = Visibility.Visible;
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

        private async void OnlineDownload_Click(object sender, RoutedEventArgs e)
        {
            var item = (sender as FrameworkElement)?.DataContext as OnlineWallpaperItem;
            if (item == null) return;

            // 按条目来源更新对应页签的状态文本（各来源已使用独立集合）
            var status = item.Source switch
            {
                "3gbizhi" => GBizhiStatus,
                "3gbizhi-dt" => DynamicStatus,
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
                    AddPath(path, ProviderFactory.DetectType(path));
                    await ApplyDownloadedWallpaperAsync(path, status);

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

        /// <summary>把已下载的壁纸加入库（若未加入）并应用到当前选中的屏幕。</summary>
        private async Task ApplyDownloadedWallpaperAsync(string path, TextBlock status)
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
            status.Text = "已设为桌面";
        }

        private async void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            if (ForceExit)
            {
                // 第二次进入：已在退出流程中，直接放行
                return;
            }

            if (_config.CloseToTray)
            {
                // 关闭主窗口时缩到托盘，壁纸继续运行
                e.Cancel = true;
                Hide();
                return;
            }

            // 用户未勾选最小化到托盘：先立即隐藏窗口，取消本次关闭事件，
            // 在后台异步停止壁纸并恢复系统静态壁纸，随后触发真正关闭。
            // 这样用户点击关闭后窗口立刻消失，不再等待 SendMessageTimeout/SPI 等耗时清理。
            e.Cancel = true;
            ForceExit = true;
            Hide();
            try
            {
                // restoreWallpaper=true：程序退出时必须把系统壁纸刷回原始静态图，
                // 否则 WorkerW 子窗口销毁后只会露出桌面背景（灰底/黑屏），不会自动恢复原壁纸。
                await _manager.StopAsync(restoreWallpaper: true);
            }
            catch (Exception ex)
            {
                Logger.Log($"[MainWindow] 停止壁纸时出错: {ex}");
            }
            _ = Dispatcher.BeginInvoke(new Action(Close), System.Windows.Threading.DispatcherPriority.Background);
        }

        private void OnDragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
                e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }

        private void OnDrop(object sender, DragEventArgs e)
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
                foreach (var f in files) AddPath(f);
            e.Handled = true;
        }
    }
}
