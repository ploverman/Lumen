# PRD: "Lumen", a Power BI-style dashboard builder in pure C# on .NET Framework 4.7 (no JavaScript)

| | |
|---|---|
| Author | Claude, for Ploverman |
| Date | 2026-10-31 |
| Status | Draft v3: **C# and .NET Framework 4.7 only, zero JavaScript** |
| Supersedes | `PRD-js-dashboard-builder.md` (v1, JavaScript) and `PRD-dashboard-builder-csharp-net47.md` (v2, C# server + d3 browser) |
| Audience | Seasoned C# developer with Power BI and SQL Server expertise |

---

## 0. Summary

Lumen is a **WPF desktop application on .NET Framework 4.7**, the direct analogue of Power BI Desktop, plus an optional **C# server** (ASP.NET Web API 2 on IIS, .NET 4.7) that plays the role of the Power BI Service. Every line of product code is C# (plus XAML and T-SQL). There is **no browser, no WebView/WebBrowser control, no d3, no JavaScript anywhere**, including for viewers.

The three big consequences of "no JavaScript":

1. **Visuals are drawn in C#** with WPF's retained-mode graphics (`DrawingVisual`, `StreamGeometry`, `DrawingContext`). We build a small C# charting core (scales, axes, shapes, hit-testing) that does the job d3 did in v1/v2.
2. **Viewers also use the WPF app** (in read-only "Viewer mode"), installed via ClickOnce or MSI. People without the app receive static exports (PDF, PNG, XPS, Excel), not interactive web pages.
3. **The server has no web UI.** It is a C# API for publishing, sharing, refresh and remote query execution, consumed only by the WPF client.

---

## 1. Problem and goal

Power BI Desktop bundles **Power Query** (M), **VertiPaq/AS Tabular** (columnar store + DAX), the **report canvas**, and the **Power BI Service** (publish, share, refresh, RLS).

**Goal:** a Windows desktop tool, written entirely in C# on .NET Framework 4.7, that lets an analyst import data, model it, define measures, and author interactive multi-page reports with cross-filtering, then publish them to a server so colleagues can open them in the same app.

### 1.1 Success criteria

- A Power BI user builds a 3-page sales report from two sources (Sales fact + Product dimension) in under 15 minutes without docs.
- Slicer click to all visuals redrawn: **< 100 ms p95** with 10M fact rows loaded locally (in-process engine, no network).
- Same interaction on a published report via the server: **< 250 ms p95** on a LAN.
- Canvas stays at 60 fps while dragging/resizing visuals on a 15-visual page.
- A saved `.lumen` file reopens identically on another machine.

---

## 2. Platform constraints (.NET Framework 4.7)

| Constraint | Consequence for the design |
|---|---|
| **Target: .NET Framework 4.7** for build and deployment (`<TargetFrameworkVersion>v4.7</TargetFrameworkVersion>`) | All libraries below were chosen for 4.7 compatibility. Prefer packages whose `net4x` assets are used; `netstandard2.0`-only packages need extra facades and binding redirects on 4.7 (`<AutoGenerateBindingRedirects>true</AutoGenerateBindingRedirects>`) |
| C# up to **7.3** (VS 2017 15.7+ or VS 2019/2022, `<LangVersion>7.3</LangVersion>`) | Tuples, pattern matching in `is`/`switch`, `ref`/`in`, local functions, `readonly struct`. No records, nullable reference types, switch expressions, `IAsyncEnumerable`, default interface methods |
| WPF on 4.7 adds **Per-Monitor DPI awareness** (declare in `app.manifest`) and improved touch/stylus | Visuals render crisply on mixed-DPI multi-monitor setups; handle `DpiChanged` in visual hosts |
| No ASP.NET Core | Server = **ASP.NET Web API 2** (or WCF) on IIS, plus a **Windows Service** for scheduled refresh |
| `Span<T>` only via `System.Memory` NuGet ("slow span") | Engine hot loops use plain arrays (the JIT removes bounds checks in simple `for` loops) and optionally `unsafe` pointers |
| SIMD via `System.Numerics.Vectors` NuGet with RyuJIT x64 | Use for SUM/MIN/MAX; **build x64** (uncheck "Prefer 32-bit") |
| Arrays > 2 GB need `<gcAllowVeryLargeObjects enabled="true"/>` | Set in `App.config` |
| `System.IO.Compression.ZipArchive` (4.5+) | `.lumen` file format is a ZIP package, like `.pbix` |
| WPF in a Windows Service is not officially supported by Microsoft | Server-side rendering for email subscriptions is a Phase 3 risk (section 14) |
| **End of support**: Microsoft has announced end of support for .NET Framework 4.6.2, 4.7 and 4.7.1 (I believe January 2027; confirm on the Microsoft lifecycle page) | Accepted risk (section 17). The engine and model assemblies have no WPF or `System.Web` dependencies, so they stay portable |

---

## 3. Key technology decisions

### 3.1 WPF, not WinForms

| | WPF | WinForms |
|---|---|---|
| Retained-mode vector graphics with hit testing | Yes (`DrawingVisual`, `Geometry`, `VisualTreeHelper.HitTest`) | No; GDI+ immediate mode in `OnPaint` |
| Resolution independence / DPI | Device-independent units; per-monitor DPI on 4.7 | Pixel-based, painful at high DPI |
| Data binding + MVVM for panes | First class | Limited |
| Animations, transforms, opacity, effects | Built in | Manual |
| Styling/theming | `ResourceDictionary`, styles, templates | Manual |
| Ribbon, docking, property grid | `System.Windows.Controls.Ribbon` (in the framework), AvalonDock, Xceed PropertyGrid | Available but dated |

WPF's retained visual tree plays the role that SVG + d3 played in the JavaScript design: each bar is an object that can be hit-tested, animated and styled.

### 3.2 Charting: build a small C# charting core (recommended), not a third-party chart control

| Option | Verdict |
|---|---|
| **Custom "Lumen.Charting" core on `DrawingVisual`** | **Recommended.** Full control over cross-highlight (draw full bar faded + highlighted portion solid), selection, theme mapping, format-pane properties, and performance. Roughly 6–8 weeks for the MVP visual set, which this PRD budgets for |
| OxyPlot (MIT, WPF, supports .NET 4.5+) | Good for line/scatter/area; weak on categorical bar/pie formatting and has no notion of cross-highlight. Acceptable as a **fallback for scatter/line in Phase 1** if time is short |
| LiveCharts 0.9 (MIT, WPF) | Attractive animations, but slow beyond a few thousand points and hard to customize hit behavior |
| `System.Windows.Forms.DataVisualization` (MS Chart) in `WindowsFormsHost` | Airspace issues (WinForms content always draws on top of WPF), no WPF styling. Reject |
| Commercial suites (DevExpress, Telerik, Syncfusion, SciChart) | Feature-rich, but licensing cost and their visual model fights Power BI's semantics. Acceptable if the team prefers buying to building |

The custom core is modeled on d3's best ideas, translated to C#:

| d3 concept | Lumen.Charting (C#) |
|---|---|
| `d3.scaleLinear` / `scaleBand` / `scaleTime` / `scaleOrdinal` | `LinearScale`, `BandScale`, `TimeScale`, `OrdinalScale` classes implementing `IScale<TDomain>` with `Map()`, `Invert()`, `Ticks()`, `Nice()` |
| `d3.axisBottom(x)` | `AxisRenderer.Draw(DrawingContext, IScale, AxisOptions)` |
| `d3.arc`, `d3.line`, `d3.area`, `d3.pie` | `ShapeBuilders.Arc/Line/Area/Pie` returning frozen `StreamGeometry` |
| Data join (`enter/update/exit`) keyed by datum | `MarkCollection<TKey>` diffing old vs new marks by key, enabling enter/exit animations |
| `d3-format` / `d3-time-format` | .NET format strings (`value.ToString("#,0.0K", culture)`) plus a small `PowerBiFormat` helper for "auto" units (K, M, bn) |
| `d3.brush` (range slicer) | `RangeSelectorAdorner` using `Thumb` controls |
| `d3-hierarchy` treemap (Phase 3) | `SquarifiedTreemapLayout` (port of the squarify algorithm) |

---

## 4. Concept map: Power BI → Lumen (C# / WPF)

| Power BI | Lumen component | .NET implementation |
|---|---|---|
| Power BI Desktop | **Lumen Desktop** (WPF app) | MVVM, ribbon, docking panes |
| Get Data | Connectors | CSV (CsvHelper), Excel (ExcelDataReader), SQL Server (`System.Data.SqlClient`), JSON (Json.NET), OData |
| Power Query Editor | **Transform Editor** window | Preview `DataGrid` + Applied Steps list (`ObservableCollection<TransformStep>`) |
| M applied steps | `TransformStep` classes | Each step `Apply(ColumnBatch)`; optional `TryFoldToSql` |
| VertiPaq | Columnar store | Typed arrays + dictionary encoding (section 8) |
| Model view | **Model Diagram** | WPF `Canvas` of table cards + relationship `Path`s, drag to relate |
| Data view | **Data Grid** | Virtualized `DataGrid` over the columnar store |
| DAX + formula bar | **LQL** + AvalonEdit editor | Syntax highlighting, completion window, error squiggles |
| Report canvas | **Report Canvas** | WPF `Canvas` hosting visual containers, `Adorner`s for selection/resize |
| Visualizations pane | **Visual Gallery + Field Wells** | `ListBox` of visual types; wells accept `DragDrop` of fields |
| Format pane | **Format Pane** | Generated from attributes on each visual's `FormatOptions` class (like `PropertyGrid`) |
| Custom visual `IVisual` | `ILumenVisual` (C#) | Plugins loaded via MEF (`System.ComponentModel.Composition`) |
| Slicer | Slicer visuals | Write filters to page state |
| Cross-filter / highlight | **Interaction Bus** | Selection events → new QuerySpecs |
| Themes JSON | Theme JSON → `ResourceDictionary` | Power BI theme keys supported |
| `.pbix` | `.lumen` ZIP package | `report.json` + `model.json` + binary column snapshots |
| Power BI Service | **Lumen Server** (Web API 2, no UI) | Publish, share, refresh, remote query, RLS |
| Power BI app / viewer | Lumen Desktop in **Viewer mode** | Same EXE, read-only, queries server engine |
| Export to PDF / PowerPoint | Export to PDF/XPS/PNG/Excel | `RenderTargetBitmap`, `XpsDocumentWriter`, PdfSharp, EPPlus 4.5 |
| Performance Analyzer | **Performance Analyzer** pane | Query time vs render time per visual |

---

## 5. Scope

### 5.1 MVP (Phases 1–2), desktop only

- Import CSV, Excel, SQL Server table/query, JSON.
- Transform steps: promote headers, change type, rename, remove columns, filter rows, replace values, split column, add custom column, remove duplicates, unpivot.
- Model: multiple tables, 1:* single-direction relationships, active/inactive, mark as date table, sort-by-column, hidden columns, format strings.
- LQL measures: `SUM, AVERAGE, MIN, MAX, COUNT, COUNTROWS, DISTINCTCOUNT, SUMX/AVERAGEX` (no context transition), `DIVIDE, IF, SWITCH, VAR/RETURN, CALCULATE` (column filters), `ALL, FILTER` (simple), `TOTALYTD, SAMEPERIODLASTYEAR`.
- Canvas: multiple pages, drag, resize, snap grid, smart guides, z-order, align/distribute, copy/paste, undo/redo.
- Visuals (drawn in C#): Card, Multi-row card, Table, Matrix, Clustered and Stacked bar/column, Line, Area, Pie/Donut, Scatter; Slicer (list, dropdown, numeric/date range, relative date).
- Cross-filter / cross-highlight with Edit Interactions; Filters pane at visual, page and report scope; TopN.
- Themes: light, dark, high contrast; import Power BI theme JSON.
- Save/open `.lumen` files; export page to PNG/PDF; print.

### 5.2 Later phases

- Phase 3: Lumen Server (publish, share, Viewer mode, scheduled refresh), hierarchies and drill, drill-through, bookmarks, sync slicers, treemap/map/waterfall/KPI/gauge, plugin SDK, bi-directional relationships, `USERELATIONSHIP`.
- Phase 4: DirectQuery to SQL Server, RLS, SSAS Tabular backend via ADOMD.NET, email subscriptions with server-side rendering.

### 5.3 Non-goals

- **Any JavaScript, browser-based viewer, WebView2 or WebBrowser control.**
- Full DAX/M parity; `.pbix` read/write.
- Many-to-many relationships in MVP.
- Mac/Linux/mobile clients (WPF is Windows only).
- Paginated reports (use SSRS).
- Natural-language Q&A, AI visuals.

---

## 6. Architecture

```
┌────────────────────────── Lumen Desktop (WPF, .NET 4.7, x64) ──────────────────────────┐
│  Views (XAML)                                                                            │
│  ┌────────┐ ┌──────────────────────────────┐ ┌────────────┐ ┌───────────┐ ┌───────────┐ │
│  │ Ribbon │ │ Report Canvas (pages)        │ │ Filters    │ │ Visual-   │ │ Fields    │ │
│  └────────┘ │  VisualContainer x N         │ │ pane       │ │ izations  │ │ pane      │ │
│  Model view │   └─ ILumenVisual (C# drawn) │ └────────────┘ │ + wells   │ └───────────┘ │
│  Data view  └──────────────────────────────┘                │ + format  │               │
│  Transform editor   LQL editor (AvalonEdit)                  └───────────┘               │
│ ──────────────────────────────────────────────────────────────────────────────────────── │
│  ViewModels (MVVM)   ReportViewModel, PageViewModel, VisualViewModel, ModelViewModel     │
│  Services            CommandStack (undo/redo), InteractionBus, ThemeService, FileService │
│ ──────────────────────────────────────────────────────────────────────────────────────── │
│  IQueryEngine ──┬── InProcessEngine (Lumen.Engine)          ← authoring, local files     │
│                 └── RemoteQueryEngine (HTTP to Lumen Server) ← Viewer mode, published    │
└───────────────────────────────────────┬─────────────────────────────────────────────────┘
                                        │ HTTPS + JSON (Json.NET), Windows auth / OIDC token
┌───────────────────────────────────────▼─────── Lumen Server (Phase 3, no web UI) ────────┐
│ IIS: ASP.NET Web API 2, .NET 4.7, 64-bit                                                  │
│   PublishController, ReportsController, QueryController (runs the same Lumen.Engine)      │
│   Security: ACLs + RLS predicates injected into every query                               │
│ Windows Service: Lumen.RefreshService (Hangfire or Quartz.NET) runs transform pipelines   │
│ SQL Server: LumenMeta DB (reports, ACLs, snapshots), customer sources (DirectQuery)       │
└───────────────────────────────────────────────────────────────────────────────────────────┘
```

The single most important design choice: **the engine is one assembly used in both places**. Power BI Desktop and the Service both run VertiPaq; Lumen Desktop and Lumen Server both run `Lumen.Engine`. The WPF client talks to `IQueryEngine` and does not know which one it has.

### 6.1 Solution layout

```
Lumen.sln   (all projects target .NET Framework 4.7, x64)
  src/Lumen.Model/          POCOs: SemanticModel, ReportDocument, FilterDef, commands, JSON contracts
  src/Lumen.Engine/         Columnar store, relationship indexes, filter propagation, LQL, cache
  src/Lumen.Connectors/     Connectors + transform steps
  src/Lumen.Charting/       Scales, axes, shape builders, mark diffing, hit testing (WPF drawing)
  src/Lumen.Visuals/        Built-in visuals (ILumenVisual implementations) + FormatOptions classes
  src/Lumen.Desktop/        WPF app: views, view models, services, ClickOnce publish profile
  src/Lumen.Server/         ASP.NET Web API 2 (Phase 3)
  src/Lumen.RefreshService/ Windows Service (Phase 3), TopShelf host
  tests/Lumen.Engine.Tests/     NUnit 3 or xUnit 2.4
  tests/Lumen.Golden/           sample model + expected results exported from Power BI
  tests/Lumen.Charting.Tests/   render-to-PNG snapshot tests
  tests/Lumen.Desktop.UITests/  FlaUI (UI Automation) end-to-end tests
```

### 6.2 Libraries (all .NET Framework 4.7 compatible, no JavaScript)

| Need | Library | License |
|---|---|---|
| MVVM helpers | Prism 7 (WPF) or MVVM Light / hand-rolled `INotifyPropertyChanged` | MIT |
| Docking panes | AvalonDock (Dirkster fork) | MS-PL |
| Ribbon | `System.Windows.Controls.Ribbon` (in the framework) | — |
| Property grid / extra controls | Extended WPF Toolkit (Xceed) community edition, version check for license | MS-PL (older) |
| Code editor for LQL | AvalonEdit | MIT |
| JSON | Newtonsoft.Json | MIT |
| CSV / Excel | CsvHelper, ExcelDataReader; EPPlus 4.5 for Excel export | MS-PL / MIT / LGPL |
| PDF export | PdfSharp 1.50 (WPF build) | MIT |
| Reactive event streams (interaction bus, debouncing) | System.Reactive 4.x | MIT |
| Plugins | MEF (`System.ComponentModel.Composition`, in the framework) | — |
| DI | Autofac 4.x or Unity | MIT / Apache |
| Logging | Serilog 2.x | Apache |
| Server | ASP.NET Web API 2, OWIN, Hangfire 1.7 | MS / LGPL |
| UI tests | FlaUI | MIT |
| Benchmarks | BenchmarkDotNet | MIT |

---

## 7. Data and state model (C#)

The whole report is one `ReportDocument`, serialized with Json.NET into `report.json` inside the `.lumen` package (and into SQL Server when published).

```csharp
public sealed class SemanticModel
{
    public List<TableDef> Tables { get; set; } = new List<TableDef>();
    public List<RelationshipDef> Relationships { get; set; } = new List<RelationshipDef>();
    public List<MeasureDef> Measures { get; set; } = new List<MeasureDef>();
}

public enum LumenDataType { String, Int64, Decimal, Double, Boolean, Date, DateTime }
public enum SummarizeBy { None, Sum, Average, Min, Max, Count, DistinctCount }

public sealed class TableDef
{
    public string Id { get; set; }
    public string Name { get; set; }
    public DataSourceRef Source { get; set; }
    public List<TransformStep> Steps { get; set; } = new List<TransformStep>();
    public List<ColumnDef> Columns { get; set; } = new List<ColumnDef>();
    public string DateTableColumnId { get; set; }   // non-null => "Mark as date table"
}

public sealed class ColumnDef
{
    public string Id { get; set; }
    public string Name { get; set; }
    public LumenDataType DataType { get; set; }
    public string FormatString { get; set; }        // .NET format string: "#,0.00", "0.0%", "yyyy-MM-dd"
    public SummarizeBy SummarizeBy { get; set; }
    public string SortByColumnId { get; set; }
    public bool IsHidden { get; set; }
    public string Expression { get; set; }          // non-null => calculated column (row context)
}

public sealed class RelationshipDef
{
    public string Id { get; set; }
    public string FromTableId { get; set; }  public string FromColumnId { get; set; } // many side
    public string ToTableId { get; set; }    public string ToColumnId { get; set; }   // one side
    public Cardinality Cardinality { get; set; }   // ManyToOne | OneToOne
    public CrossFilter CrossFilter { get; set; }   // Single (MVP) | Both (Phase 3)
    public bool IsActive { get; set; } = true;
}

public sealed class MeasureDef
{
    public string Id { get; set; }
    public string Name { get; set; }
    public string HomeTableId { get; set; }
    public string Expression { get; set; }   // LQL
    public string FormatString { get; set; }
    public string DisplayFolder { get; set; }
}

public sealed class ReportDocument
{
    public int SchemaVersion { get; set; } = 1;
    public Guid Id { get; set; }
    public string Name { get; set; }
    public SemanticModel Model { get; set; }
    public ThemeDef Theme { get; set; }
    public List<FilterDef> ReportFilters { get; set; } = new List<FilterDef>();
    public List<PageDef> Pages { get; set; } = new List<PageDef>();
}

public sealed class PageDef
{
    public string Id { get; set; }
    public string Name { get; set; }
    public double Width { get; set; } = 1280;   // device-independent units (1/96 inch)
    public double Height { get; set; } = 720;
    public List<FilterDef> PageFilters { get; set; } = new List<FilterDef>();
    public List<VisualDef> Visuals { get; set; } = new List<VisualDef>();
    public List<InteractionOverride> Interactions { get; set; } = new List<InteractionOverride>();
}

public sealed class VisualDef
{
    public string Id { get; set; }
    public string Type { get; set; }                                // "clusteredBar", "slicer"...
    public double X { get; set; } public double Y { get; set; }
    public double Width { get; set; } public double Height { get; set; }
    public int ZIndex { get; set; }
    public Dictionary<string, List<FieldRef>> Roles { get; set; }  // wells
    public List<FilterDef> VisualFilters { get; set; }
    public JObject Format { get; set; }   // deserialized into the visual's FormatOptions class
}

public sealed class FieldRef
{
    public FieldKind Kind { get; set; }           // Column | Measure | HierarchyLevel
    public string TableId { get; set; }
    public string ColumnId { get; set; }
    public string MeasureId { get; set; }
    public SummarizeBy? Aggregation { get; set; } // implicit measure, e.g. "Sum of Qty"
}

public sealed class FilterDef
{
    public string Id { get; set; }
    public FieldRef Target { get; set; }
    public FilterType Type { get; set; }          // Basic | Advanced | TopN | RelativeDate
    public List<object> Values { get; set; }
    public List<Condition> Conditions { get; set; }
    public LogicalOp Logic { get; set; }
}

public sealed class InteractionOverride
{
    public string SourceVisualId { get; set; }
    public string TargetVisualId { get; set; }
    public InteractionMode Mode { get; set; }     // Filter | Highlight | None
}
```

### 7.1 Undo/redo: command pattern

Every authoring change is a command pushed to `CommandStack`, the classic WPF editor pattern:

```csharp
public interface IReportCommand
{
    string Label { get; }                 // shown in Edit menu: "Undo Move visual"
    void Do(ReportDocument doc);
    void Undo(ReportDocument doc);
    bool TryMerge(IReportCommand next);   // coalesce many MouseMove deltas into one "Move"
}

public sealed class MoveVisualCommand : IReportCommand
{
    private readonly string _pageId, _visualId;
    private readonly Point _from;
    private Point _to;
    public MoveVisualCommand(string pageId, string visualId, Point from, Point to)
    { _pageId = pageId; _visualId = visualId; _from = from; _to = to; }

    public string Label => "Move visual";
    public void Do(ReportDocument d)   { var v = d.Find(_pageId, _visualId); v.X = _to.X;   v.Y = _to.Y; }
    public void Undo(ReportDocument d) { var v = d.Find(_pageId, _visualId); v.X = _from.X; v.Y = _from.Y; }
    public bool TryMerge(IReportCommand next)
    {
        if (next is MoveVisualCommand m && m._visualId == _visualId) { _to = m._to; return true; }
        return false;
    }
}
```

View models observe the document through change events raised by `CommandStack`, so views update via bindings.

### 7.2 `.lumen` file format (ZIP, like `.pbix`)

```
MyReport.lumen
  [Content_Types].xml        (optional)
  report.json                ReportDocument (pages, visuals, theme, filters)
  model.json                 SemanticModel (also embedded in report.json for v1 simplicity)
  data/Sales.col             binary column snapshot per table (see 8.1)
  data/Product.col
  thumbnails/page1.png       for the Open dialog and the server catalog
```

Written with `ZipArchive` and `BinaryWriter`; columns are compressed with `DeflateStream` (`CompressionLevel.Fastest`).

---

## 8. Query engine (`Lumen.Engine`)

### 8.1 Columnar storage

1. Connectors stream rows (`IDataReader` for SQL Server, CsvHelper for CSV) into **column builders**. Never a `DataTable`: it boxes every value, so 10M rows × 10 columns would be about 100M heap objects.
2. Column types:

```csharp
public abstract class Column { public string Name; public int RowCount; public abstract LumenDataType Type { get; } }
public sealed class DoubleColumn : Column { public double[] Values; public ulong[] NullBits; }
public sealed class Int64Column  : Column { public long[] Values;   public ulong[] NullBits; }
public sealed class DateColumn   : Column { public int[] DaysSinceEpoch; public ulong[] NullBits; }
public sealed class DictColumn   : Column       // strings and low-cardinality values, like VertiPaq
{
    public int[] Codes;          // per row; 0 = BLANK
    public object[] Dictionary;  // distinct values, sorted, so code order = sort order
}
```

3. **Relationship index:** for each relationship, hash the dimension key into a `Dictionary<long,int>` and build `int[] fkToDimRow` (one entry per fact row; -1 for orphans, which become the "(Blank)" member, as in Power BI).
4. Memory: 10M rows × (3 `int` code columns + 2 `double` columns) ≈ 280 MB. This is why the app is **x64 only**.

### 8.2 QuerySpec

What a visual asks the engine for, equivalent to Power BI's per-visual `SUMMARIZECOLUMNS` query:

```csharp
public sealed class QuerySpec
{
    public List<FieldRef> GroupBy { get; set; }     // Axis + Legend wells
    public List<FieldRef> Measures { get; set; }    // Values well
    public List<FilterDef> Filters { get; set; }    // report + page + visual + slicers + cross-filters
    public List<FilterDef> Highlight { get; set; }  // cross-highlight
    public List<OrderBy> OrderBy { get; set; }
    public int? TopN { get; set; }
    public int Limit { get; set; } = 3500;          // data reduction, as in Power BI
    public bool IncludeTotals { get; set; }
}

public interface IQueryEngine
{
    Task LoadAsync(Guid datasetId, SemanticModel model, CancellationToken ct);
    Task<QueryResult> ExecuteAsync(QuerySpec spec, SecurityContext sec, CancellationToken ct);
    Task<IReadOnlyList<QueryResult>> ExecuteBatchAsync(IReadOnlyList<QuerySpec> specs,
                                                      SecurityContext sec, CancellationToken ct);
}
```

### 8.3 Evaluation, step by step

1. **Collect filters** in scope order: report, page, slicers (respecting Edit Interactions), cross-filter selections, visual filters, and RLS predicates when running on the server.
2. **Filter → row mask** per table (a `Bitset` over `ulong[]`); AND masks on the same table.
3. **Propagate from the one side to the many side** along active single-direction relationships:

```csharp
Bitset PropagateToFact(TableData fact, Dictionary<string, Bitset> masks)
{
    Bitset result;
    if (!masks.TryGetValue(fact.Id, out result)) result = Bitset.AllSet(fact.RowCount);
    else result = result.Clone();

    foreach (var rel in _model.ActiveRelationshipsFrom(fact.Id))
    {
        Bitset dimMask;
        if (!masks.TryGetValue(rel.ToTableId, out dimMask)) continue;
        int[] fk = _index.FkToDimRow(rel.Id);
        for (int r = 0; r < fk.Length; r++)                 // tight loop, no allocations
            if (result[r] && (fk[r] < 0 || !dimMask[fk[r]])) result.Clear(r);
    }
    return result;
}
```

   For snowflakes (Category → Subcategory → Product → Sales), propagate from the outermost dimension inward first.
4. **Group** surviving fact rows by a composite key of dictionary codes (packed into a `long`, 16 bits per column, for up to 4 group-by columns), reaching dimension columns through `fkToDimRow`.
5. **Evaluate measures per group** in that group's filter context. Fast path: simple aggregations accumulate in one pass into `Accumulator[]`, parallelized with `Parallel.For` and thread-local accumulators for > 1M rows.
6. **Totals** are evaluated separately with the group filter removed, not by summing groups, so `DISTINCTCOUNT` and ratios match DAX.
7. **Sort, TopN, Limit**, then return a columnar `QueryResult`.
8. **Cache** by hash of (spec, dataset version, security identity).

### 8.4 LQL (DAX subset), compiled in C#

1. Lexer → tokens (`'Sales'[Qty]`, `[Measure]`, literals, operators, `VAR`, `RETURN`).
2. Pratt parser → AST.
3. Binder resolves names against `SemanticModel`, checks types/arity, and emits diagnostics with line/column, shown as squiggles in AvalonEdit.
4. Compiler: closure tree `Func<EvalContext, Value>` in MVP; `System.Linq.Expressions` compiled lambdas for row-context code (calculated columns, `SUMX` bodies) in Phase 3.
5. Semantics: measures in filter context, calculated columns in row context; `CALCULATE` replaces filters on the same column; `ALL` removes filters; `BLANK` is a real value with DAX propagation rules; `DIVIDE(x,0)` = BLANK; time intelligence requires a marked date table.

```csharp
public readonly struct Value
{
    public readonly ValueKind Kind;   // Blank, Number, Integer, String, Boolean, Date
    public readonly double Number;
    public readonly long Integer;
    public readonly string String;
    public static readonly Value Blank = default(Value);
}
```

Golden-tested measures:

```
Sales Amount      = SUMX(Sales, Sales[Qty] * Sales[UnitPrice])
Margin %          = DIVIDE([Sales Amount] - [Total Cost], [Sales Amount])
Sales YTD         = TOTALYTD([Sales Amount], 'Date'[Date])
Red Sales         = CALCULATE([Sales Amount], Product[Color] = "Red")
All-Product Share = DIVIDE([Sales Amount], CALCULATE([Sales Amount], ALL(Product)))
```

### 8.5 Threading model in the desktop app

- The engine never runs on the WPF Dispatcher (UI) thread. View models call `await engine.ExecuteBatchAsync(...)` which internally uses `Task.Run`; results marshal back to the UI thread automatically via the `async` continuation.
- Each page refresh gets a fresh `CancellationTokenSource`; a new slicer click cancels the previous in-flight batch, so fast clicking never queues stale work.
- Column arrays are immutable after load, so queries run concurrently without locks. Refresh builds a new dataset version and swaps it with `Interlocked.Exchange`.

---

## 9. Desktop authoring UX (WPF)

### 9.1 Shell layout (mirrors Power BI Desktop)

- `RibbonWindow` with tabs: Home (Get data, Transform, Refresh, New visual, New measure, Publish), Insert, Modeling, View, Help.
- Left rail: Report / Data / Model views (a `TabControl` styled as icons).
- Center: page canvas inside a `ScrollViewer` with zoom (`LayoutTransform` with `ScaleTransform`), page tabs at the bottom.
- Right: AvalonDock panes for Filters, Visualizations (gallery, wells, format), Fields. Panes can be pinned, floated, auto-hidden.

### 9.2 Report canvas, step by step

1. `ReportCanvas` is a custom `Canvas` whose children are `VisualContainer` controls, bound to `PageViewModel.Visuals` through an `ItemsControl` with `ItemsPanel = Canvas` and `Canvas.Left/Top` bound to `X/Y`.
2. Selecting a container adds a **`ResizeAdorner`** (on the `AdornerLayer`) with eight `Thumb` handles. `Thumb.DragDelta` produces `MoveVisualCommand`/`ResizeVisualCommand`, merged while dragging so one drag is one undo step.
3. **Snapping:** round to the 8-unit grid unless Alt is held; **smart guides** are computed by comparing the moving rect's edges/centers against the other visuals' within 4 units, and drawn as dashed lines in a guide adorner.
4. **Multi-select:** Ctrl+click and rubber-band selection (a `Rectangle` drawn on mouse drag over empty canvas). Align/distribute commands operate on the selection.
5. Z-order, lock, hide, and a Selection pane (`TreeView` of visuals).
6. Keyboard: arrows nudge 1 unit (Shift: 10), Ctrl+C/V/D, Delete, Ctrl+Z/Y, all as `RoutedCommand`s with `InputBindings`.

### 9.3 Fields pane and wells

1. Fields pane: `TreeView` of tables → columns, measures, hierarchies, with icons for data type and Σ for summarizable columns.
2. Dragging a field calls `DragDrop.DoDragDrop(source, new DataObject(typeof(FieldRef), fieldRef), DragDropEffects.Copy)`.
3. Wells (`ListBox` per role) handle `DragOver` (accept only compatible fields: grouping roles accept columns, measure roles accept measures or summarizable columns) and `Drop` (push `AddFieldToWellCommand`).
4. Dropping a field on empty canvas auto-creates a visual (column → Table, measure → Card, date + measure → Line), as Power BI does.
5. Numeric column in a measure role becomes an **implicit measure** ("Sum of Qty") with a dropdown to change aggregation.

### 9.4 Format pane

Each visual declares a format options class; the pane is generated by reflection, like a `PropertyGrid` with categories:

```csharp
public sealed class BarFormatOptions
{
    [FormatCard("X axis"), FormatProperty("Show")]               public bool XAxisVisible { get; set; } = true;
    [FormatCard("X axis"), FormatProperty("Font size", Min = 8, Max = 40)] public double XAxisFontSize { get; set; } = 11;
    [FormatCard("Data labels"), FormatProperty("Show")]          public bool DataLabels { get; set; }
    [FormatCard("Data labels"), FormatProperty("Display units")] public DisplayUnits LabelUnits { get; set; } = DisplayUnits.Auto;
    [FormatCard("Colors"), FormatProperty("Default color")]      public ThemeColorRef BarColor { get; set; } = ThemeColorRef.DataColor(0);
    [FormatCard("Title"), FormatProperty("Text")]                public string Title { get; set; }
}
```

Resolution order: visual setting → theme `visualStyles` → class default.

### 9.5 Transform editor (Power Query equivalent)

- Modal window: preview `DataGrid` (first 1,000 rows), Applied Steps `ListBox` on the right (select a step to see data as of that step; reorder via drag; delete; edit via gear icon), ribbon of step actions.
- Each step's `Apply` runs on a background task; errors per step shown with a warning icon, as Power Query does.

### 9.6 Model view

- Table cards on a zoomable `Canvas`; drag a column onto another table's column to create a relationship; `Path` with Bezier curve and "1" / "*" markers and a filter-direction arrow.
- Double-click a relationship to edit cardinality, direction, active flag.
- Auto-detect relationships on import by matching names and types.

### 9.7 LQL editor

- AvalonEdit with an `.xshd` highlighting definition for LQL.
- Completion window triggered on `[`, `'`, and letters: tables, columns, measures, functions with signatures.
- Background compile 300 ms after typing stops; diagnostics rendered via `TextMarkerService` squiggles and an error list.

---

## 10. Visuals drawn in C# (`Lumen.Charting` + `Lumen.Visuals`)

### 10.1 Plugin contract (mirrors Power BI's `IVisual`)

```csharp
public interface ILumenVisual
{
    VisualCapabilities Capabilities { get; }               // roles (wells), format options type
    FrameworkElement CreateView(IVisualHost host);         // called once
    void Update(VisualUpdate update);                      // data, size, format, theme, selection
    void Dispose();
}

public sealed class VisualUpdate
{
    public QueryResult Data { get; set; }
    public QueryResult Highlight { get; set; }
    public Size Size { get; set; }
    public object Format { get; set; }                     // e.g. BarFormatOptions
    public ResolvedTheme Theme { get; set; }
    public IReadOnlyCollection<DataPointKey> Selection { get; set; }
    public UpdateReason Reason { get; set; }               // [Flags] Data | Resize | Format | Theme | Selection
}

public interface IVisualHost
{
    void Select(IReadOnlyList<DataPoint> points, bool multiSelect);  // -> cross-filter
    void ShowTooltip(IReadOnlyList<TooltipItem> items, Point position);
    void HideTooltip();
    string FormatValue(Value v, string formatString);
    Color ColorFor(string seriesKey);                     // stable mapping to theme dataColors
    void ShowContextMenu(DataPoint point, Point position);
}
```

Built-in visuals are exported with MEF `[Export(typeof(ILumenVisual))] [ExportMetadata("Type", "clusteredBar")]`; third-party visuals are DLLs dropped into a `Visuals` folder (Phase 3, with a signing requirement).

### 10.2 Rendering approach

- Each visual's view is a `ChartHost : FrameworkElement` owning a `VisualCollection` of `DrawingVisual`s (one per layer: gridlines, marks, axes, labels, selection overlay). Override `GetVisualChild`/`VisualChildrenCount`.
- **Why `DrawingVisual` and not `Shape` controls:** a `Rectangle` per bar is a full `FrameworkElement` with layout, styling and events; 3,500 of them make layout slow. `DrawingVisual` is lightweight (no layout), still supports hit testing, and is the WPF equivalent of "SVG elements without the DOM overhead".
- Brushes, pens and geometries are **frozen** (`Freeze()`), which lets WPF share them across threads and skip change tracking.
- Above ~20,000 marks (big scatter), render into a `WriteableBitmap` with a simple rasterizer and use a spatial index (uniform grid) for hit testing.
- Text: `FormattedText` (with `VisualTreeHelper.GetDpi(this).PixelsPerDip` on 4.7 for correct DPI) and `GlyphRun` caching for axis labels.
- Animations: when data changes, `MarkCollection<TKey>` diffs old and new marks by key; a 250 ms tween driven by `CompositionTarget.Rendering` interpolates positions; entering marks grow from the baseline, exiting marks fade.
- Accessibility: `ChartHostAutomationPeer` exposes each data point as an automation child ("Bikes, Sales Amount 1.2M"), and arrow keys move a focus rectangle between points.

### 10.3 Example: clustered bar chart (abridged)

```csharp
[Export(typeof(ILumenVisual)), ExportMetadata("Type", "clusteredBar")]
public sealed class ClusteredBarVisual : ILumenVisual
{
    private ChartHost _chart;
    private IVisualHost _host;
    private List<BarMark> _marks = new List<BarMark>();

    public VisualCapabilities Capabilities => new VisualCapabilities
    {
        Roles =
        {
            new DataRole("category", "Y-axis",   RoleKind.Grouping, max: 3),
            new DataRole("legend",   "Legend",   RoleKind.Grouping, max: 1),
            new DataRole("values",   "X-axis",   RoleKind.Measure,  min: 1),
            new DataRole("tooltips", "Tooltips", RoleKind.Measure)
        },
        FormatOptionsType = typeof(BarFormatOptions),
        SupportsHighlight = true
    };

    public FrameworkElement CreateView(IVisualHost host)
    {
        _host = host;
        _chart = new ChartHost();
        _chart.MouseLeftButtonUp += OnClick;
        _chart.MouseMove += OnMouseMove;
        _chart.MouseLeave += (s, e) => _host.HideTooltip();
        return _chart;
    }

    public void Update(VisualUpdate u)
    {
        var fmt = (BarFormatOptions)u.Format;
        var rows = BarRows.From(u.Data, u.Highlight);           // category, value, highlightValue, key
        var margin = new Thickness(100, 8, 16, 24);
        double w = u.Size.Width - margin.Left - margin.Right;
        double h = u.Size.Height - margin.Top - margin.Bottom;

        var y = new BandScale<string>(rows.Select(r => r.Category), 0, h, padding: 0.2);
        var x = new LinearScale(0, rows.Count == 0 ? 0 : rows.Max(r => r.Value), 0, w).Nice();

        var baseBrush  = Frozen(new SolidColorBrush(_host.ColorFor("values")));
        var fadedBrush = Frozen(new SolidColorBrush(Color.FromArgb(100, baseBrush.Color.R, baseBrush.Color.G, baseBrush.Color.B)));
        bool hasSelection = u.Selection.Count > 0, hasHighlight = u.Highlight != null;

        _marks = rows.Select(r => new BarMark
        {
            Key = r.Key, Row = r,
            Rect = new Rect(margin.Left, margin.Top + y.Map(r.Category), x.Map(r.Value), y.Bandwidth)
        }).ToList();

        using (DrawingContext dc = _chart.MarksLayer.RenderOpen())
        {
            foreach (var m in _marks)
            {
                bool dim = (hasSelection && !u.Selection.Contains(m.Key)) || hasHighlight;
                dc.DrawRectangle(dim ? fadedBrush : baseBrush, null, m.Rect);
                if (hasHighlight && m.Row.HighlightValue.HasValue)   // cross-highlight: solid part over faded full bar
                    dc.DrawRectangle(baseBrush, null,
                        new Rect(m.Rect.X, m.Rect.Y, x.Map(m.Row.HighlightValue.Value), m.Rect.Height));
            }
        }

        using (DrawingContext dc = _chart.AxesLayer.RenderOpen())
        {
            AxisRenderer.DrawBottom(dc, x, new Point(margin.Left, margin.Top + h), u.Theme, fmt.XAxisFontSize, _chart.PixelsPerDip);
            AxisRenderer.DrawLeft(dc, y, new Point(margin.Left, margin.Top), u.Theme, _chart.PixelsPerDip);
        }
    }

    private void OnClick(object sender, MouseButtonEventArgs e)
    {
        var hit = HitTest(e.GetPosition(_chart));
        bool multi = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        _host.Select(hit == null ? new DataPoint[0] : new[] { hit.Row.ToDataPoint() }, multi);
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        var p = e.GetPosition(_chart);
        var hit = HitTest(p);
        if (hit == null) { _host.HideTooltip(); return; }
        _host.ShowTooltip(new[] { new TooltipItem(hit.Row.Category, _host.FormatValue(hit.Row.Value, null)) }, p);
    }

    private BarMark HitTest(Point p) => _marks.FirstOrDefault(m => m.Rect.Contains(p)); // band scale: could Invert() instead

    private static SolidColorBrush Frozen(SolidColorBrush b) { b.Freeze(); return b; }
    public void Dispose() { }
}
```

### 10.4 Scale classes (the d3 replacement)

```csharp
public interface IScale<TDomain>
{
    double Map(TDomain value);
}

public sealed class LinearScale : IScale<double>
{
    private double _d0, _d1;
    private readonly double _r0, _r1;
    public LinearScale(double d0, double d1, double r0, double r1) { _d0 = d0; _d1 = d1; _r0 = r0; _r1 = r1; }

    public double Map(double v) => _d1 == _d0 ? _r0 : _r0 + (v - _d0) / (_d1 - _d0) * (_r1 - _r0);
    public double Invert(double px) => _d0 + (px - _r0) / (_r1 - _r0) * (_d1 - _d0);

    public LinearScale Nice(int count = 5)
    {
        double step = TickStep(_d0, _d1, count);
        _d0 = Math.Floor(_d0 / step) * step;
        _d1 = Math.Ceiling(_d1 / step) * step;
        return this;
    }

    public IEnumerable<double> Ticks(int count = 5)
    {
        double step = TickStep(_d0, _d1, count);
        for (double t = Math.Ceiling(_d0 / step) * step; t <= _d1 + step / 2; t += step) yield return t;
    }

    // Same "nice numbers" rule as d3: step is 1, 2 or 5 times a power of ten
    private static double TickStep(double start, double stop, int count)
    {
        double raw = Math.Abs(stop - start) / Math.Max(1, count);
        if (raw == 0) return 1;
        double power = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        double err = raw / power;
        return power * (err >= 7.07 ? 10 : err >= 3.16 ? 5 : err >= 1.41 ? 2 : 1);
    }
}
```

`BandScale<T>`, `TimeScale` (ticks at year/quarter/month/day boundaries using `DateTime`), and `OrdinalScale<T>` (color mapping) follow the same shape.

### 10.5 Requirements for every visual

- Redraw on `SizeChanged` and `DpiChanged`.
- Title, subtitle, background, border, padding, shadow from the format pane.
- Empty-state text when required wells are empty; "too many values" notice at the 3,500-group limit.
- Tooltip as a single reusable `Popup` owned by the host (`IsHitTestVisible=false`, `Placement=Relative`).
- Context menu: Show as table, Sort by, Export data (CSV/Excel), Copy as image, Remove.
- Focus mode (expand to fill the canvas).

### 10.6 Table and Matrix

These are grids, not charts: implement as a custom virtualized control (`VirtualizingStackPanel` rows with `DrawingVisual` cells, or a `DataGrid` with row and column virtualization) bound to the `QueryResult`, with subtotals and expand/collapse for Matrix row headers, conditional formatting (background color scale, data bars) drawn per cell.

---

## 11. Interaction: slicers, cross-filtering, Edit Interactions

1. Viewer clicks bar "Bikes" in visual A. `IVisualHost.Select` publishes `SelectionChanged(A, [Product.Category = 'Bikes'], multi)` on the **InteractionBus** (a `Subject<SelectionEvent>` from System.Reactive).
2. `PageViewModel` subscribes with `.Throttle(TimeSpan.FromMilliseconds(30))` to coalesce rapid clicks.
3. For each other visual B, it reads the A→B interaction mode (defaults as in Power BI: highlight for bar/column/pie targets, filter for others; `None` skips B).
4. It rebuilds all affected QuerySpecs (selection goes to `Filters` or `Highlight`), cancels the previous batch, and calls `ExecuteBatchAsync` once.
5. Results return on the UI thread; each visual's `Update` redraws with a short tween.
6. Clicking empty chart space clears A's selection; Esc clears all.

**Slicers** are visuals whose output is a `FilterDef` stored in page runtime state (and saved as the default when the author saves). Variants: list with checkboxes, dropdown (`ComboBox` with checkable items), tiles, numeric/date "Between" with a two-thumb range control, relative date. Slicer item lists are themselves queried through the engine, so slicers cascade.

**Edit Interactions mode:** a ribbon toggle; when on, every other visual shows small filter/highlight/none icons in its corner (an adorner) for the currently selected source visual.

---

## 12. Themes

- Import Power BI theme JSON keys: `name`, `dataColors`, `background`, `foreground`, `tableAccent`, `textClasses`, `visualStyles`.
- `ThemeService` converts the JSON into a `ResolvedTheme` (C# object for visuals) and a `ResourceDictionary` merged into `Application.Resources` for the shell, so swapping a theme restyles chrome and visuals together.
- Built-ins: Default Light, Default Dark, High Contrast (also honors `SystemParameters.HighContrast`).
- Series colors are assigned deterministically (stable hash of series key → index into `dataColors`) and overridable per data point.

---

## 13. Publish, share and view (no JavaScript, no web UI)

### 13.1 Local sharing (MVP)

- Save `.lumen` file to a network share; colleagues with Lumen Desktop open it. Data is embedded, like a `.pbix`.
- **Export** for people without the app: page or whole report to **PDF** (render each page's visual tree into a `FixedDocument`, write XPS with `XpsDocumentWriter`, or draw directly with PdfSharp for vector PDF), **PNG** (`RenderTargetBitmap` at 2x DPI), **Excel** (visual data via EPPlus 4.5), and **Print** (`PrintDialog.PrintDocument`).

### 13.2 Lumen Server (Phase 3)

Web API 2 on IIS, .NET 4.7. It serves JSON to the WPF client only.

| Verb & route | Purpose | Power BI analogy |
|---|---|---|
| `POST /api/workspaces/{id}/reports` | Upload `.lumen` package | Publish |
| `GET /api/workspaces/{id}/reports` | Catalog with thumbnails | Workspace list |
| `GET /api/reports/{id}` | Report document (no data) | Open report in Service |
| `POST /api/reports/{id}/share` | Grant users/AD groups Viewer/Contributor | Share / Manage access |
| `POST /api/datasets/{id}/query/batch` | Run QuerySpecs on the server engine | Visual queries in Service |
| `POST /api/datasets/{id}/refresh` | Queue refresh (Hangfire) | Refresh now |
| `PUT /api/datasets/{id}/schedule` | Refresh schedule | Scheduled refresh |

- **Viewer mode:** Lumen Desktop opens a published report, receives only the `ReportDocument`, and uses `RemoteQueryEngine`, so **data never leaves the server unfiltered** and RLS can be enforced.
- **Auth:** Windows Integrated Authentication (intranet) via `HttpClientHandler { UseDefaultCredentials = true }`; Phase 4 option: Entra ID/ADFS with ADAL/MSAL for .NET Framework acquiring tokens in the WPF app.
- **Metadata DB (SQL Server 2016+):** `Workspace`, `Report` (DocumentJson `NVARCHAR(MAX)` with `ISJSON` check, `ROWVERSION`), `DatasetSnapshot` (`VARBINARY(MAX)` or FILESTREAM), `ReportAcl`.
- **Distribution of the client:** ClickOnce from an intranet URL (auto-update on launch) or an MSI via SCCM/Intune.

### 13.3 Email subscriptions (Phase 4, flagged risk)

Rendering WPF visuals inside a Windows Service works if done on a dedicated STA thread (`Thread.SetApartmentState(ApartmentState.STA)`, run a `Dispatcher`, render with `RenderTargetBitmap`), but Microsoft does not support WPF in services. Mitigation: run the renderer as a scheduled task in a logged-in "render agent" session, or limit subscriptions to Excel data exports.

---

## 14. SQL Server DirectQuery and RLS (Phase 4)

**DirectQuery:** a `SqlQueryEngine : IQueryEngine` translates `QuerySpec` to parameterized T-SQL using relationship metadata for joins:

```sql
SELECT p.Category, SUM(s.Qty * s.UnitPrice) AS [Sales Amount]
FROM dbo.FactSales AS s
JOIN dbo.DimProduct AS p ON p.ProductKey = s.ProductKey
JOIN dbo.DimDate    AS d ON d.DateKey    = s.OrderDateKey
WHERE d.CalendarYear = @p0 AND p.Color = @p1
GROUP BY GROUPING SETS ((p.Category), ())          -- groups + total in one round trip
ORDER BY [Sales Amount] DESC;
```

- `SqlCommand` with `CommandTimeout` and `ExecuteReaderAsync(ct)`, cancelled when a newer slicer click arrives.
- LQL functions that cannot be translated produce a binder error for DirectQuery tables, matching Power BI's DirectQuery limitations.
- Recommend clustered columnstore indexes on fact tables.

**SSAS Tabular alternative:** a `TabularQueryEngine` deploys the model with TOM (`Microsoft.AnalysisServices.Tabular`) and runs real DAX (`SUMMARIZECOLUMNS`) through ADOMD.NET. Both libraries are .NET Framework. This gives full DAX and VertiPaq performance if an SSAS license exists.

**RLS:** roles hold LQL predicates (`Region[ManagerLogin] = USERPRINCIPALNAME()`) evaluated **only on the server** and injected into every query, with identity in the cache key. RLS is not enforced on local `.lumen` files (same as Power BI Desktop: a file with embedded data is not a security boundary).

---

## 15. Non-functional requirements

| Area | Requirement |
|---|---|
| Performance | 1.1 targets; import 10M-row CSV < 60 s; open a 15-visual page < 1.5 s; 60 fps during canvas drag |
| Memory | 10M fact rows × 10 columns < 1.5 GB process working set |
| Client OS | Windows 10 / 11 x64 with .NET Framework 4.7 installed (in-box on Windows 10 1703+) |
| Server OS | Windows Server 2016+ with IIS 10 and .NET Framework 4.7 |
| SQL Server | 2016+ (JSON functions) |
| DPI | Per-monitor DPI aware (4.7 feature), crisp at 100–250% |
| Accessibility | UI Automation peers for shell and data points, keyboard-only authoring, High Contrast support |
| Reliability | Autosave to `%LOCALAPPDATA%\Lumen\Recovery` every 2 minutes; crash recovery on next launch; `SchemaVersion` migrations |
| Security | LQL never compiled via `CSharpCodeProvider`; all SQL parameterized; signed plugin assemblies only; server enforces ACLs and RLS |
| Observability | Serilog to file (client) and Seq/ELK (server); Performance Analyzer pane |
| Testing | NUnit/xUnit engine tests; golden tests vs Power BI DAX results on Contoso/AdventureWorks; chart snapshot tests (render to PNG, pixel diff with tolerance); FlaUI UI tests; BenchmarkDotNet |
| Deployment | ClickOnce or MSI for client; Web Deploy for server; TopShelf for the refresh service |

---

## 16. Milestones

Assumes 3 senior C# engineers (one with strong WPF graphics experience).

### Phase 0: Foundations (2 weeks)
- Solution, x64 builds, CI (Azure DevOps or TeamCity), Serilog, DI, MVVM base classes.
- `RibbonWindow` shell with AvalonDock panes; `CommandStack` with undo/redo on a dummy document; `.lumen` save/open.
- **Exit:** empty shell; add/move a placeholder visual, undo/redo, save, reopen.

### Phase 1: Data in, first visuals out (6 weeks)
1. CSV, Excel, SQL Server connectors streaming into columns; Transform editor with 6 steps.
2. Columnar store, Data view grid, snapshots in `.lumen`.
3. Model view with relationships; `fkToDimRow` indexes.
4. Engine: filter masks, propagation, group-by, implicit aggregations, async batch API.
5. `Lumen.Charting` core: Linear/Band scales, axis renderer, `ChartHost`, hit testing.
6. Visuals: Card, Table, Clustered Column; field wells with drag/drop.
- **Exit:** import Sales + Product, relate, show "Sum of Amount by Category" as column chart and table, filtered by a basic filter.

### Phase 2: MVP authoring (8 weeks)
1. LQL lexer/parser/binder/evaluator; AvalonEdit editor with completion and squiggles.
2. Remaining MVP visuals (bar/stacked/line/area/pie/donut/scatter/matrix/multi-row card) and animations.
3. Slicers; cross-filter and cross-highlight; Edit Interactions; Filters pane; TopN.
4. Canvas tooling: snapping, smart guides, align/distribute, selection pane, multi-page.
5. Format pane generated from options classes; themes (built-in + JSON import).
6. Date table + time intelligence; result cache; Performance Analyzer; export PNG/PDF/print.
7. Golden test suite green.
- **Exit (MVP):** success criteria 1.1 (local) met; 10 Power BI users complete the 15-minute task.

### Phase 3: Server and depth (8 weeks)
Lumen Server (Web API 2), publish/catalog/share, Viewer mode with `RemoteQueryEngine`, ClickOnce distribution, Refresh Service with schedules; hierarchies, drill down/up, drill-through, bookmarks, sync slicers; treemap, filled map (shapefile/GeoJSON parsed in C#, drawn with `StreamGeometry`), waterfall, KPI, gauge; MEF plugin SDK; bi-directional relationships, `USERELATIONSHIP`; compiled expression trees.

### Phase 4: Enterprise (8+ weeks)
DirectQuery to SQL Server with query folding of transform steps, RLS, Entra ID/ADFS option, SSAS Tabular backend, email subscriptions (with the 13.3 caveat), usage metrics and audit log.

---

## 17. Risks and mitigations

| Risk | Impact | Mitigation |
|---|---|---|
| .NET Framework 4.7 end of support (announced for 4.6.2/4.7/4.7.1; I believe January 2027, confirm on the Microsoft lifecycle page) | No security patches after that date | Accepted risk by decision. Keep `Lumen.Model`, `Lumen.Engine` and `Lumen.Charting`'s scale math free of WPF/`System.Web` references so they stay portable |
| Building charts ourselves takes longer than expected | MVP slips | Budgeted 6–8 weeks; OxyPlot as stopgap for line/scatter; strict MVP visual list |
| WPF rendering performance with many marks | Janky pages | `DrawingVisual` + frozen resources; `WriteableBitmap` above 20k marks; 3,500-group data reduction; profile with WPF Performance Suite / PerfView |
| No web viewer: every viewer needs Windows + install | Limits reach | ClickOnce auto-update; PDF/PNG/Excel exports for others; this is an explicit consequence of "no JavaScript" |
| DAX semantic subtleties | Wrong numbers | Small exact LQL; golden tests against Power BI; SSAS backend for full DAX |
| GC pauses with large arrays | Latency spikes | x64, `gcServer`/`gcConcurrent` in `App.config`, `ArrayPool<T>`, `GCSettings.LatencyMode = SustainedLowLatency` during interaction |
| WPF in services unsupported | Subscriptions fragile | Render agent session or data-only subscriptions (13.3) |
| Scope creep toward "all of Power BI" | Never ships | Non-goals in 5.3 are binding |

---

## 18. Open questions

1. Is Windows-only, install-required viewing acceptable for all audiences? (If anyone must view in a browser, some JavaScript becomes unavoidable; ASP.NET Web Forms/MVC pages with server-rendered PNG charts would be the only no-JS-authored compromise, and they would lose interactivity.)
2. Build the charting core (recommended) or buy a commercial WPF chart suite?
3. Intranet only (Windows auth) or also external users?
4. Is an SSAS Tabular license available?
5. Typical and maximum data sizes per dataset?

---

## Appendix A: Comparison of the three PRD versions

| Concern | v1 (JavaScript) | v2 (C# server + d3) | **v3 (C# only, this doc)** |
|---|---|---|---|
| Client | Browser, React | Browser, Razor + TypeScript | **WPF desktop** |
| Visuals | d3 SVG | d3 SVG | **C# `DrawingVisual` charting core** |
| Engine | TypeScript Web Worker | C# on IIS | **C# in-process (desktop) and on server (published)** |
| File format | JSON + IndexedDB | SQL Server | **`.lumen` ZIP + SQL Server when published** |
| Viewers | Any browser | Any browser | **Lumen Desktop (Viewer mode) or PDF/PNG/Excel exports** |
| Languages | TypeScript | C# + TypeScript | **C#, XAML, T-SQL only** |

## Appendix B: `App.config` essentials (client)

```xml
<configuration>
  <startup>
    <supportedRuntime version="v4.0" sku=".NETFramework,Version=v4.7" />
  </startup>
  <runtime>
    <gcServer enabled="false" />            <!-- workstation GC suits a UI app -->
    <gcConcurrent enabled="true" />         <!-- background GC keeps the UI responsive -->
    <gcAllowVeryLargeObjects enabled="true" />
  </runtime>
</configuration>
```

`app.manifest` (per-monitor DPI on .NET 4.7):

```xml
<application xmlns="urn:schemas-microsoft-com:asm.v3">
  <windowsSettings>
    <dpiAware xmlns="http://schemas.microsoft.com/SMI/2005/WindowsSettings">true/PM</dpiAware>
    <dpiAwareness xmlns="http://schemas.microsoft.com/SMI/2016/WindowsSettings">PerMonitorV2</dpiAwareness>
  </windowsSettings>
</application>
```

The refresh Windows Service and any self-hosted engine use `gcServer enabled="true"` instead.
