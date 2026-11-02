using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using Lumen.Engine;
using Lumen.Model;
using Lumen.Visuals;

namespace Lumen.Reporting
{
    /// <summary>An open report without an editor: document, data, engine and theme. The viewer uses this class as it is.
    /// The builder derives Session from it to add undo, so everything that shows a report works with either.</summary>
    public class ReportSession
    {
        /// <summary>The authored report. The instance changes when a file is opened.</summary>
        public ReportDocument Doc { get; protected set; } = new ReportDocument();
        /// <summary>The query engine, one per session.</summary>
        public InProcessEngine Engine { get; } = new InProcessEngine();
        /// <summary>Row data by table id. Deliberately outside undo: reverting a model edit must not reload or discard data.</summary>
        public Dictionary<string, TableData> Tables { get; private set; } = new Dictionary<string, TableData>();
        /// <summary>The active theme as colors.</summary>
        public ResolvedTheme Theme { get; private set; } = ResolvedTheme.Light();
        /// <summary>Path of the open file, or null if never saved.</summary>
        public string FilePath { get; set; }

        private readonly Dictionary<string, Color> _seriesColors = new Dictionary<string, Color>();
        // Model as of the last dataset build, for telling whether an edit touched it.
        private string _modelJson;

        /// <summary>Raised after any change. Positions-only changes pass false, so nothing is re-queried.</summary>
        public event Action<bool> Changed;
        /// <summary>Raised after the theme changes.</summary>
        public event Action ThemeChanged;

        /// <summary>Starts with an empty document.</summary>
        public ReportSession()
        {
            RebuildDataset();
        }

        /// <summary>The engine's current dataset.</summary>
        public Dataset Dataset => Engine.Dataset;
        /// <summary>The current model.</summary>
        public SemanticModel Model => Doc.Model;

        /// <summary>Makes a new Dataset from the model and tables. Caches die with the old one, so measures recompile and queries rerun.</summary>
        public void RebuildDataset()
        {
            var tables = Doc.Model.Tables.Where(t => Tables.ContainsKey(t.Id)).Select(t => Tables[t.Id]);
            Engine.Load(new Dataset(Doc.Model, tables));
            _modelJson = ModelJson.Serialize(Doc.Model);
        }

        /// <summary>Applies a change to the in-memory document and tells the listeners. Nothing is written to disk and nothing can be undone here;
        /// the builder overrides this with its undo stack. The filter pane calls it, so the same pane runs in both applications.</summary>
        public virtual void Edit(string label, Action change)
        {
            change();
            AfterChange();
            RaiseChanged(true);
        }

        /// <summary>Raises <see cref="Changed"/> for a derived class.</summary>
        protected void RaiseChanged(bool dataAffecting) => Changed?.Invoke(dataAffecting);

        /// <summary>Rebuilds the dataset only if the model changed. Comparing JSON is crude, but cheap next to a needless rebuild.</summary>
        protected void AfterChange()
        {
            if (ModelJson.Serialize(Doc.Model) != _modelJson) RebuildDataset();
        }

        /// <summary>Swaps in a whole new report, as File Open does.</summary>
        public void Replace(ReportDocument doc, Dictionary<string, TableData> tables, string path)
        {
            Doc = doc;
            Tables = tables;
            FilePath = path;
            _seriesColors.Clear();
            RebuildDataset();
            OnReplaced();
            ApplyTheme();
        }

        /// <summary>Runs inside <see cref="Replace"/>, after the data is in and before the theme is applied. The builder clears its history here.</summary>
        protected virtual void OnReplaced() { }

        /// <summary>Resolves the theme from the document and applies it. Windows high contrast overrides a built-in choice, but not an imported theme.</summary>
        public void ApplyTheme()
        {
            var t = ResolvedTheme.FromName(Doc.Theme.BuiltIn);
            if (SystemParameters.HighContrast && string.IsNullOrEmpty(Doc.Theme.CustomJson)) t = ResolvedTheme.HighContrast();
            if (!string.IsNullOrEmpty(Doc.Theme.CustomJson))
            {
                try { t = ResolvedTheme.FromPowerBiJson(Doc.Theme.CustomJson, t); }
                catch (Newtonsoft.Json.JsonException) { }
            }
            Theme = t;
            _seriesColors.Clear();
            ThemeService.Apply(t);
            ThemeChanged?.Invoke();
        }

        /// <summary>Series keep their colour across visuals: first seen, first colour.</summary>
        public Color ColorFor(string key)
        {
            Color c;
            if (_seriesColors.TryGetValue(key, out c)) return c;
            c = Theme.DataColors[_seriesColors.Count % Theme.DataColors.Length];
            _seriesColors[key] = c;
            return c;
        }
    }

    /// <summary>Pushes a theme into application resources. Everything uses DynamicResource, so the chrome restyles in place.</summary>
    public static class ThemeService
    {
        /// <summary>Sets the theme brushes. AccentFg is picked by luminance, so text on the accent stays readable.</summary>
        public static void Apply(ResolvedTheme t)
        {
            var res = Application.Current.Resources;
            Action<string, Color> set = (k, c) => { var b = new SolidColorBrush(c); b.Freeze(); res[k] = b; };
            set("ChromeBg", t.ChromeBackground);
            set("PanelBg", t.ChromePanel);
            set("ChromeFg", t.ChromeForeground);
            set("ChromeFg2", t.Secondary);
            set("CanvasBg", t.Background);
            set("Accent", t.Accent);
            set("Line", t.Border);
            set("AccentFg", 0.299 * t.Accent.R + 0.587 * t.Accent.G + 0.114 * t.Accent.B > 150 ? Colors.Black : Colors.White);
            // Stock control templates read these keys.
            res[SystemColors.WindowTextBrushKey] = res["ChromeFg"];
            res[SystemColors.ControlTextBrushKey] = res["ChromeFg"];
            res[SystemColors.WindowBrushKey] = res["PanelBg"];
            res[SystemColors.ControlBrushKey] = res["ChromeBg"];
        }
    }

    /// <summary>.lumen package: a ZIP of report.json, model.json and one binary snapshot per table.</summary>
    public static class LumenFile
    {
        /// <summary>Writes the package. It goes to a temp file first, so a failed write leaves the old file alone.
        /// The final delete and move are two steps, so this is not fully atomic.</summary>
        public static void Save(string path, ReportDocument doc, IDictionary<string, TableData> tables, byte[] thumbnail)
        {
            string temp = path + ".tmp";
            using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                WriteText(zip, "report.json", ModelJson.Serialize(doc));
                WriteText(zip, "model.json", ModelJson.Serialize(doc.Model));
                foreach (var def in doc.Model.Tables)
                {
                    TableData data;
                    if (!tables.TryGetValue(def.Id, out data)) continue;
                    var entry = zip.CreateEntry("data/" + def.Id + ".col", CompressionLevel.Fastest);
                    using (var s = entry.Open())
                    using (var w = new BinaryWriter(s))
                    {
                        w.Write(def.Columns.Count);
                        foreach (var c in def.Columns)
                        {
                            w.Write(c.Id);
                            ColumnIO.WriteColumn(w, data.ById[c.Id]);
                        }
                    }
                }
                if (thumbnail != null)
                {
                    var entry = zip.CreateEntry("thumbnails/page1.png", CompressionLevel.NoCompression);
                    using (var s = entry.Open()) s.Write(thumbnail, 0, thumbnail.Length);
                }
            }
            if (File.Exists(path)) File.Delete(path);
            File.Move(temp, path);
        }

        /// <summary>Writes a UTF-8 text entry.</summary>
        private static void WriteText(ZipArchive zip, string name, string text)
        {
            var entry = zip.CreateEntry(name, CompressionLevel.Fastest);
            using (var w = new StreamWriter(entry.Open())) w.Write(text);
        }

        /// <summary>Reads a package. A missing data entry leaves that table out, so its visuals report a missing field rather than failing the open.</summary>
        public static ReportDocument Open(string path, out Dictionary<string, TableData> tables)
        {
            tables = new Dictionary<string, TableData>();
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Read))
            {
                var report = zip.GetEntry("report.json");
                if (report == null) throw new InvalidDataException("This is not a Lumen report");
                ReportDocument doc;
                using (var r = new StreamReader(report.Open()))
                    doc = ModelJson.Deserialize<ReportDocument>(r.ReadToEnd());
                if (doc.SchemaVersion > 1)
                    throw new InvalidOperationException("This file was saved by a newer version of Lumen");

                foreach (var def in doc.Model.Tables)
                {
                    var entry = zip.GetEntry("data/" + def.Id + ".col");
                    if (entry == null) continue;
                    var data = new TableData(def.Id, def.Name);
                    using (var r = new BinaryReader(entry.Open()))
                    {
                        int n = r.ReadInt32();
                        for (int i = 0; i < n; i++)
                        {
                            string id = r.ReadString();
                            data.Add(id, ColumnIO.ReadColumn(r));
                        }
                    }
                    tables[def.Id] = data;
                }
                return doc;
            }
        }
    }
}
