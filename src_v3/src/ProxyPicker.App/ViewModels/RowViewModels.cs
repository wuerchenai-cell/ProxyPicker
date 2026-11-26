using System;
using System.Collections.Generic;
using ProxyPicker.Domain.Models;

namespace ProxyPicker.App.ViewModels
{
    /// <summary>应用分流列表行：内置直连 / 主代理 / 固定IP 三态直切胶囊状态与未保存脏回调。</summary>
    public sealed class AppRowViewModel : ViewModelBase
    {
        readonly Action<AppRowViewModel> _onChanged;
        RouteChannel _channel;
        string _staticChannelId = StaticChannelProfile.DefaultChannelId;
        int _activeConnections;
        bool _isMismatched;

        public string Exe { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string FullPath { get; set; } = "";
        public bool Running { get; set; }
        public int ProcessCount { get; set; }
        public bool IsProxyClient { get; set; }
        public string RadioGroupName => "AppRoute_" + Exe;

        public string StatusSummary => Running
            ? ("运行中" + (ProcessCount > 1 ? (" · " + ProcessCount + " 进程") : "") + (ActiveConnections > 0 ? (" · " + ActiveConnections + " 连接") : ""))
            : "未运行";

        public int ActiveConnections
        {
            get => _activeConnections;
            set { if (SetProperty(ref _activeConnections, value)) RaisePropertyChanged(nameof(StatusSummary)); }
        }

        public bool IsMismatched
        {
            get => _isMismatched;
            set => SetProperty(ref _isMismatched, value);
        }

        public RouteChannel Channel
        {
            get => _channel;
            set
            {
                if (SetProperty(ref _channel, value))
                {
                    RaisePropertyChanged(nameof(IsDirect));
                    RaisePropertyChanged(nameof(IsPrimaryProxy));
                    RaisePropertyChanged(nameof(IsStaticChannel));
                    _onChanged?.Invoke(this);
                }
            }
        }

        public string StaticChannelId
        {
            get => _staticChannelId;
            set
            {
                if (SetProperty(ref _staticChannelId, string.IsNullOrEmpty(value) ? StaticChannelProfile.DefaultChannelId : value))
                    _onChanged?.Invoke(this);
            }
        }

        public bool IsDirect
        {
            get => _channel == RouteChannel.Direct;
            set { if (value) Channel = RouteChannel.Direct; }
        }

        public bool IsPrimaryProxy
        {
            get => _channel == RouteChannel.PrimaryProxy;
            set { if (value) Channel = RouteChannel.PrimaryProxy; }
        }

        public bool IsStaticChannel
        {
            get => _channel == RouteChannel.StaticChannel;
            set { if (value) Channel = RouteChannel.StaticChannel; }
        }

        public AppRowViewModel(AppViewItem src, Action<AppRowViewModel> onChanged)
        {
            _onChanged = onChanged;
            Exe = src.Exe;
            DisplayName = string.IsNullOrEmpty(src.DisplayName) ? src.Exe : src.DisplayName;
            FullPath = src.FullPath;
            Running = src.Running;
            ProcessCount = src.ProcessCount;
            ActiveConnections = src.ActiveConnections;
            IsMismatched = src.IsMismatched;
            IsProxyClient = src.IsProxyClient;
            _channel = src.Channel;
            _staticChannelId = string.IsNullOrEmpty(src.StaticChannelId) ? StaticChannelProfile.DefaultChannelId : src.StaticChannelId;
        }
    }

    public sealed class NodeRowViewModel : ViewModelBase
    {
        bool _isCandidate;
        public string Name { get; set; } = "";
        public string Type { get; set; } = "";
        public int DelayMs { get; set; } = -1;
        public bool IsCurrent { get; set; }
        public string DelayLabel => DelayMs > 0 ? (DelayMs + " ms") : (DelayMs == 0 ? "超时" : "--");
        public string StateBadge => IsCurrent ? "当前出口" : (DelayMs > 0 && DelayMs < 200 ? "优秀" : (DelayMs > 0 ? "可用" : "待测"));

        public bool IsCandidate
        {
            get => _isCandidate;
            set => SetProperty(ref _isCandidate, value);
        }
    }

    public sealed class StaticChannelCardViewModel : ViewModelBase
    {
        public string Id { get; set; } = StaticChannelProfile.DefaultChannelId;
        public string Name { get; set; } = "独立固定IP";
        public bool Enabled { get; set; }
        public string EndpointMasked { get; set; } = "";
        public string HopModeLabel { get; set; } = "自动优选";
        public string LiveHopLabel { get; set; } = "待测";
        public string DelayLabel { get; set; } = "--";
        public string UdpBadge { get; set; } = "UDP 防穿透";
        public bool IsSelected { get; set; }
    }

    public sealed class ConnectionRowViewModel
    {
        public string Id { get; set; } = "";
        public string ProcessExe { get; set; } = "";
        public string TargetEndpoint { get; set; } = "";
        public string Network { get; set; } = "TCP";
        public string ActualChannel { get; set; } = "";
        public string OutboundChain { get; set; } = "";
        public string RuleMatched { get; set; } = "";
        public string TrafficText { get; set; } = "";
        public bool IsMismatched { get; set; }
        public string MismatchBadge => IsMismatched ? "错路" : "正常";
    }

    public sealed class QualityRowViewModel
    {
        public string NodeName { get; set; } = "";
        public string RiskBadge { get; set; } = "待测";
        public string RiskTone { get; set; } = "dim"; // low / med / high / dim
        public string RiskScoreText { get; set; } = "--";
        public string RegionAndKind { get; set; } = "";
        public string LatencyText { get; set; } = "--";
        public string GoogleStatus { get; set; } = "--";
        public string GeminiStatus { get; set; } = "--";
        public string OpenAiStatus { get; set; } = "--";
        public string ClaudeStatus { get; set; } = "--";
        public string VerdictSummary { get; set; } = "";
    }

    public sealed class PresetCardViewModel
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public string MetaSummary { get; set; } = "";
        public bool IsActive { get; set; }
    }

    public sealed class AuditRowViewModel
    {
        public string Timestamp { get; set; } = "";
        public string Category { get; set; } = "";
        public string Message { get; set; } = "";
    }

    public sealed class CompanionLaunchAppRow
    {
        public string FullPath { get; set; } = "";
        public string ExeName { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string KindBadge { get; set; } = "";
    }
}
