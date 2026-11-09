using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace ProxyPicker.UI
{
    /// <summary>
    /// 统一配色与字体。跟随系统浅色/深色主题（读注册表 AppsUseLightTheme）。
    /// </summary>
    internal static class Theme
    {
        public static bool Dark { get; private set; }

        public static Color Bg, Card, Line, Fg, Dim, Accent, AccentSoft, Ok, Warn, Hover, TrackOff, TrackOn, Btn, BtnHover, FooterBg;

        public static string Family { get; private set; }

        /// <summary>DPI 缩放因子（125% 屏 = 1.25）。绘制时统一 ScaleTransform，坐标按 96dpi 逻辑像素写。</summary>
        public static float S { get; private set; }

        /// <summary>窗口被拖到另一块不同 DPI 的屏幕时，按新 DPI 重新计算缩放与字体。</summary>
        public static void SetScale(float s)
        {
            if (s < 1f) s = 1f;
            if (s > 2.5f) s = 2.5f;
            S = s;
        }

        [DllImport("user32.dll")]
        static extern bool SystemParametersInfo(uint uiAction, uint uiParam, ref int pvParam, uint fWinIni);

        static Theme()
        {
            S = DetectScale();
            Load();
        }

        static float DetectScale()
        {
            uint dpi = 96;
            try
            {
                // GetDpiForSystem 是 Win10 1607+ 的系统 DPI，比 GDI+ 的无窗口 Graphics 可靠
                dpi = GetDpiForSystem();
            }
            catch
            {
                try
                {
                    using (var g = Graphics.FromHwnd(IntPtr.Zero)) dpi = (uint)Math.Round(g.DpiX);
                }
                catch { dpi = 96; }
            }
            if (dpi < 72) dpi = 96;

            float s = dpi / 96f;
            if (s < 1f) s = 1f;
            if (s > 2.5f) s = 2.5f;

            return s;
        }

        [DllImport("user32.dll")]
        static extern uint GetDpiForSystem();

        /// <summary>逻辑像素 -> 设备像素。</summary>
        public static int Px(float logical)
        {
            return (int)Math.Round(logical * S);
        }

        public static void Load()
        {
            Dark = DetectDark();
            if (Dark)
            {
                Bg = FromHex("#131519");
                Card = FromHex("#1B1E23");
                Line = FromHex("#2A2F36");
                Fg = FromHex("#E8EAED");
                Dim = FromHex("#98A1AC");
                Accent = FromHex("#5B8FF9");
                AccentSoft = FromHex("#1C2B45");
                Ok = FromHex("#57C98A");
                Warn = FromHex("#F0A75A");
                Hover = FromHex("#22262C");
                TrackOff = FromHex("#3A4048");
                TrackOn = Accent;
                Btn = FromHex("#242830");
                BtnHover = FromHex("#2E333C");
                FooterBg = FromHex("#1B1E23");
            }
            else
            {
                Bg = FromHex("#F4F5F8");
                Card = FromHex("#FFFFFF");
                Line = FromHex("#E4E7EC");
                Fg = FromHex("#17191C");
                Dim = FromHex("#6B7280");
                Accent = FromHex("#2F6FEB");
                AccentSoft = FromHex("#E9F0FE");
                Ok = FromHex("#15803D");
                Warn = FromHex("#B45309");
                Hover = FromHex("#F2F4F7");
                TrackOff = FromHex("#C9CED6");
                TrackOn = Accent;
                Btn = FromHex("#F7F8FA");
                BtnHover = FromHex("#EDEFF3");
                FooterBg = FromHex("#FFFFFF");
            }

            Family = PickFamily();
        }

        static string PickFamily()
        {
            string[] want = { "Microsoft YaHei UI", "Microsoft YaHei", "Segoe UI" };
            foreach (var f in want)
            {
                try
                {
                    using (var ff = new FontFamily(f)) { return f; }
                }
                catch { }
            }
            return "Segoe UI";
        }

        public static Color FromHex(string hex)
        {
            int v = int.Parse(hex.TrimStart('#'), System.Globalization.NumberStyles.HexNumber);
            return Color.FromArgb(255, (v >> 16) & 0xFF, (v >> 8) & 0xFF, v & 0xFF);
        }

        static bool DetectDark()
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    if (key != null)
                    {
                        object v = key.GetValue("AppsUseLightTheme");
                        if (v is int) return (int)v == 0;
                    }
                }
            }
            catch { }
            return false;
        }

        /// <summary>字号按逻辑像素给（配合 ScaleTransform），不按 pt，避免二次缩放。</summary>
        public static Font Ui(float pixelSize, FontStyle style = FontStyle.Regular)
        {
            return new Font(Family, pixelSize, style, GraphicsUnit.Pixel);
        }

        // ---------------------------------------------------------------- 绘图工具

        public static GraphicsPath RoundRect(RectangleF r, float radius)
        {
            var path = new GraphicsPath();
            if (radius <= 0.5f)
            {
                path.AddRectangle(r);
                return path;
            }
            float d = radius * 2f;
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        public static Color Blend(Color a, Color b, float t)
        {
            if (t <= 0) return a;
            if (t >= 1) return b;
            return Color.FromArgb(
                (int)(a.A + (b.A - a.A) * t),
                (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t),
                (int)(a.B + (b.B - a.B) * t));
        }

        public static void Fill(Graphics g, Color c, RectangleF r, float radius)
        {
            using (var p = RoundRect(r, radius))
            using (var b = new SolidBrush(c))
                g.FillPath(b, p);
        }

        public static void Stroke(Graphics g, Color c, RectangleF r, float radius, float width = 1f)
        {
            using (var p = RoundRect(r, radius))
            using (var pen = new Pen(c, width))
                g.DrawPath(pen, p);
        }
    }
}
