using System;
using System.Drawing;
using System.Windows.Forms;
using Microsoft.Win32;
using ProxyPicker.Core;

namespace ProxyPicker.UI
{
    /// <summary>
    /// 托盘常驻：主窗口关闭只是隐藏，程序留在右下角，点一下再打开。
    /// 这样不用每次找 exe，也不会像网页那样常驻一个浏览器标签。
    /// </summary>
    internal sealed class TrayContext : ApplicationContext
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunName = "ProxyPicker";

        readonly NotifyIcon _icon;
        readonly MainForm _form;
        readonly ToolStripMenuItem _autoStartItem;
        readonly ContextMenuStrip _menu;
        readonly Icon _trayIcon;
        TrayWatch _trayWatch;
        bool _disposed;
        bool _exiting;    // 退出还原因重入保护：DoEvents 期间再来一次退出不能起第二个还原任务

        /// <summary>
        /// explorer.exe 重启后（崩溃、更新、切用户）系统会把所有托盘图标清掉，
        /// 只有响应 "TaskbarCreated" 重新 Shell_NotifyIcon 才会回来。
        /// 不处理的话表现就是「程序在跑，但右下角找不到图标」。
        /// </summary>
        sealed class TrayWatch : NativeWindow
        {
            readonly Action _onCreated;
            readonly int _msg;

            public TrayWatch(Action onCreated)
            {
                _onCreated = onCreated;
                _msg = RegisterWindowMessage("TaskbarCreated");
                CreateHandle(new CreateParams { Caption = "ProxyPicker.TrayWatch" });
            }

            protected override void WndProc(ref Message m)
            {
                if (_msg != 0 && m.Msg == _msg)
                {
                    try { _onCreated(); } catch { }
                }
                base.WndProc(ref m);
            }

            public void Release()
            {
                try { DestroyHandle(); } catch { }
            }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
        static extern int RegisterWindowMessage(string message);

        /// <summary>
        /// 还原确认框按备份带不带 .meta 分两句文案（评审 R3）。
        /// 判断规则与 Profile.RollbackCore 的选择逻辑一致：
        /// 有 recent.yaml 用 recent.yaml，否则 original.yaml；选中的那份带 .meta 才算「新版备份」。
        /// </summary>
        static bool BackupWillFullRestore()
        {
            try
            {
                string dir = Core.Paths.BackupDir;
                string recent = System.IO.Path.Combine(dir, "recent.yaml");
                string original = System.IO.Path.Combine(dir, "original.yaml");
                string chosen = System.IO.File.Exists(recent) ? recent : original;
                if (!System.IO.File.Exists(chosen) || !System.IO.File.Exists(chosen + ".meta")) return false;
                // 独立审阅第二轮：确认框必须和 RollbackCore 用同一套判据。
                // 以前只看 .meta 文件在不在，Verge 换订阅后路径不变却会走重建，
                // 框里却说「会整份推回」，两句话互相矛盾。
                string source, fingerprint, why;
                if (!Core.Profile.ReadBackupMeta(chosen + ".meta", out source, out fingerprint)) return false;
                string current = Core.Paths.ClashYaml;
                return Core.Profile.BackupAllowed(source, current, true, fingerprint,
                                                  Core.Profile.ConfigFingerprint(current), out why);
            }
            catch { return false; }
        }

        public TrayContext(bool showOnStart)
        {
            _form = new MainForm();

            var menu = _menu = new ContextMenuStrip();
            menu.Items.Add("打开面板", null, (s, e) => ShowPanel());
            menu.Items.Add(new ToolStripSeparator());

            _autoStartItem = new ToolStripMenuItem("开机自动启动") { CheckOnClick = true };
            _autoStartItem.Checked = AutostartEnabled();
            _autoStartItem.Click += (s, e) =>
            {
                bool want = _autoStartItem.Checked;
                if (!SetAutostart(want))
                {
                    _autoStartItem.Checked = !want;
                    CrashLog.Report(new InvalidOperationException("无法保存当前用户的开机启动设置，请检查注册表权限或安全软件限制。"),
                        "保存开机启动设置", false);
                }
            };
            menu.Items.Add(_autoStartItem);

            menu.Items.Add("重新读取状态", null, (s, e) =>
            {
                _form.ShowPanel();
                _form.RefreshAsync();
            });
            menu.Items.Add("立即测速（当前节点）", null, (s, e) =>
            {
                _form.ShowPanel();
                _form.TestDelayPublic();
            });
            menu.Items.Add("节点策略与故障切换…", null, (s, e) =>
            {
                _form.ShowPanel();
                _form.SwitchPagePublic(1);
            });
            menu.Items.Add("链路体检与风险评估…", null, (s, e) =>
            {
                _form.ShowPanel();
                _form.SwitchPagePublic(2);
            });
            menu.Items.Add("一键修复分流异常", null, (s, e) =>
            {
                _form.ShowPanel();
                _form.RepairMismatchPublic();
            });
            menu.Items.Add("全部设为直连", null, (s, e) =>
            {
                _form.ShowPanel();
                _form.AllDirectPublic();   // 现在会直接保存，不再只改内存
            });
            menu.Items.Add("手动添加软件…", null, (s, e) => _form.AddExeManuallyPublic());
            menu.Items.Add("复制诊断信息", null, (s, e) => _form.CopyDiagnosticsPublic());
            menu.Items.Add("还原上次保存前的配置", null, (s, e) =>
            {
                // 评审 R3：确认框按备份带不带 .meta 说明还原语义——
                // 带 .meta 的备份是 ProxyPicker 保存的原样快照，会整份推回；
                // 不带 .meta 的是旧版备份，只会清除工具规则在当前订阅上的改动。
                bool fullRestore = BackupWillFullRestore();
                if (MessageBox.Show(
                    fullRestore
                        ? "把客户端配置还原到上次保存前（推回这份备份的整份配置），" +
                          "并清除 ProxyPicker 的接管状态（勾选名单）。\n" +
                          "已确认备份来源与当前订阅一致。还原后后台看守不会再自动接管。\n确定要还原吗？"
                        : "这份备份的来源对不上，或订阅在保存之后更新过（也可能是旧版备份）。\n" +
                          "本次不会把旧备份整份推回；改为按当前订阅清除 ProxyPicker 加的工具规则，\n" +
                          "当前订阅和你自己添加的规则都原样保留。\n确定要还原吗？",
                    "ProxyPicker", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                string msg;
                bool intentCleared;
                bool ok = Core.Profile.Rollback(out msg, out intentCleared);
                // 同步内存勾选 + 作废在途看守；名单删不掉时保留界面勾选（意图还在），
                // 只提示用户手动处理，不假装已经彻底还原（v1.3.7）。
                if (ok) _form.AfterRollback(intentCleared);
                // 成功时也要显示 msg：旧版备份、名单删除失败这类警告不能吞掉。
                MessageBox.Show(ok ? (string.IsNullOrEmpty(msg) ? "已还原为上次保存前的配置" : msg) : msg,
                    "ProxyPicker", MessageBoxButtons.OK,
                    ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            });
            // 只有接上了仓库才出现：没接上时露一个「检查更新」却永远转圈更让人困惑
            if (Updater.Enabled)
                menu.Items.Add("检查更新（当前 v" + Updater.CurrentText + "）", null,
                    (s, e) => _form.CheckUpdatePublic());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出 ProxyPicker（还原配置）", null, (s, e) => ExitApp());

            _trayIcon = Draw.AppIco(SystemInformation.SmallIconSize.Width);
            _icon = new NotifyIcon
            {
                Icon = _trayIcon,
                Text = "ProxyPicker · 点击打开",
                Visible = true,
                ContextMenuStrip = menu
            };
            _icon.MouseClick += (s, e) =>
            {
                if (e.Button == MouseButtons.Left) ShowPanel();
            };
            // explorer 重启后要把图标重新注册一遍，否则图标永久消失（只剩进程在跑）
            _trayWatch = new TrayWatch(ReAddIcon);

            // 点「—」收起时，第一次告诉用户窗口去哪了：这程序没有任务栏按钮，
            // 不说一句的话很容易以为被关掉了（或者以为压根没有最小化）。
            _form.TrayHint = () =>
            {
                try
                {
                    if (!Settings.TakeTrayHint()) return;
                    _icon.ShowBalloonTip(5000, "ProxyPicker 还在",
                        "窗口收到右下角托盘了（可能在「^」折叠区里）。找不到图标就再双击一次 exe，面板会直接回来。",
                        ToolTipIcon.Info);
                }
                catch { }
            };
            // 发现新版本时弹系统托盘通知：窗口收起来了 / --tray 后台启动也看得见。
            // 气泡点一下只打开面板，下载始终由用户自己决定（本程序绝不自动下载覆盖自己）。
            _form.TrayBalloon = (title, text) =>
            {
                try { _icon.ShowBalloonTip(8000, title, text, ToolTipIcon.Info); }
                catch { }
            };
            _icon.BalloonTipClicked += (s, e) => { try { _form.ShowPanel(); } catch { } };

            // 点窗口「×」也走退出（并还原内核），不再静默藏进托盘：
            // 用户关掉窗口却还有进程在后台，正是「我明明关了它怎么还在跑」的来源。
            _form.CloseRequested += ExitApp;

            if (showOnStart) ShowPanel();
            else
            {
                // --tray 后台启动也预创建隐藏句柄，确保 BeginInvoke 与单实例二次启动唤醒立即可用。
                try { if (!_form.IsHandleCreated) { var unused = _form.Handle; } }
                catch { }
            }
            // 放在托盘图标和气泡委托都就绪之后：后台启动也会自检，且提示能真的弹出来。
            // 必须排进消息队列再跑 —— 此刻 Application.Run 还没启动，
            // 直接调 async 方法会捕获不到 UI 同步上下文，网络回调落回线程池线程。
            try { _form.BeginInvoke(new Action(() => _form.StartAutoUpdateCheck())); }
            catch { _form.StartAutoUpdateCheck(); }
        }

        /// <summary>explorer 重启后重新注册托盘图标。</summary>
        void ReAddIcon()
        {
            try
            {
                if (_icon == null) return;
                _icon.Visible = false;
                _icon.Visible = true;
            }
            catch { }
        }

        // ---------------------------------------------------------------- 开机自启

        /// <summary>只有值确实指向当前 exe 才算已开启；便携 exe 搬过家就显示失效（评审 R6-11）。</summary>
        static bool PointsToThisExe(string command)
        {
            if (string.IsNullOrEmpty(command)) return false;
            string s = command.Trim().Trim('"');
            string exe = Application.ExecutablePath;
            return s.StartsWith(exe, StringComparison.OrdinalIgnoreCase);
        }

        static bool AutostartEnabled()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                {
                    if (k == null) return false;
                    return PointsToThisExe(Convert.ToString(k.GetValue(RunName)));
                }
            }
            catch { return false; }
        }

        static bool SetAutostart(bool on)
        {
            try
            {
                using (var k = on ? Registry.CurrentUser.CreateSubKey(RunKey) : Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (k == null) return !on;
                    if (on) k.SetValue(RunName, "\"" + Application.ExecutablePath + "\" --tray");
                    else
                    {
                        object v = k.GetValue(RunName);
                        if (v == null) return true;
                        // 同名值若属于别的安装，绝不误删。
                        if (!PointsToThisExe(Convert.ToString(v))) return false;
                        k.DeleteValue(RunName, false);
                    }
                }
                return true;
            }
            catch (Exception ex) { CrashLog.Write(ex, "保存开机启动设置失败"); return false; }
        }

        // ---------------------------------------------------------------- 显隐

        public bool MainFormHandleReady
        {
            get { return _form != null && !_form.IsDisposed && _form.IsHandleCreated; }
        }

        /// <summary>供第二个实例触发的「把面板叫出来」，任意线程安全。</summary>
        public void InvokeShow()
        {
            if (_form == null || _form.IsDisposed) return;
            if (_form.InvokeRequired) _form.BeginInvoke(new Action(ShowPanel));
            else ShowPanel();
        }

        void ShowPanel()
        {
            _form.ShowPanel();
            _form.WindowState = FormWindowState.Normal;
            _form.Activate();
        }

        /// <summary>
        /// 退出 = 把系统还回去：删掉内核里的本工具规则块、把 IPv6 恢复成接管前的值，
        /// 然后再结束进程。用户要求「不用的时候电脑跟没装一样」，这是那条要求的落点。
        /// </summary>
        void ExitApp()
        {
            if (_exiting) return;      // DoEvents 期间重复点退出不能起第二个还原任务
            _exiting = true;
            // 退出还原期间禁止再开保存或菜单任务：DoEvents 会继续分发点击，
            // 以前用户还能点「保存」，任务等锁 → 还原放锁 → 抢在 Exit 前又推一次配置。
            try { if (_menu != null) _menu.Enabled = false; } catch { }
            try { _form.BeginShutdown(); } catch { }
            bool restored = false;
            string why = "";
            try
            {
                // 还原会等锁（最多 30 秒）+ 推配置（最多 15 秒），不能卡在 UI 线程一动不动。
                // 放到后台执行，同时抽消息泵，让窗口还能重绘拖动；最多等 35 秒。
                string backgroundWhy = "";
                bool backgroundOk = false;
                var task = System.Threading.Tasks.Task.Run(() =>
                {
                    try { backgroundOk = Core.Profile.LeaveNoTrace(out backgroundWhy); }
                    catch (Exception ex) { backgroundWhy = ex.Message; }
                });
                var deadline = DateTime.UtcNow.AddSeconds(35);
                while (!task.IsCompleted && DateTime.UtcNow < deadline)
                {
                    Application.DoEvents();
                    System.Threading.Thread.Sleep(50);
                }
                if (task.IsCompleted)
                {
                    restored = backgroundOk;
                    why = backgroundWhy;
                }
                else why = "还原超时（可能仍在保存，规则未必已撤销）";
                Core.AuditLog.Record("EXIT",
                    restored
                        ? (string.IsNullOrEmpty(why) ? "退出：已还原内核配置（规则块已移除）" : "退出：" + why)
                        : "退出：还原失败 · " + why);
            }
            catch (Exception ex) { restored = false; why = ex.Message; }

            // 仅当控制面真的可疑失败（例如内核仍在跑却返 401/500、安全拦截或跨进程锁超时）才弹窗告知，
            // 并且传入合法 owner、按用户直觉「点是=立即退出 / 点否=暂不退出」处理，绝不把人卡死在弹窗里。
            if (!restored)
            {
                try
                {
                    IWin32Window owner = (_form != null && !_form.IsDisposed && _form.Visible && _form.IsHandleCreated)
                        ? (IWin32Window)_form : null;
                    string prompt = "退出前未完成配置还原：" + why +
                        "\n\n如果代理客户端仍在运行，工具写入的分流规则可能还会留在内存中。\n\n" +
                        "点「是」＝立即退出 ProxyPicker\n" +
                        "点「否」＝暂不退出（留在后台，稍后可用托盘「还原上次保存前的配置」重试）";
                    var choice = owner != null
                        ? MessageBox.Show(owner, prompt, "ProxyPicker", MessageBoxButtons.YesNo, MessageBoxIcon.Warning)
                        : MessageBox.Show(prompt, "ProxyPicker", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                    if (choice != DialogResult.Yes)
                    {
                        _exiting = false;
                        try { if (_menu != null) _menu.Enabled = true; } catch { }
                        _form.EndShutdown();
                        return;
                    }
                }
                catch
                {
                    // 对话框弹出异常时不能静默强杀未结束的后台还原，保守留在托盘并恢复状态
                    _exiting = false;
                    try { if (_menu != null) _menu.Enabled = true; } catch { }
                    _form.EndShutdown();
                    return;
                }
            }

            _icon.Visible = false;
            _form.RealExit();
            ExitThread();
            // 兜底：万一还有非后台线程挂着，直接结束进程，绝不留看不见的残留实例。
            Environment.Exit(0);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_disposed)
            {
                _disposed = true;
                if (_trayWatch != null) { _trayWatch.Release(); _trayWatch = null; }
                if (_icon != null) { _icon.Visible = false; _icon.Dispose(); }
                if (_form != null && !_form.IsDisposed) _form.Dispose();
                if (_trayIcon != null) _trayIcon.Dispose();
                if (_menu != null) _menu.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
