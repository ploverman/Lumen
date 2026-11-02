using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Lumen.Model;

namespace Lumen.Engine
{
    /// <summary>The rows of one table: a set of columns, by position and by the stable column id.</summary>
    public sealed class TableData
    {
        /// <summary>Stable table id, as in <see cref="TableDef.Id"/>.</summary>
        public string Id;
        /// <summary>Table name.</summary>
        public string Name;
        /// <summary>Columns in display order.</summary>
        public List<Column> Columns = new List<Column>();
        /// <summary>Columns by stable id.</summary>
        public Dictionary<string, Column> ById = new Dictionary<string, Column>();
        /// <summary>Number of rows.</summary>
        public int RowCount;

        private int[] _allRows;

        /// <summary>Makes an empty table.</summary>
        public TableData(string id, string name) { Id = id; Name = name; }

        /// <summary>Adds a column under a stable id.</summary>
        public void Add(string columnId, Column c)
        {
            Columns.Add(c);
            ById[columnId] = c;
            RowCount = Math.Max(RowCount, c.RowCount);
        }

        /// <summary>The identity row list, built once. Unfiltered scans use it where a filtered scan would use a row list.
        /// The race is harmless: both threads build the same array.</summary>
        public int[] AllRows
        {
            get
            {
                if (_allRows == null)
                {
                    var a = new int[RowCount];
                    for (int i = 0; i < a.Length; i++) a[i] = i;
                    _allRows = a;
                }
                return _allRows;
            }
        }
    }

    /// <summary>Immutable once built: column arrays are never written after load, so queries need no locks.
    /// A refresh builds a new Dataset and swaps it in.</summary>
    public sealed class Dataset
    {
        private static long _nextVersion;

        /// <summary>Unique per instance. It is part of the result cache key, so a refresh invalidates every cached result without any bookkeeping.</summary>
        public readonly long Version = Interlocked.Increment(ref _nextVersion);
        /// <summary>The model this data was bound to.</summary>
        public readonly SemanticModel Model;
        /// <summary>Tables by stable id.</summary>
        public readonly Dictionary<string, TableData> Tables;
        /// <summary>Compiles and caches the measures of this model. Lives here because compiled closures capture column arrays.</summary>
        public readonly LqlCompiler Compiler;

        // Foreign key maps by relationship id, and composed maps by "from|to".
        private readonly ConcurrentDictionary<string, int[]> _fk = new ConcurrentDictionary<string, int[]>();
        private readonly ConcurrentDictionary<string, int[]> _paths = new ConcurrentDictionary<string, int[]>();

        /// <summary>Takes ownership of the tables.</summary>
        public Dataset(SemanticModel model, IEnumerable<TableData> tables)
        {
            Model = model;
            Tables = tables.ToDictionary(t => t.Id);
            Compiler = new LqlCompiler(this);
        }

        /// <summary>Finds a column by ids, or null when a reference dangles.</summary>
        public Column ColumnOf(string tableId, string columnId)
        {
            TableData t;
            Column c;
            return Tables.TryGetValue(tableId, out t) && t.ById.TryGetValue(columnId, out c) ? c : null;
        }

        /// <summary>fkToDimRow: for each row on the many side, the matching row on the one side, or -1.</summary>
        public int[] FkToDimRow(RelationshipDef rel) => _fk.GetOrAdd(rel.Id, _ => BuildFk(rel));

        /// <summary>The many-side rows of each one-side row, in compressed form.</summary>
        internal sealed class FkIndex
        {
            /// <summary>CSR layout: the many-side rows of one-side row d are Rows[Start[d]] up to Rows[Start[d+1]].</summary>
            public int[] Start, Rows;
        }

        private readonly ConcurrentDictionary<string, Lazy<FkIndex>> _fkIndex = new ConcurrentDictionary<string, Lazy<FkIndex>>();

        /// <summary>The inverted foreign key of a relationship. Built on demand, once, because it costs an extra int per fact row.
        /// The Lazy keeps two threads from building it at once.</summary>
        internal FkIndex IndexOf(RelationshipDef rel) =>
            _fkIndex.GetOrAdd(rel.Id, _ => new Lazy<FkIndex>(() => BuildFkIndex(rel))).Value;

        /// <summary>A counting sort: count rows per one-side row, turn counts into offsets, then place the rows. Order within a group stays ascending.</summary>
        private FkIndex BuildFkIndex(RelationshipDef rel)
        {
            var fk = FkToDimRow(rel);
            var start = new int[Tables[rel.ToTableId].RowCount + 1];
            for (int r = 0; r < fk.Length; r++) if (fk[r] >= 0) start[fk[r] + 1]++;
            for (int d = 1; d < start.Length; d++) start[d] += start[d - 1];
            var next = (int[])start.Clone();
            var rows = new int[start[start.Length - 1]];
            for (int r = 0; r < fk.Length; r++) if (fk[r] >= 0) rows[next[fk[r]]++] = r;
            return new FkIndex { Start = start, Rows = rows };
        }

        // Row sets by filter signature. Every visual on a page starts from the same slicers, so sharing
        // these across queries is what keeps a slicer click from rescanning the fact table per visual.
        internal readonly ConcurrentDictionary<string, Lazy<Bitset>> MaskMemo = new ConcurrentDictionary<string, Lazy<Bitset>>();
        internal readonly ConcurrentDictionary<string, Lazy<int[]>> RowMemo = new ConcurrentDictionary<string, Lazy<int[]>>();
        private long _memoBytes;
        private const long MemoLimit = 256L * 1024 * 1024;

        // Group keys are as long as the surviving rows; reusing the buffers spares the large object heap.
        private readonly ConcurrentBag<long[]> _keyBuffers = new ConcurrentBag<long[]>();

        /// <summary>Borrows a group key buffer of at least the given length. Buffers are not cleared, and a too-small one taken from the bag is dropped.</summary>
        internal long[] RentKeys(int length)
        {
            long[] buffer;
            if (_keyBuffers.TryTake(out buffer) && buffer.Length >= length) return buffer;
            return new long[Math.Max(length, 1024)];
        }

        /// <summary>Gives a buffer back. At most four are kept; the rest go to the collector.</summary>
        internal void ReturnKeys(long[] buffer)
        {
            if (_keyBuffers.Count < 4) _keyBuffers.Add(buffer);
        }

        /// <summary>Accounts for memoized bytes. Past the limit both memo tables are dropped whole: a crude policy, but the
        /// next click rebuilds what it needs, and queries already holding a mask keep it alive.</summary>
        internal void NoteMemo(long bytes)
        {
            if (Interlocked.Add(ref _memoBytes, bytes) < MemoLimit) return;
            Interlocked.Exchange(ref _memoBytes, 0);
            MaskMemo.Clear();
            RowMemo.Clear();
        }

        /// <summary>Resolves the foreign key once per distinct value rather than once per row, then fans out through the codes.
        /// Types may differ across the relationship, since <see cref="Column.FindCode"/> coerces.</summary>
        private int[] BuildFk(RelationshipDef rel)
        {
            var from = ColumnOf(rel.FromTableId, rel.FromColumnId);
            var to = ColumnOf(rel.ToTableId, rel.ToColumnId);
            if (from == null || to == null) return new int[0];

            var fromEnc = from.Encoded;
            var toEnc = to.Encoded;
            var codeToRow = new int[fromEnc.Values.Length];
            // BLANK and unmatched keys map to -1. A row with -1 matches no one-side row, so any filter on the dimension drops it.
            codeToRow[0] = -1;
            for (int code = 1; code < codeToRow.Length; code++)
            {
                int dimCode = to.FindCode(fromEnc.Values[code].ToObject());
                codeToRow[code] = dimCode > 0 ? toEnc.FirstRow[dimCode] : -1;
            }

            var fk = new int[from.RowCount];
            var codes = fromEnc.Codes;
            for (int r = 0; r < fk.Length; r++) fk[r] = codeToRow[codes[r]];
            return fk;
        }

        /// <summary>Shortest many-to-one chain of active relationships from one table to another.</summary>
        public List<RelationshipDef> FindPath(string from, string to)
        {
            if (from == to) return new List<RelationshipDef>();
            var seen = new HashSet<string> { from };
            var queue = new Queue<KeyValuePair<string, List<RelationshipDef>>>();
            queue.Enqueue(new KeyValuePair<string, List<RelationshipDef>>(from, new List<RelationshipDef>()));
            while (queue.Count > 0)
            {
                var cur = queue.Dequeue();
                foreach (var rel in Model.ActiveRelationshipsFrom(cur.Key))
                {
                    if (!seen.Add(rel.ToTableId)) continue;
                    var path = new List<RelationshipDef>(cur.Value) { rel };
                    if (rel.ToTableId == to) return path;
                    queue.Enqueue(new KeyValuePair<string, List<RelationshipDef>>(rel.ToTableId, path));
                }
            }
            return null;
        }

        /// <summary>Row of 'to' for every row of 'from', following the chain. Null when from == to.</summary>
        // A path that does not exist caches as an empty map. Callers check Reaches first.
        public int[] RowMap(string from, string to)
        {
            if (from == to) return null;
            return _paths.GetOrAdd(from + "|" + to, _ =>
            {
                var path = FindPath(from, to);
                if (path == null) return new int[0];
                var map = FkToDimRow(path[0]);
                for (int i = 1; i < path.Count; i++)
                {
                    var next = FkToDimRow(path[i]);
                    var composed = new int[map.Length];
                    for (int r = 0; r < map.Length; r++) composed[r] = map[r] < 0 ? -1 : next[map[r]];
                    map = composed;
                }
                return map;
            });
        }

        /// <summary>True when filters on <paramref name="to"/> can reach <paramref name="from"/>: they are the same table, or a many-to-one path exists.</summary>
        public bool Reaches(string from, string to) => from == to || FindPath(from, to) != null;

        /// <summary>A table and everything on its one side: the DAX "expanded table".</summary>
        public HashSet<string> Expanded(string table)
        {
            var seen = new HashSet<string> { table };
            var stack = new Stack<string>();
            stack.Push(table);
            while (stack.Count > 0)
                foreach (var rel in Model.ActiveRelationshipsFrom(stack.Pop()))
                    if (seen.Add(rel.ToTableId)) stack.Push(rel.ToTableId);
            return seen;
        }
    }
}
