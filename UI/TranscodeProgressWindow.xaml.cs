using System;
using System.Windows;

namespace DynamicWallpaper.UI
{
    /// <summary>ffmpeg 转码进度窗口：显示文件名、进度条与实时百分比/耗时。</summary>
    public partial class TranscodeProgressWindow : Window
    {
        public TranscodeProgressWindow(string fileName)
        {
            InitializeComponent();
            FileText.Text = "正在转码：" + fileName;
        }

        /// <summary>更新确定进度（百分比 + 已用/总时长）。</summary>
        public void SetProgress(double percent, string status)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                Progress.IsIndeterminate = false;
                Progress.Value = Math.Clamp(percent, 0, 100);
                StatusText.Text = status;
            }));
        }

        /// <summary>无法获知总时长时切换到不确定进度条。</summary>
        public void SetIndeterminate(string status)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                Progress.IsIndeterminate = true;
                StatusText.Text = status;
            }));
        }
    }
}
