using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace ProxyPicker.Core
{
    /// <summary>
    /// 独立零干扰侧车探测引擎：
    /// 复用本机已有 Clash 客户端内置的 mihomo 内核可执行文件，
    /// 在系统临时目录中以无 TUN、无系统代理、随机回环端口（127.0.0.1）拉起临时只读实例，
    /// 从而在不切换主节点、不重载主规则、完全不影响用户正常浏览/工作的前提下，
    /// 对任意未切换节点乃至全量节点进行真实出口 IP、风险评分与主流服务连通性测试。
    /// </summary>
    internal static class ProbeSidecar
    {
        const int JobObjectExtendedLimitInformation = 9;
        const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;

        [StructLayout(LayoutKind.Sequential)]
        struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr CreateJobObjectW(IntPtr lpJobAttributes, string lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool SetInformationJobObject(IntPtr hJob, int infoClass,
            ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION lpJobObjectInfo, uint cbJobObjectInfoLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool CloseHandle(IntPtr hObject);

        /// <summary>创建开启 KILL_ON_JOB_CLOSE 的 Job Object；主进程异常强退时由内核级联回收子侧车。</summary>
        internal static IntPtr CreateKillOnCloseJob()
        {
            try
            {
                IntPtr hJob = CreateJobObjectW(IntPtr.Zero, null);
                if (hJob == IntPtr.Zero) return IntPtr.Zero;
                var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
                uint cb = (uint)Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
                if (!SetInformationJobObject(hJob, JobObjectExtendedLimitInformation, ref info, cb))
                {
                    CloseHandle(hJob);
                    return IntPtr.Zero;
                }
                return hJob;
            }
            catch { return IntPtr.Zero; }
        }

        static readonly object SessionLock = new object();
        static readonly List<SidecarSession> ActiveSessions = new List<SidecarSession>();
        static bool _exitHooked;

        public sealed class ApiJsonResult
        {
            public bool Ok;
            public int Status;
            public string Error = "";
            public Dictionary<string, object> Data;
        }

        public sealed class SidecarSession : IDisposable
        {
            public Process Process;
            public IntPtr JobHandle;
            public string TempDir = "";
            public string ApiHost = "127.0.0.1";
            public int ApiPort;
            public int ProxyPort;
            public string Secret = "";
            public WebProxy OutboundProxy;
            bool _disposed;

            public bool SwitchGlobalNode(string nodeName)
            {
                if (_disposed || string.IsNullOrWhiteSpace(nodeName)) return false;
                try
                {
                    string body = "{\"name\":" + Json.Quote(nodeName) + "}";
                    var res = HttpJson("PUT", "/proxies/GLOBAL", body, 4000);
                    return res.Ok;
                }
                catch { return false; }
            }

            public int MeasureDelay(string nodeName, int timeoutMs, out string error)
            {
                error = "";
                if (_disposed || string.IsNullOrWhiteSpace(nodeName))
                {
                    error = "会话已关闭";
                    return -1;
                }
                try
                {
                    string testUrl = Settings.DelayTestUrl;
                    bool useExp = Profile.ShouldUseExpected204(testUrl);
                    string url = Profile.BuildDelayRequestPath(nodeName, testUrl, timeoutMs, useExp);
                    var res = HttpJson("GET", url, null, timeoutMs + 1500);
                    if (useExp && res != null && !res.Ok && res.Status == 400)
                        res = HttpJson("GET", Profile.BuildDelayRequestPath(nodeName, testUrl, timeoutMs, false), null, timeoutMs + 1500);
                    if (!res.Ok || res.Data == null)
                    {
                        error = string.IsNullOrEmpty(res.Error) ? ("HTTP " + res.Status + " / 超时") : res.Error;
                        return -1;
                    }
                    object dv;
                    if (res.Data.TryGetValue("delay", out dv) && dv is double)
                    {
                        int ms = (int)Math.Round((double)dv);
                        if (ms > 0) return ms;
                    }
                    error = "延迟无响应";
                }
                catch (Exception ex) { error = ex.Message; }
                return -1;
            }

            public int MeasureDelay(string nodeName, string testUrl, int timeoutMs)
            {
                if (_disposed || string.IsNullOrWhiteSpace(nodeName)) return -1;
                try
                {
                    string target = string.IsNullOrEmpty(testUrl) ? Settings.DelayTestUrl : testUrl;
                    bool useExp = Profile.ShouldUseExpected204(target);
                    string url = Profile.BuildDelayRequestPath(nodeName, target, timeoutMs, useExp);
                    var res = HttpJson("GET", url, null, timeoutMs + 1500);
                    if (useExp && res != null && !res.Ok && res.Status == 400)
                        res = HttpJson("GET", Profile.BuildDelayRequestPath(nodeName, target, timeoutMs, false), null, timeoutMs + 1500);
                    if (!res.Ok || res.Data == null) return -1;
                    object dv;
                    if (res.Data.TryGetValue("delay", out dv) && dv is double)
                    {
                        int ms = (int)Math.Round((double)dv);
                        return ms > 0 ? ms : -1;
                    }
                }
                catch { }
                return -1;
            }

            public ApiJsonResult FetchProxies()
            {
                return HttpJson("GET", "/proxies", null, 4000);
            }

            ApiJsonResult HttpJson(string method, string path, string body, int timeoutMs)
            {
                var r = new ApiJsonResult();
                try
                {
                    var req = (HttpWebRequest)WebRequest.Create("http://" + ApiHost + ":" + ApiPort + path);
                    req.Method = method;
                    req.Timeout = timeoutMs;
                    req.ReadWriteTimeout = timeoutMs;
                    req.Proxy = null;
                    req.KeepAlive = false;
                    if (!string.IsNullOrEmpty(Secret))
                        req.Headers["Authorization"] = "Bearer " + Secret;
                    if (body != null)
                    {
                        byte[] bytes = Encoding.UTF8.GetBytes(body);
                        req.ContentType = "application/json";
                        req.ContentLength = bytes.Length;
                        using (var s = req.GetRequestStream()) s.Write(bytes, 0, bytes.Length);
                    }
                    using (var resp = (HttpWebResponse)req.GetResponse())
                    using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    {
                        r.Status = (int)resp.StatusCode;
                        string raw = sr.ReadToEnd();
                        r.Ok = r.Status >= 200 && r.Status < 300;
                        if (!string.IsNullOrWhiteSpace(raw))
                        {
                            var parsed = Json.Parse(raw);
                            r.Data = Json.AsObject(parsed);
                        }
                    }
                }
                catch (WebException we)
                {
                    var resp = we.Response as HttpWebResponse;
                    if (resp != null)
                    {
                        r.Status = (int)resp.StatusCode;
                        try { using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8)) r.Error = sr.ReadToEnd(); }
                        catch { r.Error = we.Message; }
                        finally { resp.Close(); }
                    }
                    else
                    {
                        r.Error = we.Message;
                    }
                }
                catch (Exception ex) { r.Error = ex.Message; }
                return r;
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                lock (SessionLock) ActiveSessions.Remove(this);
                try
                {
                    if (Process != null && !Process.HasExited)
                    {
                        Process.Kill();
                        Process.WaitForExit(1500);
                    }
                }
                catch { }
                try { if (JobHandle != IntPtr.Zero) CloseHandle(JobHandle); } catch { }
                JobHandle = IntPtr.Zero;
                try { if (Process != null) Process.Dispose(); } catch { }
                Process = null;
                if (!string.IsNullOrEmpty(TempDir) && Directory.Exists(TempDir))
                {
                    // 先清空覆写临时探针配置，避免静态代理账密在磁盘临时文件残留
                    try
                    {
                        string yamlFile = Path.Combine(TempDir, "probe.yaml");
                        if (File.Exists(yamlFile))
                        {
                            long len = new FileInfo(yamlFile).Length;
                            if (len > 0 && len < 2 * 1024 * 1024)
                                File.WriteAllBytes(yamlFile, new byte[len]);
                            File.Delete(yamlFile);
                        }
                    }
                    catch { }
                    for (int i = 0; i < 3; i++)
                    {
                        try { Directory.Delete(TempDir, true); break; }
                        catch { Thread.Sleep(80); }
                    }
                }
            }
        }

        static void EnsureProcessExitCleanup()
        {
            lock (SessionLock)
            {
                if (_exitHooked) return;
                _exitHooked = true;
                AppDomain.CurrentDomain.ProcessExit += (s, e) =>
                {
                    List<SidecarSession> copy;
                    lock (SessionLock) copy = new List<SidecarSession>(ActiveSessions);
                    foreach (var ss in copy) { try { ss.Dispose(); } catch { } }
                };
            }
        }

        /// <summary>识别机场订阅中混杂的信息公告条目（如剩余流量/官网/到期时间），自动批量测试时跳过。</summary>
        public static bool IsInformationalNodeName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return true;
            string s = name.Trim();
            string[] tokens =
            {
                "剩余流量", "可用流量", "套餐到期", "过期时间", "到期时间",
                "官网", "最新网址", "发布页", "重置时间", "距离下次重置",
                "群组", "客服", "频道", "续费", "过期", "Traffic:", "Expire:"
            };
            foreach (var t in tokens)
            {
                if (s.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        /// <summary>判断某文件名是否为合法的 mihomo/clash 内核进程程序（排除前端 GUI、服务外壳与卸载器）。</summary>
        internal static bool IsLikelyKernelExe(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName)) return false;
            string fn = Path.GetFileName(fileName).ToLowerInvariant();
            if (!fn.EndsWith(".exe", StringComparison.Ordinal)) return false;
            if (fn.Contains("uninst") || fn.Contains("setup") || fn.Contains("update") ||
                fn.Contains("service") || fn.Contains("helper") || fn.Contains("crash"))
                return false;
            return fn == "verge-mihomo.exe" ||
                   fn == "verge-mihomo-alpha.exe" ||
                   fn == "flclashcore.exe" ||
                   fn == "nyanpasu-mihomo.exe" ||
                   fn == "nyanpasu-mihomo-alpha.exe" ||
                   fn == "sparkle-mihomo.exe" ||
                   fn == "party-mihomo.exe" ||
                   fn == "mihomo.exe" ||
                   fn == "mihomo-alpha.exe" ||
                   fn == "mihomo-core.exe" ||
                   fn == "mihomo-windows-amd64.exe" ||
                   fn == "mihomo-windows-amd64-compatible.exe" ||
                   fn == "mihomo-windows-amd64-v1.exe" ||
                   fn == "mihomo-windows-amd64-v2.exe" ||
                   fn == "mihomo-windows-amd64-v3.exe" ||
                   fn == "mihomo-windows-arm64.exe" ||
                   fn == "clash-meta.exe" ||
                   fn == "clash.meta.exe" ||
                   fn == "clash-win64.exe" ||
                   fn == "clash-win32.exe" ||
                   fn == "clash-win.exe" ||
                   fn == "clash-core.exe" ||
                   fn == "clash.exe";
        }

        /// <summary>自动查找当前电脑上正在运行的客户端所带的 mihomo/clash 内核程序。</summary>
        public static string FindKernelExePath()
        {
            var candidates = new List<string>();
            string[] kernelProcNames =
            {
                "verge-mihomo", "verge-mihomo-alpha", "FlClashCore", "flclashcore",
                "nyanpasu-mihomo", "nyanpasu-mihomo-alpha", "sparkle-mihomo", "party-mihomo",
                "mihomo", "mihomo-alpha", "mihomo-windows-amd64", "mihomo-windows-amd64-compatible",
                "clash-meta", "clash"
            };
            foreach (var pn in kernelProcNames)
            {
                try
                {
                    foreach (var p in Process.GetProcessesByName(pn))
                    {
                        try
                        {
                            string exe = p.MainModule != null ? p.MainModule.FileName : "";
                            if (!string.IsNullOrEmpty(exe) && File.Exists(exe) && IsLikelyKernelExe(exe))
                                return exe;
                        }
                        catch { }
                        finally { p.Dispose(); }
                    }
                }
                catch { }
            }

            // 当内核运行在 SYSTEM 服务下时，直接从活跃客户端前端进程的同目录及常见子目录定位内核
            string[] guiProcNames =
            {
                "clash-verge", "Clash Verge", "Clash Nyanpasu", "clash-nyanpasu",
                "Mihomo Party", "mihomo-party", "Sparkle", "sparkle", "Clash Party", "clash-party",
                "FlClash", "flclash", "Clash for Windows"
            };
            var searchDirs = new List<string>();
            foreach (var gn in guiProcNames)
            {
                try
                {
                    foreach (var p in Process.GetProcessesByName(gn))
                    {
                        try
                        {
                            string exe = p.MainModule != null ? p.MainModule.FileName : "";
                            if (!string.IsNullOrEmpty(exe))
                            {
                                string dir = Path.GetDirectoryName(exe);
                                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) searchDirs.Add(dir);
                            }
                        }
                        catch { }
                        finally { p.Dispose(); }
                    }
                }
                catch { }
            }

            // 常见客户端默认安装位置兜底（换机未运行 GUI 仅跑服务时也找得到）
            string localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string progFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string[] staticDirs =
            {
                Path.Combine(progFiles, "Clash Verge"),
                Path.Combine(localApp, "Programs", "clash-verge"),
                Path.Combine(localApp, "Programs", "FlClash"),
                Path.Combine(progFiles, "FlClash"),
                Path.Combine(localApp, "Programs", "Clash Nyanpasu"),
                Path.Combine(progFiles, "Clash Nyanpasu"),
                Path.Combine(localApp, "Programs", "Mihomo Party"),
                Path.Combine(progFiles, "Mihomo Party"),
                Path.Combine(localApp, "Programs", "sparkle"),
                Path.Combine(progFiles, "Sparkle"),
                Path.Combine(localApp, "Programs", "clash-party"),
                Path.Combine(progFiles, "Clash Party")
            };
            foreach (var sd in staticDirs)
                if (!string.IsNullOrEmpty(sd) && Directory.Exists(sd) && !searchDirs.Contains(sd))
                    searchDirs.Add(sd);

            string[] subDirs = { "", "resources", "resources\\sidecar", "sidecar", "bin", "cores" };
            foreach (var rootDir in searchDirs)
            {
                foreach (var sub in subDirs)
                {
                    string dir = string.IsNullOrEmpty(sub) ? rootDir : Path.Combine(rootDir, sub);
                    if (!Directory.Exists(dir)) continue;
                    try
                    {
                        foreach (var f in Directory.GetFiles(dir, "*.exe"))
                            if (IsLikelyKernelExe(f)) candidates.Add(f);
                    }
                    catch { }
                }
            }

            // 优先选非 alpha 的正式版内核
            foreach (var c in candidates)
            {
                string fn = Path.GetFileName(c).ToLowerInvariant();
                if (!fn.Contains("alpha")) return c;
            }
            return candidates.Count > 0 ? candidates[0] : "";
        }

        /// <summary>
        /// 从现有 clash 配置里仅安全抽出 proxies / proxy-providers / proxy-groups，
        /// 并覆写为完全隔离的纯本地测试配置（tun 关、dns 关、无系统接管、随机空闲回环端口）。
        /// </summary>
        internal static string BuildSidecarConfigText(
            string[] origYamlLines,
            int proxyPort,
            int apiPort,
            string secret,
            string origDataDir)
        {
            return BuildSidecarConfigText(origYamlLines, proxyPort, apiPort, secret, origDataDir, null, null);
        }

        /// <summary>支持在纯本地旁路配置上叠加待测独立固定 IP 链式节点，用于零干扰体检与验证。</summary>
        internal static string BuildSidecarConfigText(
            string[] origYamlLines,
            int proxyPort,
            int apiPort,
            string secret,
            string origDataDir,
            StaticProxy.StaticProxyConfig staticOverlay,
            string mainPolicy,
            string preferredFollowNode = null)
        {
            var sb = new StringBuilder();
            sb.Append("mixed-port: ").Append(proxyPort).Append('\n');
            sb.Append("bind-address: 127.0.0.1\n");
            sb.Append("mode: global\n");
            sb.Append("allow-lan: false\n");
            sb.Append("log-level: silent\n");
            sb.Append("external-controller: 127.0.0.1:").Append(apiPort).Append('\n');
            sb.Append("secret: ").Append(Json.Quote(secret ?? "")).Append('\n');
            sb.Append("profile:\n  store-selected: false\n");
            sb.Append("tun:\n  enable: false\n");
            // 仅启用纯内核内置域名解析以兼容域名类节点/proxy-providers，不绑定系统/本地 DNS 监听端口
            sb.Append("dns:\n  enable: true\n  enhanced-mode: normal\n  nameserver:\n    - 223.5.5.5\n    - 119.29.29.29\n");

            if (origYamlLines != null)
            {
                string dataRootFull = "";
                if (!string.IsNullOrEmpty(origDataDir))
                {
                    try { dataRootFull = Path.GetFullPath(origDataDir).TrimEnd('\\') + "\\"; }
                    catch { dataRootFull = ""; }
                }
                bool keep = false;
                bool inProviders = false;
                foreach (var raw in origYamlLines)
                {
                    string ln = (raw ?? "").TrimEnd('\r');
                    bool isTopKey = ln.Length > 0 &&
                                    (char.IsLetterOrDigit(ln[0]) || ln[0] == '_' || ln[0] == '"' || ln[0] == '\'') &&
                                    ln.Contains(":");
                    if (isTopKey)
                    {
                        int col = ln.IndexOf(':');
                        string topKey = ln.Substring(0, col).Trim().Trim('"', '\'');
                        keep = topKey == "proxies" || topKey == "proxy-providers" || topKey == "proxy-groups";
                        inProviders = topKey == "proxy-providers";
                    }
                    if (!keep) continue;

                    // 若 proxy-providers 中包含相对 path（如 ./proxy_providers/foo.yaml），重写为绝对路径以免沙箱找不到订阅文件，并拦截 `..` 越界逃逸
                    if (inProviders && !string.IsNullOrEmpty(dataRootFull))
                    {
                        int pIdx = ln.IndexOf("path:", StringComparison.OrdinalIgnoreCase);
                        if (pIdx >= 0)
                        {
                            string head = ln.Substring(0, pIdx + 5);
                            string val = ln.Substring(pIdx + 5).Trim().Trim('"', '\'');
                            if (!string.IsNullOrEmpty(val) && !Path.IsPathRooted(val))
                            {
                                string cleanRel = val.Replace('/', '\\').TrimStart('.', '\\');
                                try
                                {
                                    string abs = Path.GetFullPath(Path.Combine(dataRootFull, cleanRel));
                                    if (abs.StartsWith(dataRootFull, StringComparison.OrdinalIgnoreCase))
                                        ln = head + " " + Json.Quote(abs);
                                }
                                catch { }
                            }
                        }
                    }
                    sb.Append(ln).Append('\n');
                }
            }
            string built = sb.ToString();
            if (staticOverlay != null && staticOverlay.Enabled &&
                (string.Equals(staticOverlay.HopMode, StaticProxy.HopDirect, StringComparison.OrdinalIgnoreCase) ||
                 !string.IsNullOrWhiteSpace(mainPolicy)))
                built = StaticProxy.InjectIntoYaml(built, staticOverlay, mainPolicy, preferredFollowNode);
            return built;
        }

        static int AllocateFreeLoopbackPort()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int p = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return p;
        }

        /// <summary>快速检查本机是否具备启动零干扰旁路探测引擎的条件（不启动任何进程）。</summary>
        public static bool CanPrepareSidecar(string knownConfigPath, out string reason)
        {
            reason = "";
            string cfgPath = !string.IsNullOrEmpty(knownConfigPath) ? knownConfigPath : Paths.ClashYaml;
            if (string.IsNullOrEmpty(cfgPath) || !File.Exists(cfgPath))
            {
                reason = "未找到有效的客户端配置文件";
                return false;
            }
            string kernelExe = FindKernelExePath();
            if (string.IsNullOrEmpty(kernelExe) || !File.Exists(kernelExe))
            {
                reason = "未定位到同源 mihomo 内核程序";
                return false;
            }
            return true;
        }

        static void SweepStaleTempDirs()
        {
            try
            {
                string tmp = Path.GetTempPath();
                foreach (var d in Directory.GetDirectories(tmp, "pp_probe_*"))
                {
                    try
                    {
                        if ((DateTime.UtcNow - Directory.GetCreationTimeUtc(d)).TotalHours > 1.0)
                            Directory.Delete(d, true);
                    }
                    catch { }
                }
            }
            catch { }
        }

        /// <summary>尝试启动完全不影响主网络的独立测风控沙箱内核实例；失败则返回 null 与清晰原因。</summary>
        public static SidecarSession TryStart(string knownConfigPath, out string error)
        {
            error = "";
            EnsureProcessExitCleanup();
            SweepStaleTempDirs();

            string cfgPath = !string.IsNullOrEmpty(knownConfigPath) ? knownConfigPath : Paths.ClashYaml;
            if (string.IsNullOrEmpty(cfgPath) || !File.Exists(cfgPath))
            {
                error = "未找到有效的客户端配置文件";
                return null;
            }

            string kernelExe = FindKernelExePath();
            if (string.IsNullOrEmpty(kernelExe) || !File.Exists(kernelExe))
            {
                error = "未找到同源 mihomo 内核程序";
                return null;
            }

            string[] origLines;
            try { origLines = File.ReadAllLines(cfgPath, Encoding.UTF8); }
            catch (Exception ex) { error = ex.Message; return null; }

            // 最多尝试 2 次：若选中端口在启动瞬间被其他进程抢占导致提前退出，立即换新端口重试
            string origDataDir = Path.GetDirectoryName(cfgPath) ?? "";
            for (int attempt = 0; attempt < 2; attempt++)
            {
                bool earlyExit;
                var session = TryStartOnce(origLines, origDataDir, kernelExe, null, null, null, out earlyExit, out error);
                if (session != null) return session;
                if (!earlyExit) return null;
            }
            return null;
        }

        /// <summary>为「独立固定 IP」零干扰体检启动叠加了链式静态节点的沙箱内核实例。</summary>
        public static SidecarSession TryStartForStaticTest(
            string knownConfigPath,
            StaticProxy.StaticProxyConfig staticOverlay,
            out string mainPolicy,
            out string error)
        {
            mainPolicy = "";
            error = "";
            EnsureProcessExitCleanup();
            SweepStaleTempDirs();

            string cfgPath = !string.IsNullOrEmpty(knownConfigPath) ? knownConfigPath : Paths.ClashYaml;
            if (string.IsNullOrEmpty(cfgPath) || !File.Exists(cfgPath))
            {
                error = "未找到有效的客户端配置文件，请先启动 Clash / mihomo 客户端";
                return null;
            }

            string kernelExe = FindKernelExePath();
            if (string.IsNullOrEmpty(kernelExe) || !File.Exists(kernelExe))
            {
                error = "未找到同源 mihomo 内核程序";
                return null;
            }

            string rawText;
            try { rawText = File.ReadAllText(cfgPath, Encoding.UTF8); }
            catch (Exception ex) { error = ex.Message; return null; }

            bool _;
            mainPolicy = Discovery.FindPolicy(rawText, out _);
            bool directHop = staticOverlay != null &&
                             string.Equals(staticOverlay.HopMode, StaticProxy.HopDirect, StringComparison.OrdinalIgnoreCase);
            if (!directHop && string.IsNullOrEmpty(mainPolicy))
            {
                error = "当前订阅未识别到可用的主策略组，无法建立前置跳板链路（如节点可直连，请在高级设置选「直连该节点」）";
                return null;
            }

            // 若为链式自动跟随模式，读取主内核当前真实活跃物理节点置顶于旁路跳板首位，
            // 避免沙箱无 cache.db 时落到订阅默认首行（可能是流量公告行或失效节点）导致误报前置不通。
            string liveFollowNode = null;
            if (!directHop && staticOverlay != null &&
                string.Equals(staticOverlay.HopMode, StaticProxy.HopFollow, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var pxRes = Mihomo.FetchJson("/proxies", 3000, cfgPath);
                    if (pxRes.Ok && pxRes.Data != null)
                    {
                        var proxies = Json.Obj(pxRes.Data, "proxies");
                        var detail = Profile.ResolveOutboundDetail(proxies, mainPolicy);
                        if (!string.IsNullOrWhiteSpace(detail.Outbound) &&
                            Profile.ClassifyExit(detail.Outbound) == Profile.ExitKind.Proxy &&
                            !StaticProxy.IsManagedName(detail.Outbound))
                        {
                            liveFollowNode = detail.Outbound;
                        }
                    }
                }
                catch { }
            }

            string[] origLines = rawText.Replace("\r\n", "\n").Split('\n');
            string origDataDir = Path.GetDirectoryName(cfgPath) ?? "";
            for (int attempt = 0; attempt < 2; attempt++)
            {
                bool earlyExit;
                var session = TryStartOnce(origLines, origDataDir, kernelExe, staticOverlay, mainPolicy, liveFollowNode, out earlyExit, out error);
                if (session != null) return session;
                if (!earlyExit) return null;
            }
            return null;
        }

        /// <summary>生成用于「独立固定 IP 全量前置跳板并发测速」的零干扰旁路配置。</summary>
        internal static string BuildAutoHopBenchmarkConfigText(
            string[] origYamlLines,
            int proxyPort,
            int apiPort,
            string secret,
            string origDataDir,
            StaticProxy.StaticProxyConfig staticOverlay,
            IList<string> candidateHops)
        {
            string baseYaml = BuildSidecarConfigText(origYamlLines, proxyPort, apiPort, secret, origDataDir, null, null, null);
            baseYaml = "unified-delay: true\n" + baseYaml;
            if (staticOverlay != null && candidateHops != null && candidateHops.Count > 0)
                baseYaml = StaticProxy.InjectBenchmarkProxiesIntoYaml(baseYaml, staticOverlay, candidateHops);
            return baseYaml;
        }

        /// <summary>为「独立固定 IP 端到端全量跳板优选」启动隔离旁路内核实例（绝不触碰主网络与主规则）。</summary>
        public static SidecarSession TryStartForAutoHopBenchmark(
            string knownConfigPath,
            StaticProxy.StaticProxyConfig staticOverlay,
            out List<string> candidateHops,
            out string error)
        {
            candidateHops = new List<string>();
            error = "";
            EnsureProcessExitCleanup();
            SweepStaleTempDirs();

            if (staticOverlay == null || !staticOverlay.IsValid(out error))
            {
                if (string.IsNullOrEmpty(error)) error = "独立固定 IP 参数无效";
                return null;
            }

            string cfgPath = !string.IsNullOrEmpty(knownConfigPath) ? knownConfigPath : Paths.ClashYaml;
            if (string.IsNullOrEmpty(cfgPath) || !File.Exists(cfgPath))
            {
                error = "未找到有效的客户端配置文件，请先启动 Clash / mihomo 客户端";
                return null;
            }

            string kernelExe = FindKernelExePath();
            if (string.IsNullOrEmpty(kernelExe) || !File.Exists(kernelExe))
            {
                error = "未找到同源 mihomo 内核程序";
                return null;
            }

            string rawText;
            try { rawText = File.ReadAllText(cfgPath, Encoding.UTF8); }
            catch (Exception ex) { error = ex.Message; return null; }

            candidateHops = StaticProxy.ExtractConcreteNodeNamesFromYaml(rawText, 120);
            if (candidateHops.Count == 0)
            {
                // 兼容纯 proxy-providers 订阅（顶层无内联 proxies）：回退读取主内核已加载的物理节点列表
                try
                {
                    var cat = NodeManager.LoadCatalog(cfgPath);
                    if (cat != null && cat.Ok && cat.Nodes != null)
                    {
                        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var n in cat.Nodes)
                        {
                            if (n == null || n.IsGroup || string.IsNullOrWhiteSpace(n.Name)) continue;
                            if (StaticProxy.IsManagedName(n.Name) || IsInformationalNodeName(n.Name)) continue;
                            if (seen.Add(n.Name))
                            {
                                candidateHops.Add(n.Name);
                                if (candidateHops.Count >= 120) break;
                            }
                        }
                    }
                }
                catch { }
            }
            if (candidateHops.Count == 0)
            {
                error = "当前订阅未找到可用物理节点，无法自动优选跳板";
                return null;
            }

            string[] origLines = rawText.Replace("\r\n", "\n").Split('\n');
            string origDataDir = Path.GetDirectoryName(cfgPath) ?? "";
            var hopsSnapshot = candidateHops;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                bool earlyExit;
                var session = TryStartCustomOnce(
                    (proxyPort, apiPort, secret) => BuildAutoHopBenchmarkConfigText(
                        origLines, proxyPort, apiPort, secret, origDataDir, staticOverlay, hopsSnapshot),
                    kernelExe, out earlyExit, out error);
                if (session != null) return session;
                if (!earlyExit) return null;
            }
            return null;
        }

        static SidecarSession TryStartOnce(
            string[] origLines, string origDataDir, string kernelExe,
            StaticProxy.StaticProxyConfig staticOverlay, string mainPolicy, string preferredFollowNode,
            out bool earlyExit, out string error)
        {
            return TryStartCustomOnce(
                (proxyPort, apiPort, secret) => BuildSidecarConfigText(
                    origLines, proxyPort, apiPort, secret, origDataDir, staticOverlay, mainPolicy, preferredFollowNode),
                kernelExe, out earlyExit, out error);
        }

        static SidecarSession TryStartCustomOnce(
            Func<int, int, string, string> yamlBuilder, string kernelExe,
            out bool earlyExit, out string error)
        {
            earlyExit = false;
            error = "";
            string tempDir = Path.Combine(Path.GetTempPath(), "pp_probe_" + Guid.NewGuid().ToString("N").Substring(0, 10));
            SidecarSession session = null;
            IntPtr hJob = IntPtr.Zero;
            try
            {
                Directory.CreateDirectory(tempDir);
                int proxyPort = AllocateFreeLoopbackPort();
                int apiPort = AllocateFreeLoopbackPort();
                if (apiPort == proxyPort) apiPort = AllocateFreeLoopbackPort();
                string secret = Guid.NewGuid().ToString("N").Substring(0, 20);

                string probeYaml = yamlBuilder(proxyPort, apiPort, secret);
                string probeYamlPath = Path.Combine(tempDir, "probe.yaml");
                File.WriteAllText(probeYamlPath, probeYaml, new UTF8Encoding(false));

                var psi = new ProcessStartInfo
                {
                    FileName = kernelExe,
                    Arguments = "-d \"" + tempDir + "\" -f \"" + probeYamlPath + "\"",
                    WorkingDirectory = Path.GetDirectoryName(kernelExe) ?? tempDir,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                var proc = Process.Start(psi);
                if (proc == null)
                {
                    error = "无法拉起隔离探测子进程";
                    try { Directory.Delete(tempDir, true); } catch { }
                    return null;
                }

                hJob = CreateKillOnCloseJob();
                if (hJob != IntPtr.Zero)
                {
                    try
                    {
                        if (!AssignProcessToJobObject(hJob, proc.Handle))
                            AuditLog.Record("SIDECAR", "旁路探测子进程未能绑定 Job Object，异常退出后请留意是否有残留内核进程");
                    }
                    catch (Exception jex)
                    {
                        AuditLog.Record("SIDECAR", "旁路探测 Job Object 绑定异常：" + jex.Message);
                    }
                }

                session = new SidecarSession
                {
                    Process = proc,
                    JobHandle = hJob,
                    TempDir = tempDir,
                    ApiPort = apiPort,
                    ProxyPort = proxyPort,
                    Secret = secret,
                    OutboundProxy = new WebProxy("127.0.0.1", proxyPort)
                };
                lock (SessionLock) ActiveSessions.Add(session);

                var sw = Stopwatch.StartNew();
                bool ready = false;
                while (sw.ElapsedMilliseconds < 4500)
                {
                    if (proc.HasExited)
                    {
                        earlyExit = true;
                        error = "隔离探测内核提前退出 (code=" + proc.ExitCode + ")";
                        session.Dispose();
                        return null;
                    }
                    var px = session.FetchProxies();
                    if (px.Ok && px.Data != null && Json.Obj(Json.Obj(px.Data, "proxies"), "GLOBAL") != null)
                    {
                        ready = true;
                        break;
                    }
                    Thread.Sleep(70);
                }
                if (!ready)
                {
                    error = "隔离探测内核启动超时";
                    session.Dispose();
                    return null;
                }
                return session;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                if (session != null) session.Dispose();
                else
                {
                    if (hJob != IntPtr.Zero) { try { CloseHandle(hJob); } catch { } }
                    if (Directory.Exists(tempDir)) { try { Directory.Delete(tempDir, true); } catch { } }
                }
                return null;
            }
        }

        // ---------------------------------------------------------------- 自检

        public static string SelfTest()
        {
            var sb = new StringBuilder();
            int pass = 0, fail = 0;
            Action<string, bool, string> check = (name, ok, detail) =>
            {
                if (ok) { pass++; sb.AppendLine("PASS  " + name); }
                else { fail++; sb.AppendLine("FAIL  " + name + (string.IsNullOrEmpty(detail) ? "" : " | " + detail)); }
            };

            string[] mockCfg =
            {
                "mode: rule",
                "mixed-port: 7897",
                "external-controller-pipe: \\\\.\\pipe\\verge-mihomo",
                "tun:",
                "  enable: true",
                "  stack: gVisor",
                "proxies:",
                "- name: ZeroIndentJP",
                "  type: ss",
                "  server: 1.2.3.4",
                "  port: 443",
                "  - {name: JP1, type: ss, server: 1.2.3.4, port: 443}",
                "proxy-providers:",
                "  sub1:",
                "    type: http",
                "    path: ./proxy_providers/sub1.yaml",
                "rules:",
                "- MATCH,DIRECT"
            };
            string built = BuildSidecarConfigText(mockCfg, 55001, 55002, "sec123", @"C:\Data\Verge");
            check("PS1 BuildSidecarConfigText 保留零缩进与缩进节点列表并剥离 tun/rules/管道",
                built.Contains("mixed-port: 55001") &&
                built.Contains("external-controller: 127.0.0.1:55002") &&
                built.Contains("mode: global") &&
                built.Contains("enable: false") &&
                built.Contains("ZeroIndentJP") &&
                built.Contains("JP1") &&
                !built.Contains("verge-mihomo") &&
                !built.Contains("MATCH,DIRECT") &&
                built.Contains(@"C:\\Data\\Verge\\proxy_providers\\sub1.yaml"),
                built);

            check("PS2 IsLikelyKernelExe 准确区分 mihomo 内核与前端 GUI/安装器",
                IsLikelyKernelExe(@"D:\Clash Verge\verge-mihomo.exe") &&
                IsLikelyKernelExe(@"C:\App\mihomo.exe") &&
                !IsLikelyKernelExe(@"D:\Clash Verge\clash-verge.exe") &&
                !IsLikelyKernelExe(@"D:\Clash Verge\clash-verge-service.exe") &&
                !IsLikelyKernelExe(@"D:\Clash Verge\uninstall.exe"),
                "");

            check("PS3 IsInformationalNodeName 识别流量提示行而不误杀物理节点",
                IsInformationalNodeName("剩余流量：499.98 GB") &&
                IsInformationalNodeName("套餐到期：2027-01-01") &&
                IsInformationalNodeName("官网：example.com") &&
                !IsInformationalNodeName("🇯🇵 日本 Tokyo 01") &&
                !IsInformationalNodeName("🇺🇸 US-BGP"),
                "");

            check("PS4 IsLikelyKernelExe 覆盖 FlClashCore/Sparkle/Nyanpasu 且拦截 FlClash 前端与服务",
                IsLikelyKernelExe(@"C:\Program Files\FlClash\FlClashCore.exe") &&
                IsLikelyKernelExe(@"C:\App\mihomo-windows-amd64-v3.exe") &&
                IsLikelyKernelExe(@"C:\App\nyanpasu-mihomo.exe") &&
                IsLikelyKernelExe(@"C:\App\clash-win64.exe") &&
                !IsLikelyKernelExe(@"C:\Program Files\FlClash\FlClash.exe") &&
                !IsLikelyKernelExe(@"C:\Program Files\FlClash\FlClashHelperService.exe"),
                "");

            IntPtr jobTest = CreateKillOnCloseJob();
            bool jobOk = jobTest != IntPtr.Zero;
            if (jobTest != IntPtr.Zero) CloseHandle(jobTest);
            check("PS5 CreateKillOnCloseJob 可正常建立并设置 KILL_ON_JOB_CLOSE（防孤儿侧车）", jobOk, "");

            return "\n[零干扰侧车探测 ProbeSidecar 自检] " + pass + " PASS / " + fail + " FAIL\n" + sb;
        }
    }
}
