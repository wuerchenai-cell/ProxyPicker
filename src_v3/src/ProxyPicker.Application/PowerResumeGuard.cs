using System;
using ProxyPicker.Domain.Interfaces;

namespace ProxyPicker.Application
{
    /// <summary>
    /// 休眠唤醒与网络抖动防风暴护盾（彻底消除模拟场景 2：合盖唤醒瞬间 4 类后台定时任务同时误触发红警和乱切节点）。
    /// </summary>
    public sealed class PowerResumeGuard : IDisposable
    {
        readonly IPowerMonitor _monitor;
        readonly IClock _clock;
        readonly TimeSpan _gracePeriod;
        readonly object _gate = new object();

        DateTime _quietUntilUtc = DateTime.MinValue;
        DateTime _lastWallTickUtc;
        bool _disposed;

        public PowerResumeGuard(IPowerMonitor monitor, IClock clock, int graceSeconds = 12)
        {
            _monitor = monitor;
            _clock = clock;
            _gracePeriod = TimeSpan.FromSeconds(Math.Max(3, graceSeconds));
            _lastWallTickUtc = _clock.UtcNow;

            if (_monitor != null)
            {
                _monitor.SystemResumed += TriggerQuietWindow;
                _monitor.NetworkChanged += TriggerQuietWindow;
            }
        }

        public void TriggerQuietWindow()
        {
            lock (_gate)
            {
                var until = _clock.UtcNow + _gracePeriod;
                if (until > _quietUntilUtc) _quietUntilUtc = until;
                _lastWallTickUtc = _clock.UtcNow;
            }
        }

        /// <summary>
        /// 检查当前是否处于休眠唤醒/网络恢复静默宽限窗；
        /// 同时内置墙钟跳变自检：若距离上次心跳瞬间跳跃 >= 60 秒，即使系统未投递 PowerMode 事件也自动进入宽限窗！
        /// </summary>
        public bool IsInGraceWindow(out double remainingSeconds)
        {
            lock (_gate)
            {
                var now = _clock.UtcNow;
                if (_lastWallTickUtc != DateTime.MinValue && (now - _lastWallTickUtc).TotalSeconds >= 60)
                {
                    var until = now + _gracePeriod;
                    if (until > _quietUntilUtc) _quietUntilUtc = until;
                }
                _lastWallTickUtc = now;

                if (now < _quietUntilUtc)
                {
                    remainingSeconds = (_quietUntilUtc - now).TotalSeconds;
                    return true;
                }
                remainingSeconds = 0;
                return false;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_monitor != null)
            {
                _monitor.SystemResumed -= TriggerQuietWindow;
                _monitor.NetworkChanged -= TriggerQuietWindow;
            }
        }
    }
}
