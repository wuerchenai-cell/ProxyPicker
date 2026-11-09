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
            menu.Items.Add("全部设为直连", null, (s, e) =>
            {
                _form.ShowPanel();
                _form.AllDirectPublic();   // 现在会直接保存，不再只改内存
            });
            menu.Items.Add("复制诊断信息", null, (s, e) => _form.CopyDiagnosticsPublic());
            menu.Items.Add("还原上次保存前的配置", null, (s, e) =>
            {
                if (MessageBox.Show(
                    "把客户端配置还原到上次保存前，并清空勾选名单。\n确定要还原吗？",
                    "ProxyPicker", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                string msg;
                bool ok = Core.Profile.Rollback(out msg);
                if (ok) _form.AfterRollback();   // 同步内存勾选，别让看守把还原撤销
                MessageBox.Show(ok ? "已还原为上次保存前的配置，界面已同步刷新" : msg,
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
            // 点窗口「×」也走退出（并还原内核），不再静默藏进托盘：
            // 用户关掉窗口却还有进程在后台，正是「我明明关了它怎么还在跑」的来源。
            _form.CloseRequested += ExitApp;

            if (showOnStart) ShowPanel();
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
            bool restored = false;
            string why = "";
            try
            {
                // 还原会等锁（最多 30 秒）+ 推配置（最多 15 秒），不能卡在 UI 线程一动不动。
                // 放到后台执行，同时抽消息泵，让窗口还能重绘；最多等 35 秒。
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
                    restored ? "退出：已还原内核配置（规则块已移除）" : "退出：还原失败 · " + why);
            }
            catch (Exception ex) { restored = false; why = ex.Message; }

            // 说清楚再退出：消息循环结束之后再弹窗不可靠。
            // 保存中退出以前会静默跳过还原，用户以为已经还原——现在必须明确告知。
            if (!restored)
            {
                try
                {
                    MessageBox.Show("退出前没能把配置还原回去：" + why +
                        "\n\n规则可能还留在内核里。重新打开本程序，用托盘菜单里的「还原上次保存前的配置」可以清掉。",
                        "ProxyPicker", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                catch { }
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
