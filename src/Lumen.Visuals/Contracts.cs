using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using Lumen.Charting;
using Lumen.Model;
using Newtonsoft.Json.Linq;

namespace Lumen.Visuals
{
    /// <summary>What a field well accepts. Grouping takes columns; Measure takes measures or columns (which get an aggregation); Any takes both.</summary>
    public enum RoleKind { Grouping, Measure, Any }

    /// <summary>One field well of a visual.</summary>
    public sealed class DataRole
    {
        /// <summary>Key in <see cref="VisualDef.Roles"/>, and the label in the Visualizations pane.</summary>
        public string Name, Display;
        /// <summary>What the well accepts.</summary>
        public RoleKind Kind;
        /// <summary>Allowed field count. Max is enforced when a field is dropped.</summary>
        public int Min, Max;

        /// <summary>Defines a well.</summary>
        public DataRole(string name, string display, RoleKind kind, int min = 0, int max = int.MaxValue)
        {
            Name = name; Display = display; Kind = kind; Min = min; Max = max;
        }

        /// <summary>Whether a field may go in this well.</summary>
        public bool Accepts(FieldRef f, SemanticModel model)
        {
            if (Kind == RoleKind.Grouping) return f.Kind == FieldKind.Column;
            if (Kind == RoleKind.Any) return true;
            if (f.Kind == FieldKind.Measure) return true;
            return f.Kind == FieldKind.Column;
        }
    }

    /// <summary>What a visual declares about itself. The Visualizations pane and the canvas are driven entirely by this.</summary>
    public sealed class VisualCapabilities
    {
        /// <summary>Name shown in the gallery and tooltips.</summary>
        public string DisplayName;
        /// <summary>The field wells, in display order.</summary>
        public List<DataRole> Roles = new List<DataRole>();
        /// <summary>The format class. The Format tab is generated from its attributes by reflection.</summary>
        public Type FormatType = typeof(ChartFormat);
        /// <summary>True if the visual can draw a highlight over its full value. Otherwise a cross-highlight falls back to filtering.</summary>
        public bool SupportsHighlight;
        /// <summary>True for grids and slicers, which handle the mouse themselves.</summary>
        public bool IsInteractiveControl;   // has its own mouse handling, so drag only from the title
        /// <summary>True for slicers, whose output is a filter rather than a selection.</summary>
        public bool IsSlicer;
    }

    /// <summary>A selected mark, as the visual reports it to the canvas.</summary>
    public sealed class DataPoint
    {
        /// <summary>Row key, comparable with <see cref="QueryResult.RowKey"/>, used to draw the selection.</summary>
        public string Key;
        /// <summary>The group columns and values that identify the mark. These become cross-filters on other visuals.</summary>
        public List<KeyValuePair<FieldRef, object>> Fields = new List<KeyValuePair<FieldRef, object>>();
    }

    /// <summary>One line of a tooltip.</summary>
    public sealed class TooltipItem
    {
        /// <summary>Left and right text.</summary>
        public string Label, Value;
        /// <summary>Makes a line.</summary>
        public TooltipItem(string label, string value) { Label = label; Value = value; }
    }

    /// <summary>What a visual may ask of the canvas. This is the visual's only way out.</summary>
    public interface IVisualHost
    {
        /// <summary>Selects a mark, cross-filtering the page. With <paramref name="multiSelect"/> the point toggles.</summary>
        void Select(DataPoint point, bool multiSelect);
        /// <summary>Clears this visual's selection.</summary>
        void ClearSelection();
        /// <summary>Shows a tooltip at the mouse.</summary>
        void ShowTooltip(IReadOnlyList<TooltipItem> items);
        /// <summary>Hides the tooltip.</summary>
        void HideTooltip();
        /// <summary>A stable theme color for a series or category, so that the same name has one color across a page.</summary>
        Color ColorFor(string seriesKey);
        /// <summary>Publishes a slicer's filter. Null clears it.</summary>
        void SetSlicerFilter(FilterDef filter);
    }

    /// <summary>Everything a visual needs to redraw. Passed to <see cref="ILumenVisual.Update"/>.</summary>
    public sealed class VisualUpdate
    {
        /// <summary>The first query's result.</summary>
        public QueryResult Data;
        /// <summary>All query results, in the order of <see cref="ILumenVisual.BuildQueries"/>.</summary>
        public IReadOnlyList<QueryResult> All;
        /// <summary>Available size.</summary>
        public Size Size;
        /// <summary>Deserialized format, of the capabilities' FormatType.</summary>
        public object Format;
        /// <summary>The active theme.</summary>
        public ResolvedTheme Theme;
        /// <summary>Row keys selected in this visual. Empty means nothing is selected.</summary>
        public HashSet<string> Selection = new HashSet<string>();
        /// <summary>The visual's definition.</summary>
        public VisualDef Def;
        /// <summary>The model, for labels.</summary>
        public SemanticModel Model;
    }

    /// <summary>The visual contract. The canvas calls <see cref="CreateView"/> once, then <see cref="BuildQueries"/> and <see cref="Update"/> on every refresh.
    /// To add a visual, implement this and register it in <see cref="VisualRegistry"/>.</summary>
    public interface ILumenVisual : IDisposable
    {
        /// <summary>The visual's static description.</summary>
        VisualCapabilities Capabilities { get; }
        /// <summary>Creates the WPF element, keeping the host for later callbacks.</summary>
        FrameworkElement CreateView(IVisualHost host);
        /// <summary>Turns the field wells into queries. Runs on the UI thread and must not touch data. Returning none means the wells are incomplete.</summary>
        IReadOnlyList<QuerySpec> BuildQueries(VisualDef def, SemanticModel model);
        /// <summary>Redraws from new results, on the UI thread.</summary>
        void Update(VisualUpdate update);
    }

    /// <summary>A theme as concrete colors, ready for drawing.</summary>
    public sealed class ResolvedTheme
    {
        /// <summary>Theme name.</summary>
        public string Name;
        /// <summary>Series palette, used in rotation.</summary>
        public Color[] DataColors;
        /// <summary>Page background.</summary>
        public Color Background;        // page
        /// <summary>Default visual background.</summary>
        public Color VisualBackground;
        /// <summary>Main text.</summary>
        public Color Foreground;
        /// <summary>Axis labels and captions.</summary>
        public Color Secondary;
        /// <summary>Gridlines.</summary>
        public Color Grid;
        /// <summary>Selection and active states.</summary>
        public Color Accent;
        /// <summary>Visual borders.</summary>
        public Color Border;
        /// <summary>Application panes.</summary>
        public Color ChromeBackground;
        /// <summary>Controls inside panes.</summary>
        public Color ChromePanel;
        /// <summary>Application text.</summary>
        public Color ChromeForeground;

        /// <summary>Parses a color, falling back when it is missing or malformed.</summary>
        public static Color Parse(string hex, Color fallback)
        {
            try { return string.IsNullOrWhiteSpace(hex) ? fallback : (Color)ColorConverter.ConvertFromString(hex); }
            catch (FormatException) { return fallback; }
        }

        private static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);

        private static Color[] Colors(params string[] hex) => hex.Select(C).ToArray();

        /// <summary>The default theme.</summary>
        public static ResolvedTheme Light() => new ResolvedTheme
        {
            Name = "Light",
            DataColors = Colors("#118DFF", "#12239E", "#E66C37", "#6B007B", "#E044A7", "#744EC2", "#D9B300", "#D64550"),
            Background = C("#F3F2F1"), VisualBackground = C("#FFFFFF"), Foreground = C("#252423"), Secondary = C("#605E5C"),
            Grid = C("#E6E6E6"), Accent = C("#118DFF"), Border = C("#D2D0CE"),
            ChromeBackground = C("#FAF9F8"), ChromePanel = C("#FFFFFF"), ChromeForeground = C("#252423")
        };

        /// <summary>The dark theme.</summary>
        public static ResolvedTheme Dark() => new ResolvedTheme
        {
            Name = "Dark",
            DataColors = Colors("#4EA8FF", "#7FE0C3", "#FF9C5B", "#D59BFF", "#FF7DC3", "#A99BFF", "#F5D547", "#FF7A85"),
            Background = C("#1B1A19"), VisualBackground = C("#252423"), Foreground = C("#F3F2F1"), Secondary = C("#C8C6C4"),
            Grid = C("#3B3A39"), Accent = C("#4EA8FF"), Border = C("#484644"),
            ChromeBackground = C("#201F1E"), ChromePanel = C("#292827"), ChromeForeground = C("#F3F2F1")
        };

        /// <summary>Pure colors on black.</summary>
        public static ResolvedTheme HighContrast() => new ResolvedTheme
        {
            Name = "High contrast",
            DataColors = Colors("#FFFF00", "#00FFFF", "#00FF00", "#FF00FF", "#FFFFFF", "#FF8000", "#80FF00", "#FF0080"),
            Background = C("#000000"), VisualBackground = C("#000000"), Foreground = C("#FFFFFF"), Secondary = C("#FFFFFF"),
            Grid = C("#808080"), Accent = C("#FFFF00"), Border = C("#FFFFFF"),
            ChromeBackground = C("#000000"), ChromePanel = C("#000000"), ChromeForeground = C("#FFFFFF")
        };

        /// <summary>Looks up a built-in theme; anything unknown is Light.</summary>
        public static ResolvedTheme FromName(string name)
        {
            switch (name)
            {
                case "Dark": return Dark();
                case "High contrast": return HighContrast();
                default: return Light();
            }
        }

        /// <summary>Reads the Power BI theme keys: name, dataColors, background, foreground, tableAccent.</summary>
        public static ResolvedTheme FromPowerBiJson(string json, ResolvedTheme baseTheme)
        {
            var o = JObject.Parse(json);
            var t = (ResolvedTheme)baseTheme.MemberwiseClone();
            t.Name = (string)o["name"] ?? "Custom";
            if (o["dataColors"] is JArray colors && colors.Count > 0)
                t.DataColors = colors.Select(c => Parse((string)c, t.Accent)).ToArray();
            t.VisualBackground = Parse((string)o["background"], t.VisualBackground);
            t.Foreground = Parse((string)o["foreground"], t.Foreground);
            t.Secondary = Parse((string)o["foregroundNeutralSecondary"], t.Secondary);
            t.Background = Parse((string)o["backgroundLight"], t.Background);
            t.Accent = Parse((string)o["tableAccent"], t.DataColors.Length > 0 ? t.DataColors[0] : t.Accent);
            return t;
        }
    }

    /// <summary>Puts a format property in a named card of the Format tab.</summary>
    [AttributeUsage(AttributeTargets.Property)]
    public sealed class FormatCardAttribute : Attribute
    {
        /// <summary>Card title.</summary>
        public string Card;
        /// <summary>Names the card.</summary>
        public FormatCardAttribute(string card) { Card = card; }
    }

    /// <summary>Labels a format property. The editor is picked from the property type: check box, combo box for enums, number or text box.</summary>
    [AttributeUsage(AttributeTargets.Property)]
    public sealed class FormatPropertyAttribute : Attribute
    {
        /// <summary>Editor label.</summary>
        public string Label;
        /// <summary>Numeric bounds. NaN means unbounded. Attributes cannot use nullable types, hence NaN.</summary>
        public double Min = double.NaN, Max = double.NaN;
        /// <summary>Sets the label.</summary>
        public FormatPropertyAttribute(string label) { Label = label; }
    }

    /// <summary>Options every visual has. Only values that differ from these defaults are stored in the file.</summary>
    public class VisualFormat
    {
        /// <summary>Draw the title bar.</summary>
        [FormatCard("Title"), FormatProperty("Show")] public bool ShowTitle { get; set; } = true;
        /// <summary>Title text. Null derives one from the fields.</summary>
        [FormatCard("Title"), FormatProperty("Text")] public string Title { get; set; }
        /// <summary>Title size.</summary>
        [FormatCard("Title"), FormatProperty("Font size", Min = 8, Max = 40)] public double TitleSize { get; set; } = 13;
        /// <summary>Background override, such as #FFFFFF. Null uses the theme.</summary>
        [FormatCard("Effects"), FormatProperty("Background (hex)")] public string Background { get; set; }
        /// <summary>Draw a border.</summary>
        [FormatCard("Effects"), FormatProperty("Border")] public bool ShowBorder { get; set; }
        /// <summary>Inner margin.</summary>
        [FormatCard("Effects"), FormatProperty("Padding", Min = 0, Max = 40)] public double Padding { get; set; } = 6;
    }

    /// <summary>Options of axis charts and pies.</summary>
    public class ChartFormat : VisualFormat
    {
        /// <summary>Draw the X axis.</summary>
        [FormatCard("X axis"), FormatProperty("Show")] public bool ShowXAxis { get; set; } = true;
        /// <summary>Draw the Y axis.</summary>
        [FormatCard("Y axis"), FormatProperty("Show")] public bool ShowYAxis { get; set; } = true;
        /// <summary>Axis label size.</summary>
        [FormatCard("Axes"), FormatProperty("Font size", Min = 8, Max = 24)] public double AxisFontSize { get; set; } = 11;
        /// <summary>Draw gridlines.</summary>
        [FormatCard("Gridlines"), FormatProperty("Show")] public bool ShowGridlines { get; set; } = true;
        /// <summary>Draw the legend, when there is more than one series.</summary>
        [FormatCard("Legend"), FormatProperty("Show")] public bool ShowLegend { get; set; } = true;
        /// <summary>Label each mark.</summary>
        [FormatCard("Data labels"), FormatProperty("Show")] public bool DataLabels { get; set; }
        /// <summary>Units of data labels.</summary>
        [FormatCard("Data labels"), FormatProperty("Display units")] public DisplayUnits LabelUnits { get; set; } = DisplayUnits.Auto;
        /// <summary>Override for the first series color.</summary>
        [FormatCard("Colors"), FormatProperty("Default color (hex)")] public string Color { get; set; }
    }

    /// <summary>Options of card visuals.</summary>
    public class CardFormat : VisualFormat
    {
        /// <summary>Cards hide their title by default; the label underneath already names them.</summary>
        public CardFormat() { ShowTitle = false; }
        /// <summary>Size of the big number.</summary>
        [FormatCard("Callout"), FormatProperty("Font size", Min = 10, Max = 80)] public double ValueSize { get; set; } = 34;
        /// <summary>Units of the big number.</summary>
        [FormatCard("Callout"), FormatProperty("Display units")] public DisplayUnits Units { get; set; } = DisplayUnits.Auto;
        /// <summary>Show the field name.</summary>
        [FormatCard("Label"), FormatProperty("Show")] public bool ShowLabel { get; set; } = true;
    }

    /// <summary>Options of tables and matrices.</summary>
    public class GridFormat : VisualFormat
    {
        /// <summary>Cell text size.</summary>
        [FormatCard("Grid"), FormatProperty("Font size", Min = 8, Max = 24)] public double FontSize { get; set; } = 12;
        /// <summary>Show the totals row.</summary>
        [FormatCard("Totals"), FormatProperty("Show")] public bool ShowTotals { get; set; } = true;
        /// <summary>Draw bars in numeric cells.</summary>
        [FormatCard("Conditional formatting"), FormatProperty("Data bars")] public bool DataBars { get; set; }
        /// <summary>Shade numeric cells by value.</summary>
        [FormatCard("Conditional formatting"), FormatProperty("Background color scale")] public bool ColorScale { get; set; }
    }

    /// <summary>How a slicer shows its values.</summary>
    public enum SlicerStyle { List, Dropdown, Between, RelativeDate }

    /// <summary>Options of slicers.</summary>
    public class SlicerFormat : VisualFormat
    {
        /// <summary>The slicer's look and filter type.</summary>
        [FormatCard("Slicer"), FormatProperty("Style")] public SlicerStyle Style { get; set; } = SlicerStyle.List;
    }

    /// <summary>The camera positions of the warehouse visual.</summary>
    public enum WarehouseView { Overview, Plan, Aisle }

    /// <summary>What the warehouse visual colors its boxes and floor tiles by.</summary>
    public enum WarehouseColor { FillRate, StockAge, OpenSlots }

    /// <summary>Options of the warehouse visual. The floor plan is derived from the wells, so these sizes shape it without any coordinates in the data.</summary>
    public class WarehouseFormat : VisualFormat
    {
        /// <summary>The scene fills the visual, so the title is off and the padding is gone by default.</summary>
        public WarehouseFormat() { ShowTitle = false; Padding = 0; }

        /// <summary>Clear height of one level, in meters.</summary>
        [FormatCard("Geometry"), FormatProperty("Level height (m)", Min = 0.8, Max = 5)] public double LevelHeight { get; set; } = 1.6;
        /// <summary>Slot width along the aisle, in meters.</summary>
        [FormatCard("Geometry"), FormatProperty("Location width (m)", Min = 0.4, Max = 5)] public double LocationWidth { get; set; } = 1.2;
        /// <summary>Slot depth across the aisle, in meters.</summary>
        [FormatCard("Geometry"), FormatProperty("Location depth (m)", Min = 0.4, Max = 5)] public double LocationDepth { get; set; } = 1.2;
        /// <summary>Clear aisle width, in meters.</summary>
        [FormatCard("Geometry"), FormatProperty("Aisle width (m)", Min = 1, Max = 12)] public double AisleWidth { get; set; } = 3.4;
        /// <summary>Space between zones, in meters.</summary>
        [FormatCard("Geometry"), FormatProperty("Zone gap (m)", Min = 0, Max = 100)] public double ZoneGap { get; set; } = 14;
        /// <summary>Zones per row of the floor plan.</summary>
        [FormatCard("Geometry"), FormatProperty("Zones across", Min = 1, Max = 12)] public int ZonesAcross { get; set; } = 2;

        /// <summary>A bay row under this percentage is low.</summary>
        [FormatCard("Heat colours"), FormatProperty("Fill rate low (%)", Min = 0, Max = 100)] public int FillLow { get; set; } = 30;
        /// <summary>A bay row over this percentage is high.</summary>
        [FormatCard("Heat colours"), FormatProperty("Fill rate high (%)", Min = 0, Max = 100)] public int FillHigh { get; set; } = 70;
        /// <summary>Upper limit of the youngest stock age band, in days.</summary>
        [FormatCard("Heat colours"), FormatProperty("Stock age fresh (days)", Min = 1)] public int AgeFresh { get; set; } = 30;
        /// <summary>Upper limit of the second band, in days.</summary>
        [FormatCard("Heat colours"), FormatProperty("Stock age middle (days)", Min = 1)] public int AgeMiddle { get; set; } = 90;
        /// <summary>Upper limit of the third band, in days. Older stock is the fourth band.</summary>
        [FormatCard("Heat colours"), FormatProperty("Stock age old (days)", Min = 1)] public int AgeOld { get; set; } = 180;
        /// <summary>Lowest open slot count of the second band.</summary>
        [FormatCard("Heat colours"), FormatProperty("Open slots low", Min = 1)] public int OpenLow { get; set; } = 6;
        /// <summary>Lowest open slot count of the third band.</summary>
        [FormatCard("Heat colours"), FormatProperty("Open slots middle", Min = 1)] public int OpenMiddle { get; set; } = 20;
        /// <summary>Lowest open slot count of the top band.</summary>
        [FormatCard("Heat colours"), FormatProperty("Open slots high", Min = 1)] public int OpenHigh { get; set; } = 40;
        /// <summary>Low fill color. Null uses the default.</summary>
        [FormatCard("Heat colours"), FormatProperty("Low fill (hex)")] public string ColorUnder { get; set; }
        /// <summary>Middle fill color. Null uses the default.</summary>
        [FormatCard("Heat colours"), FormatProperty("Middle fill (hex)")] public string ColorMiddle { get; set; }
        /// <summary>High fill color. Null uses the default.</summary>
        [FormatCard("Heat colours"), FormatProperty("High fill (hex)")] public string ColorOver { get; set; }
        /// <summary>Carton color in the Fill rate mode. Null uses the default.</summary>
        [FormatCard("Heat colours"), FormatProperty("Occupied box (hex)")] public string ColorOccupied { get; set; }
        /// <summary>Upright color. Null uses the default.</summary>
        [FormatCard("Heat colours"), FormatProperty("Uprights (hex)")] public string ColorUpright { get; set; }
        /// <summary>Beam color. Null uses the default.</summary>
        [FormatCard("Heat colours"), FormatProperty("Beams (hex)")] public string ColorBeam { get; set; }

        /// <summary>The Status value that means a location holds stock. Anything else counts as open.</summary>
        [FormatCard("Interface"), FormatProperty("Filled status value")] public string FilledValue { get; set; } = "Filled";
        /// <summary>The view on opening.</summary>
        [FormatCard("Interface"), FormatProperty("Start view")] public WarehouseView StartView { get; set; } = WarehouseView.Overview;
        /// <summary>The color mode on opening.</summary>
        [FormatCard("Interface"), FormatProperty("Start color")] public WarehouseColor StartColor { get; set; } = WarehouseColor.FillRate;
        /// <summary>Length of a camera flight. Zero jumps.</summary>
        [FormatCard("Interface"), FormatProperty("Camera flight (ms)", Min = 0, Max = 5000)] public double FlightMs { get; set; } = 1100;
        /// <summary>Show the hint and the mode and view buttons.</summary>
        [FormatCard("Interface"), FormatProperty("Toolbar")] public bool ShowToolbar { get; set; } = true;
        /// <summary>Show the legend.</summary>
        [FormatCard("Interface"), FormatProperty("Legend")] public bool ShowLegend { get; set; } = true;
        /// <summary>Show the pinned callout on a click.</summary>
        [FormatCard("Interface"), FormatProperty("Callout")] public bool ShowCallout { get; set; } = true;
    }

    /// <summary>The catalog of visual types. Keys are stored in report files, so they must not change.</summary>
    public static class VisualRegistry
    {
        private static readonly Dictionary<string, Func<ILumenVisual>> Factories = new Dictionary<string, Func<ILumenVisual>>
        {
            { "card", () => new CardVisual() },
            { "multiRowCard", () => new MultiRowCardVisual() },
            { "table", () => new TableVisual() },
            { "matrix", () => new MatrixVisual() },
            { "clusteredBar", () => new BarVisual(true, false) },
            { "stackedBar", () => new BarVisual(true, true) },
            { "clusteredColumn", () => new BarVisual(false, false) },
            { "stackedColumn", () => new BarVisual(false, true) },
            { "line", () => new LineVisual(false) },
            { "area", () => new LineVisual(true) },
            { "pie", () => new PieVisual(false) },
            { "donut", () => new PieVisual(true) },
            { "scatter", () => new ScatterVisual() },
            { "slicer", () => new SlicerVisual() },
            { "warehouse3d", () => new WarehouseVisual() }
        };

        /// <summary>All type keys, in gallery order.</summary>
        public static IEnumerable<string> Types => Factories.Keys;

        /// <summary>Instantiates a visual. An unknown type becomes a card, so a newer file still opens.</summary>
        public static ILumenVisual Create(string type)
        {
            Func<ILumenVisual> f;
            return Factories.TryGetValue(type ?? "", out f) ? f() : new CardVisual();
        }

        /// <summary>The friendly name of a type.</summary>
        public static string DisplayName(string type)
        {
            using (var v = Create(type)) return v.Capabilities.DisplayName;
        }

        /// <summary>Deserializes a visual's stored overrides over the format class's defaults. Bad JSON gives plain defaults.</summary>
        public static object ReadFormat(VisualDef def, Type type)
        {
            try { return (def.Format ?? new JObject()).ToObject(type, Newtonsoft.Json.JsonSerializer.Create(ModelJson.Settings)); }
            catch (Newtonsoft.Json.JsonException) { return Activator.CreateInstance(type); }
        }
    }

    /// <summary>Helpers visuals share when building queries.</summary>
    public static class QueryHelpers
    {
        /// <summary>Makes value-well fields into measures: a bare column takes its Summarize by, or Sum for numbers and Count otherwise.</summary>
        public static List<FieldRef> Normalize(IEnumerable<FieldRef> fields, SemanticModel model)
        {
            return fields.Select(f =>
            {
                if (f.Kind == FieldKind.Measure || f.IsMeasureLike) return f;
                var col = model.Table(f.TableId)?.Column(f.ColumnId);
                var agg = col != null && col.SummarizeBy != SummarizeBy.None ? col.SummarizeBy
                    : col != null && col.DataType.IsNumeric() ? SummarizeBy.Sum : SummarizeBy.Count;
                return FieldRef.ForColumn(f.TableId, f.ColumnId, agg);
            }).ToList();
        }

        /// <summary>Ordered axes (dates, numbers, sort-by columns) keep their order; anything else sorts by value.</summary>
        public static bool HasNaturalOrder(FieldRef f, SemanticModel model)
        {
            var c = model.Table(f.TableId)?.Column(f.ColumnId);
            return c != null && (c.SortByColumnId != null || c.DataType.IsTemporal() || c.DataType.IsNumeric());
        }
    }
}
