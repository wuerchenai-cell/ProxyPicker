using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using ProxyPicker.Domain.Interfaces;
using ProxyPicker.Domain.Models;
using ProxyPicker.Domain.Services;

namespace ProxyPicker.Platform.Windows
{
    /// <summary>
    /// Windows 进程探测、优雅关闭与应用启动器（集中收口 psapi / kernel32 / user32 P/Invoke）。
    /// </summary>
    public sealed class NativeProcessProbe : IProcessProbe, IAppLauncher
    {
        public const string MainMutexName = @"Local\ProxyPicker.SingleInstance";
        public const string WatchMutexName = @"Local\ProxyPicker.CompanionWatch";

        const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        const int ERROR_INSUFFICIENT_BUFFER = 122;
        const uint WM_CLOSE = 0x0010;

        [DllImport("psapi.dll", SetLastError = true)]
        static extern bool EnumProcesses(uint[] pProcessIds, uint cb, out uint lpcbNeeded);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool QueryFullProcessImageNameW(IntPtr hProcess, uint dwFlags, StringBuilder lpExeName, ref uint lpdwSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool CloseHandle(IntPtr hObject);

        delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll")]
        static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        public static bool TryQueryImagePathByPid(int pid, out string fullPath)
        {
            fullPath = "";
            if (pid <= 0) return false;
            IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
            if (h == IntPtr.Zero) return false;
            try
            {
                int cap = 1024;
                for (int attempt = 0; attempt < 6 && cap <= 65536; attempt++)
                {
                    var sb = new StringBuilder(cap);
                    uint size = (uint)cap;
                    if (QueryFullProcessImageNameW(h, 0, sb, ref size))
                    {
                        fullPath = sb.ToString();
                        return fullPath.Length > 0;
                    }
                    if (Marshal.GetLastWin32Error() != ERROR_INSUFFICIENT_BUFFER) return false;
                    cap *= 2;
                }
                return false;
            }
            finally
            {
                CloseHandle(h);
            }
        }

        public ProcessProbeState ProbeTarget(string exeOrPath, bool requireVisibleWindowForMultiProc)
        {
            string raw = (exeOrPath ?? "").Trim().Trim('"');
            string exe = CompanionPolicyEngine.ExtractExeName(raw);
            if (exe.Length <= 4 || !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                return ProcessProbeState.NotRunning;

            bool hasFullPath = false;
            try { hasFullPath = Path.IsPathRooted(raw); } catch { hasFullPath = false; }

            string procName = exe.Substring(0, exe.Length - 4);
            Process[] procs;
            try { procs = Process.GetProcessesByName(procName); }
            catch { return ProcessProbeState.Unknown; }

            if (procs == null || procs.Length == 0) return ProcessProbeState.NotRunning;

            var matchedPids = new HashSet<int>();
            bool sawUnreadablePath = false;
            try
            {
                foreach (var p in procs)
                {
                    try
                    {
                        if (!hasFullPath) matchedPids.Add(p.Id);
                        else
                        {
                            string imgPath;
                            if (TryQueryImagePathByPid(p.Id, out imgPath))
                            {
                                if (CompanionPolicyEngine.MatchesTargetProcessPath(raw, imgPath))
                                    matchedPids.Add(p.Id);
                            }
                            else sawUnreadablePath = true;
                        }
                    }
                    catch { sawUnreadablePath = true; }
                    finally { try { p.Dispose(); } catch { } }
                }
            }
            catch { return ProcessProbeState.Unknown; }

            if (matchedPids.Count == 0)
                return sawUnreadablePath ? ProcessProbeState.Unknown : ProcessProbeState.NotRunning;

            if (requireVisibleWindowForMultiProc && CompanionPolicyEngine.IsMultiProcessBrowserExe(exe))
                return HasVisibleWindowForPids(matchedPids) ? ProcessProbeState.Running : ProcessProbeState.NotRunning;

            return ProcessProbeState.Running;
        }

        public bool IsMainInstanceRunning()
        {
            return IsNamedMutexAlive(MainMutexName);
        }

        public bool IsWatcherRunning()
        {
            return IsNamedMutexAlive(WatchMutexName);
        }

        static bool IsNamedMutexAlive(string name)
        {
            try
            {
                using (var m = Mutex.OpenExisting(name))
                    return m != null;
            }
            catch (WaitHandleCannotBeOpenedException) { return false; }
            catch (UnauthorizedAccessException) { return true; }
            catch { return false; }
        }

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
            catch { return true; }
            return found;
        }

        public Dictionary<string, RunningProcessInfo> ScanRunningProcesses()
        {
            var map = new Dictionary<string, RunningProcessInfo>(StringComparer.OrdinalIgnoreCase);
            uint[] pids = new uint[4096];
            uint needed = 0;
            for (int attempt = 0; attempt < 5; attempt++)
            {
                if (!EnumProcesses(pids, (uint)(pids.Length * sizeof(uint)), out needed))
                    return map;
                if (needed < (uint)(pids.Length * sizeof(uint))) break;
                if (pids.Length >= 65536) break;
                pids = new uint[pids.Length * 2];
            }
            int count = (int)(needed / sizeof(uint));
            var byDir = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < count; i++)
            {
                uint pid = pids[i];
                if (pid == 0) continue;
                string full;
                if (!TryQueryImagePathByPid((int)pid, out full)) continue;
                int slash = full.LastIndexOf('\\');
                string exe = (slash >= 0 ? full.Substring(slash + 1) : full).ToLowerInvariant();
                if (exe.Length == 0) continue;

                RunningProcessInfo info;
                if (!map.TryGetValue(exe, out info))
                {
                    info = new RunningProcessInfo { Exe = exe, FullPath = full, Count = 0 };
                    map[exe] = info;
                }
                info.Count++;
                info.AllPaths.Add(full);

                string dir = slash >= 0 ? full.Substring(0, slash) : "";
                if (dir.Length > 0 && !IsSystemDir(dir))
                {
                    HashSet<string> peers;
                    if (!byDir.TryGetValue(dir, out peers))
                    {
                        peers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        byDir[dir] = peers;
                    }
                    peers.Add(exe);
                }
            }

            foreach (var info in map.Values)
            {
                int slash = info.FullPath.LastIndexOf('\\');
                string dir = slash >= 0 ? info.FullPath.Substring(0, slash) : "";
                HashSet<string> peers;
                if (dir.Length > 0 && byDir.TryGetValue(dir, out peers))
                {
                    foreach (var p in peers)
                        if (!p.Equals(info.Exe, StringComparison.OrdinalIgnoreCase))
                            info.DirPeers.Add(p);
                }
            }
            return map;
        }

        /// <summary>
        /// 优雅关闭目标软件（v3.0 四向联动方向 4 核心实现）：
        /// 优先对目标进程所属全部窗口发送 WM_CLOSE 通知程序自行保存并清理（例如代理客户端还原 TUN/系统代理）；
        /// 在 timeoutMs 内轮询确认退出，不粗暴强杀用户编辑中的软件。
        /// </summary>
        public bool TryCloseGracefully(string exeOrPath, int timeoutMs, out string detail)
        {
            detail = "";
            string raw = (exeOrPath ?? "").Trim().Trim('"');
            string exe = CompanionPolicyEngine.ExtractExeName(raw);
            if (exe.Length <= 4 || !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                detail = "无效目标名";
                return false;
            }
            bool hasFullPath = false;
            try { hasFullPath = Path.IsPathRooted(raw); } catch { hasFullPath = false; }

            string procName = exe.Substring(0, exe.Length - 4);
            var targetProcs = new List<Process>();
            var targetPids = new HashSet<int>();
            try
            {
                var all = Process.GetProcessesByName(procName);
                foreach (var p in all)
                {
                    bool keep = !hasFullPath;
                    if (hasFullPath)
                    {
                        string img;
                        if (TryQueryImagePathByPid(p.Id, out img) && CompanionPolicyEngine.MatchesTargetProcessPath(raw, img))
                            keep = true;
                    }
                    if (keep)
                    {
                        targetProcs.Add(p);
                        targetPids.Add(p.Id);
                    }
                    else
                    {
                        try { p.Dispose(); } catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                detail = "读取进程列表失败：" + ex.Message;
                return false;
            }

            if (targetProcs.Count == 0)
            {
                detail = "目标未运行";
                return true;
            }

            try
            {
                // 1. 先发送 CloseMainWindow 与顶层窗口 WM_CLOSE
                foreach (var p in targetProcs)
                {
                    try { p.CloseMainWindow(); } catch { }
                }
                try
                {
                    EnumWindows((hWnd, lParam) =>
                    {
                        uint pid;
                        GetWindowThreadProcessId(hWnd, out pid);
                        if (targetPids.Contains((int)pid))
                            PostMessage(hWnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                        return true;
                    }, IntPtr.Zero);
                }
                catch { }

                var deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(1000, timeoutMs));
                while (DateTime.UtcNow < deadline)
                {
                    bool anyAlive = false;
                    foreach (var p in targetProcs)
                    {
                        try { if (!p.HasExited) { anyAlive = true; break; } }
                        catch { }
                    }
                    if (!anyAlive)
                    {
                        detail = "已优雅关闭 " + exe;
                        return true;
                    }
                    Thread.Sleep(150);
                }
                detail = exe + " 未在 " + timeoutMs + "ms 内响应优雅关闭（可能最小化在托盘或存在未保存确认窗）";
                return false;
            }
            finally
            {
                foreach (var p in targetProcs) { try { p.Dispose(); } catch { } }
            }
        }

        public bool TryLaunch(string fullPath, string arguments, bool useShellExecute, out bool alreadyRunning, out string error)
        {
            alreadyRunning = false;
            error = "";
            string clean = (fullPath ?? "").Trim().Trim('"');
            if (!File.Exists(clean))
            {
                error = "找不到程序文件：" + Path.GetFileName(clean);
                return false;
            }
            if (string.IsNullOrEmpty(arguments) && ProbeTarget(clean, false) == ProcessProbeState.Running)
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
                    Arguments = arguments ?? "",
                    WorkingDirectory = (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) ? dir : "",
                    UseShellExecute = useShellExecute,
                    CreateNoWindow = !useShellExecute
                };
                using (var p = Process.Start(psi))
                    return p != null || useShellExecute;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        static bool IsSystemDir(string dir)
        {
            string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            return !string.IsNullOrEmpty(win) && dir.StartsWith(win, StringComparison.OrdinalIgnoreCase);
        }
    }
}
