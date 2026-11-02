# PRD: "Lumen", a Power BI-style dashboard builder on C# and .NET Framework 4.7

| | |
|---|---|
| Author | Claude, for Ploverman |
| Date | 2026-10-31 |
| Status | Draft v2 (C# / .NET Framework 4.7 edition; supersedes the TypeScript-engine design in `PRD-js-dashboard-builder.md`) |
| Audience | Seasoned C# developer with Power BI and SQL Server expertise |

---

## 0. What changed from v1, in one paragraph

v1 put everything in the browser: a TypeScript engine in a Web Worker, React shell, d3 visuals. v2 moves **all logic that matters into C# on .NET Framework 4.7**: data import, transform pipeline, columnar store, relationship-aware filter propagation, the DAX-subset language, persistence, security, and the SQL Server connector. The browser becomes a **thin rendering and interaction layer**: d3.js draws the visuals (there is no C# charting option on .NET 4.7 that gives Power BI-grade interactive web visuals), and a small amount of TypeScript handles drag/drop on the canvas. Every slicer click becomes an HTTP call to the C# engine. Section 3 explains the two hosting options (web app vs WPF desktop) and why the web app is the recommended default.

---

## 1. Problem and goal

Power BI Desktop combines four products: **Power Query** (M), **VertiPaq / AS Tabular** (columnar store + DAX), the **report canvas**, and the **Power BI Service** (publish, share, refresh, RLS).

**Goal:** an in-house application, built on the .NET Framework 4.7 stack, that lets an analyst import data, model it, define measures, and author interactive multi-page reports with cross-filtering, then publish and share them, with an authoring experience recognizably similar to Power BI Desktop.

**Non-goals:** `.pbix` compatibility, full DAX, full M (see 5.3).

### 1.1 Success criteria

- A Power BI user builds a 3-page sales report from two sources (Sales fact + Product dimension) in under 15 minutes without docs.
- Slicer click to all visuals re-rendered: **< 200 ms p95** on a LAN with 10M fact rows in the server engine (the round trip replaces v1's in-browser 100 ms target).
- A saved report reopens identically from one JSON document stored in SQL Server.

---

## 2. Platform constraints of .NET Framework 4.7 (read this first)

These constraints shape every decision below.

| Constraint | Consequence |
|---|---|
| **No ASP.NET Core** (Core 2.x could run on Framework but is long out of support) | Web host is **ASP.NET MVC 5 + Web API 2 on IIS**, with OWIN middleware for auth and **SignalR 2** for push |
| **C# language version**: 4.7 shipped with C# 7.0; with VS 2017 15.7+/VS 2019 you can set `<LangVersion>7.3</LangVersion>` | Tuples, pattern matching (`is`/`switch` type patterns), `ref` locals/returns, `in` params, `Span<T>` syntax OK. **No** records, nullable reference types, switch expressions, `IAsyncEnumerable`, default interface methods (C# 8+ features need .NET Core 3+ or unsupported hacks) |
| `Span<T>`/`Memory<T>` only via the `System.Memory` NuGet ("slow span") | Usable, but plain arrays + `unsafe`/pointer loops are as fast or faster for the engine's hot paths |
| `System.ValueTuple` is built in from 4.7 | Fine for composite group keys |
| `System.Numerics.Vector<T>` (SIMD) via NuGet, RyuJIT x64 | Use for SUM/MIN/MAX over `double[]`; **run the app pool 64-bit** |
| Arrays > 2 GB need `<gcAllowVeryLargeObjects enabled="true"/>` | Set it in `web.config`; required for 100M+ row columns |
| Large Object Heap fragmentation | Pool column buffers (`ArrayPool<T>` from `System.Buffers` NuGet); set `GCSettings.LargeObjectHeapCompactionMode` after refreshes |
| IIS app pool recycling | In-memory datasets are lost on recycle; persist snapshots and reload lazily (section 8.6) |
| **Target framework**: .NET Framework 4.7 for build and deployment (`<TargetFrameworkVersion>v4.7</TargetFrameworkVersion>`) | Every library chosen in 6.2 runs on 4.7. When a NuGet package offers both `net45`/`net46x` and `netstandard2.0` assets, prefer packages whose `net4x` asset is used: .NET Standard 2.0 support on 4.7 relies on extra facade assemblies and binding redirects (`<AutoGenerateBindingRedirects>true</AutoGenerateBindingRedirects>`). End of support is tracked as a risk in section 15 |

---

## 3. Hosting options and recommendation

### Option A (recommended): Web application on IIS

```
Browser:  Razor-served page + TypeScript + d3.js (render only)
Server:   ASP.NET MVC 5 / Web API 2 / SignalR 2 on IIS, .NET Framework 4.7, 64-bit
Engine:   C# in-memory columnar engine (in-process, or separate Windows Service)
Storage:  SQL Server (metadata + data snapshots), optional file share for snapshots
```

- Same deliverable as Power BI Desktop + Service combined: authors and viewers use a browser; sharing is just a URL with ACLs.
- d3 runs where it was designed to run.
- Cost: each interaction is a network round trip, so the engine must be fast and the API chatty-proof (batching, caching).

### Option B: WPF desktop app (closest to Power BI Desktop itself)

```
WPF shell (.NET Framework 4.7) — panes, ribbon, model diagram, canvas — MVVM
  └─ WebView2 control (supports .NET Framework 4.6.2+) hosting d3 visuals
Engine: same C# engine, in-process
Publish: same Web API server as Option A
```

- Pros: rich desktop editing, local data, no round trip for the author, and the WPF ribbon/property grid feel familiar.
- Cons: two UIs to build (WPF authoring + web viewing), WebView2 runtime deployment, interop messages between WPF and JavaScript for every visual.
- **Do not use the old WinForms/WPF `WebBrowser` control**: it is IE11, and modern d3 (v7, ES2015+) will not run on it without heavy transpiling.

**Recommendation:** build Option A. The engine and API are designed so a WPF authoring client (Option B) can be added later against the same engine assembly, the way Power BI Desktop and the Service share VertiPaq.

### Why d3.js stays (and not p5.js or a .NET chart library)

- .NET 4.7 web stacks have no first-class interactive charting: `System.Web.DataVisualization` (MS Chart) renders server-side images with no cross-filtering or hover; third-party suites (Telerik, DevExpress, Syncfusion) work but lock the visual model to the vendor and cost money.
- d3 binds data to SVG elements, which gives hit-testing, tooltips, keyboard focus and accessibility for free. p5.js is an immediate-mode canvas sketching library (think overriding `OnPaint` and redrawing everything every frame); you would rebuild axes, hit-testing and accessibility yourself.
- The C# server does all the math; d3 receives already-aggregated rows (a few hundred to 3,500 per visual), which is d3's sweet spot.

---

## 4. Concept map: Power BI → Lumen (C#)

| Power BI | Lumen component | .NET implementation |
|---|---|---|
| Get Data | **Connectors** | `IDataConnector`: CSV (`TextFieldParser` or CsvHelper), Excel (ExcelDataReader / EPPlus 4.x LGPL), SQL Server (`System.Data.SqlClient`), OData, JSON (Json.NET) |
| Power Query applied steps | **Transform Pipeline** | `IReadOnlyList<TransformStep>`; each step is a class with `Apply(DataTable)` → typed column batches |
| Query folding | Step folding to T-SQL | `ISqlFoldable` on steps; fold until first non-foldable step |
| VertiPaq | **Columnar Store** | `double[]`, `long[]`, `int[]` dictionary codes, `BitArray`/`ulong[]` masks |
| Relationships | **Model** | `RelationshipDef` + precomputed `int[] fkToDimRow` |
| DAX | **LQL** (DAX subset) | Hand-written lexer + Pratt parser → AST → compiled to `Expression<Func<EvalContext, Value>>` via `System.Linq.Expressions` |
| Filter context | `FilterContext` class | Immutable, structurally hashed |
| `SUMMARIZECOLUMNS` query | `QuerySpec` | JSON from browser, deserialized with Json.NET |
| Report canvas | Canvas editor (browser) | TypeScript + `interact.js` or `dnd-kit`-free vanilla drag; d3 visuals |
| Visual capabilities (`capabilities.json`) | `VisualCapabilities` | C# metadata served to the browser |
| Themes | Theme JSON | Stored and validated server-side |
| Publish / Service | **Lumen Server** | Web API 2 + SQL Server metadata DB |
| Scheduled refresh | **Refresh service** | Windows Service with Quartz.NET or Hangfire 1.7 (supports .NET 4.5+) |
| RLS | **Security filters** | LQL predicates injected server-side |
| DirectQuery | **SQL pushdown** | `QuerySpec` → parameterized T-SQL |
| Live connection to AS | **Optional SSAS Tabular backend** | ADOMD.NET; send real DAX (section 8.7) |

---

## 5. Scope

### 5.1 MVP (Phases 1–2)

- Import CSV, Excel, SQL Server table/query, JSON.
- Transform steps: promote headers, change type, rename, remove columns, filter rows, replace values, split column, add custom column, remove duplicates, unpivot.
- Model: many tables, 1:* single-direction relationships, active/inactive, mark as date table, sort-by-column, hidden columns, format strings (.NET format strings: `"#,0.00"`, `"0.0%"`, `"yyyy-MM-dd"`).
- LQL measures: `SUM, AVERAGE, MIN, MAX, COUNT, COUNTROWS, DISTINCTCOUNT, SUMX/AVERAGEX (no context transition), DIVIDE, IF, SWITCH, VAR/RETURN, CALCULATE` (column filters), `ALL, FILTER` (simple), `TOTALYTD, SAMEPERIODLASTYEAR`.
- Canvas: multiple pages, drag, resize, snap grid, z-order, align/distribute, copy/paste, undo/redo.
- Visuals (d3): Card, Multi-row card, Table, Matrix, Clustered and Stacked bar/column, Line, Area, Pie/Donut, Scatter, Slicer (list, dropdown, numeric/date range, relative date).
- Cross-filter / cross-highlight with Edit Interactions; Filters pane at visual, page, report scope.
- Themes: built-in light/dark/high contrast, import theme JSON.
- Save/load in the server's SQL Server metadata DB; Windows (Integrated) authentication for MVP.

### 5.2 Later

- Phase 3: share/permissions, viewer mode, scheduled refresh, drill-down/through, bookmarks, sync slicers, treemap/map/waterfall/KPI/gauge, custom visual SDK, bi-directional relationships, USERELATIONSHIP.
- Phase 4: DirectQuery to SQL Server, RLS, Entra ID / ADFS auth via OWIN OIDC, SSAS Tabular backend, embedding, WPF authoring client (Option B).

### 5.3 Non-goals

Full DAX/M parity, `.pbix` read/write, many-to-many relationships in MVP, mobile authoring, paginated reports (use SSRS), natural-language Q&A, Blazor or .NET Core migration (out of scope by constraint).

---

## 6. Architecture

```
┌──────────────── Browser (render + interaction only) ─────────────────┐
│ Razor shell (_Layout.cshtml) + TypeScript bundle (webpack)            │
│  Fields pane │ Canvas (pages, drag/resize) │ Format pane │ Model view │
│  d3 visuals: IVisual plugins render QueryResult JSON                  │
│  Client state: ReportDocument (JSON) + undo stack of commands         │
└───────────────┬──────────────────────────────────────▲──────────────┘
                │ HTTPS JSON (Web API 2)                │ SignalR 2 (refresh done,
                │ POST /api/query/batch                 │  collaborator edits)
┌───────────────▼──────────────────────────────────────┴──────────────┐
│ IIS app pool (.NET 4.7, 64-bit, AlwaysRunning, idle timeout 0)       │
│  Lumen.Web        MVC 5 controllers, Web API 2 controllers, OWIN auth │
│  Lumen.Api        DTOs, validation, QuerySpec batching                │
│  Lumen.Engine     columnar store, filter propagation, LQL, cache      │
│  Lumen.Model      SemanticModel, ReportDocument, commands, migrations │
│  Lumen.Connectors CSV, Excel, SqlClient, JSON; transform pipeline     │
│  Lumen.Security   ACLs, RLS predicate injection                       │
└───────────────┬────────────────────────────┬──────────────────────────┘
                │ ADO.NET / EF6              │ ADO.NET (DirectQuery)
┌───────────────▼─────────────┐   ┌──────────▼───────────────────────────┐
│ SQL Server: LumenMeta DB     │   │ Customer SQL Server sources          │
│ (reports, models, ACLs,      │   │ (columnstore fact tables)            │
│  snapshots as VARBINARY/     │   └──────────────────────────────────────┘
│  FILESTREAM)                 │
└─────────────────────────────┘
┌────────────────────────────────────────────────────────────────────┐
│ Lumen.RefreshService (Windows Service, Hangfire/Quartz.NET):       │
│ runs transform pipelines on schedule, writes snapshots, notifies    │
│ web tier via SignalR backplane (SQL Server) to hot-swap datasets    │
└────────────────────────────────────────────────────────────────────┘
```

### 6.1 Solution layout (Visual Studio 2019/2022, packages.config or PackageReference)

```
Lumen.sln
  src/Lumen.Model/          (class library, net47)  POCOs, JSON contracts, commands
  src/Lumen.Engine/         (net47)  columnar store, LQL compiler, query executor
  src/Lumen.Connectors/     (net47)  connectors + transform steps
  src/Lumen.Security/       (net47)
  src/Lumen.Web/            (ASP.NET Web Application, MVC + Web API, net47)
     Scripts/app/           TypeScript sources (canvas, panes)
     Scripts/visuals/       d3 visual plugins (TypeScript)
  src/Lumen.RefreshService/ (Windows Service, net47; TopShelf for easy install)
  tests/Lumen.Engine.Tests/ (NUnit or xUnit 2.4, net47)
  tests/Lumen.Golden/       sample model + expected results exported from Power BI
  tests/Lumen.E2E/          Selenium WebDriver or Playwright for .NET (needs netcoreapp; run as separate test project)
```

### 6.2 Key libraries (all .NET Framework 4.7 compatible)

| Need | Library |
|---|---|
| JSON | Newtonsoft.Json 12/13 (Web API 2 default formatter) |
| DI | Autofac 4.x with Autofac.WebApi2 / Autofac.Mvc5, or Simple Injector |
| ORM for metadata | Entity Framework 6.x (or Dapper for speed) |
| Logging | Serilog 2.x or NLog 4.x |
| CSV | CsvHelper (12.x supports net45+) |
| Excel | ExcelDataReader (read), EPPlus 4.5 (LGPL; 5+ is commercial) |
| Background jobs | Hangfire 1.7 with SQL Server storage |
| Real time | Microsoft.AspNet.SignalR 2.4 with SQL Server backplane |
| Auth | Microsoft.Owin.Security.OpenIdConnect (Entra ID / ADFS), or Windows Auth |
| SIMD / buffers | System.Numerics.Vectors, System.Buffers, System.Memory |
| Parser (optional) | Hand-written (recommended) or ANTLR4 C# runtime |
| Front end | TypeScript 4.x + webpack, d3 v7, Monaco editor (LQL editing) |

---

## 7. Data and state model (C#)

The `ReportDocument` is the single serializable unit, stored as JSON in SQL Server and sent to the browser. The browser and the server share the schema: C# classes are the source of truth, and TypeScript interfaces are generated from them (NJsonSchema / NSwag, or TypeLite).

```csharp
// ----- Semantic model (≈ TMSL / model.bim) -----
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
    public StorageMode StorageMode { get; set; }    // Import | DirectQuery (Phase 4)
}

public sealed class ColumnDef
{
    public string Id { get; set; }
    public string Name { get; set; }
    public LumenDataType DataType { get; set; }
    public string FormatString { get; set; }        // .NET format string
    public SummarizeBy SummarizeBy { get; set; }
    public string SortByColumnId { get; set; }
    public bool IsHidden { get; set; }
    public string Expression { get; set; }          // non-null => calculated column
}

public sealed class RelationshipDef
{
    public string Id { get; set; }
    public string FromTableId { get; set; }  public string FromColumnId { get; set; } // many side
    public string ToTableId { get; set; }    public string ToColumnId { get; set; }   // one side
    public Cardinality Cardinality { get; set; }            // ManyToOne | OneToOne
    public CrossFilter CrossFilter { get; set; }            // Single (MVP) | Both (Phase 3)
    public bool IsActive { get; set; } = true;
}

public sealed class MeasureDef
{
    public string Id { get; set; }
    public string Name { get; set; }
    public string HomeTableId { get; set; }
    public string Expression { get; set; }   // LQL source
    public string FormatString { get; set; }
    public string DisplayFolder { get; set; }
}

// ----- Transform steps (≈ M applied steps), polymorphic JSON via a "kind" discriminator -----
[JsonConverter(typeof(TransformStepConverter))]
public abstract class TransformStep
{
    public abstract string Kind { get; }
    public abstract ColumnBatch Apply(ColumnBatch input, TransformContext ctx);
    public virtual bool TryFoldToSql(SqlFoldContext ctx) => false;  // query folding
}
public sealed class RenameColumnsStep : TransformStep { /* Dictionary<string,string> Map */ }
public sealed class FilterRowsStep    : TransformStep { /* string Predicate (LQL) */ }
public sealed class UnpivotStep       : TransformStep { /* Keep, AttributeName, ValueName */ }
// ... ChangeType, RemoveColumns, ReplaceValues, SplitColumn, AddColumn, RemoveDuplicates

// ----- Report (≈ report.json / PBIR) -----
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
    public int Width { get; set; } = 1280;
    public int Height { get; set; } = 720;
    public List<FilterDef> PageFilters { get; set; } = new List<FilterDef>();
    public List<VisualDef> Visuals { get; set; } = new List<VisualDef>();
    public List<InteractionOverride> Interactions { get; set; } = new List<InteractionOverride>();
}

public sealed class VisualDef
{
    public string Id { get; set; }
    public string Type { get; set; }                       // "clusteredBar", "slicer", ...
    public Rect Layout { get; set; }                        // X, Y, W, H, Z
    public Dictionary<string, List<FieldRef>> Roles { get; set; } // wells
    public List<FilterDef> VisualFilters { get; set; }
    public JObject Format { get; set; }                     // validated against visual's schema
    public SortSpec Sort { get; set; }
    public TopNSpec TopN { get; set; }
}

public sealed class FieldRef
{
    public FieldKind Kind { get; set; }          // Column | Measure | HierarchyLevel
    public string TableId { get; set; }
    public string ColumnId { get; set; }
    public string MeasureId { get; set; }
    public SummarizeBy? Aggregation { get; set; } // implicit measure ("Sum of Qty")
}

public sealed class FilterDef
{
    public string Id { get; set; }
    public FieldRef Target { get; set; }
    public FilterType Type { get; set; }         // Basic | Advanced | TopN | RelativeDate
    public List<object> Values { get; set; }     // Basic: IN list
    public List<Condition> Conditions { get; set; }
    public LogicalOp Logic { get; set; }
}

public sealed class InteractionOverride
{
    public string SourceVisualId { get; set; }
    public string TargetVisualId { get; set; }
    public InteractionMode Mode { get; set; }    // Filter | Highlight | None
}
```

### 7.1 Editing model: commands and undo

- Authoring edits happen in the browser for responsiveness, as small **commands** (`MoveVisual`, `AddFieldToWell`, `CreateMeasure`...), the same `ICommand` + undo stack pattern as a WPF editor.
- The browser sends commands (not whole documents) to `POST /api/reports/{id}/commands` with the document's `Version` (a `rowversion` column). The server re-applies them with the C# implementation, validates (e.g. LQL compiles, field exists), bumps the version, and returns errors per command. Optimistic concurrency conflict → HTTP 409, client reloads.
- Model-changing commands (new measure, relationship, calculated column) also invalidate the engine cache for that dataset.

### 7.2 Metadata database (SQL Server)

```sql
CREATE TABLE dbo.Workspace (WorkspaceId UNIQUEIDENTIFIER PRIMARY KEY, Name NVARCHAR(200) NOT NULL);
CREATE TABLE dbo.Report (
    ReportId     UNIQUEIDENTIFIER PRIMARY KEY,
    WorkspaceId  UNIQUEIDENTIFIER NOT NULL REFERENCES dbo.Workspace,
    Name         NVARCHAR(200) NOT NULL,
    DocumentJson NVARCHAR(MAX) NOT NULL CHECK (ISJSON(DocumentJson) = 1),
    Version      ROWVERSION,
    ModifiedBy   NVARCHAR(256) NOT NULL,
    ModifiedUtc  DATETIME2(3) NOT NULL);
CREATE TABLE dbo.DatasetSnapshot (
    ReportId UNIQUEIDENTIFIER NOT NULL REFERENCES dbo.Report,
    TableId  NVARCHAR(64) NOT NULL,
    RefreshedUtc DATETIME2(3) NOT NULL,
    Payload  VARBINARY(MAX) NOT NULL,     -- engine's binary column format, GZip/LZ4
    PRIMARY KEY (ReportId, TableId));
CREATE TABLE dbo.ReportAcl (ReportId UNIQUEIDENTIFIER, Principal NVARCHAR(256), Role TINYINT,
    PRIMARY KEY (ReportId, Principal));
```

`ISJSON` needs SQL Server 2016+. For large snapshots consider FILESTREAM or a file share and store only the path.

---

## 8. The C# query engine (the heart of the product)

### 8.1 Columnar storage

Step by step, at refresh time:

1. The connector streams rows (`IDataReader` for SqlClient, CsvHelper for CSV) into **column builders**, never a `DataTable` (a `DataTable` boxes every value; 10M rows × 10 columns would be ~100M objects).
2. Each column becomes one of:

```csharp
public abstract class Column
{
    public string Name { get; }
    public int RowCount { get; }
    public abstract LumenDataType Type { get; }
}
public sealed class DoubleColumn : Column { public double[] Values; public ulong[] NullMask; }
public sealed class Int64Column  : Column { public long[] Values;   public ulong[] NullMask; }
public sealed class DateColumn   : Column { public int[] DaysSinceEpoch; public ulong[] NullMask; }
// Strings and low-cardinality values: dictionary encoding, like VertiPaq
public sealed class DictColumn   : Column
{
    public int[] Codes;            // per row; 0 = BLANK
    public object[] Dictionary;    // distinct values, sorted (so code order = sort order)
}
```

3. **Relationship index.** For each relationship, build `int[] fkToDimRow` (length = fact rows) by hashing the dimension key column into a `Dictionary<long,int>` (or `Dictionary<string,int>`) and probing per fact row. Unmatched rows map to -1, the "blank" dimension member, which is exactly how Power BI shows "(Blank)" for orphan keys.
4. Persist each table as a compact binary snapshot (`BinaryWriter` of the arrays + GZip) into `DatasetSnapshot`.

**Memory math for a C# developer:** 10M rows × (3 `int` code columns + 2 `double` columns) = 10M × (12 + 16) bytes ≈ 280 MB. Fine on a 64-bit app pool; impossible on 32-bit. Hence `Enable 32-Bit Applications = False`.

### 8.2 QuerySpec (what a visual asks for)

Equivalent to the `SUMMARIZECOLUMNS` query Power BI generates per visual (visible in Performance Analyzer):

```csharp
public sealed class QuerySpec
{
    public List<FieldRef> GroupBy { get; set; }     // Axis + Legend wells
    public List<FieldRef> Measures { get; set; }    // Values well
    public List<FilterDef> Filters { get; set; }    // report ∪ page ∪ visual ∪ slicers ∪ cross-filters
    public List<FilterDef> Highlight { get; set; }  // cross-highlight
    public List<OrderBy> OrderBy { get; set; }
    public int? TopN { get; set; }
    public int Limit { get; set; } = 3500;          // data reduction, like Power BI
    public bool IncludeTotals { get; set; }
}
```

The browser sends **one batch per page refresh**: `POST /api/datasets/{id}/query/batch` with all visuals' specs, so a slicer click on a 12-visual page is one HTTP request, executed in parallel on the server with `Parallel.ForEach` (or `Task.WhenAll` over `Task.Run`).

### 8.3 Evaluation algorithm

```csharp
public QueryResult Execute(QuerySpec q, SecurityContext sec)
{
    // 1. Collect filters: report, page, slicers, cross-filters, visual, + RLS predicates (always on)
    var filters = q.Filters.Concat(_rls.FiltersFor(sec));

    // 2. Each filter -> row mask on its own table (bitset over that table's rows)
    var masks = new Dictionary<string, Bitset>();
    foreach (var f in filters)
        masks[f.Target.TableId] = masks.TryGetValue(f.Target.TableId, out var m)
            ? m.And(EvaluateFilter(f)) : EvaluateFilter(f);

    // 3. Propagate one side -> many side (single-direction relationships)
    var factMask = PropagateToFact(_model.FactTableFor(q), masks);

    // 4. Group surviving fact rows by composite key of dictionary codes
    //    (codes may come from dimension columns via fkToDimRow[row])
    var groups = GroupRows(factMask, q.GroupBy);       // Dictionary<GroupKey, RowList>

    // 5. Evaluate each measure per group, in that group's filter context
    // 6. Totals: re-evaluate measures with no group-by filter (NOT sum of groups),
    //    so DISTINCTCOUNT and ratios are correct, as in DAX
    // 7. Sort, TopN, Limit; 8. Cache by hash(spec + dataset version + RLS identity)
    ...
}
```

Detail of step 3, the core of "it behaves like Power BI":

```csharp
Bitset PropagateToFact(TableData fact, Dictionary<string, Bitset> dimMasks)
{
    var result = dimMasks.TryGetValue(fact.Id, out var own) ? own.Clone() : Bitset.AllSet(fact.RowCount);
    foreach (var rel in _model.RelationshipsFrom(fact.Id).Where(r => r.IsActive))
    {
        if (!dimMasks.TryGetValue(rel.ToTableId, out var dimMask)) continue;
        int[] fk = _index.FkToDimRow(rel.Id);
        // Hot loop: keep it a plain for over arrays; JIT removes bounds checks
        for (int r = 0; r < fk.Length; r++)
            if (result[r] && (fk[r] < 0 || !dimMask[fk[r]])) result.Clear(r);
    }
    return result;
}
```

Snowflake schemas (Product → Subcategory → Category) are handled by first propagating Category's mask to Subcategory, then Subcategory's to Product, then to the fact: walk the relationship graph from the outermost dimensions inward.

**Group key:** for up to 4 group-by columns, pack dictionary codes into a `long` (16 bits each) or a `ValueTuple<int,int,int,int>`; with `Dictionary<long, Accumulator>` this is integer-only hashing.

**Aggregation fast path:** implicit and simple measures (`SUM`, `COUNT`, `MIN`, `MAX`, `AVERAGE`) are evaluated in a single pass over the fact rows, updating `Accumulator[]` per group, no per-group re-scan. Use `Parallel.For` with thread-local accumulators merged at the end for tables over ~1M rows.

### 8.4 Performance targets for the engine

| Scenario | Target (server, 8 cores) |
|---|---|
| 10M fact rows, 2 dim filters, group by 1 dim column, 2 SUM measures | < 40 ms |
| Same with DISTINCTCOUNT | < 120 ms |
| Cached result | < 2 ms |
| Page batch of 12 visuals | < 150 ms server time |

Measure with BenchmarkDotNet (supports .NET Framework) and keep benchmarks in CI.

### 8.5 LQL: the DAX subset, compiled in C#

Pipeline, Roslyn-style:

1. **Lexer** → tokens (`'Sales'[Qty]`, `[Measure]`, numbers, strings, operators, `VAR`, `RETURN`).
2. **Pratt parser** → AST (`BinaryExpr`, `CallExpr`, `ColumnRef`, `MeasureRef`, `VarBlock`).
3. **Binder** resolves names against `SemanticModel`, checks function arity and types, emits diagnostics with line/column (shown inline in the Monaco editor in the browser).
4. **Compiler** produces a delegate. Two options:
   - Closure tree (`Func<EvalContext, Value>` objects composed at runtime): simple and debuggable. **Start here.**
   - `System.Linq.Expressions` compiled to IL with `Expression.Lambda(...).Compile()`: faster for row-context expressions (calculated columns, `SUMX` bodies). Add in Phase 3.
5. **Semantics:**
   - Measures run in **filter context**; calculated columns in **row context** at refresh.
   - `CALCULATE(expr, filters…)` builds a new `FilterContext` (replace filters on the same column, like DAX) and re-evaluates.
   - `ALL(Table | Column)` removes filters; `FILTER(Table, pred)` returns a mask.
   - `BLANK` is a real value (`Value.Blank`) with DAX propagation rules; `DIVIDE(x, 0)` returns BLANK.
   - Time intelligence requires a marked date table.

Value type (avoid boxing in hot paths):

```csharp
public readonly struct Value
{
    public readonly ValueKind Kind;   // Blank, Number, Integer, String, Boolean, Date
    public readonly double Number;
    public readonly long Integer;
    public readonly string String;
    public static readonly Value Blank = default(Value);
    // operators implement DAX rules: Blank + 1 = 1, Blank * 1 = Blank, etc.
}
```

Measures the MVP must evaluate correctly (golden-tested against Power BI on the same data):

```
Sales Amount      = SUMX(Sales, Sales[Qty] * Sales[UnitPrice])
Margin %          = DIVIDE([Sales Amount] - [Total Cost], [Sales Amount])
Sales YTD         = TOTALYTD([Sales Amount], 'Date'[Date])
Red Sales         = CALCULATE([Sales Amount], Product[Color] = "Red")
All-Product Share = DIVIDE([Sales Amount], CALCULATE([Sales Amount], ALL(Product)))
```

### 8.6 Dataset lifecycle inside IIS

- `DatasetCache` singleton (`ConcurrentDictionary<Guid, Lazy<Task<LoadedDataset>>>`) loads snapshots on first use.
- App pool: **AlwaysRunning**, idle timeout 0, recycle on a schedule off-hours, Application Initialization module enabled with a warm-up URL that preloads the most-used datasets.
- Memory budget per pool with LRU eviction of cold datasets; expose usage on an admin page.
- On refresh completion the Refresh Service writes a new snapshot; a SignalR message tells web servers to load the new version alongside the old and swap atomically (`Interlocked.Exchange`), so queries in flight finish on the old one.
- **Scale-out option:** if one IIS box is not enough, move `Lumen.Engine` into a separate Windows Service per dataset shard and call it over WCF (net.tcp) or a small self-hosted Web API (OWIN self-host). The engine assembly is unchanged.

### 8.7 Alternative backend: SQL Server Analysis Services Tabular

Since you already run SQL Server, consider an engine option Power BI itself uses: **SSAS Tabular** (VertiPaq + real DAX).

- Deploy the model as TMSL via TOM (`Microsoft.AnalysisServices.Tabular`), and run each visual's query as real DAX (`SUMMARIZECOLUMNS`) through **ADOMD.NET** (`AdomdConnection`, `AdomdCommand`), both available for .NET Framework.
- The `IQueryEngine` interface lets you swap: `InMemoryEngine` (custom) vs `SsasTabularEngine` (generates DAX from `QuerySpec`).
- Pros: full DAX, proven VertiPaq performance, RLS built in. Cons: SSAS licensing (Enterprise or Standard with limits), an extra server to run, and LQL becomes "DAX passthrough".
- Recommendation: keep the custom engine for MVP (learning value and no licensing), and build the SSAS backend in Phase 4 as an enterprise option.

```csharp
public interface IQueryEngine
{
    Task LoadAsync(Guid datasetId, SemanticModel model, CancellationToken ct);
    Task<QueryResult> ExecuteAsync(QuerySpec spec, SecurityContext sec, CancellationToken ct);
}
```

---

## 9. Web API surface (Web API 2)

| Verb & route | Purpose | PBI analogy |
|---|---|---|
| `GET /api/reports/{id}` | Report document + version | Open .pbix / report |
| `POST /api/reports/{id}/commands` | Apply authoring commands (optimistic concurrency) | Editing in Desktop |
| `POST /api/datasets/{id}/import` | Upload CSV/Excel (multipart) or register SQL source | Get Data |
| `POST /api/datasets/{id}/preview` | Run transform steps on first 1,000 rows | Power Query preview |
| `POST /api/datasets/{id}/refresh` | Full refresh (queued to Hangfire) | Refresh |
| `POST /api/datasets/{id}/query/batch` | Execute QuerySpecs for a page | Visual queries |
| `POST /api/measures/validate` | Compile LQL, return diagnostics | DAX formula bar errors |
| `GET /api/visuals/capabilities` | Wells + format schema per visual type | `capabilities.json` |
| `POST /api/reports/{id}/share` | Grant users/groups (Phase 3) | Share |
| SignalR hub `/signalr` → `ReportHub` | Refresh completed, version changed | Service notifications |

```csharp
[RoutePrefix("api/datasets")]
[Authorize]
public sealed class QueryController : ApiController
{
    private readonly IQueryEngine _engine;
    private readonly ISecurityContextFactory _sec;
    public QueryController(IQueryEngine engine, ISecurityContextFactory sec) { _engine = engine; _sec = sec; }

    [HttpPost, Route("{datasetId:guid}/query/batch")]
    public async Task<IHttpActionResult> Batch(Guid datasetId, [FromBody] List<QuerySpec> specs,
                                               CancellationToken ct)
    {
        var sec = _sec.For(User, datasetId);              // ACL check + RLS identity
        var tasks = specs.Select(s => _engine.ExecuteAsync(s, sec, ct));
        return Ok(await Task.WhenAll(tasks));
    }
}
```

Serialization: send results as **columnar JSON** (`{ columns: [...], data: [[col0 values], [col1 values]] }`) rather than an array of row objects; it is 2–4x smaller. Enable dynamic compression in IIS.

---

## 10. Browser layer (TypeScript + d3), deliberately thin

The browser is responsible for: canvas layout and drag/resize, panes and wells, sending commands and query batches, and rendering results with d3. It does **no aggregation** and **no filter logic**; it only describes what the user clicked.

### 10.1 Visual plugin contract

Mirrors Power BI's custom visual `IVisual`, so the concept transfers:

```ts
interface IVisual {
  init(element: HTMLElement, host: IVisualHost): void;
  update(opts: VisualUpdateOptions): void;   // data, size, format, theme, selection changed
  destroy(): void;
}
interface IVisualHost {
  select(points: DataPointSelection[], multi: boolean): void; // -> cross-filter
  showTooltip(items: TooltipItem[], x: number, y: number): void;
  hideTooltip(): void;
  formatValue(v: unknown, formatString?: string): string;   // .NET-style format strings, JS port
  colorFor(seriesKey: string): string;
}
```

### 10.2 Example d3 visual (abridged)

```ts
update({ data, size, selection }: VisualUpdateOptions) {
  const rows = toRows(data);  // server already aggregated: [{ key, category, value }]
  const y = d3.scaleBand().domain(rows.map(r => r.category)).range([0, h]).padding(0.2);
  const x = d3.scaleLinear().domain([0, d3.max(rows, r => r.value) ?? 0]).nice().range([0, w]);
  const selected = new Set(selection.map(s => s.key));

  this.bars.selectAll<SVGRectElement, Row>('rect')
    .data(rows, r => r.key)          // keyed join: like ObservableCollection diffing an ItemsControl
    .join('rect')
      .attr('y', r => y(r.category)!).attr('height', y.bandwidth())
      .attr('opacity', r => selected.size && !selected.has(r.key) ? 0.4 : 1)
      .on('click', (ev, r) => this.host.select([{ key: r.key, filter: r.filter }], ev.ctrlKey))
    .transition().duration(250)
      .attr('width', r => x(r.value));
}
```

### 10.3 Cross-filter flow (end to end)

1. Viewer clicks bar "Bikes" in visual A. d3 handler calls `host.select`.
2. The client interaction bus records `{ visual: A, filter: Product.Category IN ('Bikes') }`.
3. For every other visual B on the page, it reads the Edit Interactions mode A→B (defaults as in Power BI: highlight for bar/column/pie, filter for others).
4. It rebuilds the QuerySpecs (filter goes into `Filters` or `Highlight`) and sends **one** batch to the C# engine.
5. Results come back; each visual's `update()` re-renders with transitions.

### 10.4 Format pane and themes

- Each visual type's format schema is a C# class with attributes (`[FormatCard("X axis")]`, `[FormatProperty("Font size", Min = 8, Max = 40)]`); the server emits JSON Schema; the browser generates the pane, much like a WinForms `PropertyGrid`.
- Theme JSON (Power BI-compatible keys: `dataColors`, `background`, `foreground`, `textClasses`, `visualStyles`) is validated server-side and applied as CSS variables plus a resolved object passed to d3.

### 10.5 Number and date formatting

Format strings are .NET style everywhere. The server formats totals and cards when convenient (`value.ToString(fmt, culture)`), and the browser uses a small JS port of .NET custom numeric/date format strings for axes and tooltips, so the author learns one format syntax.

---

## 11. Security

- **MVP:** Windows Integrated Authentication in IIS (intranet), `[Authorize]` on all controllers, ACL table per report.
- **Phase 4:** OWIN OpenID Connect for Entra ID or ADFS; cookie auth for MVC, bearer for API.
- **RLS:** roles hold LQL boolean predicates (`Region[ManagerEmail] = USERPRINCIPALNAME()`); `SecurityContext` injects them into every QuerySpec server-side and includes the identity in the cache key. The browser never sees unfiltered data.
- LQL is parsed and interpreted, never passed to `CSharpCodeProvider` or `eval`.
- DirectQuery SQL is always parameterized (`SqlParameter`), never concatenated.
- Anti-forgery tokens on MVC posts; CSP headers; encode all user text in titles/tooltips.
- Custom visuals (Phase 3) run in sandboxed iframes.

---

## 12. SQL Server DirectQuery (Phase 4)

`QuerySpec` → T-SQL generator, reusing relationship metadata for joins:

```sql
SELECT p.Category, SUM(s.Qty * s.UnitPrice) AS [Sales Amount]
FROM dbo.FactSales AS s
JOIN dbo.DimProduct AS p ON p.ProductKey = s.ProductKey
JOIN dbo.DimDate    AS d ON d.DateKey    = s.OrderDateKey
WHERE d.CalendarYear = @p0 AND p.Color = @p1
GROUP BY p.Category
ORDER BY [Sales Amount] DESC
OFFSET 0 ROWS FETCH NEXT 3500 ROWS ONLY;
```

- Executed with `SqlCommand` + `CommandTimeout` + `CancellationToken` (cancel when the user clicks another slicer before results arrive).
- Totals via `GROUPING SETS ((p.Category), ())` in the same query.
- LQL functions that cannot be translated cause a binder error for DirectQuery tables ("not supported in DirectQuery mode"), same as Power BI's DirectQuery restrictions.
- Recommend clustered columnstore indexes on fact tables.

---

## 13. Non-functional requirements

| Area | Requirement |
|---|---|
| Performance | See 1.1 and 8.4. Import 10M-row CSV in < 60 s. Page with 15 visuals first paint < 1.5 s |
| Capacity | One 64 GB / 8-core IIS server holds ~50 datasets of 10M rows each |
| Browsers | Edge, Chrome, Firefox current; **no IE11** (d3 v7) |
| Server OS | Windows Server 2016+ with IIS 10, .NET Framework 4.7 runtime installed |
| SQL Server | 2016+ (JSON functions); 2019+ recommended |
| Accessibility | WCAG 2.1 AA in viewer mode |
| Reliability | Snapshot reload on recycle; atomic dataset swap on refresh; Hangfire retries |
| Observability | Serilog to Seq/ELK; per-query timings; built-in Performance Analyzer panel (server time vs render time per visual) |
| Testing | NUnit/xUnit unit tests; golden tests comparing LQL to Power BI DAX results on Contoso/AdventureWorks; BenchmarkDotNet; Selenium E2E with screenshot diffs |
| Deployment | MSBuild/Web Deploy (MSDeploy) or Octopus; Windows Service via TopShelf installer |

---

## 14. Milestones

Assumes 2–3 senior C# engineers plus one front-end engineer.

### Phase 0: Foundations (2 weeks)
- Solution skeleton, net47 class libraries, MVC 5 + Web API 2 site, Autofac, Serilog, metadata DB with migrations (EF6 or DbUp), CI (Azure DevOps / TeamCity).
- TypeScript + webpack pipeline inside the web project; C#→TS contract generation.
- **Exit:** empty editor shell served by MVC; a command round-trips to the server and persists.

### Phase 1: Data in, one visual out (5 weeks)
1. CSV, Excel, SQL Server connectors streaming into column builders.
2. Transform pipeline with 6 core steps, preview endpoint, Applied Steps UI.
3. Columnar store, dictionary encoding, snapshots to SQL Server.
4. Model view: create relationships; build `fkToDimRow` indexes.
5. Engine: filter masks, propagation, group-by, implicit aggregations, batch endpoint.
6. Canvas (one page), Table, Card, Clustered Column in d3.
- **Exit:** import Sales + Product, relate them, show "Sum of Amount by Category" as column chart and table, sliced by a basic filter.

### Phase 2: MVP authoring (7 weeks)
1. LQL lexer/parser/binder/evaluator; `/measures/validate`; Monaco editor with IntelliSense fed by model metadata.
2. Remaining MVP visuals and slicers.
3. Cross-filter/highlight + Edit Interactions; Filters pane at three scopes; TopN.
4. Multi-page canvas tools, format pane from C# schemas, themes.
5. Date table + time intelligence; result cache; Performance Analyzer.
6. Golden test suite green against Power BI outputs.
- **Exit (MVP):** success criteria 1.1 met; 10 Power BI users complete the 15-minute task.

### Phase 3: Share and depth (6–8 weeks)
Sharing/ACL UI, viewer mode, Refresh Service with Hangfire schedules, SignalR notifications, hierarchies and drill, drill-through, bookmarks, sync slicers, more visuals, custom visual SDK, bi-directional relationships, `USERELATIONSHIP`, compiled expression trees for row context.

### Phase 4: Enterprise (8+ weeks)
DirectQuery to SQL Server with query folding of transform steps, RLS, Entra ID/ADFS, SSAS Tabular backend via ADOMD.NET, engine scale-out as Windows Service, embedding, optional WPF authoring client (Option B).

---

## 15. Risks and mitigations

| Risk | Impact | Mitigation |
|---|---|---|
| .NET Framework 4.7 end of support (Microsoft has announced it for 4.6.2, 4.7 and 4.7.1; I believe January 2027, confirm on the Microsoft lifecycle page) | No security patches after that date | Accepted risk. Keep `Lumen.Engine` and `Lumen.Model` free of `System.Web` dependencies so a later framework move, if ever needed, touches only the web host |
| Round-trip latency per interaction | Sluggish slicers | Batch per page, result cache, cancellation of stale requests, optimistic UI (fade visuals during load), keep engine in-process |
| GC pauses / LOH with big arrays | Latency spikes | 64-bit, `gcServer=true`, `gcConcurrent=true`, pooled buffers, `GCLatencyMode.SustainedLowLatency` during queries |
| App pool recycle drops datasets | Cold starts | AlwaysRunning, Application Initialization warm-up, snapshot reload |
| DAX semantic subtleties | Wrong numbers | Small exact LQL + golden tests vs Power BI; SSAS backend for full DAX |
| C# 7.3 language limits | More boilerplate | Accept; use abstract classes + visitor pattern instead of records/switch expressions |
| Two languages (C# + TypeScript) | Contract drift | Generate TS types from C# DTOs in the build |
| Scope creep | Never ships | Non-goals in 5.3 are binding |

---

## 16. Open questions

1. Intranet only (Windows Auth) or internet-facing (Entra ID)?
2. Web app (Option A) vs WPF desktop authoring (Option B): is a desktop authoring experience a hard requirement?
3. Is an SSAS Tabular license available? If yes, the SSAS backend could replace much of the custom engine and LQL work.
4. Expected data sizes per dataset? Above ~50M rows per fact table, plan for DirectQuery or SSAS from the start.

---

## Appendix A: v1 → v2 mapping

| Concern | v1 (JavaScript) | v2 (C# / .NET 4.7) |
|---|---|---|
| Engine | TypeScript in Web Worker | C# `Lumen.Engine` in IIS (or Windows Service) |
| Storage | Arrow typed arrays in browser | C# arrays, snapshots in SQL Server |
| Expression language | TS parser | C# lexer/Pratt parser/binder, closures → expression trees |
| App shell | React | Razor + TypeScript (thin) |
| Visuals | d3 | d3 (unchanged) |
| Persistence | IndexedDB | SQL Server metadata DB |
| Server | ASP.NET Core (Phase 3) | ASP.NET MVC 5 / Web API 2 / SignalR 2 from day one |
| Refresh | n/a in MVP | Windows Service + Hangfire |
| Max data | ~5–10M rows (browser memory) | ~10M+ per dataset in RAM, unlimited with DirectQuery/SSAS |

## Appendix B: web.config essentials

```xml
<configuration>
  <system.web>
    <compilation targetFramework="4.7" />
    <httpRuntime targetFramework="4.7" maxRequestLength="512000" executionTimeout="600" />
  </system.web>
  <runtime>
    <gcServer enabled="true" />
    <gcConcurrent enabled="true" />
    <gcAllowVeryLargeObjects enabled="true" />
  </runtime>
  <system.webServer>
    <urlCompression doDynamicCompression="true" />
    <applicationInitialization doAppInitAfterRestart="true">
      <add initializationPage="/warmup" />
    </applicationInitialization>
  </system.webServer>
</configuration>
```

(Note: `gcServer` in `web.config` is ignored under IIS, which uses the server GC automatically on multi-core machines; it is listed here for self-hosted/Windows Service hosts, where it goes in `App.config`.)
