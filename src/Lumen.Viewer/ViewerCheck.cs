using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Lumen.Charting;
using Lumen.Model;
using Lumen.Reporting;
using Lumen.Visuals;

namespace Lumen.Viewer
{
    /// <summary>Drives the real window against a report, writes PNGs and a log, then exits with 0 or 1. It is how a build gets verified without a person:
    /// <c>LumenViewer.exe --check report.lumen out.png</c>. The steps are the promises of the viewer: every visual renders, a slicer and a cross-filter change
    /// the numbers, Reset puts them back, Refresh reloads from source, PDF export works, and the file on disk is byte-for-byte what it was.</summary>
    internal sealed class ViewerCheck
    {
        private readonly ViewerWindow _w;
        private readonly List<string> _log = new List<string>();
        private string _stem;
        private int _failed;

        /// <summary>Holds the window to drive.</summary>
        public ViewerCheck(ViewerWindow window) { _w = window; }

        private ReportSurface Surface => _w.Surface;
        private ReportSession Report => _w.Session;

        /// <summary>Runs every step and shuts down. A failed step is logged and the rest still run.</summary>
        public async void Run(string file, string outPath)
        {
            _stem = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(outPath)), Path.GetFileNameWithoutExtension(outPath));
            string logPath = _stem + ".log";
            try
            {
                string hashBefore = Hash(file);
                var stamp = File.GetLastWriteTimeUtc(file);
                _w.Show();

                var sw = Stopwatch.StartNew();
                Expect(await _w.OpenAsync(file), "Report opens");
                await Settle();
                _log.Add($"Opened in {sw.ElapsedMilliseconds} ms: {Report.Doc.Pages.Count} pages, {Report.Tables.Values.Sum(t => (long)t.RowCount):#,0} rows");

                await EveryPage();
                await SlicerAndCrossFilter();
                await RealClicks();
                await Other();
                await Refresh();

                string pdf = _stem + ".pdf";
                await _w.WritePdf(pdf);
                Expect(File.Exists(pdf) && new FileInfo(pdf).Length > 0, $"PDF written, {new FileInfo(pdf).Length / 1024} KB");

                Expect(Hash(file) == hashBefore && File.GetLastWriteTimeUtc(file) == stamp, "The .lumen file is unchanged");
                _log.Add(_failed == 0 && App.Failures.Count == 0 ? "OK" : $"FAILED: {_failed} steps, {App.Failures.Count} errors");
            }
            catch (Exception ex)
            {
                _log.Add("FAILED: " + ex);
                _failed++;
            }
            _log.AddRange(App.Failures);
            File.WriteAllLines(logPath, _log);
            Application.Current.Shutdown(_failed == 0 && App.Failures.Count == 0 ? 0 : 1);
        }

        /// <summary>Visits each page, photographs it, and fails on any visual whose query returned an error.</summary>
        private async Task EveryPage()
        {
            int at = 0;
            foreach (var page in Report.Doc.Pages.ToList())
            {
                at++;
                await ShowPage(page);
                var errors = page.Visuals.Select(v => Surface.Container(v.Id))
                    .SelectMany(c => (c.LastResults ?? new QueryResult[0]).Where(r => r.Error != null).Select(r => c.Caps.DisplayName + ": " + r.Error)).ToList();
                Expect(errors.Count == 0, $"Page {at} '{page.Name}': {page.Visuals.Count} visuals" + (errors.Count == 0 ? "" : "; " + string.Join("; ", errors)));
                await Snap("page" + at);
            }
        }

        /// <summary>On the first page with a slicer and a card: a slicer value changes the card, a chart selection changes it again, and Reset restores it.</summary>
        private async Task SlicerAndCrossFilter()
        {
            var page = Report.Doc.Pages.First(p => p.Visuals.Any(v => v.Type == "slicer") && p.Visuals.Any(v => v.Type == "card"));
            await ShowPage(page);
            var slicer = page.Visuals.First(v => v.Type == "slicer");
            string baseline = Cards(page);

            var field = slicer.Role("field")[0];
            var values = Surface.Container(slicer.Id).LastResults[0];
            int pick = Enumerable.Range(0, values.RowCount).First(i => !values.Group(0, i).IsBlank);
            ((IVisualHost)Surface.Container(slicer.Id)).SetSlicerFilter(FilterDef.Basic(field, new[] { values.Group(0, pick).ToObject() }));
            await AfterRefresh();
            string slicered = Cards(page);
            Expect(slicered != baseline, $"Slicer on '{values.Group(0, pick).ToDisplayString()}': cards {baseline} to {slicered}");
            await Snap("slicer");

            var chart = page.Visuals.FirstOrDefault(v => v.Type == "clusteredBar" || v.Type == "clusteredColumn" || v.Type == "stackedColumn");
            if (chart != null)
            {
                var c = Surface.Container(chart.Id);
                var r = c.LastResults[0];
                var point = new DataPoint { Key = r.RowKey(0) };
                for (int g = 0; g < r.GroupCount; g++)
                    point.Fields.Add(new KeyValuePair<FieldRef, object>(r.Columns[g].Field, r.Group(g, 0).ToObject()));
                ((IVisualHost)c).Select(point, false);
                await AfterRefresh();
                await Snap("crossfilter");
                string crossed = Cards(page);
                Expect(crossed != slicered, $"Cross-filter on '{r.RowKey(0)}': cards {slicered} to {crossed}");
                Expect(Surface.ClearEscape(), "Esc clears the cross-filter");
                await AfterRefresh();
            }

            var task = AfterRefresh();
            _w.Reset();
            await task;
            Expect(Cards(page) == baseline && Report.Doc.Find(page.Id, slicer.Id).SlicerState == null, "Reset restores the cards");
        }

        /// <summary>Real mouse clicks on a bar and on bare page, then check boxes in a list slicer and the filter pane. WPF reads a click's position from the system cursor,
        /// so only a real cursor can test marks, and that part is skipped if someone is using the machine. Check boxes run their click path directly: see <see cref="Click"/>.</summary>
        private async Task RealClicks()
        {
            var page = Report.Doc.Pages.First(p => p.Visuals.Any(v => v.Type == "clusteredBar" && !v.IsHidden));
            await ShowPage(page);
            var li = new LastInput { Size = 8 };
            GetLastInputInfo(ref li);
            double idle = unchecked((uint)Environment.TickCount - li.Tick) / 1000.0;
            if (idle < 30) _log.Add($"Real mouse check SKIPPED: last input {idle:0} s ago");
            else
            {
                NativePoint home;
                GetCursorPos(out home);
                _w.Topmost = true;
                _w.Activate();
                try
                {
                    await Task.Delay(300);
                    var bar = page.Visuals.First(v => v.Type == "clusteredBar");
                    var c = Surface.Container(bar.Id);
                    // The chart is sorted by value, so the top band's bar reaches past this point.
                    var task = AfterRefresh();
                    await RealClick(c, c.ActualWidth * 0.3, c.ActualHeight * 0.16);
                    await task;
                    Expect(Surface.SelectionKeysFor(bar.Id).Count == 1, "Real click on a bar selects it");
                    await Snap("realclick");

                    // Below the last visual there is bare page, unless the page is full.
                    double y = page.Visuals.Max(v => v.Y + v.Height) + 2;
                    if (y < page.Height)
                    {
                        task = AfterRefresh();
                        await RealClick(Surface.PageSurface, page.Width / 2, y + 1);
                        await task;
                        Expect(Surface.SelectionKeysFor(bar.Id).Count == 0, "Real click on bare page clears the cross-filter");
                    }
                }
                finally
                {
                    _w.Topmost = false;
                    SetCursorPos(home.X, home.Y);
                }
                Surface.ClearSelection();
                await Task.Delay(300);
            }

            var listPage = Report.Doc.Pages.FirstOrDefault(p => p.Visuals.Any(v => v.Type == "slicer" && !((string)v.Format["Style"] == "Dropdown")));
            if (listPage != null)
            {
                await ShowPage(listPage);
                var slicer = listPage.Visuals.First(v => v.Type == "slicer" && (string)v.Format["Style"] != "Dropdown");
                string before = Results(listPage);
                var box = Descendants<CheckBox>(Surface.Container(slicer.Id)).First(b => b.IsChecked != true);
                var task = AfterRefresh();
                Click(box);
                await task;
                Expect(Results(listPage) != before, $"List slicer box '{box.Content}' changes the page's results");
                await Snap("realslicer");
                task = AfterRefresh();
                _w.Reset();
                await task;
                Expect(Results(listPage) == before, "Reset undoes the click");
            }

            // The filter pane: the report has none of its own, so one goes in memory for a moment, and Reset takes it away.
            var filtered = Report.Doc.Pages.First(p => p.Visuals.Any(v => v.Type == "slicer"));
            await ShowPage(filtered);
            var field = filtered.Visuals.First(v => v.Type == "slicer").Role("field")[0];
            string start = Cards(filtered);
            _w.SetFiltersShown(true);
            var filterTask = AfterRefresh();
            Report.Edit("Add filter", () => filtered.PageFilters.Add(new FilterDef { Target = field, Type = FilterType.Basic }));
            await filterTask;
            var check = Descendants<CheckBox>(_w).First(b => Descendants<ReportSurface>(_w).All(s => !s.IsAncestorOf(b)));
            filterTask = AfterRefresh();
            Click(check);
            await filterTask;
            Expect(Cards(filtered) != start, $"Filter pane value: cards {start} to {Cards(filtered)}");
            await Snap("filters");
            filterTask = AfterRefresh();
            _w.Reset();
            await filterTask;
            _w.SetFiltersShown(false);
            Expect(Cards(filtered) == start && filtered.PageFilters.Count == 0, "Reset removes the filter");
        }

        /// <summary>Focus mode and the table window, which reach the shared surface through the right-click menu.</summary>
        private async Task Other()
        {
            var page = Report.Doc.Pages[0];
            await ShowPage(page);
            var c = Surface.Container(page.Visuals.First(v => v.Type != "card" && v.Type != "slicer").Id);
            Surface.ToggleFocus(c);
            await Task.Delay(400);
            await Snap("focus");
            Expect(Surface.ClearEscape(), "Esc leaves focus mode");
            await Task.Delay(300);
            Surface.ShowAsTable(c);
            await Task.Delay(300);
            var table = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.Title == "Show as table");
            Expect(table != null, "Show as table opens");
            table?.Close();
            _w.GoToPage(page.Id);
            await Settle();
        }

        /// <summary>Reloads from the source, then checks the rows and that a page still draws. A missing source is reported, not failed: the check also runs where the CSV is not present.</summary>
        private async Task Refresh()
        {
            var sources = Report.Model.Tables.Where(t => t.Source?.Path != null).Select(t => t.Source.Path).ToList();
            if (sources.Any(s => !File.Exists(s))) { _log.Add("Refresh SKIPPED: source not found: " + string.Join(", ", sources)); return; }
            long before = Report.Tables.Values.Sum(t => (long)t.RowCount);
            var sw = Stopwatch.StartNew();
            var failures = await _w.RefreshDataAsync();
            await Task.Delay(500);
            await Settle();
            long after = Report.Tables.Values.Sum(t => (long)t.RowCount);
            Expect(failures.Count == 0 && after == before, $"Refresh in {sw.Elapsed.TotalSeconds:0.0} s: {before:#,0} rows to {after:#,0}");
            await Snap("refreshed");
        }

        /// <summary>Shows a page and waits until it has drawn.</summary>
        private async Task ShowPage(PageDef page)
        {
            var task = AfterRefresh();
            _w.GoToPage(page.Id);
            await task;
            await Settle();
        }

        /// <summary>A task that ends after the next completed refresh, with a limit, so that a page that never refreshes fails a step and not the run.</summary>
        private Task AfterRefresh()
        {
            var done = new TaskCompletionSource<bool>();
            Action handler = null;
            handler = () => { Surface.Refreshed -= handler; done.TrySetResult(true); };
            Surface.Refreshed += handler;
            return Task.WhenAny(done.Task, Task.Delay(20000)).ContinueWith(t => Settle(), TaskScheduler.FromCurrentSynchronizationContext()).Unwrap();
        }

        /// <summary>Waits for the dispatcher to go idle.</summary>
        private async Task Settle()
        {
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            await Task.Delay(100);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        }

        /// <summary>The short values of the page's cards, for comparing states. Reads the results, because cards draw straight to a visual. Long text cards are captions, and are left out.</summary>
        private string Cards(PageDef page) => string.Join(" | ", page.Visuals.Where(v => v.Type == "card").Select(CardText).Where(t => t.Length < 24));

        /// <summary>Every visual's row count and last cell, for telling whether a page's data moved. Slicers are left out, since they do not filter themselves.</summary>
        private string Results(PageDef page) => string.Join(" | ", page.Visuals.Where(v => v.Type != "slicer").Select(v => Surface.Container(v.Id).LastResults?.FirstOrDefault())
            .Where(r => r != null && r.Error == null && r.RowCount > 0).Select(r => r.RowCount + ":" + r.Data.Last()[r.RowCount - 1].ToDisplayString()));

        /// <summary>A card's value as text.</summary>
        private string CardText(VisualDef card)
        {
            var r = Surface.Container(card.Id).LastResults?.FirstOrDefault();
            return r == null || r.RowCount == 0 || r.Error != null ? "(none)" : PowerBiFormat.Format(r.Data[r.GroupCount][0], r.Columns[r.GroupCount].FormatString);
        }

        /// <summary>Photographs the whole window.</summary>
        private async Task Snap(string name)
        {
            await Settle();
            var root = (FrameworkElement)_w.Content;
            var bmp = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(root);
            File.WriteAllBytes(_stem + "-" + name + ".png", Exporter.Png(bmp));
        }

        /// <summary>Logs a step.</summary>
        private void Expect(bool ok, string text)
        {
            _log.Add((ok ? "ok    " : "FAIL  ") + text);
            if (!ok) _failed++;
        }

        /// <summary>SHA-256 of a file.</summary>
        private static string Hash(string path)
        {
            using (var sha = SHA256.Create())
            using (var fs = File.OpenRead(path))
                return Convert.ToBase64String(sha.ComputeHash(fs));
        }

        /// <summary>All elements of a type under a root.</summary>
        private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is T t) yield return t;
                foreach (var d in Descendants<T>(child)) yield return d;
            }
        }

        /// <summary>Runs a button's click path, for controls that a real cursor would have to scroll to.</summary>
        private static void Click(ButtonBase button) =>
            typeof(ButtonBase).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(button, null);

        /// <summary>Moves the cursor to a point in an element and clicks, with pauses long enough for WPF to see each step.</summary>
        private static async Task RealClick(FrameworkElement e, double x, double y)
        {
            var p = e.PointToScreen(new Point(x, y));
            await Task.Run(() =>
            {
                SetCursorPos((int)p.X, (int)p.Y);
                System.Threading.Thread.Sleep(120);
                mouse_event(MouseLeftDown, 0, 0, 0, UIntPtr.Zero);
                System.Threading.Thread.Sleep(60);
                mouse_event(MouseLeftUp, 0, 0, 0, UIntPtr.Zero);
                System.Threading.Thread.Sleep(120);
            });
        }

        private const uint MouseLeftDown = 0x0002, MouseLeftUp = 0x0004;
        [StructLayout(LayoutKind.Sequential)] private struct LastInput { public uint Size, Tick; }
        [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
        [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LastInput info);
        [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint p);
        [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
    }
}
