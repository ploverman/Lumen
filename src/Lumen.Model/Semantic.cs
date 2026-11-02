using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;

namespace Lumen.Model
{
    /// <summary>Column types. Decimal is stored as double in the engine, so it keeps the name, not the precision.</summary>
    public enum LumenDataType { String, Int64, Decimal, Double, Boolean, Date, DateTime }
    /// <summary>Default aggregation of a column dropped in a value well, also the aggregation stored on an implicit measure.</summary>
    public enum SummarizeBy { None, Sum, Average, Min, Max, Count, DistinctCount }
    /// <summary>Relationship cardinality. Recorded and shown; the engine treats both values alike and always uses the first match on the one side.</summary>
    public enum Cardinality { ManyToOne, OneToOne }
    /// <summary>Relationship filter direction. Only <see cref="Single"/> (one side to many side) is implemented.</summary>
    public enum CrossFilter { Single, Both }
    /// <summary>What a <see cref="FieldRef"/> points at. HierarchyLevel is reserved; nothing creates it yet.</summary>
    public enum FieldKind { Column, Measure, HierarchyLevel }
    /// <summary>The four filter cards of the Filters pane.</summary>
    public enum FilterType { Basic, Advanced, TopN, RelativeDate }
    /// <summary>How the conditions of an Advanced filter combine.</summary>
    public enum LogicalOp { And, Or }
    /// <summary>Operators of an Advanced filter condition, and of the Filter rows transform.</summary>
    public enum ConditionOp { Equals, NotEquals, LessThan, LessOrEqual, GreaterThan, GreaterOrEqual, Contains, NotContains, StartsWith, IsBlank, IsNotBlank }
    /// <summary>What a selection in one visual does to another: filter it, highlight within it, or nothing.</summary>
    public enum InteractionMode { Filter, Highlight, None }
    /// <summary>Unit of a relative date filter.</summary>
    public enum RelativeUnit { Days, Weeks, Months, Years }

    /// <summary>Type predicates for <see cref="LumenDataType"/>.</summary>
    public static class LumenTypes
    {
        /// <summary>True for Int64, Decimal and Double.</summary>
        public static bool IsNumeric(this LumenDataType t) =>
            t == LumenDataType.Int64 || t == LumenDataType.Decimal || t == LumenDataType.Double;
        /// <summary>True for Date and DateTime.</summary>
        public static bool IsTemporal(this LumenDataType t) =>
            t == LumenDataType.Date || t == LumenDataType.DateTime;
    }

    /// <summary>The authored model: tables, relationships and measures. Plain data, saved as JSON.
    /// Anything that needs row data lives in Lumen.Engine.Dataset, which is built from this.</summary>
    public sealed class SemanticModel
    {
        /// <summary>All tables, in the order they show in the Fields pane.</summary>
        public List<TableDef> Tables { get; set; } = new List<TableDef>();
        /// <summary>All relationships, active or not.</summary>
        public List<RelationshipDef> Relationships { get; set; } = new List<RelationshipDef>();
        /// <summary>All measures. A measure is global; its home table only decides where the Fields pane lists it.</summary>
        public List<MeasureDef> Measures { get; set; } = new List<MeasureDef>();

        /// <summary>Finds a table by stable id, or null.</summary>
        public TableDef Table(string id) => Tables.FirstOrDefault(t => t.Id == id);
        /// <summary>Finds a table by name, ignoring case, as LQL does.</summary>
        public TableDef TableByName(string name) =>
            Tables.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
        /// <summary>Finds a measure by stable id, or null.</summary>
        public MeasureDef Measure(string id) => Measures.FirstOrDefault(m => m.Id == id);
        /// <summary>Finds a measure by name, ignoring case, as LQL does.</summary>
        public MeasureDef MeasureByName(string name) =>
            Measures.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
        /// <summary>Active relationships whose many side is this table. Walking these goes from many side to one side.</summary>
        public IEnumerable<RelationshipDef> ActiveRelationshipsFrom(string tableId) =>
            Relationships.Where(r => r.IsActive && r.FromTableId == tableId);

        /// <summary>The label for a field: its name, or "Sum of Qty" for an implicit measure. Never throws on a dangling reference.</summary>
        public string DisplayName(FieldRef f)
        {
            if (f == null) return "";
            if (f.Kind == FieldKind.Measure) return Measure(f.MeasureId)?.Name ?? "(missing)";
            var col = Table(f.TableId)?.Column(f.ColumnId);
            if (col == null) return "(missing)";
            if (f.Aggregation.HasValue && f.Aggregation.Value != SummarizeBy.None)
                return AggregationLabel(f.Aggregation.Value) + " of " + col.Name;
            return col.Name;
        }

        /// <summary>The word used for an aggregation in labels.</summary>
        public static string AggregationLabel(SummarizeBy s)
        {
            switch (s)
            {
                case SummarizeBy.DistinctCount: return "Count (Distinct)";
                case SummarizeBy.Average: return "Average";
                case SummarizeBy.Min: return "Min";
                case SummarizeBy.Max: return "Max";
                case SummarizeBy.Count: return "Count";
                default: return "Sum";
            }
        }
    }

    /// <summary>A table's definition: where it comes from, the steps applied to it and its columns. The rows are in Lumen.Engine.TableData.</summary>
    public sealed class TableDef
    {
        /// <summary>Stable id. Reports refer to this, never to the name, so a rename breaks nothing.</summary>
        public string Id { get; set; }
        /// <summary>Display name, and the name LQL uses.</summary>
        public string Name { get; set; }
        /// <summary>Where Refresh and the transform window read from.</summary>
        public DataSourceRef Source { get; set; }
        /// <summary>Applied steps, in order.</summary>
        public List<TransformStepDef> Steps { get; set; } = new List<TransformStepDef>();
        /// <summary>Columns after the steps, rebuilt by TableLoader.Bind on every load.</summary>
        public List<ColumnDef> Columns { get; set; } = new List<ColumnDef>();
        /// <summary>Set when the table is marked as a date table. Time intelligence refuses to compile without it.</summary>
        public string DateTableColumnId { get; set; }

        // Designer position on the model diagram.
        /// <summary>Left edge of the table card in Model view.</summary>
        public double DiagramX { get; set; }
        /// <summary>Top edge of the table card in Model view.</summary>
        public double DiagramY { get; set; }

        /// <summary>Finds a column by stable id, or null.</summary>
        public ColumnDef Column(string id) => Columns.FirstOrDefault(c => c.Id == id);
        /// <summary>Finds a column by name, ignoring case.</summary>
        public ColumnDef ColumnByName(string name) =>
            Columns.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A column's settings. Rows are in Lumen.Engine.Column.</summary>
    public sealed class ColumnDef
    {
        /// <summary>Stable id, kept across refreshes by matching on name.</summary>
        public string Id { get; set; }
        /// <summary>Display name, and the name LQL uses.</summary>
        public string Name { get; set; }
        /// <summary>Type of the data as last loaded. TableLoader.Bind overwrites this from the column itself.</summary>
        public LumenDataType DataType { get; set; }
        /// <summary>.NET format string used for display, or null.</summary>
        public string FormatString { get; set; }
        /// <summary>Aggregation applied when the column lands in a value well. None makes it a grouping column.</summary>
        public SummarizeBy SummarizeBy { get; set; }
        /// <summary>Column whose order this one displays in, as month names sort by month number.</summary>
        public string SortByColumnId { get; set; }
        /// <summary>Hidden from the Fields pane. Still usable from LQL.</summary>
        public bool IsHidden { get; set; }
        /// <summary>Reserved for calculated columns, which are not implemented. Use an Add custom column step instead.</summary>
        public string Expression { get; set; }
    }

    /// <summary>A many-to-one link. "From" is the many side, "To" is the one side, and filters flow from To to From.</summary>
    public sealed class RelationshipDef
    {
        /// <summary>Stable id; also the key of the foreign key caches in Dataset.</summary>
        public string Id { get; set; }
        /// <summary>Table on the many side.</summary>
        public string FromTableId { get; set; }
        /// <summary>Foreign key column.</summary>
        public string FromColumnId { get; set; }
        /// <summary>Table on the one side.</summary>
        public string ToTableId { get; set; }
        /// <summary>Key column. Expected to be unique; on duplicates the first row wins.</summary>
        public string ToColumnId { get; set; }
        /// <summary>Display only; see <see cref="Lumen.Model.Cardinality"/>.</summary>
        public Cardinality Cardinality { get; set; }
        /// <summary>Always Single today; see <see cref="Lumen.Model.CrossFilter"/>.</summary>
        public CrossFilter CrossFilter { get; set; }
        /// <summary>Inactive relationships are drawn dashed and ignored by the engine. There is no USERELATIONSHIP.</summary>
        public bool IsActive { get; set; } = true;
    }

    /// <summary>A named LQL expression, evaluated in the filter context of each cell.</summary>
    public sealed class MeasureDef
    {
        /// <summary>Stable id. Visuals refer to this.</summary>
        public string Id { get; set; }
        /// <summary>Display name, and the name [Name] resolves to in LQL. Unique across measures.</summary>
        public string Name { get; set; }
        /// <summary>Table the measure is listed under.</summary>
        public string HomeTableId { get; set; }
        /// <summary>LQL source, compiled lazily on first use and cached per Dataset.</summary>
        public string Expression { get; set; }
        /// <summary>.NET format string, or null.</summary>
        public string FormatString { get; set; }
        /// <summary>Reserved for grouping in the Fields pane. Not read yet.</summary>
        public string DisplayFolder { get; set; }
    }

    /// <summary>Where a table's rows come from. Strings rather than subclasses, to keep CLR type names out of the JSON.</summary>
    public sealed class DataSourceRef
    {
        /// <summary>Connector selector; see TableLoader.ReadSource.</summary>
        public string Kind { get; set; }        // csv | excel | json | sql | sample
        /// <summary>File path for csv, excel and json.</summary>
        public string Path { get; set; }        // file path
        /// <summary>Worksheet name for excel. Null means the first sheet.</summary>
        public string Sheet { get; set; }       // Excel sheet
        /// <summary>ADO.NET connection string for sql. The dialog only produces integrated security, so it holds no password.</summary>
        public string ConnectionString { get; set; }
        /// <summary>For sql, a table name or a SELECT or WITH statement. For sample, the sample table's name.</summary>
        public string Query { get; set; }       // SQL text or table name
        /// <summary>Whether the first row of a csv or excel source holds column names.</summary>
        public bool HasHeaders { get; set; } = true;
    }

    /// <summary>One applied step. Data, not behaviour: Lumen.Connectors interprets Kind and Args,
    /// which keeps the JSON free of CLR type names.</summary>
    public sealed class TransformStepDef
    {
        /// <summary>Stable id of the step.</summary>
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        /// <summary>One of the StepKinds constants.</summary>
        public string Kind { get; set; }
        /// <summary>Text shown in the Applied steps list.</summary>
        public string Label { get; set; }
        /// <summary>Step arguments, as strings. Lists are '|' separated.</summary>
        public Dictionary<string, string> Args { get; set; } = new Dictionary<string, string>();
    }

    /// <summary>Everything the user authors: the root of report.json inside a .lumen file.</summary>
    public sealed class ReportDocument
    {
        /// <summary>File format version. LumenFile.Open refuses a file newer than it knows.</summary>
        public int SchemaVersion { get; set; } = 1;
        /// <summary>Identity of the report.</summary>
        public Guid Id { get; set; } = Guid.NewGuid();
        /// <summary>Report name, used when there is no file name yet.</summary>
        public string Name { get; set; } = "Untitled";
        /// <summary>The semantic model.</summary>
        public SemanticModel Model { get; set; } = new SemanticModel();
        /// <summary>The chosen theme.</summary>
        public ThemeDef Theme { get; set; } = new ThemeDef();
        /// <summary>Filters on all pages.</summary>
        public List<FilterDef> ReportFilters { get; set; } = new List<FilterDef>();
        /// <summary>Pages, in tab order.</summary>
        public List<PageDef> Pages { get; set; } = new List<PageDef>();

        /// <summary>Finds a page by id, or null.</summary>
        public PageDef Page(string id) => Pages.FirstOrDefault(p => p.Id == id);
        /// <summary>Finds a visual on a page, or null.</summary>
        public VisualDef Find(string pageId, string visualId) =>
            Page(pageId)?.Visuals.FirstOrDefault(v => v.Id == visualId);
    }

    /// <summary>A theme choice. <see cref="CustomJson"/> wins over <see cref="BuiltIn"/> when set.</summary>
    public sealed class ThemeDef
    {
        /// <summary>"Light", "Dark" or "High contrast".</summary>
        public string BuiltIn { get; set; } = "Light";
        /// <summary>An imported Power BI theme file, kept verbatim so a report carries its theme with it.</summary>
        public string CustomJson { get; set; }
    }

    /// <summary>One report page.</summary>
    public sealed class PageDef
    {
        /// <summary>Stable id.</summary>
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        /// <summary>Text on the page tab.</summary>
        public string Name { get; set; }
        /// <summary>Page width in device-independent pixels.</summary>
        public double Width { get; set; } = 1280;
        /// <summary>Page height in device-independent pixels.</summary>
        public double Height { get; set; } = 720;
        /// <summary>Filters on this page.</summary>
        public List<FilterDef> PageFilters { get; set; } = new List<FilterDef>();
        /// <summary>Visuals on the page, in no particular z order; see <see cref="VisualDef.ZIndex"/>.</summary>
        public List<VisualDef> Visuals { get; set; } = new List<VisualDef>();
        /// <summary>Only the pairs the user changed. Everything else follows the defaults in ReportCanvas.InteractionMode.</summary>
        public List<InteractionOverride> Interactions { get; set; } = new List<InteractionOverride>();
    }

    /// <summary>One visual on a page: position, field wells, filters and format.</summary>
    public sealed class VisualDef
    {
        /// <summary>Stable id.</summary>
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        /// <summary>Key in VisualRegistry, such as "clusteredColumn".</summary>
        public string Type { get; set; }
        /// <summary>Left edge on the page.</summary>
        public double X { get; set; }
        /// <summary>Top edge on the page.</summary>
        public double Y { get; set; }
        /// <summary>Width on the page.</summary>
        public double Width { get; set; }
        /// <summary>Height on the page.</summary>
        public double Height { get; set; }
        /// <summary>Stacking order; higher draws on top.</summary>
        public int ZIndex { get; set; }
        /// <summary>Set from the Selection pane. Hidden visuals run no queries.</summary>
        public bool IsHidden { get; set; }
        /// <summary>Set from the Selection pane. Locked visuals cannot be dragged or resized.</summary>
        public bool IsLocked { get; set; }
        /// <summary>Field wells: role name to the fields in it. Role names come from the visual's capabilities.</summary>
        public Dictionary<string, List<FieldRef>> Roles { get; set; } = new Dictionary<string, List<FieldRef>>();
        /// <summary>Filters on this visual only.</summary>
        public List<FilterDef> VisualFilters { get; set; } = new List<FilterDef>();
        /// <summary>Only the format options that differ from the defaults, by property name of the visual's format class.
        /// A JObject, so that this assembly need not know those classes.</summary>
        public JObject Format { get; set; } = new JObject();

        /// <summary>Current output of a slicer. Saved, so a slicer's selection is the report's default.</summary>
        public FilterDef SlicerState { get; set; }

        /// <summary>The fields in a role. Never null, even for an unknown role.</summary>
        public List<FieldRef> Role(string name)
        {
            List<FieldRef> list;
            return Roles != null && Roles.TryGetValue(name, out list) ? list : new List<FieldRef>();
        }
    }

    /// <summary>A reference to a column or measure, by id. The currency of wells, filters and queries.</summary>
    public sealed class FieldRef
    {
        /// <summary>Column or measure.</summary>
        public FieldKind Kind { get; set; }
        /// <summary>Table of a column reference.</summary>
        public string TableId { get; set; }
        /// <summary>Column of a column reference.</summary>
        public string ColumnId { get; set; }
        /// <summary>Measure of a measure reference.</summary>
        public string MeasureId { get; set; }
        /// <summary>When set to anything but None, the column acts as an implicit measure.</summary>
        public SummarizeBy? Aggregation { get; set; }

        /// <summary>Identity for comparing fields, for instance to match an OrderBy to a query field.</summary>
        [JsonIgnore]
        public string Key => Kind == FieldKind.Measure
            ? "m:" + MeasureId
            : "c:" + TableId + ":" + ColumnId + ":" + (Aggregation?.ToString() ?? "");

        /// <summary>A shallow copy, which is a full copy for this class.</summary>
        public FieldRef Clone() => (FieldRef)MemberwiseClone();

        /// <summary>Makes a column reference, optionally with an aggregation.</summary>
        public static FieldRef ForColumn(string tableId, string columnId, SummarizeBy? agg = null) =>
            new FieldRef { Kind = FieldKind.Column, TableId = tableId, ColumnId = columnId, Aggregation = agg };

        /// <summary>Makes a measure reference.</summary>
        public static FieldRef ForMeasure(string measureId) =>
            new FieldRef { Kind = FieldKind.Measure, MeasureId = measureId };

        /// <summary>A column in a value well is an implicit measure, which behaves like Sum of Qty.</summary>
        [JsonIgnore]
        public bool IsMeasureLike => Kind == FieldKind.Measure
            || (Aggregation.HasValue && Aggregation.Value != SummarizeBy.None);
    }

    /// <summary>One test of an Advanced filter.</summary>
    public sealed class Condition
    {
        /// <summary>The operator.</summary>
        public ConditionOp Op { get; set; }
        /// <summary>Right-hand value. Comes back from JSON as string, long, double or DateTime, so the engine coerces it to the column type.</summary>
        public object Value { get; set; }
    }

    /// <summary>A filter as authored, at any scope, and the output of a slicer or a cross-filter selection.</summary>
    public sealed class FilterDef
    {
        /// <summary>Identity of the card in the Filters pane. Not part of the meaning, so the result cache strips it.</summary>
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        /// <summary>Column being filtered. A measure target is ignored by the engine; filters on measures are not implemented.</summary>
        public FieldRef Target { get; set; }
        /// <summary>Which of the option groups below applies.</summary>
        public FilterType Type { get; set; }
        /// <summary>Basic: the allowed values. Empty means no constraint.</summary>
        public List<object> Values { get; set; } = new List<object>();
        /// <summary>Advanced: the conditions.</summary>
        public List<Condition> Conditions { get; set; } = new List<Condition>();
        /// <summary>Advanced: how conditions combine.</summary>
        public LogicalOp Logic { get; set; }

        // TopN
        /// <summary>TopN: how many values to keep.</summary>
        public int TopCount { get; set; } = 10;
        /// <summary>TopN: keep the lowest rather than the highest.</summary>
        public bool TopIsBottom { get; set; }
        /// <summary>TopN: the measure that ranks the values.</summary>
        public FieldRef TopBy { get; set; }

        // RelativeDate: last N units, ending today (or at the end of the current unit).
        /// <summary>RelativeDate: how many units back from today.</summary>
        public int RelativeCount { get; set; } = 1;
        /// <summary>RelativeDate: the unit.</summary>
        public RelativeUnit RelativeUnit { get; set; } = RelativeUnit.Months;

        /// <summary>A filter with nothing selected does not constrain anything.</summary>
        [JsonIgnore]
        public bool IsEmpty
        {
            get
            {
                switch (Type)
                {
                    case FilterType.Basic: return Values == null || Values.Count == 0;
                    case FilterType.Advanced: return Conditions == null || Conditions.Count == 0;
                    case FilterType.TopN: return TopBy == null || TopCount <= 0;
                    default: return RelativeCount <= 0;
                }
            }
        }

        /// <summary>Shorthand for a Basic filter, which is what slicers and cross-filter selections produce.</summary>
        public static FilterDef Basic(FieldRef target, IEnumerable<object> values) =>
            new FilterDef { Target = target, Type = FilterType.Basic, Values = values.ToList() };
    }

    /// <summary>One user choice from Edit interactions.</summary>
    public sealed class InteractionOverride
    {
        /// <summary>Visual being clicked.</summary>
        public string SourceVisualId { get; set; }
        /// <summary>Visual being affected.</summary>
        public string TargetVisualId { get; set; }
        /// <summary>What the click does to the target.</summary>
        public InteractionMode Mode { get; set; }
    }

    /// <summary>The one JSON configuration used for files, undo snapshots and cache keys.</summary>
    /// <remarks><see cref="TypeNameHandling.None"/> is deliberate: a .lumen file cannot name CLR types to instantiate.</remarks>
    public static class ModelJson
    {
        /// <summary>Indented, nulls omitted, enums as strings, no type names.</summary>
        public static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
        {
            TypeNameHandling = TypeNameHandling.None,
            Formatting = Formatting.Indented,
            NullValueHandling = NullValueHandling.Ignore,
            Converters = { new StringEnumConverter() }
        };

        /// <summary>Serializes with <see cref="Settings"/>.</summary>
        public static string Serialize(object o) => JsonConvert.SerializeObject(o, Settings);
        /// <summary>Deserializes with <see cref="Settings"/>.</summary>
        public static T Deserialize<T>(string json) => JsonConvert.DeserializeObject<T>(json, Settings);
        /// <summary>Copies through JSON. Slow, but authored objects are small and this cannot miss a field.</summary>
        public static T DeepClone<T>(T o) => Deserialize<T>(Serialize(o));
    }
}
