using System;
using System.Timers;
using Microsoft.Win32;
using DynamicWallpaper.Core;

namespace DynamicWallpaper.Core
{
    /// <summary>
    /// 虚拟桌面检测：通过注册表 <c>HKCU\...\Explorer\VirtualDesktops\CurrentVirtualDesktop</c>
    /// （REG_BINARY，16 字节 = 当前活动桌面的 GUID）轮询当前桌面，切换即变更并触发
    /// <see cref="DesktopChanged"/> 事件。
    ///
    /// 不依赖任何 COM 组件（官方 <c>IVirtualDesktopManager</c> 的 CLSID 在部分 Win10/Win11
    /// 上未注册，会直接 80040154 失败），注册表方案跨 Win10/Win11 各版本稳定可用。
    ///
    /// 程序自绘壁纸层盖在系统壁纸之上，系统自带的多桌面壁纸不会透出，因此由本模块在切换时
    /// 按「桌面 GUID × 屏幕」组合热替换各屏壁纸内容，实现应用层多桌面独立壁纸
    ///（Win10/Win11 通用，且同时支持「每显示器 × 每虚拟桌面」，这是系统原生做不到的）。
    /// </summary>
    public sealed class VirtualDesktop : IDisposable
    {
        /// <summary>尚无/未使用虚拟桌面时的稳定哨兵 GUID，使「按桌面记忆」在单桌面下也能一致地存取。</summary>
        private static readonly Guid DefaultDesktop = new Guid("FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF");

        private const string RegKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\VirtualDesktops";
        private const string RegValue = "CurrentVirtualDesktop";

        private readonly System.Timers.Timer _timer = new(300);
        private Guid _lastDesktop = DefaultDesktop;

        /// <summary>虚拟桌面切换事件，参数为新桌面的 GUID。</summary>
        public event Action<Guid>? DesktopChanged;

        /// <summary>功能是否可用。注册表方案在 Win10/Win11 上恒可用。</summary>
        public bool Available => true;

        public Guid CurrentDesktopId => _lastDesktop;

        /// <summary>当前系统是否支持按虚拟桌面记忆壁纸。注册表方案在 Win10/Win11 上恒可用。</summary>
        public static bool IsSupported() => true;

        public VirtualDesktop() { }

        /// <summary>启动轮询检测（每 300ms 读一次注册表当前桌面 GUID，变化即触发事件）。</summary>
        public void Start()
        {
            _lastDesktop = ReadCurrent() ?? DefaultDesktop;
            _timer.Elapsed += PollTick;
            _timer.AutoReset = true;
            _timer.Start();
        }

        private void PollTick(object? sender, ElapsedEventArgs e)
        {
            var cur = ReadCurrent() ?? DefaultDesktop;
            if (cur != _lastDesktop)
            {
                var prev = _lastDesktop;
                _lastDesktop = cur;
                Logger.Log($"[VirtualDesktop] 虚拟桌面切换：{prev} → {cur}");
                DesktopChanged?.Invoke(cur);
            }
        }

        /// <summary>读取注册表中的当前虚拟桌面 GUID。无虚拟桌面 / 值缺失时返回 null。</summary>
        private static Guid? ReadCurrent()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RegKey);
                if (key == null) return null;
                var val = key.GetValue(RegValue);
                if (val is byte[] b && b.Length == 16) return new Guid(b);
                if (val is string s && Guid.TryParse(s, out var g)) return g;
            }
            catch (Exception ex)
            {
                Logger.Log($"[VirtualDesktop] 读取当前虚拟桌面失败：{ex.Message}");
            }
            return null;
        }

        public void Dispose()
        {
            try { _timer.Stop(); } catch { }
            try { _timer.Dispose(); } catch { }
        }
    }
}
