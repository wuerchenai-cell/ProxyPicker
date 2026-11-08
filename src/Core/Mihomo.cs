using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace ProxyPicker.Core
{
    internal sealed class ApiResult
    {
        public int Status;
        public string Body = "";
        public long Ms;
        public string Error;

        public bool Ok { get { return Error == null && Status > 0 && Status < 400; } }
    }

    /// <summary>
    /// mihomo 控制面客户端，支持两种传输：
    ///
    ///  ① 命名管道（Clash Verge 系默认）：不开任何 TCP 端口，最安全。
    ///     注意管道实际名常是 verge-mihomo-production-&lt;uid&gt;，配置里写的 sidecar 连不上，必须枚举。
    ///  ② HTTP（其他客户端/裸内核）：从配置读 external-controller，读不到再试常见端口。
    ///
    /// 先管道后 HTTP，两者都没有就明确报「找不到内核」，而不是抛一堆异常。
    /// </summary>
    internal static class Mihomo
    {
        const string PipeRoot = @"\\.\pipe\";

        static string _pipeName;
        static string _secret;
        static string _endpoint;
        static bool _endpointTried;

        public static string LastPipeName { get { return _pipeName; } }
        public static string LastEndpoint { get { return _endpoint; } }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr FindFirstFileW(string lpFileName, out WIN32_FIND_DATA lpFindFileData);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool FindNextFileW(IntPtr hFindFile, out WIN32_FIND_DATA lpFindFileData);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool FindClose(IntPtr hFindFile);

        // 注意：FILETIME 是两个 DWORD（8 字节），不是两个 long（16 字节）。
        // 写成 long 会让后面的 cFileName 偏移错位，读到的管道名只剩尾部片段。
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct WIN32_FIND_DATA
        {
            public uint dwFileAttributes;
            public uint ftCreationTimeLow, ftCreationTimeHigh;
            public uint ftLastAccessTimeLow, ftLastAccessTimeHigh;
            public uint ftLastWriteTimeLow, ftLastWriteTimeHigh;
            public uint nFileSizeHigh, nFileSizeLow;
            public uint dwReserved0, dwReserved1;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string cFileName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string cAlternateFileName;
        }

        public static List<string> EnumNamedPipes()
        {
            var result = new List<string>();
            WIN32_FIND_DATA fd;
            IntPtr h = FindFirstFileW(PipeRoot + "*", out fd);
            if (h == new IntPtr(-1)) return result;
            try
            {
                do
                {
                    string n = fd.cFileName;
                    if (!string.IsNullOrEmpty(n) && n != "." && n != "..") result.Add(n);
                }
                while (FindNextFileW(h, out fd));
            }
            finally { FindClose(h); }
            return result;
        }

        /// <summary>找到 mihomo 的控制管道（优先 production）。</summary>
        public static string FindPipe()
        {
            var all = EnumNamedPipes();
            var hits = all
                .Where(n => n.StartsWith("verge-mihomo-", StringComparison.OrdinalIgnoreCase) ||
                            n.StartsWith("mihomo-", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (hits.Count == 0)
            {
                hits = all
                    .Where(n => n.IndexOf("mihomo", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                n.IndexOf("clash", StringComparison.OrdinalIgnoreCase) >= 0)
                    .ToList();
            }
            if (hits.Count == 0) return null;
            return hits.FirstOrDefault(n => n.IndexOf("production", StringComparison.OrdinalIgnoreCase) >= 0)
                   ?? hits[0];
        }

        public static string ReadSecret()
        {
            try
            {
                string p = Discovery.Detect().ConfigPath;
                if (string.IsNullOrEmpty(p) || !File.Exists(p)) return "";
                foreach (var line in File.ReadLines(p))
                {
                    if (line.StartsWith("secret:", StringComparison.Ordinal))
                        return line.Substring(7).Trim().Trim('\'', '"');
                }
            }
            catch { }
            return "";
        }

        /// <summary>发一次请求：先管道，失败再走本地 HTTP。</summary>
        public static ApiResult Request(string method, string path, string body, int timeoutMs)
        {
            var sw = Stopwatch.StartNew();
            var viaPipe = TryPipe(method, path, body, timeoutMs, sw);
            if (viaPipe != null) return viaPipe;

            var viaHttp = TryHttp(method, path, body, timeoutMs, sw);
            if (viaHttp != null) return viaHttp;

            return new ApiResult
            {
                Error = "找不到 mihomo 控制接口（Clash / mihomo 没启动？）",
                Ms = sw.ElapsedMilliseconds
            };
        }

        static ApiResult TryPipe(string method, string path, string body, int timeoutMs, Stopwatch sw)
        {
            if (_pipeName == null) _pipeName = FindPipe();
            if (_pipeName == null) return null;
            if (_secret == null) _secret = ReadSecret();

            var r = new ApiResult();
            try
            {
                using (var pipe = PipeConnection.Open(PipeRoot + _pipeName))
                {
                    byte[] payload = Encoding.UTF8.GetBytes(body ?? "");
                    string head =
                        method + " " + path + " HTTP/1.0\r\n" +
                        "Host: localhost\r\n" +
                        "Authorization: Bearer " + _secret + "\r\n" +
                        "Content-Type: application/json\r\n" +
                        "Content-Length: " + payload.Length + "\r\n\r\n";
                    byte[] hb = Encoding.ASCII.GetBytes(head);
                    byte[] all = hb;
                    if (payload.Length > 0)
                    {
                        all = new byte[hb.Length + payload.Length];
                        Buffer.BlockCopy(hb, 0, all, 0, hb.Length);
                        Buffer.BlockCopy(payload, 0, all, hb.Length, payload.Length);
                    }
                    pipe.WriteAll(all);
                    byte[] raw = pipe.ReadToEnd(timeoutMs);
                    ParseHttp(Encoding.UTF8.GetString(raw), r);
                    r.Ms = sw.ElapsedMilliseconds;
                    return r;
                }
            }
            catch (Exception ex)
            {
                // 管道名可能因为内核重启而变，下次重新枚举；同时放行去试 HTTP
                _pipeName = null;
                r.Error = ex.GetType().Name + ": " + ex.Message;
                r.Ms = sw.ElapsedMilliseconds;
                return null;
            }
        }

        static ApiResult TryHttp(string method, string path, string body, int timeoutMs, Stopwatch sw)
        {
            string ep = Endpoint();
            if (ep == null) return null;
            if (_secret == null) _secret = ReadSecret();

            var r = new ApiResult();
            try
            {
                var req = (HttpWebRequest)WebRequest.Create("http://" + ep + path);
                req.Method = method;
                req.Timeout = Math.Max(1500, timeoutMs);
                req.ReadWriteTimeout = Math.Max(1500, timeoutMs);
                req.Proxy = null;          // 别让系统代理把控制请求也带走
                req.KeepAlive = false;
                req.AllowAutoRedirect = false;
                if (!string.IsNullOrEmpty(_secret))
                    req.Headers["Authorization"] = "Bearer " + _secret;

                if (body != null)
                {
                    byte[] data = Encoding.UTF8.GetBytes(body);
                    req.ContentType = "application/json";
                    req.ContentLength = data.Length;
                    using (var s = req.GetRequestStream()) s.Write(data, 0, data.Length);
                }

                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    r.Status = (int)resp.StatusCode;
                    r.Body = sr.ReadToEnd();
                }
            }
            catch (WebException we)
            {
                var resp = we.Response as HttpWebResponse;
                if (resp != null)
                {
                    r.Status = (int)resp.StatusCode;
                    try
                    {
                        using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                            r.Body = sr.ReadToEnd();
                    }
                    catch { }
                    resp.Close();
                }
                else
                {
                    _endpoint = null; _endpointTried = false;
                    r.Error = we.Status + ": " + we.Message;
                }
            }
            catch (Exception ex)
            {
                _endpoint = null; _endpointTried = false;
                r.Error = ex.GetType().Name + ": " + ex.Message;
            }
            r.Ms = sw.ElapsedMilliseconds;
            return r;
        }

        static void ParseHttp(string text, ApiResult r)
        {
            int sep = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            string headTxt = sep < 0 ? text : text.Substring(0, sep);
            r.Body = sep < 0 ? "" : text.Substring(sep + 4);
            string firstLine = headTxt.Split(new[] { "\r\n" }, StringSplitOptions.None)[0];
            string[] parts = firstLine.Split(' ');
            if (parts.Length >= 2)
            {
                int st;
                if (int.TryParse(parts[1], out st)) r.Status = st;
            }
        }

        /// <summary>挑一个能连上的控制地址：配置里写的优先，其次常见端口。</summary>
        static string Endpoint()
        {
            if (_endpointTried && _endpoint != null) return _endpoint;
            _endpointTried = true;
            _endpoint = null;

            var candidates = new List<string>();
            var env = Discovery.Detect();
            if (!string.IsNullOrEmpty(env.Controller)) candidates.Add(env.Controller);
            candidates.Add("127.0.0.1:9090");
            candidates.Add("127.0.0.1:9097");
            candidates.Add("127.0.0.1:6170");
            candidates.Add("127.0.0.1:9091");

            foreach (var ep in candidates)
            {
                string host = ep;
                int port = 0;
                int colon = ep.LastIndexOf(':');
                if (colon > 0)
                {
                    host = ep.Substring(0, colon);
                    int.TryParse(ep.Substring(colon + 1), out port);
                }
                if (port <= 0) continue;
                try
                {
                    using (var c = new TcpClient())
                    {
                        var ar = c.BeginConnect(host, port, null, null);
                        using (var wait = ar.AsyncWaitHandle)
                            if (!wait.WaitOne(280)) continue;
                        c.EndConnect(ar);
                        _endpoint = ep;
                        return _endpoint;
                    }
                }
                catch { }
            }
            return null;
        }

        public static Dictionary<string, object> GetJson(string path, int timeoutMs = 10000)
        {
            var r = Request("GET", path, null, timeoutMs);
            if (!r.Ok) return null;
            try { return Json.AsObject(Json.Parse(r.Body)); }
            catch { return null; }
        }

        /// <summary>把完整配置文本推给内核（不落盘、不触发 SAFE_PATHS 限制）。</summary>
        public static ApiResult PushConfig(string yamlText, int timeoutMs = 90000)
        {
            return Request("PUT", "/configs", "{\"payload\":" + Json.Quote(yamlText) + "}", timeoutMs);
        }

        /// <summary>关闭指定的一条旧连接。只由「本次改动软件」的定向重连调用。</summary>
        public static ApiResult DeleteConnection(string id, int timeoutMs = 5000)
        {
            return Request("DELETE", "/connections/" + Uri.EscapeDataString(id ?? ""), null, timeoutMs);
        }

        /// <summary>
        /// 调试用：走一遍和正式请求相同的通道，但把原始响应（含状态行与头部）原样返回。
        ///
        /// 存在的理由：正式路径只解析 body，一旦路径写错或撞上非 mihomo 的服务，
        /// 看到的只是一句 404，分不清是「没连上」还是「连错了地方」。
        /// </summary>
        public static string RequestRaw(string method, string path, string body, int timeoutMs)
        {
            var sb = new StringBuilder();
            string pipe = FindPipe();
            string sec = ReadSecret();
            sb.AppendLine("pipe     : " + (pipe ?? "<未找到>"));
            sb.AppendLine("secret   : " + (string.IsNullOrEmpty(sec) ? "未配置" : "已配置（不回显）"));
            sb.AppendLine("endpoint : " + (Endpoint() ?? "<无 HTTP 端口>"));
            sb.AppendLine();
            if (pipe == null) return sb.ToString();

            try
            {
                using (var p = PipeConnection.Open(PipeRoot + pipe))
                {
                    byte[] payload = Encoding.UTF8.GetBytes(body ?? "");
                    string head =
                        method + " " + path + " HTTP/1.0\r\n" +
                        "Host: localhost\r\n" +
                        "Authorization: Bearer " + sec + "\r\n" +
                        "Content-Type: application/json\r\n" +
                        "Content-Length: " + payload.Length + "\r\n\r\n";
                    byte[] hb = Encoding.ASCII.GetBytes(head);
                    byte[] all = hb;
                    if (payload.Length > 0)
                    {
                        all = new byte[hb.Length + payload.Length];
                        Buffer.BlockCopy(hb, 0, all, 0, hb.Length);
                        Buffer.BlockCopy(payload, 0, all, hb.Length, payload.Length);
                    }
                    p.WriteAll(all);
                    sb.Append(Encoding.UTF8.GetString(p.ReadToEnd(timeoutMs)));
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine("[异常] " + ex.GetType().Name + ": " + ex.Message);
            }
            return sb.ToString();
        }

        public static void Forget()
        {
            _pipeName = null;
            _secret = null;
            _endpoint = null;
            _endpointTried = false;
        }
    }
}
