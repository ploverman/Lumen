using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Lumen.Connectors;
using Lumen.Engine;
using Lumen.Model;
using Xunit;

namespace Lumen.Engine.Tests
{
    /// <summary>Golden values are recomputed independently here by a plain LINQ walk over the same rows.</summary>
    public class EngineTests
    {
        /// <summary>One denormalized sales row, read from the columns and used as the independent answer.</summary>
        private sealed class Row { public DateTime Date; public string Region, Category, Color; public long Qty; public double Price, Cost; public double Amount => Qty * Price; }

        // The small fixture runs serial code paths. The big one crosses the parallel threshold of Par.Chunk * 2 rows.
        private static readonly Lazy<Fixture> F = new Lazy<Fixture>(() => new Fixture(20000));
        private static readonly Lazy<Fixture> Big = new Lazy<Fixture>(() => new Fixture(400000));

        /// <summary>A sample dataset, its engine, and the same rows as plain objects.</summary>
        private sealed class Fixture
        {
            public readonly Dataset Ds;
            public readonly SemanticModel Model;
            public readonly InProcessEngine Engine = new InProcessEngine();
            public readonly List<Row> Rows = new List<Row>();

            public Fixture(int rows)
            {
                Ds = SampleData.BuildDataset(rows, out Model);
                Engine.Load(Ds);
                var s = Ds.Tables["sales"]; var p = Ds.Tables["product"];
                Func<TableData, string, Column> col = (t, n) => t.Columns.First(c => c.Name == n);
                var keyToRow = new Dictionary<long, int>();
                for (int r = 0; r < p.RowCount; r++) keyToRow[col(p, "ProductKey").Get(r).Integer] = r;
                for (int r = 0; r < s.RowCount; r++)
                {
                    int pr = keyToRow[col(s, "ProductKey").Get(r).Integer];
                    Rows.Add(new Row
                    {
                        Date = col(s, "OrderDate").Get(r).AsDate,
                        Region = col(s, "Region").Get(r).String,
                        Category = col(p, "Category").Get(pr).String,
                        Color = col(p, "Color").Get(pr).String,
                        Qty = col(s, "Qty").Get(r).Integer,
                        Price = col(s, "UnitPrice").Get(r).Number,
                        Cost = col(s, "UnitCost").Get(r).Number
                    });
                }
            }

            /// <summary>A column reference by names.</summary>
            public FieldRef Col(string table, string name)
            {
                var t = Model.TableByName(table);
                return FieldRef.ForColumn(t.Id, t.ColumnByName(name).Id);
            }

            /// <summary>A measure reference by name.</summary>
            public FieldRef Measure(string name) => FieldRef.ForMeasure(Model.MeasureByName(name).Id);

            /// <summary>Runs a query and fails the test on an error.</summary>
            public QueryResult Run(QuerySpec spec)
            {
                var r = Engine.Execute(spec, CancellationToken.None);
                Assert.Null(r.Error);
                return r;
            }
        }

        /// <summary>Result rows as a map from row key to measure value.</summary>
        private static Dictionary<string, double> ByGroup(QueryResult r, int measure = 0)
        {
            var d = new Dictionary<string, double>();
            for (int i = 0; i < r.RowCount; i++) d[r.RowKey(i)] = r.Measure(measure, i).AsDouble();
            return d;
        }

        // Above Par.Chunk * 2 rows the engine scans in parallel, uses dense partitions and the foreign key index.
        /// <summary>Parallel scan, dense partition and FK index give the same totals as LINQ.</summary>
        [Fact]
        public void LargeTablePathsMatchLinq()
        {
            var f = Big.Value;
            var north = FilterDef.Basic(f.Col("Sales", "Region"), new object[] { "North" });
            var r = f.Run(new QuerySpec { GroupBy = { f.Col("Product", "Category") }, Measures = { f.Measure("Sales Amount") }, Filters = { north } });
            var actual = ByGroup(r);
            var expected = f.Rows.Where(x => x.Region == "North").GroupBy(x => x.Category).ToDictionary(g => g.Key, g => g.Sum(x => x.Amount));
            Assert.Equal(expected.Count, actual.Count);
            foreach (var kv in expected) Assert.Equal(kv.Value, actual[kv.Key], 3);
        }

        /// <summary>A date filter narrow enough to use the inverted index, including a time-intelligence measure.</summary>
        [Fact]
        public void NarrowDateFilterThroughTheIndexMatchesLinq()
        {
            var f = Big.Value;
            var north = FilterDef.Basic(f.Col("Sales", "Region"), new object[] { "North" });
            var r = f.Run(new QuerySpec { GroupBy = { f.Col("Date", "Year"), f.Col("Date", "Month") }, Measures = { f.Measure("Sales Amount"), f.Measure("Sales LY") }, Filters = { north }, Limit = 100 });
            var current = ByGroup(r, 0);
            var last = ByGroup(r, 1);
            var culture = System.Globalization.CultureInfo.InvariantCulture;
            var rows = f.Rows.Where(x => x.Region == "North").ToList();
            Func<int, int, double> month = (y, m) => rows.Where(x => x.Date.Year == y && x.Date.Month == m).Sum(x => x.Amount);
            for (int m = 1; m <= 12; m++)
            {
                string key = "2025\u001f" + new DateTime(2025, m, 1).ToString("MMM", culture);
                Assert.Equal(month(2025, m), current[key], 3);
                Assert.Equal(month(2024, m), last[key], 3);
            }
        }

        /// <summary>Two identical specs with different filter ids hit one cached result.</summary>
        [Fact]
        public void ResultCacheIgnoresTheFilterId()
        {
            var f = Big.Value;
            Func<QuerySpec> spec = () => new QuerySpec
            {
                GroupBy = { f.Col("Product", "Color") }, Measures = { f.Measure("Orders") },
                Filters = { FilterDef.Basic(f.Col("Sales", "Region"), new object[] { "South" }) }
            };
            Assert.Same(f.Run(spec()), f.Run(spec()));
        }

        /// <summary>The SUMX kernel, per group and in total.</summary>
        [Fact]
        public void SumxMatchesByCategory()
        {
            var f = F.Value;
            var r = f.Run(new QuerySpec { GroupBy = { f.Col("Product", "Category") }, Measures = { f.Measure("Sales Amount") }, IncludeTotals = true });
            var actual = ByGroup(r);
            var expected = f.Rows.GroupBy(x => x.Category).ToDictionary(g => g.Key, g => g.Sum(x => x.Amount));
            Assert.Equal(expected.Count, actual.Count);
            foreach (var kv in expected) Assert.Equal(kv.Value, actual[kv.Key], 4);
            Assert.Equal(f.Rows.Sum(x => x.Amount), r.Totals[0].AsDouble(), 4);
        }

        /// <summary>A ratio's total is evaluated, not added up from the groups.</summary>
        [Fact]
        public void MarginRatioTotalIsNotSumOfGroups()
        {
            var f = F.Value;
            var r = f.Run(new QuerySpec { GroupBy = { f.Col("Product", "Category") }, Measures = { f.Measure("Margin %") }, IncludeTotals = true });
            double amount = f.Rows.Sum(x => x.Amount), cost = f.Rows.Sum(x => x.Qty * x.Cost);
            Assert.Equal((amount - cost) / amount, r.Totals[0].AsDouble(), 8);
        }

        /// <summary>A CALCULATE predicate overrides its column and leaves other filters alone.</summary>
        [Fact]
        public void CalculateColumnFilterKeepsOtherFilters()
        {
            var f = F.Value;
            var r = f.Run(new QuerySpec { GroupBy = { f.Col("Sales", "Region") }, Measures = { f.Measure("Red Sales") } });
            var expected = f.Rows.Where(x => x.Color == "Red").GroupBy(x => x.Region).ToDictionary(g => g.Key, g => g.Sum(x => x.Amount));
            var actual = ByGroup(r);
            foreach (var kv in expected) Assert.Equal(kv.Value, actual[kv.Key], 4);
        }

        /// <summary>ALL(table) removes that table's filters, while filters elsewhere stay.</summary>
        [Fact]
        public void AllRemovesProductFiltersButKeepsRegionFilter()
        {
            var f = F.Value;
            var spec = new QuerySpec
            {
                GroupBy = { f.Col("Product", "Category") },
                Measures = { f.Measure("All-Product Share") },
                Filters = { FilterDef.Basic(f.Col("Sales", "Region"), new object[] { "North" }) }
            };
            var actual = ByGroup(f.Run(spec));
            var north = f.Rows.Where(x => x.Region == "North").ToList();
            double total = north.Sum(x => x.Amount);
            foreach (var g in north.GroupBy(x => x.Category))
                Assert.Equal(g.Sum(x => x.Amount) / total, actual[g.Key], 8);
        }

        /// <summary>TOTALYTD sums from January through the month in context.</summary>
        [Fact]
        public void TotalYtdAccumulatesWithinYear()
        {
            var f = F.Value;
            var spec = new QuerySpec
            {
                GroupBy = { f.Col("Date", "Year"), f.Col("Date", "Month") },
                Measures = { f.Measure("Sales YTD") },
            };
            var r = f.Run(spec);
            var map = ByGroup(r);
            double march2025 = f.Rows.Where(x => x.Date.Year == 2025 && x.Date.Month <= 3).Sum(x => x.Amount);
            Assert.Equal(march2025, map["2025\u001fMar"], 4);
        }

        /// <summary>SAMEPERIODLASTYEAR shifts the date filter back one year.</summary>
        [Fact]
        public void SamePeriodLastYearShiftsTheYear()
        {
            var f = F.Value;
            var r = f.Run(new QuerySpec { GroupBy = { f.Col("Date", "Year") }, Measures = { f.Measure("Sales LY") } });
            var map = ByGroup(r);
            Assert.Equal(f.Rows.Where(x => x.Date.Year == 2024).Sum(x => x.Amount), map["2025"], 4);
        }

        /// <summary>FILTER over a row expression that touches two columns.</summary>
        [Fact]
        public void FilterIteratorOverExpression()
        {
            var f = F.Value;
            var m = new MeasureDef { Id = "big", Name = "Big", Expression = "CALCULATE(COUNTROWS(Sales), FILTER(Sales, Sales[Qty] >= 4 && Sales[UnitPrice] > 100))" };
            f.Model.Measures.Add(m);
            var r = f.Run(new QuerySpec { Measures = { FieldRef.ForMeasure("big") } });
            f.Model.Measures.Remove(m);
            Assert.Equal(f.Rows.Count(x => x.Qty >= 4 && x.Price > 100), (int)r.Data[0][0].AsDouble());
        }

        /// <summary>VAR, SWITCH(TRUE(), ...) and nested IF.</summary>
        [Fact]
        public void VarReturnSwitchIf()
        {
            var f = F.Value;
            var m = new MeasureDef
            {
                Id = "tier", Name = "Tier",
                Expression = "VAR s = [Sales Amount] VAR t = 1000 RETURN SWITCH(TRUE(), s > t, \"High\", s > 0, IF(s > 1, \"Low\", \"Tiny\"), \"None\")"
            };
            f.Model.Measures.Add(m);
            var r = f.Run(new QuerySpec { Measures = { FieldRef.ForMeasure("tier") } });
            f.Model.Measures.Remove(m);
            Assert.Equal("High", r.Data[0][0].String);
        }

        /// <summary>Month names order by their sort column, not alphabetically.</summary>
        [Fact]
        public void SortByColumnOrdersMonths()
        {
            var f = F.Value;
            var r = f.Run(new QuerySpec { GroupBy = { f.Col("Date", "Month") }, Measures = { f.Measure("Orders") } });
            Assert.Equal("Jan", r.Group(0, 0).String);
            Assert.Equal("Feb", r.Group(0, 1).String);
            Assert.Equal("Dec", r.Group(0, 11).String);
        }

        /// <summary>A TopN filter keeps the right members.</summary>
        [Fact]
        public void TopNFilterKeepsBestProducts()
        {
            var f = F.Value;
            var filter = new FilterDef { Target = f.Col("Product", "Product"), Type = FilterType.TopN, TopCount = 3, TopBy = f.Measure("Sales Amount") };
            var r = f.Run(new QuerySpec { GroupBy = { f.Col("Product", "Product") }, Measures = { f.Measure("Sales Amount") }, Filters = { filter } });
            Assert.Equal(3, r.RowCount);
            var all = ByGroup(f.Run(new QuerySpec { GroupBy = { f.Col("Product", "Product") }, Measures = { f.Measure("Sales Amount") } }));
            var best = all.OrderByDescending(kv => kv.Value).Take(3).Select(kv => kv.Key).OrderBy(x => x);
            Assert.Equal(best, Enumerable.Range(0, 3).Select(i => r.RowKey(i)).OrderBy(x => x));
        }

        /// <summary>Highlight values line up with the base rows.</summary>
        [Fact]
        public void HighlightAlignsWithRows()
        {
            var f = F.Value;
            var r = f.Run(new QuerySpec
            {
                GroupBy = { f.Col("Product", "Category") },
                Measures = { f.Measure("Sales Amount") },
                Highlight = { FilterDef.Basic(f.Col("Sales", "Region"), new object[] { "West" }) }
            });
            Assert.NotNull(r.Highlight);
            for (int i = 0; i < r.RowCount; i++)
            {
                string cat = r.Group(0, i).String;
                Assert.Equal(f.Rows.Where(x => x.Category == cat && x.Region == "West").Sum(x => x.Amount), r.Highlight[0][i].AsDouble(), 4);
            }
        }

        /// <summary>Each SummarizeBy kind of implicit measure.</summary>
        [Fact]
        public void ImplicitAggregations()
        {
            var f = F.Value;
            var qty = f.Col("Sales", "Qty");
            var region = f.Col("Sales", "Region");
            var measures = new[] { SummarizeBy.Sum, SummarizeBy.Average, SummarizeBy.Min, SummarizeBy.Max, SummarizeBy.Count }
                .Select(a => FieldRef.ForColumn(qty.TableId, qty.ColumnId, a)).ToList();
            measures.Add(FieldRef.ForColumn(region.TableId, region.ColumnId, SummarizeBy.DistinctCount));
            var r = f.Run(new QuerySpec { Measures = measures });
            Assert.Equal(f.Rows.Sum(x => x.Qty), r.Data[0][0].AsDouble(), 6);
            Assert.Equal(f.Rows.Average(x => x.Qty), r.Data[1][0].AsDouble(), 6);
            Assert.Equal(1, r.Data[2][0].AsDouble());
            Assert.Equal(5, r.Data[3][0].AsDouble());
            Assert.Equal(f.Rows.Count, r.Data[4][0].AsDouble());
            Assert.Equal(4, r.Data[5][0].AsDouble());
        }

        /// <summary>Errors carry line and column; a bare column outside any row context is an error.</summary>
        [Fact]
        public void DiagnosticsCarryPosition()
        {
            var f = F.Value;
            var d = LqlCompiler.Validate(f.Ds, "SUM(Sales[Nope])");
            Assert.Single(d);
            Assert.Equal(1, d[0].Line);
            Assert.Equal(5, d[0].Column);
            Assert.Empty(LqlCompiler.Validate(f.Ds, "SUM(Sales[Qty])"));
            Assert.Single(LqlCompiler.Validate(f.Ds, "Sales[Qty] + 1"));
        }

        /// <summary>DIVIDE by zero gives BLANK or the alternate, and BLANK acts as zero in addition.</summary>
        [Fact]
        public void BlankAndDivideRules()
        {
            var f = F.Value;
            var m = new MeasureDef { Id = "d0", Name = "D0", Expression = "DIVIDE(10, 0)" };
            var n = new MeasureDef { Id = "d1", Name = "D1", Expression = "DIVIDE(10, 0, -1) + BLANK()" };
            f.Model.Measures.Add(m); f.Model.Measures.Add(n);
            var r = f.Run(new QuerySpec { Measures = { FieldRef.ForMeasure("d0"), FieldRef.ForMeasure("d1") } });
            f.Model.Measures.Remove(m); f.Model.Measures.Remove(n);
            Assert.True(r.Data[0][0].IsBlank);
            Assert.Equal(-1, r.Data[1][0].AsDouble());
        }

        /// <summary>Fact rows with no dimension match form a BLANK group, which sorts first.</summary>
        [Fact]
        public void OrphansGroupAsBlank()
        {
            var fact = new TableDef { Id = "f", Name = "F", Columns = { new ColumnDef { Id = "k", Name = "K", DataType = LumenDataType.Int64 }, new ColumnDef { Id = "v", Name = "V", DataType = LumenDataType.Double } } };
            var dim = new TableDef { Id = "d", Name = "D", Columns = { new ColumnDef { Id = "k", Name = "K", DataType = LumenDataType.Int64 }, new ColumnDef { Id = "n", Name = "N", DataType = LumenDataType.String } } };
            var k = new Int64Builder(3); k.Add(1); k.Add(2); k.Add(99);
            var v = new DoubleBuilder(3); v.Add(10); v.Add(20); v.Add(30);
            var dk = new Int64Builder(2); dk.Add(1); dk.Add(2);
            var dn = new DictBuilder(); dn.Add("One"); dn.Add("Two");
            var ft = new TableData("f", "F"); ft.Add("k", k.Finish("K")); ft.Add("v", v.Finish("V"));
            var dt = new TableData("d", "D"); dt.Add("k", dk.Finish("K")); dt.Add("n", dn.Finish("N"));
            var model = new SemanticModel { Tables = { fact, dim }, Relationships = { new RelationshipDef { Id = "r", FromTableId = "f", FromColumnId = "k", ToTableId = "d", ToColumnId = "k" } } };
            var ds = new Dataset(model, new[] { ft, dt });
            var r = new QueryScope(ds, CancellationToken.None).Execute(new QuerySpec
            {
                GroupBy = { FieldRef.ForColumn("d", "n") },
                Measures = { FieldRef.ForColumn("f", "v", SummarizeBy.Sum) }
            });
            Assert.Null(r.Error);
            Assert.Equal(3, r.RowCount);
            Assert.True(r.Group(0, 0).IsBlank);
            Assert.Equal(30, r.Measure(0, 0).AsDouble());
        }

        /// <summary>CSV quoting and type inference.</summary>
        [Fact]
        public void CsvParsesQuotesAndInfersTypes()
        {
            var csv = "Name,Qty,Price,When,Flag\n\"Smith, J\",3,1.5,2025-01-02,true\n\"He said \"\"hi\"\"\",4,2.5,2025-01-03,false\n";
            var b = CsvConnector.Read(new StringReader(csv), true, CancellationToken.None);
            Assert.Equal(2, b.RowCount);
            Assert.Equal("Smith, J", b.Find("Name").Get(0).String);
            Assert.Equal("He said \"hi\"", b.Find("Name").Get(1).String);
            Assert.Equal(LumenDataType.Int64, b.Find("Qty").Type);
            Assert.Equal(LumenDataType.Double, b.Find("Price").Type);
            Assert.Equal(LumenDataType.Date, b.Find("When").Type);
            Assert.Equal(LumenDataType.Boolean, b.Find("Flag").Type);
        }

        /// <summary>A chain of transform steps, each checked.</summary>
        [Fact]
        public void TransformStepsRun()
        {
            var csv = "Id,Name,Jan,Feb\n1,A-x,10,20\n2,B-y,30,40\n2,B-y,30,40\n";
            var ct = CancellationToken.None;
            var b = CsvConnector.Read(new StringReader(csv), true, ct);
            b = TransformEngine.Apply(b, StepKinds.Create(StepKinds.RemoveDuplicates, "dedupe"), ct);
            Assert.Equal(2, b.RowCount);
            b = TransformEngine.Apply(b, StepKinds.Create(StepKinds.SplitColumn, "split", "column", "Name", "delimiter", "-"), ct);
            Assert.NotNull(b.Find("Name.2"));
            b = TransformEngine.Apply(b, StepKinds.Create(StepKinds.Rename, "rename", "column", "Name.1", "name", "Code"), ct);
            b = TransformEngine.Apply(b, StepKinds.Create(StepKinds.AddCustomColumn, "custom", "name", "Sum", "table", "T", "expression", "T[Jan] + T[Feb]"), ct);
            Assert.Equal(30, b.Find("Sum").Get(0).AsDouble());
            b = TransformEngine.Apply(b, StepKinds.Create(StepKinds.Unpivot, "unpivot", "columns", "Jan|Feb", "attribute", "Month", "value", "Amount"), ct);
            Assert.Equal(4, b.RowCount);
            b = TransformEngine.Apply(b, StepKinds.Create(StepKinds.FilterRows, "filter", "column", "Amount", "op", "GreaterThan", "value", "15"), ct);
            Assert.Equal(3, b.RowCount);
            b = TransformEngine.Apply(b, StepKinds.Create(StepKinds.ReplaceValues, "replace", "column", "Code", "find", "B", "replace", "Z"), ct);
            Assert.Equal("Z", b.Find("Code").Get(2).String);
            b = TransformEngine.Apply(b, StepKinds.Create(StepKinds.ChangeType, "type", "column", "Amount", "type", "Int64"), ct);
            Assert.Equal(LumenDataType.Int64, b.Find("Amount").Type);
            b = TransformEngine.Apply(b, StepKinds.Create(StepKinds.RemoveColumns, "drop", "columns", "Name.2"), ct);
            Assert.Null(b.Find("Name.2"));
        }

        /// <summary>PromoteHeaders re-infers types once the header row has left the data.</summary>
        [Fact]
        public void PromoteHeadersUsesFirstRow()
        {
            var csv = "a,b\n1,2\n3,4\n";
            var ct = CancellationToken.None;
            var b = CsvConnector.Read(new StringReader(csv), false, ct);
            Assert.Equal(3, b.RowCount);
            b = TransformEngine.Apply(b, StepKinds.Create(StepKinds.PromoteHeaders, "promote"), ct);
            Assert.Equal(2, b.RowCount);
            Assert.Equal(LumenDataType.Int64, b.Find("a").Type);
            Assert.Equal(3, b.Find("a").Get(1).AsDouble());
        }

        /// <summary>Every column type survives the binary format.</summary>
        [Fact]
        public void ColumnSnapshotsRoundTrip()
        {
            var f = F.Value;
            foreach (var t in f.Ds.Tables.Values)
            {
                foreach (var c in t.Columns)
                {
                    var ms = new MemoryStream();
                    using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8, true)) ColumnIO.WriteColumn(w, c);
                    ms.Position = 0;
                    Column back;
                    using (var r = new BinaryReader(ms)) back = ColumnIO.ReadColumn(r);
                    Assert.Equal(c.RowCount, back.RowCount);
                    Assert.Equal(c.Type, back.Type);
                    for (int i = 0; i < Math.Min(100, c.RowCount); i++)
                        Assert.Equal(0, Value.Compare(c.Get(i), back.Get(i)));
                }
            }
        }

        /// <summary>Commands sharing a gesture id become one undo step.</summary>
        [Fact]
        public void CommandStackMergesDragsAndUndoes()
        {
            var doc = new ReportDocument();
            var page = new PageDef { Name = "P" };
            var v = new VisualDef { Type = "card", X = 0, Y = 0, Width = 10, Height = 10 };
            page.Visuals.Add(v);
            doc.Pages.Add(page);
            var stack = new CommandStack(() => doc);
            SetBoundsCommand.Bounds B(double x) => new SetBoundsCommand.Bounds { X = x, Y = 0, W = 10, H = 10 };
            stack.Execute(new SetBoundsCommand("Move", "drag1", page.Id, v.Id, B(0), B(5)));
            stack.Execute(new SetBoundsCommand("Move", "drag1", page.Id, v.Id, B(5), B(9)));
            Assert.Equal(9, v.X);
            stack.Undo();
            Assert.Equal(0, v.X);
            Assert.False(stack.CanUndo);
            stack.Redo();
            Assert.Equal(9, v.X);
        }

        /// <summary>The name is historic: this is the 1M row smoke test.</summary>
        [Fact]
        public void TenMillionRowSmoke()
        {
            // Not a benchmark, a guard: 1M rows, three visuals' worth of grouped queries.
            SemanticModel m;
            var ds = SampleData.BuildDataset(1000000, out m);
            var e = new InProcessEngine();
            e.Load(ds);
            var cat = FieldRef.ForColumn("product", m.TableByName("Product").ColumnByName("Category").Id);
            var r = e.Execute(new QuerySpec { GroupBy = { cat }, Measures = { FieldRef.ForMeasure("m-sales-amount") }, IncludeTotals = true }, CancellationToken.None);
            Assert.Null(r.Error);
            Assert.Equal(4, r.RowCount);
        }
    }
}
