using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Lumen.Charting;
using Lumen.Model;

namespace Lumen.Visuals
{
    /// <summary>A visual whose output is a FilterDef. Its items are queried through the engine, so slicers cascade.</summary>
    public sealed class SlicerVisual : ILumenVisual
    {
        private IVisualHost _host;
        private readonly Grid _root = new Grid();
        private VisualUpdate _u;
        private SlicerStyle? _builtStyle;
        private string _builtField;
        private bool _updating;

        // List and dropdown
        private StackPanel _items;
        private TextBlock _summary;
        private readonly HashSet<string> _checked = new HashSet<string>();
        private List<KeyValuePair<string, object>> _options = new List<KeyValuePair<string, object>>();
        private Dictionary<string, CheckBox> _boxes = new Dictionary<string, CheckBox>();

        // Between
        private Slider _low, _high;
        private TextBox _lowText, _highText;
        private bool _isDate;

        // Relative date
        private TextBox _count;
        private ComboBox _unit;

        /// <inheritdoc/>
        public VisualCapabilities Capabilities => new VisualCapabilities
        {
            DisplayName = "Slicer",
            Roles = { new DataRole("field", "Field", RoleKind.Grouping, 1, 1) },
            FormatType = typeof(SlicerFormat),
            IsInteractiveControl = true,
            IsSlicer = true
        };

        /// <inheritdoc/>
        public FrameworkElement CreateView(IVisualHost host)
        {
            _host = host;
            return _root;
        }

        /// <summary>The distinct values of the field, up to 1,000. With no measures, the driver is the smallest table that reaches the field.</summary>
        public IReadOnlyList<QuerySpec> BuildQueries(VisualDef def, SemanticModel model)
        {
            var f = def.Role("field");
            if (f.Count == 0) return new QuerySpec[0];
            return new[] { new QuerySpec { GroupBy = { f[0] }, Limit = 1000 } };
        }

        /// <summary>Rebuilds the controls only when the style or field changed, so that a refresh does not lose focus or scroll position.</summary>
        public void Update(VisualUpdate u)
        {
            _u = u;
            var fmt = (SlicerFormat)u.Format;
            var field = u.Def.Role("field").FirstOrDefault();
            if (field == null) { Show("Add a field to build this slicer"); _builtStyle = null; return; }
            if (u.Data == null) return;
            if (u.Data.Error != null) { Show(u.Data.Error); _builtStyle = null; return; }

            var style = fmt.Style;
            if (_builtStyle != style || _builtField != field.Key)
            {
                _root.Children.Clear();
                _builtStyle = style;
                _builtField = field.Key;
                switch (style)
                {
                    case SlicerStyle.Between: BuildBetween(); break;
                    case SlicerStyle.RelativeDate: BuildRelative(); break;
                    case SlicerStyle.Dropdown: BuildDropdown(); break;
                    default: BuildList(); break;
                }
                RestoreState();
            }

            var fg = Frozen.Brush(u.Theme.Foreground);
            System.Windows.Documents.TextElement.SetForeground(_root, fg);
            // Guard: setting controls here must not publish a filter of its own.
            _updating = true;
            switch (style)
            {
                case SlicerStyle.Between: UpdateBetween(); break;
                case SlicerStyle.RelativeDate: break;
                default: UpdateItems(); break;
            }
            _updating = false;
        }

        /// <summary>Shows a message in place of the controls.</summary>
        private void Show(string text)
        {
            _root.Children.Clear();
            _root.Children.Add(new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = _u == null ? Brushes.Gray : Frozen.Brush(_u.Theme.Secondary)
            });
        }

        /// <summary>Reloads the checked values from the saved filter after a rebuild.</summary>
        private void RestoreState()
        {
            var state = _u.Def.SlicerState;
            _checked.Clear();
            if (state == null) return;
            if (state.Type == FilterType.Basic)
                foreach (var v in state.Values) _checked.Add(Key(Value.FromObject(v)));
            if (state.Type == FilterType.RelativeDate && _count != null)
            {
                _count.Text = state.RelativeCount.ToString(CultureInfo.InvariantCulture);
                _unit.SelectedItem = state.RelativeUnit;
            }
        }

        /// <summary>A value's identity in the list. NUL stands for BLANK, which no displayed value can equal.</summary>
        private static string Key(Value v) => v.IsBlank ? "\u0000" : v.ToDisplayString();

        // --------------------------------------------------- list / dropdown

        /// <summary>Builds the check box list.</summary>
        private void BuildList()
        {
            _items = new StackPanel { Margin = new Thickness(2) };
            _root.Children.Add(new ScrollViewer { Content = _items, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        }

        /// <summary>Builds the same list inside a popup under a toggle button.</summary>
        private void BuildDropdown()
        {
            _items = new StackPanel { Margin = new Thickness(6) };
            _summary = new TextBlock { Text = "All", VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            var scroll = new ScrollViewer { Content = _items, MaxHeight = 260, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var toggle = new System.Windows.Controls.Primitives.ToggleButton { Content = _summary, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(6, 3, 6, 3), Height = 28, VerticalAlignment = VerticalAlignment.Top };
            var popup = new System.Windows.Controls.Primitives.Popup
            {
                Child = new Border
                {
                    Child = scroll,
                    MinWidth = 160,
                    BorderThickness = new Thickness(1),
                    BorderBrush = Frozen.Brush(_u.Theme.Border),
                    Background = Frozen.Brush(_u.Theme.VisualBackground)
                },
                StaysOpen = false,
                PlacementTarget = toggle,
                Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom
            };
            toggle.Click += (s, e) => popup.IsOpen = toggle.IsChecked == true;
            popup.Closed += (s, e) => toggle.IsChecked = false;
            _root.Children.Add(toggle);
            _root.Children.Add(popup);
        }

        /// <summary>Syncs the check boxes to the values on offer. Boxes are rebuilt only if the list changed, and checked values that left the list stay remembered.</summary>
        private void UpdateItems()
        {
            var r = _u.Data;
            var opts = new List<KeyValuePair<string, object>>();
            for (int i = 0; i < r.RowCount; i++)
            {
                var v = r.Group(0, i);
                opts.Add(new KeyValuePair<string, object>(Key(v), v.ToObject()));
            }
            bool same = opts.Select(o => o.Key).SequenceEqual(_options.Select(o => o.Key));
            _options = opts;
            if (!same)
            {
                _items.Children.Clear();
                _boxes = new Dictionary<string, CheckBox>();
                string format = r.Columns[0].FormatString;
                for (int i = 0; i < r.RowCount; i++)
                {
                    var v = r.Group(0, i);
                    string key = Key(v);
                    var box = new CheckBox { Content = v.IsBlank ? "(Blank)" : PowerBiFormat.Format(v, format), Margin = new Thickness(2, 3, 2, 3), Tag = key };
                    box.Checked += OnBox;
                    box.Unchecked += OnBox;
                    _items.Children.Add(box);
                    _boxes[key] = box;
                }
            }
            foreach (var kv in _boxes)
            {
                kv.Value.IsChecked = _checked.Contains(kv.Key);
                kv.Value.Foreground = Frozen.Brush(_u.Theme.Foreground);
            }
            UpdateSummary();
        }

        /// <summary>Updates the dropdown's caption.</summary>
        private void UpdateSummary()
        {
            if (_summary != null)
                _summary.Text = _checked.Count == 0 ? "All" : _checked.Count == 1 ? _checked.First() : "Multiple selections";
        }

        /// <summary>Publishes a Basic filter from the checked values. Nothing checked means no filter.</summary>
        private void OnBox(object sender, RoutedEventArgs e)
        {
            if (_updating) return;
            var box = (CheckBox)sender;
            if (box.IsChecked == true) _checked.Add((string)box.Tag); else _checked.Remove((string)box.Tag);
            UpdateSummary();
            var field = _u.Def.Role("field")[0];
            var values = _options.Where(o => _checked.Contains(o.Key)).Select(o => o.Value).ToList();
            _host.SetSlicerFilter(values.Count == 0 ? null : FilterDef.Basic(field, values));
        }

        // --------------------------------------------------- between

        /// <summary>Builds two sliders and two text boxes for a numeric or date range.</summary>
        private void BuildBetween()
        {
            var g = new Grid { Margin = new Thickness(6), VerticalAlignment = VerticalAlignment.Center };
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            for (int i = 0; i < 3; i++) g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _lowText = new TextBox { Margin = new Thickness(0, 0, 0, 4) };
            _highText = new TextBox { Margin = new Thickness(0, 0, 0, 4) };
            _low = new Slider { Margin = new Thickness(0, 2, 0, 2) };
            _high = new Slider { Margin = new Thickness(0, 2, 0, 2) };
            Grid.SetColumn(_highText, 2);
            Grid.SetRow(_low, 1); Grid.SetColumnSpan(_low, 3);
            Grid.SetRow(_high, 2); Grid.SetColumnSpan(_high, 3);
            g.Children.Add(_lowText); g.Children.Add(_highText); g.Children.Add(_low); g.Children.Add(_high);
            _root.Children.Add(g);

            _low.ValueChanged += (s, e) => OnSlider(true);
            _high.ValueChanged += (s, e) => OnSlider(false);
            // Publish when the mouse is released, not on every tick of the drag.
            _low.PreviewMouseLeftButtonUp += (s, e) => PublishBetween();
            _high.PreviewMouseLeftButtonUp += (s, e) => PublishBetween();
            _lowText.LostFocus += (s, e) => FromText(_lowText, _low);
            _highText.LostFocus += (s, e) => FromText(_highText, _high);
        }

        /// <summary>Applies a typed bound.</summary>
        private void FromText(TextBox box, Slider slider)
        {
            if (_updating) return;
            double d;
            if (_isDate)
            {
                DateTime dt;
                if (!DateTime.TryParse(box.Text, CultureInfo.CurrentCulture, DateTimeStyles.None, out dt)) return;
                d = dt.ToOADate();
            }
            else if (!double.TryParse(box.Text, NumberStyles.Any, CultureInfo.CurrentCulture, out d)) return;
            slider.Value = d;
            PublishBetween();
        }

        /// <summary>Formats a bound. Dates travel through the sliders as OLE dates.</summary>
        private string Text(double d) =>
            _isDate ? DateTime.FromOADate(d).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : d.ToString("#,0.##", CultureInfo.CurrentCulture);

        /// <summary>Keeps low at or below high, and refreshes the text boxes.</summary>
        private void OnSlider(bool low)
        {
            if (_updating) return;
            _updating = true;
            if (low && _low.Value > _high.Value) _high.Value = _low.Value;
            if (!low && _high.Value < _low.Value) _low.Value = _high.Value;
            _lowText.Text = Text(_low.Value);
            _highText.Text = Text(_high.Value);
            _updating = false;
        }

        /// <summary>Resets the slider range to the visible values.</summary>
        private void UpdateBetween()
        {
            var r = _u.Data;
            var values = Enumerable.Range(0, r.RowCount).Select(i => r.Group(0, i)).Where(v => !v.IsBlank).ToList();
            if (values.Count == 0) return;
            _isDate = values[0].Kind == ValueKind.Date;
            double min = values.Min(v => v.AsDouble()), max = values.Max(v => v.AsDouble());
            // A fresh Slider's defaults identify the first update, since there is no flag for it.
            bool first = _low.Maximum == 10 && _low.Minimum == 0 && _high.Maximum == 10 && _high.Value == 0;
            _low.Minimum = _high.Minimum = min;
            _low.Maximum = _high.Maximum = max;
            if (first)
            {
                _low.Value = min; _high.Value = max;
                var st = _u.Def.SlicerState;
                if (st != null && st.Conditions.Count == 2)
                {
                    _low.Value = Value.FromObject(st.Conditions[0].Value).AsDouble();
                    _high.Value = Value.FromObject(st.Conditions[1].Value).AsDouble();
                }
            }
            _lowText.Text = Text(_low.Value);
            _highText.Text = Text(_high.Value);
        }

        /// <summary>Publishes an Advanced filter of two conditions, or clears it when the range is full.</summary>
        private void PublishBetween()
        {
            if (_updating || _u == null) return;
            var field = _u.Def.Role("field")[0];
            bool full = _low.Value <= _low.Minimum && _high.Value >= _high.Maximum;
            if (full) { _host.SetSlicerFilter(null); return; }
            Func<double, object> box = d => _isDate ? (object)DateTime.FromOADate(d).Date : d;
            _host.SetSlicerFilter(new FilterDef
            {
                Target = field,
                Type = FilterType.Advanced,
                Logic = LogicalOp.And,
                Conditions =
                {
                    new Lumen.Model.Condition { Op = ConditionOp.GreaterOrEqual, Value = box(_low.Value) },
                    new Lumen.Model.Condition { Op = ConditionOp.LessOrEqual, Value = box(_high.Value) }
                }
            });
        }

        // --------------------------------------------------- relative date

        /// <summary>Builds the "Last N units" row. It needs no data, only the field.</summary>
        private void BuildRelative()
        {
            var p = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6), VerticalAlignment = VerticalAlignment.Top };
            p.Children.Add(new TextBlock { Text = "Last", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
            _count = new TextBox { Width = 44, Text = "12", VerticalContentAlignment = VerticalAlignment.Center };
            _unit = new ComboBox { Width = 90, Margin = new Thickness(6, 0, 0, 0), ItemsSource = Enum.GetValues(typeof(RelativeUnit)), SelectedItem = RelativeUnit.Months };
            var apply = new Button { Content = "Apply", Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(8, 2, 8, 2) };
            var clear = new Button { Content = "Clear", Margin = new Thickness(4, 0, 0, 0), Padding = new Thickness(8, 2, 8, 2) };
            apply.Click += (s, e) =>
            {
                int n;
                if (!int.TryParse(_count.Text, out n) || n <= 0) return;
                _host.SetSlicerFilter(new FilterDef
                {
                    Target = _u.Def.Role("field")[0],
                    Type = FilterType.RelativeDate,
                    RelativeCount = n,
                    RelativeUnit = (RelativeUnit)_unit.SelectedItem
                });
            };
            clear.Click += (s, e) => _host.SetSlicerFilter(null);
            p.Children.Add(_count); p.Children.Add(_unit); p.Children.Add(apply); p.Children.Add(clear);
            _root.Children.Add(p);
        }

        /// <summary>Nothing to release.</summary>
        public void Dispose() { }
    }
}
