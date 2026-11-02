using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Controls.Ribbon;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Lumen.Charting;
using Lumen.Connectors;
using Lumen.Engine;
using Lumen.Model;
using Lumen.Reporting;
using Lumen.Visuals;
using Microsoft.Win32;

namespace Lumen.Desktop
{
    /// <summary>The shell: ribbon, left rail, canvas and panes, as Power BI Desktop lays them out.</summary>
    public sealed class MainWindow : Window
    {
        private readonly Session _s = new Session();
        private readonly ReportCanvas _canvas;
        private readonly FieldsPane _fields;
        private readonly VisualizationsPane _viz;
        private readonly FiltersPane _filters;
        private readonly SelectionPane _selection;
        private readonly PerformancePane _perf = new PerformancePane();
        private readonly DataView _data;
        private readonly ModelView _model;

        private readonly ContentControl _center = new ContentControl();
        private readonly Grid _reportView = new Grid();
        private readonly StackPanel _pageTabs = new StackPanel { Orientation = Orientation.Horizontal };
        private readonly TextBlock _status = new TextBlock { Margin = new Thickness(10, 3, 10, 3), FontSize = 12 };
        private readonly Slider _zoomSlider = new Slider { Width = 120, Minimum = 0.3, Maximum = 2, Value = 1, VerticalAlignment = VerticalAlignment.Center };
        private readonly Ribbon _ribbon = new Ribbon();
        private readonly ColumnDefinition _filterColumn = new ColumnDefinition { Width = new GridLength(210) };
        private readonly ColumnDefinition _vizColumn = new ColumnDefinition { Width = new GridLength(240) };
        private readonly ColumnDefinition _fieldColumn = new ColumnDefinition { Width = new GridLength(210) };
        private readonly Grid _paneHost = new Grid();
        private RibbonButton _undo, _redo;
        private RibbonToggleButton _interactions;
        private TabControl _leftTabs;
        private readonly DispatcherTimer _autosave = new DispatcherTimer { Interval = TimeSpan.FromMinutes(2) };

        /// <summary>Autosave location, %LOCALAPPDATA%\Lumen\Recovery\autosave.lumen. If this file exists at startup, the last run did not end normally.</summary>
        private static string RecoveryPath => System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lumen", "Recovery", "autosave.lumen");

        /// <summary>Builds the shell and starts on an empty report.</summary>
        public MainWindow()
        {
            Title = "Lumen";
            Width = Math.Min(1680, SystemParameters.WorkArea.Width);
            Height = Math.Min(960, SystemParameters.WorkArea.Height);
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            FontFamily = new FontFamily("Segoe UI");
            ThemeService.Apply(_s.Theme);
            SetResourceReference(BackgroundProperty, "ChromeBg");
            SetResourceReference(ForegroundProperty, "ChromeFg");

            _canvas = new ReportCanvas(_s);
            _fields = new FieldsPane(_s, this);
            _viz = new VisualizationsPane(_s, this);
            _filters = new FiltersPane(_s);
            _selection = new SelectionPane(_s);
            _data = new DataView(_s, this);
            _model = new ModelView(_s);

            BuildRibbon();
            BuildLayout();
            BindKeys();

            _s.Changed += OnChanged;
            _s.ThemeChanged += () => { _canvas.Sync(true); _model.Rebuild(); };
            _canvas.SelectionChanged += RefreshPanes;
            _canvas.PerfUpdated += _perf.Show;
            _zoomSlider.ValueChanged += (x, e) => _canvas.Zoom = _zoomSlider.Value;

            _autosave.Tick += (x, e) => Autosave();
            _autosave.Start();
            Closing += OnClosing;

            NewReport();
        }

        // ---------------------------------------------------------- layout

        /// <summary>Builds the rail, the report view, the side panes and the status bar. The report view stays alive when another view is shown.</summary>
        private void BuildLayout()
        {
            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition());
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            root.Children.Add(_ribbon);

            var body = new Grid();
            Grid.SetRow(body, 1);
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
            body.ColumnDefinitions.Add(new ColumnDefinition());
            root.Children.Add(body);

            var rail = new StackPanel().Bg("ChromeBg");
            _railButtons = new List<ToggleButton>();
            Action<string, string, int> railButton = (glyph, tip, index) =>
            {
                var b = new ToggleButton
                {
                    Content = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 18,
                    Height = 44, ToolTip = tip, BorderThickness = new Thickness(0), Background = Brushes.Transparent
                }.Fg("ChromeFg");
                b.Click += (x, e) => ShowView(index);
                _railButtons.Add(b);
                rail.Children.Add(b);
            };
            railButton("", "Report view", 0);
            railButton("", "Data view", 1);
            railButton("", "Model view", 2);
            body.Children.Add(rail);

            // Report view: canvas with page tabs, then the three panes.
            _reportView.ColumnDefinitions.Add(new ColumnDefinition());
            _reportView.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _reportView.ColumnDefinitions.Add(_filterColumn);
            _reportView.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _reportView.ColumnDefinitions.Add(_vizColumn);
            _reportView.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _reportView.ColumnDefinitions.Add(_fieldColumn);

            var left = new DockPanel().Bg("CanvasBg");
            var tabs = new DockPanel().Bg("ChromeBg");
            var add = new Button { Content = "+", Width = 28, Margin = new Thickness(4, 3, 2, 3), ToolTip = "New page" };
            add.Click += (x, e) => NewPage();
            var zoomRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 8, 0) };
            zoomRow.Children.Add(Ui.Button("Fit", FitZoom));
            zoomRow.Children.Add(_zoomSlider);
            DockPanel.SetDock(zoomRow, Dock.Right);
            DockPanel.SetDock(add, Dock.Left);
            tabs.Children.Add(zoomRow);
            var tabScroll = new ScrollViewer { Content = _pageTabs, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
            tabs.Children.Add(tabScroll);
            DockPanel.SetDock(tabs, Dock.Bottom);
            left.Children.Add(tabs);
            left.Children.Add(_canvas);
            _reportView.Children.Add(left);

            Action<int> splitter = col =>
            {
                var g = new GridSplitter { Width = 3, ResizeBehavior = GridResizeBehavior.PreviousAndNext, VerticalAlignment = VerticalAlignment.Stretch, HorizontalAlignment = HorizontalAlignment.Center };
                g.SetResourceReference(BackgroundProperty, "Line");
                Grid.SetColumn(g, col);
                _reportView.Children.Add(g);
            };
            splitter(1); splitter(3); splitter(5);

            _leftTabs = new TabControl { BorderThickness = new Thickness(0), Padding = new Thickness(0) };
            _leftTabs.Items.Add(new TabItem { Header = "Filters", Content = _filters });
            _leftTabs.Items.Add(new TabItem { Header = "Selection", Content = _selection });
            _leftTabs.Items.Add(new TabItem { Header = "Performance", Content = _perf });
            Grid.SetColumn(_leftTabs, 2);
            _reportView.Children.Add(_leftTabs);
            Grid.SetColumn(_viz, 4);
            _reportView.Children.Add(_viz);
            Grid.SetColumn(_fields, 6);
            _reportView.Children.Add(_fields);

            Grid.SetColumn(_center, 1);
            body.Children.Add(_center);

            var statusBar = new Border { Child = _status, BorderThickness = new Thickness(0, 1, 0, 0) }.Bg("ChromeBg");
            statusBar.SetResourceReference(Border.BorderBrushProperty, "Line");
            _status.Fg("ChromeFg2");
            Grid.SetRow(statusBar, 2);
            root.Children.Add(statusBar);

            Content = root;
            ShowView(0);
        }

        private List<ToggleButton> _railButtons;

        /// <summary>Switches between Report (0), Data (1) and Model (2).</summary>
        private void ShowView(int index)
        {
            for (int i = 0; i < _railButtons.Count; i++) _railButtons[i].IsChecked = i == index;
            if (index == 1) { _center.Content = _data; _data.Rebuild(); }
            else if (index == 2) { _center.Content = _model; _model.Rebuild(); }
            else { _center.Content = _reportView; }
        }

        /// <summary>Turns an icon font glyph into a frozen image, since ribbon buttons take images.</summary>
        private static ImageSource Icon(string glyph, double size = 32)
        {
            var text = new FormattedText(glyph, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe MDL2 Assets"), size * 0.72, new SolidColorBrush(Color.FromRgb(0x20, 0x70, 0xC0)), 1);
            var geo = text.BuildGeometry(new Point((size - text.Width) / 2, (size - text.Height) / 2));
            var group = new DrawingGroup();
            group.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, size, size))));
            group.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(0x20, 0x70, 0xC0)), null, geo));
            var image = new DrawingImage(group);
            image.Freeze();
            return image;
        }

        /// <summary>Adds a ribbon button.</summary>
        private RibbonButton Rb(RibbonGroup group, string label, string glyph, Action click)
        {
            var b = new RibbonButton { Label = label, LargeImageSource = Icon(glyph), SmallImageSource = Icon(glyph, 16) };
            b.Click += (x, e) => click();
            group.Items.Add(b);
            return b;
        }

        /// <summary>Builds the four ribbon tabs. The ribbon is a Microsoft control whose chrome does not follow the app's dark themes.</summary>
        private void BuildRibbon()
        {
            _ribbon.ApplicationMenu = new RibbonApplicationMenu { Visibility = Visibility.Collapsed };
            _ribbon.Margin = new Thickness(0, -22, 0, 0);  // the unused quick-access strip


            var home = new RibbonTab { Header = "Home" };
            var file = new RibbonGroup { Header = "File" };
            Rb(file, "New", "", NewReport);
            Rb(file, "Open", "", OpenDialog);
            Rb(file, "Save", "", () => Save(false));
            Rb(file, "Save as", "", () => Save(true));
            var data = new RibbonGroup { Header = "Data" };
            var get = new RibbonMenuButton { Label = "Get data", LargeImageSource = Icon("") };
            Action<string, Action> gi = (t, a) =>
            {
                var m = new RibbonMenuItem { Header = t };
                m.Click += (x, e) => a();
                get.Items.Add(m);
            };
            gi("Text/CSV", () => ImportFile("csv", "CSV|*.csv;*.txt;*.tsv"));
            gi("Excel workbook", () => ImportFile("excel", "Excel|*.xlsx;*.xls;*.xlsb"));
            gi("JSON", () => ImportFile("json", "JSON|*.json"));
            gi("SQL Server", ImportSql);
            gi("Sample report", LoadSample);
            gi("Warehouse sample", LoadWarehouseSample);
            data.Items.Add(get);
            Rb(data, "Transform", "", () => TransformTable(null));
            Rb(data, "Refresh", "", RefreshData);
            var insert = new RibbonGroup { Header = "Insert" };
            Rb(insert, "New page", "", NewPage);
            Rb(insert, "New measure", "", () => NewMeasure(null));
            var edit = new RibbonGroup { Header = "Edit" };
            _undo = Rb(edit, "Undo", "", _s.Stack.Undo);
            _redo = Rb(edit, "Redo", "", _s.Stack.Redo);
            Rb(edit, "Copy", "", _canvas_Copy);
            Rb(edit, "Paste", "", _canvas_Paste);
            var export = new RibbonGroup { Header = "Export" };
            Rb(export, "PNG", "", ExportPng);
            Rb(export, "PDF", "", ExportPdf);
            Rb(export, "Print", "", () => Exporter.Print(_canvas.PageSurface, _s.Doc.Name));
            home.Items.Add(file); home.Items.Add(data); home.Items.Add(insert); home.Items.Add(edit); home.Items.Add(export);

            var ins = new RibbonTab { Header = "Insert" };
            var cards = new RibbonGroup { Header = "Visuals" };
            foreach (var t in VisualRegistry.Types)
            {
                var type = t;
                var b = Rb(cards, VisualRegistry.DisplayName(t), "?", () => GalleryClicked(type));
                var drawing = new GeometryDrawing(new SolidColorBrush(Color.FromRgb(0x20, 0x70, 0xC0)), null, Ui.VisualIcons[t]);
                b.LargeImageSource = b.SmallImageSource = new DrawingImage(drawing);
            }
            ins.Items.Add(cards);

            var modeling = new RibbonTab { Header = "Modeling" };
            var m1 = new RibbonGroup { Header = "Calculations" };
            Rb(m1, "New measure", "", () => NewMeasure(null));
            var m2 = new RibbonGroup { Header = "Model" };
            Rb(m2, "Data view", "", () => ShowView(1));
            Rb(m2, "Relationships", "", () => ShowView(2));
            modeling.Items.Add(m1); modeling.Items.Add(m2);

            var view = new RibbonTab { Header = "View" };
            var themes = new RibbonGroup { Header = "Themes" };
            Rb(themes, "Light", "", () => SetTheme("Light"));
            Rb(themes, "Dark", "", () => SetTheme("Dark"));
            Rb(themes, "High contrast", "", () => SetTheme("High contrast"));
            Rb(themes, "Import theme", "", ImportTheme);
            var arrange = new RibbonGroup { Header = "Arrange" };
            foreach (var kv in new Dictionary<string, string> { { "Left", "left" }, { "Center", "hcenter" }, { "Right", "right" }, { "Top", "top" }, { "Middle", "vcenter" }, { "Bottom", "bottom" }, { "Space across", "hdist" }, { "Space down", "vdist" } })
            {
                var how = kv.Value;
                Rb(arrange, kv.Key, "", () => _canvas.Align(how));
            }
            var panes = new RibbonGroup { Header = "Show" };
            Action<string, ColumnDefinition, double> paneToggle = (label, col, width) =>
            {
                var t = new RibbonToggleButton { Label = label, LargeImageSource = Icon(""), IsChecked = true };
                t.Click += (x, e) => col.Width = new GridLength(t.IsChecked == true ? width : 0);
                panes.Items.Add(t);
            };
            paneToggle("Filters", _filterColumn, 210);
            paneToggle("Visualizations", _vizColumn, 240);
            paneToggle("Fields", _fieldColumn, 210);
            var inter = new RibbonGroup { Header = "Interactions" };
            _interactions = new RibbonToggleButton { Label = "Edit interactions", LargeImageSource = Icon("") };
            _interactions.Click += (x, e) => _canvas.SetEditInteractions(_interactions.IsChecked == true);
            inter.Items.Add(_interactions);
            view.Items.Add(themes); view.Items.Add(arrange); view.Items.Add(panes); view.Items.Add(inter);

            _ribbon.Items.Add(home);
            _ribbon.Items.Add(ins);
            _ribbon.Items.Add(modeling);
            _ribbon.Items.Add(view);
        }

        private void _canvas_Copy() => _canvas.Copy();
        private void _canvas_Paste() => _canvas.Paste();

        /// <summary>Global shortcuts. Text editors keep their keys: nothing here fires while one has focus. Canvas shortcuts work only on the report view.</summary>
        private void BindKeys()
        {
            PreviewKeyDown += (s, e) =>
            {
                if (Keyboard.FocusedElement is TextBox || Keyboard.FocusedElement is ICSharpCode.AvalonEdit.Editing.TextArea) return;
                bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
                double step = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 10 : 1;
                bool reportShown = ReferenceEquals(_center.Content, _reportView);
                if (ctrl)
                {
                    switch (e.Key)
                    {
                        case Key.Z: _s.Stack.Undo(); e.Handled = true; return;
                        case Key.Y: _s.Stack.Redo(); e.Handled = true; return;
                        case Key.S: Save(false); e.Handled = true; return;
                        case Key.O: OpenDialog(); e.Handled = true; return;
                    }
                }
                if (!reportShown) return;
                if (ctrl)
                {
                    switch (e.Key)
                    {
                        case Key.C: _canvas.Copy(); e.Handled = true; return;
                        case Key.V: _canvas.Paste(); e.Handled = true; return;
                        case Key.D: _canvas.Duplicate(); e.Handled = true; return;
                    }
                }
                switch (e.Key)
                {
                    case Key.Delete: if (_canvas.Selected.Count > 0) { _canvas.Remove(_canvas.Selected.ToList()); e.Handled = true; } break;
                    case Key.Left: if (_canvas.Selected.Count > 0) { _canvas.Nudge(-step, 0); e.Handled = true; } break;
                    case Key.Right: if (_canvas.Selected.Count > 0) { _canvas.Nudge(step, 0); e.Handled = true; } break;
                    case Key.Up: if (_canvas.Selected.Count > 0) { _canvas.Nudge(0, -step); e.Handled = true; } break;
                    case Key.Down: if (_canvas.Selected.Count > 0) { _canvas.Nudge(0, step); e.Handled = true; } break;
                    case Key.Escape: e.Handled = _canvas.ClearEscape(); break;
                }
            };
        }

        // ---------------------------------------------------------- state

        /// <summary>Reacts to any edit, undo or redo. Layout-only changes skip queries and pane rebuilds.</summary>
        private void OnChanged(bool dataAffecting)
        {
            _undo.IsEnabled = _s.Stack.CanUndo;
            _redo.IsEnabled = _s.Stack.CanRedo;
            _undo.ToolTipTitle = _s.Stack.CanUndo ? "Undo " + _s.Stack.UndoLabel : null;
            _redo.ToolTipTitle = _s.Stack.CanRedo ? "Redo " + _s.Stack.RedoLabel : null;
            if (_s.Doc.Page(_canvas.PageId) == null && _s.Doc.Pages.Count > 0) { _canvas.ShowPage(_s.Doc.Pages[0].Id); }
            else _canvas.Sync(dataAffecting);
            if (dataAffecting)
            {
                RebuildTabs();
                RefreshPanes();
                _fields.Rebuild();
                if (ReferenceEquals(_center.Content, _model)) _model.Rebuild();
                if (ReferenceEquals(_center.Content, _data)) _data.Rebuild();
            }
            UpdateTitle();
        }

        /// <summary>Updates the three panes that follow the selection.</summary>
        private void RefreshPanes()
        {
            _viz.Rebuild(_canvas.PrimarySelection);
            _filters.Rebuild(_canvas.Page, _canvas.PrimarySelection?.Def);
            _selection.Rebuild(_canvas);
        }

        /// <summary>Updates the title bar (a star means unsaved changes) and the status bar.</summary>
        private void UpdateTitle()
        {
            string name = _s.FilePath != null ? Path.GetFileNameWithoutExtension(_s.FilePath) : _s.Doc.Name;
            Title = (_s.Stack.IsDirty ? "*" : "") + name + " - Lumen";
            long rows = _s.Tables.Values.Sum(t => (long)t.RowCount);
            _status.Text = $"Page {_s.Doc.Pages.FindIndex(p => p.Id == _canvas.PageId) + 1} of {_s.Doc.Pages.Count}   |   {_s.Model.Tables.Count} tables, {rows:#,0} rows";
        }

        /// <summary>Rebuilds the page tabs and their menus. Duplicate page gives every visual a new id and remaps the interaction overrides to match.</summary>
        private void RebuildTabs()
        {
            _pageTabs.Children.Clear();
            foreach (var p in _s.Doc.Pages)
            {
                var page = p;
                var b = new ToggleButton { Content = p.Name, IsChecked = p.Id == _canvas.PageId, Padding = new Thickness(12, 3, 12, 3), Margin = new Thickness(1, 3, 1, 3) };
                b.Click += (x, e) => { _canvas.ShowPage(page.Id); RebuildTabs(); UpdateTitle(); };
                var menu = new ContextMenu();
                var rename = new MenuItem { Header = "Rename page" };
                rename.Click += (x, e) =>
                {
                    var n = Prompt.Text("Rename page", "Name", page.Name);
                    if (!string.IsNullOrWhiteSpace(n)) _s.Edit("Rename page", () => page.Name = n);
                };
                var del = new MenuItem { Header = "Delete page", IsEnabled = _s.Doc.Pages.Count > 1 };
                del.Click += (x, e) => _s.Edit("Delete page", () => _s.Doc.Pages.RemoveAll(q => q.Id == page.Id));
                var dup = new MenuItem { Header = "Duplicate page" };
                dup.Click += (x, e) => _s.Edit("Duplicate page", () =>
                {
                    var c = ModelJson.DeepClone(page);
                    c.Id = Guid.NewGuid().ToString("N");
                    c.Name = page.Name + " copy";
                    var map = c.Visuals.ToDictionary(v => v.Id, v => Guid.NewGuid().ToString("N"));
                    foreach (var v in c.Visuals) v.Id = map[v.Id];
                    foreach (var i in c.Interactions)
                    {
                        i.SourceVisualId = map.ContainsKey(i.SourceVisualId) ? map[i.SourceVisualId] : i.SourceVisualId;
                        i.TargetVisualId = map.ContainsKey(i.TargetVisualId) ? map[i.TargetVisualId] : i.TargetVisualId;
                    }
                    _s.Doc.Pages.Add(c);
                });
                menu.Items.Add(rename); menu.Items.Add(dup); menu.Items.Add(del);
                b.ContextMenu = menu;
                _pageTabs.Children.Add(b);
            }
            var add = new Button { Content = "+", Width = 28, Margin = new Thickness(4, 3, 2, 3), ToolTip = "New page" };
            add.Click += (x, e) => NewPage();
            _pageTabs.Children.Add(add);
        }

        /// <summary>Refreshes the whole shell after a document swap.</summary>
        private void AfterLoad()
        {
            if (_s.Doc.Pages.Count == 0) _s.Doc.Pages.Add(new PageDef { Name = "Page 1" });
            _canvas.ShowPage(_s.Doc.Pages[0].Id);
            RebuildTabs();
            _fields.Rebuild();
            RefreshPanes();
            UpdateTitle();
            _undo.IsEnabled = _redo.IsEnabled = false;
            if (IsLoaded) FitZoom(); else Loaded += (s, e) => FitZoom();
        }

        /// <summary>Zooms so the page fits the canvas, up to 150%.</summary>
        private void FitZoom()
        {
            var page = _canvas.Page;
            if (page == null || _canvas.ActualWidth < 50) return;
            double z = Math.Min((_canvas.ActualWidth - 48) / page.Width, (_canvas.ActualHeight - 48) / page.Height);
            _zoomSlider.Value = Math.Max(_zoomSlider.Minimum, Math.Min(1.5, z));
        }

        /// <summary>Starts an empty report, after offering to save.</summary>
        private void NewReport()
        {
            if (!ConfirmDiscard()) return;
            var doc = new ReportDocument { Name = "Untitled" };
            doc.Pages.Add(new PageDef { Name = "Page 1" });
            _s.Replace(doc, new Dictionary<string, TableData>(), null);
            AfterLoad();
        }

        /// <summary>Loads the built-in sample.</summary>
        private void LoadSample()
        {
            if (!ConfirmDiscard()) return;
            Dictionary<string, TableData> tables;
            var doc = SampleReport.Build(out tables);
            _s.Replace(doc, tables, null);
            AfterLoad();
        }

        /// <summary>Loads the warehouse sample, which shows the 3D visual.</summary>
        private void LoadWarehouseSample()
        {
            if (!ConfirmDiscard()) return;
            Dictionary<string, TableData> tables;
            var doc = WarehouseReport.Build(out tables);
            _s.Replace(doc, tables, null);
            AfterLoad();
        }

        /// <summary>Adds a page and shows it.</summary>
        private void NewPage()
        {
            var p = new PageDef { Name = "Page " + (_s.Doc.Pages.Count + 1) };
            _s.Edit("New page", () => _s.Doc.Pages.Add(p));
            _canvas.ShowPage(p.Id);
            RebuildTabs();
            UpdateTitle();
        }

        /// <summary>Offers to save unsaved changes. Returns false if the user cancelled, or the save did.</summary>
        private bool ConfirmDiscard()
        {
            if (!_s.Stack.IsDirty) return true;
            var r = MessageBox.Show(this, "Save changes first?", "Lumen", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (r == MessageBoxResult.Cancel) return false;
            if (r == MessageBoxResult.Yes) return Save(false);
            return true;
        }

        /// <summary>A normal exit deletes the recovery file, which is how the next start knows that all went well.</summary>
        private void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (!ConfirmDiscard()) { e.Cancel = true; return; }
            try { if (File.Exists(RecoveryPath)) File.Delete(RecoveryPath); }
            catch (IOException) { }
        }

        // ---------------------------------------------------------- file

        /// <summary>Picks and opens a report.</summary>
        private void OpenDialog()
        {
            if (!ConfirmDiscard()) return;
            var dlg = new OpenFileDialog { Filter = "Lumen report|*.lumen" };
            if (dlg.ShowDialog(this) == true) OpenFile(dlg.FileName);
        }

        /// <summary>Opens a report. A recovered file keeps no path, so that Save asks where to put it instead of writing over the recovery file.</summary>
        public void OpenFile(string path, bool asRecovered = false)
        {
            try
            {
                Dictionary<string, TableData> tables;
                var doc = LumenFile.Open(path, out tables);
                _s.Replace(doc, tables, asRecovered ? null : path);
                AfterLoad();
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidOperationException || ex is InvalidDataException || ex is Newtonsoft.Json.JsonException)
            {
                MessageBox.Show(this, "Could not open the file: " + ex.Message, "Lumen", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>Saves, asking for a path if there is none. Returns true on success.</summary>
        private bool Save(bool saveAs)
        {
            string path = _s.FilePath;
            if (path == null || saveAs)
            {
                var dlg = new SaveFileDialog { Filter = "Lumen report|*.lumen", FileName = _s.Doc.Name };
                if (dlg.ShowDialog(this) != true) return false;
                path = dlg.FileName;
            }
            try
            {
                LumenFile.Save(path, _s.Doc, _s.Tables, Exporter.Png(Exporter.Render(_canvas.PageSurface, _canvas.PageSurface.Width, _canvas.PageSurface.Height, 0.25)));
                _s.FilePath = path;
                _s.Stack.MarkSaved();
                UpdateTitle();
                return true;
            }
            catch (IOException ex)
            {
                MessageBox.Show(this, "Could not save: " + ex.Message, "Lumen", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        /// <summary>Writes the recovery file on a timer tick when there are unsaved changes. It runs on the UI thread, so a large model pauses the app while it writes.</summary>
        private void Autosave()
        {
            if (!_s.Stack.IsDirty) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(RecoveryPath));
                LumenFile.Save(RecoveryPath, _s.Doc, _s.Tables, null);
            }
            catch (IOException) { }
        }

        /// <summary>Offers the recovery file at startup.</summary>
        public void OfferRecovery()
        {
            if (!File.Exists(RecoveryPath)) return;
            var r = MessageBox.Show(this, "Lumen did not close cleanly. Open the recovered report?", "Lumen", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r == MessageBoxResult.Yes) OpenFile(RecoveryPath, true);
        }

        // ---------------------------------------------------------- data

        /// <summary>Loads a source off the UI thread, adds it to the model, and detects relationships. The data goes in before the edit, because the edit rebuilds the dataset.</summary>
        private async Task ImportSource(string name, DataSourceRef src)
        {
            try
            {
                _status.Text = "Importing " + name + "...";
                var def = new TableDef { Id = Guid.NewGuid().ToString("N"), Name = UniqueTable(name), Source = src };
                var data = await Task.Run(() => TableLoader.Load(def, CancellationToken.None));
                _s.Tables[def.Id] = data;
                _s.Edit("Import " + def.Name, () =>
                {
                    _s.Doc.Model.Tables.Add(def);
                });
                _s.RebuildDataset();
                var found = TableLoader.DetectRelationships(_s.Model, _s.Tables);
                if (found.Count > 0) _s.Edit("Detect relationships", () => _s.Model.Relationships.AddRange(found));
                _s.RebuildDataset();
                _fields.Rebuild();
                UpdateTitle();
            }
            catch (Exception ex) when (!(ex is OutOfMemoryException))
            {
                MessageBox.Show(this, "Import failed: " + ex.Message, "Lumen", MessageBoxButton.OK, MessageBoxImage.Error);
                UpdateTitle();
            }
        }

        /// <summary>Adds a number to a table name that is taken.</summary>
        private string UniqueTable(string name)
        {
            string c = name;
            for (int i = 2; _s.Model.TableByName(c) != null; i++) c = name + " " + i;
            return c;
        }

        /// <summary>Picks a file and imports it. For a workbook with several sheets it also asks for the sheet.</summary>
        private async void ImportFile(string kind, string filter)
        {
            var dlg = new OpenFileDialog { Filter = filter };
            if (dlg.ShowDialog(this) != true) return;
            var src = new DataSourceRef { Kind = kind, Path = dlg.FileName };
            string name = Path.GetFileNameWithoutExtension(dlg.FileName);
            if (kind == "excel")
            {
                var sheets = ExcelConnector.SheetNames(dlg.FileName);
                string sheet = sheets.Count == 1 ? sheets[0] : Prompt.Choice("Excel workbook", "Sheet", sheets);
                if (sheet == null) return;
                src.Sheet = sheet;
                name = sheet;
            }
            await ImportSource(name, src);
        }

        /// <summary>Shows the SQL dialog and imports. A query's table is named "Query", and a table reference is named after its last part.</summary>
        private async void ImportSql()
        {
            var src = Prompt.Sql();
            if (src == null) return;
            var q = src.Query.Trim();
            string name = q.StartsWith("select", StringComparison.OrdinalIgnoreCase) || q.StartsWith("with", StringComparison.OrdinalIgnoreCase) ? "Query" : q.Split('.').Last().Trim('[', ']');
            await ImportSource(name, src);
        }

        /// <summary>Opens the Transform window for a table, or asks which when <paramref name="tableId"/> is null, and applies the result as one edit.
        /// The new data stays even if that edit is undone; only the steps and column definitions revert.</summary>
        public void TransformTable(string tableId)
        {
            if (_s.Model.Tables.Count == 0) { MessageBox.Show(this, "Get some data first.", "Lumen"); return; }
            TableDef t;
            if (tableId != null) t = _s.Model.Table(tableId);
            else
            {
                var n = Prompt.Choice("Transform data", "Table", _s.Model.Tables.Select(x => x.Name));
                if (n == null) return;
                t = _s.Model.TableByName(n);
            }
            var w = new TransformWindow(t.Name, t.Source, t.Steps);
            if (w.ShowDialog() != true) return;
            var copy = ModelJson.DeepClone(t);
            var data = TableLoader.Bind(copy, w.Result);
            _s.Tables[t.Id] = data;
            _s.Edit("Transform " + t.Name, () =>
            {
                var live = _s.Model.Table(t.Id);
                live.Steps = w.Steps;
                live.Columns = copy.Columns;
            });
            _s.RebuildDataset();
            _canvas.Sync(true);
            _fields.Rebuild();
        }

        /// <summary>Reloads every table in turn. A table that fails keeps its old data and is named in a warning. Refresh is not an undo step.</summary>
        private async void RefreshData()
        {
            var failures = new List<string>();
            _status.Text = "Refreshing...";
            foreach (var def in _s.Model.Tables.ToList())
            {
                try
                {
                    var data = await Task.Run(() => TableLoader.Load(def, CancellationToken.None));
                    _s.Tables[def.Id] = data;
                }
                catch (Exception ex) when (!(ex is OutOfMemoryException)) { failures.Add(def.Name + ": " + ex.Message); }
            }
            _s.RebuildDataset();
            _canvas.Sync(true);
            UpdateTitle();
            if (failures.Count > 0)
                MessageBox.Show(this, "Some tables kept their old data:\n" + string.Join("\n", failures), "Lumen", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        /// <summary>Deletes a table with its relationships and home measures. Visuals using it stay and report a missing field.</summary>
        public void DeleteTable(string tableId)
        {
            var t = _s.Model.Table(tableId);
            var r = MessageBox.Show(this, $"Delete table '{t.Name}'?", "Lumen", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (r != MessageBoxResult.Yes) return;
            _s.Edit("Delete table", () =>
            {
                _s.Model.Tables.RemoveAll(x => x.Id == tableId);
                _s.Model.Relationships.RemoveAll(x => x.FromTableId == tableId || x.ToTableId == tableId);
                _s.Model.Measures.RemoveAll(x => x.HomeTableId == tableId);
            });
        }

        // ---------------------------------------------------------- authoring

        /// <summary>Opens the measure editor for a new measure.</summary>
        public void NewMeasure(string tableId)
        {
            if (_s.Model.Tables.Count == 0) { MessageBox.Show(this, "Get some data first.", "Lumen"); return; }
            new MeasureWindow(_s, null, tableId).ShowDialog();
        }

        /// <summary>Opens the measure editor on a measure.</summary>
        public void EditMeasure(string id)
        {
            var m = _s.Model.Measure(id);
            if (m != null) new MeasureWindow(_s, m, null).ShowDialog();
        }

        /// <summary>With one visual selected, changes its type. Otherwise adds a new visual.</summary>
        public void GalleryClicked(string type)
        {
            var sel = _canvas.PrimarySelection;
            if (sel != null && _canvas.Selected.Count == 1)
            {
                var def = sel.Def;
                _s.Edit("Change visual type", () =>
                {
                    def.Type = type;
                    def.Roles = RemapRoles(def, type);
                });
                return;
            }
            _canvas.AddVisual(type);
        }

        /// <summary>Carries fields into the new type's wells: groupings to groupings, values to values.</summary>
        private Dictionary<string, List<FieldRef>> RemapRoles(VisualDef def, string type)
        {
            var groupings = new List<FieldRef>();
            var values = new List<FieldRef>();
            foreach (var f in def.Roles.Values.SelectMany(x => x))
                (f.IsMeasureLike ? values : groupings).Add(f);

            var result = new Dictionary<string, List<FieldRef>>();
            using (var v = VisualRegistry.Create(type))
            {
                foreach (var role in v.Capabilities.Roles)
                {
                    var source = role.Kind == RoleKind.Grouping ? groupings : values;
                    if (role.Kind == RoleKind.Any) source = groupings.Concat(values).ToList();
                    var take = source.Take(role.Max).ToList();
                    if (take.Count > 0) result[role.Name] = take;
                    if (role.Kind != RoleKind.Any) source.RemoveRange(0, take.Count);
                }
            }
            return result;
        }

        /// <summary>Handles a double-click on a field: adds it to the first well that takes it, or creates a visual if none is selected.</summary>
        public void AddFieldToSelection(FieldRef field)
        {
            var c = _canvas.PrimarySelection;
            if (c == null) { _canvas.AddFieldVisual(field); return; }
            var def = c.Def;
            var role = c.Caps.Roles.FirstOrDefault(r => r.Accepts(field, _s.Model) && def.Role(r.Name).Count < r.Max);
            if (role != null) _viz.AddToWell(def, role, field);
        }

        /// <summary>Selects a built-in theme. Doing so drops an imported one.</summary>
        private void SetTheme(string name)
        {
            _s.Edit("Change theme", () => { _s.Doc.Theme.BuiltIn = name; _s.Doc.Theme.CustomJson = null; });
            _s.ApplyTheme();
        }

        /// <summary>Imports a Power BI theme file. It parses once here so that a bad file is refused before it reaches the document.</summary>
        private void ImportTheme()
        {
            var dlg = new OpenFileDialog { Filter = "Power BI theme|*.json" };
            if (dlg.ShowDialog(this) != true) return;
            string json = File.ReadAllText(dlg.FileName);
            try { ResolvedTheme.FromPowerBiJson(json, _s.Theme); }
            catch (Newtonsoft.Json.JsonException ex) { MessageBox.Show(this, "That is not a theme file: " + ex.Message, "Lumen"); return; }
            _s.Edit("Import theme", () => _s.Doc.Theme.CustomJson = json);
            _s.ApplyTheme();
        }

        // ---------------------------------------------------------- export

        /// <summary>Renders the current page.</summary>
        private BitmapSource RenderPage(double scale) =>
            Exporter.Render(_canvas.PageSurface, _canvas.PageSurface.Width, _canvas.PageSurface.Height, scale);

        /// <summary>Saves the page at twice its size.</summary>
        private void ExportPng()
        {
            var dlg = new SaveFileDialog { Filter = "PNG|*.png", FileName = _canvas.Page.Name };
            if (dlg.ShowDialog(this) == true) File.WriteAllBytes(dlg.FileName, Exporter.Png(RenderPage(2)));
        }

        /// <summary>Visits each page, waits for its data, and collects images for the PDF.</summary>
        private async void ExportPdf()
        {
            var dlg = new SaveFileDialog { Filter = "PDF|*.pdf", FileName = _s.Doc.Name };
            if (dlg.ShowDialog(this) != true) return;
            string back = _canvas.PageId;
            var pages = new List<BitmapSource>();
            foreach (var p in _s.Doc.Pages.ToList())
            {
                await ShowPageAndWait(p.Id);
                pages.Add(RenderPage(2));
            }
            _canvas.ShowPage(back);
            Exporter.SavePdf(dlg.FileName, pages);
        }

        /// <summary>Shows a page and waits for its data, keeping the tabs in step.</summary>
        private Task ShowPageAndWait(string pageId)
        {
            var done = _canvas.ShowPageAndWait(pageId);
            RebuildTabs();
            return done;
        }

        // ---------------------------------------------------------- self-check

        /// <summary>Drives the warehouse visual through its views, colors, picking, selection and slicers, writes PNGs and a log, saves the report beside them, then exits.</summary>
        public async void RunWarehouseCheck(string outPath)
        {
            var log = new List<string>();
            string full = Path.GetFullPath(outPath);
            string stem = Path.Combine(Path.GetDirectoryName(full), Path.GetFileNameWithoutExtension(full));
            int failures = 0;
            Action<bool, string> check = (ok, what) => { log.Add((ok ? "PASS " : "FAIL ") + what); if (!ok) failures++; };
            Func<string, Task> snap = async name =>
            {
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                await Task.Delay(250);
                var root = (FrameworkElement)Content;
                var bmp = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                bmp.Render(root);
                File.WriteAllBytes(stem + "-" + name + ".png", Exporter.Png(bmp));
            };
            try
            {
                Width = 1600; Height = 1000;
                Show();
                Dictionary<string, TableData> tables;
                var doc = WarehouseReport.Build(out tables);
                _s.Replace(doc, tables, null);
                AfterLoad();
                await ShowPageAndWait(doc.Pages[0].Id);
                var page = doc.Pages[0];
                var def = page.Visuals.First(v => v.Type == "warehouse3d");
                var card = page.Visuals.First(v => v.Type == "card");
                var c = FindContainer(def.Id);
                var wh = (WarehouseVisual)c.Visual;
                Func<Task> settle = async () =>
                {
                    while (wh.Flying) await Task.Delay(30);
                    await Task.Delay(400);
                };
                Func<string> firstCard = () => FindContainer(card.Id).LastResults?.FirstOrDefault()?.Measure(0, 0).ToDisplayString();
                foreach (var v in page.Visuals)
                {
                    var failed = FindContainer(v.Id).LastResults?.FirstOrDefault(r => r.Error != null);
                    check(failed == null, v.Type + " queried" + (failed == null ? "" : ": " + failed.Error));
                }
                check(wh.Layout != null && wh.Layout.Locations.Count == 14000, "14,000 locations laid out, got " + wh.Layout?.Locations.Count);
                check(wh.Layout != null && wh.Layout.BayRows.Count == 250, "250 bay rows, got " + wh.Layout?.BayRows.Count);
                await snap("overview");

                var timer = Stopwatch.StartNew();
                foreach (var mode in new[] { WarehouseColor.StockAge, WarehouseColor.OpenSlots, WarehouseColor.FillRate }) wh.SetColor(mode);
                log.Add("Three recolors took " + timer.ElapsedMilliseconds + " ms");
                wh.SetColor(WarehouseColor.OpenSlots);
                await snap("overview-open");

                wh.SetView(WarehouseView.Plan);
                await settle();
                await snap("plan-open");
                wh.SetColor(WarehouseColor.StockAge);
                wh.SetView(WarehouseView.Aisle);
                await settle();
                await snap("aisle-age");
                wh.SetColor(WarehouseColor.FillRate);
                await snap("aisle-fill");

                // A real ray through the plan finds a location, which then pins and cross-filters.
                wh.SetView(WarehouseView.Plan);
                await settle();
                WarehouseLocation hit = null;
                WarehouseBayRow tile;
                wh.PickAt(new Point(wh.ViewSize.Width * 0.3, wh.ViewSize.Height * 0.3), out hit, out tile);
                check(hit != null, "A ray pick finds a location in the plan: " + hit?.Id);
                string before = firstCard();
                wh.SelectLocation(hit, false);
                await Task.Delay(500);
                check(wh.Pinned == hit.Id, "The callout is pinned on the location");
                check(firstCard() == "1", "Selecting a location cross-filters the card to 1, got " + firstCard() + " from " + before);
                check(wh.Layout.Locations.Count(l => l.Visible) == 14000, "The 3D visual keeps every location while its own selection filters the page");
                wh.SetView(WarehouseView.Aisle);
                await settle();
                await snap("aisle-pinned");
                ((IVisualHost)c).ClearSelection();
                await Task.Delay(300);
                check(wh.Pinned == null, "Clearing the selection closes the callout");

                var zone = page.Visuals.First(v => v.Type == "slicer");
                wh.SetView(WarehouseView.Plan);
                await settle();
                ((IVisualHost)FindContainer(zone.Id)).SetSlicerFilter(FilterDef.Basic(zone.Role("field")[0], new object[] { "Zone A" }));
                await Task.Delay(500);
                await settle();
                check(wh.Layout.Locations.Count(l => l.Visible) == 3360, "Slicing to Zone A keeps 3,360 locations, got " + wh.Layout.Locations.Count(l => l.Visible));
                await snap("plan-zone-a");
                wh.SetView(WarehouseView.Overview);
                await settle();
                await snap("overview-zone-a");
                ((IVisualHost)FindContainer(zone.Id)).SetSlicerFilter(null);
                await Task.Delay(500);
                await settle();
                check(wh.Layout.Locations.Count(l => l.Visible) == 14000, "Clearing the slicer restores 14,000 locations");

                // A filter on one level leaves it at its height, because the plan comes from the unfiltered rows.
                var level = page.Visuals.Where(v => v.Type == "slicer").ElementAt(2);
                double topBefore = wh.Layout.Find("A-01-01-60-1").Slot.Y;
                ((IVisualHost)FindContainer(level.Id)).SetSlicerFilter(FilterDef.Basic(level.Role("field")[0], new object[] { 60L }));
                await Task.Delay(500);
                await settle();
                check(wh.Layout.Locations.Count(l => l.Visible) == 2000, "Slicing to level 60 keeps 2,000 locations, got " + wh.Layout.Locations.Count(l => l.Visible));
                check(wh.Layout.Find("A-01-01-60-1").Slot.Y == topBefore, "Level 60 stays at its height");
                await snap("level-60");
                ((IVisualHost)FindContainer(level.Id)).SetSlicerFilter(null);
                await Task.Delay(500);
                await settle();

                // Frame rate while the whole scene moves. The Rendering event fires once per composed frame.
                int frames = 0;
                EventHandler count = (s, e) => frames++;
                CompositionTarget.Rendering += count;
                var flight = Stopwatch.StartNew();
                wh.SetView(WarehouseView.Plan);
                await Task.Delay(50);
                while (wh.Flying) await Task.Delay(10);
                double flew = flight.Elapsed.TotalSeconds;
                CompositionTarget.Rendering -= count;
                log.Add($"Camera flight: {frames / flew:0} frames per second over {flew:0.0} s, render tier {RenderCapability.Tier >> 16}");
                wh.SetView(WarehouseView.Overview);
                await settle();

                await WarehouseMouseCheck(wh, c, check, log);

                SetTheme("Dark");
                await Task.Delay(500);
                await snap("overview-dark");
                SetTheme("Light");
                await Task.Delay(300);

                LumenFile.Save(full, _s.Doc, _s.Tables, null);
                check(File.Exists(full), "Saved " + full);
                failures += App.Failures.Count;
                log.AddRange(App.Failures);
            }
            catch (Exception ex)
            {
                failures++;
                log.Add("FAIL " + ex);
            }
            log.Add(failures == 0 ? "All checks passed" : failures + " checks failed");
            File.WriteAllLines(stem + ".log", log);
            Environment.ExitCode = failures == 0 ? 0 : 1;
            Application.Current.Shutdown();
        }

        /// <summary>Drives the real shell end to end, writes PNGs and a log, then exits. Used to verify a build.</summary>
        public async void RunScreenshot(string outPath)
        {
            var log = new List<string>();
            string stem = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(outPath)), Path.GetFileNameWithoutExtension(outPath));
            Func<string, Task> snap = async name =>
            {
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                await Task.Delay(250);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                var root = (FrameworkElement)Content;
                var bmp = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                bmp.Render(root);
                File.WriteAllBytes(stem + "-" + name + ".png", Exporter.Png(bmp));
            };

            try
            {
                Show();
                Dictionary<string, TableData> tables;
                var doc = SampleReport.Build(out tables);
                var sw = Stopwatch.StartNew();
                _s.Replace(doc, tables, null);
                AfterLoad();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                log.Add("Sample loaded in " + sw.ElapsedMilliseconds + " ms");

                for (int i = 0; i < doc.Pages.Count; i++)
                {
                    await ShowPageAndWait(doc.Pages[i].Id);
                    await snap("page" + (i + 1));
                }

                // Cross-highlight: select Bikes on the column chart of page 1.
                await ShowPageAndWait(doc.Pages[0].Id);
                var chart = doc.Pages[0].Visuals.First(v => v.Type == "clusteredColumn");
                var host = (IVisualHost)FindContainer(chart.Id);
                var cat = chart.Role("category")[0];
                host.Select(new DataPoint { Key = "Bikes", Fields = { new KeyValuePair<FieldRef, object>(cat, "Bikes") } }, false);
                await Task.Delay(400);
                await snap("crossfilter");
                host.ClearSelection();
                await Task.Delay(200);

                var slicer = doc.Pages[0].Visuals.First(v => v.Type == "slicer");
                ((IVisualHost)FindContainer(slicer.Id)).SetSlicerFilter(FilterDef.Basic(slicer.Role("field")[0], new object[] { "North" }));
                await Task.Delay(400);
                await snap("slicer");
                log.Add("Performance after a slicer change:\n" + PerfText());
                ((IVisualHost)FindContainer(slicer.Id)).SetSlicerFilter(null);
                await Task.Delay(200);

                // Undo and redo a move.
                _canvas.SetSelection(new[] { chart.Id });
                double x0 = chart.X;
                _canvas.Nudge(40, 0);
                _s.Stack.Undo();
                var undone = _s.Doc.Find(_canvas.PageId, chart.Id).X;
                log.Add($"Undo restores X: {undone == x0}");

                ShowView(1);
                await snap("data");
                ShowView(2);
                await snap("model");
                ShowView(0);

                // Every visual selected in turn: wells and the reflective format pane.
                foreach (var p in _s.Doc.Pages.ToList())
                {
                    await ShowPageAndWait(p.Id);
                    foreach (var v in p.Visuals.ToList())
                    {
                        _canvas.SetSelection(new[] { v.Id });
                        await Dispatcher.Yield(DispatcherPriority.Background);
                    }
                }
                await ShowPageAndWait(doc.Pages[0].Id);
                _canvas.SetSelection(new[] { chart.Id });
                await snap("selected");
                log.Add("Selected each visual in turn: wells and format pane built");

                // The visual types and slicer styles the sample report does not use.
                await CheckGallery(snap, log);
                await CheckFileImports(stem, log);
                await CheckDialogs(stem, log);
                await RealInputCheck(snap, log);

                var file = stem + "-roundtrip.lumen";
                LumenFile.Save(file, _s.Doc, _s.Tables, null);
                OpenFile(file);
                _s.Stack.Clear();
                log.Add($"Save and reopen: {_s.Doc.Pages.Count} pages, {_s.Tables.Count} tables, {new FileInfo(file).Length / 1024} KB");
                File.Delete(file);
                await ShowPageAndWait(_s.Doc.Pages[0].Id);

                SetTheme("Dark");
                await Task.Delay(400);
                await snap("dark");
                ShowView(1);
                await snap("dark-data");
                ShowView(0);
                SetTheme("High contrast");
                await Task.Delay(400);
                await snap("contrast");
                _s.Stack.Clear();

                var pdf = stem + ".pdf";
                Exporter.SavePdf(pdf, new[] { RenderPage(1) });
                log.Add("PDF written: " + new FileInfo(pdf).Length / 1024 + " KB");
                log.Add(App.Failures.Count == 0 ? "OK" : "FAILED with " + App.Failures.Count + " unhandled exceptions");
            }
            catch (Exception ex)
            {
                log.Add("FAILED: " + ex);
            }
            log.AddRange(App.Failures);
            File.WriteAllLines(stem + ".log", log);
            Application.Current.Shutdown();
        }

        /// <summary>Self-check: draws every visual type and slicer style that the sample leaves out, then exercises copy, paste, align and undo.</summary>
        private async Task CheckGallery(Func<string, Task> snap, List<string> log)
        {
            var page = new PageDef { Name = "Gallery" };
            var sales = _s.Model.MeasureByName("Sales Amount");
            var region = FieldRef.ForColumn("sales", _s.Model.Table("sales").ColumnByName("Region").Id);
            var category = FieldRef.ForColumn("product", _s.Model.Table("product").ColumnByName("Category").Id);
            var date = FieldRef.ForColumn("date", _s.Model.Table("date").ColumnByName("Date").Id);
            int i = 0;
            Action<string, Action<VisualDef>> add = (type, init) =>
            {
                var v = new VisualDef { Id = Guid.NewGuid().ToString("N"), Type = type, X = 16 + i % 4 * 312, Y = 16 + i / 4 * 232, Width = 300, Height = 220, ZIndex = i };
                init(v);
                page.Visuals.Add(v);
                i++;
            };
            Action<VisualDef, string, FieldRef> put = (v, role, f) =>
            {
                if (!v.Roles.ContainsKey(role)) v.Roles[role] = new List<FieldRef>();
                v.Roles[role].Add(f);
            };
            // The "series" name matches no role today, so legends are not filled here. The chart still draws.
            foreach (var type in new[] { "stackedBar", "clusteredBar", "stackedColumn", "area", "pie", "donut" })
                add(type, v =>
                {
                    using (var probe = VisualRegistry.Create(type))
                        foreach (var r in probe.Capabilities.Roles.Where(r => r.Min > 0 || r.Name == "series"))
                            put(v, r.Name, r.Kind == RoleKind.Measure ? FieldRef.ForMeasure(sales.Id) : r.Name == "series" ? region : category);
                });
            foreach (var style in new[] { "Dropdown", "Between", "RelativeDate", "List" })
                add("slicer", v =>
                {
                    put(v, "field", style == "Between" || style == "RelativeDate" ? date : category);
                    v.Format = Newtonsoft.Json.Linq.JObject.FromObject(new { Style = style });
                });
            _s.Edit("New page", () => _s.Doc.Pages.Add(page));
            await ShowPageAndWait(page.Id);
            await snap("gallery");
            foreach (var v in page.Visuals)
            {
                var c = FindContainer(v.Id);
                var failed = c.LastResults?.FirstOrDefault(r => r.Error != null);
                if (failed != null) log.Add($"Gallery {v.Type}: {failed.Error}");
            }
            log.Add("Gallery page drawn: " + page.Visuals.Count + " visuals");

            // Copy, paste, align and undo over a whole page.
            _canvas.SetSelection(page.Visuals.Take(2).Select(v => v.Id).ToArray());
            _canvas.Copy();
            _canvas.Paste();
            int pasted = page.Visuals.Count;
            _canvas.SetSelection(page.Visuals.Take(3).Select(v => v.Id).ToArray());
            _canvas.Align("left");
            _s.Stack.Undo(); _s.Stack.Undo();
            log.Add($"Paste gave {pasted} visuals, two undos left {_s.Doc.Page(page.Id).Visuals.Count}");
            _s.Edit("Remove page", () => _s.Doc.Pages.RemoveAll(p => p.Id == page.Id));
            await ShowPageAndWait(_s.Doc.Pages[0].Id);
        }

        /// <summary>Self-check: imports small CSV and JSON files, then removes them.</summary>
        private async Task CheckFileImports(string stem, List<string> log)
        {
            string csv = stem + "-import.csv", json = stem + "-import.json";
            File.WriteAllText(csv, "ProductKey,Stock,Added\n1,10,2024-01-02\n2,20,2024-02-03\n3,,2024-03-04\n");
            File.WriteAllText(json, "[{\"Region\":\"North\",\"Manager\":\"Ann\"},{\"Region\":\"South\",\"Manager\":\"Bo\"}]");
            int before = _s.Model.Relationships.Count;
            await ImportSource("Stock", new DataSourceRef { Kind = "csv", Path = csv, HasHeaders = true });
            await ImportSource("Managers", new DataSourceRef { Kind = "json", Path = json });
            var stock = _s.Model.TableByName("Stock");
            log.Add($"Imported CSV and JSON: {_s.Model.Tables.Count} tables, {_s.Model.Relationships.Count - before} new relationships, Stock types {string.Join(",", stock.Columns.Select(c => c.DataType))}");
            _s.Edit("Remove test tables", () =>
            {
                var ids = new[] { stock.Id, _s.Model.TableByName("Managers").Id };
                _s.Model.Relationships.RemoveAll(r => ids.Contains(r.FromTableId) || ids.Contains(r.ToTableId));
                _s.Model.Tables.RemoveAll(t => ids.Contains(t.Id));
            });
            File.Delete(csv);
            File.Delete(json);
        }

        /// <summary>Self-check: opens the measure, transform and SQL windows and takes pictures. The SQL part expects local servers, which is specific to the development machine.</summary>
        private async Task CheckDialogs(string stem, List<string> log)
        {
            Func<Window, string, Task> shoot = async (w, name) =>
            {
                await Task.Delay(500);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                var root = (FrameworkElement)w.Content;
                var bmp = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                bmp.Render(root);
                File.WriteAllBytes(stem + "-" + name + ".png", Exporter.Png(bmp));
            };

            var measure = new MeasureWindow(_s, _s.Model.MeasureByName("Sales YTD"), null);
            measure.Show();
            measure.SetExpression("TOTALYTD([Sales Amount], Date[Nope])");
            await shoot(measure, "measure");
            measure.Close();

            string csv = stem + "-source.csv";
            File.WriteAllText(csv, "A,B\n1,x\n2,y\n");
            var transform = new TransformWindow("Test", new DataSourceRef { Kind = "csv", Path = csv, HasHeaders = true },
                new[] { StepKinds.Create(StepKinds.Rename, "Renamed: A", "column", "A", "name", "Key") });
            transform.Show();
            await shoot(transform, "transform");
            transform.Close();
            File.Delete(csv);
            log.Add("Measure and transform windows opened");

            var sql = new SqlDialog();
            sql.Show();
            await sql.Listing;
            log.Add($"SQL dialog, localhost: {sql.Status}, list holds {sql.DatabaseCount}");
            sql.SetServer(@".\MSSQL2019");
            await sql.Listing;
            log.Add($"SQL dialog, named instance: {sql.Status}, list holds {sql.DatabaseCount}");
            sql.SetServer("no-such-server-here");
            await sql.Listing;
            log.Add($"SQL dialog, bad server: {sql.Status}, list holds {sql.DatabaseCount}");
            sql.SetServer("localhost");
            await sql.Listing;
            await shoot(sql, "sql");
            sql.PickAndCommit(0);
            var picked = new System.Data.SqlClient.SqlConnectionStringBuilder(sql.Result.ConnectionString);
            log.Add($"SQL dialog, picked a database: catalog set {picked.InitialCatalog.Length > 0}, server {picked.DataSource}");

            var cs = new System.Data.SqlClient.SqlConnectionStringBuilder { DataSource = "localhost", InitialCatalog = "master", IntegratedSecurity = true };
            await ImportSource("SqlCheck", new DataSourceRef { Kind = "sql", ConnectionString = cs.ConnectionString, Query = "SELECT name, database_id, create_date FROM sys.databases" });
            var imported = _s.Model.TableByName("SqlCheck");
            log.Add(imported == null ? "SQL import FAILED" : $"SQL import: {_s.Tables[imported.Id].RowCount} rows, types {string.Join(",", imported.Columns.Select(c => c.DataType))}");
            if (imported != null)
                _s.Edit("Remove test table", () =>
                {
                    _s.Model.Relationships.RemoveAll(r => r.FromTableId == imported.Id || r.ToTableId == imported.Id);
                    _s.Model.Tables.RemoveAll(t => t.Id == imported.Id);
                });
        }

        // ---------------------------------------------------------- real mouse

        // Win32 mouse injection. WPF's OLE drag loop ignores simulated events, so only real input tests it. mouse_event flags: 2 left down, 4 left up.
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern void mouse_event(uint flags, int dx, int dy, int data, IntPtr extra);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint p);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LastInput info);
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] private struct LastInput { public int Size; public uint Tick; }
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }

        /// <summary>Presses, moves in 16 steps, and releases. Blocks, so callers run it on a worker while the UI thread services the drag.</summary>
        private static void RealDrag(Point a, Point b)
        {
            SetCursorPos((int)a.X, (int)a.Y);
            Thread.Sleep(120);
            mouse_event(2, 0, 0, 0, IntPtr.Zero);
            Thread.Sleep(120);
            for (int i = 1; i <= 16; i++)
            {
                SetCursorPos((int)(a.X + (b.X - a.X) * i / 16), (int)(a.Y + (b.Y - a.Y) * i / 16));
                Thread.Sleep(30);
            }
            Thread.Sleep(200);
            mouse_event(4, 0, 0, 0, IntPtr.Zero);
            Thread.Sleep(500);
        }

        /// <summary>Walks the visual tree.</summary>
        private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var c = VisualTreeHelper.GetChild(root, i);
                if (c is T t) yield return t;
                foreach (var d in Descendants<T>(c)) yield return d;
            }
        }

        /// <summary>A point in an element, in screen pixels.</summary>
        private static Point Center(FrameworkElement e, double fx = 0.5, double fy = 0.5) =>
            e.PointToScreen(new Point(e.ActualWidth * fx, e.ActualHeight * fy));

        /// <summary>Drives the warehouse visual with the real cursor: hover, orbit, pan, wheel, click and the context menu. Skipped if someone is using the machine.</summary>
        private async Task WarehouseMouseCheck(WarehouseVisual wh, VisualContainer c, Action<bool, string> check, List<string> log)
        {
            var li = new LastInput { Size = 8 };
            GetLastInputInfo(ref li);
            double idle = unchecked((uint)Environment.TickCount - li.Tick) / 1000.0;
            if (idle < 30) { log.Add($"Real mouse check SKIPPED: last input {idle:0} s ago"); return; }

            NativePoint home;
            GetCursorPos(out home);
            Topmost = true;
            Activate();
            await Task.Delay(300);
            try
            {
                var view = (FrameworkElement)wh.ViewElement;
                Point middle = Center(view), right = Center(view, 0.7, 0.55), plan = Center(view, 0.3, 0.3);

                var before = wh.Camera;
                await Task.Run(() => RealDrag(middle, right));
                check(Math.Abs(wh.Camera.Yaw - before.Yaw) > 0.1, "A left drag orbits");
                check(wh.Pinned == null, "A left drag does not select");

                before = wh.Camera;
                await Task.Run(() =>
                {
                    SetCursorPos((int)middle.X, (int)middle.Y);
                    Thread.Sleep(120);
                    mouse_event(8, 0, 0, 0, IntPtr.Zero);
                    Thread.Sleep(120);
                    for (int i = 1; i <= 16; i++) { SetCursorPos((int)middle.X + i * 6, (int)middle.Y); Thread.Sleep(30); }
                    Thread.Sleep(100);
                    mouse_event(16, 0, 0, 0, IntPtr.Zero);
                    Thread.Sleep(400);
                });
                check((wh.Camera.Target - before.Target).Length > 1, "A right drag pans");
                check(c.ContextMenu == null || !c.ContextMenu.IsOpen, "A right drag does not open the menu");

                before = wh.Camera;
                await Task.Run(() => { mouse_event(0x800, 0, 0, 120, IntPtr.Zero); Thread.Sleep(300); });
                check(wh.Camera.Distance < before.Distance, "The wheel zooms in");

                wh.SetView(WarehouseView.Plan);
                while (wh.Flying) await Task.Delay(30);
                await Task.Delay(300);
                await Task.Run(() =>
                {
                    SetCursorPos((int)plan.X, (int)plan.Y);
                    Thread.Sleep(200);
                    mouse_event(2, 0, 0, 0, IntPtr.Zero);
                    Thread.Sleep(80);
                    mouse_event(4, 0, 0, 0, IntPtr.Zero);
                    Thread.Sleep(600);
                });
                check(wh.Pinned != null, "A real click pins " + wh.Pinned);
                ((IVisualHost)c).ClearSelection();
                await Task.Delay(300);

                await Task.Run(() =>
                {
                    mouse_event(8, 0, 0, 0, IntPtr.Zero);
                    Thread.Sleep(80);
                    mouse_event(16, 0, 0, 0, IntPtr.Zero);
                    Thread.Sleep(500);
                });
                check(c.ContextMenu != null && c.ContextMenu.IsOpen, "A plain right-click opens the menu");
                if (c.ContextMenu != null) c.ContextMenu.IsOpen = false;
                wh.SetView(WarehouseView.Overview);
                while (wh.Flying) await Task.Delay(30);
            }
            finally
            {
                SetCursorPos(home.X, home.Y);
                Topmost = false;
            }
        }

        /// <summary>Drives the real cursor through OLE drag and the canvas. Skipped if someone is using the machine.</summary>
        private async Task RealInputCheck(Func<string, Task> snap, List<string> log)
        {
            var li = new LastInput { Size = 8 };
            GetLastInputInfo(ref li);
            double idle = unchecked((uint)Environment.TickCount - li.Tick) / 1000.0;
            if (idle < 30) { log.Add($"Real mouse check SKIPPED: last input {idle:0} s ago"); return; }

            NativePoint home;
            GetCursorPos(out home);
            Topmost = true;
            Activate();
            ShowView(0);
            var page = new PageDef { Name = "Mouse" };
            _s.Edit("New page", () => _s.Doc.Pages.Add(page));
            await ShowPageAndWait(page.Id);
            await Task.Delay(300);
            try
            {
                Func<string, FrameworkElement> field = name => _fields._tree.Items.OfType<TreeViewItem>()
                    .SelectMany(t => t.Items.OfType<TreeViewItem>())
                    .First(i => Descendants<TextBlock>(i).Any(x => x.Text == name));
                Func<string, FrameworkElement> header = name => Descendants<TextBlock>(field(name)).First(x => x.Text == name);

                var from = Center(header("Sales Amount"));
                var drop = Center(_canvas.PageSurface, 0.3, 0.4);
                await Task.Run(() => RealDrag(from, drop));
                await Task.Delay(500);
                page = _s.Doc.Page(page.Id);
                log.Add($"Field dragged to canvas: {page.Visuals.Count} visual(s), type {page.Visuals.FirstOrDefault()?.Type}");
                if (page.Visuals.Count == 0) return;

                var id = page.Visuals[0].Id;
                _canvas.SetSelection(new[] { id });
                _s.Edit("Make it a column chart", () => _s.Doc.Find(page.Id, id).Type = "clusteredColumn");
                await Task.Delay(300);
                _canvas.SetSelection(new[] { id });
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

                var well = Descendants<Border>(_viz).First(b => b.AllowDrop);
                from = Center(header("Category"));
                var to = Center(well);
                await Task.Run(() => RealDrag(from, to));
                await Task.Delay(500);
                var v = _s.Doc.Find(page.Id, id);
                log.Add("Field dragged to first well: roles " + string.Join(", ", v.Roles.Select(r => r.Key + "=" + r.Value.Count)));

                _canvas.SetSelection(new[] { id });
                await Task.Delay(200);
                var c = FindContainer(id);
                double x0 = v.X, y0 = v.Y, w0 = v.Width;
                from = Center(c, 0.5, 0.5);
                to = new Point(from.X + 90, from.Y + 60);
                await Task.Run(() => RealDrag(from, to));
                v = _s.Doc.Find(page.Id, id);
                log.Add($"Dragged visual: X {x0} to {v.X}, Y {y0} to {v.Y}, on grid {v.X % 8 == 0 && v.Y % 8 == 0}");

                _canvas.SetSelection(new[] { id });
                await Task.Delay(200);
                c = FindContainer(id);
                var se = Descendants<Thumb>(c).OrderByDescending(t => { var q = t.TranslatePoint(new Point(), c); return q.X + q.Y; }).First();
                from = Center(se);
                to = new Point(from.X + 70, from.Y + 40);
                await Task.Run(() => RealDrag(from, to));
                v = _s.Doc.Find(page.Id, id);
                log.Add($"Resized with handle: width {w0} to {v.Width}");
                await snap("mouse");
                _s.Stack.Undo();
                log.Add($"Undo of resize: width {_s.Doc.Find(page.Id, id).Width}");
            }
            finally
            {
                Topmost = false;
                SetCursorPos(home.X, home.Y);
                _s.Edit("Remove page", () => _s.Doc.Pages.RemoveAll(p => p.Id == page.Id));
                await ShowPageAndWait(_s.Doc.Pages[0].Id);
            }
        }

        /// <summary>Slicer click to redraw, measured on the real page, at a given row count.</summary>
        public async void RunBench(int rows, string outPath)
        {
            var log = new List<string>();
            try
            {
                Show();
                var sw = Stopwatch.StartNew();
                Dictionary<string, TableData> tables;
                var doc = SampleReport.Build(out tables, rows);
                log.Add($"Rows {rows:#,0}: dataset built in {sw.Elapsed.TotalSeconds:0.0} s");
                sw.Restart();
                _s.Replace(doc, tables, null);
                AfterLoad();
                await ShowPageAndWait(doc.Pages[0].Id);
                log.Add($"First page ready in {sw.ElapsedMilliseconds} ms");

                var slicers = doc.Pages[0].Visuals.Where(v => v.Type == "slicer").ToList();
                var region = (IVisualHost)FindContainer(slicers[0].Id);
                var color = (IVisualHost)FindContainer(slicers[1].Id);
                var regionField = slicers[0].Role("field")[0];
                var colorField = slicers[1].Role("field")[0];
                string[] regions = { "North", "South", "East", "West" }, colors = { "Black", "Blue", "Red", "Silver" };

                Func<Action, Task<double>> click = async act =>
                {
                    var done = new TaskCompletionSource<bool>();
                    Action handler = null;
                    handler = () => { _canvas.Refreshed -= handler; done.TrySetResult(true); };
                    _canvas.Refreshed += handler;
                    var t = Stopwatch.StartNew();
                    act();
                    await done.Task;
                    await Dispatcher.Yield(DispatcherPriority.Render);
                    return t.Elapsed.TotalMilliseconds;
                };

                // Each region subset under each colour is a filter no query has seen: nothing is in the result cache.
                var cold = new List<double>();
                var warm = new List<double>();
                foreach (var c in colors)
                {
                    await click(() => color.SetSlicerFilter(FilterDef.Basic(colorField, new object[] { c })));
                    for (int bits = 1; bits < 15; bits++)
                    {
                        var chosen = regions.Where((x, i) => (bits >> i & 1) != 0).Cast<object>().ToArray();
                        cold.Add(await click(() => region.SetSlicerFilter(FilterDef.Basic(regionField, chosen))));
                    }
                    for (int bits = 1; bits < 15; bits++)
                    {
                        var chosen = regions.Where((x, i) => (bits >> i & 1) != 0).Cast<object>().ToArray();
                        warm.Add(await click(() => region.SetSlicerFilter(FilterDef.Basic(regionField, chosen))));
                    }
                }
                Func<List<double>, string> stats = list =>
                {
                    list.Sort();
                    return $"median {list[list.Count / 2]:0} ms, p95 {list[(int)(list.Count * 0.95)]:0} ms, max {list[list.Count - 1]:0} ms ({list.Count} clicks)";
                };
                log.Add("Slicer to redraw, including the 30 ms coalescing delay");
                log.Add("  new filter:      " + stats(cold));
                log.Add("  repeated filter: " + stats(warm));
                long peak;
                using (var p = Process.GetCurrentProcess()) peak = p.PeakWorkingSet64 / (1024 * 1024);
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                using (var p = Process.GetCurrentProcess())
                    log.Add($"Memory: managed live {GC.GetTotalMemory(false) / (1024 * 1024)} MB, working set {p.WorkingSet64 / (1024 * 1024)} MB, peak working set {peak} MB");
                log.Add(PerfText());
            }
            catch (Exception ex)
            {
                log.Add("FAILED: " + ex);
            }
            File.AppendAllLines(outPath, log);
            Application.Current.Shutdown();
        }

        /// <summary>The container of a visual on the current page.</summary>
        private VisualContainer FindContainer(string id) =>
            _canvas.PageSurface.Children.OfType<VisualContainer>().First(c => c.VisualId == id);

        /// <summary>Reads the text in the performance pane, for logs.</summary>
        private string PerfText() => (_perf.Content as Grid)?.Children.OfType<ScrollViewer>().Select(s => (s.Content as TextBlock)?.Text).FirstOrDefault() ?? "";
    }
}
