using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Lumen.Charting;
using Lumen.Model;
using Lumen.Reporting;
using Lumen.Visuals;

namespace Lumen.Desktop
{
    /// <summary>A visual in the builder: adds the selection frame, resize handles and interaction buttons to the shared container.</summary>
    public sealed class EditableVisualContainer : VisualContainer
    {
        private readonly ReportCanvas _canvas;
        private readonly Border _accent = new Border { BorderThickness = new Thickness(1.5), IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        private readonly Grid _handles = new Grid { Visibility = Visibility.Collapsed };
        private readonly StackPanel _interactionBar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 2, 0), Visibility = Visibility.Collapsed };

        /// <summary>Builds the visual and the editing chrome around it.</summary>
        public EditableVisualContainer(ReportCanvas canvas, VisualDef def) : base(canvas, def)
        {
            _canvas = canvas;
            Children.Add(_accent);
            SetRowSpan(_accent, 2);
            Children.Add(_handles);
            SetRowSpan(_handles, 2);
            Children.Add(_interactionBar);
            SetRowSpan(_interactionBar, 2);

            foreach (var h in new[] { "nw", "n", "ne", "e", "se", "s", "sw", "w" }) _handles.Children.Add(MakeHandle(h));
        }

        /// <summary>Makes one resize handle. <paramref name="dir"/> holds compass letters.</summary>
        private Thumb MakeHandle(string dir)
        {
            var t = new Thumb
            {
                Width = 9, Height = 9,
                Background = Brushes.White,
                BorderBrush = (Brush)Application.Current.Resources["Accent"],
                HorizontalAlignment = dir.Contains("w") ? HorizontalAlignment.Left : dir.Contains("e") ? HorizontalAlignment.Right : HorizontalAlignment.Center,
                VerticalAlignment = dir.Contains("n") ? VerticalAlignment.Top : dir.Contains("s") ? VerticalAlignment.Bottom : VerticalAlignment.Center,
                Margin = new Thickness(-4),
                Cursor = dir == "n" || dir == "s" ? Cursors.SizeNS : dir == "e" || dir == "w" ? Cursors.SizeWE
                    : dir == "nw" || dir == "se" ? Cursors.SizeNWSE : Cursors.SizeNESW
            };
            t.DragStarted += (s, e) => _canvas.BeginGesture();
            t.DragDelta += (s, e) => _canvas.ResizeBy(this, dir, e.HorizontalChange, e.VerticalChange);
            t.DragCompleted += (s, e) => _canvas.EndGesture("Resize visual");
            return t;
        }

        /// <summary>Applies format and theme, including the selection frame.</summary>
        public override void SyncChrome()
        {
            base.SyncChrome();
            _accent.BorderBrush = Frozen.Brush(_canvas.Report.Theme.Accent);
        }

        /// <summary>Shows or hides the selection frame and handles.</summary>
        public void SetSelected(bool selected, bool editable)
        {
            _accent.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
            _handles.Visibility = selected && editable && !(Def?.IsLocked ?? false) ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>Shows the Filter, Highlight and None buttons of Edit interactions on a target visual.</summary>
        public void ShowInteractionButtons(VisualContainer source)
        {
            _interactionBar.Children.Clear();
            if (source == null || source == this) { _interactionBar.Visibility = Visibility.Collapsed; return; }
            var current = _canvas.InteractionMode(source, this);
            foreach (InteractionMode mode in Enum.GetValues(typeof(InteractionMode)))
            {
                if (mode == InteractionMode.Highlight && !Caps.SupportsHighlight) continue;
                var b = new Button
                {
                    Content = mode == InteractionMode.Filter ? "" : mode == InteractionMode.Highlight ? "" : "",
                    FontFamily = new FontFamily("Segoe MDL2 Assets"),
                    ToolTip = mode.ToString(),
                    Width = 24, Height = 22,
                    Background = mode == current ? (Brush)Application.Current.Resources["Accent"] : Brushes.White,
                    Foreground = mode == current ? Brushes.White : Brushes.Black
                };
                var chosen = mode;
                b.Click += (s, e) => { _canvas.SetInteraction(source, this, chosen); e.Handled = true; };
                _interactionBar.Children.Add(b);
            }
            _interactionBar.Visibility = Visibility.Visible;
        }
    }

    /// <summary>The builder's canvas: the shared surface plus selection, drag, resize, snapping, guides and arrange commands.</summary>
    public sealed class ReportCanvas : ReportSurface
    {
        /// <summary>Snap grid in page units.</summary>
        public const double GridStep = 8;

        /// <summary>The session being edited.</summary>
        public readonly Session Session;
        /// <summary>True while the Edit interactions mode is on.</summary>
        public bool EditInteractions { get; private set; }
        /// <summary>Raised when the selected visuals change.</summary>
        public event Action SelectionChanged;

        private readonly Canvas _overlay = new Canvas { IsHitTestVisible = false };
        private readonly List<string> _selected = new List<string>();

        // Drag state
        // A drag starts only once the mouse has moved 4 pixels; before that a press is still a click on a chart mark.
        private Point _dragStart;
        private bool _pressed, _dragging, _rubber;
        private VisualContainer _pressedOn;
        private Rectangle _rubberBand;
        private List<KeyValuePair<string, SetBoundsCommand.Bounds>> _gestureBefore;
        private int _nudgeGesture;

        /// <summary>Builds the empty canvas.</summary>
        public ReportCanvas(Session session) : base(session)
        {
            Session = session;
            PageRoot.Children.Add(_overlay);

            PageSurface.AllowDrop = true;
            PageSurface.PreviewMouseMove += OnPreviewMove;
            PageSurface.PreviewMouseLeftButtonUp += OnPreviewUp;
            PageSurface.DragOver += (s, e) => { e.Effects = e.Data.GetDataPresent(typeof(FieldRef)) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
            PageSurface.Drop += OnFieldDropped;
        }

        /// <summary>Selected visual ids, in selection order.</summary>
        public IReadOnlyList<string> Selected => _selected;
        /// <summary>The first selected visual, which the panes edit.</summary>
        public VisualContainer PrimarySelection => _selected.Count > 0 ? Container(_selected[0]) : null;

        /// <summary>The right-click menu: the shared items, then the editing ones.</summary>
        public override ContextMenu BuildMenu(VisualContainer c)
        {
            var m = base.BuildMenu(c);
            m.Items.Add(new Separator());
            AddItem(m, "Bring to front", () => Reorder(c, true));
            AddItem(m, "Send to back", () => Reorder(c, false));
            AddItem(m, "Remove", () => Remove(new[] { c.VisualId }));
            return m;
        }

        /// <inheritdoc/>
        protected override void OnPageChanging() => _selected.Clear();
        /// <inheritdoc/>
        protected override void OnPageChanged() => SelectionChanged?.Invoke();
        /// <inheritdoc/>
        protected override void OnSynced() => UpdateSelectionVisuals();

        /// <summary>Sizes the guide layer and drops selected ids that left the page.</summary>
        protected override void OnSyncing(PageDef page, ISet<string> ids)
        {
            _overlay.Width = page.Width;
            _overlay.Height = page.Height;
            _selected.RemoveAll(id => !ids.Contains(id));
        }

        /// <summary>Makes the editable container and wires its press.</summary>
        protected override VisualContainer CreateContainer(VisualDef v)
        {
            var c = new EditableVisualContainer(this, v);
            Wire(c);
            return c;
        }

        /// <summary>Handles press on a visual. Preview runs before the chart sees the click, so selecting and dragging come first.</summary>
        private void Wire(VisualContainer c)
        {
            c.PreviewMouseLeftButtonDown += (s, e) =>
            {
                Focus();
                bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
                if (ctrl) Toggle(c.VisualId);
                else if (!_selected.Contains(c.VisualId)) SelectOnly(c.VisualId);

                var src = e.OriginalSource as DependencyObject;
                bool onThumb = FindParent<Thumb>(src) != null;
                bool onControl = c.Caps.IsInteractiveControl && !(src is TextBlock tb && tb == TitleOf(c));
                _pressed = !onThumb && !onControl && Page.Visuals.All(v => !(_selected.Contains(v.Id) && v.IsLocked));
                _pressedOn = c;
                _dragStart = e.GetPosition(PageSurface);
                _dragging = false;
            };
        }

        /// <summary>The title element of a container.</summary>
        private static TextBlock TitleOf(VisualContainer c) => c.Children.OfType<TextBlock>().FirstOrDefault();

        /// <summary>Walks up from a hit element. Text elements are not Visuals, so it falls back to the logical tree for them.</summary>
        private static T FindParent<T>(DependencyObject o) where T : DependencyObject
        {
            while (o != null)
            {
                if (o is T t) return t;
                o = o is Visual || o is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(o) : LogicalTreeHelper.GetParent(o);
            }
            return null;
        }

        // ---------------------------------------------------------- selection

        /// <summary>Makes one visual the whole selection.</summary>
        private void SelectOnly(string id)
        {
            _selected.Clear();
            _selected.Add(id);
            UpdateSelectionVisuals();
            SelectionChanged?.Invoke();
        }

        /// <summary>Adds or removes a visual from the selection.</summary>
        private void Toggle(string id)
        {
            if (!_selected.Remove(id)) _selected.Add(id);
            UpdateSelectionVisuals();
            SelectionChanged?.Invoke();
        }

        /// <summary>Replaces the selection.</summary>
        public void SetSelection(IEnumerable<string> ids)
        {
            _selected.Clear();
            _selected.AddRange(ids);
            UpdateSelectionVisuals();
            SelectionChanged?.Invoke();
        }

        /// <summary>Updates frames and interaction buttons.</summary>
        private void UpdateSelectionVisuals()
        {
            foreach (var kv in Containers) ((EditableVisualContainer)kv.Value).SetSelected(_selected.Contains(kv.Key), true);
            RefreshInteractionBars();
        }

        /// <summary>Shows interaction buttons on all other visuals while the mode is on.</summary>
        private void RefreshInteractionBars()
        {
            var source = EditInteractions ? PrimarySelection : null;
            foreach (var c in Containers.Values) ((EditableVisualContainer)c).ShowInteractionButtons(source);
        }

        /// <summary>Turns Edit interactions on or off.</summary>
        public void SetEditInteractions(bool on)
        {
            EditInteractions = on;
            RefreshInteractionBars();
        }

        // ---------------------------------------------------------- pointer gestures

        /// <summary>A press on the bare page clears selections and starts a rubber band.</summary>
        protected override void OnBarePagePressed(MouseButtonEventArgs e)
        {
            if (_selected.Count > 0) SetSelection(new string[0]);
            base.OnBarePagePressed(e);
            _rubber = true;
            _dragStart = e.GetPosition(PageSurface);
            _rubberBand = new Rectangle
            {
                Stroke = (Brush)Application.Current.Resources["Accent"],
                StrokeDashArray = new DoubleCollection { 3, 2 },
                Fill = new SolidColorBrush(Color.FromArgb(30, 17, 141, 255))
            };
            _overlay.Children.Add(_rubberBand);
            PageSurface.CaptureMouse();
        }

        /// <summary>Updates the rubber band, or starts or continues a drag.</summary>
        private void OnPreviewMove(object sender, MouseEventArgs e)
        {
            var p = e.GetPosition(PageSurface);
            if (_rubber)
            {
                var r = new Rect(_dragStart, p);
                System.Windows.Controls.Canvas.SetLeft(_rubberBand, r.X);
                System.Windows.Controls.Canvas.SetTop(_rubberBand, r.Y);
                _rubberBand.Width = r.Width;
                _rubberBand.Height = r.Height;
                return;
            }
            if (!_pressed || e.LeftButton != MouseButtonState.Pressed) return;
            if (!_dragging)
            {
                if ((p - _dragStart).Length < 4) return;
                _dragging = true;
                BeginGesture();
                _pressedOn.CaptureMouse();
            }
            MoveSelection(p - _dragStart);
        }

        /// <summary>Finishes a rubber band or a drag.</summary>
        private void OnPreviewUp(object sender, MouseButtonEventArgs e)
        {
            if (_rubber)
            {
                _rubber = false;
                PageSurface.ReleaseMouseCapture();
                var r = new Rect(_dragStart, e.GetPosition(PageSurface));
                _overlay.Children.Remove(_rubberBand);
                if (r.Width > 2 || r.Height > 2)
                    SetSelection(Page.Visuals.Where(v => !v.IsHidden && r.IntersectsWith(new Rect(v.X, v.Y, v.Width, v.Height))).Select(v => v.Id).ToList());
                return;
            }
            if (_dragging)
            {
                _pressedOn.ReleaseMouseCapture();
                _dragging = false;
                _pressed = false;
                EndGesture("Move visual");
                e.Handled = true;  // a drag must not count as a click on a chart mark
                return;
            }
            _pressed = false;
        }

        /// <summary>Remembers the selected bounds, for one undo step covering the whole gesture.</summary>
        public void BeginGesture()
        {
            var ids = _selected.ToList();
            _gestureBefore = ids.Select(id =>
            {
                var v = Session.Doc.Find(PageId, id);
                return new KeyValuePair<string, SetBoundsCommand.Bounds>(id, new SetBoundsCommand.Bounds { X = v.X, Y = v.Y, W = v.Width, H = v.Height });
            }).ToList();
        }

        /// <summary>Records the gesture as an undo step.</summary>
        public void EndGesture(string label)
        {
            ClearGuides();
            if (_gestureBefore == null) return;
            Session.RecordBounds(label, _gestureBefore, PageId);
            _gestureBefore = null;
        }

        /// <summary>Rounds to the grid, unless Alt is down.</summary>
        private double Snap(double v) => (Keyboard.Modifiers & ModifierKeys.Alt) != 0 ? v : Math.Round(v / GridStep) * GridStep;

        /// <summary>Moves the selection live, writing straight to the definitions. Offsets come from the lead visual so that a group keeps its shape, and stay within the page.</summary>
        private void MoveSelection(Vector delta)
        {
            var page = Page;
            var moving = _gestureBefore.ToDictionary(k => k.Key, k => k.Value);
            var lead = moving.ContainsKey(_pressedOn.VisualId) ? moving[_pressedOn.VisualId] : moving.Values.First();
            double nx = Snap(lead.X + delta.X), ny = Snap(lead.Y + delta.Y);

            // Smart guides: compare the lead's edges and centre with the visuals standing still.
            ClearGuides();
            var others = page.Visuals.Where(v => !moving.ContainsKey(v.Id) && !v.IsHidden).ToList();
            double gx = double.NaN, gy = double.NaN;
            foreach (var o in others)
            {
                foreach (var ox in new[] { o.X, o.X + o.Width / 2, o.X + o.Width })
                foreach (var mx in new[] { 0, lead.W / 2, lead.W })
                    if (Math.Abs(nx + mx - ox) <= 4) { nx = ox - mx; gx = ox; }
                foreach (var oy in new[] { o.Y, o.Y + o.Height / 2, o.Y + o.Height })
                foreach (var my in new[] { 0, lead.H / 2, lead.H })
                    if (Math.Abs(ny + my - oy) <= 4) { ny = oy - my; gy = oy; }
            }
            if (!double.IsNaN(gx)) AddGuide(gx, 0, gx, page.Height);
            if (!double.IsNaN(gy)) AddGuide(0, gy, page.Width, gy);

            double dx = nx - lead.X, dy = ny - lead.Y;
            foreach (var kv in moving)
            {
                var v = Session.Doc.Find(PageId, kv.Key);
                v.X = Math.Max(0, Math.Min(page.Width - v.Width, kv.Value.X + dx));
                v.Y = Math.Max(0, Math.Min(page.Height - v.Height, kv.Value.Y + dy));
                Place(Containers[kv.Key], v);
            }
        }

        /// <summary>Draws a dashed alignment guide.</summary>
        private void AddGuide(double x1, double y1, double x2, double y2)
        {
            _overlay.Children.Add(new Line
            {
                X1 = x1, Y1 = y1, X2 = x2, Y2 = y2,
                Stroke = Brushes.DeepPink,
                StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 4, 3 },
                Tag = "guide"
            });
        }

        /// <summary>Removes the guides.</summary>
        private void ClearGuides()
        {
            foreach (var l in _overlay.Children.OfType<Line>().Where(x => (x.Tag as string) == "guide").ToList()) _overlay.Children.Remove(l);
        }

        /// <summary>Resizes live by moving the edges named in <paramref name="dir"/>. Minimum size is 48.</summary>
        public void ResizeBy(VisualContainer c, string dir, double dx, double dy)
        {
            var v = c.Def;
            double left = v.X, top = v.Y, right = v.X + v.Width, bottom = v.Y + v.Height;
            if (dir.Contains("w")) left = Snap(left + dx);
            if (dir.Contains("e")) right = Snap(right + dx);
            if (dir.Contains("n")) top = Snap(top + dy);
            if (dir.Contains("s")) bottom = Snap(bottom + dy);
            const double min = 48;
            if (right - left < min) { if (dir.Contains("w")) left = right - min; else right = left + min; }
            if (bottom - top < min) { if (dir.Contains("n")) top = bottom - min; else bottom = top + min; }
            v.X = Math.Max(0, left); v.Y = Math.Max(0, top);
            v.Width = right - v.X; v.Height = bottom - v.Y;
            Place(c, v);
        }

        /// <summary>Moves the selection by arrow key. All visuals of one press share a gesture id, so they undo together.</summary>
        public void Nudge(double dx, double dy)
        {
            if (_selected.Count == 0) return;
            string gesture = "nudge" + _nudgeGesture;
            foreach (var id in _selected)
            {
                var v = Session.Doc.Find(PageId, id);
                var from = new SetBoundsCommand.Bounds { X = v.X, Y = v.Y, W = v.Width, H = v.Height };
                var to = from;
                to.X = Math.Max(0, from.X + dx);
                to.Y = Math.Max(0, from.Y + dy);
                Session.Stack.Execute(new SetBoundsCommand("Nudge visual", gesture, PageId, id, from, to));
            }
            _nudgeGesture++;
        }

        // ---------------------------------------------------------- arrange

        /// <summary>Deletes visuals.</summary>
        public void Remove(IEnumerable<string> ids)
        {
            var list = ids.ToList();
            Session.Edit("Delete visual", () => Page.Visuals.RemoveAll(v => list.Contains(v.Id)));
        }

        /// <summary>Moves a visual to the top or bottom of the stack.</summary>
        public void Reorder(VisualContainer c, bool front)
        {
            Session.Edit(front ? "Bring to front" : "Send to back", () =>
            {
                var v = c.Def;
                var z = Page.Visuals.Select(x => x.ZIndex).ToList();
                v.ZIndex = front ? z.Max() + 1 : z.Min() - 1;
            });
        }

        /// <summary>Aligns or distributes the selection. Needs two or more.</summary>
        public void Align(string how)
        {
            var items = _selected.Select(id => Session.Doc.Find(PageId, id)).ToList();
            if (items.Count < 2) return;
            Session.Edit("Align visuals", () =>
            {
                switch (how)
                {
                    case "left": { double x = items.Min(v => v.X); items.ForEach(v => v.X = x); break; }
                    case "right": { double x = items.Max(v => v.X + v.Width); items.ForEach(v => v.X = x - v.Width); break; }
                    case "top": { double y = items.Min(v => v.Y); items.ForEach(v => v.Y = y); break; }
                    case "bottom": { double y = items.Max(v => v.Y + v.Height); items.ForEach(v => v.Y = y - v.Height); break; }
                    case "hcenter": { double x = items.Average(v => v.X + v.Width / 2); items.ForEach(v => v.X = x - v.Width / 2); break; }
                    case "vcenter": { double y = items.Average(v => v.Y + v.Height / 2); items.ForEach(v => v.Y = y - v.Height / 2); break; }
                    case "hdist": Distribute(items, v => v.X, v => v.Width, (v, x) => v.X = x); break;
                    case "vdist": Distribute(items, v => v.Y, v => v.Height, (v, y) => v.Y = y); break;
                }
            });
        }

        /// <summary>Spaces items evenly between the outermost two.</summary>
        private static void Distribute(List<VisualDef> items, Func<VisualDef, double> pos, Func<VisualDef, double> size, Action<VisualDef, double> set)
        {
            var sorted = items.OrderBy(pos).ToList();
            double start = pos(sorted[0]), end = pos(sorted.Last()) + size(sorted.Last());
            double gap = (end - start - sorted.Sum(size)) / (sorted.Count - 1);
            double at = start;
            foreach (var v in sorted) { set(v, at); at += size(v) + gap; }
        }

        private List<VisualDef> _clipboard = new List<VisualDef>();

        /// <summary>Copies the selection to an in-process clipboard, not the Windows one.</summary>
        public void Copy() => _clipboard = _selected.Select(id => ModelJson.DeepClone(Session.Doc.Find(PageId, id))).ToList();

        /// <summary>Pastes copies offset by 16, and copies them again, so a repeated paste cascades.</summary>
        public void Paste()
        {
            if (_clipboard.Count == 0) return;
            var ids = new List<string>();
            Session.Edit("Paste visual", () =>
            {
                int z = Page.Visuals.Count == 0 ? 0 : Page.Visuals.Max(v => v.ZIndex) + 1;
                foreach (var src in _clipboard)
                {
                    var v = ModelJson.DeepClone(src);
                    v.Id = Guid.NewGuid().ToString("N");
                    v.X += 16; v.Y += 16; v.ZIndex = z++;
                    Page.Visuals.Add(v);
                    ids.Add(v.Id);
                }
            });
            _clipboard = ids.Select(id => ModelJson.DeepClone(Session.Doc.Find(PageId, id))).ToList();
            SetSelection(ids);
        }

        /// <summary>Copy and paste in one step.</summary>
        public void Duplicate() { Copy(); Paste(); }

        // ---------------------------------------------------------- adding visuals

        /// <summary>Adds a visual at a position, or at the first free spot, and selects it.</summary>
        public VisualDef AddVisual(string type, double x = double.NaN, double y = double.NaN, Action<VisualDef> init = null)
        {
            var page = Page;
            var v = new VisualDef { Type = type, Width = 400, Height = 280 };
            if (double.IsNaN(x))
            {
                var spot = FreeSpot(v.Width, v.Height);
                x = spot.X; y = spot.Y;
            }
            v.X = x; v.Y = y;
            v.ZIndex = page.Visuals.Count == 0 ? 0 : page.Visuals.Max(q => q.ZIndex) + 1;
            init?.Invoke(v);
            Session.Edit("New visual", () => page.Visuals.Add(v));
            SelectOnly(v.Id);
            return v;
        }

        /// <summary>Scans the page for a gap on a 24 unit grid.</summary>
        private Point FreeSpot(double w, double h)
        {
            var page = Page;
            for (double y = 16; y + h <= page.Height; y += 24)
                for (double x = 16; x + w <= page.Width; x += 24)
                    if (!page.Visuals.Any(v => new Rect(v.X, v.Y, v.Width, v.Height).IntersectsWith(new Rect(x, y, w, h))))
                        return new Point(x, y);
            return new Point(24, 24);
        }

        /// <summary>Handles a field dragged from the Fields pane to empty page.</summary>
        private void OnFieldDropped(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(typeof(FieldRef))) return;
            var field = (FieldRef)e.Data.GetData(typeof(FieldRef));
            var p = e.GetPosition(PageSurface);
            AddFieldVisual(field, Snap(p.X), Snap(p.Y));
        }

        /// <summary>Column makes a table, measure makes a card, as Power BI does.</summary>
        public void AddFieldVisual(FieldRef field, double x = double.NaN, double y = double.NaN)
        {
            bool measure = field.Kind == FieldKind.Measure;
            AddVisual(measure ? "card" : "table", x, y, v => v.Roles["values"] = new List<FieldRef> { field });
        }

        // ---------------------------------------------------------- interactions

        /// <summary>Stores an interaction override.</summary>
        public void SetInteraction(VisualContainer source, VisualContainer target, InteractionMode mode)
        {
            Session.Edit("Edit interaction", () =>
            {
                var list = Page.Interactions;
                list.RemoveAll(i => i.SourceVisualId == source.VisualId && i.TargetVisualId == target.VisualId);
                list.Add(new InteractionOverride { SourceVisualId = source.VisualId, TargetVisualId = target.VisualId, Mode = mode });
            });
        }
    }
}
