using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using ProxyPicker.App.Controls;
using ProxyPicker.App.Theme;
using ProxyPicker.Application;
using ProxyPicker.Domain;
using ProxyPicker.Domain.Interfaces;
using ProxyPicker.Domain.Models;
using ProxyPicker.Domain.Services;

namespace ProxyPicker.App.ViewModels
{
    /// <summary>
    /// ProxyPicker v3.0 顶层企业级工作台 ViewModel：
    /// 严格单向订阅 AppStateStore，零底层网络/YAML/Win32 杂糅，统一管理 6 大一级工作区状态与命令。
    /// </summary>
    public sealed class ShellViewModel : ViewModelBase
    {
        readonly ProxyPickerEngine _engine;
        readonly IShortcutCreator _shortcutCreator;
        readonly string _selfExePath;
        readonly Action<ThemePreference> _applyThemeAction;

        // 用于主窗口同步曲线画布
        public event Action<double, double, double> TrafficSamplePushed;
        public event Action CompactModeRequested;
        public event Action<string, string, string> ToastNotification;

        int _selectedNavIndex;
        bool _hasUnsavedRoutingChanges;
        string _statusMessage = "正在初始化 ProxyPicker v3.0…";
        string _statusTone = "accent";
        string _clientName = "探测中…";
        string _kernelSummary = "等待连接代理内核";
        bool _kernelConnected;
        bool _rulesApplied;

        // 顶部双通道独立仪表盘字段
        string _primaryNodeName = "--";
        string _primaryDelayText = "--";
        string _primaryDelayTone = "dim";
        string _staticChannelSummary = "未启用独立固定 IP";
        string _staticDelayText = "--";
        string _staticDelayTone = "dim";
        string _staticHopSummary = "点击左侧「节点与固定IP」可配置多条专属固定出口";

        // 实时三路流量摘要
        string _proxySpeedText = "0 B/s";
        string _staticSpeedText = "0 B/s";
        string _directSpeedText = "0 B/s";
        int _activeConnTotal;
        int _mismatchedConnCount;

        // 页面 1：应用分流筛选与草稿
        string _appSearchText = "";
        int _appFilterTab; // 0=全部 1=运行中 2=主代理 3=固定IP
        int _splitModeIndex; // 0=白名单 1=订阅兼容
        List<AppRowViewModel> _allAppRows = new List<AppRowViewModel>();

        // 页面 2：多固定 IP 编辑器字段
        string _editingChannelId = StaticChannelProfile.DefaultChannelId;
        string _editingChannelName = "独立固定IP";
        bool _editingChannelEnabled;
        int _editingProtocolIndex; // 0=socks5, 1=http
        string _editingHost = "";
        string _editingPort = "";
        string _editingUsername = "";
        string _editingPassword = "";
        bool _editingAllowUdp;
        int _editingHopModeIndex; // 0=Auto, 1=Follow, 2=Pin, 3=Direct
        string _editingPinnedNode = "";
        string _quickPasteInput = "";
        string _staticOptimizeStatus = "";
        CancellationTokenSource _staticOptCts;

        // 页面 3：实时连接监视器筛选
        string _connSearchText = "";
        int _connFilterTab; // 0=全部 1=主代理 2=固定IP 3=直连 4=仅错路
        List<ConnectionRecord> _rawConnections = new List<ConnectionRecord>();

        // 页面 4：链路与 DNS 体检
        string _dnsSeverity = "ok";
        string _dnsSummary = "等待检测内核 DNS 与 TUN 状态";
        bool _dnsTunEnabled;
        bool _dnsKernelEnabled;
        bool _dnsUdpGuardActive;
        string _desiredRegion = "auto";
        bool _qualityRunning;
        string _qualityProgressText = "未开始检测（支持零干扰隔离侧车单节点或全量节点风控体检）";
        string _latestQualityHeadline = "尚未执行节点风控体检";
        string _latestQualityDetail = "点击右侧按钮即可零干扰实测真实出口 IP、机房/家宽属性、地区漂移及 Google / Gemini / OpenAI / Claude 四门放行状态。";
        CancellationTokenSource _qualityCts;

        // 页面 5：四向联动与场景预设
        bool _compFollowEnabled;
        string _compFollowAppExe = "";
        string _compFollowAppPath = "";
        string _compFollowAppName = "";
        bool _compFollowAutoExit = true;
        bool _compFollowWatchAtLogon = true;
        bool _compLaunchEnabled;
        bool _compLaunchOnFollow = true;
        bool _compCloseLinkedOnExit;
        bool _watcherAlive;
        string _newPresetName = "";
        string _newPresetDesc = "";

        // 页面 6：系统设置与审计日志
        int _themeIndex; // 0=System 1=Dark 2=Light
        bool _ipv6GuardOn;
        bool _mainAutostartOn;
        bool _autoFailoverOn;
        string _updateStatusText = "当前版本 v" + ProductInfo.Version + "（只读安全检查，不自动下载）";
        string _latestReleaseUrl = "";
        string _auditSearchText = "";
        string _auditCategoryFilter = "全部";
        List<AuditLogEntry> _allAuditEntries = new List<AuditLogEntry>();

        // Toast 轻量内嵌条
        bool _toastVisible;
        string _toastTitle = "";
        string _toastBody = "";
        string _toastTone = "info";

        public ObservableCollection<AppRowViewModel> FilteredApps { get; } = new ObservableCollection<AppRowViewModel>();
        public ObservableCollection<NodeRowViewModel> Nodes { get; } = new ObservableCollection<NodeRowViewModel>();
        public ObservableCollection<StaticChannelCardViewModel> StaticChannels { get; } = new ObservableCollection<StaticChannelCardViewModel>();
        public ObservableCollection<ConnectionRowViewModel> FilteredConnections { get; } = new ObservableCollection<ConnectionRowViewModel>();
        public ObservableCollection<string> DnsRecommendations { get; } = new ObservableCollection<string>();
        public ObservableCollection<QualityRowViewModel> QualityReports { get; } = new ObservableCollection<QualityRowViewModel>();
        public ObservableCollection<CompanionLaunchAppRow> CompanionLaunchApps { get; } = new ObservableCollection<CompanionLaunchAppRow>();
        public ObservableCollection<PresetCardViewModel> Presets { get; } = new ObservableCollection<PresetCardViewModel>();
        public ObservableCollection<AuditRowViewModel> FilteredAudits { get; } = new ObservableCollection<AuditRowViewModel>();

        public string ProductTitle => ProductInfo.Name + " v" + ProductInfo.Version;
        public int SelectedNavIndex { get => _selectedNavIndex; set => SetProperty(ref _selectedNavIndex, value); }
        public bool HasUnsavedRoutingChanges { get => _hasUnsavedRoutingChanges; set => SetProperty(ref _hasUnsavedRoutingChanges, value); }
        public string StatusMessage { get => _statusMessage; set => SetProperty(ref _statusMessage, value); }
        public string StatusTone { get => _statusTone; set => SetProperty(ref _statusTone, value); }
        public string ClientName { get => _clientName; set => SetProperty(ref _clientName, value); }
        public string KernelSummary { get => _kernelSummary; set => SetProperty(ref _kernelSummary, value); }
        public bool KernelConnected { get => _kernelConnected; set => SetProperty(ref _kernelConnected, value); }
        public bool RulesApplied { get => _rulesApplied; set => SetProperty(ref _rulesApplied, value); }

        public string PrimaryNodeName { get => _primaryNodeName; set => SetProperty(ref _primaryNodeName, value); }
        public string PrimaryDelayText { get => _primaryDelayText; set => SetProperty(ref _primaryDelayText, value); }
        public string PrimaryDelayTone { get => _primaryDelayTone; set => SetProperty(ref _primaryDelayTone, value); }
        public string StaticChannelSummary { get => _staticChannelSummary; set => SetProperty(ref _staticChannelSummary, value); }
        public string StaticDelayText { get => _staticDelayText; set => SetProperty(ref _staticDelayText, value); }
        public string StaticDelayTone { get => _staticDelayTone; set => SetProperty(ref _staticDelayTone, value); }
        public string StaticHopSummary { get => _staticHopSummary; set => SetProperty(ref _staticHopSummary, value); }

        public string ProxySpeedText { get => _proxySpeedText; set => SetProperty(ref _proxySpeedText, value); }
        public string StaticSpeedText { get => _staticSpeedText; set => SetProperty(ref _staticSpeedText, value); }
        public string DirectSpeedText { get => _directSpeedText; set => SetProperty(ref _directSpeedText, value); }
        public int ActiveConnTotal { get => _activeConnTotal; set => SetProperty(ref _activeConnTotal, value); }
        public int MismatchedConnCount { get => _mismatchedConnCount; set => SetProperty(ref _mismatchedConnCount, value); }

        public string AppSearchText { get => _appSearchText; set { if (SetProperty(ref _appSearchText, value)) ApplyAppFilter(); } }
        public int AppFilterTab { get => _appFilterTab; set { if (SetProperty(ref _appFilterTab, value)) ApplyAppFilter(); } }
        public int SplitModeIndex
        {
            get => _splitModeIndex;
            set { if (SetProperty(ref _splitModeIndex, value)) HasUnsavedRoutingChanges = true; }
        }

        public string EditingChannelId { get => _editingChannelId; set => SetProperty(ref _editingChannelId, value); }
        public string EditingChannelName { get => _editingChannelName; set => SetProperty(ref _editingChannelName, value); }
        public bool EditingChannelEnabled { get => _editingChannelEnabled; set => SetProperty(ref _editingChannelEnabled, value); }
        public int EditingProtocolIndex { get => _editingProtocolIndex; set => SetProperty(ref _editingProtocolIndex, value); }
        public string EditingHost { get => _editingHost; set => SetProperty(ref _editingHost, value); }
        public string EditingPort { get => _editingPort; set => SetProperty(ref _editingPort, value); }
        public string EditingUsername { get => _editingUsername; set => SetProperty(ref _editingUsername, value); }
        public string EditingPassword { get => _editingPassword; set => SetProperty(ref _editingPassword, value); }
        public bool EditingAllowUdp { get => _editingAllowUdp; set => SetProperty(ref _editingAllowUdp, value); }
        public int EditingHopModeIndex { get => _editingHopModeIndex; set => SetProperty(ref _editingHopModeIndex, value); }
        public string EditingPinnedNode { get => _editingPinnedNode; set => SetProperty(ref _editingPinnedNode, value); }
        public string QuickPasteInput { get => _quickPasteInput; set => SetProperty(ref _quickPasteInput, value); }
        public string StaticOptimizeStatus { get => _staticOptimizeStatus; set => SetProperty(ref _staticOptimizeStatus, value); }

        public string ConnSearchText { get => _connSearchText; set { if (SetProperty(ref _connSearchText, value)) ApplyConnectionFilter(); } }
        public int ConnFilterTab { get => _connFilterTab; set { if (SetProperty(ref _connFilterTab, value)) ApplyConnectionFilter(); } }

        public string DnsSeverity { get => _dnsSeverity; set => SetProperty(ref _dnsSeverity, value); }
        public string DnsSummary { get => _dnsSummary; set => SetProperty(ref _dnsSummary, value); }
        public bool DnsTunEnabled { get => _dnsTunEnabled; set => SetProperty(ref _dnsTunEnabled, value); }
        public bool DnsKernelEnabled { get => _dnsKernelEnabled; set => SetProperty(ref _dnsKernelEnabled, value); }
        public bool DnsUdpGuardActive { get => _dnsUdpGuardActive; set => SetProperty(ref _dnsUdpGuardActive, value); }
        public string DesiredRegion { get => _desiredRegion; set => SetProperty(ref _desiredRegion, value); }
        public bool QualityRunning { get => _qualityRunning; set => SetProperty(ref _qualityRunning, value); }
        public string QualityProgressText { get => _qualityProgressText; set => SetProperty(ref _qualityProgressText, value); }
        public string LatestQualityHeadline { get => _latestQualityHeadline; set => SetProperty(ref _latestQualityHeadline, value); }
        public string LatestQualityDetail { get => _latestQualityDetail; set => SetProperty(ref _latestQualityDetail, value); }

        public bool CompFollowEnabled
        {
            get => _compFollowEnabled;
            set
            {
                if (SetProperty(ref _compFollowEnabled, value) && value)
                {
                    // 根治反直觉：用户开启跟随主软件时自动默认打开后台静默守望与跟随退出
                    CompFollowWatchAtLogon = true;
                    CompFollowAutoExit = true;
                }
            }
        }
        public string CompFollowAppExe { get => _compFollowAppExe; set => SetProperty(ref _compFollowAppExe, value); }
        public string CompFollowAppPath { get => _compFollowAppPath; set => SetProperty(ref _compFollowAppPath, value); }
        public string CompFollowAppName { get => _compFollowAppName; set => SetProperty(ref _compFollowAppName, value); }
        public bool CompFollowAutoExit { get => _compFollowAutoExit; set => SetProperty(ref _compFollowAutoExit, value); }
        public bool CompFollowWatchAtLogon { get => _compFollowWatchAtLogon; set => SetProperty(ref _compFollowWatchAtLogon, value); }
        public bool CompLaunchEnabled { get => _compLaunchEnabled; set => SetProperty(ref _compLaunchEnabled, value); }
        public bool CompLaunchOnFollow { get => _compLaunchOnFollow; set => SetProperty(ref _compLaunchOnFollow, value); }
        public bool CompCloseLinkedOnExit { get => _compCloseLinkedOnExit; set => SetProperty(ref _compCloseLinkedOnExit, value); }
        public bool WatcherAlive { get => _watcherAlive; set { if (SetProperty(ref _watcherAlive, value)) RaisePropertyChanged(nameof(WatcherStatusBadge)); } }
        public string WatcherStatusBadge => WatcherAlive ? "后台静默守望运行中" : "守望未启动";
        public string NewPresetName { get => _newPresetName; set => SetProperty(ref _newPresetName, value); }
        public string NewPresetDesc { get => _newPresetDesc; set => SetProperty(ref _newPresetDesc, value); }

        public int ThemeIndex
        {
            get => _themeIndex;
            set
            {
                if (SetProperty(ref _themeIndex, value))
                {
                    var pref = (ThemePreference)Math.Max(0, Math.Min(2, value));
                    _applyThemeAction?.Invoke(pref);
                    _engine?.SaveGeneralSettings(s => s.Theme = pref);
                }
            }
        }
        public bool Ipv6GuardOn { get => _ipv6GuardOn; set => SetProperty(ref _ipv6GuardOn, value); }
        public bool MainAutostartOn { get => _mainAutostartOn; set => SetProperty(ref _mainAutostartOn, value); }
        public bool AutoFailoverOn { get => _autoFailoverOn; set => SetProperty(ref _autoFailoverOn, value); }
        public string UpdateStatusText { get => _updateStatusText; set => SetProperty(ref _updateStatusText, value); }
        public string LatestReleaseUrl { get => _latestReleaseUrl; set => SetProperty(ref _latestReleaseUrl, value); }
        public string AuditSearchText { get => _auditSearchText; set { if (SetProperty(ref _auditSearchText, value)) ApplyAuditFilter(); } }
        public string AuditCategoryFilter { get => _auditCategoryFilter; set { if (SetProperty(ref _auditCategoryFilter, value)) ApplyAuditFilter(); } }

        public bool ToastVisible { get => _toastVisible; set => SetProperty(ref _toastVisible, value); }
        public string ToastTitle { get => _toastTitle; set => SetProperty(ref _toastTitle, value); }
        public string ToastBody { get => _toastBody; set => SetProperty(ref _toastBody, value); }
        public string ToastTone { get => _toastTone; set => SetProperty(ref _toastTone, value); }

        // ---- 命令集 ----
        public AsyncRelayCommand RefreshCommand { get; }
        public AsyncRelayCommand SaveRulesCommand { get; }
        public RelayCommand ToggleCompactCommand { get; }
        public AsyncRelayCommand BenchmarkNodesCommand { get; }
        public AsyncRelayCommand SwitchNodeCommand { get; }
        public AsyncRelayCommand SaveCandidatesCommand { get; }
        public RelayCommand SelectStaticChannelCommand { get; }
        public RelayCommand AddStaticChannelCommand { get; }
        public AsyncRelayCommand DeleteStaticChannelCommand { get; }
        public RelayCommand ParseQuickPasteCommand { get; }
        public AsyncRelayCommand SaveStaticChannelCommand { get; }
        public AsyncRelayCommand TestStaticDelayCommand { get; }
        public AsyncRelayCommand OptimizeStaticChannelCommand { get; }
        public AsyncRelayCommand CloseConnectionCommand { get; }
        public AsyncRelayCommand CloseMismatchedConnectionsCommand { get; }
        public AsyncRelayCommand ProbeCurrentNodeCommand { get; }
        public AsyncRelayCommand ProbeAllNodesCommand { get; }
        public RelayCommand CancelQualityProbeCommand { get; }
        public RelayCommand CopyDiagnosticReportCommand { get; }
        public RelayCommand SaveCompanionCommand { get; }
        public RelayCommand RemoveLaunchAppCommand { get; }
        public RelayCommand CreateDesktopShortcutCommand { get; }
        public RelayCommand SavePresetCommand { get; }
        public AsyncRelayCommand ApplyPresetCommand { get; }
        public RelayCommand DeletePresetCommand { get; }
        public AsyncRelayCommand ToggleIpv6GuardCommand { get; }
        public RelayCommand ToggleAutostartCommand { get; }
        public AsyncRelayCommand CheckUpdateCommand { get; }
        public AsyncRelayCommand RestoreKernelCommand { get; }
        public RelayCommand RefreshAuditCommand { get; }

        public ShellViewModel(
            ProxyPickerEngine engine,
            IShortcutCreator shortcutCreator,
            string selfExePath,
            Action<ThemePreference> applyThemeAction)
        {
            _engine = engine;
            _shortcutCreator = shortcutCreator;
            _selfExePath = selfExePath ?? "";
            _applyThemeAction = applyThemeAction;

            RefreshCommand = new AsyncRelayCommand(async () =>
            {
                if (_engine == null) return;
                await _engine.RefreshFullStateAsync(true);
                LoadAuditLogs();
                ShowToast("已刷新状态", "内核规则、节点延迟与进程列表已同步", "ok");
            });

            SaveRulesCommand = new AsyncRelayCommand(SaveRulesAsync);
            ToggleCompactCommand = new RelayCommand(() => CompactModeRequested?.Invoke());

            BenchmarkNodesCommand = new AsyncRelayCommand(async () =>
            {
                if (_engine == null) return;
                ShowToast("节点测速中", "正在并发检测主策略组全部节点延迟…", "info");
                await _engine.BenchmarkAllPrimaryNodesAsync();
                ShowToast("批量测速完成", "节点延迟已更新", "ok");
            });

            SwitchNodeCommand = new AsyncRelayCommand(async param =>
            {
                if (_engine == null || !(param is NodeRowViewModel row)) return;
                var res = await _engine.SwitchPrimaryNodeAsync(row.Name);
                ShowToast(res.Ok ? "已切换主节点" : "切换失败", res.Message, res.Ok ? "ok" : "warn");
            });

            SaveCandidatesCommand = new AsyncRelayCommand(async () =>
            {
                if (_engine == null) return;
                var cands = Nodes.Where(n => n.IsCandidate).Select(n => n.Name).ToList();
                await _engine.SaveCandidateNodesAsync(cands);
                _engine.SaveGeneralSettings(s => s.AutoFailoverEnabled = AutoFailoverOn);
                ShowToast("故障转移池已更新", "已保存 " + cands.Count + " 个备选节点", "ok");
            });

            SelectStaticChannelCommand = new RelayCommand(p => { if (p is StaticChannelCardViewModel card) LoadChannelIntoEditor(card.Id); });
            AddStaticChannelCommand = new RelayCommand(CreateNewStaticChannelDraft);
            DeleteStaticChannelCommand = new AsyncRelayCommand(DeleteEditingStaticChannelAsync);
            ParseQuickPasteCommand = new RelayCommand(ParseQuickPasteIntoEditor);
            SaveStaticChannelCommand = new AsyncRelayCommand(SaveEditingStaticChannelAsync);
            TestStaticDelayCommand = new AsyncRelayCommand(async () =>
            {
                if (_engine == null) return;
                await _engine.MeasureAllActiveStaticDelaysAsync();
                ShowToast("测速已更新", "所有启用中的固定 IP 通道延迟已刷新", "ok");
            });
            OptimizeStaticChannelCommand = new AsyncRelayCommand(OptimizeEditingStaticChannelAsync);

            CloseConnectionCommand = new AsyncRelayCommand(async p =>
            {
                if (_engine == null || !(p is ConnectionRowViewModel c)) return;
                int n = await _engine.CloseConnectionsAsync(new[] { c.Id });
                ShowToast("连接操作完成", n > 0 ? ("已断开 " + c.ProcessExe + " 的该条连接") : "连接已自动关闭", "ok");
            });

            CloseMismatchedConnectionsCommand = new AsyncRelayCommand(async () =>
            {
                if (_engine == null) return;
                var ids = _rawConnections.Where(c => c.IsMismatched).Select(c => c.Id).ToList();
                if (ids.Count == 0) { ShowToast("无错路连接", "当前所有活跃连接均与规则一致", "ok"); return; }
                int n = await _engine.CloseConnectionsAsync(ids);
                ShowToast("已清理错路连接", "共断开 " + n + " 条旧路径连接，软件将按新规则重连", "ok");
            });

            ProbeCurrentNodeCommand = new AsyncRelayCommand(async () => await RunQualityProbeAsync(false));
            ProbeAllNodesCommand = new AsyncRelayCommand(async () => await RunQualityProbeAsync(true));
            CancelQualityProbeCommand = new RelayCommand(() => { try { _qualityCts?.Cancel(); } catch { } });
            CopyDiagnosticReportCommand = new RelayCommand(() =>
            {
                string txt = _engine != null ? _engine.BuildSanitizedDiagnosticReport() : "诊断报告预览";
                try { Clipboard.SetText(txt); ShowToast("已复制脱敏诊断报告", "所有真实 IP、账密与用户名已自动打码", "ok"); }
                catch { }
            });

            SaveCompanionCommand = new RelayCommand(SaveCompanionSettings);
            RemoveLaunchAppCommand = new RelayCommand(p =>
            {
                if (p is CompanionLaunchAppRow row) CompanionLaunchApps.Remove(row);
            });
            CreateDesktopShortcutCommand = new RelayCommand(CreateCompanionShortcut);

            SavePresetCommand = new RelayCommand(SaveCurrentPreset);
            ApplyPresetCommand = new AsyncRelayCommand(async p =>
            {
                if (_engine == null || !(p is PresetCardViewModel card)) return;
                var res = await _engine.ApplyNamedPresetAsync(card.Id);
                ShowToast(res.Ok ? "已切换场景预设" : "切换失败", res.Message, res.Ok ? "ok" : "warn");
            });
            DeletePresetCommand = new RelayCommand(p =>
            {
                if (_engine == null || !(p is PresetCardViewModel card)) return;
                string err;
                if (_engine.Presets.DeletePreset(card.Id, out err))
                {
                    _ = _engine.RefreshFullStateAsync(false);
                    ShowToast("已删除预设", card.Name, "ok");
                }
            });

            ToggleIpv6GuardCommand = new AsyncRelayCommand(async () =>
            {
                if (_engine == null) return;
                var res = await _engine.SetIpv6AntiLeakAsync(Ipv6GuardOn);
                ShowToast(res.Ok ? "IPv6 设置已更新" : "切换失败", res.Message, res.Ok ? "ok" : "warn");
            });

            ToggleAutostartCommand = new RelayCommand(() =>
            {
                if (_engine == null) return;
                bool ok = _engine.SetMainAutostart(MainAutostartOn);
                ShowToast(ok ? "开机自启已更新" : "设置失败", MainAutostartOn ? "已写入当前用户自启项" : "已移除开机自启", ok ? "ok" : "warn");
            });

            CheckUpdateCommand = new AsyncRelayCommand(async () =>
            {
                if (_engine == null) return;
                UpdateStatusText = "正在只读查询 GitHub Releases…";
                var r = await _engine.CheckForUpdateAsync();
                if (!r.Ok) UpdateStatusText = "检查失败：" + r.Error;
                else if (r.HasNewer)
                {
                    LatestReleaseUrl = r.ReleaseUrl;
                    UpdateStatusText = "发现新版本 v" + r.LatestVersion + "（发布于 " + r.PublishedDate + "）";
                    ShowToast("发现新版本 v" + r.LatestVersion, "点击前往官方 GitHub Release 页面手动下载", "accent");
                }
                else
                {
                    LatestReleaseUrl = r.ReleaseUrl;
                    UpdateStatusText = "当前 v" + ProductInfo.Version + " 已是最新正式版";
                    ShowToast("已是最新版本", "当前为最新正式版 v" + ProductInfo.Version, "ok");
                }
            });

            RestoreKernelCommand = new AsyncRelayCommand(async () =>
            {
                if (_engine == null) return;
                var res = await _engine.RestoreCleanKernelAsync(true);
                await _engine.RefreshFullStateAsync(false);
                ShowToast(res.Ok ? "已还原内核配置" : "还原失败", res.Message, res.Ok ? "ok" : "warn");
            });

            RefreshAuditCommand = new RelayCommand(LoadAuditLogs);

            if (_engine != null)
            {
                _engine.Store.StateChanged += OnEngineStateChanged;
                _engine.Store.TrafficUpdated += OnEngineTrafficUpdated;
                _engine.Store.ToastRequested += (t, b, tone) => RunOnUi(() => ShowToast(t, b, tone));
            }
        }

        // ---- 同步引擎状态至 UI ----

        void OnEngineStateChanged(AppStateSnapshot snap)
        {
            RunOnUi(() => ApplySnapshotToUi(snap));
        }

        void OnEngineTrafficUpdated(TrafficSnapshot traffic)
        {
            RunOnUi(() =>
            {
                double proxyTotal = traffic.ProxyDownBps + traffic.ProxyUpBps;
                double staticTotal = traffic.StaticDownBps + traffic.StaticUpBps;
                double directTotal = traffic.DirectDownBps + traffic.DirectUpBps;
                ProxySpeedText = SparklineChart.FormatBps(proxyTotal);
                StaticSpeedText = SparklineChart.FormatBps(staticTotal);
                DirectSpeedText = SparklineChart.FormatBps(directTotal);
                ActiveConnTotal = traffic.Connections != null ? traffic.Connections.Count : 0;
                MismatchedConnCount = traffic.MismatchCount;
                _rawConnections = traffic.Connections ?? new List<ConnectionRecord>();

                var mismatchSet = new HashSet<string>(traffic.MismatchedApps ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
                foreach (var row in _allAppRows)
                {
                    int cnt;
                    row.ActiveConnections = traffic.ActiveConnByProcess != null && traffic.ActiveConnByProcess.TryGetValue(row.Exe, out cnt) ? cnt : 0;
                    row.IsMismatched = mismatchSet.Contains(row.Exe);
                }
                if (SelectedNavIndex == 2) ApplyConnectionFilter();
                TrafficSamplePushed?.Invoke(proxyTotal, staticTotal, directTotal);
            });
        }

        public void ApplySnapshotToUi(AppStateSnapshot snap)
        {
            if (snap == null) return;
            var k = snap.Kernel ?? new KernelSnapshot();
            KernelConnected = k.Connected;
            RulesApplied = k.RulesApplied;
            ClientName = string.IsNullOrEmpty(k.ClientName) ? "未检测到客户端" : k.ClientName;
            KernelSummary = k.Connected
                ? (k.Mode.ToUpperInvariant() + (k.TunEnabled ? " · TUN" : "") + (k.DnsEnabled ? " · DNS" : "") + " · " + k.TotalRuleCount + " 规则")
                : (string.IsNullOrEmpty(k.Error) ? "内核未连接" : k.Error);
            StatusMessage = snap.StatusMessage;
            StatusTone = snap.StatusTone;

            // 主通道仪表
            PrimaryNodeName = string.IsNullOrEmpty(k.PrimaryOutboundNode) ? "未选择节点" : k.PrimaryOutboundNode;
            PrimaryDelayText = k.PrimaryDelayMs > 0 ? (k.PrimaryDelayMs + " ms") : (k.PrimaryTimedOut ? "超时" : "--");
            PrimaryDelayTone = k.PrimaryDelayMs > 0 ? (k.PrimaryDelayMs < 200 ? "ok" : "warn") : (k.PrimaryTimedOut ? "danger" : "dim");

            // 固定 IP 通道仪表 + 卡片列表
            var activeStatic = snap.StaticVault != null ? snap.StaticVault.GetActiveChannels() : new List<StaticChannelProfile>();
            var primaryRt = snap.StaticRuntimes?.FirstOrDefault(x => x.Enabled) ?? snap.StaticRuntimes?.FirstOrDefault();
            if (activeStatic.Count == 0)
            {
                StaticChannelSummary = "未启用独立固定 IP";
                StaticDelayText = "--";
                StaticDelayTone = "dim";
                StaticHopSummary = "可在「节点与固定IP」配置专属家宽/机房固定出口";
            }
            else
            {
                StaticChannelSummary = activeStatic.Count == 1 ? activeStatic[0].Name : (activeStatic.Count + " 条固定 IP 通道在线");
                int d = primaryRt != null ? primaryRt.DelayMs : -1;
                StaticDelayText = (primaryRt != null && primaryRt.Optimizing) ? "优选测速中…" : (d > 0 ? (d + " ms") : (primaryRt != null && primaryRt.TimedOut ? "超时" : "--"));
                StaticDelayTone = d > 0 ? (d < 350 ? "ok" : "warn") : (primaryRt != null && primaryRt.TimedOut ? "danger" : "dim");
                string hop = primaryRt != null && !string.IsNullOrEmpty(primaryRt.LiveHopNode) ? primaryRt.LiveHopNode : "自动优选";
                StaticHopSummary = "前置跳板：" + hop + (primaryRt != null && primaryRt.UdpBlocked ? " · UDP防穿透" : "");
            }

            // 同步应用列表（若用户没有未保存草稿，用最新列表覆盖；有草稿则仅合并运行状态）
            if (!HasUnsavedRoutingChanges)
            {
                _splitModeIndex = snap.Settings.Mode == SplitMode.Compat ? 1 : 0;
                RaisePropertyChanged(nameof(SplitModeIndex));
                _allAppRows = (snap.Apps ?? new List<AppViewItem>())
                    .Select(a => new AppRowViewModel(a, _ => HasUnsavedRoutingChanges = true))
                    .ToList();
                ApplyAppFilter();
            }

            // 同步节点列表
            Nodes.Clear();
            foreach (var n in snap.Nodes ?? new List<ProxyNodeItem>())
            {
                Nodes.Add(new NodeRowViewModel
                {
                    Name = n.Name,
                    Type = n.Type,
                    DelayMs = n.DelayMs,
                    IsCurrent = n.IsCurrent,
                    IsCandidate = n.IsCandidate
                });
            }

            // 同步多固定 IP 通道卡片
            StaticChannels.Clear();
            if (snap.StaticVault != null)
            {
                foreach (var ch in snap.StaticVault.Channels)
                {
                    var rt = snap.StaticRuntimes?.FirstOrDefault(x => string.Equals(x.ChannelId, ch.Id, StringComparison.OrdinalIgnoreCase));
                    StaticChannels.Add(new StaticChannelCardViewModel
                    {
                        Id = ch.Id,
                        Name = ch.Name,
                        Enabled = ch.Enabled,
                        EndpointMasked = string.IsNullOrEmpty(ch.Host) ? "未填写地址" : (ch.Protocol.ToUpperInvariant() + " · " + MaskHostForCard(ch.Host) + ":" + ch.Port),
                        HopModeLabel = ch.HopMode == StaticHopMode.Auto ? "10分钟自动优选" : (ch.HopMode == StaticHopMode.Follow ? "跟随主节点" : (ch.HopMode == StaticHopMode.Pin ? "固定跳板" : "直连")),
                        LiveHopLabel = rt != null && !string.IsNullOrEmpty(rt.LiveHopNode) ? rt.LiveHopNode : "未选",
                        DelayLabel = rt != null && rt.DelayMs > 0 ? (rt.DelayMs + " ms") : "--",
                        UdpBadge = ch.AllowUdp ? "UDP 放行" : "UDP/QUIC 防穿透",
                        IsSelected = string.Equals(ch.Id, EditingChannelId, StringComparison.OrdinalIgnoreCase)
                    });
                }
                if (string.IsNullOrEmpty(EditingHost) && snap.StaticVault.Channels.Count > 0)
                    LoadChannelIntoEditor(snap.StaticVault.Channels[0].Id, snap.StaticVault);
            }

            // 同步 DNS 体检卡片
            if (_engine != null)
            {
                var dns = _engine.GetDnsDiagnosticAdvice();
                DnsSeverity = dns.Severity;
                DnsSummary = dns.Summary;
                DnsTunEnabled = dns.TunEnabled;
                DnsKernelEnabled = dns.KernelDnsEnabled;
                DnsUdpGuardActive = dns.StaticUdpGuardActive;
                DnsRecommendations.Clear();
                foreach (var rec in dns.Recommendations) DnsRecommendations.Add(rec);
            }

            // 同步四向联动与场景预设
            SyncCompanionFromSettings(snap.Settings.Companion, snap.WatcherAlive);
            Presets.Clear();
            foreach (var p in snap.Settings.Presets ?? new List<WorkspacePreset>())
            {
                Presets.Add(new PresetCardViewModel
                {
                    Id = p.Id,
                    Name = p.Name,
                    Description = string.IsNullOrEmpty(p.Description) ? "自定义分流工作区" : p.Description,
                    MetaSummary = (p.Mode == SplitMode.Whitelist ? "白名单" : "订阅兼容") + " · " + p.Rules.Count + " 条软件规则",
                    IsActive = string.Equals(p.Id, snap.Settings.ActivePresetId, StringComparison.OrdinalIgnoreCase)
                });
            }

            // 同步设置页开关
            _themeIndex = (int)snap.Settings.Theme; RaisePropertyChanged(nameof(ThemeIndex));
            Ipv6GuardOn = snap.Settings.Ipv6GuardOn;
            AutoFailoverOn = snap.Settings.AutoFailoverEnabled;
            if (_engine != null) MainAutostartOn = _engine.IsMainAutostartEnabled();
        }

        // ---- 业务命令实现 ----

        async Task SaveRulesAsync()
        {
            if (_engine == null) return;
            var rules = new List<AppRouteRule>();
            foreach (var r in _allAppRows)
            {
                if (r.Channel == RouteChannel.Direct) continue;
                rules.Add(new AppRouteRule
                {
                    Exe = r.Exe,
                    Channel = r.Channel,
                    StaticChannelId = r.StaticChannelId
                });
            }
            var mode = SplitModeIndex == 1 ? SplitMode.Compat : SplitMode.Whitelist;
            var res = await _engine.SaveAndApplyRulesAsync(rules, mode);
            if (res.Ok)
            {
                HasUnsavedRoutingChanges = false;
                ShowToast("分流规则已推送生效", "已应用 " + rules.Count + " 条代理规则（仅重连变动软件）", "ok");
            }
            else
            {
                ShowToast("推送失败", res.Message, "danger");
            }
        }

        public async Task AddCustomAppByPathAsync(string path)
        {
            if (_engine == null || string.IsNullOrWhiteSpace(path)) return;
            await _engine.AddCustomAppAsync(path);
            ShowToast("已添加软件", CompanionPolicyEngine.ExtractExeName(path), "ok");
        }

        void LoadChannelIntoEditor(string channelId, StaticChannelVault vaultOverride = null)
        {
            var vault = vaultOverride ?? _engine?.Store.GetSnapshot().StaticVault;
            var ch = vault?.FindById(channelId);
            if (ch == null) return;
            EditingChannelId = ch.Id;
            EditingChannelName = ch.Name;
            EditingChannelEnabled = ch.Enabled;
            EditingProtocolIndex = string.Equals(ch.Protocol, "http", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            EditingHost = ch.Host;
            EditingPort = ch.Port > 0 ? ch.Port.ToString() : "";
            EditingUsername = ch.Username;
            EditingPassword = ch.Password;
            EditingAllowUdp = ch.AllowUdp;
            EditingHopModeIndex = (int)ch.HopMode;
            EditingPinnedNode = ch.PinnedHopNode;
            foreach (var c in StaticChannels) c.IsSelected = string.Equals(c.Id, ch.Id, StringComparison.OrdinalIgnoreCase);
        }

        void CreateNewStaticChannelDraft()
        {
            EditingChannelId = "ch_" + Guid.NewGuid().ToString("N").Substring(0, 6);
            EditingChannelName = "专属固定IP #" + (StaticChannels.Count + 1);
            EditingChannelEnabled = true;
            EditingProtocolIndex = 0;
            EditingHost = "";
            EditingPort = "1080";
            EditingUsername = "";
            EditingPassword = "";
            EditingAllowUdp = false;
            EditingHopModeIndex = 0;
            EditingPinnedNode = "";
            ShowToast("新建固定 IP 通道", "填写地址或粘贴一行 SOCKS5 链接后点保存即可加入通道池", "info");
        }

        void ParseQuickPasteIntoEditor()
        {
            string proto, host, user, pass;
            int port;
            if (!StaticChannelAlgorithms.TryParseQuickLine(QuickPasteInput, out proto, out host, out port, out user, out pass))
            {
                ShowToast("无法解析粘贴内容", "支持格式：socks5://user:pass@ip:port 或 ip:port:user:pass", "warn");
                return;
            }
            EditingProtocolIndex = string.Equals(proto, "http", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            EditingHost = host;
            EditingPort = port.ToString();
            EditingUsername = user;
            EditingPassword = pass;
            EditingChannelEnabled = true;
            QuickPasteInput = "";
            ShowToast("已解析固定 IP 参数", proto.ToUpperInvariant() + " " + MaskHostForCard(host) + ":" + port + "，点击保存立即生效", "ok");
        }

        async Task SaveEditingStaticChannelAsync()
        {
            if (_engine == null) return;
            int port;
            int.TryParse((EditingPort ?? "").Trim(), out port);
            var vault = _engine.Store.GetSnapshot().StaticVault.Clone();
            var existing = vault.FindById(EditingChannelId);
            var target = existing ?? new StaticChannelProfile { Id = EditingChannelId };
            target.Name = string.IsNullOrWhiteSpace(EditingChannelName) ? "独立固定IP" : EditingChannelName.Trim();
            target.Enabled = EditingChannelEnabled;
            target.Protocol = EditingProtocolIndex == 1 ? "http" : "socks5";
            target.Host = (EditingHost ?? "").Trim();
            target.Port = port;
            target.Username = (EditingUsername ?? "").Trim();
            target.Password = (EditingPassword ?? "").Trim();
            target.AllowUdp = EditingAllowUdp;
            target.HopMode = (StaticHopMode)Math.Max(0, Math.Min(3, EditingHopModeIndex));
            target.PinnedHopNode = (EditingPinnedNode ?? "").Trim();

            string err;
            if (target.Enabled && !target.IsValid(out err))
            {
                ShowToast("固定 IP 参数校验未通过", err, "warn");
                return;
            }
            if (existing == null) vault.Channels.Add(target);

            var res = await _engine.SaveStaticVaultAndApplyAsync(vault);
            ShowToast(res.Ok ? "固定 IP 通道已保存" : "保存失败", res.Message, res.Ok ? "ok" : "danger");
        }

        async Task DeleteEditingStaticChannelAsync()
        {
            if (_engine == null) return;
            if (string.Equals(EditingChannelId, StaticChannelProfile.DefaultChannelId, StringComparison.OrdinalIgnoreCase))
            {
                ShowToast("默认通道不可删除", "你可以关闭「启用此固定 IP 通道」开关来停用默认通道", "warn");
                return;
            }
            var vault = _engine.Store.GetSnapshot().StaticVault.Clone();
            vault.Channels.RemoveAll(c => string.Equals(c.Id, EditingChannelId, StringComparison.OrdinalIgnoreCase));
            var res = await _engine.SaveStaticVaultAndApplyAsync(vault);
            if (vault.Channels.Count > 0) LoadChannelIntoEditor(vault.Channels[0].Id, vault);
            ShowToast(res.Ok ? "已删除固定 IP 通道" : "删除失败", res.Message, res.Ok ? "ok" : "warn");
        }

        async Task OptimizeEditingStaticChannelAsync()
        {
            if (_engine == null) return;
            try { _staticOptCts?.Cancel(); } catch { }
            _staticOptCts = new CancellationTokenSource();
            StaticOptimizeStatus = "正在启动零干扰隔离侧车对前置跳板执行 3 次稳态复核…";
            bool ok = await _engine.OptimizeStaticChannelAsync(EditingChannelId, msg => RunOnUi(() => StaticOptimizeStatus = msg), _staticOptCts.Token);
            StaticOptimizeStatus = ok ? "稳速优选已完成并热切换至最优前置跳板" : "优选未完成，请确认该通道已启用并保存";
            ShowToast(ok ? "固定 IP 跳板优选完成" : "优选未完成", StaticOptimizeStatus, ok ? "ok" : "warn");
        }

        async Task RunQualityProbeAsync(bool allNodes)
        {
            if (_engine == null || QualityRunning) return;
            try { _qualityCts?.Cancel(); } catch { }
            _qualityCts = new CancellationTokenSource();
            QualityRunning = true;
            QualityReports.Clear();
            try
            {
                if (!allNodes)
                {
                    QualityProgressText = "正在零干扰实测当前主节点的延迟、双源 IP 纯净度与四门服务放行…";
                    var rep = await _engine.ProbeSingleNodeQualityAsync(PrimaryNodeName, DesiredRegion, _qualityCts.Token);
                    AddQualityRow(rep);
                    LatestQualityHeadline = rep.VerdictTitle + " · 风险分 " + (rep.RiskScore >= 0 ? (rep.RiskScore + "/100") : "--");
                    LatestQualityDetail = rep.RegionNote + " · " + rep.IpKindLabel + " · " + rep.VerdictDetail;
                    QualityProgressText = "单节点体检完成（" + (rep.IsSidecarIsolated ? "零干扰隔离侧车" : "宿主通道") + "）";
                }
                else
                {
                    var nodeList = Nodes.Select(n => n.Name).Take(40).ToList();
                    QualityProgressText = "正在零干扰旁路批量检测 " + nodeList.Count + " 个节点…";
                    await _engine.ProbeAllNodesQualityAsync(nodeList, DesiredRegion, (done, total, rep) =>
                    {
                        RunOnUi(() =>
                        {
                            AddQualityRow(rep);
                            QualityProgressText = "全量体检进度：" + done + " / " + total + "（零干扰隔离侧车）";
                        });
                    }, _qualityCts.Token);
                    QualityProgressText = "全量节点风控体检完成，共评估 " + QualityReports.Count + " 个节点";
                }
            }
            finally
            {
                QualityRunning = false;
            }
        }

        void AddQualityRow(NodeQualityReport r)
        {
            if (r == null) return;
            string badge = r.Level == NodeRiskLevel.Low ? "低风险" : (r.Level == NodeRiskLevel.Medium ? "中风险" : (r.Level == NodeRiskLevel.High ? "高风险" : "未知"));
            string tone = r.Level == NodeRiskLevel.Low ? "low" : (r.Level == NodeRiskLevel.Medium ? "med" : (r.Level == NodeRiskLevel.High ? "high" : "dim"));
            QualityReports.Add(new QualityRowViewModel
            {
                NodeName = r.TargetNode,
                RiskBadge = badge,
                RiskTone = tone,
                RiskScoreText = r.RiskScore >= 0 ? (r.RiskScore + "/100") : "--",
                RegionAndKind = (string.IsNullOrEmpty(r.CountryName) ? "未知" : r.CountryName) + " · " + r.IpKindLabel,
                LatencyText = r.Net.AvgMs > 0 ? (r.Net.AvgMs + "ms ±" + r.Net.JitterMs) : "超时",
                GoogleStatus = FormatGate(r.GoogleSearch),
                GeminiStatus = FormatGate(r.GeminiApi),
                OpenAiStatus = FormatGate(r.OpenAiApi),
                ClaudeStatus = FormatGate(r.ClaudeApi),
                VerdictSummary = r.VerdictDetail
            });
        }

        static string FormatGate(ServiceGateResult g)
        {
            if (g == null || !g.Tested) return "--";
            return g.Ok ? "✓ 放行" : (g.Blocked ? "✗ 拦截" : "异常");
        }

        public void SetFollowTargetFromPath(string fullPath)
        {
            if (string.IsNullOrWhiteSpace(fullPath)) return;
            CompFollowAppPath = fullPath.Trim();
            CompFollowAppExe = CompanionPolicyEngine.ExtractExeName(fullPath);
            CompFollowAppName = System.IO.Path.GetFileNameWithoutExtension(CompFollowAppExe);
            CompFollowEnabled = true;
        }

        public void AddLaunchAppFromPath(string fullPath)
        {
            if (string.IsNullOrWhiteSpace(fullPath)) return;
            string exe = CompanionPolicyEngine.ExtractExeName(fullPath);
            if (CompanionLaunchApps.Any(x => string.Equals(x.FullPath, fullPath, StringComparison.OrdinalIgnoreCase))) return;
            CompanionLaunchApps.Add(new CompanionLaunchAppRow
            {
                FullPath = fullPath.Trim(),
                ExeName = exe,
                DisplayName = System.IO.Path.GetFileNameWithoutExtension(exe),
                KindBadge = CompanionPolicyEngine.IsProxyClientExe(exe) ? "代理客户端（优先启动 / 最后关闭）" : "业务软件（规则就绪后启动 / 先关闭）"
            });
            CompLaunchEnabled = true;
        }

        void SyncCompanionFromSettings(CompanionConfig c, bool watcherAlive)
        {
            WatcherAlive = watcherAlive;
            if (c == null) return;
            _compFollowEnabled = c.FollowEnabled; RaisePropertyChanged(nameof(CompFollowEnabled));
            CompFollowAppExe = c.FollowAppExe;
            CompFollowAppPath = c.FollowAppPath;
            CompFollowAppName = c.FollowAppName;
            CompFollowAutoExit = c.FollowAutoExit;
            CompFollowWatchAtLogon = c.FollowWatchAtLogon;
            CompLaunchEnabled = c.LaunchAppsEnabled;
            CompLaunchOnFollow = c.LaunchAppsOnFollow;
            CompCloseLinkedOnExit = c.CloseLinkedAppsOnExit;

            CompanionLaunchApps.Clear();
            foreach (var p in c.LaunchAppPaths ?? new List<string>())
            {
                string exe = CompanionPolicyEngine.ExtractExeName(p);
                CompanionLaunchApps.Add(new CompanionLaunchAppRow
                {
                    FullPath = p,
                    ExeName = exe,
                    DisplayName = System.IO.Path.GetFileNameWithoutExtension(exe),
                    KindBadge = CompanionPolicyEngine.IsProxyClientExe(exe) ? "代理客户端（先开后关）" : "业务软件（后开先关）"
                });
            }
        }

        void SaveCompanionSettings()
        {
            if (_engine == null) return;
            var cfg = new CompanionConfig
            {
                FollowEnabled = CompFollowEnabled,
                FollowAppExe = CompFollowAppExe,
                FollowAppPath = CompFollowAppPath,
                FollowAppName = CompFollowAppName,
                FollowAutoExit = CompFollowAutoExit,
                FollowWatchAtLogon = CompFollowWatchAtLogon,
                LaunchAppsEnabled = CompLaunchEnabled,
                LaunchAppsOnFollow = CompLaunchOnFollow,
                LaunchAppPaths = CompanionLaunchApps.Select(x => x.FullPath).ToList(),
                CloseLinkedAppsOnExit = CompCloseLinkedOnExit
            };
            string err;
            if (_engine.SaveCompanionConfig(cfg, out err))
                ShowToast("四向软件联动已保存生效", cfg.FollowWatchAtLogon && cfg.HasValidFollowTarget ? "后台静默守望已就绪，开关软件将自动双向同步" : "联动策略已更新", "ok");
            else
                ShowToast("保存联动设置失败", err, "danger");
        }

        void CreateCompanionShortcut()
        {
            if (_shortcutCreator == null || string.IsNullOrWhiteSpace(CompFollowAppPath))
            {
                ShowToast("请先选择要跟随的主软件", "先点击「浏览选择软件」指定目标 .exe 路径", "warn");
                return;
            }
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string created, err;
            if (_shortcutCreator.CreateCompanionShortcut(CompFollowAppPath, CompFollowAppName, desktop, _selfExePath, out created, out err))
                ShowToast("桌面快捷方式已创建", "双击即可先拉起 ProxyPicker 并同步启动该软件", "ok");
            else
                ShowToast("创建失败", err, "danger");
        }

        void SaveCurrentPreset()
        {
            if (_engine == null) return;
            WorkspacePreset created;
            string err;
            if (_engine.Presets.SaveCurrentAsPreset(NewPresetName, NewPresetDesc, out created, out err))
            {
                NewPresetName = "";
                NewPresetDesc = "";
                _ = _engine.RefreshFullStateAsync(false);
                ShowToast("已保存场景预设", created.Name, "ok");
            }
            else ShowToast("保存预设失败", err, "warn");
        }

        public void LoadAuditLogs()
        {
            if (_engine == null) return;
            _allAuditEntries = _engine.GetRecentAuditLogs(250);
            ApplyAuditFilter();
        }

        public string ExportConfigBundle(string passphrase, bool includeStatic)
        {
            return _engine != null ? _engine.ExportEncryptedConfig(passphrase, includeStatic) : "";
        }

        public async Task<Tuple<bool, string>> ImportConfigBundleAsync(string json, string passphrase)
        {
            if (_engine == null) return Tuple.Create(false, "引擎未就绪");
            var r = await _engine.ImportEncryptedConfigAsync(json, passphrase);
            ShowToast(r.Item1 ? "配置迁移导入成功" : "导入失败", r.Item2, r.Item1 ? "ok" : "danger");
            return r;
        }

        // ---- 筛选器 ----

        void ApplyAppFilter()
        {
            FilteredApps.Clear();
            string kw = (AppSearchText ?? "").Trim();
            foreach (var r in _allAppRows)
            {
                if (AppFilterTab == 1 && !r.Running) continue;
                if (AppFilterTab == 2 && r.Channel != RouteChannel.PrimaryProxy) continue;
                if (AppFilterTab == 3 && r.Channel != RouteChannel.StaticChannel) continue;
                if (kw.Length > 0 &&
                    r.DisplayName.IndexOf(kw, StringComparison.OrdinalIgnoreCase) < 0 &&
                    r.Exe.IndexOf(kw, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                FilteredApps.Add(r);
            }
        }

        void ApplyConnectionFilter()
        {
            FilteredConnections.Clear();
            string kw = (ConnSearchText ?? "").Trim();
            foreach (var c in _rawConnections)
            {
                if (ConnFilterTab == 1 && c.ActualChannel != RouteChannel.PrimaryProxy) continue;
                if (ConnFilterTab == 2 && c.ActualChannel != RouteChannel.StaticChannel) continue;
                if (ConnFilterTab == 3 && c.ActualChannel != RouteChannel.Direct) continue;
                if (ConnFilterTab == 4 && !c.IsMismatched) continue;
                if (kw.Length > 0 &&
                    (c.Process ?? "").IndexOf(kw, StringComparison.OrdinalIgnoreCase) < 0 &&
                    (c.TargetHostMasked ?? "").IndexOf(kw, StringComparison.OrdinalIgnoreCase) < 0 &&
                    (c.ChainDisplay ?? "").IndexOf(kw, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                string chanLabel = c.ActualChannel == RouteChannel.PrimaryProxy ? "主代理"
                    : (c.ActualChannel == RouteChannel.StaticChannel ? "固定IP" : "直连");

                FilteredConnections.Add(new ConnectionRowViewModel
                {
                    Id = c.Id,
                    ProcessExe = c.Process,
                    TargetEndpoint = c.TargetPort > 0 ? (c.TargetHostMasked + ":" + c.TargetPort) : c.TargetHostMasked,
                    Network = (c.Network ?? "TCP").ToUpperInvariant(),
                    ActualChannel = chanLabel,
                    OutboundChain = c.ChainDisplay,
                    RuleMatched = string.IsNullOrEmpty(c.RulePayload) ? c.Rule : (c.Rule + "(" + c.RulePayload + ")"),
                    TrafficText = "↓" + FormatBytes(c.DownloadTotalBytes) + "  ↑" + FormatBytes(c.UploadTotalBytes),
                    IsMismatched = c.IsMismatched
                });
            }
        }

        void ApplyAuditFilter()
        {
            FilteredAudits.Clear();
            string kw = (AuditSearchText ?? "").Trim();
            string cat = AuditCategoryFilter ?? "全部";
            foreach (var a in _allAuditEntries)
            {
                if (cat != "全部" && !string.Equals(a.Category, cat, StringComparison.OrdinalIgnoreCase)) continue;
                if (kw.Length > 0 &&
                    a.Message.IndexOf(kw, StringComparison.OrdinalIgnoreCase) < 0 &&
                    a.Category.IndexOf(kw, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                FilteredAudits.Add(new AuditRowViewModel { Timestamp = a.TimestampLocal.ToString("MM-dd HH:mm:ss"), Category = a.Category, Message = a.Message });
            }
        }

        public void ShowToast(string title, string body, string tone = "info")
        {
            ToastTitle = title ?? "";
            ToastBody = body ?? "";
            ToastTone = tone ?? "info";
            ToastVisible = true;
            ToastNotification?.Invoke(ToastTitle, ToastBody, ToastTone);
        }

        /// <summary>向离屏视觉验收模式注入高逼真演示数据（零真实用户隐私、零改写磁盘）。</summary>
        public void LoadVisualVerificationPreview()
        {
            KernelConnected = true;
            RulesApplied = true;
            ClientName = "Clash Verge Rev";
            KernelSummary = "RULE · TUN · 内置DNS · 24 条规则";
            StatusMessage = "分流守护中 · 11 条软件规则全部与内核一致";
            StatusTone = "ok";
            PrimaryNodeName = "日本-东京-IEPL-01";
            PrimaryDelayText = "68 ms";
            PrimaryDelayTone = "ok";
            StaticChannelSummary = "2 条固定 IP 通道在线";
            StaticDelayText = "182 ms";
            StaticDelayTone = "ok";
            StaticHopSummary = "前置跳板：日本-东京-IEPL-01 · UDP/QUIC 防穿透";
            ProxySpeedText = "1.42 MB/s";
            StaticSpeedText = "386.4 KB/s";
            DirectSpeedText = "624.8 KB/s";
            ActiveConnTotal = 38;
            MismatchedConnCount = 1;

            _allAppRows = new List<AppRowViewModel>
            {
                new AppRowViewModel(new AppViewItem { Exe = "antigravity.exe", DisplayName = "Google Antigravity", Running = true, ProcessCount = 6, ActiveConnections = 9, Channel = RouteChannel.StaticChannel }, _ => HasUnsavedRoutingChanges = true),
                new AppRowViewModel(new AppViewItem { Exe = "chrome.exe", DisplayName = "Google Chrome", Running = true, ProcessCount = 14, ActiveConnections = 18, Channel = RouteChannel.PrimaryProxy }, _ => HasUnsavedRoutingChanges = true),
                new AppRowViewModel(new AppViewItem { Exe = "cursor.exe", DisplayName = "Cursor", Running = true, ProcessCount = 4, ActiveConnections = 5, Channel = RouteChannel.StaticChannel }, _ => HasUnsavedRoutingChanges = true),
                new AppRowViewModel(new AppViewItem { Exe = "telegram.exe", DisplayName = "Telegram", Running = true, ProcessCount = 1, ActiveConnections = 3, Channel = RouteChannel.PrimaryProxy }, _ => HasUnsavedRoutingChanges = true),
                new AppRowViewModel(new AppViewItem { Exe = "wechat.exe", DisplayName = "微信", Running = true, ProcessCount = 2, ActiveConnections = 2, Channel = RouteChannel.Direct }, _ => HasUnsavedRoutingChanges = true),
                new AppRowViewModel(new AppViewItem { Exe = "steam.exe", DisplayName = "Steam", Running = false, Channel = RouteChannel.Direct }, _ => HasUnsavedRoutingChanges = true),
                new AppRowViewModel(new AppViewItem { Exe = "code.exe", DisplayName = "VS Code", Running = true, ProcessCount = 5, ActiveConnections = 1, Channel = RouteChannel.PrimaryProxy, IsMismatched = true }, _ => HasUnsavedRoutingChanges = true)
            };
            ApplyAppFilter();

            Nodes.Clear();
            Nodes.Add(new NodeRowViewModel { Name = "日本-东京-IEPL-01", Type = "Shadowsocks", DelayMs = 68, IsCurrent = true, IsCandidate = true });
            Nodes.Add(new NodeRowViewModel { Name = "日本-大阪-专线-02", Type = "VMess", DelayMs = 84, IsCandidate = true });
            Nodes.Add(new NodeRowViewModel { Name = "新加坡-狮城-01", Type = "Trojan", DelayMs = 112, IsCandidate = true });
            Nodes.Add(new NodeRowViewModel { Name = "美国-洛杉矶-家宽", Type = "VLESS", DelayMs = 176, IsCandidate = false });

            StaticChannels.Clear();
            StaticChannels.Add(new StaticChannelCardViewModel { Id = "default", Name = "美国住宅固定IP（主通道）", Enabled = true, EndpointMasked = "SOCKS5 · 198.51.*.*:1080", HopModeLabel = "10分钟自动优选", LiveHopLabel = "日本-东京-IEPL-01", DelayLabel = "182 ms", UdpBadge = "UDP/QUIC 防穿透", IsSelected = true });
            StaticChannels.Add(new StaticChannelCardViewModel { Id = "jp_dev", Name = "日本静态开发出口", Enabled = true, EndpointMasked = "SOCKS5 · 203.0.*.*:2080", HopModeLabel = "固定跳板", LiveHopLabel = "日本-大阪-专线-02", DelayLabel = "118 ms", UdpBadge = "UDP/QUIC 防穿透" });
            EditingChannelName = "美国住宅固定IP（主通道）";
            EditingChannelEnabled = true;
            EditingHost = "198.51.100.10";
            EditingPort = "1080";
            EditingUsername = "demo_user";
            StaticOptimizeStatus = "上次稳速优选：3 分钟前 · 日本-东京-IEPL-01（182ms，抖动 6ms）";

            _rawConnections = new List<ConnectionRecord>
            {
                new ConnectionRecord { Id = "c1", Process = "antigravity.exe", TargetHostMasked = "generativelanguage.googleapis.com", TargetPort = 443, Network = "tcp", ActualChannel = RouteChannel.StaticChannel, ChainDisplay = "PP-STATIC-OUT → 日本-东京-IEPL-01", Rule = "PROCESS-NAME", RulePayload = "antigravity.exe", DownloadTotalBytes = 4820190, UploadTotalBytes = 612400 },
                new ConnectionRecord { Id = "c2", Process = "chrome.exe", TargetHostMasked = "github.com", TargetPort = 443, Network = "tcp", ActualChannel = RouteChannel.PrimaryProxy, ChainDisplay = "Proxy → 日本-东京-IEPL-01", Rule = "PROCESS-NAME", RulePayload = "chrome.exe", DownloadTotalBytes = 12840960, UploadTotalBytes = 890120 },
                new ConnectionRecord { Id = "c3", Process = "wechat.exe", TargetHostMasked = "szshort.weixin.qq.com", TargetPort = 443, Network = "tcp", ActualChannel = RouteChannel.Direct, ChainDisplay = "DIRECT", Rule = "MATCH", RulePayload = "DIRECT", DownloadTotalBytes = 2048100, UploadTotalBytes = 152300 },
                new ConnectionRecord { Id = "c4", Process = "code.exe", TargetHostMasked = "api.githubcopilot.com", TargetPort = 443, Network = "tcp", ActualChannel = RouteChannel.Direct, ChainDisplay = "DIRECT", Rule = "旧连接残留", DownloadTotalBytes = 120400, UploadTotalBytes = 34200, IsMismatched = true }
            };
            ApplyConnectionFilter();

            DnsSeverity = "ok";
            DnsSummary = "TUN 虚拟网卡与客户端内置 DNS 均已开启，固定 IP UDP/QUIC 防穿透规则正在运行";
            DnsTunEnabled = true; DnsKernelEnabled = true; DnsUdpGuardActive = true;
            DnsRecommendations.Clear();
            DnsRecommendations.Add("浏览器已建议开启安全 DNS (DoH 1.1.1.1)，所有分流软件均零 DNS 泄露风险。");

            LatestQualityHeadline = "低风险 · 纯净原生出口 · 风险分 6/100";
            LatestQualityDetail = "与预期地区一致（日本） · 住宅家宽 IP · Google / Gemini / OpenAI / Claude 4/4 全放行";
            QualityReports.Clear();
            QualityReports.Add(new QualityRowViewModel { NodeName = "日本-东京-IEPL-01", RiskBadge = "低风险", RiskTone = "low", RiskScoreText = "6/100", RegionAndKind = "日本 · 住宅家宽 IP", LatencyText = "68ms ±4", GoogleStatus = "✓ 放行", GeminiStatus = "✓ 放行", OpenAiStatus = "✓ 放行", ClaudeStatus = "✓ 放行", VerdictSummary = "纯净原生出口，全服务正常放行" });
            QualityReports.Add(new QualityRowViewModel { NodeName = "新加坡-狮城-01", RiskBadge = "中风险", RiskTone = "med", RiskScoreText = "28/100", RegionAndKind = "新加坡 · 机房 IP", LatencyText = "112ms ±9", GoogleStatus = "✓ 放行", GeminiStatus = "✓ 放行", OpenAiStatus = "✓ 放行", ClaudeStatus = "✗ 拦截", VerdictSummary = "机房 IP，常规业务可用" });

            CompFollowEnabled = true; CompFollowAppName = "Clash Verge"; CompFollowAppExe = "clash-verge.exe";
            CompFollowAppPath = @"C:\Program Files\Clash Verge\clash-verge.exe";
            CompFollowAutoExit = true; CompFollowWatchAtLogon = true;
            CompLaunchEnabled = true; CompLaunchOnFollow = true; CompCloseLinkedOnExit = true;
            WatcherAlive = true;
            CompanionLaunchApps.Clear();
            CompanionLaunchApps.Add(new CompanionLaunchAppRow { ExeName = "clash-verge.exe", DisplayName = "Clash Verge", FullPath = @"C:\Program Files\Clash Verge\clash-verge.exe", KindBadge = "代理客户端（先开后关）" });
            CompanionLaunchApps.Add(new CompanionLaunchAppRow { ExeName = "antigravity.exe", DisplayName = "Google Antigravity", FullPath = @"C:\Users\Demo\AppData\Local\Programs\Antigravity\Antigravity.exe", KindBadge = "业务软件（后开先关）" });

            Presets.Clear();
            Presets.Add(new PresetCardViewModel { Id = "p1", Name = "AI 深度开发（默认）", Description = "IDE 与 AI 客户端走住宅固定 IP，浏览器走低延迟主代理，国内软件直连", MetaSummary = "白名单 · 11 条软件规则", IsActive = true });
            Presets.Add(new PresetCardViewModel { Id = "p2", Name = "纯净会议 / 办公直连", Description = "仅保留终端与文档查资料代理，释放全部本地带宽", MetaSummary = "白名单 · 3 条软件规则", IsActive = false });

            _allAuditEntries = new List<AuditLogEntry>
            {
                new AuditLogEntry { TimestampLocal = new DateTime(2026, 11, 26, 21, 45, 12), Category = "STATIC", Message = "固定 IP 稳速优选切换前置跳板：日本-东京-IEPL-01（182ms，32/36 可达）" },
                new AuditLogEntry { TimestampLocal = new DateTime(2026, 11, 26, 21, 40, 3), Category = "APPLY", Message = "保存并推送分流成功（11 条 · Whitelist · rev=4）" },
                new AuditLogEntry { TimestampLocal = new DateTime(2026, 11, 26, 21, 35, 1), Category = "COMPANION", Message = "后台静默守望检测到 clash-verge.exe 启动，已自动拉起 ProxyPicker 并校验规则" }
            };
            ApplyAuditFilter();
        }

        static string MaskHostForCard(string host)
        {
            if (string.IsNullOrWhiteSpace(host)) return "";
            string h = host.Trim();
            string[] parts = h.Split('.');
            if (parts.Length == 4) return parts[0] + "." + parts[1] + ".*.*";
            return h.Length > 8 ? (h.Substring(0, 4) + "***" + h.Substring(h.Length - 3)) : "***";
        }

        static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.0") + " KB";
            return (bytes / (1024.0 * 1024.0)).ToString("0.0") + " MB";
        }

        static void RunOnUi(Action action)
        {
            var disp = System.Windows.Application.Current?.Dispatcher;
            if (disp == null || disp.CheckAccess()) action();
            else disp.BeginInvoke(action);
        }
    }
}
