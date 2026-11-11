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

        enum Btn { None, Save, Direct, Refresh, Close, Help, Sort, HelpClose, HelpDiag, HelpUnhide, HelpMode, HelpIpv6, Min, HelpUpdate, Mismatch }
        Btn _hover = Btn.None;
        int _tab;
        int _hoverTab = -1;
        bool _busy;
        bool _polling;
        bool _checkingOutbound;
        bool _resourcesDisposed;
        bool _timerRaised;   // 已把系统定时器精度提到 1ms，退出时要还回去
        int _revision;
        readonly Icon _windowIcon;      // 小图标（任务栏按钮 / Alt+Tab）
        readonly Icon _windowIconBig;   // 大图标（Alt+Tab 预览、任务视图）
        readonly Bitmap _titleIcon;
        string _lastFailure;
        DateTime _lastFailureAt;
        bool _realExit;
        bool _help;
        bool _modeChanged;   // 在帮助面板里切过分流模式，关闭时提醒去保存
        bool _ipv6Changed;   // 在帮助面板里切过 IPv6 防泄漏，关闭时提醒去保存
        Updater.Info _update;      // null = 还没查过；查过之后失败也留着，用来解释为什么没提示
        string _lastEnvWarn;       // 上次弹过的多客户端提醒文字；同一条不重复弹
        bool _updateBusy;
        bool _updateSessionDone;  // 本次运行已经自动查过一次，别每次打开面板都去问
        string _notifiedVersion;  // 本次运行已为哪个版本弹过提示，避免联网复查重复打扰
        // 真正推给内核的代理名单（含 helper / 登录窗口 / 本程序自身等派生规则）。
        // 对账的「预期」必须用它，只用界面勾选会把自己放行的进程报成异常（实机 2026-11-10）。
        volatile HashSet<string> _effectiveProxy;

        /// <summary>点了「—」之后由托盘那边弹一次气泡，告诉用户窗口去哪了。首次才弹。</summary>
        public Action TrayHint;
        /// <summary>发现新版本时由托盘弹系统通知；窗口收在托盘里也看得见。</summary>
        public Action<string, string> TrayBalloon;

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
        // 界面显示值与内核采样值分开：采样每秒跳一次，显示值按时间连续逼近，
        // 数字不再「0 → 800 KB/s → 0」地瞬间变脸。
        double _rateProxyDown, _rateProxyUp, _rateDirectDown, _rateDirectUp;
        readonly List<float> _sparkProxy = new List<float>();
        readonly List<float> _sparkDirect = new List<float>();
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
        bool _blindToasted;

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
            ClientSize = new Size(Theme.Px(WinW), Theme.Px(WinH));
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
                // 复审 P2：Esc 不再在这里收起面板，只由窗体级处理「清空搜索」。
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

        const int WM_SETICON = 0x0080;
        const int ICON_SMALL = 0;
        const int ICON_BIG = 1;

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try { int round = 2; DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int)); } catch { }
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
        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg != WM_DPICHANGED || IsDisposed) return;
            try
            {
                int dpi = (int)(m.WParam.ToInt64() & 0xFFFF);
                if (dpi < 72) return;
                Theme.SetScale(dpi / 96f);
                BuildFonts();
                _search.Font = _fInput;
                ClientSize = new Size(Theme.Px(WinW), Theme.Px(WinH));
                LayoutChildren();
                Invalidate();
            }
            catch { }
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

            if (_toasts.Tick((float)dt)) need = true;

            bool trafficAlive = Visible && !_help && _traffic != null && _traffic.Ok;
            if (trafficAlive)
            {
                // 内核数据仍然是一秒一个真实平均值，这里只平滑「显示」，不伪造新采样。
                // 220ms 时间常数既能去掉台阶，也不会把真实变化拖成慢动作。
                _rateProxyDown = Approach(_rateProxyDown, _traffic.ProxyDown, dt, 0.22);
                _rateProxyUp = Approach(_rateProxyUp, _traffic.ProxyUp, dt, 0.22);
                _rateDirectDown = Approach(_rateDirectDown, _traffic.DirectDown, dt, 0.22);
                _rateDirectUp = Approach(_rateDirectUp, _traffic.DirectUp, dt, 0.22);
                summaryNeed = true;
            }

            bool sparkAlive = Visible && !_help && _sparkProxy.Count >= 2;
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
            else if (!sparkAlive) _clock.Stop();
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
            Theme.Load();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Show(); BringToFront(); Activate(); _search.Focus();
            if (_all.Count == 0 || (DateTime.UtcNow - _lastLoad).TotalSeconds > 20) RefreshAsync();
            else { ShowDirtyStatus(); RebuildVisible(false); }
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
                // 名单不是空的、也没报错，但规则不见了 —— 只可能是客户端自己重新生成了配置
                // （换订阅、重启 Verge、改设置都会）。这时光提示「请保存一次」没用，
                // 用户会以为软件坏了或者代理失效了，干脆替他补回去。
                needRepair = !_state.Applied && _state.Error == null
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
                var wantedItems = await Task.Run(() => ComposeWithHelpers(exes, visibleForAudit, pathsForAudit));
                var wantedAll = wantedItems.Select(i => i.IsPathRule ? i.PathRegex : i.Exe).ToList();
                var auditPolicies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var r in _all)
                    if (r.Proxy && !string.IsNullOrEmpty(r.Policy)) auditPolicies[r.Exe] = r.Policy;
                var audit = await Task.Run(() => Guard.Audit(wantedAll, auditPolicies, watchCfg));
                if (!UiAlive || revision != _revision) return;

                // 用户刚在帮助面板切了模式但还没点保存，看守不能替他把新模式推下去（评审 R4-2）。
                if (_modeChanged) return;

                // 全局模式 / 关掉进程识别时规则根本不参与匹配，自动重推没有意义，只提示。
                if (audit.Ok && !string.IsNullOrEmpty(audit.Mode) &&
                    !audit.Mode.Equals("rule", StringComparison.OrdinalIgnoreCase))
                {
                    SetStatus("客户端当前是「" + audit.Mode + "」模式，按软件分流不生效；请切回规则模式", Theme.Warn);
                    return;
                }
                // mihomo：always=强制查进程、strict=按需查（默认，PROCESS 规则正常生效）、off=关闭。
                // 只有 off 才该放弃补回；以前把 strict 也当成没开，默认 Verge Rev 下规则永远补不回来（F1）。
                if (audit.Ok && !string.IsNullOrEmpty(audit.FindProcess) &&
                    audit.FindProcess.Equals("off", StringComparison.OrdinalIgnoreCase))
                {
                    SetStatus("客户端关掉了进程识别（find-process-mode=off），按软件分流不生效", Theme.Warn);
                    return;
                }

                string why = Guard.Judge(audit, wantedItems.Count, patch);

                // IPv6 归客户端的应用设置管：默认完全不碰，也不据此触发自动重推。
                // 只有用户在帮助面板里主动开了「IPv6 防泄漏」，才把它算作需要补回的事。
                bool ipv6Guard = Settings.Ipv6GuardOn;
                if (ipv6Guard && audit.Ok && !audit.Ipv6On) _ipv6Attempts = 0;
                if (ipv6Guard && why == null && audit.Ok && audit.Ipv6On && _ipv6Attempts < 3)
                    why = "已开启 IPv6 防泄漏，但内核 IPv6 仍开着";

                if (why == null) { _repairFails = 0; return; }
                if ((DateTime.UtcNow - _lastRepairAt).TotalSeconds < 30) return;
                if (_repairFails >= 3) return;

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
                if (!UiAlive || _busy) return;
                if (_modeChanged) return;   // 模式切换还没保存，任何自动补回都不能推新模式
                // 推送前最后确认一次意图：托盘还原会删掉名单文件，
                // 已启动的旧任务不能因为它重新创建接管状态。
                if (!Profile.HasSavedIntent) return;
                owns = true;
                BeginBusy();
                _lastRepairAt = DateTime.UtcNow;
                _repairFails++;

                var visible = VisibleExes();
                var paths = PathMap();
                var res = await Task.Run(() => Profile.Apply(ComposeWithHelpers(exes, visible, paths), true));
                if (!UiAlive) return;
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
            // 面板从没打开过时 _all 是空的（开机就缩在托盘）。这时不能只补进程名，
            // 否则目录规则仍缺失、界面却报「已补回」——现扫一次拿路径（复审 F-09）。
            if (map.Count == 0)
            {
                try
                {
                    foreach (var kv in Scanner.Scan())
                    {
                        if (kv.Value == null || string.IsNullOrEmpty(kv.Value.Path)) continue;
                        if (!map.ContainsKey(kv.Key)) map[kv.Key] = kv.Value.Path;
                    }
                }
                catch { }
            }
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
            try
            {
                foreach (var kv in Scanner.Scan())
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

            // 整树覆盖：只对「能确定是这个软件自己的安装目录」生成目录规则。
            // 公共容器（Program Files / AppData / 桌面 / 下载）会被 AppGroup 拒掉，
            // 不会因为勾了一个软件把整台机器的程序都代理走。
            List<string> roots = null;
            try
            {
                if (paths != null && !Profile.PathRulesRejected && AppGroup.KernelSupportsPathRules())
                {
                    var chosen = new List<string>();
                    foreach (var exe in proxy)
                    {
                        string path;
                        if (paths.TryGetValue(exe, out path)) chosen.Add(path);
                    }
                    roots = AppGroup.Roots(chosen);
                }
            }
            catch { roots = null; }

            var items = Profile.Compose(proxy, null, peers, roots, policies);

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

            // 「启动自检更新」是本程序自己发的那个 GET。完全接管下没勾的一律落到
            // MATCH,DIRECT，GitHub 连不上就等于没有这个功能；所以只给本程序放行这一条。
            // 关掉 settings.json 里的 updateCheck 即不再出网，规则也会自动消失。
            if (!Settings.PatchOnly && proxy.Count > 0 && Settings.Current.UpdateCheck &&
                !items.Any(i => i.Exe.Equals("proxypicker.exe", StringComparison.OrdinalIgnoreCase)))
            {
                items.Add(new Profile.Item("proxypicker.exe", "PP-FIXED") { Derived = true });
            }

            // 对账的「预期」必须是真正下发的东西：helper、登录窗口、本程序自身
            // 都是派生规则，只用界面勾选会把它们报成「没勾选却走代理」。
            var eff = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var it in items) if (!it.IsPathRule) eff.Add(it.Exe);
            _effectiveProxy = eff;

            return items;
        }

        sealed class Pack
        {
            public Profile.KernelState State;
            public List<AppRow> Rows;
        }

        static Pack LoadPack()
        {
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
                var now = new HashSet<string>(proxy, StringComparer.OrdinalIgnoreCase);
                var changed = now.Where(x => !_savedProxy.Contains(x))
                    .Concat(_savedProxy.Where(x => !now.Contains(x))).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var visible = VisibleExes();
                var paths = PathMap();
                var policies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var r in _all)
                    if (r.Proxy && !string.IsNullOrEmpty(r.Policy)) policies[r.Exe] = r.Policy;
                var pack = await Task.Run(() =>
                {
                    var p = new SavePack();
                    p.Snapshot = Profile.CaptureConnections(changed); // 先快照旧连接，再推新规则
                    p.Apply = Profile.Apply(ComposeWithHelpers(proxy, visible, paths, policies));
                    if (p.Apply.Ok && changed.Count > 0 && p.Snapshot.Error == null)
                        p.Reconnect = Profile.CloseCapturedConnections(p.Snapshot);
                    return p;
                });
                if (!UiAlive) return;

                // 推送成功就把内存名单对齐（外部评审 F4）：回读校验失败时内核其实已经改了，
                // 若仍拿旧名单去对账，看守会把用户刚取消勾选的软件又补回来。
                // 收尾批 P3-2：但「名单没写进磁盘」是例外——此时对齐内存会让看守拿一份
                // 磁盘上并不存在的名单去对账。保持旧内存名单，下一轮看守自然把内核补回
                // 磁盘那份，状态重新一致；界面也会明确提示本次改动没有落盘。
                if (pack.Apply.Pushed && !pack.Apply.PersistFailed)
                {
                    _savedProxy = now;
                    _list.SavedProxy = now;
                }
                if (pack.Apply.Ok)
                {
                    _modeChanged = false;   // 模式切换直到这次保存成功才算落地
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
        }

        /// <summary>
        /// 托盘菜单是命令：先关帮助面板、避开忙碌，再清空并直接保存。
        /// 复审 P1：以前撞上 _help / _busy 守卫会静默不保存，且提示仍说「记得点保存」。
        /// </summary>
        public void AllDirectPublic()
        {
            if (!UiAlive) return;
            if (_help) CloseHelp();
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
            menu.Show(this, new Point(Theme.Px(388), Theme.Px(TabsTop + 30)));
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
                if (!UiAlive || !Visible || _polling) return;
                ownsPoll = true;
                _polling = true;
                // 流量采样与「刷新软件列表」互不阻塞；否则刷新期间曲线必然断一拍。
                // 每十次采样顺手检查出口（约 10 秒），两个请求各自防重入。
                if (++_outTick % 10 == 0) CheckOutbound();
                // 「预期」用真正推给内核的名单（含派生规则），不是界面上勾了几个。
                // 首次保存前还没有下发记录，才退回界面勾选。
                var effective = _effectiveProxy;
                var set = effective != null && effective.Count > 0
                    ? effective
                    : new HashSet<string>(_all.Where(r => r.Proxy).Select(r => r.Exe), StringComparer.OrdinalIgnoreCase);
                // 只有「代理白名单」模式下预期出口才明确：订阅兼容里未勾选的软件
                // 按订阅规则走代理是合法的，判成异常反而会一直误报。
                var s = await Task.Run(() => Traffic.Poll(set, !Settings.PatchOnly));
                if (!UiAlive || !Visible) return;
                _traffic = s;
                if (s.Ok) ApplyCoverage(s);
                Wake();   // 曲线是每帧推进的，采样到点后要把时钟叫醒（失败也要，否则转圈会僵住）
                if (s.Ok && s.Seconds > 0)
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
        void ApplyCoverage(Traffic.Sample s)
        {
            var now = DateTime.UtcNow;
            foreach (var kv in s.Seen) _seenAt[kv.Key] = now;
            _visibleProcs = s.Seen.Count;
            _mismatch = s.Mismatch;
            _mismatchItems = s.Mismatches ?? new List<Traffic.MismatchItem>();

            int covered = 0;
            foreach (var r in _all)
                if (r.Proxy && s.Seen.ContainsKey(r.Exe)) covered++;
            _coveredProcs = covered;

            // 观察期不足 90 秒不下"看不见"的结论：软件可能只是还没发请求
            bool judged = (now - _startedAt).TotalSeconds >= 90;
            bool canBeBlind = !_state.Tun;   // 只有系统代理底座才可能真的漏掉进程

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
                foreach (var r in _all)
                {
                    if (!r.Proxy || r.Coverage != 2) continue;
                    _blindToasted = true;
                    Toast("有软件勾了却没生效",
                        "它不读系统代理，流量没进内核 —— 切 TUN，或在它自己的设置里填 127.0.0.1:7897",
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

        void Toast(string title, string sub, Color accent, int kind)
        {
            _toasts.Show(title, sub, accent, kind);
            Wake();
            _list.Invalidate();
        }

        // ---------------------------------------------------------------- 布局

        RectangleF SummaryRect { get { return new RectangleF(18, SummaryTop, WinW - 36, SummaryH); } }

        /// <summary>摘要卡第二行。有分流异常时整行可点击，打开完整明细。</summary>
        RectangleF MismatchRect { get { return new RectangleF(32, SummaryTop + 33, 205, 16); } }
        RectangleF SparkRect
        {
            get
            {
                float rx = 244, rw = WinW - 18 - 12 - rx;
                return new RectangleF(rx, SummaryTop + 45, rw, 26);
            }
        }
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
        RectangleF HelpDiagRect { get { return new RectangleF(44, HelpCard.Bottom - 52, 94, 32); } }
        RectangleF HelpUnhideRect { get { return new RectangleF(142, HelpCard.Bottom - 52, 94, 32); } }
        RectangleF HelpModeRect { get { return new RectangleF(240, HelpCard.Bottom - 52, 104, 32); } }
        RectangleF HelpIpv6Rect { get { return new RectangleF(348, HelpCard.Bottom - 52, 86, 32); } }
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

            if (_titleIcon != null) g.DrawImage(_titleIcon, 18f, 14f, 24f, 24f);
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
            // _state 已在后台 RefreshAsync 里填好；绘制函数不能再做进程/磁盘探测，
            // 否则 30fps 下每一次曲线重绘都在重复扫描客户端。
            if (!string.IsNullOrEmpty(_state.Client)) return _state.Client + " · 按软件分流";
            if (!string.IsNullOrEmpty(_state.Error)) return "还没找到 Clash / mihomo";
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
            // 第二行顺便报出「内核这一轮看得到几个进程」。
            // 这个数字是本工具存在感的唯一实证：TUN 下它≈全部在跑的软件，
            // 系统代理下会明显变小 —— 变小就意味着有软件我们管不到。
            string rest = Settings.PatchOnly ? "其余按订阅规则" : "其余软件全部直连";
            // 预期出口与实测链路不一致时，优先把这句话摆出来：
            // 「勾了却没生效」比「现在有几个进程可见」重要得多。
            if (_mismatch > 0)
            {
                // 异常一多，把名字往这一行里塞必然显示不全（用户实机反馈）。
                // 改成「数量 + 可点击入口」：完整清单在 ShowMismatchDialog 里逐条列出，
                // 帮助面板和「复制诊断」也都能拿到全量，不再用「… 等」糊过去。
                rest = "分流异常 " + _mismatch + " 个 · 点这里看全部";
            }
            else if (_visibleProcs > 0) rest += " · 可见 " + _visibleProcs + " 个";
            Draw.Text(g, rest, _fSmall, _mismatch > 0 ? Theme.Warn : Theme.Dim, MismatchRect);

            // 第三行：左边内核状态，右边用胶囊直接摆出「勾选软件实际走哪个节点」。
            //
            // 为什么单独立一格：PP-FIXED 指向主选择组后，真正生效的是这条链路末端，
            // 而 GLOBAL 的选择可能完全不是同一个节点。以前只显示 GLOBAL，
            // 用户会以为「我明明换了节点」，界面却纹丝不动 —— 看起来就像贴图。
            // 底座只有两种：TUN 全部接管，或系统代理只能覆盖一部分。
            // 这里直接写结论而不是"TUN 开/关"，因为开关本身没人关心，覆盖范围才关心。
            string meta = _state.Tun ? "TUN · 全部接管" : "系统代理 · 部分失效";
            Color metaColor = _state.Tun ? Theme.Dim : Theme.Warn;
            string outb = string.IsNullOrEmpty(_state.Outbound) ? _state.Node : _state.Outbound;
            if (string.IsNullOrEmpty(outb))
            {
                Draw.Text(g, meta, _fTiny, metaColor, new RectangleF(32, SummaryTop + 54, 200, 16));
            }
            else
            {
                Draw.Text(g, meta, _fTiny, metaColor, new RectangleF(32, SummaryTop + 54, 96, 16));

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
                "↓ " + Traffic.Speed(_rateProxyDown) + "   ↑ " + Traffic.Speed(_rateProxyUp),
                _fRate, Theme.Accent, new RectangleF(rx + 28, SummaryTop + 4, rw - 28, 16), Draw.Right);

            Draw.Text(g, "直连", _fTiny, Theme.Dim, new RectangleF(rx, SummaryTop + 25, 26, 15));
            Draw.Text(g, _traffic == null || !_traffic.Ok ? "—" :
                "↓ " + Traffic.Speed(_rateDirectDown) + "   ↑ " + Traffic.Speed(_rateDirectUp),
                _fRate, Theme.Dim, new RectangleF(rx + 28, SummaryTop + 24, rw - 28, 16), Draw.Right);

            var sparkRect = SparkRect;
            var sparkAt = _sparkAt.ToArray();
            if (_sparkProxy.Count >= 2 && sparkAt.Length == _sparkProxy.Count)
                Draw.Spark(g, _sparkProxy.ToArray(), sparkAt, _sparkAxis, SparkPeriodMs, SparkPoints,
                    sparkRect, Theme.Accent, _sparkPulse);
            if (_sparkDirect.Count >= 2 && sparkAt.Length == _sparkDirect.Count)
                Draw.Spark(g, _sparkDirect.ToArray(), sparkAt, _sparkAxis, SparkPeriodMs, SparkPoints,
                    sparkRect, Theme.Dim, _sparkPulse);

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

            string firstTitle = "开 = 走代理，关 = 直连";
            string firstBody = "改完点「保存并生效」，只重连改到的软件。网速是每秒采样估值，短连接可能少算。";
            if (_mismatch > 0)
            {
                // 把「哪个软件、什么原因、下一步」直接摆在帮助面板第一屏，
                // 不再让用户自己去猜「分流异常 1 个」是哪一个。
                var lines = new List<string>();
                foreach (var m in _mismatchItems.Take(3))
                    lines.Add(m.Exe + "：预期" + (m.Expected ? "代理" : "直连") + "，实际 "
                        + m.Proxy + " 代理 / " + m.Direct + " 直连。" + m.Reason + "；" + m.Action);
                if (_mismatchItems.Count > 3)
                    lines.Add("还有 " + (_mismatchItems.Count - 3) + " 个，回主界面点「分流异常」看完整清单。");
                firstTitle = "分流异常 " + _mismatch + " 个（主界面可点开全部）";
                firstBody = string.Join("  ", lines.ToArray());
            }

            string[][] items =
            {
                new[] { firstTitle, firstBody },
                new[] { "四个标签只是在换视角", "常用只放高频和已选软件；找不到就切「全部」（标签上有总数）或在搜索框输名字。绿色版、MSIX、脚本类软件可能扫不到，托盘菜单「手动添加软件…」可手选 exe，加过一次会一直留着。" },
                new[] { "顺序可以拖", "按住任意一行上下拖动即可排序；点标签右侧按钮，能切回「智能」按使用习惯自动排。" },
                new[] { "右键有更多", "看完整路径、复制进程名；同名冲突和相关辅助进程会提示，也能一键一起勾上。" },
                new[] { "换电脑直接用", "默认「代理白名单」：只有勾的走代理，其余直连；想按订阅分流点下面切「订阅兼容」。登录窗口（WebView2）已自动放行，避免 ChatGPT 登不上。每次启动自动查一次新版本，有新版会弹托盘通知；要关掉把 settings.json 的 loginWindowProxy / updateCheck 设为 false。" }
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

            // 底部四个按钮：诊断 / 恢复隐藏 / 分流模式 / IPv6 防泄漏。
            // 后两个会改全局行为，开启时用强调色描边，跟一次性的动作区分开。
            var line = new RectangleF(44, card.Bottom - 68, card.Width - 40, 1);
            using (var pen = new Pen(Theme.Line)) g.DrawLine(pen, line.X, line.Y, line.Right, line.Y);

            using (var font = Theme.Ui(9.5f))
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

            if (_mismatch > 0 && MismatchRect.Contains(x, y)) return Btn.Mismatch;
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
            if (b == Btn.Close) { Close(); return; }   // 走 OnFormClosing -> 还原配置 -> 真退出
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
            if (b == Btn.HelpIpv6) { ToggleIpv6Guard(); return; }
            if (b == Btn.HelpUpdate) { VersionClick(); return; }
            if (b == Btn.Mismatch) { ShowMismatchDialog(); return; }

            int tab = HitTab(e.Location);
            if (tab >= 0) { _tab = tab; Settings.Current.Tab = tab; Settings.Save(); RebuildVisible(true); return; }
            if (!_help && e.Y / Theme.S < TitleH)
            {
                ReleaseCapture(); SendMessage(Handle, WM_NCLBUTTONDOWN, new IntPtr(HTCAPTION), IntPtr.Zero);
            }
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
                sb.AppendLine("   下一步：" + m.Action);
                sb.AppendLine();
            }
            sb.AppendLine("统计只算本次规则生效后新建的连接；之前建立的长连接已排除，");
            sb.AppendLine("程序自动放行的辅助进程已按「走代理」计入预期，不会再误报。");
            sb.AppendLine("按 Ctrl+C 可复制这份清单；帮助面板的「复制诊断」还带内核状态。");
            MessageBox.Show(this, sb.ToString(), "分流异常明细（" + _mismatchItems.Count + " 个）",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
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
            if (!UiAlive || _busy) return;
            bool on = !Settings.Ipv6GuardOn;
            SetStatus(on ? "正在开启 IPv6 防泄漏…" : "正在关闭 IPv6 防泄漏…", Theme.Warn);
            Task.Run(() => Settings.SetIpv6Guard(on)).ContinueWith(t =>
            {
                if (!UiAlive) return;
                // 失败不能只吞掉异常还显示「已开启」（评审 R5-4）。
                if (t.IsFaulted)
                {
                    string msg = t.Exception == null ? "未知错误" : t.Exception.GetBaseException().Message;
                    try
                    {
                        BeginInvoke(new Action(() =>
                        {
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
                if (string.IsNullOrEmpty(url))
                    url = "https://github.com/" + Updater.RepoSlug + "/releases";
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
        public void AfterRollback()
        {
            if (!UiAlive) return;
            // 作废所有在途看守结果：还原那一刻可能已有一次对账跑到一半，
            // 不改 revision 的话它会按旧结论把刚还原的规则重新推回去（外部评审 P0）。
            _revision++;
            _savedProxy = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _list.SavedProxy = _savedProxy;
            foreach (var r in _all) { r.Proxy = false; r.Known = false; }
            _state.Applied = false;
            _modeChanged = false;
            _autoRepaired = false;
            RebuildVisible(true);
            RefreshAsync();
        }

        public string DiagnosticsText() { return DiagnosticsText(_traffic); }

        /// <summary>面板与托盘共用的完整诊断文本：环境 + 出口核对 + 最近动作，统一脱敏。</summary>
        public string DiagnosticsText(Traffic.Sample traffic)
        {
            string text = Discovery.Diagnose();
            // 出口核对：以 mihomo 返回的真实链路为准，逐条列「预期 / 实际 / 判定」。
            if (traffic != null && traffic.Ok)
                text += "\n---- 出口核对（以内核连接链路为准）----\n" + traffic.DescribeOutbound();
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
                if (!UiAlive) return;
                try
                {
                    BeginInvoke(new Action(() =>
                    {
                        if (t.IsFaulted)
                        {
                            string msg = t.Exception == null ? "未知错误" : t.Exception.GetBaseException().Message;
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

        protected override void OnResize(EventArgs e) { base.OnResize(e); LayoutChildren(); }

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
            if (_help)
            {
                if (e.KeyCode == Keys.Escape || e.KeyCode == Keys.F1) { CloseHelp(); e.SuppressKeyPress = true; }
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
