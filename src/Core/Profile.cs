using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ProxyPicker.Core
{
    /// <summary>
    /// 唯一勾选名单的读写，以及「名单 -> 内联规则 -> 配置全文」的生成。
    ///
    /// 设计约束（都是实测踩出来的，不要随意改动）：
    ///  1. 只维护这一份名单，不引入第二份「直连清单」。
    ///  2. 未出现在名单里的软件，落到 rules 段首的 MATCH,DIRECT，即默认直连。
    ///  3. 内核不重读 file 型 provider（PUT /providers/rules 只改 updatedAt，ruleCount 不变），
    ///     所以真正生效靠「把名单内联成 PROCESS-NAME 规则，再 PUT /configs 推全文」。
    ///     实测这条路不断已建立的长连接。
    ///  4. 规则插入位置和策略名都不写死，由 Discovery 按当前配置现场决定，换客户端也能用。
    /// </summary>
    internal static class Profile
    {
        public sealed class Item
        {
            public string Exe;
            public string Policy;   // PP-FIXED / DIRECT
            public Item(string exe, string policy) { Exe = exe; Policy = policy; }
        }

        public sealed class ApplyOutcome
        {
            public bool Ok;
            public string Message;
            public int RuleCount;
            public long Ms;
            public string Policy = "";
            public bool Ipv6Off;    // 回读确认 ipv6 已经是 false
        }

        const string Header =
            "# ProxyPicker 唯一勾选清单（由面板自动改写，手改也可）\n" +
            "#\n" +
            "# 语义：PROCESS-NAME,<exe 名>,<策略>；PP-FIXED=走固定节点，DIRECT=直连。\n" +
            "# 未出现在本清单的软件，落到 rules 段首的 MATCH,DIRECT（默认直连）。\n" +
            "#\n" +
            "# 注意：匹配粒度是「可执行文件名」，同一 exe 的所有进程一起分流；\n" +
            "#       msedge.exe 不等于 msedgewebview2.exe，勾 Edge 不覆盖 WebView2 宿主。\n";

        // ---------------------------------------------------------------- 读

        public static List<Item> Load()
        {
            var items = new List<Item>();
            string path = Paths.ListPath;
            if (!File.Exists(path)) return items;
            string[] lines;
            try { lines = File.ReadAllLines(path, Encoding.UTF8); }
            catch { return items; }

            bool inPayload = false;
            foreach (var raw in lines)
            {
                string s = raw.Trim();
                if (s.StartsWith("payload:", StringComparison.Ordinal)) { inPayload = true; continue; }
                if (!inPayload || !s.StartsWith("-", StringComparison.Ordinal)) continue;
                string body = s.Substring(1).Trim().Trim('\'', '"');
                string[] parts = body.Split(',');
                if (parts.Length >= 3 && parts[0].Trim().Equals("PROCESS-NAME", StringComparison.OrdinalIgnoreCase))
                    items.Add(new Item(parts[1].Trim(), parts[2].Trim().ToUpperInvariant()));
            }
            return items;
        }

        public static Dictionary<string, string> LoadPolicyMap()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var it in Load()) map[it.Exe] = it.Policy;
            return map;
        }

        // ---------------------------------------------------------------- 写

        public static void Save(List<Item> items)
        {
            string path = Paths.ListPath;
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var sb = new StringBuilder();
            sb.Append(Header);
            sb.Append("payload:\n");
            foreach (var it in items)
                sb.Append("  - 'PROCESS-NAME,").Append(it.Exe).Append(',').Append(it.Policy).Append("'\n");

            // UTF-8 无 BOM + LF：与 Python 侧工具写的格式保持一致
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }

        /// <summary>
        /// 把勾选结果整理成名单。
        ///
        /// 只写「要代理」的项：未出现在名单里的软件由 rules 段首的 MATCH,DIRECT 兜底。
        /// 这样名单永远只反映「我要代理谁」，不会因为每次扫描到新进程就越来越长。
        /// 所有软件一视同仁，没有强制保护项；WorkBuddy 也可以自由切换。
        /// </summary>
        public static List<Item> Compose(IEnumerable<string> proxy, IEnumerable<string> direct)
        {
            var items = new List<Item>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in proxy)
            {
                string exe = Normalize(name);
                if (exe.Length == 0 || !seen.Add(exe)) continue;
                items.Add(new Item(exe, "PP-FIXED"));
            }
            // direct 项不写入：MATCH,DIRECT 已经是兜底，显式写 DIRECT 只会让名单变长。
            return items;
        }

        public static string Normalize(string name)
        {
            string n = (name ?? "").Trim();
            if (n.Length == 0) return "";
            if (!n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) n += ".exe";
            return n;
        }

        // ---------------------------------------------------------------- 生成 payload

        /// <summary>按当前环境生成完整配置文本。</summary>
        public static string BuildPayload(IList<Item> items)
        {
            if (items == null) items = new List<Item>();
            return BuildPayload(Paths.ClashYaml, items);
        }

        /// <summary>
        /// 读 baseline，把规则写进 rules 段首。
        /// 写多少条由设置里的分流模式决定：完全接管 = 名单 + MATCH,DIRECT；
        /// 只改勾的 = 名单插在最前面，订阅原规则原样保留。
        /// baseline 只是文本来源；规则插入与策略名解析都交给 Discovery 做通用处理。
        /// </summary>
        public static string BuildPayload(string baselinePath, IList<Item> items)
        {
            if (string.IsNullOrEmpty(baselinePath) || !File.Exists(baselinePath))
                throw new FileNotFoundException("没有找到 Clash / mihomo 的配置文件；请先启动客户端");

            // 统一成 LF：Python 版读文件时做了 universal newline，两边保持一致
            string text = File.ReadAllText(baselinePath, Encoding.UTF8).Replace("\r\n", "\n");
            bool reused;
            string policy = Discovery.FindPolicy(text, out reused);
            string payload = Discovery.BuildPayload(text, items ?? new List<Item>(), policy, Settings.PatchOnly);

            // IPv6 不跟着订阅走：它原来只写在某一版订阅的扩展配置里，一换订阅就丢，
            // 而丢了之后完全看不出来——规则都正常，只是 IPv6 流量从物理网卡直出了。
            // 所以放到「推给内核的这份配置」里最稳：换订阅、重启客户端都不会影响它。
            // 注意只加在真实推送路径上，--dryrun 的逐字节基线不受影响。
            return Guard.EnsureIpv6Off(payload);
        }

        /// <summary>当前配置里会用的策略名（界面显示用）。</summary>
        public static string PolicyName()
        {
            try
            {
                string p = Paths.ClashYaml;
                if (string.IsNullOrEmpty(p) || !File.Exists(p)) return "GLOBAL";
                bool reused;
                return Discovery.FindPolicy(File.ReadAllText(p, Encoding.UTF8), out reused);
            }
            catch { return "GLOBAL"; }
        }

        // ---------------------------------------------------------------- 应用

        /// <summary>写名单 -> 生成 payload -> 推给内核 -> 回读规则表校验。</summary>
        public static ApplyOutcome Apply(List<Item> items)
        {
            var result = new ApplyOutcome();
            var sw = System.Diagnostics.Stopwatch.StartNew();

            string cfg = Paths.ClashYaml;
            if (string.IsNullOrEmpty(cfg) || !File.Exists(cfg))
            {
                result.Ok = false;
                result.Message = "没找到 Clash / mihomo 的配置文件；请先启动客户端再保存";
                result.Ms = sw.ElapsedMilliseconds;
                return result;
            }

            BackupOnce(cfg);

            string payload;
            try
            {
                payload = BuildPayload(cfg, items);
            }
            catch (Exception ex)
            {
                result.Ok = false;
                result.Message = "生成配置失败：" + ex.Message;
                result.Ms = sw.ElapsedMilliseconds;
                return result;
            }

            result.Policy = PolicyName();
            try { Save(items); } catch { }

            var push = Mihomo.PushConfig(payload);
            if (!push.Ok)
            {
                result.Ok = false;
                result.Message = "推送失败：" + (push.Error ?? ("HTTP " + push.Status + " " + Trim(push.Body, 160)));
                result.Ms = sw.ElapsedMilliseconds;
                return result;
            }

            // 回读校验：前 N 条规则必须和名单逐条一致
            var rules = ReadRules();
            result.RuleCount = rules.Count;
            bool good = rules.Count >= items.Count;
            if (good)
            {
                for (int i = 0; i < items.Count; i++)
                {
                    string got = rules[i].Payload ?? "";
                    if (!got.Equals(items[i].Exe, StringComparison.OrdinalIgnoreCase)) { good = false; break; }
                }
            }
            result.Ok = good;
            result.Message = good
                ? "已生效 " + items.Count + " 条规则"
                : "已推送但校验不一致，请点刷新核对";

            // 推送成功不代表 IPv6 真的关了：内核有可能忽略这个键。既然它坏了界面看不出来，
            // 就在这里当场回读一次，宁可把不确定说出来，也不假装成功。
            try
            {
                var after = Mihomo.GetJson("/configs", 4000);
                if (after != null && Json.Bool(after, "ipv6", false))
                    result.Message += "（IPv6 仍开着，可能被客户端配置覆盖）";
                else if (after != null)
                    result.Ipv6Off = true;
            }
            catch { }

            result.Ms = sw.ElapsedMilliseconds;
            return result;
        }

        // ---------------------------------------------------------------- 备份与回滚

        /// <summary>第一次接管前留一份原件，回滚时能推回去。</summary>
        public static void BackupOnce(string configPath)
        {
            try
            {
                string dir = Paths.BackupDir;
                string stamp = File.Exists(Path.Combine(dir, "original.yaml")) ? null : "original.yaml";
                if (stamp == null) return;
                File.Copy(configPath, Path.Combine(dir, stamp), true);
            }
            catch { }
        }

        /// <summary>
        /// 一条命令回滚：把最初的配置原样推回内核，并清空勾选名单。
        /// 返回是否成功。内核会重新接管全部流量，行为退回订阅原样。
        /// </summary>
        public static bool Rollback(out string message)
        {
            try
            {
                string original = Path.Combine(Paths.BackupDir, "original.yaml");
                if (!File.Exists(original))
                {
                    message = "没有找到备份（本程序还没在这台机器上保存过）";
                    return false;
                }
                string text = File.ReadAllText(original, Encoding.UTF8);
                var push = Mihomo.PushConfig(text);
                if (!push.Ok)
                {
                    message = "推送原件失败：" + (push.Error ?? ("HTTP " + push.Status));
                    return false;
                }
                try { Save(new List<Item>()); } catch { }
                message = "已还原为最初的配置";
                return true;
            }
            catch (Exception ex)
            {
                message = "回滚失败：" + ex.Message;
                return false;
            }
        }

        public sealed class RuleInfo
        {
            public string Type;
            public string Payload;
            public string Proxy;
        }

        public static List<RuleInfo> ReadRules()
        {
            var list = new List<RuleInfo>();
            var obj = Mihomo.GetJson("/rules");
            if (obj == null) return list;
            object raw;
            if (!obj.TryGetValue("rules", out raw)) return list;
            var arr = Json.AsArray(raw);
            if (arr == null) return list;
            foreach (var o in arr)
            {
                var d = Json.AsObject(o);
                if (d == null) continue;
                list.Add(new RuleInfo
                {
                    Type = Json.Str(d, "type") ?? "",
                    Payload = Json.Str(d, "payload") ?? "",
                    Proxy = Json.Str(d, "proxy") ?? ""
                });
            }
            return list;
        }

        /// <summary>内核当前实际运行的节点（GLOBAL 当前选择）。</summary>
        public static string CurrentNode()
        {
            var obj = Mihomo.GetJson("/proxies");
            if (obj == null) return "";
            var proxies = Json.Obj(obj, "proxies");
            var global = Json.Obj(proxies, "GLOBAL");
            string now = Json.Str(global, "now");
            return now ?? "";
        }

        /// <summary>
        /// 解析「勾选软件实际走哪个节点」。
        ///
        /// PP-FIXED 现在指向主选择组（🚀 节点选择），所以不能只看 GLOBAL ——
        /// 必须沿 now 一路往下追，直到追到一个没有 now 字段的真节点。
        /// 追不到就返回空，界面会自动隐藏这一行。
        /// </summary>
        public static string ResolveOutbound()
        {
            string global;
            return ResolveOutbound(out global);
        }

        public static string ResolveOutbound(out string global)
        {
            global = "";
            var obj = Mihomo.GetJson("/proxies");
            if (obj == null) return "";
            var proxies = Json.Obj(obj, "proxies");
            if (proxies == null) return "";

            var g = Json.Obj(proxies, "GLOBAL");
            global = Json.Str(g, "now") ?? "";

            object raw;
            string name = proxies.TryGetValue("PP-FIXED", out raw) ? "PP-FIXED" : "GLOBAL";

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (!string.IsNullOrEmpty(name) && seen.Add(name))
            {
                var node = Json.Obj(proxies, name);
                if (node == null) break;
                string now = Json.Str(node, "now");
                if (string.IsNullOrEmpty(now)) return name;   // 已经是真节点
                name = now;
            }
            return name ?? "";
        }

        public sealed class KernelState
        {
            public string Node = "";        // GLOBAL 的选择
            public string Outbound = "";    // 勾选软件实际走的节点（PP-FIXED 链路末端）
            public string Mode = "?";
            public string FindProcess = "?";
            public bool Tun;
            public bool Applied;
            public int RuleCount;
            public string Error;
            public string Policy = "";
            public string Client = "";
        }

        public static KernelState ReadKernelState()
        {
            var st = new KernelState();
            var env = Discovery.Detect();
            st.Client = env.Client;

            var cfg = Mihomo.GetJson("/configs");
            if (cfg == null)
            {
                st.Error = env.Ok
                    ? "读不到内核配置（客户端可能没启动）"
                    : "没检测到 Clash / mihomo 客户端";
                return st;
            }
            st.Mode = Json.Str(cfg, "mode") ?? "?";
            st.FindProcess = Json.Str(cfg, "find-process-mode") ?? "?";
            var tun = Json.Obj(cfg, "tun");
            st.Tun = Json.Bool(tun, "enable", false);

            string global;
            st.Outbound = ResolveOutbound(out global);
            st.Node = global;
            st.Policy = PolicyName();

            var rules = ReadRules();
            st.RuleCount = rules.Count;

            // 名单生效有三种合法形态，都要认：
            //  ① 内联：首条是 PROCESS-NAME（本工具推送后的形态）
            //  ② RULE-SET：Clash Verge 重新加载配置后会退回这种形态，provider 文件就是名单
            //  ③ 空名单：首条直接是 MATCH,DIRECT
            if (rules.Count > 0)
            {
                var first = rules[0];
                string type = first.Type ?? "";
                if (type.Equals("ProcessName", StringComparison.OrdinalIgnoreCase)) st.Applied = true;
                else if (type.Equals("RuleSet", StringComparison.OrdinalIgnoreCase) &&
                         (first.Payload ?? "").IndexOf("pp-whitelist", StringComparison.OrdinalIgnoreCase) >= 0) st.Applied = true;
                else if (type.Equals("Match", StringComparison.OrdinalIgnoreCase) &&
                         (first.Proxy ?? "").Equals("DIRECT", StringComparison.OrdinalIgnoreCase)) st.Applied = true;
            }

            // 兼容模式下名单是空的 = 本来就没有要生效的东西。
            // 这时候再显示「名单未生效，请保存一次」只会让人以为程序坏了。
            if (!st.Applied && Settings.PatchOnly && Load().Count == 0) st.Applied = true;

            return st;
        }

        // ---------------------------------------------------------------- 定向重连

        public sealed class ConnectionSnapshot
        {
            public readonly List<string> Ids = new List<string>();
            public readonly Dictionary<string, int> ByProcess = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            public int Total;
            public string Error;
        }

        public sealed class ReconnectOutcome
        {
            public int Attempted;
            public int Closed;
            public int Failed;
        }

        /// <summary>
        /// 推送新规则「之前」记录改动软件的旧连接 ID。
        /// 必须先快照再推送，这样不会误关推送后刚建立的新连接。
        /// </summary>
        public static ConnectionSnapshot CaptureConnections(IEnumerable<string> changedExes)
        {
            var snap = new ConnectionSnapshot();
            var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in changedExes ?? new string[0])
            {
                string exe = Normalize(raw);
                if (exe.Length > 0) wanted.Add(exe);
            }
            if (wanted.Count == 0) return snap;

            var root = Mihomo.GetJson("/connections");
            if (root == null) { snap.Error = "读取旧连接失败"; return snap; }
            object rawConnections;
            if (!root.TryGetValue("connections", out rawConnections)) { snap.Error = "连接列表格式异常"; return snap; }
            var arr = Json.AsArray(rawConnections);
            if (arr == null) { snap.Error = "连接列表格式异常"; return snap; }
            snap.Total = arr.Count;

            foreach (var item in arr)
            {
                var d = Json.AsObject(item);
                var meta = Json.Obj(d, "metadata");
                string exe = Json.Str(meta, "process");
                if (string.IsNullOrEmpty(exe))
                    exe = Path.GetFileName(Json.Str(meta, "processPath") ?? "");
                if (string.IsNullOrEmpty(exe) || !wanted.Contains(exe)) continue;
                string id = Json.Str(d, "id");
                if (string.IsNullOrEmpty(id)) continue;
                snap.Ids.Add(id);
                int n;
                snap.ByProcess.TryGetValue(exe, out n);
                snap.ByProcess[exe] = n + 1;
            }
            return snap;
        }

        /// <summary>只关闭快照里的旧连接；404 表示它已自然结束，不算失败。</summary>
        public static ReconnectOutcome CloseCapturedConnections(ConnectionSnapshot snap)
        {
            var outcome = new ReconnectOutcome();
            if (snap == null) return outcome;
            foreach (var id in snap.Ids)
            {
                outcome.Attempted++;
                var r = Mihomo.DeleteConnection(id);
                if (r.Ok || r.Status == 404) outcome.Closed++;
                else outcome.Failed++;
            }
            return outcome;
        }

        static string Trim(string s, int n)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("\r", " ").Replace("\n", " ").Trim();
            return s.Length <= n ? s : s.Substring(0, n) + "…";
        }
    }
}
