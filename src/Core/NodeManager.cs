using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace ProxyPicker.Core
{
    /// <summary>
    /// 节点管理与超时自动切换引擎：
    /// 1) 从 Clash /proxies 读取策略组下的全量代理节点、协议类型与历史延迟；
    /// 2) 识别可由 PUT /proxies/{组名} 切换的 Selector 策略组；
    /// 3) 批量测速与故障切换选优（仅在用户勾选的备选节点池中选择最低延迟可用节点）；
    /// 4) 切换节点后精确断开卡死在旧节点上的连接，避免 Electron / 浏览器持续复用半开套接字。
    /// </summary>
    internal static class NodeManager
    {
        public sealed class ProxyNodeInfo
        {
            public string Name = "";
            public string Type = "";
            public bool? Alive;
            public int HistoryDelayMs = -1;
            /// <summary>实时测速值：&gt;0 正常延迟，-1 未测，-2 本轮测速超时/不可达。</summary>
            public int LiveDelayMs = -1;
            public string LastError = "";
            public bool IsGroup;
            public bool IsCurrent;
            public bool IsCandidate;
            public string ExpectedRegion = "";

            public int EffectiveDelayMs
            {
                get { return LiveDelayMs > 0 ? LiveDelayMs : (LiveDelayMs == -2 ? -1 : HistoryDelayMs); }
            }
        }

        public sealed class SwitchableGroupInfo
        {
            public bool Ok;
            public string Error = "";
            public string ConfigPath = "";
            public string KernelMode = "rule";
            public string PolicyName = "";
            public string GroupName = "";
            public string GroupType = "";
            public string CurrentSelection = "";
            public string CurrentOutbound = "";
            public readonly List<ProxyNodeInfo> Nodes = new List<ProxyNodeInfo>();
        }

        public sealed class SwitchOutcome
        {
            public bool Ok;
            public string GroupName = "";
            public string PreviousOutbound = "";
            public string NewSelection = "";
            public string NewOutbound = "";
            public int ClosedConnections;
            public string Message = "";
        }

        public sealed class ProbeRoutingPlan
        {
            public bool Ok;
            public string Error = "";
            public string OriginalMode = "rule";
            public string TargetSwitchGroup = "";
            public string OriginalSelection = "";
            public string OriginalGlobalSelection = "";
            public bool HasOwnProxyRule;
            public bool UseGlobalSelector;
            public bool NeedsTempGlobal;
        }

        /// <summary>判断是否需要内存级临时切换为 global 才能保证在陌生机器/订阅兼容下精准穿过目标节点。</summary>
        internal static bool PlanRequiresTempGlobal(string kernelMode, bool hasOwnProxyRule, bool targetInDefaultSelector)
        {
            string m = (kernelMode ?? "").Trim().ToLowerInvariant();
            if (m == "global") return false;
            return !hasOwnProxyRule || !targetInDefaultSelector;
        }

        /// <summary>检测当前规则与策略组是否能直达目标节点；否则提供「临时 Global 隔离探测」方案。</summary>
        public static ProbeRoutingPlan InspectProbeRouting(string targetNode, string knownConfigPath = null)
        {
            string cfgPath = !string.IsNullOrEmpty(knownConfigPath) ? knownConfigPath : Paths.ClashYaml;
            var plan = new ProbeRoutingPlan();

            var cfgRes = Mihomo.FetchJson("/configs", 3500, cfgPath);
            if (!cfgRes.Ok || cfgRes.Data == null)
            {
                plan.Error = string.IsNullOrEmpty(cfgRes.Error) ? "无法连接 Clash 控制接口" : cfgRes.Error;
                return plan;
            }
            plan.OriginalMode = (Json.Str(cfgRes.Data, "mode") ?? "rule").Trim().ToLowerInvariant();

            string policy = Profile.PolicyName(cfgPath);
            var pxRes = Mihomo.FetchJson("/proxies", 4000, cfgPath);
            if (!pxRes.Ok || pxRes.Data == null)
            {
                plan.Error = string.IsNullOrEmpty(pxRes.Error) ? "无法读取 Clash 代理列表" : pxRes.Error;
                return plan;
            }
            var proxies = Json.Obj(pxRes.Data, "proxies");

            var globalObj = Json.Obj(proxies, "GLOBAL");
            if (globalObj != null) plan.OriginalGlobalSelection = Json.Str(globalObj, "now") ?? "";

            string defaultGroup = FindSwitchableGroupName(proxies, policy, plan.OriginalMode);
            var grpObj = !string.IsNullOrEmpty(defaultGroup) ? Json.Obj(proxies, defaultGroup) : null;
            if (grpObj != null) plan.OriginalSelection = Json.Str(grpObj, "now") ?? "";

            // 看默认策略组是否包含待测节点（传空表示批量全测时检查当前策略组本身是否有效）
            bool targetInDefault = !string.IsNullOrEmpty(defaultGroup) && grpObj != null;
            if (targetInDefault && !string.IsNullOrEmpty(targetNode))
            {
                targetInDefault = false;
                object rawAll;
                var arr = grpObj.TryGetValue("all", out rawAll) ? Json.AsArray(rawAll) : null;
                if (arr != null)
                {
                    foreach (var it in arr)
                    {
                        if (string.Equals(it as string, targetNode, StringComparison.OrdinalIgnoreCase))
                        { targetInDefault = true; break; }
                    }
                }
            }

            // 检查当前生效规则头是否有精确命中 proxypicker.exe 走代理组（未走全局也不靠订阅直连）
            bool hasOwnRule = false;
            var ruleRes = Mihomo.FetchJson("/rules", 3500, cfgPath);
            if (ruleRes.Ok && ruleRes.Data != null)
            {
                object rawRules;
                var arr = ruleRes.Data.TryGetValue("rules", out rawRules) ? Json.AsArray(rawRules) : null;
                if (arr != null)
                {
                    foreach (var item in arr)
                    {
                        var r = Json.AsObject(item);
                        if (r == null) continue;
                        string type = Json.Str(r, "type") ?? "";
                        string payload = Json.Str(r, "payload") ?? "";
                        string rProxy = Json.Str(r, "proxy") ?? "";
                        if (type.Equals("ProcessName", StringComparison.OrdinalIgnoreCase) &&
                            payload.Equals("proxypicker.exe", StringComparison.OrdinalIgnoreCase) &&
                            !Profile.IsBlockedPolicy(rProxy))
                        {
                            hasOwnRule = true;
                            break;
                        }
                    }
                }
            }
            plan.HasOwnProxyRule = hasOwnRule;
            plan.NeedsTempGlobal = PlanRequiresTempGlobal(plan.OriginalMode, hasOwnRule, targetInDefault);

            if ((plan.NeedsTempGlobal || string.Equals(plan.OriginalMode, "global", StringComparison.OrdinalIgnoreCase)) &&
                globalObj != null)
            {
                plan.UseGlobalSelector = true;
                plan.TargetSwitchGroup = "GLOBAL";
                if (string.IsNullOrEmpty(plan.OriginalSelection)) plan.OriginalSelection = plan.OriginalGlobalSelection;
            }
            else
            {
                plan.TargetSwitchGroup = defaultGroup;
            }

            plan.Ok = !string.IsNullOrEmpty(plan.TargetSwitchGroup);
            if (!plan.Ok) plan.Error = "未找到可切换的 Clash 策略组";
            return plan;
        }

        /// <summary>仅在内存层临时调整内核 mode（不落盘），探测完由调用方在 finally 立即恢复原值。</summary>
        public static bool SetKernelModeTemp(string mode, string knownConfigPath = null)
        {
            if (string.IsNullOrWhiteSpace(mode)) return false;
            string cfgPath = !string.IsNullOrEmpty(knownConfigPath) ? knownConfigPath : Paths.ClashYaml;
            var res = Mihomo.Request("PATCH", "/configs", "{\"mode\":" + Json.Quote(mode.Trim().ToLowerInvariant()) + "}", 3500, cfgPath);
            return res.Ok;
        }

        static readonly Regex RxJp = new Regex(@"(?<![A-Za-z])(?:JP|JPN|JAPAN|NRT|HND|KIX)(?![A-Za-z])", RegexOptions.IgnoreCase);
        static readonly Regex RxUs = new Regex(@"(?<![A-Za-z])(?:US|USA|LAX|SJC|NYC|SFO|DFW|ORD)(?![A-Za-z])", RegexOptions.IgnoreCase);
        static readonly Regex RxSeaIata = new Regex(@"(?<![A-Za-z])SEA(?![A-Za-z])", RegexOptions.IgnoreCase);
        static readonly Regex RxSg = new Regex(@"(?<![A-Za-z])(?:SG|SGP|SIN)(?![A-Za-z])", RegexOptions.IgnoreCase);
        static readonly Regex RxTw = new Regex(@"(?<![A-Za-z])(?:TW|TWN|TPE)(?![A-Za-z])", RegexOptions.IgnoreCase);
        static readonly Regex RxHk = new Regex(@"(?<![A-Za-z])(?:HK|HKG)(?![A-Za-z])", RegexOptions.IgnoreCase);
        static readonly Regex RxKr = new Regex(@"(?<![A-Za-z])(?:KR|KOR|ICN)(?![A-Za-z])", RegexOptions.IgnoreCase);
        static readonly Regex RxGb = new Regex(@"(?<![A-Za-z])(?:UK|GB|GBR|LHR)(?![A-Za-z])", RegexOptions.IgnoreCase);
        static readonly Regex RxDe = new Regex(@"(?<![A-Za-z])(?:DE|DEU)(?![A-Za-z])", RegexOptions.IgnoreCase);
        static readonly Regex RxCa = new Regex(@"(?<![A-Za-z])(?:CA|CAN|YYZ|YVR)(?![A-Za-z])", RegexOptions.IgnoreCase);
        static readonly Regex RxAu = new Regex(@"(?<![A-Za-z])(?:AU|AUS|SYD|MEL)(?![A-Za-z])", RegexOptions.IgnoreCase);
        static readonly Regex RxFr = new Regex(@"(?<![A-Za-z])(?:FR|FRA|CDG)(?![A-Za-z])", RegexOptions.IgnoreCase);
        static readonly Regex RxNl = new Regex(@"(?<![A-Za-z])(?:NL|NLD|AMS)(?![A-Za-z])", RegexOptions.IgnoreCase);
        static readonly Regex RxRu = new Regex(@"(?<![A-Za-z])(?:RU|RUS|MOW)(?![A-Za-z])", RegexOptions.IgnoreCase);

        /// <summary>从节点名提取预期两位国家/地区代码（未知返回空字符串）。</summary>
        public static string ParseRegionFromNodeName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "";
            string s = name.Trim();

            if (s.IndexOf("🇯🇵", StringComparison.Ordinal) >= 0 ||
                s.IndexOf("日本", StringComparison.Ordinal) >= 0 ||
                s.IndexOf("东京", StringComparison.Ordinal) >= 0 ||
                s.IndexOf("大阪", StringComparison.Ordinal) >= 0 ||
                s.IndexOf("Tokyo", StringComparison.OrdinalIgnoreCase) >= 0 ||
                s.IndexOf("Osaka", StringComparison.OrdinalIgnoreCase) >= 0 ||
                RxJp.IsMatch(s)) return "JP";

            if (s.IndexOf("🇺🇸", StringComparison.Ordinal) >= 0 ||
                s.IndexOf("美国", StringComparison.Ordinal) >= 0 ||
                s.IndexOf("洛杉矶", StringComparison.Ordinal) >= 0 ||
                s.IndexOf("圣何塞", StringComparison.Ordinal) >= 0 ||
                s.IndexOf("西雅图", StringComparison.Ordinal) >= 0 ||
                s.IndexOf("纽约", StringComparison.Ordinal) >= 0 ||
                s.IndexOf("硅谷", StringComparison.Ordinal) >= 0 ||
                s.IndexOf("United States", StringComparison.OrdinalIgnoreCase) >= 0 ||
                s.IndexOf("Los Angeles", StringComparison.OrdinalIgnoreCase) >= 0 ||
                s.IndexOf("San Jose", StringComparison.OrdinalIgnoreCase) >= 0 ||
                s.IndexOf("Seattle", StringComparison.OrdinalIgnoreCase) >= 0 ||
                RxUs.IsMatch(s)) return "US";

            if (s.IndexOf("🇸🇬", StringComparison.Ordinal) >= 0 ||
                s.IndexOf("新加坡", StringComparison.Ordinal) >= 0 ||
                s.IndexOf("狮城", StringComparison.Ordinal) >= 0 ||
                s.IndexOf("Singapore", StringComparison.OrdinalIgnoreCase) >= 0 ||
                RxSg.IsMatch(s)) return "SG";

            if (RxSeaIata.IsMatch(s)) return "US";

            if (s.IndexOf("🇹🇼", StringComparison.Ordinal) >= 0 ||
                s.IndexOf("台湾", StringComparison.Ordinal) >= 0 ||
                s.IndexOf("台北", StringComparison.Ordinal) >= 0 ||
                s.IndexOf("Taiwan", StringComparison.OrdinalIgnoreCase) >= 0 ||
                s.IndexOf("Taipei", StringComparison.OrdinalIgnoreCase) >= 0 ||
                RxTw.IsMatch(s)) return "TW";

            if (s.IndexOf("🇭🇰", StringComparison.Ordinal) >= 0 ||
                s.IndexOf("香港", StringComparison.Ordinal) >= 0 ||
                s.IndexOf("Hong Kong", StringComparison.OrdinalIgnoreCase) >= 0 ||
                RxHk.IsMatch(s)) return "HK";

            if (s.IndexOf("🇰🇷", StringComparison.Ordinal) >= 0 ||
                s.IndexOf("韩国", StringComparison.Ordinal) >= 0 ||
                s.IndexOf("首尔", StringComparison.Ordinal) >= 0 ||
                s.IndexOf("Korea", StringComparison.OrdinalIgnoreCase) >= 0 ||
                s.IndexOf("Seoul", StringComparison.OrdinalIgnoreCase) >= 0 ||
                RxKr.IsMatch(s)) return "KR";

            if (s.IndexOf("🇬🇧", StringComparison.Ordinal) >= 0 ||
                s.IndexOf("英国", StringComparison.Ordinal) >= 0 ||
                s.IndexOf("伦敦", StringComparison.Ordinal) >= 0 ||
                s.IndexOf("London", StringComparison.OrdinalIgnoreCase) >= 0 ||
                RxGb.IsMatch(s)) return "GB";

            if (s.IndexOf("🇩🇪", StringComparison.Ordinal) >= 0 ||
                s.IndexOf("德国", StringComparison.Ordinal) >= 0 ||
                s.IndexOf("法兰克福", StringComparison.Ordinal) >= 0 ||
                s.IndexOf("Germany", StringComparison.OrdinalIgnoreCase) >= 0 ||
                s.IndexOf("Frankfurt", StringComparison.OrdinalIgnoreCase) >= 0 ||
                RxDe.IsMatch(s)) return "DE";

            if (s.IndexOf("🇨🇦", StringComparison.Ordinal) >= 0 ||
                s.IndexOf("加拿大", StringComparison.Ordinal) >= 0 ||
                s.IndexOf("Canada", StringComparison.OrdinalIgnoreCase) >= 0 ||
                RxCa.IsMatch(s)) return "CA";

            if (s.IndexOf("🇦🇺", StringComparison.Ordinal) >= 0 ||
                s.IndexOf("澳大利亚", StringComparison.Ordinal) >= 0 ||
                s.IndexOf("澳洲", StringComparison.Ordinal) >= 0 ||
                s.IndexOf("悉尼", StringComparison.Ordinal) >= 0 ||
                s.IndexOf("Australia", StringComparison.OrdinalIgnoreCase) >= 0 ||
                s.IndexOf("Sydney", StringComparison.OrdinalIgnoreCase) >= 0 ||
                RxAu.IsMatch(s)) return "AU";

            if (s.IndexOf("🇫🇷", StringComparison.Ordinal) >= 0 ||
                s.IndexOf("法国", StringComparison.Ordinal) >= 0 ||
                s.IndexOf("巴黎", StringComparison.Ordinal) >= 0 ||
                s.IndexOf("France", StringComparison.OrdinalIgnoreCase) >= 0 ||
                RxFr.IsMatch(s)) return "FR";

            if (s.IndexOf("🇳🇱", StringComparison.Ordinal) >= 0 ||
                s.IndexOf("荷兰", StringComparison.Ordinal) >= 0 ||
                s.IndexOf("Netherlands", StringComparison.OrdinalIgnoreCase) >= 0 ||
                RxNl.IsMatch(s)) return "NL";

            if (s.IndexOf("🇷🇺", StringComparison.Ordinal) >= 0 ||
                s.IndexOf("俄罗斯", StringComparison.Ordinal) >= 0 ||
                s.IndexOf("Russia", StringComparison.OrdinalIgnoreCase) >= 0 ||
                RxRu.IsMatch(s)) return "RU";

            if (s.IndexOf("🇲🇾", StringComparison.Ordinal) >= 0 ||
                s.IndexOf("马来西亚", StringComparison.Ordinal) >= 0 ||
                s.IndexOf("Malaysia", StringComparison.OrdinalIgnoreCase) >= 0) return "MY";

            return "";
        }

        public static string RegionDisplayName(string code)
        {
            string c = (code ?? "").Trim().ToUpperInvariant();
            switch (c)
            {
                case "AUTO": return "跟节点名";
                case "ANY": return "不限地区";
                case "JP": return "日本";
                case "US": return "美国";
                case "SG": return "新加坡";
                case "TW": return "台湾";
                case "HK": return "香港";
                case "KR": return "韩国";
                case "GB":
                case "UK": return "英国";
                case "DE": return "德国";
                case "CA": return "加拿大";
                case "AU": return "澳大利亚";
                case "FR": return "法国";
                case "NL": return "荷兰";
                case "RU": return "俄罗斯";
                case "MY": return "马来西亚";
                case "CN": return "中国大陆";
                case "MO": return "中国澳门";
                default: return c;
            }
        }

        /// <summary>
        /// 定位最合适切换节点的 Selector 策略组：
        /// 沿当前生效策略（全局模式从 GLOBAL 开始，规则模式从 policy 开始）向下走，
        /// 优先取「包含真实物理代理节点的 Selector 组」，没有则退回到链上第一个 Selector。
        /// </summary>
        internal static string FindSwitchableGroupName(Dictionary<string, object> proxies, string policy, string kernelMode)
        {
            if (proxies == null) return "";
            string start = (!string.IsNullOrEmpty(kernelMode) &&
                            kernelMode.Trim().Equals("global", StringComparison.OrdinalIgnoreCase))
                ? "GLOBAL"
                : policy;
            if (string.IsNullOrEmpty(start) || !proxies.ContainsKey(start))
            {
                if (proxies.ContainsKey("PP-FIXED")) start = "PP-FIXED";
                else if (proxies.ContainsKey("GLOBAL")) start = "GLOBAL";
                else return "";
            }

            string firstSelector = "";
            string bestConcreteSelector = "";
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string cur = start;
            for (int depth = 0; depth < 8 && !string.IsNullOrEmpty(cur) && seen.Add(cur); depth++)
            {
                var g = Json.Obj(proxies, cur);
                if (g == null) break;
                string type = Json.Str(g, "type") ?? "";
                if (type.Equals("Selector", StringComparison.OrdinalIgnoreCase))
                {
                    if (firstSelector.Length == 0) firstSelector = cur;
                    if (GroupContainsConcreteNode(proxies, g)) bestConcreteSelector = cur;
                }
                string now = Json.Str(g, "now");
                if (string.IsNullOrEmpty(now)) break;
                cur = now;
            }
            if (bestConcreteSelector.Length > 0) return bestConcreteSelector;
            if (firstSelector.Length > 0) return firstSelector;
            return "";
        }

        static bool GroupContainsConcreteNode(Dictionary<string, object> proxies, Dictionary<string, object> group)
        {
            object rawAll;
            if (group == null || !group.TryGetValue("all", out rawAll)) return false;
            var arr = Json.AsArray(rawAll);
            if (arr == null) return false;
            foreach (var item in arr)
            {
                string name = item as string;
                if (string.IsNullOrEmpty(name)) continue;
                var entry = Json.Obj(proxies, name);
                string type = entry != null ? (Json.Str(entry, "type") ?? "") : "";
                if (Profile.IsBuiltinVirtualPolicy(name, type)) continue;
                if (!Profile.IsGroupType(type) && type.Length > 0) return true;
            }
            return false;
        }

        /// <summary>在给定 `/proxies` 数据上解析主策略组、全部节点及备选池勾选状态。</summary>
        internal static SwitchableGroupInfo BuildCatalogFromProxies(
            Dictionary<string, object> proxies,
            string policy,
            string kernelMode,
            ICollection<string> candidatePool)
        {
            var info = new SwitchableGroupInfo
            {
                PolicyName = policy ?? "",
                KernelMode = string.IsNullOrEmpty(kernelMode) ? "rule" : kernelMode.Trim().ToLowerInvariant()
            };
            if (proxies == null)
            {
                info.Error = "内核未返回代理列表";
                return info;
            }

            string activePolicy = info.KernelMode == "global" ? "GLOBAL"
                : (info.KernelMode == "direct" ? "DIRECT" : policy);
            var detail = Profile.ResolveOutboundDetail(proxies, activePolicy);
            info.CurrentOutbound = detail.Outbound;

            string group = FindSwitchableGroupName(proxies, policy, info.KernelMode);
            if (string.IsNullOrEmpty(group))
            {
                info.Error = info.KernelMode == "direct"
                    ? "当前处于直连模式，未启用代理组"
                    : "未找到可手动切换的 Selector 策略组";
                return info;
            }

            var gObj = Json.Obj(proxies, group);
            if (gObj == null)
            {
                info.Error = "策略组 " + group + " 不存在";
                return info;
            }

            info.GroupName = group;
            info.GroupType = Json.Str(gObj, "type") ?? "";
            info.CurrentSelection = Json.Str(gObj, "now") ?? "";

            var cand = candidatePool != null
                ? new HashSet<string>(candidatePool, StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            object rawAll;
            var arr = gObj.TryGetValue("all", out rawAll) ? Json.AsArray(rawAll) : null;
            if (arr != null)
            {
                var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in arr)
                {
                    string name = item as string;
                    if (string.IsNullOrEmpty(name) || !seenNames.Add(name)) continue;
                    // 独立固定 IP 托管节点与前置跳板组绝不混入普通节点列表及备选故障切换池
                    if (StaticProxy.IsManagedName(name)) continue;
                    var entry = Json.Obj(proxies, name);
                    string type = entry != null ? (Json.Str(entry, "type") ?? "") : "";
                    if (Profile.IsBuiltinVirtualPolicy(name, type)) continue;

                    bool isGroup = Profile.IsGroupType(type);
                    bool? alive = null;
                    if (entry != null)
                    {
                        object aliveObj;
                        if (entry.TryGetValue("alive", out aliveObj) && aliveObj is bool)
                            alive = (bool)aliveObj;
                    }
                    int hist = Profile.ExtractLastHistoryDelay(entry);
                    bool isCur = string.Equals(name, info.CurrentOutbound, StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(name, info.CurrentSelection, StringComparison.OrdinalIgnoreCase);

                    info.Nodes.Add(new ProxyNodeInfo
                    {
                        Name = name,
                        Type = type,
                        Alive = alive,
                        HistoryDelayMs = hist,
                        IsGroup = isGroup,
                        IsCurrent = isCur,
                        IsCandidate = !isGroup && cand.Contains(name),
                        ExpectedRegion = isGroup ? "" : ParseRegionFromNodeName(name)
                    });
                }
            }
            info.Ok = true;
            return info;
        }

        /// <summary>从当前 Clash 内核加载完整节点目录与选中备选池。</summary>
        public static SwitchableGroupInfo LoadCatalog(string knownConfigPath = null)
        {
            string cfgPath = !string.IsNullOrEmpty(knownConfigPath) ? knownConfigPath : Paths.ClashYaml;
            var cfgRes = Mihomo.FetchJson("/configs", 5000, cfgPath);
            if (!cfgRes.Ok || cfgRes.Data == null)
            {
                return new SwitchableGroupInfo
                {
                    ConfigPath = cfgPath ?? "",
                    Error = !string.IsNullOrEmpty(cfgRes.Error) ? cfgRes.Error : "读不到内核配置（/configs）"
                };
            }
            string mode = Json.Str(cfgRes.Data, "mode") ?? "rule";
            string policy = Profile.PolicyName(cfgPath);
            var proxiesRes = Mihomo.FetchJson("/proxies", 6000, cfgPath);
            if (!proxiesRes.Ok || proxiesRes.Data == null)
            {
                return new SwitchableGroupInfo
                {
                    ConfigPath = cfgPath ?? "",
                    KernelMode = mode,
                    PolicyName = policy,
                    Error = !string.IsNullOrEmpty(proxiesRes.Error) ? proxiesRes.Error : "读不到代理节点（/proxies）"
                };
            }
            var proxies = Json.Obj(proxiesRes.Data, "proxies");
            var info = BuildCatalogFromProxies(proxies, policy, mode, Settings.GetCandidateNodes());
            info.ConfigPath = cfgPath ?? "";
            return info;
        }

        /// <summary>
        /// 从候选池选出最适合故障切换的节点：
        /// 仅限用户已勾选的真实代理节点、排除刚刚超时的原节点、要求本次实时测速延迟 &gt; 0，
        /// 优先取延迟最低者；同延迟时优先保留符合期望地区的节点。
        /// </summary>
        public static ProxyNodeInfo PickBestFailoverCandidate(
            IList<ProxyNodeInfo> nodes,
            string currentTimedOutNode,
            string preferredRegion = null)
        {
            if (nodes == null || nodes.Count == 0) return null;
            ProxyNodeInfo best = null;
            string pref = (preferredRegion ?? "").Trim().ToUpperInvariant();
            if (pref == "AUTO" || pref == "ANY") pref = "";

            foreach (var n in nodes)
            {
                if (n == null || n.IsGroup || !n.IsCandidate) continue;
                if (!string.IsNullOrEmpty(currentTimedOutNode) &&
                    string.Equals(n.Name, currentTimedOutNode, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (n.LiveDelayMs <= 0) continue;

                if (best == null) { best = n; continue; }
                if (n.LiveDelayMs < best.LiveDelayMs) { best = n; continue; }
                if (n.LiveDelayMs == best.LiveDelayMs && pref.Length == 2)
                {
                    bool nPref = string.Equals(n.ExpectedRegion, pref, StringComparison.OrdinalIgnoreCase);
                    bool bPref = string.Equals(best.ExpectedRegion, pref, StringComparison.OrdinalIgnoreCase);
                    if (nPref && !bPref) best = n;
                }
            }
            return best;
        }

        /// <summary>按「延迟优先、同延迟优先匹配期望地区」排序返回所有可用故障切换候选（供上层顺序复核与切后验真）。</summary>
        public static List<ProxyNodeInfo> RankFailoverCandidates(
            IList<ProxyNodeInfo> nodes,
            string currentTimedOutNode,
            string preferredRegion = null)
        {
            var list = new List<ProxyNodeInfo>();
            if (nodes == null || nodes.Count == 0) return list;
            string pref = (preferredRegion ?? "").Trim().ToUpperInvariant();
            if (pref == "AUTO" || pref == "ANY") pref = "";

            foreach (var n in nodes)
            {
                if (n == null || n.IsGroup || !n.IsCandidate) continue;
                if (!string.IsNullOrEmpty(currentTimedOutNode) &&
                    string.Equals(n.Name, currentTimedOutNode, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (n.LiveDelayMs <= 0) continue;
                list.Add(n);
            }

            list.Sort((a, b) =>
            {
                int cmp = a.LiveDelayMs.CompareTo(b.LiveDelayMs);
                if (cmp != 0) return cmp;
                if (pref.Length == 2)
                {
                    bool ap = string.Equals(a.ExpectedRegion, pref, StringComparison.OrdinalIgnoreCase);
                    bool bp = string.Equals(b.ExpectedRegion, pref, StringComparison.OrdinalIgnoreCase);
                    if (ap != bp) return ap ? -1 : 1;
                }
                return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });
            return list;
        }

        /// <summary>
        /// 通过 Mihomo PUT /proxies/{group} 切换节点，并可选断开卡死在旧节点上的连接。
        /// 严格复用 knownConfigPath 遵守监听器与跨配置安全边界。
        /// </summary>
        public static SwitchOutcome SwitchProxyNode(
            string groupName,
            string targetNode,
            bool closeOldConnections,
            string oldOutboundToClose,
            string knownConfigPath = null)
        {
            var outcome = new SwitchOutcome
            {
                GroupName = groupName ?? "",
                PreviousOutbound = oldOutboundToClose ?? "",
                NewSelection = targetNode ?? ""
            };
            if (string.IsNullOrWhiteSpace(groupName) || string.IsNullOrWhiteSpace(targetNode))
            {
                outcome.Message = "策略组名或目标节点不能为空";
                return outcome;
            }

            string cfgPath = !string.IsNullOrEmpty(knownConfigPath) ? knownConfigPath : Paths.ClashYaml;
            string path = "/proxies/" + Uri.EscapeDataString(groupName);
            string body = "{\"name\":" + Json.Quote(targetNode) + "}";
            var putRes = Mihomo.Request("PUT", path, body, 5000, cfgPath);
            if (!putRes.Ok)
            {
                outcome.Message = !string.IsNullOrEmpty(putRes.Error)
                    ? putRes.Error
                    : ("切换节点失败（HTTP " + putRes.Status + "）" +
                       (!string.IsNullOrEmpty(putRes.Body) ? "：" + putRes.Body.Trim() : ""));
                return outcome;
            }

            // 回读确认选择器确实切到了目标节点
            var proxiesRes = Mihomo.FetchJson("/proxies", 5000, cfgPath);
            if (proxiesRes.Ok && proxiesRes.Data != null)
            {
                var proxies = Json.Obj(proxiesRes.Data, "proxies");
                var gObj = Json.Obj(proxies, groupName);
                string now = Json.Str(gObj, "now") ?? "";
                if (!string.Equals(now, targetNode, StringComparison.OrdinalIgnoreCase))
                {
                    outcome.Message = "内核未确认切换（当前仍为 " + (now.Length > 0 ? now : "未知") + "；该组可能不是 Selector 类型）";
                    return outcome;
                }
                var detail = Profile.ResolveOutboundDetail(proxies, groupName);
                outcome.NewOutbound = !string.IsNullOrEmpty(detail.Outbound) ? detail.Outbound : targetNode;
            }
            else
            {
                outcome.NewOutbound = targetNode;
            }

            if (closeOldConnections && !string.IsNullOrEmpty(oldOutboundToClose) &&
                !string.Equals(oldOutboundToClose, outcome.NewOutbound, StringComparison.OrdinalIgnoreCase))
            {
                outcome.ClosedConnections = CloseConnectionsOnNode(oldOutboundToClose, cfgPath);
            }

            outcome.Ok = true;
            outcome.Message = "已切到 " + targetNode;
            return outcome;
        }

        /// <summary>只关闭链路里确实经过指定旧节点的非直连连接，保留直连软件（微信/QQ/本地）。</summary>
        public static int CloseConnectionsOnNode(string oldNode, string knownConfigPath = null)
        {
            if (string.IsNullOrWhiteSpace(oldNode) || Profile.IsBlockedPolicy(oldNode)) return 0;
            string cfgPath = !string.IsNullOrEmpty(knownConfigPath) ? knownConfigPath : Paths.ClashYaml;
            var root = Mihomo.GetJson("/connections", 6000, cfgPath);
            if (root == null) return 0;
            object rawConns;
            if (!root.TryGetValue("connections", out rawConns)) return 0;
            var arr = Json.AsArray(rawConns);
            if (arr == null || arr.Count == 0) return 0;

            var ids = new List<string>();
            foreach (var item in arr)
            {
                var d = Json.AsObject(item);
                if (d == null) continue;
                string id = Json.Str(d, "id");
                if (string.IsNullOrEmpty(id)) continue;
                object rawChains;
                if (!d.TryGetValue("chains", out rawChains)) continue;
                var chains = Json.AsArray(rawChains);
                if (chains == null || chains.Count == 0) continue;

                bool hitOld = false;
                bool terminalBlocked = false;
                for (int i = 0; i < chains.Count; i++)
                {
                    string c = (chains[i] as string ?? "").Trim();
                    if (c.Length == 0) continue;
                    if (i == 0 && Profile.IsBlockedPolicy(c)) { terminalBlocked = true; break; }
                    if (string.Equals(c, oldNode, StringComparison.OrdinalIgnoreCase)) hitOld = true;
                }
                if (hitOld && !terminalBlocked) ids.Add(id);
            }

            if (ids.Count == 0) return 0;
            int closed = 0;
            var sw = Stopwatch.StartNew();
            int budgetMs = Math.Min(8000, Math.Max(2500, ids.Count * 500));
            foreach (var id in ids)
            {
                if (sw.ElapsedMilliseconds > budgetMs) break;
                var r = Mihomo.DeleteConnection(id, 2000, cfgPath);
                if (r.Ok) closed++;
            }
            return closed;
        }

        /// <summary>顺序批量测速指定节点列表（带取消与每节点上限），不抢占保存事务。</summary>
        public static List<ProxyNodeInfo> BatchMeasureNodes(
            IList<ProxyNodeInfo> targets,
            int perNodeTimeoutMs,
            Func<bool> shouldCancel,
            Action<ProxyNodeInfo> onEachDone,
            string knownConfigPath = null)
        {
            bool unreachable;
            return BatchMeasureNodes(targets, perNodeTimeoutMs, shouldCancel, onEachDone, knownConfigPath, out unreachable);
        }

        /// <summary>顺序批量测速指定节点列表（带取消、内核失联标志与状态清理），不抢占保存事务。</summary>
        public static List<ProxyNodeInfo> BatchMeasureNodes(
            IList<ProxyNodeInfo> targets,
            int perNodeTimeoutMs,
            Func<bool> shouldCancel,
            Action<ProxyNodeInfo> onEachDone,
            string knownConfigPath,
            out bool kernelUnreachable)
        {
            kernelUnreachable = false;
            var results = new List<ProxyNodeInfo>();
            if (targets == null || targets.Count == 0) return results;
            string cfgPath = !string.IsNullOrEmpty(knownConfigPath) ? knownConfigPath : Paths.ClashYaml;
            int budget = Math.Max(1500, Math.Min(6000, perNodeTimeoutMs));

            foreach (var node in targets)
            {
                if (node == null || node.IsGroup) continue;
                if (shouldCancel != null && shouldCancel()) break;

                var d = Profile.MeasureDelay(node.Name, Settings.DelayTestUrl, budget, cfgPath);
                if (d.KernelUnreachable)
                {
                    kernelUnreachable = true;
                    // 内核失联时同步将当前项标为失效（-2 / false），避免 UI 继续显示测前遗留的绿字旧延迟
                    node.LiveDelayMs = -2;
                    node.Alive = false;
                    node.LastError = d.Error;
                    results.Add(node);
                    if (onEachDone != null) onEachDone(node);
                    break;   // 内核失联，立刻停手
                }
                if (d.Ok && d.DelayMs > 0)
                {
                    node.LiveDelayMs = d.DelayMs;
                    node.HistoryDelayMs = d.DelayMs;
                    node.Alive = true;
                    node.LastError = "";
                }
                else
                {
                    node.LiveDelayMs = -2;
                    node.Alive = false;
                    node.LastError = d.Error ?? "超时";
                }
                results.Add(node);
                if (onEachDone != null) onEachDone(node);
            }
            return results;
        }

        /// <summary>并发预筛自动切换候选池（控制度 4），大幅缩短大量超时节点引起的阻塞延迟窗口。</summary>
        public static bool BatchMeasureFailoverPool(
            IList<ProxyNodeInfo> pool,
            int perNodeTimeoutMs,
            Func<bool> shouldCancel,
            string knownConfigPath)
        {
            if (pool == null || pool.Count == 0) return false;
            string cfgPath = !string.IsNullOrEmpty(knownConfigPath) ? knownConfigPath : Paths.ClashYaml;
            int budget = Math.Max(2000, Math.Min(4500, perNodeTimeoutMs));
            int hitUnreachable = 0;

            System.Threading.Tasks.Parallel.ForEach(
                pool,
                new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = 4 },
                (node, state) =>
                {
                    if (node == null || node.IsGroup) return;
                    if (System.Threading.Volatile.Read(ref hitUnreachable) != 0 ||
                        (shouldCancel != null && shouldCancel()))
                    {
                        state.Stop();
                        return;
                    }

                    var d = Profile.MeasureDelay(node.Name, Settings.DelayTestUrl, budget, cfgPath);
                    if (d.KernelUnreachable)
                    {
                        System.Threading.Interlocked.Exchange(ref hitUnreachable, 1);
                        node.LiveDelayMs = -2;
                        node.Alive = false;
                        node.LastError = d.Error;
                        state.Stop();
                        return;
                    }
                    if (d.Ok && d.DelayMs > 0)
                    {
                        node.LiveDelayMs = d.DelayMs;
                        node.HistoryDelayMs = d.DelayMs;
                        node.Alive = true;
                        node.LastError = "";
                    }
                    else
                    {
                        node.LiveDelayMs = -2;
                        node.Alive = false;
                        node.LastError = d.Error ?? "超时";
                    }
                });
            return hitUnreachable != 0;
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

            // 1. Region tag parsing without substring false positives (含法国 FRA vs 德国法兰克福消歧)
            check("NM1 节点名地区解析（日本/美国/新加坡/台湾/香港/韩国/法德FRA消歧/假阳性拦截）",
                ParseRegionFromNodeName("🇯🇵日本-B(流量)") == "JP" &&
                ParseRegionFromNodeName("🇺🇸 LosAngeles VLESS 02") == "US" &&
                ParseRegionFromNodeName("🇸🇬 狮城 SG-01") == "SG" &&
                ParseRegionFromNodeName("台北 TW-Relay") == "TW" &&
                ParseRegionFromNodeName("HK-BGP-03") == "HK" &&
                ParseRegionFromNodeName("🇰🇷 首尔 KR1") == "KR" &&
                ParseRegionFromNodeName("🇫🇷 法国 FRA") == "FR" &&
                ParseRegionFromNodeName("FRA-01") == "FR" &&
                ParseRegionFromNodeName("🇩🇪 法兰克福 01") == "DE" &&
                ParseRegionFromNodeName("德国 FRA-BGP") == "DE" &&
                ParseRegionFromNodeName("Frankfurt-01") == "DE" &&
                ParseRegionFromNodeName("Singapore SEA-01") == "SG" &&
                ParseRegionFromNodeName("SEA-01") == "US" &&
                ParseRegionFromNodeName("🇬🇧 伦敦 UK1") == "GB" &&
                ParseRegionFromNodeName("🇦🇺 悉尼 AU1") == "AU" &&
                ParseRegionFromNodeName("Focus-VIP-01") == "" &&
                ParseRegionFromNodeName("Russia-MOW") == "RU",
                "Focus=" + ParseRegionFromNodeName("Focus-VIP-01") +
                " RU=" + ParseRegionFromNodeName("Russia-MOW") +
                " FRA01=" + ParseRegionFromNodeName("FRA-01") +
                " DE_FRA=" + ParseRegionFromNodeName("德国 FRA-BGP"));

            // 2. Proxies catalog & switchable selector discovery (nested PP-FIXED -> 主组)
            string mockJson = @"{
                ""proxies"": {
                    ""GLOBAL"": { ""type"": ""Selector"", ""now"": ""🚀 节点选择"", ""all"": [""🚀 节点选择"", ""DIRECT""] },
                    ""PP-FIXED"": { ""type"": ""Selector"", ""now"": ""🚀 节点选择"", ""all"": [""🚀 节点选择"", ""♻️ 自动选择"", ""DIRECT""] },
                    ""🚀 节点选择"": { ""type"": ""Selector"", ""now"": ""♻️ 自动选择"", ""all"": [""♻️ 自动选择"", ""DIRECT"", ""REJECT"", ""PASS"", ""🇯🇵日本-A"", ""🇺🇸美国-B"", ""🇸🇬新加坡-C""] },
                    ""♻️ 自动选择"": { ""type"": ""URLTest"", ""now"": ""🇯🇵日本-A"", ""all"": [""🇯🇵日本-A"", ""🇺🇸美国-B""] },
                    ""🇯🇵日本-A"": { ""type"": ""AnyTLS"", ""alive"": true, ""history"": [{""delay"": 95}] },
                    ""🇺🇸美国-B"": { ""type"": ""Hysteria2"", ""alive"": true, ""history"": [{""delay"": 180}] },
                    ""🇸🇬新加坡-C"": { ""type"": ""Vless"", ""alive"": false, ""history"": [] },
                    ""DIRECT"": { ""type"": ""Direct"" },
                    ""REJECT"": { ""type"": ""Reject"" },
                    ""PASS"": { ""type"": ""Pass"" }
                }
            }";
            var root = Json.AsObject(Json.Parse(mockJson));
            var proxies = Json.Obj(root, "proxies");

            var catRule = BuildCatalogFromProxies(proxies, "PP-FIXED", "rule", new[] { "🇺🇸美国-B", "🇸🇬新加坡-C" });
            check("NM2 嵌套策略组穿透到包含物理节点的 Selector 并过滤内置策略",
                catRule.Ok &&
                catRule.GroupName == "🚀 节点选择" &&
                catRule.CurrentOutbound == "🇯🇵日本-A" &&
                catRule.Nodes.Count == 4 &&
                catRule.Nodes[0].IsGroup &&
                catRule.Nodes[1].Name == "🇯🇵日本-A" && catRule.Nodes[1].HistoryDelayMs == 95 && catRule.Nodes[1].IsCurrent &&
                catRule.Nodes[2].IsCandidate && !catRule.Nodes[1].IsCandidate && catRule.Nodes[3].IsCandidate,
                "grp=" + catRule.GroupName + " out=" + catRule.CurrentOutbound + " cnt=" + catRule.Nodes.Count);

            var catGlobal = BuildCatalogFromProxies(proxies, "PP-FIXED", "global", new string[0]);
            check("NM3 全局模式下正确沿 GLOBAL 解析可切换组",
                catGlobal.Ok && catGlobal.GroupName == "🚀 节点选择" && catGlobal.KernelMode == "global",
                "grp=" + catGlobal.GroupName);

            // 3. Failover candidate picking invariants
            var candList = new List<ProxyNodeInfo>
            {
                new ProxyNodeInfo { Name = "🇯🇵日本-A", IsCandidate = true, LiveDelayMs = 40 },    // 当前已超时的原节点必须跳过
                new ProxyNodeInfo { Name = "未勾选更快", IsCandidate = false, LiveDelayMs = 15 },  // 未在备选池禁止选择
                new ProxyNodeInfo { Name = "♻️ 自动组", IsGroup = true, IsCandidate = true, LiveDelayMs = 10 }, // 策略组不可当单节点
                new ProxyNodeInfo { Name = "🇸🇬超时", IsCandidate = true, LiveDelayMs = -2 },      // 测速失败不可选
                new ProxyNodeInfo { Name = "🇺🇸美国-B", IsCandidate = true, LiveDelayMs = 185, ExpectedRegion = "US" },
                new ProxyNodeInfo { Name = "🇯🇵日本-C", IsCandidate = true, LiveDelayMs = 92, ExpectedRegion = "JP" }
            };
            var picked = PickBestFailoverCandidate(candList, "🇯🇵日本-A", "US");
            var sameDelayTie = new List<ProxyNodeInfo>
            {
                new ProxyNodeInfo { Name = "🇯🇵日本-1", IsCandidate = true, LiveDelayMs = 100, ExpectedRegion = "JP" },
                new ProxyNodeInfo { Name = "🇫🇷 法国 FRA-01", IsCandidate = true, LiveDelayMs = 100, ExpectedRegion = ParseRegionFromNodeName("🇫🇷 法国 FRA-01") }
            };
            var tiePickedFr = PickBestFailoverCandidate(sameDelayTie, "其他", "FR");
            var ranked = RankFailoverCandidates(candList, "🇯🇵日本-A", "US");
            check("NM4 Failover 严格只从勾选备选池选最低延迟活节点并跳过当前节点（同延迟优选期望地区 + 候选有序排列）",
                picked != null && picked.Name == "🇯🇵日本-C" &&
                tiePickedFr != null && tiePickedFr.Name == "🇫🇷 法国 FRA-01" &&
                ranked.Count == 2 && ranked[0].Name == "🇯🇵日本-C" && ranked[1].Name == "🇺🇸美国-B",
                "picked=" + (picked == null ? "null" : picked.Name) + " tie=" + (tiePickedFr == null ? "null" : tiePickedFr.Name));

            var noneAlive = PickBestFailoverCandidate(new List<ProxyNodeInfo>
            {
                new ProxyNodeInfo { Name = "🇺🇸美国-B", IsCandidate = true, LiveDelayMs = -2 },
                new ProxyNodeInfo { Name = "未选", IsCandidate = false, LiveDelayMs = 20 }
            }, "🇯🇵日本-A");
            check("NM5 备选池全超时时不误切未勾选或失败节点", noneAlive == null, "");

            // 4. Settings roundtrip for failover / candidateNodes / desiredRegion
            var sdata = new Settings.Data
            {
                Mode = Settings.ModeWhitelist,
                AutoFailover = true,
                DesiredRegion = "JP"
            };
            sdata.CandidateNodes.Add("🇯🇵日本-A");
            sdata.CandidateNodes.Add("🇺🇸美国-B");
            string sjson = Settings.SerializeData(sdata);
            var reparsed = Settings.ParseDataJson(sjson);
            check("NM6 Settings.AutoFailover / DesiredRegion / CandidateNodes 序列化往返",
                reparsed.AutoFailover && reparsed.DesiredRegion == "JP" &&
                reparsed.CandidateNodes.Count == 2 && reparsed.CandidateNodes[0] == "🇯🇵日本-A",
                "cnt=" + reparsed.CandidateNodes.Count + " af=" + reparsed.AutoFailover + " reg=" + reparsed.DesiredRegion);

            check("NM7 PlanRequiresTempGlobal 对陌生机/兼容模式启用临时 Global 并保护已有规则机",
                !PlanRequiresTempGlobal("global", false, false) &&
                !PlanRequiresTempGlobal("rule", true, true) &&
                PlanRequiresTempGlobal("rule", false, true) &&
                PlanRequiresTempGlobal("rule", true, false),
                "");

            return "\n[节点管理 NodeManager 自检] " + pass + " PASS / " + fail + " FAIL\n" + sb;
        }
    }
}
