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
                // 无电池的台式机（含部分 UPS 环境）PowerLineStatus 可能返回 Offline，
                // 若直接据此判定"电池供电"会导致壁纸被永久自动暂停（且无从恢复感知）。
                // 必须先确认系统真的存在电池。
                var ps = SystemInformation.PowerStatus;
                if (ps.BatteryChargeStatus == BatteryChargeStatus.NoSystemBattery)
                    return false;
                return ps.PowerLineStatus == PowerLineStatus.Offline;
            }
            catch { return false; }
        }
    }
}
