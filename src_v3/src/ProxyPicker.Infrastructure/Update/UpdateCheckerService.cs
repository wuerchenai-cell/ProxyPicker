using System;
using System.IO;
using System.Net;
using System.Text;
using ProxyPicker.Domain;
using ProxyPicker.Domain.Interfaces;
using ProxyPicker.Domain.Json;
using ProxyPicker.Domain.Models;
using ProxyPicker.Domain.Services;

namespace ProxyPicker.Infrastructure.Update
{
    /// <summary>
    /// 只读 GitHub Release 更新检查服务：
    /// 1) 严禁自动静默下载或自覆盖 exe（避免自签名二进制触发杀软误报）；
    /// 2) 当机场共享出口命中 GitHub API 403/429 匿名限流时，自动回退至网页 releases/latest 302 Location 解析；
    /// 3) 强制校验返回链接属于官方 HTTPS github.com 仓库路径（防响应篡改）。
    /// </summary>
    public sealed class UpdateCheckerService : IUpdateChecker
    {
        readonly string _repoSlug;

        public UpdateCheckerService(string repoSlug = null)
        {
            _repoSlug = string.IsNullOrWhiteSpace(repoSlug) ? ProductInfo.RepositorySlug : repoSlug.Trim('/');
        }

        public string SafeReleasesUrl => "https://github.com/" + _repoSlug + "/releases";

        public bool IsSafeReleaseUrl(string url) => VersionComparer.IsSafeReleaseUrl(url, _repoSlug);

        public UpdateCheckResult CheckLatest(int timeoutMs = 6000)
        {
            var res = new UpdateCheckResult { CurrentVersion = ProductInfo.Version };
            if (string.IsNullOrWhiteSpace(_repoSlug) || _repoSlug.IndexOf('/') <= 0)
            {
                res.Error = "未配置官方仓库地址";
                return res;
            }

            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
            try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)12288; } catch { }

            try
            {
                var req = (HttpWebRequest)WebRequest.Create("https://api.github.com/repos/" + _repoSlug + "/releases/latest");
                req.Method = "GET";
                req.Timeout = timeoutMs;
                req.ReadWriteTimeout = timeoutMs;
                req.UserAgent = "ProxyPicker/" + ProductInfo.Version;
                req.Accept = "application/vnd.github+json";
                req.KeepAlive = false;
                req.AllowAutoRedirect = false;
                req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;

                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    var root = JsonCodec.AsObject(JsonCodec.Parse(sr.ReadToEnd()));
                    string tag = JsonCodec.Str(root, "tag_name");
                    if (string.IsNullOrEmpty(tag)) { res.Error = "发布信息中缺少版本号"; return res; }
                    res.Ok = true;
                    res.LatestVersion = VersionComparer.CleanTag(tag);
                    string htmlUrl = JsonCodec.Str(root, "html_url");
                    res.ReleaseUrl = IsSafeReleaseUrl(htmlUrl) ? htmlUrl.Trim() : SafeReleasesUrl;
                    string pub = JsonCodec.Str(root, "published_at");
                    if (pub.Length >= 10) res.PublishedDate = pub.Substring(0, 10);
                    res.HasNewer = VersionComparer.IsNewer(res.LatestVersion, ProductInfo.Version);
                    return res;
                }
            }
            catch (WebException wex)
            {
                int code = ExtractStatusCode(wex);
                if ((code == 403 || code == 429) && TryFallbackViaRedirect(timeoutMs, res))
                    return res;
                res.Error = DescribeError(wex, code);
            }
            catch (Exception ex)
            {
                res.Error = ex.Message;
            }
            return res;
        }

        public bool TryParseRedirectRelease(string location, out string version, out string safeUrl)
        {
            version = "";
            safeUrl = "";
            if (string.IsNullOrWhiteSpace(location)) return false;
            string loc = location.Trim();
            if (loc.StartsWith("/", StringComparison.Ordinal)) loc = "https://github.com" + loc;
            if (!IsSafeReleaseUrl(loc)) return false;

            Uri uri;
            if (!Uri.TryCreate(loc, UriKind.Absolute, out uri)) return false;
            string prefix = "/" + _repoSlug + "/releases/tag/";
            string path = uri.AbsolutePath ?? "";
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
            string rawTag = path.Substring(prefix.Length).Trim('/');
            if (rawTag.Length == 0 || rawTag.IndexOf('/') >= 0) return false;
            try { rawTag = Uri.UnescapeDataString(rawTag); } catch { }
            string clean = VersionComparer.CleanTag(rawTag);
            if (clean.Length == 0) return false;
            version = clean;
            safeUrl = "https://github.com" + path;
            return true;
        }

        bool TryFallbackViaRedirect(int timeoutMs, UpdateCheckResult res)
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(SafeReleasesUrl + "/latest");
                req.Method = "HEAD";
                req.Timeout = timeoutMs;
                req.ReadWriteTimeout = timeoutMs;
                req.UserAgent = "ProxyPicker/" + ProductInfo.Version;
                req.KeepAlive = false;
                req.AllowAutoRedirect = false;

                string loc = null;
                try
                {
                    using (var resp = (HttpWebResponse)req.GetResponse())
                    {
                        int st = (int)resp.StatusCode;
                        if (st >= 300 && st < 400) loc = resp.Headers["Location"];
                    }
                }
                catch (WebException wex)
                {
                    using (var errResp = wex.Response as HttpWebResponse)
                    {
                        if (errResp != null && (int)errResp.StatusCode >= 300 && (int)errResp.StatusCode < 400)
                            loc = errResp.Headers["Location"];
                    }
                }

                string ver, url;
                if (!TryParseRedirectRelease(loc, out ver, out url)) return false;
                res.Ok = true;
                res.LatestVersion = ver;
                res.ReleaseUrl = url;
                res.HasNewer = VersionComparer.IsNewer(ver, ProductInfo.Version);
                res.Error = "";
                return true;
            }
            catch { return false; }
        }

        static int ExtractStatusCode(WebException wex)
        {
            try
            {
                using (var resp = wex?.Response as HttpWebResponse)
                    return resp != null ? (int)resp.StatusCode : 0;
            }
            catch { return 0; }
        }

        static string DescribeError(WebException wex, int code)
        {
            if (code == 404) return "仓库暂无已发布版本";
            if (code == 403 || code == 429) return "GitHub 访问频率受限，请稍后再试";
            if (code >= 500) return "GitHub 服务暂时不可用";
            if (code > 0) return "远端返回 HTTP " + code;
            return wex.Status == WebExceptionStatus.Timeout ? "连接 GitHub 超时" : "无法连接 GitHub（未开启代理时属正常）";
        }
    }
}
