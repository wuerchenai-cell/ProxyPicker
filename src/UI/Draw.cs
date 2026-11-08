using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace ProxyPicker.UI
{
    /// <summary>所有自绘原件：开关、图标、文字、滚动条。</summary>
    internal static class Draw
    {
        public static readonly StringFormat Left = new StringFormat(StringFormatFlags.NoWrap)
        {
            Alignment = StringAlignment.Near,
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter
        };

        public static readonly StringFormat Center = new StringFormat(StringFormatFlags.NoWrap)
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center
        };

        public static readonly StringFormat Right = new StringFormat(StringFormatFlags.NoWrap)
        {
            Alignment = StringAlignment.Far,
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter
        };

        /// <summary>多行自动换行，最后一行放不下时按词截断。</summary>
        public static readonly StringFormat Wrap = new StringFormat
        {
            Alignment = StringAlignment.Near,
            LineAlignment = StringAlignment.Near,
            Trimming = StringTrimming.EllipsisWord
        };

        public static void Text(Graphics g, string s, Font f, Color c, RectangleF r, StringFormat fmt = null)
        {
            using (var b = new SolidBrush(c))
                g.DrawString(s ?? "", f, b, r, fmt ?? Left);
        }

        public static void TextWrap(Graphics g, string s, Font f, Color c, RectangleF r)
        {
            Text(g, s, f, c, r, Wrap);
        }

        /// <summary>开关：44x24，圆角胶囊 + 带阴影的滑块。</summary>
        public static void Toggle(Graphics g, RectangleF r, float t, bool enabled)
        {
            float radius = r.Height / 2f;
            Color track = Theme.Blend(Theme.TrackOff, Theme.TrackOn, t);
            if (!enabled) track = Theme.Blend(track, Theme.Card, 0.45f);

            g.SmoothingMode = SmoothingMode.AntiAlias;
            Theme.Fill(g, track, r, radius);

            float pad = 3f;
            float thumb = r.Height - pad * 2;
            float x = r.X + pad + (r.Width - pad * 2 - thumb) * t;

            // 滑块阴影
            using (var sb = new SolidBrush(Color.FromArgb(enabled ? 46 : 24, 0, 0, 0)))
                g.FillEllipse(sb, x, r.Y + pad + 1f, thumb, thumb);

            using (var tb = new SolidBrush(enabled ? Color.White : Theme.Blend(Color.White, Theme.Bg, 0.25f)))
                g.FillEllipse(tb, x, r.Y + pad, thumb, thumb);
        }

        /// <summary>小标签胶囊（"运行中"/"保护"）。</summary>
        public static void Pill(Graphics g, string text, Font f, Color fg, Color bg, PointF at)
        {
            SizeF sz = g.MeasureString(text, f);
            var r = new RectangleF(at.X, at.Y, sz.Width + 14f, sz.Height + 2f);
            Theme.Fill(g, bg, r, r.Height / 2f);
            Text(g, text, f, fg, r, Center);
        }

        /// <summary>圆角按钮，返回其矩形。</summary>
        public static RectangleF Button(Graphics g, string text, Font f, RectangleF r,
            Color bg, Color fg, Color border, bool hover)
        {
            if (hover) bg = Theme.Blend(bg, fg, 0.06f);
            Theme.Fill(g, bg, r, 8f);
            if (border != Color.Empty) Theme.Stroke(g, border, r, 8f);
            Text(g, text, f, fg, r, Center);
            return r;
        }

        /// <summary>右侧细滚动条。</summary>
        public static void ScrollBar(Graphics g, RectangleF track, float viewRatio, float posRatio)
        {
            if (viewRatio >= 0.999f) return;
            float h = Math.Max(28f, track.Height * viewRatio);
            float y = track.Y + (track.Height - h) * posRatio;
            var thumb = new RectangleF(track.X, y, track.Width, h);
            using (var b = new SolidBrush(Color.FromArgb(Theme.Dark ? 90 : 70, Theme.Dim)))
                using (var p = Theme.RoundRect(thumb, track.Width / 2f))
                    g.FillPath(b, p);
        }

        /// <summary>从同一套多尺寸资源绘制标题栏图标，返回值由调用者释放。</summary>
        public static Bitmap AppIcon(int size)
        {
            using (var icon = AppIco(size))
            {
                var bitmap = icon.ToBitmap();
                if (bitmap.Width == size && bitmap.Height == size) return bitmap;
                try { return new Bitmap(bitmap, new Size(size, size)); }
                finally { bitmap.Dispose(); }
            }
        }

        static readonly Lazy<byte[]> IconData = new Lazy<byte[]>(() =>
        {
            using (var stream = typeof(Draw).Assembly.GetManifestResourceStream("ProxyPicker.Assets.ProxyPicker.ico"))
            {
                if (stream == null) throw new System.IO.FileNotFoundException("缺少嵌入图标资源");
                using (var buffer = new System.IO.MemoryStream())
                {
                    stream.CopyTo(buffer);
                    return buffer.ToArray();
                }
            }
        });

        public static Icon AppIco(int size)
        {
            if (size < 1 || size > 1024) throw new ArgumentOutOfRangeException(nameof(size));
            byte[] data = IconData.Value;
            int count = BitConverter.ToUInt16(data, 4);
            int best = -1, bestSize = 0, distance = int.MaxValue;
            for (int i = 0; i < count; i++)
            {
                int frameSize = data[6 + i * 16] == 0 ? 256 : data[6 + i * 16];
                int delta = Math.Abs(frameSize - size);
                if (delta < distance || (delta == distance && frameSize > bestSize))
                {
                    best = i; bestSize = frameSize; distance = delta;
                }
            }
            if (best < 0) throw new System.IO.InvalidDataException("图标没有可用帧");
            int entry = 6 + best * 16;
            int length = BitConverter.ToInt32(data, entry + 8);
            int offset = BitConverter.ToInt32(data, entry + 12);
            // 构造单帧容器，避免系统图标选择算法偏向较小帧；同距时优先较大帧。
            byte[] single = new byte[22 + length];
            single[2] = 1; single[4] = 1;
            Buffer.BlockCopy(data, entry, single, 6, 16);
            Buffer.BlockCopy(BitConverter.GetBytes(22), 0, single, 18, 4);
            Buffer.BlockCopy(data, offset, single, 22, length);
            using (var stream = new System.IO.MemoryStream(single, false))
            using (var icon = new Icon(stream, new Size(bestSize, bestSize)))
                return (Icon)icon.Clone();
        }

        public static void Hint(Graphics g)
        {
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        }

        /// <summary>Toast 左侧的圆形标记：0=对勾 1=感叹号 2=信息点。</summary>
        public static void Badge(Graphics g, int kind, RectangleF r, Color c)
        {
            using (var pen = new Pen(c, 1.8f))
            {
                pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round;
                pen.LineJoin = LineJoin.Round;
                float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f;
                if (kind == 0)
                {
                    g.DrawLines(pen, new[]
                    {
                        new PointF(cx - 4.6f, cy + 0.2f),
                        new PointF(cx - 1.4f, cy + 3.4f),
                        new PointF(cx + 4.8f, cy - 3.4f)
                    });
                }
                else if (kind == 1)
                {
                    g.DrawLine(pen, cx, cy - 4.6f, cx, cy + 1.2f);
                    using (var b = new SolidBrush(c)) g.FillEllipse(b, cx - 1f, cy + 3.2f, 2f, 2f);
                }
                else
                {
                    g.DrawLine(pen, cx, cy - 4.8f, cx, cy + 4.8f);
                    using (var b = new SolidBrush(c))
                    {
                        g.FillEllipse(b, cx - 1.1f, cy - 4.9f, 2.2f, 2.2f);
                        g.FillEllipse(b, cx - 1.1f, cy + 3.6f, 2.2f, 2.2f);
                    }
                }
            }
        }

        /// <summary>可旋转的刷新箭头：圆弧 + 箭头，角度变化即转圈。</summary>
        public static void RefreshArrow(Graphics g, float cx, float cy, float radius, float angle, Color c)
        {
            using (var pen = new Pen(c, 1.7f))
            {
                pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round;
                // 留一个缺口，看起来才像「刷新」而不是一个圈
                g.DrawArc(pen, cx - radius, cy - radius, radius * 2f, radius * 2f, angle + 40f, 275f);

                // 箭头：沿圆弧末端的切线方向画一个小折角
                double end = (angle + 315f) * Math.PI / 180.0;
                float ax = cx + (float)Math.Cos(end) * radius;
                float ay = cy + (float)Math.Sin(end) * radius;
                double tangent = end + Math.PI / 2.0;
                float dx = (float)Math.Cos(tangent), dy = (float)Math.Sin(tangent);
                float nx = (float)Math.Cos(end), ny = (float)Math.Sin(end);
                g.DrawLine(pen, ax, ay, ax - dx * 4.6f + nx * 1.4f, ay - dy * 4.6f + ny * 1.4f);
                g.DrawLine(pen, ax, ay, ax - dx * 4.6f - nx * 1.4f, ay - dy * 4.6f - ny * 1.4f);
            }
        }

        /// <summary>迷你折线：两条通道的速率走势，作为摘要卡的背景装饰。</summary>
        public static void Spark(Graphics g, float[] data, RectangleF r, Color color, float peak)
        {
            if (data == null || data.Length < 2 || r.Width <= 4) return;
            if (peak <= 0.0001f) peak = 1f;

            var pts = new PointF[data.Length];
            float step = r.Width / (data.Length - 1);
            for (int i = 0; i < data.Length; i++)
            {
                float v = data[i] < 0 ? 0 : data[i];
                float h = (float)Math.Log(1 + v / peak * 9.0) / 2.302585f; // log 压缩，小流量也看得见
                if (h > 1f) h = 1f;
                pts[i] = new PointF(r.X + step * i, r.Bottom - 1.5f - h * (r.Height - 3f));
            }

            using (var path = new GraphicsPath())
            {
                path.AddLines(pts);
                var fill = (GraphicsPath)path.Clone();
                fill.AddLine(pts[pts.Length - 1].X, r.Bottom, r.X, r.Bottom);
                fill.CloseFigure();
                using (var b = new LinearGradientBrush(r,
                    Color.FromArgb(52, color), Color.FromArgb(0, color), 90f))
                    g.FillPath(b, fill);
                fill.Dispose();

                using (var pen = new Pen(Color.FromArgb(150, color), 1.3f))
                {
                    pen.LineJoin = LineJoin.Round;
                    g.DrawPath(pen, path);
                }
            }

            using (var b = new SolidBrush(Color.FromArgb(190, color)))
                g.FillEllipse(b, pts[pts.Length - 1].X - 1.8f, pts[pts.Length - 1].Y - 1.8f, 3.6f, 3.6f);
        }

        /// <summary>拖动排序的抓手（悬停时才显示，暗示「这行能拖」）。</summary>
        public static void Grip(Graphics g, RectangleF r, Color c)
        {
            using (var b = new SolidBrush(c))
            {
                for (int row = 0; row < 3; row++)
                    for (int col = 0; col < 2; col++)
                        g.FillEllipse(b, r.X + col * 4.2f, r.Y + row * 4.2f, 1.9f, 1.9f);
            }
        }

        /// <summary>柔和的卡片投影。</summary>
        public static void Shadow(Graphics g, RectangleF r, float radius, int alpha)
        {
            for (int i = 3; i >= 1; i--)
            {
                var sh = new RectangleF(r.X - i, r.Y - i + 1.5f, r.Width + i * 2, r.Height + i * 2);
                using (var b = new SolidBrush(Color.FromArgb(alpha / (i + 1), 0, 0, 0)))
                using (var p = Theme.RoundRect(sh, radius + i))
                    g.FillPath(b, p);
            }
        }
    }
}
