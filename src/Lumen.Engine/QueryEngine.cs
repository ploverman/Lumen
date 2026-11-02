using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Lumen.Model;

namespace Lumen.Engine
{
    /// <summary>The query boundary. The UI talks only to this, so a remote engine could replace the in-process one.</summary>
    public interface IQueryEngine
    {
        /// <summary>Runs one query off the UI thread.</summary>
        Task<QueryResult> ExecuteAsync(QuerySpec spec, CancellationToken ct);
        /// <summary>Runs several queries concurrently and returns the results in input order. A page refresh uses this.</summary>
        Task<IReadOnlyList<QueryResult>> ExecuteBatchAsync(IReadOnlyList<QuerySpec> specs, CancellationToken ct);
    }

    /// <summary>State of one query: filter-mask memo and per-table row partitions by group.</summary>
    public sealed class QueryScope
    {
        /// <summary>The data being queried.</summary>
        public readonly Dataset Ds;
        /// <summary>Cancelled when a newer refresh supersedes this query.</summary>
        public readonly CancellationToken Ct;

        // Sentinel stored in the memo for "no constraint". A Lazy holding null would work, but this keeps the memo's meaning explicit.
        private static readonly Bitset NoMask = new Bitset(0);

        // Grouping state
        // The filter context before grouping.
        private FilterContext _base;
        // The group columns, in spec order.
        private List<GroupCol> _cols;
        // Mixed-radix place value of each group column. A group key is the sum of code * _mult[j]; (key / _mult[j]) % cardinality recovers the code.
        private long[] _mult;
        // The distinct keys of the driver table, indexed by group number.
        private long[] _groupKeys;
        // Product of all cardinalities: the size of a dense array indexed by key.
        private long _keySpace;
        // Up to about a million possible keys, an array beats a dictionary.
        private const long DenseKeys = 1 << 20;
        // One partition per table the measures touch. The Lazy makes parallel group evaluation build each once.
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<Partition>> _partitions = new System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<Partition>>();

        /// <summary>A resolved group column.</summary>
        private sealed class GroupCol
        {
            public string TableId, ColumnId;
            public Column Data;
            public Encoding Enc;
            /// <summary>Display position of each code; see <see cref="SortRank"/>.</summary>
            public int[] Rank;
        }

        /// <summary>A table's rows split by group key.</summary>
        private sealed class Partition
        {
            /// <summary>Row list per key.</summary>
            public Dictionary<long, int[]> Rows;
            public long[] ProjectionMask;   // per group column: 1 if this table sees it
        }

        /// <summary>Scopes are single use: one query, then discarded. The grouping fields make them stateful.</summary>
        public QueryScope(Dataset ds, CancellationToken ct) { Ds = ds; Ct = ct; }

        /// <summary>Row mask of a table under a filter context, or null when nothing constrains it.</summary>
        public Bitset Mask(FilterContext f, string tableId)
        {
            string key = f.Signature + "#" + tableId;
            var mask = Shared(Ds.MaskMemo, key, () => BuildMask(f, tableId));
            return ReferenceEquals(mask, NoMask) ? null : mask;
        }

        /// <summary>Concurrent queries wait for one computation. A cancelled computation is forgotten, and
        /// a query that was not itself cancelled computes the value again.</summary>
        private T Shared<T>(System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<T>> memo, string key, Func<T> build)
        {
            while (true)
            {
                var entry = memo.GetOrAdd(key, _ => new Lazy<T>(build));
                try { return entry.Value; }
                catch (OperationCanceledException)
                {
                    Lazy<T> gone;
                    memo.TryRemove(key, out gone);
                    Ct.ThrowIfCancellationRequested();
                }
            }
        }

        /// <summary>Computes a table's mask by ANDing its own filters with whatever arrives over its relationships.</summary>
        private Bitset BuildMask(FilterContext f, string tableId)
        {
            Ct.ThrowIfCancellationRequested();
            var table = Ds.Tables[tableId];

            // Each filter's rows are memoised on their own: per-group contexts differ in one filter only,
            // so a line chart rescans nothing for the eleven months after the first.
            var parts = new List<Bitset>();
            foreach (var cf in f.ColumnsOn(tableId))
                parts.Add(Shared(Ds.MaskMemo, "c:" + cf.Sig, () => ScanColumn(table, cf)));
            foreach (var rf in f.RowsOn(tableId)) parts.Add(rf.Rows);

            // Filters flow from the one side to the many side, so snowflakes resolve inward by recursion.
            foreach (var rel in Ds.Model.ActiveRelationshipsFrom(tableId))
            {
                var dimMask = Mask(f, rel.ToTableId);
                if (dimMask == null) continue;
                parts.Add(Shared(Ds.MaskMemo, "r:" + rel.Id + "=" + dimMask.ContentHash(), () => FollowRelationship(table, rel, dimMask)));
            }

            if (parts.Count == 0) return NoMask;
            if (parts.Count == 1) return parts[0];
            var mask = parts[0].Clone();
            for (int i = 1; i < parts.Count; i++) mask.And(parts[i]);
            Ds.NoteMemo(mask.Words.Length * 8L);
            return mask;
        }

        /// <summary>Turns allowed codes into allowed rows by testing every row's code.</summary>
        private Bitset ScanColumn(TableData table, ColFilter cf)
        {
            Ct.ThrowIfCancellationRequested();
            var codes = Ds.ColumnOf(table.Id, cf.ColumnId).Encoded.Codes;
            var allowed = cf.Allowed;
            var mask = Bitset.AllSet(table.RowCount);
            Par.Ranges(codes.Length, (from, to) =>
            {
                for (int r = from; r < to; r++)
                    if (!allowed[codes[r]]) mask.Clear(r);
            });
            Ds.NoteMemo(mask.Words.Length * 8L);
            return mask;
        }

        /// <summary>Projects a mask from the one side onto this table. Two strategies: fan out through the inverted index when
        /// fewer than one dimension row in eight survives, otherwise test each fact row against the mask.</summary>
        private Bitset FollowRelationship(TableData table, RelationshipDef rel, Bitset dimMask)
        {
            Ct.ThrowIfCancellationRequested();
            Bitset mask;
            // A narrow dimension filter (one month, say) reads the index instead of the whole fact table.
            if (table.RowCount > Par.Chunk * 2 && dimMask.Count() * 8L < dimMask.Length)
            {
                var index = Ds.IndexOf(rel);
                mask = new Bitset(table.RowCount);
                foreach (int d in dimMask.ToRows())
                    for (int i = index.Start[d]; i < index.Start[d + 1]; i++) mask.Set(index.Rows[i]);
            }
            else
            {
                var fk = Ds.FkToDimRow(rel);
                var all = Bitset.AllSet(table.RowCount);
                Par.Ranges(fk.Length, (from, to) =>
                {
                    for (int r = from; r < to; r++)
                        if (fk[r] < 0 || !dimMask[fk[r]]) all.Clear(r);
                });
                mask = all;
            }
            Ds.NoteMemo(mask.Words.Length * 8L);
            return mask;
        }

        /// <summary>The surviving rows of a table as an ascending list. The list costs more memory than the mask, but it is what the aggregation loops take.</summary>
        public int[] Rows(FilterContext f, string tableId)
        {
            string key = f.Signature + "#" + tableId;
            return Shared(Ds.RowMemo, key, () =>
            {
                var mask = Mask(f, tableId);
                if (mask == null) return Ds.Tables[tableId].AllRows;
                var rows = mask.ToRows();
                Ds.NoteMemo(rows.Length * 4L);
                return rows;
            });
        }

        /// <summary>Builds the full filter context of one group: the base context plus "this code only" per group column.
        /// Called lazily through <see cref="EvalCtx.F"/>, since a plain SUM in a group never needs it.</summary>
        internal FilterContext GroupFilter(int group)
        {
            var f = _base;
            long key = _groupKeys[group];
            for (int j = 0; j < _cols.Count; j++)
            {
                var c = _cols[j];
                int code = (int)(key / _mult[j] % c.Enc.Values.Length);
                var allowed = new Bitset(c.Enc.Values.Length);
                allowed.Set(code);
                f = f.AndColumn(new ColFilter { TableId = c.TableId, ColumnId = c.ColumnId, Allowed = allowed });
            }
            return f;
        }

        /// <summary>The rows of a table in one group, without touching a filter context. This is the fast path behind every plain aggregate.</summary>
        internal int[] GroupRows(string tableId, int group)
        {
            var p = _partitions.GetOrAdd(tableId, id => new Lazy<Partition>(() => BuildPartition(id))).Value;
            // A table that sees only some group columns was partitioned on those alone, so the key is rebuilt with the others zeroed.
            long key = _groupKeys[group], projected = 0;
            for (int j = 0; j < _cols.Count; j++)
                if (p.ProjectionMask[j] != 0)
                    projected += (key / _mult[j] % _cols[j].Enc.Values.Length) * _mult[j];
            int[] rows;
            return p.Rows.TryGetValue(projected, out rows) ? rows : new int[0];
        }

        /// <summary>Splits a table's surviving rows by group key, once per query. Each row lands in one group,
        /// so evaluating a measure over all groups costs one pass, however many groups there are.</summary>
        private Partition BuildPartition(string tableId)
        {
            Ct.ThrowIfCancellationRequested();
            var rows = Rows(_base, tableId);
            int n = _cols.Count;
            var proj = new long[n];
            var codes = new int[n][];
            var maps = new int[n][];
            for (int j = 0; j < n; j++)
            {
                if (!Ds.Reaches(tableId, _cols[j].TableId)) continue;
                proj[j] = 1;
                codes[j] = _cols[j].Enc.Codes;
                maps[j] = Ds.RowMap(tableId, _cols[j].TableId);
            }

            // Compute keys in parallel, each row writing its own slot. Only the bucketing that follows is sequential.
            var keys = Ds.RentKeys(rows.Length);
            Par.Ranges(rows.Length, (from, to) =>
            {
                for (int i = from; i < to; i++)
                {
                    int row = rows[i];
                    long key = 0;
                    for (int j = 0; j < n; j++)
                    {
                        if (proj[j] == 0) continue;
                        // A null map means the group column is on this very table. An unmatched row falls into the BLANK group.
                        int target = maps[j] == null ? row : maps[j].Length == 0 ? -1 : maps[j][row];
                        int code = target < 0 ? 0 : codes[j][target];
                        key += code * _mult[j];
                    }
                    keys[i] = key;
                }
            });

            try
            {
                return _keySpace <= DenseKeys ? DensePartition(rows, keys, proj) : SparsePartition(rows, keys, proj);
            }
            finally { Ds.ReturnKeys(keys); }
        }

        /// <summary>Buckets rows through dictionaries, for key spaces too large for an array: count, allocate, fill.</summary>
        private Partition SparsePartition(int[] rows, long[] keys, long[] proj)
        {
            var counts = new Dictionary<long, int>();
            for (int i = 0; i < rows.Length; i++)
            {
                int c;
                counts.TryGetValue(keys[i], out c);
                counts[keys[i]] = c + 1;
            }

            var result = new Dictionary<long, int[]>(counts.Count);
            var fill = new Dictionary<long, int>(counts.Count);
            foreach (var kv in counts) { result[kv.Key] = new int[kv.Value]; fill[kv.Key] = 0; }
            for (int i = 0; i < rows.Length; i++)
            {
                long key = keys[i];
                int at = fill[key];
                result[key][at] = rows[i];
                fill[key] = at + 1;
            }
            return new Partition { Rows = result, ProjectionMask = proj };
        }

        /// <summary>The same three steps through arrays indexed by key, which is several times faster.</summary>
        private Partition DensePartition(int[] rows, long[] keys, long[] proj)
        {
            var counts = new int[_keySpace];
            for (int i = 0; i < rows.Length; i++) counts[keys[i]]++;
            var lists = new int[_keySpace][];
            var result = new Dictionary<long, int[]>();
            for (int k = 0; k < counts.Length; k++)
            {
                if (counts[k] == 0) continue;
                lists[k] = new int[counts[k]];
                result[k] = lists[k];
                // counts doubles as the fill position from here on.
                counts[k] = 0;
            }
            for (int i = 0; i < rows.Length; i++)
            {
                long k = keys[i];
                lists[k][counts[k]++] = rows[i];
            }
            return new Partition { Rows = result, ProjectionMask = proj };
        }

        // ------------------------------------------------------------ execution

        /// <summary>Runs the query, then the highlight query if there is one.</summary>
        public QueryResult Execute(QuerySpec spec)
        {
            var sw = Stopwatch.StartNew();
            var filter = BuildFilters(spec.Filters);
            var result = Run(spec, filter);
            if (spec.Highlight != null && spec.Highlight.Count > 0 && result.Error == null)
            {
                // A second scope, since Run keeps state per query. Limit is lifted so that every main row can find its match.
                var hl = new QueryScope(Ds, Ct);
                var hlSpec = new QuerySpec { GroupBy = spec.GroupBy, Measures = spec.Measures, Limit = int.MaxValue };
                var hlResult = hl.Run(hlSpec, hl.ApplyAll(filter, spec.Highlight));
                result.Highlight = AlignHighlight(result, hlResult);
            }
            result.QueryMs = sw.Elapsed.TotalMilliseconds;
            return result;
        }

        /// <summary>Lines the highlight rows up with the main rows by group key. A main row with no match stays BLANK: nothing to highlight in that bar.</summary>
        private static Value[][] AlignHighlight(QueryResult main, QueryResult hl)
        {
            var index = new Dictionary<string, int>();
            for (int r = 0; r < hl.RowCount; r++) index[hl.RowKey(r)] = r;
            var output = new Value[main.MeasureCount][];
            for (int m = 0; m < output.Length; m++)
            {
                output[m] = new Value[main.RowCount];
                for (int r = 0; r < main.RowCount; r++)
                {
                    int at;
                    if (index.TryGetValue(main.RowKey(r), out at)) output[m][r] = hl.Measure(m, at);
                }
            }
            return output;
        }

        /// <summary>Turns authored filters into a filter context, starting from nothing.</summary>
        internal FilterContext BuildFilters(IEnumerable<FilterDef> filters) => ApplyAll(FilterContext.Empty, filters);

        /// <summary>Applies filters on top of an existing context. Filters on measures and empty filters are skipped.</summary>
        internal FilterContext ApplyAll(FilterContext start, IEnumerable<FilterDef> filters)
        {
            var f = start;
            var list = (filters ?? Enumerable.Empty<FilterDef>()).Where(x => x != null && !x.IsEmpty && x.Target != null && x.Target.Kind != FieldKind.Measure).ToList();
            foreach (var d in list.Where(x => x.Type != FilterType.TopN)) f = Apply(f, d);
            // TopN ranks within every other filter, so it goes last.
            var withoutTop = f;
            foreach (var d in list.Where(x => x.Type == FilterType.TopN)) f = ApplyTopN(f, withoutTop, d);
            return f;
        }

        /// <summary>Compiles one Basic, Advanced or RelativeDate filter into allowed codes. Testing the dictionary, not the rows, makes the cost depend on distinct values only.
        /// AndColumn means two filters on one column intersect, whatever scope each came from.</summary>
        private FilterContext Apply(FilterContext f, FilterDef d)
        {
            var col = Ds.ColumnOf(d.Target.TableId, d.Target.ColumnId);
            if (col == null) return f;
            var enc = col.Encoded;
            var allowed = new Bitset(enc.Values.Length);

            if (d.Type == FilterType.Basic)
            {
                foreach (var v in d.Values)
                {
                    int code = col.FindCode(v);
                    if (code >= 0) allowed.Set(code);
                }
            }
            else if (d.Type == FilterType.Advanced)
            {
                for (int code = 0; code < enc.Values.Length; code++)
                    if (Matches(enc.Values[code], d, col.Type)) allowed.Set(code);
            }
            else if (d.Type == FilterType.RelativeDate)
            {
                // The window runs from N units before today through the end of today. It is read from the clock per query, while the result cache is keyed on the spec, so a cached result can outlive midnight.
                var today = DateTime.Today;
                DateTime start;
                switch (d.RelativeUnit)
                {
                    case RelativeUnit.Days: start = today.AddDays(-d.RelativeCount); break;
                    case RelativeUnit.Weeks: start = today.AddDays(-7 * d.RelativeCount); break;
                    case RelativeUnit.Years: start = today.AddYears(-d.RelativeCount); break;
                    default: start = today.AddMonths(-d.RelativeCount); break;
                }
                for (int code = 1; code < enc.Values.Length; code++)
                {
                    var v = enc.Values[code];
                    if (v.Kind != ValueKind.Date) continue;
                    if (v.AsDate >= start && v.AsDate < today.AddDays(1)) allowed.Set(code);
                }
            }
            return f.AndColumn(new ColFilter { TableId = d.Target.TableId, ColumnId = d.Target.ColumnId, Allowed = allowed });
        }

        /// <summary>Combines an Advanced filter's conditions, stopping at the first that decides the answer.</summary>
        private static bool Matches(Value v, FilterDef d, LumenDataType type)
        {
            bool isAnd = d.Logic == LogicalOp.And;
            foreach (var cond in d.Conditions)
            {
                bool ok = Test(v, cond, type);
                if (isAnd && !ok) return false;
                if (!isAnd && ok) return true;
            }
            return isAnd;
        }

        /// <summary>Tests one value against one condition. Text operators work on the display string, so they also apply to numbers and dates.</summary>
        private static bool Test(Value v, Condition cond, LumenDataType type)
        {
            if (cond.Op == ConditionOp.IsBlank) return v.IsBlank;
            if (cond.Op == ConditionOp.IsNotBlank) return !v.IsBlank;
            var rhs = Column.Coerce(Value.FromObject(cond.Value), type);
            string text = v.IsBlank ? "" : v.ToDisplayString();
            string needle = Convert.ToString(cond.Value) ?? "";
            switch (cond.Op)
            {
                case ConditionOp.Equals: return Value.Compare(v, rhs) == 0 && v.IsBlank == rhs.IsBlank;
                case ConditionOp.NotEquals: return Value.Compare(v, rhs) != 0 || v.IsBlank != rhs.IsBlank;
                case ConditionOp.LessThan: return !v.IsBlank && Value.Compare(v, rhs) < 0;
                case ConditionOp.LessOrEqual: return !v.IsBlank && Value.Compare(v, rhs) <= 0;
                case ConditionOp.GreaterThan: return !v.IsBlank && Value.Compare(v, rhs) > 0;
                case ConditionOp.GreaterOrEqual: return !v.IsBlank && Value.Compare(v, rhs) >= 0;
                case ConditionOp.Contains: return text.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
                case ConditionOp.NotContains: return text.IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0;
                case ConditionOp.StartsWith: return text.StartsWith(needle, StringComparison.OrdinalIgnoreCase);
                default: return true;
            }
        }

        /// <summary>Runs a nested query to rank the column's values, then filters to the winners. A failed ranking leaves the filter out rather than failing the visual.</summary>
        private FilterContext ApplyTopN(FilterContext f, FilterContext rankWithin, FilterDef d)
        {
            var col = Ds.ColumnOf(d.Target.TableId, d.Target.ColumnId);
            if (col == null) return f;
            var field = FieldRef.ForColumn(d.Target.TableId, d.Target.ColumnId);
            var spec = new QuerySpec
            {
                GroupBy = new List<FieldRef> { field },
                Measures = new List<FieldRef> { d.TopBy },
                OrderBy = new List<OrderBy> { new OrderBy { Field = d.TopBy, Descending = !d.TopIsBottom } },
                TopN = d.TopCount,
                Limit = int.MaxValue
            };
            var result = new QueryScope(Ds, Ct).Run(spec, rankWithin);
            if (result.Error != null) return f;
            var allowed = new Bitset(col.Encoded.Values.Length);
            for (int r = 0; r < result.RowCount; r++)
            {
                int code = col.FindCode(result.Group(0, r).ToObject());
                if (code >= 0) allowed.Set(code);
            }
            return f.AndColumn(new ColFilter { TableId = d.Target.TableId, ColumnId = d.Target.ColumnId, Allowed = allowed });
        }

        /// <summary>Fills in result column metadata, before evaluation, so that a failed query can still show its headers.</summary>
        private void DescribeColumns(QuerySpec spec, QueryResult r)
        {
            foreach (var g in spec.GroupBy)
            {
                var def = Ds.Model.Table(g.TableId)?.Column(g.ColumnId);
                r.Columns.Add(new ResultColumn
                {
                    Name = Ds.Model.DisplayName(g),
                    Field = g,
                    DataType = def?.DataType ?? LumenDataType.String,
                    FormatString = def?.FormatString
                });
            }
            foreach (var m in spec.Measures)
            {
                string format = null;
                var type = LumenDataType.Double;
                if (m.Kind == FieldKind.Measure) format = Ds.Model.Measure(m.MeasureId)?.FormatString;
                else
                {
                    var def = Ds.Model.Table(m.TableId)?.Column(m.ColumnId);
                    bool counts = m.Aggregation == SummarizeBy.Count || m.Aggregation == SummarizeBy.DistinctCount;
                    format = counts ? "#,0" : def?.FormatString;
                    if (!counts && def != null && def.DataType.IsTemporal()) type = def.DataType;
                }
                r.Columns.Add(new ResultColumn { Name = Ds.Model.DisplayName(m), Field = m, IsMeasure = true, DataType = type, FormatString = format });
            }
            r.GroupCount = spec.GroupBy.Count;
        }

        /// <summary>The set of tables that can see every group column; the biggest one drives the groups.</summary>
        private string PickDriver(QuerySpec spec)
        {
            var groupTables = spec.GroupBy.Select(g => g.TableId).Distinct().ToList();
            var candidates = Ds.Tables.Values.Where(t => groupTables.All(g => Ds.Reaches(t.Id, g))).ToList();
            if (candidates.Count == 0) return null;
            return spec.Measures.Count > 0
                ? candidates.OrderByDescending(t => t.RowCount).First().Id
                : candidates.OrderBy(t => t.RowCount).First().Id;
        }

        /// <summary>The core: resolve the driver, partition it to find the groups, evaluate each measure per group, sort, cap and project.
        /// Formula errors become a failed <see cref="QueryResult"/>; cancellation propagates as an exception.</summary>
        internal QueryResult Run(QuerySpec spec, FilterContext filter)
        {
            var result = new QueryResult();
            try
            {
                DescribeColumns(spec, result);
                var evals = spec.Measures.Select(m => Ds.Compiler.FieldEval(m)).ToArray();
                _base = filter;

                if (spec.GroupBy.Count == 0)
                {
                    var c = new EvalCtx(this, filter);
                    result.Data = evals.Select(e => new[] { e(c) }).ToArray();
                    result.RowCount = 1;
                    result.Totals = result.Data.Select(a => a[0]).ToArray();
                    return result;
                }

                foreach (var g in spec.GroupBy)
                    if (Ds.ColumnOf(g.TableId, g.ColumnId) == null)
                        return QueryResult.Failed("A field in this visual no longer exists");

                var driver = PickDriver(spec);
                if (driver == null)
                    return QueryResult.Failed("Can't determine relationships between the fields; add a relationship in Model view");

                PrepareGroups(spec);
                // Only keys with surviving rows become groups, so empty combinations cost nothing.
                var rawKeys = BuildPartition(driver).Rows.Keys.ToArray();

                _groupKeys = rawKeys;
                int groups = rawKeys.Length;
                var values = new Value[evals.Length][];
                for (int m = 0; m < evals.Length; m++) values[m] = new Value[groups];
                var keep = new bool[groups];
                Action<int> evaluate = g =>
                {
                    if ((g & 63) == 0) Ct.ThrowIfCancellationRequested();
                    var c = new EvalCtx(this, null, g);
                    // A group whose measures are all BLANK is dropped, as in Power BI. With no measures, every group stays.
                    bool any = evals.Length == 0;
                    for (int m = 0; m < evals.Length; m++)
                    {
                        values[m][g] = evals[m](c);
                        if (!values[m][g].IsBlank) any = true;
                    }
                    keep[g] = any;
                };
                // Small work stays sequential. Inner Par loops in a parallel group run are fine; they just find fewer idle workers.
                if (groups < 4 || Ds.Tables[driver].RowCount <= Par.Chunk * 2)
                    for (int g = 0; g < groups; g++) evaluate(g);
                else
                {
                    try { Parallel.For(0, groups, evaluate); }
                    // Rethrow the real exception, so the handlers below see LqlException or cancellation rather than a wrapper.
                    catch (AggregateException ex) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw(); }
                }

                var order = Enumerable.Range(0, groups).Where(g => keep[g]).ToList();
                order.Sort((a, b) => CompareGroups(spec, values, rawKeys[a], rawKeys[b], a, b));

                int limit = spec.Limit;
                if (spec.TopN.HasValue) limit = Math.Min(limit, spec.TopN.Value);
                if (order.Count > limit)
                {
                    result.Truncated = !spec.TopN.HasValue;
                    order = order.Take(limit).ToList();
                }

                result.RowCount = order.Count;
                result.Data = new Value[spec.GroupBy.Count + evals.Length][];
                for (int j = 0; j < _cols.Count; j++)
                {
                    var column = new Value[order.Count];
                    for (int i = 0; i < order.Count; i++)
                        column[i] = _cols[j].Enc.Values[(int)(rawKeys[order[i]] / _mult[j] % _cols[j].Enc.Values.Length)];
                    result.Data[j] = column;
                }
                for (int m = 0; m < evals.Length; m++)
                {
                    var column = new Value[order.Count];
                    for (int i = 0; i < order.Count; i++) column[i] = values[m][order[i]];
                    result.Data[_cols.Count + m] = column;
                }

                if (spec.IncludeTotals)
                {
                    // Separately, with the group filter gone, so DISTINCTCOUNT and ratios stay right.
                    var c = new EvalCtx(this, filter);
                    result.Totals = evals.Select(e => e(c)).ToArray();
                }
                return result;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is InvalidOperationException || ex is LqlException)
            {
                var failed = QueryResult.Failed(ex.Message);
                failed.Columns = result.Columns;
                failed.GroupCount = result.GroupCount;
                return failed;
            }
        }

        /// <summary>Resolves group columns and lays out the mixed-radix key. checked() turns an impossible key space into an exception, not a silent wrap.</summary>
        private void PrepareGroups(QuerySpec spec)
        {
            _cols = new List<GroupCol>();
            foreach (var g in spec.GroupBy)
            {
                var data = Ds.ColumnOf(g.TableId, g.ColumnId);
                _cols.Add(new GroupCol { TableId = g.TableId, ColumnId = g.ColumnId, Data = data, Enc = data.Encoded, Rank = SortRank(g) });
            }
            _mult = new long[_cols.Count];
            long m = 1;
            for (int j = 0; j < _cols.Count; j++)
            {
                _mult[j] = m;
                m = checked(m * Math.Max(1, _cols[j].Enc.Values.Length));
            }
            _keySpace = m;
        }

        /// <summary>Position of each code in display order, honouring "sort by column".</summary>
        private int[] SortRank(FieldRef g)
        {
            var data = Ds.ColumnOf(g.TableId, g.ColumnId);
            var enc = data.Encoded;
            var def = Ds.Model.Table(g.TableId)?.Column(g.ColumnId);
            var rank = new int[enc.Values.Length];
            var sortBy = def?.SortByColumnId == null ? null : Ds.ColumnOf(g.TableId, def.SortByColumnId);
            if (sortBy == null)
            {
                for (int i = 0; i < rank.Length; i++) rank[i] = i;
                return rank;
            }
            var sortCodes = sortBy.Encoded.Codes;
            for (int i = 0; i < rank.Length; i++)
                rank[i] = enc.FirstRow[i] < 0 ? 0 : sortCodes[enc.FirstRow[i]];
            return rank;
        }

        /// <summary>Orders two groups: by the OrderBy keys, then by each group column in display order as a stable tiebreak.</summary>
        private int CompareGroups(QuerySpec spec, Value[][] values, long keyA, long keyB, int a, int b)
        {
            if (spec.OrderBy != null)
            {
                foreach (var o in spec.OrderBy)
                {
                    if (o.Field == null) continue;
                    int cmp = 0;
                    int mi = spec.Measures.FindIndex(x => x.Key == o.Field.Key);
                    if (mi >= 0) cmp = Value.Compare(values[mi][a], values[mi][b]);
                    else
                    {
                        int gi = spec.GroupBy.FindIndex(x => x.Key == o.Field.Key);
                        if (gi >= 0) cmp = CompareColumn(gi, keyA, keyB);
                    }
                    if (cmp != 0) return o.Descending ? -cmp : cmp;
                }
            }
            for (int j = 0; j < _cols.Count; j++)
            {
                int cmp = CompareColumn(j, keyA, keyB);
                if (cmp != 0) return cmp;
            }
            return 0;
        }

        /// <summary>Compares two groups on one group column by display rank.</summary>
        private int CompareColumn(int j, long keyA, long keyB)
        {
            int card = _cols[j].Enc.Values.Length;
            var rank = _cols[j].Rank;
            int ca = (int)(keyA / _mult[j] % card);
            int cb = (int)(keyB / _mult[j] % card);
            return rank[ca].CompareTo(rank[cb]);
        }
    }

    /// <summary>Runs on the thread pool, never on the WPF dispatcher. Identical specs against the
    /// same dataset version come straight from the cache.</summary>
    public sealed class InProcessEngine : IQueryEngine
    {
        // Swapped atomically by Load; queries take one snapshot and keep it for their whole life.
        private Dataset _dataset;
        // Result objects are shared between callers, so visuals must treat a QueryResult as read-only.
        private readonly ConcurrentDictionary<string, QueryResult> _cache = new ConcurrentDictionary<string, QueryResult>();
        // Past this the cache is emptied whole. Results are small, and the mask memos soften the cold start.
        private const int MaxCached = 4000;
        // Strips "Id": "..." from serialized specs. See the note in Execute.
        private static readonly System.Text.RegularExpressions.Regex FilterIds =
            new System.Text.RegularExpressions.Regex("\"Id\":\\s*\"[^\"]*\",?", System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>The current dataset, or null before the first load.</summary>
        public Dataset Dataset => Volatile.Read(ref _dataset);

        /// <summary>Publishes a new dataset. Running queries finish against the old one, which the collector frees afterward.</summary>
        public void Load(Dataset ds)
        {
            Interlocked.Exchange(ref _dataset, ds);
            _cache.Clear();
        }

        /// <inheritdoc/>
        public Task<QueryResult> ExecuteAsync(QuerySpec spec, CancellationToken ct) =>
            Task.Run(() => Execute(spec, ct), ct);

        /// <inheritdoc/>
        public Task<IReadOnlyList<QueryResult>> ExecuteBatchAsync(IReadOnlyList<QuerySpec> specs, CancellationToken ct) =>
            Task.Run<IReadOnlyList<QueryResult>>(() =>
            {
                var results = new QueryResult[specs.Count];
                var options = new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Environment.ProcessorCount };
                try
                {
                    Parallel.For(0, specs.Count, options, i => results[i] = Execute(specs[i], ct));
                }
                catch (AggregateException ex) when (ex.InnerExceptions.All(e => e is OperationCanceledException))
                {
                    throw new OperationCanceledException(ct);
                }
                return results;
            }, ct);

        /// <summary>Answers from the cache or runs the query synchronously. Failures are never cached, so a transient error can clear itself.</summary>
        public QueryResult Execute(QuerySpec spec, CancellationToken ct)
        {
            var ds = Dataset;
            if (ds == null) return QueryResult.Failed("No data loaded");
            // A filter's own id is not part of the question being asked.
            string key = ds.Version + "\n" + FilterIds.Replace(ModelJson.Serialize(spec), "");
            QueryResult cached;
            if (_cache.TryGetValue(key, out cached)) return cached;
            var result = new QueryScope(ds, ct).Execute(spec);
            if (result.Error == null)
            {
                if (_cache.Count >= MaxCached) _cache.Clear();
                _cache[key] = result;
            }
            return result;
        }
    }
}
