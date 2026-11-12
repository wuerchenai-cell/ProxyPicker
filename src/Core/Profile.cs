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
        /// <summary>接管意图的代次：还原（删名单）和成功保存都会 +1。
        /// 在途的保存 / 补回任务用它判断「我拿到的意图是不是已经作废」。</summary>
        static long _intentEpoch;

        public static long IntentEpoch { get { return System.Threading.Interlocked.Read(ref _intentEpoch); } }

        static void BumpIntentEpoch() { System.Threading.Interlocked.Increment(ref _intentEpoch); }

        /// <summary>
        /// 接管意图的**磁盘**指纹（跨进程共享）。_intentEpoch 只是本进程的静态计数：
        /// 另一个 ProxyPicker 进程执行 --restore 后它不会变，于是「GUI 等锁期间被命令行还原」
        /// 这种情况代次校验完全发现不了（v1.3.7 的跨进程锁只挡住了同时写，没挡住意图变化）。
        /// 名单文件是接管意图的唯一载体（还原＝删它），所以用存在性 + 内容哈希当签名。
        /// </summary>
        internal static string IntentSignature()
        {
            try
            {
                string p = Paths.ListPath;
                if (string.IsNullOrEmpty(p) || !File.Exists(p)) return "absent";
                return "sha256:" + Sha256Hex(File.ReadAllBytes(p));
            }
            catch { return "unreadable"; }
        }

        /// <summary>等锁前后比对：签名不一致说明期间有人还原或改写了名单。</summary>
        internal static bool IntentChangedSince(string signature)
        {
            if (signature == null) return false;
            return !string.Equals(IntentSignature(), signature, StringComparison.Ordinal);
        }

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
                BumpIntentEpoch();
                LastGoodItems = new List<Item>();
                LastLoadError = "";
                LastSaveError = "";
            }
            return ok;
        }

        /// <summary>配置文件内容指纹。Verge 下换订阅路径不变，只有内容能证明「还是当初那份」。</summary>
        internal static string ConfigFingerprint(string configPath)
        {
            try
            {
                if (string.IsNullOrEmpty(configPath) || !File.Exists(configPath)) return "";
                return Sha256Hex(File.ReadAllBytes(configPath));
            }
            catch { return ""; }
        }

        /// <summary>同一份字节的 SHA-256。备份时先读一次，指纹和备份内容都从这团字节来。</summary>
        internal static string Sha256Hex(byte[] bytes)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(bytes ?? new byte[0]);
                var sb = new System.Text.StringBuilder(64);
                foreach (byte b in hash) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        /// <summary>先写临时文件再原子替换：断电 / 强杀时不会留下半个备份或半个 .meta。
        /// v1.3.8：降级路径不再「先删旧文件再改名」——那会在两步之间留下「目标文件不存在」的
        /// 窗口，此刻掉电就真的丢了备份。改成先把旧文件改名保留成 .prev，再把新文件就位；
        /// 新文件没成功就位时把旧文件放回去，宁可不更新也不丢内容。</summary>
        internal static void WriteTextAtomic(string path, string text)
        {
            var utf8 = new UTF8Encoding(false);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, text ?? "", utf8);
            PromoteTemp(tmp, path);
        }

        /// <summary>
        /// 把已经完整写好的临时文件换成正式文件。
        /// 优先 File.Replace（NTFS 上的原子替换）；文件系统不支持或目标被短暂占用时，
        /// 改用「旧文件改名保留成 .prev → 新文件就位 → 删除 .prev」。
        /// v1.3.8 之前这里写的是「删旧文件 → 改名」，两步之间目标文件不存在，
        /// 此刻断电 / 强杀就真的丢了备份。现在任何一步失败都能把旧文件放回去。
        /// </summary>
        static void PromoteTemp(string tmp, string path)
        {
            try
            {
                if (File.Exists(path)) File.Replace(tmp, path, null);
                else File.Move(tmp, path);
                return;
            }
            catch (Exception)
            {
                // 落到下面的保守路径
            }

            string prev = path + ".prev";
            bool movedAside = false;
            try
            {
                try { if (File.Exists(prev)) File.Delete(prev); } catch { }
                if (File.Exists(path)) { File.Move(path, prev); movedAside = true; }
                File.Move(tmp, path);
            }
            catch (Exception ex)
            {
                if (movedAside)
                {
                    try { if (!File.Exists(path)) File.Move(prev, path); } catch { }
                }
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                throw new IOException("写入 " + Path.GetFileName(path) + " 失败：" + ex.Message, ex);
            }
            try { if (File.Exists(prev)) File.Delete(prev); } catch { }
        }

        /// <summary>新版 .meta 两行：来源路径 + 备份时配置内容指纹。旧版只有一行路径。
        /// v1.3.7：指纹由调用方传入（与备份内容来自同一次读取），不再自己重读配置——
        /// 以前「备份写的是订阅 A、指纹算的是订阅 B」，换订阅时能骗过还原校验。</summary>
        internal static void WriteBackupMeta(string metaPath, string configPath)
        {
            WriteBackupMeta(metaPath, configPath, ConfigFingerprint(configPath));
        }

        internal static void WriteBackupMeta(string metaPath, string configPath, string fingerprint)
        {
            WriteTextAtomic(metaPath, (configPath ?? "") + "\n" + (fingerprint ?? ""));
        }

        /// <summary>兼容读取新旧两种 .meta；读不到或空行返回 false（按旧版备份处理）。</summary>
        internal static bool ReadBackupMeta(string metaPath, out string source, out string fingerprint)
        {
            source = ""; fingerprint = "";
            try
            {
                string[] lines = File.ReadAllText(metaPath, Encoding.UTF8)
                                     .Replace("\r\n", "\n").Split('\n');
                source = (lines[0] ?? "").Trim();
                if (lines.Length > 1) fingerprint = (lines[1] ?? "").Trim();
                return source.Length > 0;
            }
            catch { return false; }
        }

        /// <summary>
        /// 备份能不能整份推回（fail-closed）：
        /// 1) 无 .meta / 无内容指纹（旧版备份）：改走基于当前订阅的干净重建；
        /// 2) 探测不到当前配置：停止；
        /// 3) 来源路径不一致：停止；
        /// 4) 内容指纹不一致：订阅在保存之后被更新过（Clash Verge Rev 换订阅时路径不变，
        ///    只比路径会漏掉这种情况），改走重建，绝不把旧快照推给内核。
        ///
        /// 只有「路径一致 + 内容一致」才放行整份推回。调用方必须先确认 currentPath 可信。
        /// </summary>
        internal static bool BackupAllowed(string metaSource, string currentPath, bool metaExists, out string why)
        {
            return BackupAllowed(metaSource, currentPath, metaExists, "", "", out why);
        }

        internal static bool BackupAllowed(string metaSource, string currentPath, bool metaExists,
                                           string metaFingerprint, string currentFingerprint, out string why)
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
                why = "备份来自另一份客户端配置，已改用当前订阅重建配置，不整份推回";
                return false;
            }
            // 独立审阅 v1.3.2 第二轮：Verge 的所有订阅都生成同一个 clash-verge.yaml，
            // 路径永远相等；必须再比一次备份时的文件内容，否则更新订阅后还原会推回旧快照。
            if (string.IsNullOrEmpty(metaFingerprint) || string.IsNullOrEmpty(currentFingerprint))
            {
                why = "这份备份没有记录配置内容指纹（旧版备份）；已改用当前订阅重建配置，不整份推回";
                return false;
            }
            if (!metaFingerprint.Equals(currentFingerprint, StringComparison.OrdinalIgnoreCase))
            {
                why = "客户端配置在保存之后更新过，备份里的订阅已经过期；已改用当前订阅重建配置，不整份推回";
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
            /// <summary>v1.3.8：名单没落盘、且「把内核退回磁盘那份名单」的补偿推送也失败了。
            /// 此时内核与磁盘确实不一致，界面必须明说，不能只说一句「后台看守会恢复」。</summary>
            public bool RecoveryRequired;
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
            "# 未出现在本清单的软件：\n" +
            "#   代理白名单模式 → 落到 rules 段首的 MATCH,DIRECT（默认直连）；\n" +
            "#   订阅兼容模式   → 不受影响，继续按订阅自己的规则分流（取消勾选≠直连）。\n" +
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

            List<int> bad;
            bool ok = ParseWhitelistLines(lines, out items, out bad, out error);
            LastSkippedLineNumbers.AddRange(bad);
            LastSkippedLines = bad.Count;
            return ok;
        }

        /// <summary>纯文本解析，供自检直接喂字符串，不碰真机名单文件（独立审阅第二轮）。</summary>
        internal static bool ParseWhitelistLines(string[] lines, out List<Item> items,
                                                 out List<int> badLines, out string error)
        {
            items = new List<Item>();
            badLines = new List<int>();
            error = "";
            bool inPayload = false, sawPayload = false, sawContent = false;
            for (int lineNo = 0; lineNo < (lines ?? new string[0]).Length; lineNo++)
            {
                string s = (lines[lineNo] ?? "").Trim();
                if (s.Length > 0 && !s.StartsWith("#", StringComparison.Ordinal)) sawContent = true;
                if (s.StartsWith("payload:", StringComparison.Ordinal)) { inPayload = true; sawPayload = true; continue; }
                if (!inPayload) continue;
                if (s.Length == 0 || s.StartsWith("#", StringComparison.Ordinal)) continue;
                // payload 段里的非空行必须是列表项。以前少了前导「-」的行被直接吞掉，
                // 整份名单会静默变成空名单（语义＝全部直连），必须记成坏行。
                if (!s.StartsWith("-", StringComparison.Ordinal))
                {
                    badLines.Add(lineNo + 1);
                    continue;
                }
                string body = s.Substring(1).Trim().Trim('\'', '"');
                string[] parts = body.Split(',');
                // 兼容早期/手写的两字段写法；类型不认识 / 名字为空 / 字段过多都算坏行。
                bool nameRule = (parts.Length == 2 || parts.Length == 3) &&
                    parts[0].Trim().Equals("PROCESS-NAME", StringComparison.OrdinalIgnoreCase) &&
                    parts[1].Trim().Length > 0;
                bool pathRule = (parts.Length == 2 || parts.Length == 3) &&
                    parts[0].Trim().Equals("PROCESS-PATH-REGEX", StringComparison.OrdinalIgnoreCase) &&
                    parts[1].Trim().Length > 0;
                if (nameRule)
                    items.Add(new Item(parts[1].Trim(), parts.Length >= 3 && parts[2].Trim().Length > 0
                        ? parts[2].Trim() : "PP-FIXED"));
                else if (pathRule)
                    items.Add(new Item(parts[1].Trim(), parts.Length >= 3 && parts[2].Trim().Length > 0
                        ? parts[2].Trim() : "PP-FIXED", true) { Derived = true });
                else badLines.Add(lineNo + 1);
            }
            // 文件里有内容、却连 payload: 段都没有：这不是空名单，是格式坏了（复审 F-04）。
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
            string temp = SavePrepare(items);
            if (temp == null) return false;
            return SaveCommit(temp, items);
        }

        /// <summary>
        /// 两阶段提交的第一阶段：把新名单完整写到临时文件（不替换正式文件）。
        /// v1.3.7：推送内核与落盘名单不是一个原子事务——以前先推内核再写名单，
        /// 写盘失败时内核已经是新规则、名单还是旧的。现在把「可能失败的大动作」
        /// 提到推送之前：临时文件写成功才推内核，推成功后再做一次原子替换。
        /// </summary>
        internal static string SavePrepare(List<Item> items)
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
                return temp;
            }
            catch (Exception ex)
            {
                LastSaveError = ex.Message;
                CrashLog.Write(ex, "准备勾选名单失败");
                if (temp != null && File.Exists(temp)) { try { File.Delete(temp); } catch { } }
                return null;
            }
        }

        /// <summary>第二阶段：原子替换正式名单。失败时删除临时文件，调用方按 PersistFailed 处理。</summary>
        internal static bool SaveCommit(string temp, List<Item> items)        {
            try
            {
                string path = Paths.ListPath;
                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);
                LastSaveError = "";
                LastLoadError = "";
                LastGoodItems = new List<Item>(items);
                BumpIntentEpoch();
                return true;
            }
            catch (Exception ex)
            {
                LastSaveError = ex.Message;
                CrashLog.Write(ex, "保存勾选名单失败");
                if (temp != null && File.Exists(temp)) { try { File.Delete(temp); } catch { } }
                return false;
            }
        }

        /// <summary>
        /// 备份里保留了订阅的节点密码 / UUID / provider URL（还原需要它们），
        /// 所以至少把目录权限收紧到「当前用户 + 管理员」，不再继承 Users 的读权限。
        /// 失败不阻断备份：ACL 改不动时仍以可用性优先，但说明文档必须写明这一点。
        /// </summary>
        static void ProtectBackupDir(string dir)
        {
            try
            {
                var di = new DirectoryInfo(dir);
                var sec = di.GetAccessControl();
                sec.SetAccessRuleProtection(true, false);   // 不再继承父目录
                var me = System.Security.Principal.WindowsIdentity.GetCurrent().User;
                if (me != null)
                {
                    sec.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                        me, System.Security.AccessControl.FileSystemRights.FullControl,
                        System.Security.AccessControl.InheritanceFlags.ContainerInherit |
                        System.Security.AccessControl.InheritanceFlags.ObjectInherit,
                        System.Security.AccessControl.PropagationFlags.None,
                        System.Security.AccessControl.AccessControlType.Allow));
                }
                sec.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                    new System.Security.Principal.SecurityIdentifier(
                        System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, null),
                    System.Security.AccessControl.FileSystemRights.FullControl,
                    System.Security.AccessControl.InheritanceFlags.ContainerInherit |
                    System.Security.AccessControl.InheritanceFlags.ObjectInherit,
                    System.Security.AccessControl.PropagationFlags.None,
                    System.Security.AccessControl.AccessControlType.Allow));
                di.SetAccessControl(sec);
            }
            catch { }
        }

        /// <summary>推送失败 / 中途放弃时清掉已准备好的临时名单，不留下垃圾文件。</summary>
        static void DiscardPendingList(string temp)        {
            try { if (!string.IsNullOrEmpty(temp) && File.Exists(temp)) File.Delete(temp); }
            catch { }
        }

        /// <summary>
        /// 名单落盘失败后的补偿：按**磁盘上现存的**名单重新生成 payload 推回内核，
        /// 让「内核规则」和「磁盘名单」重新对齐，而不是等看守下一轮（最多 30 秒，
        /// 而且看守被暂停 / 程序正在退出时压根不会来）。
        /// 只在失败路径调用，正常保存不经过这里。
        /// </summary>
        static bool RestoreKernelToDisk(string cfg, out string why)
        {
            why = "";
            try
            {
                var disk = Load();
                string text = BuildPayload(cfg, disk);
                var r = Mihomo.PushConfig(text, 15000, cfg);
                if (r == null || !r.Ok)
                {
                    why = r == null ? "推送无响应"
                        : (r.Error ?? ("HTTP " + r.Status + " " + Trim(r.Body, 120)));
                    return false;
                }
                Traffic.NotePush();
                AuditLog.Record("SAVE", "勾选名单落盘失败，已按磁盘名单补偿推送内核");
                return true;
            }
            catch (Exception ex) { why = ex.Message; return false; }
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
                        // 辅助进程跟随主软件的策略：主软件手改成 DIRECT 时，
                        // 以前 helper 仍写 PP-FIXED → 同一个软件里一半直连、一半走代理。
                        items.Add(new Item(helper, pol) { Derived = true });
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
            // 失败关闭：有勾选规则却找不到可证明的代理组时，绝不能拿空/GLOBAL 去凑。
            // 以前会生成 `PROCESS-NAME,x,GLOBAL`，GLOBAL 在规则模式下常是 DIRECT，
            // 推送还成功、界面显示「已生效」——用户看到的就是「勾了也直连」。
            if (policy.Length == 0 && items != null && items.Count > 0)
                throw new InvalidOperationException(
                    "找不到可用的代理策略组（订阅的 proxy-groups 为空或无法识别）；" +
                    "为免规则指向 GLOBAL/DIRECT，已停止保存。请检查订阅配置里的策略组名称。");
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
        /// <summary>策略组名。传入 knownConfigPath 时直接读本次保存已确认的那份配置，
        /// 不再各自重新识别客户端（独立审阅第二轮：保存事务同源）。</summary>
        public static string PolicyName(string knownConfigPath = null)
        {
            try
            {
                string p = !string.IsNullOrEmpty(knownConfigPath) ? knownConfigPath : Paths.ClashYaml;
                if (string.IsNullOrEmpty(p) || !File.Exists(p)) return "";
                bool reused;
                return Discovery.FindPolicy(File.ReadAllText(p, Encoding.UTF8), out reused);
            }
            catch { return ""; }
        }

        // ---------------------------------------------------------------- 应用

        /// <summary>
        /// 跨会话互斥：Local\ 命名对象只在当前 Windows 会话可见，本地桌面 + 远程桌面
        /// 同时跑本程序时，两边会各拿一把锁，串行保护等于没有。
        /// Global\ 在当前用户下可用；个别环境没有权限创建时退回 Local\（保持原行为）。
        /// </summary>
        internal static System.Threading.Mutex CreateApplyMutex()
        {
            // 名字带上当前用户 SID：跨会话（本地桌面 + 远程桌面）能互斥，
            // 同一台电脑的另一个 Windows 账户又不会撞到同一把锁（否则第二个账户存不了盘）。
            try { return new System.Threading.Mutex(false, ApplyMutexName()); }
            catch { return new System.Threading.Mutex(false, @"Local\ProxyPicker.Apply"); }
        }

        static string ApplyMutexName()
        {
            try
            {
                using (var id = System.Security.Principal.WindowsIdentity.GetCurrent())
                {
                    if (id != null && id.User != null)
                        return @"Global\ProxyPicker.Apply." + id.User.Value;
                }
            }
            catch { }
            return @"Global\ProxyPicker.Apply";
        }

        /// <summary>
        /// 把「旧连接快照 → 推送 → 回读 → 重连」整段放进同一把跨进程锁。
        /// v1.3.7：以前锁只包住 ApplyCore，快照在锁外、删连接在锁释放之后；
        /// 保存进行到一半时托盘还原能插进来，旧保存任务随后还会继续删连接、
        /// 把界面状态改回「已生效」。同一线程重复获取同名 Mutex 是递归的，内部 Apply 不会自锁。
        /// </summary>
        public static T WithApplyLock<T>(Func<T> body)
        {
            using (var gate = CreateApplyMutex())
            {
                bool owned = false;
                try { owned = gate.WaitOne(TimeSpan.FromSeconds(30)); }
                catch (System.Threading.AbandonedMutexException) { owned = true; }
                if (!owned) throw new TimeoutException("另一个 ProxyPicker 正在保存，请稍后再试");
                try { return body(); }
                finally { try { gate.ReleaseMutex(); } catch { } }
            }
        }

        /// <summary>跨进程互斥：命令行 --apply 与界面保存不能交叉写名单 / 推配置（评审 R6-4）。
        /// autoRecover=true 时在锁内推送前再核一次接管意图，消除修复与托盘还原的竞态（评审 P1-②）；
        /// 订阅兼容模式同样核对名单文件——它是接管意图的唯一载体（评审 R1）。</summary>
        public static ApplyOutcome Apply(List<Item> items, bool autoRecover = false,
                                         string knownConfigPath = null, long expectedEpoch = -1,
                                         string expectedIntentSig = null)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            using (var gate = CreateApplyMutex())
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
                try { return ApplyCore(items, sw, autoRecover, knownConfigPath, expectedEpoch, expectedIntentSig); }
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
        static ApplyOutcome ApplyCore(List<Item> items, System.Diagnostics.Stopwatch sw, bool autoRecover,
                                      string knownConfigPath = null, long expectedEpoch = -1,
                                      string expectedIntentSig = null)
        {
            var result = new ApplyOutcome();

            // 竞态消除：自动化补回的推送前必达条件留在临界区内（同一把 Apply 互斥锁）。
            // 否则「修复任务发出前检查意图」与「托盘还原删除名单文件」之间的窗口里：
            // 还原完成，修复任务照旧把接管状态重新推回去，还原等于没做（评审 P1-②）。
            // 评审 R1：曾经这里额外排除了订阅兼容模式（!Settings.PatchOnly），但名单文件
            // 是接管意图的唯一载体，与模式无关——订阅兼容模式下还原同样删名单文件，
            // 若豁免核对，托盘还原后看守照旧补回，还原等于没做。两种模式一律核对。
            // 界面保存也要核对「这次保存开始时拿到的意图代次」：保存等锁期间用户点了托盘还原，
            // 锁释放后旧保存任务不能又把配置推回去（v1.3.7）。
            if (expectedEpoch >= 0 && IntentEpoch != expectedEpoch)
            {
                result.Ok = false;
                result.Message = RevokedIntentMessage;
                result.Ms = sw.ElapsedMilliseconds;
                return result;
            }
            // v1.3.8：跨进程还原。等锁期间另一个 ProxyPicker 进程删掉了名单文件，
            // 本进程的 _intentEpoch 不会变（它是进程内静态字段），只有磁盘签名会变。
            // 只用代次判断等于「跨进程锁挡住了同时写，却没挡住意图被撤销」。
            if (IntentChangedSince(expectedIntentSig))
            {
                result.Ok = false;
                result.Message = RevokedIntentMessage;
                result.Ms = sw.ElapsedMilliseconds;
                return result;
            }
            // 还原时名单文件删不掉 → 看守暂停，直到用户主动保存一次。
            // 否则「还原成功」的下一秒，守卫又按残留名单把规则推回去（v1.3.7）。
            if (autoRecover && Settings.RepairSuspended)
            {
                result.Ok = false;
                result.Message = RevokedIntentMessage;
                result.Ms = sw.ElapsedMilliseconds;
                return result;
            }
            if (AutoRecoverStopped(autoRecover))
            {
                result.Ok = false;
                result.Message = RevokedIntentMessage;
                result.Ms = sw.ElapsedMilliseconds;
                return result;
            }

            // 保存事务同源：调用方已确认配置路径时直接复用，整条链（原值捕获、
            // payload 生成、推送、回读、连接快照与删除）只认这一份，不各自重新识别。
            string cfg = !string.IsNullOrEmpty(knownConfigPath) ? knownConfigPath : Paths.ClashYaml;
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
            // 保存事务同源：原值捕获也用本次已确认的 cfg，不再各走一次 Detect。
            if (Settings.Ipv6GuardOn) Settings.CaptureIpv6Original(cfg);
            // 第一次保存前记下客户端原本的 find-process-mode，退出时才能恢复原值。
            Settings.CaptureFindProcessOriginal(cfg);

            string payload;
            string fpBeforeBuild = ConfigFingerprint(cfg);
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
            // 生成期间客户端正好更新订阅（Verge 会原地重写同一个文件）：这份 payload 基于旧内容，
            // 策略组 / secret 可能已经换了。宁可让用户再点一次保存，也不要拿半新半旧的数据推送。
            if (!string.IsNullOrEmpty(fpBeforeBuild) && ConfigFingerprint(cfg) != fpBeforeBuild)
            {
                result.Ok = false;
                result.Message = "客户端刚更新了订阅配置，本次保存已中止（避免新旧内容混用）；请等订阅更新完成后再保存一次";
                result.Ms = sw.ElapsedMilliseconds;
                return result;
            }

            result.Policy = PolicyName(cfg);
            // 两阶段提交第一阶段：名单先完整写到临时文件。写不进去就别推内核，
            // 避免「内核已经换了规则、磁盘还是旧名单」。
            // 只把「用户勾选的」写回名单；helper / 目录规则是推导出来的，不能落盘成
            // 独立条目，否则取消勾选主软件后它们仍会留在名单里继续代理（F-08）。
            var persist = new List<Item>();
            foreach (var it in items) if (!it.Derived) persist.Add(it);
            string pendingList = SavePrepare(persist);
            if (pendingList == null)
            {
                result.Ok = false;
                result.PersistFailed = true;
                result.Message = "勾选名单无法写入（" + LastSaveError + "），已停止推送；" +
                                 "请检查文件权限或磁盘空间后重新保存";
                result.Ms = sw.ElapsedMilliseconds;
                return result;
            }
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
                DiscardPendingList(pendingList);
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
            // 两阶段提交第二阶段：内核已确认接收新规则，这里只做一次原子替换。
            if (!SaveCommit(pendingList, persist))
            {
                // v1.3.8：名单没落盘、内核却已是新规则。以前只把结果标成「失败」并声称
                // 「后台看守会按原名单恢复」——那要等最多 30 秒，而且看守被暂停时不成立。
                // 现在立刻按磁盘上的旧名单做一次补偿推送，把内核退回去；退不回去就如实标记。
                string backWhy;
                bool rolledBack = RestoreKernelToDisk(cfg, out backWhy);
                result.Ok = false;
                result.PersistFailed = true;
                result.RecoveryRequired = !rolledBack;
                result.Message = "规则已推送，但勾选名单写入失败：" + LastSaveError +
                    (rolledBack
                        ? "；已按磁盘原名单把内核退回，请检查文件权限后重新保存"
                        : "；且把内核退回原名单也失败了（" + backWhy +
                          "）。内核现在是新规则、与磁盘名单不一致，请立即重新保存或点还原");
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
            // 独立审阅第二轮：默认「代理白名单」的硬约束——兜底 MATCH 必须存在、
            // 策略必须是 DIRECT、位置必须在勾选规则之后。以前只在「恰好找到 MATCH」
            // 时查位置，MATCH 消失或被改成 MATCH,PROXY 都会漏检，界面仍显示「名单已生效」。
            // 兼容模式（patchOnly）下订阅规则原样保留，不适用这套自家规则约束。
            if (good && !Settings.PatchOnly)
            {
                int firstMatch = -1, matchCount = 0;
                string matchProxy = "";
                for (int i = 0; i < rules.Count; i++)
                {
                    if (!(rules[i].Type ?? "").Equals("Match", StringComparison.OrdinalIgnoreCase)) continue;
                    matchCount++;
                    if (firstMatch < 0) { firstMatch = i; matchProxy = rules[i].Proxy ?? ""; }
                }
                if (matchCount == 0)
                { good = false; whyBad = "缺少直连兜底 MATCH，未勾选的软件可能被代理"; }
                else if (matchProxy.Length > 0 && !Profile.IsDirectPolicy(matchProxy))
                { good = false; whyBad = "兜底 MATCH 的策略是 " + matchProxy + "，不是 DIRECT，白名单承诺不成立"; }
                else if (items.Count == 0 && firstMatch != 0)
                { good = false; whyBad = "空名单下缺少段首直连兜底"; }
                else if (items.Count > 0 && firstMatch < items.Count)
                { good = false; whyBad = "直连兜底排在勾选规则前面"; }
            }
            if (good)
            {
                Dictionary<string, object> conf = null;
                try { conf = Mihomo.GetJson("/configs", 4000, cfg); }
                catch { conf = null; }
                // v1.3.7：读不到 /configs 时以前直接跳过校验，good 仍为 true。
                // 内核可能正处在 global 模式或关掉进程识别，规则根本不参与匹配，
                // 界面却报「已生效」。现在读不到就按「未能确认」处理，不当成通过。
                if (conf == null)
                {
                    good = false;
                    whyBad = "读不到内核配置（/configs），无法确认规则模式与进程识别是否生效";
                }
                else
                {
                    string mode = Json.Str(conf, "mode");
                    string fpm = Json.Str(conf, "find-process-mode");
                    // v1.3.8：以前只在「字段非空且不等于 rule」时才判失败，于是返回一个
                    // 非空 JSON、但连 mode 都没有时（不是 mihomo，或是残废的兼容实现）
                    // 会被当成「已确认」。mode 是所有版本都有的字段，缺失＝证明不了规则生效。
                    if (string.IsNullOrEmpty(mode))
                    { good = false; whyBad = "内核 /configs 里没有 mode 字段，无法确认当前是不是规则模式"; }
                    else if (!mode.Equals("rule", StringComparison.OrdinalIgnoreCase))
                    { good = false; whyBad = "客户端当前是 " + mode + " 模式，规则不生效，请切到规则模式"; }
                    // strict 是 mihomo 默认档，PROCESS-NAME 规则正常生效；只有 off 才是真的关闭（F1）。
                    // find-process-mode 在老内核里可能没有，缺省不据此判失败，但存在且为 off 必须拦。
                    else if (!string.IsNullOrEmpty(fpm) && fpm.Equals("off", StringComparison.OrdinalIgnoreCase))
                    { good = false; whyBad = "进程识别已关闭（find-process-mode=off）"; }
                }
            }
            // 语义校验：规则写进去了，但策略组本身指向 DIRECT / REJECT / 不存在时，
            // 勾选软件仍然不会走代理。以前只看自动选出的那一个组，而且只认 DIRECT：
            //  - 手工给某个软件指定的策略组完全不查；
            //  - 组切到 REJECT 时勾选软件直接断网，界面仍报「已生效」。
            // 现在按本次实际写入的所有策略组逐条核对，且只取一次 /proxies。
            if (good && items.Count > 0)
            {
                var checks = new List<string>();
                // v1.3.8：只有真有条目用「默认策略」时才核对自动选出的那个组。
                // 以前无条件先把它塞进来：所有软件都手工指定了策略、而自动识别出的默认组
                // 恰好是 DIRECT 时，保存会因为一个**没有任何规则在用**的组而失败。
                bool usesDefault = false;
                foreach (var it in items)
                {
                    string ip = (it.Policy ?? "").Trim();
                    if (ip.Length == 0 || ip.Equals("PP-FIXED", StringComparison.OrdinalIgnoreCase))
                        usesDefault = true;
                }
                if (usesDefault && !string.IsNullOrEmpty(result.Policy)) checks.Add(result.Policy);
                foreach (var it in items)
                {
                    string p = (it.Policy ?? "").Trim();
                    if (p.Length == 0 || p.Equals("PP-FIXED", StringComparison.OrdinalIgnoreCase))
                        p = result.Policy;
                    if (string.IsNullOrEmpty(p)) continue;
                    // 用户手写 DIRECT / REJECT 是明确的意图，不算故障；只校验代理组。
                    if (IsBlockedPolicy(p)) continue;
                    if (!checks.Contains(p, StringComparer.OrdinalIgnoreCase)) checks.Add(p);
                }
                if (checks.Count > 0)
                {
                    var obj = Mihomo.GetJson("/proxies", 6000, cfg);
                    var proxies = obj == null ? null : Json.Obj(obj, "proxies");
                    if (proxies == null)
                    {
                        good = false;
                        whyBad = "读不到内核代理状态（/proxies），无法确认勾选软件的实际出口";
                    }
                    else
                    {
                        foreach (var p in checks)
                        {
                            string global, exit = ResolveIn(proxies, p, out global);
                            if (string.IsNullOrEmpty(exit))
                            {
                                good = false;
                                whyBad = "策略组 " + p + " 在内核里不存在或出口无法确认，规则可能不生效";
                                break;
                            }
                            if (IsBlockedPolicy(exit))
                            {
                                good = false;
                                whyBad = "策略组 " + p + " 当前出口是 " + exit + "，勾选软件不会走代理";
                                break;
                            }
                        }
                    }
                }
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
                ProtectBackupDir(dir);
                // v1.3.7：只读一次。备份内容与本 .meta 里的指纹必须来自同一份快照，
                // 否则 Clash Verge 恰好在读文件与算指纹之间换了订阅，就会出现
                // 「备份是旧订阅 A、指纹是新订阅 B」，还原时被误判为来源一致而整份推回 A。
                byte[] bytes = File.ReadAllBytes(configPath);
                string fingerprint = Sha256Hex(bytes);
                string scrubbed = ScrubSecret(Encoding.UTF8.GetString(bytes));
                string source = configPath ?? "";

                string original = Path.Combine(dir, "original.yaml");
                if (!File.Exists(original))
                {
                    WriteTextAtomic(original, scrubbed);
                    WriteBackupMeta(original + ".meta", source, fingerprint);
                }

                string recent = Path.Combine(dir, "recent.yaml");
                string prev2 = Path.Combine(dir, "recent-2.yaml");
                string prev3 = Path.Combine(dir, "recent-3.yaml");
                // 先把新快照写到临时文件，再轮转；任何一步中断都还能保留上一份完整备份。
                string staged = recent + ".new";
                var utf8 = new UTF8Encoding(false);
                File.WriteAllText(staged, scrubbed, utf8);
                if (File.Exists(prev2)) { File.Copy(prev2, prev3, true); CopyBackupMeta(prev2, prev3); }
                if (File.Exists(recent)) { File.Copy(recent, prev2, true); CopyBackupMeta(recent, prev2); }
                // 轮转的最后一跳同样不能「先删旧文件」：掉电会把最近一份备份一起抹掉。
                PromoteTemp(staged, recent);
                WriteBackupMeta(recent + ".meta", source, fingerprint);
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
            bool cleared;
            return Rollback(out message, out cleared);
        }

        /// <summary>v1.3.7：区分「内核还原成功」与「接管意图已撤销」。
        /// 名单文件删不掉时，内核规则虽然清掉了，但看守下一轮会按文件补回来。</summary>
        public static bool Rollback(out string message, out bool intentCleared)
        {
            intentCleared = false;
            using (var gate = CreateApplyMutex())
            {
                bool owned = false;
                try { owned = gate.WaitOne(TimeSpan.FromSeconds(30)); }
                catch (System.Threading.AbandonedMutexException) { owned = true; }
                if (!owned)
                {
                    message = "另一个 ProxyPicker 正在保存，请稍后再试";
                    return false;
                }
                try { return RollbackCore(out message, out intentCleared); }
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
            using (var gate = CreateApplyMutex())
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

        static bool RollbackCore(out string message, out bool intentCleared)
        {
            intentCleared = false;
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

                // 备份来源核对：新版 .meta 记「来源路径 + 备份时配置内容指纹」。
                // 只比路径不够：Clash Verge Rev 换订阅时路径不变，内容变了，
                // 旧快照整份推回去会把用户刚更新的订阅换掉（独立审阅第二轮）。
                bool metaExists = File.Exists(chosen + ".meta");
                string metaSource = "", metaFingerprint = "";
                if (metaExists)
                    metaExists = ReadBackupMeta(chosen + ".meta", out metaSource, out metaFingerprint);

                string currentCfg = Paths.ClashYaml;
                string sourceWhy;
                bool fullRestore = BackupAllowed(metaSource, currentCfg, metaExists,
                                                 metaFingerprint, ConfigFingerprint(currentCfg),
                                                 out sourceWhy);
                if (!fullRestore)
                {
                    // 不推整份原件，改为基于当前订阅重建一份干净 payload：
                    // 只剔除本工具的规则块，订阅原规则与用户设置逐字保留。
                    string back = BuildCleanRollbackPayload(currentCfg);
                    if (back == null)
                    {
                        message = sourceWhy + "；且当前订阅不可读（" + currentCfg + "），无法重建配置。" +
                                  "请先启动客户端并保存一次，再点还原。";
                        return false;
                    }
                    var rebuild = Mihomo.PushConfig(back, 15000, currentCfg);
                    if (!rebuild.Ok)
                    {
                        message = "推送清理后的配置失败：" + (rebuild.Error ?? ("HTTP " + rebuild.Status));
                        return false;
                    }
                    // 重建分支也必须如实汇报名单删除结果（独立审阅第二轮）：
                    // 以前忽略返回值、无条件写「接管状态已清除」，删失败时看守会重新接管。
                    string clearError2;
                    bool cleared2 = ClearSavedIntent(out clearError2);
                    intentCleared = cleared2;
                    bool suspended2 = false;
                    if (cleared2) Settings.ClearRestoreBaseline();
                    else suspended2 = Settings.SuspendRepair();
                    message = cleared2
                        ? sourceWhy + "；已按当前订阅重建干净配置，接管状态已清除"
                        : sourceWhy + "；内核规则已按当前订阅清除，但勾选名单文件删除失败：" + clearError2 +
                          (suspended2
                              ? "。已暂停后台自动补回，请手动删除该文件后重启本程序"
                              : "。且暂停状态没能写进磁盘：请立刻手动删除该文件，否则重启后看守可能重新接管");
                    return true;
                }

                string text = File.ReadAllText(chosen, Encoding.UTF8);
                if (text.IndexOf("<备份时已移除", StringComparison.Ordinal) >= 0)
                {
                    // 区分「没读到 secret 行」和「合法的无认证配置 secret: ""」；
                    // 后者是合法设置，不能当成密钥读取失败而中止还原。
                    string secret;
                    if (!Mihomo.TryReadSecret(currentCfg, out secret))
                    {
                        message = "备份里的控制密钥已被移除，且当前配置读不到顶层 secret 行；为安全起见已停止还原";
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
                intentCleared = cleared;
                bool suspended = false;
                if (cleared) Settings.ClearRestoreBaseline();
                else suspended = Settings.SuspendRepair();
                if (!cleared)
                {
                    message = "配置已还原，但勾选名单文件删除失败：" + clearError +
                              (suspended
                                  ? "。已暂停后台自动补回，请手动删除该文件后重启本程序"
                                  : "。且暂停状态没能写进磁盘：请立刻手动删除该文件，否则重启后看守可能重新接管");
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

        /// <summary>兜底 MATCH 的策略必须是 DIRECT，否则「未勾选一律直连」不成立。</summary>
        internal static bool IsDirectPolicy(string proxy)
        {
            return (proxy ?? "").Trim().Equals("DIRECT", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 「这条链路不会把流量送到代理」。除了 DIRECT，REJECT / REJECT-DROP 同样不可用：
        /// v1.3.7 以前只判 DIRECT，策略组切到 REJECT 时勾选的软件直接断网，
        /// 但保存校验仍报「已生效」——Release 里写的「DIRECT/REJECT 都会报错」并不成立。
        /// </summary>
        internal static bool IsBlockedPolicy(string proxy)
        {
            var kind = ClassifyExit(proxy);
            return kind == ExitKind.Direct || kind == ExitKind.Rejected;
        }

        /// <summary>
        /// 出口语义的唯一分类口径。保存校验、状态卡、看守、流量对账全部走这一处：
        /// v1.3.7 修好了 Profile 的 IsBlockedPolicy，却把 Traffic.ChainOf 留着自己那套
        /// 字符串判断，漏掉了 REJECT-DROP，于是同一个出口在「保存」和「流量统计」里
        /// 被算成两种东西。以后再新增阻断型策略只改这里。
        /// </summary>
        internal enum ExitKind { Unknown = 0, Proxy = 1, Direct = 2, Rejected = 3 }

        internal static ExitKind ClassifyExit(string proxy)
        {
            string p = (proxy ?? "").Trim();
            if (p.Length == 0) return ExitKind.Unknown;
            if (p.Equals("DIRECT", StringComparison.OrdinalIgnoreCase)) return ExitKind.Direct;
            if (p.Equals("REJECT", StringComparison.OrdinalIgnoreCase) ||
                p.Equals("REJECT-DROP", StringComparison.OrdinalIgnoreCase)) return ExitKind.Rejected;
            return ExitKind.Proxy;
        }

        /// <summary>mihomo 的策略组类型：这些名字出现在 /proxies 里时，没有 now 也不能当终点。</summary>
        internal static bool IsGroupType(string type)
        {
            string t = (type ?? "").Trim();
            return t.Equals("Selector", StringComparison.OrdinalIgnoreCase) ||
                   t.Equals("URLTest", StringComparison.OrdinalIgnoreCase) ||
                   t.Equals("Fallback", StringComparison.OrdinalIgnoreCase) ||
                   t.Equals("LoadBalance", StringComparison.OrdinalIgnoreCase) ||
                   t.Equals("Relay", StringComparison.OrdinalIgnoreCase) ||
                   t.Equals("Compatible", StringComparison.OrdinalIgnoreCase);
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

        public static string ResolveOutbound(out string global, string policy = null,
                                             string knownConfigPath = null)
        {
            global = "";
            var obj = Mihomo.GetJson("/proxies", 6000, knownConfigPath);
            if (obj == null) return "";
            var proxies = Json.Obj(obj, "proxies");
            if (proxies == null) return "";
            return ResolveIn(proxies, policy, out global);
        }

        /// <summary>在已经取到的 /proxies 上解析出口；校验多条策略时只取一次数据。</summary>
        internal static string ResolveIn(Dictionary<string, object> proxies, string policy, out string global)
        {
            global = "";
            if (proxies == null) return "";

            var g = Json.Obj(proxies, "GLOBAL");
            global = Json.Str(g, "now") ?? "";

            // 跟随「本次规则实际写入的策略组」，而不是一律跟 GLOBAL。
            // GLOBAL 在规则模式下经常停在 DIRECT；以前界面因此显示 DIRECT，
            // 用户以为规则没生效（实机截图：明明订阅组是日本，顶部却写 DIRECT）。
            if (!string.IsNullOrEmpty(policy) &&
                (policy.Equals("DIRECT", StringComparison.OrdinalIgnoreCase) ||
                 policy.Equals("REJECT", StringComparison.OrdinalIgnoreCase)))
                return policy.ToUpperInvariant();

            object raw;
            string name;
            if (!string.IsNullOrEmpty(policy))
            {
                // 指定的策略组不在 /proxies 里：不要退回 GLOBAL 假装有出口，
                // 让调用方看到空值并提示「策略组不存在」。
                if (!proxies.ContainsKey(policy)) return "";
                name = policy;
            }
            else if (proxies.TryGetValue("PP-FIXED", out raw)) name = "PP-FIXED";
            else name = "GLOBAL";

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (!string.IsNullOrEmpty(name) && seen.Add(name))
            {
                var node = Json.Obj(proxies, name);
                // 链路指向一个不存在的节点：不能把上一个组名当成终点（以前就是 `break` 后
                // 返回非空名字，于是「无法证明」被当成「有效代理」）。直接返回空，交给调用方报错。
                if (node == null) return "";
                string now = Json.Str(node, "now");
                if (string.IsNullOrEmpty(now))
                {
                    // 没有 now 的可能是真节点，也可能是 LoadBalance 这类没有当前选择的组。
                    // 只有「类型不是策略组」才算终点。
                    string type = Json.Str(node, "type");
                    if (IsGroupType(type) || string.IsNullOrEmpty(type)) return "";
                    return name;
                }
                name = now;
            }
            return "";   // 循环引用 / 超长链路：无法确认终点
        }

        /// <summary>策略组当前链路末端是不是不可用（DIRECT / REJECT / 不存在 / 解析不出）。</summary>
        public static bool PolicyGoesDirect(string policy, string knownConfigPath, out string exit)
        {
            string global;
            exit = ResolveOutbound(out global, policy, knownConfigPath);
            if (string.IsNullOrEmpty(exit)) return true;   // 证明不了有代理出口，按不可用处理
            return IsBlockedPolicy(exit);
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
            st.Policy = PolicyName();
            // 没解析到任何代理组时 FindPolicy 返回空（不再退回 GLOBAL）；GLOBAL 在
            // 规则模式下经常停在 DIRECT，勾选软件也不会走代理。把这句话直接摆到状态里，
            // 别让用户以为「保存成功」就等于分流生效。
            if (string.IsNullOrEmpty(st.Policy))
                st.Error = "找不到可用的代理策略组（订阅可能没有 proxy-groups）；勾选软件不会走代理";
            else if (st.Policy.Equals("GLOBAL", StringComparison.OrdinalIgnoreCase))
                st.Error = "策略组只能指向 GLOBAL（规则模式下常为 DIRECT）；勾选软件可能仍然直连";
            st.Outbound = ResolveOutbound(out global, st.Policy);
            st.Node = global;

            var rules = ReadRules();
            st.RuleCount = rules.Count;

            // 独立审阅第二轮：Applied 必须能证明「本工具的规则确实在内核里」。
            // 以前只看首条规则类型，订阅自己写在段首的 PROCESS-NAME 也会被误判为已生效，
            // 导致刷新后本该立刻补回却跳过。
            var diskItems = Load();
            // 白名单 + 空名单＝用户明确要全部直连，出口就是 DIRECT，
            // 不能拿订阅策略组的节点来显示（否则界面说日本、实际全直连）。
            if (!Settings.PatchOnly && diskItems.Count == 0 && !LastLoadFailed)
                st.Outbound = "DIRECT";
            // 勾了软件、但规则指向的策略组本身是 DIRECT/REJECT：这是「保存成功但没代理」，
            // 必须在状态卡里说清楚，不能只显示一个「已生效」。
            if (diskItems.Count > 0 && !string.IsNullOrEmpty(st.Outbound) &&
                IsBlockedPolicy(st.Outbound))
                st.Error = "策略组 " + st.Policy + " 当前出口是 " + st.Outbound +
                           "；勾选软件不会走代理";
            var mine = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var it in diskItems) if (!string.IsNullOrEmpty(it.Exe)) mine.Add(it.Exe);
            bool hasOwnRule = false, hasDirectFallback = false;
            int firstDirectMatch = -1, lastOwnProcess = -1;
            for (int i = 0; i < rules.Count; i++)
            {
                var r = rules[i];
                string type = r.Type ?? "";
                if (type.StartsWith("Process", StringComparison.OrdinalIgnoreCase) &&
                    (mine.Contains(r.Payload ?? "") ||
                     (r.Proxy ?? "").Equals("PP-FIXED", StringComparison.OrdinalIgnoreCase)))
                { hasOwnRule = true; lastOwnProcess = i; }
                else if (type.Equals("RuleSet", StringComparison.OrdinalIgnoreCase) &&
                         (r.Payload ?? "").IndexOf("pp-whitelist", StringComparison.OrdinalIgnoreCase) >= 0) hasOwnRule = true;
                else if (type.Equals("Match", StringComparison.OrdinalIgnoreCase) && IsDirectPolicy(r.Proxy))
                { hasDirectFallback = true; if (firstDirectMatch < 0) firstDirectMatch = i; }
            }
            // 空名单＝用户明确要「全部直连」，段首的 DIRECT 兜底本身就是生效证据。
            bool emptyAllDirect = diskItems.Count == 0 && HasSavedIntent && firstDirectMatch == 0;
            // 白名单模式还要查位置：兜底 MATCH 必须排在所有自家进程规则之后。
            // 只看「有 MATCH」不够——客户端、扩展或用户把 MATCH 挪到前面时，
            // 勾选的软件会落到直连，状态卡却仍显示「名单已生效」（独立审阅第二轮 P2）。
            // 兼容模式不受这条约束：订阅自带的提前 MATCH 是订阅作者的分流语义。
            bool positionOk = Settings.PatchOnly || !hasOwnRule || firstDirectMatch < 0 ||
                              firstDirectMatch > lastOwnProcess;
            // v1.3.8：兼容模式不要求 DIRECT 兜底。订阅本身写成 MATCH,节点选择 时，
            // 规则生成、保存校验、看守都认可它，只有这张状态卡还在硬要 hasDirectFallback，
            // 于是「实际已生效、界面说没生效」，用户会反复重存。白名单模式仍然必须有直连兜底。
            bool fallbackOk = Settings.PatchOnly || hasDirectFallback;
            if (rules.Count > 0 && (hasOwnRule || emptyAllDirect) && fallbackOk && positionOk)
                st.Applied = true;
            else if (!Settings.PatchOnly && hasOwnRule && hasDirectFallback && !positionOk)
                st.Error = "直连兜底排在勾选规则前面，勾选的软件会走直连";
            // 兼容模式下名单是空的＝本来就没有要生效的东西，不该提示「请保存一次」。
            if (!st.Applied && Settings.PatchOnly && diskItems.Count == 0) st.Applied = true;

            // 兼容模式会原样保留订阅自带的段首 MATCH。那是订阅作者写的分流语义，
            // 不是本工具的故障；以前在这里判「提前 MATCH → 未生效」，和规则生成器、
            // 后台看守互相打架（独立审阅第二轮 P1），现在不再据此推翻 Applied。
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
            /// <summary>404 单独计数：既可能是连接自然结束，也可能是请求打到了别的内核。
            /// 不再默默混进「已关闭」，便于诊断定向重连是否真的作用在同一个客户端上。</summary>
            public int NotFound;
        }

        /// <summary>
        /// 推送新规则「之前」记录改动软件的旧连接 ID。
        /// 必须先快照再推送，这样不会误关推送后刚建立的新连接。
        /// </summary>
        public static ConnectionSnapshot CaptureConnections(IEnumerable<string> changedExes,
                                                            string knownConfigPath = null)
        {
            var snap = new ConnectionSnapshot();
            var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in changedExes ?? new string[0])
            {
                string exe = Normalize(raw);
                if (exe.Length > 0) wanted.Add(exe);
            }
            if (wanted.Count == 0) return snap;

            var root = Mihomo.GetJson("/connections", 10000, knownConfigPath);
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
        public static ReconnectOutcome CloseCapturedConnections(ConnectionSnapshot snap,
                                                               string knownConfigPath = null)
        {
            var outcome = new ReconnectOutcome();
            if (snap == null) return outcome;
            // v1.3.7：以前写「4 路并发、最坏 ceil(N/4)×2.5s」，但每条请求最终都走
            // Mihomo.Request() 里的 ChannelGate，实际仍是严格串行——注释承诺的耗时是假的。
            // 现在如实按顺序做，并给整批一个总预算：超过预算就停手，剩下的大多是同一个
            // 连不上的内核，继续等只会让保存卡更久。
            int attempted = 0, closed = 0, failed = 0, notFound = 0;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            int budgetMs = Math.Min(15000, Math.Max(3000, snap.Ids.Count * 900));
            foreach (var id in snap.Ids)
            {
                if (clock.ElapsedMilliseconds > budgetMs) break;
                attempted++;
                var r = Mihomo.DeleteConnection(id, 2500, knownConfigPath);
                if (r.Ok) closed++;
                else if (r.Status == 404) notFound++;
                else failed++;
            }
            outcome.Attempted = attempted;
            outcome.Closed = closed;
            outcome.Failed = failed;
            outcome.NotFound = notFound;
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
