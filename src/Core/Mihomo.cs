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

        public static string LastPipeName { get { return _pipeName; } }
        public static string LastEndpoint { get { return _endpoint; } }
        public static string LastSecurityError { get { return _securityError; } }
        public static string LastPipeTrustDetail { get { return _pipeTrustDetail; } }

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

        /// <summary>找到 mihomo 的控制管道（优先 production）。</summary>
        public static string FindPipe()
        {
            var all = EnumNamedPipes();
            var hits = all
                .Where(n => n.StartsWith("verge-mihomo-", StringComparison.OrdinalIgnoreCase) ||
                            n.StartsWith("mihomo-", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (hits.Count == 0)
            {
                hits = all
                    .Where(n => n.IndexOf("mihomo", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                n.IndexOf("clash", StringComparison.OrdinalIgnoreCase) >= 0)
                    .ToList();
            }
            _pipeAmbiguous = null;
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

        public static string ReadSecret()
        {
            try
            {
                string p = Discovery.Detect().ConfigPath;
                if (string.IsNullOrEmpty(p) || !File.Exists(p)) return "";
                foreach (var line in File.ReadLines(p))
                {
                    if (line.StartsWith("secret:", StringComparison.Ordinal))
                        return line.Substring(7).Trim().Trim('\'', '"');
                }
            }
            catch { }
            return "";
        }

        /// <summary>发一次请求：先管道，失败再走本地 HTTP。</summary>
        public static ApiResult Request(string method, string path, string body, int timeoutMs)
        {
            var sw = Stopwatch.StartNew();
            var viaPipe = TryPipe(method, path, body, timeoutMs, sw);
            if (viaPipe != null) { HandleAuthFailure(viaPipe); return viaPipe; }

            var viaHttp = TryHttp(method, path, body, timeoutMs, sw);
            if (viaHttp != null) { HandleAuthFailure(viaHttp); return viaHttp; }

            return new ApiResult
            {
                Error = !string.IsNullOrEmpty(_pipeAmbiguous) ? _pipeAmbiguous :
                        !string.IsNullOrEmpty(_securityError) ? _securityError
                    : "找不到 mihomo 控制接口（Clash / mihomo 没启动？）",
                Ms = sw.ElapsedMilliseconds
            };
        }

        /// <summary>客户端重启换过 Secret / 控制面时，401/403 后重新读取，不必重启本程序（评审 R3-9）。</summary>
        static void HandleAuthFailure(ApiResult r)
        {
            if (r != null && (r.Status == 401 || r.Status == 403)) Forget();
        }

        static ApiResult TryPipe(string method, string path, string body, int timeoutMs, Stopwatch sw)
        {
            if (_pipeName == null) _pipeName = FindPipe();
            if (_pipeName == null && !string.IsNullOrEmpty(_pipeAmbiguous))
                return new ApiResult { Error = _pipeAmbiguous, Ms = sw.ElapsedMilliseconds };
            if (_pipeName == null) return null;
            if (_secret == null) _secret = ReadSecret();

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
                        // 身份校验抛异常同样失败关闭，不静默回退到 HTTP（评审 R5-1）。
                        _securityError = "管道身份校验异常：" + ex.Message;
                        r.Error = "已阻止向命名管道发送控制密钥：身份校验异常";
                        r.Ms = sw.ElapsedMilliseconds;
                        return r;
                    }
                    if (trustError != null)
                    {
                        _securityError = trustError;
                        r.Error = "已阻止向命名管道发送控制密钥：" + trustError;
                        r.Ms = sw.ElapsedMilliseconds;
                        return r;
                    }

                    byte[] payload = Encoding.UTF8.GetBytes(body ?? "");
                    string head =
                        method + " " + path + " HTTP/1.0\r\n" +
                        "Host: localhost\r\n" +
                        "Authorization: Bearer " + _secret + "\r\n" +
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

        static bool IsKernelExecutable(string fullPathOrName)
        {
            string name = Path.GetFileName(fullPathOrName ?? "").ToLowerInvariant();
            return name.Contains("mihomo") || name.Contains("clash") || name.Contains("verge");
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

        static ApiResult TryHttp(string method, string path, string body, int timeoutMs, Stopwatch sw)
        {
            // 多实例时连 HTTP 回退也不能发密钥，避免把 A 的 Secret 送到 B（评审 R6-2）。
            if (!string.IsNullOrEmpty(_pipeAmbiguous))
                return new ApiResult { Error = _pipeAmbiguous, Ms = sw.ElapsedMilliseconds };
            string ep = Endpoint();
            if (ep == null)
            {
                if (!string.IsNullOrEmpty(_securityError))
                    return new ApiResult { Error = _securityError, Ms = sw.ElapsedMilliseconds };
                return null;
            }
            if (_secret == null) _secret = ReadSecret();

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
                if (!string.IsNullOrEmpty(_secret))
                    req.Headers["Authorization"] = "Bearer " + _secret;

                if (body != null)
                {
                    byte[] data = Encoding.UTF8.GetBytes(body);
                    req.ContentType = "application/json";
                    req.ContentLength = data.Length;
                    using (var s = req.GetRequestStream()) s.Write(data, 0, data.Length);
                }

                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    r.Status = (int)resp.StatusCode;
                    r.Body = sr.ReadToEnd();
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
                        using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                            r.Body = sr.ReadToEnd();
                    }
                    catch { }
                    resp.Close();
                }
                else
                {
                    _endpoint = null; _endpointTried = false;
                    r.Error = we.Status + ": " + we.Message;
                }
            }
            catch (Exception ex)
            {
                _endpoint = null; _endpointTried = false;
                r.Error = ex.GetType().Name + ": " + ex.Message;
            }
            r.Ms = sw.ElapsedMilliseconds;
            return r;
        }

        static void ParseHttp(string text, ApiResult r)
        {
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
        }

        /// <summary>
        /// 挑控制地址。两条硬规则：
        /// ① external-controller 必须是纯本机地址，非本机一律拒绝且不回退，防止 Bearer Secret 走明文 HTTP 出本机；
        /// ② 端口能连上还不够，先用不带密钥的 /version 确认监听者确实是 mihomo。
        /// </summary>
        static string Endpoint()
        {
            if (_endpointTried && _endpoint != null) return _endpoint;
            _endpointTried = true;
            _endpoint = null;
            _securityError = null;

            string configured = Discovery.Detect().Controller;
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
                if (!ProbeMihomo(ep, true)) return null;
                _endpoint = ep;
                return _endpoint;
            }

            string[] candidates = { "127.0.0.1:9090", "127.0.0.1:9097", "127.0.0.1:6170", "127.0.0.1:9091" };
            foreach (var ep in candidates)
            {
                if (!ProbeMihomo(ep, false)) continue;
                _endpoint = ep;
                return _endpoint;
            }
            return null;
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
        /// </summary>
        static bool ProbeMihomo(string ep, bool allowSecret)
        {
            // 第一次绝不带 Authorization 头：没设 Secret 的实例不需要多余的信息披露。
            if (ProbeVersion(ep, null)) return true;
            if (!allowSecret) return false;
            string secret = ReadSecret();
            if (string.IsNullOrEmpty(secret)) return false;
            // 401 只说明「需要认证」，不能据此认定服务可信：地址已被 TryLoopbackEndpoint
            // 限定为本机回环，且带密钥重试后仍必须通过 LooksLikeMihomoVersion 校验响应体。
            return ProbeVersion(ep, secret);
        }

        static bool ProbeVersion(string ep, string secret)
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create("http://" + ep + "/version");
                req.Method = "GET";
                req.Timeout = 900;
                req.ReadWriteTimeout = 900;
                req.Proxy = null;
                req.KeepAlive = false;
                req.AllowAutoRedirect = false;
                if (!string.IsNullOrEmpty(secret))
                    req.Headers["Authorization"] = "Bearer " + secret;
                using (var resp = (HttpWebResponse)req.GetResponse())
                {
                    if (resp.StatusCode != HttpStatusCode.OK) return false;
                    string body;
                    using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                        body = sr.ReadToEnd();
                    return LooksLikeMihomoVersion(body);
                }
            }
            catch { return false; }
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

        public static Dictionary<string, object> GetJson(string path, int timeoutMs = 10000)
        {
            var r = Request("GET", path, null, timeoutMs);
            if (!r.Ok) return null;
            try { return Json.AsObject(Json.Parse(r.Body)); }
            catch { return null; }
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
        public static VersionInfo KernelVersion()
        {
            lock (VersionGate)
            {
                if (_version != null && (DateTime.UtcNow - _versionAt).TotalSeconds < 60) return _version;
                var json = GetJson("/version", 3000);
                if (json == null) return null;
                var v = new VersionInfo();
                v.Version = Json.Str(json, "version") ?? "";
                v.Meta = Json.Bool(json, "meta", false);
                ParseVersion(v.Version, v);
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

        /// <summary>把完整配置文本推给内核（不落盘、不触发 SAFE_PATHS 限制）。</summary>
        public static ApiResult PushConfig(string yamlText, int timeoutMs = 15000)
        {
            return Request("PUT", "/configs", "{\"payload\":" + Json.Quote(yamlText) + "}", timeoutMs);
        }

        /// <summary>关闭指定的一条旧连接。只由「本次改动软件」的定向重连调用。</summary>
        public static ApiResult DeleteConnection(string id, int timeoutMs = 5000)
        {
            return Request("DELETE", "/connections/" + Uri.EscapeDataString(id ?? ""), null, timeoutMs);
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
            string pipe = FindPipe();
            string sec = ReadSecret();
            sb.AppendLine("pipe     : " + (pipe ?? "<未找到>"));
            sb.AppendLine("secret   : " + (string.IsNullOrEmpty(sec) ? "未配置" : "已配置（不回显）"));
            sb.AppendLine("endpoint : " + (Endpoint() ?? "<无 HTTP 端口>"));
            if (!string.IsNullOrEmpty(_securityError)) sb.AppendLine("security : " + _securityError);
            sb.AppendLine();
            if (pipe == null) return sb.ToString();

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

                    byte[] payload = Encoding.UTF8.GetBytes(body ?? "");
                    string head =
                        method + " " + path + " HTTP/1.0\r\n" +
                        "Host: localhost\r\n" +
                        "Authorization: Bearer " + sec + "\r\n" +
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

        public static void Forget()
        {
            _pipeName = null;
            _secret = null;
            _endpoint = null;
            _endpointTried = false;
            _securityError = null;
            _trustedPipePid = 0;
            _trustedPipeName = null;
            _trustedAtTick = 0;
            _pipeTrustDetail = null;
            _pipeAmbiguous = null;
        }
    }
}
