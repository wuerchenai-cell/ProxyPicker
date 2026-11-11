using System;
using System.Net;
using System.Text;

namespace ProxyPicker.Core
{
    /// <summary>
    /// 「有没有新版本」的查询。只做一件事：向 GitHub 的 releases/latest 发一次 GET，
    /// 把最新版本号拿回来跟自己的比一比。
    ///
    /// 刻意不做自动下载、自动替换：这个 exe 只有自签名证书，Windows 不认识发布者，
    /// 「自己下载自己覆盖自己」这个行为特征和恶意软件高度重合，会显著提高拦截概率。
    /// 查到新版本只提示 + 打开网页，下载和安装始终由用户自己决定。
    ///
    /// 另外还刻意压住了三件事：失败必须静默（没网、被墙、公司代理都算正常情况）、
    /// 每天最多查一次、只发一个 GET 并且不带任何本机信息（UA 只写程序名和版本）。
    /// </summary>
    internal static class Updater
    {
        /// <summary>
        /// 仓库地址，格式「用户名/仓库名」。留空表示检查更新未接上：
        /// 界面上不会出现版本链接，也不会发出任何网络请求。
        /// 现在已填我们自己的仓库：Enabled 只校验格式，不代表远端已经有比本机更新的
        /// Release；只有远端版本号严格大于本机才提示（远端与本机同为最新版时不提醒）。
        /// </summary>
        const string DefaultSlug = "wuerchenai-cell/ProxyPicker";

        // 只在 --checkupdate 自检时被临时替换，正式运行永远用 DefaultSlug。
        // 有了它，接仓库之前就能拿一个真实的公开仓库把「发请求 + 解析 + 比版本」整条链路验通。
        static string _testSlug;

        public static string RepoSlug
        {
            get { return string.IsNullOrEmpty(_testSlug) ? DefaultSlug : _testSlug; }
        }

        public static void OverrideForTest(string slug) { _testSlug = slug; }

        const string ApiRoot = "https://api.github.com/repos/";

        public static bool Enabled
        {
            get { return !string.IsNullOrEmpty(RepoSlug) && RepoSlug.IndexOf('/') > 0; }
        }

        /// <summary>自己这一版的版本号，来自 csproj 的 &lt;Version&gt;。</summary>
        public static Version Current
        {
            get
            {
                try { return typeof(Updater).Assembly.GetName().Version ?? new Version(1, 0, 0, 0); }
                catch { return new Version(1, 0, 0, 0); }
            }
        }

        /// <summary>显示用的版本号：1.0.0（不显示修订号那一位的 0）。</summary>
        public static string CurrentText
        {
            get
            {
                var v = Current;
                return v.Major + "." + v.Minor + "." + Math.Max(0, v.Build);
            }
        }

        public sealed class Info
        {
            public bool Ok;          // 真的问到了（拿到 200 且解析成功）
            public bool Newer;       // 有没有比本机更新
            public string Latest = "";   // 形如 1.1.0
            public string Url = "";      // Release 页面
            public string Date = "";     // 发布时间（只取日期部分）
            public string Error = "";    // 失败原因，给「手动检查」时显示一句人话
        }

        /// <summary>
        /// 查一次最新版本。任何异常都不往外抛，全部折成 Info.Error。
        /// 走系统代理（默认行为）：用户开着代理才可能连上，所以不主动置 Proxy=null。
        /// </summary>
        public static Info Check(int timeoutMs)
        {
            var info = new Info();
            if (!Enabled) { info.Error = "仓库地址还没配置"; return info; }

            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; }
            catch { }

            try
            {
                string url = ApiRoot + RepoSlug + "/releases/latest";
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "GET";
                req.Timeout = timeoutMs;
                req.ReadWriteTimeout = timeoutMs;
                req.UserAgent = "ProxyPicker/" + CurrentText;
                req.Accept = "application/vnd.github+json";
                req.KeepAlive = false;
                req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;

                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var stream = resp.GetResponseStream())
                using (var reader = new System.IO.StreamReader(stream, Encoding.UTF8))
                {
                    string body = reader.ReadToEnd();
                    var root = Json.AsObject(Json.Parse(body));

                    string tag = Json.Str(root, "tag_name");
                    if (string.IsNullOrEmpty(tag)) { info.Error = "发布信息里没有版本号"; return info; }

                    info.Ok = true;
                    info.Latest = Clean(tag);
                    info.Url = Json.Str(root, "html_url");
                    string published = Json.Str(root, "published_at");
                    if (published.Length >= 10) info.Date = published.Substring(0, 10);
                    info.Newer = NewerThan(info.Latest, Current);
                    if (string.IsNullOrEmpty(info.Url)) info.Url = "https://github.com/" + RepoSlug + "/releases";
                }
            }
            catch (WebException wex)
            {
                info.Error = Describe(wex);
            }
            catch (Exception ex)
            {
                info.Error = ex.Message;
            }
            return info;
        }

        /// <summary>
        /// 把失败翻成人话。用户手动点「检查更新」时会看到这一句，
        /// 所以别写「远程服务器返回错误: (403) 已禁止」这种只有开发者看得懂的原文。
        /// </summary>
        static string Describe(WebException wex)
        {
            try
            {
                var resp = wex.Response as HttpWebResponse;
                if (resp != null)
                {
                    int code = (int)resp.StatusCode;
                    if (code == 404) return "仓库里还没有发布版本";
                    if (code == 403) return "查询次数太多，明天再看";
                    if (code >= 500) return "GitHub 暂时不可用";
                    return "服务器返回 " + code;
                }
                if (wex.Status == WebExceptionStatus.Timeout) return "网络没通，连不上 GitHub";
                return "网络没通（没开代理时属正常）";
            }
            catch { return "网络没通"; }
        }

        /// <summary>把 tag 清洗成纯版本号：v1.2.0 → 1.2.0，1.2.0-beta.1 → 1.2.0。</summary>
        internal static string Clean(string tag)
        {
            if (string.IsNullOrEmpty(tag)) return "";
            string s = tag.Trim();
            if (s.StartsWith("v", StringComparison.OrdinalIgnoreCase) ||
                s.StartsWith("V", StringComparison.OrdinalIgnoreCase)) s = s.Substring(1);
            if (s.StartsWith("release-", StringComparison.OrdinalIgnoreCase)) s = s.Substring(8);
            int cut = s.IndexOfAny(new[] { '-', '+', ' ' });
            if (cut > 0) s = s.Substring(0, cut);
            return s.Trim();
        }

        /// <summary>latest 是否比 current 新。解析不出来的版本号一律当作「不新」，不去骚扰用户。</summary>
        internal static bool NewerThan(string latest, Version current)
        {
            try
            {
                string s = Clean(latest);
                if (s.Length == 0) return false;
                // Version 需要至少两段：1 → 1.0
                if (s.IndexOf('.') < 0) s += ".0";
                var remote = new Version(s);
                if (current == null) return remote > new Version(0, 0);
                return remote > new Version(current.Major, current.Minor,
                    Math.Max(0, current.Build), Math.Max(0, current.Revision));
            }
            catch { return false; }
        }

        /// <summary>自检用：把版本比较的边界情况全跑一遍，防止「新版本来了却提示已是最新」。</summary>
        internal static string SelfTest()
        {
            var sb = new StringBuilder();
            var cur = new Version(1, 0, 0, 0);
            object[][] cases =
            {
                new object[] { "v1.0.1", true,  "补丁号变大算新" },
                new object[] { "1.1.0",  true,  "次版本变大算新" },
                new object[] { "v2.0.0", true,  "主版本变大算新" },
                new object[] { "v1.0.0", false, "同一版本不算新" },
                new object[] { "v0.9.9", false, "更旧的版本不算新" },
                new object[] { "v1.0.0-beta.1", false, "同号的预发布不算新" },
                new object[] { "v1.1.0-rc.2", true, "更高号的预发布算新" },
                new object[] { "2", true, "只写一段也能比" },
                new object[] { "", false, "空字符串不算新" },
                new object[] { "abc", false, "解析不了不算新" }
            };
            int pass = 0;
            foreach (var c in cases)
            {
                bool want = (bool)c[1];
                bool got = NewerThan((string)c[0], cur);
                bool ok = got == want;
                if (ok) pass++;
                sb.AppendLine((ok ? "PASS  " : "FAIL  ") + "版本比较 " + (string)c[0] + " → " + got +
                              "（期望 " + want + "，" + (string)c[2] + "）");
            }
            sb.AppendLine(pass + "/" + cases.Length + " 版本比较通过；当前版本 " + CurrentText +
                          "；仓库 " + (Enabled ? RepoSlug : "未配置"));
            return sb.ToString();
        }
    }
}
