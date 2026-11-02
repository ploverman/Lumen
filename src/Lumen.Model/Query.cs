using System.Collections.Generic;
using System.Text;

namespace Lumen.Model
{
    /// <summary>One sort key of a query.</summary>
    public sealed class OrderBy
    {
        /// <summary>A field that is also in GroupBy or Measures. Any other field is skipped.</summary>
        public FieldRef Field { get; set; }
        /// <summary>Sort direction.</summary>
        public bool Descending { get; set; }
    }

    /// <summary>What a visual asks the engine for, like Power BI's per-visual SUMMARIZECOLUMNS.</summary>
    public sealed class QuerySpec
    {
        /// <summary>Grouping columns. Empty gives one row, the grand value.</summary>
        public List<FieldRef> GroupBy { get; set; } = new List<FieldRef>();
        /// <summary>Measures, or columns with an aggregation.</summary>
        public List<FieldRef> Measures { get; set; } = new List<FieldRef>();
        /// <summary>Every filter in force, merged from all scopes by ReportCanvas.ComposeFilters.</summary>
        public List<FilterDef> Filters { get; set; } = new List<FilterDef>();
        /// <summary>Filters of a cross-highlight. Applied on top of <see cref="Filters"/> in a second query, so the full bar stays and the highlighted part is drawn over it.</summary>
        public List<FilterDef> Highlight { get; set; } = new List<FilterDef>();
        /// <summary>Sort keys. Ties, and the default, follow the group columns in display order.</summary>
        public List<OrderBy> OrderBy { get; set; } = new List<OrderBy>();
        /// <summary>Keep only this many rows after sorting. Does not mark the result truncated.</summary>
        public int? TopN { get; set; }
        /// <summary>Row cap, so one visual cannot ask for millions of marks. Exceeding it sets <see cref="QueryResult.Truncated"/>.</summary>
        public int Limit { get; set; } = 3500;
        /// <summary>Also evaluate each measure with no group filter.</summary>
        public bool IncludeTotals { get; set; }
    }

    /// <summary>Describes one column of a <see cref="QueryResult"/>.</summary>
    public sealed class ResultColumn
    {
        /// <summary>Display name.</summary>
        public string Name { get; set; }
        /// <summary>The field that produced it.</summary>
        public FieldRef Field { get; set; }
        /// <summary>True for measure columns.</summary>
        public bool IsMeasure { get; set; }
        /// <summary>Data type. Measures report Double unless they aggregate a date column.</summary>
        public LumenDataType DataType { get; set; }
        /// <summary>Format string of the column or measure.</summary>
        public string FormatString { get; set; }
    }

    /// <summary>Columnar result: Columns lists group columns first, then measures.</summary>
    public sealed class QueryResult
    {
        /// <summary>Group columns, then measures.</summary>
        public List<ResultColumn> Columns { get; set; } = new List<ResultColumn>();
        /// <summary>Cell values, indexed [column][row].</summary>
        public Value[][] Data { get; set; } = new Value[0][];
        /// <summary>Rows in <see cref="Data"/>.</summary>
        public int RowCount { get; set; }
        /// <summary>How many leading entries of <see cref="Columns"/> are group columns.</summary>
        public int GroupCount { get; set; }
        /// <summary>Grand total per measure, or null when totals were not requested.</summary>
        public Value[] Totals { get; set; }                // one per measure
        /// <summary>Highlight values per measure, aligned with the rows, or null when no highlight was requested.</summary>
        public Value[][] Highlight { get; set; }           // one per measure, aligned with rows; null if none
        /// <summary>The query had more rows than its limit.</summary>
        public bool Truncated { get; set; }
        /// <summary>Set when the query failed. Visuals show this text in place of the chart.</summary>
        public string Error { get; set; }
        /// <summary>Time spent in the engine.</summary>
        public double QueryMs { get; set; }
        /// <summary>Reserved. The cache returns the stored result as is, so this is not set.</summary>
        public bool FromCache { get; set; }

        /// <summary>Number of measure columns.</summary>
        public int MeasureCount => Columns.Count - GroupCount;
        /// <summary>A cell of a group column.</summary>
        public Value Group(int col, int row) => Data[col][row];
        /// <summary>A cell of a measure column, indexed from the first measure.</summary>
        public Value Measure(int measure, int row) => Data[GroupCount + measure][row];

        /// <summary>Stable identity of a row across queries (selection, highlight alignment).</summary>
        public string RowKey(int row)
        {
            var sb = new StringBuilder();
            for (int c = 0; c < GroupCount; c++)
            {
                if (c > 0) sb.Append('\u001f');
                sb.Append(Data[c][row].ToDisplayString());
            }
            return sb.ToString();
        }

        /// <summary>A result that carries only an error message.</summary>
        public static QueryResult Failed(string error) => new QueryResult { Error = error };
    }
}
