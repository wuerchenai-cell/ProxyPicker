using System;
using System.Collections.Generic;
using System.Drawing;

namespace ProxyPicker.UI
{
    /// <summary>
    /// 轻量浮层通知：不弹系统对话框、不抢焦点，从底部滑上来再淡出。
    /// 由 MainForm 的动画时钟驱动，静止时不刷新。
    /// </summary>
    internal sealed class ToastHost
    {
        public sealed class Item
        {
            public string Title = "";
            public string Sub = "";
            public Color Accent;
            public int Kind;          // 0=成功 1=提醒 2=信息
            public double Age;
        }

        const double Life = 3.4;
        const double FadeOut = 0.42;

        readonly List<Item> _items = new List<Item>();
        public const float CardH = 44f;
        const float Gap = 6f;

        public bool Any { get { return _items.Count > 0; } }

        public void Show(string title, string sub, Color accent, int kind)
        {
            var it = new Item { Title = title ?? "", Sub = sub ?? "", Accent = accent, Kind = kind, Age = 0 };
            _items.Add(it);
            while (_items.Count > 3) _items.RemoveAt(0);
        }

        public void Clear() { _items.Clear(); }

        /// <summary>推进动画；返回是否还需要继续刷新。</summary>
        public bool Tick(double dt)
        {
            for (int i = _items.Count - 1; i >= 0; i--)
            {
                _items[i].Age += dt;
                if (_items[i].Age >= Life) _items.RemoveAt(i);
            }
            return _items.Count > 0;
        }

        /// <summary>在指定区域底部绘制。area 为列表面板范围。</summary>
        public void Render(Graphics g, RectangleF area)
        {
            if (_items.Count == 0) return;

            float h = CardH;
            float bottom = area.Bottom - 10f;
            for (int i = _items.Count - 1; i >= 0; i--)
            {
                var it = _items[i];
                float alpha = 1f;
                float rise = 0f;

                if (it.Age < 0.20)
                {
                    float t = (float)(it.Age / 0.20);
                    alpha = t;
                    rise = (1f - EaseOut(t)) * 14f;
                }
                else if (it.Age > Life - FadeOut)
                {
                    float t = (float)((Life - it.Age) / FadeOut);
                    alpha = Math.Max(0f, t);
                    rise = (1f - t) * 6f;
                }

                float w = Math.Min(340f, area.Width - 32f);
                var rect = new RectangleF(area.X + (area.Width - w) / 2f, bottom - h + rise, w, h);
                DrawOne(g, it, rect, alpha);
                bottom -= h + Gap;
            }
        }

        void DrawOne(Graphics g, Item it, RectangleF r, float alpha)
        {
            int a = (int)(255 * alpha);
            if (a <= 2) return;

            // 阴影：两层，模拟轻微悬浮
            using (var sh = new SolidBrush(Color.FromArgb((int)(26 * alpha), 0, 0, 0)))
            using (var p = Theme.RoundRect(new RectangleF(r.X, r.Y + 2, r.Width, r.Height), 11f))
                g.FillPath(sh, p);

            var bg = Theme.Dark ? Theme.FromHex("#23272E") : Color.White;
            using (var b = new SolidBrush(Color.FromArgb(a, bg)))
            using (var p = Theme.RoundRect(r, 11f))
                g.FillPath(b, p);
            using (var pen = new Pen(Color.FromArgb((int)(70 * alpha), it.Accent), 1f))
            using (var p = Theme.RoundRect(r, 11f))
                g.DrawPath(pen, p);

            var badge = new RectangleF(r.X + 11, r.Y + (r.Height - 22) / 2f, 22, 22);
            using (var b = new SolidBrush(Color.FromArgb((int)(40 * alpha), it.Accent)))
                g.FillEllipse(b, badge);
            Draw.Badge(g, it.Kind, badge, Color.FromArgb(a, it.Accent));

            float x = badge.Right + 10;
            float w = r.Right - x - 12;
            var fTitle = Theme.Ui(12.5f, FontStyle.Bold);
            var fSub = Theme.Ui(10.5f);
            bool hasSub = !string.IsNullOrEmpty(it.Sub);
            try
            {
                if (hasSub)
                {
                    Draw.Text(g, it.Title, fTitle, Color.FromArgb(a, Theme.Fg), new RectangleF(x, r.Y + 6, w, 17));
                    Draw.Text(g, it.Sub, fSub, Color.FromArgb(a, Theme.Dim), new RectangleF(x, r.Y + 23, w, 15));
                }
                else
                {
                    Draw.Text(g, it.Title, fTitle, Color.FromArgb(a, Theme.Fg), new RectangleF(x, r.Y, w, r.Height));
                }
            }
            finally { fTitle.Dispose(); fSub.Dispose(); }
        }

        static float EaseOut(float t) { return 1f - (1f - t) * (1f - t) * (1f - t); }
    }
}
