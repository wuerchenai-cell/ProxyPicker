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

namespace ProxyPicker.Infrastructure.Sidecar
{
    /// <summary>
    /// 零干扰旁路探测侧车服务：
    /// 1) 临时目录收口于当前用户私有目录 LocalAppData\ProxyPicker\probe_tmp\；
    /// 2) 绑定 Win32 KillOnCloseJob，退出 6 轮零字节覆写删除 probe.yaml，启动自动清扫过期残留；
    /// 3) 16 路并发端到端双跳初筛 + Top 5 连续 3 次稳态复核，不切宿主主节点、不断正常连接。
    /// </summary>
    public sealed class ProbeSidecarService : IProbeSidecar
    {
        readonly string _ownDataDir;
        readonly IAuditSink _audit;
        readonly object _activeGate = new object();
        readonly HashSet<string> _activeDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public ProbeSidecarService(string ownDataDir, IAuditSink audit)
        {
            _ownDataDir = ownDataDir;
            _audit = audit;
        }

        string PrivateProbeRoot
        {
            get
            {
                string dir = Path.Combine(_ownDataDir, "probe_tmp");
                try { Directory.CreateDirectory(dir); } catch { }
                return dir;
            }
        }

        public int SweepStaleTempDirs(TimeSpan? maxAge = null)
        {
            var age = maxAge ?? TimeSpan.FromMinutes(10);
            DateTime cutoff = DateTime.UtcNow - age;
            int cleaned = 0;
            var roots = new[] { PrivateProbeRoot, Path.GetTempPath() };
            foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;
                string[] dirs;
                try { dirs = Directory.GetDirectories(root, "pp_probe_*"); } catch { continue; }
                foreach (var d in dirs)
                {
                    bool active;
                    lock (_activeGate) active = _activeDirs.Contains(d);
                    if (active) continue;
                    try
                    {
                        DateTime lw = Directory.GetLastWriteTimeUtc(d);
                        string yf = Path.Combine(d, "probe.yaml");
                        if (File.Exists(yf))
                        {
                            DateTime yw = File.GetLastWriteTimeUtc(yf);
                            if (yw > lw) lw = yw;
                        }
                        if (lw > cutoff) continue;
                    }
                    catch { }
                    if (WipeAndDeleteDir(d)) cleaned++;
                }
            }
            return cleaned;
        }

        public HopBenchmarkOutcome BenchmarkStaticHops(
            StaticChannelProfile channel,
            string configPath,
            Action<string> progress,
            CancellationToken ct,
            string currentLiveHop = null)
        {
            var outcome = new HopBenchmarkOutcome();
            if (channel == null || !channel.IsActive())
            {
                outcome.Error = "固定 IP 通道未启用或参数无效";
                return outcome;
            }
            if (string.IsNullOrEmpty(configPath) || !File.Exists(configPath))
            {
                outcome.Error = "找不到客户端订阅配置文件";
                return outcome;
            }
            string kernelExe = LocateKernelExecutable();
            if (string.IsNullOrEmpty(kernelExe))
            {
                outcome.Error = "未找到可用的 mihomo 内核可执行文件";
                return outcome;
            }

            try { SweepStaleTempDirs(); } catch { }

            string rawYaml = File.ReadAllText(configPath, Encoding.UTF8);
            var nodes = YamlRuleEngine.ExtractConcreteNodeNames(rawYaml, 120);
            if (nodes.Count == 0) { outcome.Error = "订阅中未解析到可用候选节点"; return outcome; }

            outcome.TestedCount = nodes.Count;
            string tempDir = Path.Combine(PrivateProbeRoot, "pp_probe_" + Guid.NewGuid().ToString("N").Substring(0, 10));
            lock (_activeGate) _activeDirs.Add(tempDir);

            Process proc = null;
            KillOnCloseJob job = null;
            try
            {
                Directory.CreateDirectory(tempDir);
                int apiPort = AllocateFreePort();
                int proxyPort = AllocateFreePort();
                string secret = Guid.NewGuid().ToString("N").Substring(0, 16);
                string yamlPath = Path.Combine(tempDir, "probe.yaml");
                string benchYaml = BuildBenchmarkYaml(rawYaml, Path.GetDirectoryName(configPath) ?? "", proxyPort, apiPort, secret, channel, nodes);
                File.WriteAllText(yamlPath, benchYaml, new UTF8Encoding(false));

                var psi = new ProcessStartInfo
                {
                    FileName = kernelExe,
                    Arguments = "-d \"" + tempDir + "\" -f \"" + yamlPath + "\"",
                    WorkingDirectory = Path.GetDirectoryName(kernelExe) ?? tempDir,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                proc = Process.Start(psi);
                if (proc == null) { outcome.Error = "无法启动旁路探测内核"; return outcome; }

                job = new KillOnCloseJob();
                if (job.IsValid) job.Assign(proc.Handle);

                // 等待旁路内核 API 就绪
                var sw = Stopwatch.StartNew();
                bool ready = false;
                while (sw.ElapsedMilliseconds < 4500 && !ct.IsCancellationRequested)
                {
                    if (proc.HasExited) { outcome.Error = "旁路探测内核提前退出 (code=" + proc.ExitCode + ")"; return outcome; }
                    if (PingApi(apiPort, secret)) { ready = true; break; }
                    Thread.Sleep(80);
                }
                if (!ready) { outcome.Error = "旁路探测内核启动超时"; return outcome; }

                // 第 1 阶段：16 路并发端到端真实双跳初筛
                progress?.Invoke("正在零干扰并发初筛 " + nodes.Count + " 个前置跳板…");
                var initialResults = new List<HopCandidateMetric>();
                var lockObj = new object();
                using (var sem = new SemaphoreSlim(16, 16))
                {
                    var tasks = new List<Task>();
                    for (int idx = 0; idx < nodes.Count; idx++)
                    {
                        int i = idx;
                        string nodeName = nodes[i];
                        tasks.Add(Task.Run(() =>
                        {
                            sem.Wait(ct);
                            try
                            {
                                int d = QueryBenchDelay(apiPort, secret, "PP-BENCH-" + i, 4000);
                                if (d > 0)
                                {
                                    lock (lockObj)
                                        initialResults.Add(new HopCandidateMetric { NodeName = nodeName, DelayMs = d, SuccessSamples = 1, StabilityScore = d });
                                }
                            }
                            finally { sem.Release(); }
                        }, ct));
                    }
                    try { Task.WaitAll(tasks.ToArray(), ct); } catch { }
                }

                outcome.ReachableCount = initialResults.Count;
                if (initialResults.Count == 0) { outcome.Error = "全部 " + nodes.Count + " 个跳板均无法连通固定 IP"; return outcome; }

                initialResults.Sort((a, b) => a.DelayMs.CompareTo(b.DelayMs));
                var finalists = initialResults.Take(5).ToList();
                if (!string.IsNullOrWhiteSpace(currentLiveHop) && !finalists.Any(x => x.NodeName.Equals(currentLiveHop, StringComparison.OrdinalIgnoreCase)))
                {
                    var curMatch = initialResults.FirstOrDefault(x => x.NodeName.Equals(currentLiveHop, StringComparison.OrdinalIgnoreCase));
                    if (curMatch != null) finalists.Add(curMatch);
                }

                // 第 2 阶段：对 Top 5 + 当前跳板追加 2 轮连续复核（共 3 次采样），计算平均延迟与抖动稳态分
                progress?.Invoke("正在对前 " + finalists.Count + " 名候选跳板执行 3 次稳态复核…");
                var nodeIndexMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < nodes.Count; i++) nodeIndexMap[nodes[i]] = i;

                var verified = new List<HopCandidateMetric>();
                foreach (var f in finalists)
                {
                    if (ct.IsCancellationRequested) break;
                    var samples = new List<int> { f.DelayMs };
                    int bIdx = nodeIndexMap[f.NodeName];
                    for (int round = 0; round < 2; round++)
                    {
                        int d = QueryBenchDelay(apiPort, secret, "PP-BENCH-" + bIdx, 4000);
                        if (d > 0) samples.Add(d);
                    }
                    int avg = (int)Math.Round(samples.Average());
                    int jitter = samples.Count > 1 ? (samples.Max() - samples.Min()) : 0;
                    verified.Add(new HopCandidateMetric
                    {
                        NodeName = f.NodeName,
                        DelayMs = avg,
                        JitterMs = jitter,
                        SuccessSamples = samples.Count,
                        StabilityScore = StaticChannelAlgorithms.ComputeStabilityScore(avg, jitter)
                    });
                }

                StaticChannelAlgorithms.RankVerifiedCandidates(verified);
                var best = verified[0];
                outcome.Ok = true;
                outcome.BestNode = best.NodeName;
                outcome.BestDelayMs = best.DelayMs;
                outcome.BestJitterMs = best.JitterMs;
                outcome.TopCandidates = verified.Concat(initialResults.Where(x => !verified.Any(v => v.NodeName.Equals(x.NodeName, StringComparison.OrdinalIgnoreCase)))).Take(12).ToList();
                return outcome;
            }
            catch (Exception ex)
            {
                outcome.Error = ex.Message;
                return outcome;
            }
            finally
            {
                try { if (proc != null && !proc.HasExited) { proc.Kill(); proc.WaitForExit(3000); } } catch { }
                try { job?.Dispose(); } catch { }
                try { proc?.Dispose(); } catch { }
                WipeAndDeleteDir(tempDir);
                lock (_activeGate) _activeDirs.Remove(tempDir);
            }
        }

        /// <summary>分析当前本机内核 DNS/TUN 与固定 IP UDP 防护配置，输出只读 DNS 防泄漏体检建议。</summary>
        public static DnsDiagnosticAdvice AnalyzeDnsLeakRisk(KernelSnapshot kernel, StaticChannelVault vault)
        {
            var adv = new DnsDiagnosticAdvice
            {
                KernelDnsEnabled = kernel != null && kernel.DnsEnabled,
                TunEnabled = kernel != null && kernel.TunEnabled,
                StaticUdpGuardActive = vault != null && vault.GetActiveChannels().Any(c => !c.AllowUdp)
            };
            if (kernel == null || !kernel.Connected)
            {
                adv.Severity = "warn";
                adv.Summary = "未连接到代理内核，无法检查 DNS 配置";
                return adv;
            }
            if (!adv.TunEnabled)
            {
                adv.Severity = "warn";
                adv.Summary = "当前未开启 TUN 模式，非系统代理软件可能直连本地 DNS";
                adv.Recommendations.Add("建议在代理客户端开启 TUN 虚拟网卡模式，使按进程分流覆盖所有客户端与 CLI 工具。");
            }
            if (!adv.KernelDnsEnabled)
            {
                adv.Severity = "warn";
                adv.Summary = "代理客户端内置 DNS 未启用，浏览器的系统 DNS 查询可能走国内本地 DNS";
                adv.Recommendations.Add("在 Chrome / Edge 的「隐私和安全 → 安全 → 使用安全 DNS」中开启 Cloudflare (1.1.1.1) DoH，即可消除检测页 DNS 泄露告警。");
                adv.Recommendations.Add("或在代理客户端中按需启用内置 DNS（Fake-IP / Redir-Host），ProxyPicker 遵循最小干预原则不会自动修改客户端 DNS。");
            }
            else if (adv.TunEnabled)
            {
                adv.Severity = "ok";
                adv.Summary = "TUN 模式与内置 DNS 均已开启，配合固定 IP UDP/QUIC 防穿透规则可有效防泄露";
            }
            return adv;
        }

        static string BuildBenchmarkYaml(string rawYaml, string origDataDir, int proxyPort, int apiPort, string secret, StaticChannelProfile ch, List<string> nodes)
        {
            var sb = new StringBuilder();
            sb.AppendLine("mixed-port: " + proxyPort);
            sb.AppendLine("external-controller: 127.0.0.1:" + apiPort);
            sb.AppendLine("secret: " + JsonCodec.Quote(secret));
            sb.AppendLine("mode: global");
            sb.AppendLine("unified-delay: true");
            sb.AppendLine("tun:");
            sb.AppendLine("  enable: false");
            sb.AppendLine("proxies:");

            // 拷贝源配置中的物理节点块（剥离已有 PP-STATIC 标记块）
            string clean = YamlRuleEngine.StripStaticMarkedBlocks(rawYaml);
            string[] lines = clean.Replace("\r\n", "\n").Split('\n');
            bool inProxies = false;
            foreach (var raw in lines)
            {
                string t = raw.Trim();
                if (!inProxies)
                {
                    if (t == "proxies:" || t.StartsWith("proxies:", StringComparison.Ordinal)) inProxies = true;
                    continue;
                }
                if (raw.Length > 0 && raw[0] != ' ' && raw[0] != '\t' && raw[0] != '-' && !t.StartsWith("#", StringComparison.Ordinal))
                    break;
                if (t.Length > 0 && !t.StartsWith("#", StringComparison.Ordinal))
                    sb.AppendLine(raw.StartsWith("  ", StringComparison.Ordinal) ? raw : ("  " + raw));
            }

            // 为每个候选跳板叠加一个 PP-BENCH-i 链式代理指向目标固定 IP
            for (int i = 0; i < nodes.Count; i++)
            {
                sb.AppendLine("  - name: " + JsonCodec.Quote("PP-BENCH-" + i));
                sb.AppendLine("    type: " + ch.Protocol);
                sb.AppendLine("    server: " + JsonCodec.Quote(ch.Host.Trim()));
                sb.AppendLine("    port: " + ch.Port);
                if (!string.IsNullOrEmpty(ch.Username)) sb.AppendLine("    username: " + JsonCodec.Quote(ch.Username));
                if (!string.IsNullOrEmpty(ch.Password)) sb.AppendLine("    password: " + JsonCodec.Quote(ch.Password));
                if (ch.Protocol == "socks5") sb.AppendLine("    udp: " + (ch.AllowUdp ? "true" : "false"));
                sb.AppendLine("    dialer-proxy: " + JsonCodec.Quote(nodes[i]));
            }
            sb.AppendLine("rules:");
            sb.AppendLine("  - MATCH,DIRECT");
            return sb.ToString();
        }

        static string LocateKernelExecutable()
        {
            // 1. 优先从正在运行的内核进程取同源 exe 路径
            foreach (var name in new[] { "verge-mihomo", "mihomo", "FlClashCore", "nyanpasu-mihomo", "sparkle-mihomo" })
            {
                try
                {
                    foreach (var p in Process.GetProcessesByName(name))
                    {
                        try
                        {
                            string img;
                            if (NativeProcessProbe.TryQueryImagePathByPid(p.Id, out img) && File.Exists(img))
                                return img;
                        }
                        finally { try { p.Dispose(); } catch { } }
                    }
                }
                catch { }
            }
            // 2. 常见安装目录兜底
            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string[] paths =
            {
                Path.Combine(pf, "Clash Verge", "verge-mihomo.exe"),
                @"D:\Clash Verge\verge-mihomo.exe",
                Path.Combine(pf, "FlClash", "FlClashCore.exe")
            };
            foreach (var p in paths) if (File.Exists(p)) return p;
            return "";
        }

        static int AllocateFreePort()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }

        static bool PingApi(int port, string secret)
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + port + "/version");
                req.Timeout = 800;
                req.Proxy = null;
                req.Headers["Authorization"] = "Bearer " + secret;
                using (var resp = (HttpWebResponse)req.GetResponse()) return resp.StatusCode == HttpStatusCode.OK;
            }
            catch { return false; }
        }

        static int QueryBenchDelay(int port, string secret, string benchName, int timeoutMs)
        {
            try
            {
                string url = "http://127.0.0.1:" + port + "/proxies/" + Uri.EscapeDataString(benchName) +
                             "/delay?timeout=" + timeoutMs + "&url=" + Uri.EscapeDataString("http://www.gstatic.com/generate_204");
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Timeout = timeoutMs + 1000;
                req.Proxy = null;
                req.Headers["Authorization"] = "Bearer " + secret;
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    var obj = JsonCodec.ParseObject(sr.ReadToEnd());
                    return JsonCodec.Int(obj, "delay", -1);
                }
            }
            catch { return -1; }
        }

        static bool WipeAndDeleteDir(string dir)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return true;
            string yaml = Path.Combine(dir, "probe.yaml");
            for (int i = 0; i < 6; i++)
            {
                try
                {
                    if (File.Exists(yaml))
                    {
                        long len = new FileInfo(yaml).Length;
                        if (len > 0 && len < 4 * 1024 * 1024) File.WriteAllBytes(yaml, new byte[len]);
                        File.Delete(yaml);
                    }
                    break;
                }
                catch { Thread.Sleep(100); }
            }
            for (int i = 0; i < 6; i++)
            {
                try { if (!Directory.Exists(dir)) return true; Directory.Delete(dir, true); return true; }
                catch { Thread.Sleep(100); }
            }
            return !Directory.Exists(dir);
        }
    }
}
