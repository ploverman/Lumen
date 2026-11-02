using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Lumen.Charting;
using Lumen.Model;

namespace Lumen.Reporting
{
    /// <summary>Filters at visual, page and report scope. In the builder the pane authors filters. With authoring off, as in the viewer,
    /// a reader can change the values of the filters the report already has, but cannot add or remove one or change its type.</summary>
    public sealed class FiltersPane : UserControl
    {
        private readonly ReportSession _s;
        private readonly bool _authoring;
        private readonly StackPanel _root = new StackPanel { Margin = new Thickness(8, 0, 8, 8) };

        /// <summary>Builds the empty pane.</summary>
        public FiltersPane(ReportSession s, bool authoring = true)
        {
            _s = s;
            _authoring = authoring;
            Content = Ui.Pane("Filters", new ScrollViewer { Content = _root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        }

        /// <summary>Shows up to three scopes: the selected visual, the page and the report.</summary>
        public void Rebuild(PageDef page, VisualDef selected)
        {
            _root.Children.Clear();
            if (selected != null) Section("On this visual", selected.VisualFilters);
            if (page != null) Section("On this page", page.PageFilters);
            Section("On all pages", _s.Doc.ReportFilters);
            if (_root.Children.Count == 0) _root.Children.Add(Ui.Label("This report has no filters on this page. Use the slicers on the page."));
        }

        /// <summary>One scope: its filter cards and a drop zone. Only columns can be filter targets; measure filters are not implemented.</summary>
        private void Section(string title, List<FilterDef> scope)
        {
            if (!_authoring && scope.Count == 0) return;
            _root.Children.Add(Ui.Heading(title));
            foreach (var f in scope.ToList()) _root.Children.Add(Card(scope, f));
            if (!_authoring) return;

            var drop = new Border { BorderThickness = new Thickness(1), Padding = new Thickness(6, 8, 6, 8), AllowDrop = true, Margin = new Thickness(0, 2, 0, 4) };
            drop.SetResourceReference(Border.BorderBrushProperty, "Line");
            drop.Child = new TextBlock { Text = "Add data fields here", FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center }.Fg("ChromeFg2");
            drop.DragOver += (s, e) =>
            {
                var f = e.Data.GetData(typeof(FieldRef)) as FieldRef;
                e.Effects = f != null && f.Kind == FieldKind.Column ? DragDropEffects.Copy : DragDropEffects.None;
                e.Handled = true;
            };
            drop.Drop += (s, e) =>
            {
                var f = e.Data.GetData(typeof(FieldRef)) as FieldRef;
                if (f == null || f.Kind != FieldKind.Column) return;
                _s.Edit("Add filter", () => scope.Add(new FilterDef { Target = FieldRef.ForColumn(f.TableId, f.ColumnId), Type = FilterType.Basic }));
            };
            _root.Children.Add(drop);
        }

        /// <summary>One filter card. Changing the type clears values and conditions, because they do not carry over between filter types.</summary>
        private UIElement Card(List<FilterDef> scope, FilterDef f)
        {
            var body = new StackPanel { Margin = new Thickness(0, 2, 0, 2) };
            var head = new DockPanel();
            if (_authoring)
            {
                var remove = new Button { Content = "", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 9, Width = 20, Height = 20 };
                remove.Click += (s, e) => _s.Edit("Remove filter", () => scope.RemoveAll(x => x.Id == f.Id));
                DockPanel.SetDock(remove, Dock.Right);
                head.Children.Add(remove);
            }
            head.Children.Add(new TextBlock { Text = _s.Model.DisplayName(f.Target), FontWeight = FontWeights.SemiBold, FontSize = 12 }.Fg("ChromeFg"));
            body.Children.Add(head);

            if (_authoring) TypePicker(body, scope, f);

            switch (f.Type)
            {
                case FilterType.Basic: BasicEditor(body, scope, f); break;
                case FilterType.Advanced: AdvancedEditor(body, scope, f); break;
                case FilterType.TopN: TopNEditor(body, scope, f); break;
                case FilterType.RelativeDate: RelativeEditor(body, scope, f); break;
            }
            var frame = new Border { BorderThickness = new Thickness(1), Padding = new Thickness(6), Margin = new Thickness(0, 2, 0, 4), Child = body };
            frame.SetResourceReference(Border.BorderBrushProperty, "Line");
            return frame;
        }

        /// <summary>The filter type combo box.</summary>
        private void TypePicker(StackPanel body, List<FilterDef> scope, FilterDef f)
        {
            var col = _s.Model.Table(f.Target.TableId)?.Column(f.Target.ColumnId);
            var types = new List<FilterType> { FilterType.Basic, FilterType.Advanced, FilterType.TopN };
            if (col != null && col.DataType.IsTemporal()) types.Add(FilterType.RelativeDate);
            var type = new ComboBox { ItemsSource = types, SelectedItem = f.Type, Margin = new Thickness(0, 2, 0, 2) };
            type.SelectionChanged += (s, e) =>
            {
                var chosen = (FilterType)type.SelectedItem;
                _s.Edit("Change filter type", () =>
                {
                    var t = scope.First(x => x.Id == f.Id);
                    t.Type = chosen; t.Values.Clear(); t.Conditions.Clear();
                });
            };
            body.Children.Add(type);
        }

        /// <summary>Check boxes for up to 500 values. The query runs synchronously on the UI thread, which is fine for a distinct list but is the slowest thing this pane does.</summary>
        private void BasicEditor(StackPanel body, List<FilterDef> scope, FilterDef f)
        {
            var spec = new QuerySpec { GroupBy = { FieldRef.ForColumn(f.Target.TableId, f.Target.ColumnId) }, Limit = 500 };
            var r = _s.Engine.Execute(spec, CancellationToken.None);
            if (r.Error != null) { body.Children.Add(Ui.Label(r.Error)); return; }
            var panel = new StackPanel();
            var fmt = r.Columns[0].FormatString;
            var selected = new HashSet<string>(f.Values.Select(v => Value.FromObject(v).ToDisplayString()));
            for (int i = 0; i < r.RowCount; i++)
            {
                var v = r.Group(0, i);
                var cb = new CheckBox { Content = v.IsBlank ? "(Blank)" : PowerBiFormat.Format(v, fmt), IsChecked = selected.Contains(v.ToDisplayString()), Margin = new Thickness(0, 1, 0, 1) }.Fg("ChromeFg");
                var value = v.ToObject();
                cb.Click += (s, e) =>
                {
                    bool on = cb.IsChecked == true;
                    _s.Edit("Change filter", () =>
                    {
                        var t = scope.First(x => x.Id == f.Id);
                        t.Values.RemoveAll(x => Value.FromObject(x).ToDisplayString() == Value.FromObject(value).ToDisplayString());
                        if (on) t.Values.Add(value);
                    });
                };
                panel.Children.Add(cb);
            }
            body.Children.Add(new ScrollViewer { Content = panel, MaxHeight = 170, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        }

        /// <summary>One condition. The model allows more, and the Between slicer writes two, but this editor shows and replaces only the first.</summary>
        private void AdvancedEditor(StackPanel body, List<FilterDef> scope, FilterDef f)
        {
            var existing = f.Conditions.FirstOrDefault();
            var op = new ComboBox { ItemsSource = Enum.GetValues(typeof(ConditionOp)), SelectedItem = existing?.Op ?? ConditionOp.Equals, Margin = new Thickness(0, 2, 0, 2) };
            var val = new TextBox { Text = Convert.ToString(existing?.Value) ?? "" };
            body.Children.Add(op);
            body.Children.Add(val);
            body.Children.Add(Ui.Button("Apply filter", () => _s.Edit("Change filter", () =>
            {
                var t = scope.First(x => x.Id == f.Id);
                t.Conditions = new List<Lumen.Model.Condition> { new Lumen.Model.Condition { Op = (ConditionOp)op.SelectedItem, Value = val.Text } };
            })));
        }

        /// <summary>Top or bottom N by a measure.</summary>
        private void TopNEditor(StackPanel body, List<FilterDef> scope, FilterDef f)
        {
            var dir = new ComboBox { ItemsSource = new[] { "Top", "Bottom" }, SelectedIndex = f.TopIsBottom ? 1 : 0, Margin = new Thickness(0, 2, 0, 2) };
            var count = new TextBox { Text = f.TopCount.ToString(CultureInfo.InvariantCulture) };
            var measures = _s.Model.Measures.ToList();
            var by = new ComboBox { ItemsSource = measures, DisplayMemberPath = "Name", Margin = new Thickness(0, 2, 0, 2) };
            by.SelectedItem = measures.FirstOrDefault(m => f.TopBy != null && m.Id == f.TopBy.MeasureId);
            body.Children.Add(dir);
            body.Children.Add(count);
            body.Children.Add(Ui.Label("By value"));
            body.Children.Add(by);
            body.Children.Add(Ui.Button("Apply filter", () =>
            {
                int n;
                if (!int.TryParse(count.Text, out n) || by.SelectedItem == null) return;
                var m = (MeasureDef)by.SelectedItem;
                bool bottom = dir.SelectedIndex == 1;
                _s.Edit("Change filter", () =>
                {
                    var t = scope.First(x => x.Id == f.Id);
                    t.TopCount = n; t.TopIsBottom = bottom; t.TopBy = FieldRef.ForMeasure(m.Id);
                });
            }));
        }

        /// <summary>Last N units of a date column.</summary>
        private void RelativeEditor(StackPanel body, List<FilterDef> scope, FilterDef f)
        {
            var count = new TextBox { Text = f.RelativeCount.ToString(CultureInfo.InvariantCulture) };
            var unit = new ComboBox { ItemsSource = Enum.GetValues(typeof(RelativeUnit)), SelectedItem = f.RelativeUnit, Margin = new Thickness(0, 2, 0, 2) };
            body.Children.Add(Ui.Label("In the last"));
            body.Children.Add(count);
            body.Children.Add(unit);
            body.Children.Add(Ui.Button("Apply filter", () =>
            {
                int n;
                if (!int.TryParse(count.Text, out n)) return;
                var u = (RelativeUnit)unit.SelectedItem;
                _s.Edit("Change filter", () =>
                {
                    var t = scope.First(x => x.Id == f.Id);
                    t.RelativeCount = n; t.RelativeUnit = u;
                });
            }));
        }
    }
}
