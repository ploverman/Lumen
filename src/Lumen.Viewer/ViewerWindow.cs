using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Lumen.Connectors;
using Lumen.Engine;
using Lumen.Model;
using Lumen.Reporting;
using Microsoft.Win32;

namespace Lumen.Viewer
{
    /// <summary>The read-only shell around <see cref="ReportSurface"/>: toolbar, page list, filter pane and status bar.
    /// <para>Nothing here writes the .lumen file. The document is held in memory, a reader's slicer and filter choices change only that copy,
    /// and they are gone when the window closes. <see cref="LumenFile.Save"/> is never called, and there is no autosave.</para></summary>
    public sealed class ViewerWindow : Window
    {
        private readonly ReportSession _s = new ReportSession();
        private readonly ReportSurface _surface;
        private readonly FiltersPane _filters;

        private readonly StackPanel _pageList = new StackPanel();
        private readonly TextBlock _status = new TextBlock { Margin = new Thickness(10, 3, 10, 3), FontSize = 12 };
        private readonly TextBlock _empty = new TextBlock { Text = "Open a .lumen report, or drop one here", FontSize = 18, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        private readonly Slider _zoomSlider = new Slider { Width = 120, Minimum = 0.3, Maximum = 2, Value = 1, VerticalAlignment = VerticalAlignment.Center };
        private readonly ToggleButton _fit = new ToggleButton { Content = "Fit", IsChecked = true, Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 2, 4, 2) };
        private readonly ToggleButton _filterToggle = new ToggleButton { Content = "Filters", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 2, 4, 2) };
        private readonly ColumnDefinition _filterColumn = new ColumnDefinition { Width = new GridLength(0) };
        private readonly List<FrameworkElement> _needsReport = new List<FrameworkElement>();
        private Button _openButton, _refreshButton;

        // The document as opened, for Reset. Filters and slicer states are read back from here, never from disk, so a reset keeps refreshed data.
        private string _opened;
        private string _dataNote = "";
        // Set while a data refresh runs. A second press of the button cancels through it.
        private CancellationTokenSource _refreshing;
        // True while FitToWindow moves the slider, so that the slider's handler can tell a fit from the user.
        private bool _fitting;
        private WindowState _stateBeforeFullScreen;

        /// <summary>Builds the shell with no report open.</summary>
        public ViewerWindow()
        {
            Title = "Lumen Viewer";
            Width = Math.Min(1500, SystemParameters.WorkArea.Width);
            Height = Math.Min(900, SystemParameters.WorkArea.Height);
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            FontFamily = new FontFamily("Segoe UI");
            ThemeService.Apply(_s.Theme);
            SetResourceReference(BackgroundProperty, "ChromeBg");
            SetResourceReference(ForegroundProperty, "ChromeFg");
            AllowDrop = true;

            _surface = new ReportSurface(_s);
            _filters = new FiltersPane(_s, false);
            BuildLayout();
            BindKeys();

            _s.Changed += dataAffecting => { _surface.Sync(dataAffecting); RebuildFilters(); };
            _s.ThemeChanged += () => _surface.Sync(true);
            _surface.SizeChanged += (x, e) => { if (_fit.IsChecked == true) FitToWindow(); };
            _surface.Visibility = Visibility.Collapsed;
            _zoomSlider.ValueChanged += (x, e) =>
            {
                _surface.Zoom = _zoomSlider.Value;
                if (!_fitting) _fit.IsChecked = false;
            };
            _fit.Click += (x, e) => { if (_fit.IsChecked == true) FitToWindow(); };
            _filterToggle.Click += (x, e) => SetFiltersShown(_filterToggle.IsChecked == true);
            Drop += OnDrop;
            UpdateCommands();
        }

        /// <summary>The surface showing the report.</summary>
        internal ReportSurface Surface => _surface;
        /// <summary>The open report.</summary>
        internal ReportSession Session => _s;

        // ---------------------------------------------------------- layout

        /// <summary>Builds toolbar, page list, surface, filter pane and status bar. All of it is code, as in the builder.</summary>
        private void BuildLayout()
        {
            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition());
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var bar = new DockPanel { Margin = new Thickness(8, 4, 8, 4) };
            var left = new StackPanel { Orientation = Orientation.Horizontal };
            _openButton = Ui.Button("Open", OpenDialog);
            left.Children.Add(_openButton);
            _refreshButton = Command(left, "Refresh data", RefreshData);
            Command(left, "Reset filters", Reset);
            left.Children.Add(new Border { Width = 12 });
            Command(left, "Export PNG", ExportPng);
            Command(left, "Export PDF", ExportPdf);
            Command(left, "Print", () => Exporter.Print(_surface.PageSurface, _s.Doc.Name));

            var right = new StackPanel { Orientation = Orientation.Horizontal };
            right.Children.Add(_filterToggle);
            _needsReport.Add(_filterToggle);
            right.Children.Add(Ui.Button("Full screen", ToggleFullScreen));
            right.Children.Add(new Border { Width = 12 });
            right.Children.Add(_fit);
            right.Children.Add(_zoomSlider);
            DockPanel.SetDock(right, Dock.Right);
            bar.Children.Add(right);
            bar.Children.Add(left);
            root.Children.Add(bar);

            var body = new Grid();
            Grid.SetRow(body, 1);
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
            body.ColumnDefinitions.Add(new ColumnDefinition());
            body.ColumnDefinitions.Add(_filterColumn);

            body.Children.Add(Ui.Pane("Pages", new ScrollViewer { Content = _pageList, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }));

            var center = new Grid().Bg("CanvasBg");
            Grid.SetColumn(center, 1);
            center.Children.Add(_empty.Fg("ChromeFg2"));
            center.Children.Add(_surface);
            body.Children.Add(center);

            Grid.SetColumn(_filters, 2);
            body.Children.Add(_filters);
            root.Children.Add(body);

            var status = new Border { Child = _status.Fg("ChromeFg2") }.Bg("ChromeBg");
            Grid.SetRow(status, 2);
            root.Children.Add(status);
            Content = root;
        }

        /// <summary>Adds a toolbar button that stays disabled until a report is open.</summary>
        private Button Command(Panel bar, string text, Action click)
        {
            var b = Ui.Button(text, click);
            bar.Children.Add(b);
            _needsReport.Add(b);
            return b;
        }

        /// <summary>Keyboard: Ctrl+O open, F5 refresh, Ctrl+P print, Ctrl+Page Up and Page Down change page, F11 full screen, Esc leaves focus mode, a cross-filter, or full screen.</summary>
        private void BindKeys()
        {
            PreviewKeyDown += (s, e) =>
            {
                bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
                bool open = _opened != null;
                switch (e.Key)
                {
                    case Key.O when ctrl: OpenDialog(); e.Handled = true; break;
                    case Key.F5 when open: RefreshData(); e.Handled = true; break;
                    case Key.P when ctrl && open: Exporter.Print(_surface.PageSurface, _s.Doc.Name); e.Handled = true; break;
                    case Key.PageUp when ctrl && open: StepPage(-1); e.Handled = true; break;
                    case Key.PageDown when ctrl && open: StepPage(1); e.Handled = true; break;
                    case Key.F11: ToggleFullScreen(); e.Handled = true; break;
                    case Key.Escape:
                        e.Handled = _surface.ClearEscape();
                        if (!e.Handled && WindowStyle == WindowStyle.None) { ToggleFullScreen(); e.Handled = true; }
                        break;
                }
            };
        }

        // ---------------------------------------------------------- pages

        /// <summary>Shows a page, updating the list and the status bar.</summary>
        internal void GoToPage(string pageId)
        {
            _surface.ShowPage(pageId);
            AfterPageChange();
        }

        /// <summary>Moves to the next or previous page, stopping at the ends.</summary>
        private void StepPage(int by)
        {
            var pages = _s.Doc.Pages;
            int at = pages.FindIndex(p => p.Id == _surface.PageId) + by;
            if (at >= 0 && at < pages.Count) GoToPage(pages[at].Id);
        }

        /// <summary>Brings the parts that follow the page up to date.</summary>
        private void AfterPageChange()
        {
            RebuildPageList();
            RebuildFilters();
            UpdateStatus();
            if (_fit.IsChecked == true) FitToWindow();
        }

        /// <summary>One toggle per page, in tab order.</summary>
        private void RebuildPageList()
        {
            _pageList.Children.Clear();
            int number = 0;
            foreach (var p in _s.Doc.Pages)
            {
                var page = p;
                var b = new ToggleButton
                {
                    Content = new TextBlock { Text = ++number + "  " + p.Name, TextTrimming = TextTrimming.CharacterEllipsis },
                    IsChecked = p.Id == _surface.PageId,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Padding = new Thickness(10, 5, 8, 5),
                    Margin = new Thickness(6, 1, 6, 1),
                    ToolTip = p.Name
                };
                b.Click += (x, e) => GoToPage(page.Id);
                _pageList.Children.Add(b);
            }
        }

        /// <summary>The pane lists the page and report filters. No visual is selected in the viewer, so the visual scope never shows.</summary>
        private void RebuildFilters() => _filters.Rebuild(_surface.Page, null);

        /// <summary>Opens or closes the filter pane.</summary>
        internal void SetFiltersShown(bool shown)
        {
            _filterToggle.IsChecked = shown;
            _filterColumn.Width = new GridLength(shown ? 240 : 0);
        }

        /// <summary>Zooms so the page fits the surface, up to the slider's maximum.</summary>
        private void FitToWindow()
        {
            var page = _surface.Page;
            if (page == null || _surface.ActualWidth < 50) return;
            double z = Math.Min((_surface.ActualWidth - 48) / page.Width, (_surface.ActualHeight - 48) / page.Height);
            _fitting = true;
            _zoomSlider.Value = Math.Max(_zoomSlider.Minimum, Math.Min(_zoomSlider.Maximum, z));
            _fitting = false;
        }

        /// <summary>Enters or leaves a borderless maximized window.</summary>
        private void ToggleFullScreen()
        {
            if (WindowStyle == WindowStyle.None)
            {
                WindowStyle = WindowStyle.SingleBorderWindow;
                WindowState = _stateBeforeFullScreen;
                return;
            }
            _stateBeforeFullScreen = WindowState;
            // A maximized window ignores the style change until it is restored once.
            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Maximized;
        }

        // ---------------------------------------------------------- file

        /// <summary>Picks and opens a report.</summary>
        private void OpenDialog()
        {
            var dlg = new OpenFileDialog { Filter = "Lumen report|*.lumen" };
            if (dlg.ShowDialog(this) == true) Open(dlg.FileName);
        }

        /// <summary>Takes a .lumen file dropped on the window.</summary>
        private void OnDrop(object sender, DragEventArgs e)
        {
            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            var file = files?.FirstOrDefault(f => f.EndsWith(".lumen", StringComparison.OrdinalIgnoreCase));
            if (file != null) Open(file);
        }

        /// <summary>Opens a report. The package unzips off the UI thread, so the window stays live on a large model. Failure leaves the current report on screen.</summary>
        public async void Open(string path) => await OpenAsync(path);

        /// <summary>Opens a report and reports whether it worked.</summary>
        internal async Task<bool> OpenAsync(string path)
        {
            try
            {
                _status.Text = "Opening " + Path.GetFileName(path) + "...";
                var loaded = await Task.Run(() =>
                {
                    Dictionary<string, TableData> tables;
                    var doc = LumenFile.Open(path, out tables);
                    return Tuple.Create(doc, tables);
                });
                var report = loaded.Item1;
                _opened = ModelJson.Serialize(report);
                _s.Replace(report, loaded.Item2, path);
                _dataNote = "data as saved in the file";
                if (report.Pages.Count == 0) report.Pages.Add(new PageDef { Name = "Page 1" });
                _surface.Visibility = Visibility.Visible;
                _empty.Visibility = Visibility.Collapsed;
                Title = Path.GetFileNameWithoutExtension(path) + " - Lumen Viewer (read-only)";
                UpdateCommands();
                GoToPage(report.Pages[0].Id);
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException || ex is InvalidDataException || ex is Newtonsoft.Json.JsonException)
            {
                UpdateStatus();
                ShowMessage("Could not open the file: " + ex.Message, MessageBoxImage.Error);
                return false;
            }
        }

        /// <summary>Puts every slicer and filter back as the file has them, and clears the cross-filter. Data from a refresh stays.</summary>
        internal void Reset()
        {
            var saved = ModelJson.Deserialize<ReportDocument>(_opened);
            _s.Doc.ReportFilters = saved.ReportFilters;
            foreach (var page in _s.Doc.Pages)
            {
                var original = saved.Page(page.Id);
                if (original == null) continue;
                page.PageFilters = original.PageFilters;
                foreach (var v in page.Visuals)
                {
                    var was = original.Visuals.FirstOrDefault(x => x.Id == v.Id);
                    if (was == null) continue;
                    v.SlicerState = was.SlicerState;
                    v.VisualFilters = was.VisualFilters;
                }
            }
            GoToPage(_surface.PageId);
        }

        // ---------------------------------------------------------- data

        /// <summary>Reloads every table from its source and applied steps, then requeries the page. Nothing is saved: the file keeps the data it had.
        /// A table that fails keeps its old data and is named in a warning. Pressing the button again cancels.</summary>
        internal async void RefreshData() => await RefreshDataAsync();

        /// <summary>The body of <see cref="RefreshData"/>. Returns the tables that failed.</summary>
        internal async Task<List<string>> RefreshDataAsync()
        {
            var failures = new List<string>();
            if (_refreshing != null) { _refreshing.Cancel(); return failures; }
            var cts = new CancellationTokenSource();
            _refreshing = cts;
            _refreshButton.Content = "Cancel refresh";
            try
            {
                RelinkSources();
                foreach (var def in _s.Model.Tables.ToList())
                {
                    _status.Text = "Refreshing " + def.Name + "...";
                    try
                    {
                        var data = await Task.Run(() => TableLoader.Load(def, cts.Token));
                        _s.Tables[def.Id] = data;
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex) when (!(ex is OutOfMemoryException)) { failures.Add(def.Name + ": " + ex.Message); }
                }
                if (cts.IsCancellationRequested) failures.Add("The refresh was cancelled");
                else _dataNote = "data refreshed " + DateTime.Now.ToString("t");
                _s.RebuildDataset();
                _surface.Sync(true);
            }
            finally
            {
                _refreshing = null;
                _refreshButton.Content = "Refresh data";
                UpdateStatus();
            }
            if (failures.Count > 0)
                ShowMessage("Some tables kept their old data:\n" + string.Join("\n", failures), MessageBoxImage.Warning);
            return failures;
        }

        /// <summary>A report that moved to another machine usually names a source path that is gone. When a file of the same name sits beside the report, use that.
        /// This changes the document in memory only.</summary>
        private void RelinkSources()
        {
            string folder = Path.GetDirectoryName(_s.FilePath);
            foreach (var src in _s.Model.Tables.Select(t => t.Source).Where(x => x != null && !string.IsNullOrEmpty(x.Path) && !File.Exists(x.Path)))
            {
                string beside = Path.Combine(folder, Path.GetFileName(src.Path));
                if (File.Exists(beside)) src.Path = beside;
            }
        }

        // ---------------------------------------------------------- export

        /// <summary>Renders the current page.</summary>
        private BitmapSource RenderPage(double scale) =>
            Exporter.Render(_surface.PageSurface, _surface.PageSurface.Width, _surface.PageSurface.Height, scale);

        /// <summary>Saves the page at twice its size.</summary>
        private void ExportPng()
        {
            var dlg = new SaveFileDialog { Filter = "PNG|*.png", FileName = _surface.Page.Name };
            if (dlg.ShowDialog(this) == true) File.WriteAllBytes(dlg.FileName, Exporter.Png(RenderPage(2)));
        }

        /// <summary>Picks a file and writes the PDF.</summary>
        private async void ExportPdf()
        {
            var dlg = new SaveFileDialog { Filter = "PDF|*.pdf", FileName = _s.Doc.Name };
            if (dlg.ShowDialog(this) == true) await WritePdf(dlg.FileName);
        }

        /// <summary>Visits each page, waits for its data, and writes one PDF page per report page. A cross-filter is lost on the way, as with any page change; slicers stay.</summary>
        internal async Task WritePdf(string path)
        {
            string back = _surface.PageId;
            var pages = new List<BitmapSource>();
            foreach (var p in _s.Doc.Pages.ToList())
            {
                await _surface.ShowPageAndWait(p.Id);
                pages.Add(RenderPage(2));
            }
            GoToPage(back);
            Exporter.SavePdf(path, pages);
        }

        // ---------------------------------------------------------- state

        /// <summary>Commands that need a report stay off until one is open.</summary>
        private void UpdateCommands()
        {
            foreach (var e in _needsReport) e.IsEnabled = _opened != null;
        }

        /// <summary>Writes the status bar.</summary>
        private void UpdateStatus()
        {
            if (_opened == null) { _status.Text = "Read-only viewer"; return; }
            long rows = _s.Tables.Values.Sum(t => (long)t.RowCount);
            _status.Text = $"Read-only   |   Page {_s.Doc.Pages.FindIndex(p => p.Id == _surface.PageId) + 1} of {_s.Doc.Pages.Count}   |   {rows:#,0} rows, {_dataNote}";
        }

        /// <summary>Shows a message, or logs it as a failure during a self-check, where no one could press OK.</summary>
        private void ShowMessage(string text, MessageBoxImage icon)
        {
            if (App.SelfCheck) App.Failures.Add(text);
            else MessageBox.Show(this, text, "Lumen Viewer", MessageBoxButton.OK, icon);
        }
    }
}
