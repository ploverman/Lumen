using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Lumen.Charting;
using Lumen.Engine;
using Lumen.Model;
using Lumen.Reporting;

namespace Lumen.Desktop
{
    /// <summary>The Data view: table list, row grid and a strip of properties for the current column.</summary>
    public sealed class DataView : UserControl
    {
        private readonly Session _s;
        private readonly MainWindow _owner;
        private readonly ListBox _tables = new ListBox { Width = 170, BorderThickness = new Thickness(0) };
        private readonly DataGrid _grid = new DataGrid
        {
            AutoGenerateColumns = false, IsReadOnly = true, EnableRowVirtualization = true, EnableColumnVirtualization = true,
            CanUserSortColumns = false, HeadersVisibility = DataGridHeadersVisibility.Column, SelectionUnit = DataGridSelectionUnit.CellOrRowHeader,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal
        };
        private readonly WrapPanel _props = new WrapPanel { Margin = new Thickness(8, 6, 8, 6) };
        private readonly TextBlock _status = new TextBlock { Margin = new Thickness(8, 2, 8, 4) };
        private List<ColumnDef> _shown = new List<ColumnDef>();
        private ColumnDef _column;
        private bool _building;

        /// <summary>Builds the view. The current column follows the grid's current cell.</summary>
        public DataView(Session s, MainWindow owner)
        {
            _s = s;
            _owner = owner;
            _status.Fg("ChromeFg2");
            var d = new DockPanel().Bg("PanelBg");
            _tables.Bg("PanelBg");
            DockPanel.SetDock(_tables, Dock.Left);
            d.Children.Add(_tables);
            var right = new DockPanel();
            DockPanel.SetDock(_props, Dock.Top);
            DockPanel.SetDock(_status, Dock.Bottom);
            right.Children.Add(_props);
            right.Children.Add(_status);
            right.Children.Add(_grid);
            d.Children.Add(right);
            Content = d;
            _tables.SelectionChanged += (x, e) => { if (!_building) ShowTable(); };
            _grid.CurrentCellChanged += (x, e) =>
            {
                int idx = _grid.CurrentColumn?.DisplayIndex ?? -1;
                if (idx >= 0 && idx < _shown.Count) { _column = _shown[idx]; BuildProps(); }
            };
        }

        /// <summary>The selected table.</summary>
        private TableDef Current => _tables.SelectedItem as TableDef;

        /// <summary>Reloads the table list and keeps the selection when it can.</summary>
        public void Rebuild()
        {
            _building = true;
            var keep = Current?.Id;
            _tables.ItemsSource = null;
            _tables.DisplayMemberPath = "Name";
            _tables.ItemsSource = _s.Model.Tables;
            _tables.SelectedItem = _s.Model.Tables.FirstOrDefault(t => t.Id == keep) ?? _s.Model.Tables.FirstOrDefault();
            _building = false;
            ShowTable();
        }

        /// <summary>Fills the grid. Columns in the model with no data are left out.</summary>
        private void ShowTable()
        {
            var t = Current;
            _props.Children.Clear();
            _shown = new List<ColumnDef>();
            TableData data;
            if (t == null || !_s.Tables.TryGetValue(t.Id, out data))
            {
                _grid.ItemsSource = null;
                _status.Text = "No data";
                return;
            }
            _shown = t.Columns.Where(c => data.ById.ContainsKey(c.Id)).ToList();
            VirtualRows.Fill(_grid, _shown.Select(c => data.ById[c.Id]).ToList(), _shown.Select(c => c.FormatString).ToList(), data.RowCount);
            _status.Text = $"{data.RowCount:#,0} rows, {_shown.Count} columns";
            _column = _column != null ? _shown.FirstOrDefault(c => c.Id == _column.Id) ?? _shown.FirstOrDefault() : _shown.FirstOrDefault();
            BuildProps();
        }

        /// <summary>Builds the column property strip. Each control writes through <see cref="Session.Edit"/>, so all of it can be undone.</summary>
        private void BuildProps()
        {
            _props.Children.Clear();
            var t = Current;
            var c = _column;
            if (t == null || c == null) return;
            Action<string, FrameworkElement> field = (label, element) =>
            {
                var box = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
                box.Children.Add(Ui.Label(label));
                box.Children.Add(element);
                _props.Children.Add(box);
            };

            var name = new TextBox { Text = c.Name, Width = 130 };
            name.LostFocus += (x, e) => { if (name.Text.Length > 0 && name.Text != c.Name) _s.Edit("Rename column", () => c.Name = name.Text); };
            field("Column", name);

            var fmt = new TextBox { Text = c.FormatString ?? "", Width = 100 };
            fmt.LostFocus += (x, e) => { if (fmt.Text != (c.FormatString ?? "")) _s.Edit("Format column", () => c.FormatString = fmt.Text.Length == 0 ? null : fmt.Text); };
            field("Format string", fmt);

            var sum = new ComboBox { ItemsSource = Enum.GetValues(typeof(SummarizeBy)), SelectedItem = c.SummarizeBy, Width = 110 };
            sum.SelectionChanged += (x, e) => _s.Edit("Change summarization", () => c.SummarizeBy = (SummarizeBy)sum.SelectedItem);
            field("Summarize by", sum);

            var others = new[] { "(none)" }.Concat(t.Columns.Where(o => o.Id != c.Id).Select(o => o.Name)).ToList();
            var sort = new ComboBox { ItemsSource = others, Width = 120, SelectedItem = t.Column(c.SortByColumnId ?? "")?.Name ?? "(none)" };
            sort.SelectionChanged += (x, e) => _s.Edit("Sort by column", () => c.SortByColumnId = t.ColumnByName((string)sort.SelectedItem)?.Id);
            field("Sort by column", sort);

            var hidden = new CheckBox { Content = "Hidden", IsChecked = c.IsHidden, Margin = new Thickness(0, 4, 0, 0) }.Fg("ChromeFg");
            hidden.Click += (x, e) => _s.Edit("Hide column", () => c.IsHidden = hidden.IsChecked == true);
            field("Visibility", hidden);

            if (c.DataType.IsTemporal())
            {
                var mark = new CheckBox { Content = "Date table", IsChecked = t.DateTableColumnId == c.Id, Margin = new Thickness(0, 4, 0, 0) }.Fg("ChromeFg");
                mark.Click += (x, e) => _s.Edit("Mark as date table", () => t.DateTableColumnId = mark.IsChecked == true ? c.Id : null);
                field("Mark as", mark);
            }
        }
    }

    /// <summary>Table cards on a canvas, with relationships as curves. Drag a column onto another table's column to relate them.</summary>
    public sealed class ModelView : UserControl
    {
        private readonly Session _s;
        private readonly Canvas _canvas = new Canvas { Width = 2400, Height = 1600 };
        private readonly Canvas _lines = new Canvas { IsHitTestVisible = true };
        private readonly ScaleTransform _zoom = new ScaleTransform(1, 1);
        private readonly Dictionary<string, Border> _cards = new Dictionary<string, Border>();
        private const double CardW = 190;

        /// <summary>Builds the view. Ctrl+wheel zooms between 40% and 200%.</summary>
        public ModelView(Session s)
        {
            _s = s;
            _canvas.Bg("CanvasBg");
            _canvas.LayoutTransform = _zoom;
            _canvas.Children.Add(_lines);
            var scroll = new ScrollViewer { Content = _canvas, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            scroll.PreviewMouseWheel += (x, e) =>
            {
                if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
                double z = Math.Max(0.4, Math.Min(2, _zoom.ScaleX + (e.Delta > 0 ? 0.1 : -0.1)));
                _zoom.ScaleX = _zoom.ScaleY = z;
                e.Handled = true;
            };
            var dock = new DockPanel();
            var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 4, 8, 4) };
            bar.Children.Add(Ui.Button("Auto-detect relationships", AutoDetect));
            bar.Children.Add(Ui.Label("Drag a column onto a column of another table to relate them. Double-click a line to edit it."));
            DockPanel.SetDock(bar, Dock.Top);
            dock.Children.Add(bar);
            dock.Children.Add(scroll);
            Content = dock;
        }

        /// <summary>Runs relationship detection over the loaded tables.</summary>
        private void AutoDetect()
        {
            var found = Lumen.Connectors.TableLoader.DetectRelationships(_s.Model, _s.Tables);
            if (found.Count == 0) { MessageBox.Show("No new relationships were found.", "Lumen"); return; }
            _s.Edit("Auto-detect relationships", () => _s.Model.Relationships.AddRange(found));
        }

        /// <summary>Recreates the cards, then draws lines once layout has measured them. Tables with no saved position line up in a row.</summary>
        public void Rebuild()
        {
            foreach (var c in _cards.Values) _canvas.Children.Remove(c);
            _cards.Clear();
            int i = 0;
            foreach (var t in _s.Model.Tables)
            {
                if (t.DiagramX == 0 && t.DiagramY == 0) { t.DiagramX = 40 + i * 240; t.DiagramY = 40; }
                var card = MakeCard(t);
                Canvas.SetLeft(card, t.DiagramX);
                Canvas.SetTop(card, t.DiagramY);
                _cards[t.Id] = card;
                _canvas.Children.Add(card);
                i++;
            }
            Dispatcher.BeginInvoke(new Action(DrawLines), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        /// <summary>Makes a table card: the header drags the card, and each column row is both a drag source and a drop target.
        /// Card moves write straight to the definition, outside undo, so they do not mark the report as changed.</summary>
        private Border MakeCard(TableDef t)
        {
            var body = new StackPanel();
            var head = new TextBlock { Text = t.Name, FontWeight = FontWeights.SemiBold, Padding = new Thickness(8, 5, 8, 5), Cursor = Cursors.SizeAll }.Fg("ChromeFg");
            body.Children.Add(head);
            foreach (var c in t.Columns)
            {
                var col = c;
                var row = new TextBlock { Text = c.Name, FontSize = 12, Padding = new Thickness(10, 2, 8, 2), AllowDrop = true, Background = Brushes.Transparent, Opacity = c.IsHidden ? 0.5 : 1 }.Fg("ChromeFg");
                row.MouseLeftButtonDown += (s, e) =>
                {
                    DragDrop.DoDragDrop(row, new DataObject("lumen-column", t.Id + "|" + col.Id), DragDropEffects.Link);
                    e.Handled = true;
                };
                row.Drop += (s, e) =>
                {
                    var data = e.Data.GetData("lumen-column") as string;
                    if (data != null) Relate(data, t.Id, col.Id);
                    e.Handled = true;
                };
                row.DragOver += (s, e) =>
                {
                    var data = e.Data.GetData("lumen-column") as string;
                    e.Effects = data != null && !data.StartsWith(t.Id + "|") ? DragDropEffects.Link : DragDropEffects.None;
                    e.Handled = true;
                };
                body.Children.Add(row);
            }
            var card = new Border { Width = CardW, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3), Child = body, Padding = new Thickness(0, 0, 0, 6) }.Bg("PanelBg");
            card.SetResourceReference(Border.BorderBrushProperty, "Line");

            Point? press = null;
            double ox = 0, oy = 0;
            head.MouseLeftButtonDown += (s, e) =>
            {
                press = e.GetPosition(_canvas);
                ox = t.DiagramX; oy = t.DiagramY;
                head.CaptureMouse();
                e.Handled = true;
            };
            head.MouseMove += (s, e) =>
            {
                if (press == null) return;
                var d = e.GetPosition(_canvas) - press.Value;
                t.DiagramX = Math.Max(0, ox + d.X);
                t.DiagramY = Math.Max(0, oy + d.Y);
                Canvas.SetLeft(card, t.DiagramX);
                Canvas.SetTop(card, t.DiagramY);
                DrawLines();
            };
            head.MouseLeftButtonUp += (s, e) => { press = null; head.ReleaseMouseCapture(); };
            return card;
        }

        /// <summary>Creates a relationship from a column drop. The side with unique values becomes the one side, and a second relationship between the same tables starts inactive.</summary>
        private void Relate(string from, string toTable, string toColumn)
        {
            var parts = from.Split('|');
            string fromTable = parts[0], fromColumn = parts[1];
            if (fromTable == toTable) return;
            var a = _s.Dataset.ColumnOf(fromTable, fromColumn);
            var b = _s.Dataset.ColumnOf(toTable, toColumn);
            if (a == null || b == null) return;

            // The unique side is the one side.
            bool bUnique = b.Encoded.Values.Length - 1 >= b.RowCount;
            bool aUnique = a.Encoded.Values.Length - 1 >= a.RowCount;
            if (!bUnique && !aUnique)
            {
                MessageBox.Show("Neither column has unique values, and many-to-many relationships are not supported.", "Lumen");
                return;
            }
            bool swap = !bUnique;
            var rel = new RelationshipDef
            {
                Id = Guid.NewGuid().ToString("N"),
                FromTableId = swap ? toTable : fromTable, FromColumnId = swap ? toColumn : fromColumn,
                ToTableId = swap ? fromTable : toTable, ToColumnId = swap ? fromColumn : toColumn,
                Cardinality = aUnique && bUnique ? Cardinality.OneToOne : Cardinality.ManyToOne
            };
            rel.IsActive = !_s.Model.Relationships.Any(r => r.IsActive && r.FromTableId == rel.FromTableId && r.ToTableId == rel.ToTableId);
            _s.Edit("New relationship", () => _s.Model.Relationships.Add(rel));
        }

        /// <summary>Draws each relationship as a curve with * at the many side, 1 at the one side, and an arrow for filter direction. Inactive ones are dashed.</summary>
        private void DrawLines()
        {
            _lines.Children.Clear();
            foreach (var rel in _s.Model.Relationships)
            {
                Border from, to;
                if (!_cards.TryGetValue(rel.FromTableId, out from) || !_cards.TryGetValue(rel.ToTableId, out to)) continue;
                var a = Rect(from);
                var b = Rect(to);
                bool rightward = b.X >= a.X + a.Width;
                var p0 = new Point(rightward ? a.Right : a.Left, a.Top + a.Height / 2);
                var p1 = new Point(rightward ? b.Left : b.Right, b.Top + b.Height / 2);
                double bend = Math.Max(40, Math.Abs(p1.X - p0.X) / 2) * (rightward ? 1 : -1);

                var fig = new PathFigure { StartPoint = p0 };
                fig.Segments.Add(new BezierSegment(new Point(p0.X + bend, p0.Y), new Point(p1.X - bend, p1.Y), p1, true));
                var path = new Path { Data = new PathGeometry(new[] { fig }), StrokeThickness = 2, Stroke = (Brush)Application.Current.Resources["ChromeFg2"], Cursor = Cursors.Hand, ToolTip = Describe(rel) };
                if (!rel.IsActive) path.StrokeDashArray = new DoubleCollection { 4, 3 };
                var r = rel;
                path.MouseLeftButtonDown += (s, e) => { if (e.ClickCount == 2) EditRelationship(r); };
                _lines.Children.Add(path);

                _lines.Children.Add(Marker("*", p0, rightward ? 8 : -14));
                _lines.Children.Add(Marker("1", p1, rightward ? -14 : 8));
                // Direction: filters flow from the one side to the many side.
                var mid = new Point((p0.X + p1.X) / 2, (p0.Y + p1.Y) / 2);
                _lines.Children.Add(Marker(rightward ? "◀" : "▶", mid, -6));
            }
        }

        /// <summary>Text form of a relationship.</summary>
        private string Describe(RelationshipDef r)
        {
            var from = _s.Model.Table(r.FromTableId);
            var to = _s.Model.Table(r.ToTableId);
            return $"{from?.Name}[{from?.Column(r.FromColumnId)?.Name}] * → 1 {to?.Name}[{to?.Column(r.ToColumnId)?.Name}]" + (r.IsActive ? "" : " (inactive)");
        }

        /// <summary>A small label on a line.</summary>
        private static TextBlock Marker(string text, Point at, double dx)
        {
            var t = new TextBlock { Text = text, FontWeight = FontWeights.Bold, IsHitTestVisible = false }.Fg("ChromeFg");
            Canvas.SetLeft(t, at.X + dx);
            Canvas.SetTop(t, at.Y - 18);
            return t;
        }

        /// <summary>A card's bounds, with a fallback before first layout.</summary>
        private static Rect Rect(Border card) =>
            new Rect(Canvas.GetLeft(card), Canvas.GetTop(card), card.ActualWidth > 0 ? card.ActualWidth : CardW, card.ActualHeight > 0 ? card.ActualHeight : 120);

        /// <summary>The relationship dialog. Direction is fixed to Single. Active and cardinality are stored, but the engine reads only IsActive; it ignores inactive relationships.</summary>
        private void EditRelationship(RelationshipDef r)
        {
            var w = new Window
            {
                Title = "Edit relationship", Width = 420, SizeToContent = SizeToContent.Height,
                Owner = Window.GetWindow(this), WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize
            };
            w.Bg("PanelBg");
            var p = new StackPanel { Margin = new Thickness(14) };
            p.Children.Add(Ui.Label(Describe(r)));
            var active = new CheckBox { Content = "Active", IsChecked = r.IsActive, Margin = new Thickness(0, 8, 0, 0) }.Fg("ChromeFg");
            var card = new ComboBox { ItemsSource = Enum.GetValues(typeof(Cardinality)), SelectedItem = r.Cardinality, Margin = new Thickness(0, 6, 0, 0) };
            var dir = new ComboBox { ItemsSource = new[] { "Single" }, SelectedIndex = 0, Margin = new Thickness(0, 6, 0, 0), IsEnabled = false, ToolTip = "Bi-directional filtering is a later phase" };
            p.Children.Add(active);
            p.Children.Add(Ui.Label("Cardinality"));
            p.Children.Add(card);
            p.Children.Add(Ui.Label("Cross-filter direction"));
            p.Children.Add(dir);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
            buttons.Children.Add(Ui.Button("OK", () =>
            {
                _s.Edit("Edit relationship", () =>
                {
                    var t = _s.Model.Relationships.First(x => x.Id == r.Id);
                    t.IsActive = active.IsChecked == true;
                    t.Cardinality = (Cardinality)card.SelectedItem;
                });
                w.Close();
            }));
            buttons.Children.Add(Ui.Button("Delete", () =>
            {
                _s.Edit("Delete relationship", () => _s.Model.Relationships.RemoveAll(x => x.Id == r.Id));
                w.Close();
            }));
            buttons.Children.Add(Ui.Button("Cancel", w.Close));
            p.Children.Add(buttons);
            w.Content = p;
            w.ShowDialog();
        }
    }
}
