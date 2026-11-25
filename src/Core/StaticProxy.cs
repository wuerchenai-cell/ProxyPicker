using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ProxyPicker.Core
{
    /// <summary>
    /// 可选「独立固定 IP（链式代理）」核心引擎：
    /// 1) 极简一行粘贴解析（支持 ip:port:user:pass、user:pass@ip:port、socks5://... 及多行标签文本）；
    /// 2) Windows 原生 DPAPI（crypt32.dll CryptProtectData）本地加密存储于 %LOCALAPPDATA%\ProxyPicker\static_proxy.dat，
    ///    与便携 settings.json 物理隔离，打包/分享绝不带出个人账密；
    /// 3) 纯内存幂等 YAML 注入（PP-STATIC-OUT + PP-STATIC-HOP），数学保证严格无环（DAG），
    ///    兼容块式/流式/空 proxies 与纯 proxy-providers 订阅，停用时零字节残留；
    /// 4) 复用 ProbeSidecar 与 NodeQuality 做零干扰真实链式体检与精准分层排错。
    /// </summary>
    internal static class StaticProxy
    {
        /// <summary>内核层固定纯 ASCII 落地节点名，避免中文/Emoji/逗号在规则与 API 中的兼容坑。</summary>
        public const string NodeName = "PP-STATIC-OUT";

        /// <summary>内核层固定纯 ASCII 前置跳板策略组名。</summary>
        public const string HopGroupName = "PP-STATIC-HOP";

        /// <summary>独立智能优选前置跳板模式：全量端到端测速自动挑选最低延迟前置，与主 VPN 节点完全解耦。</summary>
        public const string HopAuto = "auto";
        public const string HopFollow = "follow";
        public const string HopPin = "pin";
        /// <summary>直连落地模式（不经前置跳板）：适用于海外网络、自带中转或内网直达的 SOCKS5 节点。</summary>
        public const string HopDirect = "direct";

        /// <summary>旁路端到端测速临时节点前缀。</summary>
        internal const string BenchNodePrefix = "PP-BENCH-";

        /// <summary>双跳跨洋静态 SOCKS5 健康检查单次超时上限（毫秒）。</summary>
        public const int HealthTimeoutMs = 10000;

        /// <summary>智能优选跳板后台定期稳速刷新周期（分钟）：每 10 分钟零干扰巡检一次，确保固定 IP 通道长期稳定。</summary>
        public const int AutoHopRefreshIntervalMinutes = 10;

        /// <summary>独立固定 IP 实时端到端延迟探测间隔（秒，独立于主节点测速周期）。</summary>
        public const int StaticHealthCheckIntervalSeconds = 45;

        /// <summary>检测到延迟连续劣化时，提前触发优选的防抖冷却（秒）。</summary>
        public const int AutoHopDegradeCooldownSeconds = 180;

        /// <summary>检测到超时不可达时，触发紧急重优切换的防抖冷却（秒）。</summary>
        public const int AutoHopEmergencyCooldownSeconds = 120;

        /// <summary>
        /// 稳态优选综合评分：兼顾平均延迟与多轮抖动（均值 + 抖动/2），
        /// 防止挑到单次偶发偏低但剧烈波动的不稳定节点。
        /// </summary>
        public static int ComputeStabilityScore(int avgMs, int jitterMs)
        {
            if (avgMs <= 0) return int.MaxValue;
            int j = jitterMs > 0 ? jitterMs : 0;
            return avgMs + (j / 2);
        }

        /// <summary>
        /// 判断当前固定 IP 实时延迟是否出现明显劣化（触发提前稳速刷新）：
        /// 绝对延迟 &gt;= 750ms，或存在历史基线且当前 &gt;= 420ms 且较基线暴涨 &gt;= 65%。
        /// </summary>
        public static bool IsStaticDelayDegraded(int currentMs, int baselineMs)
        {
            if (currentMs <= 0) return false;
            if (currentMs >= 750) return true;
            if (baselineMs > 0 && currentMs >= 420 && currentMs * 100 >= baselineMs * 165)
                return true;
            return false;
        }

        /// <summary>
        /// 智能优选后台定期稳速刷新调度判据（供定时看守与健康检查共用）：
        /// 1) 超时不可达：满足急救冷却（120 秒）立即触发重优；
        /// 2) 延迟连续 2 轮明显劣化：满足冷却（180 秒）提前重优；
        /// 3) 正常稳态：距本会话与上次落盘优选均满 10 分钟（或尚未实测过）自动刷新。
        /// </summary>
        public static bool ShouldRunScheduledAutoHopRefresh(
            bool isAutoMode,
            bool optimizing,
            DateTime lastSessionUtc,
            long lastSavedUtcTicks,
            DateTime nowUtc,
            int slowStreak,
            bool timedOut)
        {
            if (!isAutoMode || optimizing) return false;
            double sessionSec = lastSessionUtc == DateTime.MinValue ? 999999.0 : (nowUtc - lastSessionUtc).TotalSeconds;
            if (timedOut) return sessionSec >= AutoHopEmergencyCooldownSeconds;
            if (slowStreak >= 2) return sessionSec >= AutoHopDegradeCooldownSeconds;

            bool sessionDue = sessionSec >= AutoHopRefreshIntervalMinutes * 60.0;
            bool savedDue = lastSavedUtcTicks <= 0 ||
                            (nowUtc - new DateTime(lastSavedUtcTicks, DateTimeKind.Utc)).TotalMinutes >= AutoHopRefreshIntervalMinutes;
            return sessionDue && savedDue;
        }

        /// <summary>
        /// 静态 IP 健康检查防抖动判据：
        /// 手动测速失败立即提示；后台巡检首次超时先静默复检（底层规则依然锁定防漂移），
        /// 连续两轮仍超时且距上次报警 &gt;= 120 秒才触发告警，避免 10 秒瞬间抖动误吓唬用户。
        /// </summary>
        public static bool ShouldAlertStaticTimeout(bool manual, int timeoutStreak, DateTime lastAlertUtc, DateTime nowUtc)
        {
            if (manual) return true;
            if (timeoutStreak < 2) return false;
            return lastAlertUtc == DateTime.MinValue || (nowUtc - lastAlertUtc).TotalSeconds >= 120;
        }

        const string BeginMarker = "# PP-STATIC-BEGIN";
        const string EndMarker = "# PP-STATIC-END";

        static readonly byte[] DpapiEntropy = Encoding.UTF8.GetBytes("ProxyPicker.StaticProxy.DPAPI.v1");

        /// <summary>仅自检使用：覆盖凭据存储路径，绝不碰真机 %LOCALAPPDATA% 凭据。</summary>
        internal static string StorePathOverride;

        /// <summary>仅自检使用：覆盖当前内存配置，避免读取本机真实配置影响断言。</summary>
        internal static StaticProxyConfig TestConfigOverride;

        static readonly object Gate = new object();
        static StaticProxyConfig _cached;
        static bool _loaded;
        static string _lastLoadWarning = "";

        public sealed class StaticProxyConfig
        {
            public bool Enabled;
            public string Label = "独立固定IP";
            public string Host = "";
            public int Port;
            public string Username = "";
            public string Password = "";
            /// <summary>auto = 独立自动优选最低延迟跳板；follow = 自动跟随当前高速主出口组；pin = 锁定单条指定节点；direct = 直连落地。</summary>
            public string HopMode = HopFollow;
            public string PinnedHopNode = "";
            /// <summary>智能优选模式下当前端到端实测最快的前置节点名。</summary>
            public string AutoBestHopNode = "";
            /// <summary>智能优选模式下当前最快跳板的实测端到端延迟（ms，&lt;=0 表示尚未实测）。</summary>
            public int AutoBestDelayMs = -1;
            /// <summary>智能优选模式下的 Top 候选高速前置池（按端到端延迟从低到高排序）。</summary>
            public List<string> AutoBestHopPool = new List<string>();
            /// <summary>上次端到端优选完成时间（UTC Ticks）。</summary>
            public long AutoBestUpdatedAtTicks;
            /// <summary>多数静态 SOCKS5 节点不支持 UDP Associate，默认关闭并在规则层自动 REJECT UDP/QUIC 防止穿透回直连。</summary>
            public bool AllowUdp;
            /// <summary>写入 ip-version: ipv4，防止双栈环境下 IPv6 退化或泄漏。</summary>
            public bool ForceIpv4 = true;
            /// <summary>宁断不漂：落地不可达时禁止自动漂移回普通共享节点。</summary>
            public bool FailClosed = true;
            public long LastVerifiedAtTicks;
            public bool LastVerifiedOk;
            public string LastVerifiedSummary = "";

            public StaticProxyConfig Clone()
            {
                return new StaticProxyConfig
                {
                    Enabled = Enabled,
                    Label = Label ?? "独立固定IP",
                    Host = Host ?? "",
                    Port = Port,
                    Username = Username ?? "",
                    Password = Password ?? "",
                    HopMode = string.IsNullOrEmpty(HopMode) ? HopFollow : HopMode,
                    PinnedHopNode = PinnedHopNode ?? "",
                    AutoBestHopNode = AutoBestHopNode ?? "",
                    AutoBestDelayMs = AutoBestDelayMs,
                    AutoBestHopPool = AutoBestHopPool != null ? new List<string>(AutoBestHopPool) : new List<string>(),
                    AutoBestUpdatedAtTicks = AutoBestUpdatedAtTicks,
                    AllowUdp = AllowUdp,
                    ForceIpv4 = ForceIpv4,
                    FailClosed = FailClosed,
                    LastVerifiedAtTicks = LastVerifiedAtTicks,
                    LastVerifiedOk = LastVerifiedOk,
                    LastVerifiedSummary = LastVerifiedSummary ?? ""
                };
            }

            public bool IsValid(out string error)
            {
                return ValidateFields(Host, Port, Username, Password, out error);
            }

            public bool IsValid()
            {
                string _;
                return IsValid(out _);
            }

            public bool IsActive()
            {
                return Enabled && IsValid();
            }

            public string DisplayLabel
            {
                get
                {
                    string t = (Label ?? "").Trim();
                    return t.Length > 0 ? t : "独立固定IP";
                }
            }
        }

        public sealed class ParsedEndpoint
        {
            public string Host = "";
            public int Port;
            public string Username = "";
            public string Password = "";
            public string DetectedFormat = "";
        }

        public sealed class HopCandidateResult
        {
            public string NodeName = "";
            public int DelayMs = -1;
            public int JitterMs;
            public int StabilityScore = int.MaxValue;
            public int SuccessSamples;
        }

        public sealed class AutoHopBenchmarkResult
        {
            public bool Ok;
            public string Error = "";
            public string BestNode = "";
            public int BestDelayMs = -1;
            public int BestJitterMs;
            public int TestedCount;
            public int ReachableCount;
            public int VerifiedCount;
            public long ElapsedMs;
            public List<HopCandidateResult> TopCandidates = new List<HopCandidateResult>();
        }

        /// <summary>
        /// 对经过稳态复测的候选跳板排序：
        /// 1) 优先全部采样均成功（SuccessSamples 最高），淘汰复测期掉线的假稳节点；
        /// 2) 其次按 StabilityScore（平均延迟 + 抖动惩罚）从低到高；
        /// 3) 最后按平均延迟与名称确定性兜底。
        /// </summary>
        internal static void RankVerifiedHopCandidates(List<HopCandidateResult> candidates)
        {
            if (candidates == null || candidates.Count <= 1) return;
            candidates.Sort((a, b) =>
            {
                if (ReferenceEquals(a, b)) return 0;
                if (a == null) return 1;
                if (b == null) return -1;
                int cmp = b.SuccessSamples.CompareTo(a.SuccessSamples);
                if (cmp != 0) return cmp;
                int sa = a.StabilityScore > 0 && a.StabilityScore < int.MaxValue
                    ? a.StabilityScore : ComputeStabilityScore(a.DelayMs, a.JitterMs);
                int sb = b.StabilityScore > 0 && b.StabilityScore < int.MaxValue
                    ? b.StabilityScore : ComputeStabilityScore(b.DelayMs, b.JitterMs);
                cmp = sa.CompareTo(sb);
                if (cmp != 0) return cmp;
                cmp = a.DelayMs.CompareTo(b.DelayMs);
                if (cmp != 0) return cmp;
                return string.Compare(a.NodeName, b.NodeName, StringComparison.OrdinalIgnoreCase);
            });
        }

        public sealed class StaticProbeResult
        {
            public bool Ok;
            public string Stage = "";        // sidecar / hop / static / quality / done
            public string Error = "";
            public string Summary = "";
            public int HopDelayMs = -1;
            public int StaticDelayMs = -1;
            public string MainPolicy = "";
            public string SelectedHopNode = "";
            public AutoHopBenchmarkResult AutoBench;
            public NodeQuality.QualityReport Quality;
        }

        /// <summary>判断名称是否为本模块托管的内部节点/组（NodeManager 备选池与 FindPolicy 必须排除）。</summary>
        public static bool IsManagedName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            string t = name.Trim();
            return t.Equals(NodeName, StringComparison.OrdinalIgnoreCase) ||
                   t.Equals(HopGroupName, StringComparison.OrdinalIgnoreCase) ||
                   t.StartsWith(BenchNodePrefix, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>判断策略名是否指向独立固定 IP 通道。</summary>
        public static bool IsStaticPolicy(string policy)
        {
            return !string.IsNullOrWhiteSpace(policy) &&
                   policy.Trim().Equals(NodeName, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 启用独立固定 IP 且未勾选 AllowUdp 时，必须在规则层显式拦截该进程的 UDP（含 HTTP/3 QUIC 与 WebRTC）：
        /// mihomo 遇到指向 udp:false 代理的规则会跳过该规则，导致浏览器 QUIC UDP:443 穿透到 MATCH,DIRECT 泄漏本地真实 IP。
        /// </summary>
        public static bool ShouldBlockStaticUdp(StaticProxyConfig cfg)
        {
            return cfg != null && cfg.IsActive() && !cfg.AllowUdp;
        }

        /// <summary>生成指向 REJECT 的进程 UDP 防穿透规则文本。</summary>
        public static string BuildUdpGuardRule(bool isPathRule, string target)
        {
            return "AND,((NETWORK,UDP),(" +
                   (isPathRule ? "PROCESS-PATH-REGEX," : "PROCESS-NAME,") +
                   target + ")),REJECT";
        }

        /// <summary>判断 YAML 规则行文本是否为本工具生成的固定 IP UDP 防穿透规则。</summary>
        public static bool IsStaticUdpGuardRuleLine(string ruleText)
        {
            if (string.IsNullOrWhiteSpace(ruleText)) return false;
            string s = ruleText.Trim();
            if (!s.StartsWith("AND,", StringComparison.OrdinalIgnoreCase)) return false;
            if (!s.EndsWith(",REJECT", StringComparison.OrdinalIgnoreCase)) return false;
            return s.IndexOf("(NETWORK,UDP)", StringComparison.OrdinalIgnoreCase) >= 0 &&
                   (s.IndexOf("(PROCESS-NAME,", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    s.IndexOf("(PROCESS-PATH-REGEX,", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        /// <summary>
        /// 识别内核 /rules 返回的固定 IP UDP 防穿透规则并提取目标（exe 名或目录正则）。
        /// mihomo /rules 返回格式：type="AND", proxy="REJECT",
        /// payload="((Network,udp) &amp;&amp; (ProcessName,chrome.exe))" 或 "((Network,udp) &amp;&amp; (ProcessPathRegex,...))"。
        /// </summary>
        public static bool IsStaticUdpGuardKernelRule(Profile.RuleInfo r, out string extractedTarget)
        {
            extractedTarget = "";
            if (r == null) return false;
            if (!(r.Type ?? "").Equals("AND", StringComparison.OrdinalIgnoreCase)) return false;
            if (!Profile.IsBlockedPolicy(r.Proxy ?? "")) return false;
            string payload = (r.Payload ?? "").Trim();
            if (payload.Length == 0) return false;
            if (payload.IndexOf("Network,udp", StringComparison.OrdinalIgnoreCase) < 0 &&
                payload.IndexOf("NETWORK,UDP", StringComparison.OrdinalIgnoreCase) < 0)
                return false;

            string[] markers = { "(ProcessName,", "(PROCESS-NAME,", "(ProcessPathRegex,", "(PROCESS-PATH-REGEX," };
            foreach (var mk in markers)
            {
                int idx = payload.IndexOf(mk, StringComparison.OrdinalIgnoreCase);
                if (idx < 0) continue;
                int valStart = idx + mk.Length;
                // 找到与 '(' 配对的 ')'（目录正则中本身可能包含括号如 (?i) 与 \(x86\)）
                int depth = 1;
                int valEnd = -1;
                for (int i = valStart; i < payload.Length; i++)
                {
                    char c = payload[i];
                    if (c == '\\' && i + 1 < payload.Length) { i++; continue; }
                    if (c == '(') depth++;
                    else if (c == ')')
                    {
                        depth--;
                        if (depth == 0) { valEnd = i; break; }
                    }
                }
                if (valEnd > valStart)
                {
                    extractedTarget = payload.Substring(valStart, valEnd - valStart).Trim();
                    return extractedTarget.Length > 0;
                }
            }
            return false;
        }

        /// <summary>
        /// 自动优选前置跳板切换防抖判据：
        /// 手动触发 / 当前跳板不可达 / 尚无前置节点时立即切换；
        /// 后台平滑周期重测时，新跳板须比当前延迟至少快 60ms 且降幅 &gt;= 15% 才热切，避免相近节点来回横跳。
        /// </summary>
        public static bool ShouldSwitchAutoHop(
            string currentHop, int currentMs,
            string candidateHop, int candidateMs,
            bool currentBroken, bool manual)
        {
            if (string.IsNullOrWhiteSpace(candidateHop) || candidateMs <= 0) return false;
            if (IsManagedName(candidateHop)) return false;
            string cur = (currentHop ?? "").Trim();
            string cand = candidateHop.Trim();
            if (cur.Equals(cand, StringComparison.OrdinalIgnoreCase)) return false;
            if (manual || currentBroken || cur.Length == 0 || currentMs <= 0) return true;
            int gain = currentMs - candidateMs;
            return gain >= 60 && gain * 100 >= currentMs * 15;
        }

        /// <summary>物理隔离的凭据文件路径（永不落在 exe 旁的便携 settings.json）。</summary>
        public static string StorePath
        {
            get
            {
                if (!string.IsNullOrEmpty(StorePathOverride)) return StorePathOverride;
                return Path.Combine(Paths.OwnDir, "static_proxy.dat");
            }
        }

        public static string LastLoadWarning
        {
            get { lock (Gate) return _lastLoadWarning ?? ""; }
        }

        /// <summary>当前生效的静态代理配置快照（克隆，线程安全）。</summary>
        public static StaticProxyConfig Current
        {
            get
            {
                lock (Gate)
                {
                    if (TestConfigOverride != null) return TestConfigOverride.Clone();
                    if (!_loaded)
                    {
                        _cached = LoadFromDisk(StorePath, out _lastLoadWarning);
                        _loaded = true;
                        SyncRedactor(_cached);
                    }
                    return _cached.Clone();
                }
            }
        }

        public static bool IsEnabledAndValid
        {
            get { return Current.IsActive(); }
        }

        public static void Reload()
        {
            lock (Gate)
            {
                _loaded = false;
                _cached = LoadFromDisk(StorePath, out _lastLoadWarning);
                _loaded = true;
                SyncRedactor(_cached);
            }
        }

        public static bool Save(StaticProxyConfig cfg, out string error)
        {
            error = "";
            if (cfg == null)
            {
                error = "配置不能为空";
                return false;
            }
            var clean = cfg.Clone();
            clean.Host = (clean.Host ?? "").Trim();
            clean.Label = (clean.Label ?? "").Trim();
            if (clean.Label.Length == 0) clean.Label = "独立固定IP";
            if (clean.Label.Length > 24) clean.Label = clean.Label.Substring(0, 24);
            clean.HopMode = NormalizeHopMode(clean.HopMode);
            clean.PinnedHopNode = (clean.PinnedHopNode ?? "").Trim();
            clean.AutoBestHopNode = (clean.AutoBestHopNode ?? "").Trim();
            if (clean.AutoBestHopPool == null) clean.AutoBestHopPool = new List<string>();
            else
            {
                var dedupPool = new List<string>();
                var seenPool = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var n in clean.AutoBestHopPool)
                {
                    string tn = (n ?? "").Trim();
                    if (tn.Length > 0 && !IsManagedName(tn) && seenPool.Add(tn))
                    {
                        dedupPool.Add(tn);
                        if (dedupPool.Count >= 12) break;
                    }
                }
                clean.AutoBestHopPool = dedupPool;
            }

            if (clean.Enabled)
            {
                if (!clean.IsValid(out error)) return false;
            }
            else if (!string.IsNullOrEmpty(clean.Host) || clean.Port != 0)
            {
                // 未启用但填了部分字段时也拦控制字符，防止损坏落盘格式
                if (HasControlChar(clean.Host) || HasControlChar(clean.Username) || HasControlChar(clean.Password))
                {
                    error = "参数不能包含换行或控制字符";
                    return false;
                }
            }

            if (!SaveToDisk(StorePath, clean, out error)) return false;

            lock (Gate)
            {
                _cached = clean.Clone();
                _loaded = true;
                _lastLoadWarning = "";
                SyncRedactor(_cached);
            }
            return true;
        }

        static void SyncRedactor(StaticProxyConfig cfg)
        {
            if (cfg == null)
            {
                Redactor.ClearRuntimeSecrets();
                return;
            }
            Redactor.RegisterRuntimeSecrets(cfg.Host, cfg.Username, cfg.Password);
        }

        // ============================================================
        // 1. 参数校验与智能一行粘贴解析
        // ============================================================

        static string NormalizeHopMode(string mode)
        {
            if (string.Equals(mode, HopAuto, StringComparison.OrdinalIgnoreCase)) return HopAuto;
            if (string.Equals(mode, HopPin, StringComparison.OrdinalIgnoreCase)) return HopPin;
            if (string.Equals(mode, HopDirect, StringComparison.OrdinalIgnoreCase)) return HopDirect;
            return HopFollow;
        }

        static bool HasControlChar(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c < 0x20 || c == 0x7F) return true;
            }
            return false;
        }

        public static bool ValidateFields(string host, int port, string username, string password, out string error)
        {
            error = "";
            string h = (host ?? "").Trim();
            if (h.Length == 0) { error = "请填写服务器地址（IP 或域名）"; return false; }
            if (HasControlChar(h) || h.IndexOfAny(new[] { ' ', '\t', '/', '\\', '@', '"' }) >= 0)
            { error = "服务器地址格式无效（不能包含空格、斜杠或引号）"; return false; }
            // 允许标准 IPv4、域名、或纯 IPv6
            string bareHost = h.StartsWith("[", StringComparison.Ordinal) && h.EndsWith("]", StringComparison.Ordinal)
                ? h.Substring(1, h.Length - 2) : h;
            IPAddress parsedIp;
            bool isIp = IPAddress.TryParse(bareHost, out parsedIp);
            if (!isIp)
            {
                if (bareHost.IndexOf(':') >= 0)
                { error = "服务器地址包含多余冒号（端口请填在端口栏）"; return false; }
                if (bareHost.Length > 253 || !Regex.IsMatch(bareHost, @"^[A-Za-z0-9._-]+$"))
                { error = "服务器域名包含非法字符"; return false; }
            }
            if (port < 1 || port > 65535) { error = "端口必须在 1–65535 之间"; return false; }
            if (HasControlChar(username)) { error = "用户名不能包含换行或控制字符"; return false; }
            if (HasControlChar(password)) { error = "密码不能包含换行或控制字符"; return false; }
            if (string.IsNullOrEmpty(username) && !string.IsNullOrEmpty(password))
            { error = "填写了密码但未填写用户名"; return false; }
            return true;
        }

        /// <summary>
        /// 智能解析主流代理服务商与自建 SOCKS5 节点的常见粘贴格式：
        ///  A) socks5://user:pass@host:port 或 socks5h://...
        ///  B) user:pass@host:port
        ///  C) host:port:user:pass（密码中含冒号或 @ 时保留完整密码）
        ///  D) host:port（无账密）
        ///  E) 多行键值对文本（Host / Port / Username / Password 或中文标签）
        /// </summary>
        public static bool ParseQuickInput(string raw, out ParsedEndpoint ep, out string error)
        {
            ep = new ParsedEndpoint();
            error = "";
            if (string.IsNullOrWhiteSpace(raw))
            {
                error = "请先粘贴代理参数";
                return false;
            }

            string trimmed = raw.Trim();

            // 形态 E：多行键值文本（如代理服务商后台一次复制 4 行）
            if (trimmed.IndexOf('\n') >= 0 || trimmed.IndexOf('\r') >= 0)
            {
                if (TryParseMultiLine(trimmed, out ep))
                {
                    string vErr;
                    if (!ValidateFields(ep.Host, ep.Port, ep.Username, ep.Password, out vErr))
                    { error = vErr; return false; }
                    return true;
                }
                // 如果多行里只有一行有效非空内容，退回单行解析
                var nonEmpty = new List<string>();
                foreach (var ln in trimmed.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string t = ln.Trim();
                    if (t.Length > 0) nonEmpty.Add(t);
                }
                if (nonEmpty.Count == 1) trimmed = nonEmpty[0];
                else
                {
                    error = "未能从多行文本中识别出完整的服务器地址与端口，请检查粘贴内容";
                    return false;
                }
            }

            // 去掉首尾不小心带入的引号
            if (trimmed.Length >= 2 &&
                ((trimmed[0] == '"' && trimmed[trimmed.Length - 1] == '"') ||
                 (trimmed[0] == '\'' && trimmed[trimmed.Length - 1] == '\'')))
                trimmed = trimmed.Substring(1, trimmed.Length - 2).Trim();

            string work = trimmed;
            bool hadScheme = false;
            if (work.StartsWith("socks5h://", StringComparison.OrdinalIgnoreCase))
            {
                work = work.Substring("socks5h://".Length);
                hadScheme = true;
            }
            else if (work.StartsWith("socks5://", StringComparison.OrdinalIgnoreCase))
            {
                work = work.Substring("socks5://".Length);
                hadScheme = true;
            }
            else if (work.StartsWith("socks://", StringComparison.OrdinalIgnoreCase))
            {
                work = work.Substring("socks://".Length);
                hadScheme = true;
            }
            else if (work.IndexOf("://", StringComparison.Ordinal) >= 0)
            {
                error = "目前仅支持 SOCKS5 协议（请提取 socks5 参数）";
                return false;
            }

            // 去掉 URL 末尾残留的 `/` 或 `#remark`
            int hashIdx = hadScheme ? work.IndexOf('#') : -1;
            if (hashIdx >= 0) work = work.Substring(0, hashIdx);
            if (hadScheme) work = work.TrimEnd('/');

            // 形态 A / B：含 `@`（按最后一个 `@` 切分，兼容密码或用户名含 `@`）。
            // 若没有 socks5:// 前缀且 `@` 右侧不是合法 host:port，说明 `@` 实际在 host:port:user:pass 的密码中，
            // 自动回退到下面的 host:port:user:pass 分支（避免密码含 @ 误判）。
            int atIdx = work.LastIndexOf('@');
            if (atIdx >= 0)
            {
                string authPart = work.Substring(0, atIdx);
                string hostPart = work.Substring(atIdx + 1);
                string host;
                int port;
                if (TrySplitHostPort(hostPart, out host, out port))
                {
                    string user = "", pass = "";
                    if (authPart.Length > 0)
                    {
                        int cIdx = authPart.IndexOf(':');
                        if (cIdx >= 0)
                        {
                            user = authPart.Substring(0, cIdx);
                            pass = authPart.Substring(cIdx + 1);
                        }
                        else
                        {
                            user = authPart;
                        }
                        if (hadScheme)
                        {
                            user = SafeUnescape(user);
                            pass = SafeUnescape(pass);
                        }
                    }
                    ep.Host = host;
                    ep.Port = port;
                    ep.Username = user;
                    ep.Password = pass;
                    ep.DetectedFormat = hadScheme ? "socks5://URI" : "user:pass@host:port";
                    string vErr;
                    if (!ValidateFields(ep.Host, ep.Port, ep.Username, ep.Password, out vErr))
                    { error = vErr; return false; }
                    return true;
                }
                if (hadScheme)
                {
                    error = "无法解析服务器地址与端口，正确示例：socks5://user:pass@1.2.3.4:1080";
                    return false;
                }
            }

            // 形态 C / D：host:port:user:pass 或 host:port（支持 [ipv6]:port:...）
            if (work.StartsWith("[", StringComparison.Ordinal))
            {
                int rb = work.IndexOf(']');
                if (rb < 2 || rb + 1 >= work.Length || work[rb + 1] != ':')
                {
                    error = "IPv6 地址格式无效，示例：[2001:db8::1]:1080:user:pass";
                    return false;
                }
                string ip6 = work.Substring(1, rb - 1);
                string rest = work.Substring(rb + 2);
                string[] parts = rest.Split(new[] { ':' }, 3);
                int port;
                if (!int.TryParse(parts[0], out port))
                {
                    error = "端口格式无效：" + parts[0];
                    return false;
                }
                ep.Host = ip6;
                ep.Port = port;
                if (parts.Length >= 2) ep.Username = parts[1];
                if (parts.Length >= 3) ep.Password = parts[2];
                ep.DetectedFormat = parts.Length >= 2 ? "[ipv6]:port:user:pass" : "[ipv6]:port";
                string vErr;
                if (!ValidateFields(ep.Host, ep.Port, ep.Username, ep.Password, out vErr))
                { error = vErr; return false; }
                return true;
            }

            string[] segs = work.Split(new[] { ':' }, 4);
            if (segs.Length == 2 || segs.Length == 4)
            {
                int port;
                if (!int.TryParse(segs[1].Trim(), out port))
                {
                    error = "端口必须是数字（支持 ip:port:user:pass 或 socks5://user:pass@ip:port）";
                    return false;
                }
                ep.Host = segs[0].Trim();
                ep.Port = port;
                if (segs.Length == 4)
                {
                    ep.Username = segs[2];
                    ep.Password = segs[3];
                    ep.DetectedFormat = "host:port:user:pass";
                }
                else
                {
                    ep.DetectedFormat = "host:port";
                }
                string vErr;
                if (!ValidateFields(ep.Host, ep.Port, ep.Username, ep.Password, out vErr))
                { error = vErr; return false; }
                return true;
            }

            // 形态 F：网页表格直接复制的一行（Tab / 逗号 / 多空格分隔 4 列或 2 列：host port user pass）
            if (work.IndexOf('\t') >= 0 || work.IndexOf(',') >= 0 || work.IndexOf(' ') >= 0)
            {
                string[] cols = work.Split(new[] { '\t', ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                int tabPort;
                if ((cols.Length == 2 || cols.Length == 4) && int.TryParse(cols[1].Trim(), out tabPort))
                {
                    ep.Host = cols[0].Trim();
                    ep.Port = tabPort;
                    if (cols.Length == 4)
                    {
                        ep.Username = cols[2].Trim();
                        ep.Password = cols[3].Trim();
                        ep.DetectedFormat = "表格分列(4列)";
                    }
                    else
                    {
                        ep.DetectedFormat = "表格分列(2列)";
                    }
                    string vErr;
                    if (!ValidateFields(ep.Host, ep.Port, ep.Username, ep.Password, out vErr))
                    { error = vErr; return false; }
                    return true;
                }
            }

            error = "未能识别参数格式，支持：ip:port:user:pass、user:pass@ip:port、socks5://... 或表格整行复制";
            return false;
        }

        static bool TrySplitHostPort(string hostPort, out string host, out int port)
        {
            host = "";
            port = 0;
            string s = (hostPort ?? "").Trim();
            if (s.Length == 0) return false;
            if (s.StartsWith("[", StringComparison.Ordinal))
            {
                int rb = s.IndexOf(']');
                if (rb < 2 || rb + 2 > s.Length || s[rb + 1] != ':') return false;
                host = s.Substring(1, rb - 1);
                return int.TryParse(s.Substring(rb + 2), out port);
            }
            int col = s.LastIndexOf(':');
            if (col <= 0 || col >= s.Length - 1) return false;
            host = s.Substring(0, col).Trim();
            return int.TryParse(s.Substring(col + 1).Trim(), out port);
        }

        static bool TryParseMultiLine(string text, out ParsedEndpoint ep)
        {
            ep = new ParsedEndpoint { DetectedFormat = "多行键值" };
            string host = "", user = "", pass = "";
            int port = 0;
            foreach (var rawLine in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string line = rawLine.Trim();
                if (line.Length == 0) continue;
                int sep = line.IndexOfAny(new[] { ':', '：', '=' });
                if (sep <= 0) continue;
                string key = line.Substring(0, sep).Trim().ToLowerInvariant();
                string val = line.Substring(sep + 1).Trim();
                if (val.Length == 0) continue;

                if (key == "host" || key == "server" || key == "ip" || key == "address" ||
                    key == "主机" || key == "服务器" || key == "服务器地址" || key == "地址" || key == "代理地址")
                {
                    // 有些后台把 ip:port 写在同一行 Address: 1.2.3.4:1080
                    string h2; int p2;
                    if (port == 0 && TrySplitHostPort(val, out h2, out p2))
                    { host = h2; port = p2; }
                    else host = val;
                }
                else if (key == "port" || key == "端口" || key == "端口号")
                {
                    int.TryParse(val, out port);
                }
                else if (key == "user" || key == "username" || key == "account" ||
                         key == "用户名" || key == "账号" || key == "账户")
                {
                    user = val;
                }
                else if (key == "pass" || key == "password" || key == "pwd" || key == "密码")
                {
                    pass = val;
                }
            }
            if (string.IsNullOrEmpty(host) || port <= 0) return false;
            ep.Host = host;
            ep.Port = port;
            ep.Username = user;
            ep.Password = pass;
            return true;
        }

        static string SafeUnescape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            try { return Uri.UnescapeDataString(s); }
            catch { return s; }
        }

        // ============================================================
        // 2. Windows 原生 DPAPI 加密持久化（crypt32.dll，零外部依赖）
        // ============================================================

        [StructLayout(LayoutKind.Sequential)]
        struct DATA_BLOB
        {
            public int cbData;
            public IntPtr pbData;
        }

        const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool CryptProtectData(
            ref DATA_BLOB pDataIn,
            string szDataDescr,
            ref DATA_BLOB pOptionalEntropy,
            IntPtr pvReserved,
            IntPtr pPromptStruct,
            int dwFlags,
            out DATA_BLOB pDataOut);

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool CryptUnprotectData(
            ref DATA_BLOB pDataIn,
            IntPtr ppszDataDescr,
            ref DATA_BLOB pOptionalEntropy,
            IntPtr pvReserved,
            IntPtr pPromptStruct,
            int dwFlags,
            out DATA_BLOB pDataOut);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr LocalFree(IntPtr hMem);

        internal static byte[] ProtectBytes(byte[] plain)
        {
            if (plain == null) plain = new byte[0];
            DATA_BLOB inBlob = new DATA_BLOB();
            DATA_BLOB entBlob = new DATA_BLOB();
            DATA_BLOB outBlob = new DATA_BLOB();
            try
            {
                inBlob.cbData = plain.Length;
                inBlob.pbData = Marshal.AllocHGlobal(Math.Max(1, plain.Length));
                if (plain.Length > 0) Marshal.Copy(plain, 0, inBlob.pbData, plain.Length);

                entBlob.cbData = DpapiEntropy.Length;
                entBlob.pbData = Marshal.AllocHGlobal(DpapiEntropy.Length);
                Marshal.Copy(DpapiEntropy, 0, entBlob.pbData, DpapiEntropy.Length);

                if (!CryptProtectData(ref inBlob, "ProxyPickerStaticProxy", ref entBlob,
                    IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out outBlob))
                {
                    int err = Marshal.GetLastWin32Error();
                    throw new IOException("Windows DPAPI 加密失败（Win32=" + err + "）");
                }
                byte[] cipher = new byte[outBlob.cbData];
                if (outBlob.cbData > 0) Marshal.Copy(outBlob.pbData, cipher, 0, outBlob.cbData);
                return cipher;
            }
            finally
            {
                if (inBlob.pbData != IntPtr.Zero) Marshal.FreeHGlobal(inBlob.pbData);
                if (entBlob.pbData != IntPtr.Zero) Marshal.FreeHGlobal(entBlob.pbData);
                if (outBlob.pbData != IntPtr.Zero) LocalFree(outBlob.pbData);
            }
        }

        internal static byte[] UnprotectBytes(byte[] cipher)
        {
            if (cipher == null || cipher.Length == 0) return new byte[0];
            DATA_BLOB inBlob = new DATA_BLOB();
            DATA_BLOB entBlob = new DATA_BLOB();
            DATA_BLOB outBlob = new DATA_BLOB();
            try
            {
                inBlob.cbData = cipher.Length;
                inBlob.pbData = Marshal.AllocHGlobal(cipher.Length);
                Marshal.Copy(cipher, 0, inBlob.pbData, cipher.Length);

                entBlob.cbData = DpapiEntropy.Length;
                entBlob.pbData = Marshal.AllocHGlobal(DpapiEntropy.Length);
                Marshal.Copy(DpapiEntropy, 0, entBlob.pbData, DpapiEntropy.Length);

                if (!CryptUnprotectData(ref inBlob, IntPtr.Zero, ref entBlob,
                    IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out outBlob))
                {
                    int err = Marshal.GetLastWin32Error();
                    throw new IOException("Windows DPAPI 解密失败（可能来自其他电脑或账户，Win32=" + err + "）");
                }
                byte[] plain = new byte[outBlob.cbData];
                if (outBlob.cbData > 0) Marshal.Copy(outBlob.pbData, plain, 0, outBlob.cbData);
                return plain;
            }
            finally
            {
                if (inBlob.pbData != IntPtr.Zero) Marshal.FreeHGlobal(inBlob.pbData);
                if (entBlob.pbData != IntPtr.Zero) Marshal.FreeHGlobal(entBlob.pbData);
                if (outBlob.pbData != IntPtr.Zero) LocalFree(outBlob.pbData);
            }
        }

        static string SerializeConfig(StaticProxyConfig c)
        {
            var sb = new StringBuilder(512);
            sb.Append("{\n");
            sb.Append("  \"v\": 1,\n");
            sb.Append("  \"enabled\": ").Append(c.Enabled ? "true" : "false").Append(",\n");
            sb.Append("  \"label\": ").Append(Json.Quote(c.Label ?? "独立固定IP")).Append(",\n");
            sb.Append("  \"host\": ").Append(Json.Quote(c.Host ?? "")).Append(",\n");
            sb.Append("  \"port\": ").Append(c.Port).Append(",\n");
            sb.Append("  \"username\": ").Append(Json.Quote(c.Username ?? "")).Append(",\n");
            sb.Append("  \"password\": ").Append(Json.Quote(c.Password ?? "")).Append(",\n");
            sb.Append("  \"hopMode\": ").Append(Json.Quote(c.HopMode ?? HopFollow)).Append(",\n");
            sb.Append("  \"pinnedHopNode\": ").Append(Json.Quote(c.PinnedHopNode ?? "")).Append(",\n");
            sb.Append("  \"autoBestHopNode\": ").Append(Json.Quote(c.AutoBestHopNode ?? "")).Append(",\n");
            sb.Append("  \"autoBestDelayMs\": ").Append(c.AutoBestDelayMs).Append(",\n");
            sb.Append("  \"autoBestHopPool\": [");
            if (c.AutoBestHopPool != null && c.AutoBestHopPool.Count > 0)
            {
                for (int i = 0; i < c.AutoBestHopPool.Count; i++)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append(Json.Quote(c.AutoBestHopPool[i] ?? ""));
                }
            }
            sb.Append("],\n");
            sb.Append("  \"autoBestUpdatedAtTicks\": ").Append(c.AutoBestUpdatedAtTicks).Append(",\n");
            sb.Append("  \"allowUdp\": ").Append(c.AllowUdp ? "true" : "false").Append(",\n");
            sb.Append("  \"forceIpv4\": ").Append(c.ForceIpv4 ? "true" : "false").Append(",\n");
            sb.Append("  \"failClosed\": ").Append(c.FailClosed ? "true" : "false").Append(",\n");
            sb.Append("  \"lastVerifiedAtTicks\": ").Append(c.LastVerifiedAtTicks).Append(",\n");
            sb.Append("  \"lastVerifiedOk\": ").Append(c.LastVerifiedOk ? "true" : "false").Append(",\n");
            sb.Append("  \"lastVerifiedSummary\": ").Append(Json.Quote(c.LastVerifiedSummary ?? "")).Append("\n");
            sb.Append("}\n");
            return sb.ToString();
        }

        static long JsonLong(Dictionary<string, object> o, string key, long def = 0)
        {
            object v;
            if (o == null || !o.TryGetValue(key, out v) || v == null) return def;
            if (v is double) return (long)Math.Round((double)v);
            if (v is long) return (long)v;
            if (v is int) return (int)v;
            long parsed;
            if (v is string && long.TryParse((string)v, out parsed)) return parsed;
            return def;
        }

        static StaticProxyConfig DeserializeConfig(string json)
        {
            var root = Json.AsObject(Json.Parse(json));
            if (root == null) throw new InvalidDataException("凭据 JSON 结构损坏");
            var c = new StaticProxyConfig
            {
                Enabled = Json.Bool(root, "enabled", false),
                Label = Json.Str(root, "label") ?? "独立固定IP",
                Host = Json.Str(root, "host") ?? "",
                Port = Json.Int(root, "port", 0),
                Username = Json.Str(root, "username") ?? "",
                Password = Json.Str(root, "password") ?? "",
                HopMode = NormalizeHopMode(Json.Str(root, "hopMode")),
                PinnedHopNode = Json.Str(root, "pinnedHopNode") ?? "",
                AutoBestHopNode = Json.Str(root, "autoBestHopNode") ?? "",
                AutoBestDelayMs = Json.Int(root, "autoBestDelayMs", -1),
                AutoBestUpdatedAtTicks = JsonLong(root, "autoBestUpdatedAtTicks", 0),
                AllowUdp = Json.Bool(root, "allowUdp", false),
                ForceIpv4 = Json.Bool(root, "forceIpv4", true),
                FailClosed = Json.Bool(root, "failClosed", true),
                LastVerifiedAtTicks = JsonLong(root, "lastVerifiedAtTicks", 0),
                LastVerifiedOk = Json.Bool(root, "lastVerifiedOk", false),
                LastVerifiedSummary = Json.Str(root, "lastVerifiedSummary") ?? ""
            };
            object poolRaw;
            if (root.TryGetValue("autoBestHopPool", out poolRaw))
            {
                var poolArr = Json.AsArray(poolRaw);
                if (poolArr != null)
                {
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var item in poolArr)
                    {
                        string s = (item as string ?? "").Trim();
                        if (s.Length > 0 && !IsManagedName(s) && seen.Add(s))
                            c.AutoBestHopPool.Add(s);
                    }
                }
            }
            return c;
        }

        internal static bool SaveToDisk(string path, StaticProxyConfig cfg, out string error)
        {
            error = "";
            string temp = null;
            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                byte[] plain = new UTF8Encoding(false).GetBytes(SerializeConfig(cfg));
                byte[] cipher = ProtectBytes(plain);
                Array.Clear(plain, 0, plain.Length);

                temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    fs.Write(cipher, 0, cipher.Length);
                    fs.Flush(true);
                }
                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);
                temp = null;
                TightenFileAcl(path);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                if (temp != null && File.Exists(temp)) { try { File.Delete(temp); } catch { } }
                return false;
            }
        }

        internal static StaticProxyConfig LoadFromDisk(string path, out string warning)
        {
            warning = "";
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                    return new StaticProxyConfig();
                byte[] cipher = File.ReadAllBytes(path);
                if (cipher.Length == 0)
                    return new StaticProxyConfig();
                byte[] plain = UnprotectBytes(cipher);
                string json = Encoding.UTF8.GetString(plain);
                Array.Clear(plain, 0, plain.Length);
                return DeserializeConfig(json);
            }
            catch (Exception ex)
            {
                warning = "独立固定 IP 本地凭据无法解密（可能更换了电脑或已损坏），已自动暂停启用；如需使用请重新粘贴参数。" +
                          "[" + ex.GetType().Name + "]";
                return new StaticProxyConfig { Enabled = false };
            }
        }

        static void TightenFileAcl(string path)
        {
            try
            {
                var fi = new FileInfo(path);
                var sec = fi.GetAccessControl();
                sec.SetAccessRuleProtection(true, false);
                var me = WindowsIdentity.GetCurrent().User;
                if (me != null)
                    sec.AddAccessRule(new FileSystemAccessRule(me, FileSystemRights.FullControl, AccessControlType.Allow));
                sec.AddAccessRule(new FileSystemAccessRule(
                    new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                    FileSystemRights.FullControl, AccessControlType.Allow));
                fi.SetAccessControl(sec);
            }
            catch { }
        }

        // ============================================================
        // 3. 纯内存幂等 YAML 链式注入（严格无环 DAG + 6 种基线全兼容）
        // ============================================================

        /// <summary>
        /// 清理指向 PP-STATIC-OUT 的孤儿规则：
        /// 当独立固定 IP 未启用时，绝不能向内核推送指向不存在节点 PP-STATIC-OUT 的规则，否则 mihomo 会拒载整份配置。
        /// </summary>
        public static List<Profile.Item> SanitizeItemsForInactiveStatic(IList<Profile.Item> items, string fallbackPolicy)
        {
            var res = new List<Profile.Item>();
            if (items == null) return res;
            bool drop = string.IsNullOrEmpty(fallbackPolicy) ||
                        fallbackPolicy.Equals("DIRECT", StringComparison.OrdinalIgnoreCase);
            foreach (var it in items)
            {
                if (it == null) continue;
                if (IsStaticPolicy(it.Policy))
                {
                    if (drop) continue;
                    res.Add(it.IsPathRule
                        ? new Profile.Item(it.PathRegex, fallbackPolicy, true) { Derived = it.Derived }
                        : new Profile.Item(it.Exe, fallbackPolicy) { Derived = it.Derived });
                }
                else
                {
                    res.Add(it);
                }
            }
            return res;
        }

        /// <summary>统计名单中当前绑定了独立固定 IP 的主软件列表（排除派生规则）。</summary>
        public static List<string> FindStaticBoundExes(IEnumerable<Profile.Item> items)
        {
            var list = new List<string>();
            if (items == null) return list;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var it in items)
            {
                if (it == null || it.Derived || it.IsPathRule) continue;
                if (!IsStaticPolicy(it.Policy)) continue;
                string exe = Profile.Normalize(it.Exe);
                if (exe.Length > 0 && seen.Add(exe)) list.Add(exe);
            }
            return list;
        }

        /// <summary>
        /// 在内存中向 Clash / mihomo YAML 安全注入链式静态落地节点与前置跳板组。
        /// 保证：
        ///  1) cfg 未启用时原样逐字节返回 yaml；
        ///  2) 幂等（反复注入先移除旧 PP-STATIC 标记块，绝不累加重复节点）；
        ///  3) 严格无环（DAG）：PP-STATIC-OUT 绝不加入任何原策略组；PP-STATIC-HOP 仅指向合法的非静态前置目标；
        ///  4) 全标量走 Json.Quote() 双引号转义，杜绝特殊密码引发 YAML 注入；
        ///  5) 兼容块式 proxies、流式 proxies: [] / proxies: [{...}]、无 proxies 仅 proxy-providers 订阅。
        /// </summary>
        public static string InjectIntoYaml(string yaml, StaticProxyConfig cfg, string mainPolicy, string preferredFollowNode = null)
        {
            if (string.IsNullOrEmpty(yaml) || cfg == null || !cfg.Enabled) return yaml;

            string valErr;
            if (!cfg.IsValid(out valErr))
                throw new InvalidOperationException("独立固定 IP 参数无效：" + valErr);

            bool directHop = string.Equals(cfg.HopMode, HopDirect, StringComparison.OrdinalIgnoreCase);
            string hopTarget = (mainPolicy ?? "").Trim();
            if (!directHop &&
                (hopTarget.Length == 0 ||
                 hopTarget.Equals("DIRECT", StringComparison.OrdinalIgnoreCase) ||
                 hopTarget.Equals("REJECT", StringComparison.OrdinalIgnoreCase) ||
                 hopTarget.Equals("REJECT-DROP", StringComparison.OrdinalIgnoreCase) ||
                 hopTarget.Equals("GLOBAL", StringComparison.OrdinalIgnoreCase) ||
                 IsManagedName(hopTarget)))
            {
                throw new InvalidOperationException(
                    "无法为独立固定 IP 确定有效的前置跳板策略组（当前主策略组不可用），已停止注入以防止环路或直连泄漏");
            }

            string text = yaml.Replace("\r\n", "\n");
            // 1. 先剥离可能存在的旧注入块，保证幂等性
            text = StripStaticMarkedBlocks(text);

            // 2. 构造 PP-STATIC-OUT 节点 YAML 片段（直连模式不写 dialer-proxy；链式模式指向 PP-STATIC-HOP）
            var proxyEntry = BuildProxyNodeBlock(cfg, !directHop);
            text = InjectIntoTopListSection(text, "proxies", proxyEntry);

            if (!directHop)
            {
                // 3. 决定前置跳板组成员列表（绝不包含 PP-STATIC-OUT / PP-STATIC-HOP，防环路）
                var hopMembers = new List<string>();
                var hopSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                Action<string> tryAddHop = cand =>
                {
                    string n = (cand ?? "").Trim();
                    if (n.Length == 0 ||
                        IsManagedName(n) ||
                        n.Equals("DIRECT", StringComparison.OrdinalIgnoreCase) ||
                        n.Equals("REJECT", StringComparison.OrdinalIgnoreCase) ||
                        n.Equals("REJECT-DROP", StringComparison.OrdinalIgnoreCase) ||
                        n.Equals("GLOBAL", StringComparison.OrdinalIgnoreCase))
                        return;
                    if (!hopSeen.Contains(n) && YamlContainsProxyOrGroupName(text, n))
                    {
                        hopSeen.Add(n);
                        hopMembers.Add(n);
                    }
                };

                if (string.Equals(cfg.HopMode, HopAuto, StringComparison.OrdinalIgnoreCase))
                {
                    // 智能优选模式：将实测最快跳板与 Top 备选池置顶，并把订阅内有效物理节点全量纳入 PP-STATIC-HOP，
                    // 这样后台/手动随时优选出更快跳板时，只需 PUT /proxies/PP-STATIC-HOP 毫秒热切，无需重载内核不断连。
                    tryAddHop(preferredFollowNode);
                    tryAddHop(cfg.AutoBestHopNode);
                    if (cfg.AutoBestHopPool != null)
                        foreach (var pNode in cfg.AutoBestHopPool) tryAddHop(pNode);
                    foreach (var concrete in ExtractConcreteNodeNamesFromYaml(text, 200))
                        tryAddHop(concrete);
                }
                else if (string.Equals(cfg.HopMode, HopPin, StringComparison.OrdinalIgnoreCase) &&
                         !string.IsNullOrWhiteSpace(cfg.PinnedHopNode))
                {
                    tryAddHop(cfg.PinnedHopNode);
                }
                else if (string.Equals(cfg.HopMode, HopFollow, StringComparison.OrdinalIgnoreCase) &&
                         !string.IsNullOrWhiteSpace(preferredFollowNode))
                {
                    // 旁路沙箱无 cache.db：把主内核当前真实活跃物理节点置顶，防止默认命中订阅首行公告/失效节点
                    tryAddHop(preferredFollowNode);
                }
                if (!hopSeen.Contains(hopTarget))
                {
                    hopSeen.Add(hopTarget);
                    hopMembers.Add(hopTarget);
                }

                var groupEntry = BuildHopGroupBlock(hopMembers);
                text = InjectIntoTopListSection(text, "proxy-groups", groupEntry);
            }
            return text;
        }

        /// <summary>
        /// 推送新配置后主动对齐内核中 PP-STATIC-HOP 的当前选中项：
        /// 消除 mihomo 在 store-selected: true 下从 cache.db 恢复旧选择的记忆残留（如从 follow 改成 pin/auto 后仍粘在旧跳板）。
        /// </summary>
        public static void SyncHopSelectionInKernel(StaticProxyConfig cfg, string mainPolicy, string knownConfigPath)
        {
            if (cfg == null || !cfg.IsActive()) return;
            if (string.Equals(cfg.HopMode, HopDirect, StringComparison.OrdinalIgnoreCase)) return;
            string desired = (mainPolicy ?? "").Trim();
            if (string.Equals(cfg.HopMode, HopAuto, StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(cfg.AutoBestHopNode) && !IsManagedName(cfg.AutoBestHopNode))
                    desired = cfg.AutoBestHopNode.Trim();
                else if (cfg.AutoBestHopPool != null && cfg.AutoBestHopPool.Count > 0 && !string.IsNullOrWhiteSpace(cfg.AutoBestHopPool[0]))
                    desired = cfg.AutoBestHopPool[0].Trim();
            }
            else if (string.Equals(cfg.HopMode, HopPin, StringComparison.OrdinalIgnoreCase) &&
                     !string.IsNullOrWhiteSpace(cfg.PinnedHopNode) &&
                     !IsManagedName(cfg.PinnedHopNode))
            {
                desired = cfg.PinnedHopNode.Trim();
            }
            if (string.IsNullOrEmpty(desired)) return;
            string _;
            if (!SwitchHopNodeInKernel(desired, knownConfigPath, out _) &&
                !desired.Equals(mainPolicy ?? "", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrEmpty(mainPolicy))
            {
                SwitchHopNodeInKernel(mainPolicy.Trim(), knownConfigPath, out _);
            }
        }

        /// <summary>不重载配置、不断连：直接热切主内核中 PP-STATIC-HOP 的当前前置跳板节点。</summary>
        public static bool SwitchHopNodeInKernel(string nodeName, string knownConfigPath, out string error)
        {
            error = "";
            string target = (nodeName ?? "").Trim();
            if (target.Length == 0 || IsManagedName(target))
            {
                error = "无效的前置跳板节点名";
                return false;
            }
            try
            {
                string body = "{\"name\":" + Json.Quote(target) + "}";
                var res = Mihomo.Request("PUT", "/proxies/" + Uri.EscapeDataString(HopGroupName), body, 3500, knownConfigPath);
                if (res != null && res.Ok) return true;
                error = res != null ? (string.IsNullOrEmpty(res.Error) ? ("HTTP " + res.Status) : res.Error) : "内核无响应";
                return false;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        /// <summary>读取主内核中 PP-STATIC-HOP 当前真实选中的前置节点名。</summary>
        public static string ReadLiveHopNode(string knownConfigPath)
        {
            try
            {
                var res = Mihomo.FetchJson("/proxies/" + Uri.EscapeDataString(HopGroupName), 2500, knownConfigPath);
                if (res != null && res.Ok && res.Data != null)
                    return (Json.Str(res.Data, "now") ?? "").Trim();
            }
            catch { }
            return "";
        }

        /// <summary>从订阅 YAML 的顶层 proxies 段安全抽取物理节点名称列表（兼容块式与行内流式，过滤公告与托管节点）。</summary>
        internal static List<string> ExtractConcreteNodeNamesFromYaml(string yaml, int maxCount = 120)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(yaml) || maxCount <= 0) return result;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string[] lines = yaml.Replace("\r\n", "\n").Split('\n');
            bool inProxies = false;

            Action<string> tryAccept = rawName =>
            {
                if (result.Count >= maxCount) return;
                string n = UnquoteYamlScalar(rawName);
                if (n.Length == 0) return;
                if (IsManagedName(n) ||
                    n.Equals("DIRECT", StringComparison.OrdinalIgnoreCase) ||
                    n.Equals("REJECT", StringComparison.OrdinalIgnoreCase) ||
                    n.Equals("REJECT-DROP", StringComparison.OrdinalIgnoreCase) ||
                    n.Equals("GLOBAL", StringComparison.OrdinalIgnoreCase) ||
                    ProbeSidecar.IsInformationalNodeName(n))
                    return;
                if (seen.Add(n)) result.Add(n);
            };

            for (int i = 0; i < lines.Length && result.Count < maxCount; i++)
            {
                string raw = lines[i].TrimEnd();
                int indent = raw.Length - raw.TrimStart().Length;
                string t = raw.TrimStart();
                if (!inProxies)
                {
                    if (indent == 0 && (t == "proxies:" || (t.StartsWith("proxies:", StringComparison.Ordinal) &&
                        (t.Length == 8 || t[8] == ' ' || t[8] == '\t' || t[8] == '#'))))
                    {
                        inProxies = true;
                        string rest = t.Substring(8).Trim();
                        if (rest.StartsWith("[", StringComparison.Ordinal))
                        {
                            foreach (var flowObj in SplitTopLevelFlow(rest.Trim('[', ']')))
                                TryExtractNameFromInlineMap(flowObj, tryAccept);
                            break;
                        }
                    }
                    continue;
                }
                if (t.Length == 0 || t.StartsWith("#", StringComparison.Ordinal)) continue;
                if (indent == 0 && !t.StartsWith("-", StringComparison.Ordinal)) break;

                if (t.StartsWith("-", StringComparison.Ordinal))
                {
                    string afterDash = t.Substring(1).TrimStart();
                    if (afterDash.StartsWith("{", StringComparison.Ordinal))
                    {
                        TryExtractNameFromInlineMap(afterDash, tryAccept);
                        continue;
                    }
                    if (afterDash.StartsWith("name:", StringComparison.OrdinalIgnoreCase))
                    {
                        tryAccept(afterDash.Substring(5));
                        continue;
                    }
                }
                else if (t.StartsWith("name:", StringComparison.OrdinalIgnoreCase))
                {
                    tryAccept(t.Substring(5));
                }
            }
            return result;
        }

        static void TryExtractNameFromInlineMap(string mapText, Action<string> onName)
        {
            if (string.IsNullOrEmpty(mapText)) return;
            string s = mapText.Trim();
            if (s.StartsWith("{", StringComparison.Ordinal)) s = s.Substring(1);
            int close = s.LastIndexOf('}');
            if (close >= 0) s = s.Substring(0, close);
            foreach (var kv in SplitTopLevelFlow(s))
            {
                int col = kv.IndexOf(':');
                if (col <= 0) continue;
                string k = kv.Substring(0, col).Trim().Trim('"', '\'');
                if (k.Equals("name", StringComparison.OrdinalIgnoreCase))
                {
                    onName(kv.Substring(col + 1));
                    return;
                }
            }
        }

        static string UnquoteYamlScalar(string raw)
        {
            string s = (raw ?? "").Trim();
            if (s.Length == 0) return "";
            if (s.StartsWith("\"", StringComparison.Ordinal))
            {
                var sb = new StringBuilder(s.Length);
                for (int i = 1; i < s.Length; i++)
                {
                    char c = s[i];
                    if (c == '\\' && i + 1 < s.Length) { sb.Append(s[++i]); continue; }
                    if (c == '"') break;
                    sb.Append(c);
                }
                return sb.ToString().Trim();
            }
            if (s.StartsWith("'", StringComparison.Ordinal))
            {
                var sb = new StringBuilder(s.Length);
                for (int i = 1; i < s.Length; i++)
                {
                    char c = s[i];
                    if (c == '\'' && i + 1 < s.Length && s[i + 1] == '\'') { sb.Append('\''); i++; continue; }
                    if (c == '\'') break;
                    sb.Append(c);
                }
                return sb.ToString().Trim();
            }
            int hash = s.IndexOf(" #", StringComparison.Ordinal);
            if (hash >= 0) s = s.Substring(0, hash);
            return s.Trim();
        }

        /// <summary>为旁路端到端测速构造一批指向不同候选跳板的临时静态 SOCKS5 节点块。</summary>
        internal static List<string> BuildBenchmarkProxyBlocks(StaticProxyConfig cfg, IList<string> candidateHops)
        {
            var lines = new List<string>();
            if (cfg == null || candidateHops == null || candidateHops.Count == 0) return lines;
            for (int i = 0; i < candidateHops.Count; i++)
            {
                string hop = (candidateHops[i] ?? "").Trim();
                if (hop.Length == 0 || IsManagedName(hop)) continue;
                lines.Add("- name: " + Json.Quote(BenchNodePrefix + i));
                lines.Add("  type: socks5");
                lines.Add("  server: " + Json.Quote(cfg.Host.Trim()));
                lines.Add("  port: " + cfg.Port);
                if (!string.IsNullOrEmpty(cfg.Username))
                {
                    lines.Add("  username: " + Json.Quote(cfg.Username));
                    lines.Add("  password: " + Json.Quote(cfg.Password ?? ""));
                }
                lines.Add("  udp: false");
                if (cfg.ForceIpv4)
                    lines.Add("  ip-version: ipv4");
                lines.Add("  dialer-proxy: " + Json.Quote(hop));
            }
            return lines;
        }

        /// <summary>将一批临时测速节点注入旁路 YAML 的 proxies 段首（供 ProbeSidecar 调用）。</summary>
        internal static string InjectBenchmarkProxiesIntoYaml(string yaml, StaticProxyConfig cfg, IList<string> candidateHops)
        {
            var block = BuildBenchmarkProxyBlocks(cfg, candidateHops);
            if (block.Count == 0) return yaml;
            return InjectIntoTopListSection(yaml.Replace("\r\n", "\n"), "proxies", block);
        }

        static List<string> BuildProxyNodeBlock(StaticProxyConfig cfg, bool useDialerProxy)
        {
            var lines = new List<string>
            {
                BeginMarker,
                "- name: " + Json.Quote(NodeName),
                "  type: socks5",
                "  server: " + Json.Quote(cfg.Host.Trim()),
                "  port: " + cfg.Port
            };
            if (!string.IsNullOrEmpty(cfg.Username))
            {
                lines.Add("  username: " + Json.Quote(cfg.Username));
                lines.Add("  password: " + Json.Quote(cfg.Password ?? ""));
            }
            lines.Add("  udp: " + (cfg.AllowUdp ? "true" : "false"));
            if (cfg.ForceIpv4)
                lines.Add("  ip-version: ipv4");
            if (useDialerProxy)
                lines.Add("  dialer-proxy: " + Json.Quote(HopGroupName));
            lines.Add(EndMarker);
            return lines;
        }

        static List<string> BuildHopGroupBlock(List<string> members)
        {
            var lines = new List<string>
            {
                BeginMarker,
                "- name: " + Json.Quote(HopGroupName),
                "  type: select",
                "  proxies:"
            };
            foreach (var m in members)
                lines.Add("    - " + Json.Quote(m));
            lines.Add(EndMarker);
            return lines;
        }

        internal static string StripStaticMarkedBlocks(string text)
        {
            if (string.IsNullOrEmpty(text) || text.IndexOf(BeginMarker, StringComparison.Ordinal) < 0)
                return text;
            string[] lines = text.Split('\n');
            var kept = new List<string>(lines.Length);
            int i = 0;
            while (i < lines.Length)
            {
                if (lines[i].Trim().Equals(BeginMarker, StringComparison.Ordinal))
                {
                    int end = i + 1;
                    while (end < lines.Length && !lines[end].Trim().Equals(EndMarker, StringComparison.Ordinal))
                        end++;
                    if (end < lines.Length)
                    {
                        i = end + 1;
                        continue;
                    }
                }
                kept.Add(lines[i]);
                i++;
            }
            return string.Join("\n", kept);
        }

        /// <summary>向顶层列表段（proxies / proxy-groups）头部安全插入块式条目，兼容空数组、流式列表与缺段场景。</summary>
        static string InjectIntoTopListSection(string text, string sectionName, List<string> blockLines)
        {
            string[] lines = text.Split('\n');
            int secIdx = -1;
            string secPrefix = sectionName + ":";
            for (int i = 0; i < lines.Length; i++)
            {
                string raw = lines[i].TrimEnd();
                if (raw.Length == 0 || raw.Length - raw.TrimStart().Length > 0) continue;
                string t = raw.Trim();
                if (t == secPrefix || (t.StartsWith(secPrefix, StringComparison.Ordinal) &&
                    (t.Length == secPrefix.Length || t[secPrefix.Length] == ' ' || t[secPrefix.Length] == '\t' || t[secPrefix.Length] == '#')))
                {
                    secIdx = i;
                    break;
                }
            }

            if (secIdx < 0)
            {
                // 订阅无顶层 proxies:（例如纯 proxy-providers 订阅）→ 在 rules: 前或文件末尾补建
                int insertBefore = FindTopKeyLine(lines, "rules");
                if (insertBefore < 0) insertBefore = lines.Length;
                var outLines = new List<string>(lines.Length + blockLines.Count + 2);
                for (int i = 0; i < insertBefore; i++) outLines.Add(lines[i]);
                outLines.Add(secPrefix);
                foreach (var bl in blockLines) outLines.Add("  " + bl);
                for (int i = insertBefore; i < lines.Length; i++) outLines.Add(lines[i]);
                return string.Join("\n", outLines);
            }

            string head = lines[secIdx].Trim();
            string rest = head.Length > secPrefix.Length ? head.Substring(secPrefix.Length).Trim() : "";

            // 处理流式写法：proxies: [] 或 proxies: [{...}, {...}] 或 proxies: null
            var flowItems = new List<string>();
            bool replacedFlow = false;
            string headComment = "";
            if (rest.StartsWith("[", StringComparison.Ordinal))
            {
                int close = Discovery.FindFlowClose(rest);
                if (close < 0)
                    throw new InvalidOperationException(sectionName + ": 的流式列表未闭合，已停止注入以防配置损坏");
                string tail = rest.Substring(close + 1).Trim();
                if (tail.Length > 0 && !tail.StartsWith("#", StringComparison.Ordinal))
                    throw new InvalidOperationException(sectionName + ": 的流式列表后存在无法识别内容，已停止注入");
                if (tail.StartsWith("#", StringComparison.Ordinal)) headComment = " " + tail;
                string inner = rest.Substring(1, close - 1).Trim();
                if (inner.Length > 0)
                {
                    foreach (var part in SplitTopLevelFlow(inner))
                    {
                        string p = part.Trim();
                        if (p.Length > 0) flowItems.Add(p);
                    }
                }
                replacedFlow = true;
            }
            else if (rest.Equals("null", StringComparison.OrdinalIgnoreCase) || rest == "~")
            {
                replacedFlow = true;
            }

            // 检测该段现有列表项缩进（0 或 2 空格最常见）
            string indent = "  ";
            if (!replacedFlow)
            {
                for (int j = secIdx + 1; j < lines.Length; j++)
                {
                    string ln = lines[j];
                    string tr = ln.Trim();
                    if (tr.Length == 0 || tr.StartsWith("#", StringComparison.Ordinal)) continue;
                    int sp = ln.Length - ln.TrimStart().Length;
                    if (sp == 0 && !tr.StartsWith("-", StringComparison.Ordinal)) break;
                    if (tr.StartsWith("-", StringComparison.Ordinal))
                    {
                        indent = ln.Substring(0, sp);
                        break;
                    }
                }
            }

            var rebuilt = new List<string>(lines.Length + blockLines.Count + flowItems.Count + 2);
            for (int i = 0; i < secIdx; i++) rebuilt.Add(lines[i]);
            rebuilt.Add(replacedFlow ? (secPrefix + headComment) : lines[secIdx]);
            foreach (var bl in blockLines) rebuilt.Add(indent + bl);
            foreach (var fi in flowItems) rebuilt.Add(indent + "- " + fi);
            for (int i = secIdx + 1; i < lines.Length; i++) rebuilt.Add(lines[i]);
            return string.Join("\n", rebuilt);
        }

        static int FindTopKeyLine(string[] lines, string key)
        {
            string prefix = key + ":";
            for (int i = 0; i < lines.Length; i++)
            {
                string raw = lines[i].TrimEnd();
                if (raw.Length == 0 || raw.Length - raw.TrimStart().Length > 0) continue;
                string t = raw.Trim();
                if (t == prefix || (t.StartsWith(prefix, StringComparison.Ordinal) &&
                    (t.Length == prefix.Length || t[prefix.Length] == ' ' || t[prefix.Length] == '\t' || t[prefix.Length] == '#')))
                    return i;
            }
            return -1;
        }

        static List<string> SplitTopLevelFlow(string inner)
        {
            var res = new List<string>();
            int depth = 0;
            bool inS = false, inD = false;
            int start = 0;
            for (int i = 0; i < inner.Length; i++)
            {
                char c = inner[i];
                if (inD)
                {
                    if (c == '\\') { i++; continue; }
                    if (c == '"') inD = false;
                    continue;
                }
                if (inS)
                {
                    if (c == '\'' && i + 1 < inner.Length && inner[i + 1] == '\'') { i++; continue; }
                    if (c == '\'') inS = false;
                    continue;
                }
                if (c == '"') { inD = true; continue; }
                if (c == '\'') { inS = true; continue; }
                if (c == '{' || c == '[') depth++;
                else if ((c == '}' || c == ']') && depth > 0) depth--;
                else if (c == ',' && depth == 0)
                {
                    res.Add(inner.Substring(start, i - start));
                    start = i + 1;
                }
            }
            if (start <= inner.Length) res.Add(inner.Substring(start));
            return res;
        }

        static bool YamlContainsProxyOrGroupName(string yaml, string name)
        {
            if (string.IsNullOrEmpty(yaml) || string.IsNullOrEmpty(name)) return false;
            var groups = Discovery.GroupNames(yaml);
            foreach (var g in groups)
                if (g.Equals(name, StringComparison.OrdinalIgnoreCase)) return true;

            // 检查 proxies 段内是否存在同名节点（支持块式 name: X 与行内 {name: X, ...}）
            bool inProxies = false;
            foreach (var raw in yaml.Split('\n'))
            {
                string t = raw.TrimEnd();
                int indent = t.Length - t.TrimStart().Length;
                string s = t.TrimStart();
                if (!inProxies)
                {
                    if (indent == 0 && (s == "proxies:" || s.StartsWith("proxies:", StringComparison.Ordinal)))
                    {
                        inProxies = true;
                        if (s.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0) return true;
                    }
                    continue;
                }
                if (indent == 0 && s.Length > 0 && !s.StartsWith("-", StringComparison.Ordinal) && !s.StartsWith("#", StringComparison.Ordinal))
                    break;
                if (s.IndexOf("name:", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    s.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }

        // ============================================================
        // 4. 零干扰旁路实测（ProbeSidecar + NodeQuality 真链路体检）
        // ============================================================

        /// <summary>
        /// 在独立零干扰旁路 mihomo 实例中对用户刚输入的静态 SOCKS5 参数做端到端链式实测：
        /// 1) 绝不修改主网络规则或主节点；
        /// 2) 精准区分「前置跳板不通」vs「落地 SOCKS5 账密/端口失败」vs「全链通畅」；
        /// 3) 测通后直接复用 NodeQuality 获取真实出口 IP、ASN、家宽/风控分与四门服务可用性。
        /// </summary>
        /// <summary>将 mihomo 原始 JSON 错误串整理为干净的一行中文提示。</summary>
        internal static string CleanProbeError(string raw)
        {
            string s = (raw ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            if (s.Length == 0) return "超时或连接被拒";
            if (s.StartsWith("{", StringComparison.Ordinal))
            {
                try
                {
                    var obj = Json.AsObject(Json.Parse(s));
                    string msg = Json.Str(obj, "message");
                    if (!string.IsNullOrWhiteSpace(msg)) s = msg.Trim();
                }
                catch { }
            }
            if (s.Equals("Timeout", StringComparison.OrdinalIgnoreCase) ||
                s.IndexOf("timeout", StringComparison.OrdinalIgnoreCase) >= 0 ||
                s.IndexOf("timed out", StringComparison.OrdinalIgnoreCase) >= 0)
                return "连接超时";
            if (s.IndexOf("An error occurred in the delay test", StringComparison.OrdinalIgnoreCase) >= 0)
                return "握手失败或被目标拒接";
            return s.Length > 60 ? s.Substring(0, 60) + "…" : s;
        }

        /// <summary>
        /// 在零干扰旁路 mihomo 中并发实测订阅内所有物理候选节点经由固定 IP 的端到端真实双跳延迟，
        /// 并对入围前 5 名（含当前活跃跳板）执行 2 轮稳态复核（共 3 次采样计算均延迟+抖动稳态分），
        /// 挑出真正既快又稳的冠军跳板与 Top 12 备选池（绝不修改主网络节点或规则）。
        /// </summary>
        public static AutoHopBenchmarkResult BenchmarkBestHopsThroughSidecar(
            StaticProxyConfig candidate,
            string knownConfigPath = null,
            Action<string> progress = null,
            CancellationToken cancel = default(CancellationToken),
            string currentHopNode = null)
        {
            var res = new AutoHopBenchmarkResult();
            if (candidate == null)
            {
                res.Error = "未填写独立固定 IP 参数";
                return res;
            }
            string valErr;
            if (!candidate.IsValid(out valErr))
            {
                res.Error = valErr;
                return res;
            }
            if (string.Equals(candidate.HopMode, HopDirect, StringComparison.OrdinalIgnoreCase))
            {
                res.Error = "直连模式不需要前置跳板优选";
                return res;
            }

            var testCfg = candidate.Clone();
            testCfg.Enabled = true;

            var sw = Stopwatch.StartNew();
            List<string> candidateHops;
            string startErr;
            if (progress != null) progress("正在启动零干扰旁路引擎准备全量端到端优选…");
            using (var session = ProbeSidecar.TryStartForAutoHopBenchmark(knownConfigPath, testCfg, out candidateHops, out startErr))
            {
                if (session == null || candidateHops == null || candidateHops.Count == 0)
                {
                    res.Error = string.IsNullOrEmpty(startErr) ? "订阅中未发现可用候选跳板节点" : startErr;
                    return res;
                }

                int total = candidateHops.Count;
                res.TestedCount = total;
                var results = new HopCandidateResult[total];
                var idxByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < total; i++)
                    if (!idxByName.ContainsKey(candidateHops[i])) idxByName[candidateHops[i]] = i;

                int done = 0;
                using (var sem = new SemaphoreSlim(16))
                {
                    var tasks = new List<Task>(total);
                    for (int i = 0; i < total; i++)
                    {
                        int idx = i;
                        string hopName = candidateHops[idx];
                        tasks.Add(Task.Run(() =>
                        {
                            if (cancel.IsCancellationRequested)
                            {
                                results[idx] = new HopCandidateResult { NodeName = hopName, DelayMs = -1 };
                                return;
                            }
                            sem.Wait();
                            try
                            {
                                int ms = -1;
                                if (!cancel.IsCancellationRequested)
                                {
                                    string _;
                                    ms = session.MeasureDelay(BenchNodePrefix + idx, 4500, out _);
                                }
                                results[idx] = new HopCandidateResult
                                {
                                    NodeName = hopName,
                                    DelayMs = ms,
                                    SuccessSamples = ms > 0 ? 1 : 0,
                                    StabilityScore = ms > 0 ? ComputeStabilityScore(ms, 0) : int.MaxValue
                                };
                            }
                            catch
                            {
                                results[idx] = new HopCandidateResult { NodeName = hopName, DelayMs = -1 };
                            }
                            finally
                            {
                                sem.Release();
                                int curDone = Interlocked.Increment(ref done);
                                if (progress != null && (curDone == total || curDone % 4 == 0))
                                    progress("正在端到端初筛候选跳板（" + curDone + "/" + total + "）…");
                            }
                        }));
                    }
                    Task.WaitAll(tasks.ToArray());
                }

                if (cancel.IsCancellationRequested)
                {
                    sw.Stop();
                    res.ElapsedMs = sw.ElapsedMilliseconds;
                    res.Error = "已取消跳板优选";
                    return res;
                }

                var okList = new List<HopCandidateResult>();
                for (int i = 0; i < results.Length; i++)
                    if (results[i] != null && results[i].DelayMs > 0) okList.Add(results[i]);

                okList.Sort((a, b) =>
                {
                    int cmp = a.DelayMs.CompareTo(b.DelayMs);
                    return cmp != 0 ? cmp : string.Compare(a.NodeName, b.NodeName, StringComparison.OrdinalIgnoreCase);
                });

                res.ReachableCount = okList.Count;
                if (okList.Count == 0)
                {
                    sw.Stop();
                    res.ElapsedMs = sw.ElapsedMilliseconds;
                    res.Error = "全量 " + total + " 个候选跳板均无法连通该固定 IP，请检查 SOCKS5 参数或机场节点状态";
                    return res;
                }

                // 第二阶段：挑选前 5 名 + 当前活跃跳板（若可达），再做 2 轮连续端到端稳态复核（共 3 次采样），
                // 计算均延迟与抖动稳态分，杜绝选到单次偶快但高抖动/掉线的不稳定跳板。
                var verifyTargets = new List<HopCandidateResult>();
                for (int i = 0; i < okList.Count && i < 5; i++) verifyTargets.Add(okList[i]);
                if (!string.IsNullOrWhiteSpace(currentHopNode))
                {
                    string curTrim = currentHopNode.Trim();
                    bool alreadyIn = false;
                    foreach (var vt in verifyTargets)
                        if (string.Equals(vt.NodeName, curTrim, StringComparison.OrdinalIgnoreCase)) { alreadyIn = true; break; }
                    if (!alreadyIn)
                    {
                        foreach (var ok in okList)
                        {
                            if (string.Equals(ok.NodeName, curTrim, StringComparison.OrdinalIgnoreCase))
                            {
                                verifyTargets.Add(ok);
                                break;
                            }
                        }
                    }
                }

                if (verifyTargets.Count > 0 && !cancel.IsCancellationRequested)
                {
                    if (progress != null)
                        progress("正在对前 " + verifyTargets.Count + " 个候选跳板做多轮稳态复核…");
                    var verifyTasks = new List<Task>(verifyTargets.Count);
                    foreach (var item in verifyTargets)
                    {
                        var target = item;
                        int benchIdx;
                        if (!idxByName.TryGetValue(target.NodeName, out benchIdx)) continue;
                        verifyTasks.Add(Task.Run(() =>
                        {
                            var samples = new List<int>(3) { target.DelayMs };
                            for (int round = 0; round < 2; round++)
                            {
                                if (cancel.IsCancellationRequested) break;
                                string _;
                                int ms = session.MeasureDelay(BenchNodePrefix + benchIdx, 4500, out _);
                                if (ms > 0) samples.Add(ms);
                            }
                            if (samples.Count > 0)
                            {
                                int sum = 0, min = int.MaxValue, max = 0;
                                foreach (int s in samples)
                                {
                                    sum += s;
                                    if (s < min) min = s;
                                    if (s > max) max = s;
                                }
                                int avg = (int)Math.Round(sum / (double)samples.Count);
                                int jitter = samples.Count >= 2 ? (max - min) : 0;
                                target.DelayMs = avg;
                                target.JitterMs = jitter;
                                target.SuccessSamples = samples.Count;
                                target.StabilityScore = ComputeStabilityScore(avg, jitter);
                            }
                        }));
                    }
                    Task.WaitAll(verifyTasks.ToArray());
                    res.VerifiedCount = verifyTargets.Count;
                }

                sw.Stop();
                res.ElapsedMs = sw.ElapsedMilliseconds;
                if (cancel.IsCancellationRequested)
                {
                    res.Error = "已取消跳板优选";
                    return res;
                }

                // 排好入围复核组，再把剩余未入围的可达候选按初筛延迟附在后面形成完整 Top 12 备选池
                RankVerifiedHopCandidates(verifyTargets);
                var verifiedSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var vt in verifyTargets) verifiedSet.Add(vt.NodeName);

                var finalList = new List<HopCandidateResult>(okList.Count);
                foreach (var vt in verifyTargets) finalList.Add(vt);
                foreach (var ok in okList)
                    if (!verifiedSet.Contains(ok.NodeName)) finalList.Add(ok);

                for (int i = 0; i < finalList.Count && i < 12; i++)
                    res.TopCandidates.Add(finalList[i]);
                res.BestNode = finalList[0].NodeName;
                res.BestDelayMs = finalList[0].DelayMs;
                res.BestJitterMs = finalList[0].JitterMs;
                res.Ok = true;
                return res;
            }
        }

        public static StaticProbeResult ProbeThroughSidecar(
            StaticProxyConfig candidate,
            string knownConfigPath = null,
            Action<string> progress = null)
        {
            var res = new StaticProbeResult();
            if (candidate == null)
            {
                res.Stage = "validate";
                res.Error = "未填写独立固定 IP 参数";
                return res;
            }
            string valErr;
            if (!candidate.IsValid(out valErr))
            {
                res.Stage = "validate";
                res.Error = valErr;
                return res;
            }

            // 测试副本强制 Enabled=true，不影响持久化开关状态
            var testCfg = candidate.Clone();
            testCfg.Enabled = true;

            // 智能优选模式：先并发端到端测出当前最快前置跳板，再用该冠军跳板跑完整出口与风控体检
            if (string.Equals(testCfg.HopMode, HopAuto, StringComparison.OrdinalIgnoreCase))
            {
                var bench = BenchmarkBestHopsThroughSidecar(testCfg, knownConfigPath, progress);
                res.AutoBench = bench;
                if (!bench.Ok || string.IsNullOrEmpty(bench.BestNode))
                {
                    res.Stage = "hop";
                    res.Error = string.IsNullOrEmpty(bench.Error) ? "未能自动优选出可用前置跳板" : bench.Error;
                    return res;
                }
                testCfg.AutoBestHopNode = bench.BestNode;
                testCfg.AutoBestDelayMs = bench.BestDelayMs;
                testCfg.AutoBestUpdatedAtTicks = DateTime.UtcNow.Ticks;
                testCfg.AutoBestHopPool = new List<string>();
                foreach (var tc in bench.TopCandidates)
                    if (!string.IsNullOrEmpty(tc.NodeName)) testCfg.AutoBestHopPool.Add(tc.NodeName);
                res.SelectedHopNode = bench.BestNode;
                if (progress != null)
                    progress("已优选最快跳板「" + bench.BestNode + "」（" + bench.BestDelayMs + "ms），正在核验出口纯净度…");
            }

            string mainPolicy;
            string startErr;
            if (progress != null) progress("正在启动零干扰旁路检测引擎…");
            using (var session = ProbeSidecar.TryStartForStaticTest(knownConfigPath, testCfg, out mainPolicy, out startErr))
            {
                res.MainPolicy = mainPolicy ?? "";
                if (session == null)
                {
                    res.Stage = "sidecar";
                    res.Error = string.IsNullOrEmpty(startErr) ? "无法启动旁路检测引擎" : startErr;
                    return res;
                }

                bool directHop = string.Equals(testCfg.HopMode, HopDirect, StringComparison.OrdinalIgnoreCase);
                if (!directHop && string.IsNullOrEmpty(res.SelectedHopNode))
                {
                    if (string.Equals(testCfg.HopMode, HopPin, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(testCfg.PinnedHopNode))
                        res.SelectedHopNode = testCfg.PinnedHopNode.Trim();
                }
                int hopMs = -1;
                if (!directHop)
                {
                    // 步骤 1：先测前置跳板（PP-STATIC-HOP）是否通畅，避免把前置故障误怪到静态 IP 身上
                    if (progress != null) progress("正在验证第一跳前置加速通道…");
                    string hopErr;
                    hopMs = session.MeasureDelay(HopGroupName, 5000, out hopErr);
                    res.HopDelayMs = hopMs;
                    if (hopMs <= 0)
                    {
                        res.Stage = "hop";
                        res.Error = "前置跳板节点暂不可达（" + CleanProbeError(hopErr) +
                                    "）。请先确认当前代理节点正常可用，或在高级设置中切换跳板/直连模式。";
                        return res;
                    }
                    if (progress != null) progress("前置通道正常（" + hopMs + "ms），正在握手验证独立固定 IP…");
                }
                else
                {
                    if (progress != null) progress("直连模式，正在直接握手验证独立固定 IP…");
                }

                // 步骤 2：测试落地静态 SOCKS5 节点（PP-STATIC-OUT）
                string staticErr;
                int staticMs = session.MeasureDelay(NodeName, 7000, out staticErr);
                res.StaticDelayMs = staticMs;
                if (staticMs <= 0)
                {
                    res.Stage = "static";
                    res.Error = (directHop ? "直连独立固定 IP 握手失败（" : ("前置通道正常（" + hopMs + "ms），但独立固定 IP 握手失败（")) +
                                CleanProbeError(staticErr) +
                                "）。请检查 SOCKS5 地址、端口、用户名、密码及套餐有效期。";
                    return res;
                }

                // 步骤 3：切旁路 GLOBAL -> PP-STATIC-OUT，跑真实出口 IP / 家宽 / 风控 / 四门服务体检
                if (!session.SwitchGlobalNode(NodeName))
                {
                    res.Stage = "static";
                    res.Error = "旁路实例切换到独立固定 IP 失败";
                    return res;
                }

                if (progress != null) progress("链式握手成功（" + staticMs + "ms），正在核验真实出口 IP 与纯净度…");
                var report = NodeQuality.RunQualityProbe(
                    NodeName,
                    "any",
                    false,
                    knownConfigPath,
                    session);
                res.Quality = report;

                if (report != null && report.RoutedDirect)
                {
                    res.Stage = "quality";
                    res.Error = "检测到出口疑似回落直连，未能经由目标静态 IP 出站，请检查前置节点状态";
                    return res;
                }

                res.Ok = true;
                res.Stage = "done";
                res.Summary = BuildHumanSummary(res);
                return res;
            }
        }

        internal static string BuildHumanSummary(StaticProbeResult res)
        {
            if (res == null) return "";
            if (!res.Ok) return string.IsNullOrEmpty(res.Error) ? "检测未通过" : res.Error;
            var parts = new List<string>();
            var q = res.Quality;
            if (q != null && !string.IsNullOrEmpty(q.Ip))
            {
                string loc = string.IsNullOrEmpty(q.CountryCode) ? "" : q.CountryCode;
                if (!string.IsNullOrEmpty(q.City))
                    loc = string.IsNullOrEmpty(loc) ? q.City : (loc + " " + q.City);
                if (!string.IsNullOrEmpty(loc)) parts.Add(loc);

                if (!string.IsNullOrEmpty(q.IpKindLabel) && q.IpKindLabel != "未知")
                    parts.Add(q.IpKindLabel);

                if (q.RiskScore >= 0)
                    parts.Add("风险" + q.RiskScore + "(" + (q.Level == NodeQuality.RiskLevel.Low ? "低" :
                              q.Level == NodeQuality.RiskLevel.Medium ? "中" : "高") + ")");
            }
            int ms = (q != null && q.Net != null && q.Net.AvgMs > 0) ? q.Net.AvgMs : res.StaticDelayMs;
            if (ms > 0) parts.Add(ms + "ms");
            if (q != null && (q.GoogleSearch.Tested || q.GeminiApi.Tested || q.OpenAiApi.Tested || q.ClaudeApi.Tested))
                parts.Add("服务 " + q.MainstreamReadyCount + "/4");
            return parts.Count > 0 ? string.Join(" · ", parts) : ("可用 · " + res.StaticDelayMs + "ms");
        }

        // ============================================================
        // 5. 自检套件（纯离线 + 零真机污染）
        // ============================================================

        public static string SelfTest()
        {
            var sb = new StringBuilder();
            int pass = 0, fail = 0;
            Action<string, bool, string> check = (name, ok, detail) =>
            {
                if (ok) { pass++; sb.AppendLine("PASS  " + name); }
                else { fail++; sb.AppendLine("FAIL  " + name + " | " + detail); }
            };

            // SP1–SP8：智能一行粘贴解析与畸形拦截
            ParsedEndpoint ep; string err;
            check("SP1 解析 host:port:user:pass（密码含额外冒号）",
                ParseQuickInput("23.45.67.89:10808:myuser:p@ss:with:colon#1", out ep, out err) &&
                ep.Host == "23.45.67.89" && ep.Port == 10808 && ep.Username == "myuser" && ep.Password == "p@ss:with:colon#1",
                "err=" + err + " pass=" + (ep != null ? ep.Password : ""));

            check("SP2 解析 socks5://user:pass@host:port（含 URL 编码与尾部斜杠）",
                ParseQuickInput("socks5://u%3A1:p%40ss%232@us-isp.example.com:9050/", out ep, out err) &&
                ep.Host == "us-isp.example.com" && ep.Port == 9050 && ep.Username == "u:1" && ep.Password == "p@ss#2",
                "err=" + err + " u=" + ep.Username + " p=" + ep.Password);

            check("SP3 解析 user:pass@host:port（密码含 @，按最后 @ 切分）",
                ParseQuickInput("alice:p@ss@word@198.51.100.12:12345", out ep, out err) &&
                ep.Host == "198.51.100.12" && ep.Port == 12345 && ep.Username == "alice" && ep.Password == "p@ss@word",
                "err=" + err + " h=" + ep.Host + " p=" + ep.Password);

            check("SP4 解析通用多行中英文标签文本",
                ParseQuickInput("服务器地址：198.51.100.88\n端口: 5500\n用户名: isp_u1\n密码: S3cr3t!#\"'", out ep, out err) &&
                ep.Host == "198.51.100.88" && ep.Port == 5500 && ep.Username == "isp_u1" && ep.Password == "S3cr3t!#\"'",
                "err=" + err + " h=" + ep.Host);

            check("SP5 解析 [ipv6]:port:user:pass",
                ParseQuickInput("[2001:db8::9]:1080:u6:p6", out ep, out err) &&
                ep.Host == "2001:db8::9" && ep.Port == 1080 && ep.Username == "u6" && ep.Password == "p6",
                "err=" + err);

            check("SP6 拦截非 SOCKS5 协议（http://）与越界端口",
                !ParseQuickInput("http://u:p@1.2.3.4:8080", out ep, out err) &&
                !ParseQuickInput("1.2.3.4:70000:u:p", out ep, out err) &&
                !ValidateFields("1.2.3.4\nmalicious: true", 1080, "u", "p", out err),
                "应全部拦截");

            check("SP7 拦截密码含换行/控制字符防 YAML 注入",
                !ValidateFields("1.2.3.4", 1080, "u", "pass\n  type: direct", out err) &&
                !ValidateFields("1.2.3.4", 1080, "u\0x", "pass", out err),
                "应拦截控制字符");

            // SP8：DPAPI 加密往返 + 临时文件隔离 + 损坏降级
            string tmpDat = Path.Combine(Path.GetTempPath(), "pp_sp_test_" + Guid.NewGuid().ToString("N") + ".dat");
            try
            {
                var origCfg = new StaticProxyConfig
                {
                    Enabled = true,
                    Label = "测试独享节点",
                    Host = "203.0.113.55",
                    Port = 18888,
                    Username = "user_dpapi",
                    Password = "P@ss\"#\\:99",
                    HopMode = HopPin,
                    PinnedHopNode = "Node-US-01",
                    AllowUdp = false,
                    ForceIpv4 = true
                };
                string saveErr;
                bool saved = SaveToDisk(tmpDat, origCfg, out saveErr);
                byte[] rawCipher = saved ? File.ReadAllBytes(tmpDat) : new byte[0];
                string cipherAscii = Encoding.UTF8.GetString(rawCipher);
                string warn;
                var loaded = LoadFromDisk(tmpDat, out warn);
                check("SP8 DPAPI 加密落盘无明文泄漏且完整往返",
                    saved &&
                    cipherAscii.IndexOf("203.0.113.55", StringComparison.Ordinal) < 0 &&
                    cipherAscii.IndexOf("user_dpapi", StringComparison.Ordinal) < 0 &&
                    cipherAscii.IndexOf("P@ss", StringComparison.Ordinal) < 0 &&
                    string.IsNullOrEmpty(warn) &&
                    loaded.Enabled && loaded.Host == origCfg.Host && loaded.Port == origCfg.Port &&
                    loaded.Username == origCfg.Username && loaded.Password == origCfg.Password &&
                    loaded.HopMode == HopPin && loaded.PinnedHopNode == "Node-US-01",
                    "saveErr=" + saveErr + " warn=" + warn);

                // 损坏文件优雅降级
                File.WriteAllBytes(tmpDat, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
                string badWarn;
                var fallback = LoadFromDisk(tmpDat, out badWarn);
                check("SP9 凭据损坏/跨机不可解密时优雅降级关闭并给出提示",
                    !fallback.Enabled && !string.IsNullOrEmpty(badWarn),
                    "enabled=" + fallback.Enabled + " warn=" + badWarn);
            }
            finally
            {
                try { if (File.Exists(tmpDat)) File.Delete(tmpDat); } catch { }
            }

            // SP10–SP16：纯内存 YAML 注入（6 种基线 + 幂等 + 防环 + 特殊密码转义 + 停用零差异）
            var activeCfg = new StaticProxyConfig
            {
                Enabled = true,
                Host = "198.51.100.77",
                Port = 10808,
                Username = "u#\"\\",
                Password = "p: 'secret' #comment",
                HopMode = HopFollow,
                AllowUdp = false,
                ForceIpv4 = true
            };
            var inactiveCfg = activeCfg.Clone();
            inactiveCfg.Enabled = false;

            string baseBlockYaml =
                "mode: rule\n" +
                "proxies:\n" +
                "  - name: Node-US-01\n" +
                "    type: vless\n" +
                "proxy-groups:\n" +
                "  - name: PP-FIXED\n" +
                "    type: select\n" +
                "    proxies:\n" +
                "      - Node-US-01\n" +
                "rules:\n" +
                "  - MATCH,DIRECT\n";

            check("SP10 未启用时 InjectIntoYaml 原样逐字节返回（零侵入）",
                string.Equals(InjectIntoYaml(baseBlockYaml, inactiveCfg, "PP-FIXED"), baseBlockYaml, StringComparison.Ordinal),
                "未启用时改动了原配置");

            string inj1 = InjectIntoYaml(baseBlockYaml, activeCfg, "PP-FIXED");
            string inj2 = InjectIntoYaml(inj1, activeCfg, "PP-FIXED");
            bool hasOut = inj1.Contains("- name: \"PP-STATIC-OUT\"");
            bool hasHop = inj1.Contains("- name: \"PP-STATIC-HOP\"");
            bool hasDialer = inj1.Contains("dialer-proxy: \"PP-STATIC-HOP\"");
            bool hasIpv4 = inj1.Contains("ip-version: ipv4");
            bool hasEscapedPass = inj1.Contains("password: \"p: 'secret' #comment\"");
            bool hasEscapedUser = inj1.Contains("username: \"u#\\\"\\\\\"");
            // 校验 PP-STATIC-OUT 绝未进入 PP-FIXED 组（DAG 无环）
            int fixedIdx = inj1.IndexOf("- name: PP-FIXED", StringComparison.Ordinal);
            string afterFixed = fixedIdx >= 0 ? inj1.Substring(fixedIdx) : "";
            int rulesIdx = afterFixed.IndexOf("rules:", StringComparison.Ordinal);
            string fixedSection = rulesIdx >= 0 ? afterFixed.Substring(0, rulesIdx) : afterFixed;
            check("SP11 标准块式注入含安全转义/ip-version/dialer-proxy、严格无环且二次注入幂等",
                hasOut && hasHop && hasDialer && hasIpv4 && hasEscapedPass && hasEscapedUser &&
                !fixedSection.Contains(NodeName) &&
                string.Equals(inj1, inj2, StringComparison.Ordinal),
                "out=" + hasOut + " hop=" + hasHop + " idem=" + string.Equals(inj1, inj2, StringComparison.Ordinal));

            // 流式空 proxies: []
            string flowEmptyYaml =
                "mode: rule\nproxies: []\nproxy-groups:\n  - name: PROXY\n    type: select\n    proxies: [DIRECT]\nrules:\n  - MATCH,PROXY\n";
            string injFlowEmpty = InjectIntoYaml(flowEmptyYaml, activeCfg, "PROXY");
            check("SP12 流式 proxies: [] 自动展开为合法块式并注入",
                injFlowEmpty.Contains("proxies:\n  # PP-STATIC-BEGIN") &&
                !injFlowEmpty.Contains("proxies: []") &&
                injFlowEmpty.Contains("- name: \"PP-STATIC-OUT\""),
                injFlowEmpty);

            // 纯 proxy-providers（完全没有 proxies: 顶层段）
            string providerOnlyYaml =
                "mode: rule\nproxy-providers:\n  airport:\n    type: file\n    path: ./a.yaml\n" +
                "proxy-groups:\n  - name: 🚀 节点选择\n    type: select\n    use:\n      - airport\n" +
                "rules:\n  - MATCH,🚀 节点选择\n";
            string injProv = InjectIntoYaml(providerOnlyYaml, activeCfg, "🚀 节点选择");
            check("SP13 纯 proxy-providers 缺 proxies 段时自动补建顶层段并挂接主组",
                injProv.Contains("\nproxies:\n  # PP-STATIC-BEGIN") &&
                injProv.Contains("- \"🚀 节点选择\"") &&
                injProv.IndexOf("\nproxies:", StringComparison.Ordinal) < injProv.IndexOf("\nrules:", StringComparison.Ordinal),
                injProv);

            // Pin 模式 + 环路拦截
            var pinCfg = activeCfg.Clone();
            pinCfg.HopMode = HopPin;
            pinCfg.PinnedHopNode = "Node-US-01";
            string injPin = InjectIntoYaml(baseBlockYaml, pinCfg, "PP-FIXED");
            var pinMissing = activeCfg.Clone();
            pinMissing.HopMode = HopPin;
            pinMissing.PinnedHopNode = "不存在的已删节点";
            string injPinFallback = InjectIntoYaml(baseBlockYaml, pinMissing, "PP-FIXED");
            check("SP14 Pin 跳板模式命中时优先锁定该节点，节点失效时安全回退主组",
                injPin.Contains("- \"Node-US-01\"") &&
                !injPinFallback.Contains("不存在的已删节点") &&
                injPinFallback.Contains("- \"PP-FIXED\""),
                "pin check failed");

            bool loopBlocked = false;
            try { InjectIntoYaml(baseBlockYaml, activeCfg, NodeName); }
            catch (InvalidOperationException) { loopBlocked = true; }
            bool directBlocked = false;
            try { InjectIntoYaml(baseBlockYaml, activeCfg, "DIRECT"); }
            catch (InvalidOperationException) { directBlocked = true; }
            check("SP15 严格拦截自环主组与 DIRECT 主组防止环路或直连泄漏",
                loopBlocked && directBlocked,
                "loop=" + loopBlocked + " direct=" + directBlocked);

            // FindPolicy 排除托管组
            bool _;
            string foundPol = Discovery.FindPolicy(inj1, out _);
            check("SP16 Discovery.FindPolicy 永不把 PP-STATIC-HOP 误识别为主策略组",
                foundPol == "PP-FIXED",
                "got=" + foundPol);

            // 停用防孤儿规则清理
            var mixedItems = new List<Profile.Item>
            {
                new Profile.Item("cursor.exe", "PP-FIXED"),
                new Profile.Item("chrome.exe", NodeName),
                new Profile.Item(@"(?i)^C:\\Tools\\App\\", NodeName, true) { Derived = true }
            };
            var dropped = SanitizeItemsForInactiveStatic(mixedItems, "DIRECT");
            var downgraded = SanitizeItemsForInactiveStatic(mixedItems, "PP-FIXED");
            check("SP17 停用防孤儿清理：改为直连时移除固定IP规则，切回高速时全部改写为 PP-FIXED",
                dropped.Count == 1 && dropped[0].Exe == "cursor.exe" &&
                downgraded.Count == 3 && downgraded[1].Policy == "PP-FIXED" && downgraded[2].Policy == "PP-FIXED",
                "dropped=" + dropped.Count + " downgraded=" + downgraded.Count);

            // SP18：Redactor 动态脱敏验证（恢复原有注册，零污染）
            var savedBefore = Redactor.SnapshotRuntimeSecrets();
            try
            {
                Redactor.RegisterRuntimeSecrets("static-isp.example.com", "isp_user_99", "SuperSecret!9988");
                string rawDiag = "static host=static-isp.example.com user=isp_user_99 pass=SuperSecret!9988\nstaticHop=MySecretAirportNode(1x)\n固定 IP 稳速优选切换前置跳板：MySecretAirportNode(1x)（192ms）";
                string masked = Redactor.Apply(rawDiag);
                check("SP18 Redactor 动态脱敏彻底隐藏静态主机/用户名/密码及 staticHop 与优选日志节点名",
                    !masked.Contains("static-isp.example.com") &&
                    !masked.Contains("isp_user_99") &&
                    !masked.Contains("SuperSecret!9988") &&
                    !masked.Contains("MySecretAirportNode") &&
                    masked.Contains("staticHop=<节点名已隐藏>"),
                    "masked=" + masked);
            }
            finally
            {
                Redactor.RestoreRuntimeSecrets(savedBefore);
            }

            // SP19：Profile.Compose 目录整树策略继承（主软件走 PP-STATIC-OUT，目录规则也走 PP-STATIC-OUT）
            var compPolicies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["chrome.exe"] = NodeName
            };
            var rootPols = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [@"C:\Apps\IsolatedChrome"] = NodeName
            };
            var compItems = Profile.Compose(
                new[] { "chrome.exe" },
                null,
                new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    ["chrome.exe"] = new List<string> { "chrome_helper.exe" }
                },
                new[] { @"C:\Apps\IsolatedChrome" },
                compPolicies,
                rootPols);
            bool allStatic = compItems.Count == 3 &&
                             compItems[0].Policy == NodeName &&
                             compItems[1].Policy == NodeName &&
                             compItems[2].IsPathRule && compItems[2].Policy == NodeName;
            check("SP19 Profile.Compose 主进程、同目录 helper 与目录整树规则统一继承 PP-STATIC-OUT",
                allStatic,
                "count=" + compItems.Count);

            // SP20：Guard 三态审计（锁定走 PP-STATIC-OUT 但内核被改成 PP-FIXED 必须报 WrongPolicy）
            var kernelRules = new List<Profile.RuleInfo>
            {
                new Profile.RuleInfo { Type = "ProcessName", Payload = "chrome.exe", Proxy = "PP-FIXED" },
                new Profile.RuleInfo { Type = "Match", Payload = "", Proxy = "DIRECT" }
            };
            var gAudit = Guard.AuditRules(kernelRules, new[] { "chrome.exe" }, compPolicies);
            check("SP20 Guard 三态审计检出固定 IP 规则被降级为普通代理组",
                gAudit.Ok && gAudit.WrongPolicy == 1 && gAudit.WrongPolicyNames.Contains("chrome.exe"),
                "wrong=" + gAudit.WrongPolicy);

            // SP21：Traffic 三态对账（锁定走 PP-STATIC-OUT 但连接走了普通机场节点必须报固定 IP 偏离）
            var mockConns = new Dictionary<string, object>
            {
                ["connections"] = new List<object>
                {
                    new Dictionary<string, object>
                    {
                        ["id"] = "c-drift-1",
                        ["upload"] = 100.0,
                        ["download"] = 200.0,
                        ["start"] = DateTime.UtcNow.ToString("O"),
                        ["chains"] = new List<object> { "Node-JP-01", "PROXY", "PP-FIXED" },
                        ["metadata"] = new Dictionary<string, object> { ["process"] = "chrome.exe" }
                    },
                    new Dictionary<string, object>
                    {
                        ["id"] = "c-ok-2",
                        ["upload"] = 100.0,
                        ["download"] = 200.0,
                        ["start"] = DateTime.UtcNow.ToString("O"),
                        ["chains"] = new List<object> { "Node-US-01", HopGroupName, NodeName },
                        ["metadata"] = new Dictionary<string, object> { ["process"] = "antigravity.exe" }
                    }
                }
            };
            var sample = Traffic.AnalyzeConnections(
                mockConns,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "chrome.exe", "antigravity.exe" },
                true,
                DateTime.MinValue,
                null,
                DateTime.UtcNow.Ticks,
                false,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "chrome.exe", "antigravity.exe" },
                null);
            check("SP21 Traffic 三态防漂移对账：固定IP软件走普通机场即报偏离，走 PP-STATIC-OUT 判正常",
                sample.Mismatch == 1 &&
                sample.Mismatches.Count == 1 &&
                sample.Mismatches[0].Exe == "chrome.exe" &&
                sample.Mismatches[0].StaticDrift,
                "mismatch=" + sample.Mismatch + (sample.Mismatches.Count > 0 ? (" exe=" + sample.Mismatches[0].Exe + " drift=" + sample.Mismatches[0].StaticDrift) : ""));

            // SP22：NodeManager.BuildCatalogFromProxies 屏蔽 PP-STATIC-OUT / PP-STATIC-HOP，防误入备选池
            var mockProxies = new Dictionary<string, object>
            {
                ["PP-FIXED"] = new Dictionary<string, object>
                {
                    ["type"] = "Selector",
                    ["now"] = "Node-US-01",
                    ["all"] = new List<object> { "Node-US-01", NodeName, HopGroupName }
                },
                ["Node-US-01"] = new Dictionary<string, object> { ["type"] = "VLESS", ["alive"] = true },
                [NodeName] = new Dictionary<string, object> { ["type"] = "Socks5", ["alive"] = true },
                [HopGroupName] = new Dictionary<string, object> { ["type"] = "Selector", ["now"] = "PP-FIXED" }
            };
            var cat = NodeManager.BuildCatalogFromProxies(mockProxies, "PP-FIXED", "rule", new[] { NodeName });
            check("SP22 NodeManager 节点目录自动排除 PP-STATIC-OUT 与 PP-STATIC-HOP",
                cat.Ok && cat.Nodes.Count == 1 && cat.Nodes[0].Name == "Node-US-01",
                "count=" + cat.Nodes.Count);

            // SP23：ProbeSidecar 沙箱配置生成支持叠加静态链式节点且不含 tun/rules
            string scYaml = ProbeSidecar.BuildSidecarConfigText(
                baseBlockYaml.Split('\n'), 17890, 19090, "testsec", "", activeCfg, "PP-FIXED");
            check("SP23 ProbeSidecar 旁路配置正确叠加 PP-STATIC-OUT 与 PP-STATIC-HOP 且剥离 tun/rules",
                scYaml.Contains("- name: \"PP-STATIC-OUT\"") &&
                scYaml.Contains("- name: \"PP-STATIC-HOP\"") &&
                scYaml.Contains("dialer-proxy: \"PP-STATIC-HOP\"") &&
                scYaml.Contains("tun:\n  enable: false") &&
                !scYaml.Contains("\nrules:"),
                "sidecar overlay invalid");

            // SP24：BuildPayload 在静态启用与停用切换下的安全行为（使用临时基线 + TestConfigOverride，零污染）
            string tmpBase = Path.Combine(Path.GetTempPath(), "pp_sp_base_" + Guid.NewGuid().ToString("N") + ".yaml");
            var prevOverride = TestConfigOverride;
            try
            {
                File.WriteAllText(tmpBase, baseBlockYaml, new UTF8Encoding(false));
                TestConfigOverride = activeCfg.Clone();
                string pActive = Profile.BuildPayload(tmpBase, new List<Profile.Item>
                {
                    new Profile.Item("chrome.exe", NodeName),
                    new Profile.Item("cursor.exe", "PP-FIXED")
                });
                TestConfigOverride = inactiveCfg.Clone();
                string pInactive = Profile.BuildPayload(tmpBase, new List<Profile.Item>
                {
                    new Profile.Item("chrome.exe", NodeName),
                    new Profile.Item("cursor.exe", "PP-FIXED")
                });
                check("SP24 BuildPayload 启用时注入链式节点并写 PP-STATIC-OUT，停用时零节点残留且不留孤儿规则",
                    pActive.Contains("- name: \"PP-STATIC-OUT\"") &&
                    pActive.Contains("PROCESS-NAME,chrome.exe,PP-STATIC-OUT") &&
                    pActive.Contains("PROCESS-NAME,cursor.exe,PP-FIXED") &&
                    !pInactive.Contains("PP-STATIC-OUT") &&
                    !pInactive.Contains("PP-STATIC-HOP") &&
                    pInactive.Contains("PROCESS-NAME,cursor.exe,PP-FIXED"),
                    "payload mismatch");
            }
            finally
            {
                TestConfigOverride = prevOverride;
                try { if (File.Exists(tmpBase)) File.Delete(tmpBase); } catch { }
            }

            // SP25：StorePathOverride + Save/Reload 隔离测试（不触碰真机凭据）
            string tmpStore = Path.Combine(Path.GetTempPath(), "pp_sp_store_" + Guid.NewGuid().ToString("N") + ".dat");
            string prevStore = StorePathOverride;
            var prevSecrets = Redactor.SnapshotRuntimeSecrets();
            try
            {
                StorePathOverride = tmpStore;
                string sErr;
                bool okSave = Save(activeCfg, out sErr);
                Reload();
                var cur = Current;
                check("SP25 Save/Reload 通过隔离 StorePathOverride 加密落盘并自动同步 Redactor",
                    okSave && cur.Enabled && cur.Host == activeCfg.Host &&
                    !Redactor.Apply("leak=" + activeCfg.Password).Contains(activeCfg.Password),
                    "err=" + sErr);
            }
            finally
            {
                StorePathOverride = prevStore;
                lock (Gate) { _loaded = false; _cached = null; }
                Redactor.RestoreRuntimeSecrets(prevSecrets);
                try { if (File.Exists(tmpStore)) File.Delete(tmpStore); } catch { }
            }

            // SP26：Profile.SelectWrongRouteConnections 精准选中固定 IP 走偏到普通机场的连接
            var wrongSnap = Profile.SelectWrongRouteConnections(mockConns, sample.Mismatches);
            check("SP26 SelectWrongRouteConnections 精准捕获固定 IP 走偏连接且不误伤正常固定 IP 连接",
                wrongSnap.Ids.Count == 1 && wrongSnap.Ids[0] == "c-drift-1",
                "ids=" + wrongSnap.Ids.Count);

            // SP27：单行 flow mapping 写法 proxies: [{name: n1, type: ss}] 展开注入
            string inlineFlowYaml =
                "mode: rule\nproxies: [{name: n1, type: ss, server: 1.1.1.1, port: 443}]\n" +
                "proxy-groups:\n  - name: PROXY\n    type: select\n    proxies: [n1]\nrules:\n  - MATCH,PROXY\n";
            string injInline = InjectIntoYaml(inlineFlowYaml, activeCfg, "PROXY");
            check("SP27 流式单行 proxies: [{...}] 正确展开并保留原节点与注入静态节点",
                injInline.Contains("- name: \"PP-STATIC-OUT\"") &&
                injInline.Contains("- {name: n1, type: ss, server: 1.1.1.1, port: 443}"),
                "inline flow expand failed");

            // SP28：FindStaticBoundExes 仅统计主软件不重复、排除派生规则
            var bound = FindStaticBoundExes(new List<Profile.Item>
            {
                new Profile.Item("chrome.exe", NodeName),
                new Profile.Item("chrome_helper.exe", NodeName) { Derived = true },
                new Profile.Item("cursor.exe", "PP-FIXED"),
                new Profile.Item("antigravity", NodeName)
            });
            check("SP28 FindStaticBoundExes 精准识别绑定固定 IP 的主软件并排除派生规则",
                bound.Count == 2 && bound.Contains("chrome.exe") && bound.Contains("antigravity.exe"),
                "count=" + bound.Count);

            // SP29：直连落地模式（HopDirect）不写 dialer-proxy 且无需 PP-STATIC-HOP，适配海外/中转/内网场景
            var directCfg = activeCfg.Clone();
            directCfg.HopMode = HopDirect;
            string injDirect = InjectIntoYaml(baseBlockYaml, directCfg, "");
            check("SP29 直连落地模式（HopDirect）仅注入 PP-STATIC-OUT 且不写 dialer-proxy",
                injDirect.Contains("- name: \"PP-STATIC-OUT\"") &&
                !injDirect.Contains("dialer-proxy:") &&
                !injDirect.Contains(HopGroupName),
                "direct hop inject failed");

            // SP30：自动刷新无损增量合并（更新运行态、发现新进程、且 100% 保留未保存勾选与固定 IP 策略草稿）
            var draftRows = new List<UI.AppRow>
            {
                new UI.AppRow { Exe = "chrome.exe", Name = "Chrome", Proxy = true, Policy = NodeName, Running = 0 },
                new UI.AppRow { Exe = "cursor.exe", Name = "Cursor", Proxy = true, Policy = "PP-FIXED", Running = 2 }
            };
            var scannedNow = new Dictionary<string, Scanner.ProcInfo>(StringComparer.OrdinalIgnoreCase)
            {
                ["chrome.exe"] = new Scanner.ProcInfo { Count = 3, Path = @"C:\Apps\Chrome\chrome.exe" },
                ["firefox.exe"] = new Scanner.ProcInfo { Count = 1, Path = @"C:\Apps\Firefox\firefox.exe" }
            };
            bool merged = UI.MainForm.MergeScannedProcesses(draftRows, scannedNow);
            bool unchangedSecond = !UI.MainForm.MergeScannedProcesses(draftRows, scannedNow);
            var ffRow = draftRows.Find(r => r.Exe == "firefox.exe");
            check("SP30 自动刷新 MergeScannedProcesses 更新运行态并发现新软件，同时 100% 保留未保存勾选与固定 IP 草稿",
                merged && unchangedSecond &&
                draftRows[0].Running == 3 && draftRows[0].Proxy && draftRows[0].Policy == NodeName &&
                draftRows[1].Running == 0 && draftRows[1].Proxy && draftRows[1].Policy == "PP-FIXED" &&
                ffRow != null && ffRow.Running == 1 && !ffRow.Proxy,
                "merged=" + merged + " unchanged2=" + unchangedSecond + " rows=" + draftRows.Count);

            // SP31：CleanProbeError 清理 mihomo JSON 报错换行 & AppList 保留滚动无损刷新
            string cleanTo = CleanProbeError("{\"message\":\"Timeout\"}\n");
            string cleanErr = CleanProbeError("{\"message\":\"An error occurred in the delay test\"}\r\n");
            bool scrollKept = false;
            using (var testList = new UI.AppList { Width = 400, Height = 120 })
            {
                var many = new List<UI.AppRow>();
                for (int i = 0; i < 12; i++) many.Add(new UI.AppRow { Exe = "app" + i + ".exe", Name = "App" + i });
                testList.SetRows(many, false);
                testList.SetScrollForTest(160f);
                testList.SetRows(many, false, true);
                scrollKept = Math.Abs(testList.CurrentScrollOffset - 160f) < 1f;
            }
            check("SP31 报错 JSON 清洗为单行中文 + AppList 保留滚动位置无损刷新",
                cleanTo == "连接超时" && cleanErr == "握手失败或被目标拒接" && scrollKept,
                "to=" + cleanTo + " err=" + cleanErr + " scrollKept=" + scrollKept);

            // SP32：网页表格复制（Tab / 逗号 / 空格 4 列）自动解析
            check("SP32 解析表格整行复制（Tab 与逗号分隔 4 列 host/port/user/pass）",
                ParseQuickInput("198.51.100.22\t10808\ttab_u\ttab_p!#", out ep, out err) &&
                ep.Host == "198.51.100.22" && ep.Port == 10808 && ep.Username == "tab_u" && ep.Password == "tab_p!#" &&
                ParseQuickInput("203.0.113.9,9050,csv_u,csv_p", out ep, out err) &&
                ep.Host == "203.0.113.9" && ep.Port == 9050 && ep.Username == "csv_u" && ep.Password == "csv_p",
                "err=" + err);

            // SP33：旁路探测 follow 模式优先置顶主内核当前活跃物理节点，防止撞到订阅首行公告节点
            string injPref = InjectIntoYaml(baseBlockYaml, activeCfg, "PP-FIXED", "Node-US-01");
            int idxPref = injPref.IndexOf("- \"Node-US-01\"", StringComparison.Ordinal);
            int idxFallback = injPref.IndexOf("- \"PP-FIXED\"", StringComparison.Ordinal);
            check("SP33 旁路沙箱 follow 模式将主内核真实活跃节点置顶于 PP-STATIC-HOP 首位",
                idxPref > 0 && idxFallback > idxPref,
                "pref=" + idxPref + " fb=" + idxFallback);

            // SP34：健康检查防瞬态抖动判据（单次后台超时不误弹窗，连续两轮才触发报警，并遵守 120s 冷却）
            DateTime t0 = new DateTime(2026, 11, 25, 10, 0, 0, DateTimeKind.Utc);
            check("SP34 后台单次超时抖动不误报不可达（手动或连续两轮才告警且守 120s 冷却）",
                !ShouldAlertStaticTimeout(false, 1, DateTime.MinValue, t0) &&
                ShouldAlertStaticTimeout(true, 1, t0, t0.AddSeconds(5)) &&
                ShouldAlertStaticTimeout(false, 2, DateTime.MinValue, t0) &&
                !ShouldAlertStaticTimeout(false, 2, t0, t0.AddSeconds(60)) &&
                ShouldAlertStaticTimeout(false, 2, t0, t0.AddSeconds(121)),
                "alertPolicy");

            // SP35：停用确认窗默认主按钮必须为「切回高速代理」，防止误关闭原本的高速代理开关
            bool disablePromptOk = false;
            using (var prompt = new UI.StaticProxyDialog.DisableConfirmPrompt(new List<string> { "Chrome", "Edge" }))
            {
                var acceptBtn = prompt.AcceptButton as System.Windows.Forms.Button;
                disablePromptOk = acceptBtn != null &&
                                  acceptBtn.Text == "切回高速代理" &&
                                  prompt.SelectedOutcome == UI.StaticProxyDialog.DialogOutcome.Cancelled;
            }
            check("SP35 停用确认窗默认焦点为「切回高速代理」且不默认关闭高速代理",
                disablePromptOk,
                "disablePromptOk=" + disablePromptOk);

            // SP36：开启独立固定 IP 摘要卡高度扩展与全宽度无碰撞几何边界自检（466px 到 880px 全断点）
            bool staticLayoutOk = true;
            foreach (float tw in new[] { 466f, 519f, 520f, 534f, 577f, 639f, 640f, 706f, 880f })
            {
                bool sDual, twoRow;
                float sDivX, sLeft1R, sLeft2R;
                System.Drawing.RectangleF sSpark, sMini, sRect, sortRect;
                System.Drawing.RectangleF[] tRects;
                UI.MainForm.ComputeSummaryLayoutBounds(
                    tw, true, out sDual, out sDivX, out sSpark, out sMini, out sLeft1R, out sLeft2R);
                UI.MainForm.ComputeAppsToolbarBounds(tw, true, out sRect, out tRects, out sortRect, out twoRow);
                float sumBot = 88f + UI.MainForm.SummaryHeightFor(true);
                if (sDual != (tw >= 520f)) staticLayoutOk = false;
                if (sSpark.IsEmpty || sSpark.Width < 120f || sSpark.Height < 26f || sSpark.Bottom >= sumBot) staticLayoutOk = false;
                if (!sDual && (sMini.IsEmpty || sMini.Height < 30f || sMini.Bottom >= sSpark.Y)) staticLayoutOk = false;
                if (sDual && (sLeft1R >= sDivX || sLeft2R >= sDivX || sDivX >= sSpark.X)) staticLayoutOk = false;
                if (sRect.Y <= sumBot || UI.MainForm.ListTopFor(twoRow, true) <= tRects[0].Bottom) staticLayoutOk = false;
                if (twoRow && sRect.Right >= sortRect.X) staticLayoutOk = false;
                if (!twoRow && (sRect.Right >= tRects[0].X || tRects[3].Right >= sortRect.X)) staticLayoutOk = false;
            }
            check("SP36 开启独立固定 IP 后摘要卡三通道速率与三色曲线全断点无碰撞（466–880px）",
                staticLayoutOk,
                "staticLayoutOk=" + staticLayoutOk);

            // SP37：固定 IP 未开启 AllowUdp 时，自动生成 AND,((NETWORK,UDP),...),REJECT 防穿透规则（禁止 QUIC/WebRTC 落入 MATCH,DIRECT）
            string chromePathRx = @"(?i)^C:\\Program\ Files\\Google\\Chrome\\Application\\";
            var udpTestItems = new List<Profile.Item>
            {
                new Profile.Item("chrome.exe", NodeName),
                new Profile.Item("cursor.exe", "PP-FIXED"),
                new Profile.Item(chromePathRx, NodeName, true) { Derived = true }
            };
            string payUdpGuard = Discovery.BuildPayload(baseBlockYaml, udpTestItems, "PP-FIXED", false, null, true);
            string payNoUdpGuard = Discovery.BuildPayload(baseBlockYaml, udpTestItems, "PP-FIXED", false, null, false);
            int idxGuardName = payUdpGuard.IndexOf("- AND,((NETWORK,UDP),(PROCESS-NAME,chrome.exe)),REJECT", StringComparison.Ordinal);
            int idxGuardPath = payUdpGuard.IndexOf("- AND,((NETWORK,UDP),(PROCESS-PATH-REGEX," + chromePathRx + ")),REJECT", StringComparison.Ordinal);
            int idxMatchDir = payUdpGuard.IndexOf("- MATCH,DIRECT", StringComparison.Ordinal);
            string parsedExe, parsedRx;
            bool parseNameOk = IsStaticUdpGuardKernelRule(new Profile.RuleInfo
            {
                Type = "AND",
                Payload = "((Network,udp) && (ProcessName,chrome.exe))",
                Proxy = "REJECT"
            }, out parsedExe) && parsedExe == "chrome.exe";
            bool parsePathOk = IsStaticUdpGuardKernelRule(new Profile.RuleInfo
            {
                Type = "AND",
                Payload = "((Network,udp) && (ProcessPathRegex," + chromePathRx + "))",
                Proxy = "REJECT"
            }, out parsedRx) && parsedRx == chromePathRx;
            check("SP37 固定 IP 默认自动注入 AND/UDP/REJECT 防穿透规则且位于 MATCH,DIRECT 之前（开启 UDP 时不注入）",
                idxGuardName > 0 && idxGuardPath > idxGuardName && idxMatchDir > idxGuardPath &&
                !payUdpGuard.Contains("cursor.exe)),REJECT") &&
                !payNoUdpGuard.Contains("NETWORK,UDP") &&
                parseNameOk && parsePathOk &&
                IsStaticUdpGuardRuleLine(BuildUdpGuardRule(false, "chrome.exe")) &&
                IsStaticUdpGuardRuleLine(BuildUdpGuardRule(true, chromePathRx)),
                "idxN=" + idxGuardName + " idxP=" + idxGuardPath + " idxM=" + idxMatchDir + " pN=" + parseNameOk + " pP=" + parsePathOk);

            // SP38：Guard.AuditRules + Judge 审计固定 IP 防 UDP 穿透规则缺失与 MATCH 越位
            var udpPolicies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["chrome.exe"] = NodeName };
            var rulesMissingUdp = new List<Profile.RuleInfo>
            {
                new Profile.RuleInfo { Type = "ProcessName", Payload = "chrome.exe", Proxy = NodeName },
                new Profile.RuleInfo { Type = "Match", Payload = "", Proxy = "DIRECT" }
            };
            var rulesWithUdp = new List<Profile.RuleInfo>
            {
                new Profile.RuleInfo { Type = "ProcessName", Payload = "chrome.exe", Proxy = NodeName },
                new Profile.RuleInfo { Type = "AND", Payload = "((Network,udp) && (ProcessName,chrome.exe))", Proxy = "REJECT" },
                new Profile.RuleInfo { Type = "Match", Payload = "", Proxy = "DIRECT" }
            };
            var rulesMatchBeforeUdp = new List<Profile.RuleInfo>
            {
                new Profile.RuleInfo { Type = "ProcessName", Payload = "chrome.exe", Proxy = NodeName },
                new Profile.RuleInfo { Type = "Match", Payload = "", Proxy = "DIRECT" },
                new Profile.RuleInfo { Type = "AND", Payload = "((Network,udp) && (ProcessName,chrome.exe))", Proxy = "REJECT" }
            };
            var audMiss = Guard.AuditRules(rulesMissingUdp, new[] { "chrome.exe" }, udpPolicies, null, true);
            var audGood = Guard.AuditRules(rulesWithUdp, new[] { "chrome.exe" }, udpPolicies, null, true);
            var audBadOrder = Guard.AuditRules(rulesMatchBeforeUdp, new[] { "chrome.exe" }, udpPolicies, null, true);
            check("SP38 Guard 精准揪出固定 IP 缺失 UDP 防穿透规则及 MATCH 越位并在规则齐全时判定通过",
                audMiss.MissingStaticUdpGuards == 1 &&
                !string.IsNullOrEmpty(Guard.Judge(audMiss, 1, false)) &&
                audGood.MissingStaticUdpGuards == 0 &&
                Guard.Judge(audGood, 1, false) == null &&
                !string.IsNullOrEmpty(Guard.Judge(audBadOrder, 1, false)),
                "miss=" + audMiss.MissingStaticUdpGuards + " good=" + audGood.MissingStaticUdpGuards);

            // SP39：Traffic 对账在固定 IP 有大量预期 UDP REJECT 拦截时仍准确判「正常」（不误报链路未知或偏离）
            var mockStaticRejConns = new Dictionary<string, object>
            {
                ["connections"] = new List<object>
                {
                    new Dictionary<string, object>
                    {
                        ["id"] = "s-tcp-1",
                        ["chains"] = new List<object> { NodeName },
                        ["upload"] = 1000.0,
                        ["download"] = 5000.0,
                        ["metadata"] = new Dictionary<string, object> { ["process"] = "chrome.exe" }
                    },
                    new Dictionary<string, object>
                    {
                        ["id"] = "s-udp-r1",
                        ["chains"] = new List<object> { "REJECT" },
                        ["upload"] = 0.0,
                        ["download"] = 0.0,
                        ["metadata"] = new Dictionary<string, object> { ["process"] = "chrome.exe" }
                    },
                    new Dictionary<string, object>
                    {
                        ["id"] = "s-udp-r2",
                        ["chains"] = new List<object> { "REJECT" },
                        ["upload"] = 0.0,
                        ["download"] = 0.0,
                        ["metadata"] = new Dictionary<string, object> { ["process"] = "chrome.exe" }
                    }
                }
            };
            var sampStaticRej = Traffic.AnalyzeConnections(
                mockStaticRejConns,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "chrome.exe" },
                true, DateTime.MinValue, null, 0, false,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "chrome.exe" }, null);
            check("SP39 Traffic 固定 IP 进程含多条预期 UDP REJECT 拦截时仍判正常且不计偏离",
                sampStaticRej.Ok && sampStaticRej.Mismatch == 0 &&
                sampStaticRej.DescribeOutbound().Contains("正常"),
                "mismatch=" + sampStaticRej.Mismatch);

            // SP40：HopAuto（智能优选最低延迟跳板）DPAPI 持久化、物理节点提取与全量热切注入（严格 DAG 无环）
            var autoCfg = activeCfg.Clone();
            autoCfg.HopMode = HopAuto;
            autoCfg.AutoBestHopNode = "Node-US-01";
            autoCfg.AutoBestDelayMs = 198;
            autoCfg.AutoBestHopPool = new List<string> { "Node-US-01", "Node-JP-02" };
            autoCfg.AutoBestUpdatedAtTicks = t0.Ticks;
            string autoJson = SerializeConfig(autoCfg);
            var autoBack = DeserializeConfig(autoJson);
            string multiNodeYaml =
                "mode: rule\nproxies:\n" +
                "- name: 剩余流量：99GB\n  type: ss\n  server: 1.1.1.1\n  port: 443\n" +
                "- name: Node-JP-02\n  type: vless\n  server: 2.2.2.2\n  port: 443\n" +
                "- {name: \"Node-US-01\", type: trojan, server: 3.3.3.3, port: 443}\n" +
                "proxy-groups:\n" +
                "- name: PP-FIXED\n  type: select\n  proxies:\n    - Node-JP-02\n    - Node-US-01\n" +
                "rules:\n- MATCH,DIRECT\n";
            var extracted = ExtractConcreteNodeNamesFromYaml(multiNodeYaml, 20);
            string injAuto = InjectIntoYaml(multiNodeYaml, autoCfg, "PP-FIXED");
            int posUs = injAuto.IndexOf("- \"Node-US-01\"", StringComparison.Ordinal);
            int posJp = injAuto.IndexOf("- \"Node-JP-02\"", StringComparison.Ordinal);
            int posFb = injAuto.IndexOf("- \"PP-FIXED\"", StringComparison.Ordinal);
            check("SP40 HopAuto 序列化、物理节点提取与 PP-STATIC-HOP 优选置顶+热切全量成员注入（过滤公告且无环）",
                autoBack.HopMode == HopAuto && autoBack.AutoBestHopNode == "Node-US-01" &&
                autoBack.AutoBestDelayMs == 198 && autoBack.AutoBestHopPool.Count == 2 &&
                extracted.Count == 2 && extracted.Contains("Node-JP-02") && extracted.Contains("Node-US-01") &&
                posUs > 0 && posJp > posUs && posFb > posJp &&
                !injAuto.Contains("剩余流量：99GB\"\n"),
                "ext=" + extracted.Count + " us=" + posUs + " jp=" + posJp + " fb=" + posFb);

            // SP41：ShouldSwitchAutoHop 防抖决策（故障/手动/降幅>=15%且>=60ms 切；微小波动不横跳）
            check("SP41 ShouldSwitchAutoHop 防抖阈值（>=15% 且 >=60ms 切换；轻微波动不横跳；故障/手动立即切）",
                !ShouldSwitchAutoHop("A", 300, "A", 180, false, false) &&
                !ShouldSwitchAutoHop("A", 300, "B", 260, false, false) &&
                !ShouldSwitchAutoHop("A", 200, "B", 160, false, false) &&
                ShouldSwitchAutoHop("A", 400, "B", 310, false, false) &&
                ShouldSwitchAutoHop("A", 400, "B", 390, true, false) &&
                ShouldSwitchAutoHop("A", 400, "B", 395, false, true) &&
                !ShouldSwitchAutoHop("A", 400, NodeName, 100, true, true),
                "debounce");

            // SP42：旁路端到端测速配置生成（unified-delay + PP-BENCH-*）与节点页双按钮全断点无重叠
            string benchYaml = ProbeSidecar.BuildAutoHopBenchmarkConfigText(
                multiNodeYaml.Split('\n'), 55101, 55102, "sec", @"C:\Data", autoCfg, extracted);
            bool barBtnsOk = true;
            foreach (float tw in new[] { 466f, 520f, 639f, 640f, 880f })
            {
                System.Drawing.RectangleF optR, manR;
                UI.NodesPage.ComputeStaticBarButtons(tw, 180f, true, out optR, out manR);
                if (optR.IsEmpty || manR.IsEmpty || optR.Right >= manR.Left || manR.Right > tw || optR.Left < 180f)
                    barBtnsOk = false;
            }
            check("SP42 旁路端到端测速配置正确叠加 unified-delay 与 PP-BENCH-* 节点且节点页优选按钮全断点无碰撞",
                benchYaml.Contains("unified-delay: true") &&
                benchYaml.Contains("- name: \"PP-BENCH-0\"") &&
                benchYaml.Contains("- name: \"PP-BENCH-1\"") &&
                benchYaml.Contains("dialer-proxy: \"Node-JP-02\"") &&
                benchYaml.Contains("dialer-proxy: \"Node-US-01\"") &&
                barBtnsOk,
                "barBtnsOk=" + barBtnsOk);

            // SP43：首页固定 IP 延迟胶囊文案格式化（ms/测速中/稳速刷新/超时）与三行摘要卡全断点无碰撞（466–880px）
            string spPref, spMid, spSuf, spTone;
            UI.MainForm.FormatStaticDelayPillParts(autoCfg, "Node-US-01", 196, false, false, false, true, true,
                out spPref, out spMid, out spSuf, out spTone);
            bool pillOk = spPref == "固定IP " && spMid == "优选:Node-US-01" && spSuf == " · 196ms" && spTone == "ok";
            UI.MainForm.FormatStaticDelayPillParts(autoCfg, "Node-US-01", 520, false, false, false, true, true,
                out spPref, out spMid, out spSuf, out spTone);
            pillOk = pillOk && spSuf == " · 520ms" && spTone == "warn";
            UI.MainForm.FormatStaticDelayPillParts(autoCfg, "Node-US-01", -1, false, true, false, true, true,
                out spPref, out spMid, out spSuf, out spTone);
            pillOk = pillOk && spSuf == " · 测速中…" && spTone == "ok";
            UI.MainForm.FormatStaticDelayPillParts(autoCfg, "Node-US-01", 196, false, false, true, true, true,
                out spPref, out spMid, out spSuf, out spTone);
            pillOk = pillOk && spSuf == " · 稳速刷新中…" && spTone == "accent";
            UI.MainForm.FormatStaticDelayPillParts(autoCfg, "Node-US-01", -1, true, false, false, true, true,
                out spPref, out spMid, out spSuf, out spTone);
            pillOk = pillOk && spSuf == " · 超时" && spTone == "danger";
            string fittedNarrow = UI.MainForm.FitPillLabel("固定IP ", "优选:很长的日本东京专线节点名称(高级流量)", " · 196ms",
                120f, s => s.Length * 7.5f);
            pillOk = pillOk && fittedNarrow.EndsWith(" · 196ms", StringComparison.Ordinal);

            bool row3GeoOk = true;
            foreach (float tw in new[] { 466f, 519f, 520f, 534f, 640f, 880f })
            {
                bool sDual;
                float sDivX, l1, l2;
                System.Drawing.RectangleF spk, mini, r1, r2, r3;
                UI.MainForm.ComputeSummaryLayoutBounds(tw, true, out sDual, out sDivX, out spk, out mini, out l1, out l2);
                UI.MainForm.ComputeSummaryRowBounds(tw, true, out r1, out r2, out r3);
                float sumBot = 88f + UI.MainForm.SummaryHeightFor(true);
                if (r1.Bottom > r2.Top || r2.Bottom > r3.Top || r3.Bottom > sumBot - 6f) row3GeoOk = false;
                if (r3.Width < 200f || r3.Right > l2 + 0.5f) row3GeoOk = false;
                if (!sDual && r3.Right >= mini.Left) row3GeoOk = false;
                if (sDual && r3.Right >= sDivX) row3GeoOk = false;
            }
            check("SP43 首页固定 IP 延迟胶囊全态格式化、窄屏保毫秒尾缀与三行摘要卡全断点零碰撞",
                pillOk && row3GeoOk,
                "pillOk=" + pillOk + " row3GeoOk=" + row3GeoOk);

            // SP44：定期稳速刷新调度策略（10 分钟定时、连续 2 轮劣化 3 分钟冷却、超时 2 分钟急救、抖动惩罚分）
            var nowRef = t0.AddMinutes(11);
            bool schedOk =
                ComputeStabilityScore(200, 20) < ComputeStabilityScore(190, 120) &&
                !IsStaticDelayDegraded(260, 200) &&
                IsStaticDelayDegraded(450, 200) &&
                IsStaticDelayDegraded(760, -1) &&
                !ShouldRunScheduledAutoHopRefresh(false, false, DateTime.MinValue, 0, nowRef, 0, false) &&
                !ShouldRunScheduledAutoHopRefresh(true, true, DateTime.MinValue, 0, nowRef, 0, false) &&
                ShouldRunScheduledAutoHopRefresh(true, false, t0, t0.Ticks, nowRef, 0, false) &&
                !ShouldRunScheduledAutoHopRefresh(true, false, t0.AddMinutes(5), t0.AddMinutes(5).Ticks, nowRef, 0, false) &&
                ShouldRunScheduledAutoHopRefresh(true, false, nowRef.AddSeconds(-185), nowRef.AddSeconds(-185).Ticks, nowRef, 2, false) &&
                !ShouldRunScheduledAutoHopRefresh(true, false, nowRef.AddSeconds(-100), nowRef.AddSeconds(-100).Ticks, nowRef, 2, false) &&
                ShouldRunScheduledAutoHopRefresh(true, false, nowRef.AddSeconds(-125), nowRef.AddSeconds(-125).Ticks, nowRef, 0, true) &&
                !ShouldRunScheduledAutoHopRefresh(true, false, nowRef.AddSeconds(-60), nowRef.AddSeconds(-60).Ticks, nowRef, 0, true);
            check("SP44 定期稳速刷新策略（10分钟巡检/2轮劣化提前触发/超时急救冷却/稳态抖动惩罚分）",
                schedOk,
                "schedOk=" + schedOk);

            // SP45：稳态复核排序（3 次全通低抖动节点打败 1 次偶快掉线节点与高抖动节点）
            var rankTest = new List<HopCandidateResult>
            {
                new HopCandidateResult { NodeName = "FlakyFast", DelayMs = 150, JitterMs = 0, SuccessSamples = 1, StabilityScore = ComputeStabilityScore(150, 0) },
                new HopCandidateResult { NodeName = "JitteryFast", DelayMs = 185, JitterMs = 140, SuccessSamples = 3, StabilityScore = ComputeStabilityScore(185, 140) },
                new HopCandidateResult { NodeName = "SteadyBest", DelayMs = 200, JitterMs = 16, SuccessSamples = 3, StabilityScore = ComputeStabilityScore(200, 16) }
            };
            RankVerifiedHopCandidates(rankTest);
            check("SP45 稳态复核排序优先 3 次全通且低抖动节点（SteadyBest > JitteryFast > FlakyFast）",
                rankTest[0].NodeName == "SteadyBest" &&
                rankTest[1].NodeName == "JitteryFast" &&
                rankTest[2].NodeName == "FlakyFast",
                "order=" + rankTest[0].NodeName + "," + rankTest[1].NodeName + "," + rankTest[2].NodeName);

            sb.Insert(0, "\n[独立固定 IP StaticProxy 自检] " + pass + " PASS / " + fail + " FAIL\n");
            return sb.ToString();
        }
    }
}
