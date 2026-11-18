using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;
using ProxyPicker.Core;

namespace ProxyPicker.UI
{
    /// <summary>
    /// 链路体检与通用风控评估工作区：
    /// 企业级双层控制台 + 上下文返回导航（支持返回评估矩阵与返回上一页）+
    /// 全尺寸断点响应式布局（最小兼容 466px 经典侧边栏至 2400px 宽屏工作台）。
    /// </summary>
    internal sealed class QualityPage : Control
    {
        const float RankRowWideH = 42f;
        const float RankRowCompactH = 62f;

        float ControlH { get { return Compact ? 78f : 82f; } }

        readonly Timer _scrollTimer;
        Font _fTitle, _fBig, _fBold, _fReg, _fSmall, _fTiny;
        bool _fontsDisposed;

        string _backLabel = "";
        bool _detailFromMatrix;
        float _savedRankScroll;

        string _currentOutbound = "";
        string _selectedTargetNode = "";
        List<NodeManager.ProxyNodeInfo> _availableNodes = new List<NodeManager.ProxyNodeInfo>();

        int _subView; // 0 = 单节点报告, 1 = 评估矩阵
        bool _testing;
        string _testingNode = "";
        NodeQuality.QualityReport _report;

        bool _batchScanning;
        int _batchDone;
        int _batchTotal;
        string _batchCurrentNode = "";
        int _rankFilterMode; // 0=全部 1=仅看中低风控 2=仅看四项全通

        readonly Dictionary<string, NodeQuality.QualityReport> _reportsByNode =
            new Dictionary<string, NodeQuality.QualityReport>(StringComparer.OrdinalIgnoreCase);
        readonly List<NodeQuality.QualityReport> _rankView = new List<NodeQuality.QualityReport>();

        float _scroll;
        float _scrollTarget;
        bool _draggingThumb;
        float _dragStartY;
        float _dragStartScroll;
        string _hoverHit = "";
        int _hoverRow = -1;

        public Action<Graphics, RectangleF> Overlay;

        public event Action BackRequested;
        public event Action<string> InspectRequested;
        public event Action BatchRiskScanRequested;
        public event Action DesiredRegionCycled;
        public event Action JumpToNodesRequested;
        public event Action<string> SwitchNodeRequested;
        public event Action<List<string>> ApplyLowRiskPoolRequested;
        public event Action<string> CopyIpRequested;

        public QualityPage()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            TabStop = false;
            BackColor = Theme.Bg;
            BuildFonts();
            _scrollTimer = new Timer { Interval = 16 };
            _scrollTimer.Tick += (s, e) => StepScroll();
        }

        void BuildFonts()
        {
            DisposeFonts();
            _fTitle = Theme.Ui(13f, FontStyle.Bold);
            _fBig = Theme.Ui(18f, FontStyle.Bold);
            _fBold = Theme.Ui(11f, FontStyle.Bold);
            _fReg = Theme.Ui(11f);
            _fSmall = Theme.Ui(10f);
            _fTiny = Theme.Ui(9.2f);
            _fontsDisposed = false;
        }

        public void RefreshThemeAndFonts()
        {
            BackColor = Theme.Bg;
            BuildFonts();
            Invalidate();
        }

        void DisposeFonts()
        {
            if (_fTitle != null) _fTitle.Dispose();
            if (_fBig != null) _fBig.Dispose();
            if (_fBold != null) _fBold.Dispose();
            if (_fReg != null) _fReg.Dispose();
            if (_fSmall != null) _fSmall.Dispose();
            if (_fTiny != null) _fTiny.Dispose();
            _fTitle = _fBig = _fBold = _fReg = _fSmall = _fTiny = null;
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

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            ClampScroll();
        }

        // ---------------------------------------------------------------- 状态同步与导航历史

        public void SetBackLabel(string label)
        {
            _backLabel = label ?? "";
            Invalidate();
        }

        public bool GoBack()
        {
            if (_subView == 0 && _detailFromMatrix)
            {
                _subView = 1;
                _detailFromMatrix = false;
                _scroll = _scrollTarget = _savedRankScroll;
                ClampScroll();
                Invalidate();
                return true;
            }
            if (!string.IsNullOrEmpty(_backLabel) && BackRequested != null)
            {
                BackRequested();
                return true;
            }
            return false;
        }

        internal int DebugReportCacheCount { get { return _reportsByNode.Count; } }

        public void SetCatalog(string currentOutbound, List<NodeManager.ProxyNodeInfo> concreteNodes)
        {
            _currentOutbound = currentOutbound ?? "";
            _availableNodes = concreteNodes != null
                ? new List<NodeManager.ProxyNodeInfo>(concreteNodes)
                : new List<NodeManager.ProxyNodeInfo>();
            if (_availableNodes.Count > 0)
            {
                var valid = new HashSet<string>(_availableNodes.Select(n => n.Name), StringComparer.OrdinalIgnoreCase);
                bool pruned = false;
                if (_reportsByNode.Count > 0)
                {
                    foreach (var staleKey in _reportsByNode.Keys.Where(k => !valid.Contains(k)).ToList())
                    {
                        _reportsByNode.Remove(staleKey);
                        pruned = true;
                    }
                }
                if (!string.IsNullOrEmpty(_selectedTargetNode) && !valid.Contains(_selectedTargetNode) &&
                    !string.Equals(_selectedTargetNode, _currentOutbound, StringComparison.OrdinalIgnoreCase))
                {
                    _selectedTargetNode = "";
                }
                if (_report != null && !string.IsNullOrEmpty(_report.TargetNode) && !valid.Contains(_report.TargetNode) &&
                    !string.Equals(_report.TargetNode, _currentOutbound, StringComparison.OrdinalIgnoreCase))
                {
                    _report = null;
                    _detailFromMatrix = false;
                }
                if (pruned) RebuildRankView();
            }
            Invalidate();
        }

        public void SetCurrentOutbound(string currentOutbound)
        {
            _currentOutbound = currentOutbound ?? "";
            Invalidate();
        }

        public void SetSelectedTarget(string nodeName)
        {
            _selectedTargetNode = nodeName ?? "";
            Invalidate();
        }

        public void SetTesting(bool testing, string nodeName)
        {
            _testing = testing;
            _testingNode = nodeName ?? "";
            if (testing && !_batchScanning) _subView = 0;
            Invalidate();
        }

        public void SetReport(NodeQuality.QualityReport report)
        {
            _testing = false;
            _testingNode = "";
            if (report != null)
            {
                _report = report;
                if (!string.IsNullOrEmpty(report.TargetNode))
                    _reportsByNode[report.TargetNode] = report;
                RebuildRankView();
                if (!_batchScanning)
                {
                    _subView = 0;
                    _detailFromMatrix = false;
                }
                _scroll = _scrollTarget = 0f;
            }
            Invalidate();
        }

        public void SetBatchScanning(bool scanning, int done, int total, string currentNode)
        {
            _batchScanning = scanning;
            _batchDone = done;
            _batchTotal = total;
            _batchCurrentNode = currentNode ?? "";
            if (scanning)
            {
                _subView = 1;
                _detailFromMatrix = false;
            }
            Invalidate();
        }

        public void UpsertBatchReport(NodeQuality.QualityReport report)
        {
            if (report == null || string.IsNullOrEmpty(report.TargetNode)) return;
            _reportsByNode[report.TargetNode] = report;
            if (_report == null || string.Equals(_report.TargetNode, report.TargetNode, StringComparison.OrdinalIgnoreCase))
                _report = report;
            RebuildRankView();
            Invalidate();
        }

        void RebuildRankView()
        {
            _rankView.Clear();
            foreach (var rep in _reportsByNode.Values)
            {
                if (_rankFilterMode == 1 && (rep.RoutedDirect || rep.Level == NodeQuality.RiskLevel.High || rep.Level == NodeQuality.RiskLevel.Unknown))
                    continue;
                if (_rankFilterMode == 2 && (rep.RoutedDirect || rep.MainstreamReadyCount < 4))
                    continue;
                _rankView.Add(rep);
            }
            _rankView.Sort((a, b) =>
            {
                bool aAlive = !a.RoutedDirect && a.Level != NodeQuality.RiskLevel.Unknown;
                bool bAlive = !b.RoutedDirect && b.Level != NodeQuality.RiskLevel.Unknown;
                if (aAlive != bAlive) return aAlive ? -1 : 1;

                int la = a.Level == NodeQuality.RiskLevel.Unknown ? 99 : (int)a.Level;
                int lb = b.Level == NodeQuality.RiskLevel.Unknown ? 99 : (int)b.Level;
                if (la != lb) return la.CompareTo(lb);

                if (a.MainstreamReadyCount != b.MainstreamReadyCount)
                    return b.MainstreamReadyCount.CompareTo(a.MainstreamReadyCount);

                int sa = a.RiskScore >= 0 ? a.RiskScore : 999;
                int sb = b.RiskScore >= 0 ? b.RiskScore : 999;
                if (sa != sb) return sa.CompareTo(sb);

                int da = a.Net != null && a.Net.AvgMs > 0 ? a.Net.AvgMs : int.MaxValue;
                int db = b.Net != null && b.Net.AvgMs > 0 ? b.Net.AvgMs : int.MaxValue;
                return da.CompareTo(db);
            });
            ClampScroll();
        }

        string EffectiveTargetNode
        {
            get
            {
                if (!string.IsNullOrEmpty(_selectedTargetNode)) return _selectedTargetNode;
                return _currentOutbound ?? "";
            }
        }

        // ---------------------------------------------------------------- 双断点企业级流式几何

        float LogicalW { get { return Math.Max(400f, ClientSize.Width / Theme.S); } }
        float LogicalH { get { return Math.Max(260f, ClientSize.Height / Theme.S); } }
        bool Compact { get { return LogicalW < 640f; } }
        bool Wide { get { return LogicalW >= 690f; } }

        bool HasContextualBack
        {
            get { return (_subView == 0 && _detailFromMatrix) || !string.IsNullOrEmpty(_backLabel); }
        }
        string EffectiveBackText
        {
            get
            {
                if (_subView == 0 && _detailFromMatrix)
                    return Compact ? "← 矩阵" : "← 返回评估矩阵";
                return Compact && _backLabel.Length > 4 ? "← 返回" : _backLabel;
            }
        }

        /// <summary>统一计算顶部双层控制台按钮矩形，供绘制、命中检测与自检共用同一份规则。</summary>
        internal static void ComputeControlBarLayout(
            float lw,
            bool compact,
            bool wide,
            bool hasBack,
            bool detailFromMatrix,
            out RectangleF backRect,
            out RectangleF detailRect,
            out RectangleF rankRect,
            out RectangleF inspectRect,
            out RectangleF batchRect,
            out RectangleF targetRect,
            out RectangleF regionRect)
        {
            float row2Y = compact ? 44f : 45f;
            if (hasBack)
            {
                float backW = detailFromMatrix ? (compact ? 56f : 112f) : (compact ? 56f : 108f);
                backRect = new RectangleF(10f, 10f, backW, 26f);
            }
            else
            {
                backRect = RectangleF.Empty;
            }

            float detailX = hasBack ? (backRect.Right + 4f) : 10f;
            float detailW = compact ? (hasBack ? 54f : 64f) : 76f;
            detailRect = new RectangleF(detailX, 10f, detailW, 26f);

            float rankW = compact ? (hasBack ? 72f : 82f) : 92f;
            rankRect = new RectangleF(detailRect.Right + 4f, 10f, rankW, 26f);

            float batchW = compact ? 86f : 126f;
            batchRect = new RectangleF(lw - 10f - batchW, 10f, batchW, 26f);

            float inspectW = compact ? 54f : 102f;
            inspectRect = new RectangleF(batchRect.X - 5f - inspectW, 10f, inspectW, 26f);

            float regW = compact ? 96f : 114f;
            regionRect = new RectangleF(lw - 10f - regW, row2Y, regW, 26f);

            float rightBound = wide ? (regionRect.X - 186f) : (regionRect.X - 8f);
            float targetW = Math.Max(140f, rightBound - 10f);
            targetRect = new RectangleF(10f, row2Y, targetW, 26f);
        }

        // 控制台 Row 1 (y=10..36) & Row 2
        RectangleF BackBtnRect
        {
            get
            {
                RectangleF b, d, r, i, ba, t, reg;
                ComputeControlBarLayout(LogicalW, Compact, Wide, HasContextualBack, _subView == 0 && _detailFromMatrix,
                    out b, out d, out r, out i, out ba, out t, out reg);
                return b;
            }
        }
        RectangleF ViewDetailTabRect
        {
            get
            {
                RectangleF b, d, r, i, ba, t, reg;
                ComputeControlBarLayout(LogicalW, Compact, Wide, HasContextualBack, _subView == 0 && _detailFromMatrix,
                    out b, out d, out r, out i, out ba, out t, out reg);
                return d;
            }
        }
        RectangleF ViewRankTabRect
        {
            get
            {
                RectangleF b, d, r, i, ba, t, reg;
                ComputeControlBarLayout(LogicalW, Compact, Wide, HasContextualBack, _subView == 0 && _detailFromMatrix,
                    out b, out d, out r, out i, out ba, out t, out reg);
                return r;
            }
        }
        RectangleF BatchScanBtnRect
        {
            get
            {
                RectangleF b, d, r, i, ba, t, reg;
                ComputeControlBarLayout(LogicalW, Compact, Wide, HasContextualBack, _subView == 0 && _detailFromMatrix,
                    out b, out d, out r, out i, out ba, out t, out reg);
                return ba;
            }
        }
        RectangleF InspectBtnRect
        {
            get
            {
                RectangleF b, d, r, i, ba, t, reg;
                ComputeControlBarLayout(LogicalW, Compact, Wide, HasContextualBack, _subView == 0 && _detailFromMatrix,
                    out b, out d, out r, out i, out ba, out t, out reg);
                return i;
            }
        }
        RectangleF RegionBtnRect
        {
            get
            {
                RectangleF b, d, r, i, ba, t, reg;
                ComputeControlBarLayout(LogicalW, Compact, Wide, HasContextualBack, _subView == 0 && _detailFromMatrix,
                    out b, out d, out r, out i, out ba, out t, out reg);
                return reg;
            }
        }
        RectangleF TargetBtnRect
        {
            get
            {
                RectangleF b, d, r, i, ba, t, reg;
                ComputeControlBarLayout(LogicalW, Compact, Wide, HasContextualBack, _subView == 0 && _detailFromMatrix,
                    out b, out d, out r, out i, out ba, out t, out reg);
                return t;
            }
        }

        // 单节点报告垂直流式区块坐标（随 Compact/Wide 自动堆叠或并排）
        float DashW { get { return (_subView == 0 && MaxScroll > 0) ? (LogicalW - 10f) : LogicalW; } }
        float BannerTop { get { return ControlH + (Compact ? 6f : 8f); } }
        float BannerH { get { return Compact ? 54f : 54f; } }
        float MiddleTop { get { return BannerTop + BannerH + (Compact ? 6f : 8f); } }
        float MiddleCardH { get { return Compact ? 128f : 146f; } }
        float ServiceTop { get { return Compact ? (MiddleTop + MiddleCardH * 2f + 12f) : (MiddleTop + MiddleCardH + 10f); } }
        float ServiceCardH { get { return Compact ? 86f : 92f; } }
        float DashboardBottom
        {
            get
            {
                float serviceRows = Compact ? (ServiceCardH * 2f + 8f) : ServiceCardH;
                return ServiceTop + serviceRows + 10f;
            }
        }

        RectangleF SwitchNodesBtnRect
        {
            get
            {
                float lw = DashW;
                return Compact
                    ? new RectangleF(lw - 110f, BannerTop + 14.5f, 98f, 25f)
                    : new RectangleF(lw - 130f, BannerTop + 14f, 114f, 26f);
            }
        }
        RectangleF CopyIpBtnRect
        {
            get
            {
                float cardW = Compact ? DashW : (DashW - 12f) / 2f;
                return new RectangleF(cardW - 84f, MiddleTop + (Compact ? 24f : 29f), 70f, 22f);
            }
        }

        // 评估矩阵坐标系
        float RankBarTop { get { return ControlH + 8f; } }
        float RankBarH { get { return Compact ? 64f : 38f; } }
        float RankHeaderTop { get { return RankBarTop + RankBarH + 6f; } }
        float RankListTop { get { return RankHeaderTop + (Wide ? 22f : 0f); } }
        float RankRowH { get { return Wide ? RankRowWideH : RankRowCompactH; } }

        RectangleF ApplyPoolRect
        {
            get
            {
                if (Compact)
                {
                    float w = Math.Max(150f, LogicalW - 178f);
                    return new RectangleF(166f, RankBarTop + 32f, w, 26f);
                }
                return new RectangleF(LogicalW - 164f, RankBarTop + 6f, 152f, 26f);
            }
        }
        RectangleF RankFilterRect
        {
            get
            {
                if (Compact)
                    return new RectangleF(12f, RankBarTop + 32f, 146f, 26f);
                return new RectangleF(ApplyPoolRect.X - 6f - 108f, RankBarTop + 6f, 108f, 26f);
            }
        }
        RectangleF RankAreaRect { get { return new RectangleF(0f, RankListTop, LogicalW, Math.Max(80f, LogicalH - RankListTop)); } }

        float MaxScroll
        {
            get
            {
                if (_subView == 1) return Math.Max(0f, _rankView.Count * RankRowH - RankAreaRect.Height);
                return Math.Max(0f, DashboardBottom - LogicalH);
            }
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

            DrawControlBar(g);

            if (_subView == 1)
            {
                DrawRankSummaryBar(g);
                DrawRankTable(g);
                Overlay?.Invoke(g, new RectangleF(0, 0, LogicalW, LogicalH));
                return;
            }

            float dashTop = ControlH + 4f;
            var viewPort = new RectangleF(0, dashTop, LogicalW, Math.Max(40f, LogicalH - dashTop));
            var st = g.Save();
            g.SetClip(viewPort);
            g.TranslateTransform(0, -_scroll);

            DrawVerdictBanner(g);
            DrawDualMiddleCards(g);
            DrawServiceRowCards(g);

            g.Restore(st);

            // 若竖向超出视窗（如 466×540 窄高窗口），绘制右侧精致细滚动条
            if (MaxScroll > 0)
            {
                float vh = viewPort.Height;
                float total = DashboardBottom - dashTop;
                float th = Math.Max(26f, vh * (vh / Math.Max(1f, total)));
                float ty = dashTop + (_scroll / MaxScroll) * (vh - th);
                Theme.Fill(g, Theme.Dark ? Color.FromArgb(110, 255, 255, 255) : Color.FromArgb(90, 0, 0, 0),
                    new RectangleF(LogicalW - 5f, ty, 4f, th), 2f);
            }

            Overlay?.Invoke(g, new RectangleF(0, 0, LogicalW, LogicalH));
        }

        void DrawControlBar(Graphics g)
        {
            float lw = LogicalW;
            var card = new RectangleF(0, 0, lw, ControlH);
            Theme.Fill(g, Theme.Card, card, 12f);
            Theme.Stroke(g, Theme.Line, card, 12f);

            if (HasContextualBack)
            {
                var br = BackBtnRect;
                bool hbBack = _hoverHit == "back";
                Theme.Fill(g, hbBack ? Theme.AccentSoft : Theme.SubCard, br, 7f);
                Theme.Stroke(g, hbBack ? Theme.Accent : Theme.Line, br, 7f);
                Draw.Text(g, TrimText(g, EffectiveBackText, _fTiny, br.Width - 10f), _fTiny,
                    hbBack ? Theme.Accent : Theme.Fg, br, Draw.Center);
            }

            DrawMiniTab(g, ViewDetailTabRect, Compact ? "单节点" : "单节点报告", _subView == 0, _hoverHit == "tabDetail");
            string rankLbl = _reportsByNode.Count > 0
                ? ((Compact ? "矩阵(" : "评估矩阵(") + _reportsByNode.Count + ")")
                : (Compact ? "评估矩阵" : "评估矩阵");
            DrawMiniTab(g, ViewRankTabRect, rankLbl, _subView == 1, _hoverHit == "tabRank");

            Theme.Fill(g, _hoverHit == "inspect" ? Theme.BtnHover : Theme.Btn, InspectBtnRect, 7f);
            Theme.Stroke(g, _testing ? Theme.Accent : Theme.Line, InspectBtnRect, 7f);
            Draw.Text(g, _testing ? "检测中…" : (Compact ? "单检" : "单节点检测"), _fSmall, _testing ? Theme.Accent : Theme.Fg,
                InspectBtnRect, Draw.Center);

            Color bBg = _batchScanning ? Theme.Btn
                : (_hoverHit == "batchScan" ? Theme.Blend(Theme.Accent, Color.White, 0.12f) : Theme.Accent);
            Theme.Fill(g, bBg, BatchScanBtnRect, 7f);
            if (_batchScanning) Theme.Stroke(g, Theme.Accent, BatchScanBtnRect, 7f);
            string bTxt = _batchScanning
                ? ("停止 " + _batchDone + "/" + _batchTotal)
                : (Compact ? "全量评估" : "全量节点评估");
            Draw.Text(g, bTxt, _fBold, _batchScanning ? Theme.Accent : Color.White, BatchScanBtnRect, Draw.Center);

            // 分隔线
            using (var pen = new Pen(Theme.Line))
                g.DrawLine(pen, 10f, 40f, lw - 10f, 40f);

            // Row 2: 对象挑选 + 期望地区 + 旁路提示
            string cleanTarget = Draw.CleanEmoji(EffectiveTargetNode);
            string targetLabel = string.IsNullOrEmpty(cleanTarget)
                ? "对象：未识别节点"
                : ((string.IsNullOrEmpty(_selectedTargetNode) ||
                    string.Equals(_selectedTargetNode, _currentOutbound, StringComparison.OrdinalIgnoreCase))
                    ? ("在用：" + cleanTarget)
                    : ("目标：" + cleanTarget));
            Theme.Fill(g, _hoverHit == "target" ? Theme.BtnHover : Theme.SubCard, TargetBtnRect, 6.5f);
            Theme.Stroke(g, Theme.Line, TargetBtnRect, 6.5f);
            Draw.Text(g, TrimText(g, targetLabel, _fSmall, Math.Max(40f, TargetBtnRect.Width - 22f)) + " ▾",
                _fSmall, Theme.Fg, new RectangleF(TargetBtnRect.X + 8f, TargetBtnRect.Y, TargetBtnRect.Width - 14f, TargetBtnRect.Height));

            if (Wide)
            {
                float badgeX = TargetBtnRect.Right + 8f;
                float badgeW = Math.Max(100f, RegionBtnRect.X - 8f - badgeX);
                var isoRect = new RectangleF(badgeX, 45f, badgeW, 26f);
                Theme.Fill(g, Theme.OkSoft, isoRect, 6.5f);
                Draw.Text(g, "● 旁路隔离探测 · 不中断当前上网", _fTiny, Theme.Ok, isoRect, Draw.Center);
            }

            string regText = "期望：" + NodeManager.RegionDisplayName(Settings.DesiredRegion) + " ▾";
            Theme.Fill(g, _hoverHit == "region" ? Theme.Hover : Theme.AccentSoft, RegionBtnRect, 6.5f);
            Draw.Text(g, regText, _fSmall, Theme.Accent, RegionBtnRect, Draw.Center);
        }

        void DrawMiniTab(Graphics g, RectangleF r, string text, bool active, bool hover)
        {
            Color bg = active ? Theme.AccentSoft : (hover ? Theme.Hover : Theme.SubCard);
            Color fg = active ? Theme.Accent : Theme.Dim;
            Theme.Fill(g, bg, r, 6.5f);
            Theme.Stroke(g, active ? Theme.Accent : Theme.Line, r, 6.5f);
            Draw.Text(g, TrimText(g, text, _fTiny, Math.Max(20f, r.Width - 6f)), _fTiny, fg, r, Draw.Center);
        }

        void DrawVerdictBanner(Graphics g)
        {
            float lw = DashW;
            var card = new RectangleF(0, BannerTop, lw, BannerH);
            Theme.Fill(g, Theme.Card, card, 12f);
            Theme.Stroke(g, Theme.Line, card, 12f);

            var r = _report;
            if (r == null)
            {
                using (var strip = new SolidBrush(Theme.Accent))
                    g.FillRectangle(strip, 2, BannerTop + 11f, 4, BannerH - 22f);
                Draw.Text(g, _testing ? ("正在旁路隔离检测「" + Draw.CleanEmoji(_testingNode) + "」…") : "点击「单节点检测」或「全量节点评估」生成报告",
                    _fTitle, Theme.Fg, new RectangleF(16, BannerTop + 7f, lw - 32f, 20));
                Draw.Text(g, TrimText(g, "出口 IP 归属 · 双源 0–100 风控评分 · 延迟抖动/IPv6 检查 · Google / Gemini / OpenAI / Claude 实测", _fTiny, lw - 32f),
                    _fTiny, Theme.Dim, new RectangleF(16, BannerTop + 29f, lw - 32f, 18));
                return;
            }

            Color accent = r.Level == NodeQuality.RiskLevel.Low ? Theme.Ok
                : (r.Level == NodeQuality.RiskLevel.Medium ? Theme.Warn : Theme.Danger);
            using (var strip = new SolidBrush(accent))
                g.FillRectangle(strip, 2, BannerTop + 11f, 4, BannerH - 22f);

            string badge = r.Level == NodeQuality.RiskLevel.Low ? "✓ "
                : (r.Level == NodeQuality.RiskLevel.High || r.RoutedDirect ? "✕ " : "! ");
            bool showJump = r.Level == NodeQuality.RiskLevel.High || !r.RegionMatched || r.RoutedDirect;
            float titleW = Math.Max(160f, lw - (showJump ? (SwitchNodesBtnRect.Width + 32f) : 32f));
            string modeNote = r.IsSidecarIsolated ? " · 旁路隔离" : "";
            Draw.Text(g, TrimText(g, badge + r.VerdictTitle + modeNote, _fTitle, titleW), _fTitle, accent,
                new RectangleF(16, BannerTop + 8f, titleW, 20));
            Draw.Text(g, TrimText(g, r.VerdictDetail, _fTiny, titleW), _fTiny, Theme.Dim,
                new RectangleF(16, BannerTop + 31f, titleW, 18));

            if (showJump)
            {
                Theme.Fill(g, _hoverHit == "jumpNodes" ? Theme.Accent : Theme.AccentSoft, SwitchNodesBtnRect, 7f);
                Draw.Text(g, Compact ? "换备用节点 →" : "切换备用节点 →", _fTiny,
                    _hoverHit == "jumpNodes" ? Color.White : Theme.Accent, SwitchNodesBtnRect, Draw.Center);
            }
        }

        void DrawDualMiddleCards(Graphics g)
        {
            var r = _report;
            float lw = DashW;
            bool compact = Compact;

            float leftW = compact ? lw : (lw - 12f) / 2f;
            float leftY = MiddleTop;
            float rightX = compact ? 0f : (leftW + 12f);
            float rightY = compact ? (MiddleTop + MiddleCardH + 6f) : MiddleTop;
            float rightW = compact ? lw : (lw - rightX);

            // 卡 1：出口 IP 与综合风险评分
            var leftCard = new RectangleF(0, leftY, leftW, MiddleCardH);
            Theme.Fill(g, Theme.Card, leftCard, 12f);
            Theme.Stroke(g, Theme.Line, leftCard, 12f);
            Draw.Text(g, "出口 IP 与综合风险评分", _fBold, Theme.Fg, new RectangleF(14, leftY + (compact ? 6f : 8f), leftW - 28f, 18));

            if (r == null || string.IsNullOrEmpty(r.Ip))
            {
                Draw.Text(g, r != null && !string.IsNullOrEmpty(r.Error) ? r.Error : "等待出口 IP 探测结果…",
                    _fSmall, Theme.Dim, new RectangleF(14, leftY + (compact ? 32f : 40f), leftW - 28f, 40));
            }
            else
            {
                Draw.Text(g, r.Ip, _fBig, Theme.Fg, new RectangleF(14, leftY + (compact ? 23f : 28f), Math.Max(120f, leftW - 104f), 25));
                Theme.Fill(g, _hoverHit == "copyIp" ? Theme.BtnHover : Theme.Btn, CopyIpBtnRect, 6f);
                Theme.Stroke(g, Theme.Line, CopyIpBtnRect, 6f);
                Draw.Text(g, "复制 IP", _fTiny, Theme.Fg, CopyIpBtnRect, Draw.Center);

                string regionDisplay = string.IsNullOrEmpty(r.CountryName) ? r.CountryCode : r.CountryName;
                if (!string.IsNullOrEmpty(r.City)) regionDisplay += " · " + r.City;
                string badgeTxt = r.RoutedDirect ? "直连(CN)" : (r.RegionMatched ? "✓ 地区相符" : "! 地区漂移");
                Color bBg = (r.RoutedDirect || !r.RegionMatched) ? Theme.DangerSoft : Theme.OkSoft;
                Color bFg = (r.RoutedDirect || !r.RegionMatched) ? Theme.Danger : Theme.Ok;
                var bRect = new RectangleF(leftW - 96f, leftY + (compact ? 49f : 58f), 82f, 20f);
                Theme.Fill(g, bBg, bRect, 10f);
                Draw.Text(g, badgeTxt, _fTiny, bFg, bRect, Draw.Center);

                Draw.Text(g, TrimText(g, "归属：" + regionDisplay, _fSmall, Math.Max(100f, leftW - 118f)), _fSmall, Theme.Fg,
                    new RectangleF(14, leftY + (compact ? 50f : 59f), Math.Max(100f, leftW - 114f), 18));

                string asnIsp = (string.IsNullOrEmpty(r.Asn) ? "" : (r.Asn + " ")) +
                                (string.IsNullOrEmpty(r.Isp) ? "" : r.Isp);
                Draw.Text(g, TrimText(g, "属性：" + r.IpKindLabel + (asnIsp.Length > 0 ? (" · " + asnIsp) : ""), _fTiny, leftW - 28f),
                    _fTiny, Theme.Dim, new RectangleF(14, leftY + (compact ? 70f : 81f), leftW - 28f, 16));

                int score = r.RiskScore >= 0 ? r.RiskScore : 0;
                Color scoreCol = score >= 65 ? Theme.Danger : (score >= 30 ? Theme.Warn : Theme.Ok);
                string lvlWord = score >= 65 ? "高风险" : (score >= 30 ? "中风险" : "低风险");
                Draw.Text(g, r.RiskScore >= 0 ? ("风险评分：" + score + " / 100（" + lvlWord + "）") : "风险评分暂无数据",
                    _fTiny, scoreCol, new RectangleF(14, leftY + (compact ? 88f : 104f), leftW - 28f, 16));

                var track = new RectangleF(14, leftY + (compact ? 107f : 123f), leftW - 28f, 8);
                Theme.Fill(g, Theme.SubCard, track, 4f);
                float fillW = Math.Max(score > 0 ? 10f : 0f, track.Width * (score / 100f));
                if (fillW > 0f)
                    Theme.Fill(g, scoreCol, new RectangleF(track.X, track.Y, fillW, track.Height), 4f);
            }

            // 卡 2：传输品质与通道安全
            var rightCard = new RectangleF(rightX, rightY, rightW, MiddleCardH);
            Theme.Fill(g, Theme.Card, rightCard, 12f);
            Theme.Stroke(g, Theme.Line, rightCard, 12f);
            Draw.Text(g, "传输品质与通道安全", _fBold, Theme.Fg, new RectangleF(rightX + 14, rightY + (compact ? 6f : 8f), rightW - 28f, 18));

            if (r == null)
            {
                Draw.Text(g, "检测后展示节点延迟、网络抖动、丢包率、代理协议端口及 IPv6 泄漏状态。",
                    _fSmall, Theme.Dim, new RectangleF(rightX + 14, rightY + (compact ? 32f : 40f), rightW - 28f, 48));
                return;
            }

            string avgStr = r.Net.AvgMs > 0 ? (r.Net.AvgMs + " ms") : (r.Net.Sent > 0 ? "超时" : "-");
            Color avgCol = r.Net.AvgMs <= 0 ? Theme.Danger : (r.Net.AvgMs < 180 ? Theme.Ok : (r.Net.AvgMs < 360 ? Theme.Fg : Theme.Warn));
            Draw.Text(g, avgStr, _fBig, avgCol, new RectangleF(rightX + 14, rightY + (compact ? 23f : 28f), 120, 25));
            Draw.Text(g, TrimText(g, "平均延迟 · 抖动 " + (r.Net.JitterMs >= 0 ? r.Net.JitterMs + "ms" : "-") +
                         " · 丢包 " + r.Net.LossPercent + "%", _fSmall, rightW - 28f),
                _fSmall, Theme.Fg, new RectangleF(rightX + 14, rightY + (compact ? 50f : 59f), rightW - 28f, 18));

            string baseInfo = r.IsSidecarIsolated
                ? ("隔离沙箱端口 " + r.MixedPort + "（主客户端未切节点）")
                : ((r.TunEnabled ? ("TUN (" + (r.TunStack.Length > 0 ? r.TunStack : "已开") + ")") : "系统代理模式") +
                   " · " + (r.DnsEnabled ? "DNS 已接管" : "DNS 未接管") +
                   (r.MixedPort > 0 ? (" · 端口 " + r.MixedPort) : ""));
            Draw.Text(g, TrimText(g, "通道底座：" + baseInfo, _fTiny, rightW - 28f), _fTiny, Theme.Dim,
                new RectangleF(rightX + 14, rightY + (compact ? 70f : 81f), rightW - 28f, 16));

            string cfText = !string.IsNullOrEmpty(r.CfColo)
                ? ("边缘 POP：" + r.CfColo + (r.Cloudflare.LatencyMs > 0 ? (" · " + r.Cloudflare.LatencyMs + "ms") : ""))
                : "边缘 POP：待验证";
            Draw.Text(g, TrimText(g, cfText, _fTiny, rightW - 28f), _fTiny, Theme.Dim,
                new RectangleF(rightX + 14, rightY + (compact ? 86f : 98f), rightW - 28f, 15));

            var leakBox = new RectangleF(rightX + 14, rightY + (compact ? 103f : 116f), rightW - 28f, compact ? 19f : 22f);
            if (r.DirectIpv6Leak)
            {
                Theme.Fill(g, Theme.DangerSoft, leakBox, 6f);
                string leakTxt = "! 存在本地 IPv6 直连泄漏：" + TrimText(g, r.LeakedIpv6, _fTiny, Math.Max(40f, rightW - 180f));
                Draw.Text(g, leakTxt, _fTiny, Theme.Danger, leakBox, Draw.Center);
            }
            else if (!r.Ipv6LeakChecked)
            {
                Theme.Fill(g, Theme.SubCard, leakBox, 6f);
                string skipTxt = r.IsSidecarIsolated
                    ? "— 旁路隔离未测宿主直连 IPv6（切为主节点可复核）"
                    : "— 本次未核验 IPv6 直连泄漏（可单检复核）";
                Draw.Text(g, TrimText(g, skipTxt, _fTiny, Math.Max(60f, rightW - 36f)), _fTiny, Theme.Dim, leakBox, Draw.Center);
            }
            else
            {
                Theme.Fill(g, Theme.OkSoft, leakBox, 6f);
                Draw.Text(g, "✓ 已核验：无本地 IPv6 直连泄漏风险", _fTiny, Theme.Ok, leakBox, Draw.Center);
            }
        }

        void DrawServiceRowCards(Graphics g)
        {
            var r = _report;
            float lw = DashW;
            bool compact = Compact;
            const float gap = 8f;
            int cols = compact ? 2 : 4;
            float tileW = (lw - gap * (cols - 1)) / cols;
            float cardH = ServiceCardH;

            var tiles = new[]
            {
                new { Name = "Google 搜索", Gate = r?.GoogleSearch },
                new { Name = "Google AI (Gemini)", Gate = r?.GeminiApi },
                new { Name = "OpenAI / ChatGPT", Gate = r?.OpenAiApi },
                new { Name = "Claude (Anthropic)", Gate = r?.ClaudeApi }
            };

            for (int i = 0; i < 4; i++)
            {
                int col = i % cols;
                int row = i / cols;
                float x = col * (tileW + gap);
                float y = ServiceTop + row * (cardH + gap);
                var rect = new RectangleF(x, y, tileW, cardH);
                Theme.Fill(g, Theme.Card, rect, 11f);

                var gate = tiles[i].Gate;
                bool ok = gate != null && gate.Tested && gate.Ok;
                bool blk = gate != null && gate.Tested && gate.Blocked;
                Color border = blk ? Theme.Danger : (ok ? Theme.Ok : Theme.Line);
                Theme.Stroke(g, border, rect, 11f, blk ? 1.2f : 1f);

                Draw.Text(g, TrimText(g, tiles[i].Name, _fSmall, tileW - 22f), _fSmall, Theme.Fg,
                    new RectangleF(x + 12, y + 9f, tileW - 24, 17));

                if (gate == null || !gate.Tested)
                {
                    Draw.Text(g, "待检测", _fBold, Theme.Dim, new RectangleF(x + 12, y + 32f, tileW - 24, 20));
                    Draw.Text(g, "主流服务可用性", _fTiny, Theme.Dim, new RectangleF(x + 12, y + cardH - 28f, tileW - 24, 15));
                }
                else
                {
                    Color stateCol = ok ? Theme.Ok : (blk ? Theme.Danger : Theme.Warn);
                    string bigState = ok ? "✓ 正常放行" : (blk ? "✕ 风控/地区拦截" : "! 连接异常");
                    Draw.Text(g, bigState, _fBold, stateCol, new RectangleF(x + 12, y + 32f, tileW - 24, 20));
                    Draw.Text(g, TrimText(g, gate.Detail, _fTiny, tileW - 24f), _fTiny, Theme.Dim,
                        new RectangleF(x + 12, y + cardH - 28f, tileW - 24, 15));
                }
            }
        }

        // ---------------------------------------------------------------- 全量节点评估矩阵

        void DrawRankSummaryBar(Graphics g)
        {
            float lw = LogicalW;
            bool compact = Compact;
            var card = new RectangleF(0, RankBarTop, lw, RankBarH);
            Theme.Fill(g, Theme.Card, card, 10f);
            Theme.Stroke(g, Theme.Line, card, 10f);

            int tested = _reportsByNode.Count;
            int all4 = _reportsByNode.Values.Count(x => !x.RoutedDirect && x.MainstreamReadyCount == 4);
            int lowOrMed = _reportsByNode.Values.Count(x => !x.RoutedDirect && (x.Level == NodeQuality.RiskLevel.Low || x.Level == NodeQuality.RiskLevel.Medium));

            string msg = _batchScanning
                ? ("正在隔离评估 (" + _batchDone + "/" + _batchTotal + ")：" + TrimText(g, _batchCurrentNode, _fSmall, 160f))
                : (tested == 0
                    ? "尚未运行批量评估 — 点击右上「全量节点评估」输出综合榜单"
                    : ("已评估 " + tested + " 个 · 中低风控 " + lowOrMed + " 个 · 四项全通 " + all4 + " 个"));
            float msgW = compact ? (lw - 24f) : Math.Max(140f, RankFilterRect.X - 20f);
            Draw.Text(g, TrimText(g, msg, _fSmall, msgW), _fSmall,
                _batchScanning ? Theme.Accent : Theme.Fg, new RectangleF(12, RankBarTop + 9f, msgW, 18));

            string filterLabel = _rankFilterMode == 1 ? "仅看中低风控 ✓" : (_rankFilterMode == 2 ? "仅看四项全通 ✓" : "筛选：全部结果");
            Color fBg = _rankFilterMode > 0 ? Theme.AccentSoft : (_hoverHit == "rankFilter" ? Theme.Hover : Theme.Btn);
            Theme.Fill(g, fBg, RankFilterRect, 6.5f);
            Theme.Stroke(g, _rankFilterMode > 0 ? Theme.Accent : Theme.Line, RankFilterRect, 6.5f);
            Draw.Text(g, filterLabel, _fTiny,
                _rankFilterMode > 0 ? Theme.Accent : Theme.Dim, RankFilterRect, Draw.Center);

            bool hasPool = lowOrMed > 0;
            Color pBg = hasPool ? (_hoverHit == "applyPool" ? Theme.Hover : Theme.AccentSoft) : Theme.Btn;
            Theme.Fill(g, pBg, ApplyPoolRect, 6.5f);
            Theme.Stroke(g, hasPool ? Theme.Accent : Theme.Line, ApplyPoolRect, 6.5f);
            Draw.Text(g, "推荐节点设为备选池", _fTiny,
                hasPool ? Theme.Accent : Theme.Dim, ApplyPoolRect, Draw.Center);
        }

        void DrawRankTable(Graphics g)
        {
            float lw = LogicalW;
            float listW = MaxScroll > 0 ? lw - 10f : lw;
            bool wide = Wide;

            if (wide)
            {
                float opX = listW - 102f;
                float msX = opX - 52f;
                float gateRight = msX - 6f;
                float gateX = gateRight - 196f;
                float rkX = gateX - 88f;
                float regX = rkX - 134f;
                float nameMaxW = Math.Max(90f, regX - 20f);

                Draw.Text(g, "节点名称", _fTiny, Theme.Dim, new RectangleF(12, RankHeaderTop, nameMaxW, 16));
                Draw.Text(g, "落地国家 / 出口 IP", _fTiny, Theme.Dim, new RectangleF(regX, RankHeaderTop, 130, 16));
                Draw.Text(g, "风险评级", _fTiny, Theme.Dim, new RectangleF(rkX, RankHeaderTop, 82, 16));
                Draw.Text(g, "Google", _fTiny, Theme.Dim, new RectangleF(gateX, RankHeaderTop, 48, 16), Draw.Center);
                Draw.Text(g, "Gemini", _fTiny, Theme.Dim, new RectangleF(gateX + 49, RankHeaderTop, 48, 16), Draw.Center);
                Draw.Text(g, "OpenAI", _fTiny, Theme.Dim, new RectangleF(gateX + 98, RankHeaderTop, 48, 16), Draw.Center);
                Draw.Text(g, "Claude", _fTiny, Theme.Dim, new RectangleF(gateX + 147, RankHeaderTop, 48, 16), Draw.Center);
                Draw.Text(g, "延迟", _fTiny, Theme.Dim, new RectangleF(msX, RankHeaderTop, 48, 16), Draw.Right);
                Draw.Text(g, "操作", _fTiny, Theme.Dim, new RectangleF(opX, RankHeaderTop, 94, 16), Draw.Center);
            }

            var area = RankAreaRect;
            var clip = g.Save();
            g.SetClip(area);

            if (_rankView.Count == 0)
            {
                string empty = _batchScanning
                    ? "正在通过隔离探测通道扫描首批节点，马上呈现评估矩阵…"
                    : (_reportsByNode.Count > 0 ? "当前筛选条件下暂无节点，切换筛选按钮可查看全部" : "点击「全量节点评估」，全部节点将按风险纯净度与服务放行率自动排榜");
                Draw.Text(g, empty, _fSmall, Theme.Dim, area, Draw.Center);
                g.Restore(clip);
                return;
            }

            int first = Math.Max(0, (int)Math.Floor(_scroll / RankRowH));
            int last = Math.Min(_rankView.Count - 1, (int)Math.Ceiling((_scroll + area.Height) / RankRowH));

            for (int i = first; i <= last; i++)
            {
                var rep = _rankView[i];
                float ry = area.Y + i * RankRowH - _scroll;
                var rowRect = new RectangleF(0, ry + 1.5f, listW, RankRowH - 3f);
                bool isCur = string.Equals(rep.TargetNode, _currentOutbound, StringComparison.OrdinalIgnoreCase);
                bool hoverRow = _hoverRow == i;
                Theme.Fill(g, isCur ? Theme.AccentSoft : (hoverRow ? Theme.Hover : Theme.Card), rowRect, 9f);
                Theme.Stroke(g, isCur ? Theme.Accent : Theme.Line, rowRect, 9f);

                if (wide)
                    DrawRankWideRow(g, rep, i, ry, listW, isCur);
                else
                    DrawRankCompactRow(g, rep, i, ry, listW, isCur);
            }

            if (MaxScroll > 0)
            {
                float h = area.Height;
                float total = _rankView.Count * RankRowH;
                float th = Math.Max(26f, h * (h / total));
                float ty = area.Y + (MaxScroll <= 0 ? 0 : (_scroll / MaxScroll) * (h - th));
                Theme.Fill(g, Theme.Dark ? Color.FromArgb(110, 255, 255, 255) : Color.FromArgb(90, 0, 0, 0),
                    new RectangleF(lw - 6f, ty, 5f, th), 3f);
            }
            g.Restore(clip);
        }

        void DrawRankWideRow(Graphics g, NodeQuality.QualityReport rep, int i, float ry, float listW, bool isCur)
        {
            float opX = listW - 102f;
            float msX = opX - 52f;
            float gateRight = msX - 6f;
            float gateX = gateRight - 196f;
            float rkX = gateX - 88f;
            float regX = rkX - 134f;

            float nx = 12f;
            if (isCur)
            {
                using (var dot = new SolidBrush(Theme.Accent))
                    g.FillEllipse(dot, nx, ry + 17.5f, 7f, 7f);
                nx += 11f;
            }
            Draw.Text(g, TrimText(g, Draw.CleanEmoji(rep.TargetNode), _fSmall, Math.Max(60f, regX - nx - 6f)), _fBold,
                isCur ? Theme.Accent : Theme.Fg, new RectangleF(nx, ry + 12f, Math.Max(60f, regX - nx - 4f), 18f));

            string regStr = rep.RoutedDirect ? "直连(CN)"
                : ((string.IsNullOrEmpty(rep.CountryCode) ? "?" : rep.CountryCode) +
                   (string.IsNullOrEmpty(rep.Ip) ? "" : (" · " + rep.Ip)));
            Draw.Text(g, TrimText(g, regStr, _fTiny, 128f), _fTiny,
                rep.RoutedDirect || !rep.RegionMatched ? Theme.Danger : Theme.Dim,
                new RectangleF(regX, ry + 13f, 130, 16));

            DrawRiskPill(g, rep, new RectangleF(rkX, ry + 10f, 82, 22));

            DrawGateCell(g, new RectangleF(gateX, ry + 12f, 48, 18), rep.GoogleSearch, rep.RoutedDirect);
            DrawGateCell(g, new RectangleF(gateX + 49, ry + 12f, 48, 18), rep.GeminiApi, rep.RoutedDirect);
            DrawGateCell(g, new RectangleF(gateX + 98, ry + 12f, 48, 18), rep.OpenAiApi, rep.RoutedDirect);
            DrawGateCell(g, new RectangleF(gateX + 147, ry + 12f, 48, 18), rep.ClaudeApi, rep.RoutedDirect);

            string msStr = rep.Net != null && rep.Net.AvgMs > 0 ? (rep.Net.AvgMs + "ms") : "超时";
            Color msCol = rep.Net != null && rep.Net.AvgMs > 0 ? (rep.Net.AvgMs < 180 ? Theme.Ok : Theme.Fg) : Theme.Danger;
            Draw.Text(g, msStr, _fTiny, msCol, new RectangleF(msX, ry + 13f, 48, 16), Draw.Right);

            DrawRowActionButtons(g, i, opX, ry + 9f, isCur);
        }

        void DrawRankCompactRow(Graphics g, NodeQuality.QualityReport rep, int i, float ry, float listW, bool isCur)
        {
            float opX = listW - 102f;
            float msX = opX - 54f;

            // 行 1：节点名 + 延迟 + 切换/详情按钮
            float nx = 12f;
            if (isCur)
            {
                using (var dot = new SolidBrush(Theme.Accent))
                    g.FillEllipse(dot, nx, ry + 14f, 7f, 7f);
                nx += 11f;
            }
            Draw.Text(g, TrimText(g, Draw.CleanEmoji(rep.TargetNode), _fBold, Math.Max(70f, msX - nx - 6f)), _fBold,
                isCur ? Theme.Accent : Theme.Fg, new RectangleF(nx, ry + 8f, Math.Max(70f, msX - nx - 4f), 19f));

            string msStr = rep.Net != null && rep.Net.AvgMs > 0 ? (rep.Net.AvgMs + "ms") : "超时";
            Color msCol = rep.Net != null && rep.Net.AvgMs > 0 ? (rep.Net.AvgMs < 180 ? Theme.Ok : Theme.Fg) : Theme.Danger;
            Draw.Text(g, msStr, _fTiny, msCol, new RectangleF(msX, ry + 9f, 48, 17f), Draw.Right);

            DrawRowActionButtons(g, i, opX, ry + 6f, isCur);

            // 行 2：落地国家·IP + 风险评级 + 四门紧凑标识 (G✓ Ge✓ O✓ C✓)
            var rkRect = new RectangleF(listW - 228f, ry + 34f, 76f, 20f);
            float ipAvail = Math.Max(80f, rkRect.X - 16f);
            string regStr = rep.RoutedDirect ? "直连(CN)"
                : ((string.IsNullOrEmpty(rep.CountryCode) ? "?" : rep.CountryCode) +
                   (string.IsNullOrEmpty(rep.Ip) ? "" : (" · " + rep.Ip)));
            Draw.Text(g, TrimText(g, regStr, _fTiny, ipAvail), _fTiny,
                rep.RoutedDirect || !rep.RegionMatched ? Theme.Danger : Theme.Dim,
                new RectangleF(12f, ry + 35f, ipAvail, 17f));

            DrawRiskPill(g, rep, rkRect);

            float gx = rkRect.Right + 6f;
            DrawCompactGateTag(g, new RectangleF(gx, ry + 35f, 34f, 18f), "G", rep.GoogleSearch, rep.RoutedDirect);
            DrawCompactGateTag(g, new RectangleF(gx + 35f, ry + 35f, 38f, 18f), "Ge", rep.GeminiApi, rep.RoutedDirect);
            DrawCompactGateTag(g, new RectangleF(gx + 74f, ry + 35f, 34f, 18f), "O", rep.OpenAiApi, rep.RoutedDirect);
            DrawCompactGateTag(g, new RectangleF(gx + 109f, ry + 35f, 34f, 18f), "C", rep.ClaudeApi, rep.RoutedDirect);
        }

        void DrawRiskPill(Graphics g, NodeQuality.QualityReport rep, RectangleF rkRect)
        {
            Color rkBg = rep.Level == NodeQuality.RiskLevel.Low ? Theme.OkSoft
                : (rep.Level == NodeQuality.RiskLevel.High || rep.RoutedDirect ? Theme.DangerSoft : Theme.WarnSoft);
            Color rkFg = rep.Level == NodeQuality.RiskLevel.Low ? Theme.Ok
                : (rep.Level == NodeQuality.RiskLevel.High || rep.RoutedDirect ? Theme.Danger : Theme.Warn);
            string rkText = rep.RoutedDirect ? "直连失效"
                : (rep.RiskScore >= 0 ? (rep.RiskScore + "分·" + (rep.Level == NodeQuality.RiskLevel.Low ? "低" : rep.Level == NodeQuality.RiskLevel.Medium ? "中" : "高")) : "未通");
            Theme.Fill(g, rkBg, rkRect, 6f);
            Draw.Text(g, rkText, _fTiny, rkFg, rkRect, Draw.Center);
        }

        void DrawRowActionButtons(Graphics g, int i, float opX, float y, bool isCur)
        {
            var swRect = new RectangleF(opX, y, 44, 24);
            bool hSw = _hoverRow == i && _hoverHit == "rankSwitch";
            Theme.Fill(g, isCur ? Theme.AccentSoft : (hSw ? Theme.Accent : Theme.Btn), swRect, 6f);
            if (!isCur) Theme.Stroke(g, hSw ? Theme.Accent : Theme.Line, swRect, 6f);
            Draw.Text(g, isCur ? "在用" : "切换", _fTiny, isCur ? Theme.Accent : (hSw ? Color.White : Theme.Fg), swRect, Draw.Center);

            var dtRect = new RectangleF(opX + 48, y, 46, 24);
            bool hDt = _hoverRow == i && _hoverHit == "rankDetail";
            Theme.Fill(g, hDt ? Theme.BtnHover : Theme.Btn, dtRect, 6f);
            Theme.Stroke(g, Theme.Line, dtRect, 6f);
            Draw.Text(g, "详情", _fTiny, Theme.Fg, dtRect, Draw.Center);
        }

        void DrawCompactGateTag(Graphics g, RectangleF r, string prefix, NodeQuality.ServiceGateResult gate, bool direct)
        {
            if (direct || gate == null || !gate.Tested)
            {
                Draw.Text(g, prefix + "-", _fTiny, Theme.Dim, r, Draw.Center);
                return;
            }
            string mark = gate.Ok ? "✓" : (gate.Blocked ? "✕" : "!");
            Color col = gate.Ok ? Theme.Ok : (gate.Blocked ? Theme.Danger : Theme.Warn);
            Draw.Text(g, prefix + mark, _fTiny, col, r, Draw.Center);
        }

        void DrawGateCell(Graphics g, RectangleF r, NodeQuality.ServiceGateResult gate, bool direct)
        {
            if (direct || gate == null || !gate.Tested)
            {
                Draw.Text(g, "—", _fTiny, Theme.Dim, r, Draw.Center);
                return;
            }
            string mark = gate.Ok ? "✓" : (gate.Blocked ? "✕" : "!");
            Color col = gate.Ok ? Theme.Ok : (gate.Blocked ? Theme.Danger : Theme.Warn);
            Draw.Text(g, mark, _fBold, col, r, Draw.Center);
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
            if (HasContextualBack && BackBtnRect.Contains(x, y)) { hit = "back"; return; }
            if (ViewDetailTabRect.Contains(x, y)) { hit = "tabDetail"; return; }
            if (ViewRankTabRect.Contains(x, y)) { hit = "tabRank"; return; }
            if (TargetBtnRect.Contains(x, y)) { hit = "target"; return; }
            if (RegionBtnRect.Contains(x, y)) { hit = "region"; return; }
            if (InspectBtnRect.Contains(x, y)) { hit = "inspect"; return; }
            if (BatchScanBtnRect.Contains(x, y)) { hit = "batchScan"; return; }

            if (_subView == 1)
            {
                if (RankFilterRect.Contains(x, y)) { hit = "rankFilter"; return; }
                if (ApplyPoolRect.Contains(x, y)) { hit = "applyPool"; return; }
                if (!RankAreaRect.Contains(x, y) || _rankView.Count == 0) return;
                if (MaxScroll > 0 && x >= LogicalW - 10f) { hit = "scrollbar"; return; }
                int idx = (int)Math.Floor((y - RankListTop + _scroll) / RankRowH);
                if (idx < 0 || idx >= _rankView.Count) return;
                rowIdx = idx;
                float ly = (y - RankListTop + _scroll) - idx * RankRowH;
                float listW = MaxScroll > 0 ? LogicalW - 10f : LogicalW;
                float opX = listW - 102f;
                var rep = _rankView[idx];
                bool isCur = string.Equals(rep.TargetNode, _currentOutbound, StringComparison.OrdinalIgnoreCase);
                float btnH = Wide ? RankRowH : 36f;
                if (!isCur && new RectangleF(opX, 4, 44, btnH - 6).Contains(x, ly)) { hit = "rankSwitch"; return; }
                if (new RectangleF(opX + 48, 4, 46, btnH - 6).Contains(x, ly)) { hit = "rankDetail"; return; }
                hit = "rankRow";
                return;
            }

            float sy = y + _scroll;
            if (_report != null && (_report.Level == NodeQuality.RiskLevel.High || !_report.RegionMatched || _report.RoutedDirect) &&
                SwitchNodesBtnRect.Contains(x, sy))
            { hit = "jumpNodes"; return; }
            if (_report != null && !string.IsNullOrEmpty(_report.Ip) && CopyIpBtnRect.Contains(x, sy))
            { hit = "copyIp"; return; }
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (MaxScroll <= 0) return;
            _scrollTarget -= Math.Sign(e.Delta) * 44f;
            ClampScroll();
            _scrollTimer.Start();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_draggingThumb)
            {
                float dy = (e.Y / Theme.S) - _dragStartY;
                float areaH = RankAreaRect.Height;
                float th = Math.Max(26f, areaH * (areaH / Math.Max(1f, _rankView.Count * RankRowH)));
                float trackH = Math.Max(1f, areaH - th);
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
                Cursor = hit.Length > 0 ? Cursors.Hand : Cursors.Default;
                Invalidate();
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (_draggingThumb) { _draggingThumb = false; Capture = false; }
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

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            string hit; int row;
            HitTest(e.Location, out hit, out row);

            if (hit == "back") { GoBack(); return; }
            if (hit == "scrollbar")
            {
                _draggingThumb = true;
                _dragStartY = e.Y / Theme.S;
                _dragStartScroll = _scroll;
                Capture = true;
                return;
            }
            if (hit == "tabDetail") { _subView = 0; _detailFromMatrix = false; _scroll = _scrollTarget = 0f; ClampScroll(); Invalidate(); return; }
            if (hit == "tabRank") { _subView = 1; _detailFromMatrix = false; _scroll = _scrollTarget = 0f; ClampScroll(); Invalidate(); return; }
            if (hit == "target") { ShowTargetPickerMenu(); return; }
            if (hit == "region") { DesiredRegionCycled?.Invoke(); Invalidate(); return; }
            if (hit == "inspect") { InspectRequested?.Invoke(EffectiveTargetNode); return; }
            if (hit == "batchScan") { BatchRiskScanRequested?.Invoke(); return; }
            if (hit == "rankFilter") { _rankFilterMode = (_rankFilterMode + 1) % 3; RebuildRankView(); Invalidate(); return; }
            if (hit == "applyPool")
            {
                var good = _reportsByNode.Values
                    .Where(x => !x.RoutedDirect && (x.Level == NodeQuality.RiskLevel.Low || x.Level == NodeQuality.RiskLevel.Medium))
                    .OrderBy(x => (int)x.Level)
                    .ThenByDescending(x => x.MainstreamReadyCount)
                    .ThenBy(x => x.RiskScore)
                    .Select(x => x.TargetNode)
                    .ToList();
                ApplyLowRiskPoolRequested?.Invoke(good);
                return;
            }
            if (hit == "jumpNodes") { JumpToNodesRequested?.Invoke(); return; }
            if (hit == "copyIp" && _report != null && !string.IsNullOrEmpty(_report.Ip))
            { CopyIpRequested?.Invoke(_report.Ip); return; }

            if (row >= 0 && row < _rankView.Count)
            {
                var rep = _rankView[row];
                if (hit == "rankSwitch") SwitchNodeRequested?.Invoke(rep.TargetNode);
                else if (hit == "rankDetail" || hit == "rankRow")
                {
                    _savedRankScroll = _scroll;
                    _report = rep;
                    _selectedTargetNode = rep.TargetNode;
                    _subView = 0;
                    _detailFromMatrix = true;
                    _scroll = _scrollTarget = 0f;
                    ClampScroll();
                    Invalidate();
                }
            }
        }

        void ShowTargetPickerMenu()
        {
            var menu = new ContextMenuStrip { ShowImageMargin = false };
            menu.Closed += (s, e) =>
            {
                var c = (ContextMenuStrip)s;
                BeginInvoke(new Action(() => { if (!c.IsDisposed) c.Dispose(); }));
            };

            if (!string.IsNullOrEmpty(_currentOutbound))
            {
                var curItem = new ToolStripMenuItem("● 当前在用：" + Draw.CleanEmoji(_currentOutbound));
                curItem.Click += (s, e) => { _selectedTargetNode = _currentOutbound; Invalidate(); };
                menu.Items.Add(curItem);
                menu.Items.Add(new ToolStripSeparator());
            }

            foreach (var n in _availableNodes.Where(x => !x.IsGroup && !ProbeSidecar.IsInformationalNodeName(x.Name)).Take(28))
            {
                string nm = n.Name;
                string suffix = n.EffectiveDelayMs > 0 ? (" · " + n.EffectiveDelayMs + "ms") : "";
                var item = new ToolStripMenuItem((n.IsCurrent ? "✓ " : "   ") + Draw.CleanEmoji(nm) + suffix);
                item.Click += (s, e) => { _selectedTargetNode = nm; Invalidate(); };
                menu.Items.Add(item);
            }

            menu.Show(this, new Point(Theme.Px(TargetBtnRect.X), Theme.Px(TargetBtnRect.Bottom + 4)));
        }
    }
}
