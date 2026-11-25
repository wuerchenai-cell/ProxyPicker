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

        /// <summary>轻量检测指定进程名（如 clash-verge.exe）当前是否至少有一个运行中的实例。</summary>
        public static bool IsProcessRunning(string exeOrPath)
        {
            string exe = ExtractExeName(exeOrPath);
            if (exe.Length <= 4 || !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return false;
            string procName = exe.Substring(0, exe.Length - 4);
            try
            {
                var procs = Process.GetProcessesByName(procName);
                bool alive = procs != null && procs.Length > 0;
                if (procs != null)
                {
                    foreach (var p in procs) { try { p.Dispose(); } catch { } }
                }
                return alive;
            }
            catch { return false; }
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
            public readonly int MissThreshold;
            public bool SeenRunning { get; private set; }
            public int ConsecutiveMisses { get; private set; }

            public FollowExitTracker(string targetExe, int missThreshold = 2)
            {
                TargetExe = ExtractExeName(targetExe);
                MissThreshold = Math.Max(1, missThreshold);
            }

            /// <summary>推进一步探测结果；返回 true 表示确认宿主已彻底关闭，应执行同生共死还原退出。</summary>
            public bool Observe(bool isRunningNow)
            {
                if (TargetExe.Length == 0) return false;
                if (isRunningNow)
                {
                    SeenRunning = true;
                    ConsecutiveMisses = 0;
                    return false;
                }
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
        /// 轻量后台守望器触发决策状态机（纯逻辑，支持离线单测）：
        /// - 宿主首次从「未运行 → 运行中」且主程序未运行时，返回 true 拉起 ProxyPicker；
        /// - 同一轮宿主运行周期内若用户手动退出了 ProxyPicker，绝不再强行重新拉起；
        /// - 宿主关闭退出后再重新打开时，自动重置会话并再次联动拉起。
        /// </summary>
        internal sealed class CompanionWatchStateMachine
        {
            public bool TriggeredForCurrentHostRun { get; private set; }
            string _lastTargetExe = "";

            public void ResetForTarget(string targetExe)
            {
                string norm = ExtractExeName(targetExe);
                if (!string.Equals(_lastTargetExe, norm, StringComparison.OrdinalIgnoreCase))
                {
                    _lastTargetExe = norm;
                    TriggeredForCurrentHostRun = false;
                }
            }

            public void MarkManualExitSuppressed()
            {
                TriggeredForCurrentHostRun = true;
            }

            public bool Step(bool followEnabled, string targetExe, bool hostRunning, bool mainRunning)
            {
                ResetForTarget(targetExe);
                if (!followEnabled || _lastTargetExe.Length == 0)
                {
                    TriggeredForCurrentHostRun = false;
                    return false;
                }
                if (!hostRunning)
                {
                    // 目标软件已关闭：解除本轮已触发锁，等用户下次打开目标软件时重新联动
                    TriggeredForCurrentHostRun = false;
                    return false;
                }
                if (mainRunning)
                {
                    TriggeredForCurrentHostRun = true;
                    return false;
                }
                if (TriggeredForCurrentHostRun)
                {
                    // 本轮宿主仍在运行但主程序已退出（通常是用户手动退出），不再流氓反复拉起
                    return false;
                }
                TriggeredForCurrentHostRun = true;
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

        static bool PointsToCurrentExe(string command, string exePath)
        {
            if (string.IsNullOrEmpty(command) || string.IsNullOrEmpty(exePath)) return false;
            string s = command.Trim().Trim('"');
            return s.StartsWith(exePath, StringComparison.OrdinalIgnoreCase);
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
                        if (PointsToCurrentExe(Convert.ToString(v), exe))
                            k.DeleteValue(WatchRunName, false);
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
                    var cfg = Settings.GetCompanionConfig();

                    while (true)
                    {
                        try
                        {
                            string sp = Settings.FilePath;
                            if (File.Exists(sp))
                            {
                                DateTime wt = File.GetLastWriteTimeUtc(sp);
                                if (wt != lastSettingsWrite)
                                {
                                    lastSettingsWrite = wt;
                                    Settings.ReloadCompanionFromDisk();
                                    cfg = Settings.GetCompanionConfig();
                                }
                            }
                        }
                        catch { }

                        if (!cfg.HasValidFollowTarget || !cfg.FollowWatchAtLogon)
                            return;

                        bool hostRunning = IsProcessRunning(cfg.FollowAppExe);
                        bool mainRunning = IsMainInstanceRunning();
                        if (sm.Step(cfg.FollowEnabled, cfg.FollowAppExe, hostRunning, mainRunning))
                        {
                            try
                            {
                                var psi = new ProcessStartInfo
                                {
                                    FileName = selfExe,
                                    Arguments = "--tray --follow-exe \"" + cfg.FollowAppExe + "\"",
                                    WorkingDirectory = Path.GetDirectoryName(selfExe) ?? "",
                                    UseShellExecute = false
                                };
                                using (var p = Process.Start(psi)) { }
                            }
                            catch { }
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

            sb.Insert(0, "\n[软件联动启停 AppCompanion 自检] " + pass + " PASS / " + fail + " FAIL\n");
            return sb.ToString();
        }
    }
}
