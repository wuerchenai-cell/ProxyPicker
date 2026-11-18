using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace ProxyPicker.Core
{
    /// <summary>
    /// 当前节点网络质量、真实出口 IP、风控纯净度与 AI/Antigravity 专项预检引擎：
    /// 1) 连续 3 次节点探测计算平均延迟、抖动（Jitter）与丢包率；
    /// 2) 采集 TUN / IPv6 / DNS 通道健康状态，并独立探测是否存在本地 IPv6 直连泄漏；
    /// 3) 通过双源风控接口（ipquery.io + proxycheck.io）核验真实出口 IP、落地地区、ASN、机房/家宽属性与风控分；
    /// 4) 实测 Google 搜索（检测 /sorry 异常流量验证）、Google Gemini/Antigravity API 地区支持、OpenAI 与 Cloudflare 边缘节点。
    /// </summary>
    internal static class NodeQuality
    {
        public enum RiskLevel { Unknown = 0, Low = 1, Medium = 2, High = 3 }

        public sealed class ServiceGateResult
        {
            public string Name = "";
            public bool Tested;
            public bool Ok;
            public int StatusCode;
            public int LatencyMs = -1;
            public string Detail = "";
            public bool Blocked;
        }

        public sealed class QualityStats
        {
            public int Sent;
            public int Received;
            public int AvgMs = -1;
            public int MinMs = -1;
            public int MaxMs = -1;
            public int JitterMs = -1;
            public int LossPercent = 100;
        }

        public sealed class IpIntelligence
        {
            public bool Ok;
            public string Source = "";
            public string Ip = "";
            public string CountryCode = "";
            public string Country = "";
            public string Region = "";
            public string City = "";
            public string Asn = "";
            public string Isp = "";
            public string Org = "";
            public string IpType = "";   // Residential / Business / Hosting / VPN / ...
            public bool IsDatacenter;
            public bool IsVpn;
            public bool IsProxy;
            public bool IsTor;
            public bool IsMobile;
            public int RawRiskScore = -1;
            public string Error = "";
        }

        public sealed class QualityReport
        {
            public DateTime CheckedAtUtc = DateTime.UtcNow;
            public string TargetNode = "";
            public string KernelMode = "rule";
            public bool TunEnabled;
            public string TunStack = "";
            public bool KernelIpv6On;
            public bool DnsEnabled;
            public int MixedPort;

            public QualityStats Net = new QualityStats();
            public bool RoutedDirect;
            public bool Ipv6LeakChecked;
            public bool DirectIpv6Leak;
            public string LeakedIpv6 = "";

            public string Ip = "";
            public string CountryCode = "";
            public string CountryName = "";
            public string City = "";
            public string Asn = "";
            public string Isp = "";
            public string ExpectedRegion = "";
            public string DesiredRegion = "auto";
            public bool RegionMatched = true;
            public string RegionNote = "";

            public RiskLevel Level = RiskLevel.Unknown;
            public int RiskScore = -1;
            public string IpKindLabel = "未知";
            public bool GoogleAiUsable;
            public bool AntigravityUsable
            {
                get { return GoogleAiUsable; }
                set { GoogleAiUsable = value; }
            }
            public int MainstreamReadyCount;
            public bool IsSidecarIsolated;
            public string VerdictTitle = "";
            public string VerdictDetail = "";

            public ServiceGateResult GoogleSearch = new ServiceGateResult { Name = "Google" };
            public ServiceGateResult GeminiApi = new ServiceGateResult { Name = "Google AI (Gemini)" };
            public ServiceGateResult OpenAiApi = new ServiceGateResult { Name = "OpenAI" };
            public ServiceGateResult ClaudeApi = new ServiceGateResult { Name = "Claude" };
            public ServiceGateResult Cloudflare = new ServiceGateResult { Name = "Cloudflare" };
            public string CfColo = "";
            public string Error = "";
        }

        public sealed class CachedIpRisk
        {
            public IpIntelligence Primary;
            public IpIntelligence Secondary;
        }

        public sealed class BatchIpCache
        {
            readonly object _sync = new object();
            readonly Dictionary<string, CachedIpRisk> _byIp = new Dictionary<string, CachedIpRisk>(StringComparer.OrdinalIgnoreCase);

            public bool TryGet(string ip, out CachedIpRisk entry)
            {
                entry = null;
                if (string.IsNullOrWhiteSpace(ip)) return false;
                lock (_sync) return _byIp.TryGetValue(ip.Trim(), out entry);
            }

            public void Put(string ip, IpIntelligence primary, IpIntelligence secondary)
            {
                if (string.IsNullOrWhiteSpace(ip) || primary == null || !primary.Ok) return;
                lock (_sync)
                    _byIp[ip.Trim()] = new CachedIpRisk { Primary = primary, Secondary = secondary };
            }

            public int Count { get { lock (_sync) return _byIp.Count; } }
        }

        const int MaxProbeBodyBytes = 64 * 1024;
        const string ProbeUserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

        static void EnsureTlsProtocols()
        {
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
            try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)12288; } catch { }
        }

        /// <summary>计算 3 连发测速统计：平均延迟、极值、最大抖动及丢包率。</summary>
        public static QualityStats ComputeQualityStats(IList<int> samples)
        {
            var st = new QualityStats();
            if (samples == null || samples.Count == 0) return st;

            st.Sent = samples.Count;
            int sum = 0;
            int min = int.MaxValue;
            int max = 0;
            foreach (int ms in samples)
            {
                if (ms <= 0) continue;
                st.Received++;
                sum += ms;
                if (ms < min) min = ms;
                if (ms > max) max = ms;
            }
            st.LossPercent = (int)Math.Round(((st.Sent - st.Received) * 100.0) / st.Sent);
            if (st.Received > 0)
            {
                st.AvgMs = (int)Math.Round(sum / (double)st.Received);
                st.MinMs = min;
                st.MaxMs = max;
                st.JitterMs = st.Received >= 2 ? (max - min) : 0;
            }
            return st;
        }

        /// <summary>综合双源 IP 情报、Google/Antigravity/OpenAI 实测与 IPv6 泄漏，生成确定性的风控评级与地区结论。</summary>
        public static void EvaluateReport(QualityReport r, IpIntelligence primary, IpIntelligence secondary)
        {
            if (r == null) return;

            string ip = !string.IsNullOrEmpty(primary?.Ip) ? primary.Ip : (secondary?.Ip ?? "");
            string cc = (!string.IsNullOrEmpty(primary?.CountryCode) ? primary.CountryCode : (secondary?.CountryCode ?? "")).ToUpperInvariant();
            string country = !string.IsNullOrEmpty(primary?.Country) ? primary.Country : (secondary?.Country ?? "");
            string city = !string.IsNullOrEmpty(primary?.City) ? primary.City : (secondary?.City ?? "");
            string asn = !string.IsNullOrEmpty(primary?.Asn) ? primary.Asn : (secondary?.Asn ?? "");
            string isp = !string.IsNullOrEmpty(primary?.Isp) ? primary.Isp : (secondary?.Isp ?? "");
            if (string.IsNullOrEmpty(isp)) isp = !string.IsNullOrEmpty(primary?.Org) ? primary.Org : (secondary?.Org ?? "");

            r.Ip = ip;
            r.CountryCode = cc;
            r.CountryName = cc.Length == 2 ? NodeManager.RegionDisplayName(cc) : country;
            r.City = city;
            r.Asn = asn;
            r.Isp = isp;

            // 1. 判定是否误走了国内直连（未经过境外代理节点）
            if (Profile.ClassifyExit(r.TargetNode) == Profile.ExitKind.Proxy &&
                string.Equals(cc, "CN", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(r.ExpectedRegion, "CN", StringComparison.OrdinalIgnoreCase))
            {
                r.RoutedDirect = true;
            }

            // 2. 期望地区对比（auto = 跟节点名；any = 不限；两字码 = 用户显式指定地区）
            string targetRegion = r.DesiredRegion;
            if (string.IsNullOrEmpty(targetRegion) ||
                targetRegion.Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                targetRegion = !string.IsNullOrEmpty(r.ExpectedRegion)
                    ? r.ExpectedRegion
                    : NodeManager.ParseRegionFromNodeName(r.TargetNode);
            }
            else if (targetRegion.Equals("any", StringComparison.OrdinalIgnoreCase))
            {
                targetRegion = "";
            }
            r.ExpectedRegion = targetRegion;

            if (r.RoutedDirect)
            {
                r.RegionMatched = false;
                r.RegionNote = "探针走了本地直连（CN），未经过代理节点";
            }
            else if (cc.Length == 2 && targetRegion.Length == 2)
            {
                r.RegionMatched = string.Equals(cc, targetRegion, StringComparison.OrdinalIgnoreCase);
                r.RegionNote = r.RegionMatched
                    ? ("与预期地区一致（" + NodeManager.RegionDisplayName(cc) + "）")
                    : ("地区漂移：预期 " + NodeManager.RegionDisplayName(targetRegion) +
                       "，实际落地 " + NodeManager.RegionDisplayName(cc) + " (" + cc + ")");
            }
            else if (cc.Length == 2)
            {
                r.RegionMatched = true;
                r.RegionNote = "当前落地：" + NodeManager.RegionDisplayName(cc) + " (" + cc + ")";
            }
            else
            {
                r.RegionMatched = false;
                r.RegionNote = "未能识别落地国家/地区";
            }

            // 3. 合并双源类型与风控值
            bool isTor = (primary?.IsTor ?? false) || (secondary?.IsTor ?? false);
            bool isVpn = (primary?.IsVpn ?? false) || (secondary?.IsVpn ?? false);
            bool isProxy = (primary?.IsProxy ?? false) || (secondary?.IsProxy ?? false);
            bool isDc = (primary?.IsDatacenter ?? false) || (secondary?.IsDatacenter ?? false);
            bool isMob = (primary?.IsMobile ?? false) || (secondary?.IsMobile ?? false);

            string rawType = !string.IsNullOrEmpty(secondary?.IpType) ? secondary.IpType : (primary?.IpType ?? "");
            if (rawType.Equals("Hosting", StringComparison.OrdinalIgnoreCase) ||
                rawType.Equals("Data Center/Web Hosting/Transit", StringComparison.OrdinalIgnoreCase))
                isDc = true;
            if (rawType.Equals("VPN", StringComparison.OrdinalIgnoreCase) ||
                rawType.Equals("Proxy", StringComparison.OrdinalIgnoreCase))
                isVpn = true;

            if (isTor) r.IpKindLabel = "Tor 匿名节点";
            else if (isVpn || isProxy) r.IpKindLabel = isDc ? "机房 VPN/共享代理" : "已标记代理 IP";
            else if (isMob) r.IpKindLabel = "移动蜂窝 IP";
            else if (isDc) r.IpKindLabel = "机房 / 数据中心 IP";
            else if (rawType.Equals("Residential", StringComparison.OrdinalIgnoreCase)) r.IpKindLabel = "住宅家宽 IP";
            else if (rawType.Equals("Business", StringComparison.OrdinalIgnoreCase)) r.IpKindLabel = "商业宽带 / ISP";
            else if (ip.Length > 0) r.IpKindLabel = "常规 ISP IP";
            else r.IpKindLabel = "未知";

            int s1 = primary != null && primary.RawRiskScore >= 0 ? primary.RawRiskScore : -1;
            int s2 = secondary != null && secondary.RawRiskScore >= 0 ? secondary.RawRiskScore : -1;
            int score = Math.Max(s1, s2);
            if (score < 0 && ip.Length > 0) score = 0;

            if (score >= 0)
            {
                if (isDc && score < 25) score = 25;         // 机房 IP 不应误标为 0 分家宽
                if ((isVpn || isProxy) && score < 45) score = 45;
                if (isTor && score < 95) score = 95;
                if (r.GoogleSearch.Tested && r.GoogleSearch.Blocked)
                    score = Math.Max(65, score + 25);       // Google 已弹 /sorry 人机风控
                if (r.OpenAiApi.Tested && r.OpenAiApi.Blocked)
                    score = Math.Max(score, 55);
                score = Math.Max(0, Math.Min(100, score));
            }
            r.RiskScore = score;

            // 4. 主流服务与综合风控结论（通用化：Google Search / Google AI / OpenAI / Claude）
            bool geminiBlocked = r.GeminiApi.Tested && r.GeminiApi.Blocked;
            bool googleBlocked = r.GoogleSearch.Tested && r.GoogleSearch.Blocked;
            bool claudeBlocked = r.ClaudeApi.Tested && r.ClaudeApi.Blocked;
            bool openaiBlocked = r.OpenAiApi.Tested && r.OpenAiApi.Blocked;
            bool highLoss = r.Net.Sent > 0 && r.Net.LossPercent >= 66;
            bool unsupportedRegion = IsAiUnsupportedCountry(cc);

            int ready = 0;
            if (r.GoogleSearch.Tested && r.GoogleSearch.Ok) ready++;
            if (r.GeminiApi.Tested && r.GeminiApi.Ok) ready++;
            if (r.OpenAiApi.Tested && r.OpenAiApi.Ok) ready++;
            if (r.ClaudeApi.Tested && r.ClaudeApi.Ok) ready++;
            r.MainstreamReadyCount = ready;

            if (r.RoutedDirect)
            {
                r.Level = RiskLevel.Unknown;
                r.GoogleAiUsable = false;
                r.VerdictTitle = "未走代理节点（命中本地直连）";
                r.VerdictDetail = "本次探测从本地直连出网。请使用内置隔离侧车通道，或在白名单保存后再检测。";
                return;
            }

            if (string.IsNullOrEmpty(ip) && !r.GeminiApi.Tested && !r.GoogleSearch.Tested)
            {
                r.Level = RiskLevel.Unknown;
                r.GoogleAiUsable = false;
                r.VerdictTitle = "网络不可达或节点超时";
                r.VerdictDetail = string.IsNullOrEmpty(r.Error) ? "无法访问外网风控检测接口，请更换节点重试。" : r.Error;
                return;
            }

            if (score >= 65 || googleBlocked || geminiBlocked || unsupportedRegion || isTor)
            {
                r.Level = RiskLevel.High;
                r.GoogleAiUsable = false;
                var reasons = new List<string>();
                if (geminiBlocked || unsupportedRegion) reasons.Add("部分服务地区受限（Google AI / 主流 AI）");
                if (googleBlocked) reasons.Add("Google 搜索触发异常流量验证");
                if (claudeBlocked) reasons.Add("Claude 触发安全盾/地区拦截");
                if (openaiBlocked) reasons.Add("OpenAI 地区/代理受限");
                if (score >= 65) reasons.Add("IP 风险评分偏高 (" + score + "/100)");
                if (isTor) reasons.Add("Tor 出口");
                r.VerdictTitle = "高风险 · 存在风控或地区受限";
                r.VerdictDetail = string.Join("；", reasons.ToArray()) +
                    (r.DirectIpv6Leak ? "；另检测到本地 IPv6 直连泄漏" : "") + "。敏感业务建议切换其他低风控节点。";
            }
            else if (score >= 30 || isDc || isVpn || isProxy || r.DirectIpv6Leak || !r.RegionMatched || highLoss || claudeBlocked || openaiBlocked)
            {
                r.Level = RiskLevel.Medium;
                r.GoogleAiUsable = !geminiBlocked && !unsupportedRegion && !highLoss;
                var notes = new List<string>();
                if (r.DirectIpv6Leak) notes.Add("检测到 IPv6 直连泄漏（可在帮助中开启防泄漏）");
                if (!r.RegionMatched) notes.Add(r.RegionNote);
                if (highLoss) notes.Add("节点丢包较高 (" + r.Net.LossPercent + "%)");
                if (claudeBlocked) notes.Add("Claude 出现盾/地区拦截");
                if (openaiBlocked) notes.Add("OpenAI 存在拦截");
                if (notes.Count == 0) notes.Add(r.IpKindLabel + "，主流服务可用 (" + ready + "/4)，高频或高敏感业务请留意风控提示");
                r.VerdictTitle = r.GoogleAiUsable ? "中风险 · 常规业务可用" : "中风险 · 链路不够稳定";
                r.VerdictDetail = string.Join("；", notes.ToArray()) + "。";
            }
            else
            {
                r.Level = RiskLevel.Low;
                r.GoogleAiUsable = true;
                r.VerdictTitle = "低风险 · 纯净原生出口";
                r.VerdictDetail = "出口地区与 IP 纯净度优异，Google / Gemini / OpenAI / Claude 等常规及敏感服务均正常放行。";
            }
        }

        static bool IsAiUnsupportedCountry(string cc)
        {
            if (string.IsNullOrEmpty(cc)) return false;
            string c = cc.ToUpperInvariant();
            return c == "CN" || c == "HK" || c == "MO" || c == "RU" || c == "BY" || c == "IR" || c == "KP" || c == "CU" || c == "SY";
        }

        internal static bool ShouldRunHostIpv6LeakProbe(bool isSidecar, bool hasOutboundProxy, bool tunEnabled)
        {
            return !isSidecar && (hasOutboundProxy || tunEnabled);
        }

        /// <summary>执行完整节点质量、IP 归属地、双源风控与主流服务可用性检测（支持传入隔离侧车引擎零干扰探测）。</summary>
        public static QualityReport RunQualityProbe(
            string targetNode,
            string desiredRegion,
            bool forceDirectFallbackHint,
            string knownConfigPath = null,
            ProbeSidecar.SidecarSession sidecar = null)
        {
            EnsureTlsProtocols();
            string cfgPath = !string.IsNullOrEmpty(knownConfigPath) ? knownConfigPath : Paths.ClashYaml;
            var report = new QualityReport
            {
                CheckedAtUtc = DateTime.UtcNow,
                TargetNode = targetNode ?? "",
                DesiredRegion = Settings.NormalizeDesiredRegion(desiredRegion),
                ExpectedRegion = NodeManager.ParseRegionFromNodeName(targetNode),
                IsSidecarIsolated = sidecar != null
            };

            // 1. 读运行底座配置（若走旁路隔离侧车则直接复用侧车端口与隔离状态，不依赖宿主内核）
            int mixedPort = 0;
            WebProxy outboundProxy = null;
            if (sidecar != null && sidecar.OutboundProxy != null)
            {
                outboundProxy = sidecar.OutboundProxy;
                report.MixedPort = sidecar.ProxyPort;
                report.KernelMode = "global";
                report.TunEnabled = false;
                report.DnsEnabled = false;
                report.KernelIpv6On = false;
            }
            else
            {
                var cfgRes = Mihomo.FetchJson("/configs", 4000, cfgPath);
                if (cfgRes.Ok && cfgRes.Data != null)
                {
                    var cfg = cfgRes.Data;
                    report.KernelMode = (Json.Str(cfg, "mode") ?? "rule").Trim().ToLowerInvariant();
                    var tun = Json.Obj(cfg, "tun");
                    report.TunEnabled = Json.Bool(tun, "enable", false);
                    report.TunStack = Json.Str(tun, "stack") ?? "";
                    var dns = Json.Obj(cfg, "dns");
                    report.DnsEnabled = Json.Bool(dns, "enable", false);
                    report.KernelIpv6On = Json.Bool(cfg, "ipv6", false);
                    outboundProxy = ResolveOutboundProxy(cfg, out mixedPort);
                    report.MixedPort = mixedPort;
                }
            }

            // 2. 连续 3 次节点测速（算平均延迟 / 抖动 / 丢包率）
            var pingSamples = new List<int>(3);
            if (!string.IsNullOrEmpty(report.TargetNode) &&
                Profile.ClassifyExit(report.TargetNode) == Profile.ExitKind.Proxy)
            {
                for (int i = 0; i < 3; i++)
                {
                    if (sidecar != null)
                    {
                        pingSamples.Add(sidecar.MeasureDelay(report.TargetNode, Settings.DelayTestUrl,
                            Math.Min(Settings.DelayTimeoutMs, 3500)));
                    }
                    else
                    {
                        var d = Profile.MeasureDelay(report.TargetNode, Settings.DelayTestUrl,
                            Math.Min(Settings.DelayTimeoutMs, 3500), cfgPath);
                        pingSamples.Add(d.Ok ? d.DelayMs : -1);
                    }
                }
            }
            report.Net = ComputeQualityStats(pingSamples);

            // 3. 并行发起：出口 IP 查询 + 5 项主流服务/边缘预检 + IPv6 直连泄漏复核
            IpIntelligence primaryIp = null;
            string cfColo = "";
            string cfIp = "";
            string cfLoc = "";

            string rawV6 = "";
            bool ipv6ProbeDone = false;
            bool allowTunFallback = sidecar == null && report.TunEnabled;
            bool runHostIpv6Probe = ShouldRunHostIpv6LeakProbe(sidecar != null, outboundProxy != null, report.TunEnabled);
            Task tIp = Task.Run(() => { primaryIp = FetchPrimaryIpAndRisk(outboundProxy, allowTunFallback); });
            Task tCf = Task.Run(() => { report.Cloudflare = ProbeCloudflare(outboundProxy, allowTunFallback, out cfIp, out cfLoc, out cfColo); });
            Task tGoogle = Task.Run(() => { report.GoogleSearch = ProbeGoogleSearch(outboundProxy, allowTunFallback); });
            Task tGemini = Task.Run(() => { report.GeminiApi = ProbeGeminiAntigravity(outboundProxy, allowTunFallback); });
            Task tOpenAi = Task.Run(() => { report.OpenAiApi = ProbeOpenAi(outboundProxy, allowTunFallback); });
            Task tClaude = Task.Run(() => { report.ClaudeApi = ProbeClaude(outboundProxy, allowTunFallback); });
            Task tIpv6 = Task.Run(() =>
            {
                if (!runHostIpv6Probe) return;
                rawV6 = ProbeRawIpv6Leak();
                System.Threading.Volatile.Write(ref ipv6ProbeDone, true);
            });

            Task.WaitAll(new[] { tIp, tCf, tGoogle, tGemini, tOpenAi, tClaude, tIpv6 }, 9000);
            report.Ipv6LeakChecked = System.Threading.Volatile.Read(ref ipv6ProbeDone);

            report.CfColo = cfColo;
            if (primaryIp == null) primaryIp = new IpIntelligence();

            // 如果主接口被墙/失败但 Cloudflare trace 拿到了真实出口 IP 和国家码，回填后继续查风控库
            if (string.IsNullOrEmpty(primaryIp.Ip) && !string.IsNullOrEmpty(cfIp))
            {
                primaryIp.Ip = cfIp;
                primaryIp.CountryCode = cfLoc;
                primaryIp.Source = "cloudflare";
                IpIntelligence extra = FetchIpQueryForIp(cfIp, outboundProxy, allowTunFallback);
                if (extra != null && extra.Ok) primaryIp = extra;
            }

            if (!string.IsNullOrEmpty(rawV6))
            {
                // 验证直出 IPv6 是否属于中国大陆或与 IPv4 出口国家不一致，避免误伤真正的双栈代理节点
                var v6Info = FetchIpQueryForIp(rawV6, outboundProxy, allowTunFallback);
                string v6Cc = v6Info != null ? (v6Info.CountryCode ?? "") : "";
                bool diffCountry = !string.IsNullOrEmpty(primaryIp.CountryCode) &&
                                   !string.IsNullOrEmpty(v6Cc) &&
                                   !string.Equals(v6Cc, primaryIp.CountryCode, StringComparison.OrdinalIgnoreCase);
                if (string.Equals(v6Cc, "CN", StringComparison.OrdinalIgnoreCase) || diffCountry)
                {
                    report.DirectIpv6Leak = true;
                    report.LeakedIpv6 = rawV6;
                }
            }

            // 4. 第二源风控复核（proxycheck.io），交叉验证主机类型与滥用风险分
            IpIntelligence secondaryIp = null;
            if (!string.IsNullOrEmpty(primaryIp.Ip))
                secondaryIp = FetchProxyCheck(primaryIp.Ip, outboundProxy, allowTunFallback);

            if (forceDirectFallbackHint && !report.RoutedDirect &&
                string.Equals(report.KernelMode, "rule", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(primaryIp.CountryCode, "CN", StringComparison.OrdinalIgnoreCase))
            {
                report.RoutedDirect = true;
            }

            if (string.IsNullOrEmpty(primaryIp.Ip) && !string.IsNullOrEmpty(primaryIp.Error))
                report.Error = primaryIp.Error;

            EvaluateReport(report, primaryIp, secondaryIp);
            return report;
        }

        /// <summary>
        /// 批量全部节点测风控专用的快速体检入口：
        /// 1) 利用 Cloudflare trace 毫秒级拿到出口 IP 后查询同轮 BatchIpCache，命中同落地节点直接复用 ASN/机房风控情报防 429 限流；
        /// 2) 对节点本身仍独立执行 Google / Antigravity / OpenAI 实测与延迟核验，不偷工减料、每节点仅需 ~1.2–2s。
        /// </summary>
        public static QualityReport RunFastQualityProbe(
            string targetNode,
            string desiredRegion,
            bool forceDirectFallbackHint,
            int knownDelayMs,
            BatchIpCache ipCache,
            string knownConfigPath = null,
            ProbeSidecar.SidecarSession sidecar = null)
        {
            EnsureTlsProtocols();
            string cfgPath = !string.IsNullOrEmpty(knownConfigPath) ? knownConfigPath : Paths.ClashYaml;
            var report = new QualityReport
            {
                CheckedAtUtc = DateTime.UtcNow,
                TargetNode = targetNode ?? "",
                DesiredRegion = Settings.NormalizeDesiredRegion(desiredRegion),
                ExpectedRegion = NodeManager.ParseRegionFromNodeName(targetNode),
                IsSidecarIsolated = sidecar != null
            };

            int mixedPort = 0;
            WebProxy outboundProxy = null;
            if (sidecar != null && sidecar.OutboundProxy != null)
            {
                outboundProxy = sidecar.OutboundProxy;
                report.MixedPort = sidecar.ProxyPort;
                report.KernelMode = "global";
                report.TunEnabled = false;
                report.DnsEnabled = false;
                report.KernelIpv6On = false;
            }
            else
            {
                var cfgRes = Mihomo.FetchJson("/configs", 2500, cfgPath);
                if (cfgRes.Ok && cfgRes.Data != null)
                {
                    var cfg = cfgRes.Data;
                    report.KernelMode = (Json.Str(cfg, "mode") ?? "rule").Trim().ToLowerInvariant();
                    var tun = Json.Obj(cfg, "tun");
                    report.TunEnabled = Json.Bool(tun, "enable", false);
                    report.TunStack = Json.Str(tun, "stack") ?? "";
                    var dns = Json.Obj(cfg, "dns");
                    report.DnsEnabled = Json.Bool(dns, "enable", false);
                    report.KernelIpv6On = Json.Bool(cfg, "ipv6", false);
                    outboundProxy = ResolveOutboundProxy(cfg, out mixedPort);
                    report.MixedPort = mixedPort;
                }
            }

            if (knownDelayMs > 0)
            {
                report.Net = new QualityStats { Sent = 1, Received = 1, AvgMs = knownDelayMs, MinMs = knownDelayMs, MaxMs = knownDelayMs, JitterMs = 0, LossPercent = 0 };
            }
            else if (!string.IsNullOrEmpty(report.TargetNode) &&
                     Profile.ClassifyExit(report.TargetNode) == Profile.ExitKind.Proxy)
            {
                int ms = sidecar != null
                    ? sidecar.MeasureDelay(report.TargetNode, Settings.DelayTestUrl, 2500)
                    : -1;
                if (sidecar == null)
                {
                    var d = Profile.MeasureDelay(report.TargetNode, Settings.DelayTestUrl, 2500, cfgPath);
                    ms = d.Ok ? d.DelayMs : -1;
                }
                report.Net = ComputeQualityStats(new[] { ms });
                if (ms <= 0)
                {
                    report.Error = "节点测速超时";
                    EvaluateReport(report, new IpIntelligence { Error = report.Error }, null);
                    return report;
                }
            }

            bool allowTunFallback = sidecar == null && report.TunEnabled;
            string cfIp = "", cfLoc = "", cfColo = "";
            Task tCf = Task.Run(() => { report.Cloudflare = ProbeCloudflare(outboundProxy, allowTunFallback, out cfIp, out cfLoc, out cfColo); });
            Task tGoogle = Task.Run(() => { report.GoogleSearch = ProbeGoogleSearch(outboundProxy, allowTunFallback); });
            Task tGemini = Task.Run(() => { report.GeminiApi = ProbeGeminiAntigravity(outboundProxy, allowTunFallback); });
            Task tOpenAi = Task.Run(() => { report.OpenAiApi = ProbeOpenAi(outboundProxy, allowTunFallback); });
            Task tClaude = Task.Run(() => { report.ClaudeApi = ProbeClaude(outboundProxy, allowTunFallback); });
            Task.WaitAll(new[] { tCf, tGoogle, tGemini, tOpenAi, tClaude }, 4200);

            report.CfColo = cfColo;

            IpIntelligence primaryIp = null;
            IpIntelligence secondaryIp = null;
            CachedIpRisk cached;
            if (!string.IsNullOrEmpty(cfIp) && ipCache != null && ipCache.TryGet(cfIp, out cached))
            {
                primaryIp = cached.Primary;
                secondaryIp = cached.Secondary;
            }
            else
            {
                primaryIp = !string.IsNullOrEmpty(cfIp)
                    ? FetchIpQueryForIp(cfIp, outboundProxy, allowTunFallback)
                    : FetchPrimaryIpAndRisk(outboundProxy, allowTunFallback);
                if ((primaryIp == null || !primaryIp.Ok) && !string.IsNullOrEmpty(cfIp))
                {
                    primaryIp = FetchPrimaryIpAndRisk(outboundProxy, allowTunFallback);
                    if (primaryIp == null || !primaryIp.Ok)
                        primaryIp = new IpIntelligence { Ok = !string.IsNullOrEmpty(cfLoc), Ip = cfIp, CountryCode = cfLoc, Source = "cloudflare" };
                }
                string ipKey = !string.IsNullOrEmpty(primaryIp?.Ip) ? primaryIp.Ip : cfIp;
                if (!string.IsNullOrEmpty(ipKey))
                    secondaryIp = FetchProxyCheck(ipKey, outboundProxy, allowTunFallback);

                if (ipCache != null && !string.IsNullOrEmpty(ipKey) && primaryIp != null && primaryIp.Ok)
                    ipCache.Put(ipKey, primaryIp, secondaryIp);
            }

            if (primaryIp == null) primaryIp = new IpIntelligence();
            if (forceDirectFallbackHint && !report.RoutedDirect &&
                string.Equals(report.KernelMode, "rule", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(primaryIp.CountryCode, "CN", StringComparison.OrdinalIgnoreCase))
            {
                report.RoutedDirect = true;
            }
            if (string.IsNullOrEmpty(primaryIp.Ip) && !string.IsNullOrEmpty(primaryIp.Error))
                report.Error = primaryIp.Error;

            EvaluateReport(report, primaryIp, secondaryIp);
            return report;
        }

        /// <summary>从内核 `/configs` 解析本地出站代理端口与安全回环主机名（兼容 mixed-port / http port / bind-address）。</summary>
        internal static WebProxy ResolveOutboundProxy(Dictionary<string, object> cfg, out int resolvedPort)
        {
            resolvedPort = 0;
            if (cfg == null) return null;
            int p = ReadIntField(cfg, "mixed-port");
            if (p <= 0) p = ReadIntField(cfg, "port");
            if (p <= 0 || p > 65535) return null;
            resolvedPort = p;

            string bind = (Json.Str(cfg, "bind-address") ?? "").Trim();
            string host = "127.0.0.1";
            if (!string.IsNullOrEmpty(bind) &&
                bind != "*" && bind != "0.0.0.0" && bind != "::" &&
                bind.StartsWith("127.", StringComparison.Ordinal))
            {
                host = bind;
            }
            return new WebProxy(host, resolvedPort);
        }

        static bool HttpGetWithFallback(string url, WebProxy proxy, bool allowTunFallback, int timeoutMs,
            out int status, out string locationHeader, out string body, out int ms, out string err)
        {
            if (HttpGetTextWithRedirect(url, proxy, timeoutMs, out status, out locationHeader, out body, out ms, out err))
                return true;
            // 若用户只开 TUN 且客户端端口策略拦截了代理连接，回退直发交由 TUN 接管一次
            if (proxy != null && allowTunFallback)
                return HttpGetTextWithRedirect(url, null, timeoutMs, out status, out locationHeader, out body, out ms, out err);
            return false;
        }

        static IpIntelligence FetchPrimaryIpAndRisk(WebProxy proxy, bool allowTunFallback)
        {
            string body, loc, err;
            int status, ms;
            if (HttpGetWithFallback("https://api.ipquery.io/?format=json", proxy, allowTunFallback, 3800,
                    out status, out loc, out body, out ms, out err) && status >= 200 && status < 300)
            {
                var info = ParseIpQueryJson(body);
                if (info.Ok) return info;
            }

            // 第二备用源：ipwho.is（自带 ASN / ISP / security.vpn/proxy/hosting，应对 ipquery 429 限流或地区节点波动）
            if (HttpGetWithFallback("https://ipwho.is/", proxy, allowTunFallback, 3800,
                    out status, out loc, out body, out ms, out err) && status >= 200 && status < 300)
            {
                var who = ParseIpWhoIsJson(body);
                if (who.Ok) return who;
            }

            // 第三备用源：ipinfo.io/json
            if (HttpGetWithFallback("https://ipinfo.io/json", proxy, allowTunFallback, 3800,
                    out status, out loc, out body, out ms, out err) && status >= 200 && status < 300)
            {
                var fallback = ParseIpInfoJson(body);
                if (fallback.Ok && !string.IsNullOrEmpty(fallback.Ip))
                {
                    var enriched = FetchIpQueryForIp(fallback.Ip, proxy, allowTunFallback);
                    return (enriched != null && enriched.Ok) ? enriched : fallback;
                }
            }

            return new IpIntelligence { Error = !string.IsNullOrEmpty(err) ? err : ("出口 IP 查询失败（HTTP " + status + "）") };
        }

        static IpIntelligence FetchIpQueryForIp(string ip, WebProxy proxy, bool allowTunFallback)
        {
            string body, loc, err;
            int status, ms;
            if (HttpGetWithFallback("https://api.ipquery.io/" + Uri.EscapeDataString(ip), proxy, allowTunFallback, 3800,
                    out status, out loc, out body, out ms, out err) && status >= 200 && status < 300)
            {
                var q = ParseIpQueryJson(body);
                if (q.Ok) return q;
            }
            if (HttpGetWithFallback("https://ipwho.is/" + Uri.EscapeDataString(ip), proxy, allowTunFallback, 3800,
                    out status, out loc, out body, out ms, out err) && status >= 200 && status < 300)
            {
                var w = ParseIpWhoIsJson(body);
                if (w.Ok) return w;
            }
            return null;
        }

        static IpIntelligence FetchProxyCheck(string ip, WebProxy proxy, bool allowTunFallback)
        {
            string body, loc, err;
            int status, ms;
            string url = "https://proxycheck.io/v2/" + Uri.EscapeDataString(ip) + "?vpn=1&asn=1&risk=1";
            if (HttpGetWithFallback(url, proxy, allowTunFallback, 3800, out status, out loc, out body, out ms, out err) &&
                status >= 200 && status < 300)
                return ParseProxyCheckJson(body, ip);
            return null;
        }

        static ServiceGateResult ProbeCloudflare(WebProxy proxy, bool allowTunFallback, out string ip, out string loc, out string colo)
        {
            ip = ""; loc = ""; colo = "";
            var g = new ServiceGateResult { Name = "Cloudflare", Tested = true };
            string body, redirectLoc, err;
            int status, ms;
            bool ok = HttpGetWithFallback("https://cloudflare.com/cdn-cgi/trace", proxy, allowTunFallback, 3400,
                out status, out redirectLoc, out body, out ms, out err);
            g.StatusCode = status;
            g.LatencyMs = ms;
            if (ok && status == 200)
            {
                ParseCloudflareTrace(body, out ip, out loc, out colo);
                g.Ok = true;
                g.Detail = string.IsNullOrEmpty(colo) ? ("200 OK · " + ms + "ms") : (colo + " · " + ms + "ms");
            }
            else if (status == 403 || status == 429)
            {
                g.Blocked = true;
                g.Detail = "拦截 HTTP " + status;
            }
            else
            {
                g.Detail = !string.IsNullOrEmpty(err) ? err : ("HTTP " + status);
            }
            return g;
        }

        static ServiceGateResult ProbeGoogleSearch(WebProxy proxy, bool allowTunFallback)
        {
            string body, err, location;
            int status, ms;
            bool ok = HttpGetWithFallback("https://www.google.com/search?q=connectivity&hl=en",
                proxy, allowTunFallback, 3600, out status, out location, out body, out ms, out err);
            return EvaluateGoogleRiskResponse(ok, status, location, body, ms, err);
        }

        static ServiceGateResult ProbeGeminiAntigravity(WebProxy proxy, bool allowTunFallback)
        {
            string body, loc, err;
            int status, ms;
            bool ok = HttpGetWithFallback("https://generativelanguage.googleapis.com/v1beta/models",
                proxy, allowTunFallback, 3600, out status, out loc, out body, out ms, out err);
            return EvaluateGeminiRegionResponse(ok, status, body, ms, err);
        }

        static ServiceGateResult ProbeOpenAi(WebProxy proxy, bool allowTunFallback)
        {
            string body, loc, err;
            int status, ms;
            bool ok = HttpGetWithFallback("https://api.openai.com/v1/models",
                proxy, allowTunFallback, 3600, out status, out loc, out body, out ms, out err);
            return EvaluateOpenAiResponse(ok, status, body, ms, err);
        }

        static ServiceGateResult ProbeClaude(WebProxy proxy, bool allowTunFallback)
        {
            string body, loc, err;
            int status, ms;
            bool ok = HttpGetWithFallback("https://api.anthropic.com/v1/models",
                proxy, allowTunFallback, 3600, out status, out loc, out body, out ms, out err);
            return EvaluateClaudeResponse(ok, status, body, ms, err);
        }

        /// <summary>故意绕过 HTTP 代理直探 api6.ipify.org，检测系统是否存在跳过 Clash 的本地公网 IPv6 直连泄漏。</summary>
        static string ProbeRawIpv6Leak()
        {
            string body, err;
            int status, ms;
            if (!HttpGetText("https://api6.ipify.org?format=json", null, 2500, out status, out body, out ms, out err))
                return "";
            if (status < 200 || status >= 300 || string.IsNullOrEmpty(body)) return "";
            try
            {
                var obj = Json.AsObject(Json.Parse(body));
                string ip = Json.Str(obj, "ip") ?? "";
                IPAddress addr;
                if (IPAddress.TryParse(ip, out addr) && addr.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
                    return ip;
            }
            catch { }
            return "";
        }

        // ---------------------------------------------------------------- 纯解析与响应评估（自检直接覆盖）

        internal static IpIntelligence ParseIpQueryJson(string text)
        {
            var r = new IpIntelligence { Source = "ipquery.io" };
            try
            {
                var root = Json.AsObject(Json.Parse(text));
                if (root == null) return r;
                r.Ip = Json.Str(root, "ip") ?? "";
                var isp = Json.Obj(root, "isp");
                if (isp != null)
                {
                    r.Asn = Json.Str(isp, "asn") ?? "";
                    r.Org = Json.Str(isp, "org") ?? "";
                    r.Isp = Json.Str(isp, "isp") ?? "";
                }
                var loc = Json.Obj(root, "location");
                if (loc != null)
                {
                    r.Country = Json.Str(loc, "country") ?? "";
                    r.CountryCode = (Json.Str(loc, "country_code") ?? "").ToUpperInvariant();
                    r.City = Json.Str(loc, "city") ?? "";
                    r.Region = Json.Str(loc, "state") ?? "";
                }
                var risk = Json.Obj(root, "risk");
                if (risk != null)
                {
                    r.IsMobile = Json.Bool(risk, "is_mobile", false);
                    r.IsVpn = Json.Bool(risk, "is_vpn", false);
                    r.IsTor = Json.Bool(risk, "is_tor", false);
                    r.IsProxy = Json.Bool(risk, "is_proxy", false);
                    r.IsDatacenter = Json.Bool(risk, "is_datacenter", false);
                    int score = ReadIntField(risk, "risk_score");
                    if (score >= 0) r.RawRiskScore = score;
                }
                r.Ok = r.Ip.Length > 0;
            }
            catch (Exception ex) { r.Error = ex.Message; }
            return r;
        }

        internal static IpIntelligence ParseIpWhoIsJson(string text)
        {
            var r = new IpIntelligence { Source = "ipwho.is" };
            try
            {
                var root = Json.AsObject(Json.Parse(text));
                if (root == null || !Json.Bool(root, "success", true)) return r;
                r.Ip = Json.Str(root, "ip") ?? "";
                r.Country = Json.Str(root, "country") ?? "";
                r.CountryCode = (Json.Str(root, "country_code") ?? "").ToUpperInvariant();
                r.Region = Json.Str(root, "region") ?? "";
                r.City = Json.Str(root, "city") ?? "";
                var conn = Json.Obj(root, "connection");
                if (conn != null)
                {
                    int asnNum = ReadIntField(conn, "asn");
                    r.Asn = asnNum > 0 ? ("AS" + asnNum) : (Json.Str(conn, "asn") ?? "");
                    r.Org = Json.Str(conn, "org") ?? "";
                    r.Isp = Json.Str(conn, "isp") ?? "";
                }
                var sec = Json.Obj(root, "security");
                if (sec != null)
                {
                    r.IsVpn = Json.Bool(sec, "vpn", false);
                    r.IsProxy = Json.Bool(sec, "proxy", false);
                    r.IsTor = Json.Bool(sec, "tor", false);
                    r.IsDatacenter = Json.Bool(sec, "hosting", false);
                }
                r.Ok = r.Ip.Length > 0;
            }
            catch (Exception ex) { r.Error = ex.Message; }
            return r;
        }

        internal static IpIntelligence ParseProxyCheckJson(string text, string ip)
        {
            var r = new IpIntelligence { Source = "proxycheck.io", Ip = ip ?? "" };
            try
            {
                var root = Json.AsObject(Json.Parse(text));
                if (root == null) return r;
                var entry = Json.Obj(root, ip);
                if (entry == null) return r;

                r.Asn = Json.Str(entry, "asn") ?? "";
                r.Isp = Json.Str(entry, "provider") ?? "";
                r.Org = Json.Str(entry, "organisation") ?? "";
                r.Country = Json.Str(entry, "country") ?? "";
                r.CountryCode = (Json.Str(entry, "isocode") ?? "").ToUpperInvariant();
                r.City = Json.Str(entry, "city") ?? "";
                r.Region = Json.Str(entry, "region") ?? "";
                r.IpType = Json.Str(entry, "type") ?? "";
                string proxyFlag = Json.Str(entry, "proxy") ?? "";
                r.IsProxy = proxyFlag.Equals("yes", StringComparison.OrdinalIgnoreCase);
                if (r.IpType.Equals("VPN", StringComparison.OrdinalIgnoreCase)) r.IsVpn = true;
                if (r.IpType.Equals("Hosting", StringComparison.OrdinalIgnoreCase)) r.IsDatacenter = true;
                int risk = ReadIntField(entry, "risk");
                if (risk >= 0) r.RawRiskScore = risk;
                r.Ok = true;
            }
            catch (Exception ex) { r.Error = ex.Message; }
            return r;
        }

        internal static IpIntelligence ParseIpInfoJson(string text)
        {
            var r = new IpIntelligence { Source = "ipinfo.io" };
            try
            {
                var root = Json.AsObject(Json.Parse(text));
                if (root == null) return r;
                r.Ip = Json.Str(root, "ip") ?? "";
                r.CountryCode = (Json.Str(root, "country") ?? "").ToUpperInvariant();
                r.City = Json.Str(root, "city") ?? "";
                r.Region = Json.Str(root, "region") ?? "";
                r.Org = Json.Str(root, "org") ?? "";
                r.Isp = r.Org;
                if (r.Org.StartsWith("AS", StringComparison.OrdinalIgnoreCase))
                {
                    int sp = r.Org.IndexOf(' ');
                    if (sp > 2)
                    {
                        r.Asn = r.Org.Substring(0, sp);
                        r.Isp = r.Org.Substring(sp + 1).Trim();
                    }
                }
                r.Ok = r.Ip.Length > 0;
            }
            catch (Exception ex) { r.Error = ex.Message; }
            return r;
        }

        internal static void ParseCloudflareTrace(string text, out string ip, out string loc, out string colo)
        {
            ip = ""; loc = ""; colo = "";
            if (string.IsNullOrEmpty(text)) return;
            foreach (var raw in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = raw.IndexOf('=');
                if (eq <= 0) continue;
                string k = raw.Substring(0, eq).Trim();
                string v = raw.Substring(eq + 1).Trim();
                if (k == "ip") ip = v;
                else if (k == "loc") loc = v.ToUpperInvariant();
                else if (k == "colo") colo = v.ToUpperInvariant();
            }
        }

        internal static ServiceGateResult EvaluateGoogleRiskResponse(
            bool gotResponse, int status, string locationHeader, string body, int ms, string err)
        {
            var g = new ServiceGateResult
            {
                Name = "Google",
                Tested = true,
                StatusCode = status,
                LatencyMs = ms
            };
            if (!gotResponse)
            {
                g.Detail = !string.IsNullOrEmpty(err) ? err : "连接超时";
                return g;
            }

            bool sorryRedirect = !string.IsNullOrEmpty(locationHeader) &&
                locationHeader.IndexOf("/sorry", StringComparison.OrdinalIgnoreCase) >= 0;
            bool sorryBody = !string.IsNullOrEmpty(body) &&
                (body.IndexOf("/sorry/index", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 body.IndexOf("unusual traffic", StringComparison.OrdinalIgnoreCase) >= 0);

            if (sorryRedirect || sorryBody || status == 429 || status == 403)
            {
                g.Blocked = true;
                g.Ok = false;
                g.Detail = "触发人机风控 (" + status + ")";
                return g;
            }

            if (status >= 200 && status < 400)
            {
                g.Ok = true;
                g.Detail = "放行 · " + ms + "ms";
                return g;
            }

            g.Detail = "HTTP " + status;
            return g;
        }

        internal static ServiceGateResult EvaluateGeminiRegionResponse(
            bool gotResponse, int status, string body, int ms, string err)
        {
            var g = new ServiceGateResult
            {
                Name = "Google AI (Gemini)",
                Tested = true,
                StatusCode = status,
                LatencyMs = ms
            };
            if (!gotResponse)
            {
                g.Detail = !string.IsNullOrEmpty(err) ? err : "不可达/超时";
                return g;
            }
            string low = (body ?? "").ToLowerInvariant();
            if (low.Contains("location is not supported") || (low.Contains("country") && low.Contains("not supported")))
            {
                g.Blocked = true;
                g.Ok = false;
                g.Detail = "地区不支持";
                return g;
            }
            if (status == 200 ||
                ((status == 400 || status == 401 || status == 403) &&
                 (low.Contains("api key") || low.Contains("unregistered") || low.Contains("credentials") || low.Contains("permission_denied") && !low.Contains("location"))))
            {
                g.Ok = true;
                g.Detail = "地区支持 · " + ms + "ms";
                return g;
            }
            if (status == 403 || status == 429)
            {
                g.Blocked = true;
                g.Ok = false;
                g.Detail = "风控拦截 HTTP " + status;
                return g;
            }
            g.Detail = "HTTP " + status;
            return g;
        }

        internal static ServiceGateResult EvaluateOpenAiResponse(
            bool gotResponse, int status, string body, int ms, string err)
        {
            var g = new ServiceGateResult
            {
                Name = "OpenAI",
                Tested = true,
                StatusCode = status,
                LatencyMs = ms
            };
            if (!gotResponse)
            {
                g.Detail = !string.IsNullOrEmpty(err) ? err : "不可达/超时";
                return g;
            }
            string low = (body ?? "").ToLowerInvariant();
            if (status == 401 || status == 200 || (status == 400 && low.Contains("openai")))
            {
                g.Ok = true;
                g.Detail = "放行 · " + ms + "ms";
                return g;
            }
            if (status == 403 || status == 429 || low.Contains("unsupported_country") || low.Contains("cloudflare"))
            {
                g.Blocked = true;
                g.Ok = false;
                g.Detail = "地区/盾拦截 (" + status + ")";
                return g;
            }
            g.Detail = "HTTP " + status;
            return g;
        }

        internal static ServiceGateResult EvaluateClaudeResponse(
            bool gotResponse, int status, string body, int ms, string err)
        {
            var g = new ServiceGateResult
            {
                Name = "Claude",
                Tested = true,
                StatusCode = status,
                LatencyMs = ms
            };
            if (!gotResponse)
            {
                g.Detail = !string.IsNullOrEmpty(err) ? err : "不可达/超时";
                return g;
            }
            string low = (body ?? "").ToLowerInvariant();
            if (status == 401 || status == 200 || status == 405 || (status == 400 && low.Contains("anthropic")))
            {
                g.Ok = true;
                g.Detail = "放行 · " + ms + "ms";
                return g;
            }
            if (status == 403 || status == 451 || status == 429 ||
                low.Contains("cloudflare") || low.Contains("just a moment") ||
                low.Contains("not available") || low.Contains("unsupported"))
            {
                g.Blocked = true;
                g.Ok = false;
                g.Detail = "地区/盾拦截 (" + status + ")";
                return g;
            }
            g.Detail = "HTTP " + status;
            return g;
        }

        // ---------------------------------------------------------------- HTTP 探针传输工具

        static int ReadIntField(Dictionary<string, object> o, string key)
        {
            if (o == null) return -1;
            object v;
            if (o.TryGetValue(key, out v) && v is double) return (int)Math.Round((double)v);
            return -1;
        }

        static bool HttpGetText(string url, WebProxy proxy, int timeoutMs,
            out int status, out string body, out int ms, out string error)
        {
            string loc;
            return HttpGetTextWithRedirect(url, proxy, timeoutMs, out status, out loc, out body, out ms, out error);
        }

        static bool HttpGetTextWithRedirect(string url, WebProxy proxy, int timeoutMs,
            out int status, out string locationHeader, out string body, out int ms, out string error)
        {
            status = 0;
            locationHeader = "";
            body = "";
            error = "";
            var sw = Stopwatch.StartNew();
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "GET";
                req.Timeout = timeoutMs;
                req.ReadWriteTimeout = timeoutMs;
                req.Proxy = proxy;
                req.KeepAlive = false;
                req.AllowAutoRedirect = false;
                req.UserAgent = ProbeUserAgent;
                req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;

                using (var resp = (HttpWebResponse)req.GetResponse())
                {
                    status = (int)resp.StatusCode;
                    locationHeader = resp.Headers["Location"] ?? "";
                    body = ReadLimitedString(resp.GetResponseStream());
                    ms = (int)sw.ElapsedMilliseconds;
                    return true;
                }
            }
            catch (WebException we)
            {
                var resp = we.Response as HttpWebResponse;
                if (resp != null)
                {
                    try
                    {
                        status = (int)resp.StatusCode;
                        locationHeader = resp.Headers["Location"] ?? "";
                        body = ReadLimitedString(resp.GetResponseStream());
                    }
                    catch { }
                    finally { resp.Close(); }
                    ms = (int)sw.ElapsedMilliseconds;
                    return true;
                }
                error = we.Status == WebExceptionStatus.Timeout ? "连接超时" : we.Message;
                ms = (int)sw.ElapsedMilliseconds;
                return false;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                ms = (int)sw.ElapsedMilliseconds;
                return false;
            }
        }

        static string ReadLimitedString(Stream stream)
        {
            if (stream == null) return "";
            using (stream)
            using (var ms = new MemoryStream())
            {
                byte[] buf = new byte[4096];
                int left = MaxProbeBodyBytes;
                while (left > 0)
                {
                    int n = stream.Read(buf, 0, Math.Min(buf.Length, left));
                    if (n <= 0) break;
                    ms.Write(buf, 0, n);
                    left -= n;
                }
                return Encoding.UTF8.GetString(ms.ToArray());
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

            // 1. Latency stats (jitter + loss)
            var st = ComputeQualityStats(new[] { 80, 120, -1 });
            check("NQ1 ComputeQualityStats 计算平均延迟/抖动/丢包率",
                st.Sent == 3 && st.Received == 2 && st.AvgMs == 100 && st.JitterMs == 40 && st.LossPercent == 33,
                "avg=" + st.AvgMs + " jit=" + st.JitterMs + " loss=" + st.LossPercent);

            // 2. Dual-source JSON parsing
            string iqJson = @"{""ip"":""203.0.113.5"",""isp"":{""asn"":""AS2516"",""org"":""KDDI"",""isp"":""KDDI""},""location"":{""country"":""Japan"",""country_code"":""JP"",""city"":""Tokyo""},""risk"":{""is_mobile"":false,""is_vpn"":false,""is_tor"":false,""is_proxy"":false,""is_datacenter"":false,""risk_score"":4}}";
            string pcJson = @"{""status"":""ok"",""203.0.113.5"":{""country"":""Japan"",""isocode"":""JP"",""type"":""Residential"",""proxy"":""no"",""risk"":5}}";
            var iq = ParseIpQueryJson(iqJson);
            var pc = ParseProxyCheckJson(pcJson, "203.0.113.5");
            check("NQ2 ipquery + proxycheck JSON 字段解析正确",
                iq.Ok && iq.CountryCode == "JP" && iq.RawRiskScore == 4 &&
                pc.Ok && pc.IpType == "Residential" && pc.RawRiskScore == 5,
                "iq=" + iq.Ok + " pc=" + pc.Ok);

            // 3. Low-risk residential node usable for Antigravity
            var repLow = new QualityReport
            {
                TargetNode = "🇯🇵日本-家宽",
                DesiredRegion = "auto",
                GoogleSearch = EvaluateGoogleRiskResponse(true, 200, "", "<html>ok</html>", 110, ""),
                GeminiApi = EvaluateGeminiRegionResponse(true, 403, @"{""error"":{""message"":""Method doesn't allow unregistered callers. Pass an API key.""}}", 120, ""),
                OpenAiApi = EvaluateOpenAiResponse(true, 401, @"{""error"":{""message"":""Missing bearer""}}", 100, "")
            };
            EvaluateReport(repLow, iq, pc);
            check("NQ3 低风险家宽日本节点且 AI 全通评为 Low + AntigravityUsable",
                repLow.Level == RiskLevel.Low && repLow.RiskScore == 5 && repLow.AntigravityUsable && repLow.RegionMatched,
                "lvl=" + repLow.Level + " score=" + repLow.RiskScore);

            // 4. Clean datacenter gets >=25 floor (not misclassified as 0-risk residential)
            var iqDc = new IpIntelligence { Ok = true, Ip = "45.32.16.1", CountryCode = "JP", IsDatacenter = true, RawRiskScore = 0 };
            var pcDc = new IpIntelligence { Ok = true, Ip = "45.32.16.1", CountryCode = "JP", IpType = "Hosting", RawRiskScore = 0 };
            var repDc = new QualityReport
            {
                TargetNode = "🇯🇵JP-01",
                DesiredRegion = "JP",
                GoogleSearch = EvaluateGoogleRiskResponse(true, 200, "", "ok", 90, ""),
                GeminiApi = EvaluateGeminiRegionResponse(true, 400, "API key not valid", 90, ""),
                OpenAiApi = EvaluateOpenAiResponse(true, 401, "missing", 90, "")
            };
            EvaluateReport(repDc, iqDc, pcDc);
            check("NQ4 纯净机房 IP 至少记为 25 分（与家宽区分）且不过度判高风险",
                repDc.RiskScore >= 25 && repDc.Level == RiskLevel.Medium && repDc.AntigravityUsable,
                "score=" + repDc.RiskScore + " lvl=" + repDc.Level);

            // 5. Gemini/Antigravity region unsupported blocks AI even when raw score = 0
            var iqHk = new IpIntelligence { Ok = true, Ip = "198.51.100.8", CountryCode = "JP", IsDatacenter = true, RawRiskScore = 10 };
            var repBlocked = new QualityReport
            {
                TargetNode = "🇯🇵看似日本",
                DesiredRegion = "auto",
                GoogleSearch = EvaluateGoogleRiskResponse(true, 302, "https://www.google.com/sorry/index?continue=...", "", 80, ""),
                GeminiApi = EvaluateGeminiRegionResponse(true, 400, @"{""error"":{""message"":""User location is not supported for the API use.""}}", 80, "")
            };
            EvaluateReport(repBlocked, iqHk, null);
            check("NQ5 Google /sorry 人机拦截与 Gemini 地区封禁准确判为高风险且 Antigravity 不可用",
                repBlocked.GoogleSearch.Blocked && repBlocked.GeminiApi.Blocked &&
                repBlocked.Level == RiskLevel.High && !repBlocked.AntigravityUsable && repBlocked.RiskScore >= 65,
                "lvl=" + repBlocked.Level + " score=" + repBlocked.RiskScore);

            // 6. Region drift detection & CN direct leak detection
            var iqDrift = new IpIntelligence { Ok = true, Ip = "198.51.100.22", CountryCode = "SG", RawRiskScore = 15 };
            var repDrift = new QualityReport { TargetNode = "🇯🇵日本-专线", DesiredRegion = "auto" };
            EvaluateReport(repDrift, iqDrift, null);
            check("NQ6 节点写日本但落地新加坡检出地区漂移",
                !repDrift.RegionMatched && repDrift.ExpectedRegion == "JP" && repDrift.CountryCode == "SG",
                repDrift.RegionNote);

            var iqCn = new IpIntelligence { Ok = true, Ip = "111.199.65.59", CountryCode = "CN", RawRiskScore = 0 };
            var repCn = new QualityReport { TargetNode = "🇯🇵日本-B", DesiredRegion = "auto" };
            EvaluateReport(repCn, iqCn, null);
            check("NQ7 境外代理节点探测回源中国大陆自动检出 RoutedDirect 防误报",
                repCn.RoutedDirect && !repCn.AntigravityUsable && repCn.Level == RiskLevel.Unknown,
                "direct=" + repCn.RoutedDirect);

            // 7. Cloudflare trace parser
            string cfIp, cfLoc, cfColo;
            ParseCloudflareTrace("ip=1.2.3.4\nloc=JP\ncolo=NRT\n", out cfIp, out cfLoc, out cfColo);
            check("NQ8 Cloudflare trace 解析 ip/loc/colo",
                cfIp == "1.2.3.4" && cfLoc == "JP" && cfColo == "NRT",
                cfIp + "/" + cfLoc + "/" + cfColo);

            // 8. Standalone Gemini/Antigravity block with clean Google Search & clean JP IP
            var iqSolo = new IpIntelligence { Ok = true, Ip = "198.51.100.44", CountryCode = "JP", IsDatacenter = false, RawRiskScore = 0 };
            var repGeminiOnly = new QualityReport
            {
                TargetNode = "🇯🇵干净却被AI拦",
                DesiredRegion = "auto",
                GoogleSearch = EvaluateGoogleRiskResponse(true, 200, "", "<html>ok</html>", 90, ""),
                GeminiApi = EvaluateGeminiRegionResponse(true, 400, @"{""error"":{""message"":""User location is not supported for the API use.""}}", 90, ""),
                OpenAiApi = EvaluateOpenAiResponse(true, 401, "missing", 90, "")
            };
            EvaluateReport(repGeminiOnly, iqSolo, null);
            check("NQ9 Google 正常且风控分为 0 时仅凭 Gemini 地区不支持仍标 High 且 Antigravity 不可用",
                repGeminiOnly.GeminiApi.Blocked && repGeminiOnly.Level == RiskLevel.High && !repGeminiOnly.AntigravityUsable,
                "lvl=" + repGeminiOnly.Level + " ai=" + repGeminiOnly.AntigravityUsable);

            // 9. ipwho.is fallback parser & BatchIpCache reuse with independent service results
            string whoJson = @"{""ip"":""45.76.10.2"",""success"":true,""country"":""Japan"",""country_code"":""JP"",""city"":""Tokyo"",""connection"":{""asn"":20473,""org"":""Vultr"",""isp"":""The Constant Company""},""security"":{""vpn"":true,""proxy"":false,""tor"":false,""hosting"":true}}";
            var whoOk = ParseIpWhoIsJson(whoJson);
            var whoFail = ParseIpWhoIsJson(@"{""success"":false,""message"":""limit""}");
            check("NQ10 ipwho.is 备用源正确提取 ASN/机房/VPN 与拒绝 success=false",
                whoOk.Ok && whoOk.Asn == "AS20473" && whoOk.IsDatacenter && whoOk.IsVpn && !whoFail.Ok,
                whoOk.Asn + "/" + whoOk.IsDatacenter + "/" + whoFail.Ok);

            var cache = new BatchIpCache();
            cache.Put("203.0.113.5", iq, pc);
            CachedIpRisk hit;
            bool hitOk = cache.TryGet("203.0.113.5", out hit) && hit.Primary != null && hit.Primary.CountryCode == "JP";
            var proxyPort = ResolveOutboundProxy(new Dictionary<string, object> { { "mixed-port", 7897.0 }, { "bind-address", "*" } }, out int resolvedP);
            check("NQ11 BatchIpCache 同出口复用与 ResolveOutboundProxy 兼容任意监听端口",
                hitOk && resolvedP == 7897 && proxyPort != null && proxyPort.Address.Host == "127.0.0.1",
                "hit=" + hitOk + " p=" + resolvedP);

            var clOk = EvaluateClaudeResponse(true, 401, @"{""type"":""error"",""error"":{""type"":""authentication_error""}}", 95, "");
            var clBlk = EvaluateClaudeResponse(true, 403, "<html>Just a moment... Cloudflare</html>", 80, "");
            repBlocked.ClaudeApi = clBlk;
            EvaluateReport(repBlocked, iqHk, null);
            check("NQ12 EvaluateClaudeResponse 与通用评估矩阵不限定单一工具",
                clOk.Ok && !clOk.Blocked && clBlk.Blocked && !clBlk.Ok &&
                repBlocked.VerdictTitle.IndexOf("Antigravity", StringComparison.OrdinalIgnoreCase) < 0 &&
                repLow.VerdictTitle.IndexOf("Antigravity", StringComparison.OrdinalIgnoreCase) < 0,
                clOk.Detail + " / " + repBlocked.VerdictTitle);

            var repDefault = new QualityReport();
            check("NQ13 区分系统代理直连 IPv6 探测与旁路侧车/默认未检测状态（不将未测误标无泄漏）",
                !repDefault.Ipv6LeakChecked && !repDefault.DirectIpv6Leak &&
                ShouldRunHostIpv6LeakProbe(false, true, false) &&
                ShouldRunHostIpv6LeakProbe(false, false, true) &&
                !ShouldRunHostIpv6LeakProbe(true, true, true),
                "");

            return "\n[网络质量与风控 NodeQuality 自检] " + pass + " PASS / " + fail + " FAIL\n" + sb;
        }
    }
}
