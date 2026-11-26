using System;
using System.Net.NetworkInformation;
using Microsoft.Win32;
using ProxyPicker.Domain.Interfaces;

namespace ProxyPicker.Platform.Windows
{
    /// <summary>Windows 电源休眠唤醒与网络状态变更监视器（驱动 PowerResumeGuard 防止休眠唤醒风暴）。</summary>
    public sealed class WindowsPowerMonitor : IPowerMonitor, IDisposable
    {
        public event Action SystemResumed;
        public event Action NetworkChanged;

        bool _disposed;

        public WindowsPowerMonitor()
        {
            try { SystemEvents.PowerModeChanged += OnPowerModeChanged; } catch { }
            try { NetworkChange.NetworkAvailabilityChanged += OnNetworkChanged; } catch { }
        }

        void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode == PowerModes.Resume)
            {
                try { SystemResumed?.Invoke(); } catch { }
            }
        }

        void OnNetworkChanged(object sender, NetworkAvailabilityEventArgs e)
        {
            try { NetworkChanged?.Invoke(); } catch { }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { SystemEvents.PowerModeChanged -= OnPowerModeChanged; } catch { }
            try { NetworkChange.NetworkAvailabilityChanged -= OnNetworkChanged; } catch { }
        }
    }
}
