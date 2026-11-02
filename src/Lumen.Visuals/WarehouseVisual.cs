using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using Lumen.Charting;
using Lumen.Model;

namespace Lumen.Visuals
{
    /// <summary>Where the camera is, as an orbit around a target: compass heading, height angle and distance, plus the horizontal field of view.
    /// Orbit values interpolate into a sweeping flight, which straight lines between camera positions would not.</summary>
    public struct WarehouseCamera
    {
        /// <summary>The point looked at.</summary>
        public Point3D Target;
        /// <summary>Heading in radians. Zero looks north from the south.</summary>
        public double Yaw;
        /// <summary>Elevation in radians. Half pi looks straight down.</summary>
        public double Pitch;
        /// <summary>Distance from the target, in meters.</summary>
        public double Distance;
        /// <summary>Horizontal field of view, in degrees.</summary>
        public double Fov;

        /// <summary>The unit vector from the camera to the target.</summary>
        public Vector3D Forward => new Vector3D(-Math.Cos(Pitch) * Math.Sin(Yaw), -Math.Sin(Pitch), -Math.Cos(Pitch) * Math.Cos(Yaw));
        /// <summary>The unit up vector, also well defined when looking straight down.</summary>
        public Vector3D Up => new Vector3D(-Math.Sin(Pitch) * Math.Sin(Yaw), Math.Cos(Pitch), -Math.Sin(Pitch) * Math.Cos(Yaw));
        /// <summary>The unit vector to the right of the view.</summary>
        public Vector3D Right => Vector3D.CrossProduct(Forward, Up);
        /// <summary>The camera's position.</summary>
        public Point3D Position => Target - Forward * Distance;

        /// <summary>The ray under a point of a viewport. WPF's field of view is horizontal, so the vertical extent follows from the aspect ratio.</summary>
        public void Ray(Point p, Size viewport, out Point3D origin, out Vector3D direction)
        {
            double aspect = viewport.Width / viewport.Height;
            double tan = Math.Tan(Fov * Math.PI / 360);
            double nx = 2 * p.X / viewport.Width - 1, ny = 1 - 2 * p.Y / viewport.Height;
            origin = Position;
            direction = Forward + Right * (nx * tan) + Up * (ny * tan / aspect);
        }

        /// <summary>A step along the flight from one camera to another. The heading takes the short way round and distance eases geometrically, so a long dive does not look like a jump.</summary>
        public static WarehouseCamera Lerp(WarehouseCamera a, WarehouseCamera b, double t)
        {
            double yaw = Math.IEEERemainder(b.Yaw - a.Yaw, 2 * Math.PI);
            return new WarehouseCamera
            {
                Target = a.Target + (b.Target - a.Target) * t,
                Yaw = a.Yaw + yaw * t,
                Pitch = a.Pitch + (b.Pitch - a.Pitch) * t,
                Distance = a.Distance * Math.Pow(b.Distance / a.Distance, t),
                Fov = a.Fov + (b.Fov - a.Fov) * t
            };
        }
    }

    /// <summary>A walkable 3D warehouse in the style of a custom Power BI visual: racks, cartons and heat tiles from one row per location.
    /// It has Overview, Plan and Aisle views with animated camera flights, and Fill rate, Stock age and Open slots coloring.
    /// A hover shows the location, a click pins a callout and cross-filters the page, and filters narrow what is drawn and refit the camera.
    /// The scene is a Viewport3D of frozen meshes, so orbiting never rebuilds geometry. Picking is a ray test in C#: WPF's own 3D hit test walks every triangle.</summary>
    public sealed class WarehouseVisual : ILumenVisual
    {
        private const int MaxLocations = 150000;
        private const double PlanFov = 14, OverviewFov = 40, AisleFov = 70;
        private const double EyeHeight = 3.4, AisleLookAhead = 12;
        private const double DragThreshold = 4;

        private IVisualHost _host;
        private VisualUpdate _u;
        private readonly Grid _root = new Grid { Background = Brushes.Transparent, ClipToBounds = true };
        private readonly Viewport3D _viewport = new Viewport3D { IsHitTestVisible = false, ClipToBounds = true };
        private readonly PerspectiveCamera _lens = new PerspectiveCamera();
        private readonly ModelVisual3D _lights = new ModelVisual3D(), _structure = new ModelVisual3D(), _heat = new ModelVisual3D(), _selected = new ModelVisual3D(), _labels = new ModelVisual3D();

        // Overlays
        private readonly TextBlock _message = new TextBlock { TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16) };
        private readonly StackPanel _leftBar = new StackPanel { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(6) };
        private readonly StackPanel _rightBar = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(6) };
        private readonly Border _hint = new Border { Padding = new Thickness(7, 4, 7, 4), Margin = new Thickness(0, 0, 0, 4) };
        private readonly TextBlock _hintText = new TextBlock { FontSize = 11, Text = "Drag to orbit. Scroll to zoom. Right-drag to pan. Click a location or floor tile for detail." };
        private readonly StackPanel _colorButtons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left };
        private readonly StackPanel _viewButtons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        private readonly ComboBox _aislePicker = new ComboBox { FontSize = 11, MinWidth = 90, Margin = new Thickness(0, 4, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
        private readonly Border _legend = new Border { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(6), Padding = new Thickness(7, 4, 7, 4) };
        private readonly WrapPanel _legendItems = new WrapPanel { MaxWidth = 620 };
        private readonly Border _callout = new Border { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(6), Padding = new Thickness(10, 8, 10, 8), Width = 250, BorderThickness = new Thickness(1), Visibility = Visibility.Collapsed };

        // Data and scene state
        private WarehouseLayout _layout;
        private WarehouseFields _fields;
        private WarehouseFormat _format;
        private WarehousePalette _palette;
        private QueryResult _builtFrom, _filteredBy;
        private string _builtFormat, _builtTheme;
        private WarehouseView _view;
        private WarehouseColor _color;
        private bool _started;
        private bool _updatingPicker;
        private string _aisle;
        private string _pinned;

        // Camera and flight
        private WarehouseCamera _cam, _flightFrom, _flightTo;
        private readonly Stopwatch _flightClock = new Stopwatch();
        private bool _flying;
        private double _flightMs;

        // Mouse
        private MouseButton? _button;
        private Point _pressAt, _lastAt;
        private bool _dragged;

        /// <inheritdoc/>
        public VisualCapabilities Capabilities => new VisualCapabilities
        {
            DisplayName = "Warehouse 3D",
            Roles =
            {
                new DataRole("location", "Location ID", RoleKind.Grouping, 1, 1),
                new DataRole("zone", "Zone", RoleKind.Grouping, 1, 1),
                new DataRole("aisle", "Aisle", RoleKind.Grouping, 1, 1),
                new DataRole("bayRow", "Bay row", RoleKind.Grouping, 0, 1),
                new DataRole("bay", "Bay", RoleKind.Grouping, 1, 1),
                new DataRole("level", "Level", RoleKind.Grouping, 1, 1),
                new DataRole("position", "Position", RoleKind.Grouping, 1, 1),
                new DataRole("status", "Status", RoleKind.Grouping, 1, 1),
                new DataRole("sku", "SKU", RoleKind.Grouping, 0, 1),
                new DataRole("units", "Units", RoleKind.Grouping, 0, 1),
                new DataRole("age", "Stock age (days)", RoleKind.Grouping, 0, 1)
            },
            FormatType = typeof(WarehouseFormat),
            IsInteractiveControl = true
        };

        /// <summary>The view on show. Exposed for the self-check.</summary>
        public WarehouseView View => _view;
        /// <summary>The color mode on show. Exposed for the self-check.</summary>
        public WarehouseColor ColorMode => _color;
        /// <summary>True while the camera is moving by itself.</summary>
        public bool Flying => _flying;
        /// <summary>The floor plan, or null before data arrives.</summary>
        public WarehouseLayout Layout => _layout;
        /// <summary>The id or bay row key of the pinned callout, or null.</summary>
        public string Pinned => _pinned;
        /// <summary>The element that takes the mouse.</summary>
        public FrameworkElement ViewElement => _root;
        /// <summary>The current camera.</summary>
        public WarehouseCamera Camera => _cam;
        /// <summary>The size of the scene on screen.</summary>
        public Size ViewSize => new Size(_root.ActualWidth, _root.ActualHeight);

        /// <inheritdoc/>
        public FrameworkElement CreateView(IVisualHost host)
        {
            _host = host;
            _viewport.Camera = _lens;
            _viewport.Children.Add(_lights);
            _viewport.Children.Add(_structure);
            _viewport.Children.Add(_heat);
            _viewport.Children.Add(_selected);
            _viewport.Children.Add(_labels);
            _lights.Content = WarehouseScene.Lights();

            _hint.Child = _hintText;
            _leftBar.Children.Add(_hint);
            _leftBar.Children.Add(_colorButtons);
            _rightBar.Children.Add(_viewButtons);
            _rightBar.Children.Add(_aislePicker);
            _legend.Child = _legendItems;

            _root.Children.Add(_viewport);
            _root.Children.Add(_legend);
            _root.Children.Add(_callout);
            _root.Children.Add(_leftBar);
            _root.Children.Add(_rightBar);
            _root.Children.Add(_message);

            _aislePicker.SelectionChanged += (s, e) =>
            {
                if (_updatingPicker || _aislePicker.SelectedItem == null) return;
                _aisle = (string)_aislePicker.SelectedItem;
                FlyToView();
            };
            // The overlays swallow presses, so a click on a button or the callout does not orbit or select through it.
            foreach (var overlay in new FrameworkElement[] { _hint, _legend, _callout, _aislePicker })
                overlay.MouseLeftButtonDown += (s, e) => e.Handled = true;

            _root.MouseLeftButtonDown += (s, e) => Press(MouseButton.Left, e);
            _root.MouseRightButtonDown += (s, e) => Press(MouseButton.Right, e);
            _root.MouseLeftButtonUp += (s, e) => Release(MouseButton.Left, e);
            _root.MouseRightButtonUp += (s, e) => Release(MouseButton.Right, e);
            _root.MouseMove += OnMove;
            _root.MouseWheel += OnWheel;
            _root.MouseLeave += (s, e) => _host.HideTooltip();
            _root.Unloaded += (s, e) => StopFlight();
            return _root;
        }

        /// <summary>Two queries over the same columns: what the filters keep, and every location. The second lays out the floor,
        /// so a filter on one level does not sink it to the ground, and it stays in the engine's cache while the filters change.</summary>
        public IReadOnlyList<QuerySpec> BuildQueries(VisualDef def, SemanticModel model)
        {
            if (!new WarehouseFields(def).Complete) return new QuerySpec[0];
            var filtered = new QuerySpec { Limit = MaxLocations };
            filtered.GroupBy.AddRange(WarehouseFields.Collect(def));
            var all = new QuerySpec { Limit = MaxLocations, Unfiltered = true };
            all.GroupBy.AddRange(WarehouseFields.Collect(def));
            return new[] { filtered, all };
        }

        /// <summary>Rebuilds only what changed. A resize or a selection touches no geometry, so most calls cost almost nothing.</summary>
        public void Update(VisualUpdate u)
        {
            _u = u;
            _fields = new WarehouseFields(u.Def);
            _format = (WarehouseFormat)u.Format;
            StyleOverlays();

            if (!_fields.Complete) { Message("Add Location ID, Zone, Aisle, Bay, Level, Position and Status to build this visual"); return; }
            if (u.All == null || u.All.Count < 2) return;
            var failed = u.All.FirstOrDefault(r => r.Error != null);
            if (failed != null) { Message(failed.Error); return; }
            if (u.Data.Truncated || u.All[1].Truncated) { Message("Too many locations: this visual draws up to " + MaxLocations.ToString("N0", CultureInfo.CurrentCulture)); return; }
            if (u.Size.Width < 20 || u.Size.Height < 20) return;
            Message(null);

            if (!_started)
            {
                _started = true;
                _view = _format.StartView;
                _color = _format.StartColor;
            }

            string format = u.Def.Format?.ToString(Newtonsoft.Json.Formatting.None) ?? "";
            bool rebuild = _layout == null || !ReferenceEquals(_builtFrom, u.All[1]) || format != _builtFormat;
            bool theme = u.Theme.Name != _builtTheme;
            bool refilter = rebuild || !ReferenceEquals(_filteredBy, u.Data);
            bool first = _layout == null;

            if (rebuild)
            {
                _layout = WarehouseLayout.Build(u.All[1], _fields, _format);
                _palette = new WarehousePalette(_format);
                _builtFrom = u.All[1];
                _builtFormat = format;
            }
            bool visibleChanged = false;
            if (refilter)
            {
                _filteredBy = u.Data;
                visibleChanged = _layout.ApplyFilter(u.Data, _fields);
            }
            if (rebuild || visibleChanged || theme)
            {
                _structure.Content = WarehouseScene.Structure(_layout, _format, u.Theme);
                _labels.Content = WarehouseScene.Labels(_layout, _format, u.Theme);
                _builtTheme = u.Theme.Name;
            }
            if (refilter) DrawHeat();

            SyncAisles();
            SyncButtons();
            SyncLegend();
            SyncSelection();

            if (first) { _cam = TargetCamera(); ApplyCamera(); }
            else if (visibleChanged) FlyToView();
            else if (!_flying) ApplyCamera();
        }

        /// <summary>Shows a note in place of the scene, or hides it for null.</summary>
        private void Message(string text)
        {
            _message.Text = text ?? "";
            var show = text == null ? Visibility.Visible : Visibility.Collapsed;
            _viewport.Visibility = show;
            _leftBar.Visibility = _rightBar.Visibility = text == null && _format.ShowToolbar ? Visibility.Visible : Visibility.Collapsed;
            _legend.Visibility = text == null && _format.ShowLegend ? Visibility.Visible : Visibility.Collapsed;
            if (text != null) _callout.Visibility = Visibility.Collapsed;
            _message.Visibility = text == null ? Visibility.Collapsed : Visibility.Visible;
        }

        /// <summary>Redraws the tiles and cartons for the current mode.</summary>
        private void DrawHeat() => _heat.Content = WarehouseScene.Heat(_layout, _palette, _color);

        // ---------------------------------------------------------- overlays

        /// <summary>A translucent panel color, so that the scene stays readable behind the overlays.</summary>
        private Brush PanelBrush()
        {
            var c = _u.Theme.VisualBackground;
            c.A = 0xE6;
            return Frozen.Brush(c);
        }

        /// <summary>Applies the theme to the fixed overlays.</summary>
        private void StyleOverlays()
        {
            var panel = PanelBrush();
            _hint.Background = panel;
            _hintText.Foreground = Frozen.Brush(_u.Theme.Secondary);
            _legend.Background = panel;
            _callout.Background = Frozen.Brush(_u.Theme.VisualBackground);
            _callout.BorderBrush = Frozen.Brush(_u.Theme.Border);
            _message.Foreground = Frozen.Brush(_u.Theme.Secondary);
        }

        /// <summary>One segment of a button group. Its look shows whether it is the current choice.</summary>
        private Border Segment(string text, bool active, Action click)
        {
            var theme = _u.Theme;
            var b = new Border
            {
                Padding = new Thickness(8, 3, 8, 3),
                Margin = new Thickness(0, 0, 1, 0),
                Cursor = Cursors.Hand,
                Background = active ? Frozen.Brush(theme.Accent) : PanelBrush(),
                Child = new TextBlock { Text = text, FontSize = 11, FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal, Foreground = active ? Brushes.White : Frozen.Brush(theme.Foreground) }
            };
            b.MouseLeftButtonDown += (s, e) => e.Handled = true;
            b.MouseLeftButtonUp += (s, e) => { e.Handled = true; click(); };
            return b;
        }

        /// <summary>Rebuilds the mode and view buttons.</summary>
        private void SyncButtons()
        {
            _colorButtons.Children.Clear();
            _colorButtons.Children.Add(Segment("Fill rate", _color == WarehouseColor.FillRate, () => SetColor(WarehouseColor.FillRate)));
            _colorButtons.Children.Add(Segment("Stock age", _color == WarehouseColor.StockAge, () => SetColor(WarehouseColor.StockAge)));
            _colorButtons.Children.Add(Segment("Open slots", _color == WarehouseColor.OpenSlots, () => SetColor(WarehouseColor.OpenSlots)));
            _viewButtons.Children.Clear();
            _viewButtons.Children.Add(Segment("Overview", _view == WarehouseView.Overview, () => SetView(WarehouseView.Overview)));
            _viewButtons.Children.Add(Segment("Plan view", _view == WarehouseView.Plan, () => SetView(WarehouseView.Plan)));
            _viewButtons.Children.Add(Segment("Aisle view", _view == WarehouseView.Aisle, () => SetView(WarehouseView.Aisle)));
            _aislePicker.Visibility = _view == WarehouseView.Aisle ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>Lists the visible aisles and keeps the current one, or moves to the first if the filters removed it.</summary>
        private void SyncAisles()
        {
            var names = _layout.Aisles.Where(a => _layout.BayRows.Any(r => r.Aisle == a.Name && r.Total > 0)).Select(a => a.Name).ToList();
            if (_aisle == null || !names.Contains(_aisle)) _aisle = names.FirstOrDefault();
            _updatingPicker = true;
            if (!_aislePicker.Items.Cast<string>().SequenceEqual(names))
            {
                _aislePicker.Items.Clear();
                foreach (var n in names) _aislePicker.Items.Add(n);
            }
            _aislePicker.SelectedItem = _aisle;
            _updatingPicker = false;
        }

        /// <summary>Rebuilds the legend for the current mode.</summary>
        private void SyncLegend()
        {
            _legendItems.Children.Clear();
            _legendItems.Children.Add(new TextBlock { Text = WarehousePalette.Caption(_color), FontSize = 10.5, Margin = new Thickness(0, 0, 8, 0), Foreground = Frozen.Brush(_u.Theme.Secondary) });
            foreach (var s in _palette.Swatches(_color))
            {
                var item = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 8, 0) };
                item.Children.Add(new Border { Width = 10, Height = 10, Margin = new Thickness(0, 0, 3, 0), Background = Frozen.Brush(s.Color), VerticalAlignment = VerticalAlignment.Center });
                item.Children.Add(new TextBlock { Text = s.Text, FontSize = 10.5, Foreground = Frozen.Brush(_u.Theme.Secondary) });
                _legendItems.Children.Add(item);
            }
        }

        /// <summary>Follows the page's selection: highlights what is selected, and closes the callout once nothing is.</summary>
        private void SyncSelection()
        {
            var boxes = new List<Rect3D>();
            foreach (var key in _u.Selection)
            {
                var l = _layout.Find(key);
                if (l != null) { boxes.Add(l.Slot); continue; }
                var row = _layout.FindRow(key);
                if (row != null) boxes.Add(row.Tile);
            }
            _selected.Content = boxes.Count == 0 ? null : WarehouseScene.Highlight(boxes, _u.Theme.Accent);

            if (_pinned != null && !_u.Selection.Contains(_pinned)) _pinned = _u.Selection.FirstOrDefault();
            if (_pinned == null || !_format.ShowCallout) { _callout.Visibility = Visibility.Collapsed; return; }
            BuildCallout();
            _callout.Visibility = Visibility.Visible;
        }

        /// <summary>Fills the callout for the pinned location or bay row: identity, the row's totals, and its fill by level.</summary>
        private void BuildCallout()
        {
            var theme = _u.Theme;
            var location = _layout.Find(_pinned);
            var row = location?.Row ?? _layout.FindRow(_pinned);
            if (row == null) return;

            var stack = new StackPanel();
            var head = new DockPanel();
            var close = new TextBlock { Text = "✕", FontSize = 11, Cursor = Cursors.Hand, Foreground = Frozen.Brush(theme.Secondary), Margin = new Thickness(8, 0, 0, 0) };
            close.MouseLeftButtonUp += (s, e) => { e.Handled = true; _pinned = null; _host.ClearSelection(); _callout.Visibility = Visibility.Collapsed; };
            DockPanel.SetDock(close, Dock.Right);
            head.Children.Add(close);
            head.Children.Add(new TextBlock { Text = location?.Id ?? (row.Aisle + ", bay row " + row.Number), FontWeight = FontWeights.SemiBold, FontSize = 12.5, Foreground = Frozen.Brush(theme.Foreground) });
            stack.Children.Add(head);

            Action<string> line = t => stack.Children.Add(new TextBlock { Text = t, FontSize = 10.5, TextWrapping = TextWrapping.Wrap, Foreground = Frozen.Brush(theme.Secondary) });
            if (location != null)
            {
                line(location.Filled ? "Filled: " + Describe(location) : "Open slot");
                line("Bay " + location.Bay + ", level " + location.Level + ", position " + location.Position);
            }
            stack.Children.Add(new TextBlock
            {
                Text = row.Fill.ToString("0%", CultureInfo.CurrentCulture) + " filled",
                FontSize = 15, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 4, 0, 0),
                Foreground = Frozen.Brush(theme.Foreground)
            });
            line(row.Filled + " of " + row.Total + " locations, " + row.Open + " open");
            if (row.AgeCount > 0) line("Average stock age " + row.AverageAge.ToString("0", CultureInfo.CurrentCulture) + " days");
            stack.Children.Add(new TextBlock { Text = "Fill rate by level", FontSize = 10.5, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 5, 0, 2), Foreground = Frozen.Brush(theme.Foreground) });

            foreach (var level in _layout.Levels.AsEnumerable().Reverse())
            {
                var at = row.Locations.Where(l => l.Level == level && l.Visible).ToList();
                if (at.Count == 0) continue;
                int filled = at.Count(l => l.Filled);
                double share = (double)filled / at.Count;
                var bar = new Grid { Height = 12 };
                bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
                bar.ColumnDefinitions.Add(new ColumnDefinition());
                bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
                bar.Children.Add(new TextBlock { Text = "L" + level, FontSize = 9.5, Foreground = Frozen.Brush(theme.Secondary) });
                var track = new Grid { Margin = new Thickness(0, 2, 4, 2), Background = Frozen.Brush(theme.Grid) };
                track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(share, 0.0001), GridUnitType.Star) });
                track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(1 - share, 0.0001), GridUnitType.Star) });
                track.Children.Add(new Border { Background = Frozen.Brush(_palette.FillColor(share)) });
                Grid.SetColumn(track, 1);
                bar.Children.Add(track);
                var count = new TextBlock { Text = filled + "/" + at.Count, FontSize = 9.5, TextAlignment = TextAlignment.Right, Foreground = Frozen.Brush(theme.Secondary) };
                Grid.SetColumn(count, 2);
                bar.Children.Add(count);
                stack.Children.Add(bar);
            }
            _callout.Child = stack;
        }

        /// <summary>The contents of a filled location in words.</summary>
        private static string Describe(WarehouseLocation l)
        {
            var parts = new List<string>();
            if (l.Sku != null) parts.Add(l.Sku);
            if (!double.IsNaN(l.Units)) parts.Add(l.Units.ToString("0", CultureInfo.CurrentCulture) + " units");
            if (!double.IsNaN(l.Age)) parts.Add(l.Age.ToString("0", CultureInfo.CurrentCulture) + " days old");
            return parts.Count == 0 ? "in stock" : string.Join(", ", parts);
        }

        // ---------------------------------------------------------- views

        /// <summary>Flies to a view. Entering the Aisle view goes to the aisle of the selection, when there is one. The self-check calls this too.</summary>
        public void SetView(WarehouseView view)
        {
            _view = view;
            if (view == WarehouseView.Aisle)
            {
                var row = _layout?.Find(_pinned)?.Row ?? _layout?.FindRow(_pinned);
                if (row != null) { _aisle = row.Aisle; SyncAisles(); }
            }
            SyncButtons();
            FlyToView();
        }

        /// <summary>Recolors the scene for a mode. The self-check calls this too.</summary>
        public void SetColor(WarehouseColor color)
        {
            _color = color;
            DrawHeat();
            SyncButtons();
            SyncLegend();
        }

        /// <summary>Starts a flight to the current view's camera.</summary>
        private void FlyToView()
        {
            if (_layout == null || _layout.Locations.Count == 0) return;
            _flightFrom = _cam;
            _flightTo = TargetCamera();
            _flightMs = _format.FlightMs;
            if (_flightMs <= 0 || !_root.IsLoaded)
            {
                StopFlight();
                _cam = _flightTo;
                ApplyCamera();
                return;
            }
            _flightClock.Restart();
            if (_flying) return;
            _flying = true;
            CompositionTarget.Rendering += OnFrame;
        }

        /// <summary>Ends a flight where it is.</summary>
        private void StopFlight()
        {
            if (!_flying) return;
            _flying = false;
            CompositionTarget.Rendering -= OnFrame;
        }

        /// <summary>Advances a flight, easing in and out.</summary>
        private void OnFrame(object sender, EventArgs e)
        {
            double t = Math.Min(1, _flightClock.Elapsed.TotalMilliseconds / _flightMs);
            double eased = t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;
            _cam = WarehouseCamera.Lerp(_flightFrom, _flightTo, eased);
            ApplyCamera();
            if (t >= 1) StopFlight();
        }

        /// <summary>The camera of the current view, for what is visible.</summary>
        private WarehouseCamera TargetCamera()
        {
            var b = _layout.VisibleBounds;
            var center = new Point3D(b.X + b.SizeX / 2, b.Y + b.SizeY / 2, b.Z + b.SizeZ / 2);
            double aspect = AspectRatio();
            switch (_view)
            {
                case WarehouseView.Plan:
                {
                    double h = Math.Tan(PlanFov * Math.PI / 360), v = h / aspect;
                    double fit = Math.Max(b.SizeX / 2 / h, b.SizeZ / 2 / v) * 1.14;
                    return new WarehouseCamera { Target = new Point3D(center.X, 0, center.Z), Yaw = 0, Pitch = Math.PI / 2, Distance = fit + b.SizeY, Fov = PlanFov };
                }
                case WarehouseView.Aisle:
                {
                    var a = _layout.Aisles.FirstOrDefault(x => x.Name == _aisle);
                    if (a == null) goto default;
                    // Standing at the south end, eyes above the first beam, tilted slightly down so the tiles show.
                    double pitch = 4 * Math.PI / 180;
                    var eye = new Point3D(a.CenterX, EyeHeight, a.Bounds.Z + a.Bounds.SizeZ + 5);
                    var cam = new WarehouseCamera { Yaw = 0, Pitch = pitch, Distance = AisleLookAhead, Fov = AisleFov };
                    cam.Target = eye + cam.Forward * AisleLookAhead;
                    return cam;
                }
                default:
                {
                    double h = OverviewFov * Math.PI / 360, v = Math.Atan(Math.Tan(h) / aspect);
                    double radius = new Vector3D(b.SizeX, b.SizeY, b.SizeZ).Length / 2;
                    return new WarehouseCamera
                    {
                        Target = center,
                        Yaw = -18 * Math.PI / 180,
                        Pitch = 38 * Math.PI / 180,
                        Distance = radius / Math.Sin(Math.Min(h, v)) * 0.72,
                        Fov = OverviewFov
                    };
                }
            }
        }

        /// <summary>The viewport's aspect ratio, with a sensible value before layout has run.</summary>
        private double AspectRatio() => _u != null && _u.Size.Height > 1 ? _u.Size.Width / _u.Size.Height : 1.6;

        /// <summary>Pushes the orbit state into the WPF camera. The near plane follows the distance:
        /// a plan view looks from a kilometer away, where a fixed near plane would leave too little depth precision to keep the tiles off the floor.</summary>
        private void ApplyCamera()
        {
            _lens.Position = _cam.Position;
            _lens.LookDirection = _cam.Forward;
            _lens.UpDirection = _cam.Up;
            _lens.FieldOfView = _cam.Fov;
            _lens.NearPlaneDistance = Math.Max(0.2, _cam.Distance * 0.04);
            _lens.FarPlaneDistance = double.PositiveInfinity;
        }

        // ---------------------------------------------------------- mouse

        /// <summary>Starts a drag or click.</summary>
        private void Press(MouseButton button, MouseButtonEventArgs e)
        {
            if (_layout == null) return;
            _button = button;
            _pressAt = _lastAt = e.GetPosition(_root);
            _dragged = false;
            _root.CaptureMouse();
            _host.HideTooltip();
            // Left presses are the visual's. A right press stays unhandled so a plain right-click still opens the menu.
            if (button == MouseButton.Left) e.Handled = true;
        }

        /// <summary>Ends a drag, or treats the press as a click. A right-drag eats the release, which would otherwise open the context menu.</summary>
        private void Release(MouseButton button, MouseButtonEventArgs e)
        {
            if (_button != button) return;
            _button = null;
            _root.ReleaseMouseCapture();
            if (_dragged) { e.Handled = true; return; }
            if (button == MouseButton.Left) Click(e.GetPosition(_root));
        }

        /// <summary>Selects what is under the mouse, or clears on empty floor. Ctrl adds to the selection.</summary>
        private void Click(Point p)
        {
            WarehouseLocation l;
            WarehouseBayRow tile;
            PickAt(p, out l, out tile);
            bool multi = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            if (l != null) SelectLocation(l, multi);
            else if (tile != null) SelectRow(tile, multi);
            else { _pinned = null; _host.ClearSelection(); }
        }

        /// <summary>Selects one location: pins its callout and filters the page to it. The self-check calls this too.</summary>
        public void SelectLocation(WarehouseLocation l, bool multi)
        {
            var point = new DataPoint { Key = l.Id };
            point.Fields.Add(new KeyValuePair<FieldRef, object>(_u.Def.Role("location")[0], LocationValue(l)));
            _pinned = l.Id;
            _host.Select(point, multi);
        }

        /// <summary>The location id as the column stores it. The id is a label, but a numeric column must get a number back or the filter would match nothing.</summary>
        private object LocationValue(WarehouseLocation l)
        {
            var r = _builtFrom;
            int c = _fields["location"];
            for (int i = 0; i < r.RowCount; i++)
                if (r.Data[c][i].ToDisplayString() == l.Id) return r.Data[c][i].ToObject();
            return l.Id;
        }

        /// <summary>Selects a floor tile: the bay row's locations filter the page by aisle and bay.</summary>
        private void SelectRow(WarehouseBayRow row, bool multi)
        {
            var point = new DataPoint { Key = row.Key };
            var def = _u.Def;
            point.Fields.Add(new KeyValuePair<FieldRef, object>(def.Role("aisle")[0], row.Aisle));
            if (def.Role("bayRow").Count > 0) point.Fields.Add(new KeyValuePair<FieldRef, object>(def.Role("bayRow")[0], (long)row.Number));
            else foreach (var bay in row.Locations.Select(l => l.Bay).Distinct()) point.Fields.Add(new KeyValuePair<FieldRef, object>(def.Role("bay")[0], (long)bay));
            _pinned = row.Key;
            _host.Select(point, multi);
        }

        /// <summary>Finds what is under a point of the view.</summary>
        public void PickAt(Point p, out WarehouseLocation location, out WarehouseBayRow tile)
        {
            location = null;
            tile = null;
            var size = new Size(_root.ActualWidth, _root.ActualHeight);
            if (_layout == null || size.Width < 1 || size.Height < 1) return;
            Point3D origin;
            Vector3D direction;
            _cam.Ray(p, size, out origin, out direction);
            _layout.Pick(origin, direction, out location, out tile);
        }

        /// <summary>Orbits on a left drag, pans on a right drag, and otherwise shows what is under the mouse.</summary>
        private void OnMove(object sender, MouseEventArgs e)
        {
            var p = e.GetPosition(_root);
            if (_button == null)
            {
                // The overlays sit inside the root, so their moves bubble here. Nothing behind a panel is being pointed at.
                if (ReferenceEquals(e.OriginalSource, _root)) Hover(p);
                else _host.HideTooltip();
                return;
            }
            if (!_dragged && (p - _pressAt).Length < DragThreshold) return;
            _dragged = true;
            StopFlight();
            double dx = p.X - _lastAt.X, dy = p.Y - _lastAt.Y;
            _lastAt = p;
            if (_button == MouseButton.Left)
            {
                _cam.Yaw -= dx * 0.008;
                _cam.Pitch = Math.Max(0.04, Math.Min(Math.PI / 2, _cam.Pitch + dy * 0.006));
            }
            else
            {
                // One pixel of drag moves the world by one pixel at the target's distance.
                double perPixel = 2 * _cam.Distance * Math.Tan(_cam.Fov * Math.PI / 360) / Math.Max(1, _root.ActualWidth);
                _cam.Target += _cam.Right * (-dx * perPixel) + _cam.Up * (dy * perPixel);
                if (_cam.Target.Y < 0) _cam.Target = new Point3D(_cam.Target.X, 0, _cam.Target.Z);
            }
            ApplyCamera();
        }

        /// <summary>Shows the tooltip of what is under the mouse.</summary>
        private void Hover(Point p)
        {
            WarehouseLocation l;
            WarehouseBayRow tile;
            PickAt(p, out l, out tile);
            if (l != null) _host.ShowTooltip(LocationTip(l));
            else if (tile != null) _host.ShowTooltip(RowTip(tile));
            else _host.HideTooltip();
        }

        /// <summary>The tooltip of a location.</summary>
        private static List<TooltipItem> LocationTip(WarehouseLocation l)
        {
            var items = new List<TooltipItem>
            {
                new TooltipItem("Location", l.Id),
                new TooltipItem("Where", l.Zone + ", aisle " + l.Aisle + ", bay " + l.Bay),
                new TooltipItem("Level, position", l.Level + ", " + l.Position),
                new TooltipItem("Status", l.Filled ? "Filled" : "Open")
            };
            if (l.Filled)
            {
                if (l.Sku != null) items.Add(new TooltipItem("SKU", l.Sku));
                if (!double.IsNaN(l.Units)) items.Add(new TooltipItem("Units", l.Units.ToString("0", CultureInfo.CurrentCulture)));
                if (!double.IsNaN(l.Age)) items.Add(new TooltipItem("Stock age", l.Age.ToString("0", CultureInfo.CurrentCulture) + " days"));
            }
            items.Add(new TooltipItem("Bay row fill", l.Row.Fill.ToString("0%", CultureInfo.CurrentCulture) + " (" + l.Row.Filled + " of " + l.Row.Total + ")"));
            return items;
        }

        /// <summary>The tooltip of a floor tile.</summary>
        private static List<TooltipItem> RowTip(WarehouseBayRow r)
        {
            var items = new List<TooltipItem>
            {
                new TooltipItem("Bay row", r.Aisle + ", row " + r.Number),
                new TooltipItem("Fill rate", r.Fill.ToString("0%", CultureInfo.CurrentCulture) + " (" + r.Filled + " of " + r.Total + ")"),
                new TooltipItem("Open slots", r.Open.ToString(CultureInfo.CurrentCulture))
            };
            if (r.AgeCount > 0) items.Add(new TooltipItem("Average stock age", r.AverageAge.ToString("0", CultureInfo.CurrentCulture) + " days"));
            return items;
        }

        /// <summary>Zooms. In the Aisle view the wheel walks along the view instead, since a zoom around a point ahead would swing past the racks.</summary>
        private void OnWheel(object sender, MouseWheelEventArgs e)
        {
            if (_layout == null) return;
            e.Handled = true;
            StopFlight();
            double notches = e.Delta / 120.0;
            if (_view == WarehouseView.Aisle)
                _cam.Target += _cam.Forward * (notches * 3);
            else
            {
                double fit = _layout.Bounds.SizeX + _layout.Bounds.SizeZ + 1;
                _cam.Distance = Math.Max(4, Math.Min(fit * 12, _cam.Distance * Math.Pow(0.87, notches)));
            }
            ApplyCamera();
        }

        /// <summary>Stops the animation, so a removed visual is not kept alive by the rendering event.</summary>
        public void Dispose() => StopFlight();
    }
}
