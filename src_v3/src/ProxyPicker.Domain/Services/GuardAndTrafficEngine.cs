using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using ProxyPicker.Domain.Json;
using ProxyPicker.Domain.Models;

namespace ProxyPicker.Domain.Services
{
    /// <summary>内核单条运行期规则模型。</summary>
    public sealed class KernelRuleInfo
    {
        public string Type { get; set; } = "";
        public string Payload { get; set; } = "";
        public string Proxy { get; set; } = "";
    }

    /// <summary>规则对账审计结果。</summary>
    public sealed class GuardAuditResult
    {
        public bool Ok { get; set; }
        public string Error { get; set; } = "";
        public string Mode { get; set; } = "";
        public string FindProcessMode { get; set; } = "";
        public bool TunEnabled { get; set; }
        public bool Ipv6Enabled { get; set; }
        public bool DnsEnabled { get; set; }
        public int RuleCount { get; set; }
        public int ProcessRuleCount { get; set; }
        public int MatchIndex { get; set; } = -1;
        public int MatchCount { get; set; }
        public string MatchProxy { get; set; } = "";
        public int LastWantedIndex { get; set; } = -1;
        public List<string> MissingExes { get; set; } = new List<string>();
        public int MissingPathRules { get; set; }
        public List<string> WrongPolicyNames { get; set; } = new List<string>();
        public int MissingStaticUdpGuards { get; set; }
    }

    /// <summary>纯函数内核规则对账与修复判定器。</summary>
    public static class GuardEvaluator
    {
        public static GuardAuditResult EvaluateRules(
            IList<KernelRuleInfo> kernelRules,
            IList<AppRouteRule> wantedRules,
            StaticChannelVault vault,
            bool requireUdpGuard,
            GuardAuditResult baseMeta = null)
        {
            var res = baseMeta ?? new GuardAuditResult();
            if (kernelRules == null || kernelRules.Count == 0)
            {
                res.Ok = false;
                res.Error = "内核未返回规则表";
                return res;
            }
            res.Ok = true;
            res.RuleCount = kernelRules.Count;

            var firstIndexByKey = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var firstPolicyByKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var udpGuardIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < kernelRules.Count; i++)
            {
                var kr = kernelRules[i];
                string t = kr.Type ?? "";
                string p = (kr.Payload ?? "").Trim();
                string px = (kr.Proxy ?? "").Trim();

                bool isUdpPath;
                string udpTarget;
                if (YamlRuleEngine.TryParseUdpGuardRule(t, p, out isUdpPath, out udpTarget))
                {
                    string uKey = (isUdpPath ? "P:" : "N:") + (isUdpPath ? udpTarget : YamlRuleEngine.NormalizeExe(udpTarget));
                    if (!udpGuardIndex.ContainsKey(uKey)) udpGuardIndex[uKey] = i;
                    continue;
                }

                if (t.Equals("ProcessName", StringComparison.OrdinalIgnoreCase) ||
                    t.Equals("Process", StringComparison.OrdinalIgnoreCase) ||
                    t.Equals("PROCESS-NAME", StringComparison.OrdinalIgnoreCase))
                {
                    res.ProcessRuleCount++;
                    string k = "N:" + YamlRuleEngine.NormalizeExe(p);
                    if (!firstIndexByKey.ContainsKey(k)) { firstIndexByKey[k] = i; firstPolicyByKey[k] = px; }
                }
                else if (t.Equals("ProcessPathRegex", StringComparison.OrdinalIgnoreCase) ||
                         t.Equals("PROCESS-PATH-REGEX", StringComparison.OrdinalIgnoreCase) ||
                         t.Equals("ProcessPath", StringComparison.OrdinalIgnoreCase))
                {
                    res.ProcessRuleCount++;
                    string k = "P:" + p;
                    if (!firstIndexByKey.ContainsKey(k)) { firstIndexByKey[k] = i; firstPolicyByKey[k] = px; }
                }
                else if (t.Equals("Match", StringComparison.OrdinalIgnoreCase))
                {
                    res.MatchCount++;
                    if (res.MatchIndex < 0)
                    {
                        res.MatchIndex = i;
                        res.MatchProxy = px;
                    }
                }
            }

            if (wantedRules != null)
            {
                foreach (var w in wantedRules)
                {
                    if (w == null || w.Channel == RouteChannel.Direct) continue;
                    string key = w.IsPathRule ? ("P:" + w.PathRegex) : ("N:" + YamlRuleEngine.NormalizeExe(w.Exe));
                    int idx;
                    if (!firstIndexByKey.TryGetValue(key, out idx))
                    {
                        if (w.IsPathRule) res.MissingPathRules++;
                        else res.MissingExes.Add(w.Exe);
                        continue;
                    }
                    if (idx > res.LastWantedIndex) res.LastWantedIndex = idx;

                    string actualPol = firstPolicyByKey[key];
                    if (w.Channel == RouteChannel.StaticChannel)
                    {
                        var ch = vault != null ? vault.FindById(w.StaticChannelId) : null;
                        if (ch != null && ch.IsActive())
                        {
                            if (!string.Equals(actualPol, ch.KernelProxyName, StringComparison.OrdinalIgnoreCase))
                                res.WrongPolicyNames.Add(w.Exe);
                            if (requireUdpGuard && !ch.AllowUdp)
                            {
                                int uIdx;
                                if (!udpGuardIndex.TryGetValue(key, out uIdx) || (res.MatchIndex >= 0 && uIdx > res.MatchIndex))
                                    res.MissingStaticUdpGuards++;
                            }
                        }
                    }
                    else if (YamlRuleEngine.IsDirectPolicy(actualPol))
                    {
                        res.WrongPolicyNames.Add(w.Exe);
                    }
                }
            }
            return res;
        }

        /// <summary>返回需要自动补回的具体原因；无需补回返回 null。</summary>
        public static string Judge(GuardAuditResult audit, int wantedActiveCount, SplitMode mode)
        {
            if (audit == null || !audit.Ok) return null;
            if (!string.IsNullOrEmpty(audit.Mode) && !audit.Mode.Equals("rule", StringComparison.OrdinalIgnoreCase))
                return "客户端当前为 " + audit.Mode + " 模式，按软件分流暂停";
            if (!string.IsNullOrEmpty(audit.FindProcessMode) && audit.FindProcessMode.Equals("off", StringComparison.OrdinalIgnoreCase))
                return "进程识别已关闭（find-process-mode=off）";
            if (wantedActiveCount > 0 && audit.ProcessRuleCount == 0)
                return "勾选的 " + wantedActiveCount + " 条软件规则未在内核中生效";
            if (audit.MissingExes.Count > 0)
                return "有 " + audit.MissingExes.Count + " 个软件规则缺失：" + string.Join(", ", audit.MissingExes.Take(3));
            if (audit.MissingPathRules > 0)
                return "有 " + audit.MissingPathRules + " 条目录规则未生效";
            if (audit.WrongPolicyNames.Count > 0)
                return "有 " + audit.WrongPolicyNames.Count + " 条规则策略不一致：" + string.Join(", ", audit.WrongPolicyNames.Take(3));
            if (audit.MissingStaticUdpGuards > 0)
                return "有 " + audit.MissingStaticUdpGuards + " 条固定 IP 防 UDP/QUIC 穿透规则缺失";

            if (mode == SplitMode.Whitelist)
            {
                if (audit.MatchCount == 0) return "白名单模式缺少直连兜底 MATCH,DIRECT";
                if (!string.IsNullOrEmpty(audit.MatchProxy) && !YamlRuleEngine.IsDirectPolicy(audit.MatchProxy))
                    return "兜底 MATCH 策略为 " + audit.MatchProxy + "（应为 DIRECT）";
                if (wantedActiveCount == 0 && audit.MatchIndex != 0)
                    return "空名单白名单模式缺少段首直连兜底";
                if (audit.LastWantedIndex >= 0 && audit.MatchIndex >= 0 && audit.MatchIndex < audit.LastWantedIndex)
                    return "兜底 MATCH 排在白名单进程规则之前";
            }
            return null;
        }
    }

    /// <summary>
    /// 实时三路流量聚合与连接监视明细构建器（精确区分代理/固定IP/直连，防回绕负值与旧连接误报）。
    /// </summary>
    public sealed class TrafficAggregator
    {
        readonly Dictionary<string, Tuple<long, long>> _prevBytes = new Dictionary<string, Tuple<long, long>>(StringComparer.Ordinal);
        DateTime _lastSampleUtc = DateTime.MinValue;

        public void Reset()
        {
            _prevBytes.Clear();
            _lastSampleUtc = DateTime.MinValue;
        }

        public TrafficSnapshot Aggregate(
            Dictionary<string, object> connectionsJsonRoot,
            DateTime nowUtc,
            DateTime rulesAppliedAtUtc,
            SplitMode splitMode,
            IList<AppRouteRule> activeRules,
            StaticChannelVault vault,
            bool maskHosts = true)
        {
            var snap = new TrafficSnapshot { SampledAtUtc = nowUtc };
            double dtSec = _lastSampleUtc == DateTime.MinValue ? 1.0 : Math.Max(0.2, (nowUtc - _lastSampleUtc).TotalSeconds);
            _lastSampleUtc = nowUtc;

            var ruleMap = new Dictionary<string, AppRouteRule>(StringComparer.OrdinalIgnoreCase);
            var pathRules = new List<Tuple<Regex, AppRouteRule>>();
            if (activeRules != null)
            {
                foreach (var r in activeRules)
                {
                    if (r == null) continue;
                    if (r.IsPathRule)
                    {
                        try { pathRules.Add(Tuple.Create(new Regex(r.PathRegex, RegexOptions.IgnoreCase), r)); }
                        catch { }
                    }
                    else if (!string.IsNullOrEmpty(r.Exe))
                    {
                        ruleMap[YamlRuleEngine.NormalizeExe(r.Exe)] = r;
                    }
                }
            }

            var arr = JsonCodec.Arr(connectionsJsonRoot, "connections");
            var seenIds = new HashSet<string>(StringComparer.Ordinal);
            var mismatchedSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long dProxyDown = 0, dProxyUp = 0, dStaticDown = 0, dStaticUp = 0, dDirectDown = 0, dDirectUp = 0;

            if (arr != null)
            {
                foreach (var item in arr)
                {
                    var obj = JsonCodec.AsObject(item);
                    if (obj == null) continue;
                    string id = JsonCodec.Str(obj, "id");
                    if (string.IsNullOrEmpty(id)) continue;
                    seenIds.Add(id);

                    var meta = JsonCodec.Obj(obj, "metadata");
                    string proc = YamlRuleEngine.NormalizeExe(JsonCodec.Str(meta, "process"));
                    string procPath = JsonCodec.Str(meta, "processPath");
                    if (string.IsNullOrEmpty(proc) && !string.IsNullOrEmpty(procPath))
                        proc = YamlRuleEngine.NormalizeExe(procPath);

                    string net = (JsonCodec.Str(meta, "network") ?? "").ToUpperInvariant();
                    string host = JsonCodec.Str(meta, "host");
                    if (string.IsNullOrEmpty(host)) host = JsonCodec.Str(meta, "destinationIP");
                    int dstPort = JsonCodec.Int(meta, "destinationPort", 0);
                    string rule = JsonCodec.Str(obj, "rule");
                    string rulePayload = JsonCodec.Str(obj, "rulePayload");

                    long up = JsonCodec.Long(obj, "upload", 0);
                    long dn = JsonCodec.Long(obj, "download", 0);
                    long dUp = 0, dDn = 0;
                    Tuple<long, long> prev;
                    if (_prevBytes.TryGetValue(id, out prev))
                    {
                        dUp = up >= prev.Item1 ? (up - prev.Item1) : 0;
                        dDn = dn >= prev.Item2 ? (dn - prev.Item2) : 0;
                    }
                    _prevBytes[id] = Tuple.Create(up, dn);

                    var chains = new List<string>();
                    var rawChains = JsonCodec.Arr(obj, "chains");
                    if (rawChains != null)
                        foreach (var c in rawChains)
                            if (c != null) chains.Add(c.ToString());

                    bool isRejected = chains.Any(c => c.Equals("REJECT", StringComparison.OrdinalIgnoreCase) || c.Equals("REJECT-DROP", StringComparison.OrdinalIgnoreCase));
                    bool isDirect = !isRejected && chains.Any(c => c.Equals("DIRECT", StringComparison.OrdinalIgnoreCase));
                    StaticChannelProfile matchedStatic = null;
                    if (!isRejected && !isDirect && vault != null)
                    {
                        foreach (var c in chains)
                        {
                            var found = vault.FindByKernelProxyName(c);
                            if (found != null) { matchedStatic = found; break; }
                        }
                    }
                    bool viaStatic = matchedStatic != null || chains.Any(YamlRuleEngine.IsManagedStaticName);
                    RouteChannel actualCh = viaStatic ? RouteChannel.StaticChannel : (isDirect ? RouteChannel.Direct : RouteChannel.PrimaryProxy);

                    // 预期通道判定
                    AppRouteRule expectedRule = null;
                    if (!string.IsNullOrEmpty(proc)) ruleMap.TryGetValue(proc, out expectedRule);
                    if (expectedRule == null && !string.IsNullOrEmpty(procPath))
                    {
                        foreach (var pr in pathRules)
                            if (pr.Item1.IsMatch(procPath)) { expectedRule = pr.Item2; break; }
                    }
                    RouteChannel expectedCh = expectedRule != null ? expectedRule.Channel : RouteChannel.Direct;

                    DateTime startUtc = DateTime.MinValue;
                    string startStr = JsonCodec.Str(obj, "start");
                    if (!string.IsNullOrEmpty(startStr))
                        DateTime.TryParse(startStr, null, System.Globalization.DateTimeStyles.AdjustToUniversal, out startUtc);
                    bool isLegacy = rulesAppliedAtUtc > DateTime.MinValue && startUtc > DateTime.MinValue && startUtc < rulesAppliedAtUtc;

                    // 固定 IP 预期 UDP REJECT 属于合规防穿透，绝不算错路！
                    bool expectedUdpReject = isRejected && net == "UDP" && expectedCh == RouteChannel.StaticChannel;
                    bool mismatched = false;
                    if (!isLegacy && !expectedUdpReject && !string.IsNullOrEmpty(proc) && splitMode == SplitMode.Whitelist)
                    {
                        if (expectedCh != actualCh && !isRejected)
                        {
                            mismatched = true;
                            mismatchedSet.Add(proc);
                        }
                    }

                    if (isRejected) snap.RejectedConnCount++;
                    else if (actualCh == RouteChannel.StaticChannel) { snap.StaticConnCount++; dStaticDown += dDn; dStaticUp += dUp; }
                    else if (actualCh == RouteChannel.PrimaryProxy) { snap.ProxyConnCount++; dProxyDown += dDn; dProxyUp += dUp; }
                    else { snap.DirectConnCount++; dDirectDown += dDn; dDirectUp += dUp; }

                    if (!string.IsNullOrEmpty(proc))
                    {
                        int cnt;
                        snap.ActiveConnByProcess.TryGetValue(proc, out cnt);
                        snap.ActiveConnByProcess[proc] = cnt + 1;
                    }

                    snap.Connections.Add(new ConnectionRecord
                    {
                        Id = id,
                        Process = proc,
                        ProcessPath = procPath,
                        Network = net,
                        TargetHostMasked = maskHosts ? MaskHostForDisplay(host) : host,
                        TargetPort = dstPort,
                        Rule = rule,
                        RulePayload = rulePayload,
                        ChainDisplay = chains.Count > 0 ? string.Join(" → ", chains.AsEnumerable().Reverse()) : "-",
                        ActualChannel = actualCh,
                        ExpectedChannel = expectedCh,
                        StaticChannelId = matchedStatic != null ? matchedStatic.Id : "",
                        IsRejected = isRejected,
                        IsMismatched = mismatched,
                        IsLegacyConnection = isLegacy,
                        UploadTotalBytes = up,
                        DownloadTotalBytes = dn,
                        UploadDeltaBytes = dUp,
                        DownloadDeltaBytes = dDn,
                        StartTimeUtc = startUtc
                    });
                }
            }

            // 清理已关闭连接缓存，防止长驻内存增长
            var deadIds = _prevBytes.Keys.Where(k => !seenIds.Contains(k)).ToList();
            foreach (var dk in deadIds) _prevBytes.Remove(dk);

            snap.ProxyDownBps = dProxyDown / dtSec;
            snap.ProxyUpBps = dProxyUp / dtSec;
            snap.StaticDownBps = dStaticDown / dtSec;
            snap.StaticUpBps = dStaticUp / dtSec;
            snap.DirectDownBps = dDirectDown / dtSec;
            snap.DirectUpBps = dDirectUp / dtSec;
            snap.MismatchCount = mismatchedSet.Count;
            snap.MismatchedApps = mismatchedSet.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
            return snap;
        }

        static string MaskHostForDisplay(string host)
        {
            if (string.IsNullOrWhiteSpace(host)) return "-";
            string h = host.Trim();
            if (h.StartsWith("127.", StringComparison.Ordinal) || h.Equals("localhost", StringComparison.OrdinalIgnoreCase))
                return h;
            return h; // 连接监视器在本地 UI 供用户实时排查用，导出诊断时经 RedactorService 统一脱敏
        }
    }
}
