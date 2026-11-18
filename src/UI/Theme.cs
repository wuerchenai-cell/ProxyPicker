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

        public static Color Bg, Card, SubCard, Line, Fg, Dim, Accent, AccentSoft,
                            Ok, OkSoft, Warn, WarnSoft, Danger, DangerSoft,
                            Hover, TrackOff, TrackOn, Btn, BtnHover, FooterBg;

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
            return DetectWindowScale(IntPtr.Zero);
        }

        /// <summary>按指定窗口句柄所在屏幕读取 DPI（支持多屏混合 DPI 初始落点），句柄为空时回退系统 DPI。</summary>
        internal static float DetectWindowScale(IntPtr hwnd)
        {
            uint dpi = 0;
            if (hwnd != IntPtr.Zero)
            {
                try { dpi = GetDpiForWindow(hwnd); } catch { dpi = 0; }
            }
            if (dpi < 72)
            {
                try
                {
                    // GetDpiForSystem 是 Win10 1607+ 的系统 DPI，比 GDI+ 的无窗口 Graphics 可靠
                    dpi = GetDpiForSystem();
                }
                catch
                {
                    try
                    {
                        using (var g = Graphics.FromHwnd(hwnd)) dpi = (uint)Math.Round(g.DpiX);
                    }
                    catch { dpi = 96; }
                }
            }
            if (dpi < 72) dpi = 96;

            float s = dpi / 96f;
            if (s < 1f) s = 1f;
            if (s > 2.5f) s = 2.5f;

            return s;
        }

        [DllImport("user32.dll")]
        static extern uint GetDpiForSystem();
        [DllImport("user32.dll")]
        static extern uint GetDpiForWindow(IntPtr hwnd);

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
                Bg = FromHex("#11141A");
                Card = FromHex("#181C24");
                SubCard = FromHex("#1F242E");
                Line = FromHex("#2A303B");
                Fg = FromHex("#EDF0F5");
                Dim = FromHex("#949EAE");
                Accent = FromHex("#4E8DF5");
                AccentSoft = FromHex("#1A2B48");
                Ok = FromHex("#4ADE80");
                OkSoft = Color.FromArgb(36, 74, 222, 128);
                Warn = FromHex("#F59E0B");
                WarnSoft = Color.FromArgb(40, 245, 158, 11);
                Danger = FromHex("#F87171");
                DangerSoft = Color.FromArgb(44, 248, 113, 113);
                Hover = FromHex("#212630");
                TrackOff = FromHex("#3A414E");
                TrackOn = Accent;
                Btn = FromHex("#232833");
                BtnHover = FromHex("#2D3341");
                FooterBg = FromHex("#181C24");
            }
            else
            {
                Bg = FromHex("#F4F6F9");
                Card = FromHex("#FFFFFF");
                SubCard = FromHex("#F8FAFC");
                Line = FromHex("#E2E6EE");
                Fg = FromHex("#151820");
                Dim = FromHex("#646D7A");
                Accent = FromHex("#2563EB");
                AccentSoft = FromHex("#EBF2FE");
                Ok = FromHex("#15803D");
                OkSoft = Color.FromArgb(20, 21, 128, 61);
                Warn = FromHex("#B45309");
                WarnSoft = Color.FromArgb(22, 180, 83, 9);
                Danger = FromHex("#DC2626");
                DangerSoft = Color.FromArgb(20, 220, 38, 38);
                Hover = FromHex("#F1F4F9");
                TrackOff = FromHex("#CBD2DC");
                TrackOn = Accent;
                Btn = FromHex("#F6F8FB");
                BtnHover = FromHex("#EBEEF4");
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
