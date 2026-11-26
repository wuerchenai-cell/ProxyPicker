using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using ProxyPicker.Domain.Models;

namespace ProxyPicker.Domain.Services
{
    /// <summary>
    /// 全链路统一隐私与凭据脱敏引擎（涵盖长短口令、固定 IP 主机/账密、节点名、IPv4/IPv6、用户名、管道 ID、访问域名）。
    /// </summary>
    public sealed class RedactorService
    {
        readonly object _gate = new object();
        string[] _longSecrets = new string[0];
        string[] _shortPasswords = new string[0];

        public void RegisterVault(StaticChannelVault vault)
        {
            if (vault == null || vault.Channels == null)
            {
                ClearRuntimeSecrets();
                return;
            }
            var vals = new List<string>();
            var pws = new List<string>();
            foreach (var ch in vault.Channels)
            {
                if (ch == null) continue;
                vals.Add(ch.Host);
                vals.Add(ch.Username);
                vals.Add(ch.Password);
                pws.Add(ch.Password);
            }
            RegisterCredentials(vals, pws);
        }

        public void RegisterCredentials(IEnumerable<string> values, IEnumerable<string> passwordValues = null)
        {
            var longList = new List<string>();
            var seenLong = new HashSet<string>(StringComparer.Ordinal);
            if (values != null)
            {
                foreach (var raw in values)
                {
                    string t = (raw ?? "").Trim();
                    if (t.Length < 4) continue;
                    if (t.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
                        t.Equals("localhost", StringComparison.OrdinalIgnoreCase)) continue;
                    if (seenLong.Add(t)) longList.Add(t);
                }
            }
            longList.Sort((a, b) => b.Length.CompareTo(a.Length));

            var shortPw = new List<string>();
            var seenPw = new HashSet<string>(StringComparer.Ordinal);
            if (passwordValues != null)
            {
                foreach (var raw in passwordValues)
                {
                    string p = (raw ?? "").Trim();
                    if (p.Length >= 1 && p.Length < 4 && seenPw.Add(p))
                        shortPw.Add(p);
                }
            }
            shortPw.Sort((a, b) => b.Length.CompareTo(a.Length));

            lock (_gate)
            {
                _longSecrets = longList.ToArray();
                _shortPasswords = shortPw.ToArray();
            }
        }

        public void ClearRuntimeSecrets()
        {
            lock (_gate)
            {
                _longSecrets = new string[0];
                _shortPasswords = new string[0];
            }
        }

        public string Apply(string text)
        {
            if (string.IsNullOrEmpty(text)) return text ?? "";
            string s = text;

            string[] longSec;
            string[] shortPw;
            lock (_gate)
            {
                longSec = _longSecrets;
                shortPw = _shortPasswords;
            }
            for (int i = 0; i < longSec.Length; i++)
            {
                string sec = longSec[i];
                if (!string.IsNullOrEmpty(sec) && s.IndexOf(sec, StringComparison.Ordinal) >= 0)
                    s = s.Replace(sec, "<凭据已隐藏>");
            }
            for (int i = 0; i < shortPw.Length; i++)
            {
                string pw = shortPw[i];
                if (string.IsNullOrEmpty(pw)) continue;
                string esc = Regex.Escape(pw);
                s = Regex.Replace(s,
                    @"((?:""|')?(?:password|passwd|pass|pwd|密码)(?:""|')?\s*[:=：]\s*(?:""|')?)" + esc + @"(?=(?:""|')?(?:[\s,;)\]}]|$))",
                    "$1<凭据已隐藏>", RegexOptions.IgnoreCase);
                s = Regex.Replace(s,
                    @"(\b(?:socks5h?|https?)://[^/\s:@]+:)" + esc + @"(@)",
                    "$1<凭据已隐藏>$2", RegexOptions.IgnoreCase);
            }

            string user = Environment.UserName;
            if (!string.IsNullOrEmpty(user))
                s = Regex.Replace(s, @"(?<![\w])" + Regex.Escape(user) + @"(?![\w])", "<用户>", RegexOptions.IgnoreCase);

            s = Regex.Replace(s, @"[A-Za-z]:\\Users\\[^\\\r\n]+\\", @"<用户目录>\", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"https?://[^\s""']+", "<链接已隐藏>");

            string snap = s;
            s = Regex.Replace(snap, @"\b(?!127\.0\.0\.1)\d{1,3}(?:\.\d{1,3}){3}\b", m =>
            {
                int ls = snap.LastIndexOf('\n', Math.Max(0, m.Index - 1)) + 1;
                string before = snap.Substring(ls, m.Index - ls);
                if (before.TrimEnd().EndsWith("ProxyPicker", StringComparison.OrdinalIgnoreCase))
                    return m.Value;
                return "x.x.x.x";
            });

            s = Regex.Replace(s, @"(?<![\w:])(?=[0-9A-Fa-f:]*:)[0-9A-Fa-f:]{2,}(?![\w:])", m =>
            {
                IPAddress ip;
                if (!IPAddress.TryParse(m.Value, out ip) || ip.AddressFamily != AddressFamily.InterNetworkV6) return m.Value;
                if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.IPv6Any)) return m.Value;
                return "<IPv6已隐藏>";
            });

            s = Regex.Replace(s, @"(?m)^节点：.*$", "节点：<节点名已隐藏>");
            s = Regex.Replace(s, @"(?m)^链路：(.*)$", m => "链路：" + MaskChain(m.Groups[1].Value));
            s = Regex.Replace(s, @"(?m)^policy=(.*)$", m => "policy=" + MaskName(m.Groups[1].Value));
            s = Regex.Replace(s, @"(?m)^group=(.*)$", m => "group=" + MaskName(m.Groups[1].Value));
            s = Regex.Replace(s, @"(?m)^outbound=(.*)$", m => "outbound=" + MaskExit(m.Groups[1].Value));
            s = Regex.Replace(s, @"(?m)^staticHop=(.*)$", m => "staticHop=" + MaskExit(m.Groups[1].Value));
            s = Regex.Replace(s, @"(?m)^chain=(.*)$", m => "chain=" + MaskChain(m.Groups[1].Value));
            s = Regex.Replace(s, @"(固定 IP (?:前置故障自愈切换至|稳速优选切换前置跳板|智能优选前置跳板)：)[^（\r\n]+", "$1<节点名已隐藏>");
            s = Regex.Replace(s, @"(固定 IP 定时稳速巡检完成：当前跳板「)[^」\r\n]+(」)", "$1<节点名已隐藏>$2");
            s = Regex.Replace(s, @"\\\\\.\\pipe\\[^\s\r\n""']+", @"<管道名已隐藏>");
            s = Regex.Replace(s, @"(""id""\s*:\s*"")([0-9A-Fa-f][0-9A-Fa-f-]{7,})("")", "$1<标识已隐藏>$3");
            s = Regex.Replace(s, @"(""(?:host|sniffHost)""\s*:\s*"")([^""\r\n]+)("")", "$1<域名已隐藏>$3", RegexOptions.IgnoreCase);
            s = Regex.Replace(s,
                @"((?:""|')?(?:password|passwd|token|secret|authorization|api[_-]?key|access[_-]?key)(?:""|')?\s*[:=]\s*(?:""|'))([^""'\r\n]+)(""|')",
                "$1<敏感值已隐藏>$3", RegexOptions.IgnoreCase);
            return s;
        }

        static string MaskName(string v)
        {
            string t = (v ?? "").Trim();
            return (t.Length == 0 || t == "未找到" || t == "未读取" || t == "-") ? t : "<组名已隐藏>";
        }

        static string MaskExit(string v)
        {
            string t = (v ?? "").Trim();
            if (t.Length == 0 || t == "未找到" || t == "未读取" || t == "-") return t;
            if (t.Equals("DIRECT", StringComparison.OrdinalIgnoreCase) ||
                t.Equals("REJECT", StringComparison.OrdinalIgnoreCase) ||
                t.Equals("REJECT-DROP", StringComparison.OrdinalIgnoreCase)) return t.ToUpperInvariant();
            return "<节点名已隐藏>";
        }

        static string MaskChain(string v)
        {
            string t = (v ?? "").Trim();
            if (t.Length == 0 || t == "未找到" || t == "未读取" || t == "-") return t;
            if (t.Equals("DIRECT", StringComparison.OrdinalIgnoreCase) ||
                t.Equals("REJECT", StringComparison.OrdinalIgnoreCase)) return t.ToUpperInvariant();
            return "<链路已隐藏>";
        }
    }
}
