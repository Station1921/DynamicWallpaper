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

        protected override void OnStartup(StartupEventArgs e)
        {
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
            if (Current.MainWindow is MainWindow mw) mw.ForceExit = true;
            Current.Shutdown();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _tray?.Dispose();
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
