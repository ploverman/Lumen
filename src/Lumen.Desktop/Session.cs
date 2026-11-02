using System;
using System.Collections.Generic;
using System.Linq;
using Lumen.Connectors;
using Lumen.Engine;
using Lumen.Model;
using Lumen.Reporting;

namespace Lumen.Desktop
{
    /// <summary>The open report in the builder: <see cref="ReportSession"/> plus the undo stack.</summary>
    public sealed class Session : ReportSession
    {
        /// <summary>Undo and redo.</summary>
        public CommandStack Stack { get; }

        /// <summary>Starts with an empty document.</summary>
        public Session()
        {
            Stack = new CommandStack(() => Doc);
            Stack.Changed += (s, e) => { if (!_quiet) RaiseChanged(true); };
        }

        // Set while recording a change that has already been applied live, so the stack's Changed event does not trigger a query.
        private bool _quiet;

        /// <summary>Runs a change as one undo step, by snapshot: everything authored fits in small JSON,
        /// and undo restores exactly what was there, whatever the change touched.</summary>
        public override void Edit(string label, Action change)
        {
            string before = ModelJson.Serialize(Doc);
            change();
            string after = ModelJson.Serialize(Doc);
            if (before == after) return;
            // The change already happened, so the first Do only reacts to it. Redo replays the snapshot.
            bool first = true;
            Stack.Execute(new DelegateReportCommand(label,
                d => { if (first) { first = false; AfterChange(); } else Restore(after); },
                d => Restore(before)));
        }

        /// <summary>Records bounds set live during a drag; nothing is re-queried.</summary>
        public void RecordBounds(string label, List<KeyValuePair<string, SetBoundsCommand.Bounds>> before, string pageId)
        {
            var after = before.Select(kv =>
            {
                var v = Doc.Find(pageId, kv.Key);
                return new KeyValuePair<string, SetBoundsCommand.Bounds>(kv.Key, new SetBoundsCommand.Bounds { X = v.X, Y = v.Y, W = v.Width, H = v.Height });
            }).ToList();
            Action<ReportDocument, List<KeyValuePair<string, SetBoundsCommand.Bounds>>> apply = (d, list) =>
            {
                foreach (var kv in list)
                {
                    var v = d.Find(pageId, kv.Key);
                    if (v == null) continue;
                    v.X = kv.Value.X; v.Y = kv.Value.Y; v.Width = kv.Value.W; v.Height = kv.Value.H;
                }
            };
            _quiet = true;
            Stack.Execute(new DelegateReportCommand(label, d => apply(d, after), d => apply(d, before)));
            _quiet = false;
            RaiseChanged(false);
        }

        /// <summary>Replaces authored content with a snapshot, keeping the Doc instance. The canvas then syncs by id.</summary>
        private void Restore(string json)
        {
            var d = ModelJson.Deserialize<ReportDocument>(json);
            Doc.Name = d.Name;
            Doc.Model = d.Model;
            Doc.Theme = d.Theme;
            Doc.Pages = d.Pages;
            Doc.ReportFilters = d.ReportFilters;
            AfterChange();
            ApplyTheme();
        }

        /// <summary>A new report starts with an empty history.</summary>
        protected override void OnReplaced() => Stack.Clear();
    }

    /// <summary>The warehouse sample: one dashboard page around the 3D visual, laid out like the Power BI page it mirrors.</summary>
    public static class WarehouseReport
    {
        /// <summary>Builds the document and its data.</summary>
        public static ReportDocument Build(out Dictionary<string, TableData> tables)
        {
            SemanticModel model;
            tables = WarehouseSample.BuildDataset(out model).Tables;
            var doc = new ReportDocument { Name = "Warehouse Occupancy 3D", Model = model };
            var table = model.TableByName("Locations");
            Func<string, FieldRef> col = c => FieldRef.ForColumn(table.Id, table.ColumnByName(c).Id);
            Func<string, FieldRef> m = n => FieldRef.ForMeasure(model.MeasureByName(n).Id);
            var page = new PageDef { Name = "Warehouse Occupancy" };

            // Slicers across the top. Their dropdown popups open over the page.
            string[] slicers = { "Zone", "Aisle", "Level", "Stock Age Band", "Status" };
            for (int i = 0; i < slicers.Length; i++)
            {
                var s = Add(page, "slicer", 216 + i * 164, 8, 156, 58, slicers[i], "field", col(slicers[i]));
                s.Format["Style"] = "Dropdown";
                s.Format["TitleSize"] = 10;
                s.Format["Padding"] = 2;
            }

            // Cards down the left, two by three.
            var cards = new[] { "Total Locations", "Filled", "Fill Rate", "Open Slots", "Rows Over 70%", "Avg Stock Age" };
            for (int i = 0; i < cards.Length; i++)
            {
                var c = Add(page, "card", 8 + (i % 2) * 100, 8 + (i / 2) * 84, 96, 80, null, "values", m(cards[i]));
                c.Format["ValueSize"] = 20;
                c.Format["Units"] = "None";
                c.Format["Padding"] = 2;
            }
            Add(page, "clusteredBar", 8, 264, 196, 448, "Fill rate by level", "category", col("Level"), "values", m("Fill Rate"));

            Add(page, "warehouse3d", 216, 74, 816, 638, null,
                "location", col("Location ID"), "zone", col("Zone"), "aisle", col("Aisle"), "bayRow", col("Bay Row"), "bay", col("Bay"),
                "level", col("Level"), "position", col("Position"), "status", col("Status"), "sku", col("SKU"), "units", col("Units"), "age", col("Stock Age Days"));

            Add(page, "clusteredBar", 1040, 8, 232, 196, "Fill rate by zone", "category", col("Zone"), "values", m("Fill Rate"));
            var fullest = Add(page, "table", 1040, 212, 232, 216, "Fullest aisles", "values", col("Aisle"), "values", m("Fill Rate"), "values", m("Open Slots"));
            fullest.VisualFilters.Add(new FilterDef { Target = col("Aisle"), Type = FilterType.TopN, TopCount = 6, TopBy = m("Fill Rate") });
            var open = Add(page, "table", 1040, 436, 232, 276, "Most open slots", "values", col("Aisle"), "values", m("Open Slots"), "values", m("Fill Rate"));
            open.VisualFilters.Add(new FilterDef { Target = col("Aisle"), Type = FilterType.TopN, TopCount = 9, TopBy = m("Open Slots") });

            doc.Pages.Add(page);
            return doc;
        }

        /// <summary>Adds a visual. <paramref name="pairs"/> is role name and field pairs.</summary>
        private static VisualDef Add(PageDef page, string type, double x, double y, double w, double h, string title, params object[] pairs)
        {
            var v = new VisualDef { Type = type, X = x, Y = y, Width = w, Height = h, ZIndex = page.Visuals.Count };
            if (title != null) v.Format["Title"] = title;
            for (int i = 0; i + 1 < pairs.Length; i += 2)
            {
                string role = (string)pairs[i];
                if (!v.Roles.ContainsKey(role)) v.Roles[role] = new List<FieldRef>();
                v.Roles[role].Add((FieldRef)pairs[i + 1]);
            }
            page.Visuals.Add(v);
            return v;
        }
    }

    /// <summary>The sample model plus a three-page report, for a first run with no data of your own.</summary>
    public static class SampleReport
    {
        /// <summary>Builds the sample document and its data.</summary>
        public static ReportDocument Build(out Dictionary<string, TableData> tables, int rows = 50000)
        {
            SemanticModel model;
            var ds = SampleData.BuildDataset(rows, out model);
            tables = ds.Tables;
            var doc = new ReportDocument { Name = "Sales sample", Model = model };

            Func<string, string, FieldRef> col = (t, c) =>
            {
                var def = model.TableByName(t);
                return FieldRef.ForColumn(def.Id, def.ColumnByName(c).Id);
            };
            Func<string, FieldRef> m = n => FieldRef.ForMeasure(model.MeasureByName(n).Id);

            var overview = new PageDef { Name = "Overview" };
            Add(overview, "card", 16, 16, 220, 110, "values", m("Sales Amount"));
            Add(overview, "card", 248, 16, 220, 110, "values", m("Margin %"));
            Add(overview, "card", 480, 16, 220, 110, "values", m("Orders"));
            Add(overview, "slicer", 1040, 16, 224, 200, Title("Region"), "field", col("Sales", "Region"));
            Add(overview, "clusteredColumn", 16, 138, 684, 280, Title("Sales by category"), "category", col("Product", "Category"), "values", m("Sales Amount"));
            Add(overview, "donut", 712, 16, 316, 402, Title("Share by region"), "category", col("Sales", "Region"), "values", m("Sales Amount"));
            var line = Add(overview, "line", 16, 430, 1012, 274, Title("Sales by month"), "category", col("Date", "Month"), "values", m("Sales Amount"), "values", m("Sales LY"));
            Add(overview, "slicer", 1040, 228, 224, 200, Title("Color"), "field", col("Product", "Color"));
            Add(overview, "multiRowCard", 1040, 440, 224, 264, null, "category", col("Product", "Category"), "values", m("Sales Amount"), "values", m("Margin %"));

            var products = new PageDef { Name = "Products" };
            Add(products, "table", 16, 16, 620, 330, Title("Products"), "values", col("Product", "Product"), "values", col("Product", "Color"), "values", m("Sales Amount"), "values", m("Margin %"));
            Add(products, "clusteredBar", 648, 16, 616, 330, Title("Top products"), "category", col("Product", "Product"), "values", m("Sales Amount"));
            Add(products, "matrix", 16, 358, 620, 346, Title("Category by year"), "rows", col("Product", "Category"), "rows", col("Product", "Product"), "columns", col("Date", "Year"), "values", m("Sales Amount"));
            Add(products, "scatter", 648, 358, 616, 346, Title("Sales vs margin"), "details", col("Product", "Product"), "x", m("Sales Amount"), "y", m("Margin %"), "size", m("Orders"));
            products.PageFilters.Add(new FilterDef { Target = col("Product", "Product"), Type = FilterType.TopN, TopCount = 15, TopBy = m("Sales Amount") });

            var time = new PageDef { Name = "Time" };
            var slicer = Add(time, "slicer", 16, 16, 300, 100, Title("Year"), "field", col("Date", "Year"));
            slicer.Format["Style"] = "Dropdown";
            Add(time, "card", 328, 16, 300, 100, "values", m("Sales YTD"));
            Add(time, "card", 640, 16, 300, 100, "values", m("Red Sales"));
            Add(time, "card", 952, 16, 312, 100, "values", m("All-Product Share"));
            Add(time, "area", 16, 128, 1248, 280, Title("Year to date"), "category", col("Date", "Month"), "values", m("Sales YTD"));
            Add(time, "stackedColumn", 16, 420, 800, 284, Title("Month by category"), "category", col("Date", "Month"), "legend", col("Product", "Category"), "values", m("Sales Amount"));
            Add(time, "pie", 828, 420, 436, 284, Title("Color mix"), "category", col("Product", "Color"), "values", m("Sales Amount"));

            doc.Pages.AddRange(new[] { overview, products, time });
            return doc;
        }

        /// <summary>Marks a string as a title for <see cref="Add"/>.</summary>
        private static string Title(string t) => "T:" + t;

        /// <summary>Adds a visual. <paramref name="pairs"/> is an optional title followed by role name and field pairs; an odd count means the first is the title.</summary>
        private static VisualDef Add(PageDef page, string type, double x, double y, double w, double h, params object[] pairs)
        {
            var v = new VisualDef { Type = type, X = x, Y = y, Width = w, Height = h, ZIndex = page.Visuals.Count };
            int start = 0;
            if (pairs.Length % 2 == 1)
            {
                var title = pairs[0] as string;
                if (title != null) v.Format["Title"] = title.Substring(2);
                start = 1;
            }
            for (int i = start; i + 1 < pairs.Length; i += 2)
            {
                string role = (string)pairs[i];
                if (!v.Roles.ContainsKey(role)) v.Roles[role] = new List<FieldRef>();
                v.Roles[role].Add((FieldRef)pairs[i + 1]);
            }
            page.Visuals.Add(v);
            return v;
        }
    }
}
