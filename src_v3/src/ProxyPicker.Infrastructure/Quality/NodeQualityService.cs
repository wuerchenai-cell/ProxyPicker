using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ProxyPicker.Domain.Interfaces;
using ProxyPicker.Domain.Json;
using ProxyPicker.Domain.Models;
using ProxyPicker.Domain.Services;
using ProxyPicker.Platform.Windows;

namespace ProxyPicker.Infrastructure.Quality
{
    /// <summary>
    /// 零干扰节点风控与链路纯净度实测服务：
    /// 1) 优先启动隔离旁路 mihomo 会话（独立端口 + KillOnCloseJob + 私有目录），完全不干扰宿主主节点与用户连接；
    /// 2) 批量全节点检测内置同轮 IP 情报缓存（防 429 限流），同时每个节点均独立实测 Google / Gemini / OpenAI / Claude 四大门检。
    /// </summary>
    public sealed class NodeQualityService : INodeQualityService
    {
        const int MaxProbeBodyBytes = 64 * 1024;
        const string ProbeUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

        readonly IMihomoClient _mihomo;
        readonly string _ownDataDir;

        public NodeQualityService(IMihomoClient mihomo, string ownDataDir)
        {
            _mihomo = mihomo;
            _ownDataDir = ownDataDir;
        }

        public NodeQualityReport ProbeSingleNode(string targetNode, string desiredRegion, string configPath, CancellationToken ct)
        {
            EnsureTls();
            var list = ProbeBatchNodes(new[] { targetNode ?? "" }, desiredRegion, configPath, null, ct);
            return list.Count > 0 ? list[0] : new NodeQualityReport { TargetNode = targetNode ?? "", Error = "检测未完成" };
        }

        public List<NodeQualityReport> ProbeBatchNodes(
            IList<string> nodeNames,
            string desiredRegion,
            string configPath,
            Action<int, int, NodeQualityReport> onProgress,
            CancellationToken ct)
        {
            EnsureTls();
            var targets = (nodeNames ?? new string[0]).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var results = new List<NodeQualityReport>();
            if (targets.Count == 0) return results;

            var ipCache = new Dictionary<string, Tuple<IpIntelligence, IpIntelligence>>(StringComparer.OrdinalIgnoreCase);

            // 尝试启动零干扰隔离侧车会话；若找不到内核则平滑回退到宿主代理（仅单节点模式）
            using (var sidecar = TryStartQualitySidecar(configPath))
            {
                bool useSidecar = sidecar != null && sidecar.Ready;
                for (int i = 0; i < targets.Count; i++)
                {
                    if (ct.IsCancellationRequested) break;
                    string node = targets[i];
                    NodeQualityReport rep;
                    if (useSidecar)
                    {
                        sidecar.SelectGlobalNode(node);
                        rep = RunProbeCore(node, desiredRegion, sidecar.OutboundProxy, sidecar.ProxyPort, true,
                            targets.Count == 1 ? 3 : 1,
                            () => sidecar.MeasureDelay(node, 3000),
                            ipCache, configPath);
                    }
                    else
                    {
                        rep = RunProbeViaHost(node, desiredRegion, targets.Count == 1 ? 3 : 1, ipCache, configPath);
                    }
                    results.Add(rep);
                    try { onProgress?.Invoke(i + 1, targets.Count, rep); } catch { }
                }
            }
            return results;
        }

        NodeQualityReport RunProbeViaHost(
            string targetNode, string desiredRegion, int pingRounds,
            Dictionary<string, Tuple<IpIntelligence, IpIntelligence>> ipCache, string configPath)
        {
            WebProxy proxy = null;
            int mixedPort = 0;
            bool tun = false, dns = false, ipv6 = false;
            string mode = "rule";
            var cfgRes = _mihomo.FetchJson("/configs", 3000, configPath);
            if (cfgRes.Ok && cfgRes.Data != null)
            {
                mode = JsonCodec.Str(cfgRes.Data, "mode", "rule").ToLowerInvariant();
                tun = JsonCodec.Bool(JsonCodec.Obj(cfgRes.Data, "tun"), "enable", false);
                dns = JsonCodec.Bool(JsonCodec.Obj(cfgRes.Data, "dns"), "enable", false);
                ipv6 = JsonCodec.Bool(cfgRes.Data, "ipv6", false);
                mixedPort = JsonCodec.Int(cfgRes.Data, "mixed-port", 0);
                if (mixedPort <= 0) mixedPort = JsonCodec.Int(cfgRes.Data, "port", 0);
                if (mixedPort > 0 && mixedPort <= 65535) proxy = new WebProxy("127.0.0.1", mixedPort);
            }

            var rep = RunProbeCore(targetNode, desiredRegion, proxy, mixedPort, false, pingRounds,
                () =>
                {
                    var d = _mihomo.MeasureProxyDelay(targetNode, "http://www.gstatic.com/generate_204", 3500, configPath);
                    return (d.Ok && d.Data != null) ? JsonCodec.Int(d.Data, "delay", -1) : -1;
                },
                ipCache, configPath, mode, tun, dns, ipv6);
            return rep;
        }

        NodeQualityReport RunProbeCore(
            string targetNode,
            string desiredRegion,
            WebProxy outboundProxy,
            int proxyPort,
            bool isSidecar,
            int pingRounds,
            Func<int> measureDelayOnce,
            Dictionary<string, Tuple<IpIntelligence, IpIntelligence>> ipCache,
            string configPath,
            string kernelMode = "global",
            bool tunEnabled = false,
            bool dnsEnabled = false,
            bool kernelIpv6 = false)
        {
            var report = new NodeQualityReport
            {
                CheckedAtUtc = DateTime.UtcNow,
                TargetNode = targetNode ?? "",
                DesiredRegion = NodeQualityEvaluator.NormalizeDesiredRegion(desiredRegion),
                ExpectedRegion = NodeQualityEvaluator.ParseRegionFromNodeName(targetNode),
                IsSidecarIsolated = isSidecar,
                MixedPort = proxyPort,
                KernelMode = kernelMode,
                TunEnabled = tunEnabled,
                DnsEnabled = dnsEnabled,
                KernelIpv6On = kernelIpv6
            };

            var samples = new List<int>(pingRounds);
            for (int r = 0; r < pingRounds; r++)
                samples.Add(measureDelayOnce());
            report.Net = NodeQualityEvaluator.ComputeLatencyStats(samples);

            if (report.Net.Received == 0 && pingRounds == 1)
            {
                report.Error = "节点测速超时";
                NodeQualityEvaluator.EvaluateReport(report, new IpIntelligence { Error = report.Error }, null);
                return report;
            }

            bool allowTunFallback = !isSidecar && tunEnabled;
            bool runIpv6Probe = NodeQualityEvaluator.ShouldRunHostIpv6LeakProbe(isSidecar, outboundProxy != null, tunEnabled);

            string cfIp = "", cfLoc = "", cfColo = "", rawV6 = "";
            bool ipv6Done = false;

            Task tCf = Task.Run(() => { report.Cloudflare = ProbeCloudflare(outboundProxy, allowTunFallback, out cfIp, out cfLoc, out cfColo); });
            Task tG = Task.Run(() => { report.GoogleSearch = ProbeGoogle(outboundProxy, allowTunFallback); });
            Task tGem = Task.Run(() => { report.GeminiApi = ProbeGemini(outboundProxy, allowTunFallback); });
            Task tOai = Task.Run(() => { report.OpenAiApi = ProbeOpenAi(outboundProxy, allowTunFallback); });
            Task tCl = Task.Run(() => { report.ClaudeApi = ProbeClaude(outboundProxy, allowTunFallback); });
            Task tV6 = Task.Run(() =>
            {
                if (!runIpv6Probe) return;
                rawV6 = ProbeRawIpv6();
                Volatile.Write(ref ipv6Done, true);
            });
            Task.WaitAll(new[] { tCf, tG, tGem, tOai, tCl, tV6 }, 7500);

            report.CfColo = cfColo;
            report.Ipv6LeakChecked = Volatile.Read(ref ipv6Done);

            IpIntelligence primary = null;
            IpIntelligence secondary = null;
            Tuple<IpIntelligence, IpIntelligence> cached;
            if (!string.IsNullOrEmpty(cfIp) && ipCache != null && ipCache.TryGetValue(cfIp, out cached))
            {
                primary = cached.Item1;
                secondary = cached.Item2;
            }
            else
            {
                primary = !string.IsNullOrEmpty(cfIp)
                    ? FetchIpQueryForIp(cfIp, outboundProxy, allowTunFallback)
                    : FetchPrimaryIp(outboundProxy, allowTunFallback);
                if ((primary == null || !primary.Ok) && !string.IsNullOrEmpty(cfIp))
                {
                    primary = FetchPrimaryIp(outboundProxy, allowTunFallback);
                    if (primary == null || !primary.Ok)
                        primary = new IpIntelligence { Ok = !string.IsNullOrEmpty(cfLoc), Ip = cfIp, CountryCode = cfLoc, Source = "cloudflare" };
                }
                string ipKey = !string.IsNullOrEmpty(primary?.Ip) ? primary.Ip : cfIp;
                if (!string.IsNullOrEmpty(ipKey))
                    secondary = FetchProxyCheck(ipKey, outboundProxy, allowTunFallback);
                if (ipCache != null && !string.IsNullOrEmpty(ipKey) && primary != null && primary.Ok)
                    ipCache[ipKey] = Tuple.Create(primary, secondary);
            }

            if (primary == null) primary = new IpIntelligence();
            if (!string.IsNullOrEmpty(rawV6))
            {
                var v6Info = FetchIpQueryForIp(rawV6, outboundProxy, allowTunFallback);
                string v6Cc = v6Info?.CountryCode ?? "";
                bool diff = !string.IsNullOrEmpty(primary.CountryCode) && !string.IsNullOrEmpty(v6Cc) &&
                            !string.Equals(v6Cc, primary.CountryCode, StringComparison.OrdinalIgnoreCase);
                if (string.Equals(v6Cc, "CN", StringComparison.OrdinalIgnoreCase) || diff)
                {
                    report.DirectIpv6Leak = true;
                    report.LeakedIpv6 = rawV6;
                }
            }

            if (string.IsNullOrEmpty(primary.Ip) && !string.IsNullOrEmpty(primary.Error))
                report.Error = primary.Error;

            NodeQualityEvaluator.EvaluateReport(report, primary, secondary);
            return report;
        }

        // ---- HTTP 探针实现 ----

        static void EnsureTls()
        {
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
            try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)12288; } catch { }
        }

        static bool HttpGetWithFallback(string url, WebProxy proxy, bool allowTunFallback, int timeoutMs,
            out int status, out string loc, out string body, out int ms, out string err)
        {
            if (HttpGet(url, proxy, timeoutMs, out status, out loc, out body, out ms, out err)) return true;
            if (proxy != null && allowTunFallback)
                return HttpGet(url, null, timeoutMs, out status, out loc, out body, out ms, out err);
            return false;
        }

        static bool HttpGet(string url, WebProxy proxy, int timeoutMs,
            out int status, out string loc, out string body, out int ms, out string err)
        {
            status = 0; loc = ""; body = ""; err = "";
            var sw = Stopwatch.StartNew();
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "GET";
                req.Timeout = timeoutMs;
                req.ReadWriteTimeout = timeoutMs;
                req.Proxy = proxy;
                req.KeepAlive = false;
                req.AllowAutoRedirect = false;
                req.UserAgent = ProbeUserAgent;
                req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
                using (var resp = (HttpWebResponse)req.GetResponse())
                {
                    status = (int)resp.StatusCode;
                    loc = resp.Headers["Location"] ?? "";
                    body = ReadLimited(resp.GetResponseStream());
                    ms = (int)sw.ElapsedMilliseconds;
                    return true;
                }
            }
            catch (WebException we)
            {
                var resp = we.Response as HttpWebResponse;
                if (resp != null)
                {
                    try
                    {
                        status = (int)resp.StatusCode;
                        loc = resp.Headers["Location"] ?? "";
                        body = ReadLimited(resp.GetResponseStream());
                    }
                    catch { }
                    finally { resp.Close(); }
                    ms = (int)sw.ElapsedMilliseconds;
                    return true;
                }
                err = we.Status == WebExceptionStatus.Timeout ? "连接超时" : we.Message;
                ms = (int)sw.ElapsedMilliseconds;
                return false;
            }
            catch (Exception ex)
            {
                err = ex.Message;
                ms = (int)sw.ElapsedMilliseconds;
                return false;
            }
        }

        static string ReadLimited(Stream stream)
        {
            if (stream == null) return "";
            using (stream)
            using (var ms = new MemoryStream())
            {
                byte[] buf = new byte[4096];
                int left = MaxProbeBodyBytes;
                while (left > 0)
                {
                    int n = stream.Read(buf, 0, Math.Min(buf.Length, left));
                    if (n <= 0) break;
                    ms.Write(buf, 0, n);
                    left -= n;
                }
                return Encoding.UTF8.GetString(ms.ToArray());
            }
        }

        static IpIntelligence FetchPrimaryIp(WebProxy proxy, bool allowTun)
        {
            int st, ms; string loc, body, err;
            if (HttpGetWithFallback("https://api.ipquery.io/?format=json", proxy, allowTun, 3600, out st, out loc, out body, out ms, out err) && st >= 200 && st < 300)
            {
                var q = NodeQualityEvaluator.ParseIpQueryJson(body);
                if (q.Ok) return q;
            }
            if (HttpGetWithFallback("https://ipwho.is/", proxy, allowTun, 3600, out st, out loc, out body, out ms, out err) && st >= 200 && st < 300)
            {
                var w = NodeQualityEvaluator.ParseIpWhoIsJson(body);
                if (w.Ok) return w;
            }
            if (HttpGetWithFallback("https://ipinfo.io/json", proxy, allowTun, 3600, out st, out loc, out body, out ms, out err) && st >= 200 && st < 300)
            {
                var i = NodeQualityEvaluator.ParseIpInfoJson(body);
                if (i.Ok) return i;
            }
            return new IpIntelligence { Error = !string.IsNullOrEmpty(err) ? err : ("IP 查询失败 HTTP " + st) };
        }

        static IpIntelligence FetchIpQueryForIp(string ip, WebProxy proxy, bool allowTun)
        {
            int st, ms; string loc, body, err;
            if (HttpGetWithFallback("https://api.ipquery.io/" + Uri.EscapeDataString(ip), proxy, allowTun, 3400, out st, out loc, out body, out ms, out err) && st >= 200 && st < 300)
            {
                var q = NodeQualityEvaluator.ParseIpQueryJson(body);
                if (q.Ok) return q;
            }
            if (HttpGetWithFallback("https://ipwho.is/" + Uri.EscapeDataString(ip), proxy, allowTun, 3400, out st, out loc, out body, out ms, out err) && st >= 200 && st < 300)
            {
                var w = NodeQualityEvaluator.ParseIpWhoIsJson(body);
                if (w.Ok) return w;
            }
            return null;
        }

        static IpIntelligence FetchProxyCheck(string ip, WebProxy proxy, bool allowTun)
        {
            int st, ms; string loc, body, err;
            string url = "https://proxycheck.io/v2/" + Uri.EscapeDataString(ip) + "?vpn=1&asn=1&risk=1";
            if (HttpGetWithFallback(url, proxy, allowTun, 3400, out st, out loc, out body, out ms, out err) && st >= 200 && st < 300)
                return NodeQualityEvaluator.ParseProxyCheckJson(body, ip);
            return null;
        }

        static ServiceGateResult ProbeCloudflare(WebProxy proxy, bool allowTun, out string ip, out string loc, out string colo)
        {
            ip = ""; loc = ""; colo = "";
            var g = new ServiceGateResult { Name = "Cloudflare", Tested = true };
            int st, ms; string rloc, body, err;
            bool ok = HttpGetWithFallback("https://cloudflare.com/cdn-cgi/trace", proxy, allowTun, 3200, out st, out rloc, out body, out ms, out err);
            g.StatusCode = st; g.LatencyMs = ms;
            if (ok && st == 200)
            {
                NodeQualityEvaluator.ParseCloudflareTrace(body, out ip, out loc, out colo);
                g.Ok = true;
                g.Detail = string.IsNullOrEmpty(colo) ? ("200 OK · " + ms + "ms") : (colo + " · " + ms + "ms");
            }
            else if (st == 403 || st == 429) { g.Blocked = true; g.Detail = "拦截 HTTP " + st; }
            else g.Detail = !string.IsNullOrEmpty(err) ? err : ("HTTP " + st);
            return g;
        }

        static ServiceGateResult ProbeGoogle(WebProxy proxy, bool allowTun)
        {
            int st, ms; string loc, body, err;
            bool ok = HttpGetWithFallback("https://www.google.com/search?q=connectivity&hl=en", proxy, allowTun, 3400, out st, out loc, out body, out ms, out err);
            return NodeQualityEvaluator.EvaluateGoogleRiskResponse(ok, st, loc, body, ms, err);
        }

        static ServiceGateResult ProbeGemini(WebProxy proxy, bool allowTun)
        {
            int st, ms; string loc, body, err;
            bool ok = HttpGetWithFallback("https://generativelanguage.googleapis.com/v1beta/models", proxy, allowTun, 3400, out st, out loc, out body, out ms, out err);
            return NodeQualityEvaluator.EvaluateGeminiRegionResponse(ok, st, body, ms, err);
        }

        static ServiceGateResult ProbeOpenAi(WebProxy proxy, bool allowTun)
        {
            int st, ms; string loc, body, err;
            bool ok = HttpGetWithFallback("https://api.openai.com/v1/models", proxy, allowTun, 3400, out st, out loc, out body, out ms, out err);
            return NodeQualityEvaluator.EvaluateOpenAiResponse(ok, st, body, ms, err);
        }

        static ServiceGateResult ProbeClaude(WebProxy proxy, bool allowTun)
        {
            int st, ms; string loc, body, err;
            bool ok = HttpGetWithFallback("https://api.anthropic.com/v1/models", proxy, allowTun, 3400, out st, out loc, out body, out ms, out err);
            return NodeQualityEvaluator.EvaluateClaudeResponse(ok, st, body, ms, err);
        }

        static string ProbeRawIpv6()
        {
            int st, ms; string loc, body, err;
            if (!HttpGet("https://api6.ipify.org?format=json", null, 2400, out st, out loc, out body, out ms, out err)) return "";
            if (st < 200 || st >= 300 || string.IsNullOrEmpty(body)) return "";
            try
            {
                var obj = JsonCodec.AsObject(JsonCodec.Parse(body));
                string ip = JsonCodec.Str(obj, "ip");
                IPAddress addr;
                if (IPAddress.TryParse(ip, out addr) && addr.AddressFamily == AddressFamily.InterNetworkV6) return ip;
            }
            catch { }
            return "";
        }

        // ---- 轻量级隔离质检侧车会话 ----

        QualitySidecarSession TryStartQualitySidecar(string configPath)
        {
            if (string.IsNullOrEmpty(configPath) || !File.Exists(configPath)) return null;
            string exe = LocateKernelExe();
            if (string.IsNullOrEmpty(exe)) return null;
            var sess = new QualitySidecarSession(_ownDataDir, exe, configPath);
            if (!sess.Start()) { sess.Dispose(); return null; }
            return sess;
        }

        static string LocateKernelExe()
        {
            foreach (var name in new[] { "verge-mihomo.exe", "verge-mihomo-alpha.exe", "mihomo.exe", "mihomo-alpha.exe" })
            {
                try
                {
                    foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(name)))
                    {
                        try
                        {
                            string path = p.MainModule?.FileName;
                            if (!string.IsNullOrEmpty(path) && File.Exists(path)) return path;
                        }
                        catch { }
                        finally { p.Dispose(); }
                    }
                }
                catch { }
            }
            return null;
        }

        sealed class QualitySidecarSession : IDisposable
        {
            readonly string _ownDataDir;
            readonly string _kernelExe;
            readonly string _configPath;
            string _tempDir;
            Process _proc;
            KillOnCloseJob _job;
            int _apiPort;
            string _secret;

            public int ProxyPort { get; private set; }
            public WebProxy OutboundProxy { get; private set; }
            public bool Ready { get; private set; }

            public QualitySidecarSession(string ownDataDir, string kernelExe, string configPath)
            {
                _ownDataDir = ownDataDir;
                _kernelExe = kernelExe;
                _configPath = configPath;
            }

            public bool Start()
            {
                try
                {
                    string root = Path.Combine(_ownDataDir, "probe_tmp");
                    Directory.CreateDirectory(root);
                    _tempDir = Path.Combine(root, "pp_probe_q_" + Guid.NewGuid().ToString("N").Substring(0, 8));
                    Directory.CreateDirectory(_tempDir);

                    _apiPort = AllocPort();
                    ProxyPort = AllocPort();
                    _secret = Guid.NewGuid().ToString("N").Substring(0, 16);

                    string clean = YamlRuleEngine.StripStaticMarkedBlocks(File.ReadAllText(_configPath, Encoding.UTF8));
                    var sb = new StringBuilder();
                    sb.AppendLine("mixed-port: " + ProxyPort);
                    sb.AppendLine("external-controller: 127.0.0.1:" + _apiPort);
                    sb.AppendLine("secret: " + JsonCodec.Quote(_secret));
                    sb.AppendLine("mode: global");
                    sb.AppendLine("tun:\n  enable: false");
                    sb.AppendLine("proxies:");
                    bool inPx = false;
                    foreach (var raw in clean.Replace("\r\n", "\n").Split('\n'))
                    {
                        string t = raw.TrimEnd('\r');
                        if (!inPx) { if (t.TrimStart() == "proxies:") inPx = true; continue; }
                        if (t.Length > 0 && !char.IsWhiteSpace(t[0]) && !t.TrimStart().StartsWith("#", StringComparison.Ordinal)) break;
                        sb.AppendLine(t);
                    }
                    string yamlPath = Path.Combine(_tempDir, "probe.yaml");
                    File.WriteAllText(yamlPath, sb.ToString(), new UTF8Encoding(false));

                    _proc = Process.Start(new ProcessStartInfo
                    {
                        FileName = _kernelExe,
                        Arguments = "-d \"" + _tempDir + "\" -f \"" + yamlPath + "\"",
                        WorkingDirectory = Path.GetDirectoryName(_kernelExe) ?? _tempDir,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WindowStyle = ProcessWindowStyle.Hidden
                    });
                    if (_proc == null) return false;
                    _job = new KillOnCloseJob();
                    if (_job.IsValid) _job.Assign(_proc.Handle);

                    var sw = Stopwatch.StartNew();
                    while (sw.ElapsedMilliseconds < 4000)
                    {
                        if (_proc.HasExited) return false;
                        if (Ping())
                        {
                            OutboundProxy = new WebProxy("127.0.0.1", ProxyPort);
                            Ready = true;
                            return true;
                        }
                        Thread.Sleep(80);
                    }
                }
                catch { }
                return false;
            }

            public void SelectGlobalNode(string nodeName)
            {
                try
                {
                    var req = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + _apiPort + "/proxies/GLOBAL");
                    req.Method = "PUT";
                    req.Proxy = null;
                    req.Timeout = 2000;
                    req.Headers["Authorization"] = "Bearer " + _secret;
                    byte[] payload = Encoding.UTF8.GetBytes("{\"name\":" + JsonCodec.Quote(nodeName) + "}");
                    req.ContentType = "application/json";
                    req.ContentLength = payload.Length;
                    using (var s = req.GetRequestStream()) s.Write(payload, 0, payload.Length);
                    using (var resp = (HttpWebResponse)req.GetResponse()) { }
                }
                catch { }
            }

            public int MeasureDelay(string nodeName, int timeoutMs)
            {
                try
                {
                    string url = "http://127.0.0.1:" + _apiPort + "/proxies/" + Uri.EscapeDataString(nodeName) +
                                 "/delay?url=" + Uri.EscapeDataString("http://www.gstatic.com/generate_204") + "&timeout=" + timeoutMs;
                    var req = (HttpWebRequest)WebRequest.Create(url);
                    req.Method = "GET";
                    req.Proxy = null;
                    req.Timeout = timeoutMs + 1000;
                    req.Headers["Authorization"] = "Bearer " + _secret;
                    using (var resp = (HttpWebResponse)req.GetResponse())
                    using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    {
                        var obj = JsonCodec.AsObject(JsonCodec.Parse(sr.ReadToEnd()));
                        return JsonCodec.Int(obj, "delay", -1);
                    }
                }
                catch { return -1; }
            }

            bool Ping()
            {
                try
                {
                    var req = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + _apiPort + "/version");
                    req.Proxy = null; req.Timeout = 400;
                    req.Headers["Authorization"] = "Bearer " + _secret;
                    using (var resp = (HttpWebResponse)req.GetResponse())
                        return (int)resp.StatusCode == 200;
                }
                catch { return false; }
            }

            static int AllocPort()
            {
                var l = new TcpListener(IPAddress.Loopback, 0);
                l.Start();
                int p = ((IPEndPoint)l.LocalEndpoint).Port;
                l.Stop();
                return p;
            }

            public void Dispose()
            {
                try { if (_proc != null && !_proc.HasExited) { _proc.Kill(); _proc.WaitForExit(2000); } } catch { }
                try { _job?.Dispose(); } catch { }
                try { _proc?.Dispose(); } catch { }
                if (!string.IsNullOrEmpty(_tempDir) && Directory.Exists(_tempDir))
                {
                    for (int i = 0; i < 6; i++)
                    {
                        try
                        {
                            string yf = Path.Combine(_tempDir, "probe.yaml");
                            if (File.Exists(yf)) File.WriteAllBytes(yf, new byte[0]);
                            Directory.Delete(_tempDir, true);
                            break;
                        }
                        catch { Thread.Sleep(100); }
                    }
                }
            }
        }
    }
}
