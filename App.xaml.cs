using System;
using System.Diagnostics;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms;
using DynamicWallpaper.Core;
using Application = System.Windows.Application;

namespace DynamicWallpaper
{
    public partial class App : Application
    {
        private NotifyIcon? _tray;
        private Mutex? _singleInstanceMutex;
        /// <summary>周期性缓存清理定时器：仅当配置开启「自动清理过期缓存」时才有意义，
        /// 定时器本身常驻，回调内部读取最新配置决定是否执行。</summary>
        private System.Threading.Timer? _cacheCleanupTimer;

        protected override void OnStartup(StartupEventArgs e)
        {
            // 单文件（PublishSingleFile）自包含部署下，WPF 的 InitializeComponent/LoadComponent
            // 会按程序集名解析 BAML 资源所在程序集；单文件宿主把程序集内嵌，按名字直接加载会抛
            // FileNotFoundException，导致 MainWindow 构造失败、主窗口只剩背景色（黑屏/白屏）。
            // 注册解析回退：把对当前程序集名的请求解析为执行程序集本身，使 XAML 资源可正常加载。
            // （.NET 8 单文件下该失败走 AssemblyLoadContext 解析链，AppDomain.AssemblyResolve 不一定触发，
            //  因此两者都注册作双保险；多文件发布时此回退为 no-op，无害。）
            string thisAssemblyName = typeof(App).Assembly.GetName().Name!;
            AppDomain.CurrentDomain.AssemblyResolve += (_, args) =>
            {
                try
                {
                    if (new System.Reflection.AssemblyName(args.Name!).Name == thisAssemblyName)
                        return typeof(App).Assembly;
                }
                catch { }
                return null!;
            };
            System.Runtime.Loader.AssemblyLoadContext.Default.Resolving += (_, name) =>
                name.Name == thisAssemblyName ? typeof(App).Assembly : null!;

            // 全局未处理异常捕获：写入 app.log，避免进程静默崩溃
            // （之前表现为托盘图标悬停即消失、双击无反应）
            DispatcherUnhandledException += (_, ex) =>
            {
                Logger.Log("DispatcherUnhandledException", ex.Exception);
                ex.Handled = true;
            };
            AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
                Logger.Log("AppDomain.UnhandledException", (Exception)ex.ExceptionObject);
            TaskScheduler.UnobservedTaskException += (_, ex) =>
            {
                Logger.Log("TaskScheduler.UnobservedTaskException", ex.Exception);
                ex.SetObserved();
            };

            // 只允许一个实例运行，避免重复注入造成桌面出现多张相同壁纸
            const string mutexName = "DynamicWallpaper_SingleInstance_7a8f3e2d";
            bool createdNew;
            _singleInstanceMutex = new Mutex(true, mutexName, out createdNew);
            if (!createdNew)
            {
                _singleInstanceMutex = null;
                System.Windows.Forms.MessageBox.Show("动态桌面已在运行。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                Current.Shutdown();
                return;
            }

            base.OnStartup(e);

            _tray = new NotifyIcon
            {
                // 使用 EXE 自带的原生图标（ApplicationIcon 已嵌入），而非系统默认图标
                Icon = LoadAppIcon() ?? SystemIcons.Application,
                Visible = true,
                Text = "动态桌面"
            };

            var menu = new ContextMenuStrip();
            menu.Items.Add("打开", null, (_, _) => ShowMain());
            menu.Items.Add("退出", null, (_, _) => ExitApp());
            _tray.ContextMenuStrip = menu;
            _tray.DoubleClick += (_, _) => ShowMain();

            // 是否静默启动：开机自启时注册表项带 --silent 参数，仅驻留托盘、不弹出主界面。
            // （MainWindow 仍会被构造，其构造函数会恢复已保存的每屏壁纸。）
            bool silent = false;
            foreach (var a in e.Args)
            {
                if (a.Equals("--silent", StringComparison.OrdinalIgnoreCase)) { silent = true; break; }
            }

            var mw = new MainWindow();
            Current.MainWindow = mw;
            if (!silent)
            {
                Logger.Log("[App] 显示主窗口（非静默）");
                mw.Show();
            }
            else
            {
                Logger.Log("[App] 静默启动（--silent），仅驻留托盘");
            }

            // 启动周期缓存清理：启动 5 分钟后首次检查，之后每 6 小时一次。
            // 回调内部读取最新配置，仅在用户开启「自动清理过期缓存」时删除超期文件。
            StartCacheCleanupTimer();
        }

        private void StartCacheCleanupTimer()
        {
            try
            {
                _cacheCleanupTimer = new System.Threading.Timer(_ =>
                {
                    try { Core.CacheManager.RunScheduledCleanup(); }
                    catch (Exception ex) { Logger.Log("[App] 缓存自动清理异常: " + ex.Message); }
                }, null, TimeSpan.FromMinutes(5), TimeSpan.FromHours(6));
            }
            catch (Exception ex)
            {
                Logger.Log("[App] 缓存清理定时器启动失败: " + ex.Message);
            }
        }

        private void ShowMain()
        {
            if (Current.MainWindow is MainWindow mw)
            {
                mw.Show();
                mw.Activate();
            }
        }

        private void ExitApp()
        {
            // 托盘“退出”：走统一的清理流程——先停止所有壁纸并恢复系统静态壁纸，再关闭进程。
            // 这样即使程序是缩在托盘时强制退出，桌面壁纸也会被解除。
            if (Current.MainWindow is MainWindow mw)
                _ = mw.ExitAndCleanupAsync();
            else
                Current.Shutdown();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _tray?.Dispose();
            try { _cacheCleanupTimer?.Dispose(); } catch { }
            try { _singleInstanceMutex?.ReleaseMutex(); } catch { }
            _singleInstanceMutex?.Dispose();
            base.OnExit(e);
        }

        /// <summary>
        /// 从 EXE 自带的原生图标（ApplicationIcon 嵌入的 .ico）提取托盘图标；失败返回 null。
        /// </summary>
        private static Icon? LoadAppIcon()
        {
            try
            {
                var path = Process.GetCurrentProcess().MainModule?.FileName;
                if (path != null) return System.Drawing.Icon.ExtractAssociatedIcon(path);
            }
            catch { }
            return null;
        }
    }
}
