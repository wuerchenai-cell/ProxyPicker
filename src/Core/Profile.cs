using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
            /// <summary>
            /// 非空 = 这是一条按安装目录整树覆盖的规则（PROCESS-PATH-REGEX）。
            /// 用于把某个软件自己的辅助进程一起包含进来，而不是逐个猜 exe 名字。
            /// </summary>
            public string PathRegex;
            public bool IsPathRule { get { return !string.IsNullOrEmpty(PathRegex); } }
            /// <summary>
            /// true = 这条规则是工具从主软件推导出来的（helper / 目录整树），
            /// 不是用户勾选的对象。保存名单时必须过滤掉，否则取消勾选主软件后，
            /// 残留的 helper 仍会作为一条独立勾选项继续代理（复审 F-08）。
            /// </summary>
            public bool Derived;
            public Item(string exe, string policy) { Exe = exe; Policy = policy; }
            public Item(string pattern, string policy, bool pathRule)
            { Exe = pattern; Policy = policy; if (pathRule) PathRegex = pattern; }
        }

        /// <summary>
        /// 名单文件是否存在＝用户是否曾明确表达过分流意图。空名单保存时也会写出这个文件，
        /// 所以它能天然区分「新装/刚还原，从没表达过意图」与「用户明确要全部直连」。
        /// 看守据此决定要不要维护段首 MATCH，不引入会过期的独立状态位。
        /// </summary>
        public static bool HasSavedIntent
        {
            get { try { return File.Exists(Paths.ListPath); } catch { return false; } }
        }

        /// <summary>
        /// 手动「还原」＝撤销接管意图。名单文件一旦删掉，看守就不会再按空名单补回
        /// MATCH,DIRECT。用文件本身表达意图，不引入会过期 / 崩溃残留的独立状态位。
        /// 抽成按路径操作是为了让自检能用临时文件覆盖，绝不碰真机名单。
        /// </summary>
        internal static bool ClearIntentFile(string path, out string error)
        {
            error = "";
            try
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path)) File.Delete(path);
                return true;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        public static bool ClearSavedIntent(out string error)
        {
            bool ok = ClearIntentFile(Paths.ListPath, out error);
            if (ok)
            {
                LastGoodItems = new List<Item>();
                LastLoadError = "";
                LastSaveError = "";
            }
            return ok;
        }

        /// <summary>
        /// 备份能不能推给当前客户端（fail-closed，三种情况一律拒绝并给原因）：
        /// 1) 无 .meta（旧版备份）：不推整份原件，调用方改走 BuildCleanRollbackPayload 基于当前订阅重建；
        /// 2) 当前探测不到配置：核对不了来源，停止；
        /// 3) 来源与当前路径不一致：避免把 A 客户端的订阅推给 B。
        ///
        /// 只有「有 .meta 且来源与当前路径一致」才放行整份推回。
        /// 调用方必须先确认 currentPath 是否可信——探测不到就宁可停止还原。
        /// </summary>
        internal static bool BackupAllowed(string metaSource, string currentPath, bool metaExists, out string why)
        {
            why = "";
            if (!metaExists || string.IsNullOrEmpty(metaSource))
            {
                why = "这份备份没有记录来源（旧版备份）；已改用当前订阅重建配置，不整份推回";
                return false;
            }
            if (string.IsNullOrEmpty(currentPath))
            {
                why = "当前探测不到客户端配置，无法核对备份来源，已停止还原";
                return false;
            }
            if (!metaSource.Trim().Equals(currentPath.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                why = "备份来自另一份客户端配置，已停止还原以免推错订阅";
                return false;
            }
            return true;
        }

        /// <summary>
        /// 旧版备份（没有 .meta）还原兜底：不推整份备份原文——那只是「上次接管前的旧订阅」，
        /// 用户可能已经换过订阅，原样推回去等于把订阅整个换掉（外部评审 P1-③）。
        ///
        /// 改为：读当前订阅原文，用 Discovery.BuildPayload 以空名单+兼容模式重建一份
        /// 「订阅原文去掉本工具规则块」的干净 payload（订阅/规则逐字保留，只拿走
        /// 自己的 # PP-BEGIN / # PP-END 标记块；兼容模式（patchOnly: true）下订阅自带的
        /// 段首 MATCH 属于订阅自己的规则，不会被 StripHeadMatch 拿走）。
        /// 等价于退出还原。
        /// find-process-mode / IPv6 按记下的原值恢复，与 LeaveNoTraceCore 同一套口径。
        /// 返回 null 表示当前订阅不可读。
        /// </summary>
        static string BuildCleanRollbackPayload(string currentCfg)
        {
            try
            {
                if (string.IsNullOrEmpty(currentCfg) || !File.Exists(currentCfg)) return null;
                string text = File.ReadAllText(currentCfg, System.Text.Encoding.UTF8);
                string policy = "PP-FIXED";
                try { bool reused; policy = Discovery.FindPolicy(text, out reused); } catch { }
                // 未记过原值时保持现状（null = 仍写 always，与上次接管时的状态一致）。
                string fpm = Settings.Current.FindProcessOriginal;
                if (fpm.Length == 0) fpm = null;
                string clean = Discovery.BuildPayload(text, new List<Item>(), policy, true, fpm);
                bool ipv6Restore = Settings.Ipv6GuardOn && !string.IsNullOrEmpty(Settings.Current.Ipv6Original);
                if (ipv6Restore)
                    clean = Guard.RestoreIpv6(clean, Settings.Current.Ipv6Original);
                return clean;
            }
            catch { return null; }
        }

        public sealed class ApplyOutcome
        {
            public bool Ok;
            /// <summary>配置已成功推给内核（即使随后回读校验失败）。界面据此同步内存名单。</summary>
            public bool Pushed;
            /// <summary>收尾批 P3-2：内核推成功、但勾选名单没写进磁盘。此时界面不能把内存名单
            /// 对齐到「本次新名单」，否则看守会拿一份磁盘上并不存在的名单去对账；保持旧内存名单
            /// 反而能让下一轮看守把内核自愈回磁盘那份，状态重新一致。</summary>
            public bool PersistFailed;
            public string Message;
            public int RuleCount;
            public long Ms;
            public string Policy = "";
            public bool Ipv6Off;    // 回读确认 ipv6 已经是 false
            public bool Degraded;   // 内核不支持目录规则，已退回只按进程名
        }

        /// <summary>本次会话里内核明确拒绝过目录规则；之后不再尝试，避免每次保存都多推一遍。</summary>
        public static bool PathRulesRejected;

        static bool HasPathRule(List<Item> items)
        {
            if (items == null) return false;
            foreach (var it in items) if (it.IsPathRule) return true;
            return false;
        }

        /// <summary>只有 400 且响应明确指向规则类型/解析失败，才算「旧内核不支持」，才允许降级。</summary>
        static bool IsPathRuleUnsupported(ApiResult push)
        {
            if (push == null || push.Status != 400) return false;
            string body = ((push.Body ?? "") + " " + (push.Error ?? "")).ToLowerInvariant();
            if (body.IndexOf("process-path-regex", StringComparison.Ordinal) >= 0) return true;
            if (body.IndexOf("processpath", StringComparison.Ordinal) >= 0) return true;
            return body.IndexOf("rule", StringComparison.Ordinal) >= 0 &&
                   (body.IndexOf("unsupported", StringComparison.Ordinal) >= 0 ||
                    body.IndexOf("invalid", StringComparison.Ordinal) >= 0 ||
                    body.IndexOf("parse", StringComparison.Ordinal) >= 0);
        }

        const string Header =
            "# ProxyPicker 唯一勾选清单（由面板自动改写，手改也可）\n" +
            "#\n" +
            "# 语义：PROCESS-NAME,<exe 名>,<策略>；PP-FIXED=走自动选出的主节点组，\n" +
            "#       手改成 DIRECT 或某个组名也会按条目生效。\n" +
            "# 未出现在本清单的软件，落到 rules 段首的 MATCH,DIRECT（默认直连）。\n" +
            "#\n" +
            "# 两种条目：\n" +
            "#   PROCESS-NAME,<exe>,<策略>          按进程名，同一 exe 名的所有进程一起分流\n" +
            "#   PROCESS-PATH-REGEX,<正则>,<策略>   按安装目录整树覆盖，目录里的辅助程序\n" +
            "#                                      （helper / 语言服务 / 更新器）全部一起走代理，\n" +
            "#                                      以后更新新增的程序也不需要再改\n" +
            "# 目录条目的正则以 (?i)^ 开头、以 \\\\ 结尾，保证不会顺带匹配到名字相近的其它目录。\n";

        // ---------------------------------------------------------------- 读

        /// <summary>最近一次名单读取失败的原因（空 = 正常）；读失败绝不能当成「空名单」。</summary>
        public static string LastLoadError = "";
        public static bool LastLoadFailed { get { return !string.IsNullOrEmpty(LastLoadError); } }
        /// <summary>最后一次成功读到的名单，读取失败时界面用它兜底显示。</summary>
        public static List<Item> LastGoodItems = new List<Item>();

        /// <summary>最近一次读取里无法解析、被跳过的 payload 行数（第 7 轮复审 R7-6，--audit 会报出来）。</summary>
        public static int LastSkippedLines = 0;
        /// <summary>坏行行号（1 起），保存失败时直接告诉用户改哪一行。</summary>
        public static readonly List<int> LastSkippedLineNumbers = new List<int>();

        /// <summary>三态读取：文件不存在 / 真空名单都算成功；占用、权限、损坏算失败。</summary>
        public static bool TryLoad(out List<Item> items, out string error)
        {
            items = new List<Item>();
            error = "";
            LastSkippedLines = 0;
            LastSkippedLineNumbers.Clear();
            string path = Paths.ListPath;
            if (!File.Exists(path)) return true;
            string[] lines;
            try { lines = File.ReadAllLines(path, Encoding.UTF8); }
            catch (Exception ex) { error = "读取勾选名单失败：" + ex.Message; return false; }

            bool inPayload = false;
            bool sawPayload = false;
            bool sawContent = false;
            for (int lineNo = 0; lineNo < lines.Length; lineNo++)
            {
                string raw = lines[lineNo];
                string s = raw.Trim();
                if (s.Length > 0 && !s.StartsWith("#", StringComparison.Ordinal)) sawContent = true;
                if (s.StartsWith("payload:", StringComparison.Ordinal)) { inPayload = true; sawPayload = true; continue; }
                if (!inPayload || !s.StartsWith("-", StringComparison.Ordinal)) continue;
                string body = s.Substring(1).Trim().Trim('\'', '"');
                string[] parts = body.Split(',');
                // 兼容早期/手写的两字段写法：`PROCESS-NAME,chrome.exe` 视为 PP-FIXED。
                // 只有「类型不认识 / 名字为空」才算坏行，避免把正常老名单判死。
                bool nameRule = parts.Length >= 2 &&
                    parts[0].Trim().Equals("PROCESS-NAME", StringComparison.OrdinalIgnoreCase) &&
                    parts[1].Trim().Length > 0;
                bool pathRule = parts.Length >= 2 &&
                    parts[0].Trim().Equals("PROCESS-PATH-REGEX", StringComparison.OrdinalIgnoreCase) &&
                    parts[1].Trim().Length > 0;
                if (nameRule)
                    items.Add(new Item(parts[1].Trim(), parts.Length >= 3 && parts[2].Trim().Length > 0
                        ? parts[2].Trim() : "PP-FIXED"));
                else if (pathRule)
                    items.Add(new Item(parts[1].Trim(), parts.Length >= 3 && parts[2].Trim().Length > 0
                        ? parts[2].Trim() : "PP-FIXED", true) { Derived = true });
                else
                {
                    // 手改坏了的行：不静默吞掉。保存前必须拦住，否则这一条会悄悄变直连。
                    LastSkippedLineNumbers.Add(lineNo + 1);
                }
            }
            LastSkippedLines = LastSkippedLineNumbers.Count;
            // 文件里有内容、却连 payload: 段都没有：这不是空名单，是格式坏了。
            // 继续当成空名单会推出一份「全部直连」，必须停下来（复审 F-04）。
            if (sawContent && !sawPayload)
            {
                error = "勾选名单缺少 payload: 段，格式已损坏；已停止读取，避免把全部软件改成直连";
                return false;
            }
            return true;
        }

        public static List<Item> Load()
        {
            List<Item> items;
            string error;
            if (!TryLoad(out items, out error))
            {
                LastLoadError = error;
                return new List<Item>(LastGoodItems);
            }
            LastLoadError = "";
            LastGoodItems = new List<Item>(items);
            return items;
        }

        public static Dictionary<string, string> LoadPolicyMap()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            // 目录规则不是软件行：它由勾选状态自动生成，不能混进列表里变成一条「软件」。
            foreach (var it in Load()) if (!it.IsPathRule) map[it.Exe] = it.Policy;
            return map;
        }

        // ---------------------------------------------------------------- 写

        public static string LastSaveError = "";

        /// <summary>名单实际写盘次数；仅自检读取，用来断言「意图撤销时不得调用 Save」（收尾批 F-03）。</summary>
        internal static long SaveCallCount;

        /// <summary>临时文件 + 原子替换；写失败返回 false，调用方必须停止推送。</summary>
        public static bool Save(List<Item> items)
        {
            SaveCallCount++;
            string temp = null;
            try
            {
                var sb = new StringBuilder();
                sb.Append(Header);
                sb.Append("payload:\n");
                foreach (var it in items)
                {
                    sb.Append("  - '").Append(it.IsPathRule ? "PROCESS-PATH-REGEX" : "PROCESS-NAME")
                      .Append(',').Append(it.IsPathRule ? it.PathRegex : it.Exe)
                      .Append(',').Append(it.Policy).Append("'\n");
                }

                string path = Paths.ListPath;
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                byte[] bytes = new UTF8Encoding(false).GetBytes(sb.ToString());
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }
                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);
                LastSaveError = "";
                LastLoadError = "";
                LastGoodItems = new List<Item>(items);
                return true;
            }
            catch (Exception ex)
            {
                LastSaveError = ex.Message;
                CrashLog.Write(ex, "保存勾选名单失败");
                return false;
            }
            finally
            {
                if (temp != null && File.Exists(temp)) { try { File.Delete(temp); } catch { } }
            }
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
            return Compose(proxy, direct, null);
        }

        /// <summary>
        /// peers 传入「主软件 exe → 同目录辅助进程 exe 名」的映射。
        /// Electron / 类 VS Code 软件的登录、更新、语言服务常由 helper 发起连接，
        /// 只写主 exe 会让这些连接落到 MATCH,DIRECT（Antigravity 登录失败的根因）。
        /// 勾一个软件就把它的 helper 一起写进规则块。
        /// </summary>
        public static List<Item> Compose(IEnumerable<string> proxy, IEnumerable<string> direct,
                                         Dictionary<string, List<string>> peers)
        {
            return Compose(proxy, direct, peers, null);
        }

        /// <summary>
        /// roots 传入「要整树覆盖的软件安装目录」。
        /// 目录规则排在进程名规则之后：内核回读校验和「第一条是不是自家规则」的判断
        /// 都依赖第一条仍是 PROCESS-NAME，顺序不能反。
        /// </summary>
        public static List<Item> Compose(IEnumerable<string> proxy, IEnumerable<string> direct,
                                         Dictionary<string, List<string>> peers, IEnumerable<string> roots)
        {
            return Compose(proxy, direct, peers, roots, null);
        }

        /// <summary>
        /// policies：用户手改过的策略名（名字 → DIRECT / 某个组名）。
        /// 勾选状态由「名单里有没有这一条」决定，但保存时必须把手改的策略原样带回去，
        /// 否则用户在名单里写 `微信.exe,🚀 节点选择`，界面一保存就被改回 PP-FIXED（F-13）。
        /// </summary>
        public static List<Item> Compose(IEnumerable<string> proxy, IEnumerable<string> direct,
                                         Dictionary<string, List<string>> peers, IEnumerable<string> roots,
                                         Dictionary<string, string> policies)
        {
            var items = new List<Item>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in proxy)
            {
                string exe = Normalize(name);
                if (exe.Length == 0 || !seen.Add(exe)) continue;
                string pol = "PP-FIXED";
                if (policies != null)
                {
                    string custom;
                    if (policies.TryGetValue(exe, out custom) && !string.IsNullOrEmpty(custom))
                        pol = custom;
                }
                items.Add(new Item(exe, pol));

                List<string> related;
                if (peers != null && peers.TryGetValue(exe, out related))
                {
                    foreach (var p in related)
                    {
                        string helper = Normalize(p);
                        if (helper.Length == 0 || !seen.Add(helper)) continue;
                        // 辅助进程永远跟随主软件走默认组，不继承用户手改的策略。
                        items.Add(new Item(helper, "PP-FIXED") { Derived = true });
                    }
                }
            }
            // 目录规则放最后：一条规则覆盖整个安装树（含以后新增的辅助程序），
            // 进程名规则继续保留，作为路径匹配不可用时的兜底。
            if (roots != null)
            {
                var seenRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var root in roots)
                {
                    string pattern = AppGroup.PatternFor(root);
                    if (string.IsNullOrEmpty(pattern)) continue;
                    if (!seenRoots.Add(pattern)) continue;
                    items.Add(new Item(pattern, "PP-FIXED", true) { Derived = true });
                }
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

            // IPv6 归客户端自己管，默认一个字都不碰。
            // Clash Verge 新版把 ipv6 列为「应用设置接管」字段：外部再写它会被丢弃，
            // 还会在界面上弹橙色警告。以前每次保存都塞一行 ipv6: false，等于给用户添堵。
            // 只有用户在帮助面板里主动开启「IPv6 防泄漏」才写；关掉时按记下的原值推回。
            // --dryrun 走的是 Discovery.BuildPayload，不经过这里，逐字节基线不受影响。
            // 关掉开关时同样一个字都不写：我们从来没改过配置文件，只推过运行时全文，
            // 停止注入后客户端会用它自己的应用设置重新生成配置，原值自然就回来了。
            // 反过来若在这里写回 ipv6: true，Clash Verge 一样会判为「越权」并再弹一次警告。
            if (Settings.Ipv6GuardOn) return Guard.EnsureIpv6Off(payload);
            return payload;
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

        /// <summary>跨进程互斥：命令行 --apply 与界面保存不能交叉写名单 / 推配置（评审 R6-4）。
        /// autoRecover=true 时在锁内推送前再核一次接管意图，消除修复与托盘还原的竞态（评审 P1-②）；
        /// 订阅兼容模式同样核对名单文件——它是接管意图的唯一载体（评审 R1）。</summary>
        public static ApplyOutcome Apply(List<Item> items, bool autoRecover = false)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            using (var gate = new System.Threading.Mutex(false, @"Local\ProxyPicker.Apply"))
            {
                bool owned = false;
                try { owned = gate.WaitOne(TimeSpan.FromSeconds(30)); }
                catch (System.Threading.AbandonedMutexException) { owned = true; }
                if (!owned)
                {
                    var busy = new ApplyOutcome { Ok = false, Message = ApplyBusyMessage };
                    busy.Ms = sw.ElapsedMilliseconds;
                    return busy;
                }
                try { return ApplyCore(items, sw, autoRecover); }
                finally { try { gate.ReleaseMutex(); } catch { } }
            }
        }

        internal static readonly string RevokedIntentMessage =
            "接管意图已撤销，已停止自动补回；如需恢复请重新勾选后保存";

        /// <summary>另一个实例正在保存（跨进程互斥没抢到锁）。收尾批 P3-5：
        /// 这不是「补回坏了」，修复侧应按占用跳过，不能计入连败数。</summary>
        internal static readonly string ApplyBusyMessage =
            "另一个 ProxyPicker 正在保存，请稍后再试";

        /// <summary>评审 R2 判别收敛成纯函数：ApplyCore 返回的是不是「用户主动撤销接管」。
        /// RepairAsync 与自检都引用它，避免两处 StartsWith 文案各写一份而漂移。</summary>
        internal static bool IsRevokedIntent(string message)
        {
            return message != null &&
                   message.StartsWith(RevokedIntentMessage, StringComparison.Ordinal);
        }

        /// <summary>
        /// 自动补回是否应停手：名单文件（接管意图的唯一载体）不存在时一律停手。
        /// 纯函数，PatchOnly 不参与判定（评审 R1：订阅兼容模式下托盘还原同样删名单文件，
        /// 豁免核对会让还原失效）。MainForm.RepairAsync 也引用 RevokedIntentMessage
        /// 识别此语义（不计入反馈失败）。
        /// </summary>
        internal static bool AutoRecoverStopped(bool autoRecover) => autoRecover && !HasSavedIntent;

        /// <summary>
        /// 生成 payload -> 推给内核 -> 回读规则校验 -> 成功后才写名单。
        ///
        /// 评审 R1：`AutoRecoverStopped`（判定）与 `RevokedIntentMessage`（文案）在临界区内
        /// 引用，同时由 MainForm.RepairAsync 按同一常量识别「撤销」语义；
        /// Discovery.SelfTest G2d 也锁同一口径，三处不会漂移。</summary>
        static ApplyOutcome ApplyCore(List<Item> items, System.Diagnostics.Stopwatch sw, bool autoRecover)
        {
            var result = new ApplyOutcome();

            // 竞态消除：自动化补回的推送前必达条件留在临界区内（同一把 Apply 互斥锁）。
            // 否则「修复任务发出前检查意图」与「托盘还原删除名单文件」之间的窗口里：
            // 还原完成，修复任务照旧把接管状态重新推回去，还原等于没做（评审 P1-②）。
            // 评审 R1：曾经这里额外排除了订阅兼容模式（!Settings.PatchOnly），但名单文件
            // 是接管意图的唯一载体，与模式无关——订阅兼容模式下还原同样删名单文件，
            // 若豁免核对，托盘还原后看守照旧补回，还原等于没做。两种模式一律核对。
            if (AutoRecoverStopped(autoRecover))
            {
                result.Ok = false;
                result.Message = RevokedIntentMessage;
                result.Ms = sw.ElapsedMilliseconds;
                return result;
            }

            string cfg = Paths.ClashYaml;
            if (string.IsNullOrEmpty(cfg) || !File.Exists(cfg))
            {
                result.Ok = false;
                result.Message = "没找到 Clash / mihomo 的配置文件；请先启动客户端再保存";
                result.Ms = sw.ElapsedMilliseconds;
                return result;
            }

            // 名单读取失败时绝不能当成「空名单」往下推（评审 R6-3）。
            List<Item> disk;
            string loadError;
            if (!TryLoad(out disk, out loadError))
            {
                result.Ok = false;
                result.Message = loadError + "；已停止推送，避免把勾选清空";
                result.Ms = sw.ElapsedMilliseconds;
                return result;
            }
            // 名单里有解析不了的行时，绝不能继续保存：那条会被当成没勾，悄悄变直连。
            if (LastSkippedLines > 0)
            {
                string where = string.Join("、", LastSkippedLineNumbers.Take(5).Select(n => n.ToString()).ToArray());
                if (LastSkippedLineNumbers.Count > 5) where += " 等";
                result.Ok = false;
                result.Message = "勾选名单第 " + where + " 行格式不对（共 " + LastSkippedLines +
                    " 行），已停止保存；请修好后再试，避免这些软件被悄悄改回直连";
                result.Ms = sw.ElapsedMilliseconds;
                return result;
            }

            string backupErr;
            if (!BackupOnce(cfg, out backupErr))
            {
                result.Ok = false;
                result.Message = "创建备份失败，已停止本次推送以免覆盖脏状态：" + backupErr;
                result.Ms = sw.ElapsedMilliseconds;
                return result;
            }
            // 主动开了防泄漏但还没记原值（例如命令行直接保存）时，先补记一次。
            if (Settings.Ipv6GuardOn) Settings.CaptureIpv6Original();
            // 第一次保存前记下客户端原本的 find-process-mode，退出时才能恢复原值。
            Settings.CaptureFindProcessOriginal();

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
            // 先推送；推送失败时磁盘名单保持原样，界面与内核不分叉（评审 R1-6 / R3-6）。
            // 评审 R4：本次保存已确认的订阅路径 cfg（由 Paths.ClashYaml 的 Detect 结果而来）
            // 作为 knownConfigPath 传下去，让整条推送链绑定/secret/controller 全部使用
            // 生成 payload 用的同一份配置——不允许通道绑定再猜一次导致错配。
            var push = Mihomo.PushConfig(payload, 15000, cfg);

            // 只有内核「明确不认识 PROCESS-PATH-REGEX」时才降级。
            // 复审 F-07：以前超时/401/网络中断也会走这里，重试一旦成功就把目录规则
            // 从名单里永久删掉，还谎报「内核不支持」——用户既丢了 helper 覆盖，也查不出原因。
            if (!push.Ok && HasPathRule(items) && IsPathRuleUnsupported(push))
            {
                var onlyNames = new List<Item>();
                foreach (var it in items) if (!it.IsPathRule) onlyNames.Add(it);
                string fallback;
                try { fallback = BuildPayload(cfg, onlyNames); }
                catch { fallback = null; }
                if (fallback != null)
                {
                    // 评审 R4：重试同样锁定 cfg，与主推共用同一份配置
                    var retry = Mihomo.PushConfig(fallback, 15000, cfg);
                    if (retry.Ok)
                    {
                        PathRulesRejected = true;   // 本次会话不再尝试，避免每次保存都白推一遍
                        items = onlyNames;
                        payload = fallback;
                        push = retry;
                        result.Degraded = true;
                    }
                }
            }
            if (!push.Ok)
            {
                result.Ok = false;
                result.Message = "推送失败：" + (push.Error ?? ("HTTP " + push.Status + " " + Trim(push.Body, 160)));
                result.Ms = sw.ElapsedMilliseconds;
                return result;
            }
            // 推送已经生效：后面无论名单写入还是回读校验是否成功，都不能让界面
            // 继续拿旧名单去对账（否则看守会把用户刚取消勾选的软件又补回来，外部评审 F4）。
            result.Pushed = true;
            // 规则真正生效的时刻：此后建立的连接才参与「预期 vs 实际」对账。
            // mihomo 不给已存在的连接重匹配规则，早于这一刻的长连接必须排除，
            // 否则浏览器里挂着不关的 WebSocket 会一直报「勾了却走直连」的假警报。
            Traffic.NotePush();
            // 只把「用户勾选的」写回名单；helper / 目录规则是推导出来的，不能落盘成
            // 独立条目，否则取消勾选主软件后它们仍会留在名单里继续代理（F-08）。
            var persist = new List<Item>();
            foreach (var it in items) if (!it.Derived) persist.Add(it);
            if (!Save(persist))
            {
                result.Ok = false;
                result.PersistFailed = true;   // 收尾批 P3-2：界面不要据此对齐内存名单
                result.Message = "规则已推送，但勾选名单写入失败：" + LastSaveError +
                    "；本次改动没有落盘，后台看守会按原名单恢复，请检查文件权限后重新保存";
                result.Ms = sw.ElapsedMilliseconds;
                return result;
            }

            // 回读校验：条数、exe、策略、空名单兜底、内核模式与进程识别开关（评审 R3-5 / R1-7）。
            // 收尾批 P2-1：回读也锁定 cfg，避免 5 秒缓存窗口内读到另一份客户端配置。
            var rules = ReadRules(cfg);
            result.RuleCount = rules.Count;
            bool good = true;
            string whyBad = "";
            if (rules.Count < items.Count) { good = false; whyBad = "内核规则条数不足"; }
            if (good)
            {
                for (int i = 0; i < items.Count; i++)
                {
                    string type = rules[i].Type ?? "";
                    string expectType = items[i].IsPathRule ? "ProcessPathRegex" : "ProcessName";
                    // 目录规则的类型名在不同内核版本里可能写成 ProcessPathRegex / ProcessPath-REGEX，
                    // 只要求「是进程类规则」，真正的判据是下面对 payload 的逐条比对。
                    bool typeOk = type.Equals(expectType, StringComparison.OrdinalIgnoreCase) ||
                                  (items[i].IsPathRule && type.StartsWith("Process", StringComparison.OrdinalIgnoreCase));
                    if (!typeOk)
                    { good = false; whyBad = "第 " + (i + 1) + " 条不是进程规则（" + type + "）"; break; }
                    string got = rules[i].Payload ?? "";
                    string wantPayload = items[i].IsPathRule ? items[i].PathRegex : items[i].Exe;
                    if (!got.Equals(wantPayload, StringComparison.OrdinalIgnoreCase))
                    { good = false; whyBad = "第 " + (i + 1) + " 条规则对不上"; break; }
                    string want = items[i].Policy.Equals("PP-FIXED", StringComparison.OrdinalIgnoreCase)
                        ? result.Policy : items[i].Policy;
                    string proxy = rules[i].Proxy ?? "";
                    if (want.Length > 0 && !proxy.Equals(want, StringComparison.OrdinalIgnoreCase))
                    { good = false; whyBad = "第 " + (i + 1) + " 条策略被改成 " + proxy; break; }
                }
            }
            if (good && items.Count == 0 && !Settings.PatchOnly &&
                (rules.Count == 0 || !rules[0].Type.Equals("Match", StringComparison.OrdinalIgnoreCase)))
            { good = false; whyBad = "空名单下缺少直连兜底"; }
            // 复审 P2：回读也要核对短路 MATCH 的位置，不能只靠 30 秒后的看守兜底。
            // 只检查「前 items.Count 条自家规则块」，避免误判订阅里自带的进程规则。
            if (good && !Settings.PatchOnly && items.Count > 0)
            {
                int firstMatch = -1;
                for (int i = 0; i < rules.Count; i++)
                    if ((rules[i].Type ?? "").Equals("Match", StringComparison.OrdinalIgnoreCase))
                    { firstMatch = i; break; }
                if (firstMatch >= 0 && firstMatch < items.Count)
                { good = false; whyBad = "直连兜底排在勾选规则前面"; }
            }
            if (good)
            {
                try
                {
                    var conf = Mihomo.GetJson("/configs", 4000, cfg);
                    if (conf != null)
                    {
                        string mode = Json.Str(conf, "mode");
                        string fpm = Json.Str(conf, "find-process-mode");
                        if (!string.IsNullOrEmpty(mode) && !mode.Equals("rule", StringComparison.OrdinalIgnoreCase))
                        { good = false; whyBad = "客户端当前是 " + mode + " 模式，规则不生效，请切到规则模式"; }
                        // strict 是 mihomo 默认档，PROCESS-NAME 规则正常生效；只有 off 才是真的关闭（F1）。
                        else if (!string.IsNullOrEmpty(fpm) && fpm.Equals("off", StringComparison.OrdinalIgnoreCase))
                        { good = false; whyBad = "进程识别已关闭（find-process-mode=off）"; }
                    }
                }
                catch { }
            }
            result.Ok = good;
            result.Message = good
                ? "已生效 " + items.Count + " 条规则"
                : "已推送但校验不一致：" + whyBad + "，请点刷新核对";
            if (good && result.Degraded)
                result.Message += "（这台电脑的内核不支持按目录覆盖，辅助进程已退回按进程名处理）";

            // 只有用户主动开了防泄漏才回读 IPv6；否则这个开关归客户端管，
            // 我们不改、也不评价它的开或关。
            if (Settings.Ipv6GuardOn)
            {
                try
                {
                    var after = Mihomo.GetJson("/configs", 4000, cfg);
                    if (after != null && Json.Bool(after, "ipv6", false))
                        result.Message += "（IPv6 仍开着，客户端可能已覆盖；要彻底关闭请在 Clash Verge 设置里改）";
                    else if (after != null)
                        result.Ipv6Off = true;
                }
                catch { }
            }

            result.Ms = sw.ElapsedMilliseconds;
            return result;
        }

        // ---------------------------------------------------------------- 备份与回滚

        /// <summary>第一次接管留一份原件；每次保存前再滚动保留最近 3 份（评审 R1-12）。</summary>
        const string SecretPlaceholder = "secret: \"<备份时已移除，还原时自动补回>\"";

        /// <summary>备份不落明文控制密钥（复审 P2）；还原时从当前配置补回。</summary>
        static string ScrubSecret(string yaml)
        {
            return System.Text.RegularExpressions.Regex.Replace(yaml, @"(?m)^secret:\s*.*$", SecretPlaceholder);
        }

        // 收尾批 P2-2：删掉这里的第三套 secret 解析。还原补回密钥统一走
        // Mihomo.ReadSecret(currentCfg) -> Discovery.ReadController，全仓只剩一种解析口径。

        /// <summary>
        /// 第一次接管留一份原件；每次保存前再滚动保留最近 3 份（评审 R1-12）。
        ///
        /// 返回 true 表示至少写出了一份；false 表示备份目录不可写 / 磁盘问题。
        /// 调用方（ApplyCore）在备份失败时会中止本次推送，避免「推完无法回滚」
        /// 还谎报成功（外部评审 P1-③）。备份持续失败也会在每日日志里积攒警告。
        /// </summary>
        public static bool BackupOnce(string configPath, out string error)
        {
            error = "";
            try
            {
                string dir = Paths.BackupDir;
                Directory.CreateDirectory(dir);
                string scrubbed = ScrubSecret(File.ReadAllText(configPath, Encoding.UTF8));
                var utf8 = new UTF8Encoding(false);
                string source = configPath ?? "";

                string original = Path.Combine(dir, "original.yaml");
                if (!File.Exists(original))
                {
                    File.WriteAllText(original, scrubbed, utf8);
                    File.WriteAllText(original + ".meta", source, utf8);
                }

                string recent = Path.Combine(dir, "recent.yaml");
                string prev2 = Path.Combine(dir, "recent-2.yaml");
                string prev3 = Path.Combine(dir, "recent-3.yaml");
                if (File.Exists(prev2)) { File.Copy(prev2, prev3, true); CopyBackupMeta(prev2, prev3); }
                if (File.Exists(recent)) { File.Copy(recent, prev2, true); CopyBackupMeta(recent, prev2); }
                File.WriteAllText(recent, scrubbed, utf8);
                File.WriteAllText(recent + ".meta", source, utf8);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                CrashLog.Write(ex, "创建配置备份失败");
                return false;
            }
        }

        /// <summary>备份轮转时来源标记一起走；旧备份没有标记就把目标的标记删掉，避免张冠李戴。</summary>
        static void CopyBackupMeta(string fromYaml, string toYaml)
        {
            try
            {
                string from = fromYaml + ".meta", to = toYaml + ".meta";
                if (File.Exists(from)) File.Copy(from, to, true);
                else if (File.Exists(to)) File.Delete(to);
            }
            catch { }
        }

        /// <summary>
        /// 一键还原：优先推回「上次保存前的配置」，没有再用最初那份。
        /// 托盘菜单里现在有入口，不再是只有定义没有调用的空承诺。
        /// 与 Apply 共用同一把跨进程互斥锁（第 7 轮复审 R7-3）：
        /// 还原会推配置并清空名单，和界面保存 / 自动补回 / --apply 并发时会互相覆盖。
        /// </summary>
        public static bool Rollback(out string message)
        {
            using (var gate = new System.Threading.Mutex(false, @"Local\ProxyPicker.Apply"))
            {
                bool owned = false;
                try { owned = gate.WaitOne(TimeSpan.FromSeconds(30)); }
                catch (System.Threading.AbandonedMutexException) { owned = true; }
                if (!owned)
                {
                    message = "另一个 ProxyPicker 正在保存，请稍后再试";
                    return false;
                }
                try { return RollbackCore(out message); }
                finally { try { gate.ReleaseMutex(); } catch { } }
            }
        }

        /// <summary>
        /// 退出前把系统还回去：只删掉本工具写进内核的规则块，并把 IPv6 恢复成接管前的值。
        /// 不写订阅文件、不动勾选名单，所以下次打开还是原来的选择。
        /// 与保存共用同一把锁，避免退出时和后台补回互相覆盖。
        /// 用户要求「不用的时候电脑跟没装一样」，这条就是那个要求的落点。
        /// </summary>
        public static bool LeaveNoTrace(out string message)
        {
            using (var gate = new System.Threading.Mutex(false, @"Local\ProxyPicker.Apply"))
            {
                bool owned = false;
                try { owned = gate.WaitOne(TimeSpan.FromSeconds(30)); }
                catch (System.Threading.AbandonedMutexException) { owned = true; }
                if (!owned) { message = "另一个 ProxyPicker 正在保存，跳过还原"; return false; }
                try { return LeaveNoTraceCore(out message); }
                finally { try { gate.ReleaseMutex(); } catch { } }
            }
        }

        static bool LeaveNoTraceCore(out string message)
        {
            message = "";
            try
            {
                string cfg = Paths.ClashYaml;
                if (string.IsNullOrEmpty(cfg) || !File.Exists(cfg)) { message = "没找到客户端配置文件"; return false; }
                string text = File.ReadAllText(cfg, Encoding.UTF8);
                // 空名单时策略名用不到，但 FindPolicy 在极端配置上可能抛异常 ——
                // 退出流程不能因为「挑不出策略组」就放弃还原，所以给它一个兜底值。
                string policy = "PP-FIXED";
                try { bool reused; policy = Discovery.FindPolicy(text, out reused); } catch { }
                // 空名单 + 「订阅兼容」：这一步只会删掉标记块，不会插入 MATCH,DIRECT。
                // find-process-mode 要恢复成用户原值；没记到原值时保持现状（null = 仍写 always）。
                string fpm = Settings.Current.FindProcessOriginal;
                if (fpm.Length == 0) fpm = null;
                string clean = Discovery.BuildPayload(text, new List<Item>(), policy, true, fpm);
                bool ipv6Restore = Settings.Ipv6GuardOn && !string.IsNullOrEmpty(Settings.Current.Ipv6Original);
                if (ipv6Restore)
                    clean = Guard.RestoreIpv6(clean, Settings.Current.Ipv6Original);
                // 判断要不要推，必须看「内核里有没有自家规则」，不能拿磁盘配置比：
                // 本工具从不改订阅文件，磁盘上本来就没有那块规则，一比就会误判成
                // 「本来就没插过」，于是规则留在内核里、退出等于没还原。
                var mine = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    foreach (var it in Load())
                        mine.Add(it.IsPathRule ? it.PathRegex : it.Exe);
                }
                catch { }
                bool readOk;
                var live = ReadRules(out readOk, cfg);   // 收尾批 P2-1：退出还原回读同源
                // 复审 F-02：任一位置存在自家规则都算，不能只看首条；读失败必须按「有」处理。
                bool fpmRestore = !string.IsNullOrEmpty(fpm) && !fpm.Equals("always", StringComparison.OrdinalIgnoreCase);
                // 完全接管 + 明确保存过空名单时，段首 MATCH,DIRECT 也是本工具留下的，必须一并撤掉。
                bool emptyWhitelist = !Settings.PatchOnly && HasSavedIntent && mine.Count == 0;
                if (!ShouldRestore(readOk, live, mine, ipv6Restore, fpmRestore, emptyWhitelist))
                {
                    Settings.ClearRestoreBaseline();   // 没接管过，也不该留着上一轮的过期基线
                    return true;                       // 内核里本来就没有我们的东西，不必重载配置
                }
                var push = Mihomo.PushConfig(clean, 15000, cfg);   // 收尾批 P2-1
                if (!push.Ok) { message = push.Error ?? ("HTTP " + push.Status); return false; }
                Settings.ClearRestoreBaseline();       // 还原成功：这次接管收尾，下次重新捕获真实基线
                return true;
            }
            catch (Exception ex) { message = ex.Message; return false; }
        }

        /// <summary>
        /// 退出还原决策（纯函数，自检可直接覆盖）：
        /// 读失败按「有」处理；任意位置存在自家规则就还原；IPv6 / find-process-mode 需要恢复也还原。
        /// </summary>
        internal static bool ShouldRestore(bool readOk, IEnumerable<RuleInfo> live, HashSet<string> mine,
                                           bool ipv6Restore, bool fpmRestore)
        {
            return ShouldRestore(readOk, live, mine, ipv6Restore, fpmRestore, false);
        }

        /// <summary>
        /// emptyWhitelist：用户明确保存过「一个都不勾／全部直连」。此时内核里只有本工具写的
        /// 段首 MATCH,DIRECT，它既不是 PP-FIXED 也不在名单里，IsOwnRule 认不出来；
        /// 若不单独判定，退出时会认为「没有我们的东西」而把全部直连状态留在内核里（外部评审 F2）。
        /// </summary>
        internal static bool ShouldRestore(bool readOk, IEnumerable<RuleInfo> live, HashSet<string> mine,
                                           bool ipv6Restore, bool fpmRestore, bool emptyWhitelist)
        {
            if (!readOk) return true;
            if (emptyWhitelist && live != null)
            {
                foreach (var r in live)
                {
                    if (r == null) continue;
                    if ((r.Type ?? "").Equals("Match", StringComparison.OrdinalIgnoreCase) &&
                        (r.Proxy ?? "").Equals("DIRECT", StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
            if (live != null)
            {
                foreach (var r in live)
                    if (IsOwnRule(r, mine)) return true;
            }
            return ipv6Restore || fpmRestore;
        }

        /// <summary>内核里的这一条是不是本工具写的：策略是 PP-FIXED，或 payload 与名单里的条目一致。</summary>
        static bool IsOwnRule(RuleInfo r, HashSet<string> mine)
        {
            if (r == null) return false;
            if ((r.Proxy ?? "").Equals("PP-FIXED", StringComparison.OrdinalIgnoreCase)) return true;
            if (!string.IsNullOrEmpty(r.Payload) && mine != null && mine.Contains(r.Payload)) return true;
            return false;
        }

        static bool RollbackCore(out string message)
        {
            try
            {
                string dir = Paths.BackupDir;
                string recent = Path.Combine(dir, "recent.yaml");
                string original = Path.Combine(dir, "original.yaml");
                string chosen = File.Exists(recent) ? recent : original;
                if (!File.Exists(chosen))
                {
                    message = "没有找到备份（本程序还没在这台机器上保存过）";
                    return false;
                }

                // 备份来源核对：新版备份带 .meta，记录它是从哪份客户端配置拷出来的。
                // 换过客户端 / 订阅之后，旧备份推给新内核会把订阅整个换掉，必须先拦住。
                bool metaExists = File.Exists(chosen + ".meta");
                string metaSource = "";
                if (metaExists)
                {
                    try { metaSource = File.ReadAllText(chosen + ".meta", Encoding.UTF8).Trim(); }
                    catch { metaExists = false; }
                }
            string currentCfg = Paths.ClashYaml;
            string sourceWhy;
            if (!BackupAllowed(metaSource, currentCfg, metaExists, out sourceWhy))
            {
                // 旧版备份（无 .meta）：不推整份原件 —— 那是「换客户端前的旧订阅」，
                // 推回去等于把用户已经更新过的订阅整个换掉（外部评审 P1-③）。
                // 改为基于当前订阅重建一份干净 payload：剔除本工具的规则块，
                // 保留订阅原规则与用户设置。
                string back = BuildCleanRollbackPayload(currentCfg);
                if (back == null)
                {
                    message = sourceWhy + "；且当前订阅不可读（" + currentCfg + "），无法重建配置。" +
                              "请先启动客户端并保存一次，再点还原。";
                    return false;
                }
                var rebuild = Mihomo.PushConfig(back, 15000, currentCfg);   // 收尾批 P2-1
                if (!rebuild.Ok)
                {
                    message = "推送清理后的配置失败：" + (rebuild.Error ?? ("HTTP " + rebuild.Status));
                    return false;
                }
                message = sourceWhy + "；已按当前订阅重建干净配置，接管状态已清除";
                string clearError2;
                bool cleared2 = ClearSavedIntent(out clearError2);
                if (cleared2) Settings.ClearRestoreBaseline();
                return true;
            }

                string text = File.ReadAllText(chosen, Encoding.UTF8);
                if (text.IndexOf("<备份时已移除", StringComparison.Ordinal) >= 0)
                {
                    // 收尾批 P2-2：改用统一的 Discovery.ReadController 解析（经 Mihomo.ReadSecret），
                    // 且传入本次已确认的 currentCfg，不再另走一次 Paths.ClashYaml 的 Detect。
                    string secret = Mihomo.ReadSecret(currentCfg);
                    if (secret.Length == 0)
                    {
                        message = "备份里的控制密钥已被移除，且读不到当前密钥；为安全起见已停止还原";
                        return false;
                    }
                    text = System.Text.RegularExpressions.Regex.Replace(
                        text, @"(?m)^secret:\s*.*$", "secret: " + Json.Quote(secret));
                }
                var push = Mihomo.PushConfig(text, 15000, currentCfg);   // 收尾批 P2-1
                if (!push.Ok)
                {
                    message = "推送备份失败：" + (push.Error ?? ("HTTP " + push.Status));
                    return false;
                }
                // 关键：还原是「撤销接管意图」，必须删掉名单文件，不能写空名单。
                // 写空名单时 HasSavedIntent 仍为 true，看守 30–90 秒后会按「用户要全部直连」
                // 重新推回 MATCH,DIRECT，还原等于没做（外部评审 P0）。
                string clearError;
                bool cleared = ClearSavedIntent(out clearError);
                if (cleared) Settings.ClearRestoreBaseline();
                if (!cleared)
                {
                    message = "配置已还原，但勾选名单文件删除失败：" + clearError +
                              "；请手动删除该文件后重启本程序，否则后台看守可能重新接管";
                    return true;   // 内核已还原成功，界面仍要同步
                }
                message = sourceWhy.Length > 0
                    ? sourceWhy + "；已按当前配置还原，请确认订阅是否正确。"
                    : "已还原为上次保存前的配置，接管状态已清除";
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
            bool ok;
            return ReadRules(out ok);
        }

        /// <summary>收尾批 P2-1：调用方已确认配置路径时，回读也走同一份，不再二次 Detect。</summary>
        public static List<RuleInfo> ReadRules(string knownConfigPath)
        {
            bool ok;
            return ReadRules(out ok, knownConfigPath);
        }

        /// <summary>读内核规则表。ok=false 表示「没读到/格式异常」，与「真的 0 条」必须区分开。</summary>
        public static List<RuleInfo> ReadRules(out bool ok)
        {
            return ReadRules(out ok, null);
        }

        public static List<RuleInfo> ReadRules(out bool ok, string knownConfigPath)
        {
            ok = false;
            var list = new List<RuleInfo>();
            var obj = Mihomo.GetJson("/rules", 10000, knownConfigPath);
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
            ok = true;
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

            // 首条是进程规则不代表整份配置生效：兼容模式下中间若提前出现 MATCH，
            // 后面的订阅规则永远匹配不到。白名单模式的 MATCH 本来就该排在自家规则后，
            // 不能一起判（复审 F-07）。
            if (Settings.PatchOnly)
            {
                for (int i = 0; i < rules.Count; i++)
                {
                    if ((rules[i].Type ?? "").Equals("Match", StringComparison.OrdinalIgnoreCase))
                    {
                        if (i < rules.Count - 1)
                        {
                            st.Applied = false;
                            st.Error = "规则里有一条提前的 MATCH，后面的规则不会生效";
                        }
                        break;
                    }
                }
            }

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
            // 复审 F-17：以前逐条串行、每条 5 秒超时，改动 20 个软件时能卡到分钟级。
            // 现在 4 路并发、单条 2.5 秒；最坏耗时从 N×5s 降到 ceil(N/4)×2.5s。
            int attempted = 0, closed = 0, failed = 0;
            System.Threading.Tasks.Parallel.ForEach(snap.Ids,
                new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = 4 },
                id =>
                {
                    System.Threading.Interlocked.Increment(ref attempted);
                    var r = Mihomo.DeleteConnection(id, 2500);
                    if (r.Ok || r.Status == 404) System.Threading.Interlocked.Increment(ref closed);
                    else System.Threading.Interlocked.Increment(ref failed);
                });
            outcome.Attempted = attempted;
            outcome.Closed = closed;
            outcome.Failed = failed;
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
