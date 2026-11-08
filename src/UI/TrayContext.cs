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
        bool _disposed;

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
                _form.AllDirectPublic();
            });
            menu.Items.Add("复制诊断信息", null, (s, e) =>
            {
                try { Clipboard.SetText(Discovery.Diagnose()); } catch { }
            });
            // 只有接上了仓库才出现：没接上时露一个「检查更新」却永远转圈更让人困惑
            if (Updater.Enabled)
                menu.Items.Add("检查更新（当前 v" + Updater.CurrentText + "）", null,
                    (s, e) => _form.CheckUpdatePublic());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出 ProxyPicker", null, (s, e) => ExitApp());

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

            // 点「—」收起时，第一次告诉用户窗口去哪了：这程序没有任务栏按钮，
            // 不说一句的话很容易以为被关掉了（或者以为压根没有最小化）。
            _form.TrayHint = () =>
            {
                try
                {
                    if (!Settings.TakeTrayHint()) return;
                    _icon.ShowBalloonTip(4000, "ProxyPicker 还在",
                        "窗口收到右下角托盘了，点图标随时打开。要退出请右键图标选「退出 ProxyPicker」。",
                        ToolTipIcon.Info);
                }
                catch { }
            };

            if (showOnStart) ShowPanel();
        }

        // ---------------------------------------------------------------- 开机自启

        static bool AutostartEnabled()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                    return k != null && k.GetValue(RunName) != null;
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
                    else k.DeleteValue(RunName, false);
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

        void ExitApp()
        {
            _icon.Visible = false;
            _form.RealExit();
            ExitThread();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_disposed)
            {
                _disposed = true;
                if (_icon != null) { _icon.Visible = false; _icon.Dispose(); }
                if (_form != null && !_form.IsDisposed) _form.Dispose();
                if (_trayIcon != null) _trayIcon.Dispose();
                if (_menu != null) _menu.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
