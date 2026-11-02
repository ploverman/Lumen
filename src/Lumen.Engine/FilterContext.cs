using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Lumen.Engine
{
    /// <summary>Allowed dictionary codes of one column.</summary>
    public sealed class ColFilter
    {
        /// <summary>Column being filtered.</summary>
        public string TableId, ColumnId;
        /// <summary>Allowed codes, one bit per entry of the column's dictionary.</summary>
        public Bitset Allowed;
        private string _sig;
        /// <summary>Identity of the column. At most one filter per key exists in a context.</summary>
        public string Key => TableId + "|" + ColumnId;
        /// <summary>Column and allowed set as a string. Computed once and kept, since it is read for every memo lookup.</summary>
        public string Sig => _sig ?? (_sig = Key + "=" + Allowed.ContentHash());
    }

    /// <summary>Allowed rows of one table, the result of FILTER(table, predicate).</summary>
    public sealed class RowFilter
    {
        /// <summary>Table being filtered.</summary>
        public string TableId;
        /// <summary>Allowed rows.</summary>
        public Bitset Rows;
        private string _sig;
        /// <summary>Table and allowed rows as a string.</summary>
        public string Sig => _sig ?? (_sig = "r:" + TableId + "=" + Rows.ContentHash());
    }

    /// <summary>Immutable filter context. CALCULATE derives a new one; identical contexts share a
    /// Signature, which is what lets a query reuse row sets across groups.</summary>
    public sealed class FilterContext
    {
        /// <summary>No filters at all: the grand total.</summary>
        public static readonly FilterContext Empty =
            new FilterContext(new Dictionary<string, ColFilter>(), new List<RowFilter>());

        private readonly Dictionary<string, ColFilter> _cols;
        private readonly List<RowFilter> _rows;
        private string _sig;

        private FilterContext(Dictionary<string, ColFilter> cols, List<RowFilter> rows)
        {
            _cols = cols;
            _rows = rows;
        }

        /// <summary>Column filters on one table.</summary>
        public IEnumerable<ColFilter> ColumnsOn(string tableId) => _cols.Values.Where(c => c.TableId == tableId);
        /// <summary>Row filters on one table.</summary>
        public IEnumerable<RowFilter> RowsOn(string tableId) => _rows.Where(r => r.TableId == tableId);

        /// <summary>Canonical text of the whole context. Sorted, so the order in which filters were added does not matter.</summary>
        public string Signature
        {
            get
            {
                if (_sig == null)
                {
                    var sb = new StringBuilder();
                    foreach (var s in _cols.Values.Select(c => c.Sig).OrderBy(x => x, System.StringComparer.Ordinal)) sb.Append(s).Append(';');
                    foreach (var s in _rows.Select(r => r.Sig).OrderBy(x => x, System.StringComparer.Ordinal)) sb.Append(s).Append(';');
                    _sig = sb.ToString();
                }
                return _sig;
            }
        }

        /// <summary>Replaces any filter on the same column: CALCULATE semantics.</summary>
        public FilterContext WithColumn(ColFilter f)
        {
            var d = new Dictionary<string, ColFilter>(_cols);
            d[f.Key] = f;
            return new FilterContext(d, _rows);
        }

        /// <summary>Intersects with any filter on the same column: report, page and visual scopes.</summary>
        public FilterContext AndColumn(ColFilter f)
        {
            ColFilter existing;
            if (_cols.TryGetValue(f.Key, out existing))
            {
                var both = existing.Allowed.Clone();
                both.And(f.Allowed);
                f = new ColFilter { TableId = f.TableId, ColumnId = f.ColumnId, Allowed = both };
            }
            return WithColumn(f);
        }

        /// <summary>Adds a row filter. Row filters only ever accumulate.</summary>
        public FilterContext WithRowFilter(RowFilter f)
        {
            var r = new List<RowFilter>(_rows) { f };
            return new FilterContext(_cols, r);
        }

        /// <summary>Drops the filter on one column: ALL(Table[Column]) and REMOVEFILTERS.</summary>
        public FilterContext WithoutColumn(string tableId, string columnId)
        {
            var d = new Dictionary<string, ColFilter>(_cols);
            d.Remove(tableId + "|" + columnId);
            return new FilterContext(d, _rows);
        }

        /// <summary>Drops every filter on the given tables: ALL(Table), which removes filters on its expanded table.</summary>
        public FilterContext WithoutTables(ICollection<string> tables)
        {
            var d = _cols.Where(kv => !tables.Contains(kv.Value.TableId)).ToDictionary(kv => kv.Key, kv => kv.Value);
            var r = _rows.Where(x => !tables.Contains(x.TableId)).ToList();
            return new FilterContext(d, r);
        }
    }
}
