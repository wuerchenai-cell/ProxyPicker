using System;
using System.Collections.Generic;

namespace ProxyPicker.Core
{
    /// <summary>
    /// 实时网速：从内核的连接表算「走代理」和「直连」两条通道的速率。
    ///
    /// mihomo 的 /traffic 是流式接口，而命名管道是一次性请求/应答，没法长连；
    /// 所以这里改用「每 2 秒抓一次 /connections，对同一连接 id 的累计字节求差分」。
    /// 分类按进程名是否在勾选名单里，而不是看规则链路，这样即使内核切到全局也能对上人眼认知。
    /// </summary>
    internal static class Traffic
    {
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
        }

        sealed class Mark
        {
            public long Up;
            public long Down;
        }

        static Dictionary<string, Mark> _prev = new Dictionary<string, Mark>(StringComparer.Ordinal);
        static long _prevTicks;
        static readonly object Gate = new object();

        public static Sample Poll(ICollection<string> proxyExes)
        {
            var s = new Sample();
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

                bool isProxy;
                if (proxyExes != null && proxyExes.Contains(exe)) isProxy = true;
                else isProxy = ChainsThroughProxy(d);

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

        /// <summary>进程名缺失时看链路：最后一段不是 DIRECT 就算走代理。</summary>
        static bool ChainsThroughProxy(Dictionary<string, object> conn)
        {
            object raw;
            if (conn == null || !conn.TryGetValue("chains", out raw)) return false;
            var chains = Json.AsArray(raw);
            if (chains == null || chains.Count == 0) return false;
            string last = chains[chains.Count - 1] as string ?? "";
            if (last.Length == 0) return false;
            if (last.Equals("DIRECT", StringComparison.OrdinalIgnoreCase)) return false;
            if (last.Equals("REJECT", StringComparison.OrdinalIgnoreCase)) return false;
            return true;
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
