using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using ProxyPicker.Core;

namespace ProxyPicker.UI
{
    /// <summary>
    /// 节点策略与故障转移页面：
    /// 响应式企业级排版（最小兼容 466px 经典侧边栏至 2400px 宽屏工作台）；
    /// 集中管理全量节点测速、风控摘要、备选池配置、超时自动切换以及上下文返回导航。
    /// </summary>
    internal sealed class NodesPage : Control
    {
        const int EM_SETCUEBANNER = 0x1501;

        [DllImport("user32.dll", EntryPoint = "SendMessageW", CharSet = CharSet.Unicode)]
        static extern IntPtr SendMessageText(IntPtr hWnd, int msg, int wParam, string lParam);

        readonly Timer _scrollTimer;
        readonly TextBox _search = new TextBox();
        readonly List<NodeManager.ProxyNodeInfo> _allNodes = new List<NodeManager.ProxyNodeInfo>();
        readonly List<NodeManager.ProxyNodeInfo> _viewNodes = new List<NodeManager.ProxyNodeInfo>();
        readonly Dictionary<string, NodeQuality.QualityReport> _riskCache =
            new Dictionary<string, NodeQuality.QualityReport>(StringComparer.OrdinalIgnoreCase);

        Font _fTitle, _fBold, _fReg, _fSmall, _fTiny, _fInput;
        bool _fontsDisposed;

        string _backLabel = "";
        string _groupName = "";
        string _currentOutbound = "";
        int _currentDelayMs = -1;
        bool _currentTimedOut;
        bool _proxyConnected = true;
        string _catalogError = "";
        string _lastFailoverSummary = "";
        bool _loadingCatalog;
        bool _batchTesting;
        int _batchDone;
        int _batchTotal;
        string _inspectingNode = "";

        int _filterTab;
        bool _sortByDelay;
        float _scroll;
        float _scrollTarget;
        bool _draggingThumb;
        float _dragStartY;
        float _dragStartScroll;

        string _hoverHit = "";
        int _hoverRow = -1;

        public Action<Graphics, RectangleF> Overlay;

        public event Action BackRequested;
        public event Action TestCurrentDelayRequested;
        public event Action ToggleBatchDelayRequested;
        public event Action<string> SwitchNodeRequested;
        public event Action<string> ProbeAndOpenQualityRequested;
        public event Action<string, bool> CandidateToggled;
        public event Action<List<string>> CandidateBatchChanged;
        public event Action<bool> AutoFailoverToggled;

        public NodesPage()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            TabStop = false;
            BackColor = Theme.Bg;
            BuildFonts();

            _search.BorderStyle = BorderStyle.None;
            _search.BackColor = Theme.Bg;
            _search.ForeColor = Theme.Fg;
            _search.Font = _fInput;
            _search.AutoSize = false;
            _search.TextChanged += (s, e) => { RebuildView(); Invalidate(); };
            _search.GotFocus += (s, e) => Invalidate();
            _search.LostFocus += (s, e) => Invalidate();
            _search.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Escape)
                {
                    if (!string.IsNullOrEmpty(_search.Text)) _search.Text = "";
                    else Focus();
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                }
            };
            Controls.Add(_search);

            _scrollTimer = new Timer { Interval = 16 };
            _scrollTimer.Tick += (s, e) => StepScroll();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try { SendMessageText(_search.Handle, EM_SETCUEBANNER, 1, "筛选节点名 / 地区…"); } catch { }
            LayoutChildren();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            LayoutChildren();
            ClampScroll();
        }

        void LayoutChildren()
        {
            var sb = SearchBoxRect;
            _search.SetBounds(Theme.Px(sb.X + 22f), Theme.Px(sb.Y + 4.5f),
                Math.Max(20, Theme.Px(sb.Width - 28f)), Theme.Px(18));
        }

        void BuildFonts()
        {
            DisposeFonts();
            _fTitle = Theme.Ui(13f, FontStyle.Bold);
            _fBold = Theme.Ui(11f, FontStyle.Bold);
            _fReg = Theme.Ui(11f);
            _fSmall = Theme.Ui(10f);
            _fTiny = Theme.Ui(9.2f);
            _fInput = Theme.Ui(10.5f);
            _fontsDisposed = false;
        }

        public void RefreshThemeAndFonts()
        {
            BackColor = Theme.Bg;
            BuildFonts();
            _search.BackColor = Theme.Bg;
            _search.ForeColor = Theme.Fg;
            _search.Font = _fInput;
            LayoutChildren();
            Invalidate();
        }

        void DisposeFonts()
        {
            if (_fTitle != null) _fTitle.Dispose();
            if (_fBold != null) _fBold.Dispose();
            if (_fReg != null) _fReg.Dispose();
            if (_fSmall != null) _fSmall.Dispose();
            if (_fTiny != null) _fTiny.Dispose();
            if (_fInput != null) _fInput.Dispose();
            _fTitle = _fBold = _fReg = _fSmall = _fTiny = _fInput = null;
            _fontsDisposed = true;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _scrollTimer.Stop();
                _scrollTimer.Dispose();
                if (!_fontsDisposed) DisposeFonts();
            }
            base.Dispose(disposing);
        }

        // ---------------------------------------------------------------- 状态同步

        public void SetBackLabel(string label)
        {
            _backLabel = label ?? "";
            LayoutChildren();
            ClampScroll();
            Invalidate();
        }

        public void SetLoading(bool loading)
        {
            _loadingCatalog = loading;
            Invalidate();
        }

        public void SetCatalog(NodeManager.SwitchableGroupInfo catalog)
        {
            _loadingCatalog = false;
            _allNodes.Clear();
            if (catalog == null)
            {
                _groupName = "";
                _currentOutbound = "";
                _proxyConnected = false;
                _catalogError = "未读取到策略组与节点信息";
            }
            else
            {
                _groupName = catalog.GroupName ?? "";
                _currentOutbound = !string.IsNullOrEmpty(catalog.CurrentOutbound)
                    ? catalog.CurrentOutbound : (catalog.CurrentSelection ?? "");
                _catalogError = catalog.Ok ? "" : (catalog.Error ?? "读取节点失败");
                if (!catalog.Ok) _proxyConnected = false;
                if (catalog.Nodes != null) _allNodes.AddRange(catalog.Nodes);
                if (catalog.Ok && _allNodes.Count > 0 && _riskCache.Count > 0)
                {
                    var valid = new HashSet<string>(_allNodes.Select(x => x.Name), StringComparer.OrdinalIgnoreCase);
                    foreach (var staleKey in _riskCache.Keys.Where(k => !valid.Contains(k)).ToList())
                        _riskCache.Remove(staleKey);
                }
                var cur = _allNodes.FirstOrDefault(n => n.IsCurrent);
                if (cur != null && cur.EffectiveDelayMs > 0)
                {
                    _currentDelayMs = cur.EffectiveDelayMs;
                    _currentTimedOut = false;
                }
            }
            RebuildView();
            Invalidate();
        }

        public void SetCurrentOutbound(string outbound, int delayMs = -1, bool timedOut = false, bool proxyConnected = true)
        {
            _currentOutbound = outbound ?? "";
            _currentDelayMs = delayMs;
            _currentTimedOut = timedOut;
            _proxyConnected = proxyConnected;
            foreach (var n in _allNodes)
            {
                n.IsCurrent = string.Equals(n.Name, _currentOutbound, StringComparison.OrdinalIgnoreCase);
                if (n.IsCurrent)
                {
                    if (timedOut) n.LiveDelayMs = -2;
                    else if (delayMs > 0) n.LiveDelayMs = delayMs;
                }
            }
            Invalidate();
        }

        public void SetLastFailoverSummary(string text)
        {
            _lastFailoverSummary = text ?? "";
            Invalidate();
        }

        public void SetInspectingNode(string nodeName)
        {
            _inspectingNode = nodeName ?? "";
            Invalidate();
        }

        internal int DebugRiskCacheCount { get { return _riskCache.Count; } }

        public void CacheQualityReport(NodeQuality.QualityReport rep)
        {
            if (rep != null && !string.IsNullOrEmpty(rep.TargetNode))
                _riskCache[rep.TargetNode] = rep;
            Invalidate();
        }

        public void SetBatchTesting(bool running, int done, int total)
        {
            _batchTesting = running;
            _batchDone = done;
            _batchTotal = total;
            Invalidate();
        }

        public void UpdateSingleNodeDelay(string nodeName, int liveDelayMs, bool? alive, string error)
        {
            if (string.Equals(nodeName, _currentOutbound, StringComparison.OrdinalIgnoreCase))
            {
                _currentDelayMs = liveDelayMs;
                _currentTimedOut = liveDelayMs == -2;
            }
            foreach (var n in _allNodes)
            {
                if (!string.Equals(n.Name, nodeName, StringComparison.OrdinalIgnoreCase)) continue;
                n.LiveDelayMs = liveDelayMs;
                if (liveDelayMs > 0) n.HistoryDelayMs = liveDelayMs;
                n.Alive = alive;
                n.LastError = error ?? "";
                break;
            }
            if (_sortByDelay) RebuildView();
            else Invalidate();
        }

        public List<NodeManager.ProxyNodeInfo> SnapshotConcreteNodes()
        {
            return _allNodes.Where(n => !n.IsGroup && !ProbeSidecar.IsInformationalNodeName(n.Name)).ToList();
        }

        public void ClearSearchIfAny()
        {
            if (!string.IsNullOrEmpty(_search.Text)) _search.Text = "";
        }

        public void FocusSearch()
        {
            if (Visible && _search.CanFocus) _search.Focus();
        }

        public bool SearchFocused { get { return _search.Focused; } }

        void RebuildView()
        {
            string q = (_search.Text ?? "").Trim();
            _viewNodes.Clear();
            foreach (var n in _allNodes)
            {
                if (_filterTab == 1 && !n.IsCandidate) continue;
                if (q.Length > 0 &&
                    n.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0 &&
                    n.Type.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0 &&
                    n.ExpectedRegion.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                _viewNodes.Add(n);
            }
            if (_sortByDelay)
            {
                _viewNodes.Sort((a, b) =>
                {
                    if (a.IsCurrent != b.IsCurrent) return a.IsCurrent ? -1 : 1;
                    int da = a.EffectiveDelayMs > 0 ? a.EffectiveDelayMs : int.MaxValue;
                    int db = b.EffectiveDelayMs > 0 ? b.EffectiveDelayMs : int.MaxValue;
                    int cmp = da.CompareTo(db);
                    return cmp != 0 ? cmp : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
                });
            }
            ClampScroll();
        }

        // ---------------------------------------------------------------- 双断点响应式几何

        float LogicalW { get { return Math.Max(400f, ClientSize.Width / Theme.S); } }
        float LogicalH { get { return Math.Max(260f, ClientSize.Height / Theme.S); } }
        bool Compact { get { return LogicalW < 640f; } }
        bool HasBack { get { return !string.IsNullOrEmpty(_backLabel); } }

        float HeaderH { get { return HasBack ? 32f : 0f; } }
        float Card1Top { get { return HeaderH; } }
        float Card1H { get { return Compact ? 74f : 62f; } }
        float Card2Top { get { return Card1Top + Card1H + 8f; } }
        float Card2H { get { return Compact ? 100f : 68f; } }
        float ListTop { get { return Card2Top + Card2H + 8f; } }
        float RowH { get { return Compact ? 54f : 46f; } }

        /// <summary>统一计算顶部与筛选快速条几何布局，供绘制、命中测试与自检共用同一份规则。</summary>
        internal static void ComputeLayoutBounds(
            float lw,
            bool hasBack,
            out RectangleF backRect,
            out RectangleF testCurRect,
            out RectangleF batchRect,
            out RectangleF searchRect,
            out RectangleF tabAllRect,
            out RectangleF tabCandRect,
            out RectangleF sortRect,
            out RectangleF quickFastRect,
            out RectangleF quickRegRect,
            out RectangleF quickClearRect)
        {
            bool compact = lw < 640f;
            float headerH = hasBack ? 32f : 0f;
            float card1Top = headerH;
            float card1H = compact ? 74f : 62f;
            float card2Top = card1Top + card1H + 8f;
            float filterRowY = card2Top + 35f;
            float quickRowY = compact ? (card2Top + 67f) : (card2Top + 35f);

            backRect = hasBack ? new RectangleF(0f, 0f, Math.Min(156f, lw * 0.44f), 25f) : RectangleF.Empty;
            batchRect = compact
                ? new RectangleF(lw - 90f, card1Top + 39f, 78f, 25f)
                : new RectangleF(lw - 108f, card1Top + 10f, 96f, 26f);
            testCurRect = compact
                ? new RectangleF(lw - 164f, card1Top + 39f, 68f, 25f)
                : new RectangleF(lw - 194f, card1Top + 10f, 80f, 26f);

            float searchW = compact
                ? Math.Max(110f, lw - 12f - 196f)
                : Math.Max(130f, Math.Min(250f, lw - 468f));
            searchRect = new RectangleF(12f, filterRowY, searchW, 25f);
            tabAllRect = new RectangleF(searchRect.Right + 6f, filterRowY, compact ? 56f : 68f, 25f);
            tabCandRect = new RectangleF(tabAllRect.Right + 5f, filterRowY, compact ? 62f : 78f, 25f);
            sortRect = new RectangleF(tabCandRect.Right + 5f, filterRowY, compact ? 52f : 58f, 25f);

            quickClearRect = new RectangleF(lw - 58f, quickRowY, 46f, 25f);
            quickRegRect = new RectangleF(lw - 138f, quickRowY, 74f, 25f);
            quickFastRect = new RectangleF(lw - 218f, quickRowY, 74f, 25f);
        }

        RectangleF BackBtnRect
        {
            get
            {
                RectangleF b, tc, ba, s, ta, tca, so, qf, qr, qc;
                ComputeLayoutBounds(LogicalW, HasBack, out b, out tc, out ba, out s, out ta, out tca, out so, out qf, out qr, out qc);
                return b;
            }
        }
        RectangleF BatchRect
        {
            get
            {
                RectangleF b, tc, ba, s, ta, tca, so, qf, qr, qc;
                ComputeLayoutBounds(LogicalW, HasBack, out b, out tc, out ba, out s, out ta, out tca, out so, out qf, out qr, out qc);
                return ba;
            }
        }
        RectangleF TestCurRect
        {
            get
            {
                RectangleF b, tc, ba, s, ta, tca, so, qf, qr, qc;
                ComputeLayoutBounds(LogicalW, HasBack, out b, out tc, out ba, out s, out ta, out tca, out so, out qf, out qr, out qc);
                return tc;
            }
        }

        RectangleF AutoToggleRect { get { return new RectangleF(12f, Card2Top + 8f, Math.Max(200f, LogicalW - 140f), 22f); } }

        float FilterRowY { get { return Card2Top + 35f; } }
        float QuickRowY { get { return Compact ? (Card2Top + 67f) : (Card2Top + 35f); } }

        RectangleF SearchBoxRect
        {
            get
            {
                RectangleF b, tc, ba, s, ta, tca, so, qf, qr, qc;
                ComputeLayoutBounds(LogicalW, HasBack, out b, out tc, out ba, out s, out ta, out tca, out so, out qf, out qr, out qc);
                return s;
            }
        }
        RectangleF TabAllRect
        {
            get
            {
                RectangleF b, tc, ba, s, ta, tca, so, qf, qr, qc;
                ComputeLayoutBounds(LogicalW, HasBack, out b, out tc, out ba, out s, out ta, out tca, out so, out qf, out qr, out qc);
                return ta;
            }
        }
        RectangleF TabCandRect
        {
            get
            {
                RectangleF b, tc, ba, s, ta, tca, so, qf, qr, qc;
                ComputeLayoutBounds(LogicalW, HasBack, out b, out tc, out ba, out s, out ta, out tca, out so, out qf, out qr, out qc);
                return tca;
            }
        }
        RectangleF SortDelayRect
        {
            get
            {
                RectangleF b, tc, ba, s, ta, tca, so, qf, qr, qc;
                ComputeLayoutBounds(LogicalW, HasBack, out b, out tc, out ba, out s, out ta, out tca, out so, out qf, out qr, out qc);
                return so;
            }
        }

        RectangleF QuickClearRect
        {
            get
            {
                RectangleF b, tc, ba, s, ta, tca, so, qf, qr, qc;
                ComputeLayoutBounds(LogicalW, HasBack, out b, out tc, out ba, out s, out ta, out tca, out so, out qf, out qr, out qc);
                return qc;
            }
        }
        RectangleF QuickRegRect
        {
            get
            {
                RectangleF b, tc, ba, s, ta, tca, so, qf, qr, qc;
                ComputeLayoutBounds(LogicalW, HasBack, out b, out tc, out ba, out s, out ta, out tca, out so, out qf, out qr, out qc);
                return qr;
            }
        }
        RectangleF QuickFastRect
        {
            get
            {
                RectangleF b, tc, ba, s, ta, tca, so, qf, qr, qc;
                ComputeLayoutBounds(LogicalW, HasBack, out b, out tc, out ba, out s, out ta, out tca, out so, out qf, out qr, out qc);
                return qf;
            }
        }

        RectangleF ListAreaRect { get { return new RectangleF(0f, ListTop, LogicalW, Math.Max(80f, LogicalH - ListTop)); } }

        float MaxScroll
        {
            get { return Math.Max(0f, _viewNodes.Count * RowH - ListAreaRect.Height); }
        }

        void ClampScroll()
        {
            float max = MaxScroll;
            if (_scrollTarget < 0) _scrollTarget = 0;
            if (_scrollTarget > max) _scrollTarget = max;
            if (_scroll < 0) _scroll = 0;
            if (_scroll > max) _scroll = max;
        }

        void StepScroll()
        {
            float diff = _scrollTarget - _scroll;
            if (Math.Abs(diff) < 0.5f) { _scroll = _scrollTarget; _scrollTimer.Stop(); }
            else _scroll += diff * 0.35f;
            Invalidate();
        }

        // ---------------------------------------------------------------- 绘制

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Draw.Hint(g);
            g.Clear(Theme.Bg);
            g.ScaleTransform(Theme.S, Theme.S);

            if (HasBack) DrawBackStrip(g);
            DrawTopCard(g);
            DrawFailoverAndFilterCard(g);
            DrawNodeList(g);
            Overlay?.Invoke(g, ListAreaRect);
        }

        void DrawBackStrip(Graphics g)
        {
            var br = BackBtnRect;
            bool hover = _hoverHit == "back";
            Theme.Fill(g, hover ? Theme.BtnHover : Theme.Card, br, 7f);
            Theme.Stroke(g, hover ? Theme.Accent : Theme.Line, br, 7f);
            Draw.Text(g, TrimText(g, _backLabel, _fSmall, br.Width - 12f), _fSmall,
                hover ? Theme.Accent : Theme.Fg, br, Draw.Center);

            Draw.Text(g, "节点测速 · 备选池 · 超时自动故障切换", _fTiny, Theme.Dim,
                new RectangleF(br.Right + 10f, 3f, Math.Max(40f, LogicalW - br.Right - 14f), 19f), Draw.Right);
        }

        void DrawTopCard(Graphics g)
        {
            float lw = LogicalW;
            var card = new RectangleF(0, Card1Top, lw, Card1H);
            Theme.Fill(g, Theme.Card, card, 12f);
            Theme.Stroke(g, Theme.Line, card, 12f);

            Color dotCol = !_proxyConnected
                ? (string.IsNullOrEmpty(_currentOutbound) ? Theme.Dim : Theme.Warn)
                : (_currentTimedOut ? Theme.Danger : Theme.Accent);
            using (var dot = new SolidBrush(dotCol))
                g.FillEllipse(dot, 14f, Card1Top + 16f, 8f, 8f);

            string curName = string.IsNullOrEmpty(_currentOutbound) ? "未连接到代理内核" : Draw.CleanEmoji(_currentOutbound);
            string delayBadge = _proxyConnected
                ? (_currentTimedOut ? " · 超时" : (_currentDelayMs > 0 ? (" · " + _currentDelayMs + "ms") : ""))
                : "";
            float titleAvail = Compact ? Math.Max(140f, lw - 40f) : Math.Max(140f, lw - 240f);
            string titlePrefix = _proxyConnected
                ? "当前："
                : (string.IsNullOrEmpty(_currentOutbound) ? "" : "待连接（预设）：");
            string title = titlePrefix + TrimText(g, curName, _fTitle, Math.Max(90f, titleAvail - 110f)) + delayBadge;
            Color titleCol = !_proxyConnected
                ? (string.IsNullOrEmpty(_currentOutbound) ? Theme.Dim : Theme.Warn)
                : (_currentTimedOut ? Theme.Danger : Theme.Fg);
            Draw.Text(g, title, _fTitle, titleCol,
                new RectangleF(28, Card1Top + 10, titleAvail, 21));

            int concreteCount = _allNodes.Count(n => !n.IsGroup && !ProbeSidecar.IsInformationalNodeName(n.Name));
            if (Compact)
            {
                string compactSub = string.IsNullOrEmpty(_lastFailoverSummary)
                    ? ("主选择组：" + (string.IsNullOrEmpty(_groupName) ? "识别中…" : TrimText(g, Draw.CleanEmoji(_groupName), _fTiny, 118f)) + " (" + concreteCount + ")")
                    : _lastFailoverSummary;
                Draw.Text(g, TrimText(g, compactSub, _fTiny, lw - 180f), _fTiny,
                    string.IsNullOrEmpty(_lastFailoverSummary) ? Theme.Dim : Theme.Ok,
                    new RectangleF(14, Card1Top + 43, Math.Max(60f, lw - 180f), 16));
            }
            else
            {
                string groupBrief = string.IsNullOrEmpty(_groupName)
                    ? "正在识别策略组…"
                    : ("主选择组：" + TrimText(g, Draw.CleanEmoji(_groupName), _fTiny, 160f) + " (" + concreteCount + "个)");
                string hint = string.IsNullOrEmpty(_lastFailoverSummary)
                    ? "勾选左侧加入备选池 · 点击右侧可无感体检出口风控"
                    : _lastFailoverSummary;
                Color hintCol = string.IsNullOrEmpty(_lastFailoverSummary) ? Theme.Dim : Theme.Ok;
                Draw.Text(g, groupBrief, _fTiny, Theme.Dim, new RectangleF(14, Card1Top + 38, 220, 15));
                float hintX = 238f;
                float hintW = Math.Max(80f, lw - hintX - 14f);
                Draw.Text(g, TrimText(g, hint, _fTiny, hintW), _fTiny, hintCol,
                    new RectangleF(hintX, Card1Top + 38, hintW, 15));
            }

            bool ht = _hoverHit == "testCur";
            Theme.Fill(g, ht ? Theme.BtnHover : Theme.Btn, TestCurRect, 7f);
            Theme.Stroke(g, Theme.Line, TestCurRect, 7f);
            Draw.Text(g, Compact ? "测当前" : "测速", _fSmall, Theme.Fg, TestCurRect, Draw.Center);

            bool hb = _hoverHit == "batch";
            Color bBg = _batchTesting ? Theme.Btn : (hb ? Theme.Blend(Theme.Accent, Color.White, 0.12f) : Theme.Accent);
            Theme.Fill(g, bBg, BatchRect, 7f);
            if (_batchTesting) Theme.Stroke(g, Theme.Accent, BatchRect, 7f);
            string bText = _batchTesting ? ("停测 " + _batchDone + "/" + _batchTotal) : "全部测速";
            Draw.Text(g, bText, _fSmall, _batchTesting ? Theme.Accent : Color.White, BatchRect, Draw.Center);
        }

        void DrawFailoverAndFilterCard(Graphics g)
        {
            float lw = LogicalW;
            var card = new RectangleF(0, Card2Top, lw, Card2H);
            Theme.Fill(g, Theme.Card, card, 12f);
            Theme.Stroke(g, Theme.Line, card, 12f);

            bool afOn = Settings.AutoFailover;
            var sw = new RectangleF(14, Card2Top + 9, 32, 18);
            Theme.Fill(g, afOn ? Theme.TrackOn : Theme.TrackOff, sw, 9f);
            float knobX = afOn ? sw.Right - 16f : sw.X + 2f;
            using (var kb = new SolidBrush(Color.White))
                g.FillEllipse(kb, knobX, sw.Y + 2f, 14f, 14f);

            string swLabel = Compact
                ? "超时自动切备选池最低延迟节点"
                : "当前节点超时时，自动切换至备选池最低延迟节点（同步发送通知）";
            Draw.Text(g, TrimText(g, swLabel, _fSmall, Math.Max(140f, lw - 180f)),
                _fSmall, Theme.Fg, new RectangleF(52, Card2Top + 9, Math.Max(140f, lw - 178f), 18));

            int candCount = _allNodes.Count(n => !n.IsGroup && n.IsCandidate);
            string cntTxt = (afOn && candCount == 0) ? "请选节点" : ("备选 " + candCount + " 个");
            Color cntCol = (afOn && candCount == 0) ? Theme.Warn : Theme.Accent;
            Draw.Text(g, cntTxt, _fSmall, cntCol, new RectangleF(lw - 126f, Card2Top + 9, 112f, 18), Draw.Right);

            // 搜索框
            Theme.Fill(g, Theme.Bg, SearchBoxRect, 7f);
            Theme.Stroke(g, _search.Focused ? Theme.Accent : Theme.Line, SearchBoxRect, 7f, _search.Focused ? 1.3f : 1f);
            using (var pen = new Pen(_search.Focused ? Theme.Accent : Theme.Dim, 1.3f))
            {
                float cx = SearchBoxRect.X + 12f, cy = SearchBoxRect.Y + 12f;
                g.DrawEllipse(pen, cx - 3.5f, cy - 3.5f, 7f, 7f);
                g.DrawLine(pen, cx + 2.5f, cy + 2.5f, cx + 5.5f, cy + 5.5f);
            }

            DrawPillBtn(g, TabAllRect, Compact ? ("全" + _allNodes.Count) : ("全部 " + _allNodes.Count), _filterTab == 0, _hoverHit == "tabAll");
            DrawPillBtn(g, TabCandRect, Compact ? ("备选" + candCount) : ("备选池 " + candCount), _filterTab == 1, _hoverHit == "tabCand");
            DrawPillBtn(g, SortDelayRect, _sortByDelay ? "延迟↑" : "原序", _sortByDelay, _hoverHit == "sortDelay");

            if (Compact)
            {
                Draw.Text(g, "快速筛选备选：", _fTiny, Theme.Dim,
                    new RectangleF(14f, QuickRowY + 4f, Math.Max(60f, QuickFastRect.X - 18f), 17f));
            }
            DrawPillBtn(g, QuickFastRect, "优选低延迟", false, _hoverHit == "qFast");
            DrawPillBtn(g, QuickRegRect, "优选同地区", false, _hoverHit == "qReg");
            DrawPillBtn(g, QuickClearRect, "清空", false, _hoverHit == "qClear");
        }

        void DrawPillBtn(Graphics g, RectangleF r, string text, bool active, bool hover)
        {
            Color bg = active ? Theme.AccentSoft : (hover ? Theme.Hover : Theme.Btn);
            Color fg = active ? Theme.Accent : Theme.Dim;
            Theme.Fill(g, bg, r, 7f);
            Theme.Stroke(g, active ? Theme.Accent : Theme.Line, r, 7f);
            Draw.Text(g, text, _fTiny, fg, r, Draw.Center);
        }

        void DrawNodeList(Graphics g)
        {
            var area = ListAreaRect;
            var clip = g.Save();
            g.SetClip(area);

            if (_loadingCatalog && _allNodes.Count == 0)
            {
                Draw.Text(g, "正在读取 Clash 策略组与节点…", _fReg, Theme.Dim, area, Draw.Center);
                g.Restore(clip);
                return;
            }
            if (!string.IsNullOrEmpty(_catalogError) && _allNodes.Count == 0)
            {
                Draw.TextWrap(g, _catalogError, _fSmall, Theme.Warn,
                    new RectangleF(24, area.Y + 40, area.Width - 48, 80));
                g.Restore(clip);
                return;
            }
            if (_viewNodes.Count == 0)
            {
                string empty = _filterTab == 1
                    ? "备选池为空 — 点「优选低延迟」或勾选左侧复选框加入"
                    : "没有匹配的节点";
                Draw.Text(g, empty, _fSmall, Theme.Dim, area, Draw.Center);
                g.Restore(clip);
                return;
            }

            float listW = MaxScroll > 0 ? LogicalW - 10f : LogicalW;
            bool compact = Compact;
            float swW = compact ? 52f : 60f;
            float probeW = compact ? 48f : 66f;
            float swX = listW - swW - 8f;
            float probeX = swX - probeW - 6f;
            float delayX = probeX - 58f;
            float riskX = delayX - 114f;

            int first = Math.Max(0, (int)Math.Floor(_scroll / RowH));
            int last = Math.Min(_viewNodes.Count - 1, (int)Math.Ceiling((_scroll + area.Height) / RowH));

            for (int i = first; i <= last; i++)
            {
                float ry = area.Y + i * RowH - _scroll;
                var rowRect = new RectangleF(0, ry + 2f, listW, RowH - 4f);
                var n = _viewNodes[i];
                bool hoverRow = _hoverRow == i;
                Color bg = n.IsCurrent ? Theme.AccentSoft : (hoverRow ? Theme.Hover : Theme.Card);
                Theme.Fill(g, bg, rowRect, 9f);
                Theme.Stroke(g, n.IsCurrent ? Theme.Accent : Theme.Line, rowRect, 9f);

                float cbY = ry + (RowH - 17f) / 2f;
                if (!n.IsGroup)
                {
                    var cb = new RectangleF(12, cbY, 17f, 17f);
                    Theme.Fill(g, n.IsCandidate ? Theme.Accent : Theme.Bg, cb, 4.5f);
                    Theme.Stroke(g, n.IsCandidate ? Theme.Accent : Theme.Line, cb, 4.5f, 1.2f);
                    if (n.IsCandidate)
                    {
                        using (var pen = new Pen(Color.White, 1.8f))
                        {
                            pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round;
                            g.DrawLine(pen, cb.X + 4f, cb.Y + 8.8f, cb.X + 7.2f, cb.Y + 12.2f);
                            g.DrawLine(pen, cb.X + 7.2f, cb.Y + 12.2f, cb.X + 13.2f, cb.Y + 4.8f);
                        }
                    }
                }
                else
                {
                    Draw.Text(g, "组", _fTiny, Theme.Dim, new RectangleF(10, cbY, 22f, 17f), Draw.Center);
                }

                float nameX = 38f;
                if (n.IsCurrent)
                {
                    using (var dot = new SolidBrush(Theme.Accent))
                        g.FillEllipse(dot, nameX, ry + 11.5f, 7f, 7f);
                    nameX += 11f;
                }

                NodeQuality.QualityReport cached;
                _riskCache.TryGetValue(n.Name, out cached);
                float textEndX = compact ? (delayX - 6f) : (cached != null ? (riskX - 8f) : (delayX - 8f));
                float nameW = Math.Max(70f, textEndX - nameX);

                Draw.Text(g, TrimText(g, Draw.CleanEmoji(n.Name), _fReg, nameW), _fReg,
                    n.IsCurrent ? Theme.Accent : Theme.Fg,
                    new RectangleF(nameX, ry + (compact ? 7f : 6f), nameW + 4f, 18f));

                string subText;
                if (n.IsGroup)
                    subText = "自动选择策略组";
                else if (ProbeSidecar.IsInformationalNodeName(n.Name))
                    subText = "订阅状态提示条目";
                else
                {
                    string reg = string.IsNullOrEmpty(n.ExpectedRegion)
                        ? "通用区域" : NodeManager.RegionDisplayName(n.ExpectedRegion);
                    subText = reg + " · " + n.Type;
                }

                if (compact && cached != null)
                {
                    string miniRisk = cached.RoutedDirect ? "直连异常"
                        : ((cached.RiskScore >= 0 ? (cached.RiskScore + "分") : "") + " · " + cached.MainstreamReadyCount + "/4通");
                    Color mkFg = cached.Level == NodeQuality.RiskLevel.Low ? Theme.Ok
                        : (cached.Level == NodeQuality.RiskLevel.High || cached.RoutedDirect ? Theme.Danger : Theme.Warn);
                    float subAvail = Math.Max(60f, probeX - 38f - 84f);
                    Draw.Text(g, TrimText(g, subText, _fTiny, subAvail), _fTiny, Theme.Dim,
                        new RectangleF(38f, ry + 29f, subAvail, 15f));
                    Draw.Text(g, miniRisk, _fTiny, mkFg,
                        new RectangleF(probeX - 84f, ry + 29f, 80f, 15f), Draw.Right);
                }
                else
                {
                    Draw.Text(g, TrimText(g, subText, _fTiny, Math.Max(70f, textEndX - 38f)), _fTiny, Theme.Dim,
                        new RectangleF(38f, ry + (compact ? 29f : 25f), Math.Max(70f, textEndX - 34f), 14f));
                }

                if (!compact && cached != null)
                {
                    var rkRect = new RectangleF(riskX, ry + 12f, 108f, 22f);
                    Color rkBg = cached.Level == NodeQuality.RiskLevel.Low ? Theme.OkSoft
                        : (cached.Level == NodeQuality.RiskLevel.High || cached.RoutedDirect ? Theme.DangerSoft : Theme.WarnSoft);
                    Color rkFg = cached.Level == NodeQuality.RiskLevel.Low ? Theme.Ok
                        : (cached.Level == NodeQuality.RiskLevel.High || cached.RoutedDirect ? Theme.Danger : Theme.Warn);
                    string rkText = cached.RoutedDirect
                        ? "直连异常"
                        : ((cached.RiskScore >= 0 ? (cached.RiskScore + "分") : "已测") +
                           " · 服务 " + cached.MainstreamReadyCount + "/4");
                    Theme.Fill(g, rkBg, rkRect, 6f);
                    Draw.Text(g, rkText, _fTiny, rkFg, rkRect, Draw.Center);
                }

                string delayStr = "-";
                Color delayCol = Theme.Dim;
                if (n.LiveDelayMs == -2) { delayStr = "超时"; delayCol = Theme.Danger; }
                else if (n.EffectiveDelayMs > 0)
                {
                    int ms = n.EffectiveDelayMs;
                    delayStr = ms + "ms";
                    delayCol = ms < 180 ? Theme.Ok : (ms < 380 ? Theme.Fg : Theme.Warn);
                }
                else if (n.Alive == false) { delayStr = "失活"; delayCol = Theme.Danger; }
                Draw.Text(g, delayStr, _fSmall, delayCol, new RectangleF(delayX, ry + (compact ? 8f : 14f), 52f, 17f), Draw.Right);

                float btnY = ry + (compact ? 14f : 10f);
                if (!n.IsGroup && !ProbeSidecar.IsInformationalNodeName(n.Name))
                {
                    var probeRect = new RectangleF(probeX, btnY, probeW, 26f);
                    bool hp = _hoverRow == i && _hoverHit == "rowProbe";
                    bool testing = string.Equals(_inspectingNode, n.Name, StringComparison.OrdinalIgnoreCase);
                    Theme.Fill(g, hp ? Theme.BtnHover : Theme.Btn, probeRect, 6.5f);
                    Theme.Stroke(g, Theme.Line, probeRect, 6.5f);
                    Draw.Text(g, testing ? "检测中" : (compact ? "体检" : "查风控"), _fTiny,
                        testing ? Theme.Accent : Theme.Fg, probeRect, Draw.Center);
                }

                var swRect = new RectangleF(swX, btnY, swW, 26f);
                bool hs = _hoverRow == i && _hoverHit == "rowSwitch";
                if (n.IsCurrent)
                {
                    Theme.Fill(g, Theme.AccentSoft, swRect, 6.5f);
                    Draw.Text(g, "在用", _fTiny, Theme.Accent, swRect, Draw.Center);
                }
                else
                {
                    Theme.Fill(g, hs ? Theme.Accent : Theme.Btn, swRect, 6.5f);
                    Theme.Stroke(g, hs ? Theme.Accent : Theme.Line, swRect, 6.5f);
                    Draw.Text(g, "切换", _fTiny, hs ? Color.White : Theme.Fg, swRect, Draw.Center);
                }
            }

            if (MaxScroll > 0)
                Theme.Fill(g, Theme.Dark ? Color.FromArgb(110, 255, 255, 255) : Color.FromArgb(90, 0, 0, 0), ThumbRect(), 3f);

            g.Restore(clip);
        }

        RectangleF ThumbRect()
        {
            float h = ListAreaRect.Height;
            float total = _viewNodes.Count * RowH;
            float th = Math.Max(28f, h * (h / total));
            float y = ListTop + (MaxScroll <= 0 ? 0 : (_scroll / MaxScroll) * (h - th));
            return new RectangleF(LogicalW - 6f, y, 5f, th);
        }

        static string TrimText(Graphics g, string text, Font font, float maxWidth)
        {
            if (string.IsNullOrEmpty(text) || maxWidth <= 8f) return "";
            if (g.MeasureString(text, font).Width <= maxWidth) return text;
            const string ell = "…";
            int lo = 0, hi = text.Length;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (g.MeasureString(text.Substring(0, mid) + ell, font).Width <= maxWidth) lo = mid;
                else hi = mid - 1;
            }
            return lo <= 0 ? ell : (text.Substring(0, lo) + ell);
        }

        // ---------------------------------------------------------------- 交互

        void HitTest(Point p, out string hit, out int rowIdx)
        {
            hit = ""; rowIdx = -1;
            float x = p.X / Theme.S, y = p.Y / Theme.S;
            if (HasBack && BackBtnRect.Contains(x, y)) { hit = "back"; return; }
            if (TestCurRect.Contains(x, y)) { hit = "testCur"; return; }
            if (BatchRect.Contains(x, y)) { hit = "batch"; return; }
            if (AutoToggleRect.Contains(x, y)) { hit = "failover"; return; }
            if (TabAllRect.Contains(x, y)) { hit = "tabAll"; return; }
            if (TabCandRect.Contains(x, y)) { hit = "tabCand"; return; }
            if (SortDelayRect.Contains(x, y)) { hit = "sortDelay"; return; }
            if (QuickFastRect.Contains(x, y)) { hit = "qFast"; return; }
            if (QuickRegRect.Contains(x, y)) { hit = "qReg"; return; }
            if (QuickClearRect.Contains(x, y)) { hit = "qClear"; return; }

            if (!ListAreaRect.Contains(x, y) || _viewNodes.Count == 0) return;
            if (MaxScroll > 0 && x >= LogicalW - 10f) { hit = "scrollbar"; return; }

            int idx = (int)Math.Floor((y - ListTop + _scroll) / RowH);
            if (idx < 0 || idx >= _viewNodes.Count) return;
            rowIdx = idx;
            float localY = (y - ListTop + _scroll) - idx * RowH;
            var n = _viewNodes[idx];
            float listW = MaxScroll > 0 ? LogicalW - 10f : LogicalW;
            bool compact = Compact;
            float swW = compact ? 52f : 60f;
            float probeW = compact ? 48f : 66f;
            float swX = listW - swW - 8f;
            float probeX = swX - probeW - 6f;

            if (!n.IsGroup && new RectangleF(4, 4, 34, RowH - 8).Contains(x, localY)) { hit = "rowCheck"; return; }
            if (!n.IsGroup && !ProbeSidecar.IsInformationalNodeName(n.Name) &&
                new RectangleF(probeX, 8f, probeW, RowH - 16f).Contains(x, localY)) { hit = "rowProbe"; return; }
            if (!n.IsCurrent && new RectangleF(swX, 8f, swW, RowH - 16f).Contains(x, localY)) { hit = "rowSwitch"; return; }
            hit = "row";
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (MaxScroll <= 0) return;
            _scrollTarget -= Math.Sign(e.Delta) * RowH * 1.6f;
            ClampScroll();
            _scrollTimer.Start();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_draggingThumb)
            {
                float dy = (e.Y / Theme.S) - _dragStartY;
                float thumbH = ThumbRect().Height;
                float trackH = Math.Max(1f, ListAreaRect.Height - thumbH);
                _scrollTarget = _dragStartScroll + (dy / trackH) * MaxScroll;
                ClampScroll();
                _scroll = _scrollTarget;
                Invalidate();
                return;
            }
            string hit; int row;
            HitTest(e.Location, out hit, out row);
            if (hit != _hoverHit || row != _hoverRow)
            {
                _hoverHit = hit;
                _hoverRow = row;
                Cursor = (hit.Length > 0 && hit != "row") ? Cursors.Hand : Cursors.Default;
                Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hoverHit.Length > 0 || _hoverRow >= 0)
            {
                _hoverHit = "";
                _hoverRow = -1;
                Cursor = Cursors.Default;
                Invalidate();
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (_draggingThumb) { _draggingThumb = false; Capture = false; }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            string hit; int row;
            HitTest(e.Location, out hit, out row);

            if (hit == "back") { BackRequested?.Invoke(); return; }
            if (hit == "scrollbar")
            {
                _draggingThumb = true;
                _dragStartY = e.Y / Theme.S;
                _dragStartScroll = _scroll;
                Capture = true;
                return;
            }
            if (hit == "testCur") { TestCurrentDelayRequested?.Invoke(); return; }
            if (hit == "batch") { ToggleBatchDelayRequested?.Invoke(); return; }
            if (hit == "failover") { AutoFailoverToggled?.Invoke(!Settings.AutoFailover); Invalidate(); return; }
            if (hit == "tabAll") { _filterTab = 0; RebuildView(); Invalidate(); return; }
            if (hit == "tabCand") { _filterTab = 1; RebuildView(); Invalidate(); return; }
            if (hit == "sortDelay") { _sortByDelay = !_sortByDelay; RebuildView(); Invalidate(); return; }
            if (hit == "qFast") { SelectFastCandidates(); return; }
            if (hit == "qReg") { SelectSameRegionCandidates(); return; }
            if (hit == "qClear") { ClearCandidates(); return; }

            if (row >= 0 && row < _viewNodes.Count)
            {
                var n = _viewNodes[row];
                if (hit == "rowCheck" && !n.IsGroup)
                {
                    n.IsCandidate = !n.IsCandidate;
                    CandidateToggled?.Invoke(n.Name, n.IsCandidate);
                    if (_filterTab == 1) RebuildView();
                    Invalidate();
                }
                else if (hit == "rowProbe" && !n.IsGroup && !ProbeSidecar.IsInformationalNodeName(n.Name))
                {
                    ProbeAndOpenQualityRequested?.Invoke(n.Name);
                }
                else if (hit == "rowSwitch" && !n.IsCurrent)
                {
                    SwitchNodeRequested?.Invoke(n.Name);
                }
            }
        }

        void SelectFastCandidates()
        {
            var fast = _allNodes
                .Where(n => !n.IsGroup && !ProbeSidecar.IsInformationalNodeName(n.Name) && n.EffectiveDelayMs > 0 && n.EffectiveDelayMs < 350)
                .OrderBy(n => n.EffectiveDelayMs)
                .Take(8)
                .ToList();
            if (fast.Count == 0)
                fast = _allNodes.Where(n => !n.IsGroup && !ProbeSidecar.IsInformationalNodeName(n.Name) && n.Alive != false).Take(5).ToList();
            var names = new HashSet<string>(fast.Select(n => n.Name), StringComparer.OrdinalIgnoreCase);
            foreach (var n in _allNodes) if (!n.IsGroup) n.IsCandidate = names.Contains(n.Name);
            CandidateBatchChanged?.Invoke(fast.Select(n => n.Name).ToList());
            if (_filterTab == 1) RebuildView();
            Invalidate();
        }

        void SelectSameRegionCandidates()
        {
            string target = NodeManager.ParseRegionFromNodeName(_currentOutbound);
            if (string.IsNullOrEmpty(target))
                target = Settings.DesiredRegion.Length == 2 ? Settings.DesiredRegion : "";
            var chosen = new List<string>();
            if (target.Length == 2)
            {
                foreach (var n in _allNodes)
                {
                    if (n.IsGroup || ProbeSidecar.IsInformationalNodeName(n.Name)) continue;
                    bool hit = string.Equals(n.ExpectedRegion, target, StringComparison.OrdinalIgnoreCase);
                    n.IsCandidate = hit;
                    if (hit) chosen.Add(n.Name);
                }
            }
            CandidateBatchChanged?.Invoke(chosen);
            if (_filterTab == 1) RebuildView();
            Invalidate();
        }

        void ClearCandidates()
        {
            foreach (var n in _allNodes) n.IsCandidate = false;
            CandidateBatchChanged?.Invoke(new List<string>());
            if (_filterTab == 1) RebuildView();
            Invalidate();
        }
    }
}
