using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ProxyPicker.Domain.Interfaces;
using ProxyPicker.Domain.Models;
using ProxyPicker.Domain.Services;

namespace ProxyPicker.Application
{
    /// <summary>单次四向联动启动编排结果。</summary>
    public sealed class CompanionBootReport
    {
        public List<string> StartedClients { get; } = new List<string>();
        public List<string> StartedBusinessApps { get; } = new List<string>();
        public bool KernelReady { get; set; }
        public bool RulesSynced { get; set; }
        public bool BusinessAppsBlockedByGate { get; set; }
        public string BlockReason { get; set; } = "";
    }

    /// <summary>四向对称退出执行报告。</summary>
    public sealed class ShutdownOutcome
    {
        public bool AlreadyInProgress { get; set; }
        public bool KernelRestored { get; set; }
        public string RestoreDetail { get; set; } = "";
        public List<string> ClosedLinkedApps { get; } = new List<string>();
        public List<string> FailedCloseApps { get; } = new List<string>();
    }

    /// <summary>
    /// v3.0 真正的四向对称软件联动编排机：
    /// 方向 1：打开目标软件 → 静默守望自动拉起 ProxyPicker 并推送生效规则（勾选跟随默认自动开启守望，保存即生效）；
    /// 方向 2：打开 ProxyPicker → 代理客户端优先拉起 → 内核在线 + 规则实测一致强门禁 → 再拉起浏览器等业务软件；
    /// 方向 3：目标软件关闭 → 三态精确路径防抖确认 → 静默还原内核并退出 ProxyPicker；
    /// 方向 4：主动退出 ProxyPicker → 永远先经 P0 KernelCoordinator 还原内核，再按「业务软件→代理客户端」顺序优雅关闭联动软件！
    /// </summary>
    public sealed class CompanionOrchestrator
    {
        readonly IProcessProbe _probe;
        readonly IAppLauncher _launcher;
        readonly IAutostartStore _autostart;
        readonly IConfigRepository _repo;
        readonly IAuditSink _audit;
        readonly KernelCoordinator _coordinator;
        readonly PowerResumeGuard _powerGuard;
        readonly string _selfExePath;

        CompanionPolicyEngine.FollowExitTracker _tracker;
        string _sessionFollowTarget = "";
        int _shutdownLatch;

        public string ActiveFollowExe => _tracker != null ? _tracker.TargetExe : "";
        public event Action HostClosedAutoExitTriggered;

        public CompanionOrchestrator(
            IProcessProbe probe,
            IAppLauncher launcher,
            IAutostartStore autostart,
            IConfigRepository repo,
            IAuditSink audit,
            KernelCoordinator coordinator,
            PowerResumeGuard powerGuard,
            string selfExePath)
        {
            _probe = probe;
            _launcher = launcher;
            _autostart = autostart;
            _repo = repo;
            _audit = audit;
            _coordinator = coordinator;
            _powerGuard = powerGuard;
            _selfExePath = selfExePath;
        }

        /// <summary>同步静默守望进程的开机注册表与实时后台进程状态（保存设置后立即生效，无需重启电脑）。</summary>
        public void SyncWatcherRuntime(CompanionConfig cfg)
        {
            bool needWatch = cfg != null && cfg.HasValidFollowTarget && cfg.FollowWatchAtLogon;
            _autostart.SetWatcherAutostart(needWatch, _selfExePath);
            if (needWatch)
            {
                if (_probe.IsWatcherRunning())
                {
                    _autostart.SignalReloadWatcher();
                }
                else if (!string.IsNullOrEmpty(_selfExePath))
                {
                    bool already; string err;
                    _launcher.TryLaunch(_selfExePath, "--watch-companion", false, out already, out err);
                }
            }
            else
            {
                _autostart.SignalStopWatcher();
            }
        }

        /// <summary>配置或热接管同生共死目标（支持主实例在线时接收第二实例 --follow 交接）。</summary>
        public bool AttachFollowSession(string targetPathOrExe, CompanionConfig cfg, out string statusMessage)
        {
            statusMessage = "";
            string clean = (targetPathOrExe ?? "").Trim().Trim('"');
            string exe = CompanionPolicyEngine.ExtractExeName(clean);
            if (exe.Length == 0)
            {
                RefreshTracker(cfg);
                return true;
            }
            if (_tracker != null && !string.Equals(_tracker.TargetExe, exe, StringComparison.OrdinalIgnoreCase))
            {
                statusMessage = "当前已在跟随 " + _tracker.TargetExe + "，未覆盖为 " + exe;
                return false;
            }
            _sessionFollowTarget = clean;
            RefreshTracker(cfg);
            statusMessage = "已进入联动跟随：" + exe;
            return true;
        }

        public void RefreshTracker(CompanionConfig cfg)
        {
            string target = "";
            bool enable = false;
            if (!string.IsNullOrEmpty(_sessionFollowTarget))
            {
                target = _sessionFollowTarget;
                enable = cfg == null || cfg.FollowAutoExit;
            }
            else if (cfg != null && cfg.HasValidFollowTarget && cfg.FollowAutoExit)
            {
                target = !string.IsNullOrWhiteSpace(cfg.FollowAppPath) ? cfg.FollowAppPath : cfg.FollowAppExe;
                enable = true;
            }
            if (enable && !string.IsNullOrWhiteSpace(target))
            {
                int threshold = CompanionPolicyEngine.RecommendedMissThreshold(target);
                if (_tracker == null || !string.Equals(_tracker.TargetPath, target, StringComparison.OrdinalIgnoreCase) || _tracker.MissThreshold != threshold)
                    _tracker = new CompanionPolicyEngine.FollowExitTracker(target, threshold);
            }
            else
            {
                _tracker = null;
            }
        }

        /// <summary>每 2.5 秒由定时器调用：检测跟随目标是否已彻底退出（休眠宽限期内暂停判定防止误退）。</summary>
        public bool TickFollowExitCheck()
        {
            if (_tracker == null || Volatile.Read(ref _shutdownLatch) != 0) return false;
            double rem;
            if (_powerGuard != null && _powerGuard.IsInGraceWindow(out rem)) return false;

            string query = !string.IsNullOrEmpty(_tracker.TargetPath) ? _tracker.TargetPath : _tracker.TargetExe;
            var state = _probe.ProbeTarget(query, true);
            if (_tracker.Observe(state))
            {
                _audit?.Record("COMPANION", "检测到跟随目标已关闭（" + _tracker.TargetExe + "），触发同生共死自动还原退出");
                HostClosedAutoExitTriggered?.Invoke();
                return true;
            }
            return false;
        }

        /// <summary>执行方向 2 启动序列：代理客户端优先 → 等内核就绪 + 实测规则一致强门禁 → 拉起业务软件。</summary>
        public async Task<CompanionBootReport> RunBootLaunchAsync(
            CompanionConfig cfg,
            bool launchedViaCompanion,
            Func<CancellationToken, Task<bool>> probeKernelReadyAsync,
            Func<CancellationToken, Task<bool>> syncAndVerifyRulesAsync,
            CancellationToken ct)
        {
            var report = new CompanionBootReport();
            if (cfg == null) return report;

            bool shouldLaunch = cfg.HasAnyLaunchApps && (!launchedViaCompanion || cfg.LaunchAppsOnFollow);
            var ordered = shouldLaunch
                ? CompanionPolicyEngine.OrderLaunchTargets(cfg.LaunchAppPaths, ActiveFollowExe)
                : new List<string>();
            var clients = ordered.Where(CompanionPolicyEngine.IsProxyClientExe).ToList();
            var others = ordered.Where(p => !CompanionPolicyEngine.IsProxyClientExe(p)).ToList();

            foreach (var cPath in clients)
            {
                bool already; string err;
                if (_launcher.TryLaunch(cPath, "", true, out already, out err) && !already)
                    report.StartedClients.Add(CompanionPolicyEngine.ExtractExeName(cPath));
            }

            bool hasIntent = _repo.HasSavedRoutingIntent;
            bool repairSuspended = _repo.LoadSettings().RepairSuspended;
            bool needSync = launchedViaCompanion || report.StartedClients.Count > 0 || others.Count > 0;

            if (needSync && hasIntent && !repairSuspended)
            {
                for (int i = 0; i < 20 && !ct.IsCancellationRequested; i++)
                {
                    if (await probeKernelReadyAsync(ct).ConfigureAwait(false))
                    {
                        report.KernelReady = true;
                        break;
                    }
                    await Task.Delay(500, ct).ConfigureAwait(false);
                }

                if (report.KernelReady && !ct.IsCancellationRequested)
                {
                    report.RulesSynced = await syncAndVerifyRulesAsync(ct).ConfigureAwait(false);
                }
            }

            if (others.Count > 0 && !ct.IsCancellationRequested)
            {
                string skipReason;
                if (CompanionPolicyEngine.ShouldLaunchBusinessAppsAfterSync(
                        hasIntent, repairSuspended, report.KernelReady, report.RulesSynced, out skipReason))
                {
                    foreach (var oPath in others)
                    {
                        bool already; string err;
                        if (_launcher.TryLaunch(oPath, "", true, out already, out err) && !already)
                            report.StartedBusinessApps.Add(CompanionPolicyEngine.ExtractExeName(oPath));
                    }
                }
                else
                {
                    report.BusinessAppsBlockedByGate = true;
                    report.BlockReason = skipReason;
                    _audit?.Record("COMPANION", "启动强门禁拦截业务软件：" + skipReason);
                }
            }
            return report;
        }

        /// <summary>
        /// 单一防重入四向退出编排器：
        /// 1) 原子占位防二次重入；
        /// 2) 通知守望器本轮人工退出抑制；
        /// 3) 先以 P0 最高优先级经 KernelCoordinator 还原内核规则；
        /// 4) 若开启了方向 4（CloseLinkedAppsOnExit）且是人工退出，再按「业务软件 → 代理客户端」顺序优雅关闭！
        /// </summary>
        public async Task<ShutdownOutcome> ExecuteShutdownAsync(
            CompanionConfig cfg,
            bool triggeredByHostExit,
            Func<CancellationToken, KernelOpResult> restoreKernelAction)
        {
            if (Interlocked.CompareExchange(ref _shutdownLatch, 1, 0) != 0)
                return new ShutdownOutcome { AlreadyInProgress = true };

            var outcome = new ShutdownOutcome();
            var plan = CompanionPolicyEngine.PlanShutdownSequence(cfg, triggeredByHostExit);

            if (plan.NotifyWatcherManualExit)
                _autostart.NotifyManualExitToWatcher();

            _coordinator.BeginShutdown();
            if (plan.ShouldRestoreKernelFirst && restoreKernelAction != null)
            {
                var res = await _coordinator.EnqueueAsync(
                    "ShutdownRestoreKernel",
                    KernelWritePriority.P0_ShutdownRestore,
                    restoreKernelAction).ConfigureAwait(false);
                outcome.KernelRestored = res.Ok;
                outcome.RestoreDetail = res.Message;
            }
            else
            {
                outcome.KernelRestored = true;
            }

            // 只有在内核完成还原后，才按「业务软件 → 代理客户端」顺序执行方向 4 优雅关闭
            if (plan.TargetsToCloseGracefully.Count > 0)
            {
                foreach (var target in plan.TargetsToCloseGracefully)
                {
                    string detail;
                    if (_probe.TryCloseGracefully(target, plan.GracefulTimeoutMs, out detail))
                        outcome.ClosedLinkedApps.Add(CompanionPolicyEngine.ExtractExeName(target));
                    else
                        outcome.FailedCloseApps.Add(CompanionPolicyEngine.ExtractExeName(target) + "：" + detail);
                }
                _audit?.Record("COMPANION",
                    "退出同步关闭联动软件：已关闭 " + outcome.ClosedLinkedApps.Count + " 个" +
                    (outcome.FailedCloseApps.Count > 0 ? ("，未关闭 " + outcome.FailedCloseApps.Count + " 个") : ""));
            }
            return outcome;
        }

        public void ResetShutdownLatch()
        {
            Interlocked.Exchange(ref _shutdownLatch, 0);
            _coordinator.CancelShutdownState();
        }
    }
}
