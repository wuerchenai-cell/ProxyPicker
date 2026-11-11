using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ProxyPicker.Core
{
    /// <summary>
    /// v1.0 / v1.1 时代曾把规则写进 Clash Verge 的 profile 扩展链（Rules / Groups / Merge），
    /// 后来改成运行时 PUT /configs。老扩展如果不清掉，客户端一重载配置就会把
    /// 「整机白名单短路」重新注入，退出/卸载都像没还原一样（第 18 轮复审 F-02）。
    ///
    /// 这里只处理带明确 ProxyPicker 标记的扩展文件，并且清理前先整份备份；
    /// 用户自己写的扩展、名字碰巧叫 PP-FIXED 的文件一律不动。
    /// </summary>
    internal static class LegacyCleanup
    {
        public sealed class Hit
        {
            public string Uid = "";
            public string File = "";
            public string Kind = "";
        }

        static string ProfilesYaml
        {
            get
            {
                string dir = Discovery.Detect().DataDir;
                if (string.IsNullOrEmpty(dir)) return "";
                return Path.Combine(dir, "profiles.yaml");
            }
        }

        static string ProfilesDir
        {
            get
            {
                string dir = Discovery.Detect().DataDir;
                if (string.IsNullOrEmpty(dir)) return "";
                return Path.Combine(dir, "profiles");
            }
        }

        /// <summary>只读检测；返回给人看的报告，不改任何文件。</summary>
        public static string Report(out List<Hit> hits)
        {
            hits = new List<Hit>();
            var sb = new StringBuilder();
            string list = ProfilesYaml;
            sb.AppendLine("旧版 profile 注入检测");
            sb.AppendLine("profiles.yaml: " + (string.IsNullOrEmpty(list) ? "（没找到客户端数据目录）" : list));
            if (string.IsNullOrEmpty(list) || !File.Exists(list))
            {
                sb.AppendLine("结果：没有可检测的客户端 profile 链");
                return sb.ToString();
            }

            string dir = ProfilesDir;
            if (Directory.Exists(dir))
            {
                foreach (var f in Directory.GetFiles(dir, "*.yaml"))
                {
                    string text;
                    try { text = File.ReadAllText(f, Encoding.UTF8); }
                    catch { continue; }
                    string kind = DetectKind(text);
                    if (kind.Length == 0) continue;
                    hits.Add(new Hit
                    {
                        Uid = Path.GetFileNameWithoutExtension(f),
                        File = f,
                        Kind = kind
                    });
                }
            }

            if (hits.Count == 0)
            {
                sb.AppendLine("结果：没有发现带 ProxyPicker 标记的旧扩展");
                return sb.ToString();
            }

            sb.AppendLine("发现 " + hits.Count + " 个旧版扩展（清理前会整份备份）：");
            foreach (var h in hits)
                sb.AppendLine("  " + h.Uid + "  [" + h.Kind + "]  " + h.File);
            sb.AppendLine("清理：ProxyPicker.exe --cleanlegacy 结果.txt --yes");
            sb.AppendLine("注意：清理后请重启客户端，让它重新生成配置。");
            return sb.ToString();
        }

        static string DetectKind(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            // 必须带本工具的明确标记，避免误删用户自己写的同名组/扩展。
            bool ours = text.IndexOf("ProxyPicker", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        text.IndexOf("pp-whitelist", StringComparison.OrdinalIgnoreCase) >= 0;
            if (!ours) return "";
            if (text.IndexOf("RULE-SET,pp-whitelist", StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.IndexOf("Rules 扩展", StringComparison.OrdinalIgnoreCase) >= 0) return "规则扩展";
            if (text.IndexOf("name: PP-FIXED", StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.IndexOf("出口组", StringComparison.OrdinalIgnoreCase) >= 0) return "PP-FIXED 组";
            if (text.IndexOf("rule-providers:", StringComparison.OrdinalIgnoreCase) >= 0 &&
                text.IndexOf("pp-whitelist", StringComparison.OrdinalIgnoreCase) >= 0) return "Merge/provider";
            // 独立审阅第二轮：只出现软件名不能作为删除依据（用户自己写的扩展里
            // 一句提到 ProxyPicker 的注释也会命中）。没有可证明来源的历史结构就
            // 返回空＝不列入清理，留给人工核查；宁可漏清，绝不误删用户文件。
            return "";
        }

        /// <summary>备份后删除旧扩展，并从 profiles.yaml 的 option 链里摘掉引用。</summary>
        public static bool Clean(out string message)
        {
            message = "";
            List<Hit> hits;
            Report(out hits);
            if (hits.Count == 0) { message = "没有需要清理的旧版扩展"; return true; }

            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string backup = Path.Combine(Paths.BackupDir, "legacy-" + stamp);
            try { Directory.CreateDirectory(backup); }
            catch (Exception ex) { message = "创建备份目录失败：" + ex.Message; return false; }

            string list = ProfilesYaml;
            try
            {
                File.Copy(list, Path.Combine(backup, "profiles.yaml"), true);
                foreach (var h in hits)
                    File.Copy(h.File, Path.Combine(backup, Path.GetFileName(h.File)), true);
            }
            catch (Exception ex) { message = "备份失败，已停止：" + ex.Message; return false; }

            var uids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var h in hits) uids.Add(h.Uid);

            try
            {
                // 1) 从 profiles.yaml 里删掉 merge/rules/groups 指向这些 uid 的行。
                var kept = new List<string>();
                foreach (var line in File.ReadAllLines(list, Encoding.UTF8))
                {
                    string t = line.Trim();
                    bool drop = false;
                    foreach (var uid in uids)
                    {
                        if (t.Equals("merge: " + uid, StringComparison.OrdinalIgnoreCase) ||
                            t.Equals("rules: " + uid, StringComparison.OrdinalIgnoreCase) ||
                            t.Equals("groups: " + uid, StringComparison.OrdinalIgnoreCase))
                        { drop = true; break; }
                    }
                    if (!drop) kept.Add(line);
                }
                string temp = list + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllText(temp, string.Join("\n", kept.ToArray()) + "\n", new UTF8Encoding(false));
                if (File.Exists(list)) File.Replace(temp, list, null);
                else File.Move(temp, list);

                // 2) 删除旧扩展文件（已备份）。
                foreach (var h in hits)
                {
                    try { if (File.Exists(h.File)) File.Delete(h.File); }
                    catch (Exception ex) { message = "已改 profiles.yaml，但删除 " + h.File + " 失败：" + ex.Message; return false; }
                }
            }
            catch (Exception ex)
            {
                message = "清理失败（备份在 " + backup + "）：" + ex.Message + "；可手工从备份恢复";
                return false;
            }

            message = "已清理 " + hits.Count + " 个旧版扩展；备份在 " + backup + "。请重启客户端。";
            return true;
        }
    }
}
