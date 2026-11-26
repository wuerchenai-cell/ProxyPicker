using System.Collections.Generic;
using ProxyPicker.Domain.Models;
using ProxyPicker.Domain.Services;
using ProxyPicker.Infrastructure.Update;

namespace ProxyPicker.Tests
{
    public static class NodeQualityAndUpdaterTests
    {
        [TestFact]
        public static void NodeQualityEvaluator_LatencyStatsAndDualSourceScoring()
        {
            var st = NodeQualityEvaluator.ComputeLatencyStats(new[] { 80, 120, -1 });
            AssertEx.Equal(3, st.Sent);
            AssertEx.Equal(2, st.Received);
            AssertEx.Equal(100, st.AvgMs);
            AssertEx.Equal(40, st.JitterMs);
            AssertEx.Equal(33, st.LossPercent);

            string iqJson = @"{""ip"":""203.0.113.5"",""isp"":{""asn"":""AS2516"",""org"":""KDDI"",""isp"":""KDDI""},""location"":{""country"":""Japan"",""country_code"":""JP"",""city"":""Tokyo""},""risk"":{""is_mobile"":false,""is_vpn"":false,""is_tor"":false,""is_proxy"":false,""is_datacenter"":false,""risk_score"":4}}";
            string pcJson = @"{""status"":""ok"",""203.0.113.5"":{""country"":""Japan"",""isocode"":""JP"",""type"":""Residential"",""proxy"":""no"",""risk"":5}}";
            var iq = NodeQualityEvaluator.ParseIpQueryJson(iqJson);
            var pc = NodeQualityEvaluator.ParseProxyCheckJson(pcJson, "203.0.113.5");
            AssertEx.True(iq.Ok && iq.CountryCode == "JP" && iq.RawRiskScore == 4);
            AssertEx.True(pc.Ok && pc.IpType == "Residential" && pc.RawRiskScore == 5);

            var rep = new NodeQualityReport
            {
                TargetNode = "日本-家宽-01",
                DesiredRegion = "auto",
                GoogleSearch = NodeQualityEvaluator.EvaluateGoogleRiskResponse(true, 200, "", "<html>ok</html>", 90, ""),
                GeminiApi = NodeQualityEvaluator.EvaluateGeminiRegionResponse(true, 403, @"{""error"":{""message"":""Pass an API key.""}}", 100, ""),
                OpenAiApi = NodeQualityEvaluator.EvaluateOpenAiResponse(true, 401, @"{""error"":{""message"":""missing""}}", 110, ""),
                ClaudeApi = NodeQualityEvaluator.EvaluateClaudeResponse(true, 401, @"{""type"":""error""}", 105, "")
            };
            NodeQualityEvaluator.EvaluateReport(rep, iq, pc);
            AssertEx.Equal(NodeRiskLevel.Low, rep.Level);
            AssertEx.Equal(5, rep.RiskScore);
            AssertEx.True(rep.GoogleAiUsable);
            AssertEx.True(rep.RegionMatched);
            AssertEx.Equal(4, rep.MainstreamReadyCount);
        }

        [TestFact]
        public static void NodeQualityEvaluator_DetectsRegionDriftSorryBlockAndDirectCnLeak()
        {
            var iqDc = new IpIntelligence { Ok = true, Ip = "198.51.100.8", CountryCode = "SG", IsDatacenter = true, RawRiskScore = 0 };
            var repDrift = new NodeQualityReport
            {
                TargetNode = "日本-专线",
                DesiredRegion = "auto",
                GoogleSearch = NodeQualityEvaluator.EvaluateGoogleRiskResponse(true, 302, "https://www.google.com/sorry/index", "", 85, ""),
                GeminiApi = NodeQualityEvaluator.EvaluateGeminiRegionResponse(true, 400, @"{""error"":{""message"":""User location is not supported for the API use.""}}", 90, "")
            };
            NodeQualityEvaluator.EvaluateReport(repDrift, iqDc, null);
            AssertEx.False(repDrift.RegionMatched);
            AssertEx.Equal("JP", repDrift.ExpectedRegion);
            AssertEx.Equal("SG", repDrift.CountryCode);
            AssertEx.Equal(NodeRiskLevel.High, repDrift.Level);
            AssertEx.False(repDrift.GoogleAiUsable);
            AssertEx.True(repDrift.RiskScore >= 65);

            var repCn = new NodeQualityReport { TargetNode = "日本-B", DesiredRegion = "auto" };
            NodeQualityEvaluator.EvaluateReport(repCn, new IpIntelligence { Ok = true, Ip = "203.0.113.99", CountryCode = "CN", RawRiskScore = 0 }, null);
            AssertEx.True(repCn.RoutedDirect);
            AssertEx.Equal(NodeRiskLevel.Unknown, repCn.Level);
        }

        [TestFact]
        public static void VersionComparerAndUpdateChecker_EnforceWhitelistAndRedirectTagParsing()
        {
            AssertEx.True(VersionComparer.IsNewer("v3.0.1", "3.0.0"));
            AssertEx.True(VersionComparer.IsNewer("3.1.0-rc.1", "3.0.0"));
            AssertEx.False(VersionComparer.IsNewer("v3.0.0", "3.0.0"));
            AssertEx.False(VersionComparer.IsNewer("v2.6.1", "3.0.0"));
            AssertEx.False(VersionComparer.IsNewer("not-a-ver", "3.0.0"));

            var updater = new UpdateCheckerService("wuerchenai-cell/ProxyPicker");
            AssertEx.True(updater.IsSafeReleaseUrl("https://github.com/wuerchenai-cell/ProxyPicker/releases/tag/v3.0.0"));
            AssertEx.False(updater.IsSafeReleaseUrl("http://github.com/wuerchenai-cell/ProxyPicker/releases/tag/v3.0.0"));
            AssertEx.False(updater.IsSafeReleaseUrl("https://evil.example/wuerchenai-cell/ProxyPicker/releases/tag/v3.0.0"));
            AssertEx.False(updater.IsSafeReleaseUrl("https://github.com/other/repo/releases/tag/v3.0.0"));

            string ver, safeUrl;
            AssertEx.True(updater.TryParseRedirectRelease("/wuerchenai-cell/ProxyPicker/releases/tag/v3.1.2", out ver, out safeUrl));
            AssertEx.Equal("3.1.2", ver);
            AssertEx.Equal("https://github.com/wuerchenai-cell/ProxyPicker/releases/tag/v3.1.2", safeUrl);

            AssertEx.False(updater.TryParseRedirectRelease("https://evil.com/wuerchenai-cell/ProxyPicker/releases/tag/v9.9.9", out ver, out safeUrl));
        }
    }
}
