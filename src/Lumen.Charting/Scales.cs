using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using Lumen.Model;

namespace Lumen.Charting
{
    /// <summary>Maps a numeric domain to pixels, in the manner of d3.scaleLinear. Pass the range reversed for a y axis, where pixels grow downward.</summary>
    public sealed class LinearScale
    {
        private double _d0, _d1;
        private readonly double _r0, _r1;

        /// <summary>Takes domain and range ends.</summary>
        public LinearScale(double d0, double d1, double r0, double r1)
        {
            _d0 = d0; _d1 = d1; _r0 = r0; _r1 = r1;
        }

        /// <summary>Domain start.</summary>
        public double Min => _d0;
        /// <summary>Domain end.</summary>
        public double Max => _d1;
        /// <summary>Domain to pixel. A zero-width domain maps to the range start.</summary>
        public double Map(double v) => _d1 == _d0 ? _r0 : _r0 + (v - _d0) / (_d1 - _d0) * (_r1 - _r0);
        /// <summary>Pixel to domain.</summary>
        public double Invert(double px) => _d0 + (px - _r0) / (_r1 - _r0) * (_d1 - _d0);

        /// <summary>Widens the domain to round tick boundaries. Returns this, for chaining.</summary>
        public LinearScale Nice(int count = 5)
        {
            double step = TickStep(_d0, _d1, count);
            _d0 = Math.Floor(_d0 / step) * step;
            _d1 = Math.Ceiling(_d1 / step) * step;
            return this;
        }

        /// <summary>Round values inside the domain, about <paramref name="count"/> of them.</summary>
        public IEnumerable<double> Ticks(int count = 5)
        {
            double step = TickStep(_d0, _d1, count);
            int n = (int)Math.Floor((_d1 - Math.Ceiling(_d0 / step) * step) / step + 0.5);
            double first = Math.Ceiling(_d0 / step) * step;
            for (int i = 0; i <= n; i++) yield return first + i * step;
        }

        // Same "nice numbers" rule as d3: the step is 1, 2 or 5 times a power of ten.
        private static double TickStep(double start, double stop, int count)
        {
            double raw = Math.Abs(stop - start) / Math.Max(1, count);
            if (raw == 0) return 1;
            double power = Math.Pow(10, Math.Floor(Math.Log10(raw)));
            double err = raw / power;
            return power * (err >= 7.07 ? 10 : err >= 3.16 ? 5 : err >= 1.41 ? 2 : 1);
        }
    }

    /// <summary>Maps categories to equal bands, in the manner of d3.scaleBand.</summary>
    public sealed class BandScale
    {
        private readonly Dictionary<string, int> _index = new Dictionary<string, int>();
        private readonly double _start, _step;

        /// <summary>Lays out one band per distinct category. <paramref name="padding"/> is the gap as a fraction of a step.</summary>
        public BandScale(IEnumerable<string> domain, double r0, double r1, double padding = 0.2)
        {
            int n = 0;
            foreach (var d in domain) if (!_index.ContainsKey(d)) _index[d] = n++;
            _step = n == 0 ? 0 : (r1 - r0) / (n + padding);
            Bandwidth = _step * (1 - padding);
            _start = r0 + _step * padding;
        }

        /// <summary>Width of a band.</summary>
        public double Bandwidth { get; }
        /// <summary>Distance between band starts.</summary>
        public double Step => _step;
        /// <summary>Number of bands.</summary>
        public int Count => _index.Count;
        /// <summary>The categories, in order.</summary>
        public IEnumerable<string> Domain => _index.Keys;
        /// <summary>Start of a category's band. Throws for an unknown category.</summary>
        public double Map(string v) => _start + _index[v] * _step;
        /// <summary>Middle of a category's band.</summary>
        public double Center(string v) => Map(v) + Bandwidth / 2;
    }

    /// <summary>Number scaling for labels. Auto picks by magnitude.</summary>
    public enum DisplayUnits { Auto, None, Thousands, Millions, Billions }

    /// <summary>Value formatting: .NET format strings, plus Power BI's "auto" K/M/bn units.</summary>
    public static class PowerBiFormat
    {
        /// <summary>Formats a value for display. Display units win over the format string, and a bad format string falls back to the default rather than throwing.</summary>
        public static string Format(Value v, string format, DisplayUnits units = DisplayUnits.None)
        {
            if (v.IsBlank) return "";
            if (v.IsNumeric)
            {
                double d = v.AsDouble();
                if (units != DisplayUnits.None) return Scaled(d, units);
                if (!string.IsNullOrEmpty(format))
                {
                    try { return d.ToString(format, CultureInfo.CurrentCulture); }
                    catch (FormatException) { }
                }
                return v.Kind == ValueKind.Integer ? v.Integer.ToString("#,0", CultureInfo.CurrentCulture) : Trim(d);
            }
            if (v.Kind == ValueKind.Date && !string.IsNullOrEmpty(format))
            {
                try { return v.AsDate.ToString(format, CultureInfo.CurrentCulture); }
                catch (FormatException) { }
            }
            return v.ToDisplayString();
        }

        /// <summary>Default number text: thousands separators on large numbers, up to three decimals on small ones.</summary>
        private static string Trim(double d) =>
            Math.Abs(d) >= 1000 ? d.ToString("#,0.##", CultureInfo.CurrentCulture) : d.ToString("0.###", CultureInfo.CurrentCulture);

        /// <summary>Formats with a unit suffix: 1.23M.</summary>
        public static string Scaled(double d, DisplayUnits units = DisplayUnits.Auto)
        {
            double a = Math.Abs(d);
            if (units == DisplayUnits.Auto)
                units = a >= 1e9 ? DisplayUnits.Billions : a >= 1e6 ? DisplayUnits.Millions : a >= 1e3 ? DisplayUnits.Thousands : DisplayUnits.None;
            switch (units)
            {
                case DisplayUnits.Billions: return (d / 1e9).ToString("0.##", CultureInfo.CurrentCulture) + "bn";
                case DisplayUnits.Millions: return (d / 1e6).ToString("0.##", CultureInfo.CurrentCulture) + "M";
                case DisplayUnits.Thousands: return (d / 1e3).ToString("0.##", CultureInfo.CurrentCulture) + "K";
                default: return d.ToString("0.##", CultureInfo.CurrentCulture);
            }
        }
    }

    /// <summary>Frozen brushes and pens. A frozen Freezable is cheaper to render and safe to share across threads.</summary>
    public static class Frozen
    {
        /// <summary>A frozen solid brush.</summary>
        public static SolidColorBrush Brush(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        /// <summary>A frozen pen.</summary>
        public static Pen Pen(Color c, double thickness)
        {
            var p = new Pen(Brush(c), thickness);
            p.Freeze();
            return p;
        }

        /// <summary>The same color at another opacity.</summary>
        public static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);
    }

    /// <summary>Frozen StreamGeometry builders. StreamGeometry is the lightest geometry WPF has, and marks rebuild on every update.</summary>
    public static class ShapeBuilders
    {
        /// <summary>An open polyline.</summary>
        public static StreamGeometry Line(IReadOnlyList<Point> pts)
        {
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                if (pts.Count > 0)
                {
                    c.BeginFigure(pts[0], false, false);
                    for (int i = 1; i < pts.Count; i++) c.LineTo(pts[i], true, true);
                }
            }
            g.Freeze();
            return g;
        }

        /// <summary>A closed band between a top and a bottom edge, for area charts.</summary>
        public static StreamGeometry Area(IReadOnlyList<Point> top, IReadOnlyList<Point> bottom)
        {
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                if (top.Count > 0)
                {
                    c.BeginFigure(top[0], true, true);
                    for (int i = 1; i < top.Count; i++) c.LineTo(top[i], false, false);
                    for (int i = bottom.Count - 1; i >= 0; i--) c.LineTo(bottom[i], false, false);
                }
            }
            g.Freeze();
            return g;
        }

        /// <summary>Annular sector; angles in radians, clockwise from 12 o'clock.</summary>
        public static StreamGeometry Arc(Point center, double inner, double outer, double start, double end)
        {
            Func<double, double, Point> at = (r, a) => new Point(center.X + r * Math.Sin(a), center.Y - r * Math.Cos(a));
            bool large = end - start > Math.PI;
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                // A full circle cannot be one arc segment; stop just short of it.
                if (end - start >= Math.PI * 2 - 1e-6) end = start + Math.PI * 2 - 1e-4;
                c.BeginFigure(at(outer, start), true, true);
                c.ArcTo(at(outer, end), new Size(outer, outer), 0, large, SweepDirection.Clockwise, false, false);
                if (inner > 0)
                {
                    c.LineTo(at(inner, end), false, false);
                    c.ArcTo(at(inner, start), new Size(inner, inner), 0, large, SweepDirection.Counterclockwise, false, false);
                }
                else c.LineTo(center, false, false);
            }
            g.Freeze();
            return g;
        }
    }

    /// <summary>Host for layered DrawingVisuals: lightweight marks with no layout cost per element.</summary>
    public sealed class ChartHost : FrameworkElement
    {
        private readonly VisualCollection _children;

        /// <summary>Bottom layer: gridlines.</summary>
        public readonly DrawingVisual GridLayer = new DrawingVisual();
        /// <summary>Bars, lines, slices.</summary>
        public readonly DrawingVisual MarksLayer = new DrawingVisual();
        /// <summary>Axes and labels, above the marks.</summary>
        public readonly DrawingVisual AxesLayer = new DrawingVisual();
        /// <summary>Top layer: hover and selection. Redrawing it leaves the rest alone, which keeps mouse moves cheap.</summary>
        public readonly DrawingVisual OverlayLayer = new DrawingVisual();

        /// <summary>Creates the four layers.</summary>
        public ChartHost()
        {
            _children = new VisualCollection(this) { GridLayer, MarksLayer, AxesLayer, OverlayLayer };
            ClipToBounds = true;
        }

        /// <inheritdoc/>
        protected override int VisualChildrenCount => _children.Count;
        /// <inheritdoc/>
        protected override Visual GetVisualChild(int index) => _children[index];

        /// <summary>A transparent background makes the empty area receive mouse events.</summary>
        protected override void OnRender(DrawingContext dc) =>
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));

        /// <summary>The monitor's scale, which FormattedText needs for crisp text.</summary>
        public double PixelsPerDip => VisualTreeHelper.GetDpi(this).PixelsPerDip;
    }

    /// <summary>Text drawing helpers for DrawingContext.</summary>
    public static class TextDraw
    {
        private static readonly Typeface Face = new Typeface("Segoe UI");
        private static readonly Typeface Bold = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

        /// <summary>Builds a FormattedText. With a <paramref name="maxWidth"/>, the text stays on one line and ends in an ellipsis.</summary>
        public static FormattedText Make(string text, double size, Brush brush, double ppd, bool bold = false, double maxWidth = 0)
        {
            var f = new FormattedText(text ?? "", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, bold ? Bold : Face, Math.Max(1, size), brush, ppd);
            if (maxWidth > 0)
            {
                f.MaxTextWidth = maxWidth;
                f.MaxLineCount = 1;
                f.Trimming = TextTrimming.CharacterEllipsis;
            }
            return f;
        }

        /// <summary>Draws text from a left edge.</summary>
        public static void Left(DrawingContext dc, string text, double x, double y, double size, Brush brush, double ppd, bool bold = false, double maxWidth = 0) =>
            dc.DrawText(Make(text, size, brush, ppd, bold, maxWidth), new Point(x, y));

        /// <summary>Draws text centered on a point.</summary>
        public static void Center(DrawingContext dc, string text, double cx, double y, double size, Brush brush, double ppd, bool bold = false, double maxWidth = 0)
        {
            var f = Make(text, size, brush, ppd, bold, maxWidth);
            if (maxWidth > 0) f.TextAlignment = TextAlignment.Center;
            dc.DrawText(f, new Point(maxWidth > 0 ? cx - maxWidth / 2 : cx - f.Width / 2, y));
        }

        /// <summary>Draws text ending at a right edge.</summary>
        public static void Right(DrawingContext dc, string text, double right, double y, double size, Brush brush, double ppd, bool bold = false)
        {
            var f = Make(text, size, brush, ppd, bold);
            dc.DrawText(f, new Point(right - f.Width, y));
        }
    }
}
