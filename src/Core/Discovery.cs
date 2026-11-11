using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
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
            /// <summary>自动选择时的依据，写进 --diag，方便用户判断选错没有。</summary>
            public string SelectionReason = "";
            /// <summary>多客户端但没有明显运行中的那一个时给出提醒；空 = 无需提醒。</summary>
            public string Warning = "";
            public bool Running;
            public bool Ok { get { return !string.IsNullOrEmpty(ConfigPath); } }
        }

        /// <summary>一个装了/放了运行期配置的客户端候选，用于多客户端选择。</summary>
        public sealed class Candidate
        {
            public string Client = "";
            public string DataDir = "";
            public string ConfigPath = "";
            public string Secret = "";
            public string Controller = "";
            public bool ClientRunning;        // 客户端 UI / 内核进程名对得上
            public bool KernelDir;            // 正在运行的内核命令行 -d 指向这里
            public bool ControllerListening;  // 本机控制端口可连接
            public DateTime ConfigWriteUtc;
            public int Order;
            public int Score;
            public string Reason = "";
        }

        static Env _env;
        static DateTime _at = DateTime.MinValue;
        static List<Candidate> _lastCandidates;
        static readonly object Gate = new object();

        /// <summary>Detect 调用次数（含缓存命中）。仅供自检核对「同源链路不再二次 Detect」，
        /// 生产逻辑不读它；用 long 避免长时间运行溢出（收尾批 F-03）。</summary>
        internal static long DetectCalls;

        public static Env Detect(bool refresh = false)
        {
            DetectCalls++;
            lock (Gate)
            {
                if (!refresh && _env != null && (DateTime.UtcNow - _at).TotalSeconds < 5) return _env;
                _env = DetectCore();
                _at = DateTime.UtcNow;
                return _env;
            }
        }

        /// <summary>所有候选客户端（只读）；--diag 和多客户端选择用它。</summary>
        public static List<Candidate> Inspect(bool refresh = false)
        {
            Detect(refresh);
            lock (Gate)
            {
                var copy = new List<Candidate>();
                if (_lastCandidates != null) copy.AddRange(_lastCandidates);
                return copy;
            }
        }

        static Env DetectCore()
        {
            var env = new Env();
            var all = CollectCandidates();
            _lastCandidates = all;
            if (all.Count == 0)
            {
                env.Error = "没有找到 Clash / mihomo 的配置文件";
                return env;
            }

            // 已按得分降序：运行中的内核目录 > 客户端进程在跑 > 控制端口能连 > 配置最近更新。
            var best = all[0];
            env.DataDir = best.DataDir;
            env.ConfigPath = best.ConfigPath;
            env.Client = best.Client;
            env.Secret = best.Secret;
            env.Controller = best.Controller;
            env.Running = best.ClientRunning || best.KernelDir;
            env.SelectionReason = best.Reason;

            if (all.Count > 1)
            {
                int running = 0;
                foreach (var c in all)
                    if (c.ClientRunning || c.KernelDir) running++;
                if (running == 0)
                    env.Warning = "检测到 " + all.Count + " 个客户端配置，但没有看到正在运行的客户端；" +
                                  "已按最近更新选择「" + best.Client + "」。如果选错了，请启动你要用的那个客户端后重开本程序。";
                else if (running > 1)
                    env.Warning = "检测到 " + running + " 个客户端在运行，已选择「" + best.Client + "」（" + best.Reason +
                                  "）。如果不对，请只留一个客户端运行，或在诊断里确认数据目录。";
            }
            return env;
        }

        /// <summary>把本机所有「有运行期配置」的客户端列出来并打分；只读、无副作用。</summary>
        public static List<Candidate> CollectCandidates()
        {
            var list = new List<Candidate>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var kernelDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var d in FromKernelCommandLine())
                    if (!string.IsNullOrEmpty(d)) kernelDirs.Add(NormDir(d));
            }
            catch { }
            var running = RunningProcessNames();

            foreach (var raw in CandidateDirs())
            {
                if (string.IsNullOrEmpty(raw) || !Directory.Exists(raw)) continue;
                string dir = NormDir(raw);
                if (dir.Length == 0 || !seen.Add(dir)) continue;

                string cfg;
                try { cfg = PickConfig(dir); } catch { cfg = null; }
                if (cfg == null) continue;

                var c = new Candidate
                {
                    DataDir = dir,
                    ConfigPath = cfg,
                    Client = GuessClient(dir),
                    Order = list.Count
                };
                string secret, controller;
                ReadController(cfg, out secret, out controller);
                c.Secret = secret;
                c.Controller = controller;
                c.KernelDir = kernelDirs.Contains(dir);
                c.ClientRunning = IsClientRunning(dir, running);
                // 只为「看起来没在跑」的候选探端口：运行中的走命名管道时不需要，
                // 也避免每 5 秒无谓地连一次 9097。
                if (!c.KernelDir && !c.ClientRunning)
                    c.ControllerListening = ControllerListening(controller);
                try { c.ConfigWriteUtc = File.GetLastWriteTimeUtc(cfg); } catch { c.ConfigWriteUtc = DateTime.MinValue; }
                c.Score = CandidateScore(c);
                c.Reason = CandidateReason(c);
                list.Add(c);
            }

            list.Sort((a, b) =>
            {
                int d = b.Score.CompareTo(a.Score);
                return d != 0 ? d : a.Order.CompareTo(b.Order);
            });
            return list;
        }

        /// <summary>得分规则做成纯函数，便于自检覆盖「运行中优先于更新但没运行」。</summary>
        internal static int CandidateScore(Candidate c)
        {
            if (c == null) return int.MinValue;
            int score = 10 - Math.Min(c.Order, 9);
            if (c.KernelDir) score += 1000;
            if (c.ClientRunning) score += 300;
            if (c.ControllerListening) score += 150;
            double ageMin = (DateTime.UtcNow - c.ConfigWriteUtc).TotalMinutes;
            if (ageMin < 0) ageMin = 0;
            if (ageMin <= 10) score += 120;
            else if (ageMin <= 60) score += 80;
            else if (ageMin <= 24 * 60) score += 30;
            return score;
        }

        static string CandidateReason(Candidate c)
        {
            var parts = new List<string>();
            if (c.KernelDir) parts.Add("运行中的内核命令行指向此目录");
            if (c.ClientRunning) parts.Add("客户端进程正在运行");
            if (c.ControllerListening) parts.Add("控制端口可连接");
            double ageMin = (DateTime.UtcNow - c.ConfigWriteUtc).TotalMinutes;
            if (ageMin >= 0 && ageMin <= 24 * 60)
                parts.Add(ageMin <= 10 ? "配置 10 分钟内更新过" : "配置 24 小时内更新过");
            if (parts.Count == 0) parts.Add("按候选顺序/最近更新时间选择");
            return string.Join("、", parts.ToArray());
        }

        static string NormDir(string dir)
        {
            try { return (dir ?? "").Trim().TrimEnd('\\'); }
            catch { return ""; }
        }

        static HashSet<string> RunningProcessNames()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var p in Process.GetProcesses())
                {
                    try { if (!string.IsNullOrEmpty(p.ProcessName)) set.Add(p.ProcessName); }
                    catch { }
                    finally { try { p.Dispose(); } catch { } }
                }
            }
            catch { }
            return set;
        }

        /// <summary>客户端进程名和目录的对应关系。宁可少认，不能把没开的客户端认成在跑。</summary>
        static bool IsClientRunning(string dir, HashSet<string> running)
        {
            if (running == null || running.Count == 0) return false;
            string d = (dir ?? "").ToLowerInvariant();
            foreach (var name in running)
            {
                string n = name.ToLowerInvariant();
                // `*-service.exe` 是常驻服务，不是「梯子开着」的证据；
                // 否则 Clash Verge 关了界面、只留服务，也会被误判为运行中。
                if (n.IndexOf("service", StringComparison.Ordinal) >= 0) continue;
                if (d.IndexOf("clash-verge-rev") >= 0 || d.IndexOf("clash-verge") >= 0)
                {
                    if (n == "clash-verge" || n == "verge-mihomo") return true;
                }
                else if (d.IndexOf("mihomo-party") >= 0)
                {
                    if (n.IndexOf("mihomo-party") >= 0) return true;
                }
                else if (d.IndexOf("nyanpasu") >= 0)
                {
                    if (n.IndexOf("nyanpasu") >= 0) return true;
                }
                else if (d.IndexOf("flclash") >= 0 || d.IndexOf("follow.clash") >= 0)
                {
                    if (n.IndexOf("flclash") >= 0) return true;
                }
                else if (d.IndexOf("clash-for-windows") >= 0)
                {
                    if (n.IndexOf("clash-for-windows") >= 0 || n.IndexOf("clash-win") >= 0) return true;
                }
                else
                {
                    // 裸内核 / 自定义目录：只有明确叫 mihomo 或 clash 的进程才算。
                    if (n == "mihomo" || n == "clash" || n.IndexOf("mihomo") == 0 || n.IndexOf("clash") == 0) return true;
                }
            }
            return false;
        }

        /// <summary>只做本机 TCP 连接探测，不发任何控制请求、不读取密钥。</summary>
        static bool ControllerListening(string controller)
        {
            if (string.IsNullOrEmpty(controller)) return false;
            string host;
            int port;
            if (!SplitEndpoint(controller, out host, out port)) return false;
            if (!IsLoopbackHost(host)) return false;
            string key = host + ":" + port;
            // 探测结果缓存 30 秒：Detect() 每 5 秒就可能重来一轮，没开的客户端
            // 每轮白等一次 120ms 连接超时，多装几个就拖慢刷新。结果只有「没开的
            // 客户端」在用，而它开起来的瞬间进程检查（不缓存）先翻正，不靠这里。
            lock (ProbeGate)
            {
                bool cached;
                DateTime at;
                if (ProbeOk.TryGetValue(key, out cached) && ProbeAt.TryGetValue(key, out at) &&
                    (DateTime.UtcNow - at) < ProbeTtl)
                    return cached;
            }
            bool ok = ProbeTcp(host, port);
            lock (ProbeGate)
            {
                if (ProbeOk.Count > 32) { ProbeOk.Clear(); ProbeAt.Clear(); }
                ProbeOk[key] = ok;
                ProbeAt[key] = DateTime.UtcNow;
            }
            return ok;
        }

        static readonly Dictionary<string, bool> ProbeOk =
            new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, DateTime> ProbeAt =
            new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        static readonly object ProbeGate = new object();
        static readonly TimeSpan ProbeTtl = TimeSpan.FromSeconds(30);

        static bool ProbeTcp(string host, int port)
        {
            try
            {
                using (var c = new TcpClient())
                {
                    var ar = c.BeginConnect(host, port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(120)) return false;
                    c.EndConnect(ar);
                    return true;
                }
            }
            catch { return false; }
        }

        static bool SplitEndpoint(string raw, out string host, out int port)
        {
            host = ""; port = 0;
            string v = (raw ?? "").Trim().Trim('\'', '"');
            int colon = v.LastIndexOf(':');
            if (colon <= 0) return false;
            host = v.Substring(0, colon).Trim().Trim('[', ']');
            return int.TryParse(v.Substring(colon + 1).Trim(), out port) && port > 0 && port < 65536;
        }

        static bool IsLoopbackHost(string host)
        {
            return host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
                   host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                   host.Equals("::1", StringComparison.OrdinalIgnoreCase);
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
                list.Add(Path.Combine(appData, "clash"));
                list.Add(Path.Combine(appData, "mihomo"));
            }
            if (local != null)
            {
                list.Add(Path.Combine(local, "mihomo-party"));
                list.Add(Path.Combine(local, "Programs", "mihomo-party"));
                list.Add(Path.Combine(local, "Programs", "clash-verge"));
                list.Add(Path.Combine(local, "clash-verge"));
                list.Add(Path.Combine(local, "clash"));
            }
            if (profile != null)
            {
                list.Add(Path.Combine(profile, ".config", "clash"));
                list.Add(Path.Combine(profile, ".config", "mihomo"));
                list.Add(Path.Combine(profile, "clash"));
                list.Add(Path.Combine(profile, "mihomo"));
            }

            // 兜底一：内核进程的命令行。绿色版 / 自定义安装常把数据目录写在 -d 后面，
            // 那个目录不在任何已知位置，命令行是换台电脑也能找到的唯一可靠线索。
            try { foreach (var dir in FromKernelCommandLine()) list.Add(dir); } catch { }

            // 兜底二：内核进程所在目录（绿色版/自定义安装）
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

        /// <summary>
        /// 从正在运行的内核进程命令行里挖数据目录：
        ///   -d / --directory &lt;dir&gt;、-f / --config &lt;file&gt;，以及等号写法。
        /// 绿色版或自定义安装时，配置目录不在任何已知位置，命令行是唯一可靠线索；
        /// 换一台电脑（例如朋友的机器）时这条兜底经常是「能不能识别出来」的分水岭。
        /// 只读查询，不执行、不修改任何东西；WMI 不可用就安静返回空列表。
        /// </summary>
        static List<string> FromKernelCommandLine()
        {
            var dirs = new List<string>();
            try
            {
                using (var q = new System.Management.ManagementObjectSearcher(
                    "SELECT CommandLine FROM Win32_Process WHERE Name LIKE '%mihomo%' OR Name LIKE '%clash%' " +
                    "OR Name LIKE '%verge%' OR Name LIKE '%nyanpasu%' OR Name LIKE '%flclash%'"))
                {
                    foreach (System.Management.ManagementBaseObject mo in q.Get())
                    {
                        string cmd = "";
                        try { cmd = Convert.ToString(mo["CommandLine"]) ?? ""; } catch { }
                        AddDirsFromCommandLine(cmd, dirs);
                    }
                }
            }
            catch { }
            return dirs;
        }

        static void AddDirsFromCommandLine(string cmd, List<string> into)
        {
            if (string.IsNullOrEmpty(cmd)) return;
            var args = SplitCommandLine(cmd);
            for (int i = 0; i < args.Count; i++)
            {
                string a = args[i];
                string value = null;
                int eq = a.IndexOf('=');
                if (eq > 0)
                {
                    string head = a.Substring(0, eq).ToLowerInvariant();
                    if (head == "-d" || head == "--directory" || head == "-f" || head == "--config")
                        value = a.Substring(eq + 1);
                }
                else if (a == "-d" || a == "--directory" || a == "-f" || a == "--config")
                {
                    if (i + 1 < args.Count) value = args[i + 1];
                }
                else if (a.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) ||
                         a.EndsWith(".yml", StringComparison.OrdinalIgnoreCase))
                {
                    value = a;
                }
                if (string.IsNullOrEmpty(value)) continue;
                value = value.Trim('"', '\'');
                try
                {
                    if (value.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) ||
                        value.EndsWith(".yml", StringComparison.OrdinalIgnoreCase))
                    {
                        string d = Path.GetDirectoryName(value);
                        if (!string.IsNullOrEmpty(d) && Directory.Exists(d) && !into.Contains(d)) into.Add(d);
                    }
                    else if (Directory.Exists(value) && !into.Contains(value)) into.Add(value);
                }
                catch { }
            }
        }

        /// <summary>按 Windows 的引号规则粗分命令行，够用了：只关心 -d 后面的那个路径。</summary>
        static List<string> SplitCommandLine(string cmd)
        {
            var list = new List<string>();
            var sb = new StringBuilder();
            bool quoted = false;
            foreach (char c in cmd)
            {
                if (c == '"') { quoted = !quoted; continue; }
                if (!quoted && (c == ' ' || c == '\t'))
                {
                    if (sb.Length > 0) { list.Add(sb.ToString()); sb.Length = 0; }
                    continue;
                }
                sb.Append(c);
            }
            if (sb.Length > 0) list.Add(sb.ToString());
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

        /// <summary>
        /// 先读前 256 KB 做结构判断；这段里没有 rules: 时继续把剩余部分读完，
        /// 避免「大量节点写在 rules: 前面」的真配置被整份跳过（评审 R4-8）。上限 8 MiB。
        /// </summary>
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
                    var sb = new StringBuilder(new string(buf, 0, n));
                    while (sb.Length < 8 * 1024 * 1024)
                    {
                        string text = sb.ToString();
                        bool hasRules = text.IndexOf("\nrules:", StringComparison.Ordinal) >= 0 ||
                                        text.StartsWith("rules:", StringComparison.Ordinal);
                        bool hasProxy = text.IndexOf("\nproxies:", StringComparison.Ordinal) >= 0 ||
                                        text.IndexOf("\nproxy-groups:", StringComparison.Ordinal) >= 0;
                        if (hasRules) return hasProxy;
                        int more = sr.Read(buf, 0, buf.Length);
                        if (more <= 0) return false;
                        sb.Append(buf, 0, more);
                    }
                    return false;
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

        /// <summary>
        /// 入口：把路径拿到的 secret / controller 填进候选环境。算法自 EndRecovery 一致，
        /// 重复调用只是重新覆盖同一份 env，安全。
        /// </summary>
        internal static void ReadController(string cfg, Env env)
        {
            string secret, controller;
            ReadController(cfg, out secret, out controller);
            env.Secret = secret;
            env.Controller = controller;
        }

        /// <summary>读取指定配置里的 secret: 与 external-controller: 两行。
        /// 评审 R4：把这个解析放在 Discovery 而非 Mihomo 里，保证配置读取只此一种实现，
        /// 绑定链（Mihomo.Endpoint）复用它按 knownConfigPath 取值，避免第二套解析逻辑。</summary>
        internal static void ReadController(string cfg, out string secret, out string controller)
        {
            bool found;
            ReadController(cfg, out secret, out controller, out found);
        }

        /// <summary>剥掉 YAML 值的行尾注释与成对引号，不管缩进（调用方负责限定顶层）。</summary>
        internal static string StripYamlComment(string value)
        {
            string s = (value ?? "").Trim();
            if (s.Length == 0) return "";
            if (s[0] == '\'' || s[0] == '"')
            {
                char q = s[0];
                int end = s.IndexOf(q, 1);
                return end > 0 ? s.Substring(1, end - 1) : s.Trim(q);
            }
            if (s[0] == '#') return "";
            int hash = s.IndexOf(" #", StringComparison.Ordinal);
            if (hash >= 0) s = s.Substring(0, hash);
            return s.Trim();
        }

        /// <summary>
        /// 只读顶层的 secret: / external-controller: 两行。
        /// 独立审阅第二轮：以前先 Trim() 再逐行找，tun/dns 等嵌套块里的同名键会覆盖真正的
        /// 控制密钥；行尾注释也会被当成密钥的一部分。现在要求行首无缩进。
        /// secretFound 用来区分「读失败」和「合法的无认证配置 secret: ""」。
        /// </summary>
        internal static void ReadController(string cfg, out string secret, out string controller,
                                            out bool secretFound)
        {
            secret = "";
            controller = "";
            secretFound = false;
            try
            {
                foreach (var raw in File.ReadLines(cfg))
                {
                    if (string.IsNullOrEmpty(raw)) continue;
                    if (raw[0] == ' ' || raw[0] == '\t') continue;   // 只认顶层
                    string line = raw.Trim();
                    if (line.StartsWith("#", StringComparison.Ordinal)) continue;
                    if (line.StartsWith("secret:", StringComparison.Ordinal))
                    {
                        secret = StripYamlComment(line.Substring(7));
                        secretFound = true;
                    }
                    else if (line.StartsWith("external-controller:", StringComparison.Ordinal))
                    {
                        string v = StripYamlComment(line.Substring("external-controller:".Length));
                        if (v.Length > 0 && v.IndexOf(':') > 0) controller = v;
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
            // 含逗号 / # / 引号的组名不能拼进 PROCESS-NAME 规则，自动挑组时直接跳过（评审 R4-5）。
            var names = GroupNames(yaml).FindAll(n =>
                !string.IsNullOrEmpty(n) &&
                n.IndexOfAny(new char[] { ',', '#', '\n', '\r', '\'', '"' }) < 0);
            foreach (var n in names)
                if (n.Equals("PP-FIXED", StringComparison.OrdinalIgnoreCase)) { reused = true; return n; }

            // 评审 R4-1：没有 PP-FIXED 时不能让「游戏代理」这类组抢在主节点组前面。
            // 先找「节点 + 选择」同时出现的组，再按明确的常见命名找，最后才用泛关键字。
            foreach (var n in names)
                if (n.IndexOf("节点", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    n.IndexOf("选择", StringComparison.OrdinalIgnoreCase) >= 0) return n;
            string[] sharp = { "🚀", "🔰", "select", "selector", "手动切换", "选择" };
            foreach (var k in sharp)
                foreach (var n in names)
                    if (n.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0) return n;
            string[] keys = { "proxy", "节点", "代理", "手动" };
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
            return BuildPayload(yaml, items, policy, patchOnly, null);
        }

        /// <param name="findProcessMode">
        /// null = 保存时确保 always；"absent" = 删除该行（退出还原用）；其它值 = 恢复成用户原值。
        /// </param>
        public static string BuildPayload(string yaml, IList<Profile.Item> items, string policy, bool patchOnly,
                                          string findProcessMode)
        {
            if (string.IsNullOrEmpty(yaml))
                throw new InvalidOperationException("配置内容为空");

            string text = yaml.Replace("\r\n", "\n");
            text = string.IsNullOrEmpty(findProcessMode)
                ? EnsureFindProcessAlways(text)
                : EnsureFindProcess(text, findProcessMode);

            // 1. 定位顶格 rules: 段头。允许行内注释和空数组写法（评审 R1-4）。
            string[] lines = text.Split('\n');
            int rulesIdx = -1;
            for (int i = 0; i < lines.Length; i++)
            {
                string raw = lines[i].TrimEnd();
                if (raw.Length - raw.TrimStart().Length > 0) continue;
                string t = raw.Trim();
                if (t == "rules:" || t.StartsWith("rules:", StringComparison.Ordinal) &&
                    (t.Length == 6 || t[6] == ' ' || t[6] == '\t' || t[6] == '#')) { rulesIdx = i; break; }
            }
            if (rulesIdx < 0)
                throw new InvalidOperationException("没找到规则段，请把 rules: 恢复成列表形式（rules: 或 rules: []）");

            // rules: [] 后面不能接 - 列表项，改写成 rules:；行内注释保留。
            string head = lines[rulesIdx].Trim();
            string rest = head.Length > 6 ? head.Substring(6).Trim() : "";
            if (rest.StartsWith("[]", StringComparison.Ordinal))
            {
                string comment = rest.Substring(2).Trim();
                lines[rulesIdx] = "rules:" + (comment.Length > 0 ? " " + comment : "");
            }

            // 2. 收集整个 rules 段：列表项、注释、空行。
            int p = rulesIdx + 1;
            var block = new List<string>();
            while (p < lines.Length)
            {
                string t = lines[p].Trim();
                if (t.Length == 0 || t.StartsWith("#", StringComparison.Ordinal) || t.StartsWith("-", StringComparison.Ordinal))
                { block.Add(lines[p]); p++; continue; }
                break;
            }

            // 3. 只删「能确认是本工具生成的」旧块（评审 R1-1 / R3-1 / R3-2 / R6-1 / R4-4）。
            //    用户自己写在段首的 PROCESS-NAME（例如强制 DIRECT）一律原样保留。
            block = StripMarkedBlock(block);
            block = StripLegacyRuleSet(block);
            // 订阅兼容模式（patchOnly）必须原样保留段首 MATCH,DIRECT：
            // 程序每次都从没被改过的磁盘订阅重新生成，这一条只可能是用户 / 订阅自己写的，
            // 删掉会直接改变分流语义（外部评审 #4）。
            // 完全接管模式仍清一次：自家规则本来就插在段首，旧兜底留着只是死规则，
            // 且空名单场景依赖「段首只有一条 MATCH」的回读校验。
            if (!patchOnly) block = StripHeadMatch(block);
            block = TrimBlankEdges(block);

            // 4. 缩进跟随原有规则：有的配置写 rules:\n  - xxx，插到顶格会让 YAML 变成非法结构。
            string indent = "";
            foreach (var line in block)
            {
                string t = line.TrimStart();
                if (t.StartsWith("-", StringComparison.Ordinal))
                { indent = line.Substring(0, line.Length - t.Length); break; }
            }

            // 5. 新块用显式标记包住，下次整块替换；手改名单里的策略字段现在真正生效（评审 R1-8）。
            var fresh = new List<string>();
            if (items.Count > 0)
            {
                fresh.Add(indent + "# PP-BEGIN  ProxyPicker 维护的规则块；本块之外的规则一律保留");
                for (int i = 0; i < items.Count; i++)
                {
                    bool pathRule = items[i].IsPathRule;
                    string target = pathRule ? items[i].PathRegex : (items[i].Exe ?? "");
                    string pol = ItemPolicy(items[i], policy);
                    AssertRuleToken(target, pathRule ? "目录正则" : "软件名");
                    AssertRuleToken(pol, "策略名");
                    fresh.Add(indent + (pathRule ? "- PROCESS-PATH-REGEX," : "- PROCESS-NAME,") + target + "," + pol);
                }
                if (!patchOnly) fresh.Add(indent + "- MATCH,DIRECT");
                fresh.Add(indent + "# PP-END");
            }

            var rebuilt = new List<string>();
            rebuilt.AddRange(fresh);
            rebuilt.AddRange(block);

            // 兼容模式的安全网：整份规则里连一条 MATCH 都没有的话，mihomo 会拒绝加载。
            if (patchOnly)
            {
                bool hasMatch = false;
                foreach (var line in rebuilt)
                    // 独立审阅第二轮：用能剥引号 / 行尾注释的 RuleText 判定，
                    // 以前 Compact(...).StartsWith("-MATCH,") 认不出 `- 'MATCH,DIRECT' # note`，
                    // 会误判「没有兜底」而在尾部再追加一条，造成重复 MATCH。
                    if (IsMatchDirect(line)) { hasMatch = true; break; }
                if (!hasMatch) rebuilt.Add(indent + "- MATCH,DIRECT");
            }
            else if (items.Count == 0)
            {
                // 完全接管 + 空名单：一个都没勾，语义是「全部流量直连」。
                // 必须把 MATCH,DIRECT 放到段首短路掉订阅规则（整体让位），而不是加到尾部：
                //  - 放尾部时订阅规则仍先匹配，违背「全部直连」语义；
                //  - 回读校验（Profile.cs:530）要求空名单首条是 Match，尾部会误报「缺少直连兜底」；
                //  - 尾部的 MATCH 不在它该在的位置，StripHeadMatch 也够不着它。
                //    注：不会「每次保存累加一条」——每次生成都以未被本工具改过的磁盘订阅为基线，
                //    结果只推给内核、不回写磁盘（早期审查报告里的「累加」结论是误报）。
                rebuilt.Insert(0, indent + "- MATCH,DIRECT");
            }

            var sb = new StringBuilder(text.Length + rebuilt.Count * 40);
            for (int i = 0; i < rulesIdx; i++) sb.Append(lines[i]).Append('\n');
            sb.Append(lines[rulesIdx]).Append('\n');
            foreach (var line in rebuilt) sb.Append(line).Append('\n');
            for (int i = p; i < lines.Length; i++)
            {
                sb.Append(lines[i]);
                if (i < lines.Length - 1) sb.Append('\n');
            }

            // 结尾统一成一个换行，保证对同一份输出再生成一次逐字节相同。
            return sb.ToString().TrimEnd('\n') + "\n";
        }

        static string Compact(string line) { return (line ?? "").Replace(" ", "").Replace("\t", "").Trim(); }

        /// <summary>去掉列表符号、整行引号和行尾注释，拿到规则正文；带引号的旧规则也能识别。</summary>
        static string RuleText(string line)
        {
            string s = (line ?? "").Trim();
            // 顺序很重要：先剥行尾注释，再去列表符号和引号。
            // 以前先 Trim 引号，`'MATCH,DIRECT' # 注释` 剥完还剩一个尾引号，
            // 导致提前 MATCH 识别不到（复审 F-06）。
            int hash = s.IndexOf('#');
            if (hash >= 0) s = s.Substring(0, hash);
            s = s.Trim();
            if (s.StartsWith("-", StringComparison.Ordinal)) s = s.Substring(1).Trim();
            s = s.Trim('\'', '"').Trim();
            return s;
        }

        static bool IsMatchDirect(string line)
        {
            return RuleText(line).Equals("MATCH,DIRECT", StringComparison.OrdinalIgnoreCase);
        }

        static bool IsBlankOrComment(string line)
        {
            string t = (line ?? "").Trim();
            return t.Length == 0 || t.StartsWith("#", StringComparison.Ordinal);
        }

        /// <summary>
        /// 只删「带本工具标记」的块，不再按策略名猜归属。
        ///
        /// 第 18 轮复审（F-01）实测：段首连续三条用户手写的
        /// `PROCESS-NAME,keep-a.exe,PP-FIXED` 会被旧逻辑当成本工具残留静默删除。
        /// `PP-FIXED` 是公开文档里写过的组名，用户完全可能照着写，所以
        /// 「策略名 = PP-FIXED」不能作为归属判据。现在只认完整标记块；
        /// 早期无标记版本留下的规则一律不动（宁可留一条旧规则，也不删用户配置）。
        ///
        /// 标记残缺时做「有限清理」：完整块整段删除；孤立 BEGIN 删掉其后连续的
        /// 自家规则；孤立 END 删掉其前连续的自家规则；用户写的 `# PP-BEGIN note`
        /// 不再被当成标记（只认精确的两种本工具标记）。
        /// </summary>
        static List<string> StripMarkedBlock(List<string> block)
        {
            var res = new List<string>();
            int i = 0;
            while (i < block.Count)
            {
                if (IsBeginMarker(block[i]))
                {
                    int end = i + 1;
                    while (end < block.Count && !IsEndMarker(block[end])) end++;
                    // 裸 `# PP-BEGIN` 可能是用户自己的分区注释：只有块内确实有
                    // 本工具的 PROCESS-*,PP-FIXED 规则时才认定为自家块；
                    // 否则原样保留（复审 F-02：用户 DIRECT 规则不能被吞）。
                    bool ours = IsOurBeginHeader(block[i]) ||
                                (end < block.Count && HasOwnRule(block, i + 1, end));
                    if (ours)
                    {
                        if (end < block.Count) { i = end + 1; continue; }   // 完整块：连内容带 END 一起丢弃
                        // 只有 BEGIN（配置被截断）：丢掉标记和其后连续的自家规则。
                        i++;
                        while (i < block.Count && IsOwnRuleLine(block[i])) i++;
                        continue;
                    }
                    // 不是自家块：把 BEGIN 到 END 原样保留，不能因为长得像就删。
                    if (end < block.Count)
                    {
                        for (int k = i; k <= end; k++) res.Add(block[k]);
                        i = end + 1;
                    }
                    else { res.Add(block[i]); i++; }
                    continue;
                }
                if (IsEndMarker(block[i]))
                {
                    // 孤立 END：把它前面连续的自家规则一起丢掉，再丢 END 本身。
                    while (res.Count > 0 && IsOwnRuleLine(res[res.Count - 1]))
                        res.RemoveAt(res.Count - 1);
                    i++;
                    continue;
                }
                res.Add(block[i]);
                i++;
            }
            return res;
        }

        /// <summary>本工具写出的规则行：进程名/目录正则 + PP-FIXED 策略。</summary>
        static bool IsOwnRuleLine(string line)
        {
            string r = RuleText(line);
            if (!r.StartsWith("PROCESS-NAME,", StringComparison.OrdinalIgnoreCase) &&
                !r.StartsWith("PROCESS-PATH-REGEX,", StringComparison.OrdinalIgnoreCase))
                return false;
            int comma = r.LastIndexOf(',');
            if (comma <= 0) return false;
            return r.Substring(comma + 1).Trim().Equals("PP-FIXED", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>只认本工具写出的两种 BEGIN：无注释的旧标记，以及带说明的新标记。</summary>
        static bool IsBeginMarker(string line)
        {
            string t = (line ?? "").Trim();
            if (t.Equals("# PP-BEGIN", StringComparison.OrdinalIgnoreCase)) return true;
            return IsOurBeginHeader(line);
        }

        private static bool IsOurBeginHeader(string line)
        {
            string t = (line ?? "").Trim();
            return t.StartsWith("# PP-BEGIN  ProxyPicker", StringComparison.OrdinalIgnoreCase) ||
                   t.StartsWith("# PP-BEGIN ProxyPicker", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>块内是否至少有一条本工具规则；没有就当普通用户注释处理。</summary>
        static bool HasOwnRule(List<string> block, int from, int to)
        {
            for (int i = from; i < to && i < block.Count; i++)
                if (IsOwnRuleLine(block[i])) return true;
            return false;
        }

        static bool IsEndMarker(string line)
        {
            return (line ?? "").Trim().Equals("# PP-END", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>去掉规则段首尾的空行，避免空 rules 配置每保存一次多一行（F-16）。</summary>
        static List<string> TrimBlankEdges(List<string> block)
        {
            int a = 0, b = block.Count - 1;
            while (a <= b && block[a].Trim().Length == 0) a++;
            while (b >= a && block[b].Trim().Length == 0) b--;
            var res = new List<string>();
            for (int i = a; i <= b; i++) res.Add(block[i]);
            return res;
        }

        /// <summary>删掉早期注册的 pp-whitelist 规则集，以及紧跟（允许隔注释/空行）的短路 MATCH。</summary>
        static List<string> StripLegacyRuleSet(List<string> block)
        {
            var drop = new HashSet<int>();
            for (int i = 0; i < block.Count; i++)
            {
                if (!IsPpWhitelistRuleSet(block[i])) continue;
                drop.Add(i);
                int j = i + 1;
                while (j < block.Count && IsBlankOrComment(block[j])) j++;
                if (j < block.Count && IsMatchDirect(block[j])) drop.Add(j);
            }
            if (drop.Count == 0) return block;
            var res = new List<string>();
            for (int k = 0; k < block.Count; k++)
                if (!drop.Contains(k)) res.Add(block[k]);
            return res;
        }

        /// <summary>
        /// 完全接管时清掉段首第一条 MATCH,DIRECT（自家规则会插到更前面）。
        /// 只允许在 !patchOnly 下调用：订阅兼容模式里段首 MATCH 属于用户原始规则，不能动。
        /// </summary>
        static List<string> StripHeadMatch(List<string> block)
        {
            for (int i = 0; i < block.Count; i++)
            {
                if (IsBlankOrComment(block[i])) continue;
                if (!IsMatchDirect(block[i])) return block;
                var res = new List<string>(block);
                res.RemoveAt(i);
                return res;
            }
            return block;
        }

        /// <summary>名单里手写的 DIRECT / 第三方组名现在按条目生效；PP-FIXED 映射到自动选出的主组。</summary>
        static string ItemPolicy(Profile.Item it, string policy)
        {
            string p = (it.Policy ?? "").Trim();
            if (p.Length == 0 || p.Equals("PP-FIXED", StringComparison.OrdinalIgnoreCase)) return policy;
            return p;
        }

        /// <summary>mihomo 规则按逗号分字段，含逗号 / # / 引号 / 换行的名字必须拒绝而不是拼进去。</summary>
        static void AssertRuleToken(string token, string what)
        {
            if (string.IsNullOrEmpty(token) ||
                token.IndexOfAny(new char[] { ',', '#', '\n', '\r', '\'', '"' }) >= 0)
                throw new InvalidOperationException(what + "含逗号、#、引号或换行，无法安全写入规则：" + token);
        }

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
            return EnsureFindProcess(text, "always");
        }

        /// <summary>
        /// 把顶层 find-process-mode 改成指定值；"absent" 表示原本没有这一行，退出还原时删掉。
        /// 只动顶格的那一行，别碰 rules 里的同名文本。
        /// </summary>
        static string EnsureFindProcess(string text, string mode)
        {
            string[] lines = text.Split('\n');
            string want = "find-process-mode: " + mode;
            for (int i = 0; i < lines.Length; i++)
            {
                string t = lines[i];
                if (!t.TrimStart().StartsWith("find-process-mode:", StringComparison.Ordinal)) continue;
                if ((t.Length - t.TrimStart().Length) != 0) continue;
                var list = new List<string>(lines);
                if (mode.Equals("absent", StringComparison.OrdinalIgnoreCase)) list.RemoveAt(i);
                else if (t.Trim().Equals(want, StringComparison.OrdinalIgnoreCase)) return text;
                else list[i] = want;
                return string.Join("\n", list);
            }
            if (mode.Equals("absent", StringComparison.OrdinalIgnoreCase)) return text;

            var insert = new List<string>(lines);
            int at = -1;
            for (int i = 0; i < insert.Count; i++)
                if (insert[i].StartsWith("mode:", StringComparison.Ordinal)) { at = i + 1; break; }
            if (at < 0)
                for (int i = 0; i < insert.Count; i++)
                    if (insert[i].StartsWith("mixed-port:", StringComparison.Ordinal) ||
                        insert[i].StartsWith("port:", StringComparison.Ordinal)) { at = i + 1; break; }
            if (at < 0) at = 0;
            insert.Insert(at, want);
            return string.Join("\n", insert);
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
                sb.AppendLine((ok ? "PASS  " : "FAIL  ") + name
                    + (!string.IsNullOrEmpty(detail) ? "  · " + detail : ""));
            };

            // 进程识别边界：系统目录里几百个 exe 互相没关系，不能当成辅助进程。
            check("系统目录跳过同目录关联",
                Scanner.IsSystemDir(@"C:\Windows\System32") && Scanner.IsSystemDir(@"C:\Windows\SysWOW64")
                && Scanner.IsSystemDir(@"C:\Windows\SystemApps\Microsoft.Windows.Search_cw5n1h2txyewy"), "");
            check("用户软件目录参与同目录关联",
                !Scanner.IsSystemDir(@"C:\Users\me\AppData\Local\Programs\Antigravity"), "");
            var dup = new Scanner.ProcInfo { Exe = "app.exe", Path = @"D:\tools\app.exe", Count = 1 };
            dup.AllPaths.Add(@"D:\tools\app.exe");
            dup.AllPaths.Add(@"E:\portable\app.exe");
            check("同名不同路径判为冲突", dup.NameConflict && dup.AllPaths.Count == 2, "");
            var one = new Scanner.ProcInfo { Exe = "app.exe", Path = @"D:\tools\app.exe", Count = 1 };
            one.AllPaths.Add(@"D:\tools\app.exe");
            check("单一路径不误报冲突", !one.NameConflict, "");

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
            check("A 规则插在 rules 段首（带自家标记）",
                pa.IndexOf("rules:\n# PP-BEGIN", StringComparison.Ordinal) >= 0 &&
                pa.IndexOf("- PROCESS-NAME,msedge.exe,PP-FIXED", StringComparison.Ordinal) >= 0, "");
            check("A 紧跟 MATCH,DIRECT 兜底", pa.IndexOf("- PROCESS-NAME,weixin.exe,PP-FIXED\n- MATCH,DIRECT", StringComparison.Ordinal) >= 0, "");
            check("A 补上 find-process-mode", pa.IndexOf("find-process-mode: always", StringComparison.Ordinal) >= 0, "");
            check("A 清掉早期 pp-whitelist 规则集", CountOccurrences(pa, "RULE-SET,pp-whitelist") == 0, "");

            // B. Nyanpasu 风格：列表缩进、组名是 emoji、原本没有 find-process-mode
            string b = "port: 7890\nmode: rule\nproxies: []\nproxy-groups:\n  - name: 🚀 节点选择\n    type: select\n" +
                       "rules:\n  - DOMAIN-SUFFIX,google.com,🚀 节点选择\n  - MATCH,DIRECT\n";
            string policyB = FindPolicy(b, out reused);
            check("B 自动选用可用的代理组", policyB == "🚀 节点选择", policyB);

            string pb = BuildPayload(b, items, policyB, false);
            check("B 缩进风格也能插入规则（标记跟随缩进）",
                pb.IndexOf("rules:\n  # PP-BEGIN", StringComparison.Ordinal) >= 0 &&
                pb.IndexOf("  - PROCESS-NAME,msedge.exe,🚀 节点选择", StringComparison.Ordinal) >= 0, "");
            check("B 原有规则保留在自家块之后",
                pb.IndexOf("  - DOMAIN-SUFFIX,google.com", StringComparison.Ordinal) >
                pb.IndexOf("# PP-END", StringComparison.Ordinal), "");
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

            // J. 更正旧假设：程序从不写客户端配置文件，每次生成都以干净磁盘订阅为基线，
            //    所以段首的 MATCH,DIRECT 只能是用户 / 订阅自己写的。旧逻辑把它删掉再补到末尾，
            //    会把「原本全部直连」的订阅改成后面的 DOMAIN 规则先生效，语义被悄悄改掉（外部评审 #4）。
            string polluted = "mode: rule\nproxies: []\nrules:\n- MATCH,DIRECT\n- DOMAIN-SUFFIX,google.com,🚀 节点选择\n";
            string clean = BuildPayload(polluted, items, "🚀 节点选择", true);
            check("J 兼容模式保留订阅自己的段首 MATCH",
                clean.IndexOf("rules:\n# PP-BEGIN", StringComparison.Ordinal) >= 0 &&
                clean.IndexOf("- MATCH,DIRECT\n- DOMAIN-SUFFIX,google.com", StringComparison.Ordinal) >= 0 &&
                CountOccurrences(clean, "- MATCH,DIRECT") == 1, "");
            check("J 兜底不再被挪到规则末尾",
                !clean.TrimEnd('\n').EndsWith("- MATCH,DIRECT", StringComparison.Ordinal), "");

            // K. 更老的一种遗留：段首是 `RULE-SET,pp-whitelist`，它后面紧跟一条 MATCH,DIRECT。
            //    这条 MATCH 排第二、不是第一位，J 的判据抓不到，但它照样短路后面所有规则。
            string legacy = "mode: rule\nproxies: []\nrules:\n- RULE-SET,pp-whitelist,PP-FIXED\n- MATCH,DIRECT\n- DOMAIN-SUFFIX,google.com,PP-FIXED\n";
            string fixedK = BuildPayload(legacy, items, "PP-FIXED", true);
            check("K 清掉规则集后面那条遗留 MATCH",
                CountOccurrences(fixedK, "- MATCH,DIRECT") == 1 &&
                fixedK.IndexOf("DOMAIN-SUFFIX,google.com", StringComparison.Ordinal) >= 0, "");
            check("K 清掉早期 pp-whitelist 规则集",
                CountOccurrences(fixedK, "RULE-SET,pp-whitelist") == 0, "");
            check("K 清完仍是幂等的",
                BuildPayload(fixedK, items, "PP-FIXED", true) == fixedK, "");

            // L. 评审 R1-1 / R3-2：用户自己写在段首的 PROCESS-NAME 不能被当成旧块删掉。
            string userRule = "mode: rule\nproxies: []\nrules:\n- PROCESS-NAME,chrome.exe,DIRECT\n" +
                              "- DOMAIN-SUFFIX,google.com,PP-FIXED\n- MATCH,PP-FIXED\n";
            string lp = BuildPayload(userRule, items, "PP-FIXED", false);
            string lw = BuildPayload(userRule, items, "PP-FIXED", true);
            check("L 白名单模式保留用户自写进程规则",
                lp.IndexOf("- PROCESS-NAME,chrome.exe,DIRECT", StringComparison.Ordinal) >= 0, "");
            check("L 兼容模式也保留用户自写进程规则",
                lw.IndexOf("- PROCESS-NAME,chrome.exe,DIRECT", StringComparison.Ordinal) >= 0, "");
            check("L 用户规则场景仍幂等", BuildPayload(lp, items, "PP-FIXED", false) == lp, "");

            // L2. 复审 P1：用户手写多条同策略规则 + 紧跟 MATCH,DIRECT，不能被猜成自家旧块删掉。
            string userMulti = "mode: rule\nproxies: []\nrules:\n- PROCESS-NAME,chrome.exe,香港节点\n" +
                               "- PROCESS-NAME,firefox.exe,香港节点\n- MATCH,DIRECT\n" +
                               "- DOMAIN-SUFFIX,google.com,香港节点\n";
            string lmP = BuildPayload(userMulti, items, "PP-FIXED", false);
            string lmW = BuildPayload(userMulti, items, "PP-FIXED", true);
            check("L2 白名单模式保留用户多条同策略规则",
                lmP.IndexOf("- PROCESS-NAME,chrome.exe,香港节点", StringComparison.Ordinal) >= 0 &&
                lmP.IndexOf("- PROCESS-NAME,firefox.exe,香港节点", StringComparison.Ordinal) >= 0, "");
            check("L2 兼容模式也保留用户多条同策略规则",
                lmW.IndexOf("- PROCESS-NAME,chrome.exe,香港节点", StringComparison.Ordinal) >= 0 &&
                lmW.IndexOf("- PROCESS-NAME,firefox.exe,香港节点", StringComparison.Ordinal) >= 0, "");

            // L3. 复审 P2：标记不成对（用户手改 / 配置被截断）时不能越生成越长。
            string brokenMark = "mode: rule\nproxies: []\nrules:\n# PP-BEGIN\n- MATCH,DIRECT\n" +
                                "- DOMAIN-SUFFIX,google.com,PP-FIXED\n";
            string f1 = BuildPayload(brokenMark, items, "PP-FIXED", false);
            check("L3 标记残缺时仍幂等", BuildPayload(f1, items, "PP-FIXED", false) == f1, "");

            // R7-1. 第 7 轮复审 P1：段首用户规则的策略与自动选中的主组同名（🚀 节点选择），
            //       两种模式都必须保留——不能因为「策略相同」就猜成自家旧块删掉。
            string samePol = "mode: rule\nproxies: []\nproxy-groups:\n- name: 🚀 节点选择\n  type: select\n" +
                             "  proxies:\n  - n1\nrules:\n- PROCESS-NAME,chrome.exe,🚀 节点选择\n" +
                             "- PROCESS-NAME,steam.exe,DIRECT\n- DOMAIN-SUFFIX,google.com,🚀 节点选择\n- MATCH,🚀 节点选择\n";
            string spP = BuildPayload(samePol, items, "🚀 节点选择", false);
            string spW = BuildPayload(samePol, items, "🚀 节点选择", true);
            check("R7-1 接管模式保留同策略用户规则",
                spP.IndexOf("- PROCESS-NAME,chrome.exe,🚀 节点选择", StringComparison.Ordinal) >= 0, "");
            check("R7-1 兼容模式保留同策略用户规则",
                spW.IndexOf("- PROCESS-NAME,chrome.exe,🚀 节点选择", StringComparison.Ordinal) >= 0, "");

            // R7-2. 第 7 轮复审 P1：段首 MATCH,DIRECT 后面跟着用户规则时，
            //       第一遍清掉短路 MATCH、第二遍用户规则变成段首——旧逻辑会在第二遍把它删掉。
            //       两遍输出必须逐字节相同，且用户规则两遍都在。
            string headMatch = "mode: rule\nproxies: []\nproxy-groups:\n- name: 🚀 节点选择\n  type: select\n" +
                               "  proxies:\n  - n1\nrules:\n- MATCH,DIRECT\n" +
                               "- PROCESS-NAME,chrome.exe,🚀 节点选择\n- DOMAIN-SUFFIX,google.com,🚀 节点选择\n";
            string hm1 = BuildPayload(headMatch, items, "🚀 节点选择", true);
            string hm2 = BuildPayload(hm1, items, "🚀 节点选择", true);
            check("R7-2 兼容模式两遍生成逐字节相同", hm1 == hm2,
                hm1 == hm2 ? "长度 " + hm1.Length : "长度 " + hm1.Length + " → " + hm2.Length);
            check("R7-2 第二遍用户规则仍在",
                hm2.IndexOf("- PROCESS-NAME,chrome.exe,🚀 节点选择", StringComparison.Ordinal) >= 0, "");
            string hmP1 = BuildPayload(headMatch, items, "🚀 节点选择", false);
            string hmP2 = BuildPayload(hmP1, items, "🚀 节点选择", false);
            check("R7-2 接管模式两遍生成也逐字节相同", hmP1 == hmP2, "");

            // M. 评审 R6-1：旧 RULE-SET 与短路 MATCH 之间隔一条合法注释也必须清干净。
            string commented = "mode: rule\nproxies: []\nrules:\n- RULE-SET,pp-whitelist,PP-FIXED\n" +
                               "  # 订阅说明\n- MATCH,DIRECT\n- DOMAIN-SUFFIX,google.com,PP-FIXED\n";
            string fm = BuildPayload(commented, items, "PP-FIXED", true);
            check("M 注释间隔的旧组合不再短路订阅",
                fm.IndexOf("- MATCH,DIRECT", StringComparison.Ordinal) >
                fm.IndexOf("- DOMAIN-SUFFIX,google.com", StringComparison.Ordinal), "");
            check("M 注释间隔场景幂等", BuildPayload(fm, items, "PP-FIXED", true) == fm, "");

            // N. 评审 R4-4 + 第 18 轮 F-01：无标记的旧规则不再按「段首 + PP-FIXED」猜归属。
            //    它们是用户配置，宁可保留（这里期望 2 条旧的 + 2 条新的 = 4），也不能静默删除。
            string quoted = "mode: rule\nproxies: []\nrules:\n- 'PROCESS-NAME,msedge.exe,PP-FIXED'\n" +
                            "- 'PROCESS-NAME,weixin.exe,PP-FIXED'\n- DOMAIN-SUFFIX,google.com,PP-FIXED\n- MATCH,DIRECT\n";
            string fn = BuildPayload(quoted, items, "PP-FIXED", false);
            check("N 无标记旧规则不误删（保留旧 2 条 + 新 2 条）",
                CountOccurrences(fn, "PROCESS-NAME,") == 4,
                "实际 " + CountOccurrences(fn, "PROCESS-NAME,") + " 条");
            check("N2 无标记旧规则场景仍幂等",
                BuildPayload(fn, items, "PP-FIXED", false) == fn, "");

            // N3/N4：手改策略不能被保存改回 PP-FIXED；helper 必须标记为派生，不能落盘成独立条目。
            var customPolicy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "msedge.exe", "🚀 节点选择" }
            };
            var keptPolicy = Profile.Compose(new[] { "msedge.exe" }, null, null, null, customPolicy);
            check("N3 手改策略保存时原样保留",
                keptPolicy.Count == 1 && keptPolicy[0].Policy == "🚀 节点选择",
                keptPolicy.Count > 0 ? keptPolicy[0].Policy : "(空)");

            var peerMap = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                { "msedge.exe", new List<string> { "helper.exe" } }
            };
            var withPeer = Profile.Compose(new[] { "msedge.exe" }, null, peerMap, null, null);
            bool peerDerived = false;
            foreach (var it in withPeer) if (it.Exe == "helper.exe" && it.Derived) peerDerived = true;
            check("N4 辅助进程标记为派生，不写回用户名单", peerDerived, "");

            // N5：退出还原决策的纯函数——首条是用户规则、第二条才是自家规则时也必须还原。
            var liveRules = new List<Profile.RuleInfo>
            {
                new Profile.RuleInfo { Type = "DomainSuffix", Payload = "google.com", Proxy = "PROXY" },
                new Profile.RuleInfo { Type = "ProcessName", Payload = "msedge.exe", Proxy = "PP-FIXED" }
            };
            check("N5 自家规则不在首条也要还原",
                Profile.ShouldRestore(true, liveRules, null, false, false), "");
            check("N6 读取失败按「有」处理，必须还原",
                Profile.ShouldRestore(false, new List<Profile.RuleInfo>(), null, false, false), "");
            check("N7 确实没有自家规则且无需恢复时不推送",
                !Profile.ShouldRestore(true, new List<Profile.RuleInfo>
                {
                    new Profile.RuleInfo { Type = "Match", Payload = "", Proxy = "DIRECT" }
                }, null, false, false), "");
            // N7b：明确保存过空名单时，段首 MATCH,DIRECT 是本工具写的，退出必须还原（外部评审 F2）。
            // 这条锁住的是 ShouldRestore 的六参数重载，防止以后又把「空名单接管」误判成「没接管过」。
            check("N7b 空名单接管后，内核只有 MATCH,DIRECT 也要还原",
                Profile.ShouldRestore(true, new List<Profile.RuleInfo>
                {
                    new Profile.RuleInfo { Type = "Match", Payload = "", Proxy = "DIRECT" }
                }, null, false, false, true), "");
            check("N7c 从没表达过意图时不因一条用户 MATCH 误还原",
                !Profile.ShouldRestore(true, new List<Profile.RuleInfo>
                {
                    new Profile.RuleInfo { Type = "Match", Payload = "", Proxy = "DIRECT" }
                }, null, false, false, false), "");

            // F1：strict 是 mihomo 默认档，段首 PROCESS 规则正常生效；只有 off 才算真的关闭。
            // 以前 Judge 把「不是 always」一律判成没开，默认 Clash Verge Rev 下看守直接放弃补回。
            var fpmStrict = new Guard.Intent
            {
                Ok = true, Mode = "rule", FindProcess = "strict",
                FirstRuleIsMatch = true, MatchIndex = 0, LastProcessIndex = -1,
                MatchCount = 1, MatchProxy = "DIRECT"   // 白名单硬约束：兜底存在且为 DIRECT
            };
            check("F1-a strict 不再被判成进程识别关闭（看守会继续补回）",
                Guard.Judge(fpmStrict, 0, false) == null, "");
            var fpmOff = new Guard.Intent
            {
                Ok = true, Mode = "rule", FindProcess = "off",
                FirstRuleIsMatch = true, MatchIndex = 1, LastProcessIndex = 0
            };
            string offVerdict = Guard.Judge(fpmOff, 0, false);
            check("F1-b off 仍然判为进程识别关闭",
                offVerdict != null && offVerdict.IndexOf("off", StringComparison.OrdinalIgnoreCase) >= 0, "");

            // G1：订阅兼容模式不能删掉用户 / 订阅写在段首的 MATCH（外部评审 #4）。
            string headMatchYaml = "mode: rule\nproxies: []\nrules:\n- MATCH,DIRECT\n- DOMAIN-SUFFIX,google.com,DIRECT\n";
            string patchKeep = BuildPayload(headMatchYaml, new List<Profile.Item>(), "PP-FIXED", true);
            check("G1-a 订阅兼容保留段首 MATCH,DIRECT",
                patchKeep.IndexOf("- MATCH,DIRECT\n- DOMAIN-SUFFIX,google.com,DIRECT", StringComparison.Ordinal) >= 0, "");
            string wlKeep = BuildPayload(headMatchYaml, new List<Profile.Item>(), "PP-FIXED", false);
            check("G1-b 完全接管空名单仍只留一条段首 MATCH",
                CountOccurrences(wlKeep, "- MATCH,DIRECT") == 1 &&
                wlKeep.IndexOf("rules:\n- MATCH,DIRECT", StringComparison.Ordinal) >= 0, "");

            // G2：备份来源核对（不匹配拒绝 / 旧备份拒绝并走重建 / 匹配放行）。
            string why;
            check("G2-a 来源路径与内容指纹都一致时放行",
                Profile.BackupAllowed(@"D:\a\config.yaml", @"D:\a\config.yaml", true,
                                      "fp-old", "fp-old", out why) && why.Length == 0, "");
            check("G2-b 备份来自另一份配置时拒绝还原",
                !Profile.BackupAllowed(@"D:\a\config.yaml", @"D:\b\config.yaml", true, out why) &&
                why.IndexOf("另一份", StringComparison.Ordinal) >= 0, "");
            check("G2-c 旧版备份无来源标记：拒绝整份推回（改为当前订阅重建）",
                !Profile.BackupAllowed("", @"D:\b\config.yaml", false, out why) &&
                why.IndexOf("旧版", StringComparison.Ordinal) >= 0, "");

            // G3：手动还原＝删除名单文件。用临时文件覆盖，绝不碰真机名单。
            string tmpIntent = Path.Combine(Path.GetTempPath(), "pp-selftest-" + Guid.NewGuid().ToString("N") + ".yaml");
            try
            {
                File.WriteAllText(tmpIntent, "payload:\n");
                string err;
                bool gone = Profile.ClearIntentFile(tmpIntent, out err) && !File.Exists(tmpIntent) && err.Length == 0;
                bool absentOk = Profile.ClearIntentFile(tmpIntent, out err) && err.Length == 0;
                check("G3 删除名单文件后意图消失，重复删除也安全", gone && absentOk, "");
            }
            finally { try { if (File.Exists(tmpIntent)) File.Delete(tmpIntent); } catch { } }

            // G2d：评审 R1 纯函数断——补回停手只依赖名单文件存在性，与模式无关。
            // PatchOnly（订阅兼容）与 WHITELIST 两种模式下 AutoRecoverStopped(true) 必须一致；
            // manual 保存（autoRecover=false）永远是放行的。只改内存字段、不写盘，用相对断言。
            string modeBackup = Settings.Current.Mode;
            try
            {
                bool hasIntent = Profile.HasSavedIntent;
                bool expectStop = !hasIntent;
                Settings.Current.Mode = Settings.ModePatch;
                check("G2d-a 订阅兼容模式：补回停手判定等于 !HasSavedIntent",
                    Profile.AutoRecoverStopped(true) == expectStop, "expectStop=" + expectStop);
                check("G2d-b 订阅兼容模式：手动保存永远放行", !Profile.AutoRecoverStopped(false), "");
                Settings.Current.Mode = Settings.ModeWhitelist;
                check("G2d-c 完全接管模式：补回停手判定与订阅兼容一致",
                    Profile.AutoRecoverStopped(true) == expectStop, "expectStop=" + expectStop);
                check("G2d-d 完全接管模式：手动保存永远放行", !Profile.AutoRecoverStopped(false), "");
            }
            finally { Settings.Current.Mode = modeBackup; }

            // G2e：收尾批 F-03，补评审 §10 点名的测试缺口。
            // 名单路径注入到一个「故意不创建」的临时文件后，Apply 的撤销分支位于保存锁内
            // 最前面，不碰内核、不写盘，因此可以离线端到端真跑一次（不再是纯函数替身）。
            // 第三条「单次 ApplyCore 只 Detect 一次」无法在不推内核的前提下完整跑完，
            // 由 Mihomo 的 R4-3 断言锁住关键前提：传了同源路径就不再回退 Detect。
            string g2eList = Path.Combine(Path.GetTempPath(),
                "pp-selftest-intent-" + Guid.NewGuid().ToString("N") + ".yaml");
            string g2eCfg = Path.Combine(Path.GetTempPath(),
                "pp-selftest-cfg-" + Guid.NewGuid().ToString("N") + ".yaml");
            string g2eOverride = Paths.ListPathOverride;
            string g2eMode = Settings.Current.Mode;
            try
            {
                Paths.ListPathOverride = g2eList;   // 不存在 = 名单文件已删除 = 接管意图已撤销
                long savesBefore = Profile.SaveCallCount;
                long detectsBefore = DetectCalls;

                Settings.Current.Mode = Settings.ModePatch;   // 评审点名的订阅兼容模式
                var revoked = Profile.Apply(new List<Profile.Item>(), true);
                check("G2e-a 订阅兼容 + 名单已删除：Apply 失败且不得 Save / Detect",
                    !revoked.Ok && Profile.IsRevokedIntent(revoked.Message) &&
                    Profile.SaveCallCount == savesBefore && DetectCalls == detectsBefore,
                    "msg=" + (revoked.Message ?? "<空>"));

                check("G2e-b 撤销文案判 SKIP，普通失败与占用文案不判 SKIP",
                    Profile.IsRevokedIntent(Profile.RevokedIntentMessage) &&
                    !Profile.IsRevokedIntent("内核没有确认") &&
                    !Profile.IsRevokedIntent(Profile.ApplyBusyMessage), "");

                Settings.Current.Mode = Settings.ModeWhitelist;
                var revoked2 = Profile.Apply(new List<Profile.Item>(), true);
                check("G2e-c 完全接管模式同样停手（判定与模式无关）",
                    !revoked2.Ok && Profile.IsRevokedIntent(revoked2.Message), "");

                // 收尾批 P2-2：secret 曾经有三套解析，这里锁住「统一解析 + 无第三套实现」。
                File.WriteAllText(g2eCfg,
                    "# secret: COMMENTED\nsecret: 'OLD'\n  secret: INDENTED\n" +
                    "secret: \"FINAL\"\nexternal-controller: '127.0.0.1:9090'\n",
                    new UTF8Encoding(false));
                string g2eSecret, g2eController;
                ReadController(g2eCfg, out g2eSecret, out g2eController);
                check("G2e-d 多行 / 缩进 / 注释 secret 由统一解析器处理",
                    g2eSecret == "FINAL" && g2eController == "127.0.0.1:9090", "secret=" + g2eSecret);
                check("G2e-e Mihomo.ReadSecret 与统一解析结果一致",
                    Mihomo.ReadSecret(g2eCfg) == g2eSecret, "");
                var legacySecret = typeof(Profile).GetMethod("CurrentSecret",
                    System.Reflection.BindingFlags.Static |
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic);
                check("G2e-f Profile 不再保留第三套 secret 解析", legacySecret == null, "");
            }
            catch (Exception ex)
            {
                check("G2e 端到端断言执行完成", false, ex.Message);
            }
            finally
            {
                Paths.ListPathOverride = g2eOverride;
                Settings.Current.Mode = g2eMode;
                try { if (File.Exists(g2eList)) File.Delete(g2eList); } catch { }
                try { if (File.Exists(g2eCfg)) File.Delete(g2eCfg); } catch { }
            }

            // G2f：独立审阅第二轮的回归锁 —— 坏名单、备份内容指纹、看守误判、白名单硬约束。
            string g2fCfg = Path.Combine(Path.GetTempPath(),
                "pp-selftest-fp-" + Guid.NewGuid().ToString("N") + ".yaml");
            string g2fMeta = g2fCfg + ".meta";
            try
            {
                List<Profile.Item> parsed;
                List<int> bad;
                string perr;
                Profile.ParseWhitelistLines(new[] {
                    "# header", "payload:",
                    "  - 'PROCESS-NAME,chrome.exe,PP-FIXED'",
                    "  'PROCESS-NAME,firefox.exe,PP-FIXED'",
                    "  - 'PROCESS-NAME,edge.exe,PP-FIXED,EXTRA'" },
                    out parsed, out bad, out perr);
                check("G2f-a payload 段少「-」/ 字段过多的行必须拦住",
                    bad.Count == 2 && parsed.Count == 1 && perr.Length == 0,
                    "bad=" + bad.Count + " items=" + parsed.Count);
                Profile.ParseWhitelistLines(new[] { "payload:", "  - PROCESS-NAME,chrome.exe" },
                    out parsed, out bad, out perr);
                check("G2f-b 两字段老名单仍兼容", parsed.Count == 1 && bad.Count == 0 &&
                    parsed[0].Policy == "PP-FIXED", "");

                string g2fWhy;
                bool okSame = Profile.BackupAllowed(@"C:\A.yaml", @"C:\A.yaml", true, "fp1", "fp1", out g2fWhy);
                bool okChanged = Profile.BackupAllowed(@"C:\A.yaml", @"C:\A.yaml", true, "fp1", "fp2", out g2fWhy);
                bool okNoFp = Profile.BackupAllowed(@"C:\A.yaml", @"C:\A.yaml", true, "", "", out g2fWhy);
                check("G2f-c 同路径同指纹放行整份还原", okSame, "");
                check("G2f-d 同路径但订阅已更新 → 拒绝整份还原（Verge 场景）", !okChanged, "");
                check("G2f-e 旧版无指纹 → 拒绝整份还原", !okNoFp, "");

                File.WriteAllText(g2fCfg, "mode: rule\n", new UTF8Encoding(false));
                Profile.WriteBackupMeta(g2fMeta, g2fCfg);
                string metaSrc, metaFp;
                bool metaOk = Profile.ReadBackupMeta(g2fMeta, out metaSrc, out metaFp);
                string fpNow = Profile.ConfigFingerprint(g2fCfg);
                check("G2f-f 新版 .meta 记录路径与内容指纹",
                    metaOk && metaSrc == g2fCfg && metaFp == fpNow && metaFp.Length == 64, "");
                File.WriteAllText(g2fCfg, "mode: global\n", new UTF8Encoding(false));
                check("G2f-g 配置内容变化后指纹随之变化",
                    Profile.ConfigFingerprint(g2fCfg) != metaFp, "");

                File.WriteAllText(g2fCfg,
                    "secret: 'abc' # note\n  secret: NESTED\nexternal-controller: 127.0.0.1:9090 # c\n",
                    new UTF8Encoding(false));
                string sec, ctrl;
                bool found;
                ReadController(g2fCfg, out sec, out ctrl, out found);
                check("G2f-h 只认顶层 secret 且剥掉行尾注释",
                    found && sec == "abc" && ctrl == "127.0.0.1:9090", "sec=" + sec + " ctrl=" + ctrl);
                File.WriteAllText(g2fCfg, "secret: \"\"\n", new UTF8Encoding(false));
                ReadController(g2fCfg, out sec, out ctrl, out found);
                check("G2f-i secret: \"\" 是合法空密钥，不是读取失败", found && sec.Length == 0, "");

                var itPatch = new Guard.Intent
                {
                    Ok = true, Mode = "rule", FindProcess = "always",
                    ProcessRules = 1, LastProcessIndex = 0,
                    HeadShortCircuit = true, MatchCount = 1, MatchIndex = 0, MatchProxy = "DIRECT"
                };
                check("G2f-j 兼容模式不把订阅自带段首 MATCH 判成故障",
                    Guard.Judge(itPatch, 1, true) == null, "");
                var itNoMatch = new Guard.Intent
                {
                    Ok = true, Mode = "rule", FindProcess = "always",
                    ProcessRules = 1, LastProcessIndex = 0, MatchCount = 0, MatchIndex = -1
                };
                check("G2f-k 白名单缺少兜底 MATCH 必须要求修复",
                    Guard.Judge(itNoMatch, 1, false) != null, "");
                var itProxyMatch = new Guard.Intent
                {
                    Ok = true, Mode = "rule", FindProcess = "always",
                    ProcessRules = 1, LastProcessIndex = 0,
                    MatchCount = 1, MatchIndex = 1, MatchProxy = "PROXY"
                };
                check("G2f-l MATCH,PROXY 必须要求修复",
                    Guard.Judge(itProxyMatch, 1, false) != null, "");
                var itGood = new Guard.Intent
                {
                    Ok = true, Mode = "rule", FindProcess = "always",
                    ProcessRules = 1, LastProcessIndex = 0,
                    MatchCount = 1, MatchIndex = 1, MatchProxy = "DIRECT"
                };
                check("G2f-m 正常白名单不误报", Guard.Judge(itGood, 1, false) == null, "");
                check("G2f-n DIRECT 策略判定",
                    Profile.IsDirectPolicy("DIRECT") && !Profile.IsDirectPolicy("PROXY") &&
                    !Profile.IsDirectPolicy(""), "");
            }
            catch (Exception ex)
            {
                check("G2f 回归断言执行完成", false, ex.Message);
            }
            finally
            {
                try { if (File.Exists(g2fCfg)) File.Delete(g2fCfg); } catch { }
                try { if (File.Exists(g2fMeta)) File.Delete(g2fMeta); } catch { }
            }

            // G4：find-process-mode 原值以磁盘订阅为准，不能把工具写的 always 当成基线。
            check("G4-a 磁盘里的 strict 被正确读出",
                Settings.ParseFindProcessFromYaml("mode: rule\nfind-process-mode: strict\nrules:\n") == "strict", "");
            check("G4-b 磁盘里没有这一行判为 absent",
                Settings.ParseFindProcessFromYaml("mode: rule\nrules:\n") == "absent", "");
            check("G4-c 带引号与行内注释也能读",
                Settings.ParseFindProcessFromYaml("find-process-mode: 'always' # note\n") == "always", "");

            // N8：用户自己写的 `# PP-BEGIN / # PP-END` 分区注释里如果只有 DIRECT 规则，
            //     不能当成工具块删掉（CodeArts F-02）。
            string userMarked = "mode: rule\nproxies: []\nrules:\n# PP-BEGIN\n- PROCESS-NAME,user.exe,DIRECT\n# PP-END\n- MATCH,DIRECT\n";
            string um = BuildPayload(userMarked, items, "PP-FIXED", true);
            check("N8 用户 PP 分区注释里的 DIRECT 规则保留",
                um.IndexOf("PROCESS-NAME,user.exe,DIRECT", StringComparison.Ordinal) >= 0, "");

            // N9：空名单不再留下空 PP-BEGIN/PP-END。
            string emptyOut = BuildPayload("mode: rule\nrules:\n- MATCH,DIRECT\n", new List<Profile.Item>(), "PP-FIXED", true);
            check("N9 空名单不生成空标记块",
                emptyOut.IndexOf("PP-BEGIN", StringComparison.Ordinal) < 0, "");

            // N10（独立审阅第二轮修正）：兼容模式要原样保留订阅自带的段首 MATCH，
            // 且不能因为认不出带引号写法而在尾部又追加一条兜底。
            // 旧断言只比 DOMAIN 与「最后一次 MATCH」的位置，尾部新加的那条会让它误通过。
            string quotedMatch = "mode: rule\nproxies: []\nrules:\n- 'MATCH,DIRECT' # early\n- DOMAIN-SUFFIX,google.com,PROXY\n";
            string qm = BuildPayload(quotedMatch, items, "PP-FIXED", true);
            int qmMatchCount = System.Text.RegularExpressions.Regex.Matches(
                qm, @"(?m)^\s*-\s*'?MATCH,").Count;
            check("N10-a 兼容模式保留订阅自带的带引号段首 MATCH",
                qm.IndexOf("'MATCH,DIRECT' # early", StringComparison.Ordinal) >= 0, "");
            check("N10-b 不额外新增 MATCH（安全网不误判）", qmMatchCount == 1,
                  "MATCH=" + qmMatchCount);
            check("N10-c 订阅原有规则顺序不变",
                qm.IndexOf("MATCH,DIRECT", StringComparison.Ordinal) <
                qm.IndexOf("DOMAIN-SUFFIX,google.com", StringComparison.Ordinal), "");

            // N11：IPv6 脱敏要覆盖中间压缩，同时不误伤时间戳和回环。
            check("N11 压缩 IPv6 脱敏",
                Redactor.Apply("控制地址：2001:db8::1:9090").IndexOf("2001:db8", StringComparison.Ordinal) < 0, "");
            check("N12 时间戳与 ::1 不被误伤",
                Redactor.Apply("时间 12:34:56 回环 ::1") == "时间 12:34:56 回环 ::1", "");

            // N13/N14：Guard 不能漏掉「只有两条规则时的短路」和「策略被改」。
            // 兼容模式会原样保留订阅自带的段首 MATCH，不能判成故障（独立审阅第二轮）；
            // 「提前 MATCH 挡住勾选规则」这条硬约束只适用完全接管模式，改在这里测。
            var shortGuard = new Guard.Intent
            {
                Ok = true, Mode = "rule", FindProcess = "always", RuleCount = 2,
                ProcessRules = 1, LastProcessIndex = 1, HeadShortCircuit = true,
                MatchCount = 1, MatchIndex = 0, MatchProxy = "DIRECT"
            };
            check("N13 完全接管模式两条规则也能判出提前 MATCH",
                Guard.Judge(shortGuard, 1, false) != null, "");
            var wrongGuard = new Guard.Intent
            {
                Ok = true, RuleCount = 3, ProcessRules = 1, WrongPolicy = 1
            };
            wrongGuard.WrongPolicyNames.Add("chrome.exe");
            check("N14 策略被改要判不一致", Guard.Judge(wrongGuard, 1, true) != null, "");

            // N15：白名单模式空名单仍要保留 MATCH,DIRECT，但不能留空标记块。
            string emptyWl = BuildPayload("mode: rule\nrules:\n- MATCH,DIRECT\n",
                new List<Profile.Item>(), "PP-FIXED", false);
            check("N15 白名单空名单保留兜底且不留空标记",
                CountOccurrences(emptyWl, "MATCH,DIRECT") == 1 &&
                emptyWl.IndexOf("PP-BEGIN", StringComparison.Ordinal) < 0, "");

            // N15b：完全接管 + 空名单 + 真实订阅规则（非光秃 MATCH）——回归第一轮视角 20/9。
            //   首条必须是 MATCH（回读校验 Profile.cs:530 才能过）、只留一条（不累加）、
            //   订阅规则被短路到 MATCH 之后、重复生成逐字节相同（幂等）。
            string realSub = "mode: rule\nproxies:\n  - name: n1\n    type: ss\n" +
                "proxy-groups:\n- name: 🚀 节点选择\n  type: select\n  proxies:\n  - n1\n" +
                "rules:\n  - DOMAIN-SUFFIX,google.com,🚀 节点选择\n  - IP-CIDR,1.1.1.0/24,🚀 节点选择,no-resolve\n";
            string emptyTakeover = BuildPayload(realSub, new List<Profile.Item>(), "🚀 节点选择", false);
            check("N15b 空名单完全接管：首条是 MATCH（回读校验通过）",
                emptyTakeover.IndexOf("rules:\n  - MATCH,DIRECT", StringComparison.Ordinal) >= 0, "");
            check("N15b 空名单完全接管：MATCH 只有一条（不累加）",
                CountOccurrences(emptyTakeover, "- MATCH,DIRECT") == 1,
                "实际 " + CountOccurrences(emptyTakeover, "- MATCH,DIRECT") + " 条");
            check("N15b 空名单完全接管：订阅规则被短路在 MATCH 之后",
                emptyTakeover.IndexOf("- DOMAIN-SUFFIX,google.com", StringComparison.Ordinal) >
                emptyTakeover.IndexOf("- MATCH,DIRECT", StringComparison.Ordinal), "");
            check("N15b 空名单完全接管：幂等（重复生成逐字节相同）",
                BuildPayload(emptyTakeover, new List<Profile.Item>(), "🚀 节点选择", false) == emptyTakeover, "");

            // N16/N17：多客户端选择——运行中的旧配置必须压过没运行的新配置；
            // 内核命令行指向的目录优先级最高。
            var runningOld = new Candidate
            {
                Order = 3, ClientRunning = true,
                ConfigWriteUtc = DateTime.UtcNow.AddDays(-3)
            };
            var stoppedNew = new Candidate
            {
                Order = 0,
                ConfigWriteUtc = DateTime.UtcNow
            };
            check("N16 运行中的客户端优先于未运行的新配置",
                CandidateScore(runningOld) > CandidateScore(stoppedNew), "");
            var kernelDir = new Candidate
            {
                Order = 9, KernelDir = true,
                ConfigWriteUtc = DateTime.UtcNow.AddDays(-10)
            };
            check("N17 内核命令行指向的目录优先级最高",
                CandidateScore(kernelDir) > CandidateScore(runningOld), "");

            // O. 评审 R4-5：策略名含逗号会被拆字段，必须拒绝而不是硬拼。
            bool rejected = false;
            try { BuildPayload(b, new List<Profile.Item> { new Profile.Item("msedge.exe", "香港,日本") }, policyB, false); }
            catch (InvalidOperationException) { rejected = true; }
            check("O 策略名含逗号时拒绝写入", rejected, "");

            // P. 评审 R1-4：rules: 行内注释、rules: [] 都要能处理。
            bool commentOk = false, emptyOk = false;
            try
            {
                string rc = "mode: rule\nproxies: []\nrules:  # 规则段\n- MATCH,DIRECT\n";
                commentOk = BuildPayload(rc, items, "GLOBAL", false).IndexOf("- PROCESS-NAME,msedge.exe,GLOBAL", StringComparison.Ordinal) >= 0;
                string re = "mode: rule\nproxies: []\nrules: []\n";
                emptyOk = BuildPayload(re, items, "GLOBAL", false).IndexOf("- PROCESS-NAME,msedge.exe,GLOBAL", StringComparison.Ordinal) >= 0;
            }
            catch { }
            check("P rules: 带行内注释可保存", commentOk, "");
            check("P rules: [] 可保存", emptyOk, "");

            // Q. 评审 R4-1：没有 PP-FIXED 时别挑到「游戏代理」这类组。
            string q = "proxy-groups:\n- name: 游戏代理\n- name: 香港节点\n- name: 🚀 节点选择\nrules:\n- MATCH,DIRECT\n";
            check("Q 自动挑组优先主节点组", FindPolicy(q, out reused) == "🚀 节点选择", "");

            // R. 评审 R1-8：名单里手写的 DIRECT 现在按条目生效。
            string fr = BuildPayload(b, new List<Profile.Item> { new Profile.Item("chrome.exe", "DIRECT") }, policyB, false);
            check("R 手改名单写 DIRECT 生效",
                fr.IndexOf("- PROCESS-NAME,chrome.exe,DIRECT", StringComparison.Ordinal) >= 0, "");

            // S. 复审：脱敏器不能误伤版本号 / 类名，也不能漏掉管道实例 ID。
            check("S 脱敏保留程序版本号",
                Redactor.Apply("ProxyPicker 1.1.0.0").IndexOf("1.1.0.0", StringComparison.Ordinal) >= 0, "");
            check("S 脱敏不搅乱堆栈类名",
                Redactor.Apply("System.InvalidOperationException")
                    .IndexOf("InvalidOperationException", StringComparison.Ordinal) >= 0, "");
            string probe = Environment.UserName + "Extra";
            check("S 脱敏用户名只匹配整词",
                Redactor.Apply(probe).IndexOf(probe, StringComparison.Ordinal) >= 0, "");
            check("S 脱敏隐藏管道全名",
                Redactor.Apply(@"\\.\pipe\verge-mihomo-production-9f8e7d6c5b4a3210")
                    .IndexOf("<管道名已隐藏>", StringComparison.Ordinal) >= 0, "");
            check("S 脱敏隐藏短实例 ID",
                Redactor.Apply("管道：verge-mihomo-abc12345")
                    .IndexOf("<实例标识已隐藏>", StringComparison.Ordinal) >= 0, "");
            check("S 脱敏隐藏 JSON 响应里的 id 字段",
                Redactor.Apply("{\"proxies\":{\"n1\":{\"id\":\"52b59c6a-1b2c-3d4e-5f60-7a8b9c0d1e2f\",\"alive\":true}}}")
                    .IndexOf("<标识已隐藏>", StringComparison.Ordinal) >= 0, "");
            // 第四轮自查：版本号豁免必须只认「版本号那个位置」。
            // 一行里同时出现版本号和真实 IP 时，IP 仍然要打码。
            string mixedLine = Redactor.Apply("ProxyPicker 1.1.0.0 已连上 45.33.12.7");
            check("S 版本号保留但同行真实 IP 仍打码",
                mixedLine.IndexOf("1.1.0.0", StringComparison.Ordinal) >= 0 &&
                mixedLine.IndexOf("45.33.12.7", StringComparison.Ordinal) < 0 &&
                mixedLine.IndexOf("x.x.x.x", StringComparison.Ordinal) >= 0, "");
            string pipeMasked = Redactor.MaskPipeList("verge-mihomo-production-9f8e7d6c5b4a3210");
            check("S 管道清单打码实例标识",
                pipeMasked.IndexOf("<实例标识已隐藏>", StringComparison.Ordinal) >= 0 &&
                pipeMasked.IndexOf("9f8e7d6c5b4a3210", StringComparison.Ordinal) < 0 &&
                pipeMasked.IndexOf("verge-mihomo-production", StringComparison.Ordinal) >= 0, "");

            // T. 通用辅助进程覆盖：按安装目录整树，而不是逐个猜 exe 名字。
            check("T 主程序目录即软件根",
                AppGroup.RootFor(@"C:\Program Files\Antigravity\antigravity.exe") ==
                @"C:\Program Files\Antigravity", AppGroup.RootFor(@"C:\Program Files\Antigravity\antigravity.exe"));
            check("T 深处 helper 收回到软件根",
                AppGroup.RootFor(@"C:\Program Files\Antigravity\resources\bin\language_server.exe") ==
                @"C:\Program Files\Antigravity",
                AppGroup.RootFor(@"C:\Program Files\Antigravity\resources\bin\language_server.exe"));
            check("T 版本号隔层也收回到软件根",
                AppGroup.RootFor(@"D:\Tools\Foo\app-1.2.3\foo.exe") == @"D:\Tools\Foo",
                AppGroup.RootFor(@"D:\Tools\Foo\app-1.2.3\foo.exe"));
            check("T 公共容器不生成目录规则",
                !AppGroup.IsSafeRoot(@"C:\Program Files") &&
                !AppGroup.IsSafeRoot(@"C:\Program Files (x86)") &&
                !AppGroup.IsSafeRoot(Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs")) &&
                !AppGroup.IsSafeRoot(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)), "");
            check("T 盘符根与单层目录不生成目录规则",
                !AppGroup.IsSafeRoot(@"D:\") && !AppGroup.IsSafeRoot(@"D:\Tools"), "");
            // 第 18 轮 F-05：公共容器本身的子目录（Program Files\bin）不能当软件根；
            // 但装在 Public\App 里的软件，自己的 App 目录可以整树覆盖。
            check("T 公共容器下的通用子目录不作为软件根",
                AppGroup.RootFor(@"C:\Program Files\bin\x.exe") == null &&
                AppGroup.RootFor(@"C:\Users\Public\App\a.exe") == @"C:\Users\Public\App",
                AppGroup.RootFor(@"C:\Program Files\bin\x.exe") ?? "(null)");
            check("T 公共容器本身不允许整树覆盖",
                !AppGroup.IsSafeRoot(@"C:\Users\Public") &&
                !AppGroup.IsSafeRoot(@"C:\Program Files\bin"), "");
            check("T 名字叫 Users 的普通目录不被误判为公共容器",
                AppGroup.RootFor(@"D:\Tools\Users\Foo\a.exe") == @"D:\Tools\Users\Foo",
                AppGroup.RootFor(@"D:\Tools\Users\Foo\a.exe") ?? "(null)");
            check("T 软件自己的目录允许整树覆盖",
                AppGroup.IsSafeRoot(@"C:\Program Files\Antigravity") &&
                AppGroup.IsSafeRoot(@"D:\Tools\Foo"), "");

            string pattern = AppGroup.PatternFor(@"C:\Program Files\Antigravity");
            check("T 目录正则锚定且转义",
                pattern == @"(?i)^C:\\Program\ Files\\Antigravity\\", pattern ?? "(null)");
            check("T 目录正则不会顺带匹配相近目录",
                System.Text.RegularExpressions.Regex.IsMatch(
                    @"C:\Program Files\Antigravity\resources\bin\language_server.exe", pattern) &&
                !System.Text.RegularExpressions.Regex.IsMatch(
                    @"C:\Program Files\Antigravity2\antigravity.exe", pattern), "");
            check("T 含逗号的目录拒绝生成规则",
                AppGroup.PatternFor(@"D:\a,b") == null, "");

            var mixed = new List<Profile.Item>
            {
                new Profile.Item("antigravity.exe", "PP-FIXED"),
                new Profile.Item(pattern, "PP-FIXED", true)
            };
            string withPath = BuildPayload(b, mixed, "PP-FIXED", false);
            check("T 目录规则写进内联规则",
                withPath.IndexOf("- PROCESS-PATH-REGEX," + pattern + ",PP-FIXED", StringComparison.Ordinal) >= 0, "");
            check("T 进程名规则仍排在最前",
                withPath.IndexOf("- PROCESS-NAME,antigravity.exe", StringComparison.Ordinal) >= 0 &&
                withPath.IndexOf("- PROCESS-NAME,antigravity.exe", StringComparison.Ordinal) <
                withPath.IndexOf("- PROCESS-PATH-REGEX,", StringComparison.Ordinal), "");
            check("T 带目录规则时两遍生成逐字节相同",
                BuildPayload(withPath, mixed, "PP-FIXED", false) == withPath, "");
            check("T 兼容模式下目录规则也插在订阅规则之前",
                BuildPayload(b, mixed, "🚀 节点选择", true).IndexOf("- PROCESS-PATH-REGEX," + pattern + ",🚀 节点选择", StringComparison.Ordinal) >= 0, "");

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

        /// <summary>
        /// 除已选目录之外，还躺着配置文件的客户端目录。
        /// 装了多个 Clash 系客户端时，配置基线（磁盘文件）和真正在跑的内核可能不是同一个，
        /// 结果就是「名单显示已生效，但实际驱动的不是你以为的那个内核」。
        /// 这里只做提示、不做自动切换：判断信号不可靠，猜错比不提示更麻烦（评审 A3a）。
        /// </summary>
        public static List<string> OtherClients(string chosenDir)
        {
            var others = new List<string>();
            string chosen = NormDir(chosenDir);
            foreach (var c in Inspect(false))
            {
                string dir = NormDir(c.DataDir);
                if (dir.Length == 0 || string.Equals(dir, chosen, StringComparison.OrdinalIgnoreCase)) continue;
                if (others.Contains(dir)) continue;
                others.Add(dir);
            }
            return others;
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
            sb.AppendLine("选择依据：" + (env.SelectionReason.Length > 0 ? env.SelectionReason : "-"));
            if (!string.IsNullOrEmpty(env.Warning)) sb.AppendLine("多客户端提醒：" + env.Warning);
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
            var cands = Inspect(false);
            if (cands.Count > 0)
            {
                sb.AppendLine("候选客户端（按得分排序）：");
                for (int i = 0; i < cands.Count; i++)
                {
                    var c = cands[i];
                    sb.AppendLine("  " + (i == 0 ? "[选中] " : "       ") +
                        c.Client + " · 得分 " + c.Score +
                        " · 内核目录=" + (c.KernelDir ? "是" : "否") +
                        " · 客户端进程=" + (c.ClientRunning ? "在跑" : "未见") +
                        " · 控制端口=" + (c.ControllerListening ? "可连" : "未连") +
                        " · 配置更新=" + (c.ConfigWriteUtc == DateTime.MinValue ? "未知" : c.ConfigWriteUtc.ToLocalTime().ToString("MM-dd HH:mm")) +
                        " · " + c.Reason);
                    sb.AppendLine("          目录：" + c.DataDir);
                }
                if (cands.Count > 1)
                    sb.AppendLine("提示：只保留你要用的那个客户端运行，选择最稳；" +
                                  "两个都在运行时，本程序按「内核目录 > 进程 > 控制端口 > 配置更新时间」选。");
            }
            if (env.Error != null) sb.AppendLine("错误：" + env.Error);
            return sb.ToString();
        }
    }
}
