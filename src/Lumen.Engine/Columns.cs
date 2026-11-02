using System;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using Lumen.Model;

namespace Lumen.Engine
{
    /// <summary>Data-parallel loops over row ranges. Chunks have a fixed size, so a floating-point sum
    /// comes out the same on every run, and it is a multiple of 64, so no two chunks share a Bitset word.</summary>
    public static class Par
    {
        /// <summary>Rows per chunk: 65,536. Loads of at most two chunks stay on the calling thread, since a task switch would cost more than the work.</summary>
        public const int Chunk = 1 << 16;

        /// <summary>Runs <paramref name="body"/> over half-open row ranges covering 0 to <paramref name="n"/>, in parallel when worthwhile.
        /// Writes that fall in different ranges cannot race, even on a Bitset.</summary>
        public static void Ranges(int n, Action<int, int> body)
        {
            if (n <= Chunk * 2) { body(0, n); return; }
            int chunks = (n + Chunk - 1) / Chunk;
            Parallel.For(0, chunks, i => body(i * Chunk, Math.Min(n, (i + 1) * Chunk)));
        }

        /// <summary>Sums per chunk and adds the parts in chunk order. Floating-point addition is not associative,
        /// so summing as threads finish would change the low bits from run to run and break repeatable totals.</summary>
        public static double Sum(int n, Func<int, int, double> body)
        {
            if (n <= Chunk * 2) return body(0, n);
            int chunks = (n + Chunk - 1) / Chunk;
            var parts = new double[chunks];
            Parallel.For(0, chunks, i => parts[i] = body(i * Chunk, Math.Min(n, (i + 1) * Chunk)));
            double sum = 0;
            for (int i = 0; i < chunks; i++) sum += parts[i];
            return sum;
        }
    }

    /// <summary>A fixed-length set of ints in packed 64-bit words. Filter contexts use it twice over: as a set of
    /// dictionary codes (<see cref="ColFilter"/>) and as a set of rows (<see cref="RowFilter"/>, row masks).</summary>
    public sealed class Bitset
    {
        /// <summary>The storage, public so that hot loops can test whole words. Bits past <see cref="Length"/> stay zero, which <see cref="Count"/> and <see cref="ContentHash"/> rely on.</summary>
        public readonly ulong[] Words;
        /// <summary>Number of addressable bits.</summary>
        public readonly int Length;

        /// <summary>Makes an all-clear set.</summary>
        public Bitset(int length)
        {
            Length = length;
            Words = new ulong[(length + 63) / 64];
        }

        /// <summary>Tests a bit. No bounds check beyond the array's.</summary>
        public bool this[int i]
        {
            get { return (Words[i >> 6] & (1UL << (i & 63))) != 0; }
        }

        /// <summary>Sets a bit. Not atomic: concurrent writers must stay in separate 64-bit words, which <see cref="Par"/> chunks guarantee.</summary>
        public void Set(int i) { Words[i >> 6] |= 1UL << (i & 63); }
        /// <summary>Clears a bit. Same threading rule as <see cref="Set"/>.</summary>
        public void Clear(int i) { Words[i >> 6] &= ~(1UL << (i & 63)); }

        /// <summary>Makes a set with every bit below <paramref name="length"/> on.</summary>
        public static Bitset AllSet(int length)
        {
            var b = new Bitset(length);
            for (int i = 0; i < b.Words.Length; i++) b.Words[i] = ulong.MaxValue;
            // Keep the unused high bits of the last word clear, or Count would overcount.
            int rem = length & 63;
            if (rem != 0) b.Words[b.Words.Length - 1] = (1UL << rem) - 1;
            return b;
        }

        /// <summary>Copies the set. Masks shared through the memo tables are never mutated, so a caller who wants to narrow one clones it first.</summary>
        public Bitset Clone()
        {
            var b = new Bitset(Length);
            Array.Copy(Words, b.Words, Words.Length);
            return b;
        }

        /// <summary>Intersects in place. Both sets must have the same length.</summary>
        public void And(Bitset other)
        {
            for (int i = 0; i < Words.Length; i++) Words[i] &= other.Words[i];
        }

        /// <summary>Population count, by Kernighan's method. .NET Framework 4.7 has no BitOperations.PopCount.</summary>
        public int Count()
        {
            int n = 0;
            foreach (var w0 in Words)
            {
                ulong w = w0;
                while (w != 0) { w &= w - 1; n++; }
            }
            return n;
        }

        /// <summary>The set bits as an ascending int array, which is what the scan loops take.</summary>
        public int[] ToRows()
        {
            var rows = new int[Count()];
            int n = 0;
            for (int i = 0; i < Words.Length; i++)
            {
                ulong w = Words[i];
                while (w != 0)
                {
                    int bit = TrailingZeros(w);
                    rows[n++] = (i << 6) + bit;
                    // Clears the lowest set bit.
                    w &= w - 1;
                }
            }
            return rows;
        }

        /// <summary>Index of the lowest set bit, by binary search. Assumes <paramref name="w"/> is non-zero.</summary>
        private static int TrailingZeros(ulong w)
        {
            int n = 0;
            if ((w & 0xFFFFFFFF) == 0) { n += 32; w >>= 32; }
            if ((w & 0xFFFF) == 0) { n += 16; w >>= 16; }
            if ((w & 0xFF) == 0) { n += 8; w >>= 8; }
            if ((w & 0xF) == 0) { n += 4; w >>= 4; }
            if ((w & 3) == 0) { n += 2; w >>= 2; }
            if ((w & 1) == 0) n += 1;
            return n;
        }

        /// <summary>64-bit FNV-1a style hash over the words, used in filter signatures. A collision would make two filters share a
        /// memoized row set; at this width that is treated as impossible.</summary>
        public long ContentHash()
        {
            long h = -3750763034362895579L;
            foreach (var w in Words) h = unchecked((h ^ (long)w) * 1099511628211L);
            return h;
        }
    }

    /// <summary>Dictionary view of a column. Code 0 is BLANK; codes ascend in value order, so
    /// code order is sort order. Every filter and group-by works on this, whatever the storage type.</summary>
    public sealed class Encoding
    {
        /// <summary>Dictionary code per row.</summary>
        public int[] Codes;
        /// <summary>The value of each code, ascending, with Values[0] left BLANK.</summary>
        public Value[] Values;
        /// <summary>A representative row for each code.</summary>
        public int[] FirstRow;     // a representative row per code, -1 for an unused BLANK
    }

    /// <summary>One immutable column of a table. Subclasses hold typed arrays; everything generic goes through
    /// <see cref="Get"/> (slow, per row) or <see cref="Encoded"/> (fast, built once on first use).</summary>
    public abstract class Column
    {
        /// <summary>Column name as loaded.</summary>
        public string Name;
        /// <summary>Number of rows.</summary>
        public int RowCount;
        /// <summary>The declared data type.</summary>
        public abstract LumenDataType Type { get; }
        /// <summary>Reads one cell. Use array access in hot paths.</summary>
        public abstract Value Get(int row);
        /// <summary>True when the cell is BLANK.</summary>
        public abstract bool IsNull(int row);

        private Encoding _encoding;

        /// <summary>The dictionary view, built lazily. Two threads may both build it; CompareExchange keeps the first and the other is dropped,
        /// which is cheaper than holding a lock over a scan of millions of rows.</summary>
        public Encoding Encoded
        {
            get
            {
                var e = Volatile.Read(ref _encoding);
                if (e != null) return e;
                e = BuildEncoding();
                Interlocked.CompareExchange(ref _encoding, e, null);
                return Volatile.Read(ref _encoding);
            }
        }

        /// <summary>Builds the dictionary from the typed storage.</summary>
        protected abstract Encoding BuildEncoding();

        /// <summary>Records the first row of each code. The loop runs backward so that the earliest row wins; relationships use it to pick a row on the one side.</summary>
        protected static void FillFirstRows(Encoding e)
        {
            e.FirstRow = new int[e.Values.Length];
            for (int i = 0; i < e.FirstRow.Length; i++) e.FirstRow[i] = -1;
            for (int r = e.Codes.Length - 1; r >= 0; r--) e.FirstRow[e.Codes[r]] = r;
        }

        /// <summary>Finds the code of a filter value, tolerating JSON round trips (long vs double, string dates).</summary>
        public int FindCode(object value)
        {
            var enc = Encoded;
            var v = Coerce(Value.FromObject(value), Type);
            if (v.IsBlank) return 0;
            int lo = 1, hi = enc.Values.Length - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                int c = OrdinalCompare(enc.Values[mid], v);
                if (c == 0) return mid;
                if (c < 0) lo = mid + 1; else hi = mid - 1;
            }
            return -1;
        }

        /// <summary>Compares in dictionary order: strings ignore case, everything else follows <see cref="Value.Compare"/>.</summary>
        internal static int OrdinalCompare(Value a, Value b)
        {
            if (a.Kind == ValueKind.String && b.Kind == ValueKind.String)
                return string.Compare(a.String, b.String, StringComparison.OrdinalIgnoreCase);
            return Value.Compare(a, b);
        }

        /// <summary>Converts a value to a column type, invariant culture throughout. Anything that does not convert becomes BLANK rather than an error,
        /// which is how a bad cell in a Change type step ends up empty instead of failing the load.</summary>
        public static Value Coerce(Value v, LumenDataType t)
        {
            if (v.IsBlank) return v;
            switch (t)
            {
                case LumenDataType.String:
                    return v.Kind == ValueKind.String ? v : Value.FromString(ToInvariantString(v));
                case LumenDataType.Int64:
                    if (v.Kind == ValueKind.Integer) return v;
                    if (v.Kind == ValueKind.Number) return Value.FromInteger((long)Math.Round(v.Number));
                    if (v.Kind == ValueKind.Boolean) return Value.FromInteger(v.Integer);
                    if (v.Kind == ValueKind.Date) return Value.FromInteger((long)v.AsDouble());
                    long l;
                    return long.TryParse(v.String.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out l) ? Value.FromInteger(l) : Value.Blank;
                case LumenDataType.Double:
                case LumenDataType.Decimal:
                    if (v.Kind == ValueKind.Number) return v;
                    if (v.Kind == ValueKind.String)
                    {
                        double d;
                        return double.TryParse(v.String.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out d) ? Value.FromNumber(d) : Value.Blank;
                    }
                    return Value.FromNumber(v.AsDouble());
                case LumenDataType.Boolean:
                    if (v.Kind == ValueKind.Boolean) return v;
                    if (v.Kind == ValueKind.String)
                    {
                        bool b;
                        return bool.TryParse(v.String.Trim(), out b) ? Value.FromBoolean(b) : Value.Blank;
                    }
                    return Value.FromBoolean(v.AsBool());
                case LumenDataType.Date:
                case LumenDataType.DateTime:
                    if (v.Kind == ValueKind.Date)
                        return t == LumenDataType.Date ? Value.FromDate(v.AsDate.Date) : v;
                    if (v.Kind == ValueKind.String)
                    {
                        DateTime dt;
                        if (DateTime.TryParse(v.String.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.None, out dt))
                            return Value.FromDate(t == LumenDataType.Date ? dt.Date : dt);
                        return Value.Blank;
                    }
                    if (v.IsNumeric) return Value.FromDate(DateTime.FromOADate(v.AsDouble()));
                    return Value.Blank;
            }
            return v;
        }

        /// <summary>Text form for a string column. "R" keeps a double's full precision.</summary>
        private static string ToInvariantString(Value v)
        {
            switch (v.Kind)
            {
                case ValueKind.Number: return v.Number.ToString("R", CultureInfo.InvariantCulture);
                case ValueKind.Integer: return v.Integer.ToString(CultureInfo.InvariantCulture);
                default: return v.ToDisplayString();
            }
        }

        /// <summary>A new column of the given rows, in that order. Used by transforms that drop or reorder rows.</summary>
        public abstract Column Take(int[] rows);
        /// <summary>The cells as doubles. Double columns return their own array, so callers must not write to it.</summary>
        public abstract double[] AsDoubles();  // BLANK as 0, used by SUM-style fast paths
        /// <summary>Writes the payload for <see cref="ColumnIO"/>.</summary>
        public abstract void Write(BinaryWriter w);
    }

    /// <summary>Number and Decimal columns. Decimal is stored as double too, trading decimal exactness for tight loops over double arrays.</summary>
    public sealed class DoubleColumn : Column
    {
        /// <summary>The cells. A BLANK cell holds 0, so a plain sum over this array is already right.</summary>
        public double[] Values;
        /// <summary>One bit per row, set for BLANK. Null when the column has no blanks, which keeps <see cref="IsNull"/> cheap.</summary>
        public ulong[] NullBits;
        /// <summary>Double or Decimal.</summary>
        public LumenDataType Declared = LumenDataType.Double;

        /// <inheritdoc/>
        public override LumenDataType Type => Declared;
        /// <inheritdoc/>
        public override bool IsNull(int r) => NullBits != null && (NullBits[r >> 6] & (1UL << (r & 63))) != 0;
        /// <inheritdoc/>
        public override Value Get(int r) => IsNull(r) ? Value.Blank : Value.FromNumber(Values[r]);
        /// <inheritdoc/>
        public override double[] AsDoubles() => Values;

        // Distinct values, sorted, then numbered from 1. The dictionaries are local, since after this only arrays are read.
        /// <inheritdoc/>
        protected override Encoding BuildEncoding()
        {
            var distinct = new HashSet<double>();
            for (int r = 0; r < RowCount; r++) if (!IsNull(r)) distinct.Add(Values[r]);
            var sorted = distinct.ToArray();
            Array.Sort(sorted);
            var map = new Dictionary<double, int>(sorted.Length);
            var vals = new Value[sorted.Length + 1];
            for (int i = 0; i < sorted.Length; i++) { map[sorted[i]] = i + 1; vals[i + 1] = Value.FromNumber(sorted[i]); }
            var codes = new int[RowCount];
            for (int r = 0; r < RowCount; r++) codes[r] = IsNull(r) ? 0 : map[Values[r]];
            var e = new Encoding { Codes = codes, Values = vals };
            FillFirstRows(e);
            return e;
        }

        /// <inheritdoc/>
        public override Column Take(int[] rows)
        {
            var b = new DoubleBuilder(rows.Length);
            b.Declared = Declared;
            foreach (var r in rows) { if (IsNull(r)) b.AddNull(); else b.Add(Values[r]); }
            return b.Finish(Name);
        }

        /// <inheritdoc/>
        public override void Write(BinaryWriter w)
        {
            ColumnIO.WriteNulls(w, NullBits);
            for (int i = 0; i < RowCount; i++) w.Write(Values[i]);
        }
    }

    /// <summary>Whole number columns.</summary>
    public sealed class Int64Column : Column
    {
        /// <summary>The cells. A BLANK cell holds 0.</summary>
        public long[] Values;
        /// <summary>One bit per row, set for BLANK. Null when the column has no blanks.</summary>
        public ulong[] NullBits;

        /// <inheritdoc/>
        public override LumenDataType Type => LumenDataType.Int64;
        /// <inheritdoc/>
        public override bool IsNull(int r) => NullBits != null && (NullBits[r >> 6] & (1UL << (r & 63))) != 0;
        /// <inheritdoc/>
        public override Value Get(int r) => IsNull(r) ? Value.Blank : Value.FromInteger(Values[r]);

        /// <inheritdoc/>
        public override double[] AsDoubles()
        {
            var d = new double[RowCount];
            for (int i = 0; i < d.Length; i++) d[i] = Values[i];
            return d;
        }

        /// <inheritdoc/>
        protected override Encoding BuildEncoding()
        {
            var distinct = new HashSet<long>();
            for (int r = 0; r < RowCount; r++) if (!IsNull(r)) distinct.Add(Values[r]);
            var sorted = distinct.ToArray();
            Array.Sort(sorted);
            var map = new Dictionary<long, int>(sorted.Length);
            var vals = new Value[sorted.Length + 1];
            for (int i = 0; i < sorted.Length; i++) { map[sorted[i]] = i + 1; vals[i + 1] = Value.FromInteger(sorted[i]); }
            var codes = new int[RowCount];
            for (int r = 0; r < RowCount; r++) codes[r] = IsNull(r) ? 0 : map[Values[r]];
            var e = new Encoding { Codes = codes, Values = vals };
            FillFirstRows(e);
            return e;
        }

        /// <inheritdoc/>
        public override Column Take(int[] rows)
        {
            var b = new Int64Builder(rows.Length);
            foreach (var r in rows) { if (IsNull(r)) b.AddNull(); else b.Add(Values[r]); }
            return b.Finish(Name);
        }

        /// <inheritdoc/>
        public override void Write(BinaryWriter w)
        {
            ColumnIO.WriteNulls(w, NullBits);
            for (int i = 0; i < RowCount; i++) w.Write(Values[i]);
        }
    }

    /// <summary>Date and DateTime. Stores ticks rather than the PRD's days-since-epoch so time of day survives.</summary>
    public sealed class DateColumn : Column
    {
        /// <summary>DateTime.Ticks of each cell.</summary>
        public long[] Ticks;
        /// <summary>One bit per row, set for BLANK. Null when the column has no blanks.</summary>
        public ulong[] NullBits;
        /// <summary>Date or DateTime.</summary>
        public LumenDataType Declared = LumenDataType.Date;

        /// <inheritdoc/>
        public override LumenDataType Type => Declared;
        /// <inheritdoc/>
        public override bool IsNull(int r) => NullBits != null && (NullBits[r >> 6] & (1UL << (r & 63))) != 0;
        /// <inheritdoc/>
        public override Value Get(int r) => IsNull(r) ? Value.Blank : Value.FromDateTicks(Ticks[r]);

        // OLE automation dates, so dates and numbers mix in arithmetic as they do in DAX.
        /// <inheritdoc/>
        public override double[] AsDoubles()
        {
            var d = new double[RowCount];
            for (int i = 0; i < d.Length; i++) d[i] = IsNull(i) ? 0 : new DateTime(Ticks[i]).ToOADate();
            return d;
        }

        /// <inheritdoc/>
        protected override Encoding BuildEncoding()
        {
            var distinct = new HashSet<long>();
            for (int r = 0; r < RowCount; r++) if (!IsNull(r)) distinct.Add(Ticks[r]);
            var sorted = distinct.ToArray();
            Array.Sort(sorted);
            var map = new Dictionary<long, int>(sorted.Length);
            var vals = new Value[sorted.Length + 1];
            for (int i = 0; i < sorted.Length; i++) { map[sorted[i]] = i + 1; vals[i + 1] = Value.FromDateTicks(sorted[i]); }
            var codes = new int[RowCount];
            for (int r = 0; r < RowCount; r++) codes[r] = IsNull(r) ? 0 : map[Ticks[r]];
            var e = new Encoding { Codes = codes, Values = vals };
            FillFirstRows(e);
            return e;
        }

        /// <inheritdoc/>
        public override Column Take(int[] rows)
        {
            var b = new DateBuilder(rows.Length) { Declared = Declared };
            foreach (var r in rows) { if (IsNull(r)) b.AddNull(); else b.Add(Ticks[r]); }
            return b.Finish(Name);
        }

        /// <inheritdoc/>
        public override void Write(BinaryWriter w)
        {
            ColumnIO.WriteNulls(w, NullBits);
            for (int i = 0; i < RowCount; i++) w.Write(Ticks[i]);
        }
    }

    /// <summary>Strings and booleans: per-row code into a sorted dictionary, as VertiPaq does.</summary>
    public sealed class DictColumn : Column
    {
        /// <summary>Dictionary code per row. Shared with <see cref="Encoding.Codes"/>, so strings pay no second encoding.</summary>
        public int[] Codes;
        /// <summary>Sorted distinct values, with BLANK at index 0.</summary>
        public Value[] Dictionary;
        /// <summary>String or Boolean.</summary>
        public LumenDataType Declared = LumenDataType.String;

        /// <inheritdoc/>
        public override LumenDataType Type => Declared;
        /// <inheritdoc/>
        public override bool IsNull(int r) => Codes[r] == 0;
        /// <inheritdoc/>
        public override Value Get(int r) => Dictionary[Codes[r]];

        /// <inheritdoc/>
        public override double[] AsDoubles()
        {
            var d = new double[RowCount];
            for (int i = 0; i < d.Length; i++) d[i] = Dictionary[Codes[i]].AsDouble();
            return d;
        }

        /// <inheritdoc/>
        protected override Encoding BuildEncoding()
        {
            var e = new Encoding { Codes = Codes, Values = Dictionary };
            FillFirstRows(e);
            return e;
        }

        // The dictionary is shared and may keep codes the new column no longer uses. Filters tolerate that; FirstRow is -1 for them.
        /// <inheritdoc/>
        public override Column Take(int[] rows)
        {
            var codes = new int[rows.Length];
            for (int i = 0; i < rows.Length; i++) codes[i] = Codes[rows[i]];
            return new DictColumn { Name = Name, RowCount = rows.Length, Codes = codes, Dictionary = Dictionary, Declared = Declared };
        }

        // Entry 0 is implied BLANK, so the length is written but the entry is not.
        /// <inheritdoc/>
        public override void Write(BinaryWriter w)
        {
            w.Write(Dictionary.Length);
            for (int i = 1; i < Dictionary.Length; i++)
            {
                if (Declared == LumenDataType.Boolean) w.Write(Dictionary[i].AsBool());
                else w.Write(Dictionary[i].String ?? "");
            }
            for (int i = 0; i < RowCount; i++) w.Write(Codes[i]);
        }
    }

    /// <summary>Grows a column one value at a time during load, then freezes it.</summary>
    public abstract class ColumnBuilder
    {
        /// <summary>Appends a value, coercing to the column type.</summary>
        public abstract void AddValue(Value v);
        /// <summary>Freezes the builder into a column. The builder must not be used afterward; it hands over its arrays.</summary>
        public abstract Column Finish(string name);

        /// <summary>Picks the builder for a data type.</summary>
        public static ColumnBuilder Create(LumenDataType t, int capacity = 256)
        {
            switch (t)
            {
                case LumenDataType.Int64: return new Int64Builder(capacity);
                case LumenDataType.Double:
                case LumenDataType.Decimal: return new DoubleBuilder(capacity) { Declared = t };
                case LumenDataType.Date:
                case LumenDataType.DateTime: return new DateBuilder(capacity) { Declared = t };
                case LumenDataType.Boolean: return new DictBuilder { Declared = t };
                default: return new DictBuilder();
            }
        }

        /// <summary>Sets a null bit, allocating the bitmap only on the first blank, so a clean column stays bitmap-free.</summary>
        protected static void SetNull(ref ulong[] bits, int row, int capacity)
        {
            int need = (capacity + 63) / 64;
            if (bits == null) bits = new ulong[Math.Max(need, 1)];
            else if (bits.Length < need) Array.Resize(ref bits, need * 2);
            bits[row >> 6] |= 1UL << (row & 63);
        }
    }

    /// <summary>Builds a <see cref="DoubleColumn"/>.</summary>
    public sealed class DoubleBuilder : ColumnBuilder
    {
        private double[] _v;
        private ulong[] _n;
        private int _count;
        /// <summary>Double or Decimal.</summary>
        public LumenDataType Declared = LumenDataType.Double;

        /// <summary>Sizes the initial array.</summary>
        public DoubleBuilder(int capacity) { _v = new double[Math.Max(capacity, 4)]; }
        private void Grow() { if (_count == _v.Length) Array.Resize(ref _v, _v.Length * 2); }
        /// <summary>Appends a number.</summary>
        public void Add(double v) { Grow(); _v[_count++] = v; }
        /// <summary>Appends a BLANK.</summary>
        public void AddNull() { Grow(); SetNull(ref _n, _count, _count + 1); _count++; }
        /// <inheritdoc/>
        public override void AddValue(Value v) { if (v.IsBlank) AddNull(); else Add(v.AsDouble()); }

        /// <inheritdoc/>
        public override Column Finish(string name)
        {
            Array.Resize(ref _v, _count);
            return new DoubleColumn { Name = name, RowCount = _count, Values = _v, NullBits = _n, Declared = Declared };
        }
    }

    /// <summary>Builds an <see cref="Int64Column"/>.</summary>
    public sealed class Int64Builder : ColumnBuilder
    {
        private long[] _v;
        private ulong[] _n;
        private int _count;

        /// <summary>Sizes the initial array.</summary>
        public Int64Builder(int capacity) { _v = new long[Math.Max(capacity, 4)]; }
        private void Grow() { if (_count == _v.Length) Array.Resize(ref _v, _v.Length * 2); }
        /// <summary>Appends a number.</summary>
        public void Add(long v) { Grow(); _v[_count++] = v; }
        /// <summary>Appends a BLANK.</summary>
        public void AddNull() { Grow(); SetNull(ref _n, _count, _count + 1); _count++; }

        /// <inheritdoc/>
        public override void AddValue(Value v)
        {
            if (v.IsBlank) { AddNull(); return; }
            var c = Column.Coerce(v, LumenDataType.Int64);
            if (c.IsBlank) AddNull(); else Add(c.Integer);
        }

        /// <inheritdoc/>
        public override Column Finish(string name)
        {
            Array.Resize(ref _v, _count);
            return new Int64Column { Name = name, RowCount = _count, Values = _v, NullBits = _n };
        }
    }

    /// <summary>Builds a <see cref="DateColumn"/>.</summary>
    public sealed class DateBuilder : ColumnBuilder
    {
        private long[] _v;
        private ulong[] _n;
        private int _count;
        /// <summary>Date or DateTime.</summary>
        public LumenDataType Declared = LumenDataType.Date;

        /// <summary>Sizes the initial array.</summary>
        public DateBuilder(int capacity) { _v = new long[Math.Max(capacity, 4)]; }
        private void Grow() { if (_count == _v.Length) Array.Resize(ref _v, _v.Length * 2); }
        /// <summary>Appends a date as ticks.</summary>
        public void Add(long ticks) { Grow(); _v[_count++] = ticks; }
        /// <summary>Appends a BLANK.</summary>
        public void AddNull() { Grow(); SetNull(ref _n, _count, _count + 1); _count++; }

        /// <inheritdoc/>
        public override void AddValue(Value v)
        {
            var c = Column.Coerce(v, Declared);
            if (c.IsBlank) AddNull(); else Add(c.Integer);
        }

        /// <inheritdoc/>
        public override Column Finish(string name)
        {
            Array.Resize(ref _v, _count);
            return new DateColumn { Name = name, RowCount = _count, Ticks = _v, NullBits = _n, Declared = Declared };
        }
    }

    /// <summary>Builds a <see cref="DictColumn"/>. Strings are interned as they arrive and sorted once at the end.</summary>
    public sealed class DictBuilder : ColumnBuilder
    {
        private readonly Dictionary<string, int> _map = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly List<string> _values = new List<string>();
        private int[] _codes = new int[256];
        private int _count;
        /// <summary>String or Boolean.</summary>
        public LumenDataType Declared = LumenDataType.String;

        /// <summary>Rows added so far.</summary>
        public int Count => _count;

        /// <summary>Appends a string; null means BLANK. Codes here are in arrival order until <see cref="Sorted"/> remaps them.</summary>
        public void Add(string s)
        {
            if (_count == _codes.Length) Array.Resize(ref _codes, _codes.Length * 2);
            if (s == null) { _codes[_count++] = 0; return; }
            int code;
            if (!_map.TryGetValue(s, out code))
            {
                _values.Add(s);
                code = _values.Count;
                _map.Add(s, code);
            }
            _codes[_count++] = code;
        }

        // Booleans travel as "True"/"False" so that one code path handles both types, and False sorts before True.
        /// <inheritdoc/>
        public override void AddValue(Value v)
        {
            if (Declared == LumenDataType.Boolean)
            {
                var c = Column.Coerce(v, LumenDataType.Boolean);
                Add(c.IsBlank ? null : c.AsBool() ? "True" : "False");
            }
            else Add(v.IsBlank ? null : Column.Coerce(v, LumenDataType.String).String);
        }

        /// <inheritdoc/>
        public override Column Finish(string name)
        {
            Array.Resize(ref _codes, _count);
            return Sorted(name, _codes, _values, Declared);
        }

        /// <summary>Sorts the dictionary and remaps codes, so that code order is sort order.</summary>
        public static DictColumn Sorted(string name, int[] codes, List<string> values, LumenDataType type)
        {
            var order = Enumerable.Range(0, values.Count).ToArray();
            var keys = values.ToArray();
            Array.Sort(keys, order, StringComparer.OrdinalIgnoreCase);
            var remap = new int[values.Count + 1];
            var dict = new Value[values.Count + 1];
            for (int i = 0; i < order.Length; i++)
            {
                remap[order[i] + 1] = i + 1;
                dict[i + 1] = type == LumenDataType.Boolean
                    ? Value.FromBoolean(keys[i] == "True")
                    : Value.FromString(keys[i]);
            }
            for (int r = 0; r < codes.Length; r++) codes[r] = remap[codes[r]];
            return new DictColumn { Name = name, RowCount = codes.Length, Codes = codes, Dictionary = dict, Declared = type };
        }
    }

    /// <summary>Binary column snapshots for the .lumen package.</summary>
    public static class ColumnIO
    {
        /// <summary>Writes a word count and the null bitmap. Zero means no bitmap.</summary>
        internal static void WriteNulls(BinaryWriter w, ulong[] bits)
        {
            w.Write(bits == null ? 0 : bits.Length);
            if (bits != null) foreach (var b in bits) w.Write(b);
        }

        /// <summary>Reverses <see cref="WriteNulls"/>.</summary>
        private static ulong[] ReadNulls(BinaryReader r)
        {
            int n = r.ReadInt32();
            if (n == 0) return null;
            var b = new ulong[n];
            for (int i = 0; i < n; i++) b[i] = r.ReadUInt64();
            return b;
        }

        /// <summary>Writes a column: name, type as int, row count, then its payload.</summary>
        public static void WriteColumn(BinaryWriter w, Column c)
        {
            w.Write(c.Name);
            w.Write((int)c.Type);
            w.Write(c.RowCount);
            c.Write(w);
        }

        /// <summary>Reads a column written by <see cref="WriteColumn"/>. The enum's numeric value is on disk, so <see cref="LumenDataType"/> members must not be reordered.</summary>
        public static Column ReadColumn(BinaryReader r)
        {
            string name = r.ReadString();
            var type = (LumenDataType)r.ReadInt32();
            int rows = r.ReadInt32();
            switch (type)
            {
                case LumenDataType.Int64:
                {
                    var nulls = ReadNulls(r);
                    var v = new long[rows];
                    for (int i = 0; i < rows; i++) v[i] = r.ReadInt64();
                    return new Int64Column { Name = name, RowCount = rows, Values = v, NullBits = nulls };
                }
                case LumenDataType.Double:
                case LumenDataType.Decimal:
                {
                    var nulls = ReadNulls(r);
                    var v = new double[rows];
                    for (int i = 0; i < rows; i++) v[i] = r.ReadDouble();
                    return new DoubleColumn { Name = name, RowCount = rows, Values = v, NullBits = nulls, Declared = type };
                }
                case LumenDataType.Date:
                case LumenDataType.DateTime:
                {
                    var nulls = ReadNulls(r);
                    var v = new long[rows];
                    for (int i = 0; i < rows; i++) v[i] = r.ReadInt64();
                    return new DateColumn { Name = name, RowCount = rows, Ticks = v, NullBits = nulls, Declared = type };
                }
                default:
                {
                    int dictLen = r.ReadInt32();
                    var dict = new Value[dictLen];
                    for (int i = 1; i < dictLen; i++)
                        dict[i] = type == LumenDataType.Boolean ? Value.FromBoolean(r.ReadBoolean()) : Value.FromString(r.ReadString());
                    var codes = new int[rows];
                    for (int i = 0; i < rows; i++) codes[i] = r.ReadInt32();
                    return new DictColumn { Name = name, RowCount = rows, Codes = codes, Dictionary = dict, Declared = type };
                }
            }
        }
    }
}
