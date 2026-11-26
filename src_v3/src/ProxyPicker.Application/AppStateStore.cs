using System;
using System.Collections.Generic;
using System.Linq;
using ProxyPicker.Domain.Models;

namespace ProxyPicker.Application
{
    /// <summary>全局不可变应用状态快照（单向数据流核心：后台引擎更新此处，WPF ViewModel 只读订阅）。</summary>
    public sealed class AppStateSnapshot
    {
        public KernelSnapshot Kernel { get; set; } = new KernelSnapshot();
        public TrafficSnapshot Traffic { get; set; } = new TrafficSnapshot();
        public AppSettingsData Settings { get; set; } = new AppSettingsData();
        public StaticChannelVault StaticVault { get; set; } = new StaticChannelVault();
        public List<StaticChannelRuntime> StaticRuntimes { get; set; } = new List<StaticChannelRuntime>();
        public List<AppRouteRule> SavedRules { get; set; } = new List<AppRouteRule>();
        public List<AppViewItem> Apps { get; set; } = new List<AppViewItem>();
        public List<ProxyNodeItem> Nodes { get; set; } = new List<ProxyNodeItem>();
        public bool WatcherAlive { get; set; }
        public string ActiveFollowTargetExe { get; set; } = "";
        public string StatusMessage { get; set; } = "就绪";
        public string StatusTone { get; set; } = "dim"; // ok / accent / warn / danger / dim
        public DateTime RulesAppliedAtUtc { get; set; } = DateTime.MinValue;
    }

    /// <summary>线程安全应用状态仓库，对外提供原子读取与细粒度变更通知。</summary>
    public sealed class AppStateStore
    {
        readonly object _gate = new object();
        AppStateSnapshot _current = new AppStateSnapshot();

        public event Action<AppStateSnapshot> StateChanged;
        public event Action<TrafficSnapshot> TrafficUpdated;
        public event Action<string, string, string> ToastRequested; // title, body, tone

        public AppStateSnapshot GetSnapshot()
        {
            lock (_gate) return _current;
        }

        public void Update(Action<AppStateSnapshot> mutator)
        {
            AppStateSnapshot next;
            lock (_gate)
            {
                next = CloneShallow(_current);
                mutator(next);
                _current = next;
            }
            try { StateChanged?.Invoke(next); } catch { }
        }

        public void PublishTraffic(TrafficSnapshot traffic)
        {
            if (traffic == null) return;
            lock (_gate)
            {
                var next = CloneShallow(_current);
                next.Traffic = traffic;
                _current = next;
            }
            try { TrafficUpdated?.Invoke(traffic); } catch { }
        }

        public void EmitToast(string title, string body, string tone = "info")
        {
            try { ToastRequested?.Invoke(title ?? "", body ?? "", tone ?? "info"); } catch { }
        }

        static AppStateSnapshot CloneShallow(AppStateSnapshot s)
        {
            return new AppStateSnapshot
            {
                Kernel = s.Kernel,
                Traffic = s.Traffic,
                Settings = s.Settings,
                StaticVault = s.StaticVault,
                StaticRuntimes = s.StaticRuntimes != null ? new List<StaticChannelRuntime>(s.StaticRuntimes) : new List<StaticChannelRuntime>(),
                SavedRules = s.SavedRules != null ? new List<AppRouteRule>(s.SavedRules) : new List<AppRouteRule>(),
                Apps = s.Apps != null ? new List<AppViewItem>(s.Apps) : new List<AppViewItem>(),
                Nodes = s.Nodes != null ? new List<ProxyNodeItem>(s.Nodes) : new List<ProxyNodeItem>(),
                WatcherAlive = s.WatcherAlive,
                ActiveFollowTargetExe = s.ActiveFollowTargetExe,
                StatusMessage = s.StatusMessage,
                StatusTone = s.StatusTone,
                RulesAppliedAtUtc = s.RulesAppliedAtUtc
            };
        }
    }
}
