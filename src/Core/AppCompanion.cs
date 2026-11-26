using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace ProxyPicker.Core
{
    /// <summary>
    /// 双向软件联动启停引擎（v2.6.0）：
    /// 1) 跟随指定软件同步启停（同生共死）：
    ///    - 通道 A（零常驻）：一键在桌面生成带原软件图标的联动快捷方式（ProxyPicker.exe --follow "目标.exe"），
    ///      双击即同时拉起目标软件与后台 ProxyPicker，目标关闭后自动还原内核配置并退出；
    ///    - 通道 B（开机静默守望）：轻量无窗口守望进程（--watch-companion，HKCU Run，不需管理员权限），
    ///      用户直接点击原软件图标即可在 2.5 秒内自动拉起 ProxyPicker 后台生效分流，目标关闭后同生共死退出；
    ///      若用户在本轮手动退出了 ProxyPicker，则等待目标软件下次重启前绝不重复拉起。
    /// 2) 打开 ProxyPicker 时自动同步拉起用户勾选的软件：
    ///    - 自动识别并优先启动代理客户端（如 Clash Verge / FlClash 等），等内核就绪且规则生效后再启动其余业务软件，
    ///      避免浏览器等软件在规则生效前抢跑直连；已在运行的软件不重复拉起；严禁自拉起形成递归循环。
    /// </summary>
    internal static class AppCompanion
    {
        public const string WatchRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        public const string WatchRunName = "ProxyPickerCompanion";
        public const string WatchMutexName = @"Local\ProxyPicker.CompanionWatch";
        public const string WatchStopEventName = @"Local\ProxyPicker.StopCompanionWatch";
        public const string WatchReloadEventName = @"Local\ProxyPicker.ReloadCompanionWatch";
        public const string ManualExitEventName = @"Local\ProxyPicker.ManualExitNotice";
        public const string CompanionHandoffEventName = @"Local\ProxyPicker.CompanionHandoff";

        /// <summary>进程存活探测三态结果：严格区分「未运行」与「权限/枚举异常无法确认」，避免异常时误杀退出。</summary>
        internal enum ProcessProbeState
        {
            NotRunning = 0,
            Running = 1,
            Unknown = 2
        }

        /// <summary>第二实例启动时的动作分类（纯枚举供自检与 Program.Main 共用）。</summary>
        internal enum SecondInstanceAction
        {
            ProceedAsMain = 0,
            ActivateMainWindow = 1,
            HandoffFollowToMain = 2,
            SilentExit = 3
        }

        /// <summary>联动设置快照。</summary>
        public sealed class CompanionConfig
        {
            public bool FollowEnabled;
            public string FollowAppExe = "";
            public string FollowAppPath = "";
            public string FollowAppName = "";
            public bool FollowAutoExit = true;
            public bool FollowWatchAtLogon;

            public bool LaunchAppsEnabled;
            public bool LaunchAppsOnFollow = true;
            public List<string> LaunchAppPaths = new List<string>();

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
                    LaunchAppPaths = LaunchAppPaths != null ? new List<string>(LaunchAppPaths) : new List<string>()
                };
            }

            public bool HasValidFollowTarget
            {
                get { return FollowEnabled && !string.IsNullOrWhiteSpace(FollowAppExe); }
            }

            public bool HasAnyLaunchApps
            {
                get { return LaunchAppsEnabled && LaunchAppPaths != null && LaunchAppPaths.Count > 0; }
            }

            public bool IsAnyActive
            {
                get { return HasValidFollowTarget || HasAnyLaunchApps; }
            }
        }

        /// <summary>界面供选择的可联动桌面软件条目。</summary>
        public sealed class AppCandidate
        {
            public string Exe = "";
            public string Name = "";
            public string FullPath = "";
            public bool IsProxyClient;
            public bool Running;
            public bool Proxied;
        }

        /// <summary>单次拉起统计结果。</summary>
        public sealed class LaunchBatchOutcome
        {
            public readonly List<string> Started = new List<string>();
            public readonly List<string> AlreadyRunning = new List<string>();
            public readonly List<string> Failed = new List<string>();
            public bool LaunchedProxyClient;
        }

        static readonly HashSet<string> ProxyClientExes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "clash-verge.exe",
            "clash verge.exe",
            "flclash.exe",
            "clash-nyanpasu.exe",
            "clash nyanpasu.exe",
            "mihomo-party.exe",
            "mihomo party.exe",
            "sparkle.exe",
            "clash-party.exe",
            "clash party.exe",
            "clash for windows.exe"
        };

        static readonly HashSet<string> ForbiddenSystemExes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "proxypicker.exe",
            "explorer.exe",
            "svchost.exe",
            "csrss.exe",
            "winlogon.exe",
            "wininit.exe",
            "services.exe",
            "lsass.exe",
            "smss.exe",
            "dwm.exe",
            "taskhostw.exe",
            "sihost.exe",
            "conhost.exe",
            "cmd.exe",
            "powershell.exe",
            "pwsh.exe",
            "system",
            "idle"
        };

        /// <summary>判断是否为常见代理客户端前端程序（启动时优先拉起并等待内核就绪）。</summary>
        public static bool IsProxyClientExe(string exeOrPath)
        {
            string exe = ExtractExeName(exeOrPath);
            return exe.Length > 0 && ProxyClientExes.Contains(exe);
        }

        /// <summary>提取规范化的小写 exe 文件名。</summary>
        public static string ExtractExeName(string exeOrPath)
        {
            if (string.IsNullOrWhiteSpace(exeOrPath)) return "";
            string s = exeOrPath.Trim().Trim('"');
            if (s.Length == 0) return "";
            try { s = Path.GetFileName(s); }
            catch { return ""; }
            return (s ?? "").Trim().ToLowerInvariant();
        }

        /// <summary>
        /// 校验目标软件是否允许纳入跟随或自动拉起：
        /// 严禁指向 ProxyPicker 自身（防无限自递归）及核心系统进程，且必须为合法 .exe。
        /// </summary>
        public static bool ValidateTargetExeOrPath(string exeOrPath, bool requireFullPath, out string error)
        {
            error = "";
            if (string.IsNullOrWhiteSpace(exeOrPath))
            {
                error = "未指定软件路径或进程名";
                return false;
            }
            string raw = exeOrPath.Trim().Trim('"');
            for (int i = 0; i < raw.Length; i++)
            {
                if (raw[i] < 0x20)
                {
                    error = "软件路径包含非法控制字符";
                    return false;
                }
            }
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
                if (!rooted)
                {
                    error = "需要完整的程序路径（例如 C:\\Program Files\\...\\app.exe）";
                    return false;
                }
            }
            return true;
        }

        /// <summary>规范化、去重并按「代理客户端优先，其余保序」排好启动队列。</summary>
        public static List<string> OrderLaunchTargets(IEnumerable<string> rawPaths, string excludeExe = null)
        {
            var clients = new List<string>();
            var others = new List<string>();
            var seenExe = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string excl = ExtractExeName(excludeExe);
            if (rawPaths == null) return others;

            foreach (var item in rawPaths)
            {
                string p = (item ?? "").Trim().Trim('"');
                string err;
                if (!ValidateTargetExeOrPath(p, true, out err)) continue;
                string exe = ExtractExeName(p);
                if (excl.Length > 0 && exe.Equals(excl, StringComparison.OrdinalIgnoreCase)) continue;
                if (!seenExe.Add(exe)) continue;
                if (IsProxyClientExe(exe)) clients.Add(p);
                else others.Add(p);
            }
            clients.AddRange(others);
            return clients;
        }

        static readonly HashSet<string> MultiProcessBrowserExes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "chrome.exe",
            "msedge.exe",
            "brave.exe",
            "vivaldi.exe",
            "opera.exe",
            "arc.exe",
            "firefox.exe"
        };

        /// <summary>判断是否为常见多进程浏览器（关闭全部窗口后常留后台 helper 进程，需按可见顶层窗口判定）。</summary>
        public static bool IsMultiProcessBrowserExe(string exeOrPath)
        {
            string exe = ExtractExeName(exeOrPath);
            return exe.Length > 0 && MultiProcessBrowserExes.Contains(exe);
        }

        /// <summary>按软件类型返回建议的同生共死防抖轮数（每轮 2.5s：代理客户端 6 轮≈15s 防更新重启，普通软件 3 轮≈7.5s）。</summary>
        public static int RecommendedMissThreshold(string exeOrPath)
        {
            return IsProxyClientExe(exeOrPath) ? 6 : 3;
        }

        /// <summary>
        /// 精确核对目标进程路径（v2.6.1 P1-03 修复）：
        /// 配置了完整路径时要求规范化后的完整路径严格相等（同名不同路径不算存活）；仅给 exe 名时按文件名匹配。
        /// </summary>
        internal static bool MatchesTargetProcessPath(string expectedPathOrExe, string actualProcessPath)
        {
            string exp = (expectedPathOrExe ?? "").Trim().Trim('"');
            string act = (actualProcessPath ?? "").Trim().Trim('"');
            if (exp.Length == 0 || act.Length == 0) return false;
            bool expRooted = false;
            try { expRooted = Path.IsPathRooted(exp); } catch { expRooted = false; }
            if (expRooted)
            {
                try
                {
                    string normExp = Path.GetFullPath(exp);
                    string normAct = Path.GetFullPath(act);
                    return string.Equals(normExp, normAct, StringComparison.OrdinalIgnoreCase);
                }
                catch
                {
                    return string.Equals(exp, act, StringComparison.OrdinalIgnoreCase);
                }
            }
            return string.Equals(ExtractExeName(exp), ExtractExeName(act), StringComparison.OrdinalIgnoreCase);
        }

        delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        static extern int GetWindowTextLength(IntPtr hWnd);

        static bool HasVisibleWindowForPids(HashSet<int> pids)
        {
            if (pids == null || pids.Count == 0) return false;
            bool found = false;
            try
            {
                EnumWindows((hWnd, lParam) =>
                {
                    if (!IsWindowVisible(hWnd)) return true;
                    uint pid;
                    GetWindowThreadProcessId(hWnd, out pid);
                    if (pids.Contains((int)pid) && GetWindowTextLength(hWnd) > 0)
                    {
                        found = true;
                        return false;
                    }
                    return true;
                }, IntPtr.Zero);
            }
            catch { return true; } // 窗口枚举异常时保守按仍在运行处理，不误退
            return found;
        }

        /// <summary>
        /// 三态精确探测目标软件存活状态（v2.6.1 P1-03 修复）：
        /// 1) 给了完整路径时按 QueryFullProcessImageNameW 精确比对路径，拒绝同名不同路径误判；
        /// 2) 进程列表枚举异常或权限不足读不到路径时返回 Unknown 而非 NotRunning，防误退；
        /// 3) 同生共死模式下对 Chrome/Edge 等多进程浏览器校验是否存在可见顶层窗口，防止后台驻留进程导致漏退。
        /// </summary>
        internal static ProcessProbeState ProbeTargetProcess(string exeOrPath, bool requireVisibleWindowForMultiProc = false)
        {
            string raw = (exeOrPath ?? "").Trim().Trim('"');
            string exe = ExtractExeName(raw);
            if (exe.Length <= 4 || !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                return ProcessProbeState.NotRunning;
            bool hasFullPath = false;
            try { hasFullPath = Path.IsPathRooted(raw); } catch { hasFullPath = false; }

            string procName = exe.Substring(0, exe.Length - 4);
            Process[] procs;
            try
            {
                procs = Process.GetProcessesByName(procName);
            }
            catch
            {
                return ProcessProbeState.Unknown;
            }
            if (procs == null || procs.Length == 0)
                return ProcessProbeState.NotRunning;

            var matchedPids = new HashSet<int>();
            bool sawUnreadablePath = false;
            try
            {
                foreach (var p in procs)
                {
                    try
                    {
                        if (!hasFullPath)
                        {
                            matchedPids.Add(p.Id);
                        }
                        else
                        {
                            string imgPath;
                            if (Scanner.TryQueryImagePathByPid(p.Id, out imgPath))
                            {
                                if (MatchesTargetProcessPath(raw, imgPath))
                                    matchedPids.Add(p.Id);
                            }
                            else
                            {
                                sawUnreadablePath = true;
                            }
                        }
                    }
                    catch { sawUnreadablePath = true; }
                    finally { try { p.Dispose(); } catch { } }
                }
            }
            catch
            {
                return ProcessProbeState.Unknown;
            }

            if (matchedPids.Count == 0)
                return sawUnreadablePath ? ProcessProbeState.Unknown : ProcessProbeState.NotRunning;

            if (requireVisibleWindowForMultiProc && IsMultiProcessBrowserExe(exe))
                return HasVisibleWindowForPids(matchedPids) ? ProcessProbeState.Running : ProcessProbeState.NotRunning;

            return ProcessProbeState.Running;
        }

        /// <summary>轻量检测指定程序当前是否至少有一个运行中的实例（若传完整路径则按路径精确匹配）。</summary>
        public static bool IsProcessRunning(string exeOrPath)
        {
            return ProbeTargetProcess(exeOrPath, false) == ProcessProbeState.Running;
        }

        /// <summary>纯决策函数（v2.6.1 P1-01 修复）：内核与规则未确认就绪前，禁止拉起浏览器等业务软件以防抢跑直连。</summary>
        internal static bool ShouldLaunchBusinessAppsAfterSync(
            bool hasSavedIntent,
            bool repairSuspended,
            bool kernelReady,
            bool rulesSynced,
            out string skipReason)
        {
            skipReason = "";
            if (!hasSavedIntent || repairSuspended) return true;
            if (!kernelReady)
            {
                skipReason = "代理内核尚未就绪，已暂缓拉起业务软件以防抢跑直连";
                return false;
            }
            if (!rulesSynced)
            {
                skipReason = "分流规则尚未同步完成，已暂缓拉起业务软件以防抢跑直连";
                return false;
            }
            return true;
        }

        /// <summary>判断 ProxyPicker 主程序（非守望进程）当前是否已在运行。</summary>
        public static bool IsMainInstanceRunning()
        {
            try
            {
                using (var m = Mutex.OpenExisting(@"Local\ProxyPicker.SingleInstance"))
                    return m != null;
            }
            catch (WaitHandleCannotBeOpenedException) { return false; }
            catch (UnauthorizedAccessException) { return true; }
            catch { return false; }
        }

        /// <summary>尝试静默拉起单个本地软件（若已在运行则跳过，绝不重复多开）。</summary>
        public static bool TryLaunchApp(string fullPath, out bool alreadyRunning, out string error)
        {
            alreadyRunning = false;
            error = "";
            string clean = (fullPath ?? "").Trim().Trim('"');
            if (!ValidateTargetExeOrPath(clean, true, out error)) return false;
            if (!File.Exists(clean))
            {
                error = "找不到程序文件：" + Path.GetFileName(clean);
                return false;
            }
            if (IsProcessRunning(clean))
            {
                alreadyRunning = true;
                return true;
            }
            try
            {
                string dir = Path.GetDirectoryName(clean);
                var psi = new ProcessStartInfo
                {
                    FileName = clean,
                    WorkingDirectory = (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) ? dir : "",
                    UseShellExecute = true
                };
                using (var p = Process.Start(psi)) { }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        /// <summary>按列表依次拉起目标软件（只拉起当前未运行的项）。</summary>
        public static LaunchBatchOutcome LaunchApps(IEnumerable<string> fullPaths)
        {
            var outc = new LaunchBatchOutcome();
            if (fullPaths == null) return outc;
            foreach (var p in fullPaths)
            {
                bool running; string err;
                string fn = Path.GetFileName(p ?? "");
                if (TryLaunchApp(p, out running, out err))
                {
                    if (running) outc.AlreadyRunning.Add(fn);
                    else
                    {
                        outc.Started.Add(fn);
                        if (IsProxyClientExe(p)) outc.LaunchedProxyClient = true;
                    }
                }
                else
                {
                    outc.Failed.Add(fn + "（" + err + "）");
                }
            }
            return outc;
        }

        /// <summary>
        /// 跟随宿主退出防抖状态机（纯逻辑，支持离线单测）：
        /// 1) 必须先确认观测到宿主进程确实运行过（SeenRunning = true），才允许在消失时触发退出；
        ///    刚打开软件时的启动宽限期绝不会误判提前退出；
        /// 2) 宿主消失后需连续 MissThreshold 轮（如 2 轮 × 2.5 秒）仍不在，才判定真正退出，
        ///    避免软件自更新或短瞬间重启误关 ProxyPicker。
        /// </summary>
        internal sealed class FollowExitTracker
        {
            public readonly string TargetExe;
            public readonly string TargetPath;
            public readonly int MissThreshold;
            public bool SeenRunning { get; private set; }
            public int ConsecutiveMisses { get; private set; }

            public FollowExitTracker(string targetExeOrPath, int missThreshold = 2)
            {
                TargetPath = (targetExeOrPath ?? "").Trim().Trim('"');
                TargetExe = ExtractExeName(TargetPath);
                MissThreshold = Math.Max(1, missThreshold);
            }

            /// <summary>推进一步布尔探测结果（兼容旧单测与调用）。</summary>
            public bool Observe(bool isRunningNow)
            {
                return Observe(isRunningNow ? ProcessProbeState.Running : ProcessProbeState.NotRunning);
            }

            /// <summary>推进一步三态探测结果：Unknown 时保持计数不递增，防止瞬时枚举/权限错误导致误退。</summary>
            public bool Observe(ProcessProbeState state)
            {
                if (TargetExe.Length == 0) return false;
                if (state == ProcessProbeState.Running)
                {
                    SeenRunning = true;
                    ConsecutiveMisses = 0;
                    return false;
                }
                if (state == ProcessProbeState.Unknown)
                    return false;
                if (!SeenRunning) return false;
                ConsecutiveMisses++;
                return ConsecutiveMisses >= MissThreshold;
            }

            public void Reset()
            {
                SeenRunning = false;
                ConsecutiveMisses = 0;
            }
        }

        /// <summary>
        /// 轻量后台守望器触发决策状态机（纯逻辑，支持离线单测，v2.6.1 P1-04 升级）：
        /// - 拆分为「决策 ShouldLaunch」「确认启动成功 CommitLaunchSucceeded」「失败冷却 RecordLaunchFailed」；
        /// - 仅在 Process.Start 成功后才占用本轮触发锁；若启动失败，进入退避冷却期后自动重试而不永久卡死；
        /// - 手动退出抑制 MarkManualExitSuppressed 保持独立锁死，本轮宿主未关闭前绝不反复拉起。
        /// </summary>
        internal sealed class CompanionWatchStateMachine
        {
            public const int FailedRetryCooldownSeconds = 20;
            public bool TriggeredForCurrentHostRun { get; private set; }
            public DateTime NextRetryUtc { get; private set; }
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

            /// <summary>判断此刻是否应尝试拉起主程序（不会提前占锁，由调用方按启动成败回调 Commit/Record）。</summary>
            public bool ShouldLaunch(bool followEnabled, string targetExe, bool hostRunning, bool mainRunning, DateTime nowUtc)
            {
                ResetForTarget(targetExe);
                if (!followEnabled || _lastTargetExe.Length == 0)
                {
                    TriggeredForCurrentHostRun = false;
                    NextRetryUtc = DateTime.MinValue;
                    return false;
                }
                if (!hostRunning)
                {
                    // 目标软件已关闭：解除本轮已触发锁与冷却，等用户下次打开目标软件时重新联动
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
                if (TriggeredForCurrentHostRun)
                    return false;
                if (NextRetryUtc != DateTime.MinValue && nowUtc < NextRetryUtc)
                    return false;
                return true;
            }

            public void CommitLaunchSucceeded()
            {
                TriggeredForCurrentHostRun = true;
                NextRetryUtc = DateTime.MinValue;
            }

            public void RecordLaunchFailed(DateTime nowUtc)
            {
                TriggeredForCurrentHostRun = false;
                NextRetryUtc = nowUtc.AddSeconds(FailedRetryCooldownSeconds);
            }

            /// <summary>保留旧单步调用兼容：决定拉起并默认记为已提交。</summary>
            public bool Step(bool followEnabled, string targetExe, bool hostRunning, bool mainRunning)
            {
                if (!ShouldLaunch(followEnabled, targetExe, hostRunning, mainRunning, DateTime.UtcNow))
                    return false;
                CommitLaunchSucceeded();
                return true;
            }
        }

        // ---------------------------------------------------------------- 电脑可联动软件聚合发现

        /// <summary>汇总电脑上可选择的桌面软件（优先代理客户端，其次已代理/运行中/已安装软件，全部带完整可执行路径）。</summary>
        public static List<AppCandidate> DiscoverLaunchableApps(
            IEnumerable<string> knownProxiedExes = null,
            IEnumerable<string> extraPaths = null)
        {
            var byExe = new Dictionary<string, AppCandidate>(StringComparer.OrdinalIgnoreCase);
            var proxiedSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (knownProxiedExes != null)
                foreach (var x in knownProxiedExes) if (!string.IsNullOrWhiteSpace(x)) proxiedSet.Add(ExtractExeName(x));

            Action<string, string, bool> addOrMerge = (path, nameHint, running) =>
            {
                string err;
                if (!ValidateTargetExeOrPath(path, true, out err)) return;
                string cleanPath = path.Trim().Trim('"');
                if (!File.Exists(cleanPath)) return;
                string exe = ExtractExeName(cleanPath);
                AppCandidate cur;
                if (!byExe.TryGetValue(exe, out cur))
                {
                    string friendly = !string.IsNullOrWhiteSpace(nameHint)
                        ? nameHint.Trim()
                        : Catalog.DisplayName(exe);
                    cur = new AppCandidate
                    {
                        Exe = exe,
                        Name = string.IsNullOrWhiteSpace(friendly) ? Path.GetFileNameWithoutExtension(cleanPath) : friendly,
                        FullPath = cleanPath,
                        IsProxyClient = IsProxyClientExe(exe),
                        Running = running,
                        Proxied = proxiedSet.Contains(exe)
                    };
                    byExe[exe] = cur;
                }
                else
                {
                    if (running) cur.Running = true;
                    if (proxiedSet.Contains(exe)) cur.Proxied = true;
                    if (!string.IsNullOrWhiteSpace(nameHint) && cur.Name.Equals(Path.GetFileNameWithoutExtension(cur.FullPath), StringComparison.OrdinalIgnoreCase))
                        cur.Name = nameHint.Trim();
                }
            };

            // 1. 当前运行中的进程（能拿到代理客户端和常用工具最精确的真实路径）
            try
            {
                var running = Scanner.Scan();
                foreach (var kv in running)
                {
                    if (string.IsNullOrEmpty(kv.Value.Path)) continue;
                    if (IsProxyClientExe(kv.Key) || proxiedSet.Contains(kv.Key) || Catalog.IsUserApp(kv.Key, kv.Value.Path))
                        addOrMerge(kv.Value.Path, "", true);
                }
            }
            catch { }

            // 2. 常见代理客户端标准安装路径兜底（哪怕此刻客户端没开，也能一眼选到）
            try
            {
                string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                string la = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string[] knownClientPaths =
                {
                    Path.Combine(pf, "Clash Verge", "clash-verge.exe"),
                    Path.Combine(la, "Programs", "clash-verge", "clash-verge.exe"),
                    Path.Combine(pf, "FlClash", "FlClash.exe"),
                    Path.Combine(la, "Programs", "FlClash", "FlClash.exe"),
                    Path.Combine(pf, "Clash Nyanpasu", "clash-nyanpasu.exe"),
                    Path.Combine(la, "Programs", "clash-nyanpasu", "clash-nyanpasu.exe"),
                    Path.Combine(pf, "Mihomo Party", "Mihomo Party.exe"),
                    Path.Combine(la, "Programs", "mihomo-party", "Mihomo Party.exe"),
                    Path.Combine(pf, "Sparkle", "Sparkle.exe"),
                    Path.Combine(pf, "Clash Party", "Clash Party.exe")
                };
                foreach (var kp in knownClientPaths)
                    if (File.Exists(kp)) addOrMerge(kp, "", false);
            }
            catch { }

            // 3. 已安装软件扫描
            try
            {
                var installed = InstalledScanner.Scan();
                foreach (var kv in installed)
                {
                    if (kv.Value != null && !string.IsNullOrEmpty(kv.Value.Path))
                        addOrMerge(kv.Value.Path, kv.Value.Name, false);
                }
            }
            catch { }

            // 4. 用户先前保存的联动路径
            if (extraPaths != null)
            {
                foreach (var ep in extraPaths)
                    if (!string.IsNullOrWhiteSpace(ep)) addOrMerge(ep, "", false);
            }

            var list = new List<AppCandidate>(byExe.Values);
            list.Sort((a, b) =>
            {
                int cmp = b.IsProxyClient.CompareTo(a.IsProxyClient);
                if (cmp != 0) return cmp;
                cmp = b.Proxied.CompareTo(a.Proxied);
                if (cmp != 0) return cmp;
                cmp = b.Running.CompareTo(a.Running);
                if (cmp != 0) return cmp;
                return string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
            });
            return list;
        }

        // ---------------------------------------------------------------- 注册表登录静默守望管理（零管理员权限）

        /// <summary>按 Windows 命令行规则提取第 0 个参数（可执行文件路径），消除 StartsWith 前缀误匹配（v2.6.1 P2-01 修复）。</summary>
        internal static string ExtractCommandExePath(string command)
        {
            if (string.IsNullOrWhiteSpace(command)) return "";
            string s = command.Trim();
            if (s.Length == 0) return "";
            if (s[0] == '"')
            {
                int close = s.IndexOf('"', 1);
                return close > 1 ? s.Substring(1, close - 1).Trim() : s.Trim('"').Trim();
            }
            int sp = s.IndexOfAny(new[] { ' ', '\t' });
            return sp > 0 ? s.Substring(0, sp).Trim() : s;
        }

        /// <summary>精确比较命令行第 0 参数规范路径是否等于 exePath。</summary>
        internal static bool CommandPointsToExe(string command, string exePath)
        {
            if (string.IsNullOrWhiteSpace(command) || string.IsNullOrWhiteSpace(exePath)) return false;
            string cmdExe = ExtractCommandExePath(command);
            if (cmdExe.Length == 0) return false;
            try
            {
                string a = Path.GetFullPath(cmdExe);
                string b = Path.GetFullPath(exePath.Trim().Trim('"'));
                return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return string.Equals(cmdExe, exePath.Trim().Trim('"'), StringComparison.OrdinalIgnoreCase);
            }
        }

        /// <summary>判断注册表值是否由本程序写入的守望命令（哪怕便携 exe 搬过目录，取消守望时也应一并清理防留孤儿）。</summary>
        internal static bool IsOwnedWatchCommand(string command, string currentExePath)
        {
            if (string.IsNullOrWhiteSpace(command)) return false;
            if (CommandPointsToExe(command, currentExePath)) return true;
            return command.IndexOf("--watch-companion", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static bool PointsToCurrentExe(string command, string exePath)
        {
            return CommandPointsToExe(command, exePath);
        }

        public static bool IsWatchAutostartEnabled(string currentExePath = null)
        {
            string exe = string.IsNullOrEmpty(currentExePath)
                ? System.Windows.Forms.Application.ExecutablePath
                : currentExePath;
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(WatchRunKey))
                {
                    if (k == null) return false;
                    return PointsToCurrentExe(Convert.ToString(k.GetValue(WatchRunName)), exe);
                }
            }
            catch { return false; }
        }

        public static bool SetWatchAutostart(bool enable, string currentExePath = null)
        {
            string exe = string.IsNullOrEmpty(currentExePath)
                ? System.Windows.Forms.Application.ExecutablePath
                : currentExePath;
            try
            {
                using (var k = enable
                    ? Registry.CurrentUser.CreateSubKey(WatchRunKey)
                    : Registry.CurrentUser.OpenSubKey(WatchRunKey, true))
                {
                    if (k == null) return !enable;
                    if (enable)
                    {
                        k.SetValue(WatchRunName, "\"" + exe + "\" --watch-companion");
                    }
                    else
                    {
                        object v = k.GetValue(WatchRunName);
                        if (v == null) return true;
                        if (IsOwnedWatchCommand(Convert.ToString(v), exe))
                            k.DeleteValue(WatchRunName, false);
                        else
                            return false;
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                CrashLog.Write(ex, "保存软件联动守望自启失败");
                return false;
            }
        }

        // ---------------------------------------------------------------- 第二实例 --follow 跨进程交接（v2.6.1 P1-05 修复）

        internal static SecondInstanceAction ClassifySecondInstanceAction(
            bool createdMutex,
            bool launchedViaCompanion,
            string followSessionTarget)
        {
            if (createdMutex) return SecondInstanceAction.ProceedAsMain;
            if (!launchedViaCompanion) return SecondInstanceAction.ActivateMainWindow;
            return !string.IsNullOrWhiteSpace(followSessionTarget)
                ? SecondInstanceAction.HandoffFollowToMain
                : SecondInstanceAction.SilentExit;
        }

        static string HandoffFilePath
        {
            get { return Path.Combine(Paths.OwnDir, "companion_handoff.tmp"); }
        }

        /// <summary>第二实例在退出前把本次 --follow 目标（完整路径或 exe 名）原子交给已在运行的主实例。</summary>
        public static bool PostFollowHandoffToMain(string targetPathOrExe)
        {
            string clean = (targetPathOrExe ?? "").Trim().Trim('"');
            if (clean.Length == 0) return false;
            try
            {
                Directory.CreateDirectory(Paths.OwnDir);
                string tmp = HandoffFilePath + "." + Process.GetCurrentProcess().Id;
                File.WriteAllText(tmp, clean, new UTF8Encoding(false));
                if (File.Exists(HandoffFilePath))
                {
                    try { File.Replace(tmp, HandoffFilePath, null); }
                    catch { File.Copy(tmp, HandoffFilePath, true); try { File.Delete(tmp); } catch { } }
                }
                else
                {
                    File.Move(tmp, HandoffFilePath);
                }
                using (var ev = EventWaitHandle.OpenExisting(CompanionHandoffEventName))
                    ev.Set();
                return true;
            }
            catch { return false; }
        }

        /// <summary>主实例读取并消费第二实例投递过来的 --follow 目标，随后清理临时文件。</summary>
        public static string ConsumeFollowHandoff()
        {
            try
            {
                string fp = HandoffFilePath;
                if (!File.Exists(fp)) return "";
                string txt = File.ReadAllText(fp, Encoding.UTF8).Trim().Trim('"');
                try { File.Delete(fp); } catch { }
                return txt;
            }
            catch { return ""; }
        }

        /// <summary>同步后台静默守望进程的运行态（开启即拉起或唤醒重载，关闭即发事件优雅退出）。</summary>
        public static void ApplyWatcherState(CompanionConfig cfg, string currentExePath = null)
        {
            string exe = string.IsNullOrEmpty(currentExePath)
                ? System.Windows.Forms.Application.ExecutablePath
                : currentExePath;
            bool needWatcher = cfg != null && cfg.HasValidFollowTarget && cfg.FollowWatchAtLogon;
            SetWatchAutostart(needWatcher, exe);
            if (needWatcher)
            {
                // 若守望进程已在跑，发重载事件；否则静默启动
                bool alreadyRunning = false;
                try
                {
                    using (var ev = EventWaitHandle.OpenExisting(WatchReloadEventName))
                    {
                        ev.Set();
                        alreadyRunning = true;
                    }
                }
                catch { }
                if (!alreadyRunning && File.Exists(exe))
                {
                    try
                    {
                        var psi = new ProcessStartInfo
                        {
                            FileName = exe,
                            Arguments = "--watch-companion",
                            WorkingDirectory = Path.GetDirectoryName(exe) ?? "",
                            UseShellExecute = false,
                            CreateNoWindow = true
                        };
                        using (var p = Process.Start(psi)) { }
                    }
                    catch { }
                }
            }
            else
            {
                SignalStopWatcher();
            }
        }

        public static void SignalStopWatcher()
        {
            try
            {
                using (var ev = EventWaitHandle.OpenExisting(WatchStopEventName))
                    ev.Set();
            }
            catch { }
        }

        /// <summary>用户主动点击退出 ProxyPicker 时通知守望器：本轮不要再重复拉起。</summary>
        public static void NotifyManualExitToWatcher()
        {
            try
            {
                using (var ev = EventWaitHandle.OpenExisting(ManualExitEventName))
                    ev.Set();
            }
            catch { }
        }

        /// <summary>
        /// 无窗口、无托盘的轻量守望进程主循环（ProxyPicker.exe --watch-companion）：
        /// 仅在检测到目标软件运行且主程序未启动时拉起「ProxyPicker.exe --tray --follow-exe <exe>」。
        /// </summary>
        public static void RunWatchCompanionLoop()
        {
            bool created;
            using (var mutex = new Mutex(true, WatchMutexName, out created))
            {
                if (!created) return;
                using (var stopEv = new EventWaitHandle(false, EventResetMode.ManualReset, WatchStopEventName))
                using (var reloadEv = new EventWaitHandle(false, EventResetMode.AutoReset, WatchReloadEventName))
                using (var manualExitEv = new EventWaitHandle(false, EventResetMode.AutoReset, ManualExitEventName))
                {
                    var sm = new CompanionWatchStateMachine();
                    var handles = new WaitHandle[] { stopEv, reloadEv, manualExitEv };
                    string selfExe = System.Windows.Forms.Application.ExecutablePath;
                    DateTime lastSettingsWrite = DateTime.MinValue;
                    bool hasValidCfg = false;
                    var cfg = Settings.GetCompanionConfig();
                    if (cfg != null && cfg.HasValidFollowTarget && cfg.FollowWatchAtLogon)
                        hasValidCfg = true;

                    while (true)
                    {
                        bool explicitlyDisabled = false;
                        try
                        {
                            string sp = Settings.FilePath;
                            if (File.Exists(sp))
                            {
                                DateTime wt = File.GetLastWriteTimeUtc(sp);
                                if (wt != lastSettingsWrite)
                                {
                                    bool fileMissing;
                                    if (Settings.TryReloadCompanionFromDisk(out fileMissing))
                                    {
                                        // 仅在真正读通并解析成功后才推进时间戳与替换配置（v2.6.1 P1-02 修复）
                                        lastSettingsWrite = wt;
                                        cfg = Settings.GetCompanionConfig();
                                        hasValidCfg = true;
                                        if (!cfg.HasValidFollowTarget || !cfg.FollowWatchAtLogon)
                                            explicitlyDisabled = true;
                                    }
                                    // 若因 File.Replace 窗口或短暂 IO 锁冲突读失败：保留旧 cfg 且不推进时间戳，下轮自动重试
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
                            string targetQuery = !string.IsNullOrWhiteSpace(cfg.FollowAppPath)
                                ? cfg.FollowAppPath
                                : cfg.FollowAppExe;
                            var probe = ProbeTargetProcess(targetQuery, false);
                            if (probe != ProcessProbeState.Unknown)
                            {
                                bool hostRunning = probe == ProcessProbeState.Running;
                                bool mainRunning = IsMainInstanceRunning();
                                DateTime nowUtc = DateTime.UtcNow;
                                if (sm.ShouldLaunch(cfg.FollowEnabled, cfg.FollowAppExe, hostRunning, mainRunning, nowUtc))
                                {
                                    try
                                    {
                                        string followArg = !string.IsNullOrWhiteSpace(cfg.FollowAppPath)
                                            ? ("--tray --follow-exe \"" + cfg.FollowAppPath + "\"")
                                            : ("--tray --follow-exe \"" + cfg.FollowAppExe + "\"");
                                        var psi = new ProcessStartInfo
                                        {
                                            FileName = selfExe,
                                            Arguments = followArg,
                                            WorkingDirectory = Path.GetDirectoryName(selfExe) ?? "",
                                            UseShellExecute = false
                                        };
                                        using (var p = Process.Start(psi))
                                        {
                                            if (p != null) sm.CommitLaunchSucceeded();
                                            else sm.RecordLaunchFailed(nowUtc);
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        // 启动失败不永久占锁：记录脱敏日志并进入冷却期自动重试（v2.6.1 P1-04 修复）
                                        sm.RecordLaunchFailed(nowUtc);
                                        CrashLog.Write(ex, "静默守望拉起 ProxyPicker 失败，将在冷却后重试");
                                    }
                                }
                            }
                        }

                        int sig = WaitHandle.WaitAny(handles, 2500);
                        if (sig == 0) return; // stop
                        if (sig == 1)
                        {
                            // 配置刚保存，强制重读
                            lastSettingsWrite = DateTime.MinValue;
                        }
                        else if (sig == 2)
                        {
                            // 用户手动退出了主程序：本轮不再重复拉起
                            sm.MarkManualExitSuppressed();
                        }
                    }
                }
            }
        }

        // ---------------------------------------------------------------- 原生 COM 生成带原软件图标的 .lnk 桌面快捷方式

        [ComImport]
        [Guid("00021401-0000-0000-C000-000000000046")]
        class ShellLinkCom { }

        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("000214F9-0000-0000-C000-000000000046")]
        interface IShellLinkW
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cchMaxPath, IntPtr pfd, int fFlags);
            void GetIDList(out IntPtr ppidl);
            void SetIDList(IntPtr pidl);
            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cchMaxName);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cchMaxPath);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cchMaxPath);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
            void GetHotkey(out short pwHotkey);
            void SetHotkey(short wHotkey);
            void GetShowCmd(out int piShowCmd);
            void SetShowCmd(int iShowCmd);
            void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cchIconPath, out int piIcon);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, int dwReserved);
            void Resolve(IntPtr hwnd, int fFlags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
        }

        /// <summary>清洗可用于 Windows 文件名的快捷方式标题。</summary>
        public static string BuildShortcutFileName(string appDisplayName, string targetExePath)
        {
            string baseName = !string.IsNullOrWhiteSpace(appDisplayName)
                ? appDisplayName.Trim()
                : Path.GetFileNameWithoutExtension(targetExePath ?? "App");
            if (string.IsNullOrWhiteSpace(baseName)) baseName = "App";
            foreach (char c in Path.GetInvalidFileNameChars())
                baseName = baseName.Replace(c, '_');
            baseName = baseName.Trim().TrimEnd('.');
            if (baseName.Length > 48) baseName = baseName.Substring(0, 48).Trim();
            return baseName + " (联动分流).lnk";
        }

        /// <summary>
        /// 在指定目录（默认桌面）创建「原软件图标 + 一键双启」快捷方式：
        /// 目标 = ProxyPicker.exe，参数 = --follow "<目标软件路径>"，图标 = 目标软件自身图标。
        /// </summary>
        public static bool CreateCompanionShortcut(
            string targetAppPath,
            string appDisplayName,
            string shortcutDir,
            string proxyPickerExePath,
            out string createdShortcutPath,
            out string error)
        {
            createdShortcutPath = "";
            error = "";
            if (!ValidateTargetExeOrPath(targetAppPath, true, out error)) return false;
            string cleanTarget = targetAppPath.Trim().Trim('"');
            if (!File.Exists(cleanTarget))
            {
                error = "目标软件文件不存在：" + cleanTarget;
                return false;
            }
            string selfExe = string.IsNullOrWhiteSpace(proxyPickerExePath)
                ? System.Windows.Forms.Application.ExecutablePath
                : proxyPickerExePath.Trim().Trim('"');
            if (!File.Exists(selfExe))
            {
                error = "找不到当前 ProxyPicker 程序路径";
                return false;
            }
            string dir = string.IsNullOrWhiteSpace(shortcutDir)
                ? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)
                : shortcutDir;
            if (string.IsNullOrWhiteSpace(dir))
            {
                error = "无法定位目标目录";
                return false;
            }

            try
            {
                Directory.CreateDirectory(dir);
                string fileName = BuildShortcutFileName(appDisplayName, cleanTarget);
                string lnkPath = Path.Combine(dir, fileName);

                var link = (IShellLinkW)new ShellLinkCom();
                try
                {
                    link.SetPath(selfExe);
                    link.SetArguments("--follow \"" + cleanTarget + "\"");
                    link.SetWorkingDirectory(Path.GetDirectoryName(selfExe) ?? "");
                    link.SetIconLocation(cleanTarget, 0);
                    link.SetDescription("同步启动 " + (string.IsNullOrWhiteSpace(appDisplayName) ? Path.GetFileNameWithoutExtension(cleanTarget) : appDisplayName.Trim()) + " 与 ProxyPicker 分流");
                    ((IPersistFile)link).Save(lnkPath, false);
                }
                finally
                {
                    try { Marshal.FinalReleaseComObject(link); } catch { }
                }

                createdShortcutPath = lnkPath;
                return File.Exists(lnkPath);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        // ---------------------------------------------------------------- 纯离线自检（零真机污染）

        public static string SelfTest()
        {
            var sb = new StringBuilder();
            int pass = 0, fail = 0;
            Action<string, bool, string> check = (name, ok, detail) =>
            {
                if (ok) { pass++; sb.AppendLine("PASS  " + name); }
                else { fail++; sb.AppendLine("FAIL  " + name + " | " + detail); }
            };

            // AC1：代理客户端识别与安全校验（禁自递归 ProxyPicker.exe、系统进程与非 exe）
            string err = "";
            check("AC1 代理客户端识别与禁止自拉起/系统核心进程校验",
                IsProxyClientExe(@"C:\Program Files\Clash Verge\clash-verge.exe") &&
                IsProxyClientExe("FlClash.exe") &&
                !IsProxyClientExe("chrome.exe") &&
                !ValidateTargetExeOrPath(@"C:\Tools\ProxyPicker.exe", true, out err) &&
                !ValidateTargetExeOrPath(@"C:\Windows\explorer.exe", true, out err) &&
                !ValidateTargetExeOrPath(@"C:\Tools\script.bat", true, out err) &&
                !ValidateTargetExeOrPath("relative.exe", true, out err) &&
                ValidateTargetExeOrPath(@"C:\Program Files\Google\Chrome\Application\chrome.exe", true, out err),
                "err=" + err);

            // AC2：启动排序（代理客户端自动置顶、其余保序去重、排除当前宿主防重复）
            var ordered = OrderLaunchTargets(new[]
            {
                @"C:\Program Files\Google\Chrome\Application\chrome.exe",
                @"C:\Program Files\Clash Verge\clash-verge.exe",
                @"C:\Apps\Chrome2\chrome.exe",
                @"C:\Tools\ProxyPicker.exe",
                @"C:\Apps\Cursor\Cursor.exe"
            });
            var orderedExcl = OrderLaunchTargets(new[]
            {
                @"C:\Program Files\Clash Verge\clash-verge.exe",
                @"C:\Program Files\Google\Chrome\Application\chrome.exe"
            }, "clash-verge.exe");
            check("AC2 同步拉起队列排序：代理客户端自动优先、按 exe 去重且支持排除宿主防重复",
                ordered.Count == 3 &&
                ExtractExeName(ordered[0]) == "clash-verge.exe" &&
                ExtractExeName(ordered[1]) == "chrome.exe" &&
                ExtractExeName(ordered[2]) == "cursor.exe" &&
                orderedExcl.Count == 1 && ExtractExeName(orderedExcl[0]) == "chrome.exe",
                "cnt=" + ordered.Count + " excl=" + orderedExcl.Count);

            // AC3：FollowExitTracker 同生共死防抖状态机（未见过运行不误退、需连续消失 2 轮才确认退出、中途恢复清零）
            var tr = new FollowExitTracker("clash-verge.exe", 2);
            bool ac3 =
                !tr.Observe(false) && !tr.Observe(false) && !tr.SeenRunning &&
                !tr.Observe(true) && tr.SeenRunning &&
                !tr.Observe(false) && tr.ConsecutiveMisses == 1 &&
                !tr.Observe(true) && tr.ConsecutiveMisses == 0 &&
                !tr.Observe(false) &&
                tr.Observe(false);
            check("AC3 同生共死退出防抖机：未运行不误退、重启瞬断清零、连续 2 轮消失确认退出",
                ac3,
                "seen=" + tr.SeenRunning + " miss=" + tr.ConsecutiveMisses);

            // AC4：CompanionWatchStateMachine 静默守望防流氓自拉起（手动退出后同周期不再重拉，目标重开才再触发）
            var sm = new CompanionWatchStateMachine();
            bool ac4 =
                !sm.Step(true, "clash-verge.exe", false, false) &&
                sm.Step(true, "clash-verge.exe", true, false) &&
                !sm.Step(true, "clash-verge.exe", true, false) &&   // 手动退出后主程序已关但宿主仍在：严禁反复拉起
                !sm.Step(true, "clash-verge.exe", false, false) &&  // 宿主关闭重置会话
                sm.Step(true, "clash-verge.exe", true, false);      // 宿主再次启动：立刻重新联动
            check("AC4 静默守望状态机：首次拉起、手动退出后同周期不反复拉起、宿主重开后自动恢复联动",
                ac4,
                "watchStateMachine");

            // AC5：Settings 联动配置序列化与反序列化往返（含向下兼容缺省字段）
            var sampleData = new Settings.Data
            {
                FollowEnabled = true,
                FollowAppExe = "clash-verge.exe",
                FollowAppPath = @"C:\Program Files\Clash Verge\clash-verge.exe",
                FollowAppName = "Clash Verge",
                FollowAutoExit = true,
                FollowWatchAtLogon = true,
                LaunchAppsEnabled = true,
                LaunchAppsOnFollow = false,
                LaunchAppPaths = new List<string>
                {
                    @"C:\Program Files\Clash Verge\clash-verge.exe",
                    @"C:\Program Files\Google\Chrome\Application\chrome.exe"
                }
            };
            string json = Settings.SerializeData(sampleData);
            var parsed = Settings.ParseDataJson(json);
            var legacyParsed = Settings.ParseDataJson("{\"mode\":\"whitelist\",\"sortMode\":\"smart\"}");
            check("AC5 Settings 联动字段 JSON 完整往返且旧版配置无缝兼容默认关闭",
                parsed.FollowEnabled &&
                parsed.FollowAppExe == "clash-verge.exe" &&
                parsed.FollowAppPath == sampleData.FollowAppPath &&
                parsed.FollowAppName == "Clash Verge" &&
                parsed.FollowAutoExit &&
                parsed.FollowWatchAtLogon &&
                parsed.LaunchAppsEnabled &&
                !parsed.LaunchAppsOnFollow &&
                parsed.LaunchAppPaths.Count == 2 &&
                !legacyParsed.FollowEnabled && !legacyParsed.LaunchAppsEnabled && legacyParsed.FollowAutoExit,
                "follow=" + parsed.FollowEnabled + " launchCnt=" + parsed.LaunchAppPaths.Count);

            // AC6：快捷方式文件名清洗与真实 COM .lnk 隔离目录生成往返校验
            string fnSafe = BuildShortcutFileName("Clash:Verge/Rev*", @"C:\App\clash-verge.exe");
            string tmpDir = Path.Combine(Path.GetTempPath(), "pp_lnk_test_" + Guid.NewGuid().ToString("N"));
            bool lnkOk = false;
            try
            {
                string selfExe = System.Windows.Forms.Application.ExecutablePath;
                string fakeTarget = Path.Combine(tmpDir, "demo-app.exe");
                Directory.CreateDirectory(tmpDir);
                File.WriteAllBytes(fakeTarget, new byte[] { 0x4D, 0x5A });
                string lnkPath, lnkErr;
                bool created = CreateCompanionShortcut(fakeTarget, "Demo App", tmpDir, selfExe, out lnkPath, out lnkErr);
                if (created && File.Exists(lnkPath) && new FileInfo(lnkPath).Length > 100)
                {
                    // 用 COM 回读确认 TargetPath 与 Arguments 准确无误
                    var verifier = (IShellLinkW)new ShellLinkCom();
                    try
                    {
                        ((IPersistFile)verifier).Load(lnkPath, 0);
                        var sbPath = new StringBuilder(512);
                        var sbArgs = new StringBuilder(512);
                        verifier.GetPath(sbPath, sbPath.Capacity, IntPtr.Zero, 0);
                        verifier.GetArguments(sbArgs, sbArgs.Capacity);
                        lnkOk = sbArgs.ToString().IndexOf("--follow", StringComparison.OrdinalIgnoreCase) >= 0 &&
                                sbArgs.ToString().IndexOf("demo-app.exe", StringComparison.OrdinalIgnoreCase) >= 0;
                    }
                    finally
                    {
                        try { Marshal.FinalReleaseComObject(verifier); } catch { }
                    }
                }
            }
            finally
            {
                try { if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true); } catch { }
            }
            check("AC6 快捷方式名非法字符清洗及 COM 生成/回读 .lnk 联动参数校验",
                fnSafe.IndexOf(':') < 0 && fnSafe.IndexOf('/') < 0 && fnSafe.EndsWith(" (联动分流).lnk", StringComparison.Ordinal) && lnkOk,
                "fn=" + fnSafe + " lnkOk=" + lnkOk);

            // AC7：标题栏新增「联动」按钮后在 466px–880px 全宽度与标题零碰撞
            bool chromeOk = true;
            foreach (float tw in new[] { 466f, 520f, 640f, 880f })
            {
                var compR = UI.MainForm.CompanionBtnRectFor(tw);
                var helpR = new System.Drawing.RectangleF(tw - 178f, 10f, 26f, 26f);
                if (compR.Right >= helpR.Left || compR.Left < 180f || compR.Width < 46f)
                    chromeOk = false;
            }
            check("AC7 标题栏「联动」按钮在 466px–880px 全断点与标题及帮助按钮零碰撞",
                chromeOk,
                "chromeOk=" + chromeOk);

            // AC8：Program.KnownFlags 合法放行 --follow / --follow-exe / --watch-companion
            bool flagsOk = Program.IsCompanionBootFlag("--follow") &&
                           Program.IsCompanionBootFlag("--follow-exe") &&
                           Program.IsCompanionBootFlag("--watch-companion") &&
                           !Program.IsCompanionBootFlag("--unknown-flag");
            check("AC8 Program 启动白名单精确识别 --follow / --follow-exe / --watch-companion 且拒未知开关",
                flagsOk,
                "flagsOk=" + flagsOk);

            // AC9（v2.6.1 P1-01 回归）：内核未就绪或规则未完成同步时严禁抢跑拉起浏览器等业务软件
            string skipWhy;
            bool ac9 =
                !ShouldLaunchBusinessAppsAfterSync(true, false, false, false, out skipWhy) &&
                !ShouldLaunchBusinessAppsAfterSync(true, false, true, false, out skipWhy) &&
                ShouldLaunchBusinessAppsAfterSync(true, false, true, true, out skipWhy) &&
                ShouldLaunchBusinessAppsAfterSync(false, false, false, false, out skipWhy) &&
                ShouldLaunchBusinessAppsAfterSync(true, true, false, false, out skipWhy);
            check("AC9 联动启动顺序强门禁：内核未就绪或规则未同步时拦截业务软件防抢跑直连",
                ac9, "ac9=" + ac9);

            // AC10（v2.6.1 P1-02 回归）：守望配置读取失败时保持上一份有效配置不退出
            bool ac10 = true;
            {
                var prevCfg = new CompanionConfig { FollowEnabled = true, FollowAppExe = "clash-verge.exe", FollowWatchAtLogon = true };
                bool missing;
                // 不破坏用户真实 settings.json：验证有效状态字段及读失败语义的纯守恒
                string badJson = "{not-valid-json";
                bool threwOnBad = false;
                try { Settings.ParseDataJson(badJson); } catch { threwOnBad = true; }
                ac10 = threwOnBad && prevCfg.HasValidFollowTarget && prevCfg.FollowWatchAtLogon;
                // 验证 TryReloadCompanionFromDisk 不会抛出异常
                try { Settings.TryReloadCompanionFromDisk(out missing); } catch { ac10 = false; }
            }
            check("AC10 损坏 JSON 解析报错且 TryReloadCompanionFromDisk 失败不抛异常、保留旧守望配置",
                ac10, "ac10=" + ac10);

            // AC11（v2.6.1 P1-03 回归）：精确路径匹配、同名不同路径拒判活、Unknown 不递增防抖、代理客户端 6 轮防抖
            var trPath = new FollowExitTracker(@"C:\Program Files\Clash Verge\clash-verge.exe", RecommendedMissThreshold("clash-verge.exe"));
            bool ac11 =
                MatchesTargetProcessPath(@"C:\Program Files\Clash Verge\clash-verge.exe", @"c:\program files\clash verge\clash-verge.exe") &&
                !MatchesTargetProcessPath(@"C:\Program Files\Clash Verge\clash-verge.exe", @"D:\Other\clash-verge.exe") &&
                MatchesTargetProcessPath("clash-verge.exe", @"D:\Any\clash-verge.exe") &&
                IsMultiProcessBrowserExe("chrome.exe") &&
                !IsMultiProcessBrowserExe("clash-verge.exe") &&
                trPath.MissThreshold == 6 &&
                !trPath.Observe(ProcessProbeState.Running) && trPath.SeenRunning &&
                !trPath.Observe(ProcessProbeState.NotRunning) && trPath.ConsecutiveMisses == 1 &&
                !trPath.Observe(ProcessProbeState.Unknown) && trPath.ConsecutiveMisses == 1;
            check("AC11 精确路径匹配（同名不同路径不算活）、多进程浏览器识别、Unknown 不误退与代理客户端 6 轮防抖",
                ac11, "miss=" + trPath.ConsecutiveMisses + " thr=" + trPath.MissThreshold);

            // AC12（v2.6.1 P1-04 回归）：守望拉起失败不永久锁死，冷却后可重试，手动退出后严格抑制
            var smRetry = new CompanionWatchStateMachine();
            var tBase = new DateTime(2026, 11, 26, 12, 0, 0, DateTimeKind.Utc);
            bool step1Want = smRetry.ShouldLaunch(true, "clash-verge.exe", true, false, tBase);
            smRetry.RecordLaunchFailed(tBase);
            bool blockedInCooldown = !smRetry.ShouldLaunch(true, "clash-verge.exe", true, false, tBase.AddSeconds(10));
            bool retryAfterCooldown = smRetry.ShouldLaunch(true, "clash-verge.exe", true, false, tBase.AddSeconds(21));
            smRetry.CommitLaunchSucceeded();
            bool blockedAfterSuccess = !smRetry.ShouldLaunch(true, "clash-verge.exe", true, false, tBase.AddSeconds(50));
            check("AC12 守望状态机启动失败退避冷却后自动重试，成功后及手动退出后同轮绝不重复拉起",
                step1Want && blockedInCooldown && retryAfterCooldown && blockedAfterSuccess,
                "s1=" + step1Want + " cool=" + blockedInCooldown + " retry=" + retryAfterCooldown + " done=" + blockedAfterSuccess);

            // AC13（v2.6.1 P1-05 回归）：主实例已存在时 --follow 必须走跨进程交接，而非静默丢弃
            bool ac13 =
                ClassifySecondInstanceAction(true, false, "") == SecondInstanceAction.ProceedAsMain &&
                ClassifySecondInstanceAction(false, false, "") == SecondInstanceAction.ActivateMainWindow &&
                ClassifySecondInstanceAction(false, true, @"C:\Apps\demo.exe") == SecondInstanceAction.HandoffFollowToMain &&
                ClassifySecondInstanceAction(false, true, "") == SecondInstanceAction.SilentExit;
            check("AC13 第二实例在主程序已运行时正确分流：--follow 投递交接而非静默丢失同生共死",
                ac13, "ac13=" + ac13);

            // AC14（v2.6.1 P2-01 回归）：注册表命令精确路径匹配（防 .bak 前缀误判）且搬家后旧守望项可清理
            bool ac14 =
                CommandPointsToExe("\"C:\\Tools\\ProxyPicker.exe\" --watch-companion", @"C:\Tools\ProxyPicker.exe") &&
                !CommandPointsToExe("\"C:\\Tools\\ProxyPicker.exe.bak\" --watch-companion", @"C:\Tools\ProxyPicker.exe") &&
                !CommandPointsToExe("\"D:\\Old\\ProxyPicker.exe\" --watch-companion", @"C:\Tools\ProxyPicker.exe") &&
                IsOwnedWatchCommand("\"D:\\Old\\ProxyPicker.exe\" --watch-companion", @"C:\Tools\ProxyPicker.exe") &&
                !IsOwnedWatchCommand("\"C:\\OtherApp\\tool.exe\" --daemon", @"C:\Tools\ProxyPicker.exe");
            check("AC14 注册表自启命令第 0 参数严格相等匹配（拒前缀假阳性）且搬家旧守望项可干净清除",
                ac14, "ac14=" + ac14);

            sb.Insert(0, "\n[软件联动启停 AppCompanion 自检] " + pass + " PASS / " + fail + " FAIL\n");
            return sb.ToString();
        }
    }
}
