using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ProxyPicker.Domain.Models;

namespace ProxyPicker.Domain.Services
{
    /// <summary>第二实例启动动作分类。</summary>
    public enum SecondInstanceAction
    {
        ProceedAsMain = 0,
        ActivateMainWindow = 1,
        HandoffFollowToMain = 2,
        SilentExit = 3
    }

    /// <summary>四向对称退出执行计划（强制「先还原内核 → 再优雅关闭联动软件」，杜绝顺序颠倒导致 TUN 残留）。</summary>
    public sealed class ShutdownPlan
    {
        public bool ShouldRestoreKernelFirst { get; set; }
        public bool NotifyWatcherManualExit { get; set; }
        public List<string> TargetsToCloseGracefully { get; set; } = new List<string>();
        public int GracefulTimeoutMs { get; set; } = 5000;
    }

    /// <summary>
    /// 四向软件联动纯策略引擎（校验、排序、启动门禁、三态精确防抖、失败退避守望状态机与四向退出编排）。
    /// </summary>
    public static class CompanionPolicyEngine
    {
        static readonly HashSet<string> ProxyClientExes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "clash-verge.exe", "clash verge.exe", "flclash.exe",
            "clash-nyanpasu.exe", "clash nyanpasu.exe",
            "mihomo-party.exe", "mihomo party.exe",
            "sparkle.exe", "clash-party.exe", "clash party.exe",
            "clash for windows.exe"
        };

        static readonly HashSet<string> MultiProcessBrowserExes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "chrome.exe", "msedge.exe", "brave.exe", "vivaldi.exe", "opera.exe", "arc.exe", "firefox.exe"
        };

        static readonly HashSet<string> ForbiddenSystemExes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "proxypicker.exe", "explorer.exe", "svchost.exe", "csrss.exe",
            "winlogon.exe", "wininit.exe", "services.exe", "lsass.exe",
            "smss.exe", "dwm.exe", "taskhostw.exe", "sihost.exe",
            "conhost.exe", "cmd.exe", "powershell.exe", "pwsh.exe", "system", "idle"
        };

        public static string ExtractExeName(string exeOrPath)
        {
            if (string.IsNullOrWhiteSpace(exeOrPath)) return "";
            string s = exeOrPath.Trim().Trim('"');
            if (s.Length == 0) return "";
            try { s = Path.GetFileName(s); } catch { return ""; }
            return (s ?? "").Trim().ToLowerInvariant();
        }

        public static bool IsProxyClientExe(string exeOrPath)
        {
            string exe = ExtractExeName(exeOrPath);
            return exe.Length > 0 && ProxyClientExes.Contains(exe);
        }

        public static bool IsMultiProcessBrowserExe(string exeOrPath)
        {
            string exe = ExtractExeName(exeOrPath);
            return exe.Length > 0 && MultiProcessBrowserExes.Contains(exe);
        }

        public static int RecommendedMissThreshold(string exeOrPath)
        {
            return IsProxyClientExe(exeOrPath) ? 6 : 3;
        }

        public static bool ValidateTargetExeOrPath(string exeOrPath, bool requireFullPath, out string error)
        {
            error = "";
            if (string.IsNullOrWhiteSpace(exeOrPath)) { error = "未指定软件路径或进程名"; return false; }
            string raw = exeOrPath.Trim().Trim('"');
            for (int i = 0; i < raw.Length; i++)
                if (raw[i] < 0x20) { error = "软件路径包含非法控制字符"; return false; }
            string exe = ExtractExeName(raw);
            if (!exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || exe.Length <= 4)
            {
                error = "仅支持选择 .exe 可执行程序";
                return false;
            }
            if (ForbiddenSystemExes.Contains(exe))
            {
                error = exe.Equals("proxypicker.exe", StringComparison.OrdinalIgnoreCase)
                    ? "不能将 ProxyPicker 自身设为联动目标，以防循环自启动"
                    : ("为保证系统稳定，不支持绑定系统核心进程：" + exe);
                return false;
            }
            if (requireFullPath)
            {
                bool rooted = false;
                try { rooted = Path.IsPathRooted(raw); } catch { rooted = false; }
                if (!rooted) { error = "需要完整的程序路径"; return false; }
            }
            return true;
        }

        /// <summary>规范化、去重并按「代理客户端优先，其余保序」排好启动队列。</summary>
        public static List<string> OrderLaunchTargets(IEnumerable<string> rawPaths, string excludeExe = null)
        {
            var clients = new List<string>();
            var others = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string excl = ExtractExeName(excludeExe);
            if (rawPaths == null) return others;
            foreach (var item in rawPaths)
            {
                string p = (item ?? "").Trim().Trim('"');
                string err;
                if (!ValidateTargetExeOrPath(p, true, out err)) continue;
                string exe = ExtractExeName(p);
                if (excl.Length > 0 && exe.Equals(excl, StringComparison.OrdinalIgnoreCase)) continue;
                if (!seen.Add(exe)) continue;
                if (IsProxyClientExe(exe)) clients.Add(p);
                else others.Add(p);
            }
            clients.AddRange(others);
            return clients;
        }

        /// <summary>内核与规则未确认就绪前，禁止拉起浏览器等业务软件以防抢跑直连（P1-01 强门禁）。</summary>
        public static bool ShouldLaunchBusinessAppsAfterSync(
            bool hasSavedIntent, bool repairSuspended, bool kernelReady, bool rulesSynced, out string skipReason)
        {
            skipReason = "";
            if (!hasSavedIntent || repairSuspended) return true;
            if (!kernelReady) { skipReason = "代理内核尚未就绪，已暂缓拉起业务软件以防抢跑直连"; return false; }
            if (!rulesSynced) { skipReason = "分流规则尚未同步完成，已暂缓拉起业务软件以防抢跑直连"; return false; }
            return true;
        }

        /// <summary>精确路径比对：配置了完整路径时要求规范化完整路径相等，杜绝同名不同路径误判（P1-03）。</summary>
        public static bool MatchesTargetProcessPath(string expectedPathOrExe, string actualProcessPath)
        {
            string exp = (expectedPathOrExe ?? "").Trim().Trim('"');
            string act = (actualProcessPath ?? "").Trim().Trim('"');
            if (exp.Length == 0 || act.Length == 0) return false;
            bool expRooted = false;
            try { expRooted = Path.IsPathRooted(exp); } catch { expRooted = false; }
            if (expRooted)
            {
                try { return string.Equals(Path.GetFullPath(exp), Path.GetFullPath(act), StringComparison.OrdinalIgnoreCase); }
                catch { return string.Equals(exp, act, StringComparison.OrdinalIgnoreCase); }
            }
            return string.Equals(ExtractExeName(exp), ExtractExeName(act), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>按 Windows 命令行规则提取第 0 参数（可执行文件路径），用于注册表严格相等校验（P2-01）。</summary>
        public static string ExtractCommandExePath(string command)
        {
            if (string.IsNullOrWhiteSpace(command)) return "";
            string s = command.Trim();
            if (s[0] == '"')
            {
                int close = s.IndexOf('"', 1);
                return close > 1 ? s.Substring(1, close - 1).Trim() : s.Trim('"').Trim();
            }
            int sp = s.IndexOfAny(new[] { ' ', '\t' });
            return sp > 0 ? s.Substring(0, sp).Trim() : s;
        }

        public static bool CommandPointsToExe(string command, string exePath)
        {
            if (string.IsNullOrWhiteSpace(command) || string.IsNullOrWhiteSpace(exePath)) return false;
            string cmdExe = ExtractCommandExePath(command);
            if (cmdExe.Length == 0) return false;
            try { return string.Equals(Path.GetFullPath(cmdExe), Path.GetFullPath(exePath.Trim().Trim('"')), StringComparison.OrdinalIgnoreCase); }
            catch { return string.Equals(cmdExe, exePath.Trim().Trim('"'), StringComparison.OrdinalIgnoreCase); }
        }

        public static bool IsOwnedWatchCommand(string command, string currentExePath)
        {
            if (string.IsNullOrWhiteSpace(command)) return false;
            if (CommandPointsToExe(command, currentExePath)) return true;
            return command.IndexOf("--watch-companion", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static SecondInstanceAction ClassifySecondInstanceAction(
            bool createdMutex, bool launchedViaCompanion, string followSessionTarget)
        {
            if (createdMutex) return SecondInstanceAction.ProceedAsMain;
            if (!launchedViaCompanion) return SecondInstanceAction.ActivateMainWindow;
            return !string.IsNullOrWhiteSpace(followSessionTarget)
                ? SecondInstanceAction.HandoffFollowToMain
                : SecondInstanceAction.SilentExit;
        }

        /// <summary>
        /// v3.0 四向对称退出编排纯函数：
        /// 1) 无论人工退出还是同生共死，永远先还原内核配置，绝不在内核未还原前关闭代理客户端；
        /// 2) 人工退出且启用了 CloseLinkedAppsOnExit 时，按「业务软件在前 → 代理客户端在后」顺序优雅关闭，避免残留 TUN；
        /// 3) 同生共死被动退出（由宿主关闭触发）不再反向关闭该宿主，防止二次重入。
        /// </summary>
        public static ShutdownPlan PlanShutdownSequence(CompanionConfig cfg, bool triggeredByHostExit)
        {
            var plan = new ShutdownPlan
            {
                ShouldRestoreKernelFirst = true,
                NotifyWatcherManualExit = !triggeredByHostExit,
                GracefulTimeoutMs = cfg != null ? cfg.GracefulCloseTimeoutMs : 5000
            };
            if (triggeredByHostExit || cfg == null || !cfg.CloseLinkedAppsOnExit)
                return plan;

            var candidates = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Action<string> add = p =>
            {
                string err;
                if (string.IsNullOrWhiteSpace(p) || !ValidateTargetExeOrPath(p, false, out err)) return;
                string exe = ExtractExeName(p);
                if (seen.Add(exe)) candidates.Add(p.Trim().Trim('"'));
            };
            if (cfg.HasAnyLaunchApps)
                foreach (var p in cfg.LaunchAppPaths) add(p);
            if (cfg.HasValidFollowTarget)
                add(!string.IsNullOrWhiteSpace(cfg.FollowAppPath) ? cfg.FollowAppPath : cfg.FollowAppExe);

            // 反向于启动顺序：先关浏览器等业务软件，最后关代理客户端！
            var others = candidates.Where(p => !IsProxyClientExe(p)).ToList();
            var clients = candidates.Where(p => IsProxyClientExe(p)).ToList();
            plan.TargetsToCloseGracefully.AddRange(others);
            plan.TargetsToCloseGracefully.AddRange(clients);
            return plan;
        }

        /// <summary>三态同生共死防抖状态机（Unknown 不递增计数，绝不因瞬时权限/枚举异常误退）。</summary>
        public sealed class FollowExitTracker
        {
            public string TargetPath { get; }
            public string TargetExe { get; }
            public int MissThreshold { get; }
            public bool SeenRunning { get; private set; }
            public int ConsecutiveMisses { get; private set; }

            public FollowExitTracker(string targetExeOrPath, int missThreshold = 3)
            {
                TargetPath = (targetExeOrPath ?? "").Trim().Trim('"');
                TargetExe = ExtractExeName(TargetPath);
                MissThreshold = Math.Max(1, missThreshold);
            }

            public bool Observe(ProcessProbeState state)
            {
                if (TargetExe.Length == 0) return false;
                if (state == ProcessProbeState.Running) { SeenRunning = true; ConsecutiveMisses = 0; return false; }
                if (state == ProcessProbeState.Unknown) return false;
                if (!SeenRunning) return false;
                ConsecutiveMisses++;
                return ConsecutiveMisses >= MissThreshold;
            }
        }

        /// <summary>守望器触发与失败退避冷却状态机（P1-04 架构内置：启动成功才占锁，失败 20s 退避重试，手动退出严格抑制）。</summary>
        public sealed class CompanionWatchStateMachine
        {
            public const int FailedRetryCooldownSeconds = 20;
            public bool TriggeredForCurrentHostRun { get; private set; }
            public DateTime NextRetryUtc { get; private set; } = DateTime.MinValue;
            string _lastTargetExe = "";

            public void ResetForTarget(string targetExe)
            {
                string norm = ExtractExeName(targetExe);
                if (!string.Equals(_lastTargetExe, norm, StringComparison.OrdinalIgnoreCase))
                {
                    _lastTargetExe = norm;
                    TriggeredForCurrentHostRun = false;
                    NextRetryUtc = DateTime.MinValue;
                }
            }

            public void MarkManualExitSuppressed()
            {
                TriggeredForCurrentHostRun = true;
                NextRetryUtc = DateTime.MinValue;
            }

            public bool ShouldLaunch(bool followEnabled, string targetExe, bool hostRunning, bool mainRunning, DateTime nowUtc)
            {
                ResetForTarget(targetExe);
                if (!followEnabled || _lastTargetExe.Length == 0 || !hostRunning)
                {
                    TriggeredForCurrentHostRun = false;
                    NextRetryUtc = DateTime.MinValue;
                    return false;
                }
                if (mainRunning)
                {
                    TriggeredForCurrentHostRun = true;
                    NextRetryUtc = DateTime.MinValue;
                    return false;
                }
                if (TriggeredForCurrentHostRun) return false;
                if (NextRetryUtc != DateTime.MinValue && nowUtc < NextRetryUtc) return false;
                return true;
            }

            public void CommitLaunchSucceeded() { TriggeredForCurrentHostRun = true; NextRetryUtc = DateTime.MinValue; }
            public void RecordLaunchFailed(DateTime nowUtc) { TriggeredForCurrentHostRun = false; NextRetryUtc = nowUtc.AddSeconds(FailedRetryCooldownSeconds); }
        }
    }
}
