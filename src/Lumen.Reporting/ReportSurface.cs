using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Lumen.Charting;
using Lumen.Engine;
using Lumen.Model;
using Lumen.Visuals;

namespace Lumen.Reporting
{
    /// <summary>One visual on the page: frame, title and the drawn content. It holds no editing behavior; the builder derives from it to add resize handles.</summary>
    public class VisualContainer : Grid, IVisualHost
    {
        private readonly ReportSurface _surface;
        private readonly Border _frame = new Border();
        private readonly TextBlock _title = new TextBlock { FontWeight = FontWeights.SemiBold, Margin = new Thickness(8, 5, 8, 2), TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly Grid _body = new Grid();
        private readonly FrameworkElement _view;

        /// <summary>Id of the visual shown.</summary>
        public readonly string VisualId;
        /// <summary>Its type key. If the type changes, the surface replaces this container.</summary>
        public readonly string VisualType;
        /// <summary>The visual instance.</summary>
        public readonly ILumenVisual Visual;
        /// <summary>Capabilities, cached, since the getter builds a new object per call.</summary>
        public readonly VisualCapabilities Caps;
        /// <summary>Latest results, for redraws without a query and for export.</summary>
        public IReadOnlyList<QueryResult> LastResults;

        /// <summary>Builds the visual and the frame around it.</summary>
        public VisualContainer(ReportSurface surface, VisualDef def)
        {
            _surface = surface;
            VisualId = def.Id;
            VisualType = def.Type;
            Visual = VisualRegistry.Create(def.Type);
            Caps = Visual.Capabilities;
            _view = Visual.CreateView(this);

            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            RowDefinitions.Add(new RowDefinition());
            Children.Add(_frame);
            SetRowSpan(_frame, 2);
            Children.Add(_title);
            _body.Children.Add(_view);
            SetRow(_body, 1);
            Children.Add(_body);

            _body.SizeChanged += (s, e) => Redraw();
            ContextMenu = surface.BuildMenu(this);
        }

        /// <summary>The current definition, looked up each time because undo replaces the objects. Null if the visual was removed.</summary>
        public VisualDef Def => _surface.Report.Doc.Find(_surface.PageId, VisualId);

        /// <summary>The deserialized format options.</summary>
        public object Format => VisualRegistry.ReadFormat(Def, Caps.FormatType);

        /// <summary>Applies format and theme to the frame and title.</summary>
        public virtual void SyncChrome()
        {
            var def = Def;
            if (def == null) return;
            var theme = _surface.Report.Theme;
            var fmt = (VisualFormat)Format;
            _frame.Background = Frozen.Brush(ResolvedTheme.Parse(fmt.Background, theme.VisualBackground));
            _frame.BorderBrush = Frozen.Brush(theme.Border);
            _frame.BorderThickness = new Thickness(fmt.ShowBorder ? 1 : 0);
            _title.Foreground = Frozen.Brush(theme.Foreground);
            _title.FontSize = fmt.TitleSize;
            _title.Text = string.IsNullOrWhiteSpace(fmt.Title) ? AutoTitle(def) : fmt.Title;
            _title.Visibility = fmt.ShowTitle ? Visibility.Visible : Visibility.Collapsed;
            _body.Margin = new Thickness(fmt.Padding);
        }

        /// <summary>A title from up to three fields.</summary>
        private string AutoTitle(VisualDef def)
        {
            var model = _surface.Report.Model;
            var fields = def.Roles.Values.SelectMany(x => x).Select(model.DisplayName).Take(3).ToList();
            return fields.Count == 0 ? Caps.DisplayName : string.Join(" by ", fields);
        }

        /// <summary>Stores new results and redraws.</summary>
        public void Apply(IReadOnlyList<QueryResult> results)
        {
            LastResults = results;
            Redraw();
        }

        /// <summary>Redraws from the last results, as after a resize, a theme change or a format edit.</summary>
        public void Redraw()
        {
            var def = Def;
            if (def == null) return;
            Visual.Update(new VisualUpdate
            {
                Data = LastResults != null && LastResults.Count > 0 ? LastResults[0] : null,
                All = LastResults,
                Size = new Size(_body.ActualWidth, _body.ActualHeight),
                Format = Format,
                Theme = _surface.Report.Theme,
                Selection = _surface.SelectionKeysFor(VisualId),
                Def = def,
                Model = _surface.Report.Model
            });
        }

        // IVisualHost
        // Interface members are explicit, so that only a visual sees them.
        void IVisualHost.Select(DataPoint point, bool multi) => _surface.SelectPoint(this, point, multi);
        void IVisualHost.ClearSelection() => _surface.ClearSelection();
        void IVisualHost.ShowTooltip(IReadOnlyList<TooltipItem> items) => _surface.ShowTooltip(items);
        void IVisualHost.HideTooltip() => _surface.HideTooltip();
        Color IVisualHost.ColorFor(string key) => _surface.Report.ColorFor(key);
        void IVisualHost.SetSlicerFilter(FilterDef filter) => _surface.SetSlicer(this, filter);
    }

    /// <summary>Shows one page of a report and runs it: containers, zoom, slicers, cross-filtering, the refresh loop, tooltips and focus mode.
    /// It changes nothing but slicer state, so this class alone is a read-only viewer. <c>ReportCanvas</c> in the builder derives from it to add the editing gestures.</summary>
    public class ReportSurface : UserControl
    {
        /// <summary>The report being shown.</summary>
        public readonly ReportSession Report;
        /// <summary>The page shown.</summary>
        public string PageId;
        /// <summary>Page zoom, applied as a layout transform.</summary>
        public double Zoom { get => _zoom.ScaleX; set { _zoom.ScaleX = value; _zoom.ScaleY = value; } }
        /// <summary>Raised after each refresh with the performance report.</summary>
        public event Action<string> PerfUpdated;

        private readonly Canvas _page = new Canvas { ClipToBounds = true };
        private readonly ScaleTransform _zoom = new ScaleTransform(1, 1);
        private readonly System.Windows.Controls.Primitives.Popup _tooltip = new System.Windows.Controls.Primitives.Popup { Placement = PlacementMode.Mouse, IsHitTestVisible = false, AllowsTransparency = true };
        private readonly Border _tooltipBody = new Border { Padding = new Thickness(8, 5, 8, 5), BorderThickness = new Thickness(1) };
        private readonly StackPanel _tooltipLines = new StackPanel();
        private string _tooltipKey;

        private CancellationTokenSource _cts;
        private string _focusId;

        // Cross-filter
        // The visual holding a cross-filter selection, and the selected points. Only one visual on a page holds a selection at a time.
        private string _selectionSource;
        private readonly List<DataPoint> _points = new List<DataPoint>();

        /// <summary>The containers on the page, by visual id.</summary>
        protected readonly Dictionary<string, VisualContainer> Containers = new Dictionary<string, VisualContainer>();
        /// <summary>The zoomed grid that holds the page. The builder layers its guides over the page here.</summary>
        protected readonly Grid PageRoot = new Grid { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16) };

        /// <summary>Builds the empty surface.</summary>
        public ReportSurface(ReportSession report)
        {
            Report = report;
            Focusable = true;

            PageRoot.LayoutTransform = _zoom;
            PageRoot.Children.Add(_page);
            Content = new ScrollViewer { Content = PageRoot, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

            _tooltipBody.Child = _tooltipLines;
            _tooltip.Child = _tooltipBody;

            _page.MouseLeftButtonDown += (s, e) => { if (ReferenceEquals(e.OriginalSource, _page)) OnBarePagePressed(e); };
        }

        /// <summary>The current page.</summary>
        public PageDef Page => Report.Doc.Page(PageId);
        /// <summary>The page element, for rendering to images.</summary>
        public Canvas PageSurface => _page;
        /// <summary>The container of a visual on this page, or null.</summary>
        public VisualContainer Container(string visualId)
        {
            VisualContainer c;
            return Containers.TryGetValue(visualId, out c) ? c : null;
        }

        // ---------------------------------------------------------- sync with the document

        /// <summary>Switches page. All containers are rebuilt, and focus and the cross-filter are cleared.</summary>
        public void ShowPage(string pageId)
        {
            PageId = pageId;
            OnPageChanging();
            _focusId = null;
            ClearPoints();
            foreach (var c in Containers.Values) c.Visual.Dispose();
            Containers.Clear();
            _page.Children.Clear();
            Sync(true);
            OnPageChanged();
        }

        /// <summary>Shows a page, and completes once its refresh has ended and rendering has gone idle.</summary>
        public Task ShowPageAndWait(string pageId)
        {
            var done = new TaskCompletionSource<bool>();
            Action handler = null;
            handler = () =>
            {
                Refreshed -= handler;
                Dispatcher.BeginInvoke(new Action(() => done.TrySetResult(true)), DispatcherPriority.ApplicationIdle);
            };
            Refreshed += handler;
            ShowPage(pageId);
            return done.Task;
        }

        /// <summary>Reconciles containers with the document, by id: removes the stale, replaces any whose type changed, adds the new, repositions the rest.
        /// Without <paramref name="requery"/> the visuals redraw from their last results.</summary>
        public void Sync(bool requery)
        {
            var page = Page;
            if (page == null) return;
            _page.Width = page.Width;
            _page.Height = page.Height;
            _page.Background = Frozen.Brush(Report.Theme.Background);

            var ids = new HashSet<string>(page.Visuals.Select(v => v.Id));
            foreach (var id in Containers.Keys.Where(k => !ids.Contains(k)).ToList())
            {
                _page.Children.Remove(Containers[id]);
                Containers[id].Visual.Dispose();
                Containers.Remove(id);
            }
            OnSyncing(page, ids);

            foreach (var v in page.Visuals)
            {
                VisualContainer c;
                if (Containers.TryGetValue(v.Id, out c) && c.VisualType != v.Type)
                {
                    _page.Children.Remove(c);
                    c.Visual.Dispose();
                    Containers.Remove(v.Id);
                    c = null;
                }
                if (c == null)
                {
                    c = CreateContainer(v);
                    Containers[v.Id] = c;
                    _page.Children.Add(c);
                }
                Place(c, v);
                c.SyncChrome();
            }
            OnSynced();
            if (requery) Refresh(0);
            else foreach (var c in Containers.Values) c.Redraw();
        }

        /// <summary>Makes the container of a new visual.</summary>
        protected virtual VisualContainer CreateContainer(VisualDef v) => new VisualContainer(this, v);
        /// <summary>Runs as a page switch starts, before the old containers go.</summary>
        protected virtual void OnPageChanging() { }
        /// <summary>Runs after a page switch.</summary>
        protected virtual void OnPageChanged() { }
        /// <summary>Runs in <see cref="Sync"/> once the page is sized and stale containers are gone. <paramref name="ids"/> holds the visuals of the page.</summary>
        protected virtual void OnSyncing(PageDef page, ISet<string> ids) { }
        /// <summary>Runs at the end of <see cref="Sync"/>, before any query.</summary>
        protected virtual void OnSynced() { }
        /// <summary>Runs on a press on the bare page. A press there clears the cross-filter.</summary>
        protected virtual void OnBarePagePressed(MouseButtonEventArgs e)
        {
            Focus();
            ClearSelection();
        }

        /// <summary>Builds a visual's right-click menu. These items change nothing in the report; the builder adds its editing items after them.</summary>
        public virtual ContextMenu BuildMenu(VisualContainer c)
        {
            var m = new ContextMenu();
            AddItem(m, "Focus mode", () => ToggleFocus(c));
            AddItem(m, "Show as table", () => ShowAsTable(c));
            AddItem(m, "Export data (CSV)", () => ExportData(c));
            AddItem(m, "Copy as image", () => CopyAsImage(c));
            return m;
        }

        /// <summary>Adds a menu item.</summary>
        protected static void AddItem(ContextMenu menu, string text, Action act)
        {
            var mi = new MenuItem { Header = text };
            mi.Click += (s, e) => act();
            menu.Items.Add(mi);
        }

        /// <summary>Sets position and visibility, taking focus mode into account.</summary>
        protected void Place(VisualContainer c, VisualDef v)
        {
            bool focused = _focusId == v.Id;
            bool hidden = v.IsHidden || (_focusId != null && !focused);
            c.Visibility = hidden ? Visibility.Collapsed : Visibility.Visible;
            System.Windows.Controls.Canvas.SetLeft(c, focused ? 0 : v.X);
            System.Windows.Controls.Canvas.SetTop(c, focused ? 0 : v.Y);
            c.Width = focused ? Page.Width : v.Width;
            c.Height = focused ? Page.Height : v.Height;
            Panel.SetZIndex(c, focused ? 100000 : v.ZIndex);
        }

        // ---------------------------------------------------------- slicers, cross-filter, interactions

        /// <summary>The selected row keys, for the visual that holds the selection only.</summary>
        public HashSet<string> SelectionKeysFor(string visualId) =>
            visualId == _selectionSource ? new HashSet<string>(_points.Select(p => p.Key)) : new HashSet<string>();

        /// <summary>Forgets the cross-filter selection.</summary>
        private void ClearPoints()
        {
            _points.Clear();
            _selectionSource = null;
        }

        /// <summary>Handles a mark click. A second click on a selected mark deselects it.</summary>
        public void SelectPoint(VisualContainer source, DataPoint point, bool multi)
        {
            if (_selectionSource != source.VisualId) ClearPoints();
            _selectionSource = source.VisualId;
            int at = _points.FindIndex(p => p.Key == point.Key);
            if (at >= 0) _points.RemoveAt(at);
            else
            {
                if (!multi) _points.Clear();
                _points.Add(point);
            }
            if (_points.Count == 0) _selectionSource = null;
            Refresh(30);
        }

        /// <summary>Clears the cross-filter selection.</summary>
        public void ClearSelection()
        {
            if (_selectionSource == null) return;
            ClearPoints();
            Refresh(30);
        }

        /// <summary>Stores a slicer's filter and refreshes.</summary>
        public void SetSlicer(VisualContainer source, FilterDef filter)
        {
            // Runtime state; it is saved with the file but is not an undo step.
            var def = source.Def;
            if (def == null) return;
            def.SlicerState = filter;
            Refresh(30);
        }

        /// <summary>The effect of a source on a target: the user's override, else a slicer filters, and anything else highlights if the target can, or filters.</summary>
        public InteractionMode InteractionMode(VisualContainer source, VisualContainer target)
        {
            var o = Page.Interactions.FirstOrDefault(i => i.SourceVisualId == source.VisualId && i.TargetVisualId == target.VisualId);
            if (o != null) return o.Mode;
            if (source.Caps.IsSlicer) return Model.InteractionMode.Filter;
            return target.Caps.SupportsHighlight ? Model.InteractionMode.Highlight : Model.InteractionMode.Filter;
        }

        /// <summary>Turns selected points into Basic filters, one per column. With several columns this is a per-column match, not a tuple match.</summary>
        private List<FilterDef> SelectionFilters()
        {
            var filters = new List<FilterDef>();
            if (_points.Count == 0) return filters;
            foreach (var group in _points.SelectMany(p => p.Fields).GroupBy(f => f.Key.Key))
            {
                var values = group.Select(f => f.Value).Where(v => v != null).Distinct().ToList();
                if (values.Count > 0) filters.Add(FilterDef.Basic(group.First().Key, values));
            }
            return filters;
        }

        /// <summary>Merges all filter sources into a visual's query: report, page, visual, then slicers on the page, then any cross-filter selection.
        /// A slicer does not filter itself, so it keeps offering its other values.</summary>
        private void ComposeFilters(VisualContainer target, QuerySpec spec)
        {
            var page = Page;
            var def = target.Def;
            spec.Filters.AddRange(Report.Doc.ReportFilters);
            spec.Filters.AddRange(page.PageFilters);
            spec.Filters.AddRange(def.VisualFilters);

            foreach (var kv in Containers)
            {
                var source = kv.Value;
                if (source == target || !source.Caps.IsSlicer) continue;
                var state = source.Def?.SlicerState;
                if (state == null) continue;
                if (InteractionMode(source, target) != Model.InteractionMode.None) spec.Filters.Add(state);
            }

            if (_selectionSource != null && _selectionSource != target.VisualId && Containers.ContainsKey(_selectionSource))
            {
                var mode = InteractionMode(Containers[_selectionSource], target);
                if (mode == Model.InteractionMode.Filter) spec.Filters.AddRange(SelectionFilters());
                else if (mode == Model.InteractionMode.Highlight && target.Caps.SupportsHighlight && spec.Measures.Count > 0)
                    spec.Highlight.AddRange(SelectionFilters());
            }
        }

        /// <summary>One refresh at a time: a newer click cancels the batch still in flight.</summary>
        public async void Refresh(int delayMs)
        {
            _cts?.Cancel();
            var cts = new CancellationTokenSource();
            _cts = cts;
            try
            {
                if (delayMs > 0) await Task.Delay(delayMs, cts.Token);
                await RefreshCore(cts.Token);
                Refreshed?.Invoke();
            }
            catch (OperationCanceledException) { }
        }

        /// <summary>Raised after a refresh completes. The self-check waits on it.</summary>
        public event Action Refreshed;

        /// <summary>Builds every visible visual's queries on the UI thread, runs them as one batch, and draws the results.</summary>
        private async Task RefreshCore(CancellationToken ct)
        {
            var visible = Containers.Values.Where(c => c.Visibility == Visibility.Visible).ToList();
            var specs = new List<QuerySpec>();
            var ranges = new List<Tuple<VisualContainer, int, int>>();
            var model = Report.Model;
            foreach (var c in visible)
            {
                var def = c.Def;
                var list = c.Visual.BuildQueries(def, model).ToList();
                foreach (var s in list) ComposeFilters(c, s);
                ranges.Add(Tuple.Create(c, specs.Count, list.Count));
                specs.AddRange(list);
            }

            var sw = Stopwatch.StartNew();
            var results = await Report.Engine.ExecuteBatchAsync(specs, ct);
            ct.ThrowIfCancellationRequested();
            double queryMs = sw.Elapsed.TotalMilliseconds;

            var perf = new StringBuilder();
            var render = Stopwatch.StartNew();
            foreach (var r in ranges)
            {
                var slice = results.Skip(r.Item2).Take(r.Item3).ToList();
                double before = render.Elapsed.TotalMilliseconds;
                r.Item1.Apply(slice.Count == 0 ? null : slice);
                double drawn = render.Elapsed.TotalMilliseconds - before;
                perf.AppendFormat("{0,-22} query {1,7:0.0} ms   draw {2,6:0.0} ms\n",
                    Truncate(r.Item1.Caps.DisplayName, 22), slice.Sum(x => x.QueryMs), drawn);
            }
            perf.Insert(0, $"Page refresh: {queryMs:0.0} ms in the engine, {render.Elapsed.TotalMilliseconds:0.0} ms drawing\n\n");
            PerfUpdated?.Invoke(perf.ToString());
        }

        /// <summary>Cuts a string to a length.</summary>
        private static string Truncate(string s, int n) => s.Length <= n ? s : s.Substring(0, n);

        // ---------------------------------------------------------- tooltip, focus, per-visual output

        /// <summary>Shows a tooltip at the mouse. Identical content is left alone, and toggling IsOpen repositions it.</summary>
        public void ShowTooltip(IReadOnlyList<TooltipItem> items)
        {
            string key = string.Join("|", items.Select(i => i.Label + i.Value));
            if (key == _tooltipKey && _tooltip.IsOpen) return;
            _tooltipKey = key;
            var theme = Report.Theme;
            _tooltipBody.Background = Frozen.Brush(theme.VisualBackground);
            _tooltipBody.BorderBrush = Frozen.Brush(theme.Border);
            _tooltipLines.Children.Clear();
            foreach (var i in items)
            {
                var line = new TextBlock { Foreground = Frozen.Brush(theme.Foreground), FontSize = 12 };
                line.Inlines.Add(new System.Windows.Documents.Run(i.Label + ":  ") { Foreground = Frozen.Brush(theme.Secondary) });
                line.Inlines.Add(new System.Windows.Documents.Run(i.Value) { FontWeight = FontWeights.SemiBold });
                _tooltipLines.Children.Add(line);
            }
            _tooltip.IsOpen = false;
            _tooltip.IsOpen = true;
        }

        /// <summary>Hides the tooltip.</summary>
        public void HideTooltip()
        {
            _tooltip.IsOpen = false;
            _tooltipKey = null;
        }

        /// <summary>Enters or leaves focus mode.</summary>
        public void ToggleFocus(VisualContainer c)
        {
            _focusId = _focusId == c.VisualId ? null : c.VisualId;
            Sync(true);
        }

        /// <summary>Handles Esc: leaves focus mode, or else clears the selection. Returns true if it did something.</summary>
        public bool ClearEscape()
        {
            if (_focusId != null) { _focusId = null; Sync(true); return true; }
            if (_selectionSource != null) { ClearSelection(); return true; }
            return false;
        }

        /// <summary>Saves the visual's first result as CSV.</summary>
        public void ExportData(VisualContainer c)
        {
            var r = c.LastResults?.FirstOrDefault();
            if (r == null) return;
            var dlg = new Microsoft.Win32.SaveFileDialog { Filter = "CSV|*.csv", FileName = c.Caps.DisplayName + ".csv" };
            if (dlg.ShowDialog() != true) return;
            File.WriteAllText(dlg.FileName, Exporter.ToCsv(r), System.Text.Encoding.UTF8);
        }

        /// <summary>Shows the visual's first result in a window. Values are formatted once, into dictionary columns, so that the virtual grid can display them.</summary>
        public void ShowAsTable(VisualContainer c)
        {
            var r = c.LastResults?.FirstOrDefault();
            if (r == null) return;
            var grid = new DataGrid { AutoGenerateColumns = false, IsReadOnly = true, CanUserSortColumns = false };
            var columns = new List<Column>();
            for (int i = 0; i < r.Columns.Count; i++)
            {
                var b = new DictBuilder();
                for (int row = 0; row < r.RowCount; row++)
                    b.Add(r.Data[i][row].IsBlank ? null : PowerBiFormat.Format(r.Data[i][row], r.Columns[i].FormatString));
                columns.Add(b.Finish(r.Columns[i].Name));
            }
            VirtualRows.Fill(grid, columns, null, r.RowCount);
            new Window { Title = "Show as table", Width = 700, Height = 440, Owner = Window.GetWindow(this), WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = grid }.Show();
        }

        /// <summary>Copies the visual to the clipboard at twice its size.</summary>
        public void CopyAsImage(VisualContainer c) => Clipboard.SetImage(Exporter.Render(c, c.ActualWidth, c.ActualHeight, 2));
    }
}
