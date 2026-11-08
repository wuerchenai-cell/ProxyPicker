using System;
using System.Collections.Generic;

namespace ProxyPicker.Core
{
    /// <summary>
    /// 「我说的」和「内核正在做的」之间的对账。
    ///
    /// 为什么需要它：界面上显示的勾选状态，来源是名单文件和上一次保存时的快照。
    /// 但 Clash 那边随时可能重新生成配置（换订阅、更新订阅、重启客户端、改一次设置），
    /// 我们插进去的规则会跟着一起消失，而界面还停在旧照片上。于是可能出现
    /// 「界面写着走代理、实际在直连」或者反过来的情况，而且完全无声。
    ///
    /// 这里只做「读」和「判断」，不修、不写。修不修、什么时候修由调用方决定，
    /// 这样读逻辑本身没有副作用，可以被自检和命令行反复调用。
    /// </summary>
    internal static class Guard
    {
        /// <summary>一次对账的结果。字段都是「内核现在的样子」，不是我们的期望。</summary>
        public sealed class Intent
        {
            public bool Ok;
            public string Error;
            public int RuleCount;
            public int ProcessRules;
            public readonly List<string> Missing = new List<string>();
            public bool ViaRuleSet;        // 名单是靠老的 RULE-SET provider 生效的
            public bool HeadShortCircuit;  // 规则段中间出现 MATCH，后面的规则永远走不到
            public int MatchIndex = -1;    // 第一条 MATCH 的位置（-1 = 没有）
            public bool Ipv6On;            // 内核开着 IPv6 = 流量可以从物理网卡直出
            public string FindProcess = "";
            public string Policy = "";
        }

        public static Intent Audit(IEnumerable<string> wanted)
        {
            var it = new Intent();
            var cfg = Mihomo.GetJson("/configs", 6000);
            if (cfg == null) { it.Error = "读不到内核配置"; return it; }

            it.Ipv6On = Json.Bool(cfg, "ipv6", false);
            it.FindProcess = Json.Str(cfg, "find-process-mode") ?? "";

            var rules = Profile.ReadRules();
            it.RuleCount = rules.Count;
            if (rules.Count == 0) { it.Error = "内核没有返回规则表"; return it; }
            it.Ok = true;

            var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < rules.Count; i++)
            {
                var r = rules[i];
                string type = r.Type ?? "";
                if (type.Equals("ProcessName", StringComparison.OrdinalIgnoreCase))
                {
                    it.ProcessRules++;
                    if (!string.IsNullOrEmpty(r.Payload)) present.Add(r.Payload);
                    if (!string.IsNullOrEmpty(r.Proxy)) it.Policy = r.Proxy;
                }
                else if (type.Equals("RuleSet", StringComparison.OrdinalIgnoreCase) &&
                         (r.Payload ?? "").IndexOf("pp-whitelist", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    it.ViaRuleSet = true;
                    if (!string.IsNullOrEmpty(r.Proxy)) it.Policy = r.Proxy;
                }
                // MATCH 是兜底，正常配置里一定在最后一条。它出现在中间（尤其第 1、2 条），
                // 说明这份配置被写成了「白名单短路面」：后面几百条订阅规则永远不会被匹配到。
                else if (type.Equals("Match", StringComparison.OrdinalIgnoreCase) && i < rules.Count - 1)
                {
                    it.HeadShortCircuit = true;
                    if (it.MatchIndex < 0) it.MatchIndex = i;
                }
            }

            if (wanted != null)
            {
                foreach (var exe in wanted)
                {
                    string name = Profile.Normalize(exe);
                    if (name.Length == 0) continue;
                    if (!present.Contains(name)) it.Missing.Add(name);
                }
            }
            return it;
        }

        /// <summary>
        /// 判断「要不要动手」，并给出一句人能看懂的原因；不该动手时返回 null。
        ///
        /// 判据是「和用户明确表达的意图直接矛盾」，而不是「看起来不太对」。宁可少修：
        /// 少修最多是晚一点生效，多修会把用户自己在客户端里做的调整抹掉。
        /// </summary>
        public static string Judge(Intent it, int wantedCount, bool patchOnly)
        {
            if (it == null || !it.Ok || wantedCount <= 0) return null;

            if (patchOnly)
            {
                // 「只改勾的」本该保留订阅的全部规则；段首那条 MATCH 会把它们全挡死。
                if (it.HeadShortCircuit && it.RuleCount > 2)
                    return "配置里有一条提前的 MATCH，把订阅规则挡住了";
                if (!it.ViaRuleSet && it.ProcessRules == 0)
                    return "勾选的 " + wantedCount + " 条规则一条都不在内核里";
                if (it.Missing.Count > 0)
                    return "有 " + it.Missing.Count + " 个软件不在生效名单里";
                return null;
            }

            // 「完全接管」下，名单要么是内联的 PROCESS-NAME，要么是老的 RULE-SET provider。
            if (it.ViaRuleSet) return null;
            if (it.ProcessRules == 0) return "勾选的 " + wantedCount + " 条规则一条都不在内核里";
            if (it.Missing.Count > 0) return "有 " + it.Missing.Count + " 个软件不在生效名单里";
            return null;
        }

        /// <summary>
        /// 把 ipv6: false 写进要推给内核的配置。
        /// 只动顶层键，不碰 tun / dns 下面的同名项；已经关着就原样返回，保证幂等。
        /// </summary>
        public static string EnsureIpv6Off(string yaml)
        {
            if (string.IsNullOrEmpty(yaml)) return yaml;
            var lines = yaml.Replace("\r\n", "\n").Split('\n');

            for (int i = 0; i < lines.Length; i++)
                if (lines[i].StartsWith("ipv6:", StringComparison.Ordinal))
                {
                    lines[i] = "ipv6: false";
                    return string.Join("\n", lines);
                }

            var list = new List<string>(lines);
            int at = -1;
            for (int i = 0; i < list.Count; i++)
                if (list[i].StartsWith("mode:", StringComparison.Ordinal)) { at = i + 1; break; }

            if (at < 0)
            {
                // 没有 mode 就放到最前面：顶层键之间没有顺序要求，放前面最安全。
                int first = 0;
                while (first < list.Count && (list[first].Trim().Length == 0 || list[first].TrimStart().StartsWith("#", StringComparison.Ordinal)))
                    first++;
                list.Insert(first, "ipv6: false");
            }
            else list.Insert(at, "ipv6: false");

            return string.Join("\n", list);
        }

        /// <summary>一句话摘要，供状态栏和诊断使用。</summary>
        public static string Describe(Intent it)
        {
            if (it == null) return "未核对";
            if (it.Error != null) return it.Error;
            return "内核 " + it.RuleCount + " 条规则 · 名单内联 " + it.ProcessRules + " 条"
                   + (it.ViaRuleSet ? " · 另有 RULE-SET" : "")
                   + (it.Missing.Count > 0 ? " · 缺 " + it.Missing.Count + " 个" : "")
                   + (it.Ipv6On ? " · IPv6 开着" : "");
        }
    }
}
