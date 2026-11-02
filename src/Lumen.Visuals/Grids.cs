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
    /// <summary>Grids draw only the rows in view into one DrawingVisual, so row count does not affect redraw time.</summary>
    public abstract class GridVisualBase : ChartVisual
    {
        /// <summary>Vertical scroll offset. Kept across updates.</summary>
        protected double ScrollY;
        /// <summary>The format options, typed.</summary>
        protected GridFormat Fmt => (GridFormat)U.Format;
        /// <summary>Row height, from the font size.</summary>
        protected double RowH => Fmt.FontSize + 12;

        /// <summary>Adds wheel scrolling, which only redraws: all rows are already in memory.</summary>
        public override FrameworkElement CreateView(IVisualHost host)
        {
            var view = base.CreateView(host);
            view.MouseWheel += (s, e) =>
            {
                ScrollY = Math.Max(0, ScrollY - e.Delta / 2.0);
                if (U != null) { Marks.Clear(); Render(); }
                e.Handled = true;
            };
            return view;
        }

        /// <summary>The accent at an opacity for t in 0 to 1, for the color scale.</summary>
        protected Color Tint(Color c, double t) =>
            Frozen.WithAlpha(c, (byte)(30 + 150 * Math.Max(0, Math.Min(1, t))));
    }

    /// <summary>Flat table. A header click sorts on the client, so it reorders only the rows the query returned, at most the 3,500 row limit.</summary>
    public sealed class TableVisual : GridVisualBase
    {
        private int _sortColumn = -1;
        private bool _sortDesc;
        private readonly List<KeyValuePair<Rect, int>> _headers = new List<KeyValuePair<Rect, int>>();

        /// <inheritdoc/>
        public override VisualCapabilities Capabilities => new VisualCapabilities
        {
            DisplayName = "Table",
            Roles = { new DataRole("values", "Columns", RoleKind.Any, 1) },
            FormatType = typeof(GridFormat),
            IsInteractiveControl = false
        };

        /// <summary>Adds header click sorting. The preview event runs first and marks it handled, so a header click is not also a row selection.</summary>
        public override FrameworkElement CreateView(IVisualHost host)
        {
            var view = base.CreateView(host);
            view.PreviewMouseLeftButtonUp += (s, e) =>
            {
                var p = e.GetPosition(view);
                foreach (var h in _headers)
                {
                    if (!h.Key.Contains(p)) continue;
                    if (_sortColumn == h.Value) _sortDesc = !_sortDesc;
                    else { _sortColumn = h.Value; _sortDesc = false; }
                    Marks.Clear();
                    Render();
                    e.Handled = true;
                    return;
                }
            };
            return view;
        }

        /// <summary>One well, sorted out per field: summarized fields become measures and the rest become group columns. Hence result column order differs from well order.</summary>
        public override IReadOnlyList<QuerySpec> BuildQueries(VisualDef def, SemanticModel model)
        {
            var spec = new QuerySpec { IncludeTotals = true };
            foreach (var f in def.Role("values"))
            {
                bool numericByDefault = f.Kind == FieldKind.Column && !f.IsMeasureLike;
                if (f.IsMeasureLike || f.Kind == FieldKind.Measure) spec.Measures.Add(f);
                else if (numericByDefault && IsSummarized(f, model)) spec.Measures.AddRange(QueryHelpers.Normalize(new[] { f }, model));
                else spec.GroupBy.Add(f);
            }
            return spec.GroupBy.Count + spec.Measures.Count == 0 ? new QuerySpec[0] : new[] { spec };
        }

        /// <summary>True when the column has a default aggregation.</summary>
        private static bool IsSummarized(FieldRef f, SemanticModel m)
        {
            var c = m.Table(f.TableId)?.Column(f.ColumnId);
            return c != null && c.SummarizeBy != SummarizeBy.None;
        }

        /// <inheritdoc/>
        protected override void Render()
        {
            var r = U.Data;
            int cols = r.Columns.Count;
            if (cols == 0) { Message("No data"); return; }
            double W = U.Size.Width, H = U.Size.Height, ppd = Chart.PixelsPerDip, fs = Fmt.FontSize, rowH = RowH;
            bool totals = Fmt.ShowTotals && r.Totals != null && r.GroupCount > 0 && r.RowCount > 0;

            var order = Enumerable.Range(0, r.RowCount).ToArray();
            if (_sortColumn >= 0 && _sortColumn < cols)
            {
                var data = r.Data[_sortColumn];
                Array.Sort(order, (a, b) => { int c = Value.Compare(data[a], data[b]); return _sortDesc ? -c : c; });
            }

            var widths = new double[cols];
            for (int c = 0; c < cols; c++)
            {
                double w = TextDraw.Make(r.Columns[c].Name, fs, Fg, ppd, true).Width + 22;
                for (int i = 0; i < Math.Min(r.RowCount, 40); i++)
                    w = Math.Max(w, TextDraw.Make(CellText(r, c, order[i]), fs, Fg, ppd).Width + 16);
                widths[c] = w;
            }
            double totalW = widths.Sum();
            if (totalW < W) for (int c = 0; c < cols; c++) widths[c] *= W / totalW;

            double bodyH = H - rowH - (totals ? rowH : 0);
            double maxScroll = Math.Max(0, r.RowCount * rowH - bodyH);
            ScrollY = Math.Min(ScrollY, maxScroll);
            // Only rows in view are drawn, and only they get marks.
            int first = (int)(ScrollY / rowH);
            int last = Math.Min(r.RowCount, first + (int)(bodyH / rowH) + 2);

            var extremes = new Dictionary<int, double[]>();
            for (int c = r.GroupCount; c < cols; c++)
            {
                var vals = r.Data[c].Where(v => !v.IsBlank).Select(v => v.AsDouble()).ToList();
                if (vals.Count > 0) extremes[c] = new[] { vals.Min(), vals.Max() };
            }

            _headers.Clear();
            using (var dc = Chart.MarksLayer.RenderOpen())
            {
                dc.PushClip(new RectangleGeometry(new Rect(0, rowH, W, bodyH)));
                var line = Frozen.Pen(U.Theme.Grid, 1);
                var accent = U.Theme.Accent;
                for (int i = first; i < last; i++)
                {
                    int row = order[i];
                    double y = rowH + i * rowH - ScrollY;
                    string key = r.RowKey(row);
                    double opacity = Dim(key) ? 0.4 : 1;
                    dc.PushOpacity(opacity);
                    double x = 0;
                    for (int c = 0; c < cols; c++)
                    {
                        bool measure = c >= r.GroupCount;
                        var v = r.Data[c][row];
                        if (measure && !v.IsBlank && extremes.ContainsKey(c))
                        {
                            var e = extremes[c];
                            double t = e[1] > e[0] ? (v.AsDouble() - e[0]) / (e[1] - e[0]) : 1;
                            if (Fmt.ColorScale)
                                dc.DrawRectangle(Frozen.Brush(Tint(accent, t)), null, new Rect(x, y, widths[c], rowH));
                            if (Fmt.DataBars)
                                dc.DrawRectangle(Frozen.Brush(Frozen.WithAlpha(accent, 80)), null, new Rect(x + 2, y + 3, Math.Max(0, t) * (widths[c] - 4), rowH - 6));
                        }
                        var text = CellText(r, c, row);
                        if (measure) TextDraw.Right(dc, text, x + widths[c] - 6, y + 5, fs, Fg, ppd);
                        else dc.DrawText(TextDraw.Make(text, fs, Fg, ppd, false, widths[c] - 8), new Point(x + 6, y + 5));
                        x += widths[c];
                    }
                    dc.Pop();
                    dc.DrawLine(line, new Point(0, y + rowH), new Point(W, y + rowH));
                    if (r.GroupCount > 0)
                        Marks.Add(new Mark { Bounds = new Rect(0, Math.Max(rowH, y), W, rowH), Point = PointFor(row) });
                }
                dc.Pop();

                // Header
                dc.DrawRectangle(Frozen.Brush(U.Theme.VisualBackground), null, new Rect(0, 0, W, rowH));
                double hx = 0;
                for (int c = 0; c < cols; c++)
                {
                    string name = r.Columns[c].Name + (c == _sortColumn ? (_sortDesc ? " ▼" : " ▲") : "");
                    if (c >= r.GroupCount) TextDraw.Right(dc, name, hx + widths[c] - 6, 5, fs, Fg, ppd, true);
                    else dc.DrawText(TextDraw.Make(name, fs, Fg, ppd, true, widths[c] - 8), new Point(hx + 6, 5));
                    _headers.Add(new KeyValuePair<Rect, int>(new Rect(hx, 0, widths[c], rowH), c));
                    hx += widths[c];
                }
                dc.DrawLine(Frozen.Pen(U.Theme.Secondary, 1), new Point(0, rowH - 0.5), new Point(W, rowH - 0.5));

                if (totals)
                {
                    double y = H - rowH;
                    dc.DrawRectangle(Frozen.Brush(U.Theme.VisualBackground), null, new Rect(0, y, W, rowH));
                    dc.DrawLine(Frozen.Pen(U.Theme.Secondary, 1), new Point(0, y), new Point(W, y));
                    double x = 0;
                    for (int c = 0; c < cols; c++)
                    {
                        if (c == 0 && r.GroupCount > 0) dc.DrawText(TextDraw.Make("Total", fs, Fg, ppd, true), new Point(x + 6, y + 5));
                        if (c >= r.GroupCount)
                            TextDraw.Right(dc, PowerBiFormat.Format(r.Totals[c - r.GroupCount], r.Columns[c].FormatString), x + widths[c] - 6, y + 5, fs, Fg, ppd, true);
                        x += widths[c];
                    }
                }
            }
        }

        /// <summary>Formats a cell.</summary>
        private static string CellText(QueryResult r, int c, int row)
        {
            var v = r.Data[c][row];
            if (v.IsBlank) return c < r.GroupCount ? "(Blank)" : "";
            return PowerBiFormat.Format(v, r.Columns[c].FormatString);
        }
    }

    /// <summary>Rows with stepped layout and expand/collapse, one optional column group, totals both ways.
    /// Queries: one per row depth, each with and without the column group.</summary>
    public sealed class MatrixVisual : GridVisualBase
    {
        private readonly HashSet<string> _expanded = new HashSet<string>();
        private readonly List<KeyValuePair<Rect, string>> _toggles = new List<KeyValuePair<Rect, string>>();

        /// <summary>One row of the stepped tree. Path joins the labels from the root and is the key into the cell table.</summary>
        private sealed class Node
        {
            public string Path, Label;
            public int Depth;
            public List<Node> Children = new List<Node>();
        }

        /// <inheritdoc/>
        public override VisualCapabilities Capabilities => new VisualCapabilities
        {
            DisplayName = "Matrix",
            Roles =
            {
                new DataRole("rows", "Rows", RoleKind.Grouping, 1, 3),
                new DataRole("columns", "Columns", RoleKind.Grouping, 0, 1),
                new DataRole("values", "Values", RoleKind.Measure, 1),
            },
            FormatType = typeof(GridFormat)
        };

        /// <summary>Adds expand and collapse on the +/- toggles.</summary>
        public override FrameworkElement CreateView(IVisualHost host)
        {
            var view = base.CreateView(host);
            view.PreviewMouseLeftButtonUp += (s, e) =>
            {
                var p = e.GetPosition(view);
                foreach (var t in _toggles)
                {
                    if (!t.Key.Contains(p)) continue;
                    if (!_expanded.Remove(t.Value)) _expanded.Add(t.Value);
                    Marks.Clear();
                    Render();
                    e.Handled = true;
                    return;
                }
            };
            return view;
        }

        /// <summary>One query per subtotal level, from full depth down to the grand total, each repeated with the column group.
        /// Separate queries make subtotals real evaluations, so ratios and distinct counts come out right.</summary>
        public override IReadOnlyList<QuerySpec> BuildQueries(VisualDef def, SemanticModel model)
        {
            var rows = def.Role("rows");
            var cols = def.Role("columns");
            var measures = QueryHelpers.Normalize(def.Role("values"), model);
            if (rows.Count == 0 || measures.Count == 0) return new QuerySpec[0];

            var specs = new List<QuerySpec>();
            for (int depth = rows.Count; depth >= 0; depth--)
            {
                var prefix = rows.Take(depth).ToList();
                specs.Add(new QuerySpec { GroupBy = prefix.ToList(), Measures = measures });
                if (cols.Count > 0)
                    specs.Add(new QuerySpec { GroupBy = prefix.Concat(cols).ToList(), Measures = measures });
            }
            return specs;
        }

        /// <inheritdoc/>
        protected override void Render()
        {
            var all = U.All;
            var main = all[0];
            if (main.RowCount == 0) { Message("No data"); return; }

            int rowDepth = U.Def.Role("rows").Count;
            bool hasCols = U.Def.Role("columns").Count > 0;
            int measures = main.MeasureCount;

            // cells[path|colKey][measure]
            var cells = new Dictionary<string, Value>();
            var colKeys = new List<string>();
            foreach (var r in all)
            {
                if (r.Error != null) continue;
                int groups = r.GroupCount;
                bool withCol = hasCols && groups > 0 && r.Columns[groups - 1].Field.Key == U.Def.Role("columns")[0].Key;
                int rowCols = withCol ? groups - 1 : groups;
                for (int i = 0; i < r.RowCount; i++)
                {
                    string path = Path(r, rowCols, i);
                    string ck = withCol ? SeriesData.Label(r, groups - 1, i) : "";
                    if (withCol && !colKeys.Contains(ck)) colKeys.Add(ck);
                    for (int m = 0; m < measures; m++) cells[path + "\u001e" + ck + "\u001e" + m] = r.Measure(m, i);
                }
            }

            // The tree comes from the deepest query, the first one, so it holds only rows that exist there.
            var root = new Node { Path = "", Depth = -1 };
            for (int i = 0; i < main.RowCount; i++)
            {
                var n = root;
                string path = "";
                for (int d = 0; d < rowDepth; d++)
                {
                    string label = SeriesData.Label(main, d, i);
                    path = path.Length == 0 ? label : path + "\u001f" + label;
                    var child = n.Children.FirstOrDefault(c => c.Path == path);
                    if (child == null)
                    {
                        child = new Node { Path = path, Label = label, Depth = d };
                        n.Children.Add(child);
                    }
                    n = child;
                }
            }

            var flat = new List<Node>();
            Action<Node> walk = null;
            walk = n =>
            {
                foreach (var c in n.Children)
                {
                    flat.Add(c);
                    if (_expanded.Contains(c.Path)) walk(c);
                }
            };
            walk(root);

            double W = U.Size.Width, H = U.Size.Height, ppd = Chart.PixelsPerDip, fs = Fmt.FontSize, rowH = RowH;
            var columnKeys = hasCols ? colKeys.Concat(new[] { "" }).ToList() : new List<string> { "" };
            int dataCols = columnKeys.Count * measures;
            double labelW = Math.Min(W * 0.4, 36 + flat.Take(60).Select(n => TextDraw.Make(n.Label, fs, Fg, ppd).Width + n.Depth * 14).DefaultIfEmpty(60).Max());
            double cellW = Math.Max(70, (W - labelW) / Math.Max(1, dataCols));
            int headerRows = hasCols ? 2 : 1;
            double headerH = headerRows * rowH;
            double totalH = Fmt.ShowTotals ? rowH : 0;
            double bodyH = H - headerH - totalH;
            ScrollY = Math.Min(ScrollY, Math.Max(0, flat.Count * rowH - bodyH));
            int first = (int)(ScrollY / rowH), last = Math.Min(flat.Count, first + (int)(bodyH / rowH) + 2);

            Func<string, int, int, string> cell = (path, ci, m) =>
            {
                Value v;
                if (!cells.TryGetValue(path + "\u001e" + columnKeys[ci] + "\u001e" + m, out v) || v.IsBlank) return "";
                return PowerBiFormat.Format(v, main.Columns[main.GroupCount + m].FormatString);
            };

            _toggles.Clear();
            using (var dc = Chart.MarksLayer.RenderOpen())
            {
                dc.PushClip(new RectangleGeometry(new Rect(0, headerH, W, bodyH)));
                var line = Frozen.Pen(U.Theme.Grid, 1);
                for (int i = first; i < last; i++)
                {
                    var n = flat[i];
                    double y = headerH + i * rowH - ScrollY;
                    double indent = 6 + n.Depth * 14;
                    if (n.Depth < rowDepth - 1)
                    {
                        dc.DrawText(TextDraw.Make(_expanded.Contains(n.Path) ? "−" : "+", fs, Fg2, ppd, true), new Point(indent, y + 4));
                        _toggles.Add(new KeyValuePair<Rect, string>(new Rect(indent - 3, y, 16, rowH), n.Path));
                    }
                    dc.DrawText(TextDraw.Make(n.Label, fs, Fg, ppd, n.Depth == 0 && rowDepth > 1, labelW - indent - 14), new Point(indent + 14, y + 5));
                    double x = labelW;
                    for (int ci = 0; ci < columnKeys.Count; ci++)
                        for (int m = 0; m < measures; m++)
                        {
                            TextDraw.Right(dc, cell(n.Path, ci, m), x + cellW - 6, y + 5, fs, Fg, ppd, ci == columnKeys.Count - 1 && hasCols);
                            x += cellW;
                        }
                    dc.DrawLine(line, new Point(0, y + rowH), new Point(W, y + rowH));
                }
                dc.Pop();

                dc.DrawRectangle(Frozen.Brush(U.Theme.VisualBackground), null, new Rect(0, 0, W, headerH));
                double hx = labelW;
                for (int ci = 0; ci < columnKeys.Count; ci++)
                {
                    if (hasCols)
                        TextDraw.Center(dc, columnKeys[ci].Length == 0 ? "Total" : columnKeys[ci], hx + cellW * measures / 2, 5, fs, Fg, ppd, true, cellW * measures - 4);
                    for (int m = 0; m < measures; m++)
                    {
                        TextDraw.Right(dc, main.Columns[main.GroupCount + m].Name, hx + cellW - 6, (headerRows - 1) * rowH + 5, fs, Fg2, ppd, true);
                        hx += cellW;
                    }
                }
                dc.DrawLine(Frozen.Pen(U.Theme.Secondary, 1), new Point(0, headerH - 0.5), new Point(W, headerH - 0.5));

                if (Fmt.ShowTotals)
                {
                    double y = H - rowH;
                    dc.DrawRectangle(Frozen.Brush(U.Theme.VisualBackground), null, new Rect(0, y, W, rowH));
                    dc.DrawLine(Frozen.Pen(U.Theme.Secondary, 1), new Point(0, y), new Point(W, y));
                    dc.DrawText(TextDraw.Make("Total", fs, Fg, ppd, true), new Point(6, y + 5));
                    double x = labelW;
                    for (int ci = 0; ci < columnKeys.Count; ci++)
                        for (int m = 0; m < measures; m++)
                        {
                            TextDraw.Right(dc, cell("", ci, m), x + cellW - 6, y + 5, fs, Fg, ppd, true);
                            x += cellW;
                        }
                }
            }
        }

        /// <summary>Joins a row's labels down to a depth.</summary>
        private static string Path(QueryResult r, int depth, int row)
        {
            var parts = new string[depth];
            for (int d = 0; d < depth; d++) parts[d] = SeriesData.Label(r, d, row);
            return string.Join("\u001f", parts);
        }
    }
}
