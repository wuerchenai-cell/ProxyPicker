using System;
using System.Collections.Generic;
using System.Threading;
using ProxyPicker.Domain.Models;

namespace ProxyPicker.Domain.Interfaces
{
    /// <summary>统一时钟抽象（用于彻底消除对系统时钟的硬编码依赖，支持毫秒级确定性单测）。</summary>
    public interface IClock
    {
        DateTime UtcNow { get; }
        DateTime LocalNow { get; }
        long ElapsedMs { get; }
    }

    /// <summary>默认系统时钟实现。</summary>
    public sealed class SystemClock : IClock
    {
        static readonly System.Diagnostics.Stopwatch Sw = System.Diagnostics.Stopwatch.StartNew();
        public DateTime UtcNow => DateTime.UtcNow;
        public DateTime LocalNow => DateTime.Now;
        public long ElapsedMs => Sw.ElapsedMilliseconds;
    }

    /// <summary>单个已运行进程信息快照。</summary>
    public sealed class RunningProcessInfo
    {
        public string Exe { get; set; } = "";
        public string FullPath { get; set; } = "";
        public int Count { get; set; }
        public HashSet<string> AllPaths { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public List<string> DirPeers { get; set; } = new List<string>();
        public List<string> TreePeers { get; set; } = new List<string>();
    }

    /// <summary>进程探测、优雅关闭与已安装软件发现接口（平台实现收口在 Platform.Windows）。</summary>
    public interface IProcessProbe
    {
        ProcessProbeState ProbeTarget(string exeOrPath, bool requireVisibleWindowForMultiProc);
        bool IsMainInstanceRunning();
        bool IsWatcherRunning();
        Dictionary<string, RunningProcessInfo> ScanRunningProcesses();
        /// <summary>优雅关闭目标软件（优先发送主窗口关闭消息，严禁粗暴强杀破坏代理客户端 TUN 恢复）。</summary>
        bool TryCloseGracefully(string exeOrPath, int timeoutMs, out string detail);
    }

    /// <summary>外部应用启动器接口。</summary>
    public interface IAppLauncher
    {
        bool TryLaunch(string fullPath, string arguments, bool useShellExecute, out bool alreadyRunning, out string error);
    }

    /// <summary>开机自启与静默守望自启持久化接口（仅当前用户，零管理员权限）。</summary>
    public interface IAutostartStore
    {
        bool IsMainAutostartEnabled(string currentExePath);
        bool SetMainAutostart(bool enable, string currentExePath);
        bool IsWatcherAutostartEnabled(string currentExePath);
        bool SetWatcherAutostart(bool enable, string currentExePath);
        void SignalStopWatcher();
        void SignalReloadWatcher();
        void NotifyManualExitToWatcher();
        bool PostFollowHandoffToMain(string targetPathOrExe);
        string ConsumeFollowHandoff();
    }

    /// <summary>桌面快捷方式生成接口。</summary>
    public interface IShortcutCreator
    {
        bool CreateCompanionShortcut(string targetAppPath, string appDisplayName, string shortcutDir,
            string proxyPickerExePath, out string createdPath, out string error);
    }

    /// <summary>系统休眠/唤醒与网络恢复监视接口。</summary>
    public interface IPowerMonitor
    {
        event Action SystemResumed;
        event Action NetworkChanged;
    }

    /// <summary>Mihomo API 单次响应封套。</summary>
    public sealed class MihomoApiResponse
    {
        public int Status { get; set; }
        public string Body { get; set; } = "";
        public Dictionary<string, object> Data { get; set; }
        public string Error { get; set; } = "";
        public long ElapsedMs { get; set; }
        public bool Ok => Status >= 200 && Status < 300 && string.IsNullOrEmpty(Error);
    }

    /// <summary>Mihomo 离线核实报告。</summary>
    public sealed class ControlOfflineState
    {
        public bool OfflineConfirmed { get; set; }
        public int ObservedStatus { get; set; }
        public string Detail { get; set; } = "";
    }

    /// <summary>Mihomo 内核通信抽象（包含安全闸门、命名管道与 HTTP 回退）。</summary>
    public interface IMihomoClient
    {
        MihomoApiResponse FetchJson(string path, int timeoutMs, string knownConfigPath = null);
        MihomoApiResponse PutConfigPayload(string yamlPayload, int timeoutMs, string knownConfigPath = null);
        MihomoApiResponse PatchConfigJson(string jsonBody, int timeoutMs, string knownConfigPath = null);
        MihomoApiResponse SwitchProxyNode(string groupName, string nodeName, int timeoutMs, string knownConfigPath = null);
        MihomoApiResponse DeleteConnection(string connectionId, int timeoutMs, string knownConfigPath = null);
        MihomoApiResponse MeasureProxyDelay(string proxyName, string testUrl, int timeoutMs, string knownConfigPath = null);
        ControlOfflineState CheckControlOffline(string knownConfigPath = null);
        void ForgetCache();
    }

    /// <summary>零干扰旁路侧车探测抽象。</summary>
    public interface IProbeSidecar
    {
        HopBenchmarkOutcome BenchmarkStaticHops(
            StaticChannelProfile channel,
            string configPath,
            Action<string> progress,
            CancellationToken ct,
            string currentLiveHop = null);

        int SweepStaleTempDirs(TimeSpan? maxAge = null);
    }

    /// <summary>持久化配置、规则账本、多固定 IP 加密保险箱与迁移仓库抽象。</summary>
    public interface IConfigRepository
    {
        string OwnDataDir { get; }
        AppSettingsData LoadSettings();
        bool TryReloadSettings(out AppSettingsData data, out bool fileMissing);
        bool SaveSettings(AppSettingsData settings, out string error);

        bool HasSavedRoutingIntent { get; }
        List<AppRouteRule> LoadRouteRules();
        bool SaveRouteRules(IList<AppRouteRule> rules, out string error);
        bool DeleteRouteRules(out string error);

        StaticChannelVault LoadStaticVault();
        bool SaveStaticVault(StaticChannelVault vault, out string error);

        /// <summary>将配置、名单、预设与（可选加密）固定 IP 保险箱打包为便携迁移 JSON。</summary>
        string ExportEncryptedBundle(string passphrase, bool includeStaticCredentials);
        bool ImportEncryptedBundle(string bundleJson, string passphrase, out string summary, out string error);
    }

    /// <summary>结构化脱敏审计日志写入与读取抽象。</summary>
    public interface IAuditSink
    {
        void Record(string category, string message);
        List<AuditLogEntry> ReadRecent(int maxCount = 200);
        void ReportCrash(Exception ex, string context);
    }

    /// <summary>节点风控与链路纯净度评估服务接口（支持单节点体检与零干扰旁路全量巡检）。</summary>
    public interface INodeQualityService
    {
        NodeQualityReport ProbeSingleNode(string targetNode, string desiredRegion, string configPath, CancellationToken ct);
        List<NodeQualityReport> ProbeBatchNodes(IList<string> nodeNames, string desiredRegion, string configPath,
            Action<int, int, NodeQualityReport> onProgress, CancellationToken ct);
    }

    /// <summary>GitHub 官方安全只读版本检查接口。</summary>
    public interface IUpdateChecker
    {
        UpdateCheckResult CheckLatest(int timeoutMs = 6000);
    }
}
