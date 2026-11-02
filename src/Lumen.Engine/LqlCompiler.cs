using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Lumen.Model;

namespace Lumen.Engine
{
    /// <summary>A compiled expression. Closures nest, so one measure is a tree of these. They hold no per-query state, so one instance serves many threads.</summary>
    public delegate Value Eval(EvalCtx c);
    /// <summary>A compiled per-row kernel on raw doubles; see <see cref="LqlCompiler"/>.TryKernel.</summary>
    internal delegate double RowFn(int row);
    /// <summary>A compiled table expression: returns row numbers.</summary>
    internal delegate int[] TableEval(EvalCtx c);
    /// <summary>A CALCULATE argument: returns the changed filter context. Evaluated against the outer context.</summary>
    internal delegate FilterContext Modifier(EvalCtx outer, FilterContext f);

    /// <summary>A table function compiled for both of its uses: as rows to iterate, and as a CALCULATE filter.</summary>
    internal sealed class CompiledTable
    {
        /// <summary>Table the rows belong to.</summary>
        public string TableId;
        public string ColumnId;      // set when the table is the distinct values of one column
        /// <summary>Rows to iterate. One representative per value when <see cref="ColumnId"/> is set.</summary>
        public TableEval Rows;
        /// <summary>Effect when passed to CALCULATE.</summary>
        public Modifier Modify;
    }

    /// <summary>Evaluation context: filter context, row context chain, variables.</summary>
    public sealed class EvalCtx
    {
        /// <summary>The running query.</summary>
        public readonly QueryScope Q;
        // Null inside a group until first asked for: see F.
        private FilterContext _f;
        // Group number, or -1 when the context is not simply "this group".
        private readonly int _group;
        // The enclosing context, so row contexts and variables resolve through the chain.
        private readonly EvalCtx _outer;

        private string _rowTable;
        private int _row;
        private Dictionary<string, Value> _vars;

        /// <summary>Makes a context. <paramref name="group"/> enables the partition fast path in <see cref="Rows"/>.</summary>
        public EvalCtx(QueryScope q, FilterContext f, int group = -1, EvalCtx outer = null)
        {
            Q = q; _f = f; _group = group; _outer = outer;
        }

        /// <summary>The filter context, built on demand. Plain aggregates in a group never call this, which avoids a Bitset per group.</summary>
        public FilterContext F => _f ?? (_f = Q.GroupFilter(_group));

        /// <summary>Rows of a table in the current filter context.</summary>
        public int[] Rows(string tableId) =>
            _group >= 0 ? Q.GroupRows(tableId, _group) : Q.Rows(F, tableId);

        /// <summary>A child under a different filter context. Group is reset to -1, since the partition no longer applies.
        /// Row contexts stay reachable through the chain, which is why CALCULATE does not perform context transition: an aggregate inside still sees only the filter context.</summary>
        public EvalCtx WithFilter(FilterContext f) => new EvalCtx(Q, f, -1, this);

        /// <summary>A child for iterating a table. The caller moves it along with <see cref="SetRow"/>, so a scan allocates one context, not one per row.</summary>
        public EvalCtx Iterator(string tableId)
        {
            var c = new EvalCtx(Q, _f, _group, this);
            c._rowTable = tableId;
            return c;
        }

        /// <summary>Moves an iterator to a row.</summary>
        public void SetRow(int row) { _row = row; }

        /// <summary>A child holding VAR values.</summary>
        public EvalCtx WithVars(Dictionary<string, Value> vars)
        {
            var c = new EvalCtx(Q, _f, _group, this);
            c._vars = vars;
            return c;
        }

        /// <summary>Finds the current row of a table, innermost first.</summary>
        public bool TryRow(string tableId, out int row)
        {
            for (var c = this; c != null; c = c._outer)
            {
                if (c._rowTable == tableId) { row = c._row; return true; }
            }
            row = -1;
            return false;
        }

        /// <summary>The nearest row context of any table, for RELATED.</summary>
        public string InnermostRowTable(out int row)
        {
            for (var c = this; c != null; c = c._outer)
            {
                if (c._rowTable != null) { row = c._row; return c._rowTable; }
            }
            row = -1;
            return null;
        }

        /// <summary>Reads a variable, innermost first. The binder has already checked it exists.</summary>
        public Value Var(string name)
        {
            for (var c = this; c != null; c = c._outer)
            {
                Value v;
                if (c._vars != null && c._vars.TryGetValue(name, out v)) return v;
            }
            return Value.Blank;
        }
    }

    /// <summary>Compile-time environment: which variables and row contexts are in view, plus the source for error spans.
    /// The With methods copy, since a scope only applies to part of the tree.</summary>
    internal sealed class Scope
    {
        public HashSet<string> Vars = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public List<string> RowTables = new List<string>();
        public string Source;

        public Scope WithRow(string table)
        {
            var s = new Scope { Vars = Vars, Source = Source, RowTables = new List<string>(RowTables) };
            s.RowTables.Add(table);
            return s;
        }

        public Scope WithVar(string name)
        {
            var s = new Scope { Vars = new HashSet<string>(Vars, StringComparer.OrdinalIgnoreCase), Source = Source, RowTables = RowTables };
            s.Vars.Add(name);
            return s;
        }
    }

    /// <summary>Lexer, parser and binder output become a closure tree over EvalCtx. LQL is never
    /// compiled through CSharpCodeProvider, so a report file cannot run code.</summary>
    public sealed class LqlCompiler
    {
        private readonly Dataset _ds;
        // Compiled measures by id. Closures bind column arrays, so this lives and dies with the Dataset.
        private readonly ConcurrentDictionary<string, Eval> _measures = new ConcurrentDictionary<string, Eval>();
        // Columns as double arrays for kernels. Non-double columns would otherwise convert on every compile.
        private readonly ConcurrentDictionary<string, double[]> _doubles = new ConcurrentDictionary<string, double[]>();
        // Measures being compiled on this thread. Finding one twice means a cycle; a measure that depends on another is compiled during evaluation, on this same stack.
        [ThreadStatic] private static HashSet<string> _inFlight;

        /// <summary>Created by <see cref="Dataset"/>; there is one per dataset.</summary>
        public LqlCompiler(Dataset ds) { _ds = ds; }

        /// <summary>Compiles for errors only. Used as the user types, and for custom columns when a row table is given.
        /// Only the first error is reported, because the parser stops there.</summary>
        public static IReadOnlyList<LqlDiagnostic> Validate(Dataset ds, string expression, string rowTableId = null)
        {
            try
            {
                var c = ds.Compiler;
                var scope = new Scope { Source = expression };
                if (rowTableId != null) scope = scope.WithRow(rowTableId);
                c.Compile(new Parser(expression).ParseAll(), scope);
                return new LqlDiagnostic[0];
            }
            catch (LqlException ex) { return new[] { ex.Diagnostic }; }
        }

        /// <summary>Compiles a scalar expression with no row context, as measures need.</summary>
        public Eval CompileExpression(string expression)
        {
            return Compile(new Parser(expression).ParseAll(), new Scope { Source = expression });
        }

        /// <summary>Compiles an expression evaluated per row of a table, as custom columns need.</summary>
        public Eval CompileRowExpression(string expression, string tableId)
        {
            return Compile(new Parser(expression).ParseAll(), new Scope { Source = expression }.WithRow(tableId));
        }

        /// <summary>The compiled form of a measure, cached by id. Errors surface as InvalidOperationException, which <see cref="QueryScope"/> shows in the visual.</summary>
        public Eval MeasureEval(MeasureDef m)
        {
            Eval e;
            if (_measures.TryGetValue(m.Id, out e)) return e;
            if (_inFlight == null) _inFlight = new HashSet<string>();
            if (!_inFlight.Add(m.Id))
                throw new InvalidOperationException($"Measure [{m.Name}] refers to itself");
            try
            {
                e = CompileExpression(m.Expression);
                _measures[m.Id] = e;
                return e;
            }
            catch (LqlException ex)
            {
                throw new InvalidOperationException($"Measure [{m.Name}]: {ex.Message}", ex);
            }
            finally { _inFlight.Remove(m.Id); }
        }

        /// <summary>The evaluator for a value well entry: a measure, or a column with an aggregation.</summary>
        public Eval FieldEval(FieldRef f)
        {
            if (f.Kind == FieldKind.Measure)
            {
                var m = _ds.Model.Measure(f.MeasureId);
                if (m == null) throw new InvalidOperationException("The measure no longer exists");
                return MeasureEval(m);
            }
            var col = _ds.ColumnOf(f.TableId, f.ColumnId);
            if (col == null) throw new InvalidOperationException("The column no longer exists");
            return Aggregate(f.Aggregation ?? SummarizeBy.Sum, f.TableId, f.ColumnId);
        }

        /// <summary>Compiles a simple aggregation. Each closure calls <see cref="EvalCtx.Rows"/> and needs nothing else, so it stays on the fast path.</summary>
        private Eval Aggregate(SummarizeBy kind, string tableId, string columnId)
        {
            var col = _ds.ColumnOf(tableId, columnId);
            switch (kind)
            {
                case SummarizeBy.Average: return c => Average(col, c.Rows(tableId));
                case SummarizeBy.Min: return c => Extreme(col, c.Rows(tableId), false);
                case SummarizeBy.Max: return c => Extreme(col, c.Rows(tableId), true);
                case SummarizeBy.Count: return c => CountNonBlank(col, c.Rows(tableId));
                case SummarizeBy.DistinctCount: return c => DistinctCount(col, c.Rows(tableId));
                default: return c => Sum(col, c.Rows(tableId));
            }
        }

        /// <summary>SUM over rows. BLANK if there are no rows or every cell is BLANK. <c>seen</c> is only ever set to true, so the race is benign.</summary>
        private static Value Sum(Column col, int[] rows)
        {
            if (rows.Length == 0) return Value.Blank;
            bool seen = false;
            double sum = Par.Sum(rows.Length, (from, to) =>
            {
                bool any;
                double part = SumRange(col, rows, from, to, out any);
                if (any) seen = true;
                return part;
            });
            return seen ? Value.FromNumber(sum) : Value.Blank;
        }

        /// <summary>The inner loop, specialized by column type so that no Value is created per row.</summary>
        private static double SumRange(Column col, int[] rows, int from, int to, out bool any)
        {
            double sum = 0;
            any = false;
            if (col is DoubleColumn d)
            {
                var v = d.Values;
                if (d.NullBits == null)
                {
                    any = true;
                    for (int i = from; i < to; i++) sum += v[rows[i]];
                    return sum;
                }
                for (int i = from; i < to; i++) if (!d.IsNull(rows[i])) { sum += v[rows[i]]; any = true; }
            }
            else if (col is Int64Column l)
            {
                var v = l.Values;
                if (l.NullBits == null)
                {
                    any = true;
                    for (int i = from; i < to; i++) sum += v[rows[i]];
                    return sum;
                }
                for (int i = from; i < to; i++) if (!l.IsNull(rows[i])) { sum += v[rows[i]]; any = true; }
            }
            else
            {
                for (int i = from; i < to; i++) { var v = col.Get(rows[i]); if (!v.IsBlank) { sum += v.AsDouble(); any = true; } }
            }
            return sum;
        }

        /// <summary>AVERAGE over non-blank cells.</summary>
        private static Value Average(Column col, int[] rows)
        {
            long n = 0;
            double sum = 0;
            foreach (var r in rows)
            {
                if (col.IsNull(r)) continue;
                sum += col.Get(r).AsDouble();
                n++;
            }
            return n == 0 ? Value.Blank : Value.FromNumber(sum / n);
        }

        /// <summary>COUNT over non-blank cells. BLANK, not 0, when there are none, so that empty groups drop out.</summary>
        private static Value CountNonBlank(Column col, int[] rows)
        {
            long n = 0;
            foreach (var r in rows) if (!col.IsNull(r)) n++;
            return n == 0 ? Value.Blank : Value.FromInteger(n);
        }

        /// <summary>DISTINCTCOUNT by marking codes in a Bitset. A BLANK cell counts as one value, as in DAX.</summary>
        private static Value DistinctCount(Column col, int[] rows)
        {
            if (rows.Length == 0) return Value.Blank;
            var enc = col.Encoded;
            var seen = new Bitset(enc.Values.Length);
            var codes = enc.Codes;
            for (int i = 0; i < rows.Length; i++) seen.Set(codes[rows[i]]);
            return Value.FromInteger(seen.Count());
        }

        /// <summary>MIN or MAX. Codes are in value order, so comparing them is comparing the values, for strings and dates alike.</summary>
        private static Value Extreme(Column col, int[] rows, bool max)
        {
            var enc = col.Encoded;
            var codes = enc.Codes;
            int best = 0;
            foreach (var r in rows)
            {
                int c = codes[r];
                if (c == 0) continue;
                if (best == 0 || (max ? c > best : c < best)) best = c;
            }
            return best == 0 ? Value.Blank : enc.Values[best];
        }

        // ---------------------------------------------------------------- binder

        /// <summary>Makes an error pointing at a node.</summary>
        private LqlException Err(string msg, Node n, Scope s) => new LqlException(msg, n.Pos, n.Len, s.Source);

        /// <summary>Finds a table by name or reports it.</summary>
        private TableDef ResolveTable(string name, Node n, Scope s)
        {
            var t = _ds.Model.TableByName(name);
            if (t == null) throw Err($"Unknown table '{name}'", n, s);
            return t;
        }

        /// <summary>Resolves a column reference. Unqualified [X] tries the row contexts, innermost first, then any table with a column of that name, provided only one has it.</summary>
        private bool TryResolveColumn(ColumnNode n, Scope s, out TableDef table, out ColumnDef col)
        {
            table = null; col = null;
            if (n.Table != null)
            {
                table = ResolveTable(n.Table, n, s);
                col = table.ColumnByName(n.Column);
                return col != null;
            }
            foreach (var rt in Enumerable.Reverse(s.RowTables))
            {
                var t = _ds.Model.Table(rt);
                var c = t?.ColumnByName(n.Column);
                if (c != null) { table = t; col = c; return true; }
            }
            var matches = _ds.Model.Tables.Where(t => t.ColumnByName(n.Column) != null).ToList();
            if (matches.Count == 1)
            {
                table = matches[0];
                col = table.ColumnByName(n.Column);
                return true;
            }
            return false;
        }

        /// <summary>As <see cref="TryResolveColumn"/>, but an error if the node is not a resolvable column.</summary>
        private void RequireColumn(Node arg, Scope s, out TableDef table, out ColumnDef col)
        {
            var cn = arg as ColumnNode;
            if (cn == null) throw Err("Expected a column reference", arg, s);
            if (!TryResolveColumn(cn, s, out table, out col))
                throw Err($"Unknown column [{cn.Column}]", arg, s);
        }

        /// <summary>Checks the argument count of a call.</summary>
        private static void Arity(CallNode n, int min, int max, Scope s, LqlCompiler me)
        {
            if (n.Args.Count < min || n.Args.Count > max)
                throw me.Err(min == max
                    ? $"{n.Name} takes {min} argument(s)"
                    : $"{n.Name} takes {min} to {max} arguments", n, s);
        }

        /// <summary>Binds a node and returns its closure. This is the recursive heart of the compiler.</summary>
        internal Eval Compile(Node n, Scope s)
        {
            if (n is NumberNode num) { var v = Value.FromNumber(num.Value); return c => v; }
            if (n is TextNode txt) { var v = Value.FromString(txt.Value); return c => v; }
            if (n is ColumnNode col) return CompileColumn(col, s);
            if (n is TableNode tn)
            {
                if (s.Vars.Contains(tn.Name)) { var name = tn.Name; return c => c.Var(name); }
                throw Err($"'{tn.Name}' is not a variable; a table cannot be used as a scalar", n, s);
            }
            if (n is UnaryNode u)
            {
                var e = Compile(u.Operand, s);
                if (u.Op == "+") return e;
                return c =>
                {
                    var x = e(c);
                    return x.IsBlank ? x : Value.FromNumber(-x.AsDouble());
                };
            }
            if (n is BinaryNode b) return CompileBinary(b, s);
            if (n is VarBlockNode vb) return CompileVars(vb, s);
            if (n is CallNode call) return CompileCall(call, s);
            if (n is ListNode) throw Err("A list is only valid on the right of IN", n, s);
            throw Err("Unsupported expression", n, s);
        }

        /// <summary>Compiles VAR ... RETURN. Variables are evaluated eagerly, in order, in the context where the block starts, which is the behavior of DAX variables.</summary>
        private Eval CompileVars(VarBlockNode vb, Scope s)
        {
            var names = new List<string>();
            var evals = new List<Eval>();
            var scope = s;
            foreach (var kv in vb.Vars)
            {
                names.Add(kv.Key);
                evals.Add(Compile(kv.Value, scope));
                scope = scope.WithVar(kv.Key);
            }
            var body = Compile(vb.Body, scope);
            return c =>
            {
                var vars = new Dictionary<string, Value>(StringComparer.OrdinalIgnoreCase);
                var inner = c.WithVars(vars);
                for (int i = 0; i < names.Count; i++) vars[names[i]] = evals[i](inner);
                return body(inner);
            };
        }

        /// <summary>Compiles a bare reference. [X] prefers a measure, unless a row context has a column named X. A column needs a row context of its table.</summary>
        private Eval CompileColumn(ColumnNode n, Scope s)
        {
            if (n.Table == null)
            {
                var m = _ds.Model.MeasureByName(n.Column);
                if (m != null && !s.RowTables.Any(t => _ds.Model.Table(t)?.ColumnByName(n.Column) != null))
                {
                    var id = m.Id;
                    // Resolved at evaluation time, so measures may refer to each other in any definition order.
                    return c => { var def = _ds.Model.Measure(id); return MeasureEval(def)(c); };
                }
            }
            TableDef table; ColumnDef col;
            if (!TryResolveColumn(n, s, out table, out col))
            {
                // Table[Measure] is allowed, as in DAX: retry as a bare measure reference.
                if (n.Table != null)
                {
                    var m = _ds.Model.MeasureByName(n.Column);
                    if (m != null) return Compile(new ColumnNode { Column = n.Column, Pos = n.Pos, Len = n.Len }, s);
                }
                throw Err($"Unknown column or measure [{n.Column}]", n, s);
            }
            if (!s.RowTables.Contains(table.Id))
                throw Err($"Column [{col.Name}] needs a row context; wrap it in an aggregation such as SUM", n, s);

            var tableId = table.Id;
            var data = _ds.ColumnOf(table.Id, col.Id);
            if (data == null) throw Err($"Column [{col.Name}] has no data", n, s);
            return c =>
            {
                int row;
                c.TryRow(tableId, out row);
                return data.Get(row);
            };
        }

        /// <summary>Compiles an operator. Both sides are evaluated, except where the operator short-circuits through C#.</summary>
        private Eval CompileBinary(BinaryNode b, Scope s)
        {
            if (b.Op == "IN") return CompileIn(b, s);

            var l = Compile(b.Left, s);
            var r = Compile(b.Right, s);
            switch (b.Op)
            {
                case "+": return c => Add(l(c), r(c));
                case "-": return c => Subtract(l(c), r(c));
                case "*":
                    return c =>
                    {
                        var x = l(c); var y = r(c);
                        return x.IsBlank || y.IsBlank ? Value.Blank : Value.FromNumber(x.AsDouble() * y.AsDouble());
                    };
                case "/":
                    return c =>
                    {
                        var x = l(c); var y = r(c);
                        // The / operator yields Infinity or NaN on zero, unlike DIVIDE, which returns BLANK.
                        if (x.IsBlank && y.IsBlank) return Value.Blank;
                        return Value.FromNumber(x.AsDouble() / y.AsDouble());
                    };
                case "^": return c => Value.FromNumber(Math.Pow(l(c).AsDouble(), r(c).AsDouble()));
                case "&": return c => Value.FromString(Text(l(c)) + Text(r(c)));
                case "&&": return c => Value.FromBoolean(l(c).AsBool() && r(c).AsBool());
                case "||": return c => Value.FromBoolean(l(c).AsBool() || r(c).AsBool());
                case "=": return c => Value.FromBoolean(Value.Compare(l(c), r(c)) == 0);
                case "<>": return c => Value.FromBoolean(Value.Compare(l(c), r(c)) != 0);
                case "<": return c => Value.FromBoolean(Value.Compare(l(c), r(c)) < 0);
                case "<=": return c => Value.FromBoolean(Value.Compare(l(c), r(c)) <= 0);
                case ">": return c => Value.FromBoolean(Value.Compare(l(c), r(c)) > 0);
                case ">=": return c => Value.FromBoolean(Value.Compare(l(c), r(c)) >= 0);
            }
            throw Err($"Unknown operator {b.Op}", b, s);
        }

        /// <summary>Compiles x IN {list}.</summary>
        private Eval CompileIn(BinaryNode b, Scope s)
        {
            var list = b.Right as ListNode;
            if (list == null) throw Err("IN expects a list such as {\"Red\", \"Blue\"}", b.Right, s);
            var left = Compile(b.Left, s);
            var items = list.Items.Select(i => Compile(i, s)).ToArray();
            return c =>
            {
                var v = left(c);
                foreach (var e in items) if (Value.Compare(v, e(c)) == 0) return Value.FromBoolean(true);
                return Value.FromBoolean(false);
            };
        }

        /// <summary>A value as text, with BLANK as empty.</summary>
        private static string Text(Value v) => v.IsBlank ? "" : v.ToDisplayString();

        /// <summary>Addition with DAX blank rules: BLANK acts as zero. A date plus a number moves the date by days.</summary>
        private static Value Add(Value x, Value y)
        {
            if (x.IsBlank) return y;
            if (y.IsBlank) return x;
            if (x.Kind == ValueKind.Date && y.IsNumeric) return Value.FromDate(x.AsDate.AddDays(y.AsDouble()));
            return Value.FromNumber(x.AsDouble() + y.AsDouble());
        }

        /// <summary>Subtraction with the same rules. BLANK minus a number falls through to 0 minus it, and two dates subtract to a number of days.</summary>
        private static Value Subtract(Value x, Value y)
        {
            if (x.IsBlank && y.IsBlank) return x;
            if (y.IsBlank) return x;
            if (x.Kind == ValueKind.Date && y.IsNumeric) return Value.FromDate(x.AsDate.AddDays(-y.AsDouble()));
            return Value.FromNumber(x.AsDouble() - y.AsDouble());
        }

        /// <summary>The function table. To add a function, add a case here; to add a table function, see <see cref="CompileTable"/> and <see cref="IsTableExpression"/>.</summary>
        private Eval CompileCall(CallNode n, Scope s)
        {
            switch (n.Name)
            {
                case "SUM": case "AVERAGE": case "COUNT": case "COUNTA": case "DISTINCTCOUNT":
                {
                    Arity(n, 1, 1, s, this);
                    TableDef t; ColumnDef col;
                    RequireColumn(n.Args[0], s, out t, out col);
                    var kind = n.Name == "SUM" ? SummarizeBy.Sum : n.Name == "AVERAGE" ? SummarizeBy.Average
                             : n.Name == "DISTINCTCOUNT" ? SummarizeBy.DistinctCount : SummarizeBy.Count;
                    return Aggregate(kind, t.Id, col.Id);
                }
                case "MIN": case "MAX":
                {
                    Arity(n, 1, 2, s, this);
                    bool max = n.Name == "MAX";
                    if (n.Args.Count == 2)
                    {
                        var a = Compile(n.Args[0], s);
                        var b = Compile(n.Args[1], s);
                        return c =>
                        {
                            var x = a(c); var y = b(c);
                            if (x.IsBlank) return y;
                            if (y.IsBlank) return x;
                            return (Value.Compare(x, y) > 0) == max ? x : y;
                        };
                    }
                    TableDef t; ColumnDef col;
                    RequireColumn(n.Args[0], s, out t, out col);
                    return Aggregate(max ? SummarizeBy.Max : SummarizeBy.Min, t.Id, col.Id);
                }
                case "COUNTROWS":
                {
                    Arity(n, 1, 1, s, this);
                    var tab = CompileTable(n.Args[0], s);
                    return c =>
                    {
                        var rows = tab.Rows(c);
                        return rows.Length == 0 ? Value.Blank : Value.FromInteger(rows.Length);
                    };
                }
                case "SUMX": case "AVERAGEX": case "MINX": case "MAXX": case "COUNTX":
                    return CompileIterator(n, s);
                case "RELATED":
                    return CompileRelated(n, s);
                case "CALCULATE":
                    return CompileCalculate(n, s);
                case "TOTALYTD":
                {
                    Arity(n, 2, 3, s, this);
                    var body = Compile(n.Args[0], s);
                    var dates = CompileTime("DATESYTD", n.Args[1], n, s);
                    return c => body(c.WithFilter(dates.Modify(c, c.F)));
                }
                case "DIVIDE":
                {
                    Arity(n, 2, 3, s, this);
                    var a = Compile(n.Args[0], s);
                    var b = Compile(n.Args[1], s);
                    Eval alt = n.Args.Count == 3 ? Compile(n.Args[2], s) : null;
                    return c =>
                    {
                        // The denominator first, so a zero spares evaluating the numerator.
                        var d = b(c);
                        if (d.IsBlank || d.AsDouble() == 0) return alt != null ? alt(c) : Value.Blank;
                        var x = a(c);
                        return x.IsBlank ? x : Value.FromNumber(x.AsDouble() / d.AsDouble());
                    };
                }
                case "IF":
                {
                    Arity(n, 2, 3, s, this);
                    var cond = Compile(n.Args[0], s);
                    var yes = Compile(n.Args[1], s);
                    Eval no = n.Args.Count == 3 ? Compile(n.Args[2], s) : null;
                    return c => cond(c).AsBool() ? yes(c) : no != null ? no(c) : Value.Blank;
                }
                case "SWITCH":
                {
                    if (n.Args.Count < 3) throw Err("SWITCH needs an expression and at least one value/result pair", n, s);
                    var subject = Compile(n.Args[0], s);
                    int pairs = (n.Args.Count - 1) / 2;
                    var whens = new Eval[pairs];
                    var thens = new Eval[pairs];
                    for (int i = 0; i < pairs; i++)
                    {
                        whens[i] = Compile(n.Args[1 + i * 2], s);
                        thens[i] = Compile(n.Args[2 + i * 2], s);
                    }
                    Eval other = (n.Args.Count - 1) % 2 == 1 ? Compile(n.Args[n.Args.Count - 1], s) : null;
                    return c =>
                    {
                        var v = subject(c);
                        for (int i = 0; i < pairs; i++)
                            if (Value.Compare(v, whens[i](c)) == 0) return thens[i](c);
                        return other != null ? other(c) : Value.Blank;
                    };
                }
                case "AND": { Arity(n, 2, 2, s, this); var a = Compile(n.Args[0], s); var b = Compile(n.Args[1], s); return c => Value.FromBoolean(a(c).AsBool() && b(c).AsBool()); }
                case "OR": { Arity(n, 2, 2, s, this); var a = Compile(n.Args[0], s); var b = Compile(n.Args[1], s); return c => Value.FromBoolean(a(c).AsBool() || b(c).AsBool()); }
                case "NOT": { Arity(n, 1, 1, s, this); var a = Compile(n.Args[0], s); return c => Value.FromBoolean(!a(c).AsBool()); }
                case "TRUE": { Arity(n, 0, 0, s, this); var v = Value.FromBoolean(true); return c => v; }
                case "FALSE": { Arity(n, 0, 0, s, this); var v = Value.FromBoolean(false); return c => v; }
                case "BLANK": { Arity(n, 0, 0, s, this); return c => Value.Blank; }
                case "ISBLANK": { Arity(n, 1, 1, s, this); var a = Compile(n.Args[0], s); return c => Value.FromBoolean(a(c).IsBlank); }
                case "ABS": return Numeric(n, s, Math.Abs);
                case "INT": return Numeric(n, s, Math.Floor);
                case "SQRT": return Numeric(n, s, Math.Sqrt);
                case "ROUND":
                {
                    Arity(n, 1, 2, s, this);
                    var a = Compile(n.Args[0], s);
                    Eval d = n.Args.Count == 2 ? Compile(n.Args[1], s) : null;
                    return c =>
                    {
                        var x = a(c);
                        if (x.IsBlank) return x;
                        int digits = d == null ? 0 : (int)d(c).AsDouble();
                        double p = Math.Pow(10, digits);
                        return Value.FromNumber(Math.Round(x.AsDouble() * p, MidpointRounding.AwayFromZero) / p);
                    };
                }
                case "YEAR": return DatePart(n, s, d => d.Year);
                case "QUARTER": return DatePart(n, s, d => (d.Month - 1) / 3 + 1);
                case "MONTH": return DatePart(n, s, d => d.Month);
                case "DAY": return DatePart(n, s, d => d.Day);
                case "DATE":
                {
                    Arity(n, 3, 3, s, this);
                    var y = Compile(n.Args[0], s); var m = Compile(n.Args[1], s); var d = Compile(n.Args[2], s);
                    return c => Value.FromDate(new DateTime((int)y(c).AsDouble(), 1, 1)
                        .AddMonths((int)m(c).AsDouble() - 1).AddDays((int)d(c).AsDouble() - 1));
                }
                case "TODAY": { Arity(n, 0, 0, s, this); return c => Value.FromDate(DateTime.Today); }
                case "NOW": { Arity(n, 0, 0, s, this); return c => Value.FromDate(DateTime.Now); }
                // The format string goes straight to .NET, not through Power BI's own format language.
                case "FORMAT":
                {
                    Arity(n, 2, 2, s, this);
                    var a = Compile(n.Args[0], s); var f = Compile(n.Args[1], s);
                    return c =>
                    {
                        var v = a(c);
                        if (v.IsBlank) return Value.FromString("");
                        var o = v.ToObject();
                        return Value.FromString(o is IFormattable x ? x.ToString(f(c).String, CultureInfo.CurrentCulture) : Text(v));
                    };
                }
                case "CONCATENATE": { Arity(n, 2, 2, s, this); var a = Compile(n.Args[0], s); var b = Compile(n.Args[1], s); return c => Value.FromString(Text(a(c)) + Text(b(c))); }
                case "LEN": { Arity(n, 1, 1, s, this); var a = Compile(n.Args[0], s); return c => Value.FromInteger(Text(a(c)).Length); }
                case "UPPER": { Arity(n, 1, 1, s, this); var a = Compile(n.Args[0], s); return c => Value.FromString(Text(a(c)).ToUpperInvariant()); }
                case "LOWER": { Arity(n, 1, 1, s, this); var a = Compile(n.Args[0], s); return c => Value.FromString(Text(a(c)).ToLowerInvariant()); }
                case "LEFT":
                {
                    Arity(n, 2, 2, s, this);
                    var a = Compile(n.Args[0], s); var b = Compile(n.Args[1], s);
                    return c => { var t = Text(a(c)); return Value.FromString(t.Substring(0, Math.Min(t.Length, (int)b(c).AsDouble()))); };
                }
                case "RIGHT":
                {
                    Arity(n, 2, 2, s, this);
                    var a = Compile(n.Args[0], s); var b = Compile(n.Args[1], s);
                    return c => { var t = Text(a(c)); int k = Math.Min(t.Length, (int)b(c).AsDouble()); return Value.FromString(t.Substring(t.Length - k)); };
                }
                case "HASONEVALUE":
                {
                    Arity(n, 1, 1, s, this);
                    TableDef t; ColumnDef col;
                    RequireColumn(n.Args[0], s, out t, out col);
                    var data = _ds.ColumnOf(t.Id, col.Id);
                    var id = t.Id;
                    return c => Value.FromBoolean(DistinctCount(data, c.Rows(id)).Integer == 1);
                }
                case "SELECTEDVALUE":
                {
                    Arity(n, 1, 2, s, this);
                    TableDef t; ColumnDef col;
                    RequireColumn(n.Args[0], s, out t, out col);
                    var data = _ds.ColumnOf(t.Id, col.Id);
                    var id = t.Id;
                    Eval alt = n.Args.Count == 2 ? Compile(n.Args[1], s) : null;
                    return c =>
                    {
                        var rows = c.Rows(id);
                        if (DistinctCount(data, rows).Integer == 1) return data.Get(rows[0]);
                        return alt != null ? alt(c) : Value.Blank;
                    };
                }
                case "ALL": case "REMOVEFILTERS": case "FILTER": case "VALUES": case "DISTINCT":
                case "SAMEPERIODLASTYEAR": case "DATESYTD":
                    throw Err($"{n.Name} returns a table, which cannot be used as a single value", n, s);
            }
            throw Err($"Unknown function {n.Name}", n, s);
        }

        /// <summary>Compiles a one-argument math function that passes BLANK through.</summary>
        private Eval Numeric(CallNode n, Scope s, Func<double, double> f)
        {
            Arity(n, 1, 1, s, this);
            var a = Compile(n.Args[0], s);
            return c => { var x = a(c); return x.IsBlank ? x : Value.FromNumber(f(x.AsDouble())); };
        }

        /// <summary>Compiles a one-argument date function that passes BLANK through.</summary>
        private Eval DatePart(CallNode n, Scope s, Func<DateTime, int> f)
        {
            Arity(n, 1, 1, s, this);
            var a = Compile(n.Args[0], s);
            return c =>
            {
                var x = Column.Coerce(a(c), LumenDataType.DateTime);
                return x.IsBlank ? x : Value.FromInteger(f(x.AsDate));
            };
        }

        /// <summary>Compiles RELATED: follow the many-to-one chain from the current row. An unmatched row gives BLANK.</summary>
        private Eval CompileRelated(CallNode n, Scope s)
        {
            Arity(n, 1, 1, s, this);
            TableDef t; ColumnDef col;
            RequireColumn(n.Args[0], s, out t, out col);
            if (!s.RowTables.Any(rt => _ds.Reaches(rt, t.Id)))
                throw Err($"No relationship leads from the current row to '{t.Name}'", n, s);
            var data = _ds.ColumnOf(t.Id, col.Id);
            var target = t.Id;
            return c =>
            {
                int row;
                var rowTable = c.InnermostRowTable(out row);
                if (rowTable == null) return Value.Blank;
                var map = _ds.RowMap(rowTable, target);
                if (map == null) return data.Get(row);
                int dim = map.Length == 0 ? -1 : map[row];
                return dim < 0 ? Value.Blank : data.Get(dim);
            };
        }

        /// <summary>Compiles SUMX, AVERAGEX, MINX, MAXX and COUNTX. SUMX over plain arithmetic takes the parallel kernel; the rest walk one row at a time through closures.</summary>
        private Eval CompileIterator(CallNode n, Scope s)
        {
            Arity(n, 2, 2, s, this);
            var tab = CompileTable(n.Args[0], s);
            var inner = s.WithRow(tab.TableId);
            var body = Compile(n.Args[1], inner);
            var tableId = tab.TableId;
            var kernel = n.Name == "SUMX" ? TryKernel(n.Args[1], tableId, inner) : null;
            if (kernel != null)
            {
                return c =>
                {
                    var rows = tab.Rows(c);
                    if (rows.Length == 0) return Value.Blank;
                    return Value.FromNumber(Par.Sum(rows.Length, (from, to) =>
                    {
                        double part = 0;
                        for (int i = from; i < to; i++) part += kernel(rows[i]);
                        return part;
                    }));
                };
            }

            // The general path. It reuses one iterator context, and skips BLANK results.
            string fn = n.Name;
            return c =>
            {
                var rows = tab.Rows(c);
                var it = c.Iterator(tableId);
                double sum = 0; long count = 0;
                Value best = Value.Blank;
                for (int i = 0; i < rows.Length; i++)
                {
                    it.SetRow(rows[i]);
                    var v = body(it);
                    if (v.IsBlank) continue;
                    count++;
                    if (fn == "SUMX" || fn == "AVERAGEX") sum += v.AsDouble();
                    else if (fn == "MINX") { if (best.IsBlank || Value.Compare(v, best) < 0) best = v; }
                    else if (fn == "MAXX") { if (best.IsBlank || Value.Compare(v, best) > 0) best = v; }
                }
                if (count == 0) return Value.Blank;
                switch (fn)
                {
                    case "AVERAGEX": return Value.FromNumber(sum / count);
                    case "COUNTX": return Value.FromInteger(count);
                    case "SUMX": return Value.FromNumber(sum);
                    default: return best;
                }
            };
        }

        /// <summary>Fast path for SUMX over plain column arithmetic: unboxed doubles, no EvalCtx per row.
        /// A BLANK cell acts as 0, which matches SUMX for + - * since the sum skips BLANK rows anyway.</summary>
        private RowFn TryKernel(Node n, string tableId, Scope s)
        {
            if (n is NumberNode num) { double v = num.Value; return r => v; }
            if (n is ColumnNode cn)
            {
                TableDef t; ColumnDef col;
                if (!TryResolveColumn(cn, s, out t, out col) || t.Id != tableId || !col.DataType.IsNumeric()) return null;
                if (cn.Table == null && _ds.Model.MeasureByName(cn.Column) != null) return null;
                var data = _ds.ColumnOf(t.Id, col.Id);
                if (data == null) return null;
                var arr = _doubles.GetOrAdd(t.Id + "|" + col.Id, _ => data.AsDoubles());
                return r => arr[r];
            }
            if (n is BinaryNode b && (b.Op == "+" || b.Op == "-" || b.Op == "*"))
            {
                var l = TryKernel(b.Left, tableId, s);
                var r = TryKernel(b.Right, tableId, s);
                if (l == null || r == null) return null;
                switch (b.Op)
                {
                    case "+": return row => l(row) + r(row);
                    case "-": return row => l(row) - r(row);
                    default: return row => l(row) * r(row);
                }
            }
            return null;
        }

        // ---------------------------------------------------------------- CALCULATE and table functions

        /// <summary>Compiles CALCULATE: apply each filter argument in order to a copy of the context, then evaluate the body under the result.</summary>
        private Eval CompileCalculate(CallNode n, Scope s)
        {
            if (n.Args.Count < 1) throw Err("CALCULATE needs an expression", n, s);
            var body = Compile(n.Args[0], s);
            var mods = n.Args.Skip(1).Select(a => CompileModifier(a, s)).ToArray();
            return c =>
            {
                // Filter arguments see the outer context; the new context applies only to the body.
                var f = c.F;
                foreach (var m in mods) f = m(c, f);
                return body(c.WithFilter(f));
            };
        }

        /// <summary>True for arguments that <see cref="CompileTable"/> handles, as opposed to boolean predicates.</summary>
        private static bool IsTableExpression(Node n)
        {
            if (n is TableNode) return true;
            if (n is CallNode c)
            {
                switch (c.Name)
                {
                    case "ALL": case "REMOVEFILTERS": case "FILTER": case "VALUES": case "DISTINCT":
                    case "SAMEPERIODLASTYEAR": case "DATESYTD":
                        return true;
                }
            }
            return false;
        }

        /// <summary>Compiles one CALCULATE argument. A boolean predicate must name exactly one column, and replaces any filter on that column.</summary>
        private Modifier CompileModifier(Node arg, Scope s)
        {
            if (IsTableExpression(arg)) return CompileTable(arg, s).Modify;

            // A boolean predicate, such as Product[Color] = "Red" or Sales[Qty] > 5.
            var cols = new List<ColumnNode>();
            bool isStatic = Walk(arg, cols);
            var distinct = cols.GroupBy(c => (c.Table ?? "") + "|" + c.Column.ToLowerInvariant()).ToList();
            if (distinct.Count != 1)
                throw Err("A CALCULATE filter must refer to exactly one column; use FILTER for anything else", arg, s);

            TableDef t; ColumnDef col;
            RequireColumn(cols[0], s, out t, out col);
            var pred = Compile(arg, s.WithRow(t.Id));
            var data = _ds.ColumnOf(t.Id, col.Id);
            var tableId = t.Id;
            var columnId = col.Id;
            // A predicate that depends only on its column gives the same answer every time, so the first evaluation is kept.
            ColFilter cached = null;

            return (c, f) =>
            {
                var result = cached;
                if (result == null)
                {
                    result = EvaluateColumnPredicate(c, data, tableId, columnId, pred);
                    if (isStatic) cached = result;
                }
                return f.WithColumn(result);
            };
        }

        /// <summary>Runs a predicate once per distinct value, on a representative row. Cost scales with the dictionary, not the table.</summary>
        private static ColFilter EvaluateColumnPredicate(EvalCtx c, Column data, string tableId, string columnId, Eval pred)
        {
            var enc = data.Encoded;
            var allowed = new Bitset(enc.Values.Length);
            var it = c.Iterator(tableId);
            for (int code = 0; code < enc.Values.Length; code++)
            {
                int row = enc.FirstRow[code];
                if (row < 0) continue;
                it.SetRow(row);
                if (pred(it).AsBool()) allowed.Set(code);
            }
            return new ColFilter { TableId = tableId, ColumnId = columnId, Allowed = allowed };
        }

        /// <summary>Collects column references. Returns true if the node's value cannot depend on context.</summary>
        private static bool Walk(Node n, List<ColumnNode> cols)
        {
            if (n is ColumnNode c) { cols.Add(c); return true; }
            if (n is NumberNode || n is TextNode) return true;
            if (n is BinaryNode b) { bool x = Walk(b.Left, cols); return Walk(b.Right, cols) && x; }
            if (n is UnaryNode u) return Walk(u.Operand, cols);
            if (n is ListNode l) { bool all = true; foreach (var i in l.Items) all &= Walk(i, cols); return all; }
            if (n is CallNode call)
            {
                bool all = call.Name != "TODAY" && call.Name != "NOW";
                foreach (var a in call.Args) all &= Walk(a, cols);
                return all;
            }
            return false; // variables, VAR blocks
        }

        /// <summary>Compiles a table expression: a bare table name, or a table function.</summary>
        private CompiledTable CompileTable(Node n, Scope s)
        {
            if (n is TableNode tn)
            {
                var t = ResolveTable(tn.Name, n, s);
                var id = t.Id;
                return new CompiledTable { TableId = id, Rows = c => c.Rows(id), Modify = (c, f) => f };
            }
            var call = n as CallNode;
            if (call == null) throw Err("Expected a table", n, s);
            switch (call.Name)
            {
                case "ALL": case "REMOVEFILTERS":
                    Arity(call, 1, 1, s, this);
                    return CompileAll(call.Args[0], s);
                case "VALUES": case "DISTINCT":
                    Arity(call, 1, 1, s, this);
                    return CompileValues(call.Args[0], s);
                case "FILTER":
                    Arity(call, 2, 2, s, this);
                    return CompileFilter(call, s);
                case "SAMEPERIODLASTYEAR": case "DATESYTD":
                    Arity(call, 1, 1, s, this);
                    return CompileTime(call.Name, call.Args[0], call, s);
            }
            throw Err($"{call.Name} is not a table function", n, s);
        }

        /// <summary>Compiles ALL and REMOVEFILTERS. For a table, the filters on its expanded table go; for a column, only that column's.</summary>
        private CompiledTable CompileAll(Node arg, Scope s)
        {
            if (arg is TableNode tn)
            {
                var t = ResolveTable(tn.Name, arg, s);
                var id = t.Id;
                var expanded = _ds.Expanded(id);
                var all = _ds.Tables[id];
                return new CompiledTable { TableId = id, Rows = c => all.AllRows, Modify = (c, f) => f.WithoutTables(expanded) };
            }
            TableDef table; ColumnDef col;
            RequireColumn(arg, s, out table, out col);
            var data = _ds.ColumnOf(table.Id, col.Id);
            var tableId = table.Id; var columnId = col.Id;
            return new CompiledTable
            {
                TableId = tableId,
                ColumnId = columnId,
                Rows = c => data.Encoded.FirstRow.Where(r => r >= 0).ToArray(),
                Modify = (c, f) => f.WithoutColumn(tableId, columnId)
            };
        }

        /// <summary>Compiles VALUES and DISTINCT, which are the same here: the values visible in the current context.</summary>
        private CompiledTable CompileValues(Node arg, Scope s)
        {
            if (arg is TableNode) return CompileTable(arg, s);
            TableDef table; ColumnDef col;
            RequireColumn(arg, s, out table, out col);
            var data = _ds.ColumnOf(table.Id, col.Id);
            var tableId = table.Id; var columnId = col.Id;
            Func<EvalCtx, Bitset> visible = c => VisibleCodes(data, c.Rows(tableId));
            return new CompiledTable
            {
                TableId = tableId,
                ColumnId = columnId,
                Rows = c => RepresentativeRows(data, visible(c)),
                Modify = (c, f) => f.WithColumn(new ColFilter { TableId = tableId, ColumnId = columnId, Allowed = visible(c) })
            };
        }

        /// <summary>The codes that occur in the given rows.</summary>
        private static Bitset VisibleCodes(Column data, int[] rows)
        {
            var enc = data.Encoded;
            var bits = new Bitset(enc.Values.Length);
            var codes = enc.Codes;
            for (int i = 0; i < rows.Length; i++) bits.Set(codes[rows[i]]);
            return bits;
        }

        /// <summary>One row per code, standing in for "the distinct values of a column" wherever rows are required.</summary>
        private static int[] RepresentativeRows(Column data, Bitset codes)
        {
            var enc = data.Encoded;
            return codes.ToRows().Select(code => enc.FirstRow[code]).Where(r => r >= 0).ToArray();
        }

        /// <summary>Compiles FILTER(table, predicate). As a CALCULATE argument over a column it becomes a column filter;
        /// over a whole table it becomes a row filter, which costs a Bitset on the table per evaluation.</summary>
        private CompiledTable CompileFilter(CallNode call, Scope s)
        {
            var inner = CompileTable(call.Args[0], s);
            var pred = Compile(call.Args[1], s.WithRow(inner.TableId));
            var tableId = inner.TableId;
            var length = _ds.Tables[tableId].RowCount;

            TableEval rowsOf = c =>
            {
                var rows = inner.Rows(c);
                var it = c.Iterator(tableId);
                var keep = new List<int>();
                for (int i = 0; i < rows.Length; i++)
                {
                    it.SetRow(rows[i]);
                    if (pred(it).AsBool()) keep.Add(rows[i]);
                }
                return keep.ToArray();
            };

            if (inner.ColumnId != null)
            {
                var data = _ds.ColumnOf(tableId, inner.ColumnId);
                var columnId = inner.ColumnId;
                return new CompiledTable
                {
                    TableId = tableId,
                    ColumnId = columnId,
                    Rows = rowsOf,
                    Modify = (c, f) =>
                    {
                        var allowed = VisibleCodes(data, rowsOf(c));
                        return inner.Modify(c, f).WithColumn(new ColFilter { TableId = tableId, ColumnId = columnId, Allowed = allowed });
                    }
                };
            }

            return new CompiledTable
            {
                TableId = tableId,
                Rows = rowsOf,
                Modify = (c, f) =>
                {
                    var bits = new Bitset(length);
                    foreach (var r in rowsOf(c)) bits.Set(r);
                    return inner.Modify(c, f).WithRowFilter(new RowFilter { TableId = tableId, Rows = bits });
                }
            };
        }

        /// <summary>Compiles DATESYTD and SAMEPERIODLASTYEAR. Both turn the dates visible now into another date set by working on dictionary codes,
        /// then replace every filter on the date table with that set. <paramref name="site"/> is the call node, kept for error placement.</summary>
        private CompiledTable CompileTime(string fn, Node dateArg, Node site, Scope s)
        {
            TableDef table; ColumnDef col;
            RequireColumn(dateArg, s, out table, out col);
            if (!col.DataType.IsTemporal()) throw Err($"{fn} needs a date column", dateArg, s);
            if (table.DateTableColumnId == null)
                throw Err($"Time intelligence needs a marked date table; mark '{table.Name}' as the date table first", dateArg, s);

            var data = _ds.ColumnOf(table.Id, col.Id);
            var tableId = table.Id;
            var columnId = col.Id;
            var dateTable = new[] { tableId };

            Func<EvalCtx, Bitset> shift = c =>
            {
                var enc = data.Encoded;
                var visible = VisibleCodes(data, c.Rows(tableId));
                var result = new Bitset(enc.Values.Length);
                if (fn == "DATESYTD")
                {
                    int maxCode = 0;
                    foreach (var code in visible.ToRows()) if (code > maxCode) maxCode = code;
                    if (maxCode == 0) return result;
                    var end = enc.Values[maxCode].AsDate;
                    var start = new DateTime(end.Year, 1, 1);
                    for (int code = 1; code < enc.Values.Length; code++)
                    {
                        var d = enc.Values[code].AsDate;
                        if (d >= start && d <= end) result.Set(code);
                    }
                }
                else
                {
                    foreach (var code in visible.ToRows())
                    {
                        if (code == 0) continue;
                        var d = enc.Values[code].AsDate;
                        var shifted = d.AddYears(-1);
                        int prior = data.FindCode(shifted);
                        if (prior > 0) result.Set(prior);
                        // Month-end maps to month-end, so Feb 28 of a plain year also brings in Feb 29.
                        if (d.Day == DateTime.DaysInMonth(d.Year, d.Month)
                            && shifted.Day < DateTime.DaysInMonth(shifted.Year, shifted.Month))
                        {
                            int leap = data.FindCode(shifted.AddDays(1));
                            if (leap > 0) result.Set(leap);
                        }
                    }
                }
                return result;
            };

            return new CompiledTable
            {
                TableId = tableId,
                ColumnId = columnId,
                Rows = c => RepresentativeRows(data, shift(c)),
                // A date-table filter replaces Year, Month and so on, as with a marked table in DAX.
                Modify = (c, f) => f.WithoutTables(dateTable)
                    .WithColumn(new ColFilter { TableId = tableId, ColumnId = columnId, Allowed = shift(c) })
            };
        }
    }
}
