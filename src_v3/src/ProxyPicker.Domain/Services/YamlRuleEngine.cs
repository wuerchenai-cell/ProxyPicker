using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using ProxyPicker.Domain.Json;
using ProxyPicker.Domain.Models;

namespace ProxyPicker.Domain.Services
{
    /// <summary>
    /// 纯领域 YAML 分流规则与多固定 IP 通道注入/剥离/干净还原引擎（零 IO、零 UI 依赖）。
    /// 完整保留并整合 v1–v2 全部 51 轮实战边界防护（流式 rules 闭合、缩进跟随、UDP 防穿透、还原零残留）。
    /// </summary>
    public static class YamlRuleEngine
    {
        public const string RuleBeginMarker = "# PP-BEGIN";
        public const string RuleEndMarker = "# PP-END";
        public const string StaticBeginMarker = "# PP-STATIC-BEGIN";
        public const string StaticEndMarker = "# PP-STATIC-END";

        public static bool IsManagedStaticName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            string t = name.Trim();
            return t.StartsWith("PP-STATIC-OUT", StringComparison.OrdinalIgnoreCase) ||
                   t.StartsWith("PP-STATIC-HOP", StringComparison.OrdinalIgnoreCase);
        }

        public static string NormalizeExe(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";
            string s = raw.Trim().Trim('"', '\'');
            int slash = Math.Max(s.LastIndexOf('\\'), s.LastIndexOf('/'));
            if (slash >= 0) s = s.Substring(slash + 1);
            return s.Trim().ToLowerInvariant();
        }

        public static bool IsDirectPolicy(string policy)
        {
            return !string.IsNullOrWhiteSpace(policy) &&
                   policy.Trim().Equals("DIRECT", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>生成封堵固定 IP UDP/QUIC/WebRTC 穿透直连的 AND 复合规则。</summary>
        public static string BuildUdpGuardRule(bool isPathRule, string target)
        {
            string kind = isPathRule ? "PROCESS-PATH-REGEX" : "PROCESS-NAME";
            return "AND,((NETWORK,UDP),(" + kind + "," + (target ?? "").Trim() + ")),REJECT";
        }

        /// <summary>
        /// 从内核 /rules 或 YAML 规则中识别本工具注入的固定 IP UDP/QUIC 防穿透规则，并提取目标标识。
        /// 兼容 mihomo /rules 返回的 `((Network,udp) &amp;&amp; (ProcessName,chrome.exe))` 与 YAML 原始 `((NETWORK,UDP),(PROCESS-NAME,chrome.exe))` 两种格式，
        /// 并通过括号深度匹配安全支持含括号的 ProcessPathRegex。
        /// </summary>
        public static bool TryParseUdpGuardRule(string ruleType, string payload, out bool isPathRule, out string target)
        {
            isPathRule = false;
            target = "";
            if (!string.Equals(ruleType, "AND", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(payload))
                return false;
            string s = payload.Trim();
            if (s.IndexOf("Network,udp", StringComparison.OrdinalIgnoreCase) < 0 &&
                s.IndexOf("NETWORK,UDP", StringComparison.OrdinalIgnoreCase) < 0)
                return false;

            string[] markers = { "(ProcessName,", "(PROCESS-NAME,", "(ProcessPathRegex,", "(PROCESS-PATH-REGEX," };
            foreach (var mk in markers)
            {
                int idx = s.IndexOf(mk, StringComparison.OrdinalIgnoreCase);
                if (idx < 0) continue;
                isPathRule = mk.IndexOf("PathRegex", StringComparison.OrdinalIgnoreCase) >= 0;
                int valStart = idx + mk.Length;
                int depth = 1;
                int valEnd = -1;
                for (int i = valStart; i < s.Length; i++)
                {
                    char c = s[i];
                    if (c == '\\' && i + 1 < s.Length) { i++; continue; }
                    if (c == '(') depth++;
                    else if (c == ')')
                    {
                        depth--;
                        if (depth == 0) { valEnd = i; break; }
                    }
                }
                if (valEnd > valStart)
                {
                    target = s.Substring(valStart, valEnd - valStart).Trim();
                    return target.Length > 0;
                }
            }
            return false;
        }

        /// <summary>剥离 YAML 中全部由 ProxyPicker 注入的静态通道标记块（# PP-STATIC-BEGIN .. # PP-STATIC-END）。</summary>
        public static string StripStaticMarkedBlocks(string yaml)
        {
            if (string.IsNullOrEmpty(yaml)) return yaml ?? "";
            if (yaml.IndexOf(StaticBeginMarker, StringComparison.Ordinal) < 0) return yaml;

            string norm = yaml.Replace("\r\n", "\n");
            string[] lines = norm.Split('\n');
            var kept = new List<string>(lines.Length);
            bool inside = false;
            for (int i = 0; i < lines.Length; i++)
            {
                string t = lines[i].Trim();
                if (!inside && t.StartsWith(StaticBeginMarker, StringComparison.Ordinal))
                {
                    inside = true;
                    continue;
                }
                if (inside)
                {
                    if (t.StartsWith(StaticEndMarker, StringComparison.Ordinal))
                        inside = false;
                    continue;
                }
                kept.Add(lines[i]);
            }
            return string.Join("\n", kept);
        }

        /// <summary>从客户端配置提取物理代理节点名（过滤公告行与内部托管节点），供多固定 IP 优选跳板池使用。</summary>
        public static List<string> ExtractConcreteNodeNames(string yaml, int maxCount = 160)
        {
            var res = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(yaml)) return res;

            string[] lines = yaml.Replace("\r\n", "\n").Split('\n');
            bool inProxies = false;
            for (int i = 0; i < lines.Length && res.Count < maxCount; i++)
            {
                string raw = lines[i];
                string t = raw.Trim();
                if (!inProxies)
                {
                    if (t == "proxies:" || t.StartsWith("proxies:", StringComparison.Ordinal) && !t.StartsWith("proxies: [", StringComparison.Ordinal))
                        inProxies = true;
                    continue;
                }
                if (raw.Length > 0 && raw[0] != ' ' && raw[0] != '\t' && raw[0] != '-' && !t.StartsWith("#", StringComparison.Ordinal))
                    break;
                if (t.Length == 0 || t.StartsWith("#", StringComparison.Ordinal)) continue;

                string name = ExtractYamlNameField(t);
                if (string.IsNullOrEmpty(name)) continue;
                if (IsManagedStaticName(name) || IsInformationalNodeName(name)) continue;
                if (seen.Add(name)) res.Add(name);
            }
            return res;
        }

        public static bool IsInformationalNodeName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return true;
            string s = name.Trim();
            string[] tokens =
            {
                "剩余流量", "可用流量", "套餐到期", "过期时间", "到期时间",
                "官网", "最新网址", "发布页", "重置时间", "距离下次重置",
                "群组", "客服", "频道", "续费", "Traffic:", "Expire:"
            };
            foreach (var tok in tokens)
                if (s.IndexOf(tok, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        /// <summary>从配置解析可用的主代理策略组（优先最后一条非内置 MATCH 目标，否则回退首个有效 proxy-group）。</summary>
        public static string ResolvePrimaryPolicyGroup(string yaml)
        {
            if (string.IsNullOrEmpty(yaml)) return "";
            var groups = ExtractProxyGroupNames(yaml);
            if (groups.Count == 0) return "";

            string fromMatch = FindPolicyFromRulesMatch(yaml, groups);
            if (!string.IsNullOrEmpty(fromMatch)) return fromMatch;
            return groups[0];
        }

        public static List<string> ExtractProxyGroupNames(string yaml)
        {
            var groups = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(yaml)) return groups;

            string[] lines = yaml.Replace("\r\n", "\n").Split('\n');
            bool inSection = false;
            for (int i = 0; i < lines.Length; i++)
            {
                string raw = lines[i];
                string t = raw.Trim();
                if (!inSection)
                {
                    if (t == "proxy-groups:" || t.StartsWith("proxy-groups:", StringComparison.Ordinal))
                        inSection = true;
                    continue;
                }
                if (raw.Length > 0 && raw[0] != ' ' && raw[0] != '\t' && raw[0] != '-' && !t.StartsWith("#", StringComparison.Ordinal))
                    break;
                if (t.Length == 0 || t.StartsWith("#", StringComparison.Ordinal)) continue;
                string name = ExtractYamlNameField(t);
                if (string.IsNullOrEmpty(name)) continue;
                if (IsBuiltInPolicy(name) || IsManagedStaticName(name)) continue;
                if (seen.Add(name)) groups.Add(name);
            }
            return groups;
        }

        /// <summary>向 YAML 注入多组已激活的独立固定 IP 通道（proxies 代理条目 + 各自独立 PP-STATIC-HOP* 策略组）。</summary>
        public static string InjectStaticChannels(string yaml, StaticChannelVault vault, string mainPolicyGroup)
        {
            string clean = StripStaticMarkedBlocks(yaml ?? "");
            if (vault == null) return clean;
            var active = vault.GetActiveChannels();
            if (active.Count == 0) return clean;

            var concreteNodes = ExtractConcreteNodeNames(clean, 160);
            string[] lines = clean.Replace("\r\n", "\n").Split('\n');

            // 1. 构造 proxies 注入块
            int proxiesIdx = FindTopLevelSection(lines, "proxies:");
            string proxyIndent = DetectSectionItemIndent(lines, proxiesIdx);
            var proxyLines = new List<string>
            {
                proxyIndent + StaticBeginMarker + "  ProxyPicker 托管多固定 IP 通道（退出时自动剥离）"
            };
            foreach (var ch in active)
            {
                string dialer = ResolveDialerTarget(ch, mainPolicyGroup);
                proxyLines.Add(proxyIndent + "- name: " + JsonCodec.Quote(ch.KernelProxyName));
                proxyLines.Add(proxyIndent + "  type: " + ch.Protocol);
                proxyLines.Add(proxyIndent + "  server: " + JsonCodec.Quote(ch.Host.Trim()));
                proxyLines.Add(proxyIndent + "  port: " + ch.Port);
                if (!string.IsNullOrEmpty(ch.Username))
                    proxyLines.Add(proxyIndent + "  username: " + JsonCodec.Quote(ch.Username));
                if (!string.IsNullOrEmpty(ch.Password))
                    proxyLines.Add(proxyIndent + "  password: " + JsonCodec.Quote(ch.Password));
                if (ch.Protocol == "socks5")
                    proxyLines.Add(proxyIndent + "  udp: " + (ch.AllowUdp ? "true" : "false"));
                if (!string.IsNullOrEmpty(dialer))
                    proxyLines.Add(proxyIndent + "  dialer-proxy: " + JsonCodec.Quote(dialer));
            }
            proxyLines.Add(proxyIndent + StaticEndMarker);

            // 2. 为使用 Auto 优选跳板的通道构造独立 proxy-groups 块（与主 VPN 节点完全解耦）
            var autoChannels = active.Where(c => c.HopMode == StaticHopMode.Auto).ToList();
            var groupLines = new List<string>();
            if (autoChannels.Count > 0)
            {
                int pgIdx = FindTopLevelSection(lines, "proxy-groups:");
                string pgIndent = DetectSectionItemIndent(lines, pgIdx);
                groupLines.Add(pgIndent + StaticBeginMarker + "  ProxyPicker 独立前置跳板热切组");
                foreach (var ch in autoChannels)
                {
                    var members = BuildAutoHopMembers(ch, concreteNodes, mainPolicyGroup);
                    groupLines.Add(pgIndent + "- name: " + JsonCodec.Quote(ch.KernelHopGroupName));
                    groupLines.Add(pgIndent + "  type: select");
                    groupLines.Add(pgIndent + "  proxies:");
                    foreach (var m in members)
                        groupLines.Add(pgIndent + "    - " + JsonCodec.Quote(m));
                }
                groupLines.Add(pgIndent + StaticEndMarker);
            }

            var outList = new List<string>(lines);
            // 先插 proxy-groups（位置通常在 proxies 之后，倒序插不移位前面的索引）
            if (groupLines.Count > 0)
            {
                int pgIdx = FindTopLevelSection(outList.ToArray(), "proxy-groups:");
                if (pgIdx >= 0)
                    outList.InsertRange(pgIdx + 1, groupLines);
                else
                {
                    outList.Add("proxy-groups:");
                    outList.AddRange(groupLines);
                }
            }
            int pIdxNow = FindTopLevelSection(outList.ToArray(), "proxies:");
            if (pIdxNow >= 0)
                outList.InsertRange(pIdxNow + 1, proxyLines);
            else
            {
                outList.Add("proxies:");
                outList.AddRange(proxyLines);
            }
            return string.Join("\n", outList);
        }

        /// <summary>编译最终推送给 Mihomo 的完整 YAML（白名单/兼容 + 多固定 IP 注入 + UDP/QUIC 防穿透规则）。</summary>
        public static string CompilePayload(
            string baseYaml,
            IList<AppRouteRule> rules,
            SplitMode mode,
            string mainPolicyGroup,
            StaticChannelVault vault,
            string findProcessMode = "always",
            bool allowAndUdpGuard = true)
        {
            if (string.IsNullOrEmpty(baseYaml)) throw new InvalidOperationException("订阅配置原文为空");
            bool patchOnly = mode == SplitMode.Compat;

            var cleanRules = rules != null ? rules.Where(r => r != null).ToList() : new List<AppRouteRule>();
            if (cleanRules.Any(r => r.Channel == RouteChannel.PrimaryProxy && string.IsNullOrEmpty(r.CustomPolicy)) &&
                string.IsNullOrWhiteSpace(mainPolicyGroup))
            {
                throw new InvalidOperationException("当前配置未找到可用主代理策略组，已拒绝推送以防分流失效");
            }

            // 1. 先按当前激活的多固定 IP 通道池注入 proxies / proxy-groups
            string working = InjectStaticChannels(baseYaml, vault, mainPolicyGroup);
            string[] lines = working.Replace("\r\n", "\n").Split('\n');

            // 2. 更新顶层 find-process-mode（若指定）
            if (!string.IsNullOrEmpty(findProcessMode))
                lines = SetTopLevelScalar(lines, "find-process-mode", findProcessMode);

            // 3. 定位 rules: 段（支持标准块式与流式 rules: [...]）
            int rulesIdx = FindTopLevelSection(lines, "rules:");
            if (rulesIdx < 0)
            {
                var appended = new List<string>(lines) { "rules:" };
                lines = appended.ToArray();
                rulesIdx = lines.Length - 1;
            }

            List<string> block;
            int afterRulesIdx;
            ExtractRulesBlock(lines, rulesIdx, out block, out afterRulesIdx);

            // 剥离旧 # PP-BEGIN .. # PP-END 块；完全白名单模式下同时清掉段首旧兜底 MATCH
            block = StripMarkedRuleBlock(block);
            if (!patchOnly) block = StripHeadMatch(block);
            block = TrimBlankEdges(block);

            string indent = DetectRuleIndent(block);
            var fresh = new List<string>();
            if (cleanRules.Count > 0)
            {
                fresh.Add(indent + RuleBeginMarker + "  ProxyPicker v3.0 维护规则块；块外原规则保留");
                var udpGuards = new List<string>();
                var udpSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var item in cleanRules)
                {
                    if (item.Channel == RouteChannel.Direct && string.IsNullOrEmpty(item.CustomPolicy))
                        continue;

                    bool isPath = item.IsPathRule;
                    string target = isPath ? item.PathRegex : NormalizeExe(item.Exe);
                    if (string.IsNullOrWhiteSpace(target)) continue;
                    AssertSafeRuleToken(target, isPath ? "目录正则" : "进程名");

                    StaticChannelProfile staticCh = null;
                    string pol;
                    if (!string.IsNullOrWhiteSpace(item.CustomPolicy))
                    {
                        pol = item.CustomPolicy.Trim();
                    }
                    else if (item.Channel == RouteChannel.StaticChannel)
                    {
                        staticCh = vault != null ? vault.FindById(item.StaticChannelId) : null;
                        if (staticCh != null && staticCh.IsActive())
                            pol = staticCh.KernelProxyName;
                        else
                            pol = mainPolicyGroup; // 通道停用时自动回落主代理，不漏回直连
                    }
                    else
                    {
                        pol = mainPolicyGroup;
                    }
                    AssertSafeRuleToken(pol, "策略名");

                    string ruleText = (isPath ? "PROCESS-PATH-REGEX," : "PROCESS-NAME,") + target + "," + pol;
                    fresh.Add(indent + "- " + FormatYamlRuleScalar(ruleText));

                    // 对未开启 UDP 的固定 IP 通道自动补齐防 UDP/QUIC/WebRTC 穿透 REJECT 规则
                    if (allowAndUdpGuard && staticCh != null && staticCh.IsActive() && !staticCh.AllowUdp)
                    {
                        string key = (isPath ? "P:" : "N:") + target;
                        if (udpSeen.Add(key))
                            udpGuards.Add(BuildUdpGuardRule(isPath, target));
                    }
                }
                foreach (var ur in udpGuards)
                    fresh.Add(indent + "- " + FormatYamlRuleScalar(ur));

                if (!patchOnly)
                    fresh.Add(indent + "- MATCH,DIRECT");
                fresh.Add(indent + RuleEndMarker);
            }

            var rebuilt = new List<string>();
            rebuilt.AddRange(fresh);
            rebuilt.AddRange(block);

            if (patchOnly)
            {
                if (!rebuilt.Any(IsAnyMatchRuleLine))
                    rebuilt.Add(indent + "- MATCH,DIRECT");
            }
            else if (cleanRules.Count == 0)
            {
                rebuilt.Insert(0, indent + "- MATCH,DIRECT");
            }

            var sb = new StringBuilder(working.Length + rebuilt.Count * 40);
            for (int i = 0; i < rulesIdx; i++) sb.Append(lines[i]).Append('\n');
            sb.Append("rules:\n");
            foreach (var line in rebuilt) sb.Append(line).Append('\n');
            for (int i = afterRulesIdx; i < lines.Length; i++)
            {
                sb.Append(lines[i]);
                if (i < lines.Length - 1) sb.Append('\n');
            }
            return sb.ToString();
        }

        /// <summary>退出干净还原重建：先剥离所有 # PP-STATIC-BEGIN 静态通道块，再剥离规则块并还原 find-process-mode，零残留。</summary>
        public static string BuildCleanRollback(string sourceYaml, string originalFindProcessMode = null)
        {
            string stripped = StripStaticMarkedBlocks(sourceYaml ?? "");
            return CompilePayload(
                stripped,
                new List<AppRouteRule>(),
                SplitMode.Compat,
                ResolvePrimaryPolicyGroup(stripped),
                null,
                string.IsNullOrEmpty(originalFindProcessMode) ? null : originalFindProcessMode,
                false);
        }

        // ---- 内部 YAML 解析辅助 ----

        static string ResolveDialerTarget(StaticChannelProfile ch, string mainPolicyGroup)
        {
            if (ch.HopMode == StaticHopMode.Direct) return "";
            if (ch.HopMode == StaticHopMode.Auto) return ch.KernelHopGroupName;
            if (ch.HopMode == StaticHopMode.Pin) return (ch.PinnedHopNode ?? "").Trim();
            return (mainPolicyGroup ?? "").Trim();
        }

        static List<string> BuildAutoHopMembers(StaticChannelProfile ch, List<string> concreteNodes, string mainPolicyGroup)
        {
            var members = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Action<string> add = n =>
            {
                string t = (n ?? "").Trim();
                if (t.Length > 0 && !IsManagedStaticName(t) && !IsBuiltInPolicy(t) && seen.Add(t))
                    members.Add(t);
            };
            add(ch.AutoBestHopNode);
            if (ch.AutoBestHopPool != null)
                foreach (var n in ch.AutoBestHopPool) add(n);
            foreach (var n in concreteNodes) add(n);
            if (!string.IsNullOrWhiteSpace(mainPolicyGroup) && !IsManagedStaticName(mainPolicyGroup) && seen.Add(mainPolicyGroup.Trim()))
                members.Add(mainPolicyGroup.Trim());
            if (members.Count == 0) members.Add("DIRECT");
            return members;
        }

        static int FindTopLevelSection(string[] lines, string headerPrefix)
        {
            for (int i = 0; i < lines.Length; i++)
            {
                string raw = lines[i];
                if (raw.Length == 0 || raw[0] == ' ' || raw[0] == '\t' || raw[0] == '#') continue;
                if (raw.StartsWith(headerPrefix, StringComparison.Ordinal))
                {
                    if (raw.Length == headerPrefix.Length || raw[headerPrefix.Length] == ' ' ||
                        raw[headerPrefix.Length] == '\t' || raw[headerPrefix.Length] == '#' ||
                        raw[headerPrefix.Length] == '[')
                        return i;
                }
            }
            return -1;
        }

        static string DetectSectionItemIndent(string[] lines, int sectionIdx)
        {
            if (sectionIdx < 0) return "  ";
            for (int i = sectionIdx + 1; i < lines.Length; i++)
            {
                string raw = lines[i];
                string t = raw.Trim();
                if (t.Length == 0 || t.StartsWith("#", StringComparison.Ordinal)) continue;
                if (raw[0] != ' ' && raw[0] != '\t' && raw[0] != '-') break;
                if (t.StartsWith("-", StringComparison.Ordinal))
                    return raw.Substring(0, raw.Length - raw.TrimStart().Length);
            }
            return "  ";
        }

        static string[] SetTopLevelScalar(string[] lines, string key, string value)
        {
            string prefix = key + ":";
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].Length > 0 && lines[i][0] != ' ' && lines[i][0] != '\t' &&
                    lines[i].StartsWith(prefix, StringComparison.Ordinal))
                {
                    var copy = (string[])lines.Clone();
                    copy[i] = key + ": " + value;
                    return copy;
                }
            }
            var list = new List<string>(lines.Length + 1) { key + ": " + value };
            list.AddRange(lines);
            return list.ToArray();
        }

        static void ExtractRulesBlock(string[] lines, int rulesIdx, out List<string> block, out int afterIdx)
        {
            block = new List<string>();
            string first = lines[rulesIdx].Substring("rules:".Length).Trim();
            if (first.StartsWith("[", StringComparison.Ordinal))
            {
                int close = FindFlowClose(first);
                if (close < 0) throw new FormatException("流式 rules: [...] 未闭合，拒绝改写配置以防损坏");
                string inside = first.Substring(1, close - 1).Trim();
                if (inside.Length > 0)
                {
                    foreach (var tok in SplitFlowTokens(inside))
                    {
                        string v = tok.Trim();
                        if (v.Length > 0) block.Add("- " + v);
                    }
                }
                afterIdx = rulesIdx + 1;
                return;
            }

            int p = rulesIdx + 1;
            while (p < lines.Length)
            {
                string raw = lines[p];
                string t = raw.Trim();
                bool indented = raw.Length > 0 && (raw[0] == ' ' || raw[0] == '\t');
                if (t.Length == 0 || t.StartsWith("#", StringComparison.Ordinal) || t.StartsWith("-", StringComparison.Ordinal) || indented)
                {
                    block.Add(raw);
                    p++;
                    continue;
                }
                break;
            }
            afterIdx = p;
        }

        static int FindFlowClose(string s)
        {
            bool inSingle = false, inDouble = false;
            int depth = 0;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (inSingle) { if (c == '\'') inSingle = false; continue; }
                if (inDouble) { if (c == '\\') { i++; continue; } if (c == '"') inDouble = false; continue; }
                if (c == '\'') inSingle = true;
                else if (c == '"') inDouble = true;
                else if (c == '[') depth++;
                else if (c == ']') { depth--; if (depth == 0) return i; }
            }
            return -1;
        }

        static List<string> SplitFlowTokens(string s)
        {
            var res = new List<string>();
            var cur = new StringBuilder();
            bool inSingle = false, inDouble = false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (inSingle) { cur.Append(c); if (c == '\'') inSingle = false; continue; }
                if (inDouble) { cur.Append(c); if (c == '\\' && i + 1 < s.Length) { cur.Append(s[++i]); continue; } if (c == '"') inDouble = false; continue; }
                if (c == '\'') { inSingle = true; cur.Append(c); }
                else if (c == '"') { inDouble = true; cur.Append(c); }
                else if (c == ',') { res.Add(cur.ToString()); cur.Clear(); }
                else cur.Append(c);
            }
            if (cur.Length > 0) res.Add(cur.ToString());
            return res;
        }

        static List<string> StripMarkedRuleBlock(List<string> block)
        {
            var res = new List<string>(block.Count);
            bool inside = false;
            foreach (var line in block)
            {
                string t = line.Trim();
                if (!inside && t.StartsWith(RuleBeginMarker, StringComparison.Ordinal)) { inside = true; continue; }
                if (inside) { if (t.StartsWith(RuleEndMarker, StringComparison.Ordinal)) inside = false; continue; }
                res.Add(line);
            }
            return res;
        }

        static List<string> StripHeadMatch(List<string> block)
        {
            int idx = 0;
            while (idx < block.Count)
            {
                string t = block[idx].Trim();
                if (t.Length == 0 || t.StartsWith("#", StringComparison.Ordinal)) { idx++; continue; }
                if (IsAnyMatchRuleLine(t)) { block.RemoveAt(idx); continue; }
                break;
            }
            return block;
        }

        static List<string> TrimBlankEdges(List<string> block)
        {
            int s = 0, e = block.Count - 1;
            while (s <= e && string.IsNullOrWhiteSpace(block[s])) s++;
            while (e >= s && string.IsNullOrWhiteSpace(block[e])) e--;
            var res = new List<string>();
            for (int i = s; i <= e; i++) res.Add(block[i]);
            return res;
        }

        static string DetectRuleIndent(List<string> block)
        {
            foreach (var line in block)
            {
                string ts = line.TrimStart();
                if (ts.StartsWith("-", StringComparison.Ordinal))
                    return line.Substring(0, line.Length - ts.Length);
            }
            return "  ";
        }

        static bool IsAnyMatchRuleLine(string line)
        {
            string t = (line ?? "").Trim();
            if (!t.StartsWith("-", StringComparison.Ordinal)) return false;
            string body = t.Substring(1).Trim().Trim('\'', '"');
            return body.StartsWith("MATCH,", StringComparison.OrdinalIgnoreCase);
        }

        static string FindPolicyFromRulesMatch(string yaml, List<string> validGroups)
        {
            var set = new HashSet<string>(validGroups, StringComparer.OrdinalIgnoreCase);
            string[] lines = yaml.Replace("\r\n", "\n").Split('\n');
            int rIdx = FindTopLevelSection(lines, "rules:");
            if (rIdx < 0) return "";
            string found = "";
            for (int i = rIdx + 1; i < lines.Length; i++)
            {
                string raw = lines[i];
                if (raw.Length > 0 && raw[0] != ' ' && raw[0] != '\t' && raw[0] != '-' && !raw.TrimStart().StartsWith("#", StringComparison.Ordinal))
                    break;
                string t = raw.Trim();
                if (!t.StartsWith("-", StringComparison.Ordinal)) continue;
                string body = t.Substring(1).Trim();
                int hash = body.IndexOf(" #", StringComparison.Ordinal);
                if (hash > 0) body = body.Substring(0, hash).Trim();
                body = body.Trim('\'', '"');
                if (body.StartsWith("MATCH,", StringComparison.OrdinalIgnoreCase))
                {
                    string pol = body.Substring("MATCH,".Length).Trim();
                    if (set.Contains(pol)) found = pol;
                }
            }
            return found;
        }

        static string ExtractYamlNameField(string trimmedLine)
        {
            // 支持 `- name: Foo` 与 `- {name: Foo, type: ...}`
            var m = Regex.Match(trimmedLine, @"(?:^-\s*|\{\s*)name\s*:\s*(""[^""]+""|'[^']+'|[^,}\r\n#]+)");
            if (!m.Success) return "";
            return m.Groups[1].Value.Trim().Trim('"', '\'').Trim();
        }

        static bool IsBuiltInPolicy(string name)
        {
            return name.Equals("DIRECT", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("REJECT", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("REJECT-DROP", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("GLOBAL", StringComparison.OrdinalIgnoreCase);
        }

        static void AssertSafeRuleToken(string val, string label)
        {
            if (string.IsNullOrWhiteSpace(val)) throw new InvalidOperationException(label + " 不能为空");
            for (int i = 0; i < val.Length; i++)
                if (val[i] < 0x20 || val[i] == 0x7F)
                    throw new InvalidOperationException(label + " 包含非法控制字符");
        }

        static string FormatYamlRuleScalar(string ruleText)
        {
            if (ruleText.IndexOfAny(new[] { ':', '#', '\'', '"', '[', ']', '{', '}', '(', ')', '\\', ' ' }) >= 0)
                return JsonCodec.Quote(ruleText);
            return ruleText;
        }
    }
}
