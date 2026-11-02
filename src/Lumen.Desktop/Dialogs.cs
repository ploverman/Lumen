using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.CodeCompletion;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;
using ICSharpCode.AvalonEdit.Rendering;
using Lumen.Connectors;
using Lumen.Engine;
using Lumen.Model;
using Lumen.Reporting;

namespace Lumen.Desktop
{
    /// <summary>Small modal dialogs for a single answer. All return null on Cancel.</summary>
    public static class Prompt
    {
        /// <summary>The common window: owned, centered, sized to its content.</summary>
        private static Window Shell(string title, UIElement content, int width = 400)
        {
            var w = new Window
            {
                Title = title, Width = width, SizeToContent = SizeToContent.Height,
                Owner = Application.Current.MainWindow, WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize, Content = content
            };
            w.Bg("PanelBg");
            return w;
        }

        /// <summary>Asks for one line of text.</summary>
        public static string Text(string title, string label, string initial = "")
        {
            var box = new TextBox { Text = initial };
            string result = null;
            var p = new StackPanel { Margin = new Thickness(14) };
            p.Children.Add(Ui.Label(label));
            p.Children.Add(box);
            var w = Shell(title, p);
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
            var ok = Ui.Button("OK", () => { result = box.Text; w.Close(); });
            ok.IsDefault = true;
            var cancel = Ui.Button("Cancel", w.Close);
            cancel.IsCancel = true;
            row.Children.Add(ok);
            row.Children.Add(cancel);
            p.Children.Add(row);
            box.Focus();
            box.SelectAll();
            w.ShowDialog();
            return result;
        }

        /// <summary>Asks for one of a list.</summary>
        public static string Choice(string title, string label, IEnumerable<string> options, string initial = null)
        {
            var list = options.ToList();
            var combo = new ComboBox { ItemsSource = list, SelectedItem = initial ?? list.FirstOrDefault() };
            string result = null;
            var p = new StackPanel { Margin = new Thickness(14) };
            p.Children.Add(Ui.Label(label));
            p.Children.Add(combo);
            var w = Shell(title, p);
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
            var ok = Ui.Button("OK", () => { result = combo.SelectedItem as string; w.Close(); });
            ok.IsDefault = true;
            var cancel = Ui.Button("Cancel", w.Close);
            cancel.IsCancel = true;
            row.Children.Add(ok);
            row.Children.Add(cancel);
            p.Children.Add(row);
            w.ShowDialog();
            return result;
        }

        /// <summary>Asks for any number of a list.</summary>
        public static List<string> Multi(string title, string label, IEnumerable<string> options)
        {
            var p = new StackPanel { Margin = new Thickness(14) };
            p.Children.Add(Ui.Label(label));
            var panel = new StackPanel();
            var boxes = options.Select(o => new CheckBox { Content = o, Margin = new Thickness(0, 2, 0, 2) }.Fg("ChromeFg")).ToList();
            boxes.ForEach(b => panel.Children.Add(b));
            p.Children.Add(new ScrollViewer { Content = panel, MaxHeight = 320, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            List<string> result = null;
            var w = Shell(title, p);
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
            row.Children.Add(Ui.Button("OK", () => { result = boxes.Where(b => b.IsChecked == true).Select(b => (string)b.Content).ToList(); w.Close(); }));
            row.Children.Add(Ui.Button("Cancel", w.Close));
            p.Children.Add(row);
            w.ShowDialog();
            return result;
        }

        /// <summary>Shows <see cref="SqlDialog"/>.</summary>
        public static DataSourceRef Sql()
        {
            var w = new SqlDialog();
            w.ShowDialog();
            return w.Result;
        }
    }

    /// <summary>Server, database and query. The database list follows the server name as it is typed.
    /// The list is editable, so a database the login cannot enumerate can still be named by hand.</summary>
    public sealed class SqlDialog : Window
    {
        private readonly TextBox _server = new TextBox { Text = "localhost" };
        private readonly ComboBox _database = new ComboBox { IsEditable = true };
        private readonly TextBlock _status = new TextBlock { FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) };
        private readonly TextBox _query = new TextBox { AcceptsReturn = true, Height = 90, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        // Debounce: the server is contacted only after 500 ms with no typing.
        private readonly DispatcherTimer _typing = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        private CancellationTokenSource _listing;
        // The server name last listed, so that the timer and lost focus do not list the same server twice.
        private string _listedFor;

        /// <summary>The chosen source, or null if cancelled.</summary>
        public DataSourceRef Result { get; private set; }
        /// <summary>The current listing, which the self-check awaits.</summary>
        internal Task Listing { get; private set; } = Task.CompletedTask;
        /// <summary>Items in the list, for the self-check.</summary>
        internal int DatabaseCount => _database.Items.Count;
        /// <summary>Status text, for the self-check.</summary>
        internal string Status => _status.Text;
        /// <summary>Picks a database and presses OK, for the self-check.</summary>
        internal void PickAndCommit(int index) { _database.SelectedIndex = index; Commit(); }

        /// <summary>Builds the dialog and starts listing the default server.</summary>
        public SqlDialog()
        {
            Title = "SQL Server";
            Width = 460;
            SizeToContent = SizeToContent.Height;
            Owner = Application.Current.MainWindow;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;
            this.Bg("PanelBg");
            _status.Fg("ChromeFg2");

            var p = new StackPanel { Margin = new Thickness(14) };
            p.Children.Add(Ui.Label("Server"));
            p.Children.Add(_server);
            p.Children.Add(Ui.Label("Database"));
            p.Children.Add(_database);
            p.Children.Add(_status);
            p.Children.Add(Ui.Label("Table name, or a SELECT statement"));
            p.Children.Add(_query);
            p.Children.Add(Ui.Label("Connects with Windows Integrated Security."));
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
            row.Children.Add(Ui.Button("OK", Commit));
            row.Children.Add(Ui.Button("Cancel", Close));
            p.Children.Add(row);
            Content = p;

            _typing.Tick += (s, e) => { _typing.Stop(); Listing = ListDatabases(); };
            _server.TextChanged += (s, e) => { _typing.Stop(); _typing.Start(); };
            _server.LostFocus += (s, e) => { _typing.Stop(); Listing = ListDatabases(); };
            Loaded += (s, e) => { Listing = ListDatabases(); _server.Focus(); _server.SelectAll(); };
            Closed += (s, e) => { _typing.Stop(); _listing?.Cancel(); };
        }

        /// <summary>Sets the server and lists at once, for the self-check.</summary>
        internal void SetServer(string name)
        {
            _server.Text = name;
            _typing.Stop();
            Listing = ListDatabases();
        }

        /// <summary>Lists databases on the named server. A newer request cancels the older one, so a slow failure cannot overwrite a newer answer.</summary>
        private async Task ListDatabases()
        {
            string name = _server.Text.Trim();
            if (name == _listedFor) return;
            _listedFor = name;
            _listing?.Cancel();
            _database.ItemsSource = null;
            if (name.Length == 0) { _status.Text = ""; return; }

            var cts = new CancellationTokenSource();
            _listing = cts;
            _status.Text = "Looking for databases on " + name + "...";
            try
            {
                var names = await SqlConnector.DatabasesAsync(name, cts.Token);
                if (cts.IsCancellationRequested) return;
                // Setting ItemsSource clears an editable combo's text, so whatever was typed is put back.
                string typed = _database.Text;
                _database.ItemsSource = names;
                _database.Text = typed;
                _status.Text = names.Count == 1 ? "1 database" : names.Count + " databases";
            }
            catch (Exception ex) when (ex is System.Data.SqlClient.SqlException || ex is InvalidOperationException || ex is ArgumentException)
            {
                if (cts.IsCancellationRequested) return;
                // Forget the failed server, so that leaving the box tries again.
                _listedFor = null;
                _status.Text = "Could not list databases: " + ex.Message + " You can still type a name.";
            }
        }

        /// <summary>Builds the source. The string carries Windows authentication and no password, and it is saved in the report file.</summary>
        private void Commit()
        {
            var b = new System.Data.SqlClient.SqlConnectionStringBuilder { DataSource = _server.Text.Trim(), InitialCatalog = _database.Text.Trim(), IntegratedSecurity = true };
            Result = new DataSourceRef { Kind = "sql", ConnectionString = b.ConnectionString, Query = _query.Text };
            Close();
        }
    }

    /// <summary>Power Query equivalent: preview grid plus an Applied Steps list. Selecting a step shows the data as of that step.</summary>
    public sealed class TransformWindow : Window
    {
        private readonly string _table;
        private readonly DataSourceRef _source;
        private readonly List<TransformStepDef> _steps;
        private readonly ListBox _list = new ListBox { Width = 230 };
        private readonly DataGrid _grid = new DataGrid { AutoGenerateColumns = false, IsReadOnly = true, EnableRowVirtualization = true, EnableColumnVirtualization = true, CanUserSortColumns = false, SelectionUnit = DataGridSelectionUnit.Cell };
        private readonly TextBlock _status = new TextBlock { Margin = new Thickness(8, 4, 8, 4) };
        private ColumnBatch _sourceBatch;
        private ColumnBatch _preview;
        private CancellationTokenSource _cts;
        private bool _rebuilding;

        /// <summary>The full result of all steps, set on Close and apply.</summary>
        public ColumnBatch Result { get; private set; }
        /// <summary>The edited steps.</summary>
        public List<TransformStepDef> Steps => _steps;

        /// <summary>Works on a copy of the steps, so that Cancel loses nothing. The source loads once on open; each preview replays the steps from that batch.</summary>
        public TransformWindow(string tableName, DataSourceRef source, IEnumerable<TransformStepDef> steps)
        {
            _table = tableName;
            _source = source;
            _steps = steps.Select(ModelJson.DeepClone).ToList();
            Title = "Transform data: " + tableName;
            Width = 1100; Height = 680;
            Owner = Application.Current.MainWindow;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            this.Bg("PanelBg");
            _status.Fg("ChromeFg2");

            var actions = new WrapPanel { Margin = new Thickness(8, 6, 8, 6) };
            actions.Children.Add(Ui.Button("Use first row as headers", () => Add(StepKinds.Create(StepKinds.PromoteHeaders, "Promoted headers"))));
            actions.Children.Add(Ui.Button("Change type", ChangeType));
            actions.Children.Add(Ui.Button("Rename column", Rename));
            actions.Children.Add(Ui.Button("Remove columns", RemoveColumns));
            actions.Children.Add(Ui.Button("Filter rows", FilterRows));
            actions.Children.Add(Ui.Button("Replace values", ReplaceValues));
            actions.Children.Add(Ui.Button("Split column", SplitColumn));
            actions.Children.Add(Ui.Button("Add custom column", AddCustom));
            actions.Children.Add(Ui.Button("Remove duplicates", () => Add(StepKinds.Create(StepKinds.RemoveDuplicates, "Removed duplicates"))));
            actions.Children.Add(Ui.Button("Unpivot columns", Unpivot));

            var right = new DockPanel { Margin = new Thickness(4, 0, 8, 0) };
            var head = Ui.Heading("Applied steps");
            DockPanel.SetDock(head, Dock.Top);
            right.Children.Add(head);
            var stepButtons = new StackPanel { Orientation = Orientation.Horizontal };
            stepButtons.Children.Add(Ui.Button("Delete", DeleteStep));
            stepButtons.Children.Add(Ui.Button("Up", () => MoveStep(-1)));
            stepButtons.Children.Add(Ui.Button("Down", () => MoveStep(1)));
            DockPanel.SetDock(stepButtons, Dock.Bottom);
            right.Children.Add(stepButtons);
            right.Children.Add(_list);
            DockPanel.SetDock(right, Dock.Right);

            var bottom = new DockPanel();
            var ok = Ui.Button("Close & apply", Commit);
            var cancel = Ui.Button("Cancel", Close);
            DockPanel.SetDock(ok, Dock.Right);
            DockPanel.SetDock(cancel, Dock.Right);
            bottom.Children.Add(ok);
            bottom.Children.Add(cancel);
            bottom.Children.Add(_status);

            var d = new DockPanel();
            DockPanel.SetDock(actions, Dock.Top);
            DockPanel.SetDock(bottom, Dock.Bottom);
            d.Children.Add(actions);
            d.Children.Add(bottom);
            d.Children.Add(right);
            d.Children.Add(_grid);
            Content = d;

            _list.SelectionChanged += (s, e) => { if (!_rebuilding) ShowPreview(); };
            Loaded += async (s, e) =>
            {
                _status.Text = "Loading the source...";
                try
                {
                    _sourceBatch = await Task.Run(() => TableLoader.ReadSource(_source, CancellationToken.None));
                    RebuildList(_steps.Count);
                }
                catch (Exception ex) when (!(ex is OutOfMemoryException))
                {
                    _status.Text = "Could not read the source: " + ex.Message;
                }
            };
        }

        /// <summary>Refills the step list. Index 0 is the source, so step n sits at index n + 1.</summary>
        private void RebuildList(int select)
        {
            _rebuilding = true;
            _list.Items.Clear();
            _list.Items.Add("Source");
            foreach (var s in _steps) _list.Items.Add(s.Label ?? s.Kind);
            _list.SelectedIndex = select;
            _rebuilding = false;
            ShowPreview();
        }

        /// <summary>Replays the steps up to the selected one on a worker and shows the first 1,000 rows. Selecting again cancels the run in progress.
        /// A failing step shows its message and stays in the list.</summary>
        private async void ShowPreview()
        {
            if (_sourceBatch == null) return;
            _cts?.Cancel();
            var cts = new CancellationTokenSource();
            _cts = cts;
            int upTo = Math.Max(0, _list.SelectedIndex);
            var steps = _steps.Take(upTo).ToList();
            var source = _sourceBatch;
            try
            {
                _status.Text = "Working...";
                var b = await Task.Run(() => TableLoader.ApplySteps(source, steps, cts.Token), cts.Token);
                _preview = b;
                int rows = Math.Min(1000, b.RowCount);
                VirtualRows.Fill(_grid, b.Columns, null, rows);
                _status.Text = $"{b.Columns.Count} columns, {b.RowCount:#,0} rows; previewing the first {rows:#,0}";
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) when (ex is InvalidOperationException || ex is FormatException || ex is ArgumentException)
            {
                _status.Text = "⚠ This step failed: " + ex.Message;
            }
        }

        /// <summary>Inserts a step after the selected one and selects it.</summary>
        private void Add(TransformStepDef step)
        {
            int at = Math.Max(0, _list.SelectedIndex);
            _steps.Insert(at, step);
            RebuildList(at + 1);
        }

        /// <summary>Deletes the selected step. "Source" cannot be deleted.</summary>
        private void DeleteStep()
        {
            int at = _list.SelectedIndex;
            if (at <= 0) return;
            _steps.RemoveAt(at - 1);
            RebuildList(at - 1);
        }

        /// <summary>Moves the selected step up or down.</summary>
        private void MoveStep(int d)
        {
            int at = _list.SelectedIndex - 1;
            int to = at + d;
            if (at < 0 || to < 0 || to >= _steps.Count) return;
            var s = _steps[at];
            _steps.RemoveAt(at);
            _steps.Insert(to, s);
            RebuildList(to + 1);
        }

        /// <summary>Column names as of the selected step.</summary>
        private List<string> Names => _preview?.Columns.Select(c => c.Name).ToList() ?? new List<string>();

        /// <summary>Asks for a column, starting at the current grid column.</summary>
        private string PickColumn(string title)
        {
            int i = _grid.CurrentColumn?.DisplayIndex ?? -1;
            return Prompt.Choice(title, "Column", Names, i >= 0 && i < Names.Count ? Names[i] : null);
        }

        /// <summary>Adds a ChangeType step.</summary>
        private void ChangeType()
        {
            var c = PickColumn("Change type");
            if (c == null) return;
            var t = Prompt.Choice("Change type", "Data type", Enum.GetNames(typeof(LumenDataType)));
            if (t != null) Add(StepKinds.Create(StepKinds.ChangeType, "Changed type: " + c, "column", c, "type", t));
        }

        /// <summary>Adds a Rename step.</summary>
        private void Rename()
        {
            var c = PickColumn("Rename column");
            if (c == null) return;
            var n = Prompt.Text("Rename column", "New name", c);
            if (!string.IsNullOrWhiteSpace(n)) Add(StepKinds.Create(StepKinds.Rename, "Renamed: " + c, "column", c, "name", n));
        }

        /// <summary>Adds a RemoveColumns step. Multiple names are stored joined with |.</summary>
        private void RemoveColumns()
        {
            var list = Prompt.Multi("Remove columns", "Columns to remove", Names);
            if (list != null && list.Count > 0)
                Add(StepKinds.Create(StepKinds.RemoveColumns, "Removed columns", "columns", string.Join("|", list)));
        }

        /// <summary>Adds a FilterRows step. Blank tests take no value.</summary>
        private void FilterRows()
        {
            var c = PickColumn("Filter rows");
            if (c == null) return;
            var op = Prompt.Choice("Filter rows", "Keep rows where the value", Enum.GetNames(typeof(ConditionOp)));
            if (op == null) return;
            string v = "";
            if (op != "IsBlank" && op != "IsNotBlank")
            {
                v = Prompt.Text("Filter rows", "Value");
                if (v == null) return;
            }
            Add(StepKinds.Create(StepKinds.FilterRows, "Filtered rows: " + c, "column", c, "op", op, "value", v));
        }

        /// <summary>Adds a ReplaceValues step.</summary>
        private void ReplaceValues()
        {
            var c = PickColumn("Replace values");
            if (c == null) return;
            var f = Prompt.Text("Replace values", "Value to find");
            if (f == null) return;
            var r = Prompt.Text("Replace values", "Replace with");
            if (r != null) Add(StepKinds.Create(StepKinds.ReplaceValues, "Replaced values: " + c, "column", c, "find", f, "replace", r));
        }

        /// <summary>Adds a SplitColumn step.</summary>
        private void SplitColumn()
        {
            var c = PickColumn("Split column");
            if (c == null) return;
            var d = Prompt.Text("Split column", "Delimiter", ",");
            if (!string.IsNullOrEmpty(d)) Add(StepKinds.Create(StepKinds.SplitColumn, "Split: " + c, "column", c, "delimiter", d));
        }

        /// <summary>Adds an AddCustomColumn step, whose LQL expression sees the row in progress as a table with this table's name.</summary>
        private void AddCustom()
        {
            var n = Prompt.Text("Add custom column", "Column name", "Custom");
            if (string.IsNullOrWhiteSpace(n)) return;
            var e = Prompt.Text("Add custom column", $"LQL row expression, for example {_table}[Qty] * 2");
            if (!string.IsNullOrWhiteSpace(e))
                Add(StepKinds.Create(StepKinds.AddCustomColumn, "Added column: " + n, "name", n, "table", _table, "expression", e));
        }

        /// <summary>Adds an Unpivot step with fixed Attribute and Value names.</summary>
        private void Unpivot()
        {
            var list = Prompt.Multi("Unpivot columns", "Columns to unpivot", Names);
            if (list != null && list.Count > 0)
                Add(StepKinds.Create(StepKinds.Unpivot, "Unpivoted columns", "columns", string.Join("|", list), "attribute", "Attribute", "value", "Value"));
        }

        /// <summary>Applies all steps to the full source and closes with success.</summary>
        private async void Commit()
        {
            if (_sourceBatch == null) return;
            try
            {
                _status.Text = "Applying...";
                var source = _sourceBatch;
                var steps = _steps.ToList();
                Result = await Task.Run(() => TableLoader.ApplySteps(source, steps, CancellationToken.None));
                DialogResult = true;
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is FormatException || ex is ArgumentException)
            {
                _status.Text = "⚠ A step failed: " + ex.Message;
            }
        }
    }

    /// <summary>Red squiggles under LQL diagnostics.</summary>
    internal sealed class SquiggleRenderer : IBackgroundRenderer
    {
        /// <summary>The problem to mark, or null.</summary>
        public LqlDiagnostic Diagnostic;
        /// <inheritdoc/>
        public KnownLayer Layer => KnownLayer.Selection;

        /// <summary>Draws a zigzag under the diagnostic's span. A zero-length error at the end of text marks the previous character.</summary>
        public void Draw(TextView view, DrawingContext dc)
        {
            var d = Diagnostic;
            if (d == null || view.Document == null) return;
            view.EnsureVisualLines();
            int start = Math.Min(d.Offset, view.Document.TextLength);
            int len = Math.Min(d.Length, view.Document.TextLength - start);
            if (len <= 0) { start = Math.Max(0, start - 1); len = Math.Min(1, view.Document.TextLength - start); }
            if (len <= 0) return;
            var pen = new Pen(Brushes.Red, 1);
            pen.Freeze();
            foreach (var r in BackgroundGeometryBuilder.GetRectsForSegment(view, new TextSegment { StartOffset = start, Length = len }))
            {
                var g = new StreamGeometry();
                using (var c = g.Open())
                {
                    double x = r.Left, y = r.Bottom - 1;
                    c.BeginFigure(new Point(x, y), false, false);
                    bool up = true;
                    while (x < r.Right)
                    {
                        x += 3;
                        c.LineTo(new Point(x, up ? y - 2 : y), true, false);
                        up = !up;
                    }
                }
                g.Freeze();
                dc.DrawGeometry(null, pen, g);
            }
        }
    }

    /// <summary>One completion item. The inserted text can differ from the shown text, for example a closing bracket.</summary>
    internal sealed class Completion : ICompletionData
    {
        private readonly string _insert;
        /// <summary>Creates an item.</summary>
        public Completion(string text, string insert, string description) { Text = text; _insert = insert; Description = description; }
        /// <inheritdoc/>
        public ImageSource Image => null;
        /// <inheritdoc/>
        public string Text { get; }
        /// <inheritdoc/>
        public object Content => Text;
        /// <inheritdoc/>
        public object Description { get; }
        /// <inheritdoc/>
        public double Priority => 0;
        /// <inheritdoc/>
        public void Complete(TextArea area, ISegment segment, EventArgs e) => area.Document.Replace(segment, _insert);
    }

    /// <summary>LQL editor: highlighting, completion on [ ' and letters, and a 300 ms background check.</summary>
    public sealed class MeasureWindow : Window
    {
        /// <summary>Names offered by completion. A subset of what the language accepts; see the function table in LqlCompiler for all of them.</summary>
        private static readonly string[] Functions =
        {
            "SUM(", "AVERAGE(", "MIN(", "MAX(", "COUNT(", "COUNTROWS(", "DISTINCTCOUNT(", "SUMX(", "AVERAGEX(", "MINX(", "MAXX(", "COUNTX(",
            "DIVIDE(", "IF(", "SWITCH(", "CALCULATE(", "ALL(", "FILTER(", "VALUES(", "TOTALYTD(", "SAMEPERIODLASTYEAR(", "DATESYTD(",
            "RELATED(", "AND(", "OR(", "NOT(", "BLANK()", "ISBLANK(", "TRUE()", "FALSE()", "ABS(", "ROUND(", "YEAR(", "MONTH(", "DAY(", "DATE(",
            "TODAY()", "FORMAT(", "SELECTEDVALUE(", "HASONEVALUE("
        };
        // Parsed once from the embedded Lql.xshd.
        private static IHighlightingDefinition _lql;

        private readonly Session _s;
        // White in every theme: the syntax colors in Lql.xshd are chosen for a light background.
        private readonly TextEditor _editor = new TextEditor { FontFamily = new FontFamily("Consolas"), FontSize = 14, ShowLineNumbers = true, Foreground = Brushes.Black, Background = Brushes.White };
        private readonly SquiggleRenderer _squiggle = new SquiggleRenderer();
        private readonly TextBox _name = new TextBox();
        private readonly TextBox _format = new TextBox();
        private readonly ComboBox _home = new ComboBox { DisplayMemberPath = "Name" };
        private readonly TextBlock _errors = new TextBlock { Foreground = Brushes.IndianRed, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
        private readonly DispatcherTimer _check = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        private CompletionWindow _completion;
        private readonly MeasureDef _edit;

        /// <summary>Opens for a new measure when <paramref name="existing"/> is null, and otherwise for that measure.</summary>
        public MeasureWindow(Session s, MeasureDef existing, string homeTable)
        {
            _s = s;
            _edit = existing;
            Title = existing == null ? "New measure" : "Edit measure: " + existing.Name;
            Width = 720; Height = 460;
            Owner = Application.Current.MainWindow;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            this.Bg("PanelBg");

            _editor.SyntaxHighlighting = Highlighting();
            _editor.TextArea.TextView.BackgroundRenderers.Add(_squiggle);
            _editor.Text = existing?.Expression ?? "";
            _name.Text = existing?.Name ?? "Measure";
            _format.Text = existing?.FormatString ?? "#,0";
            _home.ItemsSource = s.Model.Tables;
            _home.SelectedItem = s.Model.Table(existing?.HomeTableId ?? homeTable) ?? s.Model.Tables.FirstOrDefault();

            var top = new Grid { Margin = new Thickness(12, 10, 12, 6) };
            for (int i = 0; i < 3; i++) top.ColumnDefinitions.Add(new ColumnDefinition());
            Action<string, UIElement, int> cell = (label, e, col) =>
            {
                var p = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
                p.Children.Add(Ui.Label(label));
                p.Children.Add(e);
                Grid.SetColumn(p, col);
                top.Children.Add(p);
            };
            cell("Name", _name, 0);
            cell("Format string", _format, 1);
            cell("Home table", _home, 2);

            var bottom = new DockPanel { Margin = new Thickness(12, 6, 12, 10) };
            var ok = Ui.Button("OK", Commit);
            var cancel = Ui.Button("Cancel", Close);
            DockPanel.SetDock(ok, Dock.Right);
            DockPanel.SetDock(cancel, Dock.Right);
            bottom.Children.Add(ok);
            bottom.Children.Add(cancel);
            bottom.Children.Add(_errors);

            var d = new DockPanel();
            DockPanel.SetDock(top, Dock.Top);
            DockPanel.SetDock(bottom, Dock.Bottom);
            d.Children.Add(top);
            d.Children.Add(bottom);
            var frame = new Border { BorderThickness = new Thickness(1), Margin = new Thickness(12, 0, 12, 0), Background = Brushes.White, Child = _editor };
            frame.SetResourceReference(Border.BorderBrushProperty, "Line");
            d.Children.Add(frame);
            Content = d;

            _check.Tick += (x, e) => { _check.Stop(); Check(); };
            _editor.TextChanged += (x, e) => { _check.Stop(); _check.Start(); };
            _editor.TextArea.TextEntered += OnTextEntered;
            Loaded += (x, e) => { _editor.Focus(); Check(); };
        }

        /// <summary>Loads the syntax definition.</summary>
        private static IHighlightingDefinition Highlighting()
        {
            if (_lql != null) return _lql;
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("Lumen.Desktop.Lql.xshd"))
            using (var r = XmlReader.Create(s))
                _lql = HighlightingLoader.Load(r, HighlightingManager.Instance);
            return _lql;
        }

        /// <summary>Sets the text and checks it, for the self-check.</summary>
        internal void SetExpression(string text) { _editor.Text = text; Check(); }

        /// <summary>Validates against the live dataset, then updates the squiggle and message. Only the first problem is shown. A semantic error with no position shows text only.</summary>
        private IReadOnlyList<LqlDiagnostic> Check()
        {
            var ds = _s.Dataset;
            var list = string.IsNullOrWhiteSpace(_editor.Text)
                ? new[] { new LqlDiagnostic { Message = "Enter an expression" } }
                : LqlCompiler.Validate(ds, _editor.Text);
            _squiggle.Diagnostic = list.Count > 0 && list[0].Length > 0 ? list[0] : null;
            _editor.TextArea.TextView.InvalidateLayer(KnownLayer.Selection);
            _errors.Text = list.Count > 0 ? list[0].ToString() : "";
            return list;
        }

        /// <summary>Opens completion: after [ the columns of the table before it, or measures if there is none; after ' the tables; at the start of a word, functions and tables.</summary>
        private void OnTextEntered(object sender, TextCompositionEventArgs e)
        {
            char c = e.Text[0];
            if (_completion != null) return;
            var doc = _editor.Document;
            int caret = _editor.CaretOffset;
            var items = new List<Completion>();
            int start = caret;

            if (c == '[')
            {
                // After a table name: its columns. Alone: measures.
                int p = caret - 2;
                string table = null;
                if (p >= 0)
                {
                    int e2 = p;
                    if (doc.GetCharAt(e2) == '\'')
                    {
                        int b = e2 - 1;
                        while (b >= 0 && doc.GetCharAt(b) != '\'') b--;
                        if (b >= 0) table = doc.GetText(b + 1, e2 - b - 1);
                    }
                    else
                    {
                        int b = e2;
                        while (b >= 0 && (char.IsLetterOrDigit(doc.GetCharAt(b)) || doc.GetCharAt(b) == '_')) b--;
                        if (b < e2) table = doc.GetText(b + 1, e2 - b);
                    }
                }
                var def = table == null ? null : _s.Model.TableByName(table);
                if (def != null) items.AddRange(def.Columns.Select(col => new Completion(col.Name, col.Name + "]", col.DataType.ToString())));
                else items.AddRange(_s.Model.Measures.Select(m => new Completion(m.Name, m.Name + "]", m.Expression)));
            }
            else if (c == '\'')
            {
                items.AddRange(_s.Model.Tables.Select(t => new Completion(t.Name, t.Name + "'", "Table")));
            }
            else if (char.IsLetter(c))
            {
                if (caret >= 2 && char.IsLetterOrDigit(doc.GetCharAt(caret - 2))) return;
                start = caret - 1;
                items.AddRange(Functions.Select(f => new Completion(f, f, "Function")));
                items.AddRange(_s.Model.Tables.Select(t => new Completion(t.Name, t.Name.Contains(' ') ? "'" + t.Name + "'" : t.Name, "Table")));
            }
            if (items.Count == 0) return;

            _completion = new CompletionWindow(_editor.TextArea);
            _completion.StartOffset = start;
            foreach (var i in items.OrderBy(x => x.Text)) _completion.CompletionList.CompletionData.Add(i);
            _completion.Closed += (s, a) => _completion = null;
            if (c != '[' && c != '\'') _completion.CompletionList.SelectItem(c.ToString());
            _completion.Show();
        }

        /// <summary>Checks the name and expression, then saves as one undoable edit. A measure may not share a name with another measure, or with a column in its home table.</summary>
        private void Commit()
        {
            var name = _name.Text.Trim();
            if (name.Length == 0) { _errors.Text = "A measure needs a name"; return; }
            if (_s.Model.Measures.Any(m => m != _edit && string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase))
                || (_home.SelectedItem as TableDef)?.ColumnByName(name) != null)
            {
                _errors.Text = $"The name '{name}' is already in use";
                return;
            }
            if (Check().Count > 0) return;
            var home = (_home.SelectedItem as TableDef)?.Id;
            _s.Edit(_edit == null ? "New measure" : "Edit measure", () =>
            {
                var m = _edit == null ? new MeasureDef { Id = Guid.NewGuid().ToString("N") } : _s.Model.Measure(_edit.Id);
                if (_edit == null) _s.Model.Measures.Add(m);
                m.Name = name;
                m.Expression = _editor.Text;
                m.FormatString = _format.Text.Length == 0 ? null : _format.Text;
                m.HomeTableId = home;
            });
            DialogResult = true;
        }
    }
}
