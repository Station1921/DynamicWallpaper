using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace DynamicWallpaper.Core
{
    /// <summary>单台显示器的描述，用于多屏独立壁纸。</summary>
    public class ScreenInfo
    {
        public int Index { get; set; }
        public Rectangle Bounds { get; set; }
        public string DeviceName { get; set; } = "";
        public bool IsPrimary { get; set; }
        public string DisplayName { get; set; } = "";
    }

    /// <summary>
    /// 多显示器管理：返回每台显示器的屏幕矩形与友好名称。
    /// 索引约定：主屏固定为 0，其余依次为 1、2……与持久化分配一致。
    /// </summary>
    public static class ScreenManager
    {
        public static List<ScreenInfo> GetScreens()
        {
            var all = Screen.AllScreens;
            var infos = new List<ScreenInfo>();
            foreach (var s in all)
            {
                infos.Add(new ScreenInfo
                {
                    Bounds = s.Bounds,
                    DeviceName = s.DeviceName,
                    IsPrimary = s.Primary
                });
            }

            var primary = infos.FirstOrDefault(i => i.IsPrimary);
            if (primary != null) { primary.Index = 0; primary.DisplayName = "主屏"; }

            int n = 1;
            foreach (var i in infos.Where(i => !i.IsPrimary))
            {
                i.Index = n;
                i.DisplayName = "屏幕 " + n;
                n++;
            }

            return infos.OrderBy(i => i.Index).ToList();
        }

        public static int Count => Screen.AllScreens.Length;

        public static Rectangle Primary => Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
    }
}
