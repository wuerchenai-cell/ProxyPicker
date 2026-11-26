using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace ProxyPicker.App.Controls
{
    /// <summary>
    /// 高性能 WPF 矢量三路实时流量曲线控件（蓝=主代理，紫=独立固定IP，灰=本地直连）。
    /// 采用固定对数刻度防剧烈抖动，支持 100%/150% DPI 清晰矢量渲染与深/浅主题自动适配。
    /// </summary>
    public sealed class SparklineChart : FrameworkElement
    {
        const int MaxSamples = 48;
        readonly List<double> _proxySamples = new List<double>();
        readonly List<double> _staticSamples = new List<double>();
        readonly List<double> _directSamples = new List<double>();

        public static readonly DependencyProperty ProxyBrushProperty = DependencyProperty.Register(
            nameof(ProxyBrush), typeof(Brush), typeof(SparklineChart),
            new FrameworkPropertyMetadata(Brushes.DodgerBlue, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty StaticBrushProperty = DependencyProperty.Register(
            nameof(StaticBrush), typeof(Brush), typeof(SparklineChart),
            new FrameworkPropertyMetadata(Brushes.MediumPurple, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty DirectBrushProperty = DependencyProperty.Register(
            nameof(DirectBrush), typeof(Brush), typeof(SparklineChart),
            new FrameworkPropertyMetadata(Brushes.SlateGray, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty GridBrushProperty = DependencyProperty.Register(
            nameof(GridBrush), typeof(Brush), typeof(SparklineChart),
            new FrameworkPropertyMetadata(Brushes.LightGray, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty LabelBrushProperty = DependencyProperty.Register(
            nameof(LabelBrush), typeof(Brush), typeof(SparklineChart),
            new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

        public Brush ProxyBrush { get => (Brush)GetValue(ProxyBrushProperty); set => SetValue(ProxyBrushProperty, value); }
        public Brush StaticBrush { get => (Brush)GetValue(StaticBrushProperty); set => SetValue(StaticBrushProperty, value); }
        public Brush DirectBrush { get => (Brush)GetValue(DirectBrushProperty); set => SetValue(DirectBrushProperty, value); }
        public Brush GridBrush { get => (Brush)GetValue(GridBrushProperty); set => SetValue(GridBrushProperty, value); }
        public Brush LabelBrush { get => (Brush)GetValue(LabelBrushProperty); set => SetValue(LabelBrushProperty, value); }

        public SparklineChart()
        {
            for (int i = 0; i < MaxSamples; i++)
            {
                _proxySamples.Add(0);
                _staticSamples.Add(0);
                _directSamples.Add(0);
            }
            ClipToBounds = true;
        }

        /// <summary>推入最新一秒的三路速率（字节/秒）并触发重绘。</summary>
        public void PushSample(double proxyBps, double staticBps, double directBps)
        {
            Push(_proxySamples, Math.Max(0, proxyBps));
            Push(_staticSamples, Math.Max(0, staticBps));
            Push(_directSamples, Math.Max(0, directBps));
            InvalidateVisual();
        }

        static void Push(List<double> list, double val)
        {
            list.Add(val);
            while (list.Count > MaxSamples) list.RemoveAt(0);
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth;
            double h = ActualHeight;
            if (w < 24 || h < 20) return;

            double padTop = 18;
            double padBot = 5;
            double plotH = h - padTop - padBot;

            // 计算动态对数上限（至少 64 KB/s，避免极小背景噪声放大撑满画布）
            double peak = 64 * 1024;
            for (int i = 0; i < _proxySamples.Count; i++)
            {
                if (_proxySamples[i] > peak) peak = _proxySamples[i];
                if (_staticSamples[i] > peak) peak = _staticSamples[i];
                if (_directSamples[i] > peak) peak = _directSamples[i];
            }
            double logMax = Math.Log10(peak + 1024);

            // 两条淡虚线参考网格
            var gridPen = new Pen(GridBrush, 1) { DashStyle = new DashStyle(new[] { 3.0, 4.0 }, 0) };
            gridPen.Freeze();
            dc.DrawLine(gridPen, new Point(0, padTop + plotH * 0.35), new Point(w, padTop + plotH * 0.35));
            dc.DrawLine(gridPen, new Point(0, padTop + plotH * 0.72), new Point(w, padTop + plotH * 0.72));

            // 顺序绘线：直连（底） -> 主代理 -> 固定 IP（最清晰置顶）
            DrawSeries(dc, _directSamples, DirectBrush, 1.3, 0.08, w, padTop, plotH, logMax);
            DrawSeries(dc, _proxySamples, ProxyBrush, 1.8, 0.16, w, padTop, plotH, logMax);
            DrawSeries(dc, _staticSamples, StaticBrush, 1.8, 0.16, w, padTop, plotH, logMax);

            // 右上角轻量峰值标尺
            string peakLabel = "峰值 " + FormatBps(peak);
            var ft = new FormattedText(
                peakLabel,
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                new Typeface("Segoe UI"),
                10,
                LabelBrush,
                VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(ft, new Point(Math.Max(2, w - ft.Width - 4), 1));
        }

        static void DrawSeries(DrawingContext dc, List<double> samples, Brush strokeBrush, double thickness,
            double fillOpacity, double w, double padTop, double plotH, double logMax)
        {
            if (samples.Count < 2 || strokeBrush == null) return;
            double stepX = w / (samples.Count - 1);
            var lineGeo = new StreamGeometry();
            var areaGeo = new StreamGeometry();

            using (var lc = lineGeo.Open())
            using (var ac = areaGeo.Open())
            {
                for (int i = 0; i < samples.Count; i++)
                {
                    double norm = samples[i] <= 0 ? 0 : Math.Log10(samples[i] + 1024) / logMax;
                    norm = Math.Max(0, Math.Min(1, norm));
                    double x = i * stepX;
                    double y = padTop + plotH * (1.0 - norm);
                    var pt = new Point(x, y);
                    if (i == 0)
                    {
                        lc.BeginFigure(pt, false, false);
                        ac.BeginFigure(new Point(0, padTop + plotH), true, true);
                        ac.LineTo(pt, false, false);
                    }
                    else
                    {
                        lc.LineTo(pt, true, true);
                        ac.LineTo(pt, false, false);
                    }
                }
                ac.LineTo(new Point(w, padTop + plotH), false, false);
            }
            lineGeo.Freeze();
            areaGeo.Freeze();

            if (strokeBrush is SolidColorBrush scb)
            {
                var fill = new SolidColorBrush(Color.FromArgb((byte)(fillOpacity * 255), scb.Color.R, scb.Color.G, scb.Color.B));
                fill.Freeze();
                dc.DrawGeometry(fill, null, areaGeo);
            }

            var pen = new Pen(strokeBrush, thickness) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            pen.Freeze();
            dc.DrawGeometry(null, pen, lineGeo);
        }

        public static string FormatBps(double bps)
        {
            if (bps < 1024) return ((long)bps) + " B/s";
            if (bps < 1024 * 1024) return (bps / 1024.0).ToString("0.0") + " KB/s";
            return (bps / (1024.0 * 1024.0)).ToString("0.00") + " MB/s";
        }
    }
}
