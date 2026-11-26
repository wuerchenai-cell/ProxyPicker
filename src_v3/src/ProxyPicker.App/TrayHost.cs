using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using ProxyPicker.Domain;

namespace ProxyPicker.App
{
    /// <summary>
    /// 系统托盘宿主（集成 TaskbarCreated 消息自愈：资源管理器重启后自动重建托盘图标）。
    /// </summary>
    internal sealed class TrayHost : IDisposable
    {
        readonly NotifyIcon _notifyIcon;
        readonly TaskbarMessageWindow _msgWin;
        readonly Action _onShowMain;
        readonly Action _onToggleCompact;
        readonly Action _onRefresh;
        readonly Action _onRestore;
        readonly Action _onExit;
        bool _disposed;

        public TrayHost(Action onShowMain, Action onToggleCompact, Action onRefresh, Action onRestore, Action onExit)
        {
            _onShowMain = onShowMain;
            _onToggleCompact = onToggleCompact;
            _onRefresh = onRefresh;
            _onRestore = onRestore;
            _onExit = onExit;

            _notifyIcon = new NotifyIcon
            {
                Icon = LoadEmbeddedIcon(),
                Text = ProductInfo.Name + " v" + ProductInfo.Version + " — 应用分流守护中",
                Visible = true,
                ContextMenuStrip = BuildContextMenu()
            };
            _notifyIcon.DoubleClick += (_, __) => _onShowMain?.Invoke();

            _msgWin = new TaskbarMessageWindow(RecreateTrayIcon);
        }

        ContextMenuStrip BuildContextMenu()
        {
            var menu = new ContextMenuStrip();
            var mOpen = new ToolStripMenuItem("打开 ProxyPicker 工作台") { Font = new Font(menu.Font, FontStyle.Bold) };
            mOpen.Click += (_, __) => _onShowMain?.Invoke();
            menu.Items.Add(mOpen);

            var mCompact = new ToolStripMenuItem("迷你实时监控浮窗");
            mCompact.Click += (_, __) => _onToggleCompact?.Invoke();
            menu.Items.Add(mCompact);

            var mRefresh = new ToolStripMenuItem("立即刷新内核与节点状态");
            mRefresh.Click += (_, __) => _onRefresh?.Invoke();
            menu.Items.Add(mRefresh);

            menu.Items.Add(new ToolStripSeparator());

            var mRestore = new ToolStripMenuItem("一键还原内核配置");
            mRestore.Click += (_, __) => _onRestore?.Invoke();
            menu.Items.Add(mRestore);

            var mExit = new ToolStripMenuItem("退出（自动还原内核并同步联动关闭）");
            mExit.Click += (_, __) => _onExit?.Invoke();
            menu.Items.Add(mExit);
            return menu;
        }

        void RecreateTrayIcon()
        {
            if (_disposed) return;
            try
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Visible = true;
            }
            catch { }
        }

        public void ShowBalloon(string title, string text)
        {
            if (_disposed) return;
            try { _notifyIcon.ShowBalloonTip(3000, title, text, ToolTipIcon.Info); } catch { }
        }

        static Icon LoadEmbeddedIcon()
        {
            try
            {
                using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("ProxyPicker.Assets.ProxyPicker.ico"))
                {
                    if (s != null) return new Icon(s);
                }
            }
            catch { }
            return SystemIcons.Application;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _msgWin.DestroyHandle(); } catch { }
            try { _notifyIcon.Visible = false; _notifyIcon.Dispose(); } catch { }
        }

        sealed class TaskbarMessageWindow : NativeWindow
        {
            [DllImport("user32.dll", CharSet = CharSet.Unicode)]
            static extern uint RegisterWindowMessage(string lpString);

            readonly uint _taskbarCreatedMsg;
            readonly Action _onCreated;

            public TaskbarMessageWindow(Action onCreated)
            {
                _onCreated = onCreated;
                _taskbarCreatedMsg = RegisterWindowMessage("TaskbarCreated");
                CreateHandle(new CreateParams { Caption = "ProxyPickerTrayMsgWin" });
            }

            protected override void WndProc(ref Message m)
            {
                if (_taskbarCreatedMsg != 0 && (uint)m.Msg == _taskbarCreatedMsg)
                    _onCreated?.Invoke();
                base.WndProc(ref m);
            }
        }
    }
}
