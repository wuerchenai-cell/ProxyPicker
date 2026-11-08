using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using ProxyPicker.Core;

namespace ProxyPicker.UI
{
    internal sealed class MainForm : Form
    {
        const float WinW = 466f;
        const float WinH = 700f;
        const float TitleH = 54f;
        const float SummaryTop = 60f;
        const float SummaryH = 80f;
        const float SearchTop = 148f;
        const float SearchH = 38f;
        const float TabsTop = 194f;
        const float ListTop = 230f;
        const float FooterH = 78f;

        [DllImport("user32.dll")] static extern bool ReleaseCapture();
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", EntryPoint = "SendMessageW", CharSet = CharSet.Unicode)]
        static extern IntPtr SendMessageText(IntPtr hWnd, int msg, IntPtr wParam, string lParam);
        [DllImport("dwmapi.dll", PreserveSig = true)]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        const int WM_NCLBUTTONDOWN = 0xA1;
        const int HTCAPTION = 2;
        const int EM_SETCUEBANNER = 0x1501;

        readonly AppList _list = new AppList();
        readonly TextBox _search = new TextBox();
        readonly ToastHost _toasts = new ToastHost();
        readonly Timer _clock = new Timer { Interval = 16 };
        readonly Timer _trafficTimer = new Timer { Interval = 2000 };
        // 对账用的慢时钟：每 10 秒醒一次，每三次（30 秒）才真去问内核一遍。
        // 用独立计时器而不是挂在 _trafficTimer 上，是因为收进托盘后流量计时器会停，
        // 而「换订阅后自己发现」这件事恰恰要在收进托盘时也能工作。
        readonly Timer _watchTimer = new Timer { Interval = 10000 };

        readonly Font _fTitle = Theme.Ui(15.5f, FontStyle.Bold);
        readonly Font _fSub = Theme.Ui(11.5f);
        readonly Font _fSummary = Theme.Ui(16f, FontStyle.Bold);
        Font _fPill;   // 出口胶囊，懒加载
        readonly Font _fBtn = Theme.Ui(12f, FontStyle.Bold);
        readonly Font _fHint = Theme.Ui(10.5f);
        readonly Font _fSmall = Theme.Ui(10f);
        readonly Font _fInput = Theme.Ui(12.5f);
        readonly Font _fTab = Theme.Ui(11.5f);
        readonly Font _fTabActive = Theme.Ui(11.5f, FontStyle.Bold);
        readonly Font _fRate = Theme.Ui(10.5f, FontStyle.Bold);
        readonly Font _fTiny = Theme.Ui(9.5f);
        readonly Font _fHelp = Theme.Ui(11.5f);
        readonly Font _fHelpTitle = Theme.Ui(14f, FontStyle.Bold);

        enum Btn { None, Save, Direct, Refresh, Close, Help, Sort, HelpClose, HelpDiag, HelpUnhide, HelpMode, Min, HelpUpdate }
        Btn _hover = Btn.None;
        int _tab;
        int _hoverTab = -1;
        bool _busy;
        bool _polling;
        bool _checkingOutbound;
        bool _resourcesDisposed;
        int _revision;
        readonly Icon _windowIcon;
        string _lastFailure;
        DateTime _lastFailureAt;
        bool _realExit;
        bool _help;
        bool _modeChanged;   // 在帮助面板里切过分流模式，关闭时提醒去保存
        Updater.Info _update;      // null = 还没查过；查过之后失败也留着，用来解释为什么没提示
        bool _updateBusy;
        bool _updateSessionDone;  // 本次运行已经自动查过一次，别每次打开面板都去问

        /// <summary>点了「—」之后由托盘那边弹一次气泡，告诉用户窗口去哪了。首次才弹。</summary>
        public Action TrayHint;

        Profile.KernelState _state = new Profile.KernelState();
        List<AppRow> _all = new List<AppRow>();
        HashSet<string> _savedProxy = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string _status = "正在准备…";
        Color _statusColor;

        float _spin;
        float _show = 1f;
        DateTime _spinUntil = DateTime.MinValue;
        DateTime _lastLoad = DateTime.MinValue;
        bool _spinning;
        bool _autoRepaired;   // 本次会话是否已经自动补回过规则
        int _watchTick;
        bool _watching;
        int _repairFails;     // 连续补回失败次数；对账一致时清零，连败 3 次就停手
        DateTime _lastRepairAt = DateTime.MinValue;
        int _ipv6Attempts;    // 只针对 IPv6 的补回次数：看到关就清零，连试 3 次不成就只记账
        Traffic.Sample _traffic;
        readonly List<float> _sparkProxy = new List<float>();
        readonly List<float> _sparkDirect = new List<float>();

        public MainForm()
        {
            Text = "ProxyPicker";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            ShowInTaskbar = false;
            DoubleBuffered = true;
            BackColor = Theme.Bg;
            AutoScaleMode = AutoScaleMode.None;
            ClientSize = new Size(Theme.Px(WinW), Theme.Px(WinH));
            KeyPreview = true;
            try { _windowIcon = Draw.AppIco(24); Icon = _windowIcon; } catch (Exception ex) { CrashLog.Write(ex, "读取窗口图标失败"); }

            _list.Toggled += (s, e) => ShowDirtyStatus();
            _list.Reordered += OnReordered;
            _list.ContextRequested += (s, e) => ShowRowMenu(e.Row, e.Screen);
            _list.Overlay = (g, area) => { if (_toasts.Any) _toasts.Render(g, area); };

            _search.BorderStyle = BorderStyle.None;
            _search.BackColor = Theme.Card;
            _search.ForeColor = Theme.Fg;
            _search.Font = _fInput;
            _search.AutoSize = false;
            _search.TextChanged += (s, e) => RebuildVisible(false);
            _search.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Escape) { HideToTray(); e.SuppressKeyPress = true; }
                if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; SaveAsync(); }
                if (e.KeyCode == Keys.Down) { _list.MoveSelection(1); e.SuppressKeyPress = true; }
            };
            _search.GotFocus += (s, e) => Invalidate();
            _search.LostFocus += (s, e) => Invalidate();

            Controls.Add(_list);
            Controls.Add(_search);
            KeyDown += OnFormKeyDown;
            LayoutChildren();
            SendMessageText(_search.Handle, EM_SETCUEBANNER, new IntPtr(1), "搜索软件名或进程名…");

            _clock.Tick += ClockTick;
            _trafficTimer.Tick += (s, e) => PollTraffic();
            _watchTimer.Tick += (s, e) => WatchTick();
            _watchTimer.Start();
            Opacity = 0.06;
            _show = 0f;
            _clock.Start();
        }

        static bool IsWin11OrLater
        {
            get { try { return Environment.OSVersion.Version.Build >= 22000; } catch { return false; } }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                if (!IsWin11OrLater) cp.ClassStyle |= 0x20000;
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try { int round = 2; DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int)); } catch { }
        }

        // ---------------------------------------------------------------- 时钟

        void ClockTick(object sender, EventArgs e)
        {
            bool need = false;

            // 刷新再快也让指针转够一小会儿，否则用户只觉得「闪了一下」
            _spinning = _busy || DateTime.UtcNow < _spinUntil;
            if (_spinning) { _spin = (_spin + 13f) % 360f; need = true; }

            if (_show < 1f)
            {
                _show = Math.Min(1f, _show + 0.085f);
                try { Opacity = 0.06 + _show * 0.94; } catch { }
                need = true;
            }

            if (_toasts.Tick(0.016)) need = true;

            if (need) Invalidate();
            else _clock.Stop();
        }

        void Wake()
        {
            if (!_clock.Enabled) _clock.Start();
        }

        // ---------------------------------------------------------------- 显隐

        public void ShowPanel()
        {
            Theme.Load();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Show(); BringToFront(); Activate(); _search.Focus();
            if (_all.Count == 0 || (DateTime.UtcNow - _lastLoad).TotalSeconds > 20) RefreshAsync();
            else { ShowDirtyStatus(); RebuildVisible(false); }
            _trafficTimer.Start();
            PollTraffic();
            if (Settings.Current.FirstRun)
            {
                Settings.Current.FirstRun = false;
                Settings.Save();
                Toast("先点开关，再点右下角保存",
                    Settings.PatchOnly ? "没勾的软件仍按你订阅的规则走，不会乱改" : "只重连改动到的软件，其他连接不会断",
                    Theme.Accent, 2);
            }

            // 版本检查放在最后，不挡面板出现；一次运行只查一次，查过就不再打扰
            if (!_updateSessionDone)
            {
                _updateSessionDone = true;
                if (Updater.Enabled && Settings.ShouldCheckUpdate(24)) CheckUpdateAsync(false);
            }
        }

        public void HideToTray()
        {
            Hide();
            _trafficTimer.Stop();
        }

        /// <summary>
        /// 点标题栏「—」走这里。和「×」落点一样都是收起，区别只在要不要解释一句
        /// 「窗口去哪了」——第一次点的人多半是想要它先消失，但也可能以为程序被关掉了。
        /// </summary>
        public void MinimizeToTray()
        {
            HideToTray();
            var hint = TrayHint;
            if (hint != null) { try { hint(); } catch { } }
        }

        public void RealExit() { _realExit = true; Close(); }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!_realExit && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true; Hide(); _trafficTimer.Stop(); return;
            }
            base.OnFormClosing(e);
        }

        // ---------------------------------------------------------------- 数据

        public async void RefreshAsync()
        {
            bool ownsBusy = false;
            bool needRepair = false;
            try
            {
                if (!UiAlive || _busy) return;
                if (DirtyCount > 0 && _all.Count > 0)
                {
                    SetStatus("有未保存改动；请先保存，再刷新", Theme.Warn);
                    Toast("先保存再刷新", "当前有改动还没生效，避免两边状态打架", Theme.Warn, 1);
                    return;
                }
                ownsBusy = true;
                BeginBusy();
                _spinUntil = DateTime.UtcNow.AddMilliseconds(650);
                SetStatus("正在读取软件与内核状态…", Theme.Dim);
                var pack = await Task.Run(() => LoadPack());
                if (!UiAlive) return;
                _state = pack.State;
                _all = pack.Rows;
                _lastLoad = DateTime.UtcNow;
                // 让 _all 本身也按当前排序方式排好：否则「界面看到的顺序」和「_all 的顺序」
                // 是两套，手动模式下再拖一次就会把之前的手动顺序覆盖掉。
                ApplySort(_all, Settings.Current.SortMode);
                _savedProxy = new HashSet<string>(_all.Where(r => r.Proxy).Select(r => r.Exe), StringComparer.OrdinalIgnoreCase);
                _list.SavedProxy = _savedProxy;
                Settings.Current.Tab = _tab;
                Settings.Save();
                RebuildVisible(true);
                SetStatus(_state.Error ?? (_state.Applied ? "名单已生效 · 改动后记得保存" : "名单未生效，请保存一次"),
                    _state.Error == null && _state.Applied ? Theme.Dim : Theme.Warn);
                // 名单不是空的、也没报错，但规则不见了 —— 只可能是客户端自己重新生成了配置
                // （换订阅、重启 Verge、改设置都会）。这时光提示「请保存一次」没用，
                // 用户会以为软件坏了或者代理失效了，干脆替他补回去。
                needRepair = !_state.Applied && _state.Error == null
                             && _savedProxy.Count > 0 && !_autoRepaired;
                UpdateLogContext();
            }
            catch (Exception ex) { ReportFailure("读取软件状态", ex); }
            finally { if (ownsBusy) FinishBusy(); }
            if (needRepair && UiAlive) RepairQuietly();
        }

        /// <summary>
        /// 刷新时顺手发现「名单还在、规则没了」就补一次。一次会话最多一次，
        /// 之后的持续看守交给 WatchAsync —— 那个会一直守着，包括收进托盘以后。
        /// </summary>
        void RepairQuietly()
        {
            // 准备阶段也要有保护：_all 在异常路径下可能是空的，抛出去就会变成「点了刷新结果进程没了」。
            try
            {
                var exes = _all.Where(r => r.Proxy).Select(r => r.Exe).ToList();
                if (exes.Count == 0 || _autoRepaired) return;
                _autoRepaired = true;
                _ = RepairAsync(exes, "客户端重新生成了配置", "已把规则补回来");
            }
            catch (Exception ex) { ReportFailure("自动补回规则", ex); }
        }

        // ---------------------------------------------------------------- 对账（自己发现 + 自己补回）

        void WatchTick()
        {
            if (!UiAlive || _busy || _watching) return;
            if (++_watchTick % 3 != 0) return;   // 10 秒 × 3 = 30 秒对一次账
            WatchAsync();
        }

        /// <summary>
        /// 隔一会儿去问内核一遍：我插进去的规则还在不在？界面上的「已生效」是不是还成立？
        ///
        /// 之所以要问：Clash 换订阅 / 更新订阅 / 重启 / 改一次设置都会重新生成配置，
        /// 我们插的规则会跟着消失，而界面还停在「上次保存」那张旧照片上。
        /// 用户看到的是「走代理 · 日本-B」，实际可能已经直连、或者换到别的节点上了。
        ///
        /// 只在「和用户明确表达的意图直接矛盾」时才动手，其余情况只记不修。
        /// </summary>
        async void WatchAsync()
        {
            bool owns = false;
            try
            {
                if (!UiAlive || _busy || _watching) return;

                // 面板没打开过时 _savedProxy 是空的，这时以磁盘上的名单为准；
                // 否则「开机就缩在托盘」的情况下，对账永远没有对象。
                var exes = new List<string>(_savedProxy);
                if (exes.Count == 0) exes = Profile.Load().Select(i => i.Exe).ToList();
                if (exes.Count == 0) return;

                owns = true;
                _watching = true;
                int revision = _revision;
                bool patch = Settings.PatchOnly;
                var audit = await Task.Run(() => Guard.Audit(exes));
                if (!UiAlive || revision != _revision) return;

                string why = Guard.Judge(audit, exes.Count, patch);

                // IPv6 单独算一件事：它坏了界面上完全看不出来，重推一次就能修好。
                // 看到「已经关着」就把计数清零，所以客户端下次重载又把它打开时还能再修；
                // 但连续三次都改不动就只记账不再试——那说明有别的东西在覆盖它。
                if (audit.Ok && !audit.Ipv6On) _ipv6Attempts = 0;
                if (why == null && audit.Ok && audit.Ipv6On && _ipv6Attempts < 3)
                    why = "内核的 IPv6 开着，流量可以从物理网卡直出";

                if (why == null) { _repairFails = 0; return; }
                if ((DateTime.UtcNow - _lastRepairAt).TotalSeconds < 30) return;
                if (_repairFails >= 3) return;

                if (audit.Ok && audit.Ipv6On) _ipv6Attempts++;
                AuditLog.Record("WATCH", "发现不一致：" + why);
                await RepairAsync(exes, why, "规则被冲掉了，已自动补回");
            }
            catch (Exception ex) { ReportFailure("核对规则", ex); }
            finally { if (owns) _watching = false; }
        }

        /// <summary>
        /// 真正去重推一次。成功、失败都记账；失败留在下一轮对账重试，连败 3 次就停手，
        /// 不无限重试——内核如果是被别的程序反复覆盖，重试多少次都没用。
        /// </summary>
        async Task RepairAsync(List<string> exes, string why, string title)
        {
            bool owns = false;
            try
            {
                if (!UiAlive || _busy) return;
                owns = true;
                BeginBusy();
                _lastRepairAt = DateTime.UtcNow;
                _repairFails++;

                var items = Profile.Compose(exes, null);
                var res = await Task.Run(() => Profile.Apply(items));
                if (!UiAlive) return;
                if (!res.Ok) throw new InvalidOperationException(res.Message ?? "内核没有确认");

                _state.Applied = true;
                _state.RuleCount = res.RuleCount;
                UpdateLogContext();
                AuditLog.Record("REPAIR", "第 " + _repairFails + " 次补回成功：" + exes.Count
                    + " 条规则，内核现有 " + res.RuleCount + " 条；原因：" + why);
                SetStatus("已自动补回 " + exes.Count + " 条规则", Theme.Dim);
                Toast(title, why, Theme.Accent, 2);
            }
            catch (Exception ex)
            {
                AuditLog.Record("FAIL", "补回失败（第 " + _repairFails + " 次）：" + why + " / " + ex.Message);
                ReportFailure("自动补回规则", ex);
                if (_repairFails >= 3)
                    Toast("自动补回没成功", "已经试了 3 次，点一下「保存并生效」手动试试", Theme.Warn, 1);
            }
            finally { if (owns) FinishBusy(); }
        }

        sealed class Pack
        {
            public Profile.KernelState State;
            public List<AppRow> Rows;
        }

        static Pack LoadPack()
        {
            var procs = Scanner.Scan();
            var policy = Profile.LoadPolicyMap();
            var installed = InstalledScanner.Scan();

            // 名字来源四路合流：已选名单 + 运行中 + 已安装（注册表/开始菜单）+ 常见候选
            var names = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var k in policy.Keys) if (seen.Add(k)) names.Add(k);
            foreach (var k in Catalog.Offline) if (seen.Add(k)) names.Add(k);
            foreach (var kv in procs)
                if (!seen.Contains(kv.Key) && Catalog.IsUserApp(kv.Key, kv.Value.Path) && seen.Add(kv.Key)) names.Add(kv.Key);
            foreach (var kv in installed) if (seen.Add(kv.Key)) names.Add(kv.Key);

            var rows = new List<AppRow>();
            foreach (var exe in names)
            {
                Scanner.ProcInfo info;
                procs.TryGetValue(exe, out info);
                InstalledInfo inst;
                installed.TryGetValue(exe, out inst);
                string pol;
                policy.TryGetValue(exe, out pol);
                bool proxy = "PP-FIXED".Equals(pol, StringComparison.OrdinalIgnoreCase);

                string name = Catalog.DisplayName(exe);
                if (!Catalog.HasNice(exe) && inst != null && !string.IsNullOrEmpty(inst.Name)) name = inst.Name;

                rows.Add(new AppRow
                {
                    Exe = exe,
                    Name = name,
                    Description = Catalog.Description(exe),
                    Path = info != null ? info.Path : (inst != null ? inst.Path : ""),
                    Alias = inst != null ? (inst.Name ?? "") : "",
                    Running = info != null ? info.Count : 0,
                    Proxy = proxy,
                    Known = pol != null,
                    Common = Catalog.IsCommon(exe) || (inst != null && Catalog.IsCommonByInstall(inst.Name)) ||
                             Settings.UsageOf(exe) > 0,
                    Anim = proxy ? 1f : 0f,
                    Usage = Settings.UsageOf(exe),
                    LastUsed = Settings.LastUsedOf(exe)
                });
            }
            return new Pack { State = Profile.ReadKernelState(), Rows = rows };
        }

        int DirtyCount
        {
            get
            {
                var now = new HashSet<string>(_all.Where(r => r.Proxy).Select(r => r.Exe), StringComparer.OrdinalIgnoreCase);
                int n = 0;
                foreach (var exe in now) if (!_savedProxy.Contains(exe)) n++;
                foreach (var exe in _savedProxy) if (!now.Contains(exe)) n++;
                return n;
            }
        }

        void ShowDirtyStatus()
        {
            var changed = new List<string>();
            foreach (var r in _all)
            {
                bool saved = _savedProxy.Contains(r.Exe);
                if (r.Proxy != saved) changed.Add(r.Name);
            }
            if (changed.Count == 0)
            {
                SetStatus("没有待保存改动", Theme.Dim);
                return;
            }
            string names = string.Join("、", changed.Take(3));
            if (changed.Count > 3) names += " 等 " + changed.Count + " 个";
            SetStatus(names + " · 保存后生效，不用重启软件", Theme.Warn);
        }

        sealed class SavePack
        {
            public Profile.ApplyOutcome Apply;
            public Profile.ConnectionSnapshot Snapshot;
            public Profile.ReconnectOutcome Reconnect;
        }

        async void SaveAsync()
        {
            bool ownsBusy = false;
            try
            {
                if (!UiAlive || _busy || _help) return;
                if (_all.Count == 0)
                {
                    Toast("还没有可保存的名单", "等列表读出来再试一次", Theme.Warn, 1);
                    return;
                }
                ownsBusy = true;
                BeginBusy();
                SetStatus("正在保存并更新分流…", Theme.Dim);
                var proxy = _all.Where(r => r.Proxy).Select(r => r.Exe).ToList();
                var direct = _all.Where(r => !r.Proxy).Select(r => r.Exe).ToList();
                var now = new HashSet<string>(proxy, StringComparer.OrdinalIgnoreCase);
                var changed = now.Where(x => !_savedProxy.Contains(x))
                    .Concat(_savedProxy.Where(x => !now.Contains(x))).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var items = Profile.Compose(proxy, direct);
                var pack = await Task.Run(() =>
                {
                    var p = new SavePack();
                    p.Snapshot = Profile.CaptureConnections(changed); // 先快照旧连接，再推新规则
                    p.Apply = Profile.Apply(items);
                    if (p.Apply.Ok && changed.Count > 0 && p.Snapshot.Error == null)
                        p.Reconnect = Profile.CloseCapturedConnections(p.Snapshot);
                    return p;
                });
                if (!UiAlive) return;

                if (pack.Apply.Ok)
                {
                    _savedProxy = now;
                    _list.SavedProxy = now;
                    foreach (var r in _all)
                    {
                        if (r.Proxy && !r.Known) Settings.Touch(r.Exe);
                        r.Known = r.Proxy;
                    }
                    _state.Applied = true; _state.RuleCount = pack.Apply.RuleCount;
                    _state.Policy = pack.Apply.Policy;
                    UpdateLogContext();
                    AuditLog.Record("SAVE", "手动保存 " + now.Count + " 个软件，内核现有 "
                        + pack.Apply.RuleCount + " 条规则，" + pack.Apply.Ms + "ms"
                        + (pack.Apply.Ipv6Off ? "，IPv6 已关" : "") + "；重连 " + changed.Count + " 个");

                    if (changed.Count == 0)
                    {
                        SetStatus("已生效 · 没有需要重连的软件 · " + pack.Apply.Ms + "ms", Theme.Ok);
                        Toast("已生效", "名单没有变化，规则已是最新 · " + pack.Apply.Ms + " ms", Theme.Ok, 0);
                    }
                    else if (pack.Snapshot.Error != null)
                    {
                        SetStatus("规则已生效，但旧连接自动清理失败；请新开页面", Theme.Warn);
                        Toast("规则已生效", "旧连接没能自动清理，新开一个页面即可", Theme.Warn, 1);
                    }
                    else
                    {
                        int closed = pack.Reconnect == null ? 0 : pack.Reconnect.Closed;
                        int failed = pack.Reconnect == null ? 0 : pack.Reconnect.Failed;
                        string msg = "已生效 · 重连 " + changed.Count + " 个软件 · 清理 " + closed + " 条旧连接";
                        if (failed > 0) msg += " · " + failed + " 条失败";
                        SetStatus(msg, failed > 0 ? Theme.Warn : Theme.Ok);
                        Toast(failed > 0 ? "已生效（部分旧连接未清理）" : "已生效，可以继续用了",
                            "重连 " + changed.Count + " 个软件 · 清理 " + closed + " 条旧连接 · " + pack.Apply.Ms + " ms",
                            failed > 0 ? Theme.Warn : Theme.Ok, failed > 0 ? 1 : 0);
                    }

                    // 名单变了，流量分类的基准也跟着变
                    PollTraffic();
                }
                else
                {
                    CrashLog.Write(new InvalidOperationException("应用返回失败，内核未确认生效"), "保存规则");
                    SetStatus(pack.Apply.Message, Theme.Warn);
                    Toast("没能生效", pack.Apply.Message, Theme.Warn, 1);
                }
            }
            catch (Exception ex)
            {
                ReportFailure("保存分流设置", ex);
            }
            finally { if (ownsBusy) FinishBusy(); }
        }

        void AllDirect()
        {
            if (!UiAlive || _busy) return;
            if (_all.Count == 0) return;
            foreach (var r in _all) r.Proxy = false;
            _list.Invalidate(); ShowDirtyStatus();
            Toast("已全部改为直连", "记得点保存，改动才会生效", Theme.Accent, 2);
        }

        public void AllDirectPublic() { AllDirect(); }

        // ---------------------------------------------------------------- 排序与过滤

        void ApplySort(List<AppRow> rows, string mode)
        {
            if (mode == "name")
            {
                rows.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
                return;
            }

            if (mode == "manual")
            {
                var order = Settings.Current.Order;
                var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < order.Count; i++) index[order[i]] = i;
                rows.Sort((a, b) =>
                {
                    int ia, ib;
                    bool ha = index.TryGetValue(a.Exe, out ia);
                    bool hb = index.TryGetValue(b.Exe, out ib);
                    if (ha && hb) return ia.CompareTo(ib);
                    if (ha) return -1;
                    if (hb) return 1;
                    int c = b.Common.CompareTo(a.Common);
                    if (c != 0) return c;
                    return string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
                });
                return;
            }

            // 智能：勾过的 > 常用的 > 正在运行的 > 最近用过的，其余按名字
            var score = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in rows)
            {
                int s = 0;
                if (r.Proxy) s += 1000;
                s += Math.Min(r.Usage, 12) * 60;
                if (r.Common) s += 90;
                if (r.Running > 0) s += 70;
                if (r.LastUsed > 0)
                {
                    double days = (DateTime.UtcNow.Ticks - r.LastUsed) / (double)TimeSpan.TicksPerDay;
                    if (days < 3) s += 120;
                    else if (days < 14) s += 40;
                }
                score[r.Exe] = s;
            }
            rows.Sort((a, b) =>
            {
                int c = score[b.Exe].CompareTo(score[a.Exe]);
                if (c != 0) return c;
                return string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
            });
        }

        void RebuildVisible(bool animate)
        {
            string q = (_search.Text ?? "").Trim();
            IEnumerable<AppRow> src;
            if (q.Length > 0)
            {
                src = _all.Where(r => r.Exe.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    r.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    r.Description.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    (r.Alias ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    (r.Path ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0);
                _list.EmptyText = "没有找到「" + q + "」";
                _list.EmptyHint = "试试只输入几个字母，比如 edge";
            }
            else
            {
                if (_tab == 0) src = _all.Where(r => r.Common || r.Proxy);
                else if (_tab == 1) src = _all.Where(r => r.Proxy);
                else if (_tab == 2) src = _all.Where(r => r.Running > 0);
                else src = _all;
                _list.EmptyText = _tab == 1 ? "还没有勾选软件" : _tab == 2 ? "当前没有可识别的运行软件" : "这里暂时没有软件";
                _list.EmptyHint = _tab == 1 ? "去「全部」里选几个，或在搜索框输入名字" : "换个标签，或直接在搜索框输入软件名";
            }

            var rows = src.Where(r => !Settings.HiddenOf(r.Exe) || r.Proxy).ToList();
            ApplySort(rows, Settings.Current.SortMode);
            _list.SetRows(rows, animate);
            Invalidate();
        }

        void OnReordered(object sender, ReorderEventArgs e)
        {
            AppRow row = _all.FirstOrDefault(r => string.Equals(r.Exe, e.Exe, StringComparison.OrdinalIgnoreCase));
            if (row == null) return;

            bool wasManual = Settings.Current.SortMode == "manual";

            if (!wasManual)
            {
                // 原来不是手动模式：把「眼前看到的顺序」整体落成全局顺序。
                // 只按锚点插回 _all 是不够的——_all 的顺序和当前标签页看到的顺序并不一样，
                // 那样存下来的手动顺序会和用户刚拖出来的结果对不上（这个坑踩过一次）。
                var byExe = new Dictionary<string, AppRow>(StringComparer.OrdinalIgnoreCase);
                foreach (var r in _all) if (!byExe.ContainsKey(r.Exe)) byExe[r.Exe] = r;

                var next = new List<AppRow>(_all.Count);
                foreach (var exe in _list.VisibleOrder)
                {
                    AppRow hit;
                    if (byExe.TryGetValue(exe, out hit)) { next.Add(hit); byExe.Remove(exe); }
                }
                foreach (var r in _all) if (byExe.ContainsKey(r.Exe)) next.Add(r);
                _all = next;
            }
            else
            {
                _all.Remove(row);
                int idx = _all.Count;
                if (!string.IsNullOrEmpty(e.BeforeExe))
                {
                    int found = _all.FindIndex(r => string.Equals(r.Exe, e.BeforeExe, StringComparison.OrdinalIgnoreCase));
                    if (found >= 0) idx = found;
                }
                if (idx < 0) idx = 0;
                if (idx > _all.Count) idx = _all.Count;
                _all.Insert(idx, row);
            }

            Settings.SetOrder(_all.Select(r => r.Exe));
            Toast(wasManual ? "顺序已保存" : "已切换为手动排序",
                wasManual ? "下次打开还是这个排法" : "想回到自动排序，点标签右侧的排序按钮",
                Theme.Accent, 2);
            Invalidate();
        }

        void CycleSortAsk()
        {
            if (!UiAlive || _busy) return;
            var menu = new ContextMenuStrip { ShowImageMargin = false };
            menu.Closed += (s, e) => menu.Dispose();
            string mode = Settings.Current.SortMode;
            Action<string, string> add = (key, text) =>
            {
                var item = new ToolStripMenuItem((mode == key ? "✓ " : "   ") + text);
                item.Click += (s, e) =>
                {
                    if (_busy) return;
                    Settings.Current.SortMode = key;
                    Settings.Save();
                    RebuildVisible(true);
                    Toast("排序方式已切换", text, Theme.Accent, 2);
                };
                menu.Items.Add(item);
            };
            add("smart", "智能排序（按使用习惯）");
            add("manual", "手动排序（拖动过的顺序）");
            add("name", "按名称排序");
            menu.Items.Add(new ToolStripSeparator());
            var reset = new ToolStripMenuItem("清空手动顺序");
            reset.Click += (s, e) =>
            {
                if (_busy) return;
                Settings.Current.Order.Clear();
                Settings.Current.SortMode = "smart";
                Settings.Save();
                RebuildVisible(true);
                Toast("已恢复自动排序", "手动拖出来的顺序已清空", Theme.Accent, 2);
            };
            menu.Items.Add(reset);
            menu.Show(this, new Point(Theme.Px(388), Theme.Px(TabsTop + 30)));
        }

        void ShowRowMenu(AppRow row, Point screen)
        {
            if (!UiAlive || _busy || row == null) return;
            var menu = new ContextMenuStrip { ShowImageMargin = false };
            menu.Closed += (s, e) => menu.Dispose();

            var toggle = new ToolStripMenuItem(row.Proxy ? "改为直连" : "改为走代理");
            toggle.Click += (s, e) =>
            {
                if (_busy) return;
                row.Proxy = !row.Proxy;
                _list.Invalidate();
                ShowDirtyStatus();
            };
            menu.Items.Add(toggle);

            var only = new ToolStripMenuItem("只代理这一个");
            only.Click += (s, e) =>
            {
                if (_busy) return;
                foreach (var r in _all) r.Proxy = false;
                row.Proxy = true;
                RebuildVisible(false);
                ShowDirtyStatus();
                Toast("已改为「只代理 " + row.Name + "」", "记得保存，改动才会生效", Theme.Accent, 2);
            };
            menu.Items.Add(only);

            menu.Items.Add(new ToolStripSeparator());

            var open = new ToolStripMenuItem("打开文件位置") { Enabled = !string.IsNullOrEmpty(row.Path) };
            open.Click += (s, e) =>
            {
                try { System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + row.Path + "\""); }
                catch (Exception ex) { Toast("打不开", ex.Message, Theme.Warn, 1); }
            };
            menu.Items.Add(open);

            var copy = new ToolStripMenuItem("复制进程名");
            copy.Click += (s, e) =>
            {
                try { Clipboard.SetText(row.Exe); Toast("已复制", row.Exe, Theme.Accent, 2); }
                catch { }
            };
            menu.Items.Add(copy);

            menu.Items.Add(new ToolStripSeparator());
            var hide = new ToolStripMenuItem("在列表中隐藏");
            hide.Click += (s, e) =>
            {
                if (_busy) return;
                Settings.Hide(row.Exe);
                RebuildVisible(false);
                Toast("已隐藏", row.Name + " · 帮助里可以恢复", Theme.Accent, 2);
            };
            menu.Items.Add(hide);

            menu.Show(_list, _list.PointToClient(screen));
        }

        // ---------------------------------------------------------------- 流量

        int _outTick;

        async void PollTraffic()
        {
            bool ownsPoll = false;
            try
            {
                if (!UiAlive || _busy || !Visible || _polling) return;
                ownsPoll = true;
                _polling = true;
                int revision = _revision;
                // 每五次采样顺手检查出口，两个请求各自防重入。
                if (++_outTick % 5 == 0) CheckOutbound();
                var set = new HashSet<string>(_all.Where(r => r.Proxy).Select(r => r.Exe), StringComparer.OrdinalIgnoreCase);
                var s = await Task.Run(() => Traffic.Poll(set));
                if (!UiAlive || !Visible || _busy || revision != _revision) return;
                _traffic = s;
                if (s.Ok && s.Seconds > 0)
                {
                    Push(_sparkProxy, (float)s.ProxyDown);
                    Push(_sparkDirect, (float)s.DirectDown);
                }
                if (s.Ok) Invalidate(new Rectangle(0, 0, Width, Theme.Px(SummaryTop + SummaryH + 4)));
                else Invalidate();
            }
            catch (Exception ex) { ReportFailure("读取网速", ex); }
            finally { if (ownsPoll) _polling = false; }
        }

        async void CheckOutbound()
        {
            bool ownsCheck = false;
            try
            {
                if (!UiAlive || _busy || _checkingOutbound) return;
                ownsCheck = true;
                _checkingOutbound = true;
                int revision = _revision;
                string now = await Task.Run(() => Core.Profile.ResolveOutbound());
                if (!UiAlive || _busy || revision != _revision || string.IsNullOrEmpty(now)) return;
                string prev = _state.Outbound;
                if (now == prev) return;
                _state.Outbound = now;
                Invalidate(new Rectangle(0, 0, Width, Theme.Px(SummaryTop + SummaryH + 4)));
                if (!string.IsNullOrEmpty(prev))
                    Toast("出口已切到 " + CleanEmoji(now), "勾选的软件新连接直接走这个节点", Theme.Accent, 2);
            }
            catch (Exception ex) { ReportFailure("读取代理出口", ex); }
            finally { if (ownsCheck) _checkingOutbound = false; }
        }

        // ---------------------------------------------------------------- 异常与忙碌状态

        bool UiAlive { get { return !_resourcesDisposed && !IsDisposed && !Disposing; } }

        void UpdateLogContext()
        {
            CrashLog.SetContext(_state.Client, _state.RuleCount, _savedProxy.Count);
        }

        void BeginBusy()
        {
            _busy = true;
            _revision++;
            UpdateLogContext();
            _list.Enabled = false;
            _search.Enabled = false;
            Wake();
        }

        void FinishBusy()
        {
            _busy = false;
            if (!UiAlive) return;
            try
            {
                _list.Enabled = true;
                _search.Enabled = true;
                if (_spinUntil < DateTime.UtcNow) _spinUntil = DateTime.UtcNow.AddMilliseconds(260);
                Wake(); Invalidate();
            }
            catch (Exception ex) { ReportFailure("恢复界面", ex); }
        }

        void ReportFailure(string operation, Exception ex)
        {
            try { UpdateLogContext(); } catch { }
            string path = CrashLog.Write(ex, operation);
            if (!UiAlive) return;
            try
            {
                SetStatus(operation + "失败，可以稍后重试", Theme.Warn);
                string key = operation + ":" + ex.GetType().FullName + ":" + ex.Message;
                if (_lastFailure == key && (DateTime.UtcNow - _lastFailureAt).TotalSeconds < 60) return;
                _lastFailure = key; _lastFailureAt = DateTime.UtcNow;
                Toast(operation + "没有完成", path == null ? "日志目录无法写入，请检查文件权限" : "已记录日志：" + path, Theme.Warn, 1);
            }
            catch { } // 控件销毁中的错误也不能再次逃出 async void
        }

        static void Push(List<float> list, float v)
        {
            list.Add(v);
            while (list.Count > 42) list.RemoveAt(0);
        }

        void SetStatus(string text, Color color)
        {
            _status = text ?? ""; _statusColor = color; Invalidate();
        }

        void Toast(string title, string sub, Color accent, int kind)
        {
            _toasts.Show(title, sub, accent, kind);
            Wake();
            _list.Invalidate();
        }

        // ---------------------------------------------------------------- 布局

        RectangleF SummaryRect { get { return new RectangleF(18, SummaryTop, WinW - 36, SummaryH); } }
        RectangleF SearchRect { get { return new RectangleF(18, SearchTop, WinW - 36, SearchH); } }
        RectangleF SaveRect { get { return new RectangleF(WinW - 18 - 122, WinH - 45, 122, 32); } }
        RectangleF DirectRect { get { return new RectangleF(WinW - 18 - 122 - 8 - 84, WinH - 45, 84, 32); } }
        RectangleF CloseRect { get { return new RectangleF(WinW - 42, 13, 26, 26); } }
        RectangleF MinRect { get { return new RectangleF(WinW - 76, 13, 26, 26); } }
        RectangleF RefreshRect { get { return new RectangleF(WinW - 110, 13, 26, 26); } }
        RectangleF HelpRect { get { return new RectangleF(WinW - 144, 13, 26, 26); } }
        RectangleF SortRect { get { return new RectangleF(WinW - 78, TabsTop, 60, 28); } }
        RectangleF HintRect { get { return new RectangleF(18, WinH - 42, 200, 24); } }
        RectangleF TabRect(int i) { return new RectangleF(18 + i * 92, TabsTop, 88, 28); }

        RectangleF HelpCard { get { return new RectangleF(24, 72, WinW - 48, 532); } }
        RectangleF HelpCloseRect { get { return new RectangleF(WinW - 24 - 34, 84, 24, 24); } }
        RectangleF HelpDiagRect { get { return new RectangleF(44, HelpCard.Bottom - 52, 118, 32); } }
        RectangleF HelpUnhideRect { get { return new RectangleF(170, HelpCard.Bottom - 52, 118, 32); } }
        RectangleF HelpModeRect { get { return new RectangleF(296, HelpCard.Bottom - 52, 138, 32); } }
        // 版本号挂在面板右上角、客户端名字下面一行：那一列本来就只放「谁在管分流」，放版本最自然
        RectangleF HelpUpdateRect { get { return new RectangleF(242, 108, 158, 15); } }

        void LayoutChildren()
        {
            int top = Theme.Px(ListTop);
            _list.SetBounds(0, top, ClientSize.Width, Math.Max(0, ClientSize.Height - top - Theme.Px(FooterH)));
            _search.SetBounds(Theme.Px(54), Theme.Px(SearchTop + 10),
                Math.Max(0, ClientSize.Width - Theme.Px(72)), Theme.Px(20));
        }

        // ---------------------------------------------------------------- 绘制

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Draw.Hint(g); g.Clear(Theme.Bg); g.ScaleTransform(Theme.S, Theme.S);
            Theme.Fill(g, Theme.Bg, new RectangleF(0, 0, WinW, WinH), 0f);

            using (var b = new LinearGradientBrush(
                new RectangleF(0, 0, WinW, TitleH + 6),
                Theme.Dark ? Theme.FromHex("#1A1E25") : Theme.FromHex("#FFFFFF"), Theme.Bg, 90f))
                g.FillRectangle(b, 0, 0, WinW, TitleH + 6);

            using (var icon = Draw.AppIcon(64)) g.DrawImage(icon, 18f, 14f, 24f, 24f);
            Draw.Text(g, "ProxyPicker", _fTitle, Theme.Fg, new RectangleF(50, 10, 220, 23));
            Draw.Text(g, Subtitle(), _fSub, Theme.Dim, new RectangleF(50, 32, 290, 17));

            DrawChromeButton(g, HelpRect, Btn.Help, "?");
            DrawRefresh(g);
            DrawMin(g);
            DrawClose(g);

            DrawSummary(g);

            // 搜索框：聚焦时描边变蓝，给一个很轻的「这里在等你输入」信号
            Theme.Fill(g, Theme.Card, SearchRect, 9f);
            bool focus = _search.Focused;
            Theme.Stroke(g, focus ? Theme.Accent : (Theme.Dark ? Theme.Line : Theme.FromHex("#DCE0E6")),
                SearchRect, 9f, focus ? 1.4f : 1f);
            Magnifier(g, SearchRect.X + 20, SearchRect.Y + SearchH / 2f, 5f, focus ? Theme.Accent : Theme.Dim);

            for (int i = 0; i < 4; i++)
            {
                var r = TabRect(i);
                bool active = _tab == i;
                if (!active && _hoverTab == i) Theme.Fill(g, Theme.Hover, r, 7f);
                if (active) Theme.Fill(g, Theme.AccentSoft, r, 7f);
                Draw.Text(g, TabText(i), active ? _fTabActive : _fTab, active ? Theme.Accent : Theme.Dim, r, Draw.Center);
            }

            DrawSortButton(g);

            if (_spinning)
            {
                // 顶部一条流动细光，表示「在干活」
                float w = 90f;
                float x = (_spin / 360f) * (WinW + w) - w;
                using (var b = new LinearGradientBrush(new RectangleF(x, TitleH - 2, w, 2),
                    Color.FromArgb(0, Theme.Accent), Theme.Accent, 0f))
                {
                    b.WrapMode = WrapMode.TileFlipXY;
                    g.FillRectangle(b, x, TitleH - 2, w, 2);
                }
            }

            using (var pen = new Pen(Theme.Line)) g.DrawLine(pen, 0, WinH - FooterH, WinW, WinH - FooterH);
            Draw.Text(g, _status, _fHint, _statusColor == Color.Empty ? Theme.Dim : _statusColor,
                new RectangleF(18, WinH - 72, WinW - 36, 20));
            Draw.Text(g, ClientTag(), _fTiny, Theme.Dim, HintRect);

            DrawSaveButton(g);

            if (_help) DrawHelp(g);
        }

        string Subtitle()
        {
            // 有新版本时先说这件事：平时那句「谁在管分流」在帮助面板里随时能看
            if (_update != null && _update.Ok && _update.Newer)
                return "有新版本 v" + _update.Latest + " · 点右上角「?」";
            var env = Discovery.Detect();
            if (!string.IsNullOrEmpty(_state.Client)) return _state.Client + " · 按软件分流";
            if (!env.Ok) return "还没找到 Clash / mihomo";
            return "只让需要的软件走代理";
        }

        string ClientTag()
        {
            if (!string.IsNullOrEmpty(_state.Client)) return _state.Client + " · 保存后无需重启浏览器";
            return "保存后无需重启浏览器";
        }

        void DrawSummary(Graphics g)
        {
            var card = SummaryRect;
            Draw.Shadow(g, card, 10f, Theme.Dark ? 60 : 22);
            Theme.Fill(g, Theme.Card, card, 10f);
            Theme.Stroke(g, Theme.Line, card, 10f);

            int selected = _all.Count(r => r.Proxy);
            Draw.Text(g, selected + " 个软件走代理", _fSummary, selected > 0 ? Theme.Accent : Theme.Fg,
                new RectangleF(32, SummaryTop + 7, 200, 24));
            Draw.Text(g, Settings.PatchOnly ? "其余软件按原规则" : "其余软件默认直连", _fSmall, Theme.Dim,
                new RectangleF(32, SummaryTop + 33, 190, 16));

            // 第三行：左边内核状态，右边用胶囊直接摆出「勾选软件实际走哪个节点」。
            //
            // 为什么单独立一格：PP-FIXED 指向主选择组后，真正生效的是这条链路末端，
            // 而 GLOBAL 的选择可能完全不是同一个节点。以前只显示 GLOBAL，
            // 用户会以为「我明明换了节点」，界面却纹丝不动 —— 看起来就像贴图。
            string meta = "TUN " + (_state.Tun ? "开" : "关") + " · " + (_state.Mode ?? "?");
            string outb = string.IsNullOrEmpty(_state.Outbound) ? _state.Node : _state.Outbound;
            if (string.IsNullOrEmpty(outb))
            {
                Draw.Text(g, meta, _fTiny, Theme.Dim, new RectangleF(32, SummaryTop + 54, 200, 16));
            }
            else
            {
                Draw.Text(g, meta, _fTiny, Theme.Dim, new RectangleF(32, SummaryTop + 54, 96, 16));

                string label = "出口 " + CleanEmoji(outb);
                var fp = _fPill ?? (_fPill = Theme.Ui(9.5f));
                float bw = Math.Min(g.MeasureString(label, fp).Width + 18f, 134f);
                var pill = new RectangleF(242 - bw, SummaryTop + 52, bw, 20);
                Theme.Fill(g, Theme.AccentSoft, pill, 10f);
                Draw.Text(g, label, fp, Theme.Accent, pill, Draw.Center);
            }

            // 右列：两条通道的实时速率 + 迷你走势
            float rx = 244, rw = WinW - 18 - 12 - rx;
            Draw.Text(g, "代理", _fTiny, Theme.Dim, new RectangleF(rx, SummaryTop + 5, 26, 15));
            Draw.Text(g, _traffic == null || !_traffic.Ok ? "—" :
                "↓ " + Traffic.Speed(_traffic.ProxyDown) + "   ↑ " + Traffic.Speed(_traffic.ProxyUp),
                _fRate, Theme.Accent, new RectangleF(rx + 28, SummaryTop + 4, rw - 28, 16), Draw.Right);

            Draw.Text(g, "直连", _fTiny, Theme.Dim, new RectangleF(rx, SummaryTop + 25, 26, 15));
            Draw.Text(g, _traffic == null || !_traffic.Ok ? "—" :
                "↓ " + Traffic.Speed(_traffic.DirectDown) + "   ↑ " + Traffic.Speed(_traffic.DirectUp),
                _fRate, Theme.Dim, new RectangleF(rx + 28, SummaryTop + 24, rw - 28, 16), Draw.Right);

            var sparkRect = new RectangleF(rx, SummaryTop + 45, rw, 26);
            float peak = 1024f;
            if (_traffic != null)
                peak = (float)Math.Max(peak, Math.Max(_traffic.ProxyDown, _traffic.DirectDown));
            if (_sparkProxy.Count >= 2) Draw.Spark(g, _sparkProxy.ToArray(), sparkRect, Theme.Accent, peak);
            if (_sparkDirect.Count >= 2) Draw.Spark(g, _sparkDirect.ToArray(), sparkRect, Theme.Dim, peak);

            if (_traffic != null && !_traffic.Ok)
                Draw.Text(g, "流量读取中…", _fTiny, Theme.Dim, sparkRect, Draw.Right);
        }

        void DrawSortButton(Graphics g)
        {
            var r = SortRect;
            bool hover = _hover == Btn.Sort;
            Theme.Fill(g, hover ? Theme.Hover : (Theme.Dark ? Theme.Btn : Theme.FromHex("#F1F3F6")), r, 7f);
            string mode = Settings.Current.SortMode;
            string label = mode == "manual" ? "手动" : mode == "name" ? "名称" : "智能";
            Color c = mode == "manual" ? Theme.Accent : Theme.Dim;

            // 三条长短线 = 排序图标
            using (var pen = new Pen(c, 1.3f))
            {
                pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round;
                float x = r.X + 10, y = r.Y + 10;
                g.DrawLine(pen, x, y, x + 9, y);
                g.DrawLine(pen, x, y + 4, x + 6, y + 4);
                g.DrawLine(pen, x, y + 8, x + 3, y + 8);
            }
            Draw.Text(g, label, _fTiny, c, new RectangleF(r.X + 22, r.Y, 34, r.Height));
        }

        void DrawChromeButton(Graphics g, RectangleF r, Btn btn, string text)
        {
            if (_hover == btn) Theme.Fill(g, Theme.Hover, r, 7f);
            using (var font = Theme.Ui(btn == Btn.Help ? 13f : 18f))
                Draw.Text(g, text, font, _hover == btn ? Theme.Fg : Theme.Dim, r, Draw.Center);
        }

        void DrawRefresh(Graphics g)
        {
            var r = RefreshRect;
            if (_hover == Btn.Refresh) Theme.Fill(g, Theme.Hover, r, 7f);
            Draw.RefreshArrow(g, r.X + 13, r.Y + 13, 7f, _spin, _hover == Btn.Refresh ? Theme.Fg : Theme.Dim);
        }

        void DrawClose(Graphics g)
        {
            if (_hover == Btn.Close) Theme.Fill(g, Theme.Hover, CloseRect, 7f);
            using (var pen = new Pen(_hover == Btn.Close ? Theme.Fg : Theme.Dim, 1.4f))
            {
                float cx = CloseRect.X + 13, cy = CloseRect.Y + 13, d = 4.6f;
                g.DrawLine(pen, cx - d, cy - d, cx + d, cy + d);
                g.DrawLine(pen, cx + d, cy - d, cx - d, cy + d);
            }
        }

        /// <summary>
        /// 最小化（收起）。注意这个程序和普通窗口不一样：没有任务栏按钮（ShowInTaskbar=false），
        /// 所以「最小化」的实际含义是收到右下角托盘，不是缩到任务栏。图标仍用标准的一条横线，
        /// 因为那才是用户眼睛在找的东西。
        /// </summary>
        void DrawMin(Graphics g)
        {
            if (_hover == Btn.Min) Theme.Fill(g, Theme.Hover, MinRect, 7f);
            using (var pen = new Pen(_hover == Btn.Min ? Theme.Fg : Theme.Dim, 1.4f))
            {
                // 比叉号略宽一点：叉是对角线，视觉上本来就比同长横线显得大
                float cx = MinRect.X + 13, cy = MinRect.Y + 14, d = 5.4f;
                g.DrawLine(pen, cx - d, cy, cx + d, cy);
            }
        }

        void DrawSaveButton(Graphics g)
        {
            bool saveHover = _hover == Btn.Save && !_busy;
            var saveBg = _busy ? Theme.Blend(Theme.Accent, Theme.Bg, 0.45f)
                       : saveHover ? Theme.Blend(Theme.Accent, Color.White, 0.12f) : Theme.Accent;
            if (!_busy) Draw.Shadow(g, SaveRect, 8f, Theme.Dark ? 40 : 26);
            Theme.Fill(g, saveBg, SaveRect, 8f);
            int dirty = DirtyCount;
            Draw.Text(g, _busy ? "处理中…" : dirty > 0 ? "保存 " + dirty + " 项改动" : "保存并生效",
                _fBtn, Color.White, SaveRect, Draw.Center);

            bool directHover = _hover == Btn.Direct;
            Theme.Fill(g, directHover ? Theme.BtnHover : Theme.Btn, DirectRect, 8f);
            Theme.Stroke(g, Theme.Line, DirectRect, 8f);
            Draw.Text(g, "全部直连", _fTab, Theme.Fg, DirectRect, Draw.Center);
        }

        void DrawHelp(Graphics g)
        {
            using (var b = new SolidBrush(Color.FromArgb(150, 0, 0, 0)))
                g.FillRectangle(b, 0, 0, WinW, WinH);

            var card = HelpCard;
            Draw.Shadow(g, card, 14f, 90);
            Theme.Fill(g, Theme.Card, card, 14f);
            Theme.Stroke(g, Theme.Line, card, 14f);

            Draw.Text(g, "怎么用", _fHelpTitle, Theme.Fg, new RectangleF(44, 88, 200, 24));
            Draw.Text(g, "一分钟看完，后面不用再查", _fSmall, Theme.Dim, new RectangleF(44, 112, 260, 16));

            var envInfo = Discovery.Detect();
            Draw.Text(g, envInfo.Ok ? envInfo.Client : "未检测到客户端", _fTiny, Theme.Dim,
                new RectangleF(HelpCard.Right - 160, 92, 118, 16), Draw.Right);

            // 版本号：没接仓库就只显示号码；接了就变成可点的「检查更新」；
            // 查到新版本则直接写成「v1.0.0 → v1.1.0 去下载」，点了打开网页，不自动替换。
            bool uHover = _hover == Btn.HelpUpdate;
            string verText;
            Color verColor;
            if (_update != null && _update.Ok && _update.Newer)
            {
                verText = "v" + Updater.CurrentText + " → v" + _update.Latest + " 去下载";
                verColor = Theme.Accent;
            }
            else if (Updater.Enabled)
            {
                verText = "v" + Updater.CurrentText + (_updateBusy ? " · 检查中…" : " · 检查更新");
                verColor = uHover ? Theme.Accent : Theme.Dim;
            }
            else
            {
                verText = "v" + Updater.CurrentText;
                verColor = Theme.Dim;
            }
            Draw.Text(g, verText, _fTiny, verColor, HelpUpdateRect, Draw.Right);

            if (_hover == Btn.HelpClose) Theme.Fill(g, Theme.Hover, HelpCloseRect, 6f);
            using (var pen = new Pen(_hover == Btn.HelpClose ? Theme.Fg : Theme.Dim, 1.4f))
            {
                float cx = HelpCloseRect.X + 12, cy = HelpCloseRect.Y + 12, d = 4.2f;
                g.DrawLine(pen, cx - d, cy - d, cx + d, cy + d);
                g.DrawLine(pen, cx + d, cy - d, cx - d, cy + d);
            }

            string[][] items =
            {
                new[] { "开 = 走代理，关 = 直连", "改完点右下角「保存并生效」。只重连这次改到的软件，其他连接不断，也不用重启浏览器。" },
                new[] { "四个标签只是在换视角", "常用放高频软件；全部里能找到所有已安装程序；以后新装的软件，下次打开会自动出现。" },
                new[] { "顺序可以拖", "按住任意一行上下拖动即可排序；点标签右侧按钮，能切回「智能」按使用习惯自动排。" },
                new[] { "右键有更多", "打开文件位置、复制进程名、只代理这一个、在列表中隐藏。" },
                new[] { "换电脑直接用", "会自动识别常见客户端。新电脑默认「只改勾的」，不动订阅里原有的规则；想完全说了算，点下面的模式按钮。" }
            };

            float y = 142;
            for (int i = 0; i < items.Length; i++)
            {
                var badge = new RectangleF(44, y + 1, 18, 18);
                using (var b = new SolidBrush(Theme.AccentSoft)) g.FillEllipse(b, badge);
                using (var font = Theme.Ui(10f, FontStyle.Bold))
                    Draw.Text(g, (i + 1).ToString(), font, Theme.Accent, badge, Draw.Center);

                Draw.Text(g, items[i][0], _fTabActive, Theme.Fg,
                    new RectangleF(72, y, card.Width - 60, 18));
                Draw.TextWrap(g, items[i][1], _fHelp, Theme.Dim,
                    new RectangleF(72, y + 19, card.Width - 68, 56));
                y += 76;
            }

            // 底部：诊断
            var line = new RectangleF(44, card.Bottom - 68, card.Width - 40, 1);
            using (var pen = new Pen(Theme.Line)) g.DrawLine(pen, line.X, line.Y, line.Right, line.Y);

            bool dh = _hover == Btn.HelpDiag;
            Theme.Fill(g, dh ? Theme.BtnHover : Theme.Btn, HelpDiagRect, 8f);
            Theme.Stroke(g, Theme.Line, HelpDiagRect, 8f);
            using (var font = Theme.Ui(11f))
                Draw.Text(g, "复制诊断信息", font, Theme.Fg, HelpDiagRect, Draw.Center);

            bool uh = _hover == Btn.HelpUnhide;
            Theme.Fill(g, uh ? Theme.BtnHover : Theme.Btn, HelpUnhideRect, 8f);
            Theme.Stroke(g, Theme.Line, HelpUnhideRect, 8f);
            using (var font = Theme.Ui(11f))
                Draw.Text(g, "恢复隐藏项", font, Theme.Fg, HelpUnhideRect, Draw.Center);

            // 分流模式开关：文字本身就是当前状态，点一下在两种之间切。
            // 「完全接管」用强调色描边，因为它会覆盖订阅原本的分流，属于更有存在感的那一档。
            bool mh = _hover == Btn.HelpMode;
            bool patch = Settings.PatchOnly;
            Theme.Fill(g, mh ? Theme.BtnHover : Theme.Btn, HelpModeRect, 8f);
            Theme.Stroke(g, patch ? Theme.Line : Theme.Accent, HelpModeRect, 8f);
            Draw.Text(g, patch ? "模式：只改勾的" : "模式：完全接管",
                _fHint, patch ? Theme.Fg : Theme.Accent, HelpModeRect, Draw.Center);
        }

        static string Ellipsis(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= max ? s : s.Substring(0, max - 1) + "…";
        }

        static string CleanEmoji(string s)
        {
            if (string.IsNullOrEmpty(s)) return s ?? "";
            var sb = new System.Text.StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) { i++; continue; }
                if (c == 0x200D || (c >= 0xFE00 && c <= 0xFE0F)) continue;
                sb.Append(c);
            }
            return sb.ToString().Trim();
        }

        string TabText(int i)
        {
            if (i == 0) return "常用";
            if (i == 1) return "已选 " + _all.Count(r => r.Proxy);
            if (i == 2) return "运行中";
            return "全部";
        }

        void Magnifier(Graphics g, float cx, float cy, float r, Color color)
        {
            using (var pen = new Pen(color, 1.5f))
            {
                pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round;
                g.DrawEllipse(pen, cx - r, cy - r, r * 2, r * 2);
                g.DrawLine(pen, cx + r * .72f, cy + r * .72f, cx + r * 1.6f, cy + r * 1.6f);
            }
        }

        // ---------------------------------------------------------------- 交互

        Btn HitButton(Point p)
        {
            float x = p.X / Theme.S, y = p.Y / Theme.S;

            if (_help)
            {
                if (HelpCloseRect.Contains(x, y)) return Btn.HelpClose;
                if (HelpDiagRect.Contains(x, y)) return Btn.HelpDiag;
                if (HelpUnhideRect.Contains(x, y)) return Btn.HelpUnhide;
                if (HelpModeRect.Contains(x, y)) return Btn.HelpMode;
                if (Updater.Enabled && HelpUpdateRect.Contains(x, y)) return Btn.HelpUpdate;
                return HelpCard.Contains(x, y) ? Btn.None : Btn.HelpClose;
            }

            if (CloseRect.Contains(x, y)) return Btn.Close;
            if (MinRect.Contains(x, y)) return Btn.Min;
            if (RefreshRect.Contains(x, y)) return Btn.Refresh;
            if (HelpRect.Contains(x, y)) return Btn.Help;
            if (SaveRect.Contains(x, y)) return Btn.Save;
            if (DirectRect.Contains(x, y)) return Btn.Direct;
            if (SortRect.Contains(x, y)) return Btn.Sort;
            return Btn.None;
        }

        int HitTab(Point p)
        {
            if (_help) return -1;
            float x = p.X / Theme.S, y = p.Y / Theme.S;
            for (int i = 0; i < 4; i++) if (TabRect(i).Contains(x, y)) return i;
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var b = HitButton(e.Location); int tab = HitTab(e.Location);
            if (b != _hover || tab != _hoverTab)
            {
                _hover = b; _hoverTab = tab;
                Cursor = b != Btn.None || tab >= 0 ? Cursors.Hand : Cursors.Default;
                Invalidate();
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            var b = HitButton(e.Location);
            if (b == Btn.Close) { HideToTray(); return; }
            if (b == Btn.Min) { MinimizeToTray(); return; }
            if (b == Btn.Refresh) { RefreshAsync(); return; }
            if (b == Btn.Help) { OpenHelp(); return; }
            if (b == Btn.Save) { SaveAsync(); return; }
            if (b == Btn.Direct) { AllDirect(); return; }
            if (b == Btn.Sort) { CycleSortAsk(); return; }
            if (b == Btn.HelpClose) { CloseHelp(); return; }
            if (b == Btn.HelpDiag) { CopyDiagnostics(); return; }
            if (b == Btn.HelpUnhide) { UnhideAll(); return; }
            if (b == Btn.HelpMode) { ToggleMode(); return; }
            if (b == Btn.HelpUpdate) { VersionClick(); return; }

            int tab = HitTab(e.Location);
            if (tab >= 0) { _tab = tab; Settings.Current.Tab = tab; Settings.Save(); RebuildVisible(true); return; }
            if (!_help && e.Y / Theme.S < TitleH)
            {
                ReleaseCapture(); SendMessage(Handle, WM_NCLBUTTONDOWN, new IntPtr(HTCAPTION), IntPtr.Zero);
            }
        }

        void OpenHelp()
        {
            _help = true;
            _list.Visible = false;
            _search.Visible = false;
            Invalidate();
        }

        void CloseHelp()
        {
            _help = false;
            _list.Visible = true;
            _search.Visible = true;
            // 面板里切过模式就补一句话：切换只改了偏好，真正生效还差一次保存
            if (_modeChanged)
            {
                _modeChanged = false;
                bool patch = Settings.PatchOnly;
                Toast(patch ? "已切到「只改勾的」" : "已切到「完全接管」",
                    patch ? "没勾的软件按订阅规则走 · 记得点保存" : "没勾的软件一律直连 · 记得点保存",
                    patch ? Theme.Accent : Theme.Warn, 2);
            }
            Invalidate();
        }

        /// <summary>
        /// 切换「没勾的软件」怎么走。切完不自动推送——内核里还是上一次的规则，
        /// 必须点一次保存才会改。只改偏好并提醒，免得用户以为已经生效。
        /// </summary>
        void ToggleMode()
        {
            if (!UiAlive || _busy) return;
            bool toWhitelist = Settings.PatchOnly;
            Settings.SetMode(toWhitelist ? Settings.ModeWhitelist : Settings.ModePatch);
            _state.Applied = false;
            _modeChanged = true;
            SetStatus("分流模式已切换，点「保存并生效」才会应用", Theme.Warn);
            Invalidate();
        }

        // ---------------------------------------------------------------- 版本

        /// <summary>
        /// 点版本号：已经查到新版本就直接开网页，否则先查一次。
        /// 只开浏览器，不下载、不替换、不写任何文件——所以点错了也不会出事。
        /// </summary>
        void VersionClick()
        {
            try
            {
                if (_update != null && _update.Ok && _update.Newer && !string.IsNullOrEmpty(_update.Url))
                {
                    try
                    {
                        System.Diagnostics.Process.Start(_update.Url);
                        Toast("已在浏览器打开", "下载后自己替换 exe 就行，程序不会去改自己", Theme.Accent, 2);
                    }
                    catch (Exception ex) { Toast("打不开浏览器", ex.Message, Theme.Warn, 1); }
                    return;
                }
                if (_updateBusy) return;
                CheckUpdateAsync(true);
            }
            catch (Exception ex) { ReportFailure("打开版本信息", ex); }
        }

        /// <summary>
        /// 查一次新版本。manual=false 时受「每天最多一次」和开关约束；
        /// 失败一律安静收场——没开代理时连不上 GitHub 是常态，不该弹窗骂人。
        /// </summary>
        async void CheckUpdateAsync(bool manual)
        {
            bool ownsBusy = false;
            try
            {
                if (!UiAlive || !Updater.Enabled) return;
                if (_updateBusy) return;
                if (!manual && !Settings.ShouldCheckUpdate(24)) return;

                ownsBusy = _updateBusy = true;
                if (manual) SetStatus("正在检查新版本…", Theme.Dim);
                Invalidate();

                var info = await Task.Run(() => Updater.Check(6000));
                if (!UiAlive) return;

                _update = info;
                Settings.MarkUpdateChecked();   // 失败也记：离线时不要每次打开面板都白等 6 秒
                if (info.Ok && info.Newer)
                {
                    AuditLog.Record("UPDATE", "发现新版本 v" + info.Latest);
                    Toast("有新版本 v" + info.Latest, "点帮助面板里的版本号去下载", Theme.Accent, 2);
                }
                else if (manual)
                {
                    if (info.Ok) Toast("已经是最新版本", "v" + Updater.CurrentText, Theme.Ok, 0);
                    else Toast("这次没查到", info.Error, Theme.Warn, 1);
                }
                if (manual) ShowDirtyStatus();
            }
            catch (Exception ex) { ReportFailure("检查新版本", ex); }
            finally
            {
                if (ownsBusy)
                {
                    _updateBusy = false;
                    try { if (UiAlive) Invalidate(); } catch { }
                }
            }
        }

        /// <summary>托盘菜单里的「检查更新」入口。</summary>
        public void CheckUpdatePublic()
        {
            if (!Updater.Enabled) return;
            CheckUpdateAsync(true);
        }

        void CopyDiagnostics()
        {
            try
            {
                string text = Discovery.Diagnose();
                // 出问题时最有用的一句话不是「它坏了」，而是「它几点几分改了什么」。
                string recent = AuditLog.Recent(10);
                if (recent.Length > 0) text += "\n---- 最近动作 ----\n" + recent;
                Clipboard.SetText(text);
                Toast("诊断信息已复制", "粘贴给排查的人就能看到卡在哪", Theme.Ok, 0);
            }
            catch (Exception ex) { Toast("复制失败", ex.Message, Theme.Warn, 1); }
        }

        void UnhideAll()
        {
            Settings.UnhideAll();
            RebuildVisible(true);
            Toast("已恢复隐藏项", "被隐藏的软件回到列表了", Theme.Ok, 0);
        }

        protected override void OnResize(EventArgs e) { base.OnResize(e); LayoutChildren(); }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_resourcesDisposed)
            {
                _resourcesDisposed = true;
                _clock.Stop(); _clock.Dispose();
                _trafficTimer.Stop(); _trafficTimer.Dispose();
                _watchTimer.Stop(); _watchTimer.Dispose();
                _fTitle.Dispose(); _fSub.Dispose(); _fSummary.Dispose();
                if (_fPill != null) _fPill.Dispose();
                _fBtn.Dispose(); _fHint.Dispose(); _fSmall.Dispose(); _fInput.Dispose();
                _fTab.Dispose(); _fTabActive.Dispose(); _fRate.Dispose(); _fTiny.Dispose();
                _fHelp.Dispose(); _fHelpTitle.Dispose();
                if (_windowIcon != null) _windowIcon.Dispose();
            }
            base.Dispose(disposing);
        }

        void OnFormKeyDown(object sender, KeyEventArgs e)
        {
            if (_help)
            {
                if (e.KeyCode == Keys.Escape || e.KeyCode == Keys.F1) { CloseHelp(); e.SuppressKeyPress = true; }
                return;
            }

            if (e.KeyCode == Keys.Escape)
            {
                if (!string.IsNullOrEmpty(_search.Text)) { _search.Text = ""; e.SuppressKeyPress = true; return; }
                HideToTray(); return;
            }
            if (e.KeyCode == Keys.F1) { OpenHelp(); e.SuppressKeyPress = true; return; }
            if (e.KeyCode == Keys.Down) { _list.MoveSelection(1); e.SuppressKeyPress = true; return; }
            if (e.KeyCode == Keys.Up) { _list.MoveSelection(-1); e.SuppressKeyPress = true; return; }
            if (e.KeyCode == Keys.Space) { _list.ToggleSelected(); e.SuppressKeyPress = true; return; }
            if (e.Control && e.KeyCode == Keys.S) { SaveAsync(); e.SuppressKeyPress = true; return; }
            if (e.Control && e.KeyCode == Keys.F) { _search.Focus(); e.SuppressKeyPress = true; }
        }
    }
}
