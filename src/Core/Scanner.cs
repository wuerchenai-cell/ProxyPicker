using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace ProxyPicker.Core
{
    /// <summary>只读扫描本机进程，不结束、不重启任何进程。</summary>
    internal static class Scanner
    {
        public sealed class ProcInfo
        {
            public string Exe;
            public string Path;                 // 其中一个完整路径，用于「打开文件位置」
            public int Count;
            /// <summary>同名 exe 的所有完整路径。多于一条 = 不同软件撞名，规则会一起命中。</summary>
            public readonly HashSet<string> AllPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public bool NameConflict { get { return AllPaths.Count > 1; } }
            /// <summary>同一安装目录里也在运行的其它 exe（Electron helper / 更新器 / 子进程）。</summary>
            public readonly List<string> DirPeers = new List<string>();

            /// <summary>
            /// 安装根目录**下面几层子目录**里也在运行的 exe。
            /// Antigravity 的 language_server.exe 在 resources\bin\，只做「同目录」会漏掉它，
            /// 而调用 Google OAuth 的正是这个进程 —— 漏掉就等于登录必然失败（实测 EOF）。
            /// </summary>
            public readonly List<string> TreePeers = new List<string>();
        }

        const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        [DllImport("psapi.dll", SetLastError = true)]
        static extern bool EnumProcesses(uint[] pProcessIds, uint cb, out uint lpcbNeeded);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool QueryFullProcessImageNameW(IntPtr hProcess, uint dwFlags, StringBuilder lpExeName, ref uint lpdwSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool CloseHandle(IntPtr hObject);

        public static Dictionary<string, ProcInfo> Scan()
        {
            var map = new Dictionary<string, ProcInfo>(StringComparer.OrdinalIgnoreCase);
            // 进程数超过 4096 时按 needed 扩容重试；仍装不下就明确报错，
            // 绝不静默返回空名单（评审 R5-5）。
            uint[] pids = new uint[4096];
            uint needed = 0;
            for (int attempt = 0; attempt < 5; attempt++)
            {
                if (!EnumProcesses(pids, (uint)(pids.Length * sizeof(uint)), out needed))
                    throw new InvalidOperationException("读取进程列表失败（Win32 错误 " + Marshal.GetLastWin32Error() + "）");
                if (needed < (uint)(pids.Length * sizeof(uint))) break;
                if (pids.Length >= 65536)
                    throw new InvalidOperationException("进程数达到扫描上限 65536，结果可能不完整；已中止而不是静默返回空名单");
                pids = new uint[pids.Length * 2];
            }
            int count = (int)(needed / sizeof(uint));
            var sb = new StringBuilder(1024);
            // 先按目录记住「这个目录里跑过哪些 exe」，第二遍才能给每个软件找出辅助进程。
            var byDir = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < count; i++)
            {
                uint pid = pids[i];
                if (pid == 0) continue;
                IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                if (h == IntPtr.Zero) continue;
                try
                {
                    uint size = (uint)sb.Capacity;
                    if (!QueryFullProcessImageNameW(h, 0, sb, ref size)) continue;
                    string full = sb.ToString();
                    int slash = full.LastIndexOf('\\');
                    string exe = (slash >= 0 ? full.Substring(slash + 1) : full).ToLowerInvariant();
                    if (exe.Length == 0) continue;
                    ProcInfo info;
                    if (!map.TryGetValue(exe, out info))
                    {
                        info = new ProcInfo { Exe = exe, Path = full, Count = 0 };
                        map[exe] = info;
                    }
                    info.Count++;
                    info.AllPaths.Add(full);

                    string dir = slash >= 0 ? full.Substring(0, slash) : "";
                    if (dir.Length == 0 || IsSystemDir(dir)) continue;
                    HashSet<string> inDir;
                    if (!byDir.TryGetValue(dir, out inDir))
                    {
                        inDir = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        byDir[dir] = inDir;
                    }
                    inDir.Add(exe);
                }
                finally { CloseHandle(h); }
            }

            // 主进程与辅助进程的关联只做「同目录」这一层启发式，不建进程树：
            // 系统目录里几百个 exe 互相之间毫无关系，跳过；每个软件最多记 8 个，避免刷屏。
            foreach (var kv in map)
            {
                foreach (string full in kv.Value.AllPaths)
                {
                    int slash = full.LastIndexOf('\\');
                    string dir = slash >= 0 ? full.Substring(0, slash) : "";
                    if (dir.Length == 0 || IsSystemDir(dir)) continue;
                    HashSet<string> inDir;
                    if (!byDir.TryGetValue(dir, out inDir)) continue;
                    foreach (string peer in inDir)
                    {
                        if (kv.Value.DirPeers.Count >= 8) break;
                        if (string.Equals(peer, kv.Key, StringComparison.OrdinalIgnoreCase)) continue;
                        if (!kv.Value.DirPeers.Contains(peer)) kv.Value.DirPeers.Add(peer);
                    }
                }
            }

            CollectTreePeers(map, byDir);
            return map;
        }

        /// <summary>
        /// 安装根目录的子目录关联（第二层）。
        /// 只在「exe 自己的目录」往下找 2 层，所以不会把同一盘里别的软件算进来；
        /// Program Files / 系统目录 / 盘符根这类公共根一律跳过，避免认成一片。
        /// </summary>
        static void CollectTreePeers(Dictionary<string, ProcInfo> map, Dictionary<string, HashSet<string>> byDir)
        {
            foreach (var kv in map)
            {
                foreach (string full in kv.Value.AllPaths)
                {
                    if (kv.Value.TreePeers.Count >= 8) break;
                    int slash = full.LastIndexOf('\\');
                    string root = slash >= 0 ? full.Substring(0, slash) : "";
                    if (root.Length == 0 || IsSystemDir(root) || IsSharedRoot(root)) continue;
                    string prefix = root + "\\";
                    foreach (var d in byDir)
                    {
                        if (kv.Value.TreePeers.Count >= 8) break;
                        if (!d.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                        // 深度：根目录往下最多 2 层（resources\bin 正好两层）
                        string rel = d.Key.Substring(prefix.Length);
                        int depth = 0;
                        foreach (char c in rel) if (c == '\\') depth++;
                        if (depth > 1) continue;
                        foreach (string peer in d.Value)
                        {
                            if (kv.Value.TreePeers.Count >= 8) break;
                            if (string.Equals(peer, kv.Key, StringComparison.OrdinalIgnoreCase)) continue;
                            if (kv.Value.DirPeers.Contains(peer)) continue;
                            if (kv.Value.TreePeers.Contains(peer)) continue;
                            kv.Value.TreePeers.Add(peer);
                        }
                    }
                }
            }
        }

        /// <summary>公共安装根：在这里做目录树关联会把无关软件全算成 helper。</summary>
        static bool IsSharedRoot(string dir)
        {
            string d = (dir ?? "").TrimEnd('\\').ToLowerInvariant();
            if (d.Length <= 2) return true;                       // C: / D:
            return d.EndsWith(@"\program files") || d.EndsWith(@"\program files (x86)")
                || d.EndsWith(@"\programdata") || d.EndsWith(@"\users")
                || d.EndsWith(@"\appdata\local") || d.EndsWith(@"\appdata\roaming")
                || d.EndsWith(@"\appdata\local\programs");
        }

        public static bool IsSystemDir(string dir)
        {
            string d = dir.ToLowerInvariant();
            return d.Contains(@"\windows\system32") || d.Contains(@"\windows\syswow64")
                || d.EndsWith(@"\windows") || d.Contains(@"\windows\winsxs")
                || d.Contains(@"\windows\systemapps") || d.Contains(@"\windows\immersivecontrolpanel")
                || d.Contains(@"\programdata\microsoft");
        }
    }

    internal sealed class InstalledInfo
    {
        public string Exe;
        public string Name;
        public string Path;
    }

    /// <summary>
    /// 已安装软件发现：注册表卸载项 + 开始菜单快捷方式。
    /// 每次打开/刷新面板都会重扫，因此以后新装软件不需要手工维护名单。
    /// 只做只读查询和文件存在性检查，不读取用户文档，也不运行任何被发现的程序。
    /// </summary>
    internal static class InstalledScanner
    {
        static readonly Regex ExePath = new Regex(@"[A-Za-z]:\\[^\r\n\x00""'<>|]*?\.exe",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static Dictionary<string, InstalledInfo> Scan()
        {
            var map = new Dictionary<string, InstalledInfo>(StringComparer.OrdinalIgnoreCase);
            ScanRegistry(map);
            ScanStartMenu(map);
            return map;
        }

        static void ScanRegistry(Dictionary<string, InstalledInfo> map)
        {
            const string uninstall = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
            ScanRegistryRoot(map, Registry.LocalMachine, uninstall);
            ScanRegistryRoot(map, Registry.CurrentUser, uninstall);
            // 只登记在 32 位卸载表里的软件（Chrome / WPS / Keil 等）也要扫到（评审 R4-7）。
            ScanRegistryRoot(map, Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall");
        }

        static void ScanRegistryRoot(Dictionary<string, InstalledInfo> map, RegistryKey root, string path)
        {
            try
            {
                using (var key = root.OpenSubKey(path))
                {
                    if (key == null) return;
                    foreach (var subName in key.GetSubKeyNames())
                    {
                        try
                        {
                            using (var app = key.OpenSubKey(subName))
                            {
                                if (app == null) continue;
                                string name = Convert.ToString(app.GetValue("DisplayName"));
                                if (string.IsNullOrWhiteSpace(name)) continue;
                                object system = app.GetValue("SystemComponent");
                                if (system is int && (int)system == 1) continue;
                                string release = Convert.ToString(app.GetValue("ReleaseType")) ?? "";
                                if (release.IndexOf("update", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    release.IndexOf("hotfix", StringComparison.OrdinalIgnoreCase) >= 0) continue;

                                string icon = CleanPath(Convert.ToString(app.GetValue("DisplayIcon")));
                                if (icon.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) Add(map, icon, name);
                                string dir = CleanPath(Convert.ToString(app.GetValue("InstallLocation")));
                                if (Directory.Exists(dir)) AddBestFromDirectory(map, dir, name);
                                else
                                {
                                    // 没有 InstallLocation 的安装包（常见于 Electron 应用），
                                    // 用图标路径反推安装目录，再按名字找主程序
                                    string iconDir = Path.GetDirectoryName(icon);
                                    if (!string.IsNullOrEmpty(iconDir) && Directory.Exists(iconDir))
                                        AddBestFromDirectory(map, iconDir, name, true);
                                }
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }

        static void ScanStartMenu(Dictionary<string, InstalledInfo> map)
        {
            string[] dirs =
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Windows\Start Menu\Programs"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Microsoft\Windows\Start Menu\Programs")
            };
            foreach (var dir in dirs)
            {
                if (!Directory.Exists(dir)) continue;
                string[] links;
                try { links = Directory.GetFiles(dir, "*.lnk", SearchOption.AllDirectories); }
                catch { continue; }
                foreach (var link in links)
                {
                    string name = Path.GetFileNameWithoutExtension(link);
                    if (BadName(name)) continue;
                    foreach (var target in ExtractShortcutTargets(link))
                    {
                        Add(map, target, name);
                        // 快捷方式常指向 launcher/update 这类壳程序，同目录里通常还有真正的主程序
                        string targetDir = Path.GetDirectoryName(target);
                        if (!string.IsNullOrEmpty(targetDir) && Directory.Exists(targetDir))
                            AddBestFromDirectory(map, targetDir, name, true);
                    }
                }
            }
        }

        static IEnumerable<string> ExtractShortcutTargets(string link)
        {
            var found = new List<string>();
            try
            {
                byte[] bytes = File.ReadAllBytes(link);
                if (bytes.Length > 2 * 1024 * 1024) return found;
                string[] texts = { Encoding.Unicode.GetString(bytes), Encoding.Default.GetString(bytes) };
                foreach (var text in texts)
                {
                    foreach (Match m in ExePath.Matches(text))
                    {
                        string path = CleanPath(m.Value);
                        if (File.Exists(path) && !found.Contains(path)) found.Add(path);
                    }
                }
            }
            catch { }
            return found;
        }

        static void AddBestFromDirectory(Dictionary<string, InstalledInfo> map, string dir, string name, bool requireNameMatch = false)
        {
            string[] files;
            try { files = Directory.GetFiles(dir, "*.exe", SearchOption.TopDirectoryOnly); }
            catch { return; }
            string best = null;
            int bestScore = int.MinValue;
            string wanted = (name ?? "").Replace(" ", "").ToLowerInvariant();
            foreach (var file in files)
            {
                string exe = Path.GetFileName(file);
                // 启动器/更新器/服务不参与竞争，否则会把真正的程序挤掉
                if (BadName(exe) || Catalog.IsInfrastructure(exe)) continue;
                string bare = Path.GetFileNameWithoutExtension(file);
                int score = 0;
                string compact = (bare ?? "").Replace(" ", "").ToLowerInvariant();
                if (compact.Length > 0 && wanted.Contains(compact)) score += 8;
                if (compact.Length > 0 && compact.Contains("uninstall")) score -= 20;
                if (score > bestScore) { bestScore = score; best = file; }
            }
            // 快捷方式兜底必须“名字对得上”才算数，避免把同目录的附属工具收进来
            if (best != null && (!requireNameMatch || bestScore > 0)) Add(map, best, name);
        }

        static void Add(Dictionary<string, InstalledInfo> map, string path, string name)
        {
            path = CleanPath(path);
            if (!File.Exists(path) || BadName(path)) return;
            // 少数厂商辅助组件挂在自己的目录下，名字很通用（如 CooCare 的 main.exe）
            if (path.IndexOf("\\coocare", StringComparison.OrdinalIgnoreCase) >= 0) return;
            string exe = Path.GetFileName(path).ToLowerInvariant();
            if (!Catalog.IsUserApp(exe, path) || map.ContainsKey(exe)) return;
            map[exe] = new InstalledInfo { Exe = exe, Name = name, Path = path };
        }

        static string CleanPath(string raw)
        {
            string p = (raw ?? "").Trim().Trim('"', '\'');
            int comma = p.LastIndexOf(',');
            if (comma > 4 && p.Substring(0, comma).EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                p = p.Substring(0, comma);
            try { p = Environment.ExpandEnvironmentVariables(p); } catch { }
            return p.Trim().Trim('"', '\'');
        }

        static bool BadName(string value)
        {
            string s = (value ?? "").ToLowerInvariant();
            string[] bad = { "unins", "uninstall", "卸载", "setup", "installer", "vc_redist", "vcredist",
                "crash", "updater", "update.exe", "helper.exe", "service.exe", "officeclicktorun",
                "install additional", "release notes", "帮助", "help", "manual", "许可证", "license" };
            foreach (var b in bad) if (s.IndexOf(b, StringComparison.Ordinal) >= 0) return true;
            return false;
        }
    }

    /// <summary>软件目录：常用项、通俗说明、后台噪声与基础设施过滤。</summary>
    internal static class Catalog
    {
        /// <summary>按「键, 值」成对构建字典；重复键自动忽略，避免类型初始化崩溃。</summary>
        static Dictionary<string, string> Build(params string[] pairs)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i + 1 < pairs.Length; i += 2)
                if (!d.ContainsKey(pairs[i])) d[pairs[i]] = pairs[i + 1];
            return d;
        }

        static readonly Dictionary<string, string> Nice = Build(
            "chrome.exe", "Chrome", "msedge.exe", "Edge", "firefox.exe", "Firefox",
            "msedgewebview2.exe", "WebView2 内嵌网页", "workbuddy.exe", "WorkBuddy", "antigravity.exe", "Antigravity",
            "cursor.exe", "Cursor", "codex.exe", "Codex CLI", "chatgpt.exe", "ChatGPT",
            "gemini.exe", "Gemini", "opencode.exe", "OpenCode", "trae cn.exe", "TRAE CN",
            "qoder cn.exe", "Qoder CN", "zed.exe", "Zed", "codearts-agent.exe", "CodeArts Agent",
            "node.exe", "Node.js", "node_repl.exe", "Codex 辅助进程", "python.exe", "Python",
            "pythonw.exe", "Python（无窗口）", "weixin.exe", "微信", "wechat.exe", "微信（旧版）",
            "qq.exe", "QQ", "wps.exe", "WPS 文字", "et.exe", "WPS 表格",
            "wpp.exe", "WPS 演示", "curl.exe", "curl", "sandbox-cli.exe", "WorkBuddy 沙箱",
            "cc-switch.exe", "CC Switch", "pixpin.exe", "PixPin", "spotify.exe", "Spotify",
            "telegram.exe", "Telegram", "discord.exe", "Discord", "steam.exe", "Steam",
            "thunder.exe", "迅雷", "baidunetdisk.exe", "百度网盘", "aliyundrive.exe", "阿里云盘",
            "notion.exe", "Notion", "obsidian.exe", "Obsidian", "githubdesktop.exe", "GitHub Desktop",
            "git.exe", "Git", "code.exe", "VS Code", "devenv.exe", "Visual Studio",
            "winword.exe", "Word", "excel.exe", "Excel", "powerpnt.exe", "PowerPoint",
            "acrobat.exe", "Acrobat", "foobar2000.exe", "foobar2000", "potplayermini64.exe", "PotPlayer",
            "ms-teams.exe", "Teams", "wemeetapp.exe", "腾讯会议", "cloudmusic.exe", "网易云音乐",
            "qqmusic.exe", "QQ 音乐", "kicad.exe", "KiCad", "freecad.exe", "FreeCAD",
            "uv4.exe", "Keil uVision4", "uvision.exe", "Keil uVision", "proteus.exe", "Proteus",
            "proteusdx.exe", "Proteus", "arduino ide.exe", "Arduino IDE", "arduinoide.exe", "Arduino IDE",
            "lmstudio.exe", "LM Studio", "ollama.exe", "Ollama", "claude.exe", "Claude",
            "perplexity.exe", "Perplexity", "comfyui.exe", "ComfyUI", "typora.exe", "Typora",
            "wiznote.exe", "为知笔记", "dingtalk.exe", "钉钉", "feishu.exe", "飞书",
            "lark.exe", "飞书", "wxwork.exe", "企业微信", "tim.exe", "TIM",
            "bilibili.exe", "哔哩哔哩", "iqiyi.exe", "爱奇艺", "neteasemusic.exe", "网易云音乐",
            "gitbash.exe", "Git Bash", "windowsterminal.exe", "Windows 终端", "everything.exe", "Everything",
            "7zfm.exe", "7-Zip", "winrar.exe", "WinRAR", "bandizip.exe", "Bandizip",
            "snipaste.exe", "Snipaste", "sharex.exe", "ShareX", "idea64.exe", "IntelliJ IDEA",
            "pycharm64.exe", "PyCharm", "clion64.exe", "CLion", "webstorm64.exe", "WebStorm",
            "goland64.exe", "GoLand", "rider64.exe", "Rider", "studio64.exe", "Android Studio",
            "matlab.exe", "MATLAB", "origin64.exe", "Origin", "multisim.exe", "Multisim",
            "altiumdesigner.exe", "Altium Designer", "ad.exe", "Altium Designer", "ltspice.exe", "LTspice",
            "vivado.exe", "Vivado", "quartus.exe", "Quartus", "modelsim.exe", "ModelSim",
            "pds.exe", "Proteus", "antigravity ide.exe", "Antigravity IDE", "lceda-pro.exe", "嘉立创 EDA",
            "jlc-assistant.exe", "嘉立创下单助手", "sakuracat.exe", "SakuraCat", "kuaijiasu.exe", "快加速",
            "quark.exe", "夸克", "awesun.exe", "向日葵远程控制", "deepseek harness.exe", "DeepSeek Harness"
        );

        static readonly Dictionary<string, string> Descriptions = Build(
            "chrome.exe", "谷歌浏览器", "msedge.exe", "Windows 自带浏览器", "firefox.exe", "火狐浏览器",
            "msedgewebview2.exe", "软件内嵌网页，与 Edge 分开控制", "workbuddy.exe", "AI 工作台", "antigravity.exe", "AI 编程工具",
            "cursor.exe", "AI 编程工具", "codex.exe", "OpenAI 编程助手", "chatgpt.exe", "ChatGPT 桌面版",
            "node.exe", "JavaScript 运行环境", "node_repl.exe", "Codex 的网络辅助进程", "python.exe", "Python 程序与脚本",
            "pythonw.exe", "后台 Python 程序", "weixin.exe", "聊天、语音与文件传输", "wechat.exe", "聊天与文件传输",
            "qq.exe", "聊天与文件传输", "wps.exe", "文字与 PDF", "et.exe", "电子表格",
            "wpp.exe", "幻灯片", "curl.exe", "命令行网络请求，常用于测试", "sandbox-cli.exe", "WorkBuddy 的命令执行环境",
            "cc-switch.exe", "AI 模型与接口切换", "pixpin.exe", "截图与贴图", "spotify.exe", "音乐",
            "telegram.exe", "聊天与频道", "discord.exe", "聊天与语音", "steam.exe", "游戏平台",
            "thunder.exe", "下载工具", "baidunetdisk.exe", "网盘与下载", "aliyundrive.exe", "网盘与下载",
            "notion.exe", "笔记与协作", "obsidian.exe", "本地笔记", "githubdesktop.exe", "GitHub 仓库管理",
            "git.exe", "命令行版本管理", "code.exe", "代码编辑器", "devenv.exe", "软件开发工具",
            "winword.exe", "文档编辑", "excel.exe", "表格编辑", "powerpnt.exe", "演示文稿",
            "acrobat.exe", "PDF 阅读与编辑", "foobar2000.exe", "音乐播放", "potplayermini64.exe", "视频播放"
        );

        /// <summary>默认「常用」页只放这些；已勾选的软件无论是否常用都会出现在常用页。</summary>
        static readonly HashSet<string> Common = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "chrome.exe", "msedge.exe", "firefox.exe", "workbuddy.exe", "antigravity.exe",
            "cursor.exe", "codex.exe", "chatgpt.exe", "weixin.exe", "wechat.exe", "qq.exe",
            "wps.exe", "steam.exe", "telegram.exe", "discord.exe", "spotify.exe",
            "baidunetdisk.exe", "notion.exe", "obsidian.exe", "code.exe", "cc-switch.exe",
        };

        static readonly string[] OfflineCandidates =
        {
            "chrome.exe", "msedge.exe", "firefox.exe", "weixin.exe", "qq.exe", "wps.exe",
            "steam.exe", "telegram.exe", "baidunetdisk.exe",
        };

        /// <summary>系统进程、代理基础设施和后台辅助组件，不进入普通列表。</summary>
        static readonly HashSet<string> Noise = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "conhost.exe", "svchost.exe", "dllhost.exe", "runtimebroker.exe", "searchhost.exe",
            "shellexperiencehost.exe", "textinputhost.exe", "systemsettings.exe",
            "applicationframehost.exe", "explorer.exe", "taskhostw.exe", "sihost.exe",
            "fontdrvhost.exe", "csrss.exe", "wininit.exe", "winlogon.exe", "services.exe",
            "lsass.exe", "smss.exe", "dwm.exe", "ctfmon.exe", "cmd.exe", "powershell.exe",
            "pwsh.exe", "bash.exe", "sh.exe", "prevhost.exe", "amdow.exe", "amdrsserv.exe",
            "amdrssrcext.exe", "cncmd.exe", "crashpad_handler.exe", "appvshnotify.exe",
            "chsime.exe", "securityhealthsystray.exe", "startmenuexperiencehost.exe",
            "lockapp.exe", "betterleaks.exe", "defendersessionhelper.exe", "editor_sdk.exe",
            "codex-code-mode-host.exe", "codex-computer-use-swift.exe", "crossdeviceresume.exe",
            "crossdeviceservice.exe", "desktopextension.exe", "appactions.exe",
            "coodesker-x64.exe", "regsvr32.exe", "rundll32.exe", "wscript.exe", "cscript.exe",
            "mshta.exe", "taskmgr.exe", "notepad.exe", "mspaint.exe", "snippingtool.exe",
            "verge-mihomo.exe", "clash-verge.exe", "proxypicker.exe", "proxybridge.exe",
            "proxybridgegui.exe", "proxybridgesvc.exe",
            // KiCad 的子工具：主入口是 kicad.exe，子工具不需要单独控制
            "bitmap2component.exe", "eeschema.exe", "gerbview.exe", "pcb_calculator.exe",
            "pcbnew.exe", "pl_editor.exe",
        };

        /// <summary>厂商后台组件前缀：整类都不该出现在软件列表里。</summary>
        static readonly string[] InfraPrefixes = { "hp", "omen", "widget", "microsoftstart", "reconsent", "dpinst", "nv" };

        /// <summary>服务/辅助进程的通用特征词。</summary>
        static readonly string[] InfraTokens =
        {
            "service", "helper", "updater", "uninstall", "unins", "crashpad",
            "monitor", "launcher", "telemetry", "overlay", "svc",
        };

        /// <summary>已确认的辅助程序，按名字整条排除。</summary>
        static readonly HashSet<string> InfraExact = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "licence.exe", "setlang.exe", "thunderstart.exe", "ksomisc.exe", "wpscloudsvr.exe",
            "radeonsoftware.exe", "enableloopback.exe", "kuaijiasu.exe", "sandbox-center.exe",
            "sandbox-cli.exe", "git-bash.exe", "git-cmd.exe", "git-gui.exe", "dotnet.exe",
            "extension-host.exe", "coocare.exe", "ksolaunch.exe",
            "phoneexperiencehost.exe", "systemoptimizer.exe", "reconsentnotification.exe",
        };

        /// <summary>
        /// 多个软件共用的解释器/运行时。作为「辅助进程」时绝不能写成裸进程名规则，
        /// 否则勾一个 Electron 软件会把机器上所有 node.exe 一起代理（复审 F-09 实测 13 个）。
        /// 它们仍可以由目录整树规则覆盖，也允许用户主动勾选主程序。
        /// </summary>
        static readonly HashSet<string> SharedRuntimes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "node.exe", "node64.exe", "node_repl.exe", "python.exe", "python3.exe", "pythonw.exe",
            "java.exe", "javaw.exe", "ruby.exe", "perl.exe", "php.exe",
            "powershell.exe", "pwsh.exe", "wscript.exe", "cscript.exe", "mshta.exe",
            "language_server.exe", "language_server_linux", "electron.exe",
            "msedgewebview2.exe", "webviewhost.exe",
        };

        public static bool IsSharedRuntime(string exe)
        {
            return SharedRuntimes.Contains((exe ?? "").Trim());
        }

        public static bool IsInfrastructure(string exe)
        {
            string e = (exe ?? "").ToLowerInvariant();
            if (e.Length == 0) return true;
            if (InfraExact.Contains(e)) return true;
            foreach (var p in InfraPrefixes) if (e.StartsWith(p, StringComparison.Ordinal)) return true;
            foreach (var t in InfraTokens) if (e.IndexOf(t, StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        public static string DisplayName(string exe)
        {
            string n;
            if (Nice.TryGetValue(exe, out n)) return n;
            int dot = exe.LastIndexOf('.');
            string bare = dot > 0 ? exe.Substring(0, dot) : exe;
            if (bare.Length == 0) return exe;
            return char.ToUpperInvariant(bare[0]) + bare.Substring(1);
        }

        /// <summary>是否已有手工维护的中文名（没有时界面改用安装信息里的显示名）。</summary>
        public static bool HasNice(string exe) { return Nice.ContainsKey(exe); }

        public static string Description(string exe)
        {
            string d;
            if (Descriptions.TryGetValue(exe, out d)) return d;
            return "";   // 未知软件留空，界面回退显示 exe 名，比「其他软件 · xxx」干净
        }

        public static bool IsCommon(string exe) { return Common.Contains(exe); }
        public static bool IsNoise(string exe) { return Noise.Contains(exe); }

        /// <summary>按已安装软件的显示名判断是否常用，覆盖 exe 名不规范的情况。</summary>
        static readonly string[] CommonNameKeywords =
        {
            "chrome", "edge", "firefox", "微信", "wechat", "qq", "wps", "steam",
            "telegram", "discord", "spotify", "网易云", "百度网盘", "阿里云盘",
            "notion", "obsidian", "visual studio code", "cursor", "codex", "chatgpt",
            "gemini", "trae", "qoder", "zed", "opencode", "claude", "perplexity",
            "腾讯会议", "钉钉", "飞书", "企业微信", "哔哩哔哩", "爱奇艺",
            "keil", "proteus", "kicad", "freecad", "matlab", "altium", "multisim",
            "lm studio", "ollama", "comfyui", "git", "7-zip", "winrar", "snipaste",
        };

        public static bool IsCommonByInstall(string installedName)
        {
            string s = (installedName ?? "").ToLowerInvariant();
            if (s.Length == 0) return false;
            foreach (var k in CommonNameKeywords) if (s.IndexOf(k, StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        public static bool IsUserApp(string exe, string path)
        {
            if (Noise.Contains(exe)) return false;
            if (IsInfrastructure(exe)) return false;
            if (Nice.ContainsKey(exe)) return true;
            string p = (path ?? "").ToLowerInvariant();
            if (p.StartsWith("c:\\windows", StringComparison.Ordinal)) return false;
            if (p.IndexOf("\\common files\\", StringComparison.Ordinal) >= 0) return false;
            return true;
        }

        public static IEnumerable<string> Offline { get { return OfflineCandidates; } }
    }
}
