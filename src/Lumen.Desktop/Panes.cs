using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Lumen.Charting;
using Lumen.Model;
using Lumen.Reporting;
using Lumen.Visuals;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Lumen.Desktop
{
    /// <summary>Tree of tables with their measures and columns: search, drag source, and context menus for tables and measures.</summary>
    public sealed class FieldsPane : UserControl
    {
        private readonly Session _s;
        private readonly MainWindow _owner;
        internal readonly TreeView _tree = new TreeView { BorderThickness = new Thickness(0), Background = Brushes.Transparent };
        private readonly TextBox _search = new TextBox { Margin = new Thickness(8, 0, 8, 6) };
        private readonly HashSet<string> _collapsed = new HashSet<string>();
        private Point _pressAt;
        private FieldRef _pressField;

        /// <summary>Builds the empty pane.</summary>
        public FieldsPane(Session s, MainWindow owner)
        {
            _s = s;
            _owner = owner;
            var dock = new DockPanel();
            DockPanel.SetDock(_search, Dock.Top);
            dock.Children.Add(_search);
            dock.Children.Add(_tree);
            Content = Ui.Pane("Fields", dock);
            _search.TextChanged += (x, e) => Rebuild();
            _tree.PreviewMouseMove += OnMove;
        }

        /// <summary>Rebuilds the tree from the model. Called after data-affecting edits and on each search keystroke; collapsed tables are remembered by id.</summary>
        public void Rebuild()
        {
            _tree.Items.Clear();
            string q = _search.Text.Trim();
            foreach (var t in _s.Model.Tables)
            {
                var node = new TreeViewItem { Header = Row("", t.Name, true, null), IsExpanded = !_collapsed.Contains(t.Id) };
                var tableId = t.Id;
                node.Expanded += (x, e) => _collapsed.Remove(tableId);
                node.Collapsed += (x, e) => _collapsed.Add(tableId);

                var menu = new ContextMenu();
                var nm = new MenuItem { Header = "New measure" };
                nm.Click += (x, e) => _owner.NewMeasure(tableId);
                menu.Items.Add(nm);
                var tr = new MenuItem { Header = "Transform data" };
                tr.Click += (x, e) => _owner.TransformTable(tableId);
                menu.Items.Add(tr);
                var rm = new MenuItem { Header = "Delete table" };
                rm.Click += (x, e) => _owner.DeleteTable(tableId);
                menu.Items.Add(rm);
                node.ContextMenu = menu;

                foreach (var m in _s.Model.Measures.Where(m => m.HomeTableId == t.Id && Match(m.Name, q)).OrderBy(m => m.Name))
                {
                    var measureItem = Item("", m.Name, FieldRef.ForMeasure(m.Id));
                    var mm = new ContextMenu();
                    var edit = new MenuItem { Header = "Edit measure" };
                    var measure = m;
                    edit.Click += (x, e) => _owner.EditMeasure(measure.Id);
                    var del = new MenuItem { Header = "Delete measure" };
                    del.Click += (x, e) => _s.Edit("Delete measure", () => _s.Model.Measures.RemoveAll(q2 => q2.Id == measure.Id));
                    mm.Items.Add(edit);
                    mm.Items.Add(del);
                    measureItem.ContextMenu = mm;
                    measureItem.MouseDoubleClick += (x, e) => { _owner.EditMeasure(measure.Id); e.Handled = true; };
                    node.Items.Add(measureItem);
                }
                foreach (var c in t.Columns.Where(c => !c.IsHidden && Match(c.Name, q)))
                {
                    string glyph = c.DataType.IsTemporal() ? "" : c.DataType.IsNumeric() && c.SummarizeBy != SummarizeBy.None ? "Σ" : "";
                    var field = FieldRef.ForColumn(t.Id, c.Id);
                    var columnItem = Item(glyph, c.Name, field);
                    columnItem.MouseDoubleClick += (x, e) => { _owner.AddFieldToSelection(field); e.Handled = true; };
                    node.Items.Add(columnItem);
                }
                _tree.Items.Add(node);
            }
        }

        /// <summary>Case-insensitive substring match. An empty query matches all.</summary>
        private static bool Match(string name, string q) =>
            q.Length == 0 || name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>A draggable field row. The press position is kept so that <see cref="OnMove"/> can tell a drag from a click.</summary>
        private TreeViewItem Item(string glyph, string text, FieldRef field)
        {
            var i = new TreeViewItem { Header = Row(glyph, text, false, field), Tag = field };
            i.PreviewMouseLeftButtonDown += (x, e) => { _pressAt = e.GetPosition(this); _pressField = field; };
            return i;
        }

        /// <summary>The icon and text of a tree row.</summary>
        private static StackPanel Row(string glyph, string text, bool bold, FieldRef f)
        {
            var p = new StackPanel { Orientation = Orientation.Horizontal };
            p.Children.Add(new TextBlock { Text = glyph, FontFamily = new FontFamily(glyph == "Σ" ? "Segoe UI" : "Segoe MDL2 Assets"), Width = 18, FontSize = 12, VerticalAlignment = VerticalAlignment.Center }.Fg("ChromeFg2"));
            p.Children.Add(new TextBlock { Text = text, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, FontSize = 12 }.Fg("ChromeFg"));
            return p;
        }

        /// <summary>Starts an OLE drag of a <see cref="FieldRef"/> once the mouse has moved 5 pixels. Wells, filters and the canvas read that type.</summary>
        private void OnMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed || _pressField == null) return;
            if ((e.GetPosition(this) - _pressAt).Length < 5) return;
            var f = _pressField;
            _pressField = null;
            DragDrop.DoDragDrop(this, new DataObject(typeof(FieldRef), f), DragDropEffects.Copy);
        }
    }

    /// <summary>Gallery, wells and a format pane generated from the options class by reflection.</summary>
    public sealed class VisualizationsPane : UserControl
    {
        private readonly Session _s;
        private readonly MainWindow _owner;
        private readonly WrapPanel _gallery = new WrapPanel { Margin = new Thickness(6, 0, 6, 4) };
        private readonly StackPanel _wells = new StackPanel { Margin = new Thickness(8, 0, 8, 0) };
        private readonly StackPanel _format = new StackPanel { Margin = new Thickness(8, 0, 8, 8) };
        // Set during a format edit so that the rebuild it triggers leaves the format pane alone: rebuilding would take focus from the text box being typed in.
        private bool _keepFormat;
        private string _formatFor;

        /// <summary>Builds the gallery from <see cref="VisualRegistry.Types"/>.</summary>
        public VisualizationsPane(Session s, MainWindow owner)
        {
            _s = s;
            _owner = owner;
            foreach (var type in VisualRegistry.Types)
            {
                var t = type;
                var icon = new System.Windows.Shapes.Path { Data = Ui.VisualIcons[t], Stretch = Stretch.None };
                icon.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "ChromeFg");
                var b = new Button { Content = icon, Width = 34, Height = 30, Margin = new Thickness(1), ToolTip = VisualRegistry.DisplayName(t) };
                b.Click += (x, e) => _owner.GalleryClicked(t);
                _gallery.Children.Add(b);
            }
            var stack = new StackPanel();
            stack.Children.Add(_gallery);
            stack.Children.Add(_wells);
            stack.Children.Add(_format);
            Content = Ui.Pane("Visualizations", new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        }

        /// <summary>Shows the wells and format options of a visual, or a hint when none is selected.</summary>
        public void Rebuild(VisualContainer c)
        {
            _wells.Children.Clear();
            if (c == null)
            {
                _format.Children.Clear();
                _formatFor = null;
                _wells.Children.Add(Ui.Label("Pick a visual type above to add one, or select a visual to edit its fields."));
                return;
            }
            var def = c.Def;
            if (def == null) return;
            foreach (var role in c.Caps.Roles) _wells.Children.Add(Well(c, def, role));

            // The format pane is rebuilt only for a new visual or type, which keeps expanded cards open across other edits.
            string key = def.Id + "|" + def.Type;
            if (_keepFormat && key == _formatFor) { _keepFormat = false; return; }
            _keepFormat = false;
            _formatFor = key;
            BuildFormat(c, def);
        }

        /// <summary>Builds one drop well. Drops are checked twice: DragOver sets the cursor, Drop guards the edit.</summary>
        private UIElement Well(VisualContainer c, VisualDef def, DataRole role)
        {
            var box = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
            box.Children.Add(Ui.Label(role.Display));
            var border = new Border { BorderThickness = new Thickness(1), MinHeight = 28, Padding = new Thickness(3), AllowDrop = true };
            border.SetResourceReference(Border.BorderBrushProperty, "Line");
            var list = new StackPanel();
            border.Child = list;
            box.Children.Add(border);

            var fields = def.Role(role.Name);
            if (fields.Count == 0)
                list.Children.Add(new TextBlock { Text = "Add data fields here", FontSize = 11, Margin = new Thickness(4, 3, 0, 3) }.Fg("ChromeFg2"));
            for (int i = 0; i < fields.Count; i++) list.Children.Add(WellItem(c, def, role, fields[i], i));

            border.DragOver += (s, e) =>
            {
                var f = e.Data.GetData(typeof(FieldRef)) as FieldRef;
                bool ok = f != null && role.Accepts(f, _s.Model) && fields.Count < role.Max;
                e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
                e.Handled = true;
            };
            border.Drop += (s, e) =>
            {
                var f = e.Data.GetData(typeof(FieldRef)) as FieldRef;
                if (f == null || !role.Accepts(f, _s.Model) || fields.Count >= role.Max) return;
                AddToWell(def, role, f);
                e.Handled = true;
            };
            return box;
        }

        /// <summary>Adds a field to a well. A raw column in a measure well is first given its default aggregation.</summary>
        public void AddToWell(VisualDef def, DataRole role, FieldRef f)
        {
            var field = f.Clone();
            if (role.Kind == RoleKind.Measure && field.Kind == FieldKind.Column)
                field = QueryHelpers.Normalize(new[] { field }, _s.Model)[0];
            _s.Edit("Add field", () =>
            {
                if (!def.Roles.ContainsKey(role.Name)) def.Roles[role.Name] = new List<FieldRef>();
                def.Roles[role.Name].Add(field);
            });
        }

        /// <summary>One field in a well, with a remove button, and an aggregation picker for implicit measures.</summary>
        private UIElement WellItem(VisualContainer c, VisualDef def, DataRole role, FieldRef f, int index)
        {
            var d = new DockPanel { Margin = new Thickness(0, 1, 0, 1) };
            var remove = new Button { Content = "", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 9, Width = 20, Height = 20, ToolTip = "Remove" };
            remove.Click += (s, e) => _s.Edit("Remove field", () => def.Roles[role.Name].RemoveAt(index));
            DockPanel.SetDock(remove, Dock.Right);
            d.Children.Add(remove);

            if (f.Kind == FieldKind.Column && f.Aggregation.HasValue && f.Aggregation != SummarizeBy.None)
            {
                var agg = new ComboBox { Width = 76, FontSize = 11, Margin = new Thickness(2, 0, 2, 0), ItemsSource = new[] { SummarizeBy.Sum, SummarizeBy.Average, SummarizeBy.Min, SummarizeBy.Max, SummarizeBy.Count, SummarizeBy.DistinctCount }, SelectedItem = f.Aggregation.Value };
                agg.SelectionChanged += (s, e) =>
                {
                    var chosen = (SummarizeBy)agg.SelectedItem;
                    _s.Edit("Change aggregation", () => def.Roles[role.Name][index] = FieldRef.ForColumn(f.TableId, f.ColumnId, chosen));
                };
                DockPanel.SetDock(agg, Dock.Right);
                d.Children.Add(agg);
            }
            d.Children.Add(new TextBlock { Text = _s.Model.DisplayName(f), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(3, 0, 0, 0) }.Fg("ChromeFg"));
            return d;
        }

        /// <summary>Generates the format pane from the options type: each property with [FormatCard] and [FormatProperty] gets an editor, grouped by card.
        /// A new option needs only a new attributed property.</summary>
        private void BuildFormat(VisualContainer c, VisualDef def)
        {
            _format.Children.Clear();
            _format.Children.Add(Ui.Heading("Format"));
            var options = c.Format;
            var props = options.GetType().GetProperties()
                .Select(p => new { P = p, Card = p.GetCustomAttribute<FormatCardAttribute>(), Prop = p.GetCustomAttribute<FormatPropertyAttribute>() })
                .Where(x => x.Card != null && x.Prop != null);

            foreach (var group in props.GroupBy(x => x.Card.Card))
            {
                var body = new StackPanel { Margin = new Thickness(6, 0, 0, 4) };
                foreach (var x in group) body.Children.Add(Editor(def, options, x.P, x.Prop));
                _format.Children.Add(new Expander { Header = new TextBlock { Text = group.Key, FontSize = 12 }.Fg("ChromeFg"), Content = body });
            }
        }

        /// <summary>Stores one format option as an undoable edit. Only changed properties land in VisualDef.Format; the rest stay at class defaults.</summary>
        private void Commit(VisualDef def, string name, object value)
        {
            _keepFormat = true;
            var ser = JsonSerializer.Create(ModelJson.Settings);
            _s.Edit("Format visual", () => def.Format[name] = value == null ? JValue.CreateNull() : JToken.FromObject(value, ser));
            _keepFormat = false;
        }

        /// <summary>Picks an editor by property type: check box for bool, combo box for enum, text box for double and string. Text commits on Enter or lost focus, and numbers clamp to the attribute's range.</summary>
        private UIElement Editor(VisualDef def, object options, PropertyInfo p, FormatPropertyAttribute a)
        {
            object current = p.GetValue(options);
            if (p.PropertyType == typeof(bool))
            {
                var cb = new CheckBox { Content = a.Label, IsChecked = (bool)current, Margin = new Thickness(0, 3, 0, 3) }.Fg("ChromeFg");
                cb.Click += (s, e) => Commit(def, p.Name, cb.IsChecked == true);
                return cb;
            }

            var box = new StackPanel();
            box.Children.Add(Ui.Label(a.Label));
            if (p.PropertyType.IsEnum)
            {
                var combo = new ComboBox { ItemsSource = Enum.GetValues(p.PropertyType), SelectedItem = current };
                combo.SelectionChanged += (s, e) => Commit(def, p.Name, combo.SelectedItem);
                box.Children.Add(combo);
                return box;
            }
            var text = new TextBox { Text = Convert.ToString(current, CultureInfo.CurrentCulture) ?? "" };
            Action commit = () =>
            {
                if (p.PropertyType == typeof(double))
                {
                    double d;
                    if (!double.TryParse(text.Text, NumberStyles.Any, CultureInfo.CurrentCulture, out d)) return;
                    if (!double.IsNaN(a.Min)) d = Math.Max(a.Min, d);
                    if (!double.IsNaN(a.Max)) d = Math.Min(a.Max, d);
                    Commit(def, p.Name, d);
                }
                else Commit(def, p.Name, string.IsNullOrEmpty(text.Text) ? null : text.Text);
            };
            text.LostFocus += (s, e) => commit();
            text.KeyDown += (s, e) => { if (e.Key == Key.Enter) commit(); };
            box.Children.Add(text);
            return box;
        }
    }

    /// <summary>Selection pane: every visual, with hide and lock.</summary>
    public sealed class SelectionPane : UserControl
    {
        private readonly Session _s;
        private readonly StackPanel _list = new StackPanel { Margin = new Thickness(8, 0, 8, 8) };

        /// <summary>Builds the empty pane.</summary>
        public SelectionPane(Session s)
        {
            _s = s;
            Content = Ui.Pane("Selection", new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        }

        /// <summary>Lists the page's visuals, topmost first. It creates a throwaway visual for each row just to learn its format type; those instances are never disposed, which is harmless because Dispose does nothing.</summary>
        public void Rebuild(ReportCanvas canvas)
        {
            _list.Children.Clear();
            var page = canvas.Page;
            if (page == null) return;
            foreach (var v in page.Visuals.OrderByDescending(x => x.ZIndex))
            {
                var vis = v;
                var row = new DockPanel { Margin = new Thickness(0, 1, 0, 1) };
                var hide = new CheckBox { Content = "Hide", IsChecked = v.IsHidden, FontSize = 11, Margin = new Thickness(4, 0, 0, 0) }.Fg("ChromeFg2");
                hide.Click += (s, e) => _s.Edit("Hide visual", () => vis.IsHidden = hide.IsChecked == true);
                var locked = new CheckBox { Content = "Lock", IsChecked = v.IsLocked, FontSize = 11, Margin = new Thickness(4, 0, 0, 0) }.Fg("ChromeFg2");
                locked.Click += (s, e) => _s.Edit("Lock visual", () => vis.IsLocked = locked.IsChecked == true);
                DockPanel.SetDock(hide, Dock.Right);
                DockPanel.SetDock(locked, Dock.Right);
                row.Children.Add(hide);
                row.Children.Add(locked);

                var fmt = (VisualFormat)VisualRegistry.ReadFormat(v, VisualRegistry.Create(v.Type).Capabilities.FormatType);
                string name = !string.IsNullOrWhiteSpace(fmt.Title) ? fmt.Title : VisualRegistry.DisplayName(v.Type);
                var pick = new Button { Content = name, HorizontalContentAlignment = HorizontalAlignment.Left, BorderThickness = new Thickness(0), Background = Brushes.Transparent, Padding = new Thickness(2) }.Fg("ChromeFg");
                pick.Click += (s, e) => canvas.SetSelection(new[] { vis.Id });
                row.Children.Add(pick);
                _list.Children.Add(row);
            }
        }
    }

    /// <summary>Shows the per-visual timing text that the canvas produces after each refresh.</summary>
    public sealed class PerformancePane : UserControl
    {
        private readonly TextBlock _text = new TextBlock { FontFamily = new FontFamily("Consolas"), FontSize = 11, Margin = new Thickness(8), TextWrapping = TextWrapping.Wrap };

        /// <summary>Builds the empty pane.</summary>
        public PerformancePane()
        {
            _text.Fg("ChromeFg");
            Content = Ui.Pane("Performance analyzer", new ScrollViewer { Content = _text, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        }

        /// <summary>Replaces the report.</summary>
        public void Show(string text) => _text.Text = text;
    }
}
