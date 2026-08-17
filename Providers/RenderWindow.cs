using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Brushes = System.Windows.Media.Brushes;

namespace DynamicWallpaper.Providers
{
    /// <summary>
    /// 无边框、不出现在任务栏的渲染宿主窗口，渲染内容挂接到此窗口后整体塞进 WorkerW。
    /// </summary>
    public class RenderWindow : Window
    {
        public Grid RootGrid { get; }

        public RenderWindow()
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Background = Brushes.Black;
            RootGrid = new Grid();
            Content = RootGrid;
            Width = 1920;
            Height = 1080;
        }
    }
}
