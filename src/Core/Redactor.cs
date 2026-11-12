using System;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace ProxyPicker.Core
{
    /// <summary>
    /// 诊断文本 / 崩溃日志共用的脱敏器。
    /// 面板「复制诊断」、托盘「复制诊断信息」、写入 proxypicker.log 三条入口都走这里，
    /// 避免只修了其中一个入口、另外两个还在把用户名和订阅信息原样带出去。
    /// 命令行 --diag / --api / --raw 的输出同样经过这里（第 7 轮复审 R7-4 / R7-5）：
    /// 这些文件按文档指引是可能被用户贴出去求助的，不能原文带用户名、路径、节点名和管道实例 ID。
    /// </summary>
    internal static class Redactor
    {
        public static string Apply(string text)
        {
            if (string.IsNullOrEmpty(text)) return text ?? "";
            string s = text;

            // 整词边界：短用户名（a / li / user）不能把堆栈里的类名搅成乱码（复审 P2）。
            string user = Environment.UserName;
            if (!string.IsNullOrEmpty(user))
                s = Regex.Replace(s, @"(?<![\w])" + Regex.Escape(user) + @"(?![\w])",
                    "<用户>", RegexOptions.IgnoreCase);

            s = Regex.Replace(s, @"[A-Za-z]:\\Users\\[^\\\r\n]+\\", @"<用户目录>\", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"https?://[^\s""']+", "<链接已隐藏>");

            // IPv4 打码，但别把「ProxyPicker 1.1.0.0」这类版本号涂掉（复审 P2）。
            // 豁免只认「版本号那个位置」：前面紧挨着 "ProxyPicker " 且后面就是行尾。
            // 早先写成「整行含版本字样就放过」，将来日志里只要写一句
            // 「ProxyPicker 已连上 45.33.12.7」，那个 IP 就会原样带出去。
            string snapshot = s;
            s = Regex.Replace(snapshot, @"\b(?!127\.0\.0\.1)\d{1,3}(?:\.\d{1,3}){3}\b", m =>
            {
                int lineStart = snapshot.LastIndexOf('\n', Math.Max(0, m.Index - 1)) + 1;
                int lineEnd = snapshot.IndexOf('\n', m.Index);
                if (lineEnd < 0) lineEnd = snapshot.Length;
                string before = snapshot.Substring(lineStart, m.Index - lineStart);
                // 豁免条件：这一串紧跟在 "ProxyPicker " 后面（那就是版本号的位置）。
                // 同行后面再出现别的 IP 照样打码。
                bool isVersion = before.TrimEnd().EndsWith("ProxyPicker", StringComparison.OrdinalIgnoreCase);
                if (isVersion) return m.Value;
                return "x.x.x.x";
            });
            // IPv6 也打码。用 IPAddress.TryParse 识别完整/开头压缩/中间压缩三种形态；
            // 时间戳 12:34:56 解析不成 IPv6，不会被误伤（复审 F-14）。
            s = Regex.Replace(s, @"(?<![\w:])(?=[0-9A-Fa-f:]*:)[0-9A-Fa-f:]{2,}(?![\w:])", m =>
            {
                IPAddress ip;
                if (!IPAddress.TryParse(m.Value, out ip) ||
                    ip.AddressFamily != AddressFamily.InterNetworkV6) return m.Value;
                if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.IPv6Any)) return m.Value;
                return "<IPv6已隐藏>";
            });

            // 节点名和管道实例 ID 会暴露订阅来源 / 这台机器的标识。
            s = Regex.Replace(s, @"(?m)^节点：.*$", "节点：<节点名已隐藏>");
            // v1.3.7：--diag 里「策略组：X · 出口：Y · GLOBAL：Z」是组合行，
            // 以前只盖住独立的「节点：」行，组合行里的组名和节点名照旧带出去。
            // 只隐藏名字，保留 DIRECT / REJECT / 未找到 这类排查必需的状态词。
            s = Regex.Replace(s, @"(?m)^策略组：(.+?) · 出口：(.+?) · GLOBAL：(.+)$", m =>
                "策略组：" + MaskName(m.Groups[1].Value) +
                " · 出口：" + MaskExit(m.Groups[2].Value) +
                " · GLOBAL：" + MaskName(m.Groups[3].Value));
            s = Regex.Replace(s, @"(实际 代理)（[^）\r\n]*）", "$1（节点名已隐藏）");
            // ID 放宽到 ≥8 位 hex 或 GUID 形态；\\.\pipe\ 全名一律整段隐藏（复审 P2）。
            s = Regex.Replace(s, @"(?m)^(管道：.+)-([0-9A-Fa-f][0-9A-Fa-f-]{7,})\s*$",
                "$1-<实例标识已隐藏>");
            s = Regex.Replace(s, @"\\\\\.\\pipe\\[^\s\r\n""']+", @"<管道名已隐藏>");
            // JSON 体内的机器/连接标识：--api / --raw 的响应里 "id" 字段是 UUID 或长 hex
            // （第 7 轮复审 R7-5 实测 75 个 UUID 原文带出）。
            s = Regex.Replace(s, @"(""id""\s*:\s*"")([0-9A-Fa-f][0-9A-Fa-f-]{7,})("")", "$1<标识已隐藏>$3");
            // /connections 里的访问域名同样属于隐私：诊断文件可能被贴出去求助。
            // 只打码 host/sniffHost，保留 IP、进程名、规则等排查必需的信息。
            s = Regex.Replace(s, @"(""(?:host|sniffHost)""\s*:\s*"")([^""\r\n]+)("")",
                "$1<域名已隐藏>$3", RegexOptions.IgnoreCase);
            // 独立审阅第二轮：--api / --raw 可能带出任意 JSON，固定字段规则覆盖不全。
            // 常见敏感字段名的值一律打码；但诊断仍应默认按白名单生成，不能只靠正则
            // 就承诺「可以直接公开贴出」。
            s = Regex.Replace(s,
                @"((?:""|')?(?:password|passwd|token|secret|authorization|api[_-]?key|access[_-]?key)(?:""|')?\s*[:=]\s*(?:""|'))([^""'\r\n]+)(""|')",
                "$1<敏感值已隐藏>$3", RegexOptions.IgnoreCase);
            return s;
        }

        /// <summary>诊断行里的策略组 / GLOBAL 名字：状态词保留，真名字隐藏。</summary>
        static string MaskName(string v)
        {
            string t = (v ?? "").Trim();
            if (t.Length == 0 || t == "未找到" || t == "未读取" || t == "-") return t;
            return "<组名已隐藏>";
        }

        /// <summary>出口名：DIRECT / REJECT 这类状态词要留着，节点名隐藏。</summary>
        static string MaskExit(string v)
        {
            string t = (v ?? "").Trim();
            if (t.Length == 0 || t == "未找到" || t == "未读取" || t == "-") return t;
            if (t.Equals("DIRECT", StringComparison.OrdinalIgnoreCase) ||
                t.Equals("REJECT", StringComparison.OrdinalIgnoreCase) ||
                t.Equals("REJECT-DROP", StringComparison.OrdinalIgnoreCase)) return t.ToUpperInvariant();
            return "<节点名已隐藏>";
        }

        /// <summary>
        /// --pipes 的输出是一行一个管道名，形式是「名字-实例标识」。
        /// 全局脱敏器只认带 \\.\pipe\ 前缀的整段形式，这里补一条只针对裸管道名的规则：
        /// 只打码末尾那段十六进制实例标识，名字本身留着（排查要看是哪一家的内核）。
        /// 只给 --pipes 用，不进通用脱敏，避免误伤校验和之类的内容。
        /// </summary>
        public static string MaskPipeList(string text)
        {
            if (string.IsNullOrEmpty(text)) return text ?? "";
            return Regex.Replace(text, @"(?m)^([A-Za-z0-9_.\-]{3,})-([0-9A-Fa-f]{8,})\s*$",
                "$1-<实例标识已隐藏>");
        }
    }
}
