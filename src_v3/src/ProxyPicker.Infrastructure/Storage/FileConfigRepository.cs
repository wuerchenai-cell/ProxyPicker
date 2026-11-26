using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ProxyPicker.Domain.Interfaces;
using ProxyPicker.Domain.Json;
using ProxyPicker.Domain.Models;
using ProxyPicker.Domain.Services;
using ProxyPicker.Platform.Windows;

namespace ProxyPicker.Infrastructure.Storage
{
    /// <summary>
    /// 原子持久化仓库：100% 无缝兼容并自动迁移 v2.6.1 的 settings.json / pp-whitelist.yaml / static_proxy.dat，
    /// 并支持 v3.0 多固定 IP 通道池、命名场景预设及 PBKDF2+AES-256-HMAC 便携迁移包。
    /// </summary>
    public sealed class FileConfigRepository : IConfigRepository
    {
        readonly object _ioGate = new object();
        readonly RedactorService _redactor;
        readonly IAuditSink _audit;

        public string OwnDataDir { get; }
        public string SettingsPath => Path.Combine(OwnDataDir, "settings.json");
        public string WhitelistPath => Path.Combine(OwnDataDir, "pp-whitelist.yaml");
        public string LegacyStaticDatPath => Path.Combine(OwnDataDir, "static_proxy.dat");
        public string VaultDatPath => Path.Combine(OwnDataDir, "static_vault_v3.dat");
        public string BackupDir => Path.Combine(OwnDataDir, "backup");

        public FileConfigRepository(string ownDataDir, RedactorService redactor, IAuditSink audit)
        {
            OwnDataDir = ownDataDir;
            _redactor = redactor;
            _audit = audit;
            try { Directory.CreateDirectory(OwnDataDir); } catch { }
            try { Directory.CreateDirectory(BackupDir); } catch { }
        }

        public bool HasSavedRoutingIntent => File.Exists(WhitelistPath);

        public AppSettingsData LoadSettings()
        {
            AppSettingsData data;
            bool missing;
            if (TryReloadSettings(out data, out missing)) return data;
            return missing ? new AppSettingsData { Mode = SplitMode.Whitelist } : new AppSettingsData { Mode = SplitMode.Compat };
        }

        public bool TryReloadSettings(out AppSettingsData data, out bool fileMissing)
        {
            data = null;
            fileMissing = false;
            lock (_ioGate)
            {
                if (!File.Exists(SettingsPath))
                {
                    fileMissing = true;
                    return false;
                }
                try
                {
                    if (new FileInfo(SettingsPath).Length > 2 * 1024 * 1024)
                        throw new FormatException("settings.json 超过 2 MiB 上限");
                    string text;
                    using (var fs = new FileStream(SettingsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var sr = new StreamReader(fs, Encoding.UTF8))
                        text = sr.ReadToEnd();
                    var root = JsonCodec.ParseObject(text);
                    if (root == null) throw new FormatException("settings.json 根节点必须是对象");
                    data = DeserializeSettings(root);
                    return true;
                }
                catch (FileNotFoundException)
                {
                    return false;
                }
                catch (Exception ex)
                {
                    _audit?.ReportCrash(ex, "读取 settings.json 失败");
                    return false;
                }
            }
        }

        public bool SaveSettings(AppSettingsData settings, out string error)
        {
            error = "";
            if (settings == null) { error = "设置不能为空"; return false; }
            lock (_ioGate)
            {
                try
                {
                    string json = SerializeSettings(settings);
                    WriteAtomicUtf8(SettingsPath, json);
                    return true;
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    _audit?.ReportCrash(ex, "保存 settings.json 失败");
                    return false;
                }
            }
        }

        public List<AppRouteRule> LoadRouteRules()
        {
            var list = new List<AppRouteRule>();
            lock (_ioGate)
            {
                if (!File.Exists(WhitelistPath)) return list;
                try
                {
                    string[] lines = File.ReadAllLines(WhitelistPath, Encoding.UTF8);
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var raw in lines)
                    {
                        string t = (raw ?? "").Trim();
                        if (t.Length == 0 || t.StartsWith("#", StringComparison.Ordinal) || t.StartsWith("payload:", StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (t.StartsWith("-", StringComparison.Ordinal)) t = t.Substring(1).Trim();
                        t = t.Trim('\'', '"');
                        string[] parts = t.Split(',');
                        if (parts.Length < 2) continue;

                        string type = parts[0].Trim().ToUpperInvariant();
                        string target = parts[1].Trim();
                        string pol = parts.Length >= 3 ? parts[2].Trim() : "PP-FIXED";
                        if (string.IsNullOrWhiteSpace(target)) continue;

                        var rule = new AppRouteRule();
                        if (type == "PROCESS-PATH-REGEX") rule.PathRegex = target;
                        else if (type == "PROCESS-NAME") rule.Exe = YamlRuleEngine.NormalizeExe(target);
                        else continue;

                        string key = rule.IsPathRule ? ("P:" + rule.PathRegex) : ("N:" + rule.Exe);
                        if (!seen.Add(key)) continue;

                        if (YamlRuleEngine.IsDirectPolicy(pol))
                        {
                            rule.Channel = RouteChannel.Direct;
                        }
                        else if (pol.Equals("PP-STATIC-OUT", StringComparison.OrdinalIgnoreCase))
                        {
                            rule.Channel = RouteChannel.StaticChannel;
                            rule.StaticChannelId = StaticChannelProfile.DefaultChannelId;
                        }
                        else if (pol.StartsWith("PP-STATIC-OUT-", StringComparison.OrdinalIgnoreCase))
                        {
                            rule.Channel = RouteChannel.StaticChannel;
                            rule.StaticChannelId = pol.Substring("PP-STATIC-OUT-".Length);
                        }
                        else if (pol.Equals("PP-FIXED", StringComparison.OrdinalIgnoreCase) || pol.Length == 0)
                        {
                            rule.Channel = RouteChannel.PrimaryProxy;
                        }
                        else
                        {
                            rule.Channel = RouteChannel.PrimaryProxy;
                            rule.CustomPolicy = pol;
                        }
                        list.Add(rule);
                    }
                }
                catch (Exception ex)
                {
                    _audit?.ReportCrash(ex, "读取 pp-whitelist.yaml 失败");
                }
            }
            return list;
        }

        public bool SaveRouteRules(IList<AppRouteRule> rules, out string error)
        {
            error = "";
            lock (_ioGate)
            {
                try
                {
                    var sb = new StringBuilder();
                    sb.AppendLine("# ProxyPicker 分流名单（v3.0 兼容 v2 格式）");
                    sb.AppendLine("payload:");
                    if (rules != null)
                    {
                        foreach (var r in rules)
                        {
                            if (r == null || r.Derived || r.Channel == RouteChannel.Direct) continue;
                            string target = r.IsPathRule ? r.PathRegex : YamlRuleEngine.NormalizeExe(r.Exe);
                            if (string.IsNullOrWhiteSpace(target)) continue;
                            string type = r.IsPathRule ? "PROCESS-PATH-REGEX" : "PROCESS-NAME";
                            string pol;
                            if (!string.IsNullOrWhiteSpace(r.CustomPolicy)) pol = r.CustomPolicy.Trim();
                            else if (r.Channel == RouteChannel.StaticChannel)
                            {
                                string cid = string.IsNullOrWhiteSpace(r.StaticChannelId) ? StaticChannelProfile.DefaultChannelId : r.StaticChannelId.Trim();
                                pol = cid.Equals(StaticChannelProfile.DefaultChannelId, StringComparison.OrdinalIgnoreCase)
                                    ? "PP-STATIC-OUT"
                                    : ("PP-STATIC-OUT-" + StaticChannelProfile.SanitizeId(cid));
                            }
                            else pol = "PP-FIXED";
                            sb.Append("  - ").Append(type).Append(',').Append(target).Append(',').AppendLine(pol);
                        }
                    }
                    WriteAtomicUtf8(WhitelistPath, sb.ToString());
                    return true;
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    _audit?.ReportCrash(ex, "保存 pp-whitelist.yaml 失败");
                    return false;
                }
            }
        }

        public bool DeleteRouteRules(out string error)
        {
            error = "";
            lock (_ioGate)
            {
                try
                {
                    if (File.Exists(WhitelistPath)) File.Delete(WhitelistPath);
                    return true;
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    return false;
                }
            }
        }

        public StaticChannelVault LoadStaticVault()
        {
            lock (_ioGate)
            {
                StaticChannelVault vault = null;
                // 1. 优先读取 v3 多通道保险箱
                if (File.Exists(VaultDatPath))
                {
                    try
                    {
                        byte[] plain = NativeCryptoVault.UnprotectForCurrentUser(File.ReadAllBytes(VaultDatPath));
                        var root = JsonCodec.ParseObject(Encoding.UTF8.GetString(plain));
                        vault = DeserializeVault(root);
                    }
                    catch (Exception ex)
                    {
                        _audit?.ReportCrash(ex, "解密 static_vault_v3.dat 失败");
                    }
                }
                // 2. 向下兼容：自动无损读取 v2.x 的 static_proxy.dat 作为默认通道
                if (vault == null && File.Exists(LegacyStaticDatPath))
                {
                    try
                    {
                        byte[] plain = NativeCryptoVault.UnprotectForCurrentUser(File.ReadAllBytes(LegacyStaticDatPath));
                        var root = JsonCodec.ParseObject(Encoding.UTF8.GetString(plain));
                        var ch = DeserializeSingleStaticChannel(root, StaticChannelProfile.DefaultChannelId);
                        vault = new StaticChannelVault
                        {
                            ActiveDefaultChannelId = ch.Id,
                            Channels = new List<StaticChannelProfile> { ch }
                        };
                    }
                    catch (Exception ex)
                    {
                        _audit?.ReportCrash(ex, "迁移读取旧版 static_proxy.dat 失败，已安全降级");
                    }
                }
                if (vault == null)
                {
                    vault = new StaticChannelVault
                    {
                        ActiveDefaultChannelId = StaticChannelProfile.DefaultChannelId,
                        Channels = new List<StaticChannelProfile> { new StaticChannelProfile() }
                    };
                }
                _redactor?.RegisterVault(vault);
                return vault;
            }
        }

        public bool SaveStaticVault(StaticChannelVault vault, out string error)
        {
            error = "";
            if (vault == null) { error = "固定 IP 保险箱不能为空"; return false; }
            lock (_ioGate)
            {
                try
                {
                    var clean = vault.Clone();
                    if (clean.Channels.Count == 0) clean.Channels.Add(new StaticChannelProfile());
                    foreach (var ch in clean.Channels)
                    {
                        if (ch.Enabled && !ch.IsValid(out error)) return false;
                    }
                    string json = SerializeVault(clean);
                    byte[] enc = NativeCryptoVault.ProtectForCurrentUser(Encoding.UTF8.GetBytes(json));
                    WriteAtomicBytes(VaultDatPath, enc);
                    _redactor?.RegisterVault(clean);
                    return true;
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    _audit?.ReportCrash(ex, "保存 static_vault_v3.dat 失败");
                    return false;
                }
            }
        }

        public string ExportEncryptedBundle(string passphrase, bool includeStaticCredentials)
        {
            var settings = LoadSettings();
            var rules = LoadRouteRules();
            var bundleObj = new Dictionary<string, object>
            {
                ["schemaVersion"] = 3,
                ["exportedAtUtc"] = DateTime.UtcNow.ToString("O"),
                ["settings"] = JsonCodec.ParseObject(SerializeSettings(settings)),
                ["rules"] = rules.Select(SerializeRule).ToList()
            };
            if (includeStaticCredentials)
            {
                var vault = LoadStaticVault();
                bundleObj["staticVault"] = JsonCodec.ParseObject(SerializeVault(vault));
            }
            string rawJson = JsonCodec.Stringify(bundleObj, false);
            string cipher = NativeCryptoVault.EncryptPortable(rawJson, passphrase);
            var envelope = new Dictionary<string, object>
            {
                ["format"] = "ProxyPicker-Backup-v3",
                ["encrypted"] = true,
                ["includesStatic"] = includeStaticCredentials,
                ["payload"] = cipher
            };
            return JsonCodec.Stringify(envelope, true);
        }

        public bool ImportEncryptedBundle(string bundleJson, string passphrase, out string summary, out string error)
        {
            summary = "";
            error = "";
            try
            {
                var env = JsonCodec.ParseObject(bundleJson);
                if (env == null || !string.Equals(JsonCodec.Str(env, "format"), "ProxyPicker-Backup-v3", StringComparison.Ordinal))
                {
                    error = "不是有效的 ProxyPicker v3 迁移配置文件";
                    return false;
                }
                string plainJson = NativeCryptoVault.DecryptPortable(JsonCodec.Str(env, "payload"), passphrase);
                var dataObj = JsonCodec.ParseObject(plainJson);
                if (dataObj == null) { error = "解密后内容格式损坏"; return false; }

                int importedRules = 0, importedChannels = 0;
                var sObj = JsonCodec.Obj(dataObj, "settings");
                if (sObj != null)
                {
                    var st = DeserializeSettings(sObj);
                    if (!SaveSettings(st, out error)) return false;
                }
                var rArr = JsonCodec.Arr(dataObj, "rules");
                if (rArr != null)
                {
                    var rules = rArr.Select(x => DeserializeRule(JsonCodec.AsObject(x))).Where(r => r != null).ToList();
                    if (!SaveRouteRules(rules, out error)) return false;
                    importedRules = rules.Count;
                }
                var vObj = JsonCodec.Obj(dataObj, "staticVault");
                if (vObj != null)
                {
                    var vault = DeserializeVault(vObj);
                    if (vault != null)
                    {
                        if (!SaveStaticVault(vault, out error)) return false;
                        importedChannels = vault.Channels.Count;
                    }
                }
                summary = "已导入配置（" + importedRules + " 条分流规则" + (importedChannels > 0 ? (" · " + importedChannels + " 个固定 IP 通道") : "") + "）";
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        // ---- JSON 编解码（100% 兼容 v2.6.1 字段名） ----

        public static string SerializeSettings(AppSettingsData d)
        {
            var c = d.Companion ?? new CompanionConfig();
            var root = new Dictionary<string, object>
            {
                ["mode"] = d.Mode == SplitMode.Compat ? "patch" : "whitelist",
                ["theme"] = d.Theme.ToString().ToLowerInvariant(),
                ["ipv6GuardOn"] = d.Ipv6GuardOn,
                ["ipv6Original"] = d.Ipv6Original ?? "",
                ["findProcessOriginal"] = d.FindProcessOriginal ?? "",
                ["repairSuspended"] = d.RepairSuspended,
                ["loginWindowProxy"] = d.LoginWindowProxy,
                ["autoCheckUpdates"] = d.AutoCheckUpdates,
                ["autoFailoverEnabled"] = d.AutoFailoverEnabled,
                ["candidateNodes"] = d.CandidateNodes ?? new List<string>(),
                ["customApps"] = d.CustomApps ?? new List<string>(),
                ["hiddenApps"] = d.HiddenApps ?? new List<string>(),
                ["activePresetId"] = d.ActivePresetId ?? "",
                ["presets"] = (d.Presets ?? new List<WorkspacePreset>()).Select(SerializePreset).ToList(),
                ["followEnabled"] = c.FollowEnabled,
                ["followAppExe"] = c.FollowAppExe ?? "",
                ["followAppPath"] = c.FollowAppPath ?? "",
                ["followAppName"] = c.FollowAppName ?? "",
                ["followAutoExit"] = c.FollowAutoExit,
                ["followWatchAtLogon"] = c.FollowWatchAtLogon,
                ["launchAppsEnabled"] = c.LaunchAppsEnabled,
                ["launchAppsOnFollow"] = c.LaunchAppsOnFollow,
                ["launchAppPaths"] = c.LaunchAppPaths ?? new List<string>(),
                ["closeLinkedAppsOnExit"] = c.CloseLinkedAppsOnExit,
                ["gracefulCloseTimeoutMs"] = c.GracefulCloseTimeoutMs,
                ["winW"] = d.WindowWidth,
                ["winH"] = d.WindowHeight,
                ["compactMode"] = d.CompactMode,
                ["trayHintShown"] = d.TrayHintShown
            };
            return JsonCodec.Stringify(root, true);
        }

        public static AppSettingsData DeserializeSettings(Dictionary<string, object> root)
        {
            var d = new AppSettingsData();
            if (root == null) return d;
            string modeStr = JsonCodec.Str(root, "mode", "whitelist");
            d.Mode = modeStr.Equals("patch", StringComparison.OrdinalIgnoreCase) || modeStr.Equals("compat", StringComparison.OrdinalIgnoreCase)
                ? SplitMode.Compat : SplitMode.Whitelist;

            string themeStr = JsonCodec.Str(root, "theme", "system");
            if (themeStr.Equals("dark", StringComparison.OrdinalIgnoreCase)) d.Theme = ThemePreference.Dark;
            else if (themeStr.Equals("light", StringComparison.OrdinalIgnoreCase)) d.Theme = ThemePreference.Light;
            else d.Theme = ThemePreference.System;

            d.Ipv6GuardOn = JsonCodec.Bool(root, "ipv6GuardOn", false);
            d.Ipv6Original = JsonCodec.Str(root, "ipv6Original", "");
            d.FindProcessOriginal = JsonCodec.Str(root, "findProcessOriginal", "");
            d.RepairSuspended = JsonCodec.Bool(root, "repairSuspended", false);
            d.LoginWindowProxy = JsonCodec.Bool(root, "loginWindowProxy", true);
            d.AutoCheckUpdates = JsonCodec.Bool(root, "autoCheckUpdates", true);
            d.AutoFailoverEnabled = JsonCodec.Bool(root, "autoFailoverEnabled", false);
            d.CandidateNodes = ReadStrList(root, "candidateNodes");
            d.CustomApps = ReadStrList(root, "customApps");
            d.HiddenApps = ReadStrList(root, "hiddenApps");
            d.ActivePresetId = JsonCodec.Str(root, "activePresetId", "");
            d.WindowWidth = Math.Max(780, JsonCodec.Int(root, "winW", 1040));
            d.WindowHeight = Math.Max(540, JsonCodec.Int(root, "winH", 680));
            d.CompactMode = JsonCodec.Bool(root, "compactMode", false);
            d.TrayHintShown = JsonCodec.Bool(root, "trayHintShown", false);

            var pArr = JsonCodec.Arr(root, "presets");
            if (pArr != null)
            {
                foreach (var item in pArr)
                {
                    var p = DeserializePreset(JsonCodec.AsObject(item));
                    if (p != null) d.Presets.Add(p);
                }
            }

            d.Companion = new CompanionConfig
            {
                FollowEnabled = JsonCodec.Bool(root, "followEnabled", false),
                FollowAppExe = CompanionPolicyEngine.ExtractExeName(JsonCodec.Str(root, "followAppExe", "")),
                FollowAppPath = JsonCodec.Str(root, "followAppPath", ""),
                FollowAppName = JsonCodec.Str(root, "followAppName", ""),
                FollowAutoExit = JsonCodec.Bool(root, "followAutoExit", true),
                FollowWatchAtLogon = JsonCodec.Bool(root, "followWatchAtLogon", true),
                LaunchAppsEnabled = JsonCodec.Bool(root, "launchAppsEnabled", false),
                LaunchAppsOnFollow = JsonCodec.Bool(root, "launchAppsOnFollow", true),
                LaunchAppPaths = ReadStrList(root, "launchAppPaths"),
                CloseLinkedAppsOnExit = JsonCodec.Bool(root, "closeLinkedAppsOnExit", false),
                GracefulCloseTimeoutMs = Math.Max(1500, JsonCodec.Int(root, "gracefulCloseTimeoutMs", 5000))
            };
            return d;
        }

        static Dictionary<string, object> SerializeRule(AppRouteRule r)
        {
            return new Dictionary<string, object>
            {
                ["exe"] = r.Exe ?? "",
                ["pathRegex"] = r.PathRegex ?? "",
                ["channel"] = r.Channel.ToString(),
                ["staticChannelId"] = r.StaticChannelId ?? StaticChannelProfile.DefaultChannelId,
                ["customPolicy"] = r.CustomPolicy ?? ""
            };
        }

        static AppRouteRule DeserializeRule(Dictionary<string, object> o)
        {
            if (o == null) return null;
            RouteChannel ch = RouteChannel.PrimaryProxy;
            Enum.TryParse(JsonCodec.Str(o, "channel", "PrimaryProxy"), true, out ch);
            return new AppRouteRule
            {
                Exe = JsonCodec.Str(o, "exe", ""),
                PathRegex = JsonCodec.Str(o, "pathRegex", ""),
                Channel = ch,
                StaticChannelId = JsonCodec.Str(o, "staticChannelId", StaticChannelProfile.DefaultChannelId),
                CustomPolicy = JsonCodec.Str(o, "customPolicy", "")
            };
        }

        static Dictionary<string, object> SerializePreset(WorkspacePreset p)
        {
            return new Dictionary<string, object>
            {
                ["id"] = p.Id ?? "",
                ["name"] = p.Name ?? "",
                ["description"] = p.Description ?? "",
                ["mode"] = p.Mode == SplitMode.Compat ? "compat" : "whitelist",
                ["defaultStaticChannelId"] = p.DefaultStaticChannelId ?? StaticChannelProfile.DefaultChannelId,
                ["rules"] = (p.Rules ?? new List<AppRouteRule>()).Select(SerializeRule).ToList(),
                ["updatedAtUtc"] = p.UpdatedAtUtc.ToString("O")
            };
        }

        static WorkspacePreset DeserializePreset(Dictionary<string, object> o)
        {
            if (o == null) return null;
            var p = new WorkspacePreset
            {
                Id = JsonCodec.Str(o, "id", ""),
                Name = JsonCodec.Str(o, "name", ""),
                Description = JsonCodec.Str(o, "description", ""),
                Mode = JsonCodec.Str(o, "mode", "whitelist").Equals("compat", StringComparison.OrdinalIgnoreCase) ? SplitMode.Compat : SplitMode.Whitelist,
                DefaultStaticChannelId = JsonCodec.Str(o, "defaultStaticChannelId", StaticChannelProfile.DefaultChannelId)
            };
            var rArr = JsonCodec.Arr(o, "rules");
            if (rArr != null)
                foreach (var it in rArr) { var r = DeserializeRule(JsonCodec.AsObject(it)); if (r != null) p.Rules.Add(r); }
            return string.IsNullOrWhiteSpace(p.Name) ? null : p;
        }

        static string SerializeVault(StaticChannelVault v)
        {
            var root = new Dictionary<string, object>
            {
                ["activeDefaultChannelId"] = v.ActiveDefaultChannelId ?? StaticChannelProfile.DefaultChannelId,
                ["channels"] = (v.Channels ?? new List<StaticChannelProfile>()).Select(SerializeSingleStaticChannel).ToList()
            };
            return JsonCodec.Stringify(root, false);
        }

        static Dictionary<string, object> SerializeSingleStaticChannel(StaticChannelProfile ch)
        {
            return new Dictionary<string, object>
            {
                ["id"] = ch.Id ?? StaticChannelProfile.DefaultChannelId,
                ["name"] = ch.Name ?? "独立固定IP",
                ["enabled"] = ch.Enabled,
                ["protocol"] = ch.Protocol ?? "socks5",
                ["host"] = ch.Host ?? "",
                ["port"] = ch.Port,
                ["username"] = ch.Username ?? "",
                ["password"] = ch.Password ?? "",
                ["allowUdp"] = ch.AllowUdp,
                ["hopMode"] = ch.HopMode.ToString().ToLowerInvariant(),
                ["pinnedHopNode"] = ch.PinnedHopNode ?? "",
                ["autoBestHopNode"] = ch.AutoBestHopNode ?? "",
                ["autoBestDelayMs"] = ch.AutoBestDelayMs,
                ["autoBestUpdatedAtTicks"] = ch.AutoBestUpdatedAtTicks,
                ["autoBestHopPool"] = ch.AutoBestHopPool ?? new List<string>()
            };
        }

        static StaticChannelVault DeserializeVault(Dictionary<string, object> root)
        {
            if (root == null) return null;
            var arr = JsonCodec.Arr(root, "channels");
            if (arr == null) return null;
            var v = new StaticChannelVault
            {
                ActiveDefaultChannelId = JsonCodec.Str(root, "activeDefaultChannelId", StaticChannelProfile.DefaultChannelId)
            };
            foreach (var it in arr)
            {
                var ch = DeserializeSingleStaticChannel(JsonCodec.AsObject(it), StaticChannelProfile.DefaultChannelId);
                if (ch != null) v.Channels.Add(ch);
            }
            return v;
        }

        static StaticChannelProfile DeserializeSingleStaticChannel(Dictionary<string, object> o, string fallbackId)
        {
            if (o == null) return new StaticChannelProfile { Id = fallbackId };
            string hm = JsonCodec.Str(o, "hopMode", "auto").ToLowerInvariant();
            StaticHopMode hopMode = StaticHopMode.Auto;
            if (hm == "follow") hopMode = StaticHopMode.Follow;
            else if (hm == "pin") hopMode = StaticHopMode.Pin;
            else if (hm == "direct") hopMode = StaticHopMode.Direct;

            return new StaticChannelProfile
            {
                Id = string.IsNullOrWhiteSpace(JsonCodec.Str(o, "id", "")) ? fallbackId : JsonCodec.Str(o, "id", fallbackId),
                Name = !string.IsNullOrWhiteSpace(JsonCodec.Str(o, "name", "")) ? JsonCodec.Str(o, "name", "") : JsonCodec.Str(o, "label", "独立固定IP"),
                Enabled = JsonCodec.Bool(o, "enabled", false),
                Protocol = string.IsNullOrWhiteSpace(JsonCodec.Str(o, "protocol", "")) ? "socks5" : JsonCodec.Str(o, "protocol", "socks5"),
                Host = JsonCodec.Str(o, "host", ""),
                Port = JsonCodec.Int(o, "port", 0),
                Username = JsonCodec.Str(o, "username", ""),
                Password = JsonCodec.Str(o, "password", ""),
                AllowUdp = JsonCodec.Bool(o, "allowUdp", false),
                HopMode = hopMode,
                PinnedHopNode = JsonCodec.Str(o, "pinnedHopNode", ""),
                AutoBestHopNode = JsonCodec.Str(o, "autoBestHopNode", ""),
                AutoBestDelayMs = JsonCodec.Int(o, "autoBestDelayMs", -1),
                AutoBestUpdatedAtTicks = JsonCodec.Long(o, "autoBestUpdatedAtTicks", 0),
                AutoBestHopPool = ReadStrList(o, "autoBestHopPool")
            };
        }

        static List<string> ReadStrList(Dictionary<string, object> o, string key)
        {
            var res = new List<string>();
            var arr = JsonCodec.Arr(o, key);
            if (arr == null) return res;
            foreach (var it in arr)
            {
                string s = it != null ? it.ToString().Trim() : "";
                if (s.Length > 0) res.Add(s);
            }
            return res;
        }

        static void WriteAtomicUtf8(string path, string content)
        {
            WriteAtomicBytes(path, new UTF8Encoding(false).GetBytes(content ?? ""));
        }

        static void WriteAtomicBytes(string path, byte[] data)
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            string tmp = path + ".tmp." + Guid.NewGuid().ToString("N").Substring(0, 8);
            try
            {
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    fs.Write(data, 0, data.Length);
                    fs.Flush(true);
                }
                if (File.Exists(path))
                {
                    try { File.Replace(tmp, path, null, true); }
                    catch
                    {
                        File.Copy(tmp, path, true);
                        try { File.Delete(tmp); } catch { }
                    }
                }
                else File.Move(tmp, path);
            }
            finally
            {
                if (File.Exists(tmp)) { try { File.Delete(tmp); } catch { } }
            }
        }
    }
}
