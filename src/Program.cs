using System;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using ProxyPicker.UI;

namespace ProxyPicker
{
    internal static class Program
    {
        const string MutexName = @"Local\ProxyPicker.SingleInstance";
        const string EventName = @"Local\ProxyPicker.ShowPanel";

        [STAThread]
        static void Main(string[] args)
        {
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) => CrashLog.Report(e.Exception, "界面未处理异常", false);
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
                CrashLog.Report(e.ExceptionObject as Exception ?? new Exception("未知后台异常"), "后台未处理异常", e.IsTerminating);
            try { Run(args); }
            catch (Exception ex)
            {
                if (args.Length > 0 && args[0] != "--tray") CliFailure(null, ex);
                else CrashLog.Report(ex, "启动失败", true);
                Environment.ExitCode = 1;
            }
        }

        // ---------------------------------------------------------------- 命令行与消息循环

        static void CliFailure(string output, Exception ex)
        {
            Environment.ExitCode = 1;
            CrashLog.Write(ex, "命令行执行失败");
            if (string.IsNullOrEmpty(output)) return;
            try { File.WriteAllText(output + ".err", ex.ToString(), new UTF8Encoding(false)); }
            catch (Exception writeError) { CrashLog.Write(writeError, "写命令行错误文件失败"); }
        }

        static void Run(string[] args)
        {
            // 自检出口：拿「别人电脑上的配置」试跑一次规则生成，看会不会用错策略组。
            // 用法：--dryrun <输入.yaml> <输出.yaml>。只读输入，不碰本机任何配置。
            if (args.Length >= 3 && args[0] == "--dryrun")
            {
                try
                {
                    string text = System.IO.File.ReadAllText(args[1], System.Text.Encoding.UTF8).Replace("\r\n", "\n");
                    bool reused;
                    string policy = Core.Discovery.FindPolicy(text, out reused);
                    var groups = Core.Discovery.GroupNames(text);
                    var items = new System.Collections.Generic.List<Core.Profile.Item>();
                    items.Add(new Core.Profile.Item("msedge.exe", policy));
                    items.Add(new Core.Profile.Item("weixin.exe", policy));
                    // 第 4 个参数可选：patch / whitelist，不给就按本机当前设置
                    bool patch = args.Length >= 4
                        ? args[3].Equals("patch", StringComparison.OrdinalIgnoreCase)
                        : Core.Settings.PatchOnly;
                    string payload = Core.Discovery.BuildPayload(text, items, policy, patch);
                    System.IO.File.WriteAllText(args[2], payload, new System.Text.UTF8Encoding(false));
                    System.IO.File.WriteAllText(args[2] + ".info",
                        "策略组共 " + groups.Count + " 个：" + string.Join(" | ", groups.ToArray()) + "\n" +
                        "选中策略：" + policy + (reused ? "（复用了已有的 PP-FIXED）" : "（自动挑的）") + "\n" +
                        "分流模式：" + (patch ? "只改勾的（原规则保留）" : "完全接管（原规则让位）") + "\n" +
                        "输出长度：" + payload.Length + "\n",
                        new System.Text.UTF8Encoding(false));
                }
                catch (Exception ex)
                {
                    CliFailure(args[2], ex);
                }
                return;
            }

            // 自检出口：把「按当前名单生成的完整配置」写到文件，用于和已验证的 Python 版逐字节比对。
            // 不推送、不改任何东西，纯只读。
            if (args.Length >= 2 && args[0] == "--payload")
            {
                try
                {
                    var items = Core.Profile.Load();
                    string payload = Core.Profile.BuildPayload(Core.Paths.ClashYaml, items);
                    System.IO.File.WriteAllText(args[1], payload, new System.Text.UTF8Encoding(false));
                    System.Console.Out.Write("items=" + items.Count + " chars=" + payload.Length);
                }
                catch (Exception ex)
                {
                    CliFailure(args[1], ex);
                }
                return;
            }

            // 自检出口：按当前名单执行一次完整应用（写名单 -> 生成 payload -> 推送 -> 回读校验）。
            // 幂等：名单不变时结果与现状一致，用于验证 C# 侧链路。
            if (args.Length >= 2 && args[0] == "--apply")
            {
                try
                {
                    var items = Core.Profile.Load();
                    var res = Core.Profile.Apply(items);
                    System.IO.File.WriteAllText(args[1],
                        "ok=" + res.Ok + "\nmsg=" + res.Message + "\nrules=" + res.RuleCount + "\nms=" + res.Ms + "\nitems=" + items.Count,
                        new System.Text.UTF8Encoding(false));
                }
                catch (Exception ex)
                {
                    CliFailure(args[1], ex);
                }
                return;
            }

            // 自检出口：把识别到的软件目录导出，用于核对「已安装软件是否被发现」。只读。
            if (args.Length >= 2 && args[0] == "--list")
            {
                try
                {
                    var procs = Core.Scanner.Scan();
                    var installed = Core.InstalledScanner.Scan();
                    var policy = Core.Profile.LoadPolicyMap();
                    var sb = new System.Text.StringBuilder();
                    var seen = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var names = new System.Collections.Generic.List<string>();
                    foreach (var k in policy.Keys) if (seen.Add(k)) names.Add(k);
                    foreach (var k in Core.Catalog.Offline) if (seen.Add(k)) names.Add(k);
                    foreach (var kv in procs)
                        if (!seen.Contains(kv.Key) && Core.Catalog.IsUserApp(kv.Key, kv.Value.Path) && seen.Add(kv.Key)) names.Add(kv.Key);
                    foreach (var kv in installed) if (seen.Add(kv.Key)) names.Add(kv.Key);

                    sb.AppendLine("total=" + names.Count + " installed=" + installed.Count + " running=" + procs.Count);
                    names.Sort(StringComparer.OrdinalIgnoreCase);
                    foreach (var exe in names)
                    {
                        Core.Scanner.ProcInfo pi;
                        procs.TryGetValue(exe, out pi);
                        Core.InstalledInfo ii;
                        installed.TryGetValue(exe, out ii);
                        string pol;
                        policy.TryGetValue(exe, out pol);
                        sb.AppendLine(string.Join("\t", new[]
                        {
                            exe,
                            Core.Catalog.DisplayName(exe),
                            Core.Catalog.Description(exe),
                            pi != null ? "运行" + pi.Count : "未运行",
                            pol ?? "-",
                            Core.Catalog.IsCommon(exe) || (ii != null && Core.Catalog.IsCommonByInstall(ii.Name)) ? "常用" : "",
                            ii != null ? ii.Name : "",
                            pi != null ? pi.Path : (ii != null ? ii.Path : "")
                        }));
                    }
                    System.IO.File.WriteAllText(args[1], sb.ToString(), new System.Text.UTF8Encoding(false));
                }
                catch (Exception ex)
                {
                    CliFailure(args[1], ex);
                }
                return;
            }

            // 自检出口：查一次 GitHub 上的最新版本，把原始结果和版本比较的边界用例都写进文件。
            // 只发一个 GET，不改本机任何配置，也不下载任何东西。
            if (args.Length >= 2 && args[0] == "--checkupdate")
            {
                try
                {
                    // 第 3 个参数可选：临时指定一个仓库来验证整条链路（正式运行不读它）
                    if (args.Length >= 3 && args[2].IndexOf('/') > 0) Core.Updater.OverrideForTest(args[2]);
                    var info = Core.Updater.Check(8000);
                    string text =
                        "enabled=" + Core.Updater.Enabled + "\n" +
                        "current=" + Core.Updater.CurrentText + "\n" +
                        "ok=" + info.Ok + "\n" +
                        "newer=" + info.Newer + "\n" +
                        "latest=" + info.Latest + "\n" +
                        "date=" + info.Date + "\n" +
                        "url=" + info.Url + "\n" +
                        "error=" + info.Error + "\n\n" +
                        Core.Updater.SelfTest();
                    System.IO.File.WriteAllText(args[1], text, new System.Text.UTF8Encoding(false));
                }
                catch (Exception ex)
                {
                    CliFailure(args[1], ex);
                }
                return;
            }

            // 自检出口：把版本号与版本比较结果写到文件（WinExe 没有控制台，看不到 Console 输出）。
            if (args.Length >= 2 && args[0] == "--version")
            {
                try
                {
                    System.IO.File.WriteAllText(args[1],
                        "ProxyPicker " + Core.Updater.CurrentText + "\n仓库：" +
                        (Core.Updater.Enabled ? Core.Updater.RepoSlug : "未配置") + "\n\n" +
                        Core.Updater.SelfTest(),
                        new System.Text.UTF8Encoding(false));
                }
                catch (Exception ex)
                {
                    CliFailure(args[1], ex);
                }
                return;
            }

            // 自检出口：用三份不同风格的配置验证「规则生成」是否通用。只读、不碰真机配置。
            if (args.Length >= 2 && args[0] == "--selftest")
            {
                try
                {
                    string result = Core.Discovery.SelfTest();
                    System.IO.File.WriteAllText(args[1], result, new System.Text.UTF8Encoding(false));
                    if (result.Contains("FAIL  ")) Environment.ExitCode = 1;
                }
                catch (Exception ex)
                {
                    CliFailure(args[1], ex);
                }
                return;
            }

            // 自检出口：直接向内核发一次请求，把原始响应（含状态行与头部）写文件。
            // 用法：ProxyPicker.exe --raw <输出文件> <方法> <路径>
            if (args.Length >= 4 && args[0] == "--raw")
            {
                try
                {
                    var r = Core.Mihomo.RequestRaw(args[2], args[3], null, 15000);
                    System.IO.File.WriteAllText(args[1], r, new System.Text.UTF8Encoding(false));
                }
                catch (Exception ex)
                {
                    CliFailure(args[1], ex);
                }
                return;
            }

            // 自检出口：列出内核相关命名管道。只读。
            if (args.Length >= 2 && args[0] == "--pipes")
            {
                try
                {
                    var all = Core.Mihomo.EnumNamedPipes();
                    System.IO.File.WriteAllText(args[1], string.Join("\n", all.ToArray()), new System.Text.UTF8Encoding(false));
                }
                catch (Exception ex)
                {
                    CliFailure(args[1], ex);
                }
                return;
            }

            // 自检出口：直接向内核发一次请求，把原始响应写文件。只读（只用 GET）。
            // 用法：ProxyPicker.exe --api <输出文件> <路径>   例：--api out.json /proxies
            if (args.Length >= 3 && args[0] == "--api")
            {
                try
                {
                    var r = Core.Mihomo.Request("GET", args[2], null, 15000);
                    string text = "status=" + r.Status + "\nms=" + r.Ms + "\nerror=" + (r.Error ?? "-") + "\n\n" + r.Body;
                    System.IO.File.WriteAllText(args[1], text, new System.Text.UTF8Encoding(false));
                }
                catch (Exception ex)
                {
                    CliFailure(args[1], ex);
                }
                return;
            }

            // 自检出口：把「名单」和「内核现在真正在做的」对一次账。只读，不改任何东西。
            // 用法：ProxyPicker.exe --audit <输出文件>
            if (args.Length >= 2 && args[0] == "--audit")
            {
                try
                {
                    var items = Core.Profile.Load();
                    var wanted = new System.Collections.Generic.List<string>();
                    foreach (var it in items) wanted.Add(it.Exe);
                    var a = Core.Guard.Audit(wanted);

                    var sb = new System.Text.StringBuilder();
                    sb.AppendLine("ProxyPicker " + typeof(Program).Assembly.GetName().Version + " 规则对账");
                    sb.AppendLine("分流模式：" + (Core.Settings.PatchOnly ? "只改勾的" : "完全接管"));
                    sb.AppendLine("名单条数：" + wanted.Count);
                    if (a.Error != null) sb.AppendLine("读内核失败：" + a.Error);
                    sb.AppendLine("内核规则：" + a.RuleCount + " 条（内联 PROCESS-NAME " + a.ProcessRules + " 条）");
                    sb.AppendLine("生效形态：" + (a.ProcessRules > 0
                        ? "内联 PROCESS-NAME（" + a.ProcessRules + " 条）"
                        : (a.ViaRuleSet ? "RULE-SET provider" : "没有")));
                    sb.AppendLine("提前的 MATCH：" + (a.HeadShortCircuit
                        ? "有，在第 " + (a.MatchIndex + 1) + " 条（共 " + a.RuleCount + " 条）"
                          + (Core.Settings.PatchOnly ? " —— 只改勾的模式下属异常" : " —— 完全接管下按设计如此")
                        : "无"));
                    sb.AppendLine("IPv6：" + (a.Ipv6On ? "开着（流量可从物理网卡直出）" : "关"));
                    sb.AppendLine("find-process-mode：" + (string.IsNullOrEmpty(a.FindProcess) ? "未知" : a.FindProcess));
                    sb.AppendLine("不在生效名单里的：" + (a.Missing.Count == 0 ? "无" : string.Join("、", a.Missing.ToArray())));
                    sb.AppendLine("判定：" + (Core.Guard.Judge(a, wanted.Count, Core.Settings.PatchOnly) ?? "一致，不用动"));
                    sb.AppendLine("---- 最近动作 ----");
                    string recent = Core.AuditLog.Recent(20);
                    sb.Append(recent.Length == 0 ? "（暂无）\n" : recent);

                    System.IO.File.WriteAllText(args[1], sb.ToString(), new System.Text.UTF8Encoding(false));
                }
                catch (Exception ex)
                {
                    CliFailure(args[1], ex);
                }
                return;
            }

            // 自检出口：输出环境诊断（客户端识别、控制面、内核状态）。只读。
            if (args.Length >= 2 && args[0] == "--diag")
            {
                try
                {
                    System.IO.File.WriteAllText(args[1], Core.Discovery.Diagnose(), new System.Text.UTF8Encoding(false));
                }
                catch (Exception ex)
                {
                    CliFailure(args[1], ex);
                }
                return;
            }

            bool created;
            using (var mutex = new Mutex(true, MutexName, out created))
            {
                if (!created)
                {
                    // 已有实例：让它把面板弹出来，然后自己退出
                    try
                    {
                        using (var ev = EventWaitHandle.OpenExisting(EventName))
                            ev.Set();
                    }
                    catch { }
                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                bool background = Array.IndexOf(args, "--tray") >= 0;
                var ctx = new TrayContext(!background);

                // 第二个实例被启动时，把面板叫到前台
                var listener = new Thread(() =>
                {
                    try
                    {
                        using (var ev = new EventWaitHandle(false, EventResetMode.AutoReset, EventName))
                        {
                            while (true)
                            {
                                ev.WaitOne();
                                if (ctx.MainFormHandleReady)
                                {
                                    try { ctx.InvokeShow(); } catch { }
                                }
                            }
                        }
                    }
                    catch { }
                });
                listener.IsBackground = true;
                listener.Start();

                using (ctx) Application.Run(ctx);
                GC.KeepAlive(mutex);
            }
        }
    }

    // ---------------------------------------------------------------- 故障留证（不访问内核、不读取配置或凭据）

    internal static class CrashLog
    {
        const long MaxBytes = 512 * 1024;
        static readonly object Gate = new object();
        static string _context = "客户端=尚未读取；规则数=未知；勾选数=未知";
        static int _showing;

        public static void SetContext(string client, int rules, int selected)
        {
            _context = "客户端=" + (client ?? "未知") + "；规则数=" + rules + "；勾选数=" + selected;
        }

        public static string Write(Exception ex, string source)
        {
            try
            {
                var text = new StringBuilder();
                text.AppendLine("\n[" + DateTimeOffset.Now.ToString("o") + "] ProxyPicker " + typeof(Program).Assembly.GetName().Version);
                text.AppendLine("位置=" + source);
                text.AppendLine(_context);
                // 单条异常的 Message 有界；堆栈原样保留，避免坏输入撑爆日志。
                for (Exception error = ex; error != null; error = error.InnerException)
                {
                    string message = error.Message ?? "";
                    if (message.Length > 16000) message = message.Substring(0, 16000) + "…（消息过长）";
                    text.AppendLine(error.GetType().FullName + ": " + message);
                    text.AppendLine(error.StackTrace ?? "（异常尚未抛出，无调用栈）");
                }
                string entry = text.ToString();
                lock (Gate)
                {
                    return AppendFallback(Core.Paths.LogPath, () => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "proxypicker.log"), entry);
                }
            }
            catch { return null; } // 日志故障不能再次触发异常处理器
        }

        static string AppendFallback(string primary, Func<string> fallback, string entry)
        {
            try { return Append(primary, entry); }
            catch
            {
                try { return Append(fallback(), entry); }
                catch { return null; }
            }
        }

        static string Append(string path, string text)
        {
            var encoding = new UTF8Encoding(false);
            if (File.Exists(path) && new FileInfo(path).Length + encoding.GetByteCount(text) > MaxBytes)
            {
                if (File.Exists(path + ".1")) File.Delete(path + ".1");
                File.Move(path, path + ".1");
            }
            File.AppendAllText(path, text, encoding);
            // 极端长堆栈的单条日志也轮转，仍只保留活动文件和 .1。
            if (new FileInfo(path).Length > MaxBytes)
            {
                if (File.Exists(path + ".1")) File.Delete(path + ".1");
                File.Move(path, path + ".1");
                File.WriteAllText(path, "上一条异常较长，完整记录见 .1。\n", encoding);
            }
            return path;
        }

        public static void Report(Exception ex, string source, bool terminating)
        {
            string path = Write(ex, source);
            if (Interlocked.Exchange(ref _showing, 1) != 0) return;
            try
            {
                using (var notice = new ErrorNotice(path, terminating)) notice.ShowDialog();
            }
            catch { } // 例如桌面会话已关闭时，日志仍独立保留
            finally { Interlocked.Exchange(ref _showing, 0); }
        }
    }

    // ---------------------------------------------------------------- 自绘错误提示

    internal sealed class ErrorNotice : Form
    {
        readonly string _path;
        readonly bool _terminating;
        readonly Font _title = Theme.Ui(15f * Theme.S, FontStyle.Bold);
        readonly Font _body = Theme.Ui(11f * Theme.S);

        public ErrorNotice(string path, bool terminating)
        {
            _path = path;
            _terminating = terminating;
            Text = "ProxyPicker · 错误记录";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.None;
            ClientSize = new Size(Theme.Px(440), Theme.Px(240));
            BackColor = Theme.Bg;
            DoubleBuffered = true;
            KeyPreview = true;
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Escape) Close(); };
        }

        RectangleF Okay { get { return new RectangleF(Theme.Px(300), Theme.Px(188), Theme.Px(116), Theme.Px(34)); } }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            Draw.Hint(g);
            Draw.Text(g, "遇到了一点问题", _title, Theme.Fg, new RectangleF(Theme.Px(24), Theme.Px(22), Width - Theme.Px(48), Theme.Px(30)));
            string message = _terminating ? "程序将退出，请重新打开。" : "这次操作没有完成，可以关闭提示后重试。";
            message += _path == null ? "\n日志目录也无法写入，请检查文件权限。" : "\n已记录日志，排查时可提供这个文件：\n" + _path;
            Draw.Text(g, message, _body, Theme.Dim, new RectangleF(Theme.Px(24), Theme.Px(64), Width - Theme.Px(48), Theme.Px(112)), Draw.Wrap);
            using (var brush = new SolidBrush(Theme.Accent))
            using (var shape = Theme.RoundRect(Okay, Theme.Px(8))) g.FillPath(brush, shape);
            Draw.Text(g, "知道了", _body, Color.White, Okay, Draw.Center);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Left && Okay.Contains(e.Location)) Close();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { _title.Dispose(); _body.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
