using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace ProxyPicker.UI
{
    internal sealed class AppRow
    {
        public string Exe = "";
        public string Name = "";
        public string Description = "";
        public string Path = "";
        public string Alias = "";      // 已安装扫描到的显示名，仅用于搜索匹配
        /// <summary>同名 exe 出现在多个不同路径：勾一个会连带命中另一个，必须提示。</summary>
        public bool NameConflict;
        public List<string> AllPaths = new List<string>();
        /// <summary>同一安装目录里也在运行的其它 exe，可一键一起勾上。</summary>
        public List<string> Peers = new List<string>();
        public int Running;
        public bool Proxy;
        public bool Known;
        /// <summary>名单里手改的策略（DIRECT / 某个组名）；空 = 没写过。保存时要原样带回去。</summary>
        public string Policy = "";
        public bool Common;
        public float Anim;
        public int Usage;
        public long LastUsed;

        /// <summary>
        /// 覆盖状态：0 未知/不显示，1 内核看得到（规则真的作用在它身上），
        /// 2 内核看不到（进程在跑，但流量没进内核，当前底座下勾了也白勾）。
        /// </summary>
        public int Coverage;

        /// 悬停高亮的渐入量（0~1）。以前是瞬间切换，鼠标扫过时整行"啪"一下亮起来。
        public float Hover;
    }

    internal sealed class ReorderEventArgs : EventArgs
    {
        public string Exe;
        public string BeforeExe;   // null 表示放到末尾
    }

    internal sealed class RowEventArgs : EventArgs
    {
        public AppRow Row;
        public Point Screen;
    }

    /// <summary>
    /// 整块自绘列表：开关动画、入场动画、滚动缓动、拖动排序、右键菜单、键盘选中。
    /// 所有动画共用一个 16ms 计时器，静止时自动停表，不空转占 CPU。
    /// </summary>
    internal sealed class AppList : Control
    {
        public const float RowH = 54f;

        readonly List<AppRow> _rows = new List<AppRow>();
        readonly Timer _anim;
        readonly ToolTip _tip = new ToolTip();
        readonly Font _fName = Theme.Ui(13.5f);
        readonly Font _fSub = Theme.Ui(11f);
        readonly Font _fTag = Theme.Ui(10f);
        readonly Font _fEmpty = Theme.Ui(12.5f);

        float _offset;
        float _scrollTarget;
        float _enter;
        int _hover = -1;
        int _sel = -1;

        bool _pressed;
        Point _pressPt;
        int _pressIdx = -1;
        bool _drag;
        int _dragIdx = -1;
        string _dragExe;
        int _drop = -1;
        Point _pointer;

        public event EventHandler Toggled;
        public event EventHandler<ReorderEventArgs> Reordered;
        public event EventHandler<RowEventArgs> ContextRequested;

        public string EmptyText = "没有匹配的软件";
        public string EmptyHint = "换个标签，或直接在搜索框输入软件名";

        /// <summary>由 MainForm 注入的浮层（Toast 等），画在列表最上面。</summary>
        public Action<Graphics, RectangleF> Overlay;

        /// <summary>已保存的代理名单；用来给「改了但没保存」的行加标记。</summary>
        public HashSet<string> SavedProxy = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public AppList()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Bg;
            TabStop = false;
            _anim = new Timer { Interval = 16 };
            _anim.Tick += (s, e) => Step();
            _tip.AutoPopDelay = 7000;
            _tip.InitialDelay = 620;
            _tip.ReshowDelay = 120;
        }

        public void SetRows(List<AppRow> rows, bool animate = true)
        {
            _pressed = false;
            _pressIdx = -1;
            _dragExe = null;
            if (Capture) Capture = false;
            _rows.Clear();
            _rows.AddRange(rows);
            _offset = 0;
            _scrollTarget = 0;
            _hover = -1;
            _sel = -1;
            _drag = false;
            _dragIdx = -1;
            _drop = -1;
            _enter = animate ? 0f : 1f;
            _tip.Hide(this);
            if (animate) _anim.Start();
            Invalidate();
        }

        public AppRow Selected
        {
            get { return _sel >= 0 && _sel < _rows.Count ? _rows[_sel] : null; }
        }

        public int RowCount { get { return _rows.Count; } }

        /// <summary>当前可见行的顺序（拖动之后就是用户眼前看到的顺序）。</summary>
        public List<string> VisibleOrder
        {
            get
            {
                var list = new List<string>(_rows.Count);
                foreach (var r in _rows) list.Add(r.Exe);
                return list;
            }
        }

        // ---------------------------------------------------------------- 几何

        float LogicalW { get { return Width / Theme.S; } }
        float LogicalH { get { return Height / Theme.S; } }
        float ContentH { get { return _rows.Count * RowH + 12f; } }
        float ViewH { get { return Math.Max(0f, LogicalH - 8f); } }
        float MaxOffset { get { return Math.Max(0f, ContentH - ViewH); } }
        RectangleF Outer { get { return new RectangleF(8, 4, LogicalW - 16, LogicalH - 8); } }
        float RowsTop { get { return Outer.Y + 6f - _offset; } }

        // ---------------------------------------------------------------- 动画

        void Step()
        {
            bool need = false;

            for (int i = 0; i < _rows.Count; i++)
            {
                var r = _rows[i];
                float target = r.Proxy ? 1f : 0f;
                if (Math.Abs(r.Anim - target) < 0.02f)
                {
                    if (r.Anim != target) { r.Anim = target; need = true; }
                }
                else
                {
                    r.Anim += (target - r.Anim) * 0.38f;
                    need = true;
                }

                // 悬停/选中高亮的渐入渐出：目标和值对齐后才停表
                float hTarget = (i == _hover || i == _sel) ? 1f : 0f;
                if (Math.Abs(r.Hover - hTarget) < 0.01f)
                {
                    if (r.Hover != hTarget) { r.Hover = hTarget; need = true; }
                }
                else
                {
                    r.Hover += (hTarget - r.Hover) * 0.26f;
                    need = true;
                }
            }

            if (_enter < 1f) { _enter = Math.Min(1f, _enter + 0.085f); need = true; }

            if (Math.Abs(_scrollTarget - _offset) > 0.4f)
            {
                _offset += (_scrollTarget - _offset) * 0.34f;
                need = true;
            }
            else if (_offset != _scrollTarget) { _offset = _scrollTarget; need = true; }

            // 拖动到上下边缘时自动滚动
            if (_drag)
            {
                float y = _pointer.Y / Theme.S;
                float edge = 26f;
                if (y < Outer.Y + edge) { _scrollTarget = Math.Max(0f, _scrollTarget - 9f); need = true; }
                else if (y > Outer.Bottom - edge) { _scrollTarget = Math.Min(MaxOffset, _scrollTarget + 9f); need = true; }
            }

            if (need) Invalidate();
            else _anim.Stop();
        }

        void Wake() { if (!_anim.Enabled) _anim.Start(); }

        // ---------------------------------------------------------------- 绘制

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Draw.Hint(g);
            g.Clear(Theme.Bg);
            g.ScaleTransform(Theme.S, Theme.S);

            var outer = Outer;
            if (outer.Width <= 0 || outer.Height <= 0) return;

            Theme.Fill(g, Theme.Card, outer, 12f);
            Theme.Stroke(g, Theme.Line, outer, 12f);

            var state = g.Save();
            g.SetClip(new RectangleF(outer.X + 1, outer.Y + 1, outer.Width - 2, outer.Height - 2));

            float top = RowsTop;
            for (int i = 0; i < _rows.Count; i++)
            {
                float y = top + i * RowH;
                if (y + RowH < outer.Y - 4) continue;
                if (y > outer.Bottom + 4) break;
                DrawRow(g, _rows[i], i, new RectangleF(outer.X + 6, y, outer.Width - 12, RowH));
            }

            if (_rows.Count == 0)
            {
                DrawEmpty(g, outer);
            }
            else if (_drag && _drop >= 0)
            {
                float y = top + _drop * RowH;
                using (var pen = new Pen(Theme.Accent, 2f))
                {
                    pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round;
                    g.DrawLine(pen, outer.X + 20, y, outer.Right - 20, y);
                }
                using (var b = new SolidBrush(Theme.Accent))
                {
                    g.FillEllipse(b, outer.X + 14, y - 2.5f, 5f, 5f);
                    g.FillEllipse(b, outer.Right - 19, y - 2.5f, 5f, 5f);
                }
            }

            g.Restore(state);

            if (MaxOffset > 0.5f)
            {
                var track = new RectangleF(outer.Right - 7, outer.Y + 8, 3.5f, outer.Height - 16);
                Draw.ScrollBar(g, track, ViewH / ContentH, _offset / Math.Max(0.001f, MaxOffset));
            }

            if (Overlay != null) Overlay(g, outer);
        }

        void DrawEmpty(Graphics g, RectangleF outer)
        {
            float cx = outer.X + outer.Width / 2f;
            // 一个空的「开关」线稿，比纯文字更像设计过的空状态
            var cap = new RectangleF(cx - 30, outer.Y + 46, 60, 30);
            using (var pen = new Pen(Theme.Line, 2f))
            using (var p = Theme.RoundRect(cap, 15f))
                g.DrawPath(pen, p);
            using (var b = new SolidBrush(Theme.Line))
                g.FillEllipse(b, cap.X + 5, cap.Y + 5, 20, 20);

            Draw.Text(g, EmptyText, _fEmpty, Theme.Dim,
                new RectangleF(outer.X + 24, outer.Y + 90, outer.Width - 48, 26), Draw.Center);
            Draw.Text(g, EmptyHint, _fSub, Theme.Dim,
                new RectangleF(outer.X + 24, outer.Y + 118, outer.Width - 48, 24), Draw.Center);
        }

        static Color Alpha(Color c, float a)
        {
            if (a >= 0.999f) return c;
            if (a <= 0.001f) return Color.FromArgb(0, c);
            return Color.FromArgb((int)(255 * a), c);
        }

        void DrawRow(Graphics g, AppRow r, int index, RectangleF row)
        {
            // 入场：整体淡入 + 轻微上移，越靠下的行稍晚一点
            float enterA = 1f;
            if (_enter < 1f)
            {
                float t = Math.Min(1f, _enter * 1.35f - Math.Min(index, 12) * 0.045f);
                if (t <= 0f) return;
                enterA = t;
                row = new RectangleF(row.X, row.Y + (1f - t) * 7f, row.Width, row.Height);
            }

            if (r.Proxy)
            {
                Color baseBg = Theme.Blend(Theme.Card, Theme.AccentSoft, 0.62f * r.Anim);
                using (var b = new SolidBrush(Alpha(baseBg, enterA)))
                using (var p = Theme.RoundRect(row, 8f))
                    g.FillPath(b, p);
            }
            float hv = r.Hover * (index == _sel ? 1f : 0.85f);
            if (hv > 0.01f)
            {
                using (var b = new SolidBrush(Alpha(Theme.Hover, enterA * hv)))
                using (var p = Theme.RoundRect(row, 8f))
                    g.FillPath(b, p);
            }
            if (r.Proxy)
            {
                var bar = new RectangleF(row.X + 3, row.Y + (RowH - 18) / 2f, 3f, 18f);
                Theme.Fill(g, Alpha(Theme.Accent, enterA), bar, 1.5f);
            }
            else if (r.Proxy != SavedProxy.Contains(r.Exe))
            {
                // 改过但还没保存：琥珀色标记，避免「点了却没生效」的困惑
                var bar = new RectangleF(row.X + 3, row.Y + (RowH - 18) / 2f, 3f, 18f);
                Theme.Fill(g, Alpha(Theme.Warn, enterA), bar, 1.5f);
            }

            var sw = new RectangleF(row.X + 12, row.Y + (RowH - 23) / 2f, 42, 23);
            Draw.Toggle(g, sw, r.Anim, true);

            float x = sw.Right + 14;
            bool showState = r.Running > 0;
            float rightW = showState ? (r.Coverage == 2 ? 78f : 66f) : 16f;
            var nameRect = new RectangleF(x, row.Y + 7, row.Width - (x - row.X) - rightW - 14, 20);
            Draw.Text(g, r.Name, _fName, Alpha(Theme.Fg, enterA), nameRect);

            if (showState)
            {
                // 绿点 = 内核真的看得到它；橙点 = 进程在跑但流量没进内核，勾了也没用。
                // 以前只有「运行中」一种状态，系统代理模式下会让人以为配置生效了。
                bool blind = r.Coverage == 2;
                Color tone = blind ? Theme.Warn : Theme.Ok;
                string label = blind ? "没走代理" : (r.Running > 1 ? r.Running + " 个" : "运行中");
                float dotY = row.Y + 16;
                using (var b = new SolidBrush(Alpha(tone, enterA)))
                    g.FillEllipse(b, row.Right - (blind ? 66 : 54), dotY, 5f, 5f);
                Draw.Text(g, label, _fTag, Alpha(tone, enterA),
                    new RectangleF(row.Right - (blind ? 58 : 46), row.Y + 8, blind ? 56 : 38, 17), Draw.Right);
            }

            var subRect = new RectangleF(x, row.Y + 29, row.Width - (x - row.X) - 30, 17);
            string sub = !string.IsNullOrEmpty(r.Description) ? r.Description
                       : !string.IsNullOrEmpty(r.Alias) ? r.Alias
                       : r.Exe;
            Draw.Text(g, sub, _fSub, Alpha(Theme.Dim, enterA), subRect);

            if (index == _hover && !_drag)
                Draw.Grip(g, new RectangleF(row.Right - 20, row.Y + (RowH - 10) / 2f, 10, 10),
                    Alpha(Theme.Dim, 0.55f * enterA));

            if (index < _rows.Count - 1)
            {
                using (var pen = new Pen(Alpha(Theme.Line, enterA * 0.9f)))
                    g.DrawLine(pen, x, row.Bottom, row.Right - 8, row.Bottom);
            }
        }

        // ---------------------------------------------------------------- 命中

        int HitTest(Point p)
        {
            float x = p.X / Theme.S, y = p.Y / Theme.S;
            var outer = Outer;
            if (x < outer.X || x > outer.Right || y < outer.Y || y > outer.Bottom) return -1;
            int idx = (int)((y - RowsTop) / RowH);
            return idx < 0 || idx >= _rows.Count ? -1 : idx;
        }

        int DropIndex(Point p)
        {
            float y = p.Y / Theme.S;
            int idx = (int)Math.Round((y - RowsTop) / RowH);
            if (idx < 0) idx = 0;
            if (idx > _rows.Count) idx = _rows.Count;
            return idx;
        }

        // ---------------------------------------------------------------- 交互

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            _pointer = e.Location;

            if (Enabled && _pressed && !_drag && _pressIdx >= 0 && _pressIdx < _rows.Count)
            {
                // 只有「按住左键 + 明显往下/往上走」才算拖动排序。
                // 少了 (e.Button & Left) 这个判断，SetCapture 触发的无按键 MOUSEMOVE
                // 会被当成拖动，单击就变成了「把这一行往上挪一格」（这个 bug 很隐蔽）。
                int dx = Math.Abs(e.X - _pressPt.X), dy = Math.Abs(e.Y - _pressPt.Y);
                if ((e.Button & MouseButtons.Left) != 0 && dy > 5 && dy > dx)
                {
                    _drag = true;
                    _dragIdx = _pressIdx;
                    _dragExe = _rows[_pressIdx].Exe;
                    _drop = _pressIdx;
                    _hover = _pressIdx;
                    _tip.Hide(this);
                    Wake();
                }
            }

            if (_drag)
            {
                _drop = DropIndex(e.Location);
                Cursor = Cursors.SizeAll;
                Invalidate();
                return;
            }

            int idx = HitTest(e.Location);
            if (idx != _hover)
            {
                _hover = idx;
                if (idx >= 0)
                {
                    var r = _rows[idx];
                    string path = string.IsNullOrEmpty(r.Path) ? "当前未运行" : r.Path;
                    _tip.SetToolTip(this, r.Name + "\n" + r.Exe + "\n" + path + "\n单击切换 · 按住上下拖动排序");
                }
                else _tip.Hide(this);
                Invalidate();
                Wake();   // 高亮是渐入的，得让动画表跑起来
            }
            Cursor = idx >= 0 ? Cursors.Hand : Cursors.Default;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (!Enabled) return;
            int idx = HitTest(e.Location);

            if (e.Button == MouseButtons.Right)
            {
                if (idx >= 0 && ContextRequested != null)
                {
                    _sel = idx;
                    Invalidate();
                    Wake();
                    ContextRequested(this, new RowEventArgs { Row = _rows[idx], Screen = PointToScreen(e.Location) });
                }
                return;
            }
            if (e.Button != MouseButtons.Left) return;

            _sel = idx;
            _pressIdx = idx;
            _pressPt = e.Location;
            _pressed = idx >= 0;
            _drag = false;
            if (_pressed) Capture = true;
            Invalidate();
            Wake();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;

            bool wasDrag = _drag;
            int from = _dragIdx;
            string exe = _dragExe;
            int to = _drop;

            _drag = false;
            _dragIdx = -1;
            _dragExe = null;
            _drop = -1;
            _pressed = false;
            if (Capture) Capture = false;
            if (!Enabled) { _pressIdx = -1; return; }

            if (wasDrag && from >= 0 && from < _rows.Count && to >= 0)
            {
                var row = _rows[from];
                _rows.RemoveAt(from);
                int insert = to > from ? to - 1 : to;
                if (insert < 0) insert = 0;
                if (insert > _rows.Count) insert = _rows.Count;
                _rows.Insert(insert, row);

                string before = insert + 1 < _rows.Count ? _rows[insert + 1].Exe : null;
                _sel = insert;
                Invalidate();
                if (Reordered != null)
                    Reordered(this, new ReorderEventArgs { Exe = exe, BeforeExe = before });
                return;
            }

            int idx = HitTest(e.Location);
            if (idx >= 0 && idx == _pressIdx)
            {
                var r = _rows[idx];
                r.Proxy = !r.Proxy;
                Wake();
                Invalidate();
                if (Toggled != null) Toggled(this, EventArgs.Empty);
            }
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_drag) return;
            _hover = -1;
            Cursor = Cursors.Default;
            _tip.Hide(this);
            Invalidate();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (MaxOffset <= 0.5f) return;
            _scrollTarget = Math.Max(0f, Math.Min(MaxOffset, _scrollTarget - e.Delta / 120f * 78f));
            Wake();
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            int idx = HitTest(e.Location);
            if (idx < 0) return;
            var r = _rows[idx];
            if (!string.IsNullOrEmpty(r.Path) && System.IO.File.Exists(r.Path))
            {
                try
                {
                    System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + r.Path + "\"");
                }
                catch { }
            }
        }

        // ---------------------------------------------------------------- 键盘

        public void MoveSelection(int delta)
        {
            if (_rows.Count == 0) return;
            _sel = _sel < 0 ? 0 : Math.Max(0, Math.Min(_rows.Count - 1, _sel + delta));
            EnsureVisible(_sel);
            Invalidate();
        }

        public void ToggleSelected()
        {
            if (!Enabled) return;
            if (_sel < 0 || _sel >= _rows.Count) return;
            var r = _rows[_sel];
            r.Proxy = !r.Proxy;
            Wake();
            Invalidate();
            if (Toggled != null) Toggled(this, EventArgs.Empty);
        }

        void EnsureVisible(int idx)
        {
            float top = idx * RowH;
            float bottom = top + RowH;
            if (top < _scrollTarget) _scrollTarget = top;
            else if (bottom > _scrollTarget + ViewH - 10f) _scrollTarget = bottom - ViewH + 10f;
            _scrollTarget = Math.Max(0f, Math.Min(MaxOffset, _scrollTarget));
            Wake();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            _scrollTarget = Math.Min(_scrollTarget, MaxOffset);
            _offset = Math.Min(_offset, MaxOffset);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _anim.Stop(); _anim.Dispose(); _tip.Dispose();
                _fName.Dispose(); _fSub.Dispose(); _fTag.Dispose(); _fEmpty.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
