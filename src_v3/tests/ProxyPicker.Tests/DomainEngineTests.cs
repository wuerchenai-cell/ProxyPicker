using System;
using System.Collections.Generic;
using ProxyPicker.Domain.Json;
using ProxyPicker.Domain.Models;
using ProxyPicker.Domain.Services;

namespace ProxyPicker.Tests
{
    public sealed class DomainEngineTests
    {
        const string SampleYaml = @"mode: rule
mixed-port: 7897
find-process-mode: strict
proxies:
  - name: Node-JP-01
    type: ss
    server: 198.51.100.1
    port: 443
  - name: Node-US-02
    type: vmess
    server: 198.51.100.2
    port: 443
  - name: 剩余流量：99 GB
    type: ss
    server: 198.51.100.9
    port: 443
proxy-groups:
  - name: ProxyGroup
    type: select
    proxies:
      - Node-JP-01
      - Node-US-02
rules:
  - DOMAIN-SUFFIX,google.com,ProxyGroup
  - MATCH,ProxyGroup
";

        [TestFact]
        public void YamlRuleEngine_MultiStaticChannels_InjectAndUdpGuardAndCleanRollback()
        {
            var vault = new StaticChannelVault
            {
                ActiveDefaultChannelId = "default",
                Channels = new List<StaticChannelProfile>
                {
                    new StaticChannelProfile
                    {
                        Id = "default", Name = "美国固定IP", Enabled = true, Protocol = "socks5",
                        Host = "198.51.100.50", Port = 1080, Username = "u1", Password = "p:1#secret",
                        AllowUdp = false, HopMode = StaticHopMode.Auto, AutoBestHopNode = "Node-US-02"
                    },
                    new StaticChannelProfile
                    {
                        Id = "jp2", Name = "日本固定IP", Enabled = true, Protocol = "socks5",
                        Host = "198.51.100.51", Port = 1081, Username = "u2", Password = "pwd2",
                        AllowUdp = true, HopMode = StaticHopMode.Pin, PinnedHopNode = "Node-JP-01"
                    }
                }
            };
            var rules = new List<AppRouteRule>
            {
                new AppRouteRule { Exe = "chrome.exe", Channel = RouteChannel.StaticChannel, StaticChannelId = "default" },
                new AppRouteRule { Exe = "cursor.exe", Channel = RouteChannel.StaticChannel, StaticChannelId = "jp2" },
                new AppRouteRule { Exe = "antigravity.exe", Channel = RouteChannel.PrimaryProxy }
            };

            string compiled = YamlRuleEngine.CompilePayload(SampleYaml, rules, SplitMode.Whitelist, "ProxyGroup", vault, "always", true);

            AssertEx.Contains("PP-STATIC-OUT", compiled);
            AssertEx.Contains("PP-STATIC-OUT-JP2", compiled);
            AssertEx.Contains("PP-STATIC-HOP", compiled);
            AssertEx.Contains("PROCESS-NAME,chrome.exe,PP-STATIC-OUT", compiled);
            AssertEx.Contains("PROCESS-NAME,cursor.exe,PP-STATIC-OUT-JP2", compiled);
            AssertEx.Contains("AND,((NETWORK,UDP),(PROCESS-NAME,chrome.exe)),REJECT", compiled);
            // jp2 允许 UDP，因此不应生成 cursor.exe 的 UDP REJECT
            AssertEx.DoesNotContain("AND,((NETWORK,UDP),(PROCESS-NAME,cursor.exe)),REJECT", compiled);
            // 防穿透规则必须排在 MATCH,DIRECT 之前
            int udpPos = compiled.IndexOf("AND,((NETWORK,UDP),(PROCESS-NAME,chrome.exe)),REJECT", StringComparison.Ordinal);
            int matchPos = compiled.IndexOf("- MATCH,DIRECT", StringComparison.Ordinal);
            AssertEx.True(udpPos > 0 && matchPos > udpPos, "UDP 防穿透规则必须排在兜底 MATCH,DIRECT 之前");

            // P2-04 回归：将已经注入的 dirty YAML 当作还原源输入，干净还原后必须零残留！
            string rolled = YamlRuleEngine.BuildCleanRollback(compiled, "strict");
            AssertEx.DoesNotContain("PP-STATIC-OUT", rolled);
            AssertEx.DoesNotContain("PP-STATIC-HOP", rolled);
            AssertEx.DoesNotContain("# PP-STATIC-BEGIN", rolled);
            AssertEx.DoesNotContain("# PP-BEGIN", rolled);
            AssertEx.DoesNotContain("p:1#secret", rolled);
            AssertEx.Contains("find-process-mode: strict", rolled);
            AssertEx.Contains("Node-JP-01", rolled);
        }

        [TestFact]
        public void YamlRuleEngine_UnclosedFlowRules_MustFailClosed()
        {
            string bad = "proxies:\n  - name: A\nrules: [MATCH,DIRECT\n";
            AssertEx.Throws<FormatException>(() =>
                YamlRuleEngine.CompilePayload(bad, new List<AppRouteRule>(), SplitMode.Whitelist, "ProxyGroup", null));
        }

        [TestFact]
        public void StaticChannelAlgorithms_CasMerge_MustDiscardWhenUserMutatedDuringBenchmark()
        {
            var start = new StaticChannelProfile
            {
                Id = "default", Enabled = true, Host = "198.51.100.8", Port = 1080,
                Username = "u1", Password = "old-password", HopMode = StaticHopMode.Auto, AllowUdp = false
            };
            var disabled = start.Clone(); disabled.Enabled = false;
            var changedPwd = start.Clone(); changedPwd.Password = "new-password";
            var changedMode = start.Clone(); changedMode.HopMode = StaticHopMode.Pin; changedMode.PinnedHopNode = "Node-JP-01";
            var renamedOnly = start.Clone(); renamedOnly.Name = "公司专线";

            StaticChannelProfile merged; string why;
            AssertEx.False(StaticChannelAlgorithms.TryMergeAutoHopResult(start, disabled, false, true, "Node-US-02", 180, "", 180, new[] { "Node-US-02" }, 100, out merged, out why));
            AssertEx.False(StaticChannelAlgorithms.TryMergeAutoHopResult(start, changedPwd, true, true, "Node-US-02", 180, "", 180, new[] { "Node-US-02" }, 100, out merged, out why));
            AssertEx.False(StaticChannelAlgorithms.TryMergeAutoHopResult(start, changedMode, false, true, "Node-US-02", 180, "", 180, new[] { "Node-US-02" }, 100, out merged, out why));
            AssertEx.True(StaticChannelAlgorithms.TryMergeAutoHopResult(start, renamedOnly, false, true, "Node-US-02", 180, "", 180, new[] { "Node-US-02" }, 100, out merged, out why));
            AssertEx.Equal("公司专线", merged.Name);
            AssertEx.Equal("Node-US-02", merged.AutoBestHopNode);
        }

        [TestFact]
        public void StaticChannelAlgorithms_StabilityRankingAndDebounceAndQuickParse()
        {
            var list = new List<HopCandidateMetric>
            {
                new HopCandidateMetric { NodeName = "FlakyFast", DelayMs = 130, JitterMs = 0, SuccessSamples = 1, StabilityScore = StaticChannelAlgorithms.ComputeStabilityScore(130, 0) },
                new HopCandidateMetric { NodeName = "Jittery", DelayMs = 180, JitterMs = 140, SuccessSamples = 3, StabilityScore = StaticChannelAlgorithms.ComputeStabilityScore(180, 140) },
                new HopCandidateMetric { NodeName = "Steady", DelayMs = 195, JitterMs = 12, SuccessSamples = 3, StabilityScore = StaticChannelAlgorithms.ComputeStabilityScore(195, 12) }
            };
            StaticChannelAlgorithms.RankVerifiedCandidates(list);
            AssertEx.Equal("Steady", list[0].NodeName);
            AssertEx.Equal("Jittery", list[1].NodeName);
            AssertEx.Equal("FlakyFast", list[2].NodeName);

            AssertEx.False(StaticChannelAlgorithms.ShouldSwitchAutoHop("A", 300, "B", 260, false, false));
            AssertEx.True(StaticChannelAlgorithms.ShouldSwitchAutoHop("A", 400, "B", 310, false, false));

            string proto, host, user, pass; int port;
            AssertEx.True(StaticChannelAlgorithms.TryParseQuickLine("socks5://alice:s3cr3t@198.51.100.7:10808", out proto, out host, out port, out user, out pass));
            AssertEx.Equal("socks5", proto);
            AssertEx.Equal("198.51.100.7", host);
            AssertEx.Equal(10808, port);
            AssertEx.Equal("alice", user);
            AssertEx.Equal("s3cr3t", pass);
        }

        [TestFact]
        public void RedactorService_ShouldMaskShortPasswordsAndPreserveVersionAndProse()
        {
            var r = new RedactorService();
            r.RegisterCredentials(new[] { "198.51.100.9", "ab", "x7", "supersecret99" }, new[] { "x7", "supersecret99" });
            string outTxt = r.Apply("ProxyPicker 3.0.0.0 连至 socks5://ab:x7@198.51.100.9:1080 password=x7 密码：x7 正文x7y保留 supersecret99");
            AssertEx.Contains("ProxyPicker 3.0.0.0", outTxt);
            AssertEx.DoesNotContain(":x7@", outTxt);
            AssertEx.DoesNotContain("password=x7", outTxt);
            AssertEx.DoesNotContain("密码：x7", outTxt);
            AssertEx.DoesNotContain("supersecret99", outTxt);
            AssertEx.Contains("正文x7y保留", outTxt);
        }

        [TestFact]
        public void TrafficAggregator_ShouldSplitThreeChannelsAndIgnoreExpectedUdpReject()
        {
            var agg = new TrafficAggregator();
            var vault = new StaticChannelVault
            {
                Channels = new List<StaticChannelProfile>
                {
                    new StaticChannelProfile { Id = "default", Enabled = true, Host = "198.51.100.1", Port = 1080, AllowUdp = false }
                }
            };
            var rules = new List<AppRouteRule>
            {
                new AppRouteRule { Exe = "chrome.exe", Channel = RouteChannel.StaticChannel, StaticChannelId = "default" },
                new AppRouteRule { Exe = "cursor.exe", Channel = RouteChannel.PrimaryProxy }
            };
            string json1 = "{\"connections\":[" +
                "{\"id\":\"c1\",\"upload\":100,\"download\":1000,\"chains\":[\"PP-STATIC-OUT\",\"Node-US\"],\"rule\":\"PROCESS-NAME\",\"rulePayload\":\"chrome.exe\",\"start\":\"2026-11-26T12:00:05Z\",\"metadata\":{\"process\":\"chrome.exe\",\"network\":\"tcp\",\"host\":\"example.com\",\"destinationPort\":\"443\"}}," +
                "{\"id\":\"c2\",\"upload\":50,\"download\":500,\"chains\":[\"Node-JP\",\"ProxyGroup\"],\"rule\":\"PROCESS-NAME\",\"rulePayload\":\"cursor.exe\",\"start\":\"2026-11-26T12:00:05Z\",\"metadata\":{\"process\":\"cursor.exe\",\"network\":\"tcp\",\"host\":\"api.cursor.sh\",\"destinationPort\":\"443\"}}," +
                "{\"id\":\"c3\",\"upload\":0,\"download\":0,\"chains\":[\"REJECT\"],\"rule\":\"AND\",\"rulePayload\":\"udp\",\"start\":\"2026-11-26T12:00:05Z\",\"metadata\":{\"process\":\"chrome.exe\",\"network\":\"udp\",\"host\":\"quic.example.com\",\"destinationPort\":\"443\"}}," +
                "{\"id\":\"c4\",\"upload\":10,\"download\":200,\"chains\":[\"DIRECT\"],\"rule\":\"MATCH\",\"rulePayload\":\"\",\"start\":\"2026-11-26T12:00:05Z\",\"metadata\":{\"process\":\"wechat.exe\",\"network\":\"tcp\",\"host\":\"wx.qq.com\",\"destinationPort\":\"443\"}}" +
                "]}";
            string json2 = json1.Replace("\"download\":1000", "\"download\":5000").Replace("\"download\":500", "\"download\":2500").Replace("\"download\":200", "\"download\":400");

            var t0 = new DateTime(2026, 11, 26, 12, 0, 10, DateTimeKind.Utc);
            var applyTime = new DateTime(2026, 11, 26, 12, 0, 0, DateTimeKind.Utc);
            agg.Aggregate(JsonCodec.ParseObject(json1), t0, applyTime, SplitMode.Whitelist, rules, vault);
            var s2 = agg.Aggregate(JsonCodec.ParseObject(json2), t0.AddSeconds(1), applyTime, SplitMode.Whitelist, rules, vault);

            AssertEx.Equal(1, s2.StaticConnCount);
            AssertEx.Equal(1, s2.ProxyConnCount);
            AssertEx.Equal(1, s2.DirectConnCount);
            AssertEx.Equal(1, s2.RejectedConnCount);
            AssertEx.Equal(0, s2.MismatchCount, "固定 IP 的预期 UDP REJECT 绝不可误报为错路");
            AssertEx.True(s2.StaticDownBps > 3500 && s2.ProxyDownBps > 1500 && s2.DirectDownBps > 100);
        }

        [TestFact]
        public static void GuardEvaluator_MatchesLiveMihomoProcessNameAndAndUdpGuardSyntax()
        {
            var vault = new StaticChannelVault
            {
                Channels = new List<StaticChannelProfile>
                {
                    new StaticChannelProfile { Id = "default", Enabled = true, Host = "198.51.100.1", Port = 1080, AllowUdp = false }
                }
            };
            var wanted = new List<AppRouteRule>
            {
                new AppRouteRule { Exe = "chrome.exe", Channel = RouteChannel.StaticChannel, StaticChannelId = "default" },
                new AppRouteRule { Exe = "msedge.exe", Channel = RouteChannel.PrimaryProxy }
            };
            var liveKernelRules = new List<KernelRuleInfo>
            {
                new KernelRuleInfo { Type = "ProcessName", Payload = "chrome.exe", Proxy = "PP-STATIC-OUT" },
                new KernelRuleInfo { Type = "ProcessName", Payload = "msedge.exe", Proxy = "PP-FIXED" },
                new KernelRuleInfo { Type = "AND", Payload = "((Network,udp) && (ProcessName,chrome.exe))", Proxy = "REJECT" },
                new KernelRuleInfo { Type = "Match", Payload = "", Proxy = "DIRECT" }
            };
            var audit = GuardEvaluator.EvaluateRules(liveKernelRules, wanted, vault, true, new GuardAuditResult { Mode = "rule", FindProcessMode = "always" });
            AssertEx.True(audit.Ok);
            AssertEx.Equal(0, audit.MissingExes.Count);
            AssertEx.Equal(0, audit.WrongPolicyNames.Count);
            AssertEx.Equal(0, audit.MissingStaticUdpGuards);
            AssertEx.Equal<string>(null, GuardEvaluator.Judge(audit, 2, SplitMode.Whitelist));
        }
    }
}
