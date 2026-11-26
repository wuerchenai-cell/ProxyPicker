using System;
using System.Collections.Generic;
using ProxyPicker.Domain.Json;
using ProxyPicker.Domain.Models;

namespace ProxyPicker.Domain.Services
{
    /// <summary>
    /// 节点质量与风控评估纯领域算法引擎（零网络与 OS 依赖）：
    /// 负责延迟/抖动/丢包统计、地区码识别、多源 IP 情报 JSON 解析、主流服务放行评估及综合风控评级。
    /// </summary>
    public static class NodeQualityEvaluator
    {
        static readonly Dictionary<string, string> RegionNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["JP"] = "日本", ["US"] = "美国", ["SG"] = "新加坡", ["HK"] = "中国香港",
            ["TW"] = "中国台湾", ["KR"] = "韩国", ["GB"] = "英国", ["UK"] = "英国",
            ["DE"] = "德国", ["FR"] = "法国", ["NL"] = "荷兰", ["CA"] = "加拿大",
            ["AU"] = "澳大利亚", ["MY"] = "马来西亚", ["TH"] = "泰国", ["VN"] = "越南",
            ["IN"] = "印度", ["TR"] = "土耳其", ["AR"] = "阿根廷", ["BR"] = "巴西",
            ["CN"] = "中国大陆", ["RU"] = "俄罗斯", ["MO"] = "中国澳门"
        };

        static readonly Tuple<string, string[]>[] RegionKeywords =
        {
            Tuple.Create("JP", new[] { "日本", "东京", "大阪", "🇯🇵", "japan", "tokyo", "osaka", " jp", "-jp", "_jp", "jp-", "jp_" }),
            Tuple.Create("SG", new[] { "新加坡", "狮城", "🇸🇬", "singapore", " sg", "-sg", "_sg", "sg-", "sg_" }),
            Tuple.Create("US", new[] { "美国", "洛杉矶", "硅谷", "西雅图", "纽约", "🇺🇸", "united states", "usa", "america", " us", "-us", "_us", "us-", "us_" }),
            Tuple.Create("HK", new[] { "香港", "🇭🇰", "hong kong", "hongkong", " hk", "-hk", "_hk", "hk-", "hk_" }),
            Tuple.Create("TW", new[] { "台湾", "台北", "🇹🇼", "taiwan", "taipei", " tw", "-tw", "_tw", "tw-", "tw_" }),
            Tuple.Create("KR", new[] { "韩国", "首尔", "🇰🇷", "korea", "seoul", " kr", "-kr", "_kr", "kr-", "kr_" }),
            Tuple.Create("GB", new[] { "英国", "伦敦", "🇬🇧", "united kingdom", "london", " uk", "-uk", "gb" }),
            Tuple.Create("DE", new[] { "德国", "法兰克福", "🇩🇪", "germany", "frankfurt", " de", "-de" }),
            Tuple.Create("FR", new[] { "法国", "巴黎", "🇫🇷", "france", "paris", " fr", "-fr" }),
            Tuple.Create("NL", new[] { "荷兰", "阿姆斯特丹", "🇳🇱", "netherlands", " nl", "-nl" }),
            Tuple.Create("CA", new[] { "加拿大", "多伦多", "🇨🇦", "canada", " ca", "-ca" }),
            Tuple.Create("AU", new[] { "澳大利亚", "悉尼", "🇦🇺", "australia", "sydney", " au", "-au" })
        };

        public static string RegionDisplayName(string code)
        {
            if (string.IsNullOrWhiteSpace(code)) return "未知";
            string c = code.Trim().ToUpperInvariant();
            string name;
            return RegionNames.TryGetValue(c, out name) ? name : c;
        }

        public static string ParseRegionFromNodeName(string nodeName)
        {
            if (string.IsNullOrWhiteSpace(nodeName)) return "";
            string low = " " + nodeName.ToLowerInvariant();
            foreach (var item in RegionKeywords)
            {
                foreach (var kw in item.Item2)
                {
                    if (low.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0)
                        return item.Item1;
                }
            }
            return "";
        }

        public static string NormalizeDesiredRegion(string desired)
        {
            if (string.IsNullOrWhiteSpace(desired)) return "auto";
            string s = desired.Trim().ToLowerInvariant();
            if (s == "auto" || s == "any") return s;
            if (s.Length == 2) return s.ToUpperInvariant();
            return "auto";
        }

        public static bool IsAiUnsupportedCountry(string cc)
        {
            if (string.IsNullOrEmpty(cc)) return false;
            string c = cc.ToUpperInvariant();
            return c == "CN" || c == "HK" || c == "MO" || c == "RU" || c == "BY" || c == "IR" || c == "KP" || c == "CU" || c == "SY";
        }

        public static bool ShouldRunHostIpv6LeakProbe(bool isSidecar, bool hasOutboundProxy, bool tunEnabled)
        {
            return !isSidecar && (hasOutboundProxy || tunEnabled);
        }

        public static NodeLatencyStats ComputeLatencyStats(IList<int> samples)
        {
            var st = new NodeLatencyStats();
            if (samples == null || samples.Count == 0) return st;

            st.Sent = samples.Count;
            int sum = 0;
            int min = int.MaxValue;
            int max = 0;
            foreach (int ms in samples)
            {
                if (ms <= 0) continue;
                st.Received++;
                sum += ms;
                if (ms < min) min = ms;
                if (ms > max) max = ms;
            }
            st.LossPercent = (int)Math.Round(((st.Sent - st.Received) * 100.0) / st.Sent);
            if (st.Received > 0)
            {
                st.AvgMs = (int)Math.Round(sum / (double)st.Received);
                st.MinMs = min;
                st.MaxMs = max;
                st.JitterMs = st.Received >= 2 ? (max - min) : 0;
            }
            return st;
        }

        public static IpIntelligence ParseIpQueryJson(string text)
        {
            var r = new IpIntelligence { Source = "ipquery.io" };
            try
            {
                var root = JsonCodec.AsObject(JsonCodec.Parse(text));
                if (root == null) return r;
                r.Ip = JsonCodec.Str(root, "ip");
                var isp = JsonCodec.Obj(root, "isp");
                if (isp != null)
                {
                    r.Asn = JsonCodec.Str(isp, "asn");
                    r.Org = JsonCodec.Str(isp, "org");
                    r.Isp = JsonCodec.Str(isp, "isp");
                }
                var loc = JsonCodec.Obj(root, "location");
                if (loc != null)
                {
                    r.Country = JsonCodec.Str(loc, "country");
                    r.CountryCode = JsonCodec.Str(loc, "country_code").ToUpperInvariant();
                    r.City = JsonCodec.Str(loc, "city");
                    r.Region = JsonCodec.Str(loc, "state");
                }
                var risk = JsonCodec.Obj(root, "risk");
                if (risk != null)
                {
                    r.IsMobile = JsonCodec.Bool(risk, "is_mobile", false);
                    r.IsVpn = JsonCodec.Bool(risk, "is_vpn", false);
                    r.IsTor = JsonCodec.Bool(risk, "is_tor", false);
                    r.IsProxy = JsonCodec.Bool(risk, "is_proxy", false);
                    r.IsDatacenter = JsonCodec.Bool(risk, "is_datacenter", false);
                    int score = JsonCodec.Int(risk, "risk_score", -1);
                    if (score >= 0) r.RawRiskScore = score;
                }
                r.Ok = r.Ip.Length > 0;
            }
            catch (Exception ex) { r.Error = ex.Message; }
            return r;
        }

        public static IpIntelligence ParseIpWhoIsJson(string text)
        {
            var r = new IpIntelligence { Source = "ipwho.is" };
            try
            {
                var root = JsonCodec.AsObject(JsonCodec.Parse(text));
                if (root == null || !JsonCodec.Bool(root, "success", true)) return r;
                r.Ip = JsonCodec.Str(root, "ip");
                r.Country = JsonCodec.Str(root, "country");
                r.CountryCode = JsonCodec.Str(root, "country_code").ToUpperInvariant();
                r.Region = JsonCodec.Str(root, "region");
                r.City = JsonCodec.Str(root, "city");
                var conn = JsonCodec.Obj(root, "connection");
                if (conn != null)
                {
                    int asnNum = JsonCodec.Int(conn, "asn", -1);
                    r.Asn = asnNum > 0 ? ("AS" + asnNum) : JsonCodec.Str(conn, "asn");
                    r.Org = JsonCodec.Str(conn, "org");
                    r.Isp = JsonCodec.Str(conn, "isp");
                }
                var sec = JsonCodec.Obj(root, "security");
                if (sec != null)
                {
                    r.IsVpn = JsonCodec.Bool(sec, "vpn", false);
                    r.IsProxy = JsonCodec.Bool(sec, "proxy", false);
                    r.IsTor = JsonCodec.Bool(sec, "tor", false);
                    r.IsDatacenter = JsonCodec.Bool(sec, "hosting", false);
                }
                r.Ok = r.Ip.Length > 0;
            }
            catch (Exception ex) { r.Error = ex.Message; }
            return r;
        }

        public static IpIntelligence ParseProxyCheckJson(string text, string ip)
        {
            var r = new IpIntelligence { Source = "proxycheck.io", Ip = ip ?? "" };
            try
            {
                var root = JsonCodec.AsObject(JsonCodec.Parse(text));
                if (root == null) return r;
                var entry = JsonCodec.Obj(root, ip);
                if (entry == null) return r;

                r.Asn = JsonCodec.Str(entry, "asn");
                r.Isp = JsonCodec.Str(entry, "provider");
                r.Org = JsonCodec.Str(entry, "organisation");
                r.Country = JsonCodec.Str(entry, "country");
                r.CountryCode = JsonCodec.Str(entry, "isocode").ToUpperInvariant();
                r.City = JsonCodec.Str(entry, "city");
                r.Region = JsonCodec.Str(entry, "region");
                r.IpType = JsonCodec.Str(entry, "type");
                string proxyFlag = JsonCodec.Str(entry, "proxy");
                r.IsProxy = proxyFlag.Equals("yes", StringComparison.OrdinalIgnoreCase);
                if (r.IpType.Equals("VPN", StringComparison.OrdinalIgnoreCase)) r.IsVpn = true;
                if (r.IpType.Equals("Hosting", StringComparison.OrdinalIgnoreCase)) r.IsDatacenter = true;
                int risk = JsonCodec.Int(entry, "risk", -1);
                if (risk >= 0) r.RawRiskScore = risk;
                r.Ok = true;
            }
            catch (Exception ex) { r.Error = ex.Message; }
            return r;
        }

        public static IpIntelligence ParseIpInfoJson(string text)
        {
            var r = new IpIntelligence { Source = "ipinfo.io" };
            try
            {
                var root = JsonCodec.AsObject(JsonCodec.Parse(text));
                if (root == null) return r;
                r.Ip = JsonCodec.Str(root, "ip");
                r.CountryCode = JsonCodec.Str(root, "country").ToUpperInvariant();
                r.City = JsonCodec.Str(root, "city");
                r.Region = JsonCodec.Str(root, "region");
                r.Org = JsonCodec.Str(root, "org");
                r.Isp = r.Org;
                if (r.Org.StartsWith("AS", StringComparison.OrdinalIgnoreCase))
                {
                    int sp = r.Org.IndexOf(' ');
                    if (sp > 2)
                    {
                        r.Asn = r.Org.Substring(0, sp);
                        r.Isp = r.Org.Substring(sp + 1).Trim();
                    }
                }
                r.Ok = r.Ip.Length > 0;
            }
            catch (Exception ex) { r.Error = ex.Message; }
            return r;
        }

        public static void ParseCloudflareTrace(string text, out string ip, out string loc, out string colo)
        {
            ip = ""; loc = ""; colo = "";
            if (string.IsNullOrEmpty(text)) return;
            foreach (var raw in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = raw.IndexOf('=');
                if (eq <= 0) continue;
                string k = raw.Substring(0, eq).Trim();
                string v = raw.Substring(eq + 1).Trim();
                if (k == "ip") ip = v;
                else if (k == "loc") loc = v.ToUpperInvariant();
                else if (k == "colo") colo = v.ToUpperInvariant();
            }
        }

        public static ServiceGateResult EvaluateGoogleRiskResponse(
            bool gotResponse, int status, string locationHeader, string body, int ms, string err)
        {
            var g = new ServiceGateResult { Name = "Google", Tested = true, StatusCode = status, LatencyMs = ms };
            if (!gotResponse) { g.Detail = !string.IsNullOrEmpty(err) ? err : "连接超时"; return g; }

            bool sorryRedirect = !string.IsNullOrEmpty(locationHeader) &&
                locationHeader.IndexOf("/sorry", StringComparison.OrdinalIgnoreCase) >= 0;
            bool sorryBody = !string.IsNullOrEmpty(body) &&
                (body.IndexOf("/sorry/index", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 body.IndexOf("unusual traffic", StringComparison.OrdinalIgnoreCase) >= 0);

            if (sorryRedirect || sorryBody || status == 429 || status == 403)
            {
                g.Blocked = true; g.Ok = false;
                g.Detail = "触发人机风控 (" + status + ")";
                return g;
            }
            if (status >= 200 && status < 400)
            {
                g.Ok = true; g.Detail = "放行 · " + ms + "ms";
                return g;
            }
            g.Detail = "HTTP " + status;
            return g;
        }

        public static ServiceGateResult EvaluateGeminiRegionResponse(
            bool gotResponse, int status, string body, int ms, string err)
        {
            var g = new ServiceGateResult { Name = "Google AI (Gemini)", Tested = true, StatusCode = status, LatencyMs = ms };
            if (!gotResponse) { g.Detail = !string.IsNullOrEmpty(err) ? err : "不可达/超时"; return g; }

            string low = (body ?? "").ToLowerInvariant();
            if (low.Contains("location is not supported") || (low.Contains("country") && low.Contains("not supported")))
            {
                g.Blocked = true; g.Ok = false; g.Detail = "地区不支持";
                return g;
            }
            if (status == 200 ||
                ((status == 400 || status == 401 || status == 403) &&
                 (low.Contains("api key") || low.Contains("unregistered") || low.Contains("credentials") ||
                  (low.Contains("permission_denied") && !low.Contains("location")))))
            {
                g.Ok = true; g.Detail = "地区支持 · " + ms + "ms";
                return g;
            }
            if (status == 403 || status == 429)
            {
                g.Blocked = true; g.Ok = false; g.Detail = "风控拦截 HTTP " + status;
                return g;
            }
            g.Detail = "HTTP " + status;
            return g;
        }

        public static ServiceGateResult EvaluateOpenAiResponse(
            bool gotResponse, int status, string body, int ms, string err)
        {
            var g = new ServiceGateResult { Name = "OpenAI", Tested = true, StatusCode = status, LatencyMs = ms };
            if (!gotResponse) { g.Detail = !string.IsNullOrEmpty(err) ? err : "不可达/超时"; return g; }

            string low = (body ?? "").ToLowerInvariant();
            if (status == 401 || status == 200 || (status == 400 && low.Contains("openai")))
            {
                g.Ok = true; g.Detail = "放行 · " + ms + "ms";
                return g;
            }
            if (status == 403 || status == 429 || low.Contains("unsupported_country") || low.Contains("cloudflare"))
            {
                g.Blocked = true; g.Ok = false; g.Detail = "地区/盾拦截 (" + status + ")";
                return g;
            }
            g.Detail = "HTTP " + status;
            return g;
        }

        public static ServiceGateResult EvaluateClaudeResponse(
            bool gotResponse, int status, string body, int ms, string err)
        {
            var g = new ServiceGateResult { Name = "Claude", Tested = true, StatusCode = status, LatencyMs = ms };
            if (!gotResponse) { g.Detail = !string.IsNullOrEmpty(err) ? err : "不可达/超时"; return g; }

            string low = (body ?? "").ToLowerInvariant();
            if (status == 401 || status == 200 || status == 405 || (status == 400 && low.Contains("anthropic")))
            {
                g.Ok = true; g.Detail = "放行 · " + ms + "ms";
                return g;
            }
            if (status == 403 || status == 451 || status == 429 ||
                low.Contains("cloudflare") || low.Contains("just a moment") ||
                low.Contains("not available") || low.Contains("unsupported"))
            {
                g.Blocked = true; g.Ok = false; g.Detail = "地区/盾拦截 (" + status + ")";
                return g;
            }
            g.Detail = "HTTP " + status;
            return g;
        }

        /// <summary>综合双源 IP 情报、四大主流服务实测与 IPv6 泄漏，生成确定性的风控评级与地区结论。</summary>
        public static void EvaluateReport(NodeQualityReport r, IpIntelligence primary, IpIntelligence secondary)
        {
            if (r == null) return;

            string ip = !string.IsNullOrEmpty(primary?.Ip) ? primary.Ip : (secondary?.Ip ?? "");
            string cc = (!string.IsNullOrEmpty(primary?.CountryCode) ? primary.CountryCode : (secondary?.CountryCode ?? "")).ToUpperInvariant();
            string country = !string.IsNullOrEmpty(primary?.Country) ? primary.Country : (secondary?.Country ?? "");
            string city = !string.IsNullOrEmpty(primary?.City) ? primary.City : (secondary?.City ?? "");
            string asn = !string.IsNullOrEmpty(primary?.Asn) ? primary.Asn : (secondary?.Asn ?? "");
            string isp = !string.IsNullOrEmpty(primary?.Isp) ? primary.Isp : (secondary?.Isp ?? "");
            if (string.IsNullOrEmpty(isp)) isp = !string.IsNullOrEmpty(primary?.Org) ? primary.Org : (secondary?.Org ?? "");

            r.Ip = ip;
            r.CountryCode = cc;
            r.CountryName = cc.Length == 2 ? RegionDisplayName(cc) : country;
            r.City = city;
            r.Asn = asn;
            r.Isp = isp;

            bool isProxyTarget = !string.Equals(r.TargetNode, "DIRECT", StringComparison.OrdinalIgnoreCase) &&
                                 !string.Equals(r.TargetNode, "REJECT", StringComparison.OrdinalIgnoreCase);

            string targetRegion = r.DesiredRegion;
            if (string.IsNullOrEmpty(targetRegion) || targetRegion.Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                targetRegion = !string.IsNullOrEmpty(r.ExpectedRegion)
                    ? r.ExpectedRegion
                    : ParseRegionFromNodeName(r.TargetNode);
            }
            else if (targetRegion.Equals("any", StringComparison.OrdinalIgnoreCase))
            {
                targetRegion = "";
            }
            r.ExpectedRegion = targetRegion;

            if (isProxyTarget && string.Equals(cc, "CN", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(r.ExpectedRegion, "CN", StringComparison.OrdinalIgnoreCase))
            {
                r.RoutedDirect = true;
            }

            if (r.RoutedDirect)
            {
                r.RegionMatched = false;
                r.RegionNote = "探针走了本地直连（CN），未经过代理节点";
            }
            else if (cc.Length == 2 && targetRegion.Length == 2)
            {
                r.RegionMatched = string.Equals(cc, targetRegion, StringComparison.OrdinalIgnoreCase);
                r.RegionNote = r.RegionMatched
                    ? ("与预期地区一致（" + RegionDisplayName(cc) + "）")
                    : ("地区漂移：预期 " + RegionDisplayName(targetRegion) + "，实际落地 " + RegionDisplayName(cc) + " (" + cc + ")");
            }
            else if (cc.Length == 2)
            {
                r.RegionMatched = true;
                r.RegionNote = "当前落地：" + RegionDisplayName(cc) + " (" + cc + ")";
            }
            else
            {
                r.RegionMatched = false;
                r.RegionNote = "未能识别落地国家/地区";
            }

            bool isTor = (primary?.IsTor ?? false) || (secondary?.IsTor ?? false);
            bool isVpn = (primary?.IsVpn ?? false) || (secondary?.IsVpn ?? false);
            bool isProxy = (primary?.IsProxy ?? false) || (secondary?.IsProxy ?? false);
            bool isDc = (primary?.IsDatacenter ?? false) || (secondary?.IsDatacenter ?? false);
            bool isMob = (primary?.IsMobile ?? false) || (secondary?.IsMobile ?? false);

            string rawType = !string.IsNullOrEmpty(secondary?.IpType) ? secondary.IpType : (primary?.IpType ?? "");
            if (rawType.Equals("Hosting", StringComparison.OrdinalIgnoreCase) ||
                rawType.Equals("Data Center/Web Hosting/Transit", StringComparison.OrdinalIgnoreCase)) isDc = true;
            if (rawType.Equals("VPN", StringComparison.OrdinalIgnoreCase) ||
                rawType.Equals("Proxy", StringComparison.OrdinalIgnoreCase)) isVpn = true;

            if (isTor) r.IpKindLabel = "Tor 匿名节点";
            else if (isVpn || isProxy) r.IpKindLabel = isDc ? "机房 VPN/共享代理" : "已标记代理 IP";
            else if (isMob) r.IpKindLabel = "移动蜂窝 IP";
            else if (isDc) r.IpKindLabel = "机房 / 数据中心 IP";
            else if (rawType.Equals("Residential", StringComparison.OrdinalIgnoreCase)) r.IpKindLabel = "住宅家宽 IP";
            else if (rawType.Equals("Business", StringComparison.OrdinalIgnoreCase)) r.IpKindLabel = "商业宽带 / ISP";
            else if (ip.Length > 0) r.IpKindLabel = "常规 ISP IP";
            else r.IpKindLabel = "未知";

            int s1 = primary != null && primary.RawRiskScore >= 0 ? primary.RawRiskScore : -1;
            int s2 = secondary != null && secondary.RawRiskScore >= 0 ? secondary.RawRiskScore : -1;
            int score = Math.Max(s1, s2);
            if (score < 0 && ip.Length > 0) score = 0;

            if (score >= 0)
            {
                if (isDc && score < 25) score = 25;
                if ((isVpn || isProxy) && score < 45) score = 45;
                if (isTor && score < 95) score = 95;
                if (r.GoogleSearch.Tested && r.GoogleSearch.Blocked) score = Math.Max(65, score + 25);
                if (r.OpenAiApi.Tested && r.OpenAiApi.Blocked) score = Math.Max(score, 55);
                score = Math.Max(0, Math.Min(100, score));
            }
            r.RiskScore = score;

            bool geminiBlocked = r.GeminiApi.Tested && r.GeminiApi.Blocked;
            bool googleBlocked = r.GoogleSearch.Tested && r.GoogleSearch.Blocked;
            bool claudeBlocked = r.ClaudeApi.Tested && r.ClaudeApi.Blocked;
            bool openaiBlocked = r.OpenAiApi.Tested && r.OpenAiApi.Blocked;
            bool highLoss = r.Net.Sent > 0 && r.Net.LossPercent >= 66;
            bool unsupportedRegion = IsAiUnsupportedCountry(cc);

            int ready = 0;
            if (r.GoogleSearch.Tested && r.GoogleSearch.Ok) ready++;
            if (r.GeminiApi.Tested && r.GeminiApi.Ok) ready++;
            if (r.OpenAiApi.Tested && r.OpenAiApi.Ok) ready++;
            if (r.ClaudeApi.Tested && r.ClaudeApi.Ok) ready++;
            r.MainstreamReadyCount = ready;

            if (r.RoutedDirect)
            {
                r.Level = NodeRiskLevel.Unknown;
                r.GoogleAiUsable = false;
                r.VerdictTitle = "未走代理节点（命中本地直连）";
                r.VerdictDetail = "本次探测从本地直连出网，请使用隔离侧车或在白名单保存后再检测。";
                return;
            }
            if (string.IsNullOrEmpty(ip) && !r.GeminiApi.Tested && !r.GoogleSearch.Tested)
            {
                r.Level = NodeRiskLevel.Unknown;
                r.GoogleAiUsable = false;
                r.VerdictTitle = "网络不可达或节点超时";
                r.VerdictDetail = string.IsNullOrEmpty(r.Error) ? "无法访问外网检测接口，请更换节点重试。" : r.Error;
                return;
            }
            if (score >= 65 || googleBlocked || geminiBlocked || unsupportedRegion || isTor)
            {
                r.Level = NodeRiskLevel.High;
                r.GoogleAiUsable = false;
                var reasons = new List<string>();
                if (geminiBlocked || unsupportedRegion) reasons.Add("部分主流 AI 地区受限");
                if (googleBlocked) reasons.Add("Google 搜索触发人机验证");
                if (claudeBlocked) reasons.Add("Claude 触发安全盾/地区拦截");
                if (openaiBlocked) reasons.Add("OpenAI 地区或代理受限");
                if (score >= 65) reasons.Add("IP 风险分偏高 (" + score + "/100)");
                if (isTor) reasons.Add("Tor 出口");
                r.VerdictTitle = "高风险 · 存在风控或地区受限";
                r.VerdictDetail = string.Join("；", reasons) + (r.DirectIpv6Leak ? "；存在本地 IPv6 直连泄漏" : "") + "。";
            }
            else if (score >= 30 || isDc || isVpn || isProxy || r.DirectIpv6Leak || !r.RegionMatched || highLoss || claudeBlocked || openaiBlocked)
            {
                r.Level = NodeRiskLevel.Medium;
                r.GoogleAiUsable = !geminiBlocked && !unsupportedRegion && !highLoss;
                var notes = new List<string>();
                if (r.DirectIpv6Leak) notes.Add("检测到 IPv6 直连泄漏");
                if (!r.RegionMatched) notes.Add(r.RegionNote);
                if (highLoss) notes.Add("丢包偏高 (" + r.Net.LossPercent + "%)");
                if (claudeBlocked) notes.Add("Claude 存在盾/地区拦截");
                if (openaiBlocked) notes.Add("OpenAI 存在拦截");
                if (notes.Count == 0) notes.Add(r.IpKindLabel + "，主流服务放行 (" + ready + "/4)");
                r.VerdictTitle = r.GoogleAiUsable ? "中风险 · 常规业务可用" : "中风险 · 链路不够稳定";
                r.VerdictDetail = string.Join("；", notes) + "。";
            }
            else
            {
                r.Level = NodeRiskLevel.Low;
                r.GoogleAiUsable = true;
                r.VerdictTitle = "低风险 · 纯净原生出口";
                r.VerdictDetail = "出口地区与 IP 纯净度优异，主流及敏感服务均正常放行。";
            }
        }
    }
}
