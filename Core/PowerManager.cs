using System;
using Microsoft.Win32;
using System.Windows.Forms;

namespace DynamicWallpaper.Core
{
    /// <summary>
    /// 电源管理：检测是否在电池供电（笔记本），用于节能暂停。
    /// </summary>
    public class PowerManager
    {
        public event Action<bool>? BatteryChanged; // true = 使用电池

        private bool _onBattery;
        public bool IsOnBattery => _onBattery;

        public PowerManager()
        {
            _onBattery = GetOnBattery();
            SystemEvents.PowerModeChanged += (_, _) =>
            {
                bool now = GetOnBattery();
                if (now != _onBattery)
                {
                    _onBattery = now;
                    BatteryChanged?.Invoke(now);
                }
            };
        }

        private static bool GetOnBattery()
        {
            try
            {
                return SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Offline;
            }
            catch { return false; }
        }
    }
}
