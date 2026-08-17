using System.Windows;
using Microsoft.Win32;

namespace DynamicWallpaper.UI
{
    public partial class WebWallpaperDialog : Window
    {
        public string? UrlOrPath => string.IsNullOrWhiteSpace(UrlBox.Text) ? null : UrlBox.Text.Trim();

        public WebWallpaperDialog()
        {
            InitializeComponent();
        }

        private void Browse_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "网页文件|*.html;*.htm|所有文件|*.*",
                Title = "选择本地网页文件"
            };
            if (dlg.ShowDialog() == true) UrlBox.Text = dlg.FileName;
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            var v = UrlOrPath;
            if (string.IsNullOrWhiteSpace(v) ||
                (!v!.StartsWith("http://", System.StringComparison.OrdinalIgnoreCase) &&
                 !v.StartsWith("https://", System.StringComparison.OrdinalIgnoreCase) &&
                 !System.IO.File.Exists(v)))
            {
                Hint.Text = "请输入有效的网址（http/https）或存在的本地 HTML 路径。";
                Hint.Foreground = System.Windows.Media.Brushes.Orange;
                return;
            }
            DialogResult = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
