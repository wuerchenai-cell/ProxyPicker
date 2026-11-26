using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using ProxyPicker.Domain.Interfaces;
using ProxyPicker.Domain.Json;
using ProxyPicker.Domain.Models;
using ProxyPicker.Infrastructure.Discovery;
using ProxyPicker.Platform.Windows;

namespace ProxyPicker.Infrastructure.Mihomo
{
    /// <summary>
    /// 安全 Mihomo 控制客户端实现：
    /// 1) 命名管道直连优先（强制校验服务端 PID 必须为内核可执行文件）；
    /// 2) 纯回环 HTTP 回退，严格经 SecretGate 校验监听身份（Other 拒绝、Unknown 须无密钥首探确认 mihomo JSON 特征）；
    /// 3) 全量防 CRLF 请求头注入与 16 MiB 响应上限保护。
    /// </summary>
    public sealed class MihomoClient : IMihomoClient
    {
        readonly object _channelGate = new object();
        readonly ClientDiscoveryService _discovery;

        string _cachedPipe;
        string _cachedEndpoint;
        bool _cachedEndpointGuessed;
        string _verifiedUnknownHintEp;
        string _cachedSecret;
        string _boundConfigPath;
        string _securityError;

        public MihomoClient(ClientDiscoveryService discovery)
        {
            _discovery = discovery;
        }

        public static string CleanHeaderSecret(string secret)
        {
            if (string.IsNullOrEmpty(secret)) return "";
            for (int i = 0; i < secret.Length; i++)
                if (secret[i] == '\r' || secret[i] == '\n' || secret[i] == '\0') return "";
            return secret;
        }

        public static bool LooksLikeMihomoVersion(string body)
        {
            if (string.IsNullOrEmpty(body) || body.Length > 4096) return false;
            try
            {
                var root = JsonCodec.ParseObject(body);
                if (root == null || string.IsNullOrEmpty(JsonCodec.Str(root, "version"))) return false;
                object meta;
                return root.TryGetValue("meta", out meta) && meta is bool b && b;
            }
            catch { return false; }
        }

        /// <summary>校验 401/403 响应体是否为标准 mihomo JSON 未授权响应（防 Unknown 监听者盲发 Bearer）。</summary>
        public static bool LooksLikeMihomoUnauthorized(int status, string body, string wwwAuthenticate)
        {
            if (status != 401 && status != 403) return false;
            if (!string.IsNullOrWhiteSpace(wwwAuthenticate) &&
                !wwwAuthenticate.Trim().StartsWith("Bearer", StringComparison.OrdinalIgnoreCase))
                return false;
            if (string.IsNullOrWhiteSpace(body) || body.Length > 1024) return false;
            try
            {
                var root = JsonCodec.ParseObject(body);
                if (root == null) return false;
                string msg = JsonCodec.Str(root, "message", "");
                return msg.IndexOf("unauthorized", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch { return false; }
        }

        public static bool AllowSecretForVerdict(ListenerKind verdict, bool mihomoHintVerified)
        {
            if (verdict == ListenerKind.Kernel) return true;
            if (verdict == ListenerKind.Other) return false;
            return mihomoHintVerified;
        }

        public static bool TryNormalizeLoopbackEndpoint(string rawController, out string normalizedEp, out int port, out string error)
        {
            normalizedEp = "";
            port = 0;
            error = "";
            string s = (rawController ?? "").Trim().Trim('"', '\'');
            if (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) s = s.Substring(7);
            int slash = s.IndexOf('/');
            if (slash >= 0) s = s.Substring(0, slash);
            int colon = s.LastIndexOf(':');
            if (colon < 0 || !int.TryParse(s.Substring(colon + 1), out port) || port < 1 || port > 65535)
            {
                error = "无效端口";
                return false;
            }
            string host = colon == 0 ? "127.0.0.1" : s.Substring(0, colon).Trim('[', ']');
            if (host == "0.0.0.0" || host == "::" || host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
                host = "127.0.0.1";
            IPAddress ip;
            if (!IPAddress.TryParse(host, out ip) || !IPAddress.IsLoopback(ip))
            {
                error = "非本机回环控制地址（" + host + "），禁止发送控制密钥";
                return false;
            }
            normalizedEp = "127.0.0.1:" + port;
            return true;
        }

        public MihomoApiResponse FetchJson(string path, int timeoutMs, string knownConfigPath = null)
        {
            var r = Send("GET", path, null, timeoutMs, knownConfigPath);
            if (r.Ok && !string.IsNullOrWhiteSpace(r.Body))
            {
                try { r.Data = JsonCodec.ParseObject(r.Body); }
                catch (Exception ex) { r.Error = "内核返回非 JSON 对象：" + ex.Message; }
            }
            return r;
        }

        public MihomoApiResponse PutConfigPayload(string yamlPayload, int timeoutMs, string knownConfigPath = null)
        {
            string body = "{\"payload\":" + JsonCodec.Quote(yamlPayload ?? "") + "}";
            return Send("PUT", "/configs?force=true", body, timeoutMs, knownConfigPath);
        }

        public MihomoApiResponse PatchConfigJson(string jsonBody, int timeoutMs, string knownConfigPath = null)
        {
            return Send("PATCH", "/configs", jsonBody, timeoutMs, knownConfigPath);
        }

        public MihomoApiResponse SwitchProxyNode(string groupName, string nodeName, int timeoutMs, string knownConfigPath = null)
        {
            string path = "/proxies/" + Uri.EscapeDataString(groupName ?? "");
            string body = "{\"name\":" + JsonCodec.Quote(nodeName ?? "") + "}";
            return Send("PUT", path, body, timeoutMs, knownConfigPath);
        }

        public MihomoApiResponse DeleteConnection(string connectionId, int timeoutMs, string knownConfigPath = null)
        {
            return Send("DELETE", "/connections/" + Uri.EscapeDataString(connectionId ?? ""), null, timeoutMs, knownConfigPath);
        }

        public MihomoApiResponse MeasureProxyDelay(string proxyName, string testUrl, int timeoutMs, string knownConfigPath = null)
        {
            string url = "/proxies/" + Uri.EscapeDataString(proxyName ?? "") +
                         "/delay?timeout=" + Math.Max(1000, timeoutMs) +
                         "&url=" + Uri.EscapeDataString(string.IsNullOrEmpty(testUrl) ? "http://www.gstatic.com/generate_204" : testUrl);
            return FetchJson(url, timeoutMs + 1500, knownConfigPath);
        }

        public ControlOfflineState CheckControlOffline(string knownConfigPath = null)
        {
            var st = new ControlOfflineState();
            lock (_channelGate)
            {
                ClearCacheUnlocked();
                var env = ResolveEnv(knownConfigPath);
                var pipes = NativePipeAndTcp.EnumerateControlPipes();
                if (pipes.Count > 0)
                {
                    var r = TryPipeUnlocked(pipes[0], "GET", "/version", null, 1500, env.Secret);
                    if (r != null && r.Status > 0)
                    {
                        st.ObservedStatus = r.Status;
                        st.Detail = "控制管道仍在响应（HTTP " + r.Status + "）";
                        return st;
                    }
                }
                string ep, err;
                int port;
                if (!string.IsNullOrEmpty(env.Controller) && TryNormalizeLoopbackEndpoint(env.Controller, out ep, out port, out err))
                {
                    bool tblOk; int pid;
                    if (NativePipeAndTcp.IsLoopbackPortListening(port, out tblOk, out pid))
                    {
                        st.ObservedStatus = 200;
                        st.Detail = "控制端口 " + ep + " 仍有监听进程";
                        return st;
                    }
                }
                st.OfflineConfirmed = true;
                st.Detail = "无活跃控制管道与控制端口";
                return st;
            }
        }

        public void ForgetCache()
        {
            lock (_channelGate) ClearCacheUnlocked();
        }

        void ClearCacheUnlocked()
        {
            _cachedPipe = null;
            _cachedEndpoint = null;
            _cachedEndpointGuessed = false;
            _verifiedUnknownHintEp = null;
            _cachedSecret = null;
            _boundConfigPath = null;
            _securityError = null;
        }

        ClientEnvironment ResolveEnv(string knownConfigPath)
        {
            if (!string.IsNullOrEmpty(knownConfigPath))
            {
                string sec, ctrl;
                ClientDiscoveryService.ReadControllerAndSecret(knownConfigPath, out sec, out ctrl);
                return new ClientEnvironment { ConfigPath = knownConfigPath, Secret = sec, Controller = ctrl };
            }
            return _discovery.Detect();
        }

        MihomoApiResponse Send(string method, string path, string body, int timeoutMs, string knownConfigPath)
        {
            var sw = Stopwatch.StartNew();
            lock (_channelGate)
            {
                var env = ResolveEnv(knownConfigPath);
                if (!string.Equals(_boundConfigPath, env.ConfigPath, StringComparison.OrdinalIgnoreCase))
                {
                    ClearCacheUnlocked();
                    _boundConfigPath = env.ConfigPath;
                    _cachedSecret = env.Secret;
                }

                // 1. 优先走命名管道直连（Clash Verge / FlClash / Sparkle）
                if (_cachedPipe == null)
                {
                    var pipes = NativePipeAndTcp.EnumerateControlPipes();
                    if (pipes.Count == 1) _cachedPipe = pipes[0];
                    else if (pipes.Count > 1)
                    {
                        foreach (var p in pipes)
                        {
                            if (!string.IsNullOrEmpty(env.ClientName) &&
                                env.ClientName.IndexOf("Verge", StringComparison.OrdinalIgnoreCase) >= 0 &&
                                p.StartsWith("verge-mihomo", StringComparison.OrdinalIgnoreCase))
                            { _cachedPipe = p; break; }
                        }
                    }
                }
                if (_cachedPipe != null)
                {
                    var pr = TryPipeUnlocked(_cachedPipe, method, path, body, timeoutMs, _cachedSecret);
                    if (pr != null && (pr.Status > 0 || !string.IsNullOrEmpty(_securityError)))
                    {
                        pr.ElapsedMs = sw.ElapsedMilliseconds;
                        return pr;
                    }
                    _cachedPipe = null;
                }

                // 2. 回退纯本机回环 HTTP（带严格 SecretGate 校验）
                if (_cachedEndpoint == null)
                {
                    string ep, normErr;
                    int port;
                    if (!string.IsNullOrEmpty(env.Controller))
                    {
                        if (!TryNormalizeLoopbackEndpoint(env.Controller, out ep, out port, out normErr))
                        {
                            _securityError = normErr;
                            return new MihomoApiResponse { Error = normErr, ElapsedMs = sw.ElapsedMilliseconds };
                        }
                        if (ProbeHttpEndpointUnlocked(ep, port, true, _cachedSecret))
                        {
                            _cachedEndpoint = ep;
                            _cachedEndpointGuessed = false;
                        }
                    }
                }

                if (_cachedEndpoint == null)
                {
                    return new MihomoApiResponse
                    {
                        Error = !string.IsNullOrEmpty(_securityError) ? _securityError : "未检测到运行中的 mihomo / Clash 控制接口",
                        ElapsedMs = sw.ElapsedMilliseconds
                    };
                }

                if (_cachedEndpointGuessed && !(method == "GET" || method == "HEAD"))
                    return new MihomoApiResponse { Error = "猜测端口拒绝写操作", ElapsedMs = sw.ElapsedMilliseconds };

                var hr = ExecuteHttpRequest(_cachedEndpoint, method, path, body, timeoutMs, _cachedEndpointGuessed ? "" : _cachedSecret);
                if (hr.Status == 0) _cachedEndpoint = null;
                hr.ElapsedMs = sw.ElapsedMilliseconds;
                return hr;
            }
        }

        bool ProbeHttpEndpointUnlocked(string ep, int port, bool allowSecret, string secret)
        {
            bool unauthHint;
            int st1 = ProbeVersionOnce(ep, "", out unauthHint);
            if (st1 == 200) { _verifiedUnknownHintEp = ep; return true; }
            if (!allowSecret || (st1 != 401 && st1 != 403) || string.IsNullOrEmpty(secret)) return false;

            string detail;
            var verdict = NativePipeAndTcp.CheckListenerIdentity(port, out detail);
            if (!AllowSecretForVerdict(verdict, unauthHint))
            {
                _securityError = verdict == ListenerKind.Other
                    ? ("控制端口 " + ep + " 监听进程不是 mihomo：" + detail + "；已阻止发送密钥")
                    : ("控制端口 " + ep + " 监听身份未知且未呈 mihomo 特征；已阻止盲发密钥");
                return false;
            }
            bool _;
            bool ok = ProbeVersionOnce(ep, secret, out _) == 200;
            if (ok) _verifiedUnknownHintEp = ep;
            return ok;
        }

        static int ProbeVersionOnce(string ep, string secret, out bool mihomoUnauthHint)
        {
            mihomoUnauthHint = false;
            try
            {
                var req = (HttpWebRequest)WebRequest.Create("http://" + ep + "/version");
                req.Method = "GET";
                req.Timeout = 1000;
                req.ReadWriteTimeout = 1000;
                req.Proxy = null;
                req.KeepAlive = false;
                req.AllowAutoRedirect = false;
                string sec = CleanHeaderSecret(secret);
                if (!string.IsNullOrEmpty(sec)) req.Headers["Authorization"] = "Bearer " + sec;
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    string body = sr.ReadToEnd();
                    return LooksLikeMihomoVersion(body) ? 200 : 502;
                }
            }
            catch (WebException we)
            {
                if (we.Response is HttpWebResponse resp)
                {
                    int st = (int)resp.StatusCode;
                    string wa = "";
                    string body = "";
                    try { wa = resp.Headers["WWW-Authenticate"] ?? ""; } catch { }
                    try { using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8)) body = sr.ReadToEnd(); } catch { }
                    resp.Close();
                    mihomoUnauthHint = LooksLikeMihomoUnauthorized(st, body, wa);
                    return st;
                }
                return 0;
            }
            catch { return 0; }
        }

        MihomoApiResponse TryPipeUnlocked(string pipeName, string method, string path, string body, int timeoutMs, string secret)
        {
            byte[] payload = Encoding.UTF8.GetBytes(body ?? "");
            var sb = new StringBuilder();
            sb.Append(method).Append(' ').Append(path).Append(" HTTP/1.0\r\n");
            sb.Append("Host: localhost\r\n");
            string sec = CleanHeaderSecret(secret);
            if (!string.IsNullOrEmpty(sec)) sb.Append("Authorization: Bearer ").Append(sec).Append("\r\n");
            if (payload.Length > 0)
            {
                sb.Append("Content-Type: application/json\r\n");
                sb.Append("Content-Length: ").Append(payload.Length).Append("\r\n");
            }
            sb.Append("\r\n");

            byte[] head = Encoding.ASCII.GetBytes(sb.ToString());
            byte[] all = new byte[head.Length + payload.Length];
            Buffer.BlockCopy(head, 0, all, 0, head.Length);
            if (payload.Length > 0) Buffer.BlockCopy(payload, 0, all, head.Length, payload.Length);

            int pid;
            byte[] respBytes;
            string err;
            if (!NativePipeAndTcp.TrySendPipeHttp(pipeName, all, timeoutMs, out pid, out respBytes, out err))
            {
                if (!string.IsNullOrEmpty(err) && err.Contains("阻止发送"))
                    _securityError = err;
                return null;
            }

            string text = Encoding.UTF8.GetString(respBytes ?? new byte[0]);
            var res = new MihomoApiResponse();
            int sep = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            string headTxt = sep < 0 ? text : text.Substring(0, sep);
            res.Body = sep < 0 ? "" : text.Substring(sep + 4);
            string[] firstParts = headTxt.Split(new[] { "\r\n" }, StringSplitOptions.None)[0].Split(' ');
            int status;
            if (firstParts.Length >= 2 && int.TryParse(firstParts[1], out status)) res.Status = status;
            else res.Error = "命名管道返回无效 HTTP 响应";
            return res;
        }

        static MihomoApiResponse ExecuteHttpRequest(string ep, string method, string path, string body, int timeoutMs, string secret)
        {
            var r = new MihomoApiResponse();
            try
            {
                var req = (HttpWebRequest)WebRequest.Create("http://" + ep + path);
                req.Method = method;
                req.Timeout = Math.Max(1500, timeoutMs);
                req.ReadWriteTimeout = Math.Max(1500, timeoutMs);
                req.Proxy = null;
                req.KeepAlive = false;
                req.AllowAutoRedirect = false;
                string sec = CleanHeaderSecret(secret);
                if (!string.IsNullOrEmpty(sec)) req.Headers["Authorization"] = "Bearer " + sec;
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
                if (we.Response is HttpWebResponse resp)
                {
                    r.Status = (int)resp.StatusCode;
                    try { using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8)) r.Body = sr.ReadToEnd(); } catch { }
                    resp.Close();
                    r.Error = "HTTP " + r.Status;
                }
                else r.Error = we.Message;
            }
            catch (Exception ex) { r.Error = ex.Message; }
            return r;
        }
    }
}
