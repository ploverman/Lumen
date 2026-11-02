# PRD: "Lumen", a Power BI-style dashboard builder in JavaScript (d3.js)

| | |
|---|---|
| Author | Claude, for Ploverman |
| Date | 2026-10-31 |
| Status | Draft v1 |
| Audience | Seasoned C# developer with Power BI and SQL Server expertise |
| Working name | **Lumen** (placeholder; rename freely) |

---

## 0. How to read this document

Every major concept is mapped to its Power BI equivalent and, where it helps, to a C#/.NET analogy. Sections 1 to 4 are the "what and why". Sections 5 to 10 are the "how" (architecture, data model, engine, rendering). Sections 11 to 14 are scope, milestones and risk. Code snippets are TypeScript, because a C# developer will feel at home with its type system and it is the recommended language for this build.

---

## 1. Problem and goal

Power BI Desktop is a Windows desktop app that combines four products in one shell:

1. **Power Query** (M engine): get and shape data.
2. **VertiPaq / Analysis Services Tabular**: columnar in-memory store, relationships, DAX measures.
3. **Report canvas**: drag fields onto visuals, lay them out on pages, cross-filter.
4. **Power BI Service**: publish, share, refresh, row-level security.

**Goal:** build a browser-based app that lets an analyst import data, model it, define measures, and author interactive multi-page reports with cross-filtering, then save and share them, with the authoring experience recognizably similar to Power BI Desktop.

**Non-goal:** binary or file compatibility with `.pbix`, full DAX, or full M. We replicate the *experience and mental model*, not the engines bit for bit.

### 1.1 Success criteria

- An analyst who knows Power BI can build a 3-page sales report from two CSVs (Sales fact + Product dimension) in under 15 minutes without docs.
- Slicer click to all visuals re-rendered: **< 100 ms p95** on 1M fact rows in the browser.
- A saved report reopens identically (layout, filters, theme) from a single JSON document.

---

## 2. Personas

| Persona | Power BI equivalent | Needs |
|---|---|---|
| **Author** | Report developer in PBI Desktop | Import, model, measures, layout, formatting |
| **Viewer** | Consumer in PBI Service / app | Open shared report, slice, drill, export |
| **Admin** (later) | Workspace admin / tenant admin | Manage users, data sources, permissions |

---

## 3. Why d3.js and not p5.js

**Recommendation: d3.js (v7) for visuals, inside a React (or Svelte) application shell.**

| Concern | d3.js | p5.js |
|---|---|---|
| Purpose | Data-driven document manipulation; scales, axes, shapes, layouts, transitions | Creative coding sketchbook (Processing port); immediate-mode drawing loop |
| Rendering model | Retained mode: SVG/DOM elements bound to data (`selection.data().join()`) | Immediate mode: redraw the whole canvas each frame in `draw()` |
| Hit testing / tooltips / selection | Free: each bar is a DOM node with events | Manual: you compute which pixel belongs to which datum |
| Accessibility (ARIA, keyboard, screen readers) | Possible because SVG is DOM | Very hard; a canvas is one opaque bitmap |
| Charting primitives | `d3-scale`, `d3-axis`, `d3-shape`, `d3-hierarchy` (treemap), `d3-geo` (maps), `d3-format`, `d3-time-format` | None; you would build axes, ticks, number formatting yourself |
| Data utilities | `d3-array` (`group`, `rollup`, `bin`, `extent`), `d3-dsv` (CSV parsing) | None |
| Many instances on a page | Natural: each visual is its own `<svg>` | Each sketch owns a canvas and a loop; 20 sketches = 20 render loops |
| Export to SVG/PDF/PNG | Easy (serialize SVG) | PNG only |
| Large point counts (100k+ scatter) | SVG slows past ~10k nodes; use d3 with `<canvas>` for those visuals | Good at raw pixel throughput |

**Where p5 would win:** animation-heavy, generative, or very high-point-count art pieces. A BI tool is the opposite: dozens of small, static-ish, interactive, accessible charts with axes and formatted numbers. That is exactly what d3 was designed for.

**C# analogy:** d3 is like WPF's data binding + `ItemsControl` with templates (retained visual tree bound to a collection). p5 is like overriding `OnPaint` in WinForms and drawing everything with GDI+ every frame.

**Hybrid rule:** d3 computes scales and layout in all cases. A visual renders to SVG by default and switches to `<canvas>` (still driven by d3 scales) when its mark count exceeds a threshold (default 5,000).

**Important division of labour:** do *not* let d3 own the whole app. d3 is great inside one visual; it is a poor application framework. Use React for the shell (panes, field list, drag-and-drop, property panels) and give each visual a `<div>` that d3 owns. This is the same split Power BI itself uses (its custom-visuals SDK hands a d3-friendly DOM element to each visual).

---

## 4. Concept map: Power BI to Lumen

| Power BI | Lumen component | Notes |
|---|---|---|
| Get Data / Power Query Editor | **Data Import + Transform Pipeline** | Ordered list of steps, like M "Applied Steps" |
| M query | `TransformStep[]` (JSON) | Declarative, replayable, like an M `let ... in` |
| VertiPaq | **Columnar Store** (typed arrays + dictionary encoding) | Runs in a Web Worker |
| Model view, relationships | **Model Designer** | 1:* single-direction filters for MVP |
| Calculated column | `CalculatedColumn` (row-context expression) | Evaluated at refresh |
| Measure (DAX) | `Measure` in **LQL** (Lumen Query Language, a DAX subset) | Evaluated per filter context |
| Filter context | `FilterContext` object | Explicit data structure, see section 8 |
| Report view / canvas | **Canvas Editor** | Pages, absolute-positioned visuals, snap grid |
| Fields pane / Visualizations pane | **Fields Pane / Visual Gallery / Wells** | Drag fields into wells (Axis, Values, Legend...) |
| Format pane | **Format Pane** driven by a JSON schema per visual | Like `capabilities.json` in PBI custom visuals |
| Slicer | Slicer visual | Writes to page/report filter state |
| Cross-filter / cross-highlight | **Interaction Bus** | Edit Interactions matrix per page |
| Filters pane (visual/page/report) | Filter scopes | Same three levels |
| Drill down / hierarchy | Hierarchies on dimension tables | Phase 2 |
| Bookmarks | Saved state snapshots | Phase 3 |
| Themes (JSON) | Theme JSON (compatible-ish with PBI theme schema) | Phase 2 |
| .pbix | `.lumen.json` report document + data snapshot (Parquet/Arrow) | |
| Publish to Service | **Publish** to Lumen server | Phase 3 |
| Row-level security | RLS roles evaluated server-side | Phase 4 |
| DirectQuery | Server-side SQL pushdown to SQL Server | Phase 4 |

---

## 5. Scope

### 5.1 MVP (Phases 1 and 2)

- Import CSV, Excel (`.xlsx`), JSON; paste table.
- Transform steps: rename, change type, remove columns, filter rows, replace values, split column, add custom column, promote headers, remove duplicates, unpivot.
- Data model: multiple tables, 1:* relationships, single-direction filtering, active/inactive relationships (USERELATIONSHIP is Phase 3).
- Measures with LQL: `SUM, AVERAGE, MIN, MAX, COUNT, COUNTROWS, DISTINCTCOUNT, DIVIDE, IF, SWITCH, CALCULATE` (with simple column filters), `ALL`, `FILTER` (simple), basic time intelligence (`TOTALYTD`, `SAMEPERIODLASTYEAR`) once a date table is marked.
- Canvas: multiple pages, drag, resize, snap-to-grid, z-order, align/distribute, copy/paste, undo/redo.
- Visuals (10): Card, Multi-row card, Table, Matrix (no subtotals collapse in MVP), Clustered bar/column, Stacked bar/column, Line, Area, Pie/Donut, Scatter. Plus Slicer (list, dropdown, between-range for numbers/dates).
- Cross-filtering between visuals, Edit Interactions (filter / highlight / none).
- Filters pane at visual, page and report scope.
- Tooltips, data labels, legends, axis formatting, number formatting.
- Theme: one built-in light, one dark, import theme JSON.
- Save/load report locally (IndexedDB) and export/import `.lumen.json`.

### 5.2 Later phases

- Phase 3: server, publish/share, refresh schedules, bookmarks, drill-down/drill-through, treemap, map (d3-geo), waterfall, KPI, gauge, custom visuals SDK.
- Phase 4: SQL Server connector with DirectQuery-style pushdown, RLS, multi-user workspaces, audit.

### 5.3 Non-goals (explicitly out)

- Full DAX parity (iterators over arbitrary tables, context transition edge cases, calculation groups).
- Full M language.
- Reading or writing `.pbix`.
- Many-to-many and bi-directional relationships in MVP.
- Mobile authoring (viewing on mobile is fine).
- Paginated (SSRS-style) reports.
- Natural-language Q&A, AI visuals.

---

## 6. Architecture

```
┌──────────────────────────── Browser ─────────────────────────────┐
│  UI thread (React + TypeScript)                                    │
│  ┌──────────┐ ┌───────────────┐ ┌──────────┐ ┌────────────────┐   │
│  │ Fields   │ │ Canvas Editor │ │ Format   │ │ Model Designer │   │
│  │ pane     │ │  (pages)      │ │ pane     │ │ (relationships)│   │
│  └────┬─────┘ └──────┬────────┘ └────┬─────┘ └───────┬────────┘   │
│       │      ┌───────▼────────┐      │               │            │
│       └─────►│  Report Store  │◄─────┴───────────────┘            │
│              │ (Zustand/Redux,│  commands + undo stack            │
│              │  immutable)    │                                   │
│              └───────┬────────┘                                   │
│        Interaction Bus│(selection, slicers → FilterContext)        │
│              ┌───────▼────────┐    ┌──────────────────────────┐   │
│              │ Visual Host    │───►│ d3 Visual plugins        │   │
│              │ (one per tile) │    │ bar, line, table, ...    │   │
│              └───────┬────────┘    └──────────────────────────┘   │
│                      │ QuerySpec (postMessage / Comlink)          │
│  ════════════════════╪═══════════════════════════════════════════ │
│  Web Worker          ▼                                            │
│  ┌──────────────────────────────────────────────────────────┐    │
│  │ Query Engine: filter propagation → group-by → measures    │    │
│  │ Columnar Store (Arrow / typed arrays, dictionary encoded) │    │
│  │ Transform Pipeline (import & refresh)                     │    │
│  │ Optional: DuckDB-WASM backend (see 6.2)                   │    │
│  └──────────────────────────────────────────────────────────┘    │
└───────────────────────────────────────────────────────────────────┘
            │ Phase 3+: REST/JSON
            ▼
┌──────────────── Server (ASP.NET Core, your home turf) ────────────┐
│ Report storage, auth (Entra ID / OIDC), sharing, refresh jobs,    │
│ SQL Server connector, RLS, DirectQuery-style SQL generation       │
└───────────────────────────────────────────────────────────────────┘
```

### 6.1 Technology choices

| Layer | Choice | Why (C# analogy) |
|---|---|---|
| Language | TypeScript (strict) | Closest to C#: interfaces, generics, discriminated unions |
| UI framework | React 18 + Vite | Component model, ecosystem for drag/drop and panels |
| State | Zustand or Redux Toolkit with Immer | Like an immutable view-model + command pattern |
| Drag/drop & resize | `dnd-kit` (field wells), `react-grid-layout` or custom (canvas) | |
| Visuals | d3 v7 modules | See section 3 |
| Data format | Apache Arrow JS | Columnar, zero-copy transfer to worker, like `Span<T>` over typed buffers |
| Compute | Custom engine in a Web Worker, behind an interface; DuckDB-WASM as optional backend | Keeps UI thread at 60 fps, like moving work off the WPF dispatcher |
| Worker RPC | Comlink | Feels like calling an async interface across AppDomains |
| Expression parser | Hand-written Pratt parser or Chevrotain | For LQL |
| Excel import | SheetJS (xlsx) | |
| CSV import | `d3-dsv` or PapaParse (streaming) | |
| Persistence (local) | IndexedDB via `idb` | |
| Server (Phase 3) | ASP.NET Core minimal APIs, EF Core, SQL Server | Your strongest stack |
| Testing | Vitest (unit), Playwright (E2E, visual snapshots) | xUnit + Selenium equivalents |

### 6.2 Compute engine decision: custom vs DuckDB-WASM

Two viable options; the PRD requires an `IQueryEngine` abstraction so we can swap.

- **Custom columnar engine** (default for MVP): small bundle, full control of filter-context semantics, which is the heart of DAX-like behavior. Easy to make "relationship-aware" the way VertiPaq is.
- **DuckDB-WASM**: SQL engine in the browser, very fast on big data, but ~6-10 MB of WASM, and you must translate filter contexts to SQL (which you will need anyway for SQL Server pushdown in Phase 4).

Recommendation: build the custom engine for MVP up to ~5M rows; prototype a DuckDB backend in Phase 3 sharing the same SQL generator you will use for SQL Server.

```ts
interface IQueryEngine {
  loadTable(name: string, data: ArrowTable): Promise<void>;
  setModel(model: SemanticModel): Promise<void>;
  execute(q: QuerySpec): Promise<QueryResult>;
}
```

---

## 7. Data and state model

Everything the author creates is one serializable **ReportDocument**. Think of it as the `.pbix` minus the data, or as the combination of PBIP's `model.bim` (TMSL) and `report.json`.

```ts
// ----- Semantic model (≈ Tabular model / TMSL) -----
interface SemanticModel {
  tables: TableDef[];
  relationships: RelationshipDef[];
  measures: MeasureDef[];          // home table referenced by id
}

interface TableDef {
  id: string; name: string;
  source: DataSourceRef;            // csv file, xlsx sheet, sql query...
  steps: TransformStep[];           // ≈ Power Query applied steps
  columns: ColumnDef[];
  isDateTable?: { dateColumnId: string };   // "Mark as date table"
  hierarchies?: HierarchyDef[];
}

type DataType = 'string' | 'int64' | 'decimal' | 'double' | 'bool' | 'date' | 'datetime';

interface ColumnDef {
  id: string; name: string; dataType: DataType;
  formatString?: string;            // "#,0.00", "0.0%", "yyyy-MM-dd" (.NET-like)
  summarizeBy: 'sum' | 'avg' | 'min' | 'max' | 'count' | 'distinctCount' | 'none';
  sortByColumnId?: string;          // e.g. MonthName sorted by MonthNumber
  isHidden?: boolean;
  expression?: string;              // present => calculated column (row context)
  dataCategory?: 'city' | 'country' | 'latitude' | 'longitude' | 'url' | 'image';
}

interface RelationshipDef {
  id: string;
  fromTableId: string; fromColumnId: string;  // many side (fact)
  toTableId: string;   toColumnId: string;    // one side (dimension)
  cardinality: 'manyToOne' | 'oneToOne';      // M:N is a non-goal for MVP
  crossFilter: 'single' | 'both';             // 'both' Phase 3
  isActive: boolean;
}

interface MeasureDef {
  id: string; name: string; homeTableId: string;
  expression: string;               // LQL source text
  formatString?: string;
  displayFolder?: string;
}

// ----- Transform pipeline (≈ M applied steps) -----
type TransformStep =
  | { kind: 'promoteHeaders' }
  | { kind: 'changeType'; columns: Record<string, DataType> }
  | { kind: 'renameColumns'; map: Record<string, string> }
  | { kind: 'removeColumns'; columns: string[] }
  | { kind: 'filterRows'; predicate: string }     // LQL row expression
  | { kind: 'replaceValues'; column: string; find: unknown; replace: unknown }
  | { kind: 'splitColumn'; column: string; delimiter: string; into: string[] }
  | { kind: 'addColumn'; name: string; expression: string; dataType: DataType }
  | { kind: 'removeDuplicates'; columns?: string[] }
  | { kind: 'unpivot'; keep: string[]; attributeName: string; valueName: string };

// ----- Report (≈ report.json / PBIR) -----
interface ReportDocument {
  schemaVersion: 1;
  id: string; name: string;
  model: SemanticModel;
  theme: ThemeDef;
  reportFilters: FilterDef[];
  pages: PageDef[];
  bookmarks?: BookmarkDef[];        // Phase 3
}

interface PageDef {
  id: string; name: string;
  size: { width: number; height: number };   // default 1280x720 (16:9)
  background?: FillDef;
  pageFilters: FilterDef[];
  visuals: VisualDef[];
  interactions: InteractionOverride[];        // Edit Interactions matrix
}

interface VisualDef {
  id: string;
  type: string;                     // 'clusteredBar', 'line', 'slicer', ...
  layout: { x: number; y: number; w: number; h: number; z: number };
  roles: Record<string, FieldRef[]>; // wells: { category: [...], values: [...], legend: [...] }
  visualFilters: FilterDef[];
  format: Record<string, unknown>;  // validated by the visual's format schema
  sort?: { fieldRef: FieldRef; direction: 'asc' | 'desc' };
  topN?: { n: number; byMeasureId: string };
}

type FieldRef =
  | { kind: 'column'; tableId: string; columnId: string; aggregation?: ColumnDef['summarizeBy'] }
  | { kind: 'measure'; measureId: string }
  | { kind: 'hierarchyLevel'; tableId: string; hierarchyId: string; level: number };

interface FilterDef {
  id: string; target: FieldRef;
  type: 'basic' | 'advanced' | 'topN' | 'relativeDate';
  // basic: in-list; advanced: up to 2 conditions joined by and/or
  values?: unknown[]; conditions?: Condition[]; logic?: 'and' | 'or';
  isLocked?: boolean; isHidden?: boolean;
}

interface InteractionOverride {
  sourceVisualId: string; targetVisualId: string;
  mode: 'filter' | 'highlight' | 'none';
}
```

### 7.1 Runtime (non-persisted) state

```ts
interface RuntimeState {
  activePageId: string;
  selectedVisualIds: string[];                 // authoring selection
  slicerState: Record<string /*visualId*/, FilterDef>;
  dataSelections: Record<string /*visualId*/, DataPointSelection[]>; // clicked bars
  undo: Command[]; redo: Command[];
  mode: 'edit' | 'view';
}
```

**Command pattern:** every authoring change is a `Command { do(doc): doc; undo(doc): doc; label }`, exactly the `ICommand` + undo-stack pattern you know from WPF editors. Immer makes the `doc` updates immutable and cheap.

---

## 8. Query engine and filter context (the heart of the product)

This is what makes it "Power BI" rather than "a bunch of d3 charts". Step by step:

### 8.1 Storage

1. Each table is stored column-wise. Numbers in `Float64Array` / `Int32Array` / `BigInt64Array`; strings dictionary-encoded (`Uint32Array` of codes + `string[]` dictionary), just like VertiPaq's dictionary encoding.
2. For each relationship, precompute at load time a **foreign-key index**: `fkToDimRow: Int32Array` mapping each fact row to its dimension row number (-1 if no match, i.e. the "blank" member). This is the in-memory equivalent of a SQL Server nested-loop join made O(1).

### 8.2 The QuerySpec

When a visual needs data, the Visual Host builds a `QuerySpec` from its wells plus all applicable filters. This is analogous to the DAX query Power BI generates (`SUMMARIZECOLUMNS(...)`), which you can see in Performance Analyzer.

```ts
interface QuerySpec {
  groupBy: FieldRef[];            // Axis + Legend wells (columns)
  measures: FieldRef[];           // Values well (measures or implicit aggregations)
  filters: FilterDef[];           // report ∪ page ∪ visual ∪ slicers ∪ cross-filters
  highlight?: FilterDef[];        // cross-highlight: compute both full and highlighted values
  orderBy?: { field: FieldRef; dir: 'asc'|'desc' }[];
  topN?: number;
  limit: number;                  // data reduction, default 3,500 groups like PBI
}
```

### 8.3 Evaluation algorithm

For a single query:

1. **Collect filters** in scope order: report filters, page filters, slicers on the page (respecting Edit Interactions), cross-filter selections from other visuals (respecting Edit Interactions), visual filters.
2. **Resolve filters to row bitmaps per table.** For each filter on table T, scan T's column and produce a `Uint8Array` (or a Roaring bitmap later) of qualifying rows. AND together all bitmaps for the same table.
3. **Propagate along relationships, one side to many side** (single-direction, like Power BI's default). For each fact table, a fact row survives if, for every related dimension with a filter, `dimMask[fkToDimRow[r]] === 1`. This produces the fact table's effective row mask. This is precisely the "filter flows from the one side to the many side" rule from the Model view.
4. **Group.** Iterate surviving fact rows, compute a composite key from the group-by columns (which may live in dimension tables, reached via `fkToDimRow`), and bucket row indices. Use dictionary codes for keys so grouping is integer math.
5. **Evaluate measures per group.** Each group defines a *filter context* = base filters + "group-by column = this group's value". A measure is evaluated against that context. For simple aggregations, a single pass accumulates `sum/count/min/max` per bucket. For `CALCULATE`, the engine modifies the context (adds/replaces/removes filters) and re-evaluates.
6. **Totals.** Evaluate measures once more with no group-by filter (the "Total" row), not by summing the group values. This preserves DAX semantics for non-additive measures like `DISTINCTCOUNT` or ratios.
7. **Sort, apply TopN, apply data-reduction limit**, return an Arrow table.
8. **Cache** results keyed by a stable hash of the QuerySpec (like PBI's visual query cache). Invalidate on model change or data refresh.

### 8.4 LQL: Lumen Query Language (DAX subset)

- **Grammar:** expressions, function calls, `'Table'[Column]` and `[Measure]` references, operators `+ - * / & = <> < <= > >= && || IN`, `VAR ... RETURN`.
- **Pipeline:** text, tokenizer, Pratt parser, AST, binder (resolves names against the model, reports errors with line/column like Roslyn diagnostics), compiler to a closure tree `(ctx: FilterContext) => Value`.
- **Context semantics:**
  - *Measures* evaluate in **filter context**.
  - *Calculated columns* evaluate in **row context** at refresh time.
  - `CALCULATE(expr, filter1, ...)` creates a new filter context (context transition for iterators is Phase 3).
  - `ALL(Table|Column)` removes filters; `FILTER(Table, predicate)` (simple) returns a row mask.
  - `BLANK()` is a first-class value with DAX's propagation rules (`BLANK + 1 = 1`, `DIVIDE(x, 0) = BLANK`).
- **Time intelligence** requires a marked date table and a relationship to it, same as Power BI.
- **Editor:** Monaco editor with LQL syntax highlighting, IntelliSense (tables, columns, measures, functions), and inline errors.

Example the MVP must evaluate correctly:

```
Sales Amount = SUMX(Sales, Sales[Qty] * Sales[UnitPrice])   // SUMX is MVP (simple iterator, no context transition)
Margin %     = DIVIDE([Sales Amount] - [Total Cost], [Sales Amount])
Sales YTD    = TOTALYTD([Sales Amount], 'Date'[Date])
Red Sales    = CALCULATE([Sales Amount], Product[Color] = "Red")
All-Product Share = DIVIDE([Sales Amount], CALCULATE([Sales Amount], ALL(Product)))
```

---

## 9. Report canvas and authoring UX

Step-by-step flows the product must support.

### 9.1 Layout of the editor (mirrors PBI Desktop)

- Left rail: **Report**, **Data** (table grid view), **Model** (relationship diagram).
- Center: page canvas with page tabs at the bottom.
- Right: **Filters**, **Visualizations** (gallery + wells + format tabs), **Data/Fields**.
- Top ribbon: Get Data, Transform, New Measure, New Page, Theme, Publish, Undo/Redo.

### 9.2 Flow: create a visual

1. Author clicks a visual type in the gallery (or drags a field onto an empty canvas area, which auto-creates a default visual: column field gives a table, measure gives a card, date + measure gives a line).
2. A `VisualDef` with default size (e.g. 400x300) is inserted at the next free position. Command pushed to undo stack.
3. Author drags fields from the Fields pane onto wells. Each visual type declares its roles:

```ts
const clusteredBarCapabilities: VisualCapabilities = {
  roles: [
    { name: 'category', displayName: 'Y-axis',  kind: 'grouping', max: 3 /*hierarchy levels*/ },
    { name: 'legend',   displayName: 'Legend',  kind: 'grouping', max: 1 },
    { name: 'values',   displayName: 'X-axis',  kind: 'measure',  min: 1 },
    { name: 'tooltips', displayName: 'Tooltips',kind: 'measure' },
  ],
  formatSchema: barFormatSchema,   // JSON schema → auto-generated Format pane
  supportsHighlight: true,
};
```

4. The Visual Host computes a `QuerySpec`, debounces 50 ms, sends to the worker, and passes the result to the visual's `update()`.
5. Dropping a numeric column into a measure role creates an **implicit measure** using the column's `summarizeBy` (exactly like Power BI's "Sum of Qty"), switchable via a well dropdown.

### 9.3 Flow: canvas manipulation

- Drag to move, handles to resize, Shift to constrain, Alt to bypass snapping.
- Snap grid (8 px default) and smart guides to other visuals' edges and centers.
- Multi-select (Ctrl+click, marquee), group, align (left/center/right/top/middle/bottom), distribute.
- Z-order (bring forward/back), lock, hide, Selection pane listing all visuals.
- Keyboard: arrows nudge 1 px (Shift: 10 px), Ctrl+C/V/D, Delete, Ctrl+Z/Y.
- Page view modes: fit to page, fit to width, actual size.

### 9.4 Flow: cross-filtering

1. Viewer clicks a bar "Bikes" in visual A.
2. Visual A emits `select({ visualId: A, dataPoints: [{ Product.Category: 'Bikes' }], multi: ctrlKey })` on the Interaction Bus.
3. The bus looks up each other visual B on the page, reads the interaction mode for A to B (default: highlight for bar/column/pie targets, filter for others, same defaults as Power BI).
4. For **filter**, the selection becomes a filter in B's QuerySpec. For **highlight**, B queries both unfiltered and filtered values and renders the filtered part solid over a faded full bar.
5. Clicking empty space in A clears its selection; Esc clears all.

### 9.5 Flow: slicers

- Slicer is a visual whose output is a `FilterDef` stored in `RuntimeState.slicerState` (and persisted as the saved default when the author saves).
- Variants: vertical list (checkboxes, single-select toggle, "Select all"), dropdown, tile buttons, numeric/date range ("Between") with a d3-brush, relative date ("Last 3 months").
- Slicer values are themselves filtered by other slicers (cascading), via the same engine.
- Sync slicers across pages: Phase 3.

### 9.6 Flow: format pane

- Each visual ships a JSON schema of format cards (General, Title, X axis, Y axis, Legend, Data labels, Colors, Background, Border, Shadow, Tooltips).
- The Format pane is generated from the schema (like a `PropertyGrid` driven by attributes in WinForms).
- Values fall back: visual setting, then theme `visualStyles`, then built-in default.

---

## 10. Visual plugin contract (d3)

Each visual is a plugin implementing one interface. This is deliberately close to Power BI's `IVisual` from the custom visuals SDK, so the concepts transfer.

```ts
interface IVisual {
  /** Called once. `element` is a div owned exclusively by the visual. */
  init(element: HTMLElement, host: IVisualHost): void;
  /** Called on data, size, format, theme or highlight change. Must be idempotent. */
  update(opts: VisualUpdateOptions): void;
  destroy(): void;
}

interface VisualUpdateOptions {
  data: QueryResult;                     // Arrow table + column metadata
  highlight?: QueryResult;
  size: { width: number; height: number };
  format: Record<string, unknown>;       // resolved with theme fallbacks
  theme: ResolvedTheme;
  selection: DataPointSelection[];
  reason: Set<'data' | 'resize' | 'format' | 'theme' | 'selection'>;
}

interface IVisualHost {
  select(points: DataPointSelection[], multi: boolean): void;
  showTooltip(items: TooltipItem[], x: number, y: number): void;
  hideTooltip(): void;
  formatValue(v: unknown, formatString?: string): string;
  colorFor(seriesKey: string): string;   // stable colors from theme dataColors
  openContextMenu(point: DataPointSelection, x: number, y: number): void;
}
```

### 10.1 Example: clustered bar with d3 (abridged)

```ts
export class ClusteredBar implements IVisual {
  private svg!: d3.Selection<SVGSVGElement, unknown, null, undefined>;
  private host!: IVisualHost;

  init(el: HTMLElement, host: IVisualHost) {
    this.host = host;
    this.svg = d3.select(el).append('svg').attr('role', 'img');
    this.svg.append('g').attr('class', 'bars');
    this.svg.append('g').attr('class', 'x-axis');
    this.svg.append('g').attr('class', 'y-axis');
  }

  update({ data, size, format, selection }: VisualUpdateOptions) {
    const rows = toRows(data);                         // [{ category, value, key }]
    const m = { top: 8, right: 16, bottom: 24, left: 100 };
    const w = size.width - m.left - m.right, h = size.height - m.top - m.bottom;
    this.svg.attr('width', size.width).attr('height', size.height);

    const y = d3.scaleBand().domain(rows.map(r => r.category)).range([0, h]).padding(0.2);
    const x = d3.scaleLinear().domain([0, d3.max(rows, r => r.value) ?? 0]).nice().range([0, w]);

    const selected = new Set(selection.map(s => s.key));
    this.svg.select<SVGGElement>('.bars')
      .attr('transform', `translate(${m.left},${m.top})`)
      .selectAll<SVGRectElement, typeof rows[number]>('rect')
      .data(rows, r => r.key)                          // keyed join = stable transitions
      .join('rect')
        .attr('y', r => y(r.category)!).attr('height', y.bandwidth())
        .attr('fill', r => this.host.colorFor('values'))
        .attr('opacity', r => selected.size && !selected.has(r.key) ? 0.4 : 1)
        .on('click', (ev, r) => this.host.select([{ key: r.key, filter: r.filter }], ev.ctrlKey))
        .on('mousemove', (ev, r) => this.host.showTooltip(
            [{ label: r.category, value: this.host.formatValue(r.value, format.valuesFormat as string) }],
            ev.clientX, ev.clientY))
        .on('mouseleave', () => this.host.hideTooltip())
      .transition().duration(250)
        .attr('x', 0).attr('width', r => x(r.value));

    this.svg.select<SVGGElement>('.x-axis')
      .attr('transform', `translate(${m.left},${m.top + h})`)
      .call(d3.axisBottom(x).ticks(5, '~s'));
    this.svg.select<SVGGElement>('.y-axis')
      .attr('transform', `translate(${m.left},${m.top})`)
      .call(d3.axisLeft(y));
  }

  destroy() { this.svg.remove(); }
}
```

Key d3 ideas for a C# developer:

- `selection.data(array, keyFn).join(...)` is a **diff of a collection against the visual tree**, like `ObservableCollection` change notifications driving an `ItemsControl`, keyed by identity.
- **Scales** are pure functions from data domain to pixel range (`Func<double,double>` with an `Invert`).
- **Axes** are functions you `.call()` on a `<g>` to render ticks.
- Keep d3 inside `update()`; never let React re-render the inside of the visual's div.

### 10.2 Visual-level requirements (all visuals)

- Responsive to container size (ResizeObserver).
- Title, subtitle, background, border, padding from format pane.
- Empty-state message when required wells are empty.
- "Too many values" warning when the 3,500-group limit is hit.
- Keyboard navigation through data points and ARIA labels (`aria-label="Bikes, Sales 1.2M"`).
- Context menu: Show as table, Sort by, Export data (CSV), Remove.
- Focus mode (expand visual to full canvas).

---

## 11. Themes

- Theme JSON compatible with the main keys of the Power BI theme format: `name`, `dataColors`, `background`, `foreground`, `tableAccent`, `textClasses`, `visualStyles`. Importing a typical Power BI theme file should mostly work.
- Theme values are exposed as CSS custom properties on the canvas root, and as a `ResolvedTheme` object to d3 visuals.
- Built-ins: Default Light, Default Dark, High Contrast.
- Colors per series are assigned deterministically (series key hash → index into `dataColors`), overridable per data point in the format pane.

---

## 12. Persistence, publish and share

### 12.1 MVP (local only)

- Auto-save `ReportDocument` to IndexedDB every 10 s and on blur.
- Data snapshot stored as Arrow IPC blobs per table in IndexedDB.
- Export/import `name.lumen.json` (+ optional `name.lumen.zip` with Parquet data).

### 12.2 Phase 3 (server, ASP.NET Core)

| Endpoint | Purpose | PBI analogy |
|---|---|---|
| `POST /api/workspaces/{id}/reports` | Publish a report document + data | Publish to workspace |
| `GET /api/reports/{id}` | Fetch report for viewer | Open report in Service |
| `POST /api/reports/{id}/share` | Grant user/group view or edit | Share / Manage access |
| `POST /api/datasets/{id}/refresh` | Re-run transform pipeline on server | Dataset refresh |
| `PUT /api/datasets/{id}/schedule` | Cron-style refresh | Scheduled refresh |
| `POST /api/datasets/{id}/query` | Run QuerySpec server-side | Live connection |

- Auth via OIDC (Microsoft Entra ID), JWT bearer.
- Storage: SQL Server for metadata (reports, workspaces, ACLs), blob storage for data snapshots.
- Server-side engine: either port the TS engine to Node, or (recommended for Phase 4) generate SQL from QuerySpec and run against SQL Server.

### 12.3 Phase 4: SQL Server "DirectQuery"

Translate a `QuerySpec` to T-SQL. For the example "Sales Amount by Category, filtered to 2025, Color = Red":

```sql
SELECT p.Category,
       SUM(s.Qty * s.UnitPrice) AS [Sales Amount]
FROM dbo.FactSales AS s
JOIN dbo.DimProduct AS p ON p.ProductKey = s.ProductKey
JOIN dbo.DimDate    AS d ON d.DateKey    = s.OrderDateKey
WHERE d.CalendarYear = @p0
  AND p.Color = @p1
GROUP BY p.Category
ORDER BY [Sales Amount] DESC
OFFSET 0 ROWS FETCH NEXT 3500 ROWS ONLY;
```

Requirements: parameterized queries only (no string concatenation), columnstore-friendly shapes, query timeout and cancellation, and a measure-to-SQL translator that rejects LQL functions it cannot push down (fallback: import mode).

### 12.4 Row-level security (Phase 4)

- Roles defined as LQL boolean filters per table (`Region[Manager] = USERPRINCIPALNAME()`), stored in the model, evaluated server-side only, injected as an always-on filter into every QuerySpec. Never trust the client for RLS.

---

## 13. Non-functional requirements

| Area | Requirement |
|---|---|
| Performance | 1M fact rows x 3 dims: slicer interaction to all visuals updated < 100 ms p95 (desktop, Chrome). Import 1M-row CSV < 5 s. Page with 15 visuals initial render < 1.5 s |
| Memory | < 500 MB tab memory for 5M rows x 10 columns |
| Scale ceiling (client) | Soft limit 10M rows; beyond that recommend server/DirectQuery mode |
| Browser support | Latest 2 versions of Chrome, Edge, Firefox, Safari |
| Accessibility | WCAG 2.1 AA for viewer mode: keyboard navigation, focus outlines, ARIA, high-contrast theme |
| Security | No `eval` of user expressions (LQL is parsed, never `new Function`); CSP; sanitize text in tooltips/titles; server-side authz on every endpoint |
| Reliability | Autosave; crash recovery reopens last state; schema version + migrations for ReportDocument |
| Internationalization | Locale-aware number/date formatting via `Intl` and d3-format locale |
| Observability | Built-in "Performance Analyzer" panel: per visual, query time and render time (mirrors PBI's) |
| Testing | Engine: golden tests comparing LQL results to known DAX results on the AdventureWorks/Contoso sample model; visuals: Playwright screenshot diffs |

---

## 14. Milestones (phased plan)

Estimates assume 2 to 3 experienced engineers. Each phase ends with a demoable build.

### Phase 0: Foundations (2 weeks)
- Repo, Vite + React + TS strict, lint, Vitest, Playwright, CI.
- `ReportDocument` types, command/undo infrastructure, worker + Comlink plumbing.
- **Exit:** empty editor shell with panes, undo/redo working on a dummy document.

### Phase 1: Data in, single visual out (4 weeks)
1. CSV/XLSX import with type inference and preview grid.
2. Transform pipeline with 6 core steps and an Applied Steps list (reorder, delete, edit).
3. Columnar store with dictionary encoding, Data view grid (virtualized).
4. Model view: draw relationships by dragging column to column; auto-detect on matching names.
5. Engine: filter bitmaps, relationship propagation, group-by, implicit aggregations.
6. Canvas with one page, Table, Card, Clustered Column visuals; field wells.
- **Exit:** import Sales + Product, relate them, show "Sum of Amount by Category" as a column chart and a table.

### Phase 2: MVP authoring (6 weeks)
1. LQL parser, binder, evaluator; Monaco editor with IntelliSense; measures in Fields pane.
2. Remaining MVP visuals (bar/stacked/line/area/pie/donut/scatter/matrix/multi-row card).
3. Slicers (list, dropdown, range, relative date).
4. Cross-filter and cross-highlight; Edit Interactions mode.
5. Filters pane (visual/page/report), TopN filter.
6. Multi-page, canvas tools (snap, align, z-order, selection pane), format pane from schemas.
7. Themes (built-in + import JSON), date table + time intelligence.
8. Local save/load, export `.lumen.json`, Performance Analyzer panel.
- **Exit (MVP):** success criteria in section 1.1 met; 10 Power BI users complete the 15-minute task test.

### Phase 3: Share and depth (6 to 8 weeks)
- ASP.NET Core server, Entra ID auth, workspaces, publish/share, viewer mode, scheduled refresh.
- Hierarchies, drill down/up, drill-through pages, bookmarks, sync slicers, tooltips pages.
- More visuals (treemap, map, waterfall, KPI, gauge); custom visual SDK (load third-party `IVisual` in a sandboxed iframe).
- Bi-directional relationships, USERELATIONSHIP, iterators with context transition.

### Phase 4: Enterprise (8+ weeks)
- SQL Server connector, QuerySpec-to-T-SQL generation, DirectQuery mode, query folding of transform steps.
- RLS, audit log, usage metrics, embedding API (`<lumen-report>` web component + embed tokens).

---

## 15. Risks and mitigations

| Risk | Impact | Mitigation |
|---|---|---|
| DAX-like semantics are subtle (BLANK, totals, context) | Wrong numbers destroy trust | Golden test suite against real Power BI outputs from the same sample model; keep LQL small and exact rather than large and approximate |
| Browser memory limits on big data | Tab crashes | Dictionary encoding, Arrow, row-count warnings, server mode in Phase 4 |
| SVG performance with many marks | Janky visuals | Canvas fallback above 5k marks; data-reduction limit; virtualized table/matrix |
| Scope creep toward "full Power BI" | Never ships | Non-goals list in section 5.3 is binding; changes go through PRD revision |
| Drag/drop + resizing complexity | Long UX tail | Use proven libs (`dnd-kit`, `react-moveable`); build canvas interactions first in Phase 1 |
| Custom visuals = arbitrary JS | XSS / data exfiltration | Sandboxed iframes with postMessage host API, CSP, no network by default |

---

## 16. Open questions

1. Is the target purely internal (your team) or a product for external customers? This changes Phase 3/4 priority and licensing choices.
2. Is SQL Server the primary production source? If yes, consider pulling Phase 4's SQL generator forward and using it for DuckDB-WASM too.
3. React or Svelte for the shell? (React recommended for ecosystem; Svelte is lighter.)
4. Should theme JSON aim for strict Power BI compatibility so existing corporate themes can be reused?
5. Do you want a C#/Blazor shell instead of React? Possible (d3 via JS interop), but drag/drop ecosystems and d3 integration are much smoother in a JS framework; recommend keeping C# on the server.

---

## Appendix A: Glossary for the C# developer

| Term | Meaning |
|---|---|
| Selection (d3) | A wrapped set of DOM nodes, like a LINQ query over the visual tree that you can mutate |
| Data join | `selection.data(arr, key).join(enter, update, exit)`: diff collection vs. DOM |
| Scale | Function domain → range (`scaleLinear`, `scaleBand`, `scaleTime`, `scaleOrdinal`) |
| Web Worker | Background thread with its own heap; communicate by message passing (like an actor) |
| Arrow | Columnar in-memory format with zero-copy transfer between threads |
| Typed array | `Float64Array` etc., contiguous memory like `double[]` (not `List<object>`) |
| Immer | Write mutating code, get immutable result, similar to C# `record with { ... }` but nested |
| Filter context | The set of column filters active when a measure is evaluated (same as DAX) |

## Appendix B: Suggested repo layout

```
lumen/
  apps/editor/            React shell (panes, canvas, ribbon)
  packages/model/         ReportDocument types, commands, migrations
  packages/engine/        columnar store, filter propagation, LQL compiler (runs in worker)
  packages/visuals/       d3 visual plugins + capabilities + format schemas
  packages/themes/        theme schema, built-in themes, resolver
  server/                 ASP.NET Core (Phase 3+)
  tests/golden/           sample model + expected results exported from Power BI
```
