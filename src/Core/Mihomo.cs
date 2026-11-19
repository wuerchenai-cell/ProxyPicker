using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace ProxyPicker.Core
{
    internal sealed class ApiResult
    {
        public int Status;
        public string Body = "";
        public long Ms;
        public string Error;

        // 3xx 不能算成功：请求明确禁止自动重定向，301/302/307 意味着目标操作没执行（评审 R6-10）。
        public bool Ok { get { return Error == null && Status >= 200 && Status < 300; } }
    }

    /// <summary>
    /// mihomo 控制面客户端，支持两种传输：
    ///
    ///  ① 命名管道（Clash Verge 系默认）：不开任何 TCP 端口，最安全。
    ///     注意管道实际名常是 verge-mihomo-production-&lt;uid&gt;，配置里写的 sidecar 连不上，必须枚举。
    ///  ② HTTP（其他客户端/裸内核）：从配置读 external-controller，读不到再试常见端口。
    ///
    /// 先管道后 HTTP，两者都没有就明确报「找不到内核」，而不是抛一堆异常。
    /// </summary>
    internal static class Mihomo
    {
        const string PipeRoot = @"\\.\pipe\";

        static string _pipeName;
        static string _secret;
        static string _endpoint;
        static bool _endpointTried;
        static string _securityError;
        static int _trustedPipePid;
        static string _trustedPipeName;   // 信任缓存必须绑定管道名，内核重启换管道后不能复用（评审 R6-8）
        static long _trustedAtTick;
        static string _pipeTrustDetail;
        static string _pipeAmbiguous;     // 多个内核实例、无法绑定同一客户端时失败关闭（评审 R6-2）
        static string _boundConfigPath;   // 通道缓存绑定的服务配置路径；与 Discovery 当前选中不一致时整份 Forget（评审 P1-①）
        static bool _endpointGuessed;     // 控制地址是「猜端口」得来的，没和目标内核建立可验证关联

        /// <summary>通道缓存是共享可变状态：流量轮询、看守、保存并发时，
        /// 以前没有任何同步，可能出现互相清掉绑定或拼出空密钥（独立审阅第二轮）。
        /// 所有请求与绑定变更都走同一把锁；Monitor 可重入，嵌套调用不会自锁。</summary>
        static readonly object ChannelGate = new object();
        static int _forgetEpoch;
        static int _appliedForgetEpoch;

        public static string LastPipeName { get { lock (ChannelGate) { return _pipeName; } } }
        public static string LastEndpoint { get { lock (ChannelGate) { return _endpoint; } } }
        public static string LastSecurityError { get { lock (ChannelGate) { return _securityError; } } }
        public static string LastPipeTrustDetail { get { lock (ChannelGate) { return _pipeTrustDetail; } } }

        const uint TOKEN_QUERY = 0x0008;
        const int TokenUserInfoClass = 1;
        const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        const uint TH32CS_SNAPPROCESS = 0x00000002;
        const uint SC_MANAGER_ENUMERATE_SERVICE = 0x0004;
        const uint SERVICE_WIN32 = 0x00000030;
        const uint SERVICE_STATE_ALL = 0x00000003;
        const uint ERROR_MORE_DATA = 234;

        [StructLayout(LayoutKind.Sequential)]
        struct SidAndAttributes { public IntPtr Sid; public uint Attributes; }
        [StructLayout(LayoutKind.Sequential)]
        struct TokenUser { public SidAndAttributes User; }

        // 进程快照：普通权限也能读到提权服务进程的 PID / 父 PID / exe 名，
        // 这是「OpenProcess 被拒绝访问」时唯一可靠的身份来源。th32DefaultHeapID 是指针宽度。
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct PROCESSENTRY32W
        {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID;
            public uint cntThreads;
            public uint th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct SERVICE_STATUS_PROCESS
        {
            public uint dwServiceType, dwCurrentState, dwControlsAccepted, dwWin32ExitCode,
                        dwServiceSpecificExitCode, dwCheckPoint, dwWaitHint, dwProcessId, dwServiceFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct ENUM_SERVICE_STATUS_PROCESSW
        {
            public IntPtr lpServiceName;
            public IntPtr lpDisplayName;
            public SERVICE_STATUS_PROCESS ServiceStatusProcess;
        }

        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);
        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool GetTokenInformation(IntPtr tokenHandle, int infoClass,
            IntPtr tokenInformation, int length, out int returnedLength);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags,
            StringBuilder exeName, ref uint size);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);
        [DllImport("kernel32.dll")]
        static extern uint GetCurrentProcessId();

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool Process32FirstW(IntPtr snapshot, ref PROCESSENTRY32W entry);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool Process32NextW(IntPtr snapshot, ref PROCESSENTRY32W entry);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr OpenSCManagerW(string machine, string database, uint desiredAccess);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool EnumServicesStatusExW(IntPtr manager, uint infoLevel, uint serviceType,
            uint serviceState, IntPtr buffer, uint bufferSize, out uint bytesNeeded,
            out uint servicesReturned, ref uint resumeHandle, string groupName);
        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool CloseServiceHandle(IntPtr handle);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr FindFirstFileW(string lpFileName, out WIN32_FIND_DATA lpFindFileData);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool FindNextFileW(IntPtr hFindFile, out WIN32_FIND_DATA lpFindFileData);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool FindClose(IntPtr hFindFile);

        // 注意：FILETIME 是两个 DWORD（8 字节），不是两个 long（16 字节）。
        // 写成 long 会让后面的 cFileName 偏移错位，读到的管道名只剩尾部片段。
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct WIN32_FIND_DATA
        {
            public uint dwFileAttributes;
            public uint ftCreationTimeLow, ftCreationTimeHigh;
            public uint ftLastAccessTimeLow, ftLastAccessTimeHigh;
            public uint ftLastWriteTimeLow, ftLastWriteTimeHigh;
            public uint nFileSizeHigh, nFileSizeLow;
            public uint dwReserved0, dwReserved1;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string cFileName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string cAlternateFileName;
        }

        public static List<string> EnumNamedPipes()
        {
            var result = new List<string>();
            WIN32_FIND_DATA fd;
            IntPtr h = FindFirstFileW(PipeRoot + "*", out fd);
            if (h == new IntPtr(-1)) return result;
            try
            {
                do
                {
                    string n = fd.cFileName;
                    if (!string.IsNullOrEmpty(n) && n != "." && n != "..") result.Add(n);
                }
                while (FindNextFileW(h, out fd));
            }
            finally { FindClose(h); }
            return result;
        }

        /// <summary>
        /// 只列内核相关（mihomo / clash 系）的命名管道。
        /// 全系统有六百多个管道，里面混着 AppContainer 的 SID、显卡 GUID 之类的机器标识，
        /// 对排查内核毫无帮助，还会把标识带出去（第四轮自查）。
        /// </summary>
        public static List<string> EnumKernelPipes()
        {
            var all = EnumNamedPipes();
            var hits = all
                .Where(n => n.IndexOf("mihomo", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            n.IndexOf("clash", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            n.IndexOf("verge", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            n.IndexOf("nyanpasu", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            n.IndexOf("flclash", StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();
            return hits;
        }

        /// <summary>过滤掉 Windows 特权 Helper 服务 RPC 管道与 FlClash 原生 JSON-RPC 管道，仅保留 Clash/Mihomo HTTP 控制管道。</summary>
        internal static bool IsRestControlPipeName(string pipeName)
        {
            if (string.IsNullOrWhiteSpace(pipeName)) return false;
            string n = pipeName.Trim().ToLowerInvariant();
            if (n.IndexOf("service", StringComparison.Ordinal) >= 0 ||
                n.IndexOf("helper", StringComparison.Ordinal) >= 0 ||
                n.IndexOf("daemon", StringComparison.Ordinal) >= 0)
                return false;
            // FlClash Windows 命名管道仅用于 Flutter <-> FlClashCore 的 JSON-RPC，不走 Clash REST HTTP 协议
            if (n.IndexOf("flclash", StringComparison.Ordinal) >= 0)
                return false;
            return n.IndexOf("mihomo", StringComparison.Ordinal) >= 0 ||
                   n.IndexOf("clash", StringComparison.Ordinal) >= 0 ||
                   n.IndexOf("verge", StringComparison.Ordinal) >= 0 ||
                   n.IndexOf("nyanpasu", StringComparison.Ordinal) >= 0 ||
                   n.IndexOf("sparkle", StringComparison.Ordinal) >= 0 ||
                   n.IndexOf("party", StringComparison.Ordinal) >= 0;
        }

        /// <summary>找到 mihomo 的控制管道（优先 production）。</summary>
        public static string FindPipe()
        {
            return FindPipe(null);
        }

        /// <summary>
        /// v2.2.2：先过滤掉特权服务/前端 RPC 管道，再枚举全部合法 REST 控制管道，
        /// 避免因为机器上有 verge-mihomo-* / mihomo-* 残留就短路漏掉 nyanpasu-mihomo。
        /// 客户端过滤严格按白名单放行、默认拒绝。
        /// </summary>
        public static string FindPipe(string clientHint)
        {
            var all = EnumNamedPipes();
            var restPipes = all.Where(IsRestControlPipeName).ToList();
            _pipeAmbiguous = null;
            if (restPipes.Count == 0) return null;

            List<string> hits;
            if (!string.IsNullOrEmpty(clientHint))
            {
                hits = restPipes.Where(n => PipeMatchesClient(n, clientHint)).ToList();
            }
            else
            {
                hits = restPipes
                    .Where(n => n.StartsWith("verge-mihomo-", StringComparison.OrdinalIgnoreCase) ||
                                n.StartsWith("mihomo-", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (hits.Count == 0)
                {
                    hits = restPipes
                        .Where(n => n.IndexOf("mihomo", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    n.IndexOf("clash", StringComparison.OrdinalIgnoreCase) >= 0)
                        .ToList();
                }
            }
            if (hits.Count == 0) return null;

            // 同时存在多个内核控制管道时，无法确认「配置 / Secret / 管道」属于同一个客户端实例。
            // 宁可失败关闭，也不能拿 A 的密钥推给 B（评审 R6-2 的保守落地）。
            if (hits.Count > 1)
            {
                var prod = hits.Where(n => n.IndexOf("production", StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                if (prod.Count == 1) return prod[0];
                _pipeAmbiguous = "检测到 " + hits.Count + " 个 mihomo 控制管道，无法确认属于同一个客户端实例；已停止发送控制密钥";
                return null;
            }
            return hits[0];
        }

        /// <summary>判断管道名称是否属于可无额外命令行证据即信任的专属客户端命名管道。</summary>
        internal static bool IsGenericMihomoPipe(string pipe)
        {
            if (string.IsNullOrEmpty(pipe)) return false;
            string p = pipe.Trim().ToLowerInvariant();
            return p == "mihomo" || p.StartsWith("mihomo-", StringComparison.Ordinal) ||
                   p == "clash" || p.StartsWith("clash-", StringComparison.Ordinal);
        }

        /// <summary>管道名与选中客户端家族的一致性核对（默认拒绝未明确匹配的管道）。</summary>
        internal static bool PipeMatchesClient(string pipe, string clientHint)
        {
            if (!IsRestControlPipeName(pipe)) return false;
            if (string.IsNullOrEmpty(clientHint)) return true;

            string p = pipe.ToLowerInvariant();
            string c = clientHint.ToLowerInvariant();
            // FlClash 默认不提供 Mihomo REST 命名管道，统一走本地 HTTP 控制接口
            if (c.IndexOf("flclash", StringComparison.Ordinal) >= 0) return false;

            if (c.IndexOf("verge", StringComparison.Ordinal) >= 0)
                return p.StartsWith("verge-mihomo", StringComparison.Ordinal);
            if (c.IndexOf("nyanpasu", StringComparison.Ordinal) >= 0)
                return p.IndexOf("nyanpasu", StringComparison.Ordinal) >= 0;
            if (c.IndexOf("sparkle", StringComparison.Ordinal) >= 0)
                return p.IndexOf("sparkle", StringComparison.Ordinal) >= 0 ||
                       p == "mihomo" || p.StartsWith("mihomo-", StringComparison.Ordinal);
            if (c.IndexOf("party", StringComparison.Ordinal) >= 0)
                return p.IndexOf("party", StringComparison.Ordinal) >= 0 ||
                       p == "mihomo" || p.StartsWith("mihomo-", StringComparison.Ordinal);
            if (c.IndexOf("for windows", StringComparison.Ordinal) >= 0)
                return p == "clash" || p.StartsWith("clash-", StringComparison.Ordinal);
            if (c.IndexOf("自定义", StringComparison.Ordinal) >= 0 || c.IndexOf("内核", StringComparison.Ordinal) >= 0)
                return p == "mihomo" || p.StartsWith("mihomo-", StringComparison.Ordinal) ||
                       p == "clash" || p.StartsWith("clash-", StringComparison.Ordinal);
            return false;
        }

        public static string ReadSecret(string knownConfigPath = null)
        {
            string secret;
            TryReadSecret(knownConfigPath, out secret);
            return secret ?? "";
        }

        /// <summary>区分「没读到顶层 secret 行」与「合法的无认证配置 secret: ""」。
        /// 还原时后者不该被当成密钥读取失败而中止（独立审阅第二轮）。</summary>
        public static bool TryReadSecret(string knownConfigPath, out string secret)
        {
            secret = "";
            try
            {
                // 评审 R4：传入本次保存已确认的配置路径时直接按它读 secret，
                // 保证 secret 与 payload 生成 / 通道绑定判定出自同一份配置。
                string p = !string.IsNullOrEmpty(knownConfigPath) ? knownConfigPath
                                        : Discovery.Detect().ConfigPath;
                if (string.IsNullOrEmpty(p) || !File.Exists(p)) return false;
                // 收尾批 P2-2：解析统一收敛到 Discovery.ReadController（只认顶层、
                // 正确处理行尾注释与引号），不再有第二套实现。
                string controller;
                bool found;
                Discovery.ReadController(p, out secret, out controller, out found);
                return found;
            }
            catch { return false; }
        }

        /// <summary>
        /// 绑定判定：当前选中配置路径非空、且与缓存绑定的那一份不一致时返回 true（应整份 Forget）。
        /// 抽成独立方法以便自检直接驱动判定逻辑，不依赖 Discovery 的真实返回值。
        /// </summary>
        static bool BindingStaleFor(string current)
        {
            if (string.IsNullOrEmpty(current)) return false;   // 没有选中任何配置：不动缓存
            return !string.Equals(_boundConfigPath, current, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// P1-① 通道缓存绑定：多客户端 / 多订阅机器上，Discovery 按评分自动选当前最可能在跑的那个；
        /// 用户中途切换客户端（VS Code 订阅换成 Clash Verge 订阅等）时，旧的 _pipeName / _endpoint /
        /// _secret 不会自动失效——它们都缓存着上一次命中的容器管道名 / HTTP 端口 / Secret。管道名里带的
        /// 又不是跨客户端唯一，于是会出现「连的是 A 的管道、推的是 B 的配置」那类错配，而用户看得见
        /// 界面只是「保存成功」。
        /// 修法：每次复用缓存前，比对 Discovery 当前选中的配置路径与缓存绑定的那一份；不一致就整份
        /// Forget（动作原子且幂等，并发下无损），让后续枚举重新命中目标客户端。
        /// 评审 R4：一次保存里 payload 生成（Profile.ApplyCore 用 Paths.ClashYaml，触发 Detect #1）
        /// 与通道绑定判定（这里再 Detect #2）必须使用同一份配置路径——否则 5 秒缓存窗口内两次
        /// 猜测可能拿到不同订阅，导致「按 A 生成、按 B 绑定」的错配。knownConfigPath 由调用方
        /// （ApplyCore）传入本次保存已确认的配置路径：传入时不再 Detect，直接用它做绑定判定；
        /// 不传（外部命令 --raw / --diag 等旧调用点）维持原行为。
        /// </summary>
        internal static void EnsureBoundToCurrentConfig(string knownConfigPath = null)
        {
            string current;
            if (!string.IsNullOrEmpty(knownConfigPath))
            {
                current = knownConfigPath;   // R4：调用方已持有本次保存的配置路径，不再二次猜测
            }
            else
            {
                // 收尾批 F-03：只有「没拿到同源路径」才允许回退到 Detect；
                // 这个计数供自检断言「传了 knownConfigPath 就不再二次猜测」。
                DetectFallbackCount++;
                try { current = Discovery.Detect().ConfigPath; }
                catch { return; }
            }
            if (!BindingStaleFor(current)) return;
            lock (ChannelGate)
            {
                if (!BindingStaleFor(current)) return;   // 双检：并发下只让一个线程真正改绑
                Forget();
                _boundConfigPath = current;
                // 评审报告 §15（P3 可观测性）：真的改绑时留一行，路径由 AuditLog 统一脱敏。
                // 切客户端后第一次保存若重新枚举失败，排障时能看出是缓存被清过。
                if (!SuppressBindLog)
                    AuditLog.Record("BIND", "通道缓存改绑到配置 " + (current ?? ""));
            }
        }

        /// <summary>没有同源路径、回退到 Detect 的次数；仅自检读取（收尾批 F-03）。</summary>
        internal static long DetectFallbackCount;

        /// <summary>自检驱动绑定逻辑时不写审计账本，避免一次 --selftest 污染用户日志。</summary>
        internal static bool SuppressBindLog;

        /// <summary>发一次请求：先管道，失败再走本地 HTTP。</summary>
        public static ApiResult Request(string method, string path, string body, int timeoutMs,
                                        string knownConfigPath = null)
        {
            // 通道缓存（管道名 / 密钥 / 绑定路径 / 控制地址）是共享可变状态。
            // 整个请求串行化，避免流量轮询、看守与保存并发时互相清掉绑定。
            lock (ChannelGate) { return RequestCore(method, path, body, timeoutMs, knownConfigPath); }
        }

        static void SyncForgetEpochUnlocked()
        {
            int cur = System.Threading.Volatile.Read(ref _forgetEpoch);
            if (_appliedForgetEpoch != cur)
            {
                ClearChannelFieldsUnlocked();
                _appliedForgetEpoch = cur;
            }
        }

        static ApiResult RequestCore(string method, string path, string body, int timeoutMs,
                                     string knownConfigPath)
        {
            SyncForgetEpochUnlocked();
            try
            {
                EnsureBoundToCurrentConfig(knownConfigPath);
                var sw = Stopwatch.StartNew();
                var viaPipe = TryPipe(method, path, body, timeoutMs, sw, knownConfigPath);
                if (viaPipe != null) { HandleAuthFailure(viaPipe); return viaPipe; }

                var viaHttp = TryHttp(method, path, body, timeoutMs, sw, knownConfigPath);
                if (viaHttp != null) { HandleAuthFailure(viaHttp); return viaHttp; }

                return new ApiResult
                {
                    Error = !string.IsNullOrEmpty(_pipeAmbiguous) ? _pipeAmbiguous :
                            !string.IsNullOrEmpty(_securityError) ? _securityError
                        : "找不到 mihomo 控制接口（Clash / mihomo 没启动？）",
                    Ms = sw.ElapsedMilliseconds
                };
            }
            finally
            {
                SyncForgetEpochUnlocked();
            }
        }

        /// <summary>客户端重启换过 Secret / 控制面时，401/403 后重新读取，不必重启本程序（评审 R3-9）。</summary>
        static void HandleAuthFailure(ApiResult r)
        {
            if (r != null && (r.Status == 401 || r.Status == 403)) Forget();
        }

        static string ResolveClientHint(string cfg)
        {
            if (string.IsNullOrEmpty(cfg)) return "";
            string hint = Discovery.ClientForConfig(cfg);
            if (!string.IsNullOrEmpty(hint)) return hint;
            string guessed = Discovery.GuessClient(Path.GetDirectoryName(cfg));
            return string.Equals(guessed, "mihomo", StringComparison.OrdinalIgnoreCase) ? "" : guessed;
        }

        static ApiResult TryPipe(string method, string path, string body, int timeoutMs, Stopwatch sw,
                                 string knownConfigPath = null)
        {
            if (_pipeName == null) _pipeName = FindPipe(ResolveClientHint(knownConfigPath));
            if (_pipeName == null && !string.IsNullOrEmpty(_pipeAmbiguous))
                return new ApiResult { Error = _pipeAmbiguous, Ms = sw.ElapsedMilliseconds };
            if (_pipeName == null) return null;
            if (_secret == null) _secret = ReadSecret(knownConfigPath);

            var r = new ApiResult();
            bool sent = false;
            try
            {
                using (var pipe = PipeConnection.Open(PipeRoot + _pipeName))
                {
                    pipe.SetDeadline(timeoutMs);
                    // 先验证服务端身份，再决定是否把 Bearer Secret 写进管道。
                    string trustError;
                    try { trustError = ValidatePipeServer(pipe.ServerProcessId); }
                    catch (Exception ex)
                    {
                        // 此时 sent == false（密钥尚未发出）：清空不可信管道缓存，保留安全错误并允许继续尝试已核实的 HTTP 回环端口
                        _pipeName = null;
                        _securityError = "已阻止向命名管道发送控制密钥：身份校验异常（" + ex.Message + "）";
                        return null;
                    }
                    if (trustError != null)
                    {
                        // 同样尚未写出密钥：不卡死本地 HTTP 控制端口回退；若 HTTP 也不可用，RequestCore 会把 _securityError 暴露出来
                        _pipeName = null;
                        _securityError = "已阻止向命名管道发送控制密钥：" + trustError;
                        return null;
                    }
                    // 若管道只是通用名字（如 Sparkle / Party / 裸内核使用的 mihomo-*），发送密钥前必须再核对服务端进程命令行 `-f`/`-d` 是否确为当前配置
                    string cfgToProve = !string.IsNullOrEmpty(knownConfigPath) ? knownConfigPath : _boundConfigPath;
                    if (IsGenericMihomoPipe(_pipeName) && !string.IsNullOrEmpty(cfgToProve) &&
                        !PidProvesConfig(pipe.ServerProcessId, cfgToProve))
                    {
                        _pipeName = null;
                        _securityError = "已阻止向通用命名管道发送控制密钥：监听进程命令行未证明加载当前配置";
                        return null;
                    }

                    byte[] payload = Encoding.UTF8.GetBytes(body ?? "");
                    string sec = CleanHeaderSecret(_secret);
                    string head =
                        method + " " + path + " HTTP/1.0\r\n" +
                        "Host: localhost\r\n" +
                        (!string.IsNullOrEmpty(sec) ? "Authorization: Bearer " + sec + "\r\n" : "") +
                        "Content-Type: application/json\r\n" +
                        "Content-Length: " + payload.Length + "\r\n\r\n";
                    byte[] hb = Encoding.ASCII.GetBytes(head);
                    byte[] all = hb;
                    if (payload.Length > 0)
                    {
                        all = new byte[hb.Length + payload.Length];
                        Buffer.BlockCopy(hb, 0, all, 0, hb.Length);
                        Buffer.BlockCopy(payload, 0, all, hb.Length, payload.Length);
                    }
                    // 从「准备写请求」这一刻起就当作可能已发出：WriteAll 是分块写的，
                    // 首块就含 Authorization，写到一半失败时不能当成没发过去（复审 P1）。
                    // 宁可这次不回退 HTTP，也绝不把带密钥的同一请求重放一遍。
                    sent = true;
                    pipe.WriteAll(all);
                    byte[] raw = pipe.ReadToEnd(timeoutMs);
                    ParseHttp(Encoding.UTF8.GetString(raw), r);
                    r.Ms = sw.ElapsedMilliseconds;
                    return r;
                }
            }
            catch (Exception ex)
            {
                _pipeName = null;
                r.Error = ex.GetType().Name + ": " + ex.Message;
                r.Ms = sw.ElapsedMilliseconds;
                // 请求已经写进管道，结果未知：绝不能再用 HTTP 重放一次（评审 R6-5）。
                if (sent) return r;
                // 只在「连接 / 写入前」失败时允许回退 HTTP，并重枚举管道。
                return null;
            }
        }

        struct ProcInfo { public uint Pid; public uint Parent; public string Name; }

        /// <summary>
        /// 管道服务端身份核对。返回 null 才允许发送 Secret。
        ///
        /// 关键现实：Clash Verge Rev 开 TUN 时，内核由 Session 0 的 clash_verge_service
        /// 提权启动，普通权限连 OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION) 都会被拒绝
        /// （Win32 错误 5）。所以不能把「读不到 MainModule / 令牌」当成不可信，
        /// 主路径改用普通权限一定读得到的两种系统表：
        ///   ① 进程快照：查出服务端 PID 的 exe 名和父进程链；
        ///   ② 服务表：确认进程链上确实有已登记的 Clash / mihomo Windows 服务。
        /// 会话只允许「当前会话」或「Session 0 服务会话」；能读进程时再叠加
        /// 完整路径与令牌 SID 的强校验。任一步发现身份不符都失败关闭。
        /// </summary>
        static string ValidatePipeServer(int pid)
        {
            if (pid <= 0) return "管道服务端 PID 无效";
            // 信任缓存绑定管道名并设 60 秒上限，避免内核重启后 PID 被复用就跳过全部核验（评审 R6-8）。
            if (_trustedPipePid == pid && _pipeTrustDetail != null &&
                string.Equals(_trustedPipeName, _pipeName, StringComparison.OrdinalIgnoreCase) &&
                unchecked((uint)(Environment.TickCount - (int)_trustedAtTick)) < 60000) return null;

            // 提权服务（Session 0）的会话号普通权限读不到，Win32 会返回 5。
            // 这种情况不能判失败，但必须改用更强的证据：Windows 服务表。
            uint serverSession, mySession = 0;
            bool sessionKnown = ProcessIdToSessionId((uint)pid, out serverSession);
            ProcessIdToSessionId(GetCurrentProcessId(), out mySession);
            bool serviceSession = !sessionKnown || (serverSession == 0 && mySession != 0);
            if (sessionKnown && serverSession != mySession && !serviceSession)
                return "管道服务端位于其他 Windows 会话（会话 " + serverSession + "）";

            var chain = ProcessChain((uint)pid);
            if (chain.Count == 0) return "无法在进程快照中确认管道服务端";
            if (!chain.Take(2).Any(e => IsKernelExecutable(e.Name)))
                return "管道服务端不是 mihomo / Clash 内核（" + chain[0].Name + "）";

            string detail = (sessionKnown ? "会话 " + serverSession : "会话不可读（提权服务）") +
                " · 进程链 " + string.Join(" ← ", chain.Take(2).Select(e => e.Name).ToArray());

            // 服务会话 / 会话不可读：必须能在服务表里找到这台机器上登记的 Clash 服务，
            // 且该服务的 PID 就在管道服务端的父进程链上。
            bool serviceVerified = false;
            if (serviceSession)
            {
                var services = KernelServicePids();
                if (services.Count == 0)
                    return "无法读取 Windows 服务表，提权内核身份无法核实";
                if (!chain.Any(e => services.ContainsKey(e.Pid)))
                    return "管道服务端未关联到已登记的 Clash / mihomo 服务";
                detail += " · 服务 " + string.Join(", ",
                    chain.Where(e => services.ContainsKey(e.Pid))
                         .Select(e => services[e.Pid]).ToArray());
                serviceVerified = true;
            }

            // 提权服务读不到句柄是正常的；读得到就再叠加路径 + 令牌强校验。
            IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
            if (h != IntPtr.Zero && h != new IntPtr(-1))
            {
                try
                {
                    var sb = new StringBuilder(1024);
                    uint size = 1024;
                    if (QueryFullProcessImageNameW(h, 0, sb, ref size))
                    {
                        string file = sb.ToString();
                        if (!File.Exists(file) || !IsKernelExecutable(file))
                            return "管道服务端的可执行文件路径不可信";
                        detail += " · 路径已核实";
                    }
                    string sid = TokenSid(h);
                    if (sid == null) detail += " · 令牌不可读（提权进程）";
                    else if (!IsCurrentUserOrSystemSid(sid))
                        return "管道服务端不属于当前用户或系统服务账户";
                    else detail += " · 令牌已核实";
                }
                finally { CloseHandle(h); }
            }
            else
            {
                // 读不到进程句柄时，只有已经对上服务表才放行（评审 R4-6）。
                if (!serviceVerified)
                    return "管道服务端读不到进程句柄，且未通过服务表核实";
                detail += " · 提权服务（路径/令牌不可读，已用服务表核实）";
            }

            _pipeTrustDetail = detail;
            _trustedPipePid = pid;
            _trustedPipeName = _pipeName;
            _trustedAtTick = Environment.TickCount;
            return null;
        }

        /// <summary>进程快照取 PID → (父 PID, exe 名)，再沿父链向上追 5 层。</summary>
        static List<ProcInfo> ProcessChain(uint pid)
        {
            var result = new List<ProcInfo>();
            var map = new Dictionary<uint, ProcInfo>();
            IntPtr snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
            if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return result;
            try
            {
                var e = new PROCESSENTRY32W();
                e.dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32W));
                if (!Process32FirstW(snap, ref e)) return result;
                do
                {
                    map[e.th32ProcessID] = new ProcInfo
                    {
                        Pid = e.th32ProcessID, Parent = e.th32ParentProcessID,
                        Name = e.szExeFile ?? ""
                    };
                }
                while (Process32NextW(snap, ref e));
            }
            catch { return result; }
            finally { CloseHandle(snap); }

            uint cur = pid;
            for (int i = 0; i < 5; i++)
            {
                ProcInfo info;
                if (!map.TryGetValue(cur, out info)) break;
                result.Add(info);
                if (info.Parent == 0 || info.Parent == cur) break;
                cur = info.Parent;
            }
            return result;
        }

        /// <summary>服务表里所有名字含 Clash / mihomo / Verge 的服务 PID → 服务名。</summary>
        static Dictionary<uint, string> KernelServicePids()
        {
            var result = new Dictionary<uint, string>();
            IntPtr scm = OpenSCManagerW(null, null, SC_MANAGER_ENUMERATE_SERVICE);
            if (scm == IntPtr.Zero) return result;
            try
            {
                uint need = 0, count = 0, resume = 0;
                EnumServicesStatusExW(scm, 0, SERVICE_WIN32, SERVICE_STATE_ALL,
                    IntPtr.Zero, 0, out need, out count, ref resume, null);
                if (need == 0) return result;

                uint size = need + 8192;
                IntPtr buf = Marshal.AllocHGlobal((int)size);
                try
                {
                    resume = 0;
                    uint got;
                    if (!EnumServicesStatusExW(scm, 0, SERVICE_WIN32, SERVICE_STATE_ALL,
                        buf, size, out need, out got, ref resume, null)) return result;

                    int stride = Marshal.SizeOf(typeof(ENUM_SERVICE_STATUS_PROCESSW));
                    for (uint i = 0; i < got; i++)
                    {
                        var item = (ENUM_SERVICE_STATUS_PROCESSW)Marshal.PtrToStructure(
                            new IntPtr(buf.ToInt64() + i * stride),
                            typeof(ENUM_SERVICE_STATUS_PROCESSW));
                        string name = Marshal.PtrToStringUni(item.lpServiceName) ?? "";
                        string disp = Marshal.PtrToStringUni(item.lpDisplayName) ?? "";
                        string both = (name + " " + disp).ToLowerInvariant();
                        uint p = item.ServiceStatusProcess.dwProcessId;
                        if (p != 0 && (both.Contains("clash") || both.Contains("mihomo") || both.Contains("verge")))
                            result[p] = name;
                    }
                }
                catch { }
                finally { Marshal.FreeHGlobal(buf); }
            }
            finally { CloseServiceHandle(scm); }
            return result;
        }

        internal static bool IsKernelExecutable(string fullPathOrName)
        {
            return ProbeSidecar.IsLikelyKernelExe(fullPathOrName);
        }

        /// <summary>判断监听控制端口的内核进程名是否明确归属于当前选中的客户端（仅允许已登记的纯净内核程序名）。</summary>
        internal static bool KernelProcessMatchesClient(string processName, string clientHint)
        {
            string raw = (processName ?? "").Trim();
            if (raw.Length == 0) return false;
            string exeName = Path.GetFileName(raw);
            if (!exeName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) exeName += ".exe";
            if (!IsKernelExecutable(exeName)) return false;

            string p = Path.GetFileNameWithoutExtension(exeName).ToLowerInvariant();
            string c = (clientHint ?? "").ToLowerInvariant();
            if (p.Length == 0 || c.Length == 0) return false;
            if (c.IndexOf("flclash", StringComparison.Ordinal) >= 0)
                return p == "flclashcore";
            if (c.IndexOf("verge", StringComparison.Ordinal) >= 0)
                return p == "verge-mihomo" || p == "verge-mihomo-alpha";
            if (c.IndexOf("nyanpasu", StringComparison.Ordinal) >= 0)
                return p == "nyanpasu-mihomo" || p == "nyanpasu-mihomo-alpha" ||
                       p == "clash" || p == "clash-meta";
            if (c.IndexOf("sparkle", StringComparison.Ordinal) >= 0)
                return p == "sparkle-mihomo" || p == "mihomo" || p == "mihomo-alpha" ||
                       p.StartsWith("mihomo-windows-", StringComparison.Ordinal);
            if (c.IndexOf("party", StringComparison.Ordinal) >= 0)
                return p == "party-mihomo" || p == "mihomo" || p == "mihomo-alpha" ||
                       p.StartsWith("mihomo-windows-", StringComparison.Ordinal);
            if (c.IndexOf("for windows", StringComparison.Ordinal) >= 0)
                return p == "clash-win64" || p == "clash-win32" || p == "clash-win" ||
                       p == "clash" || p == "clash-meta";
            return p == "mihomo" || p == "mihomo-alpha" || p == "clash" || p == "clash-meta" ||
                   p == "mihomo-core" || p == "clash-core" || p == "clash.meta" ||
                   p.StartsWith("mihomo-windows-", StringComparison.Ordinal);
        }

        /// <summary>读进程令牌的用户 SID；读不到返回 null（提权服务属正常情况）。</summary>
        static string TokenSid(IntPtr processHandle)
        {
            IntPtr token;
            if (!OpenProcessToken(processHandle, TOKEN_QUERY, out token)) return null;
            try
            {
                int length = 0;
                GetTokenInformation(token, TokenUserInfoClass, IntPtr.Zero, 0, out length);
                if (length <= 0) return null;
                IntPtr buffer = Marshal.AllocHGlobal(length);
                try
                {
                    int needed;
                    if (!GetTokenInformation(token, TokenUserInfoClass, buffer, length, out needed)) return null;
                    var user = (TokenUser)Marshal.PtrToStructure(buffer, typeof(TokenUser));
                    return user.User.Sid == IntPtr.Zero
                        ? null : new SecurityIdentifier(user.User.Sid).Value;
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            catch { return null; }
            finally { CloseHandle(token); }
        }

        static bool IsCurrentUserOrSystemSid(string sid)
        {
            // SYSTEM / LOCAL SERVICE / NETWORK SERVICE：Clash Verge 服务的合法运行账户
            if (sid == "S-1-5-18" || sid == "S-1-5-19" || sid == "S-1-5-20") return true;
            try
            {
                using (var me = WindowsIdentity.GetCurrent())
                    return me != null && me.User != null && me.User.Value == sid;
            }
            catch { return false; }
        }

        /// <summary>猜端口通道的安全规则：只允许 GET / HEAD 这类无副作用读，且绝不发送所选配置的密钥。
        /// 抽成纯函数供自检直接验证，不依赖真实内核（独立审阅第二轮 P1）。
        ///
        /// v1.3.7：以前只判断 body == null，于是 `DELETE /connections/{id}`（无请求体）
        /// 被当成「读操作」放行，与「猜出来的端口绝不写操作」的承诺矛盾。
        /// 现在按 HTTP 方法白名单判定：写方法即使没有 body 也一律拒绝。</summary>
        internal static bool GuessedEndpointSafe(string method, string body, out bool sendSecret)
        {
            sendSecret = false;                 // 没验明身份，不发 Bearer Secret
            if (body != null) return false;      // 带体的一律拒绝（GET 也不该带体）
            string m = (method ?? "").Trim();
            return m.Equals("GET", StringComparison.OrdinalIgnoreCase) ||
                   m.Equals("HEAD", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 发送控制密钥前的**唯一**闸门。抽出来是为了让「探测失败后带密钥重试」和
        /// 「正式请求」走同一套判断——v1.3.7 只在 TryHttp 里查了一次，而 Endpoint()
        /// 早在它之前就把 Secret 发出去验证过了（本机进程抢占配置端口并回 401 就能拿到密钥）。
        ///
        /// 三档语义：
        ///   Kernel  → 允许发密钥（已留痕）；
        ///   Other   → 拒绝，并把「这个端口其实是谁在听」写进错误里；
        ///   Unknown → 允许（系统表读不到时不能把所有只开 HTTP 的用户都锁死），但已留痕。
        /// </summary>
        static bool SecretGate(string ep, out bool sendSecret, out string error)
        {
            sendSecret = false;
            error = "";
            string detail;
            var verdict = ListenerVerdict(ep, out detail);
            if (verdict == ListenerKind.Other)
            {
                error = "控制端口 " + ep + " 的监听进程不是 mihomo / Clash：" + detail +
                        "；已阻止发送控制密钥";
                _securityError = error;
                return false;
            }
            sendSecret = true;
            return true;
        }

        /// <summary>
        /// 一次请求「能不能带控制密钥」的完整判定，TryHttp 与自检共用同一份实现。
        /// 拆出来的原因：v1.3.7 的漏洞是「GuessedEndpointSafe 说别发，调用方却把
        /// sendSecret 无条件改回 true」——只测纯函数永远发现不了。现在判定只有这一处，
        /// 调用点没有第二次赋值的机会，自检也能直接断言真实决策结果。
        /// 返回 false 表示这次请求应当被整条拒绝。
        /// </summary>
        internal static bool SecretDecision(string method, string body, bool guessed, string ep,
                                            out bool sendSecret, out string error)
        {
            sendSecret = false;
            error = "";
            if (guessed)
            {
                // 白名单只放行 GET/HEAD；猜出来的端口无论什么方法都不带密钥。
                if (!GuessedEndpointSafe(method, body, out sendSecret))
                {
                    error = "控制端口是探测猜测出来的，无法确认它属于本次选中的配置；已拒绝写操作";
                    return false;
                }
                sendSecret = false;
                return true;
            }
            return SecretGate(ep, out sendSecret, out error);
        }

        static ApiResult TryHttp(string method, string path, string body, int timeoutMs, Stopwatch sw,
                                 string knownConfigPath = null)
        {
            // 多实例时连 HTTP 回退也不能发密钥，避免把 A 的 Secret 送到 B（评审 R6-2）。
            if (!string.IsNullOrEmpty(_pipeAmbiguous))
                return new ApiResult { Error = _pipeAmbiguous, Ms = sw.ElapsedMilliseconds };
            string priorSecErr = _securityError;
            string ep = Endpoint(knownConfigPath);
            if (ep == null)
            {
                if (string.IsNullOrEmpty(_securityError) && !string.IsNullOrEmpty(priorSecErr))
                    _securityError = priorSecErr;
                if (!string.IsNullOrEmpty(_securityError))
                    return new ApiResult { Error = _securityError, Ms = sw.ElapsedMilliseconds };
                return null;
            }
            // 猜出来的端口没和目标内核建立可验证关联：不发这份配置的密钥，也不执行写操作。
            bool sendSecret;
            string gateError;
            if (!SecretDecision(method, body, _endpointGuessed, ep, out sendSecret, out gateError))
                return new ApiResult { Error = gateError, Ms = sw.ElapsedMilliseconds };
            if (_secret == null) _secret = ReadSecret(knownConfigPath);

            var r = new ApiResult();
            try
            {
                var req = (HttpWebRequest)WebRequest.Create("http://" + ep + path);
                req.Method = method;
                req.Timeout = Math.Max(1500, timeoutMs);
                req.ReadWriteTimeout = Math.Max(1500, timeoutMs);
                req.Proxy = null;          // 别让系统代理把控制请求也带走
                req.KeepAlive = false;
                req.AllowAutoRedirect = false;
                // 最后一道保险：不管上面怎么改，猜出来的端口一律不许带密钥。
                // v1.3.7 就是在这里之前被一句无条件 `sendSecret = true` 覆盖掉的，
                // 所以把不变量钉在真正用到它的这一行上。
                if (_endpointGuessed && sendSecret) sendSecret = false;
                string sec = CleanHeaderSecret(_secret);
                if (sendSecret && !string.IsNullOrEmpty(sec))
                    req.Headers["Authorization"] = "Bearer " + sec;

                if (body != null)
                {
                    byte[] data = Encoding.UTF8.GetBytes(body);
                    req.ContentType = "application/json";
                    req.ContentLength = data.Length;
                    using (var s = req.GetRequestStream()) s.Write(data, 0, data.Length);
                }

                using (var resp = (HttpWebResponse)req.GetResponse())
                {
                    r.Status = (int)resp.StatusCode;
                    r.Body = ReadBodyLimited(resp.GetResponseStream(), MaxHttpResponseBytes, timeoutMs, sw);
                }
            }
            catch (WebException we)
            {
                var resp = we.Response as HttpWebResponse;
                if (resp != null)
                {
                    r.Status = (int)resp.StatusCode;
                    try
                    {
                        r.Body = ReadBodyLimited(resp.GetResponseStream(), MaxHttpResponseBytes, timeoutMs, sw);
                    }
                    catch { }
                    resp.Close();
                }
                else
                {
                    _endpoint = null; _endpointTried = false; _endpointGuessed = false;
                    r.Error = we.Status + ": " + we.Message;
                }
            }
            catch (Exception ex)
            {
                _endpoint = null; _endpointTried = false; _endpointGuessed = false;
                r.Error = ex.GetType().Name + ": " + ex.Message;
            }
            r.Ms = sw.ElapsedMilliseconds;
            return r;
        }

        /// <summary>过滤控制密钥中的换行和空字符，杜绝 HTTP 请求头注入与管道写异常。</summary>
        internal static string CleanHeaderSecret(string secret)
        {
            if (string.IsNullOrEmpty(secret)) return "";
            for (int i = 0; i < secret.Length; i++)
            {
                char c = secret[i];
                if (c == '\r' || c == '\n' || c == '\0') return "";
            }
            return secret;
        }

        internal static void ParseHttp(string text, ApiResult r)
        {
            if (string.IsNullOrEmpty(text))
            {
                r.Status = 0;
                r.Body = "";
                if (string.IsNullOrEmpty(r.Error)) r.Error = "命名管道返回空响应";
                return;
            }
            int sep = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            string headTxt = sep < 0 ? text : text.Substring(0, sep);
            r.Body = sep < 0 ? "" : text.Substring(sep + 4);
            string firstLine = headTxt.Split(new[] { "\r\n" }, StringSplitOptions.None)[0];
            string[] parts = firstLine.Split(' ');
            if (parts.Length >= 2)
            {
                int st;
                if (int.TryParse(parts[1], out st)) r.Status = st;
            }
            if (r.Status == 0 && string.IsNullOrEmpty(r.Error))
                r.Error = "命名管道返回无效响应";
        }

        /// <summary>
        /// 挑控制地址。两条硬规则：
        /// ① external-controller 必须是纯本机地址，非本机一律拒绝且不回退，防止 Bearer Secret 走明文 HTTP 出本机；
        /// ② 端口能连上还不够，先用不带密钥的 /version 确认监听者确实是 mihomo。
        /// 评审 R4：knownConfigPath 传入时（一次保存链路里由 ApplyCore 传入本次已确认的配置路径），
        /// external-controller 直接从该配置读取（复用 Discovery.ReadController 兜底解析，不二次 Detect）；
        /// 不传时保持原行为：走 Discovery.Detect() 的缓存路径。
        /// </summary>
        static string Endpoint(string knownConfigPath = null)
        {
            if (_endpointTried && _endpoint != null) return _endpoint;
            _endpointTried = true;
            _endpoint = null;
            _securityError = null;

            string configured;
            string sameFileSecret = null;
            string clientHint;
            string targetCfgPath;
            if (!string.IsNullOrEmpty(knownConfigPath))
            {
                // R4：与绑定/secret 同一份配置，控制器地址不能另走一次 Detect() 猜测。
                // 收尾批 P3-1：同一次读出的 secret 一并往下传，不再让 ProbeMihomo
                // 重开文件读第二遍（少一次 IO，也少一次两套解析发散的机会）。
                targetCfgPath = knownConfigPath;
                Discovery.ReadController(knownConfigPath, out sameFileSecret, out configured);
                clientHint = Discovery.ClientForConfig(knownConfigPath);
                if (string.IsNullOrEmpty(clientHint))
                    clientHint = Discovery.GuessClient(Path.GetDirectoryName(knownConfigPath));
            }
            else
            {
                var env = Discovery.Detect();
                targetCfgPath = env.ConfigPath;
                configured = env.Controller;
                sameFileSecret = env.Secret;
                clientHint = env.Client;
            }
            if (!string.IsNullOrEmpty(configured))
            {
                string ep, reason;
                if (!TryLoopbackEndpoint(configured, out ep, out reason))
                {
                    _securityError = "控制接口地址不安全，已阻止发送控制密钥：" + reason;
                    return null;
                }
                // 地址是本机、但 /version 探不到：多半是客户端只开命名管道、没开 HTTP 端口，
                // 属于「这条通道不可用」，不是安全事件，不能报成「已阻止发送密钥」。
                // 只有非本机地址（上面 TryLoopbackEndpoint 拒绝）才需要硬阻止并给出安全提示。
                // 只有「来自同一份客户端配置」的地址才允许带该配置的 Secret 重试；
                // 下面那些猜出来的候选端口一律不带密钥，避免把 A 的 Secret 送到 B（评审 R6-2）。
                if (!ProbeMihomo(ep, true, knownConfigPath, sameFileSecret)) return null;
                _endpoint = ep;
                _endpointGuessed = false;   // 地址来自目标配置本身，可带该配置的密钥
                return _endpoint;
            }

            // v2.2.2 P1 安全收口：配置没有写明 external-controller 且无认证 secret 时，
            // 不能仅凭“本机有个叫 mihomo.exe 的进程”就放开 PUT/PATCH 写权限。
            // 仅当：1) 唯一命中的纯 127.x 精确回环监听端口可通 /version，且
            //       2) 该监听 PID 的命令行（-f 或默认 config.yaml 下的 -d）实证正在运行同一份配置文件，且
            //       3) SecretGate 核对通过，
            // 才允许可写升级；其余所有推测端口一律标为 _endpointGuessed = true（只读 GET/HEAD、禁止任何写操作）。
            if (string.IsNullOrEmpty(sameFileSecret) && !string.IsNullOrEmpty(clientHint))
            {
                var matchedEps = EnumMatchedKernelEndpoints(clientHint);
                var verifiedOk = new List<MatchedEndpoint>();
                foreach (var mep in matchedEps)
                {
                    if (ProbeVersion(mep.Endpoint, null) == ProbeResult.Ok)
                        verifiedOk.Add(mep);
                }
                if (verifiedOk.Count == 1)
                {
                    var only = verifiedOk[0];
                    bool proven = !string.IsNullOrEmpty(targetCfgPath) && PidProvesConfig(only.Pid, targetCfgPath);
                    bool maySend;
                    string gateErr;
                    if (CanTrustMatchedEndpointWrite(false, false, 1, only.ExactLoopback, proven) &&
                        SecretGate(only.Endpoint, out maySend, out gateErr))
                    {
                        _endpoint = only.Endpoint;
                        _endpointGuessed = false;
                        return _endpoint;
                    }
                    _endpoint = only.Endpoint;
                    _endpointGuessed = true;
                    return _endpoint;
                }
            }

            // 独立审阅第二轮 P1：这些端口是猜出来的，/version 只能证明「有个 mihomo 在听」，
            // 不能证明它加载的就是这次选中的配置。标记为未关联，TryHttp 会据此
            // 拒绝写操作、也不发送所选配置的 Bearer Secret。
            string[] candidates =
            {
                "127.0.0.1:9090", "127.0.0.1:9097", "127.0.0.1:6170",
                "127.0.0.1:9091", "127.0.0.1:47808", "127.0.0.1:9092"
            };
            foreach (var ep in candidates)
            {
                if (!ProbeMihomo(ep, false, guessed: true)) continue;   // 探测阶段本来就不带密钥
                _endpoint = ep;
                _endpointGuessed = true;
                return _endpoint;
            }
            return null;
        }

        struct MatchedEndpoint
        {
            public string Endpoint;
            public int Pid;
            public bool ExactLoopback;
        }

        /// <summary>无显式控制器 + 空 secret 时是否允许将命中端点升级为可写通道（纯函数供自检约束）。</summary>
        internal static bool CanTrustMatchedEndpointWrite(
            bool hasConfiguredController,
            bool hasSecret,
            int verifiedCount,
            bool exactLoopback,
            bool cmdlineProvesConfig)
        {
            if (hasConfiguredController) return true;
            if (hasSecret) return false;
            return verifiedCount == 1 && exactLoopback && cmdlineProvesConfig;
        }

        /// <summary>核对内核进程命令行中的 -f/--config（或无 -f 时仅面向 config.yaml 的 -d）是否指向目标配置文件。</summary>
        internal static bool CommandLineProvesConfig(string cmdLine, string expectedConfigPath)
        {
            if (string.IsNullOrWhiteSpace(cmdLine) || string.IsNullOrWhiteSpace(expectedConfigPath)) return false;
            string expectedFull;
            try { expectedFull = Path.GetFullPath(expectedConfigPath.Trim('"', '\'')); }
            catch { return false; }

            var args = Discovery.SplitCommandLine(cmdLine);
            string explicitCfg = null;
            string explicitDir = null;
            for (int i = 0; i < args.Count; i++)
            {
                string a = args[i];
                int eq = a.IndexOf('=');
                string flag = eq > 0 ? a.Substring(0, eq).ToLowerInvariant() : a.ToLowerInvariant();
                string val = null;
                if (eq > 0 && (flag == "-f" || flag == "--config" || flag == "-d" || flag == "--directory"))
                    val = a.Substring(eq + 1);
                else if (flag == "-f" || flag == "--config" || flag == "-d" || flag == "--directory")
                {
                    if (i + 1 < args.Count) { val = args[i + 1]; i++; }
                }
                if (string.IsNullOrEmpty(val)) continue;
                val = val.Trim('"', '\'');
                if (flag == "-f" || flag == "--config") explicitCfg = val;
                else explicitDir = val;
            }

            try
            {
                if (!string.IsNullOrEmpty(explicitCfg))
                {
                    if (!Path.IsPathRooted(explicitCfg)) return false;
                    string gotFull = Path.GetFullPath(explicitCfg);
                    return string.Equals(gotFull, expectedFull, StringComparison.OrdinalIgnoreCase);
                }
                if (!string.IsNullOrEmpty(explicitDir) &&
                    string.Equals(Path.GetFileName(expectedFull), "config.yaml", StringComparison.OrdinalIgnoreCase))
                {
                    if (!Path.IsPathRooted(explicitDir)) return false;
                    string gotDir = Path.GetFullPath(explicitDir).TrimEnd('\\');
                    string expectedDir = (Path.GetDirectoryName(expectedFull) ?? "").TrimEnd('\\');
                    return string.Equals(gotDir, expectedDir, StringComparison.OrdinalIgnoreCase);
                }
            }
            catch { }
            return false;
        }

        static bool PidProvesConfig(int pid, string expectedConfigPath)
        {
            if (pid <= 0 || string.IsNullOrEmpty(expectedConfigPath)) return false;
            try
            {
                using (var q = new System.Management.ManagementObjectSearcher(
                    "SELECT CommandLine FROM Win32_Process WHERE ProcessId = " + pid))
                {
                    foreach (System.Management.ManagementBaseObject mo in q.Get())
                    {
                        string cmd = "";
                        try { cmd = Convert.ToString(mo["CommandLine"]) ?? ""; } catch { }
                        if (CommandLineProvesConfig(cmd, expectedConfigPath)) return true;
                    }
                }
            }
            catch { }
            return false;
        }

        /// <summary>枚举当前本机 TCP 表中由与 clientHint 匹配的核实内核进程正在监听的回环端口（优先精确 127.x 监听）。</summary>
        static List<MatchedEndpoint> EnumMatchedKernelEndpoints(string clientHint)
        {
            var exactList = new List<MatchedEndpoint>();
            var wildcardList = new List<MatchedEndpoint>();
            if (string.IsNullOrEmpty(clientHint)) return exactList;
            var exactSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var wildSeenPorts = new HashSet<int>();
            try
            {
                int size = 0;
                uint rc = GetExtendedTcpTable(IntPtr.Zero, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_LISTENER, 0);
                if (rc != ERROR_INSUFFICIENT_BUFFER || size <= 0) return exactList;
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    int allocSize = size;
                    if (allocSize < 4) return exactList;
                    IntPtr buf = Marshal.AllocHGlobal(allocSize);
                    try
                    {
                        rc = GetExtendedTcpTable(buf, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_LISTENER, 0);
                        if (rc == ERROR_INSUFFICIENT_BUFFER) continue;
                        if (rc != 0) return exactList;
                        int count = Marshal.ReadInt32(buf);
                        int maxRows = (allocSize - 4) / 24;
                        if (count > maxRows) count = maxRows;
                        IntPtr row = IntPtr.Add(buf, 4);
                        for (int i = 0; i < count; i++)
                        {
                            uint rawAddr = unchecked((uint)Marshal.ReadInt32(row, 4));
                            bool isExactLoop = (rawAddr & 0xFF) == 127;
                            bool isAny = rawAddr == 0;
                            int rawPort = Marshal.ReadInt32(row, 8);
                            int port = ((rawPort & 0xFF) << 8) | ((rawPort >> 8) & 0xFF);
                            int pid = Marshal.ReadInt32(row, 20);
                            if ((isExactLoop || isAny) && port > 0 && port <= 65535 && pid > 0)
                            {
                                var chain = ProcessChain((uint)pid);
                                if (chain.Count > 0 &&
                                    chain.Take(2).Any(e => IsKernelExecutable(e.Name)) &&
                                    chain.Take(2).Any(e => KernelProcessMatchesClient(e.Name, clientHint)))
                                {
                                    if (isExactLoop)
                                    {
                                        string ep = new IPAddress(rawAddr) + ":" + port;
                                        if (exactSeen.Add(ep))
                                            exactList.Add(new MatchedEndpoint { Endpoint = ep, Pid = pid, ExactLoopback = true });
                                    }
                                    else if (wildSeenPorts.Add(port))
                                    {
                                        wildcardList.Add(new MatchedEndpoint { Endpoint = "127.0.0.1:" + port, Pid = pid, ExactLoopback = false });
                                    }
                                }
                            }
                            row = IntPtr.Add(row, 24);
                        }
                        break;
                    }
                    finally { Marshal.FreeHGlobal(buf); }
                }
            }
            catch { }
            return exactList.Count > 0 ? exactList : wildcardList;
        }

        /// <summary>只接受 127/::1 与 localhost 字面量；绝不解析任意主机名，也不接受 0.0.0.0。</summary>
        static bool TryLoopbackEndpoint(string raw, out string endpoint, out string reason)
        {
            endpoint = null;
            reason = "";
            string v = (raw ?? "").Trim().Trim('\'', '"');
            if (v.Length == 0) { reason = "地址为空"; return false; }

            Uri u;
            string candidate = v.IndexOf("://", StringComparison.Ordinal) >= 0 ? v : "http://" + v;
            if (!Uri.TryCreate(candidate, UriKind.Absolute, out u) || u.Scheme != Uri.UriSchemeHttp || u.Port <= 0 ||
                !string.IsNullOrEmpty(u.UserInfo) || u.AbsolutePath != "/" || !string.IsNullOrEmpty(u.Query))
            {
                reason = "只支持本机 http://host:port";
                return false;
            }

            string host = u.DnsSafeHost.Trim('[', ']');
            bool local = host.Equals("localhost", StringComparison.OrdinalIgnoreCase);
            IPAddress ip;
            if (!local && IPAddress.TryParse(host, out ip)) local = IPAddress.IsLoopback(ip);
            if (!local) { reason = "只允许 127.0.0.1、::1 或 localhost，不接受局域网/公网地址"; return false; }

            endpoint = (u.HostNameType == UriHostNameType.IPv6 ? "[" + host + "]" : host) + ":" + u.Port;
            return true;
        }

        /// <summary>
        /// 探测 /version。mihomo 上游把 /version 注册在 authentication(secret) 中间件组内，
        /// 配了 Secret 的实例对无密钥请求一律返 401 —— 以前这一步直接吞掉异常判「通道不可用」，
        /// 导致只开 HTTP 控制口且设了 Secret 的客户端完全连不上（外部评审 F6）。
        /// allowSecret=true 时（地址来自同一份客户端配置）才允许带密钥重试。
        /// 评审 R4：knownConfigPath 传入时，重试密钥按该配置读取（与 push 的绑定/推送Secret 同源）。
        ///
        /// v1.3.7：返回「明确结果」而不是 bool。以前 ProbeVersion 把所有异常都变成 false，
        /// 于是超时 / 404 / 500 / 假响应都会被当成「需要认证」，接着就把 Bearer Secret
        /// 发给一个还没验明身份的服务。现在只有真正的 401/403 才允许带密钥重试。
        /// </summary>
        static bool ProbeMihomo(string ep, bool allowSecret, string knownConfigPath = null,
                                string sameFileSecret = null, bool guessed = false)
        {
            // 第一次绝不带 Authorization 头：没设 Secret 的实例不需要多余的信息披露。
            var first = ProbeVersion(ep, null);
            if (first == ProbeResult.Ok) return true;
            if (!allowSecret || first != ProbeResult.Unauthorized) return false;
            // 收尾批 P3-1：Endpoint 已读过同一份配置时直接复用，不再二次读文件。
            string secret = sameFileSecret ?? ReadSecret(knownConfigPath);
            if (string.IsNullOrEmpty(secret)) return false;
            // v1.3.8：**带密钥的那一次请求之前**必须先核对监听进程。
            // v1.3.7 把核对放在 TryHttp 里，而本函数由 Endpoint() 更早调用——
            // 本机进程只要抢占配置里的控制端口、对无密钥 /version 回 401，
            // 就能在身份核对之前收到 Authorization: Bearer <secret>。
            // 401 只证明「这个服务要认证」，不能据此把它升级成可信身份。
            if (guessed) return false;
            bool maySend;
            string gateError;
            if (!SecretGate(ep, out maySend, out gateError) || !maySend) return false;
            return ProbeVersion(ep, secret) == ProbeResult.Ok;
        }

        internal enum ProbeResult
        {
            Error = 0,        // 连不上 / 超时 / 异常
            Unauthorized = 1, // 明确的 401/403：需要认证
            NotMihomo = 2,    // 有响应，但不像 mihomo（404/500/普通 HTTP 服务）
            Ok = 3
        }

        /// <summary>退出前离线判定返回值：确认无控制管道、无回环控制端口在听且无安全/鉴权阻止。</summary>
        internal sealed class ControlOfflineReport
        {
            public bool OfflineConfirmed;
            public int ObservedStatus;
            public string Detail = "";
        }

        /// <summary>纯函数判定：仅当没有任何 HTTP 响应（如 401/500）且无安全拦截/多实例歧义时，才允许继续走内核离线收尾。</summary>
        internal static bool ShouldSkipPushWhenControlOffline(
            int rulesStatus,
            int versionStatus,
            string securityError,
            string pipeAmbiguous)
        {
            return rulesStatus == 0 && versionStatus == 0 &&
                   string.IsNullOrEmpty(securityError) &&
                   string.IsNullOrEmpty(pipeAmbiguous);
        }

        /// <summary>清空陈旧通道缓存并实探一次当前管道与回环控制端口，核实代理内核是否确实已关闭。</summary>
        public static ControlOfflineReport CheckControlOffline(string knownConfigPath = null)
        {
            var rep = new ControlOfflineReport();
            lock (ChannelGate)
            {
                ClearChannelFieldsUnlocked();
                EnsureBoundToCurrentConfig(knownConfigPath);
                string cfg = !string.IsNullOrEmpty(knownConfigPath) ? knownConfigPath : _boundConfigPath;
                string hint = ResolveClientHint(cfg);

                string pipe = FindPipe(hint);
                if (!string.IsNullOrEmpty(_pipeAmbiguous))
                {
                    rep.Detail = _pipeAmbiguous;
                    return rep;
                }
                if (pipe != null)
                {
                    var sw = Stopwatch.StartNew();
                    _pipeName = pipe;
                    var viaPipe = TryPipe("GET", "/version", null, 2000, sw, cfg);
                    if (viaPipe != null)
                    {
                        rep.ObservedStatus = viaPipe.Status;
                        rep.Detail = viaPipe.Ok ? "控制管道仍在运行" : (!string.IsNullOrEmpty(viaPipe.Error) ? viaPipe.Error : ("HTTP " + viaPipe.Status));
                        return rep;
                    }
                    if (!string.IsNullOrEmpty(_securityError))
                    {
                        rep.Detail = _securityError;
                        return rep;
                    }
                }

                // 若配置显式写了回环控制器且该端口此刻确有监听并回了 HTTP 响应（如 401/500），不算离线
                if (!string.IsNullOrEmpty(cfg) && File.Exists(cfg))
                {
                    string sec, ctrl;
                    Discovery.ReadController(cfg, out sec, out ctrl);
                    string cfgEp, cfgReason;
                    if (!string.IsNullOrEmpty(ctrl) && TryLoopbackEndpoint(ctrl, out cfgEp, out cfgReason))
                    {
                        var pv = ProbeVersion(cfgEp, null);
                        if (pv != ProbeResult.Error)
                        {
                            rep.ObservedStatus = pv == ProbeResult.Unauthorized ? 401 : 200;
                            rep.Detail = "显式控制端口仍有响应（" + pv + "）";
                            return rep;
                        }
                    }
                }

                string ep = Endpoint(cfg);
                if (!string.IsNullOrEmpty(_securityError))
                {
                    rep.Detail = _securityError;
                    return rep;
                }
                if (!string.IsNullOrEmpty(_pipeAmbiguous))
                {
                    rep.Detail = _pipeAmbiguous;
                    return rep;
                }
                if (ep != null)
                {
                    rep.ObservedStatus = 200;
                    rep.Detail = "检测到活跃控制端口 " + ep;
                    return rep;
                }

                rep.OfflineConfirmed = true;
                rep.Detail = "无可用控制管道且无活跃控制端口";
                return rep;
            }
        }

        static ProbeResult ProbeVersion(string ep, string secret)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                string host; int port; bool tableOk;
                if (SplitHostPort(ep, out host, out port) &&
                    !IsLoopbackPortListening(host, port, out tableOk) && tableOk)
                    return ProbeResult.Error;

                var req = (HttpWebRequest)WebRequest.Create("http://" + ep + "/version");
                req.Method = "GET";
                req.Timeout = 900;
                req.ReadWriteTimeout = 900;
                req.Proxy = null;
                req.KeepAlive = false;
                req.AllowAutoRedirect = false;
                string cleanSec = CleanHeaderSecret(secret);
                if (!string.IsNullOrEmpty(cleanSec))
                    req.Headers["Authorization"] = "Bearer " + cleanSec;
                using (var resp = (HttpWebResponse)req.GetResponse())
                {
                    if (resp.StatusCode != HttpStatusCode.OK) return ProbeResult.NotMihomo;
                    string body = ReadBodyLimited(resp.GetResponseStream(), 64 * 1024, 900, sw);
                    return LooksLikeMihomoVersion(body) ? ProbeResult.Ok : ProbeResult.NotMihomo;
                }
            }
            catch (WebException we)
            {
                var resp = we.Response as HttpWebResponse;
                if (resp != null)
                {
                    int st = (int)resp.StatusCode;
                    resp.Close();
                    if (st == 401 || st == 403) return ProbeResult.Unauthorized;
                    return ProbeResult.NotMihomo;
                }
                return ProbeResult.Error;
            }
            catch { return ProbeResult.Error; }
        }

        const int MaxHttpResponseBytes = 16 * 1024 * 1024;

        /// <summary>
        /// 有上限、有总时限的响应读取。以前 HTTP 两处都用 StreamReader.ReadToEnd()，
        /// 本地控制口返回超大 / 持续增长的响应就会一直吃内存（管道那边本来有 16 MiB 上限）。
        /// </summary>
        internal static string ReadBodyLimited(Stream stream, int maxBytes, int deadlineMs, Stopwatch clock)
        {
            if (stream == null) return "";
            var ms = new MemoryStream();
            var buf = new byte[16 * 1024];
            while (true)
            {
                if (clock != null && clock.ElapsedMilliseconds > Math.Max(1000, deadlineMs))
                    throw new TimeoutException("读取控制接口响应超过时限（" + deadlineMs + " ms）");
                int n = stream.Read(buf, 0, buf.Length);
                if (n <= 0) break;
                if (ms.Length + n > maxBytes)
                    throw new IOException("控制接口响应超过 " + (maxBytes / (1024 * 1024)) + " MiB，已中断");
                ms.Write(buf, 0, n);
            }
            return Encoding.UTF8.GetString(ms.ToArray());
        }

        static bool LooksLikeMihomoVersion(string body)
        {
            if (string.IsNullOrEmpty(body) || body.Length > 4096) return false;
            try
            {
                var root = Json.AsObject(Json.Parse(body));
                if (root == null || string.IsNullOrEmpty(Json.Str(root, "version"))) return false;
                object meta;
                return root.TryGetValue("meta", out meta) && meta is bool && (bool)meta;
            }
            catch { return false; }
        }

        // ---------------------------------------------------------------- 监听进程核对
        //
        // 「配置里写着 127.0.0.1:9090」不等于「9090 上跑的就是那个内核」。
        // 本地任意进程都能抢占这个端口并回一段 {"meta":true,...}。所以在发送控制密钥 /
        // 推送配置之前，先按端口反查监听者 PID，再按进程快照核对它是不是 mihomo / Clash / Verge。
        // 判定分三档：明确是内核 → 放行；明确是别的进程 → 拒绝；查不到 → 放行但留痕
        // （查不到通常是系统表不可用，不能因此让所有只开 HTTP 的用户都存不了盘）。

        internal enum ListenerKind { Unknown = 0, Kernel = 1, Other = 2 }

        const int AF_INET = 2;
        const int AF_INET6 = 23;
        const int TCP_TABLE_OWNER_PID_LISTENER = 3;
        const uint ERROR_INSUFFICIENT_BUFFER = 122;

        [DllImport("iphlpapi.dll", SetLastError = true)]
        static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref int pdwSize, bool bOrder,
            int ulAf, int tableClass, uint reserved);

        static readonly object ListenerGate = new object();
        static string _listenerEp;
        static ListenerKind _listenerVerdict;
        static string _listenerDetail;
        static long _listenerAtTick;
        static int _listenerPid;

        /// <summary>缓存里那个 PID 还活着吗。内核退出后别的进程抢到同一端口时，
        /// 10 秒身份缓存会继续把人家的旧结论当成「这是内核」——先看 PID 是否还在。</summary>
        static bool ListenerStillAlive(int pid)
        {
            if (pid <= 0) return false;
            try
            {
                using (var p = System.Diagnostics.Process.GetProcessById(pid)) return !p.HasExited;
            }
            catch { return false; }
        }

        internal static ListenerKind ListenerVerdict(string ep, out string detail)
        {
            detail = "";
            // 同一个地址 10 秒内不重复查表：保存一次会连发好几个请求。
            // v1.3.8：缓存只按端点字符串复用，端口换主人时发现不了；
            // 缓存的是「内核」结论时额外确认那个 PID 还活着，死了就重新查。
            lock (ListenerGate)
            {
                if (string.Equals(_listenerEp, ep, StringComparison.OrdinalIgnoreCase) &&
                    unchecked((uint)(Environment.TickCount - (int)_listenerAtTick)) < ListenerCacheTtlMs(_listenerVerdict) &&
                    (_listenerVerdict != ListenerKind.Kernel || ListenerStillAlive(_listenerPid)))
                {
                    detail = _listenerDetail;
                    return _listenerVerdict;
                }
            }

            var verdict = ListenerKind.Unknown;
            string why = "控制接口地址格式无法解析";
            int foundPid = 0;
            try
            {
                string host; int port;
                if (SplitHostPort(ep, out host, out port) && port > 0)
                {
                    int pid;
                    if (TryFindListenerPid(host, port, out pid))
                    {
                        foundPid = pid;
                        var chain = ProcessChain((uint)pid);
                        string name = chain.Count > 0 ? chain[0].Name : "PID " + pid;
                        if (chain.Count > 0 && chain.Take(2).Any(e => IsKernelExecutable(e.Name)))
                        {
                            verdict = ListenerKind.Kernel;
                            why = "PID " + pid + " · " + name;
                        }
                        else
                        {
                            verdict = ListenerKind.Other;
                            why = "PID " + pid + " · " + name;
                        }
                    }
                    else why = "端口 " + port + " 的监听进程查不到";
                }
            }
            catch (Exception ex) { why = "监听进程核对失败：" + ex.Message; }

            lock (ListenerGate)
            {
                _listenerEp = ep; _listenerVerdict = verdict;
                _listenerDetail = why; _listenerAtTick = Environment.TickCount;
                _listenerPid = foundPid;
            }
            // 只在真正查表（非缓存命中）时留痕：一次保存会连发好几个请求，
            // 放在这里既保证「查不到也留痕」，又不会写出一串重复审计。
            if (verdict == ListenerKind.Kernel)
                AuditLog.Record("CTRL", "控制端口监听进程已核对：" + why);
            else if (verdict == ListenerKind.Unknown)
                AuditLog.Record("CTRL", "控制端口监听进程无法确认（" + why +
                                        "），按兼容策略放行；若本机有未知程序占用控制端口，请改用命名管道");
            detail = why;
            return verdict;
        }

        /// <summary>把 "127.0.0.1:9090" / "[::1]:9090" 拆成 host + port；只给监听核对用。</summary>
        static bool SplitHostPort(string ep, out string host, out int port)
        {
            host = ""; port = 0;
            if (string.IsNullOrEmpty(ep)) return false;
            int at = ep.LastIndexOf(':');
            if (at <= 0 || at == ep.Length - 1) return false;
            host = ep.Substring(0, at).Trim('[', ']');
            return int.TryParse(ep.Substring(at + 1), out port) && port > 0 && port <= 65535;
        }

        /// <summary>
        /// 按「本地地址 + 端口」查监听进程。只比端口是不够的：Windows 允许
        /// 127.0.0.1:9090 与 127.0.0.2:9090 分别由不同进程监听，也会出现
        /// IPv4 与 IPv6 同端口各有一个监听者。只按端口取第一条，会把别的监听者
        /// 的 PID 当成目标，身份核对既可能错放也可能错杀。
        /// 精确地址优先；通配监听（0.0.0.0 / ::）只作兜底，避免被通配者冒名顶替。
        /// </summary>
        static bool TryFindListenerPid(string host, int port, out int pid)
        {
            pid = 0;
            List<IPAddress> targets = LoopbackTargets(host);
            for (int pass = 0; pass < 2; pass++)
            {
                if (TryFindListenerPidAf(port, AF_INET, targets, pass == 1, out pid)) return true;
                if (TryFindListenerPidAf(port, AF_INET6, targets, pass == 1, out pid)) return true;
            }
            return false;
        }

        /// <summary>把 host 变成可比较的 IP 集合。只认回环字面量与 localhost，不做 DNS 解析，
        /// 避免把任意主机名带进这条安全判断里。</summary>
        static List<IPAddress> LoopbackTargets(string host)
        {
            var list = new List<IPAddress>();
            try
            {
                string h = (host ?? "").Trim().Trim('[', ']');
                if (h.Length == 0) return list;
                if (h.Equals("localhost", StringComparison.OrdinalIgnoreCase))
                {
                    list.Add(IPAddress.Loopback);
                    try { if (System.Net.Sockets.Socket.OSSupportsIPv6) list.Add(IPAddress.IPv6Loopback); }
                    catch { }
                    return list;
                }
                IPAddress ip;
                if (IPAddress.TryParse(h, out ip)) list.Add(ip);
            }
            catch { }
            return list;
        }

        public static uint ListenerCacheTtlMs(ListenerKind kind)
        {
            // Unknown 缩短缓存，防止内核慢启动或短暂切换端口时卡在「身份未知」长达 10 秒
            return kind == ListenerKind.Unknown ? 1500u : 10000u;
        }

        /// <summary>预检本机 TCP 监听表，毫秒级跳过无进程监听的回环端口（避免 Windows loopback 未监听端口吃满 Connect 超时）。</summary>
        internal static bool IsLoopbackPortListening(string host, int port, out bool tableAvailable)
        {
            tableAvailable = false;
            if (port <= 0 || port > 65535) return false;
            List<IPAddress> targets = LoopbackTargets(host);
            if (targets.Count == 0) return false;

            bool anyV4 = false, anyV6 = false;
            foreach (var t in targets)
            {
                if (t.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) anyV4 = true;
                else if (t.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6) anyV6 = true;
            }

            bool v4Ok = !anyV4, v6Ok = true;
            int unusedPid;
            if (anyV4)
            {
                if (ScanTcpTableAf(port, AF_INET, targets, false, out unusedPid, out v4Ok))
                { tableAvailable = true; return true; }
                bool v4WildOk;
                if (ScanTcpTableAf(port, AF_INET, targets, true, out unusedPid, out v4WildOk))
                { tableAvailable = true; return true; }
                v4Ok = v4Ok && v4WildOk;
            }

            bool hasV6Stack = false;
            try { hasV6Stack = System.Net.Sockets.Socket.OSSupportsIPv6; } catch { }
            if (anyV6 || hasV6Stack)
            {
                v6Ok = false;
                if (anyV6)
                {
                    if (ScanTcpTableAf(port, AF_INET6, targets, false, out unusedPid, out v6Ok))
                    { tableAvailable = true; return true; }
                }
                else
                {
                    v6Ok = true;
                }
                bool v6WildOk;
                if (ScanTcpTableAf(port, AF_INET6, targets, true, out unusedPid, out v6WildOk))
                { tableAvailable = true; return true; }
                v6Ok = v6Ok && v6WildOk;
            }

            tableAvailable = v4Ok && (v6Ok || !hasV6Stack);
            return false;
        }

        static bool TryFindListenerPidAf(int port, int af, List<IPAddress> targets, bool wildcard, out int pid)
        {
            bool unusedTableOk;
            return ScanTcpTableAf(port, af, targets, wildcard, out pid, out unusedTableOk);
        }

        static bool ScanTcpTableAf(int port, int af, List<IPAddress> targets, bool wildcard, out int pid, out bool tableOk)
        {
            pid = 0;
            tableOk = false;
            int rowSize = af == AF_INET ? 24 : 56;
            int portOff = af == AF_INET ? 8 : 20;
            int pidOff = af == AF_INET ? 20 : 52;
            // IPv4 的 0.0.0.0 只能服务 IPv4 目标；没有任何同族目标时不必看通配轮。
            if (wildcard && af == AF_INET)
            {
                bool anyV4 = false;
                foreach (var t in targets)
                    if (t.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) anyV4 = true;
                if (!anyV4) { tableOk = true; return false; }
            }

            int size = 0;
            uint rc = GetExtendedTcpTable(IntPtr.Zero, ref size, false, af, TCP_TABLE_OWNER_PID_LISTENER, 0);
            if (rc == 0) { tableOk = true; return false; }
            if (rc != ERROR_INSUFFICIENT_BUFFER || size <= 0) return false;

            // TCP 监听表在两次 P/Invoke 间可能扩容导致再次返回 ERROR_INSUFFICIENT_BUFFER，最多重试 3 轮
            for (int attempt = 0; attempt < 3; attempt++)
            {
                int allocSize = size;
                if (allocSize < 4) return false;
                IntPtr buf = Marshal.AllocHGlobal(allocSize);
                try
                {
                    rc = GetExtendedTcpTable(buf, ref size, false, af, TCP_TABLE_OWNER_PID_LISTENER, 0);
                    if (rc == ERROR_INSUFFICIENT_BUFFER) continue;
                    if (rc != 0) return false;
                    tableOk = true;
                    int count = Marshal.ReadInt32(buf);
                    if (count <= 0) return false;
                    int maxRows = (allocSize - 4) / rowSize;
                    if (count > maxRows) count = maxRows;
                    IntPtr row = IntPtr.Add(buf, 4);
                    for (int i = 0; i < count; i++)
                    {
                        int rawPort = Marshal.ReadInt32(row, portOff);
                        int p = ((rawPort & 0xFF) << 8) | ((rawPort >> 8) & 0xFF);
                        if (p == port && AddressMatches(row, af, targets, wildcard))
                        {
                            pid = Marshal.ReadInt32(row, pidOff);
                            if (pid > 0) return true;
                        }
                        row = IntPtr.Add(row, rowSize);
                    }
                    return false;
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            return false;
        }

        /// <summary>比较监听行的本地地址。exact 轮只认同一个 IP；wildcard 轮只认全零地址。</summary>
        static bool AddressMatches(IntPtr row, int af, List<IPAddress> targets, bool wildcard)
        {
            int len = af == AF_INET ? 4 : 16;
            byte[] addr = new byte[len];
            // MIB_TCPROW_OWNER_PID：dwState(0) dwLocalAddr(4) dwLocalPort(8)…
            // MIB_TCP6ROW_OWNER_PID：ucLocalAddr(0) dwLocalScopeId(16) dwLocalPort(20)…
            Marshal.Copy(af == AF_INET ? IntPtr.Add(row, 4) : row, addr, 0, len);

            bool allZero = true;
            for (int i = 0; i < len; i++) if (addr[i] != 0) { allZero = false; break; }
            if (wildcard) return allZero;
            if (allZero) return false;   // 通配地址留给兜底轮

            foreach (var t in targets)
            {
                byte[] want = t.GetAddressBytes();
                if (want.Length != len) continue;
                bool same = true;
                for (int i = 0; i < len; i++) if (want[i] != addr[i]) { same = false; break; }
                if (same) return true;
            }
            return false;
        }

        /// <summary>
        /// 收尾批 P2-1：knownConfigPath 把「回读 /configs、/rules」也锁到调用方已确认的
        /// 那一份配置。以前 GetJson 没有这个参数，Request 会回退到 Detect()；Detect 有
        /// 5 秒缓存，过期窗口内可能与本次推送绑定的不是同一份配置。不传时保持原行为。
        /// </summary>
        public sealed class ApiJsonResult
        {
            public bool Ok;
            public int Status;
            public string Error;
            public int Ms;
            public Dictionary<string, object> Data;
        }

        /// <summary>保留 HTTP 状态码与错误原因的 JSON 查询，避免把鉴权失败/超时误判成「数据为空」。</summary>
        public static ApiJsonResult ParseJsonResponse(ApiResult r)
        {
            var res = new ApiJsonResult
            {
                Status = r != null ? r.Status : 0,
                Ms = r != null ? (int)r.Ms : 0
            };
            if (r == null)
            {
                res.Error = "内核请求未返回结果";
                return res;
            }
            if (!r.Ok)
            {
                res.Error = !string.IsNullOrEmpty(r.Error) ? r.Error : ("HTTP " + r.Status);
                return res;
            }
            try
            {
                var obj = Json.AsObject(Json.Parse(r.Body));
                if (obj == null)
                {
                    res.Error = "内核返回了非 JSON 对象";
                    return res;
                }
                res.Ok = true;
                res.Data = obj;
                return res;
            }
            catch (Exception ex)
            {
                res.Error = "解析内核 JSON 失败: " + ex.Message;
                return res;
            }
        }

        public static ApiJsonResult FetchJson(string path, int timeoutMs = 10000, string knownConfigPath = null)
        {
            return ParseJsonResponse(Request("GET", path, null, timeoutMs, knownConfigPath));
        }

        public static Dictionary<string, object> GetJson(string path, int timeoutMs = 10000,
                                                          string knownConfigPath = null)
        {
            var res = FetchJson(path, timeoutMs, knownConfigPath);
            return res.Ok ? res.Data : null;
        }

        /// <summary>内核版本信息；用来判断某条规则类型能不能用，而不是推过去再被整份拒绝。</summary>
        public sealed class VersionInfo
        {
            public string Version = "";
            public bool Meta;
            public int Major, Minor;
            public bool AtLeast(int major, int minor)
            {
                if (Major != major) return Major > major;
                return Minor >= minor;
            }
        }

        static VersionInfo _version;
        static DateTime _versionAt;
        static readonly object VersionGate = new object();

        /// <summary>读一次 /version 并缓存 60 秒；读不到返回 null（调用方按「保守但可用」处理）。</summary>
        public static VersionInfo KernelVersion(string knownConfigPath = null)
        {
            lock (VersionGate)
            {
                if (_version != null && (DateTime.UtcNow - _versionAt).TotalSeconds < 60) return _version;
            }
            // 先释放 VersionGate 再请求内核：GetJson -> Forget 会按 ChannelGate -> ListenerGate -> VersionGate 加锁，
            // 若在 VersionGate 内发起请求，另一线程跑 Forget() 时会形成 ABBA 互锁死锁。
            var json = GetJson("/version", 3000, knownConfigPath);
            if (json == null) return null;
            var v = new VersionInfo();
            v.Version = Json.Str(json, "version") ?? "";
            v.Meta = Json.Bool(json, "meta", false);
            ParseVersion(v.Version, v);
            lock (VersionGate)
            {
                _version = v;
                _versionAt = DateTime.UtcNow;
                return v;
            }
        }

        static void ParseVersion(string text, VersionInfo v)
        {
            try
            {
                string s = (text ?? "").Trim();
                if (s.StartsWith("v", StringComparison.OrdinalIgnoreCase)) s = s.Substring(1);
                int dash = s.IndexOf('-');
                if (dash > 0) s = s.Substring(0, dash);
                string[] parts = s.Split('.');
                int a, b;
                if (parts.Length > 0 && int.TryParse(parts[0], out a)) v.Major = a;
                if (parts.Length > 1 && int.TryParse(parts[1], out b)) v.Minor = b;
            }
            catch { }
        }

        /// <summary>把完整配置文本推给内核（不落盘、不触发 SAFE_PATHS 限制）。
        /// 评审 R4：knownConfigPath 传入时（一次保存链路里由 ApplyCore 传入本次已确认的配置路径），
        /// 整条推送链——通道绑定判定、secret 读取、external-controller 读取——都使用这一份配置，
        /// 不再各自 Detect()；从而消除「payload 按 A 生成、通道按 B 绑定」的错配窗口。不传保持原行为。</summary>
        public static ApiResult PushConfig(string yamlText, int timeoutMs = 15000,
                                           string knownConfigPath = null)
        {
            return Request("PUT", "/configs", "{\"payload\":" + Json.Quote(yamlText) + "}", timeoutMs,
                           knownConfigPath);
        }

        /// <summary>关闭指定的一条旧连接。只由「本次改动软件」的定向重连调用。</summary>
        public static ApiResult DeleteConnection(string id, int timeoutMs = 5000,
                                                string knownConfigPath = null)
        {
            return Request("DELETE", "/connections/" + Uri.EscapeDataString(id ?? ""), null,
                           timeoutMs, knownConfigPath);
        }

        /// <summary>纯字符串自检：不联网，确认非本机控制地址一定被挡住。</summary>
        public static string SecuritySelfTest()
        {
            int pass = 0, fail = 0;
            var sb = new StringBuilder();
            sb.AppendLine();
            sb.AppendLine("控制接口安全自检");

            Action<string, bool, string> check = (name, ok, detail) =>
            {
                if (ok) pass++; else fail++;
                sb.AppendLine((ok ? "PASS  " : "FAIL  ") + name + (detail.Length > 0 ? "  · " + detail : ""));
            };

            string ep, reason;
            foreach (var good in new[] { "127.0.0.1:9090", "localhost:9090", "[::1]:9090", "http://127.0.0.1:9090" })
                check("允许本机 " + good, TryLoopbackEndpoint(good, out ep, out reason), ep);
            foreach (var bad in new[] { "192.168.1.100:9090", "0.0.0.0:9090", "8.8.8.8:9090", "https://127.0.0.1:9090", "localhost.evil.com:9090" })
                check("拒绝非本机/非 HTTP " + bad, !TryLoopbackEndpoint(bad, out ep, out reason), reason);
            check("识别 mihomo /version", LooksLikeMihomoVersion("{\"meta\":true,\"version\":\"v1.19.0\"}"), "");
            check("拒绝普通 HTTP 服务的 /version", !LooksLikeMihomoVersion("{\"version\":\"nginx/1.25\"}"), "");

            // IPv6 独立化：默认不碰；主动开启时只改顶层；关掉时能按原值恢复。
            const string ipv6Yaml = "# head\nmode: rule\nipv6: true\ntun:\n  ipv6: true\n";
            check("顶层 ipv6 原值识别", Guard.Ipv6Value(ipv6Yaml) == "true", "");
            string ipv6Changed = Guard.EnsureIpv6Off(ipv6Yaml);
            check("防泄漏只改顶层、不碰 tun 里的同名项",
                Guard.Ipv6Value(ipv6Changed) == "false" && ipv6Changed.Contains("  ipv6: true"), "");
            check("重复写入幂等", Guard.EnsureIpv6Off(ipv6Changed) == ipv6Changed, "");
            check("按原值恢复", Guard.Ipv6Value(Guard.RestoreIpv6(ipv6Changed, "true")) == "true", "");
            check("没记过原值就一个字不动", Guard.RestoreIpv6(ipv6Changed, "") == ipv6Changed, "");
            check("原值 absent 时删掉整行", Guard.Ipv6Value(Guard.RestoreIpv6(ipv6Changed, "absent")) == "absent", "");

            // 负向：不是内核进程的 PID 必须被挡住，否则等于「校验永远放行」。
            // 用「故意写错的 PID」和「本程序自己」各试一次，都不联网、不碰真实管道。
            int savedTrusted = _trustedPipePid;
            string savedDetail = _pipeTrustDetail;
            try
            {
                check("拒绝不存在的管道服务端 PID",
                    ValidatePipeServer(int.MaxValue - 1) != null, "失败关闭");
                check("拒绝非内核进程冒充管道服务端",
                    ValidatePipeServer(Process.GetCurrentProcess().Id) != null, "本程序不是内核");
                check("PID 为 0 时拒绝发送密钥", ValidatePipeServer(0) != null, "失败关闭");
            }
            finally
            {
                _trustedPipePid = savedTrusted;
                _pipeTrustDetail = savedDetail;
            }

            // P1-① 通道缓存绑定：判定逻辑是纯函数，直接驱动，不碰真管道 / 不联网。
            string savedBound = _boundConfigPath;
            try
            {
                // 空选择：不动缓存（不能把没有选中配置当成切换）。
                _boundConfigPath = "C:/X.yaml";
                check("绑定判定：当前无选中配置时不动缓存", !BindingStaleFor(""), "");

                // 绑定一致：复用缓存。
                _boundConfigPath = "C:\\Client A\\config.yaml";
                check("绑定判定：与缓存绑定同一份时复用", !BindingStaleFor("C:\\Client A\\config.yaml"), _boundConfigPath);
                check("绑定判定：路径比较大小写不敏感", !BindingStaleFor("c:\\client a\\CONFIG.yaml"), _boundConfigPath);

                // 绑定不一致：应判定为需要失效。
                _boundConfigPath = "C:\\Client A\\config.yaml";
                check("绑定判定：切到另一份配置时判失效", BindingStaleFor("C:\\Client B\\config.yaml"), "");

                // 首次绑定（缓存为空）应判失效，让后续重新枚举。
                _boundConfigPath = null;
                check("绑定判定：首次未绑定时判失效", BindingStaleFor("C:\\Client A\\config.yaml"), "");
            }
            finally
            {
                _boundConfigPath = savedBound;
            }

            // Forget 必须连绑定一起清空，否则下次无法重新绑定。
            string savedBoundForForget = _boundConfigPath;
            string savedPipeForForget = _pipeName;
            try
            {
                _boundConfigPath = "C:\\Client A\\config.yaml";
                _pipeName = "pipe-X";
                Forget();
                check("Forget 连通道缓存绑定一起清空", _boundConfigPath == null && _pipeName == null, "");
            }
            finally
            {
                _boundConfigPath = savedBoundForForget;
                _pipeName = savedPipeForForget;
            }

            // 评审 R4 断：knownConfigPath 传入时，通道绑定只按该路径判定，不再走 Discovery.Detect()。
            // 用两个假路径驱动：切换运行时必须整份失效（Forget），绑定目标必须是传入的那份路径。
            // 不传路径（null）时走原 Detect 分支，该分支依赖真实机器环境，不在纯自检里下断言。
            string r4SavedBound = _boundConfigPath;
            string r4SavedPipe = _pipeName;
            bool r4SavedSuppress = SuppressBindLog;
            try
            {
                SuppressBindLog = true;   // 自检驱动绑定不写审计账本
                _boundConfigPath = null;
                _pipeName = null;
                long fallbackBefore = DetectFallbackCount;
                EnsureBoundToCurrentConfig(@"C:\PP-TEST\known-config.yaml");
                check("R4-1 绑定只用传入的 knownConfigPath",
                    _boundConfigPath == @"C:\PP-TEST\known-config.yaml", _boundConfigPath ?? "<空>");
                // 收尾批 F-03（评审 §10 第 3 条的等价自动化）：一次保存链路里，
                // 传了同源路径就绝不能再回退到 Detect() 二次猜测。
                check("R4-3 传同源路径时不再 Detect 二次猜测",
                    DetectFallbackCount == fallbackBefore, "fallback=" + (DetectFallbackCount - fallbackBefore));
                EnsureBoundToCurrentConfig(@"C:\PP-TEST\another-config.yaml");
                check("R4-2 传另一份配置路径时整份失效重新绑定",
                    _boundConfigPath == @"C:\PP-TEST\another-config.yaml", _boundConfigPath ?? "<空>");
            }
            finally
            {
                SuppressBindLog = r4SavedSuppress;
                _boundConfigPath = r4SavedBound;
                _pipeName = r4SavedPipe;
            }

            // 独立审阅第二轮：以前这两条只反射确认「方法有 knownConfigPath 参数」，
            // 测不出调用点有没有真的把路径传进来。改为真实行为断言。
            long fb0 = DetectFallbackCount;
            EnsureBoundToCurrentConfig(@"C:\PP-TEST\same-config.yaml");
            ReadSecret(@"C:\PP-TEST\same-config.yaml");
            Profile.PolicyName(@"C:\PP-TEST\same-config.yaml");
            check("R4-4 绑定/密钥/策略名传同源路径时不再二次识别客户端",
                DetectFallbackCount == fb0, "fallback=" + (DetectFallbackCount - fb0));
            bool sendSecret;
            bool safeWrite = GuessedEndpointSafe("PUT", "{}", out sendSecret);
            bool noSecretOnWrite = !sendSecret;
            bool safeRead = GuessedEndpointSafe("GET", null, out sendSecret);
            check("R4-5 猜出的端口拒绝写操作", !safeWrite && safeRead, "");
            check("R4-6 猜出的端口读写都不发送所选配置的密钥",
                noSecretOnWrite && !sendSecret, "");
            // v1.3.7 回归：DELETE 没有请求体，以前会被「body == null」放行，
            // 等于允许对猜出来的端口关闭连接；现在按方法白名单拒绝。
            bool del = GuessedEndpointSafe("DELETE", null, out sendSecret);
            bool noSecretOnDelete = !sendSecret;
            bool post = GuessedEndpointSafe("POST", null, out sendSecret);
            bool putNoBody = GuessedEndpointSafe("PUT", null, out sendSecret);
            bool head = GuessedEndpointSafe("HEAD", null, out sendSecret);
            check("v1.3.7 猜出的端口拒绝 DELETE（即使没有请求体）", !del, "");
            check("v1.3.7 猜出的端口拒绝 POST / PUT（无请求体也算写）", !post && !putNoBody, "");
            check("v1.3.7 猜出的端口仍允许 HEAD 读", head, "");
            check("v1.3.7 猜出的端口任何写方法都不发密钥", noSecretOnDelete && !sendSecret, "");

            // 监听进程核对：在本进程开一个临时端口，反查结果必须是「不是内核」——
            // 这条同时验证 TCP 表解析的字段偏移没写错（错了会永远返回 Unknown）。
            try
            {
                var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                string detail;
                var verdict = ListenerVerdict("127.0.0.1:" + port, out detail);
                check("v1.3.7 能按端口反查监听进程（并拒绝非内核进程）",
                    verdict == ListenerKind.Other, verdict + " · " + detail);
                // v1.3.8：老实现只比端口号，于是「127.0.0.2:同端口」会命中 127.0.0.1 上的
                // 这个监听者，把别的地址的进程当成目标。现在必须匹配具体地址：没有监听者＝Unknown。
                string detail2;
                var wrongHost = ListenerVerdict("127.0.0.2:" + port, out detail2);
                check("v1.3.8 监听核对按「地址+端口」匹配，不被同端口的其他本地地址冒名",
                    wrongHost == ListenerKind.Unknown, wrongHost + " · " + detail2);
                // v1.3.8：猜出来的端口永远不发密钥，与监听者是谁无关——
                // 这条锁住「GuessedEndpointSafe 的 sendSecret=false 不会被调用方覆盖」。
                bool gateHardBlocked;
                string gateError;
                bool gateOk = SecretGate("127.0.0.1:" + port, out gateHardBlocked, out gateError);
                check("v1.3.8 非内核监听者前不放行控制密钥",
                    !gateOk && !gateHardBlocked && gateError.IndexOf("不是 mihomo", StringComparison.Ordinal) >= 0,
                    gateOk + " · " + gateError);
                listener.Stop();
            }
            catch (Exception ex)
            {
                check("v1.3.7 能按端口反查监听进程（并拒绝非内核进程）", false, ex.Message);
            }

            // v1.3.8：把「猜端口绝不发密钥」从纯函数断言升级成**调用点**断言。
            // v1.3.7 的 bug 正是「GuessedEndpointSafe 返回 false，TryHttp 随后
            // 无条件 sendSecret = true」——纯函数测试全绿，真实请求照样带 Bearer。
            bool gSend;
            string gErr;
            bool gRead = SecretDecision("GET", null, true, "127.0.0.1:1", out gSend, out gErr);
            bool gReadNoSecret = gRead && !gSend;
            bool gDel = SecretDecision("DELETE", null, true, "127.0.0.1:1", out gSend, out gErr);
            bool gDelRejected = !gDel && !gSend;
            check("v1.3.8 猜出的端口在决策函数里恒不发密钥（TryHttp 的唯一密钥来源）",
                gReadNoSecret && gDelRejected, "read=" + gRead + " send=" + gSend + " del=" + gDel);

            // v1.3.8 端到端：本地假服务对 /version 回 401，ProbeMihomo 绝不能把
            // Authorization 发出去。这条锁的是**调用顺序**——v1.3.7 身份核对放在
            // TryHttp 里，而 Endpoint() 早就用 ProbeMihomo 把 Secret 送出去了，
            // 纯函数测试再全绿也发现不了。这里直接抓真实请求头。
            System.Net.Sockets.TcpListener fakeSrv = null;
            try
            {
                fakeSrv = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
                fakeSrv.Start();
                int fakePort = ((IPEndPoint)fakeSrv.LocalEndpoint).Port;
                string gotAuth = null;
                var th = new System.Threading.Thread(() =>
                {
                    // 必须循环接受：ProbeMihomo 会连两次（先无密钥、再可能带密钥）。
                    // 只接一次的话，第二次连接被拒，测试会在「请求根本没发出去」的
                    // 情况下也显示通过——那是假绿。
                    try
                    {
                        while (true)
                        {
                            using (var c = fakeSrv.AcceptTcpClient())
                            using (var ns = c.GetStream())
                            {
                                ns.ReadTimeout = 1500;
                                var buf = new byte[4096];
                                var head = new StringBuilder();
                                int n;
                                while ((n = ns.Read(buf, 0, buf.Length)) > 0)
                                {
                                    head.Append(Encoding.ASCII.GetString(buf, 0, n));
                                    if (head.ToString().IndexOf("\r\n\r\n", StringComparison.Ordinal) >= 0) break;
                                }
                                if (head.ToString().IndexOf("Authorization:", StringComparison.OrdinalIgnoreCase) >= 0)
                                    gotAuth = head.ToString();
                                byte[] resp = Encoding.ASCII.GetBytes(
                                    "HTTP/1.1 401 Unauthorized\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                                ns.Write(resp, 0, resp.Length);
                                ns.Flush();
                            }
                        }
                    }
                    catch { }
                });
                th.IsBackground = true;
                th.Start();
                bool probed = ProbeMihomo("127.0.0.1:" + fakePort, true, null, "SELFTEST-SECRET");
                bool tblDead;
                bool livePortListening = IsLoopbackPortListening("127.0.0.1", fakePort, out tblDead);
                ControlOfflineReport off401 = null;
                ControlOfflineReport offClosed = null;
                var swOff = new Stopwatch();
                bool savedSuppress = SuppressBindLog;
                SuppressBindLog = true;
                string tmpOffDir = Path.Combine(Path.GetTempPath(), "pp_offcheck_" + Guid.NewGuid().ToString("N").Substring(0, 8), "com.follow", "flclash");
                try
                {
                    Directory.CreateDirectory(tmpOffDir);
                    string tmpOffCfg = Path.Combine(tmpOffDir, "config.yaml");
                    File.WriteAllText(tmpOffCfg, "external-controller: 127.0.0.1:" + fakePort + "\n", new UTF8Encoding(false));
                    off401 = CheckControlOffline(tmpOffCfg);
                    fakeSrv.Stop();
                    th.Join(3000);
                    swOff.Start();
                    offClosed = CheckControlOffline(tmpOffCfg);
                    swOff.Stop();
                }
                finally
                {
                    SuppressBindLog = savedSuppress;
                    try { Directory.Delete(Path.GetDirectoryName(Path.GetDirectoryName(tmpOffDir)), true); } catch { }
                    Forget();
                }
                var swDead = Stopwatch.StartNew();
                bool deadPortListening = IsLoopbackPortListening("127.0.0.1", fakePort, out tblDead);
                swDead.Stop();
                check("v1.3.8 本地假服务回 401 也拿不到 Secret（身份核对先于带密钥请求）",
                    !probed && gotAuth == null,
                    gotAuth == null ? "" : "服务端收到了 Authorization 头");
                check("v2.2.6 IsLoopbackPortListening 命中活跃端口并毫秒跳过已关闭端口",
                    livePortListening && (!tblDead || (!deadPortListening && swDead.ElapsedMilliseconds < 150)),
                    "live=" + livePortListening + " tbl=" + tblDead + " dead=" + deadPortListening + " ms=" + swDead.ElapsedMilliseconds);
                check("v2.2.6 ShouldSkipPushWhenControlOffline 与 CheckControlOffline 区分离线与 401",
                    ShouldSkipPushWhenControlOffline(0, 0, null, null) &&
                    !ShouldSkipPushWhenControlOffline(401, 0, null, null) &&
                    !ShouldSkipPushWhenControlOffline(0, 500, null, null) &&
                    !ShouldSkipPushWhenControlOffline(0, 0, "已阻止发送密钥", null) &&
                    !ShouldSkipPushWhenControlOffline(0, 0, null, "检测到 2 个 mihomo 控制管道") &&
                    off401 != null && !off401.OfflineConfirmed && off401.ObservedStatus == 401 &&
                    offClosed != null && offClosed.OfflineConfirmed && offClosed.ObservedStatus == 0 &&
                    swOff.ElapsedMilliseconds < 250,
                    "off401=" + (off401 == null ? "null" : (off401.OfflineConfirmed + "/" + off401.ObservedStatus)) +
                    " offClosed=" + (offClosed == null ? "null" : (offClosed.OfflineConfirmed + "/" + offClosed.ObservedStatus)) +
                    " offMs=" + swOff.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                check("v1.3.8 本地假服务回 401 也拿不到 Secret（身份核对先于带密钥请求）", false, ex.Message);
            }
            finally
            {
                try { if (fakeSrv != null) fakeSrv.Stop(); } catch { }
            }

            // v1.4.0：回归测试 — FetchJson 状态分诊 + Forget 全缓存清理
            var err401 = ParseJsonResponse(new ApiResult { Status = 401, Error = "secret 不匹配（HTTP 401）", Ms = 12 });
            var okObj = ParseJsonResponse(new ApiResult { Status = 200, Body = "{\"version\":\"1.19.0\"}", Ms = 8 });
            var badJson = ParseJsonResponse(new ApiResult { Status = 200, Body = "<html>oops</html>", Ms = 5 });
            check("v1.4.0 FetchJson 区分鉴权/协议错误与合法 JSON 对象",
                !err401.Ok && err401.Status == 401 && err401.Error.IndexOf("401", StringComparison.Ordinal) >= 0 &&
                okObj.Ok && okObj.Data != null && Json.Str(okObj.Data, "version") == "1.19.0" &&
                !badJson.Ok && !string.IsNullOrEmpty(badJson.Error),
                "401=" + err401.Error + " ok=" + okObj.Ok + " bad=" + badJson.Error);

            lock (ListenerGate)
            {
                _listenerEp = "127.0.0.1:9090";
                _listenerVerdict = ListenerKind.Other;
                _listenerDetail = "selftest";
                _listenerAtTick = unchecked((uint)Environment.TickCount);
                _listenerPid = 12345;
            }
            lock (VersionGate) { _version = new VersionInfo { Version = "stale" }; _versionAt = DateTime.UtcNow; }
            Forget();
            bool clearedAll;
            lock (ListenerGate) clearedAll = _listenerEp == null && _listenerVerdict == ListenerKind.Unknown && _listenerPid == 0;
            lock (VersionGate) clearedAll = clearedAll && _version == null && _versionAt == DateTime.MinValue;
            check("v1.4.0 Forget() 同步清空监听核对缓存与内核版本缓存",
                clearedAll && ListenerCacheTtlMs(ListenerKind.Unknown) < ListenerCacheTtlMs(ListenerKind.Kernel),
                "cleared=" + clearedAll);

            check("v1.4.0 CleanHeaderSecret 拒绝 CRLF/NUL 头注入并放行合法密钥",
                CleanHeaderSecret("my$ecret=1") == "my$ecret=1" &&
                CleanHeaderSecret("bad\r\nX-Test: 1") == "" &&
                CleanHeaderSecret("bad\nX-Test: 1") == "" &&
                CleanHeaderSecret("bad\0sec") == "",
                "");

            var badPipeEmpty = new ApiResult();
            ParseHttp("", badPipeEmpty);
            var badPipeGarbled = new ApiResult();
            ParseHttp("GARBAGE NO STATUS\r\n\r\n{}", badPipeGarbled);
            var okPipe = new ApiResult();
            ParseHttp("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\n\r\n{\"ok\":true}", okPipe);
            check("v1.4.0 ParseHttp 对空/无状态行管道响应填充明确错误且不静默放行",
                badPipeEmpty.Status == 0 && !string.IsNullOrEmpty(badPipeEmpty.Error) && !badPipeEmpty.Ok &&
                badPipeGarbled.Status == 0 && !string.IsNullOrEmpty(badPipeGarbled.Error) && !badPipeGarbled.Ok &&
                okPipe.Ok && okPipe.Body == "{\"ok\":true}",
                "emptyErr=" + badPipeEmpty.Error + " garbledErr=" + badPipeGarbled.Error);

            check("v2.2.0 IsRestControlPipeName 拒绝服务/Helper/FlClash-RPC 管道且按客户端家族强隔离",
                !IsRestControlPipeName("clash-verge-service") &&
                !IsRestControlPipeName("FlClashHelperService") &&
                !IsRestControlPipeName("FlClash") &&
                IsRestControlPipeName("verge-mihomo-production-123") &&
                !PipeMatchesClient("verge-mihomo-production-123", "FlClash") &&
                !PipeMatchesClient("verge-mihomo-production-123", "Sparkle") &&
                PipeMatchesClient("verge-mihomo-production-123", "Clash Verge Rev") &&
                KernelProcessMatchesClient("FlClashCore.exe", "FlClash") &&
                !KernelProcessMatchesClient("verge-mihomo.exe", "FlClash"),
                "");

            // v2.2.2 P1 & P2 回归：管道默认拒绝、内核精确文件名名单、无控制器+空Secret 默认只读（需 -f 实证才可写）、Forget 不阻塞持锁 I/O
            check("v2.2.2 管道筛选默认拒绝跨端复用并识别 Nyanpasu 管道",
                !PipeMatchesClient("mihomo-999", "Clash Verge Rev") &&
                !PipeMatchesClient("verge-mihomo-production-1", "Clash Nyanpasu") &&
                PipeMatchesClient("nyanpasu-mihomo", "Clash Nyanpasu") &&
                PipeMatchesClient("sparkle-mihomo", "Sparkle") &&
                IsGenericMihomoPipe("mihomo-abc") &&
                !IsGenericMihomoPipe("sparkle-mihomo") &&
                !PipeMatchesClient("weird-custom-pipe", "Sparkle"),
                "");

            check("v2.2.2 IsKernelExecutable 与 KernelProcessMatchesClient 精确放行内核并拒绝 GUI/服务外壳",
                !IsKernelExecutable("FlClash.exe") &&
                !IsKernelExecutable("clash-verge.exe") &&
                !IsKernelExecutable("clash-verge-service.exe") &&
                !IsKernelExecutable("FlClashHelperService.exe") &&
                !IsKernelExecutable("Mihomo Party.exe") &&
                IsKernelExecutable("FlClashCore.exe") &&
                IsKernelExecutable("verge-mihomo.exe") &&
                IsKernelExecutable("clash-win64.exe") &&
                !KernelProcessMatchesClient("FlClash.exe", "FlClash") &&
                !KernelProcessMatchesClient("clash-verge-service.exe", "Clash Verge Rev"),
                "");

            bool canWriteNoProof = CanTrustMatchedEndpointWrite(false, false, 1, true, false);
            bool canWriteWildcard = CanTrustMatchedEndpointWrite(false, false, 1, false, true);
            bool canWriteWithSecret = CanTrustMatchedEndpointWrite(false, true, 1, true, true);
            bool canWriteProven = CanTrustMatchedEndpointWrite(false, false, 1, true, true);
            bool writeBlockedReadOnly = !SecretDecision("PUT", "{}", true, "127.0.0.1:9090", out gSend, out gErr) && !gSend;
            check("v2.2.2 无控制器+空Secret 缺 -f 实证时严格保持只读并拒绝写操作",
                !canWriteNoProof && !canWriteWildcard && !canWriteWithSecret && canWriteProven &&
                writeBlockedReadOnly &&
                CommandLineProvesConfig("mihomo.exe -f \"C:\\A\\cfg.yaml\"", @"C:\A\cfg.yaml") &&
                !CommandLineProvesConfig("mihomo.exe -f \"C:\\B\\cfg.yaml\"", @"C:\A\cfg.yaml") &&
                !CommandLineProvesConfig("mihomo.exe", @"C:\A\cfg.yaml") &&
                !CommandLineProvesConfig("mihomo.exe -f \"cfg.yaml\"", @"C:\A\cfg.yaml") &&
                CommandLineProvesConfig("mihomo.exe -d \"C:\\A\"", @"C:\A\config.yaml") &&
                !CommandLineProvesConfig("mihomo.exe -d \"C:\\A\"", @"C:\A\clash-verge.yaml"),
                "");

            string fSavedBound = _boundConfigPath;
            try
            {
                _boundConfigPath = @"C:\PP-TEST\hold.yaml";
                var holdEntered = new System.Threading.ManualResetEventSlim(false);
                var holdRelease = new System.Threading.ManualResetEventSlim(false);
                var holder = new System.Threading.Thread(() =>
                {
                    lock (ChannelGate)
                    {
                        SyncForgetEpochUnlocked();
                        try
                        {
                            holdEntered.Set();
                            holdRelease.Wait(1500);
                        }
                        finally { SyncForgetEpochUnlocked(); }
                    }
                });
                holder.IsBackground = true;
                holder.Start();
                holdEntered.Wait(1000);
                var fgSw = Stopwatch.StartNew();
                Forget();
                fgSw.Stop();
                holdRelease.Set();
                holder.Join(1500);
                string afterBound;
                lock (ChannelGate) { afterBound = _boundConfigPath; }
                check("v2.2.2 Forget() 在 ChannelGate 被网络持锁时不阻塞并延迟自动作废绑定",
                    fgSw.ElapsedMilliseconds < 80 && afterBound == null,
                    "ms=" + fgSw.ElapsedMilliseconds + " after=" + (afterBound ?? "null"));
            }
            finally
            {
                _boundConfigPath = fSavedBound;
            }

            sb.AppendLine();
            sb.AppendLine("合计 " + (pass + fail) + " 项：" + pass + " 通过 / " + fail + " 失败");
            return sb.ToString();
        }

        /// <summary>
        /// 调试用：走一遍和正式请求相同的通道，但把原始响应（含状态行与头部）原样返回。
        ///
        /// 存在的理由：正式路径只解析 body，一旦路径写错或撞上非 mihomo 的服务，
        /// 看到的只是一句 404，分不清是「没连上」还是「连错了地方」。
        /// </summary>
        public static string RequestRaw(string method, string path, string body, int timeoutMs)
        {
            var sb = new StringBuilder();
            string pipe, sec, ambiguous;
            lock (ChannelGate)
            {
                EnsureBoundToCurrentConfig();   // P1-①：--raw 也复用 Endpoint() 缓存，同样要先按当前选中配置失效
                pipe = FindPipe();
                sec = ReadSecret();
                ambiguous = _pipeAmbiguous;
                sb.AppendLine("pipe     : " + (pipe ?? "<未找到>"));
                sb.AppendLine("secret   : " + (string.IsNullOrEmpty(sec) ? "未配置" : "已配置（不回显）"));
                sb.AppendLine("endpoint : " + (Endpoint() ?? "<无 HTTP 端口>"));
                if (!string.IsNullOrEmpty(_securityError)) sb.AppendLine("security : " + _securityError);
                sb.AppendLine();
                if (pipe == null) return sb.ToString();
                if (!string.IsNullOrEmpty(ambiguous))
                {
                    sb.AppendLine("[已阻止] 未发送控制密钥：" + ambiguous);
                    return sb.ToString();
                }

                try
                {
                    using (var p = PipeConnection.Open(PipeRoot + pipe))
                    {
                        string trustError = ValidatePipeServer(p.ServerProcessId);
                        sb.AppendLine("identity : " + (trustError != null ? "不通过" : (_pipeTrustDetail ?? "已核实")));
                        if (trustError != null)
                        {
                            sb.AppendLine("[已阻止] 未发送控制密钥：" + trustError);
                            return sb.ToString();
                        }
                        if (IsGenericMihomoPipe(pipe) && !string.IsNullOrEmpty(_boundConfigPath) &&
                            !PidProvesConfig(p.ServerProcessId, _boundConfigPath))
                        {
                            sb.AppendLine("[已阻止] 通用管道未通过当前配置命令行证明，拒绝发送密钥");
                            return sb.ToString();
                        }

                        byte[] payload = Encoding.UTF8.GetBytes(body ?? "");
                        var headSb = new StringBuilder();
                        headSb.Append(method).Append(" ").Append(path).Append(" HTTP/1.0\r\n");
                        headSb.Append("Host: localhost\r\n");
                        string cleanSec = CleanHeaderSecret(sec);
                        if (!string.IsNullOrEmpty(cleanSec))
                            headSb.Append("Authorization: Bearer ").Append(cleanSec).Append("\r\n");
                        headSb.Append("Content-Type: application/json\r\n");
                        headSb.Append("Content-Length: ").Append(payload.Length).Append("\r\n\r\n");
                        byte[] hb = Encoding.ASCII.GetBytes(headSb.ToString());
                        byte[] all = hb;
                        if (payload.Length > 0)
                        {
                            all = new byte[hb.Length + payload.Length];
                            Buffer.BlockCopy(hb, 0, all, 0, hb.Length);
                            Buffer.BlockCopy(payload, 0, all, hb.Length, payload.Length);
                        }
                        p.WriteAll(all);
                        sb.Append(Encoding.UTF8.GetString(p.ReadToEnd(timeoutMs)));
                    }
                }
                catch (Exception ex)
                {
                    sb.AppendLine("[异常] " + ex.GetType().Name + ": " + ex.Message);
                }
                return sb.ToString();
            }
        }

        static void ClearChannelFieldsUnlocked()
        {
            _pipeName = null;
            _secret = null;
            _endpoint = null;
            _endpointTried = false;
            _endpointGuessed = false;
            _securityError = null;
            _trustedPipePid = 0;
            _trustedPipeName = null;
            _trustedAtTick = 0;
            _pipeTrustDetail = null;
            _pipeAmbiguous = null;
            _boundConfigPath = null;
        }

        public static void Forget()
        {
            int target = System.Threading.Interlocked.Increment(ref _forgetEpoch);
            if (System.Threading.Monitor.TryEnter(ChannelGate, 0))
            {
                try
                {
                    ClearChannelFieldsUnlocked();
                    _appliedForgetEpoch = target;
                }
                finally { System.Threading.Monitor.Exit(ChannelGate); }
            }
            lock (ListenerGate)
            {
                _listenerEp = null;
                _listenerVerdict = ListenerKind.Unknown;
                _listenerDetail = null;
                _listenerAtTick = 0;
                _listenerPid = 0;
            }
            lock (VersionGate)
            {
                _version = null;
                _versionAt = DateTime.MinValue;
            }
        }
    }
}
