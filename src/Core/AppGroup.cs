using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace ProxyPicker.Core
{
    /// <summary>
    /// 把「一个软件」看成一个整体，而不是一个 exe 名字。
    ///
    /// 为什么需要它：Electron / 类 VS Code 软件真正发登录、更新、语言服务请求的，
    /// 往往是安装目录深处的另一个 exe（Antigravity 是 resources\bin\language_server.exe）。
    /// 只按进程名逐条补，等于永远在追着厂商改名字：换一个软件、换一版安装包就会漏，
    /// 别人用同一份程序仍然登录不上。这里改成按**安装目录整树**覆盖：
    /// 一条 PROCESS-PATH-REGEX 就把该目录下所有现在和将来的 exe 全包括进来，
    /// 辅助进程还没启动、目录更深、以后更新新增，都不需要再改代码。
    ///
    /// 反过来，覆盖范围必须收得住：只有「这个目录确实是某个软件自己的目录」才生成路径规则。
    /// Program Files、AppData\Local、盘符根、桌面、下载这类公共容器一律跳过，
    /// 否则一条规则会把几百个无关软件一起代理，等于悄悄改了用户的分流意图。
    /// </summary>
    internal static class AppGroup
    {
        /// <summary>目录名是安装树里的「隔层」，真正属于软件本体的是它上一层。</summary>
        static readonly HashSet<string> SkipLevels = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // 注意：不要把 "app" / "apps" 放进来。Antigravity 这类软件本身就装在
            // `...\Public\App`、`...\Local\Programs\App`，把 App 当隔层会一路爬到
            // 公共容器，最后给整棵 Public 生成规则（复审 F-05 实测）。
            "bin", "resources", "current", "lib", "libs", "framework",
            "runtime", "plugins", "modules", "x64", "x86", "amd64", "win64", "win32",
        };

        /// <summary>一台机器上最多生成多少条目录规则；超过就只保留最前面几个，避免规则表失控。</summary>
        const int MaxRootRules = 40;

        /// <summary>整树里 exe 超过这个数，说明这更像一个「工具箱目录」而不是单个软件。</summary>
        const int MaxExeInTree = 60;

        // 目录遍历结果缓存：连续点击时不必每次都重新走盘。
        static readonly Dictionary<string, CacheEntry> Cache =
            new Dictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);
        static readonly object CacheGate = new object();
        sealed class CacheEntry { public int Count; public DateTime At; }

        /// <summary>
        /// 从一个 exe 的完整路径推出「软件根目录」。
        /// 只是往上收一层隔层（bin / resources / app-1.2.3 之类），不做任何猜测式搜索。
        /// 找不到可信的根就返回 null，调用方退回按进程名处理。
        /// </summary>
        public static string RootFor(string exePath)
        {
            try
            {
                if (string.IsNullOrEmpty(exePath)) return null;
                string dir = Path.GetDirectoryName(exePath);
                if (string.IsNullOrEmpty(dir)) return null;
                if (IsContainer(dir)) return null;   // exe 直接躺在 Program Files / Public 里

                for (int i = 0; i < 3; i++)
                {
                    string name = Path.GetFileName(dir);
                    if (string.IsNullOrEmpty(name)) break;
                    if (!SkipLevels.Contains(name) && !LooksLikeVersionDir(name)) break;
                    string up = Path.GetDirectoryName(dir);
                    if (string.IsNullOrEmpty(up)) break;
                    // 收上去就变成公共容器：这个目录（如 Program Files\bin）本身太宽，
                    // 不能当成某个软件的根；宁可退回按进程名，也不生成整树规则。
                    if (IsContainer(up)) return null;
                    dir = up;
                }
                if (IsContainer(dir)) return null;
                return dir.TrimEnd('\\');
            }
            catch { return null; }
        }

        /// <summary>app-1.2.3 / 1.2.3 这类版本目录也算隔层。</summary>
        static bool LooksLikeVersionDir(string name)
        {
            if (name.Length == 0) return false;
            bool digit = false, alpha = false;
            foreach (char c in name)
            {
                if (char.IsDigit(c)) digit = true;
                else if (c == '.' || c == '-' || c == '_') continue;
                else if (char.IsLetter(c)) alpha = true;
                else return false;
            }
            return digit && (!alpha || name.StartsWith("app", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>这个目录能不能安全地做整树覆盖。</summary>
        public static bool IsSafeRoot(string root)
        {
            if (string.IsNullOrEmpty(root)) return false;
            string r = root.TrimEnd('\\');
            if (IsContainer(r)) return false;
            // 父目录是公共容器、自己又是 bin/resources 这类通用隔层：不是某个软件的根。
            try
            {
                string up = Path.GetDirectoryName(r);
                if (!string.IsNullOrEmpty(up) && IsContainer(up) &&
                    SkipLevels.Contains(Path.GetFileName(r))) return false;
            }
            catch { }
            // 至少两段（C:\Program Files\App）；D:\App 这种多半是随手放的单个 exe，不生成整树规则
            string body = r.Length > 3 ? r.Substring(3) : "";
            if (body.Length == 0 || body.IndexOf('\\') <= 0) return false;
            if (r.IndexOf(',') >= 0 || r.IndexOf('\'') >= 0 || r.IndexOf('"') >= 0) return false; // 规则语法用逗号分隔
            if (r.IndexOf('#') >= 0) return false;
            return ExeCount(r) <= MaxExeInTree;
        }

        static bool IsContainer(string dir)
        {
            string d = (dir ?? "").TrimEnd('\\');
            if (d.Length <= 2) return true;                          // C: / D:
            if (d.StartsWith(@"\\", StringComparison.Ordinal)) return true;   // UNC 共享目录

            string[] seg = d.Split('\\');
            if (seg.Length < 2) return true;
            string s1 = seg[1].ToLowerInvariant();
            int n = seg.Length;

            // 盘符根下的公共目录：C:\Users、C:\Program Files、C:\ProgramData、C:\Windows
            if (n == 2)
                return s1 == "users" || s1 == "program files" || s1 == "program files (x86)"
                    || s1 == "programdata" || s1 == "windows";

            if (s1 == "users")
            {
                // C:\Users\Public
                if (n == 3 && seg[2].Equals("Public", StringComparison.OrdinalIgnoreCase)) return true;
                // C:\Users\<name>\Desktop / Downloads / Documents
                if (n == 4 && (seg[3].Equals("Desktop", StringComparison.OrdinalIgnoreCase) ||
                               seg[3].Equals("Downloads", StringComparison.OrdinalIgnoreCase) ||
                               seg[3].Equals("Documents", StringComparison.OrdinalIgnoreCase))) return true;
                // C:\Users\<name>\AppData\{Local,Roaming,LocalLow}
                if (n == 5 && seg[3].Equals("AppData", StringComparison.OrdinalIgnoreCase) &&
                    (seg[4].Equals("Local", StringComparison.OrdinalIgnoreCase) ||
                     seg[4].Equals("Roaming", StringComparison.OrdinalIgnoreCase) ||
                     seg[4].Equals("LocalLow", StringComparison.OrdinalIgnoreCase))) return true;
                // ...\AppData\Local\Programs 这一层是「所有免安装软件的共同父目录」
                if (n == 6 && seg[3].Equals("AppData", StringComparison.OrdinalIgnoreCase) &&
                    seg[4].Equals("Local", StringComparison.OrdinalIgnoreCase) &&
                    seg[5].Equals("Programs", StringComparison.OrdinalIgnoreCase)) return true;
                return false;
            }

            if (s1 == "windows" && n == 3 &&
                (seg[2].Equals("System32", StringComparison.OrdinalIgnoreCase) ||
                 seg[2].Equals("SysWOW64", StringComparison.OrdinalIgnoreCase))) return true;

            if ((s1 == "program files" || s1 == "program files (x86)") &&
                n == 3 && seg[2].Equals("Common Files", StringComparison.OrdinalIgnoreCase)) return true;

            // Steam 库：...\steamapps\common 本身是容器；再往下就是具体游戏目录，可以用。
            for (int i = 0; i + 1 < n; i++)
            {
                if (seg[i].Equals("steamapps", StringComparison.OrdinalIgnoreCase) &&
                    seg[i + 1].Equals("common", StringComparison.OrdinalIgnoreCase) &&
                    i + 2 == n) return true;
            }
            return false;
        }

        /// <summary>整树里有多少个 exe；有上限，遇到权限问题就停，不抛异常。</summary>
        static int ExeCount(string root)
        {
            lock (CacheGate)
            {
                CacheEntry hit;
                if (Cache.TryGetValue(root, out hit) && (DateTime.UtcNow - hit.At).TotalSeconds < 300)
                    return hit.Count;
            }

            int count = 0;
            var stack = new Stack<string>();
            stack.Push(root);
            int dirs = 0;
            try
            {
                while (stack.Count > 0 && dirs < 400 && count <= MaxExeInTree)
                {
                    string dir = stack.Pop();
                    dirs++;
                    try
                    {
                        foreach (var f in Directory.GetFiles(dir, "*.exe", SearchOption.TopDirectoryOnly))
                        {
                            count++;
                            if (count > MaxExeInTree) break;
                        }
                    }
                    catch { }
                    if (count > MaxExeInTree) break;
                    try
                    {
                        foreach (var sub in Directory.GetDirectories(dir))
                        {
                            // 符号链接/联接点不跟，避免绕进别的盘
                            try
                            {
                                var info = new DirectoryInfo(sub);
                                if ((info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                            }
                            catch { continue; }
                            stack.Push(sub);
                        }
                    }
                    catch { }
                }
            }
            catch { }

            // 独立审阅第二轮：扫描被 400 目录上限或 60 exe 上限截断时，
            // 「只数到少量 exe」不代表这个目录安全；必须按证据不足处理，
            // 否则大型工具箱/游戏库会被误判成可整体代理的软件根目录。
            bool truncated = dirs >= 400 || stack.Count > 0;
            bool ok = count <= MaxExeInTree && !truncated;
            int stored = ok ? count : int.MaxValue;
            lock (CacheGate)
            {
                if (Cache.Count > 200) Cache.Clear();
                Cache[root] = new CacheEntry { Count = stored, At = DateTime.UtcNow };
            }
            return stored;
        }

        /// <summary>
        /// 生成 PROCESS-PATH-REGEX 的匹配式：锚定前缀 + 转义，大小写不敏感。
        /// 末尾保留一个反斜杠，保证 Antigravity 不会顺带匹配到 Antigravity2。
        /// </summary>
        public static string PatternFor(string root)
        {
            if (string.IsNullOrEmpty(root)) return null;
            string r = root.TrimEnd('\\');
            string pattern = "(?i)^" + Regex.Escape(r) + "\\\\";
            if (pattern.IndexOf(',') >= 0) return null;
            return pattern;
        }

        /// <summary>
        /// 内核是否支持 PROCESS-PATH-REGEX。老版 Clash Premium / 早期 Meta 没有这条规则类型，
        /// 推过去会让整份配置被拒，所以先问版本；问不到就按支持处理（失败时调用方还有兜底）。
        /// </summary>
        public static bool KernelSupportsPathRules()
        {
            try
            {
                var v = Mihomo.KernelVersion();
                if (v == null) return true;
                if (!v.Meta) return false;
                return v.AtLeast(1, 16);
            }
            catch { return true; }
        }

        /// <summary>把若干软件根目录整理成去重后的路径规则列表（已过滤不安全目录）。</summary>
        public static List<string> Roots(IEnumerable<string> exePaths)
        {
            var roots = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (exePaths == null) return roots;
            foreach (var path in exePaths)
            {
                if (roots.Count >= MaxRootRules) break;
                string root = RootFor(path);
                if (root == null || !IsSafeRoot(root)) continue;
                if (!seen.Add(root)) continue;
                roots.Add(root);
            }
            return roots;
        }
    }
}
