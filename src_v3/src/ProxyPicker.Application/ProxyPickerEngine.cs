using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ProxyPicker.Domain.Interfaces;
using ProxyPicker.Domain.Json;
using ProxyPicker.Domain.Models;
using ProxyPicker.Domain.Services;

namespace ProxyPicker.Application
{
    /// <summary>
    /// 客户端环境查询委托与运行期核心编排中枢（彻底取代 v2 MainForm.cs 的 6,800 行上帝逻辑）。
    /// 所有后台轮询、内核推送、看守补回、多固定 IP 测速优选、实时连接监视与四向联动均在此统一受控运行。
    /// </summary>
    public sealed class ProxyPickerEngine : IDisposable
    {
        readonly IMihomoClient _mihomo;
        readonly IConfigRepository _repo;
        readonly IProcessProbe _probe;
        readonly IAppLauncher _launcher;
        readonly IAutostartStore _autostart;
        readonly IProbeSidecar _sidecar;
        readonly INodeQualityService _quality;
        readonly IUpdateChecker _updater;
        readonly IClock _clock;
        readonly IAuditSink _audit;
        readonly RedactorService _redactor;
        readonly Func<bool, ClientEnvironment> _detectEnv;
        readonly Func<Dictionary<string, string>> _scanInstalledApps;
        readonly Func<string, string> _displayNameOf;
        readonly Action<Action<bool, string>> _readSystemProxy;
        readonly string _selfExePath;

        readonly TrafficAggregator _trafficAgg = new TrafficAggregator();
        readonly CancellationTokenSource _cts = new CancellationTokenSource();

        public AppStateStore Store { get; }
        public KernelCoordinator Coordinator { get; }
        public PowerResumeGuard PowerGuard { get; }
        public CompanionOrchestrator Companion { get; }
        public StaticChannelScheduler StaticScheduler { get; }
        public WorkspacePresetService Presets { get; }

        Timer _trafficTimer;
        Timer _watchdogTimer;
        Timer _companionTimer;
        int _trafficTickBusy;
        int _watchdogTickBusy;
        int _watchdogCycle;
        bool _andRulesRejected;
        bool _disposed;

        public ProxyPickerEngine(
            IMihomoClient mihomo,
            IConfigRepository repo,
            IProcessProbe probe,
            IAppLauncher launcher,
            IAutostartStore autostart,
            IProbeSidecar sidecar,
            IPowerMonitor powerMonitor,
            IClock clock,
            IAuditSink audit,
            RedactorService redactor,
            Func<bool, ClientEnvironment> detectEnv,
            Func<Dictionary<string, string>> scanInstalledApps,
            Func<string, string> displayNameOf,
            Action<Action<bool, string>> readSystemProxy,
            string selfExePath,
            INodeQualityService quality = null,
            IUpdateChecker updater = null)
        {
            _mihomo = mihomo;
            _repo = repo;
            _probe = probe;
            _launcher = launcher;
            _autostart = autostart;
            _sidecar = sidecar;
            _quality = quality;
            _updater = updater;
            _clock = clock;
            _audit = audit;
            _redactor = redactor;
            _detectEnv = detectEnv;
            _scanInstalledApps = scanInstalledApps;
            _displayNameOf = displayNameOf;
            _readSystemProxy = readSystemProxy;
            _selfExePath = selfExePath ?? "";

            Store = new AppStateStore();
            Coordinator = new KernelCoordinator();
            PowerGuard = new PowerResumeGuard(powerMonitor, _clock, 12);
            Companion = new CompanionOrchestrator(_probe, _launcher, _autostart, _repo, _audit, Coordinator, PowerGuard, _selfExePath);
            StaticScheduler = new StaticChannelScheduler(_repo, _mihomo, _sidecar, Coordinator, PowerGuard, _clock, _audit, Store);
            Presets = new WorkspacePresetService(_repo, Coordinator, _audit);
        }

        /// <summary>冷启动初始化：加载设置/名单/多固定IP保险箱，后台清扫残留侧车目录，启动统一受控心跳。</summary>
        public async Task InitializeAsync(string followSessionTarget = "", bool launchedViaCompanion = false)
        {
            var settings = _repo.LoadSettings();
            var rules = _repo.LoadRouteRules();
            var vault = _repo.LoadStaticVault();

            // 后台清理上次异常遗留的旁路探测临时目录
            _ = Task.Run(() => { try { _sidecar.SweepStaleTempDirs(); } catch { } });

            var rtList = BuildInitialStaticRuntimes(vault);
            Store.Update(s =>
            {
                s.Settings = settings;
                s.SavedRules = rules;
                s.StaticVault = vault;
                s.StaticRuntimes = rtList;
                s.WatcherAlive = _probe.IsWatcherRunning();
            });

            string msg;
            Companion.AttachFollowSession(followSessionTarget, settings.Companion, out msg);
            Companion.SyncWatcherRuntime(settings.Companion);
            Store.Update(s =>
            {
                s.ActiveFollowTargetExe = Companion.ActiveFollowExe;
                s.WatcherAlive = _probe.IsWatcherRunning();
            });

            await RefreshFullStateAsync(true).ConfigureAwait(false);

            // 启动统一节拍器（流量 1s / 看守与测速 10s / 联动同退 2.5s）
            _trafficTimer = new Timer(_ => { _ = PollTrafficOnceAsync(); }, null, 1000, 1000);
            _watchdogTimer = new Timer(_ => { _ = WatchdogHeartbeatAsync(); }, null, 10000, 10000);
            _companionTimer = new Timer(_ => { try { Companion.TickFollowExitCheck(); } catch { } }, null, 2500, 2500);

            if (launchedViaCompanion || (settings.Companion != null && settings.Companion.HasAnyLaunchApps))
            {
                _ = Task.Run(() => RunCompanionStartupAsync(settings.Companion, launchedViaCompanion));
            }
        }

        async Task RunCompanionStartupAsync(CompanionConfig cfg, bool launchedViaCompanion)
        {
            try
            {
                var rep = await Companion.RunBootLaunchAsync(
                    cfg,
                    launchedViaCompanion,
                    ct => Task.Run(() =>
                    {
                        var env = _detectEnv(true);
                        if (!env.Found) return false;
                        var r = _mihomo.FetchJson("/configs", 2000, env.ConfigPath);
                        return r.Ok && r.Data != null;
                    }, ct),
                    async ct =>
                    {
                        var snap = Store.GetSnapshot();
                        if (!_repo.HasSavedRoutingIntent) return true;
                        var auditRes = await Task.Run(() => AuditKernelAgainstSaved(snap.SavedRules, snap.StaticVault), ct).ConfigureAwait(false);
                        if (auditRes.Ok && GuardEvaluator.Judge(auditRes, snap.SavedRules.Count(r => r.Channel != RouteChannel.Direct), snap.Settings.Mode) == null)
                            return true;
                        var push = await ApplyRoutingToKernelAsync(snap.SavedRules, snap.Settings.Mode, KernelWritePriority.P2_CompanionSync, false).ConfigureAwait(false);
                        if (!push.Ok) return false;
                        var verify = await Task.Run(() => AuditKernelAgainstSaved(snap.SavedRules, snap.StaticVault), ct).ConfigureAwait(false);
                        return verify.Ok && GuardEvaluator.Judge(verify, snap.SavedRules.Count(r => r.Channel != RouteChannel.Direct), snap.Settings.Mode) == null;
                    },
                    _cts.Token).ConfigureAwait(false);

                if (rep.BusinessAppsBlockedByGate)
                {
                    Store.Update(s => { s.StatusMessage = rep.BlockReason; s.StatusTone = "warn"; });
                    Store.EmitToast("已暂缓拉起配套软件", rep.BlockReason, "warn");
                }
                else if (rep.StartedClients.Count + rep.StartedBusinessApps.Count > 0)
                {
                    string all = string.Join("、", rep.StartedClients.Concat(rep.StartedBusinessApps));
                    Store.EmitToast("联动已同步启动", all, "ok");
                }
            }
            catch { }
        }

        /// <summary>全量刷新客户端环境、内核状态、规则对账、代理节点目录、固定 IP 延迟与应用扫描列表。</summary>
        public async Task RefreshFullStateAsync(bool forceDetectClient = false)
        {
            await Task.Run(() =>
            {
                var env = _detectEnv(forceDetectClient);
                var settings = _repo.LoadSettings();
                var savedRules = _repo.LoadRouteRules();
                var vault = _repo.LoadStaticVault();

                var ks = new KernelSnapshot
                {
                    ClientName = env.ClientName,
                    ConfigPath = env.ConfigPath,
                    AppliedConfigRevision = Coordinator.CurrentRevision
                };
                _readSystemProxy((en, srv) => { ks.SystemProxyEnabled = en; ks.SystemProxyServer = srv; });

                var nodes = new List<ProxyNodeItem>();
                if (!env.Found)
                {
                    ks.Error = "未检测到已安装的 Clash / mihomo 系代理客户端配置文件";
                }
                else
                {
                    var cfgRes = _mihomo.FetchJson("/configs", 4000, env.ConfigPath);
                    if (!cfgRes.Ok || cfgRes.Data == null)
                    {
                        ks.Error = !string.IsNullOrEmpty(cfgRes.Error) ? cfgRes.Error : "代理客户端内核未启动";
                    }
                    else
                    {
                        ks.Connected = true;
                        ks.Mode = JsonCodec.Str(cfgRes.Data, "mode", "rule");
                        ks.FindProcessMode = JsonCodec.Str(cfgRes.Data, "find-process-mode", "");
                        ks.Ipv6Enabled = JsonCodec.Bool(cfgRes.Data, "ipv6", false);
                        var tun = JsonCodec.Obj(cfgRes.Data, "tun");
                        ks.TunEnabled = JsonCodec.Bool(tun, "enable", false);
                        var dns = JsonCodec.Obj(cfgRes.Data, "dns");
                        ks.DnsEnabled = JsonCodec.Bool(dns, "enable", false);

                        var verRes = _mihomo.FetchJson("/version", 2000, env.ConfigPath);
                        if (verRes.Ok && verRes.Data != null)
                            ks.KernelVersion = JsonCodec.Str(verRes.Data, "version", "");

                        string rawYaml = "";
                        try { rawYaml = File.ReadAllText(env.ConfigPath, Encoding.UTF8); } catch { }
                        ks.PrimaryPolicyGroup = YamlRuleEngine.ResolvePrimaryPolicyGroup(rawYaml);

                        // 读取代理节点与当前出口
                        var pxRes = _mihomo.FetchJson("/proxies", 5000, env.ConfigPath);
                        if (pxRes.Ok && pxRes.Data != null)
                        {
                            var proxies = JsonCodec.Obj(pxRes.Data, "proxies");
                            ParseProxiesSnapshot(proxies, ks, settings.CandidateNodes, nodes);
                            SyncStaticLiveHopsFromProxies(proxies, vault);
                        }

                        // 对账规则状态
                        var audit = AuditKernelAgainstSaved(savedRules, vault, env.ConfigPath);
                        ks.TotalRuleCount = audit.RuleCount;
                        string judgeWhy = GuardEvaluator.Judge(audit, savedRules.Count(r => r.Channel != RouteChannel.Direct), settings.Mode);
                        ks.RulesApplied = audit.Ok && judgeWhy == null && _repo.HasSavedRoutingIntent;
                    }
                }

                // 汇总运行进程 + 已安装软件 + 已保存名单为统一展示行
                var apps = BuildAppViewList(savedRules, settings);
                var runtimes = MergeStaticRuntimes(Store.GetSnapshot().StaticRuntimes, vault);

                Store.Update(s =>
                {
                    s.Kernel = ks;
                    s.Settings = settings;
                    s.SavedRules = savedRules;
                    s.StaticVault = vault;
                    s.StaticRuntimes = runtimes;
                    s.Apps = apps;
                    s.Nodes = nodes;
                    s.WatcherAlive = _probe.IsWatcherRunning();
                    s.ActiveFollowTargetExe = Companion.ActiveFollowExe;
                    if (ks.Connected)
                    {
                        s.StatusMessage = ks.RulesApplied
                            ? ("分流已生效 · " + savedRules.Count(r => r.Channel != RouteChannel.Direct) + " 个代理规则")
                            : "已连接 " + ks.ClientName + "（尚未保存或需同步规则）";
                        s.StatusTone = ks.RulesApplied ? "ok" : "accent";
                    }
                    else
                    {
                        s.StatusMessage = ks.Error;
                        s.StatusTone = "warn";
                    }
                });

                // 异步测一次主节点与已启用的固定 IP 实时延迟
                if (ks.Connected)
                {
                    _ = MeasurePrimaryDelayAsync();
                    _ = MeasureAllActiveStaticDelaysAsync();
                }
            }).ConfigureAwait(false);
        }

        /// <summary>保存用户编辑的应用路由表并经 P1 KernelCoordinator 原子推送到内核（仅定向断连变动软件）。</summary>
        public async Task<KernelOpResult> SaveAndApplyRulesAsync(IList<AppRouteRule> newRules, SplitMode newMode)
        {
            var settings = _repo.LoadSettings();
            settings.Mode = newMode;
            settings.RepairSuspended = false;
            string err;
            if (!_repo.SaveSettings(settings, out err))
                return new KernelOpResult { Ok = false, Message = "保存设置失败：" + err };

            var oldRules = _repo.LoadRouteRules();
            var cleanNew = newRules.Where(r => r != null && r.Channel != RouteChannel.Direct).Select(r => r.Clone()).ToList();
            if (!_repo.SaveRouteRules(cleanNew, out err))
                return new KernelOpResult { Ok = false, Message = "保存名单失败：" + err };

            Coordinator.BumpConfigRevision();
            var changedExes = ComputeChangedExes(oldRules, cleanNew);
            var res = await ApplyRoutingToKernelAsync(cleanNew, newMode, KernelWritePriority.P1_UserAction, true, changedExes).ConfigureAwait(false);
            if (res.Ok)
            {
                _audit?.Record("APPLY", "保存并推送分流成功（" + cleanNew.Count + " 条 · " + newMode + "）");
                await RefreshFullStateAsync(false).ConfigureAwait(false);
            }
            return res;
        }

        /// <summary>保存多固定 IP 通道保险箱并热推生效。</summary>
        public async Task<KernelOpResult> SaveStaticVaultAndApplyAsync(StaticChannelVault vault)
        {
            string err;
            if (!_repo.SaveStaticVault(vault, out err))
                return new KernelOpResult { Ok = false, Message = err };

            Coordinator.BumpConfigRevision();
            var snap = Store.GetSnapshot();
            Store.Update(s =>
            {
                s.StaticVault = vault.Clone();
                s.StaticRuntimes = MergeStaticRuntimes(s.StaticRuntimes, vault);
            });

            if (_repo.HasSavedRoutingIntent)
            {
                var res = await ApplyRoutingToKernelAsync(snap.SavedRules, snap.Settings.Mode, KernelWritePriority.P1_UserAction, false).ConfigureAwait(false);
                if (res.Ok) _ = MeasureAllActiveStaticDelaysAsync();
                return res;
            }
            return new KernelOpResult { Ok = true, Executed = true, Message = "固定 IP 配置已保存" };
        }

        /// <summary>保存四向联动设置：立即同步注册表与后台静默守望进程，并刷新同生共死跟踪器。</summary>
        public bool SaveCompanionConfig(CompanionConfig cfg, out string error)
        {
            var settings = _repo.LoadSettings();
            settings.Companion = cfg.Clone();
            if (!_repo.SaveSettings(settings, out error)) return false;

            Companion.SyncWatcherRuntime(settings.Companion);
            Companion.RefreshTracker(settings.Companion);
            _audit?.Record("COMPANION",
                "更新四向联动设置：跟随=" + (cfg.HasValidFollowTarget ? cfg.FollowAppExe : "关") +
                " · 守望=" + cfg.FollowWatchAtLogon +
                " · 自启=" + (cfg.HasAnyLaunchApps ? cfg.LaunchAppPaths.Count : 0) +
                " · 退出同关=" + cfg.CloseLinkedAppsOnExit);

            Store.Update(s =>
            {
                s.Settings = settings;
                s.WatcherAlive = _probe.IsWatcherRunning();
                s.ActiveFollowTargetExe = Companion.ActiveFollowExe;
            });
            return true;
        }

        /// <summary>切换主代理节点（经 P1 KernelCoordinator 串行执行）。</summary>
        public async Task<KernelOpResult> SwitchPrimaryNodeAsync(string nodeName)
        {
            var snap = Store.GetSnapshot();
            string group = snap.Kernel.PrimaryPolicyGroup;
            string cfgPath = snap.Kernel.ConfigPath;
            if (string.IsNullOrEmpty(group))
                return new KernelOpResult { Ok = false, Message = "未检测到主代理策略组" };

            var res = await Coordinator.EnqueueAsync("SwitchPrimaryNode", KernelWritePriority.P1_UserAction, _ =>
            {
                var r = _mihomo.SwitchProxyNode(group, nodeName, 5000, cfgPath);
                return new KernelOpResult { Ok = r.Ok, Message = r.Ok ? ("已切换至 " + nodeName) : r.Error };
            }).ConfigureAwait(false);

            if (res.Ok)
            {
                _audit?.Record("NODE", "手动切换主代理节点");
                await RefreshFullStateAsync(false).ConfigureAwait(false);
            }
            return res;
        }

        /// <summary>断开单条或按进程全部活跃连接（供实时连接监视器使用）。</summary>
        public async Task<int> CloseConnectionsAsync(IEnumerable<string> connectionIds)
        {
            var ids = (connectionIds ?? new string[0]).Where(x => !string.IsNullOrEmpty(x)).Distinct().ToList();
            if (ids.Count == 0) return 0;
            string cfgPath = Store.GetSnapshot().Kernel.ConfigPath;
            int closed = 0;
            await Coordinator.EnqueueAsync("CloseConnections", KernelWritePriority.P1_UserAction, _ =>
            {
                foreach (var id in ids)
                {
                    var r = _mihomo.DeleteConnection(id, 2500, cfgPath);
                    if (r.Ok) closed++;
                }
                return new KernelOpResult { Ok = true, Message = "已关闭 " + closed + " 条连接" };
            }).ConfigureAwait(false);
            return closed;
        }

        /// <summary>彻底还原内核配置（退出还原 / 托盘手动还原共用同一干净实现）。</summary>
        public Task<KernelOpResult> RestoreCleanKernelAsync(bool revokeSavedIntent)
        {
            if (revokeSavedIntent)
            {
                string _;
                _repo.DeleteRouteRules(out _);
                var st = _repo.LoadSettings();
                st.RepairSuspended = false;
                _repo.SaveSettings(st, out _);
                Coordinator.BumpConfigRevision();
            }

            return Coordinator.EnqueueAsync("RestoreCleanKernel", KernelWritePriority.P0_ShutdownRestore, ct => ExecuteRestoreCleanKernelCore());
        }

        /// <summary>退出 ProxyPicker：走四向对称退出编排器（先 P0 还原内核，再按需优雅关闭联动软件）。</summary>
        public Task<ShutdownOutcome> ShutdownAsync(bool triggeredByHostExit)
        {
            var cfg = Store.GetSnapshot().Settings.Companion;
            return Companion.ExecuteShutdownAsync(cfg, triggeredByHostExit, _ => ExecuteRestoreCleanKernelCore());
        }

        public async Task MeasurePrimaryDelayAsync()
        {
            var snap = Store.GetSnapshot();
            if (!snap.Kernel.Connected || string.IsNullOrEmpty(snap.Kernel.PrimaryOutboundNode)) return;
            string node = snap.Kernel.PrimaryOutboundNode;
            string cfg = snap.Kernel.ConfigPath;
            var r = await Task.Run(() => _mihomo.MeasureProxyDelay(node, "http://www.gstatic.com/generate_204", 4000, cfg)).ConfigureAwait(false);
            int ms = (r.Ok && r.Data != null) ? JsonCodec.Int(r.Data, "delay", -1) : -1;
            Store.Update(s =>
            {
                var k = s.Kernel;
                k.PrimaryDelayMs = ms;
                k.PrimaryTimedOut = ms <= 0;
            });
        }

        public async Task MeasureAllActiveStaticDelaysAsync()
        {
            var snap = Store.GetSnapshot();
            if (!snap.Kernel.Connected) return;
            var active = snap.StaticVault.GetActiveChannels();
            string cfg = snap.Kernel.ConfigPath;

            foreach (var ch in active)
            {
                var r = await Task.Run(() => _mihomo.MeasureProxyDelay(ch.KernelProxyName, "http://www.gstatic.com/generate_204", 8000, cfg)).ConfigureAwait(false);
                int ms = (r.Ok && r.Data != null) ? JsonCodec.Int(r.Data, "delay", -1) : -1;
                bool degraded = ms > 0 && StaticChannelAlgorithms.IsDelayDegraded(ms, ch.AutoBestDelayMs);

                Store.Update(s =>
                {
                    var rt = s.StaticRuntimes.FirstOrDefault(x => string.Equals(x.ChannelId, ch.Id, StringComparison.OrdinalIgnoreCase));
                    if (rt != null)
                    {
                        rt.DelayMs = ms;
                        rt.TimedOut = ms <= 0;
                        rt.LastCheckedUtc = _clock.UtcNow;
                        rt.TimeoutStreak = ms <= 0 ? (rt.TimeoutStreak + 1) : 0;
                        rt.SlowStreak = degraded ? (rt.SlowStreak + 1) : 0;
                    }
                });
            }
        }

        /// <summary>手动触发指定固定 IP 通道的零干扰旁路 3 次稳态优选。</summary>
        public Task<bool> OptimizeStaticChannelAsync(string channelId, Action<string> progress, CancellationToken ct)
        {
            var snap = Store.GetSnapshot();
            return StaticScheduler.OptimizeChannelAsync(
                channelId,
                snap.Kernel.ConfigPath,
                true,
                false,
                _ => ExecuteApplyRoutingCore(snap.SavedRules, snap.Settings.Mode, false, null),
                progress,
                ct);
        }

        /// <summary>并发测量当前主策略组内全部节点的延迟。</summary>
        public async Task BenchmarkAllPrimaryNodesAsync(CancellationToken ct = default(CancellationToken))
        {
            var snap = Store.GetSnapshot();
            if (!snap.Kernel.Connected || snap.Nodes.Count == 0) return;
            string cfg = snap.Kernel.ConfigPath;
            var nodes = snap.Nodes.Select(n => n.Name).ToList();
            var delays = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var gate = new object();

            using (var sem = new SemaphoreSlim(10, 10))
            {
                var tasks = nodes.Select(name => Task.Run(() =>
                {
                    if (ct.IsCancellationRequested) return;
                    sem.Wait(ct);
                    try
                    {
                        var r = _mihomo.MeasureProxyDelay(name, "http://www.gstatic.com/generate_204", 3500, cfg);
                        int ms = (r.Ok && r.Data != null) ? JsonCodec.Int(r.Data, "delay", -1) : -1;
                        lock (gate) delays[name] = ms;
                    }
                    finally { sem.Release(); }
                }, ct)).ToArray();
                try { await Task.WhenAll(tasks).ConfigureAwait(false); } catch { }
            }

            Store.Update(s =>
            {
                var updated = new List<ProxyNodeItem>(s.Nodes.Count);
                foreach (var n in s.Nodes)
                {
                    int ms;
                    bool got = delays.TryGetValue(n.Name, out ms);
                    updated.Add(new ProxyNodeItem
                    {
                        Name = n.Name,
                        Type = n.Type,
                        DelayMs = got ? ms : n.DelayMs,
                        Alive = got ? (ms > 0) : n.Alive,
                        IsCurrent = n.IsCurrent,
                        IsCandidate = n.IsCandidate,
                        LastError = got && ms <= 0 ? "超时" : ""
                    });
                }
                s.Nodes = updated;
            });
        }

        /// <summary>单节点质量、出口 IP、双源风控与 AI 四门预检（零干扰隔离侧车优先）。</summary>
        public Task<NodeQualityReport> ProbeSingleNodeQualityAsync(string nodeName, string desiredRegion, CancellationToken ct)
        {
            if (_quality == null)
                return Task.FromResult(new NodeQualityReport { TargetNode = nodeName ?? "", Error = "质量检测服务未初始化" });
            string cfg = Store.GetSnapshot().Kernel.ConfigPath;
            string target = string.IsNullOrWhiteSpace(nodeName) ? Store.GetSnapshot().Kernel.PrimaryOutboundNode : nodeName;
            return Task.Run(() => _quality.ProbeSingleNode(target, desiredRegion, cfg, ct), ct);
        }

        /// <summary>全量节点风控体检（零干扰旁路侧车 + 同轮 IP 缓存防限流）。</summary>
        public Task<List<NodeQualityReport>> ProbeAllNodesQualityAsync(
            IList<string> nodes, string desiredRegion, Action<int, int, NodeQualityReport> onProgress, CancellationToken ct)
        {
            if (_quality == null) return Task.FromResult(new List<NodeQualityReport>());
            string cfg = Store.GetSnapshot().Kernel.ConfigPath;
            var list = (nodes != null && nodes.Count > 0) ? nodes : Store.GetSnapshot().Nodes.Select(n => n.Name).ToList();
            return Task.Run(() => _quality.ProbeBatchNodes(list, desiredRegion, cfg, onProgress, ct), ct);
        }

        /// <summary>只读分析当前本机内核 TUN / DNS / 固定 IP UDP 防穿透风险并返回可操作建议。</summary>
        public DnsDiagnosticAdvice GetDnsDiagnosticAdvice()
        {
            var snap = Store.GetSnapshot();
            var k = snap.Kernel;
            var adv = new DnsDiagnosticAdvice
            {
                KernelDnsEnabled = k != null && k.DnsEnabled,
                TunEnabled = k != null && k.TunEnabled,
                StaticUdpGuardActive = snap.StaticVault != null && snap.StaticVault.GetActiveChannels().Any(c => !c.AllowUdp)
            };
            if (k == null || !k.Connected)
            {
                adv.Severity = "warn";
                adv.Summary = "未连接到代理内核，请先启动 Clash Verge / FlClash 等客户端";
                return adv;
            }
            if (!adv.TunEnabled)
            {
                adv.Severity = "warn";
                adv.Summary = "TUN 虚拟网卡模式未开启，部分桌面软件或 CLI 可能绕过按进程分流";
                adv.Recommendations.Add("在代理客户端开启 TUN 模式，使按进程规则覆盖全部应用。");
            }
            if (!adv.KernelDnsEnabled)
            {
                adv.Severity = "warn";
                adv.Summary = "客户端内置 DNS 未开启，浏览器系统 DNS 查询可能直连国内运营商";
                adv.Recommendations.Add("推荐在浏览器「隐私与安全 → 使用安全 DNS」开启 Cloudflare (1.1.1.1) DoH，立刻消除 DNS 检测告警。");
                adv.Recommendations.Add("或在代理客户端中按需启用内置 DNS；ProxyPicker 严守最小干预原则，不擅自修改客户端 DNS 策略。");
            }
            else if (adv.TunEnabled)
            {
                adv.Severity = "ok";
                adv.Summary = "TUN 模式与内置 DNS 均已开启，配合固定 IP UDP/QUIC 防穿透规则处于最佳防泄漏状态";
            }
            return adv;
        }

        /// <summary>切换内核 IPv6 开关（IPv6 防泄漏，走 P1 协调器）。</summary>
        public async Task<KernelOpResult> SetIpv6AntiLeakAsync(bool disableIpv6)
        {
            string cfg = Store.GetSnapshot().Kernel.ConfigPath;
            var res = await Coordinator.EnqueueAsync("PatchIpv6", KernelWritePriority.P1_UserAction, _ =>
            {
                var r = _mihomo.PatchConfigJson("{\"ipv6\":" + (disableIpv6 ? "false" : "true") + "}", 4000, cfg);
                return new KernelOpResult { Ok = r.Ok, Message = r.Ok ? (disableIpv6 ? "已关闭内核 IPv6（防泄漏生效）" : "已恢复内核 IPv6") : r.Error };
            }).ConfigureAwait(false);
            if (res.Ok)
            {
                SaveGeneralSettings(s => s.Ipv6GuardOn = disableIpv6);
                _audit?.Record("NETWORK", res.Message);
                await RefreshFullStateAsync(false).ConfigureAwait(false);
            }
            return res;
        }

        /// <summary>原子修改常规偏好设置并同步至状态仓库与开机自启。</summary>
        public bool SaveGeneralSettings(Action<AppSettingsData> mutator, out string error)
        {
            var settings = _repo.LoadSettings();
            mutator?.Invoke(settings);
            if (!_repo.SaveSettings(settings, out error)) return false;
            Store.Update(s => { s.Settings = settings; });
            return true;
        }

        public bool SaveGeneralSettings(Action<AppSettingsData> mutator)
        {
            string _;
            return SaveGeneralSettings(mutator, out _);
        }

        public bool IsMainAutostartEnabled() => _autostart != null && _autostart.IsMainAutostartEnabled(_selfExePath);

        public bool SetMainAutostart(bool enable)
        {
            if (_autostart == null) return false;
            bool ok = _autostart.SetMainAutostart(enable, _selfExePath);
            if (ok) _audit?.Record("SYSTEM", enable ? "已开启 ProxyPicker 开机自启" : "已关闭 ProxyPicker 开机自启");
            return ok;
        }

        public async Task<bool> SaveCandidateNodesAsync(IList<string> candidates)
        {
            string _;
            bool ok = SaveGeneralSettings(s =>
            {
                s.CandidateNodes = (candidates ?? new string[0]).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            }, out _);
            if (ok) await RefreshFullStateAsync(false).ConfigureAwait(false);
            return ok;
        }

        public async Task<bool> AddCustomAppAsync(string exeOrPath)
        {
            string exe = YamlRuleEngine.NormalizeExe(exeOrPath);
            if (exe.Length == 0) return false;
            SaveGeneralSettings(s =>
            {
                s.HiddenApps.RemoveAll(x => string.Equals(x, exe, StringComparison.OrdinalIgnoreCase));
                if (!s.CustomApps.Any(x => string.Equals(x, exe, StringComparison.OrdinalIgnoreCase)))
                    s.CustomApps.Add(exe);
            });
            await RefreshFullStateAsync(false).ConfigureAwait(false);
            return true;
        }

        /// <summary>保存并切换命名场景预设，并立即热推到代理内核。</summary>
        public async Task<KernelOpResult> ApplyNamedPresetAsync(string presetId)
        {
            var push = await Presets.ActivatePresetAsync(
                presetId,
                (mode, rules, ct) => ExecuteApplyRoutingCore(rules, mode, false, null)).ConfigureAwait(false);
            if (push.Ok) await RefreshFullStateAsync(false).ConfigureAwait(false);
            return push;
        }

        public Task<UpdateCheckResult> CheckForUpdateAsync()
        {
            if (_updater == null)
                return Task.FromResult(new UpdateCheckResult { CurrentVersion = ProxyPicker.Domain.ProductInfo.Version, Error = "更新检查未配置" });
            return Task.Run(() => _updater.CheckLatest(6000));
        }

        public List<AuditLogEntry> GetRecentAuditLogs(int maxCount = 250)
        {
            return _audit != null ? _audit.ReadRecent(maxCount) : new List<AuditLogEntry>();
        }

        public string ExportEncryptedConfig(string passphrase, bool includeStaticCredentials)
        {
            string json = _repo.ExportEncryptedBundle(passphrase, includeStaticCredentials);
            _audit?.Record("CONFIG", "导出配置迁移包（含固定IP加密=" + includeStaticCredentials + "）");
            return json;
        }

        public async Task<Tuple<bool, string>> ImportEncryptedConfigAsync(string bundleJson, string passphrase)
        {
            string summary, err;
            if (!_repo.ImportEncryptedBundle(bundleJson, passphrase, out summary, out err))
                return Tuple.Create(false, err);
            Coordinator.BumpConfigRevision();
            _audit?.Record("CONFIG", "导入配置迁移包：" + summary);
            var settings = _repo.LoadSettings();
            Companion.SyncWatcherRuntime(settings.Companion);
            await RefreshFullStateAsync(true).ConfigureAwait(false);
            return Tuple.Create(true, summary);
        }

        /// <summary>生成全量脱敏诊断报告（供 --diag 与 UI 体检页一键复制共用）。</summary>
        public string BuildSanitizedDiagnosticReport()
        {
            var snap = Store.GetSnapshot();
            var dns = GetDnsDiagnosticAdvice();
            var sb = new StringBuilder();
            sb.AppendLine("=== " + ProxyPicker.Domain.ProductInfo.Name + " v" + ProxyPicker.Domain.ProductInfo.Version + " 脱敏诊断报告 ===");
            sb.AppendLine("时间: " + _clock.LocalNow.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("客户端: " + (string.IsNullOrEmpty(snap.Kernel.ClientName) ? "未检测到" : snap.Kernel.ClientName));
            sb.AppendLine("内核连接: " + (snap.Kernel.Connected ? ("正常 (" + snap.Kernel.KernelVersion + ")") : ("未连接 - " + snap.Kernel.Error)));
            sb.AppendLine("运行模式: " + snap.Kernel.Mode + " | TUN=" + snap.Kernel.TunEnabled + " | 内置DNS=" + snap.Kernel.DnsEnabled + " | IPv6=" + snap.Kernel.Ipv6Enabled);
            sb.AppendLine("主策略组: " + snap.Kernel.PrimaryPolicyGroup + " | 当前出口延迟: " + (snap.Kernel.PrimaryDelayMs > 0 ? (snap.Kernel.PrimaryDelayMs + "ms") : "未测/超时"));
            sb.AppendLine("分流状态: " + (snap.Kernel.RulesApplied ? "已生效" : "未生效") + " | 规则数=" + snap.Kernel.TotalRuleCount + " | 本地账本=" + snap.SavedRules.Count);
            sb.AppendLine("固定IP通道数: " + snap.StaticVault.Channels.Count + "（启用 " + snap.StaticVault.GetActiveChannels().Count + " 个）");
            sb.AppendLine("四向联动: 跟随=" + (snap.Settings.Companion.HasValidFollowTarget ? snap.Settings.Companion.FollowAppExe : "关") +
                          " | 守望在线=" + snap.WatcherAlive + " | 退出同关=" + snap.Settings.Companion.CloseLinkedAppsOnExit);
            sb.AppendLine("DNS体检: [" + dns.Severity + "] " + dns.Summary);
            sb.AppendLine("协调器修订号: rev=" + Coordinator.CurrentRevision + " | 唤醒宽限中=" + PowerGuard.IsInGraceWindow(out _));
            return _redactor != null ? _redactor.Apply(sb.ToString()) : sb.ToString();
        }

        // ---- 后台定时心跳（流量 1s / 看守+固定IP巡检 10s） ----

        async Task PollTrafficOnceAsync()
        {
            if (_disposed || Coordinator.IsShuttingDown) return;
            if (Interlocked.CompareExchange(ref _trafficTickBusy, 1, 0) != 0) return;
            try
            {
                var snap = Store.GetSnapshot();
                if (!snap.Kernel.Connected) return;
                var r = await Task.Run(() => _mihomo.FetchJson("/connections", 2500, snap.Kernel.ConfigPath)).ConfigureAwait(false);
                if (!r.Ok || r.Data == null) return;
                var traffic = _trafficAgg.Aggregate(
                    r.Data, _clock.UtcNow, snap.RulesAppliedAtUtc,
                    snap.Settings.Mode, snap.SavedRules, snap.StaticVault, true);
                Store.PublishTraffic(traffic);
            }
            catch { }
            finally { Interlocked.Exchange(ref _trafficTickBusy, 0); }
        }

        async Task WatchdogHeartbeatAsync()
        {
            if (_disposed || Coordinator.IsShuttingDown) return;
            double graceRem;
            if (PowerGuard.IsInGraceWindow(out graceRem)) return; // 休眠唤醒宽限期内绝对静默！
            if (Interlocked.CompareExchange(ref _watchdogTickBusy, 1, 0) != 0) return;
            try
            {
                int tick = Interlocked.Increment(ref _watchdogCycle);
                var snap = Store.GetSnapshot();

                // 每 30 秒做一次内核规则对账与无感补回
                if (tick % 3 == 0 && _repo.HasSavedRoutingIntent && !snap.Settings.RepairSuspended)
                {
                    var audit = AuditKernelAgainstSaved(snap.SavedRules, snap.StaticVault, snap.Kernel.ConfigPath);
                    if (audit.Ok)
                    {
                        bool modeDrifted = !string.IsNullOrEmpty(audit.Mode) && !audit.Mode.Equals("rule", StringComparison.OrdinalIgnoreCase);
                        if (!modeDrifted)
                        {
                            string why = GuardEvaluator.Judge(audit, snap.SavedRules.Count(r => r.Channel != RouteChannel.Direct), snap.Settings.Mode);
                            if (why != null)
                            {
                                _audit?.Record("WATCH", "检测到规则漂移，自动补回：" + why);
                                await ApplyRoutingToKernelAsync(snap.SavedRules, snap.Settings.Mode, KernelWritePriority.P3_AutoRepairOrFailover, false).ConfigureAwait(false);
                            }
                        }
                    }
                }

                // 每 40 秒测一次主节点与各固定 IP 通道实时延迟
                if (tick % 4 == 0 && snap.Kernel.Connected)
                {
                    await MeasurePrimaryDelayAsync().ConfigureAwait(false);
                    await MeasureAllActiveStaticDelaysAsync().ConfigureAwait(false);
                }

                // 检查各开启 Auto 模式的固定 IP 通道是否需要 10 分钟定时稳速刷新或劣化/超时提前自愈
                if (snap.Kernel.Connected)
                {
                    var latestSnap = Store.GetSnapshot();
                    foreach (var ch in latestSnap.StaticVault.GetActiveChannels())
                    {
                        if (ch.HopMode != StaticHopMode.Auto) continue;
                        var rt = latestSnap.StaticRuntimes.FirstOrDefault(x => string.Equals(x.ChannelId, ch.Id, StringComparison.OrdinalIgnoreCase));
                        if (StaticChannelAlgorithms.ShouldRunScheduledRefresh(
                                true,
                                StaticScheduler.IsOptimizing(ch.Id),
                                rt != null ? rt.LastOptimizedUtc : DateTime.MinValue,
                                ch.AutoBestUpdatedAtTicks,
                                _clock.UtcNow,
                                rt != null ? rt.SlowStreak : 0,
                                rt != null && rt.TimeoutStreak >= 2))
                        {
                            _ = StaticScheduler.OptimizeChannelAsync(
                                ch.Id, latestSnap.Kernel.ConfigPath, false,
                                rt != null && rt.TimeoutStreak >= 2,
                                ct => ExecuteApplyRoutingCore(latestSnap.SavedRules, latestSnap.Settings.Mode, false, null),
                                null, _cts.Token);
                        }
                    }
                }
            }
            catch { }
            finally { Interlocked.Exchange(ref _watchdogTickBusy, 0); }
        }

        // ---- 内核核心读写操作（全部在 Coordinator 串行上下文中执行） ----

        Task<KernelOpResult> ApplyRoutingToKernelAsync(
            IList<AppRouteRule> rules, SplitMode mode, KernelWritePriority priority,
            bool reconnectChanged, HashSet<string> changedExes = null)
        {
            long? expRev = priority >= KernelWritePriority.P3_AutoRepairOrFailover
                ? (long?)Coordinator.CurrentRevision
                : null;
            return Coordinator.EnqueueAsync("ApplyRouting:" + priority, priority,
                ct => ExecuteApplyRoutingCore(rules, mode, reconnectChanged, changedExes), expRev);
        }

        KernelOpResult ExecuteApplyRoutingCore(
            IList<AppRouteRule> rules, SplitMode mode, bool reconnectChanged, HashSet<string> changedExes)
        {
            var env = _detectEnv(false);
            if (!env.Found || !File.Exists(env.ConfigPath))
                return new KernelOpResult { Ok = false, Message = "找不到代理客户端配置文件" };

            // 先快照待定向重连的旧连接 ID（不伤及未改动软件的长连接）
            var oldConnIds = new List<string>();
            if (reconnectChanged && changedExes != null && changedExes.Count > 0)
            {
                var cRes = _mihomo.FetchJson("/connections", 4000, env.ConfigPath);
                var arr = JsonCodec.Arr(cRes.Data, "connections");
                if (arr != null)
                {
                    foreach (var it in arr)
                    {
                        var o = JsonCodec.AsObject(it);
                        var m = JsonCodec.Obj(o, "metadata");
                        string proc = YamlRuleEngine.NormalizeExe(JsonCodec.Str(m, "process"));
                        if (changedExes.Contains(proc))
                        {
                            string cid = JsonCodec.Str(o, "id");
                            if (!string.IsNullOrEmpty(cid)) oldConnIds.Add(cid);
                        }
                    }
                }
            }

            string rawYaml = File.ReadAllText(env.ConfigPath, Encoding.UTF8);
            string policy = YamlRuleEngine.ResolvePrimaryPolicyGroup(rawYaml);
            var vault = _repo.LoadStaticVault();

            string payload = YamlRuleEngine.CompilePayload(rawYaml, rules, mode, policy, vault, "always", !_andRulesRejected);
            var put = _mihomo.PutConfigPayload(payload, 12000, env.ConfigPath);
            if (!put.Ok && !_andRulesRejected && put.Status == 400 && (put.Body ?? "").IndexOf("AND", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                _andRulesRejected = true;
                payload = YamlRuleEngine.CompilePayload(rawYaml, rules, mode, policy, vault, "always", false);
                put = _mihomo.PutConfigPayload(payload, 12000, env.ConfigPath);
            }
            if (!put.Ok)
                return new KernelOpResult { Ok = false, Message = "推送内核失败：" + (!string.IsNullOrEmpty(put.Error) ? put.Error : put.Body) };

            // 定向关闭变动软件的旧连接使新分流立即生效
            foreach (var id in oldConnIds)
                _mihomo.DeleteConnection(id, 1500, env.ConfigPath);

            Store.Update(s => { s.RulesAppliedAtUtc = _clock.UtcNow; });
            return new KernelOpResult { Ok = true, Message = "分流规则已推送生效" };
        }

        KernelOpResult ExecuteRestoreCleanKernelCore()
        {
            var env = _detectEnv(false);
            if (!env.Found || !File.Exists(env.ConfigPath))
                return new KernelOpResult { Ok = true, Message = "未找到客户端配置，无需还原" };

            var offline = _mihomo.CheckControlOffline(env.ConfigPath);
            if (offline.OfflineConfirmed)
                return new KernelOpResult { Ok = true, Message = "代理内核未运行（内存规则已随客户端关闭清除）" };

            string rawYaml = File.ReadAllText(env.ConfigPath, Encoding.UTF8);
            string origFpm = _repo.LoadSettings().FindProcessOriginal;
            string cleanPayload = YamlRuleEngine.BuildCleanRollback(rawYaml, origFpm);
            var put = _mihomo.PutConfigPayload(cleanPayload, 12000, env.ConfigPath);
            if (put.Ok)
                return new KernelOpResult { Ok = true, Message = "已干净还原内核配置（规则块与托管固定 IP 块已移除）" };
            return new KernelOpResult { Ok = false, Message = "还原推送失败：" + put.Error };
        }

        GuardAuditResult AuditKernelAgainstSaved(IList<AppRouteRule> savedRules, StaticChannelVault vault, string cfgPath = null)
        {
            var env = string.IsNullOrEmpty(cfgPath) ? _detectEnv(false) : new ClientEnvironment { ConfigPath = cfgPath };
            if (string.IsNullOrEmpty(env.ConfigPath)) return new GuardAuditResult { Ok = false, Error = "无配置文件" };

            var cfgRes = _mihomo.FetchJson("/configs", 4000, env.ConfigPath);
            if (!cfgRes.Ok || cfgRes.Data == null) return new GuardAuditResult { Ok = false, Error = cfgRes.Error };

            var rulesRes = _mihomo.FetchJson("/rules", 5000, env.ConfigPath);
            if (!rulesRes.Ok || rulesRes.Data == null) return new GuardAuditResult { Ok = false, Error = rulesRes.Error };

            var list = new List<KernelRuleInfo>();
            var arr = JsonCodec.Arr(rulesRes.Data, "rules");
            if (arr != null)
            {
                foreach (var it in arr)
                {
                    var o = JsonCodec.AsObject(it);
                    if (o == null) continue;
                    list.Add(new KernelRuleInfo
                    {
                        Type = JsonCodec.Str(o, "type"),
                        Payload = JsonCodec.Str(o, "payload"),
                        Proxy = JsonCodec.Str(o, "proxy")
                    });
                }
            }
            var meta = new GuardAuditResult
            {
                Mode = JsonCodec.Str(cfgRes.Data, "mode"),
                FindProcessMode = JsonCodec.Str(cfgRes.Data, "find-process-mode"),
                TunEnabled = JsonCodec.Bool(JsonCodec.Obj(cfgRes.Data, "tun"), "enable", false),
                Ipv6Enabled = JsonCodec.Bool(cfgRes.Data, "ipv6", false),
                DnsEnabled = JsonCodec.Bool(JsonCodec.Obj(cfgRes.Data, "dns"), "enable", false)
            };
            return GuardEvaluator.EvaluateRules(list, savedRules, vault, !_andRulesRejected, meta);
        }

        void ParseProxiesSnapshot(Dictionary<string, object> proxies, KernelSnapshot ks, List<string> candidates, List<ProxyNodeItem> outNodes)
        {
            if (proxies == null) return;
            string groupKey = ks.Mode.Equals("global", StringComparison.OrdinalIgnoreCase) ? "GLOBAL" : ks.PrimaryPolicyGroup;
            var candSet = new HashSet<string>(candidates ?? new List<string>(), StringComparer.OrdinalIgnoreCase);

            // 解析当前出口链
            var chain = new List<string>();
            string cur = groupKey;
            for (int depth = 0; depth < 8 && !string.IsNullOrEmpty(cur); depth++)
            {
                chain.Add(cur);
                var obj = JsonCodec.Obj(proxies, cur);
                string next = JsonCodec.Str(obj, "now", "");
                if (string.IsNullOrEmpty(next) || next.Equals(cur, StringComparison.OrdinalIgnoreCase)) break;
                cur = next;
            }
            ks.PrimaryOutboundNode = cur ?? "";
            ks.PrimaryChain = chain.Count > 0 ? string.Join(" → ", chain) : "";

            // 展开策略组成员节点
            var gObj = JsonCodec.Obj(proxies, ks.PrimaryPolicyGroup);
            var allArr = JsonCodec.Arr(gObj, "all");
            if (allArr != null)
            {
                foreach (var raw in allArr)
                {
                    string name = raw != null ? raw.ToString() : "";
                    if (string.IsNullOrEmpty(name) || YamlRuleEngine.IsManagedStaticName(name) || YamlRuleEngine.IsInformationalNodeName(name))
                        continue;
                    var pObj = JsonCodec.Obj(proxies, name);
                    string pType = JsonCodec.Str(pObj, "type", "");
                    if (pType.Equals("Selector", StringComparison.OrdinalIgnoreCase) ||
                        pType.Equals("URLTest", StringComparison.OrdinalIgnoreCase) ||
                        pType.Equals("Fallback", StringComparison.OrdinalIgnoreCase) ||
                        pType.Equals("Direct", StringComparison.OrdinalIgnoreCase) ||
                        pType.Equals("Reject", StringComparison.OrdinalIgnoreCase))
                        continue;

                    int histDelay = -1;
                    var hist = JsonCodec.Arr(pObj, "history");
                    if (hist != null && hist.Count > 0)
                        histDelay = JsonCodec.Int(JsonCodec.AsObject(hist[hist.Count - 1]), "delay", -1);

                    outNodes.Add(new ProxyNodeItem
                    {
                        Name = name,
                        Type = pType,
                        DelayMs = histDelay,
                        Alive = JsonCodec.Bool(pObj, "alive", histDelay > 0),
                        IsCurrent = name.Equals(ks.PrimaryOutboundNode, StringComparison.OrdinalIgnoreCase),
                        IsCandidate = candSet.Contains(name)
                    });
                }
            }
        }

        void SyncStaticLiveHopsFromProxies(Dictionary<string, object> proxies, StaticChannelVault vault)
        {
            if (proxies == null || vault == null) return;
            var liveMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var ch in vault.Channels)
            {
                if (ch == null) continue;
                var grp = JsonCodec.Obj(proxies, ch.KernelHopGroupName);
                string nowHop = JsonCodec.Str(grp, "now", "");
                if (!string.IsNullOrEmpty(nowHop)) liveMap[ch.Id] = nowHop;
            }
            if (liveMap.Count == 0) return;
            Store.Update(s =>
            {
                foreach (var rt in s.StaticRuntimes)
                {
                    string hop;
                    if (liveMap.TryGetValue(rt.ChannelId, out hop)) rt.LiveHopNode = hop;
                }
            });
        }

        List<AppViewItem> BuildAppViewList(IList<AppRouteRule> savedRules, AppSettingsData settings)
        {
            var byExe = new Dictionary<string, AppViewItem>(StringComparer.OrdinalIgnoreCase);
            var running = _probe.ScanRunningProcesses();
            var installed = _scanInstalledApps();
            var hidden = new HashSet<string>(settings.HiddenApps ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            var custom = new HashSet<string>(settings.CustomApps ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            var connCounts = Store.GetSnapshot().Traffic.ActiveConnByProcess;

            Action<string, string, bool, int, List<string>> merge = (exeRaw, path, isRun, cnt, peers) =>
            {
                string exe = YamlRuleEngine.NormalizeExe(exeRaw);
                if (exe.Length == 0 || hidden.Contains(exe) || exe == "proxypicker.exe") return;
                AppViewItem row;
                if (!byExe.TryGetValue(exe, out row))
                {
                    row = new AppViewItem
                    {
                        Exe = exe,
                        DisplayName = _displayNameOf(exe),
                        FullPath = path ?? "",
                        IsCustomAdded = custom.Contains(exe),
                        IsProxyClient = CompanionPolicyEngine.IsProxyClientExe(exe)
                    };
                    byExe[exe] = row;
                }
                if (isRun) { row.Running = true; row.ProcessCount = Math.Max(row.ProcessCount, cnt); }
                if (string.IsNullOrEmpty(row.FullPath) && !string.IsNullOrEmpty(path)) row.FullPath = path;
                if (peers != null && peers.Count > 0) row.PeerExes = new List<string>(peers);
                int cc;
                if (connCounts != null && connCounts.TryGetValue(exe, out cc)) row.ActiveConnections = cc;
            };

            foreach (var kv in running) merge(kv.Key, kv.Value.FullPath, true, kv.Value.Count, kv.Value.DirPeers);
            foreach (var kv in installed) merge(kv.Key, kv.Value, false, 0, null);
            foreach (var c in custom) merge(c, "", false, 0, null);
            foreach (var r in savedRules)
            {
                if (r == null || r.IsPathRule) continue;
                merge(r.Exe, "", false, 0, null);
                AppViewItem row;
                if (byExe.TryGetValue(YamlRuleEngine.NormalizeExe(r.Exe), out row))
                {
                    row.Channel = r.Channel;
                    row.StaticChannelId = r.StaticChannelId;
                }
            }

            var list = byExe.Values.ToList();
            list.Sort((a, b) =>
            {
                int cmp = (b.Channel != RouteChannel.Direct).CompareTo(a.Channel != RouteChannel.Direct);
                if (cmp != 0) return cmp;
                cmp = b.Running.CompareTo(a.Running);
                if (cmp != 0) return cmp;
                cmp = b.ActiveConnections.CompareTo(a.ActiveConnections);
                if (cmp != 0) return cmp;
                return string.Compare(a.DisplayName, b.DisplayName, StringComparison.CurrentCultureIgnoreCase);
            });
            return list;
        }

        static List<StaticChannelRuntime> BuildInitialStaticRuntimes(StaticChannelVault vault)
        {
            var list = new List<StaticChannelRuntime>();
            if (vault == null || vault.Channels == null) return list;
            foreach (var ch in vault.Channels)
            {
                if (ch == null) continue;
                list.Add(new StaticChannelRuntime
                {
                    ChannelId = ch.Id,
                    ChannelName = ch.Name,
                    Enabled = ch.Enabled,
                    HopMode = ch.HopMode,
                    LiveHopNode = ch.HopMode == StaticHopMode.Pin ? ch.PinnedHopNode : ch.AutoBestHopNode,
                    DelayMs = ch.AutoBestDelayMs,
                    UdpBlocked = !ch.AllowUdp
                });
            }
            return list;
        }

        static List<StaticChannelRuntime> MergeStaticRuntimes(List<StaticChannelRuntime> prev, StaticChannelVault vault)
        {
            var map = (prev ?? new List<StaticChannelRuntime>()).ToDictionary(x => x.ChannelId, StringComparer.OrdinalIgnoreCase);
            var next = new List<StaticChannelRuntime>();
            foreach (var ch in vault.Channels)
            {
                StaticChannelRuntime old;
                if (map.TryGetValue(ch.Id, out old))
                {
                    old.ChannelName = ch.Name;
                    old.Enabled = ch.Enabled;
                    old.HopMode = ch.HopMode;
                    old.UdpBlocked = !ch.AllowUdp;
                    next.Add(old);
                }
                else
                {
                    next.Add(new StaticChannelRuntime
                    {
                        ChannelId = ch.Id,
                        ChannelName = ch.Name,
                        Enabled = ch.Enabled,
                        HopMode = ch.HopMode,
                        LiveHopNode = ch.HopMode == StaticHopMode.Pin ? ch.PinnedHopNode : ch.AutoBestHopNode,
                        DelayMs = ch.AutoBestDelayMs,
                        UdpBlocked = !ch.AllowUdp
                    });
                }
            }
            return next;
        }

        static HashSet<string> ComputeChangedExes(IList<AppRouteRule> oldRules, IList<AppRouteRule> newRules)
        {
            var oldMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in oldRules ?? new List<AppRouteRule>())
                if (r != null && !r.IsPathRule) oldMap[YamlRuleEngine.NormalizeExe(r.Exe)] = r.Channel + ":" + r.StaticChannelId;
            var newMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in newRules ?? new List<AppRouteRule>())
                if (r != null && !r.IsPathRule) newMap[YamlRuleEngine.NormalizeExe(r.Exe)] = r.Channel + ":" + r.StaticChannelId;

            var changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var k in oldMap.Keys.Union(newMap.Keys, StringComparer.OrdinalIgnoreCase))
            {
                string ov, nv;
                oldMap.TryGetValue(k, out ov);
                newMap.TryGetValue(k, out nv);
                if (!string.Equals(ov ?? "", nv ?? "", StringComparison.OrdinalIgnoreCase))
                    changed.Add(k);
            }
            return changed;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _cts.Cancel();
            try { _trafficTimer?.Dispose(); } catch { }
            try { _watchdogTimer?.Dispose(); } catch { }
            try { _companionTimer?.Dispose(); } catch { }
            try { PowerGuard.Dispose(); } catch { }
            try { Coordinator.Dispose(); } catch { }
            try { _cts.Dispose(); } catch { }
        }
    }
}
