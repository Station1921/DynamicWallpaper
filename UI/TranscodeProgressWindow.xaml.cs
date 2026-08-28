using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Input;

namespace DynamicWallpaper.UI
{
    /// <summary>ffmpeg 转码进度窗口：显示文件名、进度条与实时百分比/耗时。
    /// 窗口在以下情况被关闭时自动终止转码并清理半成品文件：
    ///   - 用户点击右上角关闭按钮或按 Alt+F4；
    ///   - 软件整体退出（WPF 关闭所有窗口，Closing 事件触发）。
    /// 转码逻辑在启动 ffmpeg 后把进程与产物路径赋值给本窗口，
    /// 由 OnClosing 负责 Kill 进程 + 删除未完成的产物。</summary>
    public partial class TranscodeProgressWindow : Window
    {
        private readonly CancellationTokenSource _cts = new();
        private bool _completed;

        public TranscodeProgressWindow(string fileName)
        {
            InitializeComponent();
            FileText.Text = "正在转码：" + fileName;
        }

        /// <summary>取消令牌：窗口关闭时触发，用于中断转码逻辑里的等待。</summary>
        public CancellationToken Token => _cts.Token;

        /// <summary>正在转码的 ffmpeg 进程；启动后由转码逻辑赋值，关闭窗口时强制结束。</summary>
        public Process? TranscodeProcess { get; set; }

        /// <summary>转码产物路径；启动后由转码逻辑赋值，关闭窗口且未完成时删除该半成品。</summary>
        public string? OutputPath { get; set; }

        /// <summary>标记转码成功完成（产物有效）。设置后关闭窗口不再删除产物。</summary>
        public void MarkCompleted() => _completed = true;

        public void SetProgress(double percent, string status)
        {
            if (_completed || _cts.IsCancellationRequested || Dispatcher.HasShutdownStarted) return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                Progress.IsIndeterminate = false;
                Progress.Value = Math.Clamp(percent, 0, 100);
                StatusText.Text = status;
            }));
        }

        public void SetIndeterminate(string status)
        {
            if (_completed || _cts.IsCancellationRequested || Dispatcher.HasShutdownStarted) return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                Progress.IsIndeterminate = true;
                StatusText.Text = status;
            }));
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (!_completed)
            {
                // 未完成即关闭：终止转码进程并清理半成品文件
                try { _cts.Cancel(); } catch { }
                try { if (TranscodeProcess != null && !TranscodeProcess.HasExited) TranscodeProcess.Kill(); } catch { }
                // ffmpeg 进程被 Kill 后文件句柄不会立即释放，直接 Delete 常因“文件被占用”失败，
                // 因此重试若干次（每次短暂等待句柄释放）确保半成品被真正删除。
                CleanupPartial(OutputPath);
            }
            base.OnClosing(e);
        }

        /// <summary>删除未完成的转码产物（半成品）。带重试以规避 ffmpeg 进程刚被 Kill 时
        /// 文件句柄尚未释放导致的“文件被占用”失败。源文件不会被删除。</summary>
        public static void CleanupPartial(string? path)
        {
            if (string.IsNullOrEmpty(path)) return;
            for (int i = 0; i < 15; i++)
            {
                try
                {
                    if (!File.Exists(path)) return;
                    File.Delete(path);
                    if (!File.Exists(path)) return;
                }
                catch { /* 句柄未释放或权限问题，稍后重试 */ }
                Thread.Sleep(60);
            }
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
                DragMove();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
