using System;
using System.Collections.Generic;
using System.Linq;

namespace ProxyPicker.Domain.Models
{
    /// <summary>全局分流模式：白名单（未勾选直连）或订阅兼容（仅强制已勾选）。</summary>
    public enum SplitMode
    {
        Whitelist = 0,
        Compat = 1
    }

    /// <summary>单个软件的出站路由通道（支持直连 / 主代理 / 独立固定 IP 三态直切）。</summary>
    public enum RouteChannel
    {
        Direct = 0,
        PrimaryProxy = 1,
        StaticChannel = 2
    }

    /// <summary>独立固定 IP 的前置跳板策略。</summary>
    public enum StaticHopMode
    {
        Auto = 0,
        Follow = 1,
        Pin = 2,
        Direct = 3
    }

    /// <summary>界面主题偏好。</summary>
    public enum ThemePreference
    {
        System = 0,
        Dark = 1,
        Light = 2
    }

    /// <summary>进程三态探测结果。</summary>
    public enum ProcessProbeState
    {
        NotRunning = 0,
        Running = 1,
        Unknown = 2
    }

    /// <summary>单条软件分流规则项（持久化账本模型）。</summary>
    public sealed class AppRouteRule
    {
        public string Exe { get; set; } = "";
        public string PathRegex { get; set; } = "";
        public RouteChannel Channel { get; set; } = RouteChannel.PrimaryProxy;
        /// <summary>当 Channel == StaticChannel 时指向目标固定 IP 通道 ID（默认 "default"）。</summary>
        public string StaticChannelId { get; set; } = "default";
        /// <summary>显式手写策略名（兼容高级用户直接指定订阅策略组名）。</summary>
        public string CustomPolicy { get; set; } = "";
        /// <summary>true 表示由主软件推导出的目录整树或 helper 派生规则，不写入用户账本。</summary>
        public bool Derived { get; set; }

        public bool IsPathRule => !string.IsNullOrEmpty(PathRegex);

        public AppRouteRule Clone()
        {
            return new AppRouteRule
            {
                Exe = Exe ?? "",
                PathRegex = PathRegex ?? "",
                Channel = Channel,
                StaticChannelId = string.IsNullOrEmpty(StaticChannelId) ? "default" : StaticChannelId,
                CustomPolicy = CustomPolicy ?? "",
                Derived = Derived
            };
        }
    }

    /// <summary>多固定 IP 通道配置项（v3.0 支持多通道池，ID="default" 完全兼容 v2.x 单通道语义）。</summary>
    public sealed class StaticChannelProfile
    {
        public const string DefaultChannelId = "default";

        public string Id { get; set; } = DefaultChannelId;
        public string Name { get; set; } = "独立固定IP";
        public bool Enabled { get; set; }
        public string Protocol { get; set; } = "socks5"; // socks5 / http
        public string Host { get; set; } = "";
        public int Port { get; set; }
        public string Username { get; set; } = "";
        public string Password { get; set; } = "";
        public bool AllowUdp { get; set; }
        public StaticHopMode HopMode { get; set; } = StaticHopMode.Auto;
        public string PinnedHopNode { get; set; } = "";
        public string AutoBestHopNode { get; set; } = "";
        public int AutoBestDelayMs { get; set; } = -1;
        public long AutoBestUpdatedAtTicks { get; set; }
        public List<string> AutoBestHopPool { get; set; } = new List<string>();

        /// <summary>内核中此通道对应的静态出口代理名（默认通道恒为 PP-STATIC-OUT 保持兼容）。</summary>
        public string KernelProxyName =>
            string.Equals(Id, DefaultChannelId, StringComparison.OrdinalIgnoreCase)
                ? "PP-STATIC-OUT"
                : ("PP-STATIC-OUT-" + SanitizeId(Id));

        /// <summary>内核中此通道对应的独立前置跳板策略组名。</summary>
        public string KernelHopGroupName =>
            string.Equals(Id, DefaultChannelId, StringComparison.OrdinalIgnoreCase)
                ? "PP-STATIC-HOP"
                : ("PP-STATIC-HOP-" + SanitizeId(Id));

        public bool IsValid(out string error)
        {
            error = "";
            string h = (Host ?? "").Trim();
            if (h.Length == 0) { error = "请填写固定 IP 服务器地址（主机名或 IP）"; return false; }
            if (Port < 1 || Port > 65535) { error = "端口号必须在 1–65535 之间"; return false; }
            if (HasControlChar(h) || HasControlChar(Username) || HasControlChar(Password) || HasControlChar(Name))
            {
                error = "参数中不能包含换行或控制字符";
                return false;
            }
            string proto = (Protocol ?? "socks5").Trim().ToLowerInvariant();
            if (proto != "socks5" && proto != "http") { error = "仅支持 socks5 或 http 协议"; return false; }
            if (HopMode == StaticHopMode.Pin && string.IsNullOrWhiteSpace(PinnedHopNode))
            {
                error = "指定前置跳板模式下必须选择一个跳板节点";
                return false;
            }
            return true;
        }

        public bool IsActive()
        {
            string _;
            return Enabled && IsValid(out _);
        }

        public StaticChannelProfile Clone()
        {
            return new StaticChannelProfile
            {
                Id = string.IsNullOrWhiteSpace(Id) ? DefaultChannelId : Id.Trim(),
                Name = Name ?? "独立固定IP",
                Enabled = Enabled,
                Protocol = string.IsNullOrWhiteSpace(Protocol) ? "socks5" : Protocol.Trim().ToLowerInvariant(),
                Host = Host ?? "",
                Port = Port,
                Username = Username ?? "",
                Password = Password ?? "",
                AllowUdp = AllowUdp,
                HopMode = HopMode,
                PinnedHopNode = PinnedHopNode ?? "",
                AutoBestHopNode = AutoBestHopNode ?? "",
                AutoBestDelayMs = AutoBestDelayMs,
                AutoBestUpdatedAtTicks = AutoBestUpdatedAtTicks,
                AutoBestHopPool = AutoBestHopPool != null ? new List<string>(AutoBestHopPool) : new List<string>()
            };
        }

        static bool HasControlChar(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            for (int i = 0; i < s.Length; i++)
                if (s[i] < 0x20 || s[i] == 0x7F) return true;
            return false;
        }

        public static string SanitizeId(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return DefaultChannelId;
            var chars = id.Trim().Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_').ToArray();
            return chars.Length == 0 ? DefaultChannelId : new string(chars).ToUpperInvariant();
        }
    }

    /// <summary>多固定 IP 通道集合（保险箱顶层结构）。</summary>
    public sealed class StaticChannelVault
    {
        public string ActiveDefaultChannelId { get; set; } = StaticChannelProfile.DefaultChannelId;
        public List<StaticChannelProfile> Channels { get; set; } = new List<StaticChannelProfile>();

        public StaticChannelProfile GetDefaultOrPrimary()
        {
            if (Channels == null || Channels.Count == 0)
                return new StaticChannelProfile();
            var match = Channels.FirstOrDefault(c => string.Equals(c.Id, ActiveDefaultChannelId, StringComparison.OrdinalIgnoreCase));
            return match ?? Channels[0];
        }

        public StaticChannelProfile FindById(string id)
        {
            if (Channels == null) return null;
            string key = string.IsNullOrWhiteSpace(id) ? ActiveDefaultChannelId : id;
            return Channels.FirstOrDefault(c => string.Equals(c.Id, key, StringComparison.OrdinalIgnoreCase));
        }

        public StaticChannelProfile FindByKernelProxyName(string proxyName)
        {
            if (Channels == null || string.IsNullOrWhiteSpace(proxyName)) return null;
            return Channels.FirstOrDefault(c => string.Equals(c.KernelProxyName, proxyName.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        public List<StaticChannelProfile> GetActiveChannels()
        {
            return Channels != null ? Channels.Where(c => c != null && c.IsActive()).ToList() : new List<StaticChannelProfile>();
        }

        public StaticChannelVault Clone()
        {
            return new StaticChannelVault
            {
                ActiveDefaultChannelId = string.IsNullOrWhiteSpace(ActiveDefaultChannelId) ? StaticChannelProfile.DefaultChannelId : ActiveDefaultChannelId,
                Channels = Channels != null ? Channels.Select(c => c.Clone()).ToList() : new List<StaticChannelProfile>()
            };
        }
    }

    /// <summary>v3.0 真正的四向对称软件联动配置模型。</summary>
    public sealed class CompanionConfig
    {
        // 方向 1 & 3：跟随指定电脑软件（开它 → 自动开 ProxyPicker；关它 → 自动还原并退出 ProxyPicker）
        public bool FollowEnabled { get; set; }
        public string FollowAppExe { get; set; } = "";
        public string FollowAppPath { get; set; } = "";
        public string FollowAppName { get; set; } = "";
        public bool FollowAutoExit { get; set; } = true;
        /// <summary>v3.0 修复反直觉默认项：勾选 FollowEnabled 时自动默认开启静默守望。</summary>
        public bool FollowWatchAtLogon { get; set; } = true;

        // 方向 2：打开 ProxyPicker 时自动按序拉起软件（代理客户端优先 + 规则强门禁就绪后拉业务软件）
        public bool LaunchAppsEnabled { get; set; }
        public bool LaunchAppsOnFollow { get; set; } = true;
        public List<string> LaunchAppPaths { get; set; } = new List<string>();

        // 方向 4（v3.0 新增补全）：主动退出 ProxyPicker 时，先还原内核配置再优雅关闭联动软件
        public bool CloseLinkedAppsOnExit { get; set; }
        /// <summary>优雅关闭等待上限（毫秒），默认 5000ms，超时只提示不粗暴强杀丢数据。</summary>
        public int GracefulCloseTimeoutMs { get; set; } = 5000;

        public bool HasValidFollowTarget => FollowEnabled && !string.IsNullOrWhiteSpace(FollowAppExe);
        public bool HasAnyLaunchApps => LaunchAppsEnabled && LaunchAppPaths != null && LaunchAppPaths.Count > 0;
        public bool IsAnyActive => HasValidFollowTarget || HasAnyLaunchApps || CloseLinkedAppsOnExit;

        public CompanionConfig Clone()
        {
            return new CompanionConfig
            {
                FollowEnabled = FollowEnabled,
                FollowAppExe = FollowAppExe ?? "",
                FollowAppPath = FollowAppPath ?? "",
                FollowAppName = FollowAppName ?? "",
                FollowAutoExit = FollowAutoExit,
                FollowWatchAtLogon = FollowWatchAtLogon,
                LaunchAppsEnabled = LaunchAppsEnabled,
                LaunchAppsOnFollow = LaunchAppsOnFollow,
                LaunchAppPaths = LaunchAppPaths != null ? new List<string>(LaunchAppPaths) : new List<string>(),
                CloseLinkedAppsOnExit = CloseLinkedAppsOnExit,
                GracefulCloseTimeoutMs = GracefulCloseTimeoutMs < 1000 ? 5000 : GracefulCloseTimeoutMs
            };
        }
    }

    /// <summary>v3.0 命名场景预设（一键切换分流模式 + 软件路由名单 + 固定 IP 默认通道）。</summary>
    public sealed class WorkspacePreset
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public SplitMode Mode { get; set; } = SplitMode.Whitelist;
        public string DefaultStaticChannelId { get; set; } = StaticChannelProfile.DefaultChannelId;
        public List<AppRouteRule> Rules { get; set; } = new List<AppRouteRule>();
        public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

        public WorkspacePreset Clone()
        {
            return new WorkspacePreset
            {
                Id = Id ?? "",
                Name = Name ?? "",
                Description = Description ?? "",
                Mode = Mode,
                DefaultStaticChannelId = DefaultStaticChannelId ?? StaticChannelProfile.DefaultChannelId,
                Rules = Rules != null ? Rules.Select(r => r.Clone()).ToList() : new List<AppRouteRule>(),
                UpdatedAtUtc = UpdatedAtUtc
            };
        }
    }

    /// <summary>v3.0 应用全局持久化设置（向下 100% 兼容 v2.x settings.json 字段）。</summary>
    public sealed class AppSettingsData
    {
        public SplitMode Mode { get; set; } = SplitMode.Whitelist;
        public ThemePreference Theme { get; set; } = ThemePreference.System;
        public bool Ipv6GuardOn { get; set; }
        public string Ipv6Original { get; set; } = "";
        public string FindProcessOriginal { get; set; } = "";
        public bool RepairSuspended { get; set; }
        public bool LoginWindowProxy { get; set; } = true;
        public bool AutoCheckUpdates { get; set; } = true;
        public bool AutoFailoverEnabled { get; set; }
        public List<string> CandidateNodes { get; set; } = new List<string>();
        public List<string> CustomApps { get; set; } = new List<string>();
        public List<string> HiddenApps { get; set; } = new List<string>();
        public string ActivePresetId { get; set; } = "";
        public List<WorkspacePreset> Presets { get; set; } = new List<WorkspacePreset>();
        public CompanionConfig Companion { get; set; } = new CompanionConfig();
        public int WindowWidth { get; set; } = 1040;
        public int WindowHeight { get; set; } = 680;
        public bool CompactMode { get; set; }
        public bool TrayHintShown { get; set; }

        public AppSettingsData Clone()
        {
            return new AppSettingsData
            {
                Mode = Mode,
                Theme = Theme,
                Ipv6GuardOn = Ipv6GuardOn,
                Ipv6Original = Ipv6Original ?? "",
                FindProcessOriginal = FindProcessOriginal ?? "",
                RepairSuspended = RepairSuspended,
                LoginWindowProxy = LoginWindowProxy,
                AutoCheckUpdates = AutoCheckUpdates,
                AutoFailoverEnabled = AutoFailoverEnabled,
                CandidateNodes = CandidateNodes != null ? new List<string>(CandidateNodes) : new List<string>(),
                CustomApps = CustomApps != null ? new List<string>(CustomApps) : new List<string>(),
                HiddenApps = HiddenApps != null ? new List<string>(HiddenApps) : new List<string>(),
                ActivePresetId = ActivePresetId ?? "",
                Presets = Presets != null ? Presets.Select(p => p.Clone()).ToList() : new List<WorkspacePreset>(),
                Companion = Companion != null ? Companion.Clone() : new CompanionConfig(),
                WindowWidth = WindowWidth,
                WindowHeight = WindowHeight,
                CompactMode = CompactMode,
                TrayHintShown = TrayHintShown
            };
        }
    }

    /// <summary>单条实时连接记录（用于 v3.0 实时连接监视器与三路流量聚合）。</summary>
    public sealed class ConnectionRecord
    {
        public string Id { get; set; } = "";
        public string Process { get; set; } = "";
        public string ProcessPath { get; set; } = "";
        public string Network { get; set; } = "";
        public string TargetHostMasked { get; set; } = "";
        public int TargetPort { get; set; }
        public string Rule { get; set; } = "";
        public string RulePayload { get; set; } = "";
        public string ChainDisplay { get; set; } = "";
        public RouteChannel ActualChannel { get; set; } = RouteChannel.Direct;
        public RouteChannel ExpectedChannel { get; set; } = RouteChannel.Direct;
        public string StaticChannelId { get; set; } = "";
        public bool IsRejected { get; set; }
        public bool IsMismatched { get; set; }
        public bool IsLegacyConnection { get; set; }
        public long UploadTotalBytes { get; set; }
        public long DownloadTotalBytes { get; set; }
        public long UploadDeltaBytes { get; set; }
        public long DownloadDeltaBytes { get; set; }
        public DateTime StartTimeUtc { get; set; }
    }

    /// <summary>实时流量与错路聚合快照（不可变输出）。</summary>
    public sealed class TrafficSnapshot
    {
        public DateTime SampledAtUtc { get; set; } = DateTime.UtcNow;
        public double ProxyDownBps { get; set; }
        public double ProxyUpBps { get; set; }
        public double StaticDownBps { get; set; }
        public double StaticUpBps { get; set; }
        public double DirectDownBps { get; set; }
        public double DirectUpBps { get; set; }
        public int ProxyConnCount { get; set; }
        public int StaticConnCount { get; set; }
        public int DirectConnCount { get; set; }
        public int RejectedConnCount { get; set; }
        public int MismatchCount { get; set; }
        public List<string> MismatchedApps { get; set; } = new List<string>();
        public Dictionary<string, int> ActiveConnByProcess { get; set; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        public List<ConnectionRecord> Connections { get; set; } = new List<ConnectionRecord>();
    }

    /// <summary>审计日志结构化条目。</summary>
    public sealed class AuditLogEntry
    {
        public DateTime TimestampLocal { get; set; } = DateTime.Now;
        public string Category { get; set; } = "INFO";
        public string Message { get; set; } = "";
    }

    /// <summary>候选跳板端到端测速项。</summary>
    public sealed class HopCandidateMetric
    {
        public string NodeName { get; set; } = "";
        public int DelayMs { get; set; } = -1;
        public int JitterMs { get; set; }
        public int SuccessSamples { get; set; }
        public int StabilityScore { get; set; } = int.MaxValue;
    }

    /// <summary>跳板优选整批结果。</summary>
    public sealed class HopBenchmarkOutcome
    {
        public bool Ok { get; set; }
        public string Error { get; set; } = "";
        public string BestNode { get; set; } = "";
        public int BestDelayMs { get; set; } = -1;
        public int BestJitterMs { get; set; }
        public int TestedCount { get; set; }
        public int ReachableCount { get; set; }
        public List<HopCandidateMetric> TopCandidates { get; set; } = new List<HopCandidateMetric>();
    }

    /// <summary>代理客户端发现结果。</summary>
    public sealed class ClientEnvironment
    {
        public string ClientName { get; set; } = "";
        public string DataDir { get; set; } = "";
        public string ConfigPath { get; set; } = "";
        public string Controller { get; set; } = "";
        public string Secret { get; set; } = "";
        public List<string> OtherClientsDetected { get; set; } = new List<string>();
        public bool Found => !string.IsNullOrEmpty(ConfigPath);
    }

    /// <summary>内核实时状态快照（供 AppStateStore 发布至 UI）。</summary>
    public sealed class KernelSnapshot
    {
        public bool Connected { get; set; }
        public string ClientName { get; set; } = "";
        public string ConfigPath { get; set; } = "";
        public string KernelVersion { get; set; } = "";
        public string Mode { get; set; } = "";
        public bool TunEnabled { get; set; }
        public bool Ipv6Enabled { get; set; }
        public bool DnsEnabled { get; set; }
        public string FindProcessMode { get; set; } = "";
        public string PrimaryPolicyGroup { get; set; } = "";
        public string PrimaryOutboundNode { get; set; } = "";
        public string PrimaryChain { get; set; } = "";
        public int PrimaryDelayMs { get; set; } = -1;
        public bool PrimaryTimedOut { get; set; }
        public bool RulesApplied { get; set; }
        public int TotalRuleCount { get; set; }
        public bool SystemProxyEnabled { get; set; }
        public string SystemProxyServer { get; set; } = "";
        public string Error { get; set; } = "";
        public long AppliedConfigRevision { get; set; }
    }

    /// <summary>单个固定 IP 通道的实时运行状态。</summary>
    public sealed class StaticChannelRuntime
    {
        public string ChannelId { get; set; } = StaticChannelProfile.DefaultChannelId;
        public string ChannelName { get; set; } = "独立固定IP";
        public bool Enabled { get; set; }
        public StaticHopMode HopMode { get; set; } = StaticHopMode.Auto;
        public string LiveHopNode { get; set; } = "";
        public int DelayMs { get; set; } = -1;
        public bool TimedOut { get; set; }
        public bool Checking { get; set; }
        public bool Optimizing { get; set; }
        public bool UdpBlocked { get; set; }
        public int SlowStreak { get; set; }
        public int TimeoutStreak { get; set; }
        public DateTime LastCheckedUtc { get; set; } = DateTime.MinValue;
        public DateTime LastOptimizedUtc { get; set; } = DateTime.MinValue;
    }

    /// <summary>软件列表展示行（纯数据快照）。</summary>
    public sealed class AppViewItem
    {
        public string Exe { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string FullPath { get; set; } = "";
        public string InstallRoot { get; set; } = "";
        public bool Running { get; set; }
        public int ProcessCount { get; set; }
        public int ActiveConnections { get; set; }
        public RouteChannel Channel { get; set; } = RouteChannel.Direct;
        public string StaticChannelId { get; set; } = StaticChannelProfile.DefaultChannelId;
        public bool IsMismatched { get; set; }
        public bool IsCustomAdded { get; set; }
        public bool IsProxyClient { get; set; }
        public List<string> PeerExes { get; set; } = new List<string>();
    }

    /// <summary>代理节点展示项。</summary>
    public sealed class ProxyNodeItem
    {
        public string Name { get; set; } = "";
        public string Type { get; set; } = "";
        public int DelayMs { get; set; } = -1;
        public bool Alive { get; set; }
        public bool IsCurrent { get; set; }
        public bool IsCandidate { get; set; }
        public string LastError { get; set; } = "";
    }

    /// <summary>DNS 防泄漏与安全预检诊断建议项。</summary>
    public sealed class DnsDiagnosticAdvice
    {
        public bool KernelDnsEnabled { get; set; }
        public bool TunEnabled { get; set; }
        public bool StaticUdpGuardActive { get; set; }
        public string Severity { get; set; } = "ok"; // ok / warn / danger
        public string Summary { get; set; } = "";
        public List<string> Recommendations { get; set; } = new List<string>();
    }

    /// <summary>节点风险评级。</summary>
    public enum NodeRiskLevel
    {
        Unknown = 0,
        Low = 1,
        Medium = 2,
        High = 3
    }

    /// <summary>单个主流服务（Google / Gemini / OpenAI / Claude / Cloudflare）可用性门检结果。</summary>
    public sealed class ServiceGateResult
    {
        public string Name { get; set; } = "";
        public bool Tested { get; set; }
        public bool Ok { get; set; }
        public bool Blocked { get; set; }
        public int StatusCode { get; set; }
        public int LatencyMs { get; set; } = -1;
        public string Detail { get; set; } = "";
    }

    /// <summary>连续采样延迟/抖动/丢包统计。</summary>
    public sealed class NodeLatencyStats
    {
        public int Sent { get; set; }
        public int Received { get; set; }
        public int AvgMs { get; set; } = -1;
        public int MinMs { get; set; } = -1;
        public int MaxMs { get; set; } = -1;
        public int JitterMs { get; set; } = -1;
        public int LossPercent { get; set; } = 100;
    }

    /// <summary>单源 IP 归属与风控情报。</summary>
    public sealed class IpIntelligence
    {
        public bool Ok { get; set; }
        public string Source { get; set; } = "";
        public string Ip { get; set; } = "";
        public string CountryCode { get; set; } = "";
        public string Country { get; set; } = "";
        public string Region { get; set; } = "";
        public string City { get; set; } = "";
        public string Asn { get; set; } = "";
        public string Isp { get; set; } = "";
        public string Org { get; set; } = "";
        public string IpType { get; set; } = "";
        public bool IsDatacenter { get; set; }
        public bool IsVpn { get; set; }
        public bool IsProxy { get; set; }
        public bool IsTor { get; set; }
        public bool IsMobile { get; set; }
        public int RawRiskScore { get; set; } = -1;
        public string Error { get; set; } = "";
    }

    /// <summary>单节点质量与风控综合评估报告。</summary>
    public sealed class NodeQualityReport
    {
        public DateTime CheckedAtUtc { get; set; } = DateTime.UtcNow;
        public string TargetNode { get; set; } = "";
        public string KernelMode { get; set; } = "rule";
        public bool TunEnabled { get; set; }
        public bool KernelIpv6On { get; set; }
        public bool DnsEnabled { get; set; }
        public int MixedPort { get; set; }
        public bool IsSidecarIsolated { get; set; }

        public NodeLatencyStats Net { get; set; } = new NodeLatencyStats();
        public bool RoutedDirect { get; set; }
        public bool Ipv6LeakChecked { get; set; }
        public bool DirectIpv6Leak { get; set; }
        public string LeakedIpv6 { get; set; } = "";

        public string Ip { get; set; } = "";
        public string CountryCode { get; set; } = "";
        public string CountryName { get; set; } = "";
        public string City { get; set; } = "";
        public string Asn { get; set; } = "";
        public string Isp { get; set; } = "";
        public string ExpectedRegion { get; set; } = "";
        public string DesiredRegion { get; set; } = "auto";
        public bool RegionMatched { get; set; } = true;
        public string RegionNote { get; set; } = "";

        public NodeRiskLevel Level { get; set; } = NodeRiskLevel.Unknown;
        public int RiskScore { get; set; } = -1;
        public string IpKindLabel { get; set; } = "未知";
        public bool GoogleAiUsable { get; set; }
        public int MainstreamReadyCount { get; set; }
        public string VerdictTitle { get; set; } = "";
        public string VerdictDetail { get; set; } = "";

        public ServiceGateResult GoogleSearch { get; set; } = new ServiceGateResult { Name = "Google" };
        public ServiceGateResult GeminiApi { get; set; } = new ServiceGateResult { Name = "Google AI (Gemini)" };
        public ServiceGateResult OpenAiApi { get; set; } = new ServiceGateResult { Name = "OpenAI" };
        public ServiceGateResult ClaudeApi { get; set; } = new ServiceGateResult { Name = "Claude" };
        public ServiceGateResult Cloudflare { get; set; } = new ServiceGateResult { Name = "Cloudflare" };
        public string CfColo { get; set; } = "";
        public string Error { get; set; } = "";
    }

    /// <summary>GitHub Release 版本检查结果。</summary>
    public sealed class UpdateCheckResult
    {
        public bool Ok { get; set; }
        public bool HasNewer { get; set; }
        public string CurrentVersion { get; set; } = "";
        public string LatestVersion { get; set; } = "";
        public string ReleaseUrl { get; set; } = "";
        public string PublishedDate { get; set; } = "";
        public string Error { get; set; } = "";
    }
}
