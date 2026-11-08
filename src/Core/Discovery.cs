using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace ProxyPicker.Core
{
    /// <summary>
    /// 通用性层：不假设你装的是哪一个客户端，也不假设配置文件长什么样。
    ///
    /// 发现顺序：
    ///   1. 常见客户端的数据目录（Clash Verge / Verge Rev / Nyanpasu / Mihomo Party / FlClash / CFW）
    ///   2. 正在运行的 mihomo / clash 内核所在目录（兜底：绿色版、自己编译的）
    ///   3. 目录里挑一份「含 rules: 的」运行配置（优先 clash-verge.yaml / config.yaml）
    ///
    /// 生成规则时也不依赖固定锚点：
    ///   在 rules: 段首插入 PROCESS-NAME 规则 + MATCH,DIRECT 兜底，
    ///   并自动补齐 find-process-mode: always 与可用的策略组名。
    ///   这样换一台电脑、换一个客户端，逻辑不变。
    /// </summary>
    internal static class Discovery
    {
        public sealed class Env
        {
            public string Client = "";
            public string DataDir = "";
            public string ConfigPath = "";
            public string Secret = "";
            public string Controller = "";     // host:port，空表示没有 TCP 控制面
            public string Error;
            public bool Ok { get { return !string.IsNullOrEmpty(ConfigPath); } }
        }

        static Env _env;
        static DateTime _at = DateTime.MinValue;
        static readonly object Gate = new object();

        public static Env Detect(bool refresh = false)
        {
            lock (Gate)
            {
                if (!refresh && _env != null && (DateTime.UtcNow - _at).TotalSeconds < 5) return _env;
                _env = DetectCore();
                _at = DateTime.UtcNow;
                return _env;
            }
        }

        static Env DetectCore()
        {
            var env = new Env();
            var tried = new List<string>();

            foreach (var dir in CandidateDirs())
            {
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
                if (tried.Contains(dir)) continue;
                tried.Add(dir);

                string cfg = PickConfig(dir);
                if (cfg == null) continue;

                env.DataDir = dir;
                env.ConfigPath = cfg;
                env.Client = GuessClient(dir);
                ReadController(cfg, env);
                return env;
            }

            env.Error = "没有找到 Clash / mihomo 的配置文件";
            return env;
        }

        /// <summary>配置目录候选。顺序即优先级。</summary>
        public static IEnumerable<string> CandidateDirs()
        {
            string appData = SafeFolder(Environment.SpecialFolder.ApplicationData);
            string local = SafeFolder(Environment.SpecialFolder.LocalApplicationData);
            string profile = SafeFolder(Environment.SpecialFolder.UserProfile);

            var list = new List<string>();
            if (appData != null)
            {
                list.Add(Path.Combine(appData, "io.github.clash-verge-rev.clash-verge-rev"));
                list.Add(Path.Combine(appData, "io.github.clash-nyanpasu.clash-nyanpasu"));
                list.Add(Path.Combine(appData, "clash-verge"));
                list.Add(Path.Combine(appData, "clash-nyanpasu"));
                list.Add(Path.Combine(appData, "mihomo-party"));
                list.Add(Path.Combine(appData, "mihomo-party", "work"));
                list.Add(Path.Combine(appData, "com.follow.clash"));
                list.Add(Path.Combine(appData, "io.github.flclash"));
                list.Add(Path.Combine(appData, "clash-for-windows"));
            }
            if (local != null)
            {
                list.Add(Path.Combine(local, "mihomo-party"));
                list.Add(Path.Combine(local, "Programs", "mihomo-party"));
            }
            if (profile != null)
            {
                list.Add(Path.Combine(profile, ".config", "clash"));
                list.Add(Path.Combine(profile, ".config", "mihomo"));
            }

            // 兜底：内核进程所在目录（绿色版/自定义安装）
            try
            {
                foreach (var p in Process.GetProcesses())
                {
                    try
                    {
                        string name = (p.ProcessName ?? "").ToLowerInvariant();
                        if (name.IndexOf("mihomo") < 0 && name.IndexOf("clash") < 0 &&
                            name.IndexOf("nyanpasu") < 0 && name.IndexOf("flclash") < 0) continue;
                        string file = null;
                        try { file = p.MainModule != null ? p.MainModule.FileName : null; }
                        catch { }
                        if (string.IsNullOrEmpty(file)) continue;
                        string dir = Path.GetDirectoryName(file);
                        if (!string.IsNullOrEmpty(dir))
                        {
                            list.Add(dir);
                            string up = Path.GetDirectoryName(dir);
                            if (!string.IsNullOrEmpty(up)) list.Add(up);
                        }
                    }
                    catch { }
                    finally { try { p.Dispose(); } catch { } }
                }
            }
            catch { }

            return list;
        }

        static string SafeFolder(Environment.SpecialFolder f)
        {
            try { return Environment.GetFolderPath(f); } catch { return null; }
        }

        /// <summary>在一个目录里挑最像「运行期配置」的 yaml。</summary>
        public static string PickConfig(string dir)
        {
            string[] prefer =
            {
                "clash-verge.yaml", "config.yaml", "runtime.yaml",
                "mihomo.yaml", "clash.yaml", "verge.yaml"
            };
            foreach (var name in prefer)
            {
                string p = Path.Combine(dir, name);
                if (File.Exists(p) && LooksLikeConfig(p)) return p;
            }

            try
            {
                foreach (var p in Directory.GetFiles(dir, "*.yaml", SearchOption.TopDirectoryOnly))
                    if (LooksLikeConfig(p)) return p;
                foreach (var p in Directory.GetFiles(dir, "*.yml", SearchOption.TopDirectoryOnly))
                    if (LooksLikeConfig(p)) return p;
            }
            catch { }
            return null;
        }

        /// <summary>只读前 256 KB 做结构判断，不整份读入，避免大配置拖慢启动。</summary>
        public static bool LooksLikeConfig(string path)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length < 32) return false;
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var sr = new StreamReader(fs, Encoding.UTF8))
                {
                    char[] buf = new char[256 * 1024];
                    int n = sr.Read(buf, 0, buf.Length);
                    string text = new string(buf, 0, n);
                    if (text.IndexOf("\nrules:", StringComparison.Ordinal) < 0 &&
                        !text.StartsWith("rules:", StringComparison.Ordinal)) return false;
                    bool hasProxy = text.IndexOf("\nproxies:", StringComparison.Ordinal) >= 0 ||
                                    text.IndexOf("\nproxy-groups:", StringComparison.Ordinal) >= 0;
                    return hasProxy;
                }
            }
            catch { return false; }
        }

        static string GuessClient(string dir)
        {
            string d = (dir ?? "").ToLowerInvariant();
            if (d.IndexOf("clash-verge-rev") >= 0) return "Clash Verge Rev";
            if (d.IndexOf("nyanpasu") >= 0) return "Clash Nyanpasu";
            if (d.IndexOf("mihomo-party") >= 0) return "Mihomo Party";
            if (d.IndexOf("follow.clash") >= 0 || d.IndexOf("flclash") >= 0) return "FlClash";
            if (d.IndexOf("clash-for-windows") >= 0) return "Clash for Windows";
            if (d.IndexOf("clash-verge") >= 0) return "Clash Verge";
            return "mihomo";
        }

        static void ReadController(string cfg, Env env)
        {
            try
            {
                foreach (var raw in File.ReadLines(cfg))
                {
                    string line = raw.Trim();
                    if (line.StartsWith("#", StringComparison.Ordinal)) continue;
                    if (line.StartsWith("secret:", StringComparison.Ordinal))
                    {
                        env.Secret = line.Substring(7).Trim().Trim('\'', '"');
                    }
                    else if (line.StartsWith("external-controller:", StringComparison.Ordinal))
                    {
                        string v = line.Substring("external-controller:".Length).Trim().Trim('\'', '"');
                        if (v.Length > 0 && v.IndexOf(':') > 0) env.Controller = v;
                    }
                }
            }
            catch { }
        }

        /// <summary>
        /// 从配置里挑一个可用的策略名：优先沿用已有的 PP-FIXED，
        /// 其次常见「代理组」，最后退回 GLOBAL。这样别人的电脑上不用手工建组。
        /// </summary>
        public static string FindPolicy(string yaml, out bool reused)
        {
            reused = false;
            var names = GroupNames(yaml);
            foreach (var n in names)
                if (n.Equals("PP-FIXED", StringComparison.OrdinalIgnoreCase)) { reused = true; return n; }

            string[] keys = { "代理", "节点", "proxy", "PROXY", "Proxy", "select", "选择", "🚀", "🔰", "手动" };
            foreach (var k in keys)
                foreach (var n in names)
                    if (n.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0) return n;

            if (names.Count > 0) return names[0];
            return "GLOBAL";
        }

        public static List<string> GroupNames(string yaml)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(yaml)) return list;
            string[] lines = yaml.Replace("\r\n", "\n").Split('\n');
            bool inGroups = false;
            int groupIndent = -1;
            foreach (var raw in lines)
            {
                if (raw.Length == 0) continue;
                string t = raw.TrimEnd();
                int indent = t.Length - t.TrimStart().Length;
                string s = t.TrimStart();

                if (!inGroups)
                {
                    if (s == "proxy-groups:") { inGroups = true; groupIndent = indent; }
                    continue;
                }
                if (s.StartsWith("#", StringComparison.Ordinal)) continue;

                // 结束条件只看「顶格的键」：这份配置的列表项是顶格写的（- name: X），
                // 所以不能用 indent <= groupIndent 判断，否则第一行就退出了。
                if (indent <= groupIndent && !s.StartsWith("-", StringComparison.Ordinal)) break;

                string body = s;
                if (body.StartsWith("- ", StringComparison.Ordinal)) body = body.Substring(2).Trim();
                if (body.StartsWith("name:", StringComparison.Ordinal))
                {
                    string v = body.Substring(5).Trim().Trim('\'', '"');
                    if (v.Length > 0) list.Add(v);
                }
            }
            return list;
        }

        /// <summary>
        /// 通用 payload 生成：在 rules: 段首插入规则，并保证 find-process-mode 为 always。
        /// 幂等：每次先移除上一次插入的 PROCESS-NAME 块，不会越积越多。
        ///
        /// patchOnly 决定「没勾的软件」怎么办，这是两种模式的唯一差别：
        ///   false（完全接管）：段首写 PROCESS-NAME 名单 + MATCH,DIRECT，后面原规则整段让位。
        ///   true（只改勾的）：段首只插 PROCESS-NAME 名单，原规则一条不动，未勾的继续按订阅分流。
        /// </summary>
        public static string BuildPayload(string yaml, IList<Profile.Item> items, string policy, bool patchOnly)
        {
            if (string.IsNullOrEmpty(yaml))
                throw new InvalidOperationException("配置内容为空");

            string text = yaml.Replace("\r\n", "\n");

            // 1. find-process-mode: always（进程匹配的前提）
            text = EnsureFindProcessAlways(text);

            // 2. 定位 rules: 段
            string[] lines = text.Split('\n');
            int rulesIdx = -1;
            for (int i = 0; i < lines.Length; i++)
                if (lines[i].TrimEnd() == "rules:") { rulesIdx = i; break; }
            if (rulesIdx < 0)
                throw new InvalidOperationException("这份配置里没有 rules: 段，无法安全插入分流规则");

            // 3. 收集 rules 段开头的规则块
            int p = rulesIdx + 1;
            var block = new List<string>();
            while (p < lines.Length)
            {
                string t = lines[p].Trim();
                if (t.Length == 0 || t.StartsWith("#", StringComparison.Ordinal) || t.StartsWith("-", StringComparison.Ordinal))
                {
                    block.Add(lines[p]);
                    p++;
                    continue;
                }
                break;
            }

            // 4a. 历史遗留：早期版本在规则段首写过 `RULE-SET,pp-whitelist,...`，紧跟一条
            //     `MATCH,DIRECT`。这条 MATCH 同样会把后面几百条订阅规则全部短路，
            //     而且它排第二、不是第一位，下面「只看第一位」的判据抓不到它。
            //     只认「规则集紧跟 MATCH」这一个组合，并且只看段首三行，
            //     不动订阅自己写在正常位置的 MATCH。
            int head = 0;
            while (head < block.Count && (block[head].Trim().Length == 0 || block[head].Trim().StartsWith("#", StringComparison.Ordinal))) head++;
            if (head + 1 < block.Count && IsPpWhitelistRuleSet(block[head]) && Compact(block[head + 1]) == "-MATCH,DIRECT")
                block.RemoveAt(head + 1);

            // 4. 去掉上一次生成的块（连续 PROCESS-NAME + 紧随其后的 MATCH,DIRECT）
            int k = 0;
            while (k < block.Count && (block[k].Trim().Length == 0 || block[k].Trim().StartsWith("#", StringComparison.Ordinal))) k++;
            while (k < block.Count && block[k].Trim().StartsWith("- PROCESS-NAME,", StringComparison.OrdinalIgnoreCase))
                k++;
            // 这条 MATCH,DIRECT 也要一并吞掉。判据不是「删到了 PROCESS-NAME」，而是「它在规则段第一位」——
            // MATCH 是兜底规则，正常的订阅永远把它写在最后，出现在第一位只可能是我们自己上次插的。
            // 不这么改的话，用户把勾选全部取消（名单为空）会生成一条孤零零的 MATCH,DIRECT，
            // 下次再勾选时它删不掉，于是每保存一次就多一条，越积越多。
            //
            // 两种模式都要吞，兼容模式也一样：磁盘上那份配置如果已经被上一次接管写过，
            // 段首就留着这条短路规则 —— 不清理的话「切成只改勾的」等于没切，
            // 后面几百条订阅规则照样一条都匹配不到。
            if (k < block.Count && Compact(block[k]) == "-MATCH,DIRECT") k++;

            // 5. 生成新块。缩进跟随原有规则：有的配置写 rules:\n  - xxx，插到顶格会让 YAML 变成非法结构。
            string indent = "";
            foreach (var line in block)
            {
                string t = line.TrimStart();
                if (t.StartsWith("-", StringComparison.Ordinal))
                {
                    indent = line.Substring(0, line.Length - t.Length);
                    break;
                }
            }

            var fresh = new List<string>();
            for (int i = 0; i < items.Count; i++)
                fresh.Add(indent + "- PROCESS-NAME," + items[i].Exe + "," + policy);
            if (!patchOnly) fresh.Add(indent + "- MATCH,DIRECT");

            var rebuilt = new List<string>();
            rebuilt.AddRange(fresh);
            for (int i = k; i < block.Count; i++) rebuilt.Add(block[i]);

            // 兼容模式的安全网：整份规则里连一条 MATCH 都没有的话，mihomo 会拒绝加载。
            // 从「完全接管」切回来时确实会出现这种局面（原来的兜底被上一次接管删掉过）。
            if (patchOnly)
            {
                bool hasMatch = false;
                foreach (var line in rebuilt)
                    if (Compact(line).StartsWith("-MATCH,", StringComparison.OrdinalIgnoreCase)) { hasMatch = true; break; }
                if (!hasMatch) rebuilt.Add(indent + "- MATCH,DIRECT");
            }

            var sb = new StringBuilder(text.Length + rebuilt.Count * 40);
            for (int i = 0; i < rulesIdx; i++) sb.Append(lines[i]).Append('\n');
            sb.Append("rules:").Append('\n');
            foreach (var line in rebuilt) sb.Append(line).Append('\n');
            for (int i = p; i < lines.Length; i++)
            {
                sb.Append(lines[i]);
                if (i < lines.Length - 1) sb.Append('\n');
            }

            // 结尾统一成一个换行：源文件末尾本来就是空行时，拼装后会多出一个，
            // 导致第二次生成比第一次长 1 字节（幂等自检就是抓这个的）。
            return sb.ToString().TrimEnd('\n') + "\n";
        }

        static string Compact(string line) { return (line ?? "").Replace(" ", "").Replace("\t", "").Trim(); }

        /// <summary>是不是我们自己早期注册的那条 pp-whitelist 规则集。</summary>
        static bool IsPpWhitelistRuleSet(string line)
        {
            string c = Compact(line);
            return c.StartsWith("-RULE-SET,", StringComparison.OrdinalIgnoreCase) &&
                   c.IndexOf("pp-whitelist", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>没有就补上，有但不是 always 就改掉。位置放在 mode: 后面，保持顶层语义。</summary>
        static string EnsureFindProcessAlways(string text)
        {
            string[] lines = text.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].TrimStart().StartsWith("find-process-mode:", StringComparison.Ordinal) &&
                    (lines[i].Length - lines[i].TrimStart().Length) == 0)
                {
                    if (lines[i].Trim().Equals("find-process-mode: always", StringComparison.OrdinalIgnoreCase)) return text;
                    lines[i] = "find-process-mode: always";
                    return string.Join("\n", lines);
                }
            }

            var list = new List<string>(lines);
            int at = -1;
            for (int i = 0; i < list.Count; i++)
                if (list[i].StartsWith("mode:", StringComparison.Ordinal)) { at = i + 1; break; }
            if (at < 0)
                for (int i = 0; i < list.Count; i++)
                    if (list[i].StartsWith("mixed-port:", StringComparison.Ordinal) ||
                        list[i].StartsWith("port:", StringComparison.Ordinal)) { at = i + 1; break; }
            if (at < 0) at = 0;
            list.Insert(at, "find-process-mode: always");
            return string.Join("\n", list);
        }

        /// <summary>
        /// 通用性自检：用三份「不同人写的」配置检验规则生成本身，
        /// 不碰真机配置，只做字符串处理。--selftest 会把它写成报告。
        /// </summary>
        public static string SelfTest()
        {
            int pass = 0, fail = 0;
            var sb = new StringBuilder();
            sb.AppendLine("ProxyPicker 通用性自检");
            sb.AppendLine();

            Action<string, bool, string> check = (name, ok, detail) =>
            {
                if (ok) pass++; else fail++;
                sb.AppendLine((ok ? "PASS  " : "FAIL  ") + name + (detail.Length > 0 ? "  · " + detail : ""));
            };

            var items = new List<Profile.Item>
            {
                new Profile.Item("msedge.exe", "PP-FIXED"),
                new Profile.Item("weixin.exe", "PP-FIXED")
            };

            // A. Clash Verge 风格：列表顶格、已有 PP-FIXED 组
            string a = "mixed-port: 7897\nmode: rule\nproxies:\n  - name: n1\n    type: ss\n" +
                       "proxy-groups:\n- name: PP-FIXED\n  type: select\n  proxies:\n  - n1\n" +
                       "rules:\n- RULE-SET,pp-whitelist,PP-FIXED\n- MATCH,DIRECT\n- GEOIP,CN,DIRECT\n";
            bool reused;
            string policyA = FindPolicy(a, out reused);
            check("A 识别已有分组 PP-FIXED", policyA == "PP-FIXED" && reused, policyA);

            string pa = BuildPayload(a, items, policyA, false);
            check("A 规则插在 rules 段首", pa.IndexOf("rules:\n- PROCESS-NAME,msedge.exe,PP-FIXED", StringComparison.Ordinal) >= 0, "");
            check("A 紧跟 MATCH,DIRECT 兜底", pa.IndexOf("- PROCESS-NAME,weixin.exe,PP-FIXED\n- MATCH,DIRECT", StringComparison.Ordinal) >= 0, "");
            check("A 补上 find-process-mode", pa.IndexOf("find-process-mode: always", StringComparison.Ordinal) >= 0, "");

            // B. Nyanpasu 风格：列表缩进、组名是 emoji、原本没有 find-process-mode
            string b = "port: 7890\nmode: rule\nproxies: []\nproxy-groups:\n  - name: 🚀 节点选择\n    type: select\n" +
                       "rules:\n  - DOMAIN-SUFFIX,google.com,🚀 节点选择\n  - MATCH,DIRECT\n";
            string policyB = FindPolicy(b, out reused);
            check("B 自动选用可用的代理组", policyB == "🚀 节点选择", policyB);

            string pb = BuildPayload(b, items, policyB, false);
            check("B 缩进风格也能插入规则", pb.IndexOf("rules:\n  - PROCESS-NAME,msedge.exe,🚀 节点选择", StringComparison.Ordinal) >= 0, "");
            check("B 原有规则保留在兜底之后", pb.IndexOf("  - MATCH,DIRECT\n  - DOMAIN-SUFFIX,google.com", StringComparison.Ordinal) >= 0, "");
            check("B 没有分组时也能补 find-process-mode", pb.IndexOf("\nmode: rule\nfind-process-mode: always\n", StringComparison.Ordinal) >= 0, "");

            // C. 裸 mihomo：没有 proxy-groups，退回 GLOBAL
            string c = "mixed-port: 7890\nproxies:\n  - name: n1\n    type: ss\nrules:\n- MATCH,DIRECT\n";
            string policyC = FindPolicy(c, out reused);
            check("C 没有分组时退回 GLOBAL", policyC == "GLOBAL", policyC);
            string pc = BuildPayload(c, items, policyC, false);
            check("C 仍能生成合法规则", pc.IndexOf("- PROCESS-NAME,msedge.exe,GLOBAL", StringComparison.Ordinal) >= 0, "");

            // D. 幂等：同一份配置重复生成，结果必须一致，否则规则会越滚越长
            string pb2 = BuildPayload(pb, items, policyB, false);
            check("D 重复生成结果一致（幂等）", pb2 == pb, pb2 == pb ? "长度 " + pb.Length : "长度 " + pb.Length + " → " + pb2.Length);

            // E. 换机器时列表越用越长是最常见的退化，这里守住条数
            int processRuleCount = CountOccurrences(pb, "- PROCESS-NAME,");
            check("E 只写勾选项（2 条）", processRuleCount == 2, "实际 " + processRuleCount + " 条");

            // F. 兼容模式：只往最前面插，订阅原有规则一条都不能动
            string pf = BuildPayload(b, items, policyB, true);
            check("F 兼容模式保留原订阅规则", pf.IndexOf("DOMAIN-SUFFIX,google.com", StringComparison.Ordinal) >= 0, "");
            check("F 兼容模式兜底只有一条", CountOccurrences(pf, "- MATCH,DIRECT") == 1,
                "实际 " + CountOccurrences(pf, "- MATCH,DIRECT") + " 条");
            check("F 兼容模式插在原规则之前", pf.IndexOf("  - PROCESS-NAME,weixin.exe", StringComparison.Ordinal) <
                pf.IndexOf("  - DOMAIN-SUFFIX", StringComparison.Ordinal), "");

            // G. 兼容模式也必须幂等：第二次保存不能把规则越插越长
            string pf2 = BuildPayload(pf, items, policyB, true);
            check("G 兼容模式重复生成一致（幂等）", pf2 == pf,
                pf2 == pf ? "长度 " + pf.Length : "长度 " + pf.Length + " → " + pf2.Length);

            // H. 空名单：不能留下孤儿 MATCH，也不能把原规则删掉
            string pe = BuildPayload(b, new List<Profile.Item>(), policyB, true);
            check("H 兼容模式空名单不动原配置",
                CountOccurrences(pe, "- PROCESS-NAME,") == 0 &&
                pe.IndexOf("DOMAIN-SUFFIX,google.com", StringComparison.Ordinal) >= 0 &&
                CountOccurrences(pe, "- MATCH,DIRECT") == 1, "");

            // I. 从「完全接管」切回兼容时，如果磁盘配置已被上一次接管删成没有兜底，
            //    必须自动补一条，否则 mihomo 会拒绝加载整份配置（等于梯子直接挂掉）。
            string dirty = "mode: rule\nproxies: []\nrules:\n- PROCESS-NAME,msedge.exe,PP-FIXED\n";
            string patched = BuildPayload(dirty, items, "PP-FIXED", true);
            check("I 缺兜底时自动补回 MATCH", CountOccurrences(patched, "- MATCH,DIRECT") == 1, "");

            // J. 磁盘配置被上一次接管写过（段首留着 MATCH,DIRECT），切回兼容时必须清掉，
            //    否则那条短路规则会让后面所有订阅规则永远匹配不到 —— 切了等于没切。
            string polluted = "mode: rule\nproxies: []\nrules:\n- MATCH,DIRECT\n- DOMAIN-SUFFIX,google.com,🚀 节点选择\n";
            string clean = BuildPayload(polluted, items, "🚀 节点选择", true);
            check("J 兼容模式清掉遗留的短路兜底",
                clean.IndexOf("rules:\n- MATCH,DIRECT", StringComparison.Ordinal) < 0 &&
                clean.IndexOf("DOMAIN-SUFFIX,google.com", StringComparison.Ordinal) >= 0, "");
            check("J 兜底被挪到规则末尾",
                clean.TrimEnd('\n').EndsWith("- MATCH,DIRECT", StringComparison.Ordinal), "");

            // K. 更老的一种遗留：段首是 `RULE-SET,pp-whitelist`，它后面紧跟一条 MATCH,DIRECT。
            //    这条 MATCH 排第二、不是第一位，J 的判据抓不到，但它照样短路后面所有规则。
            string legacy = "mode: rule\nproxies: []\nrules:\n- RULE-SET,pp-whitelist,PP-FIXED\n- MATCH,DIRECT\n- DOMAIN-SUFFIX,google.com,PP-FIXED\n";
            string fixedK = BuildPayload(legacy, items, "PP-FIXED", true);
            check("K 清掉规则集后面那条遗留 MATCH",
                CountOccurrences(fixedK, "- MATCH,DIRECT") == 1 &&
                fixedK.IndexOf("DOMAIN-SUFFIX,google.com", StringComparison.Ordinal) >= 0, "");
            check("K 规则集本身保留",
                CountOccurrences(fixedK, "RULE-SET,pp-whitelist") == 1, "");
            check("K 清完仍是幂等的",
                BuildPayload(fixedK, items, "PP-FIXED", true) == fixedK, "");

            sb.AppendLine();
            sb.AppendLine("合计 " + (pass + fail) + " 项：" + pass + " 通过 / " + fail + " 失败");
            return sb.ToString();
        }

        static int CountOccurrences(string text, string token)
        {
            int n = 0, i = 0;
            while (true)
            {
                i = text.IndexOf(token, i, StringComparison.Ordinal);
                if (i < 0) return n;
                n++; i += token.Length;
            }
        }

        /// <summary>诊断信息：出问题时让用户能一眼看出卡在哪。</summary>
        public static string Diagnose()
        {
            var env = Detect(true);
            var sb = new StringBuilder();
            sb.AppendLine("ProxyPicker 环境诊断");
            sb.AppendLine("客户端：" + (env.Client.Length > 0 ? env.Client : "未识别"));
            sb.AppendLine("数据目录：" + (env.DataDir.Length > 0 ? env.DataDir : "-"));
            sb.AppendLine("配置文件：" + (env.ConfigPath.Length > 0 ? env.ConfigPath : "-"));
            sb.AppendLine("控制地址：" + (env.Controller.Length > 0 ? env.Controller : "命名管道"));
            sb.AppendLine("Secret：" + (env.Secret.Length > 0 ? "已读取" : "空"));
            sb.AppendLine("系统：" + Environment.OSVersion.VersionString + " / " + (Environment.Is64BitProcess ? "x64" : "x86"));
            sb.AppendLine("DPI 缩放：" + UI.Theme.S.ToString("0.##"));
            var pack = Mihomo.Request("GET", "/version", null, 4000);
            string ver = "";
            try
            {
                var o = Json.AsObject(Json.Parse(pack.Body));
                ver = Json.Str(o, "version") ?? "";
            }
            catch { }
            sb.AppendLine("内核：" + (pack.Ok ? (ver.Length > 0 ? ver : "已连接") : ("连不上 · " + (pack.Error ?? ("HTTP " + pack.Status)))));
            sb.AppendLine("管道：" + (Mihomo.LastPipeName ?? "未发现"));
            var st = Profile.ReadKernelState();
            sb.AppendLine("模式：" + st.Mode + " · TUN " + (st.Tun ? "开" : "关") + " · 规则 " + st.RuleCount + " 条 · " + (st.Applied ? "名单生效" : "名单未生效"));
            sb.AppendLine("节点：" + (st.Node.Length > 0 ? st.Node : "未读取"));
            if (env.Error != null) sb.AppendLine("错误：" + env.Error);
            return sb.ToString();
        }
    }
}
