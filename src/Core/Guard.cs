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
            /// <summary>目录规则（PROCESS-PATH-REGEX）里没在内核中找到的条数。</summary>
            public int MissingPathRules;
            public bool ViaRuleSet;        // 名单是靠老的 RULE-SET provider 生效的
            public bool HeadShortCircuit;  // 规则段中间出现 MATCH，后面的规则永远走不到
            public int MatchIndex = -1;    // 第一条 MATCH 的位置（-1 = 没有）
            public int MatchCount;         // MATCH 总条数（0 = 兜底缺失）
            public string MatchProxy = ""; // 第一条 MATCH 的策略（必须是 DIRECT）
            public int LastProcessIndex = -1; // 最后一条 PROCESS-NAME 的位置
            public bool FirstRuleIsMatch;   // 第一条规则就是 MATCH（空名单白名单模式）
            public bool Ipv6On;            // 内核开着 IPv6 = 流量可以从物理网卡直出
            public string FindProcess = "";
            public string Mode = "";
            public string Policy = "";
            /// <summary>内核里的规则存在、但策略与名单不一致的条数。</summary>
            public int WrongPolicy;
            public readonly List<string> WrongPolicyNames = new List<string>();
        }

        public static Intent Audit(IEnumerable<string> wanted)
        {
            return Audit(wanted, null);
        }

        /// <summary>
        /// policies：名单里每条的名字 → 期望策略。只有显式写了 DIRECT / 组名时才核对；
        /// PP-FIXED 的目标要运行时才知道，交给 Apply 的回读，不在这里猜。
        /// </summary>
        public static Intent Audit(IEnumerable<string> wanted, Dictionary<string, string> policies,
                                   string knownConfigPath = null)
        {
            var it = new Intent();
            // 收尾批 P2-1：看守对账的两次读（/configs 与 /rules）锁到同一份配置，
            // 不与 5 秒缓存窗口里的另一次 Detect 交叉。不传时保持原行为。
            var cfg = Mihomo.GetJson("/configs", 6000, knownConfigPath);
            if (cfg == null) { it.Error = "读不到内核配置"; return it; }

            it.Ipv6On = Json.Bool(cfg, "ipv6", false);
            it.FindProcess = Json.Str(cfg, "find-process-mode") ?? "";
            it.Mode = Json.Str(cfg, "mode") ?? "";

            var rules = Profile.ReadRules(knownConfigPath);
            it.RuleCount = rules.Count;
            if (rules.Count == 0) { it.Error = "内核没有返回规则表"; return it; }
            it.Ok = true;
            it.FirstRuleIsMatch = rules[0].Type != null &&
                rules[0].Type.Equals("Match", StringComparison.OrdinalIgnoreCase);

            var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var presentPolicy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < rules.Count; i++)
            {
                var r = rules[i];
                string type = r.Type ?? "";
                // PROCESS-NAME / PROCESS-PATH / PROCESS-PATH-REGEX 都算「按进程分流」，
                // 目录规则的目的是把辅助进程一起包含进来，不能在对账时被当成不存在。
                if (type.StartsWith("Process", StringComparison.OrdinalIgnoreCase))
                {
                    it.ProcessRules++;
                    it.LastProcessIndex = i;
                    if (!string.IsNullOrEmpty(r.Payload)) present.Add(r.Payload);
                    if (!string.IsNullOrEmpty(r.Payload) && !string.IsNullOrEmpty(r.Proxy))
                        presentPolicy[r.Payload] = r.Proxy;
                    if (!string.IsNullOrEmpty(r.Proxy)) it.Policy = r.Proxy;
                }
                else if (type.Equals("RuleSet", StringComparison.OrdinalIgnoreCase) &&
                         (r.Payload ?? "").IndexOf("pp-whitelist", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    it.ViaRuleSet = true;
                    if (!string.IsNullOrEmpty(r.Proxy)) it.Policy = r.Proxy;
                }
                // 记录所有 MATCH：条数、策略、第一条位置。兜底是否成立由 Judge 结合模式判定，
                // 这里只做事实采集，不替调用方下结论。
                else if (type.Equals("Match", StringComparison.OrdinalIgnoreCase))
                {
                    it.MatchCount++;
                    if (it.MatchIndex < 0) { it.MatchIndex = i; it.MatchProxy = r.Proxy ?? ""; }
                    if (i < rules.Count - 1) it.HeadShortCircuit = true;
                }
            }

            if (wanted != null)
            {
                foreach (var exe in wanted)
                {
                    string raw = (exe ?? "").Trim();
                    if (raw.Length == 0) continue;
                    // 目录规则按原样比对：它是正则，不能走「补 .exe」的名字归一化。
                    if (raw.StartsWith("(?", StringComparison.Ordinal) || raw.IndexOf('\\') >= 0)
                    {
                        if (!present.Contains(raw)) it.MissingPathRules++;
                        continue;
                    }
                    string name = Profile.Normalize(raw);
                    if (name.Length == 0) continue;
                    if (!present.Contains(name)) { it.Missing.Add(name); continue; }
                    // 规则在，但策略被改成了别的（例如期望 DIRECT，实际走了代理组）。
                    string expect;
                    if (policies != null && policies.TryGetValue(name, out expect) &&
                        !string.IsNullOrEmpty(expect) &&
                        !expect.Equals("PP-FIXED", StringComparison.OrdinalIgnoreCase))
                    {
                        string actual;
                        if (presentPolicy.TryGetValue(name, out actual) &&
                            !actual.Equals(expect, StringComparison.OrdinalIgnoreCase))
                        {
                            it.WrongPolicy++;
                            if (it.WrongPolicyNames.Count < 5) it.WrongPolicyNames.Add(name);
                        }
                    }
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
            if (it == null || !it.Ok) return null;

            // 前置条件：全局模式或关掉进程识别时，规则根本不参与匹配（评审 R1-7 / R3-5）。
            if (!string.IsNullOrEmpty(it.Mode) && !it.Mode.Equals("rule", StringComparison.OrdinalIgnoreCase))
                return "客户端当前是 " + it.Mode + " 模式，规则不生效；请切回规则模式";
            // mihomo 三档：always=强制查进程、strict=按需查（默认）、off=完全不查。
            // 段首的 PROCESS-NAME 规则在 strict 下每条连接都会触发进程查询，所以只有 off 才算没开。
            // 以前把非 always 一律当成关闭，默认 Clash Verge Rev（strict）下看守直接放弃补回（外部评审 F1）。
            if (!string.IsNullOrEmpty(it.FindProcess) &&
                it.FindProcess.Equals("off", StringComparison.OrdinalIgnoreCase))
                return "进程识别已关闭（find-process-mode=off）";

            if (patchOnly)
            {
                // 独立审阅第二轮 P1：兼容模式本来就要原样保留订阅的全部规则，
                // 订阅自带的段首 MATCH 是订阅作者写的分流语义，不是本工具的故障。
                // 以前在这里把 HeadShortCircuit 判成异常 → 每轮补回重新生成 → MATCH 仍在
                // → 下一轮又判异常 → _repairFails 累到 3 后看守停摆。这里不再因它触发修复。
                // 完全接管模式由下面的硬约束检查，那是本工具自己生成的整份规则。
                if (wantedCount > 0 && !it.ViaRuleSet && it.ProcessRules == 0)
                    return "勾选的 " + wantedCount + " 条规则一条都不在内核里";
                if (it.Missing.Count > 0)
                    return "有 " + it.Missing.Count + " 个软件不在生效名单里：" + Sample(it.Missing);
                if (it.WrongPolicy > 0)
                    return "有 " + it.WrongPolicy + " 条规则的策略被改了：" + Sample(it.WrongPolicyNames);
                if (it.MissingPathRules > 0)
                    return "有 " + it.MissingPathRules + " 条软件目录规则没生效";
                return null;
            }

            // 「完全接管」下必须是内联 PROCESS-NAME：残留的旧 RULE-SET provider 不能
            // 当作「已生效」直接放行，后面的缺项 / MATCH 顺序 / 空名单校验都要跑完（复审 P2）。
            if (wantedCount > 0 && it.ProcessRules == 0)
                return "勾选的 " + wantedCount + " 条规则一条都不在内核里";
            if (it.Missing.Count > 0) return "有 " + it.Missing.Count + " 个软件不在生效名单里：" + Sample(it.Missing);
            if (it.WrongPolicy > 0)
                return "有 " + it.WrongPolicy + " 条规则的策略被改了：" + Sample(it.WrongPolicyNames);
            if (it.MissingPathRules > 0) return "有 " + it.MissingPathRules + " 条软件目录规则没生效";
            // 独立审阅第二轮 P1：白名单的兜底 MATCH 是硬约束——必须存在、策略必须 DIRECT、
            // 位置必须在所有勾选规则之后。以前只在「恰好找到 MATCH」时查位置，
            // MATCH 消失或被改成 MATCH,PROXY 都会漏检，界面仍显示「名单已生效」。
            if (it.MatchCount == 0)
                return "缺少直连兜底 MATCH，未勾选的软件可能被代理";
            if (!string.IsNullOrEmpty(it.MatchProxy) && !Profile.IsDirectPolicy(it.MatchProxy))
                return "兜底 MATCH 的策略是 " + it.MatchProxy + "，不是 DIRECT，白名单承诺不成立";
            // 空名单：白名单模式下第一条必须是直连兜底（评审 R3-5）。
            if (wantedCount == 0 && it.MatchIndex != 0) return "空名单下缺少段首直连兜底";
            // 短路 MATCH 必须在所有 PROCESS-NAME 之后，否则勾了也直连。
            if (it.MatchIndex >= 0 && it.MatchIndex <= it.LastProcessIndex)
                return "直连兜底排在勾选规则前面";
            return null;
        }

        /// <summary>只报数量用户没法行动；附前三个名字，至少知道先看哪个。</summary>
        static string Sample(List<string> names)
        {
            if (names == null || names.Count == 0) return "";
            string s = "";
            for (int i = 0; i < names.Count && i < 3; i++)
            {
                if (s.Length > 0) s += "、";
                s += names[i];
            }
            if (names.Count > 3) s += " 等";
            return s;
        }

        /// <summary>只有用户主动开启「IPv6 防泄漏」时才会调用；默认路径不再碰这个键。</summary>
        public static string EnsureIpv6Off(string yaml) { return SetIpv6(yaml, "false"); }

        /// <summary>
        /// 关掉「IPv6 防泄漏」时把配置推回开启前的样子。
        /// 没记过原值就原样返回 —— 宁可少动，也不要凭猜测改用户的全局设置。
        /// </summary>
        public static string RestoreIpv6(string yaml, string original)
        {
            if (string.IsNullOrEmpty(original)) return yaml;
            return SetIpv6(yaml, original);
        }

        /// <summary>
        /// 设置顶层 ipv6 键。value 取 "true" / "false" / "absent"（删掉这一行）。
        /// 只动顶层键，不碰 tun / dns 下面缩进的同名项；重复调用结果不变。
        /// </summary>
        public static string SetIpv6(string yaml, string value)
        {
            if (string.IsNullOrEmpty(yaml)) return yaml;
            if (value != "true" && value != "false" && value != "absent") return yaml;
            var list = new List<string>(yaml.Replace("\r\n", "\n").Split('\n'));

            for (int i = 0; i < list.Count; i++)
            {
                if (!list[i].StartsWith("ipv6:", StringComparison.Ordinal)) continue;
                if (value == "absent") list.RemoveAt(i);
                else list[i] = "ipv6: " + value;
                return string.Join("\n", list);
            }

            if (value == "absent") return string.Join("\n", list);

            int at = -1;
            for (int i = 0; i < list.Count; i++)
                if (list[i].StartsWith("mode:", StringComparison.Ordinal)) { at = i + 1; break; }

            if (at < 0)
            {
                // 没有 mode 就放到最前面：顶层键之间没有顺序要求，放前面最安全。
                int first = 0;
                while (first < list.Count && (list[first].Trim().Length == 0 || list[first].TrimStart().StartsWith("#", StringComparison.Ordinal)))
                    first++;
                at = first;
            }
            list.Insert(at, "ipv6: " + value);
            return string.Join("\n", list);
        }

        /// <summary>读顶层 ipv6 键，给自检用；没有这一行返回 "absent"。</summary>
        public static string Ipv6Value(string yaml)
        {
            foreach (var line in (yaml ?? "").Replace("\r\n", "\n").Split('\n'))
            {
                if (!line.StartsWith("ipv6:", StringComparison.Ordinal)) continue;
                string v = line.Substring(5).Trim().ToLowerInvariant();
                return v == "true" || v == "false" ? v : "absent";
            }
            return "absent";
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
