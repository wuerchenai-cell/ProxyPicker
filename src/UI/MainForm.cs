using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using ProxyPicker.Core;

namespace ProxyPicker.UI
{
    internal sealed class MainForm : Form
    {
        internal const float DefaultWinW = 880f;
        internal const float DefaultWinH = 600f;
        internal const float MinWinW = 466f;
        internal const float MinWinH = 540f;
        const float EdgeHitPx = 7f;
        const float TitleH = 46f;
        const float NavTop = 48f;
        const float NavH = 34f;
        const float SummaryTop = 88f;
        const float SummaryHBase = 74f;
        const float SummaryHWithStatic = 92f;
        const float SearchH = 34f;
        const float FooterH = 66f;
        const float FooterSlimH = 34f;

        internal static float SummaryHeightFor(bool showStatic) { return showStatic ? SummaryHWithStatic : SummaryHBase; }
        internal static float SearchTopFor(bool showStatic) { return SummaryTop + SummaryHeightFor(showStatic) + 6f; }
        internal static float TabsTopFor(bool twoRow, bool showStatic)
        {
            float st = SearchTopFor(showStatic);
            return twoRow ? (st + 40f) : (st + 3f);
        }
        internal static float ListTopFor(bool twoRow, bool showStatic)
        {
            return TabsTopFor(twoRow, showStatic) + (twoRow ? 34f : 37f);
        }

        bool ShowStaticRow { get { return StaticProxy.IsEnabledAndValid; } }
        float SummaryH { get { return SummaryHeightFor(ShowStaticRow); } }
        float SearchTop { get { return SearchTopFor(ShowStaticRow); } }
        bool NarrowShell { get { return LogicalW < 640f; } }
        bool CompactToolbar { get { return LogicalW < 640f; } }
        float TabsTop { get { return TabsTopFor(CompactToolbar, ShowStaticRow); } }
        float ListTop { get { return ListTopFor(CompactToolbar, ShowStaticRow); } }

        [DllImport("user32.dll")] static extern bool ReleaseCapture();
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", EntryPoint = "SendMessageW", CharSet = CharSet.Unicode)]
        static extern IntPtr SendMessageText(IntPtr hWnd, int msg, IntPtr wParam, string lParam);
        [DllImport("dwmapi.dll", PreserveSig = true)]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        const int WM_NCLBUTTONDOWN = 0xA1;
        const int WM_GETMINMAXINFO = 0x0024;
        const int WM_NCHITTEST = 0x0084;
        const int HTCAPTION = 2;
        const int HTCLIENT = 1;
        const int HTLEFT = 10;
        const int HTRIGHT = 11;
        const int HTTOP = 12;
        const int HTTOPLEFT = 13;
        const int HTTOPRIGHT = 14;
        const int HTBOTTOM = 15;
        const int HTBOTTOMLEFT = 16;
        const int HTBOTTOMRIGHT = 17;
        const int EM_SETCUEBANNER = 0x1501;

        [StructLayout(LayoutKind.Sequential)]
        struct POINT { public int X; public int Y; }

        [StructLayout(LayoutKind.Sequential)]
        struct MINMAXINFO
        {
            public POINT ptReserved;
            public POINT ptMaxSize;
            public POINT ptMaxPosition;
            public POINT ptMinTrackSize;
            public POINT ptMaxTrackSize;
        }

        readonly AppList _list = new AppList();
        readonly NodesPage _nodesPage = new NodesPage();
        readonly QualityPage _qualityPage = new QualityPage();
        readonly TextBox _search = new TextBox();
        readonly ToastHost _toasts = new ToastHost();
        // 走势图靠「时间轴推进」而不是「每秒挪一格」，帧率决定连续感：
        // 30fps 在 150% 缩放下仍能看出台阶，回到 60fps，并用 timeBeginPeriod(1)
        // 把系统定时器精度提上来（否则 WM_TIMER 只有 15.6ms 粒度，帧间隔忽长忽短，本身就是卡顿源）。
        readonly Timer _clock = new Timer { Interval = 16 };
        readonly Timer _trafficTimer = new Timer { Interval = 1000 };
        // 对账用的慢时钟：每 10 秒醒一次，每三次（30 秒）才真去问内核一遍。
        // 用独立计时器而不是挂在 _trafficTimer 上，是因为收进托盘后流量计时器会停，
        // 而「换订阅后自己发现」这件事恰恰要在收进托盘时也能工作。
        readonly Timer _watchTimer = new Timer { Interval = 10000 };

        Font _fTitle, _fSub, _fSummary, _fBtn, _fHint, _fSmall, _fInput,
             _fTab, _fTabActive, _fRate, _fTiny, _fHelp, _fHelpTitle;
        Font _fPill;   // 出口胶囊，懒加载

        enum Btn { None, Nav0, Nav1, Nav2, Save, Direct, Refresh, Close, Max, Help, Sort, HelpClose, HelpDiag, HelpUnhide, HelpMode, HelpIpv6, Min, HelpUpdate, Mismatch, Delay, StaticDelay, ModeChip }
        Btn _hover = Btn.None;
        int _page;          // 一级功能区：0=应用分流 1=节点策略 2=链路体检
        readonly Stack<int> _pageHistory = new Stack<int>();
        int _tab;
        int _hoverTab = -1;
        bool _busy;
        bool _restoringSize = true;
        bool _pendingRefresh;
        bool _ipv6Busy;
        bool _polling;
        bool _checkingOutbound;
        bool _resourcesDisposed;
        bool _timerRaised;   // 已把系统定时器精度提到 1ms，退出时要还回去
        int _revision;
        int _catalogSeq;
        readonly Icon _windowIcon;      // 小图标（任务栏按钮 / Alt+Tab）
        readonly Icon _windowIconBig;   // 大图标（Alt+Tab 预览、任务视图）
        readonly Bitmap _titleIcon;
        string _lastFailure;
        DateTime _lastFailureAt;
        bool _realExit;
        bool _help;
        bool _nodesLoadedOnce;
        NodeQuality.QualityReport _lastQualityReport;
        RectangleF _modeChipRect;
        bool _modeChanged;   // 在帮助面板里切过分流模式，关闭时提醒去保存
        volatile bool _shuttingDown;   // 退出还原进行中：禁止再起保存任务（v1.3.7）
        bool _compatHintShown; // 「订阅兼容下取消勾选≠直连」只提示一次，不反复弹
        bool _ipv6Changed;   // 在帮助面板里切过 IPv6 防泄漏，关闭时提醒去保存
        Updater.Info _update;      // null = 还没查过；查过之后失败也留着，用来解释为什么没提示
        string _lastEnvWarn;       // 上次弹过的多客户端提醒文字；同一条不重复弹
        bool _updateBusy;
        bool _updateSessionDone;  // 本次运行已经自动查过一次，别每次打开面板都去问
        string _notifiedVersion;  // 本次运行已为哪个版本弹过提示，避免联网复查重复打扰
        sealed class EffectiveRules
        {
            public readonly HashSet<string> Exes;
            public readonly List<Regex> PathRegexes;
            public readonly HashSet<string> StaticExes;
            public readonly List<Regex> StaticPathRegexes;

            public EffectiveRules(HashSet<string> exes, List<Regex> pathRegexes,
                                  HashSet<string> staticExes = null, List<Regex> staticPathRegexes = null)
            {
                Exes = exes ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                PathRegexes = pathRegexes ?? new List<Regex>();
                StaticExes = staticExes ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                StaticPathRegexes = staticPathRegexes ?? new List<Regex>();
            }
        }

        // 真正推给内核的代理名单与目录正则（含 helper / 登录窗口 / 本程序自身等派生规则）。
        // 对账的「预期」必须用它，只用界面勾选或只记主 exe 会把合法子进程报成异常（实机 2026-11-10 / v1.4.0）。
        volatile EffectiveRules _effective = new EffectiveRules(null, null);

        /// <summary>点了「—」之后由托盘那边弹一次气泡，告诉用户窗口去哪了。首次才弹。</summary>
        public Action TrayHint;
        /// <summary>发现新版本时由托盘弹系统通知；窗口收在托盘里也看得见。</summary>
        public Action<string, string> TrayBalloon;

        Profile.KernelState _state = new Profile.KernelState();
        List<AppRow> _all = new List<AppRow>();
        HashSet<string> _savedProxy = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> _savedPolicies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        bool _autoRefreshing;
        int _staticDelayMs = -1;
        bool _staticTimedOut;
        bool _staticChecking;
        bool _staticOptimizing;
        string _staticLiveHop = "";
        int _staticTimeoutStreak;
        int _staticSlowStreak;
        DateTime _lastStaticHealthCheckAt = DateTime.MinValue;
        DateTime _lastStaticAlertAt = DateTime.MinValue;
        DateTime _lastAutoHopOptimizeAt = DateTime.MinValue;
        bool _staticWarnShown;
        RectangleF _staticDelayRect;
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
        int _repairFails;     // 连续补回失败次数；对账一致或推送成功时清零，连败 3 次就停手
        DateTime _lastRepairAt = DateTime.MinValue;
        int _ipv6Attempts;    // 只针对 IPv6 的补回次数：看到关就清零，连试 3 次不成就只记账
        // 同一原因「推送成功但下一轮仍判异常」的连续次数：达到 3 次说明补回解决不了，
        // 停手并提示人工保存，避免每 30 秒重复推送 + 重复弹提示（独立审阅第二轮 P1）。
        string _lastRepairReason = "";
        int _repairNoProgress;
        Traffic.Sample _traffic;
        // 界面显示值与内核采样值分开：采样每秒跳一次，显示值按时间连续逼近，
        // 数字不再「0 → 800 KB/s → 0」地瞬间变脸。
        double _rateProxyDown, _rateProxyUp, _rateStaticDown, _rateStaticUp, _rateDirectDown, _rateDirectUp;
        readonly List<float> _sparkProxy = new List<float>();
        readonly List<float> _sparkStatic = new List<float>();
        readonly List<float> _sparkDirect = new List<float>();
        bool _lastShowStaticRow;
        // 横轴固定为 1 秒一格，纵轴使用固定对数刻度。
        // 不能再随当前峰值/采样间隔重缩放：那会把已经发生的历史点整体拉高、压低或拉宽，
        // 用户看到的就是「之前的速度还在上下动」。这里只有最新一段做插值，旧点永远不重算。
        readonly List<long> _sparkAt = new List<long>();
        readonly Stopwatch _watch = Stopwatch.StartNew();
        const long SparkPeriodMs = 1000;
        long _sparkAxis;           // 逻辑时间轴当前位置（毫秒）
        long _lastClockStamp;
        long _sparkStamp;          // 上一个采样点到手时的秒表读数（只用来识别断采）
        float _sparkPulse = 1f;    // 新数据落地的脉冲

        // 覆盖检测：内核最近一次「看得见」某个进程的时间。用来看穿「勾了但没生效」。
        readonly Dictionary<string, DateTime> _seenAt = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        readonly DateTime _startedAt = DateTime.UtcNow;
        int _visibleProcs;    // 这一轮内核看得到的进程种类数
        int _coveredProcs;    // 其中在名单里的个数
        int _mismatch;        // 预期出口与实际链路不一致的软件数（白名单模式下才判）
        List<Traffic.MismatchItem> _mismatchItems = new List<Traffic.MismatchItem>();
        int _mismatchStreak;  // 连续命中分流异常的采样次数（至少 2 秒稳定命中再自动修，避免建链瞬间抖动）
        int _autoMismatchCount; // 同一异常期间已自动尝试修复次数（最多 3 次防死循环）
        DateTime _lastAutoMismatchAt = DateTime.MinValue;
        bool _blindToasted;

        // 实时节点延迟与定期测速状态 / 自动故障切换
        RectangleF _delayRect;
        RectangleF _mismatchRect;
        bool _delayTesting;
        int _delayMs = -1;
        string _delayNode = "";
        string _delayError = "";
        bool _delayTimedOut;
        int _delayTimeoutStreak;
        DateTime _lastDelayTestAt = DateTime.MinValue;
        DateTime _lastDelayAlertAt = DateTime.MinValue;
        bool _delayAlertActive;
        bool _failoverBusy;
        bool _nodeActionBusy;
        bool _batchDelayRunning;
        volatile bool _batchDelayCancel;
        bool _batchQualityRunning;
        volatile bool _batchQualityCancel;
        DateTime _lastFailoverAt = DateTime.MinValue;

        void BuildFonts()
        {
            DisposeFonts();
            _fTitle = Theme.Ui(15.5f, FontStyle.Bold);
            _fSub = Theme.Ui(11.5f);
            _fSummary = Theme.Ui(16f, FontStyle.Bold);
            _fBtn = Theme.Ui(12f, FontStyle.Bold);
            _fHint = Theme.Ui(10.5f);
            _fSmall = Theme.Ui(10f);
            _fInput = Theme.Ui(12.5f);
            _fTab = Theme.Ui(11.5f);
            _fTabActive = Theme.Ui(11.5f, FontStyle.Bold);
            _fRate = Theme.Ui(10.5f, FontStyle.Bold);
            _fTiny = Theme.Ui(9.5f);
            _fHelp = Theme.Ui(11.5f);
            _fHelpTitle = Theme.Ui(14f, FontStyle.Bold);
            _fPill = null;
        }

        void DisposeFonts()
        {
            if (_fTitle != null) _fTitle.Dispose();
            if (_fSub != null) _fSub.Dispose();
            if (_fSummary != null) _fSummary.Dispose();
            if (_fBtn != null) _fBtn.Dispose();
            if (_fHint != null) _fHint.Dispose();
            if (_fSmall != null) _fSmall.Dispose();
            if (_fInput != null) _fInput.Dispose();
            if (_fTab != null) _fTab.Dispose();
            if (_fTabActive != null) _fTabActive.Dispose();
            if (_fRate != null) _fRate.Dispose();
            if (_fTiny != null) _fTiny.Dispose();
            if (_fHelp != null) _fHelp.Dispose();
            if (_fHelpTitle != null) _fHelpTitle.Dispose();
            if (_fPill != null) _fPill.Dispose();
            _fTitle = _fSub = _fSummary = _fBtn = _fHint = _fSmall = _fInput =
                _fTab = _fTabActive = _fRate = _fTiny = _fHelp = _fHelpTitle = _fPill = null;
        }

        public MainForm()
        {
            BuildFonts();
            Text = "ProxyPicker";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            // 无边框不等于工具窗口：用户需要在任务栏看到并找回这个程序。
            // 只有主动点「—」收进托盘时才 Hide()，打开面板期间任务栏按钮一直存在。
            ShowInTaskbar = true;
            DoubleBuffered = true;
            BackColor = Theme.Bg;
            AutoScaleMode = AutoScaleMode.None;
            SetStyle(ControlStyles.ResizeRedraw, true);
            float initW = Settings.Current.WinW >= MinWinW ? Settings.Current.WinW : DefaultWinW;
            float initH = Settings.Current.WinH >= MinWinH ? Settings.Current.WinH : DefaultWinH;
            ClientSize = ClampLogicalClientSize(initW, initH, Theme.S, Screen.FromPoint(Cursor.Position).WorkingArea);
            KeyPreview = true;
            // 无边框窗体的 ShowIcon 默认不可靠：必须在句柄创建后自己发 WM_SETICON，
            // 否则任务栏那一格会是空白图标（用户反馈的「任务栏没有图标」就是这个）。
            ShowIcon = true;
            try
            {
                _windowIcon = Draw.AppIco(Math.Max(16, SystemInformation.SmallIconSize.Width));
                _windowIconBig = Draw.AppIco(Math.Max(32, SystemInformation.IconSize.Width));
                Icon = _windowIconBig;
                _titleIcon = Draw.AppIcon(64);   // 解码一次，不能每帧重复解 ICO
            }
            catch (Exception ex) { CrashLog.Write(ex, "读取窗口图标失败"); }

            _list.Toggled += (s, e) => { SyncStaticUiBanner(); ShowDirtyStatus(); };
            _list.StaticToggled += (s, row) => { SyncStaticUiBanner(); ShowDirtyStatus(); };
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
                // 复审 P2：Esc 不再在这里收起面板，只由窗体级处理「清空搜索」。
                if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; SaveAsync(); }
                if (e.KeyCode == Keys.Down) { _list.MoveSelection(1); e.SuppressKeyPress = true; }
            };
            _search.GotFocus += (s, e) => Invalidate();
            _search.LostFocus += (s, e) => Invalidate();

            Controls.Add(_list);
            Controls.Add(_search);
            _nodesPage.Visible = false;
            _nodesPage.Overlay = (g, area) => { if (_toasts.Any) _toasts.Render(g, area); };
            _nodesPage.BackRequested += NavigateBack;
            _nodesPage.TestCurrentDelayRequested += () => _ = RunDelayTestAsync(true);
            _nodesPage.ToggleBatchDelayRequested += ToggleBatchDelayScan;
            _nodesPage.SwitchNodeRequested += node => _ = SwitchNodeFromPanelAsync(node);
            _nodesPage.ProbeAndOpenQualityRequested += node => InspectSpecificNodeFromPanel(node);
            _nodesPage.CandidateToggled += (node, on) =>
            {
                Settings.SetCandidateNode(node, on);
                InvalidateNavBar();
                SetStatus((on ? "已加入备选池：" : "已移出备选池：") + CleanEmoji(node), Theme.Dim);
            };
            _nodesPage.CandidateBatchChanged += names =>
            {
                Settings.SetCandidateNodes(names);
                InvalidateNavBar();
                SetStatus("已更新备选池（" + names.Count + " 个节点）", Theme.Accent);
            };
            _nodesPage.AutoFailoverToggled += on =>
            {
                Settings.SetAutoFailover(on);
                int c = Settings.GetCandidateNodes().Count;
                SetStatus(on
                    ? (c > 0 ? "已开启：当前节点超时自动切备选池最低延迟节点（切完必通知）" : "已开启自动切换（请勾选至少 1 个备选节点）")
                    : "已关闭节点超时自动切换", on && c == 0 ? Theme.Warn : Theme.Accent);
            };
            _nodesPage.ConfigureStaticProxyRequested += () => OpenStaticProxyDialog();
            _nodesPage.OptimizeStaticHopRequested += () => _ = OptimizeStaticHopAsync(true, false);
            _nodesPage.TestStaticDelayRequested += () => TestStaticDelayPublic();
            SyncStaticUiBanner();

            _qualityPage.Visible = false;
            _qualityPage.Overlay = (g, area) => { if (_toasts.Any) _toasts.Render(g, area); };
            _qualityPage.BackRequested += NavigateBack;
            _qualityPage.InspectRequested += target =>
                _ = InspectNodeQualityAsync(string.IsNullOrEmpty(target) ? _state.Outbound : target, false);
            _qualityPage.BatchRiskScanRequested += () => _ = ToggleBatchQualityScanAsync();
            _qualityPage.DesiredRegionCycled += () =>
            {
                string next = Settings.CycleDesiredRegion();
                string msg = "期望落地地区改为：" + NodeManager.RegionDisplayName(next) + "（重新执行「单节点检测」或「全量节点评估」后按新地区核对）";
                SetStatus(msg, Theme.Accent);
                Toast("期望地区已切换", NodeManager.RegionDisplayName(next), Theme.Accent, 2);
            };
            _qualityPage.JumpToNodesRequested += () => PushAndSwitchPage(1);
            _qualityPage.SwitchNodeRequested += node => _ = SwitchNodeFromPanelAsync(node);
            _qualityPage.ApplyLowRiskPoolRequested += names =>
            {
                if (names == null || names.Count == 0)
                {
                    Toast("没有符合条件的可用节点", "请先执行「全量节点评估」，或勾选低风控 / 全部就绪节点", Theme.Warn, 1);
                    return;
                }
                Settings.SetCandidateNodes(names);
                _ = LoadNodesCatalogAsync();
                InvalidateNavBar();
                Toast("已更新节点策略备选池", "已纳入 " + names.Count + " 个低风控且主流服务就绪节点", Theme.Ok, 0);
                SetStatus("已把 " + names.Count + " 个低风控优质节点设为自动切换备选池", Theme.Ok);
            };
            _qualityPage.CopyIpRequested += ip =>
            {
                try { Clipboard.SetText(ip); } catch { }
                Toast("已复制出口 IP", ip, Theme.Ok, 0);
                SetStatus("已复制当前出口 IP：" + ip, Theme.Ok);
            };

            Controls.Add(_nodesPage);
            Controls.Add(_qualityPage);
            KeyDown += OnFormKeyDown;
            LayoutChildren();
            _restoringSize = false;
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

        const int WM_SETICON = 0x0080;
        const int ICON_SMALL = 0;
        const int ICON_BIG = 1;

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try { int round = 2; DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int)); } catch { }
            try
            {
                // 多屏混合 DPI 下，窗口初始落在非主屏时立即按所在屏幕重算缩放与字号，不等拖动后 WM_DPICHANGED
                float winScale = Theme.DetectWindowScale(Handle);
                if (Math.Abs(winScale - Theme.S) > 0.02f)
                {
                    float prevW = LogicalW, prevH = LogicalH;
                    Theme.SetScale(winScale);
                    BuildFonts();
                    _search.Font = _fInput;
                    _list.RefreshThemeAndFonts();
                    _nodesPage.RefreshThemeAndFonts();
                    _qualityPage.RefreshThemeAndFonts();
                    if (WindowState == FormWindowState.Normal)
                        ClientSize = ClampLogicalClientSize(prevW, prevH, Theme.S, Screen.FromControl(this).WorkingArea);
                    LayoutChildren();
                }
            }
            catch { }
            ApplyWindowIcons();
            try { if (!_timerRaised) { timeBeginPeriod(1); _timerRaised = true; } } catch { }
        }

        [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
        static extern uint timeBeginPeriod(uint ms);
        [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
        static extern uint timeEndPeriod(uint ms);

        const int WM_DPICHANGED = 0x02E0;

        /// <summary>
        /// 窗口被拖到另一块不同 DPI 的显示器时，重建字体与尺寸。
        /// 以前只按启动时的 DPI 算一次，跨屏后自绘文字会模糊、命中矩形会错位。
        /// </summary>
        internal static Size ClampLogicalClientSize(float logicalW, float logicalH, float scale, Rectangle workingArea)
        {
            if (scale < 0.5f) scale = 1f;
            int pw = Math.Max(420, (int)Math.Round(logicalW * scale));
            int ph = Math.Max(480, (int)Math.Round(logicalH * scale));
            if (workingArea.Width > 0 && workingArea.Height > 0)
            {
                int maxW = Math.Max(420, workingArea.Width - 16);
                int maxH = Math.Max(480, workingArea.Height - 8);
                if (pw > maxW) pw = maxW;
                if (ph > maxH) ph = maxH;
            }
            return new Size(pw, ph);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_GETMINMAXINFO && !IsDisposed)
            {
                try
                {
                    var mmi = (MINMAXINFO)Marshal.PtrToStructure(m.LParam, typeof(MINMAXINFO));
                    var scr = Screen.FromHandle(Handle);
                    var wa = scr.WorkingArea;
                    var b = scr.Bounds;
                    mmi.ptMaxPosition.X = wa.X - b.X;
                    mmi.ptMaxPosition.Y = wa.Y - b.Y;
                    mmi.ptMaxSize.X = wa.Width;
                    mmi.ptMaxSize.Y = wa.Height;
                    mmi.ptMinTrackSize.X = Math.Min(wa.Width, Math.Max(420, Theme.Px(MinWinW)));
                    mmi.ptMinTrackSize.Y = Math.Min(wa.Height, Math.Max(420, Theme.Px(MinWinH)));
                    mmi.ptMaxTrackSize.X = Math.Max(mmi.ptMinTrackSize.X, wa.Width);
                    mmi.ptMaxTrackSize.Y = Math.Max(mmi.ptMinTrackSize.Y, wa.Height);
                    Marshal.StructureToPtr(mmi, m.LParam, true);
                }
                catch { }
                m.Result = IntPtr.Zero;
                return;
            }

            base.WndProc(ref m);

            if (m.Msg == WM_NCHITTEST && !IsDisposed && WindowState != FormWindowState.Maximized && !_help &&
                (int)m.Result == HTCLIENT)
            {
                try
                {
                    int raw = unchecked((int)m.LParam.ToInt64());
                    int sx = (short)(raw & 0xFFFF);
                    int sy = (short)((raw >> 16) & 0xFFFF);
                    var pt = PointToClient(new Point(sx, sy));
                    int hit = Math.Max(6, Theme.Px(EdgeHitPx));
                    int w = ClientSize.Width, h = ClientSize.Height;
                    bool left = pt.X <= hit, right = pt.X >= w - hit;
                    bool top = pt.Y <= hit, bottom = pt.Y >= h - hit;
                    if (left && top) m.Result = (IntPtr)HTTOPLEFT;
                    else if (right && top) m.Result = (IntPtr)HTTOPRIGHT;
                    else if (left && bottom) m.Result = (IntPtr)HTBOTTOMLEFT;
                    else if (right && bottom) m.Result = (IntPtr)HTBOTTOMRIGHT;
                    else if (left) m.Result = (IntPtr)HTLEFT;
                    else if (right) m.Result = (IntPtr)HTRIGHT;
                    else if (top) m.Result = (IntPtr)HTTOP;
                    else if (bottom) m.Result = (IntPtr)HTBOTTOM;
                }
                catch { }
                return;
            }

            if (m.Msg != WM_DPICHANGED || IsDisposed) return;
            try
            {
                float prevW = LogicalW, prevH = LogicalH;
                int dpi = (int)(m.WParam.ToInt64() & 0xFFFF);
                if (dpi < 72) return;
                Theme.SetScale(dpi / 96f);
                BuildFonts();
                _search.Font = _fInput;
                _list.RefreshThemeAndFonts();
                _nodesPage.RefreshThemeAndFonts();
                _qualityPage.RefreshThemeAndFonts();
                if (WindowState == FormWindowState.Normal)
                {
                    ClientSize = ClampLogicalClientSize(prevW, prevH, Theme.S, Screen.FromControl(this).WorkingArea);
                    Settings.SetWindowSize((int)Math.Round(LogicalW), (int)Math.Round(LogicalH));
                }
                LayoutChildren();
                Invalidate();
            }
            catch { }
        }

        void ToggleMaximize()
        {
            WindowState = WindowState == FormWindowState.Maximized
                ? FormWindowState.Normal
                : FormWindowState.Maximized;
            LayoutChildren();
            Invalidate();
        }

        /// <summary>
        /// 显式把两档图标交给窗口。只设 Form.Icon 在无边框窗体上时灵时不灵，
        /// 任务栏按钮和 Alt+Tab 取的是 WM_SETICON 的结果，这里两条都发。
        /// </summary>
        void ApplyWindowIcons()
        {
            try
            {
                if (_windowIcon != null)
                    SendMessage(Handle, WM_SETICON, new IntPtr(ICON_SMALL), _windowIcon.Handle);
                if (_windowIconBig != null)
                    SendMessage(Handle, WM_SETICON, new IntPtr(ICON_BIG), _windowIconBig.Handle);
            }
            catch (Exception ex) { CrashLog.Write(ex, "设置窗口图标失败"); }
        }

        // ---------------------------------------------------------------- 时钟

        void ClockTick(object sender, EventArgs e)
        {
            bool need = false;
            bool summaryNeed = false;
            long now = _watch.ElapsedMilliseconds;
            double dt = _lastClockStamp == 0 ? 0.016 : (now - _lastClockStamp) / 1000.0;
            _lastClockStamp = now;
            if (dt < 0.001) dt = 0.001;
            if (dt > 0.25) dt = 0.25;   //  debugger/系统卡顿后别把透明度之类的动画一步跳完

            // 所有动效都按真实 elapsed time 走；60Hz 和 30Hz 下速度一致。
            _spinning = _busy || DateTime.UtcNow < _spinUntil;
            if (_spinning) { _spin = (_spin + (float)(dt * 540.0)) % 360f; need = true; }

            if (_show < 1f)
            {
                _show += (1f - _show) * (float)(1.0 - Math.Exp(-dt / 0.09));
                if (_show > 0.995f) _show = 1f;
                try { Opacity = 0.06 + _show * 0.94; } catch { }
                need = true;
            }

            bool toastTicked = _toasts.Tick((float)dt);
            if (toastTicked)
            {
                InvalidateActiveToastSurface();
                if (_page == 0 && !_list.Visible) need = true;
            }

            bool trafficAlive = Visible && !_help && _page == 0 && _traffic != null && _traffic.Ok;
            if (trafficAlive)
            {
                // 内核数据仍然是一秒一个真实平均值，这里只平滑「显示」，不伪造新采样。
                // 220ms 时间常数既能去掉台阶，也不会把真实变化拖成慢动作。
                _rateProxyDown = Approach(_rateProxyDown, _traffic.ProxyDown, dt, 0.22);
                _rateProxyUp = Approach(_rateProxyUp, _traffic.ProxyUp, dt, 0.22);
                _rateStaticDown = Approach(_rateStaticDown, _traffic.StaticDown, dt, 0.22);
                _rateStaticUp = Approach(_rateStaticUp, _traffic.StaticUp, dt, 0.22);
                _rateDirectDown = Approach(_rateDirectDown, _traffic.DirectDown, dt, 0.22);
                _rateDirectUp = Approach(_rateDirectUp, _traffic.DirectUp, dt, 0.22);
                summaryNeed = true;
            }

            bool sparkAlive = Visible && !_help && _page == 0 && _sparkProxy.Count >= 2;
            if (sparkAlive)
            {
                // 逻辑时间轴跟真实时间走，但不超过「最后一个样本 + 一个采样周期」：
                // 内核断采时曲线停住不动，恢复时从一个周期之后接着走，
                // 既不会整条跳走，也不会因为点全部滚出窗口而突然消失。
                // 时间轴永远跟真实时间走。以前这里夹了个「不超过最后一个样本 + 1 秒」，
                // 内核回包慢半拍时曲线会先冻住再突然跳一格 —— 那就是用户看到的「一卡一卡」。
                // 断采很长时由 PollTraffic 里的 RebaseTrail 处理，短抖动就让曲线照常滑过去。
                _sparkAxis = now;
                if (_sparkPulse > 0f) _sparkPulse = Math.Max(0f, _sparkPulse - (float)(dt / 0.65));
                summaryNeed = true;
            }

            if (need) Invalidate();
            else if (summaryNeed)
            {
                var r = SummaryRect;
                Invalidate(Rectangle.Round(new RectangleF(
                    Theme.Px(r.X) - 3, Theme.Px(r.Y) - 3, Theme.Px(r.Width) + 6, Theme.Px(r.Height) + 6)));
            }
            else if (!sparkAlive && !_toasts.Any && !_spinning && _show >= 1f) _clock.Stop();
        }

        static double Approach(double value, double target, double seconds, double tau)
        {
            double next = target + (value - target) * Math.Exp(-seconds / tau);
            return Math.Abs(next - target) < 0.5 ? target : next;
        }

        void Wake()
        {
            _lastClockStamp = _watch.ElapsedMilliseconds;
            if (!_clock.Enabled) _clock.Start();
        }

        // ---------------------------------------------------------------- 显隐

        public void ShowPanel()
        {
            bool wasDark = Theme.Dark;
            Theme.Load();
            if (wasDark != Theme.Dark)
            {
                BackColor = Theme.Bg;
                _search.BackColor = Theme.Card;
                _search.ForeColor = Theme.Fg;
                _nodesPage.RefreshThemeAndFonts();
                _qualityPage.RefreshThemeAndFonts();
            }
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Show(); BringToFront(); Activate();
            if (_page == 0 && !_help) _search.Focus();
            if (_all.Count == 0)
            {
                RefreshAsync();
            }
            else if (DirtyCount == 0 && (DateTime.UtcNow - _lastLoad).TotalSeconds > 8)
            {
                RefreshAsync();
            }
            else
            {
                // 有未保存草稿或刚刷新过：走无损增量进程刷新，实时同步新打开/关闭的软件而不冲掉未保存勾选
                ShowDirtyStatus();
                RebuildVisible(false);
                _ = AutoRefreshRunningProcessesAsync();
                if (_page == 1 || _page == 2) _ = LoadNodesCatalogAsync();
            }
            SyncStaticUiBanner();
            if (!_staticWarnShown && !string.IsNullOrEmpty(StaticProxy.LastLoadWarning))
            {
                _staticWarnShown = true;
                Toast("独立固定 IP 需要重新配置", StaticProxy.LastLoadWarning, Theme.Warn, 3);
            }
            _trafficTimer.Start();
            _sparkStamp = _watch.ElapsedMilliseconds;   // 重新显示时把旧历史平移到当下，曲线接着走而不是凭空消失
            RebaseTrail(_sparkStamp);
            Wake();
            PollTraffic();
            if (Settings.Current.FirstRun)
            {
                Settings.Current.FirstRun = false;
                Settings.Save();
                Toast("只有打勾的软件走代理",
                    Settings.PatchOnly ? "没勾的软件仍按你订阅的规则走" : "没打勾的软件全部直连，选好再点右下角保存",
                    Theme.Accent, 2);
            }

            // 版本检查改在托盘就绪后由 StartAutoUpdateCheck() 触发（含 --tray 后台启动），
            // 不再依赖用户点开面板；这里只负责把面板显示出来。
        }

        public void HideToTray()
        {
            Hide();
            _trafficTimer.Stop();
        }

        /// <summary>
        /// 点标题栏「—」走这里：收进托盘，后台看守照常运行。
        /// 点「×」不是收起，而是「还原内核配置并退出」（见 OnFormClosing）。
        /// 这句旧注释曾把两个按钮写成一样，朋友版说明跟着写错了，现已更正。
        /// </summary>
        public void MinimizeToTray()
        {
            HideToTray();
            var hint = TrayHint;
            if (hint != null) { try { hint(); } catch { } }
        }

        public void RealExit() { _realExit = true; Close(); }

        /// <summary>
        /// 退出还原开始前调用：禁掉主窗口交互并标记全局关闭状态。
        /// 以前退出用 DoEvents 抽消息泵，用户还能点「保存」；保存任务会等锁，
        /// 等还原放锁后又抢在 Environment.Exit 之前推一次配置（v1.3.7 修）。
        /// 若用户选择「继续运行」则调用 EndShutdown 恢复。
        /// </summary>
        public void BeginShutdown()
        {
            _shuttingDown = true;
            _batchDelayCancel = true;
            _batchQualityCancel = true;
            // 退出期间先停掉轮询与看守计时器，避免后台每秒抢通道锁拖慢还原；
            // 仅禁用子控件操作而不把顶层窗体整窗 Enabled=false 冻成无响应假死。
            try { _trafficTimer.Stop(); } catch { }
            try { _watchTimer.Stop(); } catch { }
            SetChildControlsEnabled(false);
            SetStatus("正在清理内核规则并退出…", Theme.Dim);
        }

        public void EndShutdown()
        {
            _shuttingDown = false;
            try { Enabled = true; } catch { }
            SetChildControlsEnabled(true);
            try { _watchTimer.Start(); } catch { }
            try { if (Visible) _trafficTimer.Start(); } catch { }
            SetStatus("已取消退出，继续在后台守护分流", Theme.Dim);
        }

        void SetChildControlsEnabled(bool enabled)
        {
            try { if (_list != null) _list.Enabled = enabled; } catch { }
            try { if (_search != null) _search.Enabled = enabled; } catch { }
            try { if (_nodesPage != null) _nodesPage.Enabled = enabled; } catch { }
            try { if (_qualityPage != null) _qualityPage.Enabled = enabled; } catch { }
        }

        /// <summary>点「×」时抛给 TrayContext：走「还原 + 退出」，不再静默收进托盘。</summary>
        public event Action CloseRequested;

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!_realExit && e.CloseReason == CloseReason.UserClosing)
            {
                // 「关掉窗口」在用户心里就是关掉软件。以前这里只是 Hide()，
                // 于是进程一直活着、托盘图标又可能被折叠，就变成「我明明关了它怎么还在跑」。
                // 想后台看守请点标题栏的「—」。
                e.Cancel = true;
                if (_shuttingDown) return;
                var handler = CloseRequested;
                if (handler != null) { try { handler(); } catch { } return; }
                RealExit();
                return;
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
                if (!UiAlive || _busy || _shuttingDown) return;
                if (DirtyCount > 0 && _all.Count > 0)
                {
                    SetStatus("有未保存改动；请先保存，再刷新", Theme.Warn);
                    Toast("先保存再刷新", "当前有改动还没生效，避免两边状态打架", Theme.Warn, 1);
                    return;
                }
                ownsBusy = true;
                BeginBusy();
                int refreshRev = _revision;
                string prevClient = _state.Client ?? "";
                string prevOutbound = _state.Outbound ?? "";
                _spinUntil = DateTime.UtcNow.AddMilliseconds(650);
                SetStatus("正在读取软件与内核状态…", Theme.Dim);
                var pack = await Task.Run(() => LoadPack());
                if (!UiAlive || refreshRev != _revision) return;
                _state = pack.State;
                if (!_state.ConfigOk ||
                    !string.Equals(prevClient, _state.Client ?? "", StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(prevOutbound, _state.Outbound ?? "", StringComparison.OrdinalIgnoreCase))
                {
                    ResetDelayBannerState();
                    _catalogSeq++;
                    if (!_state.ConfigOk) _nodesPage.SetLoading(false);
                }
                _all = pack.Rows;
                _lastLoad = DateTime.UtcNow;
                // 让 _all 本身也按当前排序方式排好：否则「界面看到的顺序」和「_all 的顺序」
                // 是两套，手动模式下再拖一次就会把之前的手动顺序覆盖掉。
                ApplySort(_all, Settings.Current.SortMode);
                _savedProxy = new HashSet<string>(_all.Where(r => r.Proxy).Select(r => r.Exe), StringComparer.OrdinalIgnoreCase);
                _savedPolicies = SnapshotSavedPoliciesFromRows();
                _list.SavedProxy = _savedProxy;
                _list.SavedPolicies = _savedPolicies;
                SyncStaticUiBanner();
                if (_savedProxy.Count > 0)
                {
                    var savedSnap = new List<string>(_savedProxy);
                    var visibleSnap = VisibleExes();
                    var pathsSnap = PathMap();
                    var policiesSnap = PolicyMapFromRows();
                    var effInit = await Task.Run(() => ComposeWithHelpers(savedSnap, visibleSnap, pathsSnap, policiesSnap));
                    if (!UiAlive) return;
                    SetEffectiveProxy(effInit);
                }
                else
                {
                    SetEffectiveProxy(null);
                }
                Settings.Current.Tab = _tab;
                Settings.Save();
                RebuildVisible(true);
                _nodesPage.SetCurrentOutbound(_state.Outbound, _delayMs > 0 ? _delayMs : _state.DelayMs, _delayTimedOut, _state.ProxyActive);
                _qualityPage.SetCurrentOutbound(_state.Outbound);
                InvalidateNavBar();
                if (_page == 1 || _page == 2) _ = LoadNodesCatalogAsync();
                bool kernelOnline = _state.ConfigOk;
                bool proxyActive = _state.ProxyActive;
                bool kernelIsRule = kernelOnline && Profile.IsRuleMode(_state.Mode);
                if (!kernelOnline)
                {
                    _mismatch = 0;
                    _mismatchItems.Clear();
                    _mismatchStreak = 0;
                    SetStatus(_state.Error ?? "未检测到运行中的代理内核（当前未连接）", Theme.Warn);
                }
                else if (!kernelIsRule)
                {
                    _mismatch = 0;
                    _mismatchItems.Clear();
                    _mismatchStreak = 0;
                    string clientLabel = !string.IsNullOrEmpty(_state.Client) ? _state.Client : "代理内核";
                    SetStatus(clientLabel + " 当前为「" + Profile.ModeDisplayName(_state.Mode) + "」模式，按软件分流暂停生效", Theme.Warn);
                }
                else if (!proxyActive)
                {
                    _mismatch = 0;
                    _mismatchItems.Clear();
                    _mismatchStreak = 0;
                    SetStatus(_state.Error ?? "客户端已运行，但 TUN 和系统代理均未开启（当前未连接代理）", Theme.Warn);
                }
                else
                {
                    SetStatus(_state.Error ?? (_state.Applied ? "名单已生效 · 改动后记得保存" : "名单未生效，请保存一次"),
                        _state.Error == null && _state.Applied ? Theme.Dim : Theme.Warn);
                }
                // 多客户端提醒不能只躺在 --diag 里：装了多个梯子、又没都开着时，
                // 自动选中的那个如果不对，用户要第一时间知道，而不是等分流不生效再排查。
                try
                {
                    string envWarn = Discovery.Detect().Warning;
                    if (!string.IsNullOrEmpty(envWarn) && _state.Error == null && envWarn != _lastEnvWarn)
                    {
                        _lastEnvWarn = envWarn;
                        Toast("检测到多个梯子客户端", envWarn, Theme.Warn, 2);
                    }
                }
                catch { }
                // 名单不是空的、也没报错，但规则不见了 —— 只在内核处于 rule 模式时自动补回；
                // 若用户明确切在全局/直连模式，绝不偷偷把客户端改回 rule 或触发无效补回报错。
                needRepair = kernelIsRule && !_state.Applied && _state.Error == null
                             && _savedProxy.Count > 0 && !_autoRepaired;
                // 名单读取失败时显示最后一份好名单，并禁止自动补回（评审 R6-3）。
                if (Profile.LastLoadFailed)
                {
                    SetStatus("勾选名单读取失败，已用上次记录显示；暂不自动补回", Theme.Warn);
                    Toast("名单读取失败", Profile.LastLoadError + "；为避免清空分流，已停止自动推送", Theme.Warn, 2);
                    needRepair = false;
                }
                UpdateLogContext();
            }
            catch (Exception ex) { ReportFailure("读取软件状态", ex); }
            finally { if (ownsBusy) FinishBusy(); }
            if (needRepair && UiAlive) RepairQuietly();
            if (UiAlive && _state.ConfigOk && _state.ProxyActive && !string.IsNullOrEmpty(_state.Outbound) &&
                Profile.ClassifyExit(_state.Outbound) == Profile.ExitKind.Proxy &&
                (DateTime.UtcNow - _lastDelayTestAt).TotalSeconds >= 10)
            {
                _ = RunDelayTestAsync(false);
            }
            if (UiAlive && _state.ConfigOk && _state.ProxyActive && StaticProxy.IsEnabledAndValid &&
                !_staticChecking &&
                (_staticDelayMs <= 0 || (DateTime.UtcNow - _lastStaticHealthCheckAt).TotalSeconds >= 10))
            {
                _ = CheckStaticProxyHealthAsync(false);
            }
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
            if (!UiAlive || _shuttingDown) return;
            // 定期调用 Clash /proxies/{节点}/delay 测速；即使收进托盘也会按配置间隔持续巡检并在超时时弹窗提醒
            if (!_busy && !_delayTesting && ShouldRunPeriodicDelayTest())
                _ = RunDelayTestAsync(false);
            // 独立固定 IP 周期实时测速与每 10 分钟稳速刷新：不依赖主节点状态，收进托盘同样持续巡检稳速
            if (!_busy && _state.ConfigOk && _state.ProxyActive && StaticProxy.IsEnabledAndValid)
            {
                var nowUtc = DateTime.UtcNow;
                if (!_staticChecking && !_staticOptimizing &&
                    (_staticDelayMs <= 0 && !_staticTimedOut ||
                     _lastStaticHealthCheckAt == DateTime.MinValue ||
                     (nowUtc - _lastStaticHealthCheckAt).TotalSeconds >= StaticProxy.StaticHealthCheckIntervalSeconds))
                {
                    _ = CheckStaticProxyHealthAsync(false);
                }
                var spCfg = StaticProxy.Current;
                bool isAutoHop = string.Equals(spCfg.HopMode, StaticProxy.HopAuto, StringComparison.OrdinalIgnoreCase);
                if (StaticProxy.ShouldRunScheduledAutoHopRefresh(
                        isAutoHop, _staticOptimizing, _lastAutoHopOptimizeAt,
                        spCfg.AutoBestUpdatedAtTicks, nowUtc, _staticSlowStreak, _staticTimedOut))
                {
                    _ = OptimizeStaticHopAsync(false, _staticTimedOut);
                }
            }
            if (_busy || _watching) return;
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
                if (!UiAlive || _busy || _watching || _shuttingDown) return;
                // 名单读取失败时不要重试 3 次再弹「补回失败」（复审 P3）。
                if (Profile.LastLoadFailed) return;

                // 面板没打开过时 _savedProxy 是空的，这时以磁盘上的名单为准；
                // 否则「开机就缩在托盘」的情况下，对账永远没有对象。
                var exes = new List<string>(_savedProxy);
                // 名单里现在混有两类条目：进程名和目录正则。目录条目不能当成 exe 名参与对账。
                if (exes.Count == 0) exes = Profile.Load().Where(i => !i.IsPathRule).Select(i => i.Exe).ToList();
                // 空名单是正式支持的配置（「全部直连」），不能当成「无需看守」直接返回。
                // 用名单文件本身表达意图：文件不存在＝新装/刚还原、从未表达过意图，看守不推；
                // 文件存在但 payload 为空＝用户明确要全部直连，必须继续对账维护段首 MATCH（外部评审 F2）。
                // 不引入独立状态位，避免它过期、崩溃后残留或和用户手动撤销分叉。
                if (exes.Count == 0 && !Profile.HasSavedIntent) return;

                owns = true;
                _watching = true;
                int revision = _revision;
                bool patch = Settings.PatchOnly;
                // 收尾批 P2-1：本轮对账只认这一份配置，/configs 与 /rules 都按它绑定，
                // 不让 5 秒缓存窗口里的另一次 Detect 把两次读拆到不同客户端。
                string watchCfg = Paths.ClashYaml;
                // 对账要拿「实际应该存在的规则」比，而不是只比主 exe：
                // Antigravity 的语言服务进程在 resources\bin 下，属于必须一起写进规则的 helper，
                // 只比主 exe 会得出「已生效」，实际登录请求仍然直连（实测就是登录失败）。
                var visibleForAudit = VisibleExes();
                var pathsForAudit = PathMap();
                var auditPolicies = _all.Count > 0 ? PolicyMapFromRows() : Profile.LoadPolicyMap();
                var wantedItems = await Task.Run(() => ComposeWithHelpers(exes, visibleForAudit, pathsForAudit, auditPolicies));
                var wantedAll = wantedItems.Select(i => i.IsPathRule ? i.PathRegex : i.Exe).ToList();
                var audit = await Task.Run(() => Guard.Audit(wantedAll, auditPolicies, watchCfg));
                if (!UiAlive || revision != _revision) return;
                SetEffectiveProxy(wantedItems);

                bool sysWatchOn = false;
                string sysWatchSrv = "";
                Profile.ReadSystemProxyState(out sysWatchOn, out sysWatchSrv);
                _state.SystemProxyEnabled = sysWatchOn;
                _state.SystemProxyServer = sysWatchSrv;

                if (!audit.Ok)
                {
                    _state.ConfigOk = false;
                    _state.Tun = false;
                    _state.Mode = "?";
                    _state.Outbound = "";
                    _state.Chain = "";
                    _state.Applied = false;
                    _state.DelayMs = -1;
                    _state.NodeAlive = null;
                    ResetDelayBannerState();
                    _catalogSeq++;
                    _mismatch = 0;
                    _mismatchItems.Clear();
                    _mismatchStreak = 0;
                    _state.Error = !string.IsNullOrEmpty(audit.Error) ? audit.Error : "未检测到运行中的代理内核（当前未连接）";
                    _nodesPage.SetLoading(false);
                    _nodesPage.SetCurrentOutbound("", -1, false, false);
                    _qualityPage.SetCurrentOutbound("");
                    InvalidateNavBar();
                    Invalidate(new Rectangle(0, 0, Width, Theme.Px(TitleH + SummaryH + 4)));
                    return;
                }

                _state.ConfigOk = true;
                _state.Tun = audit.TunEnabled;
                if (!string.IsNullOrEmpty(audit.Mode)) _state.Mode = audit.Mode;

                // 后台定期同步最新出口节点（最小化到托盘期间也保证定期测速拿到的是当前选中节点）
                try
                {
                    string pol = !string.IsNullOrEmpty(_state.Policy) ? _state.Policy : Profile.PolicyName(watchCfg);
                    var detail = await Task.Run(() =>
                    {
                        if (!patch && exes.Count == 0 && Profile.IsRuleMode(_state.Mode))
                            return new Profile.OutboundDetail { Global = "", Outbound = "DIRECT", Chain = "DIRECT", HistoryDelayMs = -1, Alive = null };
                        string targetPol = _state.Mode.Equals("global", StringComparison.OrdinalIgnoreCase) ? "GLOBAL"
                            : (_state.Mode.Equals("direct", StringComparison.OrdinalIgnoreCase) ? "DIRECT" : pol);
                        var obj = Mihomo.GetJson("/proxies", 6000, watchCfg);
                        var proxies = Json.Obj(obj, "proxies");
                        return Core.Profile.ResolveOutboundDetail(proxies, targetPol);
                    });
                    if (!UiAlive || revision != _revision) return;
                    if (!string.IsNullOrEmpty(pol)) _state.Policy = pol;
                    if (!string.IsNullOrEmpty(detail.Outbound))
                    {
                        _state.Outbound = detail.Outbound;
                        _state.Chain = detail.Chain;
                        _state.NodeAlive = detail.Alive;
                        _state.DelayMs = detail.HistoryDelayMs > 0 ? detail.HistoryDelayMs : -1;
                    }
                    else
                    {
                        _state.Outbound = "";
                        _state.Chain = "";
                        _state.DelayMs = -1;
                        _state.NodeAlive = null;
                    }
                    _nodesPage.SetCurrentOutbound(_state.Outbound, _delayMs > 0 ? _delayMs : _state.DelayMs, _delayTimedOut, _state.ProxyActive);
                    _qualityPage.SetCurrentOutbound(_state.Outbound);
                    InvalidateNavBar();
                }
                catch { }

                // 用户刚在帮助面板切了模式但还没点保存，看守不能替他把新模式推下去（评审 R4-2）。
                if (_modeChanged) return;
                // 还原时名单文件删除失败：看守已经暂停，不能再按旧名单补回（v1.3.7）。
                if (Settings.RepairSuspended) return;

                // v1.5.0：如果用户在 Clash 客户端主动切到了全局/直连模式，尊重用户当前模式，
                // 绝不后台强行 PATCH 回 rule（否则 Clash 显示全局而内核被抢回规则，造成模式打架与控制权争夺）。
                bool modeDrifted = audit.Ok && !Profile.IsRuleMode(audit.Mode);
                if (modeDrifted)
                {
                    _mismatch = 0;
                    _mismatchItems.Clear();
                    _mismatchStreak = 0;
                    if (_state.Error == null)
                        SetStatus("Clash 当前为「" + Profile.ModeDisplayName(audit.Mode) + "」模式，按软件分流暂停中", Theme.Warn);
                    Invalidate(new Rectangle(0, 0, Width, Theme.Px(SummaryTop + SummaryH + 4)));
                    return;
                }

                // 仅在 rule 模式下如果进程识别被意外关闭（find-process-mode=off），才自动修回 always。
                if (audit.Ok && WatchShouldAutoFixConfig(audit.Mode, audit.FindProcess))
                {
                    var nowUtc = DateTime.UtcNow;
                    bool canAutoFix = Profile.HasSavedIntent &&
                                      _repairFails < 3 && _repairNoProgress < 3 && _autoMismatchCount < 3 &&
                                      (_lastRepairAt == DateTime.MinValue || (nowUtc - _lastRepairAt).TotalSeconds >= 30) &&
                                      (_lastAutoMismatchAt == DateTime.MinValue || (nowUtc - _lastAutoMismatchAt).TotalSeconds >= 30);
                    if (canAutoFix)
                    {
                        _lastAutoMismatchAt = nowUtc;
                        _autoMismatchCount++;
                        AuditLog.Record("WATCH", "进程识别被关闭（find-process-mode=off），触发自动修复");
                        BeginInvoke(new Action(() => _ = RepairMismatchAsync(true)));
                    }
                    else
                    {
                        SetStatus("客户端关掉了进程识别（find-process-mode=off），按软件分流不生效", Theme.Warn);
                    }
                    return;
                }

                string why = Guard.Judge(audit, wantedItems.Count, patch);

                // IPv6 归客户端的应用设置管：默认完全不碰，也不据此触发自动重推。
                // 只有用户在帮助面板里主动开了「IPv6 防泄漏」，才把它算作需要补回的事。
                bool ipv6Guard = Settings.Ipv6GuardOn;
                if (ipv6Guard && audit.Ok && !audit.Ipv6On) _ipv6Attempts = 0;
                if (ipv6Guard && why == null && audit.Ok && audit.Ipv6On && _ipv6Attempts < 3)
                    why = "已开启 IPv6 防泄漏，但内核 IPv6 仍开着";

                if (why == null)
                { _repairFails = 0; _repairNoProgress = 0; _lastRepairReason = ""; return; }
                if ((DateTime.UtcNow - _lastRepairAt).TotalSeconds < 30) return;
                if (_repairFails >= 3) return;
                if (_repairNoProgress >= 3) return;   // 补回解决不了，等用户手动处理

                if (ipv6Guard && audit.Ok && audit.Ipv6On) _ipv6Attempts++;
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
                if (!UiAlive || _busy || _shuttingDown) return;
                if (_modeChanged) return;   // 模式切换还没保存，任何自动补回都不能推新模式
                if (Settings.RepairSuspended) return;   // 上次还原时名单没删掉，别自动接管回去
                // 推送前最后确认一次意图：托盘还原会删掉名单文件，
                // 已启动的旧任务不能因为它重新创建接管状态。
                if (!Profile.HasSavedIntent) return;
                owns = true;
                BeginBusy();
                _lastRepairAt = DateTime.UtcNow;
                _repairFails++;

                var visible = VisibleExes();
                var paths = PathMap();
                var policies = _all.Count > 0 ? PolicyMapFromRows() : Profile.LoadPolicyMap();
                List<Profile.Item> built = null;
                var res = await Task.Run(() =>
                {
                    built = ComposeWithHelpers(exes, visible, paths, policies);
                    return Profile.Apply(built, true);
                });
                if (!UiAlive || _shuttingDown) return;
                if (!res.Ok)
                {
                    // 评审 R2：Apply 返回「接管意图已撤销…」= 用户在托盘放过还原，
                    // 看守按用户意图安静退出，不是补回失败。撤销刚才预增的失败计数，
                    // 记一条 SKIP 日志直接返回——不进 catch 触发 FAIL 记录，
                    // 也不会连败 3 次后弹「请点保存」（用户本来就是要撤，弹这个反而警告无效）。
                    // 判别用 Profile.RevokedIntentMessage 常量，与 ApplyCore 同一来源。
                    if (Profile.IsRevokedIntent(res.Message))
                    {
                        _repairFails--;
                        AuditLog.Record("SKIP", "自动补回按用户意图退出：" + why + " / " + res.Message);
                        return;
                    }
                    // 收尾批 P3-5：双开时没抢到跨进程保存锁属于「占用」，不是补回故障。
                    // 以前它走 catch 记 FAIL，连碰三次会让看守直接放弃，实际什么都没坏。
                    if (Profile.ApplyBusyMessage.Equals(res.Message, StringComparison.Ordinal))
                    {
                        _repairFails--;
                        AuditLog.Record("SKIP", "另一个 ProxyPicker 正在保存，本轮补回跳过：" + why);
                        return;
                    }
                    throw new InvalidOperationException(res.Message ?? "内核没有确认");
                }

                SetEffectiveProxy(built);
                _state.Applied = true;
                _state.RuleCount = res.RuleCount;
                UpdateLogContext();
                AuditLog.Record("REPAIR", "第 " + _repairFails + " 次补回成功：" + exes.Count
                    + " 条规则，内核现有 " + res.RuleCount + " 条；原因：" + why);
                // 推送成功就清零失败数：以前只在「对账一致」时清零，跨模块误判会让
                // 明明每次都推成功却累到 3 次后看守停摆。
                _repairFails = 0;
                if (why == _lastRepairReason) _repairNoProgress++;
                else { _repairNoProgress = 0; _lastRepairReason = why; }
                SetStatus("已自动补回 " + exes.Count + " 条规则", Theme.Dim);
                if (_repairNoProgress >= 3)
                {
                    AuditLog.Record("WATCH", "同一问题连续 3 次补回仍未消失，暂停自动修复：" + why);
                    Toast("自动补回没能稳定生效",
                          "已经连续补回 3 次，问题仍在（" + why + "）；请点「保存并生效」手动处理",
                          Theme.Warn, 1);
                }
                else Toast(title, why, Theme.Accent, 2);
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

        /// <summary>
        /// 勾一个软件时，把它同目录的辅助进程一起写进规则。
        /// Electron / 类 VS Code 软件的登录、更新、语言服务常由 helper 发起连接，
        /// 只写主 exe 会让这些连接落到 MATCH,DIRECT —— Antigravity 登录失败的根因。
        /// </summary>
        /// <summary>界面当前看得见的软件名快照，必须在 UI 线程取好再交给后台。</summary>
        HashSet<string> VisibleExes()
        {
            return new HashSet<string>(_all.Select(r => r.Exe), StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 界面行里的「exe 名 → 完整路径」快照。必须在 UI 线程取，后台只读这份副本。
        /// 目录规则要靠它反推软件装在哪里；拿不到路径的软件仍然按进程名处理。
        /// </summary>
        Dictionary<string, string> PathMap()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in _all)
            {
                if (string.IsNullOrEmpty(r.Path) || string.IsNullOrEmpty(r.Exe)) continue;
                if (!map.ContainsKey(r.Exe)) map[r.Exe] = r.Path;
            }
            // 仅收集 UI 线程内存快照；托盘冷启动 _all 为空时由后台 ComposeWithHelpers 复用 Scanner.Scan() 补齐，
            // 避免在 UI 线程同步扫描全系统进程造成界面卡顿。
            return map;
        }

        List<Profile.Item> ComposeWithHelpers(List<string> proxy, HashSet<string> visible)
        {
            return ComposeWithHelpers(proxy, visible, null, null);
        }

        List<Profile.Item> ComposeWithHelpers(List<string> proxy, HashSet<string> visible,
                                              Dictionary<string, string> paths)
        {
            return ComposeWithHelpers(proxy, visible, paths, null);
        }

        List<Profile.Item> ComposeWithHelpers(List<string> proxy, HashSet<string> visible,
                                              Dictionary<string, string> paths,
                                              Dictionary<string, string> policies)
        {
            var peers = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, Scanner.ProcInfo> scanned = null;
            try
            {
                scanned = Scanner.Scan();
                foreach (var kv in scanned)
                {
                    var good = new List<string>();
                    foreach (var p in kv.Value.DirPeers)
                    {
                        // 复审 P1：同目录 helper 只取列表里看得见、且不是后台/基础设施的，
                        // 否则 crashpad_handler / sandbox-cli 这类共用名会被写成代理规则。
                        if (visible != null && !visible.Contains(p)) continue;
                        if (Catalog.IsInfrastructure(p) || Catalog.IsNoise(p)) continue;
                        if (Catalog.IsSharedRuntime(p)) continue;   // node/python 等不能写裸名
                        good.Add(p);
                    }
                    // 安装根目录子目录里的 helper（如 Antigravity 的 resources\bin\language_server.exe）
                    // 通常压根不会出现在软件列表里，但它才是发登录请求的那个进程 ——
                    // 这里不看 visible，只看是不是基础设施/噪声。上限 8 个，避免刷屏。
                    foreach (var p in kv.Value.TreePeers)
                    {
                        if (good.Count >= 8) break;
                        if (Catalog.IsInfrastructure(p) || Catalog.IsNoise(p)) continue;
                        // 共用运行时（node.exe 等）只交给目录整树规则覆盖，不写全局裸名。
                        if (Catalog.IsSharedRuntime(p)) continue;
                        if (!good.Contains(p)) good.Add(p);
                    }
                    if (good.Count > 0) peers[kv.Key] = good;
                }
            }
            catch { }   // 扫描失败退化为只写主 exe，不阻断保存

            // 托盘未打开面板时 paths 为空：直接在后台线程复用本次 Scanner.Scan() 补全可执行文件路径
            if ((paths == null || paths.Count == 0) && scanned != null && scanned.Count > 0)
            {
                var bgPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in scanned)
                {
                    if (kv.Value == null || string.IsNullOrEmpty(kv.Value.Path)) continue;
                    if (!bgPaths.ContainsKey(kv.Key)) bgPaths[kv.Key] = kv.Value.Path;
                }
                paths = bgPaths;
            }

            // 整树覆盖：只对「能确定是这个软件自己的安装目录」生成目录规则。
            // 公共容器（Program Files / AppData / 桌面 / 下载）会被 AppGroup 拒掉，
            // 不会因为勾了一个软件把整台机器的程序都代理走。
            List<string> roots = null;
            Dictionary<string, string> rootPolicies = null;
            try
            {
                if (paths != null && !Profile.PathRulesRejected && AppGroup.KernelSupportsPathRules())
                {
                    var chosen = new List<string>();
                    var byRoot = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
                    foreach (var exe in proxy)
                    {
                        string path;
                        if (!paths.TryGetValue(exe, out path) || string.IsNullOrEmpty(path)) continue;
                        chosen.Add(path);
                        string normExe = Profile.Normalize(exe);
                        string pol = "PP-FIXED";
                        if (policies != null)
                        {
                            string custom;
                            if (policies.TryGetValue(normExe, out custom) && !string.IsNullOrEmpty(custom)) pol = custom;
                        }
                        var singleRoot = AppGroup.Roots(new[] { path });
                        foreach (var r in singleRoot)
                        {
                            HashSet<string> polSet;
                            if (!byRoot.TryGetValue(r, out polSet))
                            {
                                polSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                                byRoot[r] = polSet;
                            }
                            polSet.Add(pol);
                        }
                    }
                    var candidateRoots = AppGroup.Roots(chosen);
                    roots = new List<string>();
                    rootPolicies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var r in candidateRoots)
                    {
                        HashSet<string> polSet;
                        if (byRoot.TryGetValue(r, out polSet) && polSet.Count > 1)
                        {
                            // 同一安装目录下有多个软件绑定了不同通道（如高速 vs 固定IP）：保守跳过整树目录规则，仅走精确进程名防误伤
                            continue;
                        }
                        roots.Add(r);
                        if (polSet != null && polSet.Count == 1)
                        {
                            foreach (var pOnly in polSet) rootPolicies[r] = pOnly;
                        }
                    }
                }
            }
            catch { roots = null; rootPolicies = null; }

            var items = Profile.Compose(proxy, null, peers, roots, policies, rootPolicies);

            // 登录窗口例外：ChatGPT 桌面版这类 MSIX 应用把 OAuth / 人机验证放进共享的
            // WebView2 运行时（msedgewebview2.exe），它不在软件目录里、也不在用户勾选名单里。
            // 完全接管下没勾的一律落到 MATCH,DIRECT，登录请求被直连送出去 → 登不上；
            // 关掉本软件后规则撤销、回到订阅原规则，就又好了（用户实机报告）。
            // 这里作为派生规则放行：只在「完全接管 + 用户确实勾了软件」时生效，
            // 不写进名单、取消全部勾选即消失；订阅兼容模式本来就没有短路 MATCH，不需要它。
            if (!Settings.PatchOnly && proxy.Count > 0 && Settings.LoginWindowProxyOn &&
                !items.Any(i => i.Exe.Equals("msedgewebview2.exe", StringComparison.OrdinalIgnoreCase)))
            {
                items.Add(new Profile.Item("msedgewebview2.exe", "PP-FIXED") { Derived = true });
            }

            // 「启动自检更新 / 节点风控检测」均依赖本程序访问公网。白名单保存后将自身加入派生代理规则，
            // 这样无论用户是否关闭自动更新或自动切换，都能直接测本机节点风控。
            if (!Settings.PatchOnly && proxy.Count > 0 &&
                !items.Any(i => i.Exe.Equals("proxypicker.exe", StringComparison.OrdinalIgnoreCase)))
            {
                items.Add(new Profile.Item("proxypicker.exe", "PP-FIXED") { Derived = true });
            }

            // 返回构造后的规则清单，不直接改 UI 共享状态 _effectiveProxy；
            // 由调用方确认事务落盘/生效且修订号未过期后通过 SetEffectiveProxy 提交。
            return items;
        }

        void SetEffectiveProxy(IEnumerable<Profile.Item> items)
        {
            var eff = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var rxList = new List<Regex>();
            var staticEff = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var staticRxList = new List<Regex>();
            bool staticOn = StaticProxy.IsEnabledAndValid;
            if (items != null)
            {
                foreach (var it in items)
                {
                    if (it == null) continue;
                    bool isStatic = staticOn && StaticProxy.IsStaticPolicy(it.Policy);
                    if (it.IsPathRule)
                    {
                        string pat = !string.IsNullOrEmpty(it.PathRegex) ? it.PathRegex : it.Exe;
                        if (string.IsNullOrEmpty(pat)) continue;
                        try
                        {
                            var rx = new Regex(pat, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
                            rxList.Add(rx);
                            if (isStatic) staticRxList.Add(rx);
                        }
                        catch { }
                    }
                    else if (!string.IsNullOrEmpty(it.Exe))
                    {
                        string norm = Profile.Normalize(it.Exe);
                        eff.Add(norm);
                        if (isStatic) staticEff.Add(norm);
                    }
                }
            }
            _effective = new EffectiveRules(eff, rxList, staticEff, staticRxList);
        }

        static string NormPolicyKey(string p)
        {
            string t = (p ?? "").Trim();
            if (t.Length == 0 || t.Equals("PP-FIXED", StringComparison.OrdinalIgnoreCase)) return "PP-FIXED";
            return t;
        }

        Dictionary<string, string> SnapshotSavedPoliciesFromRows()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in _all)
                if (r.Proxy) map[r.Exe] = NormPolicyKey(r.Policy);
            return map;
        }

        bool IsRowPolicyChanged(AppRow r)
        {
            if (r == null || !r.Proxy || !_savedProxy.Contains(r.Exe)) return false;
            string savedPol;
            _savedPolicies.TryGetValue(r.Exe, out savedPol);
            return !string.Equals(NormPolicyKey(r.Policy), NormPolicyKey(savedPol), StringComparison.OrdinalIgnoreCase);
        }

        Dictionary<string, string> PolicyMapFromRows()
        {
            var policies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in _all)
                if (r.Proxy && !string.IsNullOrEmpty(r.Policy)) policies[r.Exe] = r.Policy;
            return policies;
        }

        sealed class Pack
        {
            public Profile.KernelState State;
            public List<AppRow> Rows;
        }

        static Pack LoadPack()
        {
            // 主动刷新列表时顺带重扫客户端并清空通道缓存，保证关掉 A 开 B（如 Verge -> FlClash）能立即切换过去。
            Discovery.Detect(true);
            Mihomo.Forget();
            // 老版本名单在客户端目录下；只在 GUI 读列表时迁移一次，只读命令不写盘。
            Paths.MigrateLegacyList();
            var procs = Scanner.Scan();
            var policy = Profile.LoadPolicyMap();
            var installed = InstalledScanner.Scan();

            // 名字来源四路合流：已选名单 + 运行中 + 已安装（注册表/开始菜单）+ 常见候选
            var names = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var k in policy.Keys) if (seen.Add(k)) names.Add(k);
            // 手动添加的 exe 必须参与每次重建，否则刷新一次就「消失」（外部评审 #2）。
            foreach (var k in Settings.CustomExes) if (seen.Add(k)) names.Add(k);
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
                // 名单里写的是 DIRECT 表示「明确直连」，不算勾选；
                // 写成某个组名表示「勾了，但走这个组」，必须显示为已勾选，保存时也要保留（F-13）。
                bool explicitDirect = "DIRECT".Equals(pol, StringComparison.OrdinalIgnoreCase);
                bool proxy = pol != null && !explicitDirect;

                string name = Catalog.DisplayName(exe);
                if (!Catalog.HasNice(exe) && inst != null && !string.IsNullOrEmpty(inst.Name)) name = inst.Name;

                // 识别边界写进副标题：勾一个 exe 会连带命中所有同名进程，
                // Electron 类软件还常由同目录的 helper 真正发起连接。以前这两件事完全看不出来。
                string hint = "";
                if (info != null && info.NameConflict) hint += "同名 " + info.AllPaths.Count + " 处 · ";
                if (info != null && info.DirPeers.Count > 0) hint += "另有 " + info.DirPeers.Count + " 个相关进程 · ";

                rows.Add(new AppRow
                {
                    Exe = exe,
                    Name = name,
                    Description = Settings.IsCustomExe(exe)
                        ? ("手工添加" + (hint.Length > 0 ? " · " + hint : ""))
                        : hint + Catalog.Description(exe),
                    NameConflict = info != null && info.NameConflict,
                    AllPaths = info != null ? new List<string>(info.AllPaths) : new List<string>(),
                    Peers = info != null ? new List<string>(info.DirPeers) : new List<string>(),
                    Path = info != null ? info.Path : (inst != null ? inst.Path : ""),
                    Alias = inst != null ? (inst.Name ?? "") : "",
                    Running = info != null ? info.Count : 0,
                    Proxy = proxy,
                    Known = pol != null,
                    Policy = pol ?? "",
                    Common = Catalog.IsCommon(exe) || (inst != null && Catalog.IsCommonByInstall(inst.Name)) ||
                             Settings.UsageOf(exe) > 0 || Settings.IsCustomExe(exe),
                    Anim = proxy ? 1f : 0f,
                    Usage = Settings.UsageOf(exe),
                    LastUsed = Settings.LastUsedOf(exe)
                });
            }
            // 只保留「列表里看得见」的相关进程，右键一键勾选才有对象；
            // 系统进程和没被收进列表的 helper 不拿来充数。
            var rowExes = new HashSet<string>(rows.Select(r => r.Exe), StringComparer.OrdinalIgnoreCase);
            foreach (var r in rows)
                r.Peers = r.Peers
                    .Where(p => rowExes.Contains(p) && !string.Equals(p, r.Exe, StringComparison.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToList();
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
                foreach (var r in _all) if (IsRowPolicyChanged(r)) n++;
                return n;
            }
        }

        void ShowDirtyStatus()
        {
            var changed = new List<string>();
            foreach (var r in _all)
            {
                bool saved = _savedProxy.Contains(r.Exe);
                if (r.Proxy != saved || IsRowPolicyChanged(r)) changed.Add(r.Name);
            }
            // 订阅兼容模式下「取消勾选」只是不再强制走代理，流量仍按订阅规则走。
            // 用户很容易理解成「关掉就直连」，然后在 IP 检测页反复开关都没变化。
            // 第一次出现这种取消动作时明确说一次，别让人以为软件坏了。
            if (!_compatHintShown && Settings.PatchOnly &&
                _all.Any(r => _savedProxy.Contains(r.Exe) && !r.Proxy))
            {
                _compatHintShown = true;
                Toast("取消勾选 ≠ 直连",
                      "订阅兼容模式仍按订阅规则走；想「关掉就直连」，请在帮助面板切到「代理白名单」。",
                      Theme.Warn, 2);
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
                if (!UiAlive || _busy || _help || _page != 0 || _shuttingDown) return;
                if (_all.Count == 0)
                {
                    Toast("还没有可保存的名单", "等列表读出来再试一次", Theme.Warn, 1);
                    return;
                }
                string checkCfg = Paths.ClashYaml;
                string liveMode = await Task.Run(() =>
                {
                    var res = Mihomo.FetchJson("/configs", 3000, checkCfg);
                    return res.Ok && res.Data != null ? (Json.Str(res.Data, "mode") ?? "") : "";
                });
                if (!UiAlive || _busy || _shuttingDown) return;

                bool switchBackToRule = false;
                if (!string.IsNullOrEmpty(liveMode) && !Profile.IsRuleMode(liveMode))
                {
                    var ans = MessageBox.Show(this,
                        "Clash 当前处于「" + Profile.ModeDisplayName(liveMode) + "」模式，按软件分流规则不会生效。\n\n" +
                        "是否自动将 Clash 切回「规则」模式并应用勾选名单？",
                        "ProxyPicker", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                    if (ans != DialogResult.Yes)
                    {
                        SetStatus("保持「" + Profile.ModeDisplayName(liveMode) + "」模式，未应用名单", Theme.Warn);
                        return;
                    }
                    switchBackToRule = true;
                }

                ownsBusy = true;
                int saveRevision = _revision;
                long saveEpoch = Profile.IntentEpoch;   // 等锁期间若发生还原，代次会变
                // v1.3.8：代次只是本进程的静态计数，另一个 ProxyPicker 进程做 --restore
                // 不会改它。名单文件的磁盘签名才能发现「等锁期间被跨进程还原」。
                string saveIntentSig = Profile.IntentSignature();
                bool modeChanged = _modeChanged;
                BeginBusy();
                SetStatus("正在保存并更新分流…", Theme.Dim);
                var proxy = _all.Where(r => r.Proxy).Select(r => r.Exe).ToList();
                var now = new HashSet<string>(proxy, StringComparer.OrdinalIgnoreCase);
                var policyChanged = _all.Where(r => IsRowPolicyChanged(r)).Select(r => r.Exe);
                var changed = now.Where(x => !_savedProxy.Contains(x))
                    .Concat(_savedProxy.Where(x => !now.Contains(x)))
                    .Concat(policyChanged)
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                // 只切换了分流模式、名单没动时，受影响的软件同样要重连：
                // 白名单→订阅兼容（或反向）会改变未勾选软件的路由语义，旧连接不会自己重匹配。
                if (modeChanged)
                    foreach (var exe in now) if (!changed.Contains(exe)) changed.Add(exe);
                var visible = VisibleExes();
                var paths = PathMap();
                var policies = _all.Count > 0 ? PolicyMapFromRows() : Profile.LoadPolicyMap();
                List<Profile.Item> builtItems = null;
                var pack = await Task.Run(() => Profile.WithApplyLock(() =>
                {
                    var p = new SavePack();
                    // 保存事务同源（独立审阅第二轮）：整条链只识别一次客户端，
                    // 旧连接快照 / payload 生成 / 推送 / 回读 / 删除旧连接共享同一份配置。
                    // v1.3.7：整段都在跨进程锁内，托盘还原无法插到「推送完成、删除连接之前」。
                    string cfg = Paths.ClashYaml;
                    if (switchBackToRule)
                    {
                        bool cp; string ce;
                        Profile.PatchKernelConfigsForRules(cfg, out cp, out ce);
                    }
                    p.Snapshot = Profile.CaptureConnections(changed, cfg); // 先快照旧连接，再推新规则
                    builtItems = ComposeWithHelpers(proxy, visible, paths, policies);
                    p.Apply = Profile.Apply(builtItems, false, cfg,
                                            saveEpoch, saveIntentSig, switchBackToRule);
                    if (p.Apply.Ok && changed.Count > 0 && p.Snapshot.Error == null)
                        p.Reconnect = Profile.CloseCapturedConnections(p.Snapshot, cfg);
                    return p;
                }));
                if (switchBackToRule && pack.Apply.Ok) _state.Mode = "rule";
                // 等待期间发生了还原 / 退出：内核状态已经被别人改过，不能再拿这次结果覆盖界面。
                if (!UiAlive || saveRevision != _revision) return;

                if (pack.Apply.Ok || (pack.Apply.Pushed && !pack.Apply.PersistFailed))
                    SetEffectiveProxy(builtItems);

                // 推送成功就把内存名单对齐（外部评审 F4）：回读校验失败时内核其实已经改了，
                // 若仍拿旧名单去对账，看守会把用户刚取消勾选的软件又补回来。
                // 收尾批 P3-2：但「名单没写进磁盘」是例外——此时对齐内存会让看守拿一份
                // 磁盘上并不存在的名单去对账。保持旧内存名单，下一轮看守自然把内核补回
                // 磁盘那份，状态重新一致；界面也会明确提示本次改动没有落盘。
                if (pack.Apply.Pushed && !pack.Apply.PersistFailed)
                {
                    _savedProxy = now;
                    _savedPolicies = SnapshotSavedPoliciesFromRows();
                    _list.SavedProxy = now;
                    _list.SavedPolicies = _savedPolicies;
                    SyncStaticUiBanner();
                }
                if (pack.Apply.Ok)
                {
                    // 模式切换直到这次保存成功才落盘（v1.3.7：以前一切换就写 settings.json，
                    // 没点保存就退出，下次启动模式和内核规则对不上）。
                    if (modeChanged && !Settings.CommitMode())
                        Toast("模式已生效，但没能记住", "settings.json 写入失败：" + Settings.LastSaveError +
                              "；重启后会回到原来的模式", Theme.Warn, 2);
                    Settings.ResumeRepair();   // 用户主动保存 = 重新接受自动补回
                    _modeChanged = false;
                    _mismatch = 0;
                    _mismatchItems = new List<Traffic.MismatchItem>();
                    _mismatchStreak = 0;
                    _autoMismatchCount = 0;
                    _lastAutoMismatchAt = DateTime.MinValue;
                    foreach (var r in _all)
                    {
                        if (r.Proxy && !r.Known) Settings.Touch(r.Exe);
                        r.Known = r.Proxy;
                        // 已保存改为直连的软件清空残留策略（如原 PP-STATIC-OUT），防止日后重新勾选时幽灵复发
                        if (!r.Proxy) r.Policy = "";
                    }
                    _state.Applied = true; _state.RuleCount = pack.Apply.RuleCount;
                    _state.Policy = pack.Apply.Policy;
                    UpdateLogContext();
                    // 404 单独记账：连接自然结束是正常的，但若整批都 404，
                    // 往往说明请求打到了另一个内核（连接 ID 不属于它）。写进审计日志，
                    // 「复制诊断信息」里能看到，不至于只显示一个「清理 0 条」。
                    if (pack.Reconnect != null &&
                        (pack.Reconnect.Failed > 0 || pack.Reconnect.NotFound > 0))
                        AuditLog.Record("RECONNECT", "旧连接清理：关闭 " + pack.Reconnect.Closed +
                            " / 已自然结束(404) " + pack.Reconnect.NotFound +
                            " / 失败 " + pack.Reconnect.Failed);
                    AuditLog.Record("SAVE", "手动保存 " + now.Count + " 个软件，内核现有 "
                        + pack.Apply.RuleCount + " 条规则，" + pack.Apply.Ms + "ms"
                        + (pack.Apply.Ipv6Off ? "，IPv6 已关" : "") + "；重连 " + changed.Count + " 个");

                    // v1.3.8：模式切换改变的是「未勾选软件」的路由语义。我们只能精确清理
                    // 名单里现在和过去的软件；其余软件的既有连接不会自己重匹配，
                    // 这一点必须说清楚，不能只报一句「已生效」让人以为全部切换完成。
                    string modeHint = modeChanged
                        ? " · 模式已切换：未勾选软件的旧连接可能仍走旧路由，重启这些软件可立即生效"
                        : "";
                    if (changed.Count == 0)
                    {
                        SetStatus("已生效 · 没有需要重连的软件 · " + pack.Apply.Ms + "ms" + modeHint,
                            modeChanged ? Theme.Warn : Theme.Ok);
                        Toast(modeChanged ? "已生效（模式已切换）" : "已生效",
                            "名单没有变化，规则已是最新 · " + pack.Apply.Ms + " ms" + modeHint,
                            modeChanged ? Theme.Warn : Theme.Ok, modeChanged ? 2 : 0);
                    }
                    else if (pack.Snapshot.Error != null)
                    {
                        SetStatus("规则已生效，但旧连接自动清理失败；请新开页面", Theme.Warn);
                        Toast("规则已生效", "旧连接没能自动清理，新开一个页面即可" + modeHint, Theme.Warn, 1);
                    }
                    else
                    {
                        int closed = pack.Reconnect == null ? 0 : pack.Reconnect.Closed;
                        int failed = pack.Reconnect == null ? 0 : pack.Reconnect.Failed;
                        string msg = "已生效 · 重连 " + changed.Count + " 个软件 · 清理 " + closed + " 条旧连接";
                        if (failed > 0) msg += " · " + failed + " 条失败";
                        msg += modeHint;
                        SetStatus(msg, (failed > 0 || modeChanged) ? Theme.Warn : Theme.Ok);
                        Toast(failed > 0 ? "已生效（部分旧连接未清理）" : "已生效，可以继续用了",
                            "重连 " + changed.Count + " 个软件 · 清理 " + closed + " 条旧连接 · " + pack.Apply.Ms + " ms" + modeHint,
                            (failed > 0 || modeChanged) ? Theme.Warn : Theme.Ok, (failed > 0 || modeChanged) ? 1 : 0);
                    }

                    // 名单变了，流量分类的基准也跟着变
                    PollTraffic();
                    if (StaticProxy.IsEnabledAndValid && _state.ConfigOk && _state.ProxyActive && !_staticChecking)
                        _ = CheckStaticProxyHealthAsync(false);
                }
                else
                {
                    CrashLog.Write(new InvalidOperationException("应用返回失败，内核未确认生效"), "保存规则");
                    // v1.3.8：内核已经是新规则、磁盘名单却没写进去，而且补偿推送也失败——
                    // 这是「内核与磁盘不一致」的严重状态，必须进审计，不能只弹一次提示。
                    if (pack.Apply.RecoveryRequired)
                        AuditLog.Record("SAVE", "内核与磁盘名单不一致（补偿推送失败）：" + pack.Apply.Message);
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
            if (!UiAlive || _busy || _shuttingDown) return;
            if (_all.Count == 0) return;
            foreach (var r in _all) r.Proxy = false;
            _list.Invalidate(); ShowDirtyStatus();
        }

        /// <summary>
        /// 托盘菜单是命令：先关帮助面板、避开忙碌，再清空并直接保存。
        /// 复审 P1：以前撞上 _help / _busy 守卫会静默不保存，且提示仍说「记得点保存」。
        /// </summary>
        public void AllDirectPublic()
        {
            if (!UiAlive || _shuttingDown) return;
            if (_help) CloseHelp();
            if (_page != 0) SwitchPage(0);
            if (_busy) { Toast("正在忙", "等当前操作结束再点「全部设为直连」", Theme.Warn, 1); return; }
            if (_all.Count == 0) { Toast("还没有可保存的名单", "等列表读出来再试一次", Theme.Warn, 1); return; }
            AllDirect();
            SaveAsync();   // 保存自己会给成功 / 失败提示，这里不再重复弹
        }

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

        void RebuildVisible(bool animate, bool preserveScroll = false)
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
            _list.SetRows(rows, animate, preserveScroll);
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
            // Closed 会在菜单项 Click 的内部收尾之前触发；这里立刻 Dispose，
            // WinForms 随后 ReparentToDropDownOwnerWindow 就会访问已释放对象。
            // 延后到本轮消息处理结束再释放，排序菜单和右键菜单都走这个安全时序。
            menu.Closed += (s, e) =>
            {
                var closed = (ContextMenuStrip)s;
                if (!UiAlive) return;
                BeginInvoke(new Action(() => { if (!closed.IsDisposed) closed.Dispose(); }));
            };
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
            menu.Show(this, new Point(Theme.Px(SortRect.X), Theme.Px(SortRect.Bottom + 4)));
        }

        void ShowRowMenu(AppRow row, Point screen)
        {
            if (!UiAlive || _busy || row == null) return;
            var menu = new ContextMenuStrip { ShowImageMargin = false };
            // Closed 会在菜单项 Click 的内部收尾之前触发；这里立刻 Dispose，
            // WinForms 随后 ReparentToDropDownOwnerWindow 就会访问已释放对象。
            // 延后到本轮消息处理结束再释放，排序菜单和右键菜单都走这个安全时序。
            menu.Closed += (s, e) =>
            {
                var closed = (ContextMenuStrip)s;
                if (!UiAlive) return;
                BeginInvoke(new Action(() => { if (!closed.IsDisposed) closed.Dispose(); }));
            };

            var toggle = new ToolStripMenuItem(row.Proxy ? "改为直连" : "改为走代理");
            toggle.Click += (s, e) =>
            {
                if (_busy) return;
                row.Proxy = !row.Proxy;
                _list.Invalidate();
                SyncStaticUiBanner();
                ShowDirtyStatus();
            };
            menu.Items.Add(toggle);

            if (StaticProxy.IsEnabledAndValid && row.Proxy)
            {
                var cfgSp = StaticProxy.Current;
                var toggleStatic = new ToolStripMenuItem(row.IsStatic
                    ? "切回普通高速代理"
                    : ("锁定走「" + cfgSp.DisplayLabel + "」（固定IP）"));
                toggleStatic.Click += (s, e) =>
                {
                    if (_busy) return;
                    row.Policy = row.IsStatic ? "PP-FIXED" : StaticProxy.NodeName;
                    _list.Invalidate();
                    SyncStaticUiBanner();
                    ShowDirtyStatus();
                };
                menu.Items.Add(toggleStatic);
            }

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

            // Electron / 浏览器常常由同目录的 helper 真正发起连接；勾主程序却漏了它，
            // 规则就白写了。这里给一条「一起勾上」的捷径。
            if (row.Peers.Count > 0)
            {
                var peers = new ToolStripMenuItem("把 " + row.Peers.Count + " 个相关进程一起勾上");
                peers.Click += (s, e) =>
                {
                    if (_busy) return;
                    var want = new HashSet<string>(row.Peers, StringComparer.OrdinalIgnoreCase);
                    row.Proxy = true;
                    int n = 0;
                    foreach (var r in _all)
                        if (want.Contains(r.Exe) && !r.Proxy) { r.Proxy = true; r.Anim = 1f; n++; }
                    RebuildVisible(false);
                    ShowDirtyStatus();
                    Toast("已勾上 " + n + " 个相关进程", "它们可能才是真正发起连接的那个 · 记得点保存", Theme.Accent, 2);
                };
                menu.Items.Add(peers);
            }

            menu.Items.Add(new ToolStripSeparator());

            if (!string.IsNullOrEmpty(row.Path))
                menu.Items.Add(new ToolStripMenuItem("路径：" + ShortenPath(row.Path)) { Enabled = false });

            // 同名 exe 来自不同目录时，PROCESS-NAME 会一网打尽；
            // 把每条路径摆出来，用户才可能发现「我勾的其实不是这个」。
            if (row.NameConflict && row.AllPaths.Count > 1)
            {
                var conflict = new ToolStripMenuItem("同名程序 " + row.AllPaths.Count + " 处（点路径可复制）");
                foreach (string p in row.AllPaths)
                {
                    string full = p;
                    var pathItem = new ToolStripMenuItem(ShortenPath(full));
                    pathItem.Click += (s, e) =>
                    {
                        try { Clipboard.SetText(full); Toast("已复制完整路径", full, Theme.Accent, 2); }
                        catch { }
                    };
                    conflict.DropDownItems.Add(pathItem);
                }
                menu.Items.Add(conflict);
            }

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
                if (!UiAlive || !Visible || _polling || _shuttingDown) return;
                ownsPoll = true;
                _polling = true;
                // 流量采样与「刷新软件列表」互不阻塞；否则刷新期间曲线必然断一拍。
                // 每 5 秒在应用页无感增量刷新运行中进程（不丢弃未保存勾选），每 10 秒检查出口。
                int tick = ++_outTick;
                if (tick % 5 == 0 && _page == 0 && !_help && !_busy && !_shuttingDown)
                    _ = AutoRefreshRunningProcessesAsync();
                if (tick % 10 == 0) CheckOutbound();
                // 「预期」用真正推给内核的名单（含派生规则），不是界面上勾了几个。
                // 首次保存前还没有下发记录，才退回界面勾选。
                var effective = _effective;
                var set = effective != null &&
                          (effective.Exes.Count > 0 || effective.PathRegexes.Count > 0 || Profile.HasSavedIntent)
                    ? effective.Exes
                    : new HashSet<string>(_all.Where(r => r.Proxy).Select(r => Profile.Normalize(r.Exe)), StringComparer.OrdinalIgnoreCase);
                var pathRegexes = effective != null && effective.PathRegexes.Count > 0 ? effective.PathRegexes : null;
                var staticSet = effective != null && effective.StaticExes.Count > 0 ? effective.StaticExes : null;
                var staticPathRegexes = effective != null && effective.StaticPathRegexes.Count > 0 ? effective.StaticPathRegexes : null;
                // 只有「代理白名单 + 内核处于 rule 模式」下预期出口才明确：
                // 全局模式下所有流量走 GLOBAL，订阅兼容里未勾选走订阅规则，均不能判成分流异常。
                bool wasTrafficOk = _traffic != null && _traffic.Ok;
                bool isRule = _state.ConfigOk && Profile.IsRuleMode(_state.Mode);
                string cfgForTraffic = Paths.ClashYaml;
                var s = await Task.Run(() => Traffic.Poll(set, !Settings.PatchOnly && isRule, pathRegexes, cfgForTraffic, staticSet, staticPathRegexes));
                if (!UiAlive || !Visible) return;
                _traffic = s;
                if (s.Ok) ApplyCoverage(s);
                else
                {
                    // 内核断线后立刻把旧网速归零，防止界面停留在大数字上假装还在跑流量
                    _rateProxyDown = _rateProxyUp = _rateStaticDown = _rateStaticUp = _rateDirectDown = _rateDirectUp = 0;
                    if (wasTrafficOk)
                    {
                        Mihomo.Forget();
                        CheckOutbound();
                    }
                }
                Wake();   // 曲线是每帧推进的，采样到点后要把时钟叫醒（失败也要，否则转圈会僵住）
                if (s.Ok && (s.Seconds > 0 || _sparkProxy.Count == 0))
                {
                    long now = _watch.ElapsedMilliseconds;
                    long gap = _sparkStamp == 0 ? 0 : now - _sparkStamp;
                    if (_sparkAt.Count > 0 && gap > 4000)
                    {
                        // 断采（收起过 / 内核卡了一下）：把旧历史整体平移到「现在」，
                        // 曲线继续往前挪一格。以前这里是 Clear，用户看到的就是「整条消失了」。
                        long shift = now - SparkPeriodMs - _sparkAt[_sparkAt.Count - 1];
                        if (shift > 0)
                            for (int i = 0; i < _sparkAt.Count; i++) _sparkAt[i] += shift;
                    }
                    Push(_sparkProxy, (float)s.ProxyDown);
                    Push(_sparkStatic, (float)s.StaticDown);
                    Push(_sparkDirect, (float)s.DirectDown);
                    long last = _sparkAt.Count > 0 ? _sparkAt[_sparkAt.Count - 1] : long.MinValue;
                    long next = now;
                    if (_sparkAt.Count > 0 && next <= last) next = last + 1;   // 严格递增，避免竖线
                    _sparkAt.Add(next);
                    while (_sparkAt.Count > SparkPoints) _sparkAt.RemoveAt(0);
                    // axis 只能随秒表向前，不能在采样落地时重置或换时间尺度。
                    _sparkAxis = next;
                    _sparkStamp = now;
                    _sparkPulse = 1f;
                }
                if (s.Ok) Invalidate(new Rectangle(0, 0, Width, Theme.Px(SummaryTop + SummaryH + 4)));
                else Invalidate();
            }
            catch (Exception ex) { ReportFailure("读取网速", ex); }
            finally { if (ownsPoll) _polling = false; }
        }

        /// <summary>
        /// 覆盖检测：把「内核这一轮看得到哪些进程」翻译成每一行的三态标记。
        ///
        /// 为什么需要它：本工具的底座是 TUN，系统代理模式下不读系统代理的软件
        /// （微信、游戏、Antigravity 这类 Electron 应用的 Node 侧）流量根本进不了内核，
        /// 名单写进去也是空的。以前界面照样显示"运行中"，等于替用户撒谎。
        /// </summary>
        /// <summary>
        /// 后台对账配置纠偏门槛：仅在「规则（rule）」模式下、且进程识别被关闭（off）时才由后台自动修复。
        /// 全局（global）或直连（direct）模式属于用户在客户端的主动选择，后台严禁擅自强改回 rule。
        /// </summary>
        internal static bool WatchShouldAutoFixConfig(string mode, string findProcess)
        {
            if (!Profile.IsRuleMode(mode)) return false;
            return !string.IsNullOrEmpty(findProcess) &&
                   findProcess.Trim().Equals("off", StringComparison.OrdinalIgnoreCase);
        }

        internal static bool ShouldTriggerAutoMismatch(
            int mismatchCount, int mismatchStreak, int autoCount,
            bool patchOnly, bool repairSuspended, bool hasSavedIntent,
            bool modeChanged, bool busy, bool shuttingDown,
            double secondsSinceLastAuto, double secondsSinceLastWatchRepair,
            bool isRuleMode = true)
        {
            if (!isRuleMode) return false;
            if (mismatchCount <= 0 || mismatchStreak < 2) return false;
            if (autoCount >= 3) return false;
            if (patchOnly || repairSuspended || !hasSavedIntent) return false;
            if (modeChanged || busy || shuttingDown) return false;
            if (secondsSinceLastAuto < 45 || secondsSinceLastWatchRepair < 15) return false;
            return true;
        }

        void ApplyCoverage(Traffic.Sample s)
        {
            var now = DateTime.UtcNow;
            foreach (var kv in s.Seen) _seenAt[kv.Key] = now;
            _visibleProcs = s.Seen.Count;
            bool isRule = Profile.IsRuleMode(_state.Mode);
            _mismatch = isRule ? s.Mismatch : 0;
            _mismatchItems = isRule ? (s.Mismatches ?? new List<Traffic.MismatchItem>()) : new List<Traffic.MismatchItem>();

            if (!isRule || s.Mismatch <= 0)
            {
                _mismatchStreak = 0;
                _autoMismatchCount = 0;
            }
            else
            {
                _mismatchStreak++;
                double secAuto = _lastAutoMismatchAt == DateTime.MinValue ? 999 : (now - _lastAutoMismatchAt).TotalSeconds;
                double secWatch = _lastRepairAt == DateTime.MinValue ? 999 : (now - _lastRepairAt).TotalSeconds;
                if (ShouldTriggerAutoMismatch(
                        s.Mismatch, _mismatchStreak, _autoMismatchCount,
                        Settings.PatchOnly, Settings.RepairSuspended, Profile.HasSavedIntent,
                        _modeChanged, _busy, _shuttingDown, secAuto, secWatch, isRule))
                {
                    _lastAutoMismatchAt = now;
                    _autoMismatchCount++;
                    BeginInvoke(new Action(() => _ = RepairMismatchAsync(true)));
                }
            }

            int covered = 0;
            foreach (var r in _all)
                if (r.Proxy && s.Seen.ContainsKey(r.Exe)) covered++;
            _coveredProcs = covered;

            // 观察期不足 90 秒不下"看不见"的结论：软件可能只是还没发请求
            bool judged = (now - _startedAt).TotalSeconds >= 90;
            bool canBeBlind = _state.ConfigOk && !_state.Tun && _state.SystemProxyEnabled;   // 只有已开系统代理底座才可能漏掉进程

            bool changed = false;
            foreach (var r in _all)
            {
                int c = 0;
                if (r.Running > 0)
                {
                    DateTime at;
                    bool seen = _seenAt.TryGetValue(r.Exe, out at) && (now - at).TotalSeconds < 150;
                    if (seen) c = 1;
                    else if (canBeBlind && judged) c = 2;
                }
                if (r.Coverage != c) { r.Coverage = c; changed = true; }
            }
            if (changed) _list.Invalidate();

            if (!_blindToasted && canBeBlind && judged)
            {
                int portHint = _state.MixedPort > 0 ? _state.MixedPort : 7897;
                foreach (var r in _all)
                {
                    if (!r.Proxy || r.Coverage != 2) continue;
                    _blindToasted = true;
                    Toast("有软件勾了却没生效",
                        "它不读系统代理，流量没进内核 —— 切 TUN，或在它自己的设置里填 127.0.0.1:" + portHint,
                        Theme.Warn, 3);
                    break;
                }
            }
        }

        async void CheckOutbound()
        {
            bool ownsCheck = false;
            try
            {
                if (!UiAlive || _busy || _checkingOutbound || _nodeActionBusy || _shuttingDown) return;
                ownsCheck = true;
                _checkingOutbound = true;
                int revision = _revision;
                var snap = await Task.Run(() =>
                {
                    bool sysOn;
                    string sysSrv;
                    Profile.ReadSystemProxyState(out sysOn, out sysSrv);

                    var env = Discovery.Detect();
                    string cfg = env.ConfigPath ?? "";
                    string client = env.Client ?? "";
                    var cfgRes = Mihomo.FetchJson("/configs", 4000, cfg);
                    if (!cfgRes.Ok || cfgRes.Data == null)
                    {
                        // 可能刚刚关闭旧客户端、开启了新客户端（例如 Verge -> FlClash）：重新探测一次并清通道缓存
                        Mihomo.Forget();
                        var reEnv = Discovery.Detect(true);
                        if (!string.IsNullOrEmpty(reEnv.ConfigPath))
                        {
                            cfg = reEnv.ConfigPath;
                            client = reEnv.Client ?? "";
                            cfgRes = Mihomo.FetchJson("/configs", 4000, cfg);
                            env = reEnv;
                        }
                    }

                    string policy = Profile.PolicyName(cfg);
                    string mode = "?";
                    bool tun = false;
                    string stack = "";
                    string fpm = "?";
                    int mixedPort = 0;
                    bool cfgOk = cfgRes.Ok && cfgRes.Data != null;
                    if (!cfgOk)
                    {
                        return new
                        {
                            CfgOk = false,
                            HasCfg = env.Ok,
                            Client = client,
                            Policy = policy,
                            Mode = mode,
                            Tun = false,
                            Stack = "",
                            Fpm = "?",
                            MixedPort = 0,
                            SysOn = sysOn,
                            SysSrv = sysSrv,
                            Err = !string.IsNullOrEmpty(cfgRes.Error) ? cfgRes.Error : "客户端可能未启动",
                            Detail = new Profile.OutboundDetail()
                        };
                    }

                    mode = Json.Str(cfgRes.Data, "mode") ?? "?";
                    var tunObj = Json.Obj(cfgRes.Data, "tun");
                    if (tunObj != null)
                    {
                        tun = Json.Bool(tunObj, "enable", false);
                        stack = Json.Str(tunObj, "stack") ?? "";
                    }
                    fpm = Json.Str(cfgRes.Data, "find-process-mode") ?? "?";
                    mixedPort = Json.Int(cfgRes.Data, "mixed-port", 0);
                    if (mixedPort <= 0) mixedPort = Json.Int(cfgRes.Data, "port", 0);

                    if (!Settings.PatchOnly && _savedProxy.Count == 0 && Profile.HasSavedIntent && Profile.IsRuleMode(mode))
                    {
                        return new
                        {
                            CfgOk = true,
                            HasCfg = env.Ok,
                            Client = client,
                            Policy = policy,
                            Mode = mode,
                            Tun = tun,
                            Stack = stack,
                            Fpm = fpm,
                            MixedPort = mixedPort,
                            SysOn = sysOn,
                            SysSrv = sysSrv,
                            Err = "",
                            Detail = new Profile.OutboundDetail { Outbound = "DIRECT", Chain = "DIRECT", HistoryDelayMs = -1 }
                        };
                    }

                    string activePolicy = mode.Equals("global", StringComparison.OrdinalIgnoreCase) ? "GLOBAL"
                        : (mode.Equals("direct", StringComparison.OrdinalIgnoreCase) ? "DIRECT" : policy);
                    var obj = Mihomo.GetJson("/proxies", 6000, cfg);
                    var proxies = Json.Obj(obj, "proxies");
                    var detail = Core.Profile.ResolveOutboundDetail(proxies, activePolicy);
                    return new
                    {
                        CfgOk = true,
                        HasCfg = env.Ok,
                        Client = client,
                        Policy = policy,
                        Mode = mode,
                        Tun = tun,
                        Stack = stack,
                        Fpm = fpm,
                        MixedPort = mixedPort,
                        SysOn = sysOn,
                        SysSrv = sysSrv,
                        Err = "",
                        Detail = detail
                    };
                });
                if (!UiAlive || _busy || revision != _revision) return;

                string oldClient = _state.Client ?? "";
                _state.SystemProxyEnabled = snap.SysOn;
                _state.SystemProxyServer = snap.SysSrv;
                if (!string.IsNullOrEmpty(snap.Client)) _state.Client = snap.Client;
                if (!string.Equals(oldClient, _state.Client ?? "", StringComparison.OrdinalIgnoreCase))
                    _catalogSeq++;

                if (!snap.CfgOk)
                {
                    bool wasOnline = _state.ConfigOk;
                    _state.ConfigOk = false;
                    _state.Tun = false;
                    _state.Mode = "?";
                    _state.FindProcess = "?";
                    _state.Outbound = "";
                    _state.Chain = "";
                    _state.Applied = false;
                    _state.DelayMs = -1;
                    _state.NodeAlive = null;
                    ResetDelayBannerState();
                    _catalogSeq++;
                    _mismatch = 0;
                    _mismatchItems.Clear();
                    _mismatchStreak = 0;
                    _state.Error = snap.HasCfg
                        ? ("读不到内核配置：" + snap.Err + "（当前未连接）")
                        : "未检测到运行中的代理客户端（当前未连接）";
                    _nodesPage.SetLoading(false);
                    _nodesPage.SetCurrentOutbound("", -1, false, false);
                    _qualityPage.SetCurrentOutbound("");
                    if (wasOnline) SetStatus(_state.Error, Theme.Warn);
                    InvalidateNavBar();
                    Invalidate(new Rectangle(0, 0, Width, Theme.Px(TitleH + SummaryH + 4)));
                    return;
                }

                bool wasOffline = !_state.ConfigOk;
                _state.ConfigOk = true;
                _state.MixedPort = snap.MixedPort;
                if (wasOffline)
                {
                    _state.Error = null;
                    if (_page == 0)
                    {
                        if (!Profile.IsRuleMode(snap.Mode))
                            SetStatus("已连上内核 · 当前为「" + Profile.ModeDisplayName(snap.Mode) + "」模式，按软件分流暂停", Theme.Warn);
                        else if (!_state.ProxyActive && !snap.Tun && !snap.SysOn)
                            SetStatus("客户端已连上内核，但 TUN 和系统代理均未开启（当前未接管流量）", Theme.Warn);
                        else
                            SetStatus(_state.Applied ? "已连上代理内核 · 名单生效中" : "已连上代理内核 · 点保存并生效后开启分流", Theme.Dim);
                    }
                }

                bool modeChangedNow = !string.Equals(_state.Mode, snap.Mode, StringComparison.OrdinalIgnoreCase);
                _state.Mode = snap.Mode;
                _state.Tun = snap.Tun;
                _state.TunStack = snap.Stack;
                _state.FindProcess = snap.Fpm;
                if (modeChangedNow && !Profile.IsRuleMode(_state.Mode))
                {
                    _mismatch = 0;
                    _mismatchItems.Clear();
                    _mismatchStreak = 0;
                    SetStatus("Clash 已切换到「" + Profile.ModeDisplayName(_state.Mode) + "」模式，按软件分流暂停中", Theme.Warn);
                }
                else if (modeChangedNow && Profile.IsRuleMode(_state.Mode) && _state.Error == null)
                {
                    SetStatus(_state.ProxyActive
                        ? (_state.Applied ? "已切回规则模式 · 名单生效中" : "已切回规则模式")
                        : "已切回规则模式，但 TUN 与系统代理均未开启", _state.ProxyActive ? Theme.Dim : Theme.Warn);
                }

                if (string.IsNullOrEmpty(snap.Detail.Outbound))
                {
                    _state.Outbound = "";
                    _state.Chain = "";
                    _state.DelayMs = -1;
                    _state.NodeAlive = null;
                    _delayMs = -1;
                    _nodesPage.SetCurrentOutbound("", -1, false, false);
                    _qualityPage.SetCurrentOutbound("");
                    InvalidateNavBar();
                    Invalidate(new Rectangle(0, 0, Width, Theme.Px(SummaryTop + SummaryH + 4)));
                    return;
                }
                if (!string.IsNullOrEmpty(snap.Policy)) _state.Policy = snap.Policy;
                string prev = _state.Outbound;
                _state.Chain = snap.Detail.Chain;
                _state.NodeAlive = snap.Detail.Alive;
                if (snap.Detail.Outbound == prev)
                {
                    // 同一出口优先保留最新实时测速延迟（超时则保持 -1），防止被 /proxies 的历史延迟覆盖。
                    _state.DelayMs = _delayTimedOut ? -1 : (_delayMs > 0 ? _delayMs : (snap.Detail.HistoryDelayMs > 0 ? snap.Detail.HistoryDelayMs : -1));
                    _nodesPage.SetCurrentOutbound(_state.Outbound, _state.DelayMs, _delayTimedOut, _state.ProxyActive);
                    _qualityPage.SetCurrentOutbound(_state.Outbound);
                    InvalidateNavBar();
                    Invalidate(new Rectangle(0, 0, Width, Theme.Px(TitleH + SummaryH + 4)));
                    return;
                }
                _state.DelayMs = snap.Detail.HistoryDelayMs > 0 ? snap.Detail.HistoryDelayMs : -1;
                _state.Outbound = snap.Detail.Outbound;
                _delayMs = -1;
                _delayTimedOut = false;
                _delayTimeoutStreak = 0;
                _delayAlertActive = false;
                _delayNode = snap.Detail.Outbound;
                _nodesPage.SetCurrentOutbound(_state.Outbound, _state.DelayMs, false, _state.ProxyActive);
                _qualityPage.SetCurrentOutbound(_state.Outbound);
                InvalidateNavBar();
                Invalidate(new Rectangle(0, 0, Width, Theme.Px(TitleH + SummaryH + 4)));
                if (!string.IsNullOrEmpty(prev))
                    Toast("节点已切到 " + CleanEmoji(snap.Detail.Outbound), "流量将由这个节点发出", Theme.Accent, 2);
                if (_state.ProxyActive && Core.Profile.ClassifyExit(snap.Detail.Outbound) == Core.Profile.ExitKind.Proxy)
                    _ = RunDelayTestAsync(false);
            }
            catch (Exception ex) { ReportFailure("读取代理节点", ex); }
            finally { if (ownsCheck) _checkingOutbound = false; }
        }

        bool ShouldRunPeriodicDelayTest()
        {
            if (_batchDelayRunning || _failoverBusy || _nodeActionBusy) return false;
            int interval = Settings.DelayCheckSeconds;
            if (interval <= 0) return false;
            if (!_state.ConfigOk || !_state.ProxyActive) return false;
            if (!Visible && !Profile.HasSavedIntent && !Settings.AutoFailover) return false;
            string node = _state.Outbound;
            if (string.IsNullOrEmpty(node) || Profile.ClassifyExit(node) != Profile.ExitKind.Proxy) return false;
            double effectiveInterval = (Settings.AutoFailover && _delayTimeoutStreak == 1)
                ? Math.Min(interval, 6)
                : interval;
            return (DateTime.UtcNow - _lastDelayTestAt).TotalSeconds >= effectiveInterval;
        }

        /// <summary>判断测速超时后是否可以进入自动切换：手动测速或连续 2 轮确认超时后放行，防止单次瞬间抖动/503 误切可用节点。</summary>
        internal static bool ShouldTriggerFailoverAfterTimeout(bool manual, int streak, bool autoFailoverEnabled, bool busy)
        {
            if (!autoFailoverEnabled || busy) return false;
            return manual || streak >= 2;
        }

        /// <summary>托盘菜单与摘要胶囊共用的主动测速入口。</summary>
        public void TestDelayPublic()
        {
            if (!UiAlive || _shuttingDown) return;
            if (InvokeRequired) { try { BeginInvoke(new Action(TestDelayPublic)); } catch { } return; }
            _ = RunDelayTestAsync(true);
        }

        /// <summary>首页固定 IP 延迟胶囊与节点页底条共用的独立固定 IP 实时测速入口。</summary>
        public void TestStaticDelayPublic()
        {
            if (!UiAlive || _shuttingDown) return;
            if (InvokeRequired) { try { BeginInvoke(new Action(TestStaticDelayPublic)); } catch { } return; }
            if (!StaticProxy.IsEnabledAndValid)
            {
                OpenStaticProxyDialog();
                return;
            }
            if (!_state.ConfigOk || !_state.ProxyActive)
            {
                Toast("暂无法测固定 IP 延迟", "请先确认代理内核已连接且 TUN 或系统代理已开启", Theme.Warn, 2);
                return;
            }
            _ = CheckStaticProxyHealthAsync(true);
        }

        async Task RunDelayTestAsync(bool manual)
        {
            if (!UiAlive || _delayTesting || _nodeActionBusy || _batchDelayRunning || _failoverBusy || _shuttingDown) return;
            string node = _state.Outbound;
            if (string.IsNullOrEmpty(node) || Profile.ClassifyExit(node) != Profile.ExitKind.Proxy)
            {
                if (manual)
                {
                    string reason = string.IsNullOrEmpty(node) ? "当前未识别到代理节点" : ("当前节点为 " + node + "，无需测速");
                    Toast("暂无法测速", reason, Theme.Warn, 1);
                    SetStatus(reason, Theme.Warn);
                }
                return;
            }

            _delayTesting = true;
            if (!string.Equals(_delayNode, node, StringComparison.OrdinalIgnoreCase)) _delayTimeoutStreak = 0;
            _delayNode = node;
            _lastDelayTestAt = DateTime.UtcNow;
            if (manual) SetStatus("正在通过 Clash 测试「" + CleanEmoji(node) + "」延迟…", Theme.Dim);
            Invalidate(new Rectangle(0, 0, Width, Theme.Px(SummaryTop + SummaryH + 4)));

            bool triggeredFailover = false;
            try
            {
                string cfg = Paths.ClashYaml;
                var res = await Task.Run(() => Profile.MeasureDelayConfirmed(node, null, 0, cfg));
                if (!UiAlive) return;
                // 若测速期间用户已经切到了别的节点，不拿旧结果覆盖新节点状态
                if (!string.Equals(node, _state.Outbound, StringComparison.OrdinalIgnoreCase)) return;

                if (res.KernelUnreachable)
                {
                    _delayError = res.Error;
                    if (manual)
                    {
                        Toast("读不到测速结果", res.Error, Theme.Warn, 1);
                        SetStatus("Clash 控制接口暂时不可达：" + res.Error, Theme.Warn);
                    }
                    return;
                }

                if (res.Ok)
                {
                    bool wasTimeout = _delayAlertActive || _delayTimeoutStreak > 0;
                    _delayMs = res.DelayMs;
                    _delayTimedOut = false;
                    _delayTimeoutStreak = 0;
                    _delayError = "";
                    _delayAlertActive = false;
                    _state.DelayMs = res.DelayMs;
                    _state.NodeAlive = true;
                    _nodesPage.UpdateSingleNodeDelay(node, res.DelayMs, true, "");
                    InvalidateNavBar();

                    if (wasTimeout)
                    {
                        AuditLog.Record("DELAY", "节点已恢复可用：" + node + " · " + res.DelayMs + " ms");
                        SetStatus("节点「" + CleanEmoji(node) + "」已恢复（" + res.DelayMs + " ms）", Theme.Ok);
                        Toast("节点测速已恢复", CleanEmoji(node) + " · " + res.DelayMs + " ms", Theme.Ok, 2);
                        if (!Visible && TrayBalloon != null)
                            TrayBalloon("节点已恢复：" + CleanEmoji(node), "实时测速 " + res.DelayMs + " ms");
                    }
                    else if (manual)
                    {
                        SetStatus("当前节点「" + CleanEmoji(node) + "」延迟 " + res.DelayMs + " ms", Theme.Ok);
                        Toast("测速正常", CleanEmoji(node) + " · " + res.DelayMs + " ms", Theme.Accent, 2);
                    }
                    return;
                }

                // 节点经过二次交叉确认后仍然超时或不可达
                _delayMs = -1;
                _delayTimedOut = res.TimedOut;
                _delayError = res.Error ?? "节点测速失败";
                if (res.TimedOut)
                {
                    _delayTimeoutStreak = string.Equals(_delayNode, node, StringComparison.OrdinalIgnoreCase)
                        ? _delayTimeoutStreak + 1 : 1;
                    _state.DelayMs = -1;
                    _state.NodeAlive = false;
                    _nodesPage.UpdateSingleNodeDelay(node, -2, false, _delayError);
                    InvalidateNavBar();

                    // 开启「超时自动切换」：需经过连续两轮确认（或用户手动测速双测失败）并满足冷却时间，防止瞬间抖动或 503 误踢可用节点
                    bool cooldownOk = _lastFailoverAt == DateTime.MinValue ||
                                      (DateTime.UtcNow - _lastFailoverAt).TotalSeconds >= (manual ? 15 : 30);
                    if (ShouldTriggerFailoverAfterTimeout(manual, _delayTimeoutStreak, Settings.AutoFailover, _failoverBusy || _nodeActionBusy) && cooldownOk)
                    {
                        triggeredFailover = true;
                        _ = TryAutoFailoverAsync(node);
                    }
                    else if (Settings.AutoFailover && !manual && _delayTimeoutStreak == 1)
                    {
                        SetStatus("「" + CleanEmoji(node) + "」首次测速未通过（" + _delayError + "），将在几秒后自动复检确认…", Theme.Warn);
                    }
                    else
                    {
                        bool shouldAlert = manual || !_delayAlertActive ||
                                           (DateTime.UtcNow - _lastDelayAlertAt).TotalSeconds >= 120;
                        if (shouldAlert)
                        {
                            _delayAlertActive = true;
                            _lastDelayAlertAt = DateTime.UtcNow;
                            AuditLog.Record("DELAY", "节点测速超时：" + node + " · " + _delayError);
                            SetStatus("节点「" + CleanEmoji(node) + "」测速超时，请到「2. 节点切换」切换或开启自动切换", Theme.Warn);
                            Toast("节点测速超时", CleanEmoji(node) + " · " + _delayError + "，可在「2. 节点切换」换备用节点", Theme.Warn, 2);
                            if (TrayBalloon != null)
                                TrayBalloon("节点测速超时：" + CleanEmoji(node),
                                    _delayError + "\n打开「2. 节点切换」换备用节点或勾选超时自动切换");
                        }
                    }
                }
                else
                {
                    if (manual)
                    {
                        SetStatus("测速未通过：" + _delayError, Theme.Warn);
                        Toast("测速未通过", CleanEmoji(node) + " · " + _delayError, Theme.Warn, 1);
                    }
                    // 节点可能已在 Clash 中被删除/切换导致 404：顺手刷新一次出口信息
                    try { BeginInvoke(new Action(CheckOutbound)); } catch { }
                }
            }
            catch (Exception ex) { ReportFailure("节点测速", ex); }
            finally
            {
                _delayTesting = false;
                if (UiAlive && !triggeredFailover)
                {
                    InvalidateNavBar();
                    if (_page == 0) Invalidate(new Rectangle(0, 0, Width, Theme.Px(SummaryTop + SummaryH + 4)));
                }
                if (UiAlive && _state.ConfigOk && _state.ProxyActive && StaticProxy.IsEnabledAndValid && !_staticChecking)
                    _ = CheckStaticProxyHealthAsync(manual);
            }
        }

        // ---------------------------------------------------------------- 页面切换 / 节点切换 / 风控体检 / 自动故障转移

        internal static string BuildFailoverNotifyText(string cleanOldNode, string cleanNewNode, int delayMs)
        {
            string oldPart = string.IsNullOrWhiteSpace(cleanOldNode) ? "原节点" : cleanOldNode.Trim();
            string newPart = string.IsNullOrWhiteSpace(cleanNewNode) ? "新节点" : cleanNewNode.Trim();
            return oldPart + " 超时 → 已切至 " + newPart + (delayMs > 0 ? ("（" + delayMs + "ms）") : "");
        }

        static string PageDisplayName(int page)
        {
            if (page == 0) return "应用分流";
            if (page == 1) return "节点策略";
            if (page == 2) return "链路体检";
            return "上一页";
        }

        void SyncBackLabels()
        {
            string backText = _pageHistory.Count > 0 ? ("← 返回" + PageDisplayName(_pageHistory.Peek())) : "";
            _nodesPage.SetBackLabel(_page == 1 ? backText : "");
            _qualityPage.SetBackLabel(_page == 2 ? backText : "");
        }

        void PushAndSwitchPage(int targetPage)
        {
            if (!UiAlive || targetPage < 0 || targetPage > 2) return;
            if (_page != targetPage && (_pageHistory.Count == 0 || _pageHistory.Peek() != _page))
                _pageHistory.Push(_page);
            SwitchPage(targetPage, false);
        }

        void NavigateBack()
        {
            if (!UiAlive) return;
            if (_pageHistory.Count > 0)
            {
                int prev = _pageHistory.Pop();
                SwitchPage(prev, false);
                return;
            }
            if (_page != 0) SwitchPage(0, true);
        }

        public void SwitchPagePublic(int page)
        {
            if (!UiAlive) return;
            if (InvokeRequired) { try { BeginInvoke(new Action(() => SwitchPagePublic(page))); } catch { } return; }
            SwitchPage(page, true);
        }

        void SwitchPage(int page, bool clearHistory = true)
        {
            if (!UiAlive) return;
            if (page < 0 || page > 2) page = 0;
            if (clearHistory) _pageHistory.Clear();
            if (_help) _help = false;
            _page = page;
            SyncBackLabels();
            ApplyPageVisibility();
            if (page == 0)
            {
                SetStatus(_state.Error ?? (_state.Applied ? "名单已生效 · 改动后记得保存" : "名单未生效，请保存一次"),
                    _state.Error == null && _state.Applied ? Theme.Dim : Theme.Warn);
            }
            else if (page == 1 || page == 2)
            {
                _nodesPage.SetCurrentOutbound(_state.Outbound, _delayMs, _delayTimedOut, _state.ProxyActive);
                _qualityPage.SetCurrentOutbound(_state.Outbound);
                if (!_nodesLoadedOnce || page == 1) _ = LoadNodesCatalogAsync();
            }
            Invalidate();
        }

        void ApplyPageVisibility()
        {
            _list.Visible = !_help && _page == 0;
            _search.Visible = !_help && _page == 0;
            _nodesPage.Visible = !_help && _page == 1;
            _qualityPage.Visible = !_help && _page == 2;
            if (_page == 1) _nodesPage.BringToFront();
            else if (_page == 2) _qualityPage.BringToFront();
            LayoutChildren();
        }

        internal static bool IsCatalogReplyStale(int startRev, int curRev, int startSeq, int curSeq)
        {
            return startRev != curRev || startSeq != curSeq;
        }

        void ResetDelayBannerState()
        {
            _delayMs = -1;
            _delayTimedOut = false;
            _delayTimeoutStreak = 0;
            _delayAlertActive = false;
            _delayNode = "";
        }

        async Task LoadNodesCatalogAsync()
        {
            try
            {
                if (!UiAlive) return;
                int startRev = _revision;
                int startSeq = ++_catalogSeq;
                _nodesPage.SetLoading(true);
                string cfg = Paths.ClashYaml;
                var catalog = await Task.Run(() => NodeManager.LoadCatalog(cfg));
                if (!UiAlive || IsCatalogReplyStale(startRev, _revision, startSeq, _catalogSeq)) return;
                string nowCfg = Paths.ClashYaml;
                if (!string.IsNullOrEmpty(cfg) && !string.IsNullOrEmpty(nowCfg) &&
                    !string.Equals(cfg, nowCfg, StringComparison.OrdinalIgnoreCase))
                {
                    _nodesPage.SetLoading(false);
                    return;
                }
                _nodesLoadedOnce = true;
                _nodesPage.SetCatalog(catalog);
                bool sysOn;
                string sysSrv;
                Profile.ReadSystemProxyState(out sysOn, out sysSrv);
                _state.SystemProxyEnabled = sysOn;
                _state.SystemProxyServer = sysSrv;
                if (catalog != null && catalog.Ok)
                {
                    _state.ConfigOk = true;
                    _state.Mode = catalog.KernelMode;
                    bool emptyWhitelist = !Settings.PatchOnly && _savedProxy.Count == 0 && Profile.HasSavedIntent && Profile.IsRuleMode(_state.Mode);
                    string showOutbound = emptyWhitelist ? "DIRECT" : (!string.IsNullOrEmpty(catalog.CurrentOutbound) ? catalog.CurrentOutbound : "");
                    _state.Outbound = showOutbound;
                    _nodesPage.SetCurrentOutbound(showOutbound, _delayMs > 0 ? _delayMs : _state.DelayMs, _delayTimedOut, _state.ProxyActive);
                    _qualityPage.SetCatalog(showOutbound, _nodesPage.SnapshotConcreteNodes());
                }
                else
                {
                    _state.ConfigOk = false;
                    _state.Tun = false;
                    _state.Outbound = "";
                    _state.DelayMs = -1;
                    _nodesPage.SetCurrentOutbound("", -1, false, false);
                    _qualityPage.SetCatalog("", _nodesPage.SnapshotConcreteNodes());
                }
                InvalidateNavBar();
                if (_page == 0) Invalidate(new Rectangle(0, 0, Width, Theme.Px(TitleH + SummaryH + 4)));
            }
            catch (Exception ex) { ReportFailure("读取节点列表", ex); }
        }

        async Task InspectNodeQualityAsync(string nodeName, bool switchBeforeAndKeep)
        {
            if (!UiAlive || _nodeActionBusy || _batchQualityRunning || string.IsNullOrWhiteSpace(nodeName)) return;
            _nodeActionBusy = true;
            _batchDelayCancel = true;
            if (_page != 2) PushAndSwitchPage(2);
            try
            {
                string cfg = Paths.ClashYaml;
                bool probingCurrent = string.Equals(nodeName, _state.Outbound, StringComparison.OrdinalIgnoreCase);
                bool preferSidecar = !switchBeforeAndKeep && !probingCurrent;

                _qualityPage.SetSelectedTarget(nodeName);
                _qualityPage.SetTesting(true, nodeName);
                _nodesPage.SetInspectingNode(nodeName);

                if (preferSidecar)
                {
                    SetStatus("正在独立隔离引擎中检测「" + CleanEmoji(nodeName) + "」（不影响当前上网）…", Theme.Dim);
                    string sidecarErr = "";
                    NodeQuality.QualityReport sidecarRep = null;
                    await Task.Run(() =>
                    {
                        using (var sidecar = ProbeSidecar.TryStart(cfg, out sidecarErr))
                        {
                            if (sidecar == null) return;
                            if (!sidecar.SwitchGlobalNode(nodeName))
                            {
                                sidecarErr = "旁路引擎未能切换到节点：" + nodeName;
                                return;
                            }
                            System.Threading.Thread.Sleep(150);
                            sidecarRep = NodeQuality.RunQualityProbe(nodeName, Settings.DesiredRegion, false, cfg, sidecar);
                        }
                    });
                    if (!UiAlive) return;

                    if (sidecarRep != null)
                    {
                        ApplyCompletedQualityReport(nodeName, sidecarRep, false);
                        return;
                    }

                    // 极少数场景（如未检测到内核可执行文件）无法起旁路引擎，降级弹窗确认
                    _nodesPage.SetInspectingNode("");
                    _qualityPage.SetTesting(false, "");
                    var fallbackAns = MessageBox.Show(this,
                        "未能启动零干扰旁路探测引擎（" + (string.IsNullOrEmpty(sidecarErr) ? "未知原因" : sidecarErr) + "）。\n\n" +
                        "• 点「是(Y)」：改用主客户端临时切换探测（约 3–5 秒，测完自动恢复原节点）\n" +
                        "• 点「否(N)」：直接切换到该节点并执行检测（留在新节点）\n" +
                        "• 点「取消」：取消本次检测",
                        "旁路探测不可用 · 选择回退方式", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                    if (fallbackAns == DialogResult.Cancel) return;
                    switchBeforeAndKeep = fallbackAns == DialogResult.No;
                    _qualityPage.SetTesting(true, nodeName);
                    _nodesPage.SetInspectingNode(nodeName);
                }

                var plan = await Task.Run(() => NodeManager.InspectProbeRouting(nodeName, cfg));
                if (!UiAlive) return;

                bool useTempGlobal = false;
                if (plan.Ok && plan.NeedsTempGlobal)
                {
                    var ask = MessageBox.Show(this,
                        "检测到当前分流未覆盖本检测工具（可能尚未保存白名单，或当前为「订阅兼容」/目标节点不在默认主组），直接测试可能命中本地直连或别的组。\n\n" +
                        "是否允许检测期间在 Clash 内存中临时切为「全局模式」（绝不改动磁盘配置文件，测完约 3–5 秒立刻自动切回原模式和原节点）？",
                        "使用临时隔离通道检测", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                    if (ask == DialogResult.Cancel)
                    {
                        _nodesPage.SetInspectingNode("");
                        _qualityPage.SetTesting(false, "");
                        return;
                    }
                    useTempGlobal = ask == DialogResult.Yes;
                }

                bool switching = useTempGlobal || !string.Equals(nodeName, _state.Outbound, StringComparison.OrdinalIgnoreCase);
                SetStatus(switching
                    ? ("正在切换至「" + CleanEmoji(nodeName) + "」执行链路评估…")
                    : ("正在检测「" + CleanEmoji(nodeName) + "」出口 IP、风控分值与主流服务可用性…"), Theme.Dim);

                bool directRiskHint = !useTempGlobal && !Settings.PatchOnly && (_savedProxy.Count == 0 || !_effective.Exes.Contains("proxypicker.exe"));

                var result = await Task.Run(() =>
                {
                    var cat = NodeManager.LoadCatalog(cfg);
                    string origSelection = cat.CurrentSelection;
                    string origOutbound = cat.CurrentOutbound;
                    string origGlobal = plan.OriginalGlobalSelection;
                    string origMode = string.IsNullOrEmpty(plan.OriginalMode) ? "rule" : plan.OriginalMode;
                    string group = useTempGlobal ? "GLOBAL" : (!string.IsNullOrEmpty(plan.TargetSwitchGroup) ? plan.TargetSwitchGroup : cat.GroupName);
                    bool switched = false;
                    bool switchedMode = false;

                    if ((switching || useTempGlobal) && (string.IsNullOrEmpty(group) || (!useTempGlobal && !cat.Ok)))
                        return new { Ok = false, Err = string.IsNullOrEmpty(cat.Error) ? "未找到可切换策略组" : cat.Error, Report = (NodeQuality.QualityReport)null };

                    try
                    {
                        if (useTempGlobal && !string.Equals(origMode, "global", StringComparison.OrdinalIgnoreCase))
                        {
                            if (NodeManager.SetKernelModeTemp("global", cfg))
                            {
                                switchedMode = true;
                                System.Threading.Thread.Sleep(150);
                            }
                        }

                        if (switching)
                        {
                            var sw = NodeManager.SwitchProxyNode(group, nodeName, switchBeforeAndKeep, origOutbound, cfg);
                            if (!sw.Ok)
                                return new { Ok = false, Err = sw.Message, Report = (NodeQuality.QualityReport)null };
                            switched = true;
                            System.Threading.Thread.Sleep(240);
                        }

                        var rep = NodeQuality.RunQualityProbe(nodeName, Settings.DesiredRegion, directRiskHint, cfg);
                        return new { Ok = true, Err = "", Report = rep };
                    }
                    finally
                    {
                        if (switched && !switchBeforeAndKeep)
                        {
                            if (useTempGlobal && !string.IsNullOrEmpty(origGlobal))
                                NodeManager.SwitchProxyNode("GLOBAL", origGlobal, false, "", cfg);
                            if (!useTempGlobal && !string.IsNullOrEmpty(origSelection))
                                NodeManager.SwitchProxyNode(cat.GroupName, origSelection, false, "", cfg);
                        }
                        if (switchedMode)
                            NodeManager.SetKernelModeTemp(origMode, cfg);
                    }
                });

                if (!UiAlive) return;
                _nodesPage.SetInspectingNode("");
                if (!result.Ok)
                {
                    _qualityPage.SetTesting(false, "");
                    SetStatus("节点链路检测未完成：" + result.Err, Theme.Warn);
                    Toast("链路检测未完成", result.Err, Theme.Warn, 1);
                    return;
                }

                ApplyCompletedQualityReport(nodeName, result.Report, switchBeforeAndKeep);
            }
            catch (Exception ex)
            {
                _nodesPage.SetInspectingNode("");
                _qualityPage.SetTesting(false, "");
                ReportFailure("检测节点链路质量", ex);
            }
            finally
            {
                _nodeActionBusy = false;
                if (UiAlive) _ = LoadNodesCatalogAsync();
            }
        }

        void ApplyCompletedQualityReport(string nodeName, NodeQuality.QualityReport repOut, bool switchBeforeAndKeep)
        {
            _nodesPage.SetInspectingNode("");
            _lastQualityReport = repOut;
            _qualityPage.SetReport(repOut);
            _nodesPage.CacheQualityReport(repOut);

            if (switchBeforeAndKeep)
            {
                _state.Outbound = nodeName;
                _delayNode = nodeName;
                _delayMs = repOut.Net.AvgMs;
                _state.DelayMs = repOut.Net.AvgMs;
                _delayTimedOut = repOut.Net.Sent > 0 && repOut.Net.Received == 0;
                _nodesPage.SetCurrentOutbound(nodeName, repOut.Net.AvgMs, _delayTimedOut, _state.ProxyActive);
                _qualityPage.SetCurrentOutbound(nodeName);
            }
            else if (repOut.Net.AvgMs > 0)
            {
                _nodesPage.UpdateSingleNodeDelay(nodeName, repOut.Net.AvgMs, true, "");
            }

            AuditLog.Record("QUALITY",
                "单节点链路检测：region=" + (repOut.CountryCode.Length > 0 ? repOut.CountryCode : "?") +
                " risk=" + repOut.RiskScore + " services=" + repOut.MainstreamReadyCount + "/4" +
                " avg=" + repOut.Net.AvgMs + "ms loss=" + repOut.Net.LossPercent + "%" +
                (repOut.IsSidecarIsolated ? " isolated=true" : "") +
                (repOut.DirectIpv6Leak ? " ipv6Leak=true" : "") +
                (repOut.RoutedDirect ? " direct=true" : ""));

            Color statusCol = repOut.Level == NodeQuality.RiskLevel.Low ? Theme.Ok : Theme.Warn;
            SetStatus(CleanEmoji(nodeName) + "：" + repOut.VerdictTitle, statusCol);
            Toast("链路检测完成", repOut.VerdictTitle, repOut.Level == NodeQuality.RiskLevel.Low ? Theme.Ok : Theme.Warn,
                repOut.Level == NodeQuality.RiskLevel.Low ? 0 : 1);
            InvalidateNavBar();
        }

        /// <summary>全量节点评估：优先独立旁路引擎零干扰评估全部可用节点，同 IP 复用防限流。</summary>
        async Task ToggleBatchQualityScanAsync()
        {
            if (_batchQualityRunning)
            {
                _batchQualityCancel = true;
                SetStatus("正在停止全量节点评估并清理资源…", Theme.Dim);
                return;
            }
            if (_nodeActionBusy) { Toast("当前有节点操作在进行", "请稍候再启动全量节点评估", Theme.Warn, 1); return; }

            string cfg = Paths.ClashYaml;
            _nodeActionBusy = true;
            _batchDelayCancel = true;
            try
            {
                var cat = await Task.Run(() => NodeManager.LoadCatalog(cfg));
                if (!UiAlive) return;
                if (!cat.Ok || cat.Nodes.Count == 0)
                {
                    Toast("未能读取节点目录", string.IsNullOrEmpty(cat.Error) ? "请确认 Clash 客户端已启动" : cat.Error, Theme.Warn, 1);
                    return;
                }

                var concrete = cat.Nodes
                    .Where(n => !n.IsGroup && !ProbeSidecar.IsInformationalNodeName(n.Name))
                    .ToList();
                if (concrete.Count == 0)
                {
                    Toast("当前策略组没有可用节点", "请检查 Clash 策略组配置或订阅状态", Theme.Warn, 1);
                    return;
                }

                string previewErr = "";
                bool canSidecar = await Task.Run(() => ProbeSidecar.CanPrepareSidecar(cfg, out previewErr));
                if (!UiAlive) return;

                NodeManager.ProbeRoutingPlan plan = null;
                if (!canSidecar)
                {
                    plan = await Task.Run(() => NodeManager.InspectProbeRouting("", cfg));
                    if (!UiAlive) return;
                }

                string tip = canSidecar
                    ? ("将启动独立隔离探测引擎，对共 " + concrete.Count + " 个节点执行全量链路评估（出口 IP、风控分值、Google / Gemini / OpenAI / Claude 可用性）：\n\n" +
                       "• 零干扰隔离：通过独立本地旁路端口出站，全程不切换当前节点，不影响正在使用的网页、下载或会话；\n" +
                       "• 智能加速：并发预筛连通节点后逐个评测，同一落地 IP 自动复用风险档案防限流；\n" +
                       "• 随时可控：评估期间随时点「停止评估」即时中止。\n\n" +
                       "确认启动全量节点评估吗？")
                    : ("未能准备独立旁路引擎（" + previewErr + "），将改用主客户端临时切换模式评估共 " + concrete.Count + " 个节点：\n\n" +
                       "• 预筛连通节点并跳过超时节点；\n" +
                       (plan != null && plan.Ok && plan.NeedsTempGlobal
                           ? "• 评估期间将临时切换主内核为全局模式并逐一测节点（完成后自动恢复原节点与原模式）；\n\n"
                           : "• 评估期间逐一短暂切换主节点（完成后自动恢复原节点与原模式）；\n\n") +
                       "确认开始全量节点评估吗？");
                if (MessageBox.Show(this, tip, "全量节点评估", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                    return;

                _batchQualityRunning = true;
                _batchQualityCancel = false;
                if (_page != 2) PushAndSwitchPage(2);
                _qualityPage.SetBatchScanning(true, 0, concrete.Count, "正在初始化隔离探测引擎…");
                SetStatus("正在准备全量评估（" + concrete.Count + " 个节点）…", Theme.Dim);

                ProbeSidecar.SidecarSession sidecar = null;
                string sidecarStartErr = "";
                if (canSidecar)
                {
                    sidecar = await Task.Run(() => ProbeSidecar.TryStart(cfg, out sidecarStartErr));
                    if (!UiAlive) { if (sidecar != null) sidecar.Dispose(); return; }
                    if (sidecar == null)
                    {
                        var fallback = MessageBox.Show(this,
                            "独立隔离探测引擎未能启动（" + sidecarStartErr + "）。\n\n" +
                            "是否改为主客户端临时切换模式继续评估（测完自动恢复原节点与模式）？",
                            "旁路引擎启动失败", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                        if (fallback != DialogResult.Yes)
                        {
                            _qualityPage.SetBatchScanning(false, 0, 0, "");
                            SetStatus("已取消全量节点评估", Theme.Dim);
                            return;
                        }
                        plan = await Task.Run(() => NodeManager.InspectProbeRouting("", cfg));
                        if (!UiAlive) return;
                    }
                }

                using (sidecar)
                {
                    bool isolated = sidecar != null;
                    _qualityPage.SetBatchScanning(true, 0, concrete.Count,
                        isolated ? "旁路并发预筛连通性…" : "正在预筛连通节点…");
                    SetStatus(isolated
                        ? ("旁路并发预筛 " + concrete.Count + " 个节点连通性（当前网络零影响）…")
                        : ("正在预筛 " + concrete.Count + " 个节点连通性（跳过超时节点）…"), Theme.Dim);

                    if (isolated)
                    {
                        int preDone = 0;
                        await Task.Run(() =>
                        {
                            System.Threading.Tasks.Parallel.ForEach(
                                concrete,
                                new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = 6 },
                                n =>
                                {
                                    if (_batchQualityCancel || !UiAlive || _shuttingDown) return;
                                    string err;
                                    int ms = sidecar.MeasureDelay(n.Name, 2200, out err);
                                    n.LiveDelayMs = ms > 0 ? ms : -2;
                                    n.Alive = ms > 0;
                                    n.LastError = ms > 0 ? "" : err;
                                    int cur = System.Threading.Interlocked.Increment(ref preDone);
                                    if (!UiAlive) return;
                                    try
                                    {
                                        BeginInvoke(new Action(() =>
                                        {
                                            if (!UiAlive) return;
                                            _nodesPage.UpdateSingleNodeDelay(n.Name, n.LiveDelayMs, n.Alive, n.LastError);
                                            _qualityPage.SetBatchScanning(true, cur, concrete.Count, "预筛 " + n.Name);
                                        }));
                                    }
                                    catch { }
                                });
                        });
                    }
                    else
                    {
                        await Task.Run(() =>
                        {
                            bool ignoreUnreachable;
                            NodeManager.BatchMeasureNodes(concrete, 2200,
                                () => _batchQualityCancel || !UiAlive || _shuttingDown,
                                n =>
                                {
                                    if (!UiAlive) return;
                                    try { BeginInvoke(new Action(() => { if (UiAlive) _nodesPage.UpdateSingleNodeDelay(n.Name, n.LiveDelayMs, n.Alive, n.LastError); })); }
                                    catch { }
                                }, cfg, out ignoreUnreachable);
                        });
                    }

                    if (!UiAlive || _batchQualityCancel)
                    {
                        _qualityPage.SetBatchScanning(false, 0, 0, "");
                        SetStatus("已停止全量节点评估", Theme.Dim);
                        return;
                    }

                    var aliveTargets = concrete.Where(n => n.LiveDelayMs > 0).OrderBy(n => n.LiveDelayMs).ToList();
                    if (aliveTargets.Count == 0)
                    {
                        _qualityPage.SetBatchScanning(false, 0, 0, "");
                        Toast("未检测到可用节点", "所有节点延迟探测均超时，请检查订阅或本地网络", Theme.Warn, 2);
                        SetStatus("全量预筛未发现连通节点", Theme.Warn);
                        return;
                    }

                    int total = aliveTargets.Count;
                    int done = 0;
                    int ready4 = 0;
                    int aiReady = 0;
                    bool useTempGlobal = !isolated && plan != null && plan.Ok && plan.NeedsTempGlobal;
                    bool directRiskHint = !isolated && !useTempGlobal && !Settings.PatchOnly &&
                                          (_savedProxy.Count == 0 || !_effective.Exes.Contains("proxypicker.exe"));

                    _qualityPage.SetBatchScanning(true, 0, total,
                        (isolated ? "零干扰评估 " : "准备评估 ") + total + " 个可用节点…");
                    SetStatus((isolated ? "已筛选 " : "筛选出 ") + total + " 个连通节点，正在" +
                        (isolated ? "零干扰旁路通道" : "临时通道") + "评估 IP、风控与主流服务…", Theme.Dim);

                    bool restoreWarning = false;
                    await Task.Run(() =>
                    {
                        string origMode = plan != null && !string.IsNullOrEmpty(plan.OriginalMode) ? plan.OriginalMode : "rule";
                        string origSelection = cat.CurrentSelection;
                        string origGlobal = plan != null ? plan.OriginalGlobalSelection : "";
                        string group = useTempGlobal
                            ? "GLOBAL"
                            : (plan != null && !string.IsNullOrEmpty(plan.TargetSwitchGroup) ? plan.TargetSwitchGroup : cat.GroupName);
                        bool switchedMode = false;
                        var ipCache = new NodeQuality.BatchIpCache();

                        try
                        {
                            if (!isolated && useTempGlobal && !string.Equals(origMode, "global", StringComparison.OrdinalIgnoreCase))
                            {
                                if (NodeManager.SetKernelModeTemp("global", cfg))
                                {
                                    switchedMode = true;
                                    System.Threading.Thread.Sleep(160);
                                }
                            }

                            foreach (var n in aliveTargets)
                            {
                                if (_batchQualityCancel || !UiAlive || _shuttingDown) break;
                                if (UiAlive)
                                {
                                    int cur = done;
                                    try { BeginInvoke(new Action(() => { if (UiAlive) _qualityPage.SetBatchScanning(true, cur, total, n.Name); })); }
                                    catch { }
                                }

                                bool swOk = isolated
                                    ? sidecar.SwitchGlobalNode(n.Name)
                                    : NodeManager.SwitchProxyNode(group, n.Name, false, "", cfg).Ok;
                                if (swOk)
                                {
                                    System.Threading.Thread.Sleep(isolated ? 140 : 220);
                                    var rep = NodeQuality.RunFastQualityProbe(
                                        n.Name, Settings.DesiredRegion, directRiskHint, n.LiveDelayMs, ipCache, cfg, sidecar);
                                    if (!rep.RoutedDirect)
                                    {
                                        if (rep.MainstreamReadyCount == 4) ready4++;
                                        if (rep.GoogleAiUsable) aiReady++;
                                    }
                                    done++;

                                    if (UiAlive)
                                    {
                                        int curDone = done;
                                        try
                                        {
                                            BeginInvoke(new Action(() =>
                                            {
                                                if (!UiAlive) return;
                                                _qualityPage.UpsertBatchReport(rep);
                                                _nodesPage.CacheQualityReport(rep);
                                                _qualityPage.SetBatchScanning(true, curDone, total, n.Name);
                                            }));
                                        }
                                        catch { }
                                    }
                                }
                                else
                                {
                                    done++;
                                }
                            }
                        }
                        finally
                        {
                            // 每个恢复步骤独立捕获异常并检查返回值，避免某一步报错导致后续模式恢复被跳过。
                            if (!isolated)
                            {
                                if (useTempGlobal && !string.IsNullOrEmpty(origGlobal))
                                {
                                    try
                                    {
                                        if (!NodeManager.SwitchProxyNode("GLOBAL", origGlobal, false, "", cfg).Ok)
                                            restoreWarning = true;
                                    }
                                    catch { restoreWarning = true; }
                                }
                                if (!string.IsNullOrEmpty(cat.GroupName) && !string.IsNullOrEmpty(origSelection))
                                {
                                    try
                                    {
                                        if (!NodeManager.SwitchProxyNode(cat.GroupName, origSelection, false, "", cfg).Ok)
                                            restoreWarning = true;
                                    }
                                    catch { restoreWarning = true; }
                                }
                                if (switchedMode)
                                {
                                    try
                                    {
                                        if (!NodeManager.SetKernelModeTemp(origMode, cfg))
                                        {
                                            System.Threading.Thread.Sleep(150);
                                            if (!NodeManager.SetKernelModeTemp(origMode, cfg))
                                                restoreWarning = true;
                                        }
                                    }
                                    catch { restoreWarning = true; }
                                }
                            }
                        }
                    });

                    if (!UiAlive) return;
                    _qualityPage.SetBatchScanning(false, done, total, "");
                    AuditLog.Record("QUALITY_BATCH",
                        "全量节点评估完成：isolated=" + isolated + " done=" + done + "/" + total +
                        " full4=" + ready4 + " geminiReady=" + aiReady + (restoreWarning ? " restoreWarn=True" : ""));
                    string summary = _batchQualityCancel
                        ? ("已停止全量评估（完成 " + done + "/" + total + " · 四项全通 " + ready4 + " 个）")
                        : ("全量评估完成：共评估 " + done + " 个可用节点 · 四项主流服务全通 " + ready4 + " 个");
                    if (restoreWarning)
                    {
                        SetStatus(summary + "（部分恢复失败，请检查当前节点与模式）", Theme.Warn);
                        Toast("部分主内核状态未能自动还原", "请在 Clash 客户端确认已回到原来的「规则模式」与原节点", Theme.Warn, 2);
                    }
                    else
                    {
                        SetStatus(summary, ready4 > 0 || aiReady > 0 ? Theme.Ok : Theme.Warn);
                    }
                    Toast("全量节点评估完成",
                        "已测 " + done + " 个节点 · 四项服务全通 " + ready4 + " 个，可一键纳入备选池",
                        ready4 > 0 || aiReady > 0 ? Theme.Ok : Theme.Warn,
                        ready4 > 0 || aiReady > 0 ? 0 : 1);
                }
            }
            catch (Exception ex)
            {
                _qualityPage.SetBatchScanning(false, 0, 0, "");
                ReportFailure("全量评估节点链路质量", ex);
            }
            finally
            {
                _batchQualityRunning = false;
                _nodeActionBusy = false;
                if (UiAlive) _ = LoadNodesCatalogAsync();
            }
        }

        void InspectSpecificNodeFromPanel(string nodeName)
        {
            if (!UiAlive || _nodeActionBusy || string.IsNullOrWhiteSpace(nodeName)) return;
            PushAndSwitchPage(2);
            _ = InspectNodeQualityAsync(nodeName, false);
        }

        async void ToggleBatchDelayScan()
        {
            if (_batchDelayRunning)
            {
                _batchDelayCancel = true;
                SetStatus("正在停止批量测速…", Theme.Dim);
                return;
            }
            if (_nodeActionBusy || _failoverBusy) return;
            var targets = _nodesPage.SnapshotConcreteNodes();
            if (targets.Count == 0) { Toast("没有可测速节点", "请先刷新或确认策略组有节点", Theme.Warn, 1); return; }

            _batchDelayRunning = true;
            _batchDelayCancel = false;
            int total = targets.Count;
            int done = 0;
            bool kernelUnreachable = false;
            _nodesPage.SetBatchTesting(true, 0, total);
            SetStatus("正在批量测速 " + total + " 个节点（按延迟排队，可随时点停测）…", Theme.Dim);
            string cfg = Paths.ClashYaml;

            try
            {
                await Task.Run(() =>
                {
                    bool unreach;
                    NodeManager.BatchMeasureNodes(
                        targets, 2800,
                        () => _batchDelayCancel || !UiAlive || _shuttingDown,
                        n =>
                        {
                            int curDone = System.Threading.Interlocked.Increment(ref done);
                            if (!UiAlive) return;
                            try
                            {
                                BeginInvoke(new Action(() =>
                                {
                                    if (!UiAlive) return;
                                    _nodesPage.UpdateSingleNodeDelay(n.Name, n.LiveDelayMs, n.Alive, n.LastError);
                                    _nodesPage.SetBatchTesting(true, curDone, total);
                                }));
                            }
                            catch { }
                        }, cfg, out unreach);
                    kernelUnreachable = unreach;
                });
            }
            catch (Exception ex) { ReportFailure("批量测速", ex); }
            finally
            {
                _batchDelayRunning = false;
                if (UiAlive)
                {
                    _nodesPage.SetBatchTesting(false, done, total);
                    if (kernelUnreachable)
                    {
                        SetStatus("批量测速中断：内核控制接口已断开（已测 " + done + "/" + total + "）", Theme.Warn);
                        Toast("批量测速已中断", "与 Clash 控制接口的连接已断开，请确认客户端仍在运行", Theme.Warn, 2);
                    }
                    else
                    {
                        SetStatus(_batchDelayCancel
                            ? ("已停止批量测速（完成 " + done + "/" + total + " 个）")
                            : ("批量测速完成（共 " + done + " 个节点）"), Theme.Ok);
                        Toast("全测延迟完成", "已测 " + done + "/" + total + " 个节点，可在列表左侧勾选加入备选池", Theme.Ok, 0);
                    }
                }
            }
        }

        async Task SwitchNodeFromPanelAsync(string nodeName)
        {
            if (!UiAlive || _nodeActionBusy || string.IsNullOrWhiteSpace(nodeName)) return;
            _nodeActionBusy = true;
            _batchDelayCancel = true;
            string oldOut = _state.Outbound;
            SetStatus("正在切换节点到「" + CleanEmoji(nodeName) + "」并清理旧连接…", Theme.Dim);
            try
            {
                string cfg = Paths.ClashYaml;
                var outcome = await Task.Run(() =>
                {
                    var cat = NodeManager.LoadCatalog(cfg);
                    if (!cat.Ok || string.IsNullOrEmpty(cat.GroupName))
                        return new NodeManager.SwitchOutcome { Ok = false, Message = string.IsNullOrEmpty(cat.Error) ? "未找到可切换策略组" : cat.Error };
                    return NodeManager.SwitchProxyNode(cat.GroupName, nodeName, true, oldOut, cfg);
                });
                if (!UiAlive) return;
                if (!outcome.Ok)
                {
                    SetStatus("切换失败：" + outcome.Message, Theme.Warn);
                    Toast("切换节点失败", outcome.Message, Theme.Warn, 1);
                    return;
                }
                _state.Outbound = outcome.NewOutbound;
                _delayNode = outcome.NewOutbound;
                _delayMs = -1;
                _delayTimedOut = false;
                _delayTimeoutStreak = 0;
                _delayAlertActive = false;
                _nodesPage.SetCurrentOutbound(outcome.NewOutbound, -1, false);
                _qualityPage.SetCurrentOutbound(outcome.NewOutbound);
                InvalidateNavBar();
                AuditLog.Record("SWITCH", "手动切换节点（清理旧连接 " + outcome.ClosedConnections + " 条）");
                SetStatus("已切到「" + CleanEmoji(outcome.NewOutbound) + "」" +
                          (outcome.ClosedConnections > 0 ? " · 已断开 " + outcome.ClosedConnections + " 条旧连接" : ""),
                          Theme.Ok);
                Toast("已切换节点", CleanEmoji(outcome.NewOutbound) +
                      (outcome.ClosedConnections > 0 ? " · 已断开 " + outcome.ClosedConnections + " 条旧连接" : ""), Theme.Ok, 2);
            }
            catch (Exception ex) { ReportFailure("切换节点", ex); }
            finally
            {
                _nodeActionBusy = false;
                if (UiAlive)
                {
                    await LoadNodesCatalogAsync();
                    _ = RunDelayTestAsync(false);
                }
            }
        }

        async Task TryAutoFailoverAsync(string timedOutNode)
        {
            if (!UiAlive || _failoverBusy || _nodeActionBusy || _batchDelayRunning || _shuttingDown) return;
            _failoverBusy = true;
            _lastFailoverAt = DateTime.UtcNow;
            string timeTag = DateTime.Now.ToString("HH:mm:ss");
            try
            {
                string cfg = Paths.ClashYaml;
                var candidateNames = Settings.CandidateSet();
                if (candidateNames.Count == 0)
                {
                    const string emptyMsg = "当前节点超时，但备选节点池为空（请到「2. 节点切换」勾选常用节点）";
                    AuditLog.Record("FAILOVER", "当前节点超时，但备选节点池为空，无法自动切换");
                    _nodesPage.SetLastFailoverSummary("最近提醒 " + timeTag + "：备选节点池为空，未能自动切换");
                    SetStatus(emptyMsg, Theme.Warn);
                    Toast("备选节点池为空", "请到「2. 节点切换」勾选几个常用节点加入备选池", Theme.Warn, 2);
                    if (TrayBalloon != null) TrayBalloon("ProxyPicker 自动切换提醒", emptyMsg);
                    return;
                }

                SetStatus("「" + CleanEmoji(timedOutNode) + "」连续确认超时，正在并发复核备选池寻找稳定节点…", Theme.Warn);
                var result = await Task.Run(() =>
                {
                    var cat = NodeManager.LoadCatalog(cfg);
                    if (!cat.Ok || string.IsNullOrEmpty(cat.GroupName))
                        return new { Ok = false, Unreachable = false, OrigRecovered = false, OrigDelay = -1, BestDelay = -1, Sw = (NodeManager.SwitchOutcome)null, Err = cat.Error };

                    var pool = cat.Nodes
                        .Where(n => !n.IsGroup && n.IsCandidate && !string.Equals(n.Name, timedOutNode, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    if (pool.Count == 0)
                        return new { Ok = false, Unreachable = false, OrigRecovered = false, OrigDelay = -1, BestDelay = -1, Sw = (NodeManager.SwitchOutcome)null, Err = "备选池里除当前超时节点外无其他节点" };

                    // 1. 并发快速预筛备选池（带 expected=204 过滤假活节点）
                    bool unreachable = NodeManager.BatchMeasureFailoverPool(pool, Math.Max(2000, Math.Min(Settings.DelayTimeoutMs, 4000)),
                        () => _shuttingDown || !UiAlive, cfg);
                    if (unreachable)
                        return new { Ok = false, Unreachable = true, OrigRecovered = false, OrigDelay = -1, BestDelay = -1, Sw = (NodeManager.SwitchOutcome)null, Err = "与 Clash 控制接口的连接已断开" };

                    // 2. 切前先复检原节点：若这几秒内原节点已恢复正常可用，直接留在原节点不断开现有连接
                    var origNow = Profile.MeasureDelayConfirmed(timedOutNode, null, Math.Max(2500, Math.Min(Settings.DelayTimeoutMs, 4500)), cfg);
                    if (origNow.KernelUnreachable)
                        return new { Ok = false, Unreachable = true, OrigRecovered = false, OrigDelay = -1, BestDelay = -1, Sw = (NodeManager.SwitchOutcome)null, Err = origNow.Error };
                    if (origNow.Ok && origNow.DelayMs > 0)
                        return new { Ok = false, Unreachable = false, OrigRecovered = true, OrigDelay = origNow.DelayMs, BestDelay = -1, Sw = (NodeManager.SwitchOutcome)null, Err = "" };

                    var ranked = NodeManager.RankFailoverCandidates(pool, timedOutNode, Settings.DesiredRegion);
                    if (ranked.Count == 0)
                        return new { Ok = false, Unreachable = false, OrigRecovered = false, OrigDelay = -1, BestDelay = -1, Sw = (NodeManager.SwitchOutcome)null, Err = "备选池内 " + pool.Count + " 个节点均超时" };

                    // 3. 对低延迟候选依次做「切前二次确认 -> 切换 -> 切后实地延迟复核」，拒绝盲切到假活/超时节点
                    string lastErr = "备选节点切后实地复核均超时";
                    string origSelection = cat.CurrentSelection;
                    string currentOut = timedOutNode;
                    int maxTry = Math.Min(4, ranked.Count);
                    for (int i = 0; i < maxTry; i++)
                    {
                        if (_shuttingDown || !UiAlive) break;
                        var cand = ranked[i];
                        var preConfirm = Profile.MeasureDelay(cand.Name, null, Math.Max(2500, Math.Min(Settings.DelayTimeoutMs, 4000)), cfg);
                        if (preConfirm.KernelUnreachable)
                            return new { Ok = false, Unreachable = true, OrigRecovered = false, OrigDelay = -1, BestDelay = -1, Sw = (NodeManager.SwitchOutcome)null, Err = preConfirm.Error };
                        if (!preConfirm.Ok || preConfirm.DelayMs <= 0)
                        {
                            cand.LiveDelayMs = -2;
                            cand.Alive = false;
                            continue;
                        }

                        var sw = NodeManager.SwitchProxyNode(cat.GroupName, cand.Name, true, currentOut, cfg);
                        if (!sw.Ok) { lastErr = sw.Message; continue; }
                        currentOut = sw.NewOutbound;

                        var postVerify = Profile.MeasureDelayConfirmed(sw.NewOutbound, null, Math.Max(3000, Settings.DelayTimeoutMs), cfg);
                        if (postVerify.Ok && postVerify.DelayMs > 0)
                            return new { Ok = true, Unreachable = false, OrigRecovered = false, OrigDelay = -1, BestDelay = postVerify.DelayMs, Sw = sw, Err = "" };

                        lastErr = CleanEmoji(cand.Name) + " 切换后实测仍超时（已尝试后续备选）";
                    }

                    // 全部试用备选切换后均超时：还原先前选择，避免留在刚测出超时的节点上
                    if (!string.IsNullOrEmpty(origSelection) && !string.Equals(currentOut, timedOutNode, StringComparison.OrdinalIgnoreCase))
                    {
                        try { NodeManager.SwitchProxyNode(cat.GroupName, origSelection, false, "", cfg); } catch { }
                    }
                    return new { Ok = false, Unreachable = false, OrigRecovered = false, OrigDelay = -1, BestDelay = -1, Sw = (NodeManager.SwitchOutcome)null, Err = lastErr };
                });

                if (!UiAlive) return;
                if (result.OrigRecovered)
                {
                    _delayNode = timedOutNode;
                    _delayMs = result.OrigDelay;
                    _delayTimedOut = false;
                    _delayTimeoutStreak = 0;
                    _delayAlertActive = false;
                    _state.DelayMs = result.OrigDelay;
                    _state.NodeAlive = true;
                    _lastDelayTestAt = DateTime.UtcNow;
                    _nodesPage.UpdateSingleNodeDelay(timedOutNode, result.OrigDelay, true, "");
                    AuditLog.Record("FAILOVER", "复检原节点已恢复可用，免切换保留原连接：" + timedOutNode + "（" + result.OrigDelay + "ms）");
                    string keepMsg = "原节点「" + CleanEmoji(timedOutNode) + "」已恢复（" + result.OrigDelay + "ms），无需切换";
                    _nodesPage.SetLastFailoverSummary("最近复检 " + timeTag + "：原节点已恢复正常");
                    SetStatus(keepMsg, Theme.Ok);
                    return;
                }

                if (result.Ok && result.Sw != null && result.Sw.Ok)
                {
                    _state.Outbound = result.Sw.NewOutbound;
                    _delayNode = result.Sw.NewOutbound;
                    _delayMs = result.BestDelay;
                    _delayTimedOut = false;
                    _delayTimeoutStreak = 0;
                    _delayAlertActive = false;
                    _state.DelayMs = result.BestDelay;
                    _state.NodeAlive = true;
                    _lastDelayTestAt = DateTime.UtcNow;

                    AuditLog.Record("FAILOVER",
                        "当前节点超时（" + timedOutNode + "），自动切至已验证备选节点：" +
                        result.Sw.NewOutbound + "（切后实测 " + result.BestDelay + "ms · 清理连接 " + result.Sw.ClosedConnections + " 条）");
                    string msg = BuildFailoverNotifyText(CleanEmoji(timedOutNode), CleanEmoji(result.Sw.NewOutbound), result.BestDelay);
                    _nodesPage.SetCurrentOutbound(result.Sw.NewOutbound, result.BestDelay, false, _state.ProxyActive);
                    _nodesPage.SetLastFailoverSummary("最近自动切换 " + timeTag + "：" + msg + "（内核即时生效，若重启客户端重置将再次自动优选）");
                    _qualityPage.SetCurrentOutbound(result.Sw.NewOutbound);
                    SetStatus(msg, Theme.Ok);
                    Toast("已自动切换节点", msg + "\n本次运行内核生效（若重启客户端回退将重新优选）", Theme.Ok, 2);
                    // 无论窗口在后台还是打开着，都主动弹系统托盘气泡通知用户，以免错过（用户要求）
                    if (TrayBalloon != null)
                        TrayBalloon("ProxyPicker 已自动切换节点", msg +
                            (result.Sw.ClosedConnections > 0 ? ("\n已断开 " + result.Sw.ClosedConnections + " 条旧连接") : "\n切后实测可用") +
                            "（内核生效，重启客户端后如回退将再次自动切换）");
                    if (_nodesLoadedOnce) _ = LoadNodesCatalogAsync();
                }
                else
                {
                    AuditLog.Record("FAILOVER", "自动切换未成功：" + result.Err);
                    _nodesPage.SetLastFailoverSummary("最近切换未成功 " + timeTag + "：" + result.Err);
                    SetStatus((result.Unreachable ? "控制接口已断开 · " : "节点超时 · ") + result.Err, Theme.Warn);
                    Toast(result.Unreachable ? "内核连接已断开" : "备选节点均未通过实测", result.Err, Theme.Warn, 2);
                    if (TrayBalloon != null)
                        TrayBalloon(result.Unreachable ? "控制接口已断开" : "备选节点均不可用", result.Err);
                    if (_nodesLoadedOnce) _ = LoadNodesCatalogAsync();
                }
            }
            catch (Exception ex) { ReportFailure("自动切换备用节点", ex); }
            finally
            {
                _failoverBusy = false;
                if (UiAlive)
                {
                    InvalidateNavBar();
                    if (_page == 0) Invalidate(new Rectangle(0, 0, Width, Theme.Px(SummaryTop + SummaryH + 4)));
                }
            }
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
                if (_pendingRefresh)
                {
                    _pendingRefresh = false;
                    BeginInvoke(new Action(RefreshAsync));
                }
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

        /// <summary>
        /// 走势图保留的点数。少留一点，格子就宽一点：一秒挪的距离更看得出来，
        /// 42 点的时候一步只有 4.7 像素，看上去跟静止没区别。
        /// </summary>
        const int SparkPoints = 30;

        static void Push(List<float> list, float v)
        {
            list.Add(v);
            while (list.Count > SparkPoints) list.RemoveAt(0);
        }

        /// <summary>
        /// 把旧历史整体平移到「以 now 为终点」。收起面板一段时间再打开时用：
        /// 曲线接着上一段继续走，而不是清空成一格空白。
        /// </summary>
        void RebaseTrail(long now)
        {
            if (_sparkAt.Count == 0) return;
            long shift = now - SparkPeriodMs - _sparkAt[_sparkAt.Count - 1];
            if (shift <= 0) return;
            for (int i = 0; i < _sparkAt.Count; i++) _sparkAt[i] += shift;
            _sparkAxis = _sparkAt[_sparkAt.Count - 1] + SparkPeriodMs;
        }

        void SetStatus(string text, Color color)
        {
            _status = text ?? ""; _statusColor = color; Invalidate();
        }

        void InvalidateActiveToastSurface()
        {
            if (!UiAlive) return;
            if (_page == 0) _list.Invalidate();
            else if (_page == 1) _nodesPage.Invalidate();
            else _qualityPage.Invalidate();
        }

        void InvalidateNavBar()
        {
            if (UiAlive) Invalidate(new Rectangle(0, Theme.Px(NavTop - 2), Width, Theme.Px(NavH + 4)));
        }

        void Toast(string title, string sub, Color accent, int kind)
        {
            _toasts.Show(title, sub, accent, kind);
            Wake();
            InvalidateActiveToastSurface();
        }

        // ---------------------------------------------------------------- 布局

        float LogicalW { get { return ClientSize.Width > 0 ? ClientSize.Width / Theme.S : DefaultWinW; } }
        float LogicalH { get { return ClientSize.Height > 0 ? ClientSize.Height / Theme.S : DefaultWinH; } }
        /// <summary>兼容既有 7 参数布局检查（不含独立固定 IP 增高行）。</summary>
        internal static void ComputeSummaryLayoutBounds(
            float logicalW,
            out bool dualColumn,
            out float divX,
            out RectangleF sparkRect,
            out RectangleF miniRatePillRect,
            out float leftRow1Right,
            out float leftRow2Right)
        {
            ComputeSummaryLayoutBounds(logicalW, false, out dualColumn, out divX, out sparkRect, out miniRatePillRect, out leftRow1Right, out leftRow2Right);
        }

        /// <summary>计算摘要卡在不同窗口逻辑宽度与是否开启独立固定 IP 下的对称双栏 / 极窄紧凑折线图无碰撞布局。</summary>
        internal static void ComputeSummaryLayoutBounds(
            float logicalW,
            bool showStatic,
            out bool dualColumn,
            out float divX,
            out RectangleF sparkRect,
            out RectangleF miniRatePillRect,
            out float leftRow1Right,
            out float leftRow2Right)
        {
            float cardRight = logicalW - 24f;
            dualColumn = logicalW >= 520f;
            if (dualColumn)
            {
                float rightW = logicalW < 640f
                    ? Math.Max(196f, Math.Min(230f, logicalW * 0.37f))
                    : Math.Max(226f, Math.Min(340f, logicalW * 0.36f));
                divX = cardRight - rightW - 14f;
                float rx = divX + 12f;
                float rw = Math.Max(180f, cardRight - 14f - rx);
                sparkRect = showStatic
                    ? new RectangleF(rx, SummaryTop + 55f, rw, 28f)
                    : new RectangleF(rx, SummaryTop + 42f, rw, 24f);
                miniRatePillRect = RectangleF.Empty;
                leftRow1Right = divX - 10f;
                leftRow2Right = divX - 10f;
            }
            else
            {
                divX = 0f;
                const float miniW = 122f;
                float rx = cardRight - 12f - miniW;
                if (showStatic)
                {
                    miniRatePillRect = new RectangleF(rx, SummaryTop + 6f, miniW, 33f);
                    sparkRect = new RectangleF(rx, SummaryTop + 44f, miniW, 40f);
                }
                else
                {
                    miniRatePillRect = new RectangleF(rx, SummaryTop + 8f, miniW, 20f);
                    sparkRect = new RectangleF(rx, SummaryTop + 34f, miniW, 29f);
                }
                leftRow1Right = rx - 8f;
                leftRow2Right = rx - 8f;
            }
        }

        float SummaryDivX
        {
            get
            {
                bool dual;
                float divX, l1, l2;
                RectangleF spark, mini;
                ComputeSummaryLayoutBounds(LogicalW, ShowStaticRow, out dual, out divX, out spark, out mini, out l1, out l2);
                return divX;
            }
        }

        RectangleF NavBarRect { get { return new RectangleF(24, NavTop, LogicalW - 48, NavH); } }

        internal static RectangleF NavSegmentRect(float navX, float navY, float navW, float navH, int idx)
        {
            const float pad = 3f;
            const float gap = 4f;
            float itemW = (navW - pad * 2f - gap * 2f) / 3f;
            return new RectangleF(navX + pad + idx * (itemW + gap), navY + pad, itemW, navH - pad * 2f);
        }

        RectangleF NavSegRect(int idx)
        {
            var r = NavBarRect;
            return NavSegmentRect(r.X, r.Y, r.Width, r.Height, idx);
        }

        RectangleF SummaryRect { get { return new RectangleF(24, SummaryTop, LogicalW - 48, SummaryH); } }

        /// <summary>计算摘要卡左侧各信息行的精确布局矩形（开启固定 IP 时左侧 3 行紧凑排布，与右侧三通道速率+曲线底边平齐）。</summary>
        internal static void ComputeSummaryRowBounds(
            float logicalW,
            bool showStatic,
            out RectangleF row1Rect,
            out RectangleF row2Rect,
            out RectangleF row3Rect)
        {
            bool dual;
            float divX, l1, l2;
            RectangleF spark, mini;
            ComputeSummaryLayoutBounds(logicalW, showStatic, out dual, out divX, out spark, out mini, out l1, out l2);
            float w1 = Math.Max(120f, l1 - 38f);
            float w2 = Math.Max(120f, l2 - 38f);
            if (showStatic)
            {
                row1Rect = new RectangleF(38f, SummaryTop + 6f, w1, 22f);
                row2Rect = new RectangleF(38f, SummaryTop + 34f, w2, 21f);
                row3Rect = new RectangleF(38f, SummaryTop + 62f, w2, 21f);
            }
            else
            {
                row1Rect = new RectangleF(38f, SummaryTop + 9f, w1, 24f);
                row2Rect = new RectangleF(38f, SummaryTop + 42f, w2, 21f);
                row3Rect = RectangleF.Empty;
            }
        }

        /// <summary>生成首页独立固定 IP 实时延迟胶囊的三段文本与语义色调，配合 FitPillLabel 保证毫秒尾缀永不被截断。</summary>
        internal static void FormatStaticDelayPillParts(
            StaticProxy.StaticProxyConfig cfg,
            string liveHop,
            int delayMs,
            bool timedOut,
            bool checking,
            bool optimizing,
            bool configOk,
            bool proxyActive,
            out string prefix,
            out string mid,
            out string suffix,
            out string tone)
        {
            prefix = "固定IP ";
            string mode = cfg != null ? (cfg.HopMode ?? StaticProxy.HopFollow) : StaticProxy.HopFollow;
            string hopName = "";
            if (!string.IsNullOrWhiteSpace(liveHop) && !StaticProxy.IsManagedName(liveHop))
                hopName = liveHop.Trim();
            else if (cfg != null && string.Equals(mode, StaticProxy.HopAuto, StringComparison.OrdinalIgnoreCase))
                hopName = (cfg.AutoBestHopNode ?? "").Trim();
            else if (cfg != null && string.Equals(mode, StaticProxy.HopPin, StringComparison.OrdinalIgnoreCase))
                hopName = (cfg.PinnedHopNode ?? "").Trim();
            string cleanHop = string.IsNullOrEmpty(hopName) ? "" : CleanEmoji(hopName);

            if (string.Equals(mode, StaticProxy.HopAuto, StringComparison.OrdinalIgnoreCase))
                mid = string.IsNullOrEmpty(cleanHop) ? "智能优选" : ("优选:" + cleanHop);
            else if (string.Equals(mode, StaticProxy.HopPin, StringComparison.OrdinalIgnoreCase))
                mid = string.IsNullOrEmpty(cleanHop) ? "锁定跳板" : ("跳板:" + cleanHop);
            else if (string.Equals(mode, StaticProxy.HopDirect, StringComparison.OrdinalIgnoreCase))
                mid = "直连";
            else
                mid = "跟随主节点";

            if (!configOk)
            {
                suffix = " · 未连接";
                tone = "dim";
            }
            else if (!proxyActive)
            {
                suffix = " · 待连接";
                tone = "dim";
            }
            else if (optimizing)
            {
                suffix = " · 稳速刷新中…";
                tone = "accent";
            }
            else if (checking)
            {
                suffix = " · 测速中…";
                tone = "ok";
            }
            else if (timedOut)
            {
                suffix = " · 超时";
                tone = "danger";
            }
            else if (delayMs > 0)
            {
                suffix = " · " + delayMs + "ms";
                int baseMs = cfg != null ? cfg.AutoBestDelayMs : -1;
                tone = (delayMs >= 450 || StaticProxy.IsStaticDelayDegraded(delayMs, baseMs)) ? "warn" : "ok";
            }
            else
            {
                suffix = " · 点此测速";
                tone = "ok";
            }
        }

        /// <summary>摘要卡右侧第一行提示。有分流异常时整块可点击，打开完整明细。</summary>
        RectangleF MismatchRect
        {
            get
            {
                if (!_mismatchRect.IsEmpty) return _mismatchRect;
                bool dual;
                float divX, l1, l2;
                RectangleF spark, mini;
                bool showStatic = ShowStaticRow;
                ComputeSummaryLayoutBounds(LogicalW, showStatic, out dual, out divX, out spark, out mini, out l1, out l2);
                float x = LogicalW < 660f ? 174f : 186f;
                float w = Math.Max(0f, l1 - x);
                return new RectangleF(x, SummaryTop + (showStatic ? 10f : 13f), w, 18);
            }
        }
        RectangleF SparkRect
        {
            get
            {
                bool dual;
                float divX, l1, l2;
                RectangleF spark, mini;
                ComputeSummaryLayoutBounds(LogicalW, ShowStaticRow, out dual, out divX, out spark, out mini, out l1, out l2);
                return spark;
            }
        }

        internal static void ComputeAppsToolbarBounds(
            float logicalW,
            out RectangleF searchRect,
            out RectangleF[] tabRects,
            out RectangleF sortRect,
            out bool twoRow)
        {
            ComputeAppsToolbarBounds(logicalW, false, out searchRect, out tabRects, out sortRect, out twoRow);
        }

        /// <summary>计算第 1 页工具栏（搜索框、4 枚筛选标签、排序按钮）在不同窗口宽度下的无碰撞布局矩形。</summary>
        internal static void ComputeAppsToolbarBounds(
            float logicalW,
            bool showStatic,
            out RectangleF searchRect,
            out RectangleF[] tabRects,
            out RectangleF sortRect,
            out bool twoRow)
        {
            twoRow = logicalW < 640f;
            float searchTop = SearchTopFor(showStatic);
            float tabsTop = TabsTopFor(twoRow, showStatic);

            float sortY = twoRow ? searchTop + 3f : tabsTop;
            sortRect = new RectangleF(logicalW - 24f - 76f, sortY, 76f, 28f);

            float searchW = twoRow
                ? Math.Max(160f, logicalW - 48f - 84f)
                : Math.Min(300f, Math.Max(148f, logicalW - 446f));
            searchRect = new RectangleF(24f, searchTop, searchW, SearchH);

            float startX = twoRow ? 24f : (searchRect.Right + 10f);
            float rightBound = twoRow ? (logicalW - 24f) : (sortRect.X - 8f);
            float avail = Math.Max(220f, rightBound - startX);
            float gap = twoRow ? 6f : 4f;
            float tabW = twoRow
                ? ((avail - gap * 3f) / 4f)
                : Math.Min(94f, Math.Max(54f, (avail - gap * 3f) / 4f));

            tabRects = new RectangleF[4];
            for (int i = 0; i < 4; i++)
                tabRects[i] = new RectangleF(startX + i * (tabW + gap), tabsTop, tabW, 28f);
        }

        RectangleF SearchRect
        {
            get
            {
                RectangleF s, sort;
                RectangleF[] tabs;
                bool twoRow;
                ComputeAppsToolbarBounds(LogicalW, ShowStaticRow, out s, out tabs, out sort, out twoRow);
                return s;
            }
        }
        RectangleF SaveRect
        {
            get
            {
                float w = NarrowShell ? 112f : 126f;
                return new RectangleF(LogicalW - 24 - w, LogicalH - 42, w, 30);
            }
        }
        RectangleF DirectRect
        {
            get
            {
                float w = NarrowShell ? 76f : 88f;
                return new RectangleF(SaveRect.X - 8f - w, LogicalH - 42, w, 30);
            }
        }
        RectangleF CloseRect { get { return new RectangleF(LogicalW - 50, 10, 26, 26); } }
        RectangleF MaxRect { get { return new RectangleF(LogicalW - 82, 10, 26, 26); } }
        RectangleF MinRect { get { return new RectangleF(LogicalW - 114, 10, 26, 26); } }
        RectangleF RefreshRect { get { return new RectangleF(LogicalW - 146, 10, 26, 26); } }
        RectangleF HelpRect { get { return new RectangleF(LogicalW - 178, 10, 26, 26); } }
        RectangleF SortRect
        {
            get
            {
                RectangleF s, sort;
                RectangleF[] tabs;
                bool twoRow;
                ComputeAppsToolbarBounds(LogicalW, ShowStaticRow, out s, out tabs, out sort, out twoRow);
                return sort;
            }
        }
        RectangleF HintRect { get { return new RectangleF(24, LogicalH - 36, Math.Max(120f, DirectRect.X - 32f), 22); } }
        RectangleF TabRect(int i)
        {
            RectangleF s, sort;
            RectangleF[] tabs;
            bool twoRow;
            ComputeAppsToolbarBounds(LogicalW, ShowStaticRow, out s, out tabs, out sort, out twoRow);
            return (i >= 0 && i < 4) ? tabs[i] : RectangleF.Empty;
        }

        RectangleF HelpCard
        {
            get
            {
                float hw = Math.Min(760f, LogicalW - 36f);
                float hh = Math.Min(500f, LogicalH - 48f);
                return new RectangleF((LogicalW - hw) / 2f, (LogicalH - hh) / 2f, hw, hh);
            }
        }
        RectangleF HelpCloseRect { get { return new RectangleF(HelpCard.Right - 34, HelpCard.Y + 12, 24, 24); } }
        RectangleF HelpFooterBtnRect(int idx)
        {
            var card = HelpCard;
            float gap = 8f;
            if (LogicalW < 580f)
            {
                float colW = (card.Width - 40f - gap) / 2f;
                int c = idx % 2;
                int r = idx / 2;
                return new RectangleF(card.X + 20f + c * (colW + gap), card.Bottom - 78f + r * 35f, colW, 29f);
            }
            float avail = card.Width - 40f - gap * 3f;
            float w0 = avail * 0.19f, w1 = avail * 0.19f, w2 = avail * 0.34f, w3 = avail - w0 - w1 - w2;
            float[] ws = { w0, w1, w2, w3 };
            float x = card.X + 20f;
            for (int k = 0; k < idx; k++) x += ws[k] + gap;
            return new RectangleF(x, card.Bottom - 46f, ws[idx], 30f);
        }
        RectangleF HelpDiagRect { get { return HelpFooterBtnRect(0); } }
        RectangleF HelpUnhideRect { get { return HelpFooterBtnRect(1); } }
        RectangleF HelpModeRect { get { return HelpFooterBtnRect(2); } }
        RectangleF HelpIpv6Rect { get { return HelpFooterBtnRect(3); } }
        // 版本号挂在帮助卡片右上角、客户端名字下面一行，不会与标题按钮重合
        RectangleF HelpUpdateRect { get { return new RectangleF(HelpCard.Right - 226, HelpCard.Y + 34, 184, 16); } }

        void LayoutChildren()
        {
            int listTopPx = Theme.Px(ListTop);
            _list.SetBounds(Theme.Px(16), listTopPx, Math.Max(0, ClientSize.Width - Theme.Px(32)),
                Math.Max(0, ClientSize.Height - listTopPx - Theme.Px(FooterH)));

            var sr = SearchRect;
            _search.SetBounds(Theme.Px(sr.X + 32f), Theme.Px(sr.Y + 8f),
                Math.Max(60, Theme.Px(sr.Width - 42f)), Theme.Px(18));

            int subTopPx = Theme.Px(SummaryTop);
            int subH = Math.Max(0, ClientSize.Height - subTopPx - Theme.Px(FooterSlimH));
            int subW = Math.Max(0, ClientSize.Width - Theme.Px(48));
            _nodesPage.SetBounds(Theme.Px(24), subTopPx, subW, subH);
            _qualityPage.SetBounds(Theme.Px(24), subTopPx, subW, subH);
        }

        // ---------------------------------------------------------------- 绘制

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            float lw = LogicalW, lh = LogicalH;
            Draw.Hint(g); g.Clear(Theme.Bg); g.ScaleTransform(Theme.S, Theme.S);
            Theme.Fill(g, Theme.Bg, new RectangleF(0, 0, lw, lh), 0f);

            using (var b = new LinearGradientBrush(
                new RectangleF(0, 0, lw, TitleH + 6),
                Theme.Dark ? Theme.FromHex("#1A1E25") : Theme.FromHex("#FFFFFF"), Theme.Bg, 90f))
                g.FillRectangle(b, 0, 0, lw, TitleH + 6);

            if (_titleIcon != null) g.DrawImage(_titleIcon, 24f, 11f, 24f, 24f);
            float titleAvail = Math.Max(140f, HelpRect.X - 64f);
            Draw.Text(g, "ProxyPicker", _fTitle, Theme.Fg, new RectangleF(56, 7, titleAvail, 22));
            Draw.Text(g, Subtitle(), _fSub, Theme.Dim, new RectangleF(56, 27, titleAvail, 16));

            DrawChromeButton(g, HelpRect, Btn.Help, "?");
            DrawRefresh(g);
            DrawMin(g);
            DrawMax(g);
            DrawClose(g);

            DrawNavBar(g);

            if (_spinning)
            {
                // 顶部一条流动细光，表示「在干活」
                float w = 90f;
                float x = (_spin / 360f) * (lw + w) - w;
                using (var b = new LinearGradientBrush(new RectangleF(x, TitleH - 2, w, 2),
                    Color.FromArgb(0, Theme.Accent), Theme.Accent, 0f))
                {
                    b.WrapMode = WrapMode.TileFlipXY;
                    g.FillRectangle(b, x, TitleH - 2, w, 2);
                }
            }

            if (_page == 1 || _page == 2)
            {
                using (var pen = new Pen(Theme.Line)) g.DrawLine(pen, 0, lh - FooterSlimH, lw, lh - FooterSlimH);
                float tipW = NarrowShell ? Math.Min(178f, lw * 0.42f) : Math.Min(300f, lw * 0.44f);
                float statusW = Math.Max(120f, lw - 48f - tipW - 10f);
                Draw.Text(g, _status, _fHint, _statusColor == Color.Empty ? Theme.Dim : _statusColor,
                    new RectangleF(24, lh - 26, statusW, 19));
                string pageTip = NarrowShell
                    ? "Esc 返回 · Ctrl+1/2/3"
                    : (_page == 1 ? "备选池与策略即时生效 · Esc 返回 · Ctrl+1/2/3" : "旁路隔离引擎 · Esc 返回上一视图 · Ctrl+1/2/3");
                Draw.Text(g, pageTip, _fTiny, Theme.Dim,
                    new RectangleF(lw - 24 - tipW, lh - 25, tipW, 18), Draw.Right);
                if (_help) DrawHelp(g);
                return;
            }

            DrawSummary(g);

            // 搜索框：流式响应宽度
            var sr = SearchRect;
            Theme.Fill(g, Theme.Card, sr, 9f);
            bool focus = _search.Focused;
            Theme.Stroke(g, focus ? Theme.Accent : (Theme.Dark ? Theme.Line : Theme.FromHex("#DCE0E6")),
                sr, 9f, focus ? 1.4f : 1f);
            Magnifier(g, sr.X + 18, sr.Y + SearchH / 2f, 4.8f, focus ? Theme.Accent : Theme.Dim);

            for (int i = 0; i < 4; i++)
            {
                var r = TabRect(i);
                bool active = _tab == i;
                if (!active && _hoverTab == i) Theme.Fill(g, Theme.Hover, r, 7f);
                if (active) Theme.Fill(g, Theme.AccentSoft, r, 7f);
                Draw.Text(g, TabText(i), active ? _fTabActive : _fTab, active ? Theme.Accent : Theme.Dim, r, Draw.Center);
            }

            DrawSortButton(g);

            using (var pen = new Pen(Theme.Line)) g.DrawLine(pen, 0, lh - FooterH, lw, lh - FooterH);
            Draw.Text(g, _status, _fHint, _statusColor == Color.Empty ? Theme.Dim : _statusColor,
                new RectangleF(24, lh - 61, lw - 48, 18));
            Draw.Text(g, ClientTag(), _fTiny, Theme.Dim, HintRect);

            DrawSaveButton(g);

            if (_help) DrawHelp(g);
        }

        void DrawNavBar(Graphics g)
        {
            var bar = NavBarRect;
            Color track = Theme.Dark ? Theme.FromHex("#1A1F26") : Theme.FromHex("#ECEFF4");
            Theme.Fill(g, track, bar, 11f);
            Theme.Stroke(g, Theme.Line, bar, 11f);

            bool compactNav = LogicalW < 580f;
            int selApps = _all.Count(r => r.Proxy);
            string tag0 = compactNav
                ? (selApps > 0 ? ("1. 分流 · " + selApps) : "1. 分流")
                : (selApps > 0 ? ("1. 应用分流 · " + selApps + " 已选") : "1. 应用分流");

            string delayInfo = "";
            bool delayWarn = false;
            if (!_state.ConfigOk)
            {
                delayInfo = compactNav ? " · 未连" : " · 未连接";
            }
            else if (!_state.ProxyActive)
            {
                delayInfo = compactNav ? " · 未开代理" : " · 代理未开";
            }
            else
            {
                delayWarn = _delayTimedOut || (_state.NodeAlive == false && _delayMs <= 0);
                if (delayWarn) delayInfo = " · 超时";
                else if (_delayMs > 0) delayInfo = " · " + _delayMs + "ms";
                else if (_state.DelayMs > 0) delayInfo = " · " + _state.DelayMs + "ms";
            }
            string tag1 = (compactNav ? "2. 节点" : "2. 节点策略") + delayInfo;

            string tag2 = compactNav ? "3. 体检" : "3. 链路体检";
            bool hasRep = _lastQualityReport != null;
            Color repDot = Color.Empty;
            if (hasRep)
            {
                if (_lastQualityReport.RoutedDirect || _lastQualityReport.Level == NodeQuality.RiskLevel.High)
                {
                    if (!compactNav) tag2 += " · 高风险";
                    repDot = Theme.Danger;
                }
                else if (_lastQualityReport.Level == NodeQuality.RiskLevel.Medium || !_lastQualityReport.RegionMatched)
                {
                    if (!compactNav) tag2 += " · 注意";
                    repDot = Theme.Warn;
                }
                else
                {
                    tag2 += compactNav
                        ? (" · " + _lastQualityReport.MainstreamReadyCount + "/4")
                        : (" · 就绪 " + _lastQualityReport.MainstreamReadyCount + "/4");
                    repDot = Theme.Ok;
                }
            }

            string[] labels = { tag0, tag1, tag2 };
            Btn[] btns = { Btn.Nav0, Btn.Nav1, Btn.Nav2 };

            for (int i = 0; i < 3; i++)
            {
                var seg = NavSegRect(i);
                bool active = _page == i;
                bool hover = _hover == btns[i];
                if (active)
                {
                    Draw.Shadow(g, seg, 8f, Theme.Dark ? 36 : 16);
                    Theme.Fill(g, Theme.Card, seg, 8f);
                    Theme.Stroke(g, Theme.AccentSoft, seg, 8f);
                }
                else if (hover)
                {
                    Theme.Fill(g, Theme.Hover, seg, 8f);
                }

                Color fg = active ? Theme.Accent : Theme.Dim;
                if (i == 1 && delayWarn && !active) fg = Theme.Warn;
                Draw.Text(g, labels[i], active ? _fTabActive : _fTab, fg, seg, Draw.Center);

                if ((i == 1 && delayWarn) || (i == 2 && !repDot.IsEmpty))
                {
                    Color dotCol = i == 1 ? Theme.Warn : repDot;
                    using (var br = new SolidBrush(dotCol))
                        g.FillEllipse(br, seg.Right - 14f, seg.Y + 6f, 6f, 6f);
                }
            }
        }

        string Subtitle()
        {
            // 有新版本时先说这件事：平时那句「谁在管分流」在帮助面板里随时能看
            if (_update != null && _update.Ok && _update.Newer)
                return "有新版本 v" + _update.Latest + " · 点右上角「?」";
            // _state 已在后台 RefreshAsync / CheckOutbound / WatchAsync 里填好
            if (!_state.ConfigOk)
                return !string.IsNullOrEmpty(_state.Client)
                    ? (_state.Client + " · 未连接（内核未启动）")
                    : "未检测到运行中的代理客户端";
            if (!_state.ProxyActive)
                return (!string.IsNullOrEmpty(_state.Client) ? _state.Client : "代理内核") +
                    (NarrowShell ? " · 代理未开启（TUN/系统关）" : " · 代理未开启（TUN 与系统代理均关）");
            if (!string.IsNullOrEmpty(_state.Client)) return _state.Client + " · 按软件分流";
            return "只让需要的软件走代理";
        }

        string ClientTag()
        {
            if (NarrowShell)
            {
                if (!string.IsNullOrEmpty(_state.Client)) return _state.Client + " · 保存即生效";
                return "保存后无需重启浏览器";
            }
            if (!string.IsNullOrEmpty(_state.Client)) return _state.Client + " · 保存后无需重启浏览器";
            return "保存后无需重启浏览器";
        }

        /// <summary>生成摘要卡左下角内核底座与模式状态（例如「TUN · 规则」）。</summary>
        internal static string FormatKernelMeta(bool tun, string mode, out bool isWarn)
        {
            string m = (mode ?? "").Trim().ToLowerInvariant();
            string modeLabel;
            bool modeWarn = false;
            if (m.Length == 0 || m == "rule") modeLabel = "规则";
            else if (m == "global") { modeLabel = "全局"; modeWarn = true; }
            else if (m == "direct") { modeLabel = "直连"; modeWarn = true; }
            else { modeLabel = mode.Trim(); modeWarn = true; }

            isWarn = !tun || modeWarn;
            return (tun ? "TUN · " : "系统代理 · ") + modeLabel;
        }

        /// <summary>按实际像素宽度裁剪节点名称，保证前缀（「节点 」）与延迟后缀（「 · 203ms」）不被截断。</summary>
        internal static string FitPillLabel(string prefix, string nodeName, string suffix,
            float maxTextWidth, Func<string, float> measureWidth)
        {
            prefix = prefix ?? "";
            nodeName = nodeName ?? "";
            suffix = suffix ?? "";
            string full = prefix + nodeName + suffix;
            if (measureWidth == null || measureWidth(full) <= maxTextWidth) return full;

            for (int keep = nodeName.Length - 1; keep >= 1; keep--)
            {
                string candidate = prefix + nodeName.Substring(0, keep) + "…" + suffix;
                if (measureWidth(candidate) <= maxTextWidth) return candidate;
            }
            return prefix + "…" + suffix;
        }

        void DrawRateRow(Graphics g, float rx, float rw, float y, string label, bool ok,
            double down, double up, Color valColor, bool showDot = false)
        {
            bool tight = rw < 214f;
            float dotOff = showDot ? 8f : 0f;
            float textW = tight ? (label.IndexOf('≈') >= 0 ? 31f : 27f) : (label.Length >= 4 ? 38f : 34f);
            float labelW = dotOff + textW;
            float rateX = rx + labelW, rateW = rw - labelW;
            float downW = tight ? rateW * 0.50f : Math.Min(86f, rateW * 0.52f), upW = rateW - downW;
            if (showDot)
            {
                using (var db = new SolidBrush(Color.FromArgb(215, valColor)))
                    g.FillEllipse(db, rx, y + 6f, 4.5f, 4.5f);
            }
            Draw.Text(g, label, _fTiny, Theme.Dim, new RectangleF(rx + dotOff, y + 1, textW, 15));
            if (!ok)
            {
                Draw.Text(g, "—", _fRate, valColor, new RectangleF(rateX, y, rateW, 16), Draw.Right);
                return;
            }
            string dPrefix = tight ? "↓" : "↓ ";
            string uPrefix = tight ? "↑" : "↑ ";
            Draw.Text(g, dPrefix + Traffic.Speed(down), _fRate, valColor, new RectangleF(rateX, y, downW, 16), Draw.Right);
            Draw.Text(g, uPrefix + Traffic.Speed(up), _fRate, valColor, new RectangleF(rateX + downW, y, upW, 16), Draw.Right);
        }

        void DrawSummary(Graphics g)
        {
            var card = SummaryRect;
            float lw = LogicalW;
            bool compactCopy = lw < 660f;
            bool showStatic = ShowStaticRow;
            bool dualColumn;
            float divX, leftRow1Right, leftRow2Right;
            RectangleF sparkRect, miniRatePillRect;
            ComputeSummaryLayoutBounds(lw, showStatic, out dualColumn, out divX, out sparkRect, out miniRatePillRect, out leftRow1Right, out leftRow2Right);
            RectangleF row1Bounds, row2Bounds, row3Bounds;
            ComputeSummaryRowBounds(lw, showStatic, out row1Bounds, out row2Bounds, out row3Bounds);

            Draw.Shadow(g, card, 10f, Theme.Dark ? 60 : 22);
            Theme.Fill(g, Theme.Card, card, 10f);
            Theme.Stroke(g, Theme.Line, card, 10f);

            int selected = _all.Count(r => r.Proxy);
            string countText = selected + (Settings.PatchOnly ? " 个软件强制代理" : " 个软件走代理");
            float countMeasured = g.MeasureString(countText, _fSummary).Width;
            float countBoxW = Math.Min(compactCopy ? 154f : 168f, (float)Math.Ceiling(countMeasured) + 4f);
            Draw.Text(g, countText, _fSummary, selected > 0 ? Theme.Accent : Theme.Fg,
                new RectangleF(row1Bounds.X, row1Bounds.Y, countBoxW, row1Bounds.Height));

            string rest = Settings.PatchOnly ? "按订阅规则" : "其余直连";
            bool modeKnown = _state.ConfigOk && !string.IsNullOrEmpty(_state.Mode) && _state.Mode != "?";
            bool nonRuleMode = modeKnown && !Profile.IsRuleMode(_state.Mode);
            bool notProxyActive = _state.ConfigOk && !nonRuleMode && !_state.ProxyActive;
            if (!_state.ConfigOk)
                rest = compactCopy ? "未连内核" : "未连接到代理内核";
            else if (!modeKnown)
                rest = compactCopy ? "读取中…" : "内核状态读取中…";
            else if (nonRuleMode)
                rest = compactCopy ? (Profile.ModeDisplayName(_state.Mode) + " · 暂停") : (Profile.ModeDisplayName(_state.Mode) + "模式 · 暂停规则");
            else if (notProxyActive)
                rest = compactCopy ? "TUN/系统均关" : "客户端未开代理（TUN/系统均关）";
            else if (_mismatch > 0)
                rest = "异常 " + _mismatch + (_autoMismatchCount >= 3 ? " · 点修复" : " · 修复中");
            else if (!compactCopy)
                rest = (Settings.PatchOnly ? "取消勾选≠直连 · 按订阅规则" : "其余软件全部直连") +
                       (_visibleProcs > 0 ? (" · 可见 " + _visibleProcs + " 个") : "");
            float restX = 38f + countBoxW + 8f;
            _mismatchRect = new RectangleF(restX, row1Bounds.Y + 4f, Math.Max(0f, leftRow1Right - restX), 18f);
            Draw.Text(g, rest, _fSmall, (!_state.ConfigOk || nonRuleMode || notProxyActive || _mismatch > 0) ? Theme.Warn : Theme.Dim, _mismatchRect);

            if (!dualColumn)
            {
                bool rateOkMini = _state.ConfigOk && _traffic != null && _traffic.Ok;
                Theme.Fill(g, Theme.SubCard, miniRatePillRect, 7f);
                if (!showStatic)
                {
                    string rateStr = !_state.ConfigOk
                        ? "内核未连接"
                        : (!_state.ProxyActive && !rateOkMini
                            ? "代理未开启"
                            : (rateOkMini ? ("↓" + Traffic.Speed(_rateProxyDown) + "  ↑" + Traffic.Speed(_rateProxyUp)) : "流量读取中"));
                    Draw.Text(g, rateStr, _fTiny, rateOkMini ? Theme.Accent : Theme.Dim, miniRatePillRect, Draw.Center);
                }
                else if (!rateOkMini)
                {
                    string offStr = !_state.ConfigOk ? "内核未连接" : (!_state.ProxyActive ? "代理未开启" : "流量读取中");
                    Draw.Text(g, offStr, _fTiny, Theme.Dim, miniRatePillRect, Draw.Center);
                }
                else
                {
                    float px = miniRatePillRect.X + 6f;
                    float pw = miniRatePillRect.Width - 10f;
                    float y1 = miniRatePillRect.Y + 2f;
                    float y2 = miniRatePillRect.Y + 17f;
                    using (var b1 = new SolidBrush(Theme.Accent)) g.FillEllipse(b1, px, y1 + 5f, 4f, 4f);
                    Draw.Text(g, "↓" + Traffic.Speed(_rateProxyDown) + " ↑" + Traffic.Speed(_rateProxyUp),
                        _fTiny, Theme.Accent, new RectangleF(px + 6f, y1, pw - 6f, 14f), Draw.Right);
                    using (var b2 = new SolidBrush(Theme.Ok)) g.FillEllipse(b2, px, y2 + 5f, 4f, 4f);
                    Draw.Text(g, "↓" + Traffic.Speed(_rateStaticDown) + " ↑" + Traffic.Speed(_rateStaticUp),
                        _fTiny, Theme.Ok, new RectangleF(px + 6f, y2, pw - 6f, 14f), Draw.Right);
                }
            }

            // 第二行左侧：分流底座+模式胶囊 + 当前主中转节点延迟胶囊
            float row2Y = row2Bounds.Y;
            var fp = _fPill ?? (_fPill = Theme.Ui(9.5f));
            bool metaWarn;
            string kernelMeta = FormatKernelMeta(_state.Tun, _state.Mode, out metaWarn);
            string splitLabel = Settings.PatchOnly ? "订阅兼容" : "白名单";
            string chipText;
            bool chipWarn;
            if (!_state.ConfigOk)
            {
                chipText = "未连接 · " + splitLabel + " ▾";
                chipWarn = true;
            }
            else if (!_state.ProxyActive)
            {
                chipText = (string.Equals(_state.Mode, "direct", StringComparison.OrdinalIgnoreCase) ? "直连暂停 · " : "未开代理 · ") + splitLabel + " ▾";
                chipWarn = true;
            }
            else
            {
                chipText = (_state.Tun ? "TUN · " : "系统代理 · ") + splitLabel + (metaWarn ? " !" : "") + " ▾";
                chipWarn = metaWarn;
            }
            float chipW = Math.Min(lw < 580f ? 142f : 156f, Math.Max(78f, g.MeasureString(chipText, fp).Width + 14f));
            var chipRect = new RectangleF(38, row2Y, chipW, 21);
            _modeChipRect = chipRect;
            Color chipBg = chipWarn
                ? Theme.WarnSoft
                : (_hover == Btn.ModeChip ? Theme.Hover : Theme.SubCard);
            Theme.Fill(g, chipBg, chipRect, 10f);
            Draw.Text(g, chipText, fp, chipWarn ? Theme.Warn : Theme.Dim, chipRect, Draw.Center);

            string outb = _state.Outbound;
            bool hasOut = _state.ConfigOk && !string.IsNullOrEmpty(_state.Policy) && !string.IsNullOrEmpty(outb);
            float pillStart = chipRect.Right + 8f;
            float maxPillW = Math.Max(48f, leftRow2Right - pillStart);
            if (!_state.ConfigOk)
            {
                string offTxt = FitPillLabel("", "未连接 · 点此看节点", "", maxPillW - 14f, s => g.MeasureString(s, fp).Width);
                float bw = Math.Min(maxPillW, g.MeasureString(offTxt, fp).Width + 14f);
                var pill = new RectangleF(pillStart, row2Y, bw, 21);
                _delayRect = pill;
                Theme.Fill(g, _hover == Btn.Delay ? Theme.Hover : Theme.Btn, pill, 10f);
                Draw.Text(g, offTxt, fp, Theme.Dim, pill, Draw.Center);
            }
            else if (!hasOut)
            {
                string noOut = "节点 未找到 · 去设置 →";
                float bw = Math.Min(maxPillW, g.MeasureString(noOut, fp).Width + 14f);
                var pill = new RectangleF(pillStart, row2Y, bw, 21);
                _delayRect = pill;
                Theme.Fill(g, _hover == Btn.Delay ? Theme.Hover : Theme.Btn, pill, 10f);
                Draw.Text(g, noOut, fp, Theme.Dim, pill, Draw.Center);
            }
            else if (!_state.ProxyActive)
            {
                string cleanOut = CleanEmoji(outb);
                string standby = FitPillLabel("待连接：", cleanOut, " →", maxPillW - 14f, s => g.MeasureString(s, fp).Width);
                float bw = Math.Min(maxPillW, g.MeasureString(standby, fp).Width + 14f);
                var pill = new RectangleF(pillStart, row2Y, bw, 21);
                _delayRect = pill;
                Theme.Fill(g, _hover == Btn.Delay ? Theme.Hover : Theme.SubCard, pill, 10f);
                Draw.Text(g, standby, fp, Theme.Dim, pill, Draw.Center);
            }
            else
            {
                string suffix = "";
                bool isWarn = _delayTimedOut || (_state.NodeAlive == false && _delayMs <= 0);
                if (Profile.ClassifyExit(outb) == Profile.ExitKind.Proxy)
                {
                    if (_delayTesting && string.Equals(_delayNode, outb, StringComparison.OrdinalIgnoreCase))
                        suffix = " · 测速中";
                    else if (_delayTimedOut && string.Equals(_delayNode, outb, StringComparison.OrdinalIgnoreCase))
                        suffix = " · 超时";
                    else if (_delayMs > 0 && string.Equals(_delayNode, outb, StringComparison.OrdinalIgnoreCase))
                        suffix = " · " + _delayMs + "ms";
                    else if (_state.DelayMs > 0)
                        suffix = " · " + _state.DelayMs + "ms";
                }
                suffix += " →";

                string cleanOut = CleanEmoji(outb);
                string label = FitPillLabel("节点 ", cleanOut, suffix, maxPillW - 14f, s => g.MeasureString(s, fp).Width);
                float bw = Math.Min(maxPillW, g.MeasureString(label, fp).Width + 14f);
                var pill = new RectangleF(pillStart, row2Y, bw, 21);
                _delayRect = pill;
                bool isDirectExit = Profile.ClassifyExit(outb) == Profile.ExitKind.Direct;
                Color pillBg = isWarn ? Theme.WarnSoft : (isDirectExit ? Theme.SubCard : (_hover == Btn.Delay ? Theme.Hover : Theme.AccentSoft));
                Color pillFg = isWarn ? Theme.Warn : (isDirectExit ? Theme.Dim : Theme.Accent);
                Theme.Fill(g, pillBg, pill, 10f);
                Draw.Text(g, label, fp, pillFg, pill, Draw.Center);
            }

            // 第三行左侧（仅开启独立固定 IP 时）：实时端到端延迟胶囊 + 自动稳速刷新提示（左键测速 / 右键菜单）
            if (showStatic && !row3Bounds.IsEmpty)
            {
                var spCfg = StaticProxy.Current;
                string spPrefix, spMid, spSuffix, spTone;
                FormatStaticDelayPillParts(
                    spCfg, _staticLiveHop, _staticDelayMs, _staticTimedOut,
                    _staticChecking, _staticOptimizing, _state.ConfigOk, _state.ProxyActive,
                    out spPrefix, out spMid, out spSuffix, out spTone);
                float maxSpW = Math.Max(96f, row3Bounds.Width);
                string spText = FitPillLabel(spPrefix, spMid, spSuffix, maxSpW - 18f, s => g.MeasureString(s, fp).Width);
                float spW = Math.Min(maxSpW, g.MeasureString(spText, fp).Width + 18f);
                var spPill = new RectangleF(row3Bounds.X, row3Bounds.Y, spW, row3Bounds.Height);
                _staticDelayRect = spPill;

                Color spBg, spFg;
                if (spTone == "danger") { spBg = Theme.DangerSoft; spFg = Theme.Danger; }
                else if (spTone == "warn") { spBg = Theme.WarnSoft; spFg = Theme.Warn; }
                else if (spTone == "accent") { spBg = Theme.AccentSoft; spFg = Theme.Accent; }
                else if (spTone == "dim") { spBg = Theme.SubCard; spFg = Theme.Dim; }
                else { spBg = Theme.OkSoft; spFg = Theme.Ok; }
                if (_hover == Btn.StaticDelay)
                    spBg = Theme.Blend(spBg, spFg, Theme.Dark ? 0.16f : 0.10f);
                Theme.Fill(g, spBg, spPill, 10f);
                using (var dotB = new SolidBrush(spFg))
                    g.FillEllipse(dotB, spPill.X + 7f, spPill.Y + 7.5f, 5.5f, 5.5f);
                Draw.Text(g, spText, fp, spFg, new RectangleF(spPill.X + 14f, spPill.Y, spPill.Width - 18f, spPill.Height), Draw.Left);

                float remainW = row3Bounds.Right - (spPill.Right + 8f);
                if (dualColumn && remainW >= 96f && _state.ConfigOk && _state.ProxyActive)
                {
                    bool isAuto = string.Equals(spCfg.HopMode, StaticProxy.HopAuto, StringComparison.OrdinalIgnoreCase);
                    string spTip = isAuto
                        ? (remainW >= 158f
                            ? ("每" + StaticProxy.AutoHopRefreshIntervalMinutes + "分自动稳速 · 点此测速")
                            : ("每" + StaticProxy.AutoHopRefreshIntervalMinutes + "分自动稳速"))
                        : (remainW >= 122f ? "点此测速 · 右键管理" : "点此测速");
                    Draw.Text(g, spTip, _fTiny, Theme.Dim,
                        new RectangleF(spPill.Right + 8f, row3Bounds.Y + 3f, remainW, 16f));
                }
            }
            else
            {
                _staticDelayRect = RectangleF.Empty;
            }
            if (false && kernelMeta.Length == 0) { }

            if (dualColumn)
            {
                // 中间竖线 + 右上对称流量速率（>=520px 展示完整双栏；开启独立固定 IP 时拆为「代理 / 固定IP / 直连」三行+三色曲线）
                using (var vpen = new Pen(Theme.Line))
                    g.DrawLine(vpen, divX, SummaryTop + 10f, divX, SummaryTop + SummaryH - 10f);

                float rx = sparkRect.X, rw = sparkRect.Width;
                bool estimated = _traffic != null && _traffic.Estimated > 0;
                bool rateOk = _state.ConfigOk && _traffic != null && _traffic.Ok;
                if (showStatic)
                {
                    string staticLabel = rw >= 218f ? "固定IP" : "固定";
                    DrawRateRow(g, rx, rw, SummaryTop + 5, estimated ? "代理≈" : "代理", rateOk,
                        _rateProxyDown, _rateProxyUp, Theme.Accent, true);
                    DrawRateRow(g, rx, rw, SummaryTop + 21, staticLabel, rateOk,
                        _rateStaticDown, _rateStaticUp, Theme.Ok, true);
                    DrawRateRow(g, rx, rw, SummaryTop + 37, "直连", rateOk,
                        _rateDirectDown, _rateDirectUp, Theme.Dim, true);
                }
                else
                {
                    DrawRateRow(g, rx, rw, SummaryTop + 5, estimated ? "代理≈" : "代理", rateOk,
                        _rateProxyDown, _rateProxyUp, Theme.Accent);
                    DrawRateRow(g, rx, rw, SummaryTop + 23, "直连", rateOk,
                        _rateDirectDown, _rateDirectUp, Theme.Dim);
                }
            }

            if (!sparkRect.IsEmpty)
            {
                if (_state.ConfigOk)
                {
                    using (var bpen = new Pen(Color.FromArgb(Theme.Dark ? 55 : 65, Theme.Line), 1f))
                        g.DrawLine(bpen, sparkRect.X, sparkRect.Bottom - 1.5f, sparkRect.Right, sparkRect.Bottom - 1.5f);
                }

                var sparkAt = _sparkAt.ToArray();
                bool hasDirectSpark = _sparkDirect.Count >= 2 && sparkAt.Length == _sparkDirect.Count;
                bool hasProxySpark = _sparkProxy.Count >= 2 && sparkAt.Length == _sparkProxy.Count;
                bool hasStaticSpark = showStatic && _sparkStatic.Count >= 2 && sparkAt.Length == _sparkStatic.Count;
                if (hasDirectSpark)
                    Draw.Spark(g, _sparkDirect.ToArray(), sparkAt, _sparkAxis, SparkPeriodMs, SparkPoints,
                        sparkRect, Theme.Dim, _sparkPulse);
                if (hasProxySpark)
                    Draw.Spark(g, _sparkProxy.ToArray(), sparkAt, _sparkAxis, SparkPeriodMs, SparkPoints,
                        sparkRect, Theme.Accent, _sparkPulse);
                if (hasStaticSpark)
                    Draw.Spark(g, _sparkStatic.ToArray(), sparkAt, _sparkAxis, SparkPeriodMs, SparkPoints,
                        sparkRect, Theme.Ok, _sparkPulse);

                var tipAlign = dualColumn ? Draw.Right : Draw.Center;
                if (!_state.ConfigOk)
                    Draw.Text(g, dualColumn ? "内核未连接" : "等待连接", _fTiny, Theme.Dim, sparkRect, tipAlign);
                else if (_traffic != null && !_traffic.Ok)
                    Draw.Text(g, dualColumn ? "流量读取中…" : "等待曲线…", _fTiny, Theme.Dim, sparkRect, tipAlign);
                else if (!hasProxySpark)
                    Draw.Text(g, "采样中…", _fTiny, Theme.Dim, sparkRect, tipAlign);
            }
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
            Draw.Text(g, label, _fTiny, c, new RectangleF(r.X + 24, r.Y, r.Width - 28, r.Height));
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
        /// 「—」是主动收进右下角托盘；打开面板时任务栏按钮正常保留。
        /// 如果只想暂时离开又不想保留后台进程，请点「×」彻底退出。
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

        void DrawMax(Graphics g)
        {
            var r = MaxRect;
            if (_hover == Btn.Max) Theme.Fill(g, Theme.Hover, r, 7f);
            using (var pen = new Pen(_hover == Btn.Max ? Theme.Fg : Theme.Dim, 1.35f))
            {
                float cx = r.X + 13f, cy = r.Y + 13f;
                if (WindowState == FormWindowState.Maximized)
                {
                    g.DrawRectangle(pen, cx - 3f, cy - 5f, 8f, 8f);
                    using (var br = new SolidBrush(Theme.Card))
                        g.FillRectangle(br, cx - 5f, cy - 3f, 8f, 8f);
                    g.DrawRectangle(pen, cx - 5f, cy - 3f, 8f, 8f);
                }
                else
                {
                    g.DrawRectangle(pen, cx - 4.8f, cy - 4.8f, 9.6f, 9.6f);
                }
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
            float lw = LogicalW, lh = LogicalH;
            using (var b = new SolidBrush(Color.FromArgb(150, 0, 0, 0)))
                g.FillRectangle(b, 0, 0, lw, lh);

            var card = HelpCard;
            Draw.Shadow(g, card, 14f, 90);
            Theme.Fill(g, Theme.Card, card, 14f);
            Theme.Stroke(g, Theme.Line, card, 14f);

            float helpTitleW = Math.Max(130f, card.Right - 170f - (card.X + 20f));
            Draw.Text(g, LogicalW < 580f ? "ProxyPicker 说明与诊断" : "ProxyPicker 功能说明与环境诊断",
                _fHelpTitle, Theme.Fg, new RectangleF(card.X + 20, card.Y + 14, helpTitleW, 24));
            Draw.Text(g, LogicalW < 580f ? "四边拖拽调尺寸 · Ctrl+1/2/3 切页" : "窗口四边与四角可自由拖拽缩放 · 双击标题栏最大化",
                _fSmall, Theme.Dim, new RectangleF(card.X + 20, card.Y + 38, helpTitleW, 16));

            var envInfo = Discovery.Detect();
            Draw.Text(g, envInfo.Ok ? envInfo.Client : "未检测到客户端", _fTiny, Theme.Dim,
                new RectangleF(card.Right - 196, card.Y + 16, 154, 16), Draw.Right);

            // 版本号：没接仓库就只显示号码；接了就变成可点的「检查更新」
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

            string firstTitle = "第 1 页「软件分流」：开 = 走代理，关 = 直连";
            string firstBody = "勾完软件点右下角「保存并生效」，只重连改到的软件；直接点击摘要卡片左下的「白名单 / 订阅兼容」胶囊可随手切模式。";
            if (_mismatch > 0)
            {
                var lines = new List<string>();
                foreach (var m in _mismatchItems.Take(3))
                    lines.Add(m.Exe + "：预期" + (m.Expected ? "代理" : "直连") + "，实际 "
                        + m.Proxy + " 代理 / " + m.Direct + " 直连。" + m.Reason + "；" + m.Action);
                if (_mismatchItems.Count > 3)
                    lines.Add("还有 " + (_mismatchItems.Count - 3) + " 个，回第 1 页点异常提示看完整清单。");
                firstTitle = "分流异常 " + _mismatch + " 个（点摘要卡片橙字可看明细并自动修复）";
                firstBody = string.Join("  ", lines.ToArray());
            }

            string[][] items =
            {
                new[] { firstTitle, firstBody },
                new[] { "第 2 页「节点策略」：并发测速与自动故障切换", "一键测试全部节点延迟，勾选备选池并启用「超时自动切换」；主节点异常时自动切换至低延迟备选节点并推送通知。" },
                new[] { "第 3 页「链路体检」：零干扰独立旁路检测与全量节点评估", "通过独立本地旁路引擎检测真实出口 IP、风险评级、地区一致性及 Google / Gemini / OpenAI / Claude 连通性；评估过程不切换主节点、不断开正在进行的浏览或工作。" },
                new[] { "跨主机便携与绿色软件识别", "无需依赖外部检测工具，开箱自动发现客户端内核与进程目录；支持从托盘菜单手动登记便携绿色版可执行文件。" },
                new[] { "代理模式选择 & 登录窗口保护", "「白名单模式」仅让勾选软件走代理、其余全部直连；「订阅兼容」保留订阅分流并强制送选。WebView2 系统登录窗口已内置放行。" }
            };

            bool twoRowHelpFooter = LogicalW < 580f;
            float footerLineY = card.Bottom - (twoRowHelpFooter ? 88f : 58f);
            float y = card.Y + 60f;
            float rowStep = Math.Min(68f, (footerLineY - 8f - y) / items.Length);
            for (int i = 0; i < items.Length; i++)
            {
                var badge = new RectangleF(card.X + 20, y + 1, 18, 18);
                using (var b = new SolidBrush(Theme.AccentSoft)) g.FillEllipse(b, badge);
                using (var font = Theme.Ui(10f, FontStyle.Bold))
                    Draw.Text(g, (i + 1).ToString(), font, Theme.Accent, badge, Draw.Center);

                Draw.Text(g, items[i][0], _fTabActive, Theme.Fg,
                    new RectangleF(card.X + 48, y, card.Width - 68, 18));
                Draw.TextWrap(g, items[i][1], _fHelp, Theme.Dim,
                    new RectangleF(card.X + 48, y + 19, card.Width - 68, rowStep - 22f));
                y += rowStep;
            }

            // 底部四个按钮：诊断 / 恢复隐藏 / 分流模式 / IPv6 防泄漏
            var line = new RectangleF(card.X + 20, footerLineY, card.Width - 40, 1);
            using (var pen = new Pen(Theme.Line)) g.DrawLine(pen, line.X, line.Y, line.Right, line.Y);

            using (var font = Theme.Ui(10f))
            {
                bool dh = _hover == Btn.HelpDiag;
                Theme.Fill(g, dh ? Theme.BtnHover : Theme.Btn, HelpDiagRect, 8f);
                Theme.Stroke(g, Theme.Line, HelpDiagRect, 8f);
                Draw.Text(g, "复制诊断", font, Theme.Fg, HelpDiagRect, Draw.Center);

                bool uh = _hover == Btn.HelpUnhide;
                Theme.Fill(g, uh ? Theme.BtnHover : Theme.Btn, HelpUnhideRect, 8f);
                Theme.Stroke(g, Theme.Line, HelpUnhideRect, 8f);
                Draw.Text(g, "恢复隐藏", font, Theme.Fg, HelpUnhideRect, Draw.Center);

                bool mh = _hover == Btn.HelpMode;
                bool patch = Settings.PatchOnly;
                Theme.Fill(g, mh ? Theme.BtnHover : Theme.Btn, HelpModeRect, 8f);
                Theme.Stroke(g, patch ? Theme.Line : Theme.Accent, HelpModeRect, 8f);
                Draw.Text(g, patch ? "模式：订阅兼容" : "模式：白名单",
                    font, patch ? Theme.Fg : Theme.Accent, HelpModeRect, Draw.Center);

                bool ih = _hover == Btn.HelpIpv6;
                bool guard = Settings.Ipv6GuardOn;
                Theme.Fill(g, ih ? Theme.BtnHover : Theme.Btn, HelpIpv6Rect, 8f);
                Theme.Stroke(g, guard ? Theme.Warn : Theme.Line, HelpIpv6Rect, 8f);
                Draw.Text(g, guard ? "防泄漏 已开" : "IPv6 防泄漏",
                    font, guard ? Theme.Warn : Theme.Fg, HelpIpv6Rect, Draw.Center);
            }
        }

        static string Ellipsis(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= max ? s : s.Substring(0, max - 1) + "…";
        }

        /// <summary>菜单里显示路径尾部：盘符和中间目录对判断「是不是同一个软件」没用。</summary>
        static string ShortenPath(string p)
        {
            if (string.IsNullOrEmpty(p)) return "";
            if (p.Length <= 48) return p;
            return "…" + p.Substring(p.Length - 47);
        }

        static string CleanEmoji(string s)
        {
            return Draw.CleanEmoji(s);
        }

        string TabText(int i)
        {
            if (i == 0) return "常用";
            if (i == 1) return "已选 " + _all.Count(r => r.Proxy);
            if (i == 2) return "运行中";
            return "全部 " + _all.Count;
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
                if (HelpIpv6Rect.Contains(x, y)) return Btn.HelpIpv6;
                if (Updater.Enabled && HelpUpdateRect.Contains(x, y)) return Btn.HelpUpdate;
                return HelpCard.Contains(x, y) ? Btn.None : Btn.HelpClose;
            }

            if (CloseRect.Contains(x, y)) return Btn.Close;
            if (MaxRect.Contains(x, y)) return Btn.Max;
            if (MinRect.Contains(x, y)) return Btn.Min;
            if (RefreshRect.Contains(x, y)) return Btn.Refresh;
            if (HelpRect.Contains(x, y)) return Btn.Help;

            if (NavSegRect(0).Contains(x, y)) return Btn.Nav0;
            if (NavSegRect(1).Contains(x, y)) return Btn.Nav1;
            if (NavSegRect(2).Contains(x, y)) return Btn.Nav2;

            if (_page != 0) return Btn.None;

            if (!_modeChipRect.IsEmpty && _modeChipRect.Contains(x, y)) return Btn.ModeChip;
            if (_mismatch > 0 && MismatchRect.Contains(x, y)) return Btn.Mismatch;
            if (!_delayRect.IsEmpty && _delayRect.Contains(x, y)) return Btn.Delay;
            if (!_staticDelayRect.IsEmpty && _staticDelayRect.Contains(x, y)) return Btn.StaticDelay;
            if (SaveRect.Contains(x, y)) return Btn.Save;
            if (DirectRect.Contains(x, y)) return Btn.Direct;
            if (SortRect.Contains(x, y)) return Btn.Sort;
            return Btn.None;
        }

        int HitTab(Point p)
        {
            if (_help || _page != 0) return -1;
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
            if (_shuttingDown)
            {
                if (e.Button == MouseButtons.Left && !_help && e.Y / Theme.S < TitleH && WindowState != FormWindowState.Maximized)
                {
                    ReleaseCapture(); SendMessage(Handle, WM_NCLBUTTONDOWN, new IntPtr(HTCAPTION), IntPtr.Zero);
                }
                return;
            }
            var b = HitButton(e.Location);
            if (e.Button == MouseButtons.Right)
            {
                if (b == Btn.Delay) TestDelayPublic();
                else if (b == Btn.StaticDelay) ShowStaticDelayMenu();
                else if (b == Btn.ModeChip) ShowModePickerMenu();
                return;
            }
            if (e.Button != MouseButtons.Left) return;
            if (b == Btn.Close) { Close(); return; }   // 走 OnFormClosing -> 还原配置 -> 真退出
            if (b == Btn.Max) { ToggleMaximize(); return; }
            if (b == Btn.Min) { MinimizeToTray(); return; }
            if (b == Btn.Refresh) { if (_page == 1 || _page == 2) _ = LoadNodesCatalogAsync(); else RefreshAsync(); return; }
            if (b == Btn.Help) { OpenHelp(); return; }
            if (b == Btn.Nav0) { SwitchPage(0); return; }
            if (b == Btn.Nav1) { SwitchPage(1); return; }
            if (b == Btn.Nav2) { SwitchPage(2); return; }
            if (b == Btn.ModeChip) { ShowModePickerMenu(); return; }
            if (b == Btn.Save) { SaveAsync(); return; }
            if (b == Btn.Direct) { AllDirect(); return; }
            if (b == Btn.Sort) { CycleSortAsk(); return; }
            if (b == Btn.HelpClose) { CloseHelp(); return; }
            if (b == Btn.HelpDiag) { CopyDiagnostics(); return; }
            if (b == Btn.HelpUnhide) { UnhideAll(); return; }
            if (b == Btn.HelpMode) { ToggleMode(); return; }
            if (b == Btn.HelpIpv6) { ToggleIpv6Guard(); return; }
            if (b == Btn.HelpUpdate) { VersionClick(); return; }
            if (b == Btn.Mismatch) { ShowMismatchDialog(); return; }
            if (b == Btn.Delay) { PushAndSwitchPage(1); return; }
            if (b == Btn.StaticDelay) { TestStaticDelayPublic(); return; }

            int tab = HitTab(e.Location);
            if (tab >= 0) { _tab = tab; Settings.Current.Tab = tab; Settings.Save(); RebuildVisible(true); return; }
            if (!_help && e.Y / Theme.S < TitleH && WindowState != FormWindowState.Maximized)
            {
                ReleaseCapture(); SendMessage(Handle, WM_NCLBUTTONDOWN, new IntPtr(HTCAPTION), IntPtr.Zero);
            }
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (e.Button != MouseButtons.Left || _help) return;
            if (e.Y / Theme.S < TitleH && HitButton(e.Location) == Btn.None)
                ToggleMaximize();
        }

        /// <summary>在摘要卡片点击左下角模式胶囊时直接弹出「白名单 / 订阅兼容」模式菜单。</summary>
        void ShowModePickerMenu()
        {
            if (!UiAlive || _busy || _modeChipRect.IsEmpty) return;
            var menu = new ContextMenuStrip { ShowImageMargin = false };
            menu.Closed += (s, e) =>
            {
                var c = (ContextMenuStrip)s;
                if (!UiAlive) return;
                BeginInvoke(new Action(() => { if (!c.IsDisposed) c.Dispose(); }));
            };

            bool isPatch = Settings.PatchOnly;
            var whiteItem = new ToolStripMenuItem((isPatch ? "   " : "✓ ") + "代理白名单模式（只有勾选软件走代理，其余直连）");
            whiteItem.Click += (s, e) => ApplyModeSelection(false);
            menu.Items.Add(whiteItem);

            var patchItem = new ToolStripMenuItem((isPatch ? "✓ " : "   ") + "订阅兼容模式（保留原订阅规则，仅强送勾选软件）");
            patchItem.Click += (s, e) => ApplyModeSelection(true);
            menu.Items.Add(patchItem);

            menu.Show(this, new Point(Theme.Px(_modeChipRect.X), Theme.Px(_modeChipRect.Bottom + 4)));
        }

        /// <summary>在摘要卡片右键独立固定 IP 延迟胶囊时弹出的快捷菜单（立即测速 / 立即稳速优选 / 打开设置）。</summary>
        void ShowStaticDelayMenu()
        {
            if (!UiAlive || _busy || _staticDelayRect.IsEmpty) return;
            var cfg = StaticProxy.Current;
            var menu = new ContextMenuStrip { ShowImageMargin = false };
            menu.Closed += (s, e) =>
            {
                var c = (ContextMenuStrip)s;
                if (!UiAlive) return;
                BeginInvoke(new Action(() => { if (!c.IsDisposed) c.Dispose(); }));
            };

            var testItem = new ToolStripMenuItem("立即测固定 IP 实时延迟");
            testItem.Click += (s, e) => TestStaticDelayPublic();
            menu.Items.Add(testItem);

            if (!string.Equals(cfg.HopMode, StaticProxy.HopDirect, StringComparison.OrdinalIgnoreCase))
            {
                var optItem = new ToolStripMenuItem("立即刷新优选前置跳板（每 " + StaticProxy.AutoHopRefreshIntervalMinutes + " 分钟自动稳速）");
                optItem.Enabled = !_staticOptimizing;
                optItem.Click += (s, e) => _ = OptimizeStaticHopAsync(true, false);
                menu.Items.Add(optItem);
            }

            menu.Items.Add(new ToolStripSeparator());
            var manageItem = new ToolStripMenuItem("打开独立固定 IP 设置…");
            manageItem.Click += (s, e) => OpenStaticProxyDialog();
            menu.Items.Add(manageItem);

            menu.Show(this, new Point(Theme.Px(_staticDelayRect.X), Theme.Px(_staticDelayRect.Bottom + 4)));
        }

        void ApplyModeSelection(bool patchOnly)
        {
            if (!UiAlive || _busy || Settings.PatchOnly == patchOnly) return;
            Settings.SetMode(patchOnly ? Settings.ModePatch : Settings.ModeWhitelist);
            _state.Applied = false;
            _modeChanged = true;
            SetStatus("分流模式已切到「" + (patchOnly ? "订阅兼容" : "代理白名单") + "」，点右下角「保存并生效」应用", Theme.Warn);
            Toast(patchOnly ? "已切到「订阅兼容」" : "已切到「代理白名单」",
                patchOnly ? "未勾软件保留订阅规则 · 记得点保存" : "未勾软件全部直连 · 记得点保存",
                patchOnly ? Theme.Warn : Theme.Accent, 2);
            Invalidate();
        }

        /// <summary>
        /// 分流异常完整清单。摘要卡只有一行，异常一多必然放不下（用户实机反馈），
        /// 这里逐条列全：哪个软件、预期什么、实测什么、为什么、下一步怎么办。
        /// MessageBox 的文本可直接 Ctrl+C 复制，便于贴到 issue 里排查。
        /// </summary>
        void ShowMismatchDialog()
        {
            if (_mismatchItems == null || _mismatchItems.Count == 0)
            {
                SetStatus("当前没有分流异常", Theme.Dim);
                return;
            }
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("共 " + _mismatchItems.Count + " 个软件的「实际链路」与「生效规则」不一致：");
            sb.AppendLine();
            int n = 0;
            foreach (var m in _mismatchItems)
            {
                n++;
                sb.AppendLine(n + ") " + m.Exe);
                sb.AppendLine("   预期：" + (m.Expected ? "走代理" : "直连")
                    + "   实测：" + m.Proxy + " 条代理 / " + m.Direct + " 条直连"
                    + (string.IsNullOrEmpty(m.Node) ? "" : "   节点：" + m.Node));
                sb.AppendLine("   原因：" + m.Reason);
                sb.AppendLine("   原因排查：" + m.Action);
                sb.AppendLine();
            }
            sb.AppendLine("统计只算规则生效后新建的连接；辅助进程已按走代理计入预期。");
            sb.AppendLine("按 Ctrl+C 可复制以上清单。");
            sb.AppendLine();
            sb.AppendLine("点击「是(Y)」立即自动修复：修正规则模式/进程识别、重推名单规则，并精确断开上述错路连接以立即重新分流；");
            sb.AppendLine("点击「否(N)」仅关闭窗口。");
            var choice = MessageBox.Show(this, sb.ToString(),
                "发现 " + _mismatchItems.Count + " 个分流异常 — 是否立即自动修复？",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (choice == DialogResult.Yes)
                _ = RepairMismatchAsync(false);
        }

        /// <summary>托盘菜单也可直接触发「一键修复分流异常」。</summary>
        public void RepairMismatchPublic()
        {
            if (!UiAlive) return;
            if (InvokeRequired) { try { BeginInvoke(new Action(RepairMismatchPublic)); } catch { } return; }
            _ = RepairMismatchAsync(false);
        }

        async Task RepairMismatchAsync(bool auto = false)
        {
            bool ownsBusy = false;
            try
            {
                if (!UiAlive || _busy || _shuttingDown) return;
                if (auto && (Settings.RepairSuspended || _modeChanged || !Profile.HasSavedIntent)) return;
                if (!auto) _autoMismatchCount = 0;

                ownsBusy = true;
                int fixRevision = _revision;
                BeginBusy();
                _lastRepairAt = DateTime.UtcNow;
                _lastAutoMismatchAt = DateTime.UtcNow;
                SetStatus(auto
                    ? "正在自动修复分流异常（校对内核设置、重推规则并断开错路连接）…"
                    : "正在修复分流异常（校对内核设置、重推规则并断开错路连接）…", Theme.Dim);

                var mismatchSnapshot = new List<Traffic.MismatchItem>(_mismatchItems ?? new List<Traffic.MismatchItem>());
                bool hadRowsLoaded = _all.Count > 0;
                List<string> proxy;
                if (hadRowsLoaded)
                {
                    proxy = _all.Where(r => r.Proxy).Select(r => r.Exe).ToList();
                }
                else
                {
                    // --tray 后台启动还没打开面板时，_all 尚为空：不能拿空列表覆盖磁盘已有白名单。
                    var diskItems = Profile.Load();
                    if (Profile.LastLoadFailed)
                    {
                        SetStatus("读取已保存名单失败，已中止修复以免覆盖名单：" + Profile.LastLoadError, Theme.Warn);
                        if (!auto) Toast("未能自动修复", Profile.LastLoadError, Theme.Warn, 2);
                        return;
                    }
                    proxy = diskItems.Where(i => !i.IsPathRule).Select(i => i.Exe).ToList();
                }
                var visible = VisibleExes();
                var paths = PathMap();
                var policies = _all.Count > 0 ? PolicyMapFromRows() : Profile.LoadPolicyMap();
                List<Profile.Item> builtItems = null;

                var outcome = await Task.Run(() => Profile.WithApplyLock(() =>
                {
                    string cfg = Paths.ClashYaml;
                    bool configPatched = false;
                    string configErr = "";
                    bool configOk = Profile.PatchKernelConfigsForRules(cfg, out configPatched, out configErr);

                    builtItems = ComposeWithHelpers(proxy, visible, paths, policies);
                    // 主动修复链路：
                    // 1) autoRecover=false，免受未首次保存或暂停标记阻挡；
                    // 2) forceRuleMode=true，防止 PUT /configs 全文用磁盘里的 global/direct 盖掉前面刚修好的 mode=rule。
                    var applyRes = Profile.Apply(builtItems, false, cfg, -1, null, true);

                    int scanned = 0;
                    var wrongSnap = Profile.CaptureWrongRouteConnections(mismatchSnapshot, out scanned, cfg);
                    Profile.ReconnectOutcome closeOutcome = null;
                    if (wrongSnap.Error == null && wrongSnap.Ids.Count > 0)
                        closeOutcome = Profile.CloseCapturedConnections(wrongSnap, cfg);

                    var stateAfter = Profile.ReadKernelState(cfg);
                    return new
                    {
                        ConfigOk = configOk,
                        ConfigPatched = configPatched,
                        ConfigErr = configErr,
                        Apply = applyRes,
                        WrongSnap = wrongSnap,
                        Closed = closeOutcome,
                        State = stateAfter
                    };
                }));

                if (!UiAlive || fixRevision != _revision) return;

                if (outcome.State != null) _state = outcome.State;
                if (outcome.Apply != null && outcome.Apply.Ok)
                {
                    Settings.ResumeRepair();
                    SetEffectiveProxy(builtItems);
                    if (hadRowsLoaded)
                    {
                        _savedProxy = new HashSet<string>(proxy, StringComparer.OrdinalIgnoreCase);
                        _savedPolicies = SnapshotSavedPoliciesFromRows();
                        _list.SavedProxy = _savedProxy;
                        _list.SavedPolicies = _savedPolicies;
                        SyncStaticUiBanner();
                    }
                }

                if (outcome.ConfigOk && outcome.Apply != null && outcome.Apply.Ok)
                {
                    // 修复真正通过后才重设流量基线并清空旧异常标记，下一秒重新采样验证
                    Traffic.NotePush();
                    _mismatch = 0;
                    _mismatchItems = new List<Traffic.MismatchItem>();
                    _mismatchStreak = 0;
                    _repairFails = 0;
                    _repairNoProgress = 0;
                }

                int closed = outcome.Closed != null ? outcome.Closed.Closed : 0;
                int failedClose = outcome.Closed != null ? outcome.Closed.Failed : 0;
                bool hasBlockedOut = proxy.Count > 0 && (string.IsNullOrEmpty(_state.Outbound) || Profile.IsBlockedPolicy(_state.Outbound));

                AuditLog.Record(auto ? "AUTOFIX" : "FIX",
                    "异常修复：configPatched=" + outcome.ConfigPatched +
                    " applyOk=" + (outcome.Apply != null && outcome.Apply.Ok) +
                    " wrongClosed=" + closed + " wrongFailed=" + failedClose +
                    " outbound=" + _state.Outbound);

                if (!outcome.ConfigOk)
                {
                    SetStatus("修复部分失败：" + outcome.ConfigErr, Theme.Warn);
                    Toast("未能自动修复内核设置", outcome.ConfigErr, Theme.Warn, 2);
                }
                else if (outcome.Apply != null && !outcome.Apply.Ok)
                {
                    SetStatus("规则补回未通过：" + outcome.Apply.Message, Theme.Warn);
                    Toast("规则补回未确认", outcome.Apply.Message, Theme.Warn, 2);
                }
                else if (hasBlockedOut)
                {
                    // 节点指向 DIRECT/REJECT 只能用户在 Clash 客户端手动换节点，停止自动重复重试以免扰民。
                    _autoMismatchCount = 3;
                    string warn = "规则和连接已刷新，但策略组「" + _state.Policy + "」当前指向 " +
                                  (string.IsNullOrEmpty(_state.Outbound) ? "未找到" : _state.Outbound) +
                                  "，请在 Clash 客户端切换到一个可用代理节点";
                    SetStatus(warn, Theme.Warn);
                    Toast("规则已修好，但当前未选可用节点", warn, Theme.Warn, 2);
                }
                else
                {
                    var actions = new List<string>();
                    if (outcome.ConfigPatched) actions.Add("已切回规则模式/进程识别");
                    actions.Add("已重推 " + (builtItems == null ? 0 : builtItems.Count) + " 条规则");
                    if (closed > 0) actions.Add("已断开 " + closed + " 条错路连接");
                    if (failedClose > 0) actions.Add(failedClose + " 条连接断开失败");
                    string summary = string.Join(" · ", actions.ToArray());
                    SetStatus("已自动修复分流异常：" + summary, Theme.Ok);
                    if (!auto)
                        Toast("分流异常已修复", summary + "；请重新发起一次请求验证", Theme.Ok, 2);
                    else if (_autoMismatchCount <= 1)
                        Toast("已自动修复分流异常", summary, Theme.Ok, 1);
                }
                UpdateLogContext();
                PollTraffic();
            }
            catch (Exception ex) { ReportFailure("自动修复分流异常", ex); }
            finally { if (ownsBusy) FinishBusy(); }
        }

        void OpenHelp()
        {
            _help = true;
            ApplyPageVisibility();
            Invalidate();
        }

        void CloseHelp()
        {
            _help = false;
            ApplyPageVisibility();
            // 面板里切过模式就补一句话：切换只改了偏好，真正生效还差一次保存
            if (_modeChanged)
            {
                // 复审 P1：这个标记必须一直保持到「保存成功」，
                // 关掉帮助面板就清零会让 30 秒看守替用户把新模式推下去。
                bool patch = Settings.PatchOnly;
                Toast(patch ? "已切到「订阅兼容」" : "已切到「代理白名单」",
                    patch ? "没勾的软件继续按订阅规则走 · 记得点保存" : "没勾的软件全部直连 · 记得点保存",
                    patch ? Theme.Warn : Theme.Accent, 2);
            }
            if (_ipv6Changed)
            {
                _ipv6Changed = false;
                Toast("IPv6 设置已改", "点右下角「保存并生效」才会应用", Theme.Warn, 2);
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

        /// <summary>
        /// IPv6 防泄漏：默认关，只有用户主动点开才会在保存时写 ipv6: false。
        /// 开启前要读一次内核原值（可能卡住几百毫秒），所以放到后台线程做。
        /// </summary>
        void ToggleIpv6Guard()
        {
            if (!UiAlive || _busy || _ipv6Busy) return;
            _ipv6Busy = true;
            bool on = !Settings.Ipv6GuardOn;
            SetStatus(on ? "正在开启 IPv6 防泄漏…" : "正在关闭 IPv6 防泄漏…", Theme.Warn);
            Task.Run(() => Settings.SetIpv6Guard(on)).ContinueWith(t =>
            {
                var ex = t.IsFaulted ? t.Exception : null;   // 始终先消费异步异常防止销毁后泄漏
                if (!UiAlive) return;
                // 失败不能只吞掉异常还显示「已开启」（评审 R5-4）。
                if (ex != null)
                {
                    string msg = ex.GetBaseException().Message;
                    try
                    {
                        BeginInvoke(new Action(() =>
                        {
                            _ipv6Busy = false;
                            Toast("IPv6 设置没保存成功", msg, Theme.Warn, 2);
                            SetStatus("IPv6 防泄漏开关未生效：" + msg, Theme.Warn);
                        }));
                    }
                    catch { }
                    return;
                }
                try
                {
                    BeginInvoke(new Action(() =>
                    {
                        _ipv6Busy = false;
                        Toast(on ? "已开启 IPv6 防泄漏" : "已关闭 IPv6 防泄漏",
                            on ? "保存时会写入 ipv6: false；Clash Verge 可能覆盖它，要彻底关闭请在客户端设置里改"
                               : "程序不再改动 IPv6，下次保存即恢复无干预状态",
                            on ? Theme.Warn : Theme.Accent, 2);
                        SetStatus(on ? "IPv6 防泄漏已开，点「保存并生效」应用" : "IPv6 归客户端管理，点「保存并生效」应用", Theme.Warn);
                        _state.Applied = false;
                        _ipv6Changed = true;
                        Invalidate();
                    }));
                }
                catch { }
            });
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
                        string safeUrl = Updater.IsSafeReleaseUrl(_update.Url)
                            ? _update.Url.Trim()
                            : Updater.SafeReleasesPageUrl;
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = safeUrl,
                            UseShellExecute = true
                        });
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
                // 失败也记时间（但 4 小时后重试）；成功则缓存远端版本供离线复提醒。
                Settings.MarkUpdateChecked(info.Ok, info.Latest, info.Url);
                if (info.Ok && info.Newer)
                {
                    AuditLog.Record("UPDATE", "发现新版本 v" + info.Latest);
                    NotifyNewVersion(info.Latest);
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

        /// <summary>
        /// 程序一启动（托盘就绪后）就调一次。两件事：
        /// ① 用上次查到的远端版本先提醒一次 —— 24 小时节流期间不联网也能提示，
        ///    用户不必「记得自己去点检查更新」；
        /// ② 到了检查间隔才真发那个 GET。窗口收在托盘 / --tray 后台启动同样会跑。
        /// </summary>
        public void StartAutoUpdateCheck()
        {
            if (_updateSessionDone) return;
            _updateSessionDone = true;
            if (!Updater.Enabled || !Settings.Current.UpdateCheck) return;

            string cached = Settings.CachedLatestVersion;
            if (!string.IsNullOrEmpty(cached) && Updater.NewerThan(cached, Updater.Current))
            {
                string url = Settings.CachedLatestUrl;
                if (!Updater.IsSafeReleaseUrl(url))
                    url = Updater.SafeReleasesPageUrl;
                _update = new Updater.Info
                {
                    Ok = true, Newer = true,
                    Latest = Updater.Clean(cached),
                    Url = url
                };
                NotifyNewVersion(_update.Latest);
            }
            if (Settings.ShouldCheckUpdate(24)) CheckUpdateAsync(false);
        }

        /// <summary>同一个版本每次运行只提醒一次，避免缓存提示 + 联网复查重复打扰。</summary>
        void NotifyNewVersion(string version)
        {
            if (string.IsNullOrEmpty(version) || _notifiedVersion == version) return;
            _notifiedVersion = version;
            Toast("有新版本 v" + version, "点版本号打开下载页；托盘也会弹通知", Theme.Accent, 2);
            var bubble = TrayBalloon;
            if (bubble != null)
            {
                try { bubble("ProxyPicker 有新版本 v" + version, "当前 v" + Updater.CurrentText + " · 点这条通知打开面板"); }
                catch { }
            }
        }

        /// <summary>托盘菜单里的「检查更新」入口。</summary>
        public void CheckUpdatePublic()
        {
            if (!Updater.Enabled) return;
            CheckUpdateAsync(true);
        }

        /// <summary>托盘「复制诊断信息」走同一个入口，保证脱敏口径一致。</summary>
        public void CopyDiagnosticsPublic() { CopyDiagnostics(); }

        /// <summary>托盘菜单入口：补上自动扫描的四类盲区。</summary>
        public void AddExeManuallyPublic()
        {
            ShowPanel();
            AddExeManually();
        }

        /// <summary>
        /// 自动扫描只有四个来源（已保存名单 / 内置清单 / 运行进程 / 注册表与开始菜单），
        /// 绿色版、MSIX、游戏目录、脚本类 bot 会漏。手选路径只记进程名并持久化到
        /// settings.json 的 custom，刷新列表时仍在；规则按进程名匹配，路径仅用于显示，
        /// 文件以后挪走也不影响分流。
        /// </summary>
        void AddExeManually()
        {
            using (var dlg = new OpenFileDialog
            {
                Title = "手动添加软件（选它的 .exe 主程序）",
                Filter = "可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*",
                CheckFileExists = true,
                Multiselect = false
            })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                string path = dlg.FileName;
                string exe = Profile.Normalize(System.IO.Path.GetFileName(path));
                if (string.IsNullOrEmpty(exe)) return;

                bool existed = false;
                foreach (var r in _all)
                    if (r.Exe.Equals(exe, StringComparison.OrdinalIgnoreCase))
                    {
                        existed = true;
                        r.Common = true;
                        if (string.IsNullOrEmpty(r.Path)) r.Path = path;
                        break;
                    }
                Settings.AddCustomExe(exe);
                if (!existed)
                    _all.Add(new AppRow
                    {
                        Exe = exe,
                        Name = System.IO.Path.GetFileNameWithoutExtension(path),
                        Description = "手工添加",
                        Path = path,
                        Common = true
                    });
                RebuildVisible(true);
                SetStatus("已添加 " + exe + "；勾选后点「保存」生效", Theme.Accent);
            }
        }

        /// <summary>
        /// 托盘「还原上次保存前的配置」成功后同步界面内存状态：
        /// 旧勾选还在内存里的话，30 秒看守会把刚还原的配置又补回去（复审 P2）。
        /// </summary>
        public void AfterRollback() { AfterRollback(true); }

        /// <summary>intentCleared=false：内核已清干净但名单文件删不掉，此时不能假装万事大吉。</summary>
        public void AfterRollback(bool intentCleared)
        {
            if (!UiAlive) return;
            // 作废所有在途看守结果：还原那一刻可能已有一次对账跑到一半，
            // 不改 revision 的话它会按旧结论把刚还原的规则重新推回去（外部评审 P0）。
            _revision++;
            if (intentCleared)
            {
                _savedProxy = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                _savedPolicies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                // 独立审阅第二轮：派生名单（含目录正则）也要一起清。以前还原后还留着
                // 上一次接管的「预期代理名单」，流量采样会拿旧预期去比新直连，误报分流异常。
                SetEffectiveProxy(null);
                _list.SavedProxy = _savedProxy;
                _list.SavedPolicies = _savedPolicies;
                foreach (var r in _all) { r.Proxy = false; r.Known = false; r.Policy = ""; }
                SyncStaticUiBanner();
                _modeChanged = false;
                _mismatch = 0;
                _mismatchItems = new List<Traffic.MismatchItem>();
                _mismatchStreak = 0;
                _autoMismatchCount = 0;
                _lastAutoMismatchAt = DateTime.MinValue;
            }
            _state.Applied = false;
            _autoRepaired = false;
            ResetDelayBannerState();
            _catalogSeq++;
            if (_busy) { _pendingRefresh = true; return; }
            RebuildVisible(true);
            RefreshAsync();
        }

        // ---------------------------------------------------------------- 无损自动刷新与独立固定 IP 管理

        /// <summary>
        /// 无损合并后台进程扫描结果：只更新 Running / Path / 同名冲突并增量发现新启动的用户软件，
        /// 100% 保留用户尚未保存的勾选与固定 IP 策略草稿。返回 true 表示列表发生了可见变化。
        /// </summary>
        internal static bool MergeScannedProcesses(List<AppRow> rows, Dictionary<string, Scanner.ProcInfo> procs)
        {
            if (rows == null || procs == null) return false;
            bool changed = false;
            var byExe = new Dictionary<string, AppRow>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in rows)
                if (r != null && !string.IsNullOrEmpty(r.Exe)) byExe[r.Exe] = r;

            foreach (var r in rows)
            {
                if (r == null || string.IsNullOrEmpty(r.Exe)) continue;
                Scanner.ProcInfo info;
                int nextRunning = procs.TryGetValue(r.Exe, out info) && info != null ? info.Count : 0;
                if (r.Running != nextRunning)
                {
                    r.Running = nextRunning;
                    changed = true;
                }
                if (info != null)
                {
                    if (string.IsNullOrEmpty(r.Path) && !string.IsNullOrEmpty(info.Path))
                        r.Path = info.Path;
                    r.NameConflict = info.NameConflict;
                    if (info.AllPaths != null && info.AllPaths.Count > 0)
                        r.AllPaths = new List<string>(info.AllPaths);
                }
            }

            foreach (var kv in procs)
            {
                if (byExe.ContainsKey(kv.Key)) continue;
                var info = kv.Value;
                if (info == null || !Catalog.IsUserApp(kv.Key, info.Path)) continue;
                var added = new AppRow
                {
                    Exe = kv.Key,
                    Name = Catalog.DisplayName(kv.Key),
                    Description = Catalog.Description(kv.Key),
                    NameConflict = info.NameConflict,
                    AllPaths = info.AllPaths != null ? new List<string>(info.AllPaths) : new List<string>(),
                    Path = info.Path ?? "",
                    Running = info.Count,
                    Proxy = false,
                    Known = false,
                    Policy = "",
                    Common = Catalog.IsCommon(kv.Key) || Settings.UsageOf(kv.Key) > 0 || Settings.IsCustomExe(kv.Key),
                    Anim = 0f,
                    Usage = Settings.UsageOf(kv.Key),
                    LastUsed = Settings.LastUsedOf(kv.Key)
                };
                rows.Add(added);
                byExe[kv.Key] = added;
                changed = true;
            }
            return changed;
        }

        async Task AutoRefreshRunningProcessesAsync()
        {
            if (!UiAlive || !Visible || _busy || _autoRefreshing || _shuttingDown || _all.Count == 0 || _list.IsInteracting) return;
            _autoRefreshing = true;
            int rev = _revision;
            try
            {
                var procs = await Task.Run(() => Scanner.Scan());
                if (!UiAlive || !Visible || _busy || _shuttingDown || rev != _revision || _list.IsInteracting) return;
                if (MergeScannedProcesses(_all, procs))
                {
                    ApplySort(_all, Settings.Current.SortMode);
                    RebuildVisible(false, true);
                }
            }
            catch { }
            finally
            {
                _autoRefreshing = false;
            }
        }

        void SyncStaticUiBanner()
        {
            var cfg = StaticProxy.Current;
            bool active = cfg.IsActive();
            int boundCount = _all.Count(r => r.Proxy && r.IsStatic);
            string hopDisplay = "";
            if (active)
            {
                if (!string.IsNullOrWhiteSpace(_staticLiveHop) && !StaticProxy.IsManagedName(_staticLiveHop))
                    hopDisplay = _staticLiveHop;
                else if (string.Equals(cfg.HopMode, StaticProxy.HopAuto, StringComparison.OrdinalIgnoreCase))
                    hopDisplay = cfg.AutoBestHopNode ?? "";
                else if (string.Equals(cfg.HopMode, StaticProxy.HopPin, StringComparison.OrdinalIgnoreCase))
                    hopDisplay = cfg.PinnedHopNode ?? "";
            }
            _list.StaticProxyEnabled = active;
            _nodesPage.SetStaticProxyState(
                active,
                cfg.DisplayLabel,
                cfg.LastVerifiedSummary,
                boundCount,
                _staticDelayMs,
                _staticTimedOut,
                cfg.HopMode,
                hopDisplay,
                _staticOptimizing,
                _staticChecking);
            if (active != _lastShowStaticRow)
            {
                _lastShowStaticRow = active;
                if (IsHandleCreated) LayoutChildren();
                Invalidate();
            }
            else if (IsHandleCreated)
            {
                Invalidate(new Rectangle(0, Theme.Px(SummaryTop - 2), Width, Theme.Px(SummaryH + 6)));
            }
            _list.Invalidate();
        }

        void OpenStaticProxyDialog()
        {
            if (!UiAlive || _busy) return;
            var current = StaticProxy.Current;
            var nodes = _nodesPage.SnapshotConcreteNodes().Select(n => n.Name).ToList();
            if (nodes.Count == 0 && _state.ConfigOk)
            {
                try
                {
                    var cat = NodeManager.LoadCatalog(Paths.ClashYaml);
                    if (cat != null && cat.Ok && cat.Nodes != null)
                        nodes = cat.Nodes.Where(n => !n.IsGroup && !ProbeSidecar.IsInformationalNodeName(n.Name))
                                         .Select(n => n.Name).ToList();
                }
                catch { }
            }
            var appChoices = _all.Select(r => new StaticProxyDialog.AppChoice
            {
                Exe = r.Exe,
                Name = r.Name,
                Proxy = r.Proxy,
                IsStatic = r.IsStatic,
                Running = r.Running > 0
            }).ToList();

            using (var dlg = new StaticProxyDialog(current, nodes, appChoices, Paths.ClashYaml))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK || dlg.ResultConfig == null) return;

                string saveErr;
                if (!StaticProxy.Save(dlg.ResultConfig, out saveErr))
                {
                    Toast("保存独立固定 IP 失败", saveErr, Theme.Danger, 3);
                    return;
                }

                _staticDelayMs = -1;
                _staticTimedOut = false;
                _staticTimeoutStreak = 0;

                if (dlg.Outcome == StaticProxyDialog.DialogOutcome.SavedAndEnabled)
                {
                    var selected = dlg.SelectedStaticExes ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    int boundCount = 0;
                    foreach (var r in _all)
                    {
                        if (selected.Contains(r.Exe))
                        {
                            // 在弹窗里专门勾选走独立固定 IP 的软件：自动开启代理并锁定到 PP-STATIC-OUT
                            r.Proxy = true;
                            r.Anim = 1f;
                            r.Policy = StaticProxy.NodeName;
                            boundCount++;
                        }
                        else if (r.IsStatic)
                        {
                            // 在弹窗里取消了独立 IP 勾选：切回普通高速代理，不改变其代理开关
                            r.Policy = "PP-FIXED";
                        }
                    }
                    SyncStaticUiBanner();
                    RebuildVisible(false);
                    AuditLog.Record("STATIC", "已启用独立固定 IP（hopMode=" + dlg.ResultConfig.HopMode + "，指定软件=" + boundCount + "）");
                    if (boundCount > 0)
                    {
                        Toast("已启用独立固定 IP（" + boundCount + " 个软件已锁定）",
                            "其余已代理软件继续走原高速节点，未选软件直连；正在更新内核…", Theme.Ok, 3);
                    }
                    else
                    {
                        Toast("已启用独立固定 IP",
                            "可在「1. 分流」软件旁一键点切「高速 / 固定IP」，或再次打开此处集中勾选", Theme.Ok, 3);
                    }
                    if (_state.ConfigOk && (boundCount > 0 || Profile.HasSavedIntent || _all.Any(r => r.Proxy)))
                        SaveAsync();
                    if (_state.ConfigOk &&
                        string.Equals(dlg.ResultConfig.HopMode, StaticProxy.HopAuto, StringComparison.OrdinalIgnoreCase) &&
                        string.IsNullOrWhiteSpace(dlg.ResultConfig.AutoBestHopNode))
                    {
                        // 首次切到「智能优选最低延迟跳板」且尚未测过最优前置时，后台零干扰自动优选并热切
                        _ = OptimizeStaticHopAfterSaveAsync();
                    }
                }
                else if (dlg.Outcome == StaticProxyDialog.DialogOutcome.DisabledToDirect ||
                         dlg.Outcome == StaticProxyDialog.DialogOutcome.DisabledToDefaultProxy)
                {
                    bool toDirect = dlg.Outcome == StaticProxyDialog.DialogOutcome.DisabledToDirect;
                    foreach (var r in _all)
                    {
                        if (!r.IsStatic) continue;
                        if (toDirect) { r.Proxy = false; r.Policy = ""; r.Anim = 0f; }
                        else { r.Policy = "PP-FIXED"; }
                    }
                    SyncStaticUiBanner();
                    RebuildVisible(false);
                    AuditLog.Record("STATIC", "已停用独立固定 IP（受影响软件处理：" + (toDirect ? "改为直连" : "切回高速代理") + "）");
                    Toast("已停用独立固定 IP",
                        toDirect ? "原绑定固定 IP 的软件已安全改为直连，正在更新内核…" : "原绑定固定 IP 的软件已切回高速代理，正在更新内核…",
                        Theme.Warn, 3);
                    if (_state.ConfigOk && (Profile.HasSavedIntent || _all.Any(r => r.Proxy))) SaveAsync();
                }
            }
        }

        async Task CheckStaticProxyHealthAsync(bool manual)
        {
            if (!UiAlive || _staticChecking || _shuttingDown) return;
            var cfg = StaticProxy.Current;
            if (!cfg.IsActive()) return;

            _staticChecking = true;
            _lastStaticHealthCheckAt = DateTime.UtcNow;
            SyncStaticUiBanner();
            if (manual)
                SetStatus("正在实测独立固定 IP 端到端延迟…", Theme.Dim);
            try
            {
                string cfgPath = Paths.ClashYaml;
                var res = await Task.Run(() => Profile.MeasureDelayConfirmed(
                    StaticProxy.NodeName, null, StaticProxy.HealthTimeoutMs, cfgPath));
                if (!UiAlive || res.KernelUnreachable) return;
                if (res.Ok)
                {
                    bool wasDown = _staticTimedOut;
                    _staticTimeoutStreak = 0;
                    _staticDelayMs = res.DelayMs;
                    _staticTimedOut = false;
                    if (!string.Equals(cfg.HopMode, StaticProxy.HopDirect, StringComparison.OrdinalIgnoreCase))
                    {
                        string liveHop = await Task.Run(() => StaticProxy.ReadLiveHopNode(cfgPath));
                        if (!string.IsNullOrEmpty(liveHop)) _staticLiveHop = liveHop;
                    }
                    bool degraded = StaticProxy.IsStaticDelayDegraded(res.DelayMs, cfg.AutoBestDelayMs);
                    _staticSlowStreak = degraded ? (_staticSlowStreak + 1) : 0;

                    SyncStaticUiBanner();
                    string hopHint = string.IsNullOrEmpty(_staticLiveHop)
                        ? ""
                        : ("（前置：" + CleanEmoji(_staticLiveHop) + "）");
                    if (wasDown)
                    {
                        AuditLog.Record("STATIC", "独立固定 IP 已恢复可用 · " + res.DelayMs + " ms");
                        SetStatus("独立固定 IP 已恢复：" + res.DelayMs + " ms" + hopHint, Theme.Ok);
                        Toast("独立固定 IP 已恢复", cfg.DisplayLabel + " · " + res.DelayMs + " ms" + hopHint, Theme.Ok, 2);
                    }
                    else if (manual)
                    {
                        SetStatus("独立固定 IP 端到端延迟 " + res.DelayMs + " ms" + hopHint, degraded ? Theme.Warn : Theme.Ok);
                        Toast("独立固定 IP 测速正常", res.DelayMs + " ms" + hopHint, degraded ? Theme.Warn : Theme.Ok, 2);
                    }
                    // 智能优选模式下的每 10 分钟定时稳速刷新 + 连续 2 轮劣化提前触发重优
                    bool isAutoHop = string.Equals(cfg.HopMode, StaticProxy.HopAuto, StringComparison.OrdinalIgnoreCase);
                    if (!manual && StaticProxy.ShouldRunScheduledAutoHopRefresh(
                            isAutoHop, _staticOptimizing, _lastAutoHopOptimizeAt,
                            cfg.AutoBestUpdatedAtTicks, DateTime.UtcNow, _staticSlowStreak, false))
                    {
                        _ = OptimizeStaticHopAsync(false, false);
                    }
                }
                else if (res.TimedOut)
                {
                    _staticTimeoutStreak++;
                    if (!manual && _staticTimeoutStreak == 1)
                    {
                        // 后台首次超时先静默等待 12 秒二次确认（底层 PP-STATIC-OUT 规则依然锁定防漂移），
                        // 避免跨洋瞬时 10 秒内可自愈的波动误弹窗吓唬用户去停用。
                        _ = ReconfirmStaticProxyHealthAfterDelayAsync();
                        return;
                    }
                    // 严格遵循宁断不漂（Fail-Closed）：绝不自动把固定 IP 软件漂移到普通共享节点
                    _staticDelayMs = -1;
                    _staticTimedOut = true;
                    _staticSlowStreak = 0;
                    SyncStaticUiBanner();
                    // 智能优选模式下超时：满足 120 秒急救冷却后自动触发零干扰端到端重优切换前置跳板（外部固定 IP 保持绝对不变）
                    bool isAutoHop = string.Equals(cfg.HopMode, StaticProxy.HopAuto, StringComparison.OrdinalIgnoreCase);
                    if (StaticProxy.ShouldRunScheduledAutoHopRefresh(
                            isAutoHop, _staticOptimizing, _lastAutoHopOptimizeAt,
                            cfg.AutoBestUpdatedAtTicks, DateTime.UtcNow, 0, true))
                    {
                        _ = OptimizeStaticHopAsync(false, true);
                    }
                    if (StaticProxy.ShouldAlertStaticTimeout(manual, _staticTimeoutStreak, _lastStaticAlertAt, DateTime.UtcNow))
                    {
                        _lastStaticAlertAt = DateTime.UtcNow;
                        AuditLog.Record("STATIC", "独立固定 IP 不可达（已锁定防漂移）：" + (res.Error ?? "超时"));
                        Toast("独立固定 IP 不可达（已锁定防漂移）",
                            isAutoHop
                                ? "已锁定固定 IP 防泄漏，正在后台自动重测优选可用前置跳板…"
                                : "为防止 IP 暴露，已阻断漂移到普通节点；请检查前置跳板或静态 IP 状态",
                            Theme.Danger, 3);
                        if (!Visible && TrayBalloon != null)
                            TrayBalloon("独立固定 IP 不可达（已锁定防漂移）", "请检查前置跳板或静态 IP 状态");
                    }
                }
            }
            catch { }
            finally
            {
                _staticChecking = false;
                if (UiAlive) SyncStaticUiBanner();
            }
        }

        async Task OptimizeStaticHopAfterSaveAsync()
        {
            try
            {
                // 等待刚触发的 SaveAsync 推送内核完成，再立刻零干扰优选并热切
                for (int i = 0; i < 30 && _busy; i++) await Task.Delay(250);
                if (!UiAlive || _shuttingDown || !StaticProxy.IsEnabledAndValid) return;
                await OptimizeStaticHopAsync(true, false);
            }
            catch { }
        }

        async Task OptimizeStaticHopAsync(bool manual, bool emergencyRecovery)
        {
            if (!UiAlive || _staticOptimizing || _shuttingDown) return;
            var cfg = StaticProxy.Current;
            if (!cfg.IsActive()) return;
            if (string.Equals(cfg.HopMode, StaticProxy.HopDirect, StringComparison.OrdinalIgnoreCase))
            {
                if (manual) Toast("当前为直连模式", "直连落地不经前置跳板，无需优选；如需优选请先在固定 IP 设置中选择「智能优选」", Theme.Warn, 2);
                return;
            }

            _staticOptimizing = true;
            _lastAutoHopOptimizeAt = DateTime.UtcNow;
            SyncStaticUiBanner();
            if (manual)
                SetStatus("正在通过零干扰旁路实测全部前置跳板的固定 IP 端到端延迟…", Theme.Accent);

            try
            {
                string cfgPath = Paths.ClashYaml;
                string curHopBefore = await Task.Run(() => StaticProxy.ReadLiveHopNode(cfgPath));
                var bench = await Task.Run(() => StaticProxy.BenchmarkBestHopsThroughSidecar(
                    cfg, cfgPath,
                    msg =>
                    {
                        if (manual && UiAlive && IsHandleCreated)
                        {
                            try { BeginInvoke(new Action(() => SetStatus(msg, Theme.Accent))); }
                            catch { }
                        }
                    },
                    default(System.Threading.CancellationToken),
                    curHopBefore));
                if (!UiAlive || _shuttingDown) return;
                if (bench == null || !bench.Ok || string.IsNullOrEmpty(bench.BestNode))
                {
                    if (manual)
                    {
                        string why = bench != null && !string.IsNullOrEmpty(bench.Error) ? bench.Error : "未找到可用跳板";
                        SetStatus("跳板优选未完成：" + why, Theme.Warn);
                        Toast("固定 IP 跳板优选失败", why, Theme.Warn, 3);
                    }
                    return;
                }

                string curHop = !string.IsNullOrEmpty(curHopBefore)
                    ? curHopBefore
                    : await Task.Run(() => StaticProxy.ReadLiveHopNode(cfgPath));
                int curBenchMs = _staticDelayMs;
                if (!string.IsNullOrEmpty(curHop))
                {
                    var matchCur = bench.TopCandidates.FirstOrDefault(x =>
                        x != null && string.Equals(x.NodeName, curHop, StringComparison.OrdinalIgnoreCase));
                    if (matchCur != null && matchCur.DelayMs > 0) curBenchMs = matchCur.DelayMs;
                }

                bool wasAuto = string.Equals(cfg.HopMode, StaticProxy.HopAuto, StringComparison.OrdinalIgnoreCase);
                bool switchIt = !wasAuto || StaticProxy.ShouldSwitchAutoHop(
                    curHop, curBenchMs, bench.BestNode, bench.BestDelayMs,
                    emergencyRecovery || _staticTimedOut, manual);

                var updated = cfg.Clone();
                if (manual) updated.HopMode = StaticProxy.HopAuto;
                updated.AutoBestUpdatedAtTicks = DateTime.UtcNow.Ticks;
                updated.AutoBestHopPool = bench.TopCandidates
                    .Where(x => x != null && !string.IsNullOrEmpty(x.NodeName))
                    .Select(x => x.NodeName)
                    .ToList();

                string jitterTag = bench.BestJitterMs > 0 ? (" · 抖动 " + bench.BestJitterMs + "ms") : "";
                if (switchIt)
                {
                    updated.AutoBestHopNode = bench.BestNode;
                    updated.AutoBestDelayMs = bench.BestDelayMs;
                    string saveErr;
                    StaticProxy.Save(updated, out saveErr);

                    string swErr = "";
                    bool hotSwitched = wasAuto && await Task.Run(() => StaticProxy.SwitchHopNodeInKernel(bench.BestNode, cfgPath, out swErr));
                    if (!hotSwitched)
                    {
                        // 首次由 follow/pin 切至 auto，或新节点尚未在 PP-STATIC-HOP 成员表中：重推一次完整配置立即生效
                        if (_state.ConfigOk && !_busy) SaveAsync();
                    }
                    _staticLiveHop = bench.BestNode;
                    _staticDelayMs = bench.BestDelayMs;
                    _staticTimedOut = false;
                    _staticTimeoutStreak = 0;
                    _staticSlowStreak = 0;

                    string cleanBest = CleanEmoji(bench.BestNode);
                    AuditLog.Record("STATIC",
                        (emergencyRecovery ? "固定 IP 前置故障自愈切换至：" : "固定 IP 稳速优选切换前置跳板：") +
                        bench.BestNode + "（" + bench.BestDelayMs + "ms" + jitterTag + "，" + bench.ReachableCount + "/" + bench.TestedCount + " 可达）");
                    SetStatus("固定 IP 已切至稳速最快跳板「" + cleanBest + "」（" + bench.BestDelayMs + "ms" + jitterTag + "，外部 IP 不变）", Theme.Ok);
                    if (manual || emergencyRecovery)
                    {
                        Toast(emergencyRecovery ? "固定 IP 跳板已自愈切换" : "固定 IP 已切换稳速最快跳板",
                            "独立前置：" + cleanBest + "（" + bench.BestDelayMs + "ms" + jitterTag + " · " + bench.ReachableCount + "/" + bench.TestedCount + " 可达），主 VPN 节点不受影响",
                            Theme.Ok, 3);
                    }
                }
                else
                {
                    if (string.IsNullOrEmpty(updated.AutoBestHopNode))
                        updated.AutoBestHopNode = string.IsNullOrEmpty(curHop) ? bench.BestNode : curHop;
                    int keptMs = curBenchMs > 0 ? curBenchMs : bench.BestDelayMs;
                    if (keptMs > 0)
                    {
                        updated.AutoBestDelayMs = keptMs;
                        _staticDelayMs = keptMs;
                    }
                    _staticSlowStreak = 0;
                    string saveErr;
                    StaticProxy.Save(updated, out saveErr);
                    if (!string.IsNullOrEmpty(curHop)) _staticLiveHop = curHop;
                    string cleanCur = CleanEmoji(string.IsNullOrEmpty(curHop) ? bench.BestNode : curHop);
                    AuditLog.Record("STATIC", "固定 IP 定时稳速巡检完成：当前跳板「" + cleanCur + "」保持最优（" + keptMs + "ms，" + bench.ReachableCount + "/" + bench.TestedCount + " 可达）");
                    if (manual)
                    {
                        SetStatus("当前固定 IP 跳板「" + cleanCur + "」已是最稳最优（" + keptMs + "ms" + jitterTag + "，" + bench.ReachableCount + "/" + bench.TestedCount + " 可达）", Theme.Ok);
                        Toast("当前固定 IP 跳板已是最优", cleanCur + " · " + keptMs + "ms（主 VPN 节点互不干扰）", Theme.Ok, 2);
                    }
                }
            }
            catch (Exception ex)
            {
                if (manual && UiAlive)
                    Toast("固定 IP 跳板优选异常", ex.Message, Theme.Warn, 2);
            }
            finally
            {
                _staticOptimizing = false;
                if (UiAlive) SyncStaticUiBanner();
            }
        }

        async Task ReconfirmStaticProxyHealthAfterDelayAsync()
        {
            try
            {
                await Task.Delay(12000);
                if (!UiAlive || _shuttingDown || !StaticProxy.IsEnabledAndValid || _staticTimeoutStreak <= 0) return;
                await CheckStaticProxyHealthAsync(false);
            }
            catch { }
        }

        public string DiagnosticsText() { return DiagnosticsText(_traffic); }

        /// <summary>面板与托盘共用的完整诊断文本：环境 + 出口核对 + 最近动作，统一脱敏。</summary>
        public string DiagnosticsText(Traffic.Sample traffic)
        {
            string text = Discovery.Diagnose();
            // 出口核对：以 mihomo 返回的真实链路为准，逐条列「预期 / 实际 / 判定」。
            if (traffic != null && traffic.Ok)
            {
                text += "\n---- 出口核对（以内核连接链路为准）----\n" + traffic.DescribeOutbound();
                if (traffic.Estimated > 0)
                    text += "\n注：本轮有 " + traffic.Estimated +
                            " 条连接读不出链路，速率含按名单估算的部分（面板显示「代理≈」）。";
            }
            text += "\n注：顶部「默认出口」取自默认代理策略组（无 PP-FIXED 时取 GLOBAL），" +
                    "不一定等于每个勾选软件各自使用的策略组；逐条实际链路以上面的出口核对为准。";
            // 自动挑组是启发式的，把「这次到底选了哪个组、是不是猜的」写进诊断，
            // 用户能检查；想固定就在订阅里建一个 PP-FIXED 组（独立审阅第二轮）。
            try
            {
                string cfgPath = Paths.ClashYaml;
                if (!string.IsNullOrEmpty(cfgPath) && System.IO.File.Exists(cfgPath))
                {
                    bool reused;
                    string group = Discovery.FindPolicy(System.IO.File.ReadAllText(cfgPath), out reused);
                    text += "\n代理策略组：" + group +
                            (reused ? "（沿用订阅里已有的 PP-FIXED）"
                                    : "（自动挑选；想固定请在订阅里建一个 PP-FIXED 组）");
                }
            }
            catch { }
            string recent = AuditLog.Recent(10);
            if (recent.Length > 0) text += "\n---- 最近动作 ----\n" + recent;
            return Redact(text);
        }

        void CopyDiagnostics()
        {
            // 诊断要连发多个控制请求，内核卡住时不能把界面一起冻住（评审 R3-8）。
            Traffic.Sample snap = _traffic;
            Task.Run(() => DiagnosticsText(snap)).ContinueWith(t =>
            {
                var taskEx = t.IsFaulted ? t.Exception : null;   // 防止窗体销毁后漏掉 Task 异常
                if (!UiAlive) return;
                try
                {
                    BeginInvoke(new Action(() =>
                    {
                        if (taskEx != null)
                        {
                            string msg = taskEx.GetBaseException().Message;
                            Toast("复制失败", msg, Theme.Warn, 1);
                            return;
                        }
                        Clipboard.SetText(t.Result);
                        Toast("诊断信息已复制（已脱敏）",
                            "用户名、路径、节点名、订阅链接已打码，可直接贴到 Issue；完整诊断用 --diag",
                            Theme.Ok, 0);
                    }));
                }
                catch (Exception ex) { try { Toast("复制失败", ex.Message, Theme.Warn, 1); } catch { } }
            });
        }

        /// <summary>脱敏规则下沉到 Core.Redactor：托盘、面板、崩溃日志共用同一套。</summary>
        static string Redact(string text) { return Core.Redactor.Apply(text); }

        void UnhideAll()
        {
            Settings.UnhideAll();
            RebuildVisible(true);
            Toast("已恢复隐藏项", "被隐藏的软件回到列表了", Theme.Ok, 0);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            _mismatchRect = RectangleF.Empty;
            _delayRect = RectangleF.Empty;
            _modeChipRect = RectangleF.Empty;
            LayoutChildren();
            Invalidate();
        }

        protected override void OnResizeEnd(EventArgs e)
        {
            base.OnResizeEnd(e);
            if (_restoringSize || WindowState != FormWindowState.Normal) return;
            if (ClientSize.Width > 0 && ClientSize.Height > 0)
                Settings.SetWindowSize((int)Math.Round(LogicalW), (int)Math.Round(LogicalH));
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_resourcesDisposed)
            {
                _resourcesDisposed = true;
                _clock.Stop(); _clock.Dispose();
                _trafficTimer.Stop(); _trafficTimer.Dispose();
                _watchTimer.Stop(); _watchTimer.Dispose();
                DisposeFonts();
                try { if (_timerRaised) { timeEndPeriod(1); _timerRaised = false; } } catch { }
                if (_windowIcon != null) _windowIcon.Dispose();
                if (_windowIconBig != null) _windowIconBig.Dispose();
                if (_titleIcon != null) _titleIcon.Dispose();
            }
            base.Dispose(disposing);
        }

        void OnFormKeyDown(object sender, KeyEventArgs e)
        {
            if (_shuttingDown) return;
            if (_help)
            {
                if (e.KeyCode == Keys.Escape || e.KeyCode == Keys.F1) { CloseHelp(); e.SuppressKeyPress = true; }
                return;
            }
            if (e.Control && e.KeyCode == Keys.W)
            { MinimizeToTray(); e.SuppressKeyPress = true; return; }
            if (e.Control && (e.KeyCode == Keys.D1 || e.KeyCode == Keys.NumPad1))
            { SwitchPage(0); e.SuppressKeyPress = true; return; }
            if (e.Control && (e.KeyCode == Keys.D2 || e.KeyCode == Keys.NumPad2))
            { SwitchPage(1); e.SuppressKeyPress = true; return; }
            if (e.Control && (e.KeyCode == Keys.D3 || e.KeyCode == Keys.NumPad3))
            { SwitchPage(2); e.SuppressKeyPress = true; return; }

            if (_page == 1)
            {
                if (e.Control && e.KeyCode == Keys.F) { _nodesPage.FocusSearch(); e.SuppressKeyPress = true; return; }
                if (e.KeyCode == Keys.F1) { OpenHelp(); e.SuppressKeyPress = true; return; }
                if (e.KeyCode == Keys.Escape)
                {
                    if (_nodesPage.SearchFocused) return;
                    if (_pageHistory.Count > 0) NavigateBack();
                    else SwitchPage(0, true);
                    e.SuppressKeyPress = true;
                    return;
                }
                return;
            }
            if (_page == 2)
            {
                if (e.KeyCode == Keys.F1) { OpenHelp(); e.SuppressKeyPress = true; return; }
                if (e.KeyCode == Keys.Escape)
                {
                    if (!_qualityPage.GoBack())
                    {
                        if (_pageHistory.Count > 0) NavigateBack();
                        else SwitchPage(0, true);
                    }
                    e.SuppressKeyPress = true;
                    return;
                }
                return;
            }

            // 搜索框聚焦时，全局快捷键不能吞掉输入：空格也要能打进 "Visual Studio"，
            // 更不能按一下就切换列表里选中行的勾选（评审 R3-4）。
            if (_search != null && _search.Focused)
            {
                if (e.KeyCode == Keys.Escape)
                { _search.Text = ""; e.SuppressKeyPress = true; return; }  // 只清空，不收起面板
                if (e.Control && e.KeyCode == Keys.S) { SaveAsync(); e.SuppressKeyPress = true; return; }
                return;
            }

            if (e.KeyCode == Keys.Escape)
            {
                if (!string.IsNullOrEmpty(_search.Text)) { _search.Text = ""; e.SuppressKeyPress = true; return; }
                return;
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
