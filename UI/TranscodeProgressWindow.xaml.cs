using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

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

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        /// <summary>窗口句柄就绪后，用 DWM 将标题栏背景设为黑色、文字设为白色（仅 Win10 2004+ 生效，Win11 正常）。</summary>
        private void OnSourceInitialized(object? sender, EventArgs e)
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;

            const int DWMWA_CAPTION_COLOR = 35;
            const int DWMWA_TEXT_COLOR = 36;
            int caption = 0x000000; // COLORREF 0x00BBGGRR -> 黑
            int text = 0xFFFFFF;    // 白
            _ = DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref caption, sizeof(int));
            _ = DwmSetWindowAttribute(hwnd, DWMWA_TEXT_COLOR, ref text, sizeof(int));
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
