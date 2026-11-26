using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using ProxyPicker.Domain.Models;
using ProxyPicker.Domain.Services;

namespace ProxyPicker.Infrastructure.Discovery
{
    /// <summary>
    /// 多代理客户端环境发现（Clash Verge Rev / FlClash / Sparkle / Nyanpasu / Mihomo Party / 裸 mihomo）、
    /// 系统代理状态读取、以及常见软件友好名称与已安装软件扫描。
    /// </summary>
    public sealed class ClientDiscoveryService
    {
        readonly object _cacheGate = new object();
        ClientEnvironment _cachedEnv;
        DateTime _cachedAtUtc = DateTime.MinValue;

        static readonly Dictionary<string, string> FriendlyNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["chrome.exe"] = "Google Chrome",
            ["msedge.exe"] = "Microsoft Edge",
            ["firefox.exe"] = "Mozilla Firefox",
            ["brave.exe"] = "Brave 浏览器",
            ["clash-verge.exe"] = "Clash Verge",
            ["flclash.exe"] = "FlClash",
            ["clash-nyanpasu.exe"] = "Clash Nyanpasu",
            ["mihomo party.exe"] = "Mihomo Party",
            ["sparkle.exe"] = "Sparkle",
            ["antigravity.exe"] = "Google Antigravity",
            ["cursor.exe"] = "Cursor",
            ["code.exe"] = "VS Code",
            ["windsurf.exe"] = "Windsurf",
            ["claude.exe"] = "Claude Desktop",
            ["chatgpt.exe"] = "ChatGPT",
            ["telegram.exe"] = "Telegram",
            ["discord.exe"] = "Discord",
            ["wechat.exe"] = "微信",
            ["weixin.exe"] = "微信",
            ["qq.exe"] = "QQ",
            ["steam.exe"] = "Steam",
            ["notion.exe"] = "Notion",
            ["obsidian.exe"] = "Obsidian",
            ["postman.exe"] = "Postman",
            ["git.exe"] = "Git",
            ["codex.exe"] = "OpenAI Codex"
        };

        public static string DisplayNameFor(string exeOrPath)
        {
            string exe = CompanionPolicyEngine.ExtractExeName(exeOrPath);
            if (exe.Length == 0) return "";
            string friendly;
            if (FriendlyNames.TryGetValue(exe, out friendly)) return friendly;
            string stem = Path.GetFileNameWithoutExtension(exe);
            return stem.Length > 0 ? char.ToUpperInvariant(stem[0]) + stem.Substring(1) : exe;
        }

        public ClientEnvironment Detect(bool forceRefresh = false)
        {
            lock (_cacheGate)
            {
                if (!forceRefresh && _cachedEnv != null && (DateTime.UtcNow - _cachedAtUtc).TotalSeconds < 5)
                    return _cachedEnv;
            }

            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            var candidates = new[]
            {
                Tuple.Create("Clash Verge Rev", Path.Combine(appData, "io.github.clash-verge-rev.clash-verge-rev"), "clash-verge.yaml"),
                Tuple.Create("Clash Verge", Path.Combine(appData, "clash-verge"), "clash-verge.yaml"),
                Tuple.Create("FlClash", Path.Combine(appData, "com.follow", "clash"), "config.yaml"),
                Tuple.Create("FlClash", Path.Combine(appData, "com.follow", "flclash"), "config.yaml"),
                Tuple.Create("Clash Nyanpasu", Path.Combine(appData, "clash-nyanpasu"), "clash-config.yaml"),
                Tuple.Create("Mihomo Party", Path.Combine(appData, "mihomo-party"), "config.yaml"),
                Tuple.Create("Sparkle", Path.Combine(appData, "sparkle"), "config.yaml"),
                Tuple.Create("Clash Party", Path.Combine(appData, "clash-party"), "config.yaml"),
                Tuple.Create("Mihomo", Path.Combine(userProfile, ".config", "mihomo"), "config.yaml"),
                Tuple.Create("Clash Verge Rev (Local)", Path.Combine(localAppData, "io.github.clash-verge-rev.clash-verge-rev"), "clash-verge.yaml")
            };

            var foundList = new List<Tuple<string, string, string, DateTime>>();
            foreach (var c in candidates)
            {
                string cfg = Path.Combine(c.Item2, c.Item3);
                if (File.Exists(cfg))
                {
                    DateTime lw = DateTime.MinValue;
                    try { lw = File.GetLastWriteTimeUtc(cfg); } catch { }
                    foundList.Add(Tuple.Create(c.Item1, c.Item2, cfg, lw));
                }
            }

            foundList.Sort((a, b) => b.Item4.CompareTo(a.Item4));
            var env = new ClientEnvironment();
            if (foundList.Count > 0)
            {
                var best = foundList[0];
                env.ClientName = best.Item1;
                env.DataDir = best.Item2;
                env.ConfigPath = best.Item3;
                string sec, ctrl;
                ReadControllerAndSecret(best.Item3, out sec, out ctrl);
                env.Secret = sec;
                env.Controller = ctrl;
                for (int i = 1; i < foundList.Count; i++)
                    env.OtherClientsDetected.Add(foundList[i].Item1);
            }

            lock (_cacheGate)
            {
                _cachedEnv = env;
                _cachedAtUtc = DateTime.UtcNow;
            }
            return env;
        }

        public static void ReadControllerAndSecret(string configPath, out string secret, out string controller)
        {
            secret = "";
            controller = "";
            if (string.IsNullOrEmpty(configPath) || !File.Exists(configPath)) return;
            try
            {
                foreach (var raw in File.ReadLines(configPath, Encoding.UTF8))
                {
                    if (raw.Length == 0 || raw[0] == ' ' || raw[0] == '\t' || raw[0] == '#') continue;
                    if (raw.StartsWith("external-controller:", StringComparison.Ordinal))
                        controller = StripInlineComment(raw.Substring("external-controller:".Length));
                    else if (raw.StartsWith("secret:", StringComparison.Ordinal))
                        secret = StripInlineComment(raw.Substring("secret:".Length));
                    if (controller.Length > 0 && secret.Length > 0) break;
                }
            }
            catch { }
        }

        public static void ReadSystemProxyState(out bool enabled, out string server)
        {
            enabled = false;
            server = "";
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings"))
                {
                    if (k == null) return;
                    object en = k.GetValue("ProxyEnable");
                    enabled = en is int i && i != 0;
                    server = Convert.ToString(k.GetValue("ProxyServer")) ?? "";
                }
            }
            catch { }
        }

        /// <summary>扫描常用软件与代理客户端安装路径（用于应用分流列表与四向联动下拉发现）。</summary>
        public static Dictionary<string, string> ScanInstalledApps()
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Action<string> tryAdd = path =>
            {
                string clean = (path ?? "").Trim().Trim('"');
                if (clean.Length == 0 || !File.Exists(clean)) return;
                string exe = CompanionPolicyEngine.ExtractExeName(clean);
                string err;
                if (!CompanionPolicyEngine.ValidateTargetExeOrPath(clean, true, out err)) return;
                if (!result.ContainsKey(exe)) result[exe] = clean;
            };

            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            string la = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            string[] wellKnown =
            {
                Path.Combine(pf, "Clash Verge", "clash-verge.exe"),
                @"D:\Clash Verge\clash-verge.exe",
                Path.Combine(la, "Programs", "clash-verge", "clash-verge.exe"),
                Path.Combine(pf, "FlClash", "FlClash.exe"),
                Path.Combine(pf, "Google", "Chrome", "Application", "chrome.exe"),
                Path.Combine(pf86, "Microsoft", "Edge", "Application", "msedge.exe"),
                Path.Combine(la, "Programs", "cursor", "Cursor.exe"),
                Path.Combine(la, "Programs", "Microsoft VS Code", "Code.exe"),
                Path.Combine(la, "Programs", "Antigravity", "Antigravity.exe")
            };
            foreach (var p in wellKnown) tryAdd(p);

            // 从 Windows App Paths 注册表补充已安装桌面软件
            foreach (var rootKey in new[] { Registry.CurrentUser, Registry.LocalMachine })
            {
                try
                {
                    using (var appPaths = rootKey.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths"))
                    {
                        if (appPaths == null) continue;
                        foreach (var sub in appPaths.GetSubKeyNames())
                        {
                            try
                            {
                                using (var sk = appPaths.OpenSubKey(sub))
                                {
                                    string val = Convert.ToString(sk?.GetValue(null)) ?? "";
                                    if (!string.IsNullOrWhiteSpace(val)) tryAdd(val);
                                }
                            }
                            catch { }
                        }
                    }
                }
                catch { }
            }
            return result;
        }

        static string StripInlineComment(string raw)
        {
            string s = (raw ?? "").Trim();
            if (s.StartsWith("\"", StringComparison.Ordinal) || s.StartsWith("'", StringComparison.Ordinal))
            {
                char q = s[0];
                int end = s.IndexOf(q, 1);
                return end > 0 ? s.Substring(1, end - 1).Trim() : s.Trim(q);
            }
            int hash = s.IndexOf(" #", StringComparison.Ordinal);
            if (hash >= 0) s = s.Substring(0, hash);
            return s.Trim();
        }
    }
}
