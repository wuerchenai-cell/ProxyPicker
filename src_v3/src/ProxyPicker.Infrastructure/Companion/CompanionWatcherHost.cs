using System;
using System.IO;
using System.Threading;
using ProxyPicker.Domain.Interfaces;
using ProxyPicker.Domain.Models;
using ProxyPicker.Domain.Services;
using ProxyPicker.Platform.Windows;

namespace ProxyPicker.Infrastructure.Companion
{
    /// <summary>
    /// 独立无窗口静默守望宿主（--watch-companion）：
    /// 1) 读 settings.json 遇瞬时文件锁冲突保留上一份有效配置不自杀（P1-02 架构免疫）；
    /// 2) 精确完整路径探测 + Unknown 不误清锁（P1-03）；
    /// 3) 启动失败 20 秒退避重试 + 手动退出后同周期严格抑制（P1-04）。
    /// </summary>
    public sealed class CompanionWatcherHost
    {
        readonly IConfigRepository _repo;
        readonly IProcessProbe _probe;
        readonly IAppLauncher _launcher;
        readonly IClock _clock;
        readonly IAuditSink _audit;
        readonly string _selfExePath;

        public CompanionWatcherHost(
            IConfigRepository repo, IProcessProbe probe, IAppLauncher launcher,
            IClock clock, IAuditSink audit, string selfExePath)
        {
            _repo = repo;
            _probe = probe;
            _launcher = launcher;
            _clock = clock;
            _audit = audit;
            _selfExePath = selfExePath;
        }

        public void RunLoop()
        {
            bool created;
            using (var mutex = new Mutex(true, NativeProcessProbe.WatchMutexName, out created))
            {
                if (!created) return;
                using (var stopEv = new EventWaitHandle(false, EventResetMode.ManualReset, WindowsAutostartStore.WatchStopEventName))
                using (var reloadEv = new EventWaitHandle(false, EventResetMode.AutoReset, WindowsAutostartStore.WatchReloadEventName))
                using (var manualExitEv = new EventWaitHandle(false, EventResetMode.AutoReset, WindowsAutostartStore.ManualExitEventName))
                {
                    var handles = new WaitHandle[] { stopEv, reloadEv, manualExitEv };
                    var sm = new CompanionPolicyEngine.CompanionWatchStateMachine();
                    string settingsFile = Path.Combine(_repo.OwnDataDir, "settings.json");
                    DateTime lastWrite = DateTime.MinValue;
                    var cfg = _repo.LoadSettings().Companion ?? new CompanionConfig();
                    bool hasValidCfg = cfg.HasValidFollowTarget && cfg.FollowWatchAtLogon;

                    while (true)
                    {
                        bool explicitlyDisabled = false;
                        try
                        {
                            if (File.Exists(settingsFile))
                            {
                                DateTime wt = File.GetLastWriteTimeUtc(settingsFile);
                                if (wt != lastWrite)
                                {
                                    AppSettingsData reloaded;
                                    bool missing;
                                    if (_repo.TryReloadSettings(out reloaded, out missing) && reloaded != null)
                                    {
                                        lastWrite = wt;
                                        cfg = reloaded.Companion ?? new CompanionConfig();
                                        hasValidCfg = true;
                                        if (!cfg.HasValidFollowTarget || !cfg.FollowWatchAtLogon)
                                            explicitlyDisabled = true;
                                    }
                                }
                            }
                            else if (!hasValidCfg)
                            {
                                explicitlyDisabled = true;
                            }
                        }
                        catch { }

                        if (explicitlyDisabled || (!hasValidCfg && (!cfg.HasValidFollowTarget || !cfg.FollowWatchAtLogon)))
                            return;

                        if (cfg.HasValidFollowTarget && cfg.FollowWatchAtLogon)
                        {
                            string query = !string.IsNullOrWhiteSpace(cfg.FollowAppPath) ? cfg.FollowAppPath : cfg.FollowAppExe;
                            var probe = _probe.ProbeTarget(query, false);
                            if (probe != ProcessProbeState.Unknown)
                            {
                                bool hostRunning = probe == ProcessProbeState.Running;
                                bool mainRunning = _probe.IsMainInstanceRunning();
                                DateTime nowUtc = _clock.UtcNow;
                                if (sm.ShouldLaunch(cfg.FollowEnabled, cfg.FollowAppExe, hostRunning, mainRunning, nowUtc))
                                {
                                    bool already;
                                    string err;
                                    string argTarget = !string.IsNullOrWhiteSpace(cfg.FollowAppPath) ? cfg.FollowAppPath : cfg.FollowAppExe;
                                    if (_launcher.TryLaunch(_selfExePath, "--tray --follow-exe \"" + argTarget + "\"", false, out already, out err))
                                    {
                                        sm.CommitLaunchSucceeded();
                                    }
                                    else
                                    {
                                        sm.RecordLaunchFailed(nowUtc);
                                        _audit?.Record("COMPANION", "静默守望拉起主程序失败，20 秒后自动重试：" + err);
                                    }
                                }
                            }
                        }

                        int sig = WaitHandle.WaitAny(handles, 2500);
                        if (sig == 0) return;
                        if (sig == 1) lastWrite = DateTime.MinValue;
                        else if (sig == 2) sm.MarkManualExitSuppressed();
                    }
                }
            }
        }
    }
}
