using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Lumen.Charting;
using Lumen.Model;

namespace Lumen.Visuals
{
    /// <summary>One clickable, hoverable thing in a chart: drawn once, then kept for hit testing. Marks are not WPF elements.</summary>
    public sealed class Mark
    {
        /// <summary>Hit area, used when <see cref="Shape"/> is null.</summary>
        public Rect Bounds;
        /// <summary>Exact hit area for non-rectangular marks, such as pie slices.</summary>
        public Geometry Shape;
        /// <summary>What a click selects. Null for marks that cannot be selected.</summary>
        public DataPoint Point;
        /// <summary>Tooltip lines. Null shows none.</summary>
        public List<TooltipItem> Tip;

        /// <summary>Tests a point in chart coordinates.</summary>
        public bool Hit(Point p) => Shape != null ? Shape.FillContains(p) : Bounds.Contains(p);
    }

    /// <summary>Base of everything drawn on a <see cref="ChartHost"/>: holds the update, routes clicks and hover through <see cref="Marks"/>, and handles
    /// the states before drawing. A subclass supplies <see cref="Capabilities"/> and <see cref="Render"/>.</summary>
    public abstract class ChartVisual : ILumenVisual
    {
        /// <summary>The canvas side of the contract.</summary>
        protected IVisualHost Host;
        /// <summary>The drawing surface with its four layers.</summary>
        protected ChartHost Chart;
        /// <summary>The latest update. Valid inside <see cref="Render"/>.</summary>
        protected VisualUpdate U;
        /// <summary>Marks drawn so far, in paint order. Render fills it.</summary>
        protected readonly List<Mark> Marks = new List<Mark>();

        /// <inheritdoc/>
        public abstract VisualCapabilities Capabilities { get; }

        /// <inheritdoc/>
        public virtual FrameworkElement CreateView(IVisualHost host)
        {
            Host = host;
            Chart = new ChartHost();
            Chart.MouseLeftButtonUp += OnClick;
            Chart.MouseMove += OnMove;
            Chart.MouseLeave += (s, e) => Host.HideTooltip();
            return Chart;
        }

        /// <summary>The default query: Grouping wells become GroupBy and the others become measures, all in one spec.</summary>
        public virtual IReadOnlyList<QuerySpec> BuildQueries(VisualDef def, SemanticModel model)
        {
            var spec = new QuerySpec();
            foreach (var role in Capabilities.Roles)
            {
                if (role.Kind == RoleKind.Grouping) spec.GroupBy.AddRange(def.Role(role.Name));
                else spec.Measures.AddRange(QueryHelpers.Normalize(def.Role(role.Name), model));
            }
            return spec.GroupBy.Count + spec.Measures.Count == 0 ? new QuerySpec[0] : new[] { spec };
        }

        /// <summary>True when every required well has its minimum number of fields.</summary>
        protected bool WellsFilled(VisualDef def) =>
            Capabilities.Roles.All(r => r.Min == 0 || def.Role(r.Name).Count >= r.Min);

        /// <summary>Clears all layers and either draws or shows the reason it cannot. Subclasses override <see cref="Render"/>, not this.</summary>
        public void Update(VisualUpdate u)
        {
            U = u;
            Marks.Clear();
            ClearLayers();
            if (!WellsFilled(u.Def)) { Message("Add fields to build this visual"); return; }
            if (u.Data == null) return;
            if (u.Data.Error != null) { Message(u.Data.Error); return; }
            if (u.Size.Width < 20 || u.Size.Height < 20) return;
            Render();
            if (u.Data.Truncated) Notice("Too many values; showing the first " + u.Data.RowCount);
        }

        /// <summary>Draws the chart from <see cref="U"/> and fills <see cref="Marks"/>. Only runs with valid data and a usable size.</summary>
        protected abstract void Render();

        /// <summary>Theme foreground brush.</summary>
        protected Brush Fg => Frozen.Brush(U.Theme.Foreground);
        /// <summary>Theme secondary brush.</summary>
        protected Brush Fg2 => Frozen.Brush(U.Theme.Secondary);

        /// <summary>Opening a DrawingVisual and closing it without drawing erases it.</summary>
        private void ClearLayers()
        {
            foreach (var layer in new[] { Chart.GridLayer, Chart.MarksLayer, Chart.AxesLayer, Chart.OverlayLayer })
                using (layer.RenderOpen()) { }
        }

        /// <summary>Shows centered text instead of a chart.</summary>
        protected void Message(string text)
        {
            using (var dc = Chart.OverlayLayer.RenderOpen())
            {
                double w = Math.Max(40, U.Size.Width - 16);
                var f = TextDraw.Make(text, 12, Frozen.Brush(U.Theme.Secondary), Chart.PixelsPerDip);
                f.MaxTextWidth = w;
                f.TextAlignment = TextAlignment.Center;
                dc.DrawText(f, new Point((U.Size.Width - w) / 2, Math.Max(0, U.Size.Height / 2 - f.Height / 2)));
            }
        }

        /// <summary>Shows a small note in the top right corner.</summary>
        private void Notice(string text)
        {
            using (var dc = Chart.OverlayLayer.RenderOpen())
                TextDraw.Right(dc, text, U.Size.Width - 4, 2, 10, Frozen.Brush(U.Theme.Secondary), Chart.PixelsPerDip);
        }

        /// <summary>The topmost mark at a point. Marks later in the list were painted later, so the search runs backward.</summary>
        protected Mark HitTest(Point p)
        {
            for (int i = Marks.Count - 1; i >= 0; i--) if (Marks[i].Hit(p)) return Marks[i];
            return null;
        }

        /// <summary>A click on a mark selects it, and a click on empty space clears. Ctrl adds to the selection.</summary>
        private void OnClick(object sender, MouseButtonEventArgs e)
        {
            if (U == null) return;
            var hit = HitTest(e.GetPosition(Chart));
            if (hit?.Point == null) Host.ClearSelection();
            else Host.Select(hit.Point, (Keyboard.Modifiers & ModifierKeys.Control) != 0);
        }

        /// <summary>Shows or hides the tooltip as the mouse moves.</summary>
        private void OnMove(object sender, MouseEventArgs e)
        {
            var hit = HitTest(e.GetPosition(Chart));
            if (hit?.Tip == null) Host.HideTooltip();
            else Host.ShowTooltip(hit.Tip);
        }

        /// <summary>Builds the selection payload of a result row.</summary>
        protected DataPoint PointFor(int row)
        {
            var r = U.Data;
            var p = new DataPoint { Key = r.RowKey(row) };
            for (int c = 0; c < r.GroupCount; c++)
                p.Fields.Add(new KeyValuePair<FieldRef, object>(r.Columns[c].Field, r.Group(c, row).ToObject()));
            return p;
        }

        /// <summary>True when something is selected in this visual, and it is not this row.</summary>
        protected bool Dim(string key) => U.Selection.Count > 0 && !U.Selection.Contains(key);

        /// <summary>Draws a wrapping legend at the top and returns its height, which becomes the chart's top margin.</summary>
        protected double DrawLegend(DrawingContext dc, IList<string> names, Func<int, Color> color, double width)
        {
            double x = 4, y = 2, lineHeight = 16;
            for (int i = 0; i < names.Count; i++)
            {
                var f = TextDraw.Make(names[i], 11, Fg2, Chart.PixelsPerDip);
                if (x + f.Width + 18 > width && x > 4) { x = 4; y += lineHeight; }
                dc.DrawEllipse(Frozen.Brush(color(i)), null, new Point(x + 5, y + 8), 4.5, 4.5);
                dc.DrawText(f, new Point(x + 13, y));
                x += f.Width + 26;
            }
            return y + lineHeight + 4;
        }

        /// <summary>Nothing to release; the view is collected with the container.</summary>
        public void Dispose() { }
    }

    /// <summary>Categories by series: series come from the Legend column, or else from the measures.</summary>
    internal sealed class SeriesData
    {
        /// <summary>Distinct category labels, in result order.</summary>
        public List<string> Categories = new List<string>();
        /// <summary>Distinct series names, in result order.</summary>
        public List<string> Series = new List<string>();
        /// <summary>Cell values. Null means no data at that crossing.</summary>
        public double?[][] Values;     // [series][category]
        /// <summary>Highlight values, aligned with <see cref="Values"/>.</summary>
        public double?[][] Highlights;
        /// <summary>Maps a cell back to its result row, for selection.</summary>
        public int[][] Rows;           // result row, or -1
        /// <summary>True when a cross-highlight is active.</summary>
        public bool HasHighlight;
        /// <summary>Format string per series.</summary>
        public string[] Formats;

        /// <summary>Pivots a flat result. With a legend, the last group column names the series; without one, each measure is a series. Remaining group columns join into the category label.</summary>
        public static SeriesData From(QueryResult r, bool hasLegend)
        {
            var d = new SeriesData();
            int catCols = hasLegend ? r.GroupCount - 1 : r.GroupCount;
            var catIndex = new Dictionary<string, int>();
            var seriesIndex = new Dictionary<string, int>();
            Func<int, string> catOf = row =>
            {
                var parts = new string[catCols];
                for (int c = 0; c < catCols; c++) parts[c] = Label(r, c, row);
                return catCols == 0 ? "" : string.Join(" ", parts);
            };
            for (int row = 0; row < r.RowCount; row++)
            {
                string cat = catOf(row);
                if (!catIndex.ContainsKey(cat)) { catIndex[cat] = d.Categories.Count; d.Categories.Add(cat); }
                if (hasLegend)
                {
                    string s = Label(r, r.GroupCount - 1, row);
                    if (!seriesIndex.ContainsKey(s)) { seriesIndex[s] = d.Series.Count; d.Series.Add(s); }
                }
            }
            if (!hasLegend)
                for (int m = 0; m < r.MeasureCount; m++) d.Series.Add(r.Columns[r.GroupCount + m].Name);

            int sc = d.Series.Count, cc = d.Categories.Count;
            d.Values = new double?[sc][];
            d.Highlights = new double?[sc][];
            d.Rows = new int[sc][];
            for (int s = 0; s < sc; s++)
            {
                d.Values[s] = new double?[cc];
                d.Highlights[s] = new double?[cc];
                d.Rows[s] = Enumerable.Repeat(-1, cc).ToArray();
            }
            d.HasHighlight = r.Highlight != null;
            d.Formats = new string[Math.Max(1, sc)];

            for (int row = 0; row < r.RowCount; row++)
            {
                int cat = catIndex[catOf(row)];
                if (hasLegend)
                {
                    int s = seriesIndex[Label(r, r.GroupCount - 1, row)];
                    Set(d, r, s, cat, row, 0);
                    d.Formats[s] = r.Columns[r.GroupCount].FormatString;
                }
                else
                {
                    for (int m = 0; m < r.MeasureCount; m++)
                    {
                        Set(d, r, m, cat, row, m);
                        d.Formats[m] = r.Columns[r.GroupCount + m].FormatString;
                    }
                }
            }
            return d;
        }

        /// <summary>Stores one cell.</summary>
        private static void Set(SeriesData d, QueryResult r, int s, int cat, int row, int measure)
        {
            var v = r.Measure(measure, row);
            d.Values[s][cat] = v.IsBlank ? (double?)null : v.AsDouble();
            d.Rows[s][cat] = row;
            if (r.Highlight != null)
            {
                var h = r.Highlight[measure][row];
                d.Highlights[s][cat] = h.IsBlank ? 0 : h.AsDouble();
            }
        }

        /// <summary>Formats a group cell as a label.</summary>
        public static string Label(QueryResult r, int col, int row) =>
            PowerBiFormat.Format(r.Group(col, row), r.Columns[col].FormatString);
    }

    /// <summary>Base of visuals with axes.</summary>
    public abstract class CartesianVisual : ChartVisual
    {
        /// <summary>The format options, typed.</summary>
        protected ChartFormat Fmt => (ChartFormat)U.Format;

        /// <summary>A series color: the Default color option when there is one series, otherwise the page-wide color for that name.</summary>
        protected Color SeriesColor(int index, int total, string name)
        {
            if (total == 1 && !string.IsNullOrWhiteSpace(Fmt.Color))
                return ResolvedTheme.Parse(Fmt.Color, U.Theme.DataColors[0]);
            return Host.ColorFor(name);
        }

        /// <summary>Widest label among the first 50, capped. Measuring all of them would cost more than the margin is worth.</summary>
        protected internal static double MaxLabelWidth(IEnumerable<string> labels, double size, double ppd, Brush brush, double cap)
        {
            double w = 0;
            foreach (var l in labels.Take(50)) w = Math.Max(w, TextDraw.Make(l, size, brush, ppd).Width);
            return Math.Min(w, cap);
        }
    }

    /// <summary>Bar and column charts, clustered or stacked. Four registry entries share this class.</summary>
    public sealed class BarVisual : CartesianVisual
    {
        private readonly bool _horizontal, _stacked;

        /// <summary>Sets orientation and stacking.</summary>
        public BarVisual(bool horizontal, bool stacked) { _horizontal = horizontal; _stacked = stacked; }

        /// <inheritdoc/>
        public override VisualCapabilities Capabilities => new VisualCapabilities
        {
            DisplayName = (_stacked ? "Stacked " : "Clustered ") + (_horizontal ? "bar" : "column"),
            Roles =
            {
                new DataRole("category", _horizontal ? "Y-axis" : "X-axis", RoleKind.Grouping, 1, 3),
                new DataRole("legend", "Legend", RoleKind.Grouping, 0, 1),
                new DataRole("values", _horizontal ? "X-axis" : "Y-axis", RoleKind.Measure, 1),
            },
            FormatType = typeof(ChartFormat),
            SupportsHighlight = true
        };

        /// <summary>Default query, plus: a category with no natural order sorts by the first measure, largest first, as Power BI does.</summary>
        public override IReadOnlyList<QuerySpec> BuildQueries(VisualDef def, SemanticModel model)
        {
            var specs = base.BuildQueries(def, model);
            if (specs.Count == 1 && def.Role("legend").Count == 0 && def.Role("category").Count > 0
                && specs[0].Measures.Count > 0 && !QueryHelpers.HasNaturalOrder(def.Role("category")[0], model))
                specs[0].OrderBy.Add(new OrderBy { Field = specs[0].Measures[0], Descending = true });
            return specs;
        }

        /// <inheritdoc/>
        protected override void Render()
        {
            var r = U.Data;
            bool hasLegend = U.Def.Role("legend").Count > 0;
            var d = SeriesData.From(r, hasLegend);
            if (d.Categories.Count == 0) { Message("No data"); return; }

            double ppd = Chart.PixelsPerDip;
            double fs = Fmt.AxisFontSize;
            double W = U.Size.Width, H = U.Size.Height;
            int sc = d.Series.Count, cc = d.Categories.Count;
            Func<int, Color> color = i => SeriesColor(i, sc, d.Series[i]);

            double top = 6;
            if (sc > 1 && Fmt.ShowLegend)
                using (var dc = Chart.OverlayLayer.RenderOpen()) top = DrawLegend(dc, d.Series, color, W);

            // Domain: always includes zero, and stacks count positive and negative parts apart.
            double max = 0, min = 0;
            for (int c = 0; c < cc; c++)
            {
                double stackPos = 0, stackNeg = 0;
                for (int s = 0; s < sc; s++)
                {
                    double v = d.Values[s][c] ?? 0;
                    if (_stacked) { if (v >= 0) stackPos += v; else stackNeg += v; }
                    else { max = Math.Max(max, v); min = Math.Min(min, v); }
                }
                if (_stacked) { max = Math.Max(max, stackPos); min = Math.Min(min, stackNeg); }
            }
            if (max == min) max = min + 1;

            double catLabelW = Fmt.ShowYAxis || !_horizontal
                ? MaxLabelWidth(d.Categories, fs, ppd, Fg2, W * 0.35) + 8 : 6;
            string fmt = d.Formats[0];

            Thickness m;
            if (_horizontal)
                m = new Thickness(Fmt.ShowYAxis ? catLabelW : 6, top, 16, Fmt.ShowXAxis ? fs + 12 : 6);
            else
            {
                var probe = new LinearScale(min, max, 0, 1).Nice();
                double valW = MaxLabelWidth(probe.Ticks().Select(t => PowerBiFormat.Scaled(t)), fs, ppd, Fg2, 70) + 8;
                m = new Thickness(Fmt.ShowYAxis ? valW : 6, top, 8, Fmt.ShowXAxis ? fs + 12 : 6);
            }

            double pw = Math.Max(10, W - m.Left - m.Right), ph = Math.Max(10, H - m.Top - m.Bottom);
            var band = _horizontal ? new BandScale(d.Categories, 0, ph) : new BandScale(d.Categories, 0, pw);
            var val = (_horizontal ? new LinearScale(min, max, 0, pw) : new LinearScale(min, max, ph, 0)).Nice();

            using (var dc = Chart.GridLayer.RenderOpen())
            {
                var pen = Frozen.Pen(U.Theme.Grid, 1);
                foreach (var t in val.Ticks())
                {
                    if (!Fmt.ShowGridlines) break;
                    if (_horizontal) { double x = m.Left + val.Map(t); dc.DrawLine(pen, new Point(x, m.Top), new Point(x, m.Top + ph)); }
                    else { double y = m.Top + val.Map(t); dc.DrawLine(pen, new Point(m.Left, y), new Point(m.Left + pw, y)); }
                }
            }

            double zero = val.Map(0);
            double slot = _stacked ? band.Bandwidth : band.Bandwidth / sc;
            using (var dc = Chart.MarksLayer.RenderOpen())
            {
                for (int c = 0; c < cc; c++)
                {
                    string cat = d.Categories[c];
                    double offset = band.Map(cat);
                    double pos = 0, neg = 0;
                    for (int s = 0; s < sc; s++)
                    {
                        var v = d.Values[s][c];
                        if (v == null) continue;
                        double from, to;
                        if (_stacked)
                        {
                            double b = v.Value >= 0 ? pos : neg;
                            from = b; to = b + v.Value;
                            if (v.Value >= 0) pos = to; else neg = to;
                        }
                        else { from = 0; to = v.Value; }

                        double crossAt = _stacked ? offset : offset + slot * s;
                        double thickness = Math.Max(1, slot - (_stacked ? 0 : 1));
                        Func<double, double, Rect> rect = (a, b) =>
                        {
                            double p0 = val.Map(a), p1 = val.Map(b);
                            return _horizontal
                                ? new Rect(m.Left + Math.Min(p0, p1), m.Top + crossAt, Math.Abs(p1 - p0), thickness)
                                : new Rect(m.Left + crossAt, m.Top + Math.Min(p0, p1), thickness, Math.Abs(p1 - p0));
                        };

                        var bounds = rect(from, to);
                        int row = d.Rows[s][c];
                        string key = row >= 0 ? r.RowKey(row) : null;
                        var baseColor = color(s);
                        // During a highlight the full bar goes faint and the highlighted part is drawn solid on top.
                        bool dim = (key != null && Dim(key)) || d.HasHighlight;
                        dc.DrawRectangle(Frozen.Brush(dim ? Frozen.WithAlpha(baseColor, 90) : baseColor), null, bounds);
                        if (d.HasHighlight)
                        {
                            double h = d.Highlights[s][c] ?? 0;
                            dc.DrawRectangle(Frozen.Brush(baseColor), null, rect(from, from + h));
                        }

                        if (Fmt.DataLabels)
                        {
                            string text = PowerBiFormat.Scaled(v.Value, Fmt.LabelUnits == DisplayUnits.None ? DisplayUnits.Auto : Fmt.LabelUnits);
                            var f = TextDraw.Make(text, 10, Fg, ppd);
                            var at = _horizontal
                                ? new Point(bounds.Right + 3, bounds.Top + bounds.Height / 2 - f.Height / 2)
                                : new Point(bounds.Left + bounds.Width / 2 - f.Width / 2, Math.Max(m.Top, bounds.Top - f.Height));
                            dc.DrawText(f, at);
                        }

                        Marks.Add(new Mark
                        {
                            Bounds = bounds,
                            Point = row >= 0 ? PointFor(row) : null,
                            Tip = new List<TooltipItem>
                            {
                                new TooltipItem(r.Columns[0].Name, cat),
                                new TooltipItem(d.Series[s], PowerBiFormat.Format(Value.FromNumber(v.Value), d.Formats[s]))
                            }
                        });
                    }
                }
                var axis = Frozen.Pen(U.Theme.Secondary, 1);
                if (_horizontal) dc.DrawLine(axis, new Point(m.Left + zero, m.Top), new Point(m.Left + zero, m.Top + ph));
                else dc.DrawLine(axis, new Point(m.Left, m.Top + zero), new Point(m.Left + pw, m.Top + zero));
            }

            using (var dc = Chart.AxesLayer.RenderOpen())
            {
                if (_horizontal)
                {
                    if (Fmt.ShowYAxis)
                        foreach (var c in d.Categories)
                        {
                            double y = m.Top + band.Center(c) - fs * 0.7;
                            if (band.Step >= fs * 0.9) dc.DrawText(TextDraw.Make(c, fs, Fg2, ppd, false, catLabelW - 8), new Point(4, y));
                        }
                    if (Fmt.ShowXAxis)
                        foreach (var t in val.Ticks())
                            TextDraw.Center(dc, PowerBiFormat.Scaled(t), m.Left + val.Map(t), m.Top + ph + 3, fs, Fg2, ppd);
                }
                else
                {
                    if (Fmt.ShowYAxis)
                        foreach (var t in val.Ticks())
                            TextDraw.Right(dc, PowerBiFormat.Scaled(t), m.Left - 4, m.Top + val.Map(t) - fs * 0.7, fs, Fg2, ppd);
                    if (Fmt.ShowXAxis)
                    {
                        // Skip labels rather than overlap them.
                        double labelW = MaxLabelWidth(d.Categories, fs, ppd, Fg2, 200) + 6;
                        int every = Math.Max(1, (int)Math.Ceiling(labelW / Math.Max(1, band.Step)));
                        for (int i = 0; i < cc; i += every)
                            TextDraw.Center(dc, d.Categories[i], m.Left + band.Center(d.Categories[i]), m.Top + ph + 3, fs, Fg2, ppd, false, band.Step * every);
                    }
                }
            }
        }
    }

    /// <summary>Line and area charts. Cross-highlight is not supported, so other visuals filter these instead.</summary>
    public sealed class LineVisual : CartesianVisual
    {
        private readonly bool _area;

        /// <summary>Sets whether the space under the line fills.</summary>
        public LineVisual(bool area) { _area = area; }

        /// <inheritdoc/>
        public override VisualCapabilities Capabilities => new VisualCapabilities
        {
            DisplayName = _area ? "Area" : "Line",
            Roles =
            {
                new DataRole("category", "X-axis", RoleKind.Grouping, 1, 3),
                new DataRole("legend", "Legend", RoleKind.Grouping, 0, 1),
                new DataRole("values", "Y-axis", RoleKind.Measure, 1),
            },
            FormatType = typeof(ChartFormat)
        };

        /// <inheritdoc/>
        protected override void Render()
        {
            var r = U.Data;
            var d = SeriesData.From(r, U.Def.Role("legend").Count > 0);
            if (d.Categories.Count == 0) { Message("No data"); return; }

            double ppd = Chart.PixelsPerDip, fs = Fmt.AxisFontSize, W = U.Size.Width, H = U.Size.Height;
            int sc = d.Series.Count, cc = d.Categories.Count;
            Func<int, Color> color = i => SeriesColor(i, sc, d.Series[i]);

            double top = 6;
            if (sc > 1 && Fmt.ShowLegend)
                using (var dc = Chart.OverlayLayer.RenderOpen()) top = DrawLegend(dc, d.Series, color, W);

            double max = double.MinValue, min = 0;
            foreach (var s in d.Values)
                foreach (var v in s)
                    if (v.HasValue) { max = Math.Max(max, v.Value); min = Math.Min(min, v.Value); }
            if (max == double.MinValue) { Message("No data"); return; }
            if (max == min) max = min + 1;

            var probe = new LinearScale(min, max, 0, 1).Nice();
            double valW = MaxLabelWidth(probe.Ticks().Select(t => PowerBiFormat.Scaled(t)), fs, ppd, Fg2, 70) + 8;
            var m = new Thickness(Fmt.ShowYAxis ? valW : 6, top, 12, Fmt.ShowXAxis ? fs + 12 : 6);
            double pw = Math.Max(10, W - m.Left - m.Right), ph = Math.Max(10, H - m.Top - m.Bottom);
            var y = new LinearScale(min, max, ph, 0).Nice();
            double step = cc > 1 ? pw / (cc - 1) : 0;
            Func<int, double> xAt = i => m.Left + (cc > 1 ? i * step : pw / 2);

            using (var dc = Chart.GridLayer.RenderOpen())
            {
                var pen = Frozen.Pen(U.Theme.Grid, 1);
                if (Fmt.ShowGridlines)
                    foreach (var t in y.Ticks())
                    {
                        double py = m.Top + y.Map(t);
                        dc.DrawLine(pen, new Point(m.Left, py), new Point(m.Left + pw, py));
                    }
            }

            using (var dc = Chart.MarksLayer.RenderOpen())
            {
                for (int s = 0; s < sc; s++)
                {
                    var c = color(s);
                    var pts = new List<Point>();
                    for (int i = 0; i < cc; i++)
                        if (d.Values[s][i].HasValue) pts.Add(new Point(xAt(i), m.Top + y.Map(d.Values[s][i].Value)));
                    if (_area && pts.Count > 1)
                    {
                        var bottom = pts.Select(p => new Point(p.X, m.Top + y.Map(0))).ToList();
                        dc.DrawGeometry(Frozen.Brush(Frozen.WithAlpha(c, 70)), null, ShapeBuilders.Area(pts, bottom));
                    }
                    if (pts.Count > 1) dc.DrawGeometry(null, Frozen.Pen(c, 2), ShapeBuilders.Line(pts));

                    bool dots = cc <= 60;
                    for (int i = 0; i < cc; i++)
                    {
                        var v = d.Values[s][i];
                        if (!v.HasValue) continue;
                        var p = new Point(xAt(i), m.Top + y.Map(v.Value));
                        int row = d.Rows[s][i];
                        bool dim = row >= 0 && Dim(r.RowKey(row));
                        if (dots || pts.Count == 1)
                            dc.DrawEllipse(Frozen.Brush(dim ? Frozen.WithAlpha(c, 80) : c), null, p, 3, 3);
                        if (Fmt.DataLabels)
                            TextDraw.Center(dc, PowerBiFormat.Scaled(v.Value), p.X, p.Y - 16, 10, Fg, ppd);
                        Marks.Add(new Mark
                        {
                            // A full-height strip per category, so the tooltip works anywhere above a point. Dense lines drop the dots and keep these.
                            Bounds = new Rect(p.X - Math.Max(6, step / 2), m.Top, Math.Max(12, step), ph),
                            Point = row >= 0 ? PointFor(row) : null,
                            Tip = TipFor(d, i, r)
                        });
                    }
                }
            }

            using (var dc = Chart.AxesLayer.RenderOpen())
            {
                var axis = Frozen.Pen(U.Theme.Secondary, 1);
                dc.DrawLine(axis, new Point(m.Left, m.Top + ph), new Point(m.Left + pw, m.Top + ph));
                if (Fmt.ShowYAxis)
                    foreach (var t in y.Ticks())
                        TextDraw.Right(dc, PowerBiFormat.Scaled(t), m.Left - 4, m.Top + y.Map(t) - fs * 0.7, fs, Fg2, ppd);
                if (Fmt.ShowXAxis)
                {
                    double labelW = MaxLabelWidth(d.Categories, fs, ppd, Fg2, 200) + 8;
                    int every = cc > 1 ? Math.Max(1, (int)Math.Ceiling(labelW / Math.Max(1, step))) : 1;
                    for (int i = 0; i < cc; i += every)
                        TextDraw.Center(dc, d.Categories[i], xAt(i), m.Top + ph + 3, fs, Fg2, ppd);
                }
            }
        }

        /// <summary>A tooltip listing every series at a category.</summary>
        private static List<TooltipItem> TipFor(SeriesData d, int cat, QueryResult r)
        {
            var tip = new List<TooltipItem> { new TooltipItem(r.Columns[0].Name, d.Categories[cat]) };
            for (int s = 0; s < d.Series.Count; s++)
                if (d.Values[s][cat].HasValue)
                    tip.Add(new TooltipItem(d.Series[s], PowerBiFormat.Format(Value.FromNumber(d.Values[s][cat].Value), d.Formats[s])));
            return tip;
        }
    }

    /// <summary>Pie and donut charts. Slices below zero are dropped.</summary>
    public sealed class PieVisual : ChartVisual
    {
        private readonly bool _donut;

        /// <summary>Sets whether the center is cut out.</summary>
        public PieVisual(bool donut) { _donut = donut; }

        private ChartFormat Fmt => (ChartFormat)U.Format;

        /// <inheritdoc/>
        public override VisualCapabilities Capabilities => new VisualCapabilities
        {
            DisplayName = _donut ? "Donut" : "Pie",
            Roles =
            {
                new DataRole("category", "Legend", RoleKind.Grouping, 1, 1),
                new DataRole("values", "Values", RoleKind.Measure, 1, 1),
            },
            FormatType = typeof(ChartFormat),
            SupportsHighlight = true
        };

        /// <summary>Default query, largest slice first.</summary>
        public override IReadOnlyList<QuerySpec> BuildQueries(VisualDef def, SemanticModel model)
        {
            var specs = base.BuildQueries(def, model);
            if (specs.Count == 1 && specs[0].Measures.Count > 0)
                specs[0].OrderBy.Add(new OrderBy { Field = specs[0].Measures[0], Descending = true });
            return specs;
        }

        /// <inheritdoc/>
        protected override void Render()
        {
            var r = U.Data;
            var rows = Enumerable.Range(0, r.RowCount).Where(i => r.Measure(0, i).AsDouble() > 0).ToList();
            double total = rows.Sum(i => r.Measure(0, i).AsDouble());
            if (rows.Count == 0 || total <= 0) { Message("No data"); return; }

            double W = U.Size.Width, H = U.Size.Height, ppd = Chart.PixelsPerDip;
            var names = rows.Select(i => SeriesData.Label(r, 0, i)).ToList();

            double top = 4;
            if (Fmt.ShowLegend)
                using (var dc = Chart.OverlayLayer.RenderOpen()) top = DrawLegend(dc, names, i => Host.ColorFor(names[i]), W);

            double outer = Math.Max(10, Math.Min(W, H - top) / 2 - 12);
            double inner = _donut ? outer * 0.6 : 0;
            var center = new Point(W / 2, top + (H - top) / 2);

            using (var dc = Chart.MarksLayer.RenderOpen())
            {
                double a = 0;
                var gap = Frozen.Pen(U.Theme.VisualBackground, 1);
                for (int n = 0; n < rows.Count; n++)
                {
                    int row = rows[n];
                    double v = r.Measure(0, row).AsDouble();
                    double sweep = v / total * Math.PI * 2;
                    var c = Host.ColorFor(names[n]);
                    var key = r.RowKey(row);
                    var geo = ShapeBuilders.Arc(center, inner, outer, a, a + sweep);
                    bool dim = Dim(key) || r.Highlight != null;
                    dc.DrawGeometry(Frozen.Brush(dim ? Frozen.WithAlpha(c, 90) : c), gap, geo);

                    if (r.Highlight != null)
                    {
                        // The highlight grows outward from the inner radius. The square root makes area, not radius, proportional to the share.
                        double share = v == 0 ? 0 : r.Highlight[0][row].AsDouble() / v;
                        if (share > 0)
                            dc.DrawGeometry(Frozen.Brush(c), gap, ShapeBuilders.Arc(center, inner, inner + (outer - inner) * Math.Sqrt(share), a, a + sweep));
                    }

                    if (Fmt.DataLabels || sweep > 0.25)
                    {
                        double mid = a + sweep / 2, rr = _donut ? (inner + outer) / 2 : outer * 0.65;
                        TextDraw.Center(dc, (v / total).ToString("0%"), center.X + rr * Math.Sin(mid), center.Y - rr * Math.Cos(mid) - 7, 11,
                            Frozen.Brush(Colors.White), ppd);
                    }

                    Marks.Add(new Mark
                    {
                        Shape = geo,
                        Point = PointFor(row),
                        Tip = new List<TooltipItem>
                        {
                            new TooltipItem(r.Columns[0].Name, names[n]),
                            new TooltipItem(r.Columns[1].Name, PowerBiFormat.Format(r.Measure(0, row), r.Columns[1].FormatString)),
                            new TooltipItem("Share", (v / total).ToString("0.0%"))
                        }
                    });
                    a += sweep;
                }
            }
        }
    }

    /// <summary>Scatter chart with an optional size measure. One point per Details group.</summary>
    public sealed class ScatterVisual : CartesianVisual
    {
        /// <inheritdoc/>
        public override VisualCapabilities Capabilities => new VisualCapabilities
        {
            DisplayName = "Scatter",
            Roles =
            {
                new DataRole("details", "Values (details)", RoleKind.Grouping, 1, 2),
                new DataRole("x", "X-axis", RoleKind.Measure, 1, 1),
                new DataRole("y", "Y-axis", RoleKind.Measure, 1, 1),
                new DataRole("size", "Size", RoleKind.Measure, 0, 1),
            },
            FormatType = typeof(ChartFormat)
        };

        /// <inheritdoc/>
        protected override void Render()
        {
            var r = U.Data;
            if (r.RowCount == 0 || r.MeasureCount < 2) { Message("No data"); return; }
            bool hasSize = r.MeasureCount > 2;
            double ppd = Chart.PixelsPerDip, fs = Fmt.AxisFontSize, W = U.Size.Width, H = U.Size.Height;

            var xs = Enumerable.Range(0, r.RowCount).Select(i => r.Measure(0, i).AsDouble()).ToArray();
            var ys = Enumerable.Range(0, r.RowCount).Select(i => r.Measure(1, i).AsDouble()).ToArray();
            double xMin = Math.Min(0, xs.Min()), xMax = xs.Max(), yMin = Math.Min(0, ys.Min()), yMax = ys.Max();
            if (xMax == xMin) xMax = xMin + 1;
            if (yMax == yMin) yMax = yMin + 1;

            var yProbe = new LinearScale(yMin, yMax, 0, 1).Nice();
            double valW = MaxLabelWidth(yProbe.Ticks().Select(t => PowerBiFormat.Scaled(t)), fs, ppd, Fg2, 70) + 8;
            var m = new Thickness(Fmt.ShowYAxis ? valW : 6, 8, 16, Fmt.ShowXAxis ? fs + 12 : 6);
            double pw = Math.Max(10, W - m.Left - m.Right), ph = Math.Max(10, H - m.Top - m.Bottom);
            var x = new LinearScale(xMin, xMax, 0, pw).Nice();
            var y = new LinearScale(yMin, yMax, ph, 0).Nice();

            using (var dc = Chart.GridLayer.RenderOpen())
            {
                var pen = Frozen.Pen(U.Theme.Grid, 1);
                if (Fmt.ShowGridlines)
                {
                    foreach (var t in y.Ticks()) { double p = m.Top + y.Map(t); dc.DrawLine(pen, new Point(m.Left, p), new Point(m.Left + pw, p)); }
                    foreach (var t in x.Ticks()) { double p = m.Left + x.Map(t); dc.DrawLine(pen, new Point(p, m.Top), new Point(p, m.Top + ph)); }
                }
            }

            double sizeMax = hasSize ? Enumerable.Range(0, r.RowCount).Max(i => Math.Abs(r.Measure(2, i).AsDouble())) : 1;
            var c = SeriesColor(0, 1, r.Columns[r.GroupCount + 1].Name);
            using (var dc = Chart.MarksLayer.RenderOpen())
            {
                for (int i = 0; i < r.RowCount; i++)
                {
                    var p = new Point(m.Left + x.Map(xs[i]), m.Top + y.Map(ys[i]));
                    double radius = hasSize && sizeMax > 0 ? 3 + 12 * Math.Sqrt(Math.Abs(r.Measure(2, i).AsDouble()) / sizeMax) : 5;
                    bool dim = Dim(r.RowKey(i));
                    dc.DrawEllipse(Frozen.Brush(Frozen.WithAlpha(c, (byte)(dim ? 50 : 170))), null, p, radius, radius);
                    var tip = new List<TooltipItem>();
                    for (int g = 0; g < r.GroupCount; g++) tip.Add(new TooltipItem(r.Columns[g].Name, SeriesData.Label(r, g, i)));
                    for (int k = 0; k < r.MeasureCount; k++)
                        tip.Add(new TooltipItem(r.Columns[r.GroupCount + k].Name, PowerBiFormat.Format(r.Measure(k, i), r.Columns[r.GroupCount + k].FormatString)));
                    Marks.Add(new Mark { Bounds = new Rect(p.X - radius - 2, p.Y - radius - 2, radius * 2 + 4, radius * 2 + 4), Point = PointFor(i), Tip = tip });
                }
            }

            using (var dc = Chart.AxesLayer.RenderOpen())
            {
                var axis = Frozen.Pen(U.Theme.Secondary, 1);
                dc.DrawLine(axis, new Point(m.Left, m.Top + ph), new Point(m.Left + pw, m.Top + ph));
                if (Fmt.ShowYAxis)
                    foreach (var t in y.Ticks()) TextDraw.Right(dc, PowerBiFormat.Scaled(t), m.Left - 4, m.Top + y.Map(t) - fs * 0.7, fs, Fg2, ppd);
                if (Fmt.ShowXAxis)
                    foreach (var t in x.Ticks()) TextDraw.Center(dc, PowerBiFormat.Scaled(t), m.Left + x.Map(t), m.Top + ph + 3, fs, Fg2, ppd);
            }
        }
    }

    /// <summary>A single big number.</summary>
    public sealed class CardVisual : ChartVisual
    {
        private CardFormat Fmt => (CardFormat)U.Format;

        /// <inheritdoc/>
        public override VisualCapabilities Capabilities => new VisualCapabilities
        {
            DisplayName = "Card",
            Roles = { new DataRole("values", "Fields", RoleKind.Measure, 1, 1) },
            FormatType = typeof(CardFormat)
        };

        /// <inheritdoc/>
        protected override void Render()
        {
            var r = U.Data;
            if (r.MeasureCount == 0 || r.RowCount == 0) { Message("No data"); return; }
            var v = r.Measure(0, 0);
            var col = r.Columns[r.GroupCount];
            string text = v.IsBlank ? "(Blank)" : Display(v, col, Fmt.Units);
            double ppd = Chart.PixelsPerDip;
            using (var dc = Chart.MarksLayer.RenderOpen())
            {
                var value = TextDraw.Make(text, Fmt.ValueSize, Fg, ppd, true);
                double labelH = Fmt.ShowLabel ? 20 : 0;
                double y = Math.Max(0, (U.Size.Height - value.Height - labelH) / 2);
                dc.DrawText(value, new Point((U.Size.Width - value.Width) / 2, y));
                if (Fmt.ShowLabel)
                    TextDraw.Center(dc, col.Name, U.Size.Width / 2, y + value.Height + 2, 12, Fg2, ppd);
            }
        }

        /// <summary>Card number text. Percentages keep their format, since 12.3% scaled to units would be nonsense.</summary>
        internal static string Display(Value v, ResultColumn col, DisplayUnits units)
        {
            bool percent = col.FormatString != null && col.FormatString.Contains("%");
            return v.IsNumeric && !percent && units != DisplayUnits.None
                ? PowerBiFormat.Scaled(v.AsDouble(), units)
                : PowerBiFormat.Format(v, col.FormatString);
        }
    }

    /// <summary>A stack of small cards, one per category value. Only as many rows as fit are drawn; there is no scrolling.</summary>
    public sealed class MultiRowCardVisual : ChartVisual
    {
        private CardFormat Fmt => (CardFormat)U.Format;

        /// <inheritdoc/>
        public override VisualCapabilities Capabilities => new VisualCapabilities
        {
            DisplayName = "Multi-row card",
            Roles =
            {
                new DataRole("category", "Category", RoleKind.Grouping, 0, 2),
                new DataRole("values", "Fields", RoleKind.Measure, 1),
            },
            FormatType = typeof(CardFormat)
        };

        /// <inheritdoc/>
        protected override void Render()
        {
            var r = U.Data;
            if (r.RowCount == 0) { Message("No data"); return; }
            double W = U.Size.Width, ppd = Chart.PixelsPerDip;
            int visibleRows = Math.Max(1, (int)(U.Size.Height / 62));
            int count = Math.Min(r.RowCount, visibleRows);
            double rowH = U.Size.Height / Math.Max(1, count);
            using (var dc = Chart.MarksLayer.RenderOpen())
            {
                var accent = Frozen.Brush(U.Theme.Accent);
                for (int row = 0; row < count; row++)
                {
                    double y = row * rowH;
                    dc.DrawRectangle(accent, null, new Rect(0, y + 4, 3, rowH - 8));
                    string title = string.Join(" ", Enumerable.Range(0, r.GroupCount).Select(g => SeriesData.Label(r, g, row)));
                    if (title.Length > 0) dc.DrawText(TextDraw.Make(title, 12, Fg, ppd, true, W - 12), new Point(10, y + 4));
                    int m = r.MeasureCount;
                    double cw = (W - 10) / Math.Max(1, m);
                    for (int k = 0; k < m; k++)
                    {
                        var col = r.Columns[r.GroupCount + k];
                        double x = 10 + k * cw, top = y + (title.Length > 0 ? 22 : 6);
                        dc.DrawText(TextDraw.Make(CardVisual.Display(r.Measure(k, row), col, Fmt.Units), 16, Fg, ppd, true, cw - 6), new Point(x, top));
                        dc.DrawText(TextDraw.Make(col.Name, 10, Fg2, ppd, false, cw - 6), new Point(x, top + 21));
                    }
                    if (r.GroupCount > 0)
                        Marks.Add(new Mark { Bounds = new Rect(0, y, W, rowH), Point = PointFor(row) });
                }
            }
        }
    }
}
