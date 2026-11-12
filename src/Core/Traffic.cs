using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;

namespace ProxyPicker.Core
{
    /// <summary>
    /// 实时网速：从内核的连接表算「走代理」和「直连」两条通道的速率。
    ///
    /// mihomo 的 /traffic 是流式接口，而命名管道是一次性请求/应答，没法长连；
    /// 所以这里改用「每 1 秒抓一次 /connections，对同一连接 id 的累计字节求差分」。
    ///
    /// 通道归类一律以 mihomo 返回的 chains（真实链路）为准，勾选名单只代表「预期」。
    /// 以前是「勾了就算代理流量」，于是规则没生效时界面照样报代理，等于替用户撒谎。
    /// 现在两者分开记，不一致就报出来，用户才能真的相信界面上的状态。
    /// </summary>
    internal static class Traffic
    {
        /// <summary>一个软件这一轮的出口对账。</summary>
        public sealed class Route
        {
            public bool Expected;        // 用户勾选策略：true = 应该走代理
            public int Proxy;            // 实测走代理的连接数
            public int Direct;           // 实测直连的连接数
            public int Unknown;          // chains 读不出来的连接数
            public int Rejected;         // 被规则 REJECT 的连接：既不是代理也不是直连
            public string Node = "";     // 代理链路里的节点名
        }

        /// <summary>一条「预期 vs 实际」不一致的明细，界面直接显示软件名和下一步。</summary>
        public sealed class MismatchItem
        {
            public string Exe = "";
            public bool Expected;
            public int Proxy;
            public int Direct;
            public string Node = "";
            public string Reason = "";
            public string Action = "";
        }

        public sealed class Sample
        {
            public double ProxyDown;
            public double ProxyUp;
            public double DirectDown;
            public double DirectUp;
            public int ProxyCount;
            public int DirectCount;
            public double Seconds;
            public bool Ok = true;
            public string Error;

            /// <summary>本轮是否做了「预期 vs 实际」判定（订阅兼容模式下不做，见 Poll 参数）。</summary>
            public bool Judged;
            /// <summary>预期与实际不一致的软件数，界面据此提示「分流异常」。</summary>
            public int Mismatch;
            /// <summary>本次规则生效前就建立的旧连接数：仍计入网速，但不参与对账。</summary>
            public int StaleIgnored;
            /// <summary>链路读不出来、按预期名单估算出口的连接数。界面据此标注「≈ 含估算」，
            /// 不把所有速率都当成内核实测值（独立审阅第二轮）。</summary>
            public int Estimated;
            /// <summary>不一致的明细：哪个 exe、预期什么、实际什么、下一步做什么。</summary>
            public readonly List<MismatchItem> Mismatches = new List<MismatchItem>();
            /// <summary>每个 exe 的实测链路汇总，供诊断信息逐条列出。</summary>
            public readonly Dictionary<string, Route> Routes = new Dictionary<string, Route>(StringComparer.OrdinalIgnoreCase);


            /// <summary>
            /// 这一轮内核真正看得到的进程（exe 名小写 → 连接条数）。
            /// 用途是「覆盖检测」：勾了但长期不在这里的软件，说明它的流量没进内核，
            /// 在当前底座下规则对它无效 —— 这事以前界面完全看不出来。
            /// </summary>
            public readonly Dictionary<string, int> Seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            /// <summary>逐条列出「预期出口 / 实际出口 / 判定」，给一键诊断用。</summary>
            public string DescribeOutbound()
            {
                if (Routes.Count == 0)
                    return "（这一轮没有「本次规则生效后」建立的连接；旧连接 "
                        + StaleIgnored + " 条已排除）\n";
                var sb = new StringBuilder();
                foreach (var kv in Routes.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
                {
                    var rt = kv.Value;
                    int known = rt.Proxy + rt.Direct;
                    string actual;
                    if (known == 0) actual = "无可见连接";
                    else
                    {
                        bool proxy = rt.Proxy > rt.Direct;
                        actual = proxy ? "代理" : "直连";
                        if (proxy && !string.IsNullOrEmpty(rt.Node)) actual += "（" + rt.Node + "）";
                        actual += " · " + rt.Proxy + " 代理 / " + rt.Direct + " 直连";
                    }
                    // 与 Poll 共用同一套判定，避免「界面说没事、诊断说异常」（复审 P2）。
                    string verdict;
                    if (!Judged) verdict = "订阅兼容，不判定";
                    else
                    {
                        bool bad;
                        TryVerdict(rt, out verdict, out bad);
                    }
                    sb.AppendLine(kv.Key + "  预期 " + (rt.Expected ? "代理" : "直连")
                        + " · 实际 " + actual + " · " + verdict);
                }
                if (StaleIgnored > 0)
                    sb.AppendLine("（另有 " + StaleIgnored + " 条本次规则生效前的旧连接，未参与对账）");
                return sb.ToString();
            }
        }

        /// <summary>
        /// 出口判定唯一实现：界面 Mismatch 计数与「一键诊断」文本都走这里。
        /// 链路未知、REJECT 占多数、代理/直连 1:1 平局一律不下结论（评审 R3-10 / R1-11）。
        /// </summary>
        static bool TryVerdict(Route rt, out string verdict, out bool mismatch)
        {
            mismatch = false;
            int known = rt.Proxy + rt.Direct;
            if (known == 0 || rt.Unknown + rt.Rejected > known)
            { verdict = "链路未知"; return false; }
            if (rt.Proxy == rt.Direct)
            { verdict = "样本不足，不下结论"; return false; }
            mismatch = (rt.Proxy > rt.Direct) != rt.Expected;
            verdict = mismatch ? "分流异常" : "正常";
            return true;
        }

        enum Chain { Unknown, Direct, Proxy, Rejected }

        sealed class Mark
        {
            public long Up;
            public long Down;
        }

        static Dictionary<string, Mark> _prev = new Dictionary<string, Mark>(StringComparer.Ordinal);
        static long _prevTicks;
        static readonly object Gate = new object();

        static DateTime _pushedUtc;

        /// <summary>规则成功推给内核时记一次「生效时刻」，由 Profile.Apply 调用。</summary>
        public static void NotePush() { _pushedUtc = DateTime.UtcNow; }

        /// <summary>
        /// 对账基线：优先取最近一次推送；从没推送过就取本进程启动时间。
        /// 只用进程启动时间也够挡住「上次会话留下的长连接」，推送后则更精确。
        /// </summary>
        static DateTime BaselineUtc
        {
            get
            {
                if (_pushedUtc != DateTime.MinValue) return _pushedUtc;
                try { return Process.GetCurrentProcess().StartTime.ToUniversalTime(); }
                catch { return DateTime.MinValue; }
            }
        }

        /// <summary>解析 mihomo 连接里的 start（ISO 8601，通常带 +08:00），统一换算成 UTC。</summary>
        internal static bool TryParseStartUtc(string text, out DateTime utc)
        {
            utc = DateTime.MinValue;
            if (string.IsNullOrEmpty(text)) return false;
            DateTimeOffset dto;
            if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out dto))
                return false;
            utc = dto.UtcDateTime;
            return true;
        }

        /// <summary>基线未知时不排除任何连接；规则生效前建立的连接一律算「旧」。</summary>
        internal static bool IsStale(DateTime startUtc, DateTime baselineUtc)
        {
            return baselineUtc != DateTime.MinValue && startUtc < baselineUtc;
        }

        /// <param name="judgeMismatch">
        /// 是否把「预期与实际不符」判为异常。订阅兼容模式下未勾选的软件按订阅规则走，
        /// 走代理本来就是合法的，所以那种模式不判定。
        /// </param>
        public static Sample Poll(ICollection<string> proxyExes, bool judgeMismatch = true)
        {
            var s = new Sample();
            s.Judged = judgeMismatch;
            DateTime baseline = BaselineUtc;
            var root = Mihomo.GetJson("/connections", 8000);
            if (root == null)
            {
                s.Ok = false;
                s.Error = "读不到连接表";
                return s;
            }

            object raw;
            var arr = root.TryGetValue("connections", out raw) ? Json.AsArray(raw) : null;
            if (arr == null)
            {
                s.Ok = false;
                s.Error = "连接表格式异常";
                return s;
            }

            var now = new Dictionary<string, Mark>(arr.Count, StringComparer.Ordinal);
            long proxyDown = 0, proxyUp = 0, directDown = 0, directUp = 0;

            foreach (var item in arr)
            {
                var d = Json.AsObject(item);
                var meta = Json.Obj(d, "metadata");
                string exe = Json.Str(meta, "process") ?? "";
                if (exe.Length == 0)
                {
                    string path = Json.Str(meta, "processPath") ?? "";
                    int slash = path.LastIndexOf('\\');
                    exe = slash >= 0 ? path.Substring(slash + 1) : path;
                }
                exe = exe.ToLowerInvariant();
                if (exe.Length > 0)
                {
                    int seenN;
                    s.Seen.TryGetValue(exe, out seenN);
                    s.Seen[exe] = seenN + 1;
                }

                long up = (long)Num(d, "upload");
                long down = (long)Num(d, "download");
                string id = Json.Str(d, "id") ?? "";
                if (id.Length > 0) now[id] = new Mark { Up = up, Down = down };

                Mark before;
                long du = up, dd = down;
                if (id.Length > 0 && _prev.TryGetValue(id, out before))
                {
                    du = up - before.Up; dd = down - before.Down;
                    if (du < 0) du = up;
                    if (dd < 0) dd = down;
                }
                else if (_prevTicks != 0)
                {
                    // 新连接：本次的全部字节就是它这一轮的增量
                }

                bool expected = proxyExes != null && proxyExes.Contains(exe);
                Chain chain = ChainOf(d);

                // 规则只对「新连接」生效：mihomo 不会给已经连上的连接重新匹配。
                // 推送前建立的长连接（浏览器 WebSocket / 推送 / keep-alive）一直保持旧链路，
                // 算进对账就会出现「勾了走代理，实测多数直连」的假警报（实机 2026-11-10）。
                DateTime startUtc;
                bool stale = TryParseStartUtc(Json.Str(d, "start"), out startUtc)
                             && IsStale(startUtc, baseline);

                if (exe.Length > 0 && stale)
                {
                    s.StaleIgnored++;      // 仍计入实时网速，只是不参与出口判定
                }
                else if (exe.Length > 0)
                {
                    Route rt;
                    if (!s.Routes.TryGetValue(exe, out rt))
                    {
                        rt = new Route { Expected = expected };
                        s.Routes[exe] = rt;
                    }
                    if (chain == Chain.Proxy)
                    {
                        rt.Proxy++;
                        if (rt.Node.Length == 0) rt.Node = ChainNode(d);
                    }
                    else if (chain == Chain.Direct) rt.Direct++;
                    else if (chain == Chain.Rejected) rt.Rejected++;
                    else rt.Unknown++;
                }

                // 被 REJECT 的连接没有真实传输，既不计入直连速率，也不参与出口判定（评审 R1-11）。
                if (chain == Chain.Rejected) continue;

                // 速率归类看实测链路；链路读不出来才用名单兜底，避免偶发缺字段打出假峰。
                // 这部分是估算而非实测，单独计数供界面标注。
                if (chain == Chain.Unknown && exe.Length > 0 && !stale) s.Estimated++;
                bool isProxy = chain == Chain.Unknown ? expected : chain == Chain.Proxy;

                if (isProxy)
                {
                    proxyUp += du; proxyDown += dd;
                    s.ProxyCount++;
                }
                else
                {
                    directUp += du; directDown += dd;
                    s.DirectCount++;
                }
            }

            if (judgeMismatch)
            {
                foreach (var kv in s.Routes)
                {
                    string verdict;
                    bool bad;
                    TryVerdict(kv.Value, out verdict, out bad);
                    if (!bad) continue;
                    var rt = kv.Value;
                    s.Mismatches.Add(new MismatchItem
                    {
                        Exe = kv.Key,
                        Expected = rt.Expected,
                        Proxy = rt.Proxy,
                        Direct = rt.Direct,
                        Node = rt.Node,
                        Reason = rt.Expected ? "勾了走代理，实测多数连接走直连" : "没勾选，实测多数连接走了代理",
                        Action = rt.Expected
                            ? "重新保存一次；若仍异常，确认客户端 find-process-mode 不是 off（strict 正常）"
                            : "检查是否被目录规则或订阅规则接管，必要时取消该目录规则"
                    });
                }
                s.Mismatch = s.Mismatches.Count;
            }

            long nowTicks = DateTime.UtcNow.Ticks;
            double seconds = _prevTicks == 0 ? 0 : (nowTicks - _prevTicks) / (double)TimeSpan.TicksPerSecond;
            if (seconds <= 0.05)
            {
                // 第一次采样没有参照，先给 0，避免出现一个巨大的假峰值
                s.ProxyDown = s.ProxyUp = s.DirectDown = s.DirectUp = 0;
                s.Seconds = 0;
            }
            else
            {
                s.ProxyDown = proxyDown / seconds;
                s.ProxyUp = proxyUp / seconds;
                s.DirectDown = directDown / seconds;
                s.DirectUp = directUp / seconds;
                s.Seconds = seconds;
            }

            lock (Gate)
            {
                _prev = now;
                _prevTicks = nowTicks;
            }
            return s;
        }

        /// <summary>看真实链路：DIRECT 算直连，REJECT / REJECT-DROP 算被拒，其余算代理，读不出来算未知。
        /// v1.3.8：出口分类改走 Profile.ClassifyExit 这一处口径。以前这里自己判字符串，
        /// 只认 DIRECT 和 REJECT，REJECT-DROP 的连接被算成「走代理」，和保存校验对不上。</summary>
        static Chain ChainOf(Dictionary<string, object> conn)
        {
            object raw;
            if (conn == null || !conn.TryGetValue("chains", out raw)) return Chain.Unknown;
            var chains = Json.AsArray(raw);
            if (chains == null || chains.Count == 0) return Chain.Unknown;
            string last = chains[chains.Count - 1] as string ?? "";
            if (last.Length == 0) return Chain.Unknown;
            var kind = Profile.ClassifyExit(last);
            if (kind == Profile.ExitKind.Direct) return Chain.Direct;
            if (kind == Profile.ExitKind.Rejected) return Chain.Rejected;
            return Chain.Proxy;
        }

        /// <summary>代理链路最外层那段通常是节点名，给诊断信息显示「实际走到哪」。</summary>
        static string ChainNode(Dictionary<string, object> conn)
        {
            object raw;
            if (conn == null || !conn.TryGetValue("chains", out raw)) return "";
            var chains = Json.AsArray(raw);
            if (chains == null || chains.Count == 0) return "";
            return chains[0] as string ?? "";
        }

        static double Num(Dictionary<string, object> o, string key)
        {
            object v;
            if (o == null || !o.TryGetValue(key, out v) || v == null) return 0;
            if (v is double) return (double)v;
            double d;
            if (v is string && double.TryParse((string)v, out d)) return d;
            return 0;
        }

        /// <summary>自检：把「旧连接排除」和「派生预期」两条口径锁住，防止改回去又报假异常。</summary>
        public static string SelfTest()
        {
            var sb = new StringBuilder();
            int pass = 0, total = 0;
            Action<string, bool> check = (name, ok) =>
            {
                total++;
                if (ok) pass++;
                sb.AppendLine((ok ? "PASS  " : "FAIL  ") + name);
            };

            DateTime t;
            check("T1 带 +08:00 的连接时间能换算成 UTC",
                TryParseStartUtc("2026-11-10T12:37:18.18825+08:00", out t) &&
                t.Year == 2026 && t.Month == 11 && t.Day == 10 && t.Hour == 4 && t.Minute == 37);
            check("T2 空时间不参与排除", !TryParseStartUtc("", out t));
            check("T3 乱码时间不参与排除", !TryParseStartUtc("not-a-time", out t));
            var now = new DateTime(2026, 11, 10, 5, 0, 0, DateTimeKind.Utc);
            check("T4 早于本次推送的连接算旧", IsStale(now.AddMinutes(-10), now));
            check("T5 晚于本次推送的连接算新", !IsStale(now.AddMinutes(10), now));
            check("T6 同一时刻不算旧", !IsStale(now, now));
            check("T7 基线未知时不排除任何连接", !IsStale(now.AddYears(-1), DateTime.MinValue));

            // 派生进程（helper / 登录窗口 / 本程序自身）被算进预期后，
            // 实测走代理就不再被判成「没勾选却走代理」。
            string verdict;
            bool bad;
            TryVerdict(new Route { Expected = true, Proxy = 4, Direct = 0 }, out verdict, out bad);
            check("T8 派生进程走代理不算异常", !bad && verdict == "正常");
            TryVerdict(new Route { Expected = true, Proxy = 0, Direct = 0 }, out verdict, out bad);
            check("T9 没有可用样本时不下结论", !bad && verdict == "链路未知");

            // v1.3.8：出口分类必须与保存校验同源。以前这里自己判字符串，
            // REJECT-DROP 的连接被算成「走代理」，和 Profile.IsBlockedPolicy 对不上。
            var connRejectDrop = new Dictionary<string, object>
            {
                { "chains", new List<object> { "REJECT-DROP" } }
            };
            var connReject = new Dictionary<string, object>
            {
                { "chains", new List<object> { "REJECT" } }
            };
            check("T10 REJECT-DROP 与 REJECT 归属同一类（不再被当成走代理）",
                ChainOf(connRejectDrop) == Chain.Rejected &&
                ChainOf(connReject) == Chain.Rejected &&
                Profile.ClassifyExit("REJECT-DROP") == Profile.ExitKind.Rejected &&
                Profile.IsBlockedPolicy("REJECT-DROP"));
            check("T11 真实节点仍算代理、DIRECT 仍算直连",
                Profile.ClassifyExit("日本 01") == Profile.ExitKind.Proxy &&
                !Profile.IsBlockedPolicy("日本 01") &&
                Profile.ClassifyExit("direct") == Profile.ExitKind.Direct);

            sb.AppendLine(pass + "/" + total + " 流量对账口径通过");
            return sb.ToString();
        }

        public static void Reset()
        {
            lock (Gate) { _prev = new Dictionary<string, Mark>(StringComparer.Ordinal); _prevTicks = 0; }
        }

        /// <summary>把字节速率变成人看的短字符串。</summary>
        public static string Speed(double bytesPerSecond)
        {
            if (bytesPerSecond < 0) bytesPerSecond = 0;
            if (bytesPerSecond < 1024) return ((int)Math.Round(bytesPerSecond)) + " B/s";
            double kb = bytesPerSecond / 1024.0;
            if (kb < 1024) return kb.ToString(kb < 10 ? "0.0" : "0") + " KB/s";
            double mb = kb / 1024.0;
            return mb.ToString(mb < 10 ? "0.0" : "0") + " MB/s";
        }
    }
}
