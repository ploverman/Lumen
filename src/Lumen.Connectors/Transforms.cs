using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Lumen.Engine;
using Lumen.Model;

namespace Lumen.Connectors
{
    /// <summary>The transform step vocabulary. These strings go into report files, so they must not change.</summary>
    public static class StepKinds
    {
        /// <summary>Use the first row as column names. No arguments.</summary>
        public const string PromoteHeaders = "PromoteHeaders";
        /// <summary>Args: column, type (a <see cref="LumenDataType"/> name).</summary>
        public const string ChangeType = "ChangeType";
        /// <summary>Args: column, name.</summary>
        public const string Rename = "Rename";
        /// <summary>Args: columns ('|' separated).</summary>
        public const string RemoveColumns = "RemoveColumns";
        /// <summary>Args: column, op (a <see cref="ConditionOp"/> name), value.</summary>
        public const string FilterRows = "FilterRows";
        /// <summary>Args: column, find, replace.</summary>
        public const string ReplaceValues = "ReplaceValues";
        /// <summary>Args: column, delimiter.</summary>
        public const string SplitColumn = "SplitColumn";
        /// <summary>Args: name, expression (LQL), table (the name the expression may use).</summary>
        public const string AddCustomColumn = "AddCustomColumn";
        /// <summary>Args: columns ('|' separated); empty means all columns.</summary>
        public const string RemoveDuplicates = "RemoveDuplicates";
        /// <summary>Args: columns ('|' separated), attribute, value (the two new column names).</summary>
        public const string Unpivot = "Unpivot";

        /// <summary>Makes a step from alternating argument keys and values.</summary>
        public static TransformStepDef Create(string kind, string label, params string[] keyValues)
        {
            var s = new TransformStepDef { Kind = kind, Label = label };
            for (int i = 0; i + 1 < keyValues.Length; i += 2) s.Args[keyValues[i]] = keyValues[i + 1];
            return s;
        }
    }

    /// <summary>Applies steps. Each step takes a batch and returns a new one; columns are immutable, so unchanged ones are shared by reference.</summary>
    public static class TransformEngine
    {
        /// <summary>Reads a step argument.</summary>
        private static string Arg(TransformStepDef s, string key, string fallback = null)
        {
            string v;
            return s.Args.TryGetValue(key, out v) ? v : fallback;
        }

        /// <summary>Reads a '|' separated argument. Column names containing '|' cannot be listed.</summary>
        private static List<string> ListArg(TransformStepDef s, string key) =>
            (Arg(s, key) ?? "").Split('|').Where(x => x.Length > 0).ToList();

        /// <summary>Finds a column or fails with a message for the Applied steps list.</summary>
        private static Column Require(ColumnBatch b, string name)
        {
            var c = b.Find(name ?? "");
            if (c == null) throw new InvalidOperationException($"Column '{name}' was not found");
            return c;
        }

        /// <summary>A copy of the batch with one column swapped.</summary>
        private static ColumnBatch Replace(ColumnBatch b, Column old, Column @new)
        {
            var r = new ColumnBatch { Columns = new List<Column>(b.Columns) };
            r.Columns[r.Columns.IndexOf(old)] = @new;
            return r;
        }

        /// <summary>A copy of the batch keeping the given rows.</summary>
        private static ColumnBatch TakeRows(ColumnBatch b, int[] rows) =>
            new ColumnBatch { Columns = b.Columns.Select(c => c.Take(rows)).ToList() };

        /// <summary>Rebuilds a column through a function, row by row.</summary>
        private static Column Map(Column c, LumenDataType type, Func<Value, Value> f)
        {
            var b = ColumnBuilder.Create(type, c.RowCount);
            for (int r = 0; r < c.RowCount; r++) b.AddValue(f(c.Get(r)));
            return b.Finish(c.Name);
        }

        /// <summary>Runs one step. To add a step kind, add a constant to <see cref="StepKinds"/>, a case here, and a button in TransformWindow.</summary>
        public static ColumnBatch Apply(ColumnBatch b, TransformStepDef s, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            switch (s.Kind)
            {
                case StepKinds.PromoteHeaders: return PromoteHeaders(b);
                case StepKinds.ChangeType:
                {
                    var c = Require(b, Arg(s, "column"));
                    var type = (LumenDataType)Enum.Parse(typeof(LumenDataType), Arg(s, "type"));
                    return Replace(b, c, Map(c, type, v => Column.Coerce(v, type)));
                }
                case StepKinds.Rename:
                {
                    var c = Require(b, Arg(s, "column"));
                    // Take makes a new object, since Column.Name is mutable and the old column may be shared with an earlier batch.
                    var renamed = c.Take(Enumerable.Range(0, c.RowCount).ToArray());
                    renamed.Name = Arg(s, "name");
                    return Replace(b, c, renamed);
                }
                case StepKinds.RemoveColumns:
                {
                    var names = ListArg(s, "columns");
                    return new ColumnBatch { Columns = b.Columns.Where(c => !names.Contains(c.Name, StringComparer.OrdinalIgnoreCase)).ToList() };
                }
                case StepKinds.FilterRows: return FilterRows(b, s);
                case StepKinds.ReplaceValues:
                {
                    var c = Require(b, Arg(s, "column"));
                    var find = Column.Coerce(Value.FromString(Arg(s, "find")), c.Type);
                    var with = Column.Coerce(Value.FromString(Arg(s, "replace")), c.Type);
                    return Replace(b, c, Map(c, c.Type, v => Value.Compare(v, find) == 0 && v.IsBlank == find.IsBlank ? with : v));
                }
                case StepKinds.SplitColumn: return Split(b, s);
                case StepKinds.AddCustomColumn: return AddCustom(b, s, ct);
                case StepKinds.RemoveDuplicates: return RemoveDuplicates(b, s);
                case StepKinds.Unpivot: return Unpivot(b, s);
            }
            throw new InvalidOperationException($"Unknown step '{s.Kind}'");
        }

        /// <summary>Moves the first row into the names, then re-infers types, since the header no longer forces a column to text.</summary>
        private static ColumnBatch PromoteHeaders(ColumnBatch b)
        {
            if (b.RowCount == 0) return b;
            var rows = Enumerable.Range(1, b.RowCount - 1).ToArray();
            var result = new ColumnBatch();
            foreach (var c in b.Columns)
            {
                var name = c.Get(0).IsBlank ? c.Name : c.Get(0).ToDisplayString();
                var rest = c.Take(rows);
                rest.Name = name;
                result.Columns.Add(rest is DictColumn d ? TypeInference.Infer(Reencode(d)) : rest);
            }
            return result;
        }

        /// <summary>Rebuilds a dictionary, dropping the entry that only the header row used.</summary>
        private static DictColumn Reencode(DictColumn d)
        {
            var b = new DictBuilder();
            for (int r = 0; r < d.RowCount; r++) b.AddValue(d.Get(r));
            return (DictColumn)b.Finish(d.Name);
        }

        /// <summary>Keeps rows passing one condition. It tests row by row: transforms favor simplicity over speed.</summary>
        private static ColumnBatch FilterRows(ColumnBatch b, TransformStepDef s)
        {
            var c = Require(b, Arg(s, "column"));
            var op = (ConditionOp)Enum.Parse(typeof(ConditionOp), Arg(s, "op", "Equals"));
            var filter = new FilterDef { Type = FilterType.Advanced, Conditions = { new Condition { Op = op, Value = Arg(s, "value") } } };
            var keep = new List<int>();
            for (int r = 0; r < c.RowCount; r++)
                if (RowFilterTest(c.Get(r), filter.Conditions[0], c.Type)) keep.Add(r);
            return TakeRows(b, keep.ToArray());
        }

        /// <summary>Row-level condition test. Unlike the query filter, Equals never matches a BLANK cell, and NotEquals always does.</summary>
        private static bool RowFilterTest(Value v, Condition cond, LumenDataType type)
        {
            var rhs = Column.Coerce(Value.FromObject(cond.Value), type);
            string text = v.IsBlank ? "" : v.ToDisplayString();
            string needle = Convert.ToString(cond.Value) ?? "";
            switch (cond.Op)
            {
                case ConditionOp.IsBlank: return v.IsBlank;
                case ConditionOp.IsNotBlank: return !v.IsBlank;
                case ConditionOp.Equals: return !v.IsBlank && Value.Compare(v, rhs) == 0;
                case ConditionOp.NotEquals: return v.IsBlank || Value.Compare(v, rhs) != 0;
                case ConditionOp.LessThan: return !v.IsBlank && Value.Compare(v, rhs) < 0;
                case ConditionOp.LessOrEqual: return !v.IsBlank && Value.Compare(v, rhs) <= 0;
                case ConditionOp.GreaterThan: return !v.IsBlank && Value.Compare(v, rhs) > 0;
                case ConditionOp.GreaterOrEqual: return !v.IsBlank && Value.Compare(v, rhs) >= 0;
                case ConditionOp.Contains: return text.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
                case ConditionOp.NotContains: return text.IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0;
                case ConditionOp.StartsWith: return text.StartsWith(needle, StringComparison.OrdinalIgnoreCase);
            }
            return true;
        }

        /// <summary>Splits a column into as many as the widest row needs: Name.1, Name.2 and so on. The original is removed and types are inferred per part.</summary>
        private static ColumnBatch Split(ColumnBatch b, TransformStepDef s)
        {
            var c = Require(b, Arg(s, "column"));
            string delimiter = Arg(s, "delimiter", ",");
            var parts = new List<DictBuilder>();
            for (int r = 0; r < c.RowCount; r++)
            {
                var v = c.Get(r);
                var pieces = v.IsBlank ? new string[0] : v.ToDisplayString().Split(new[] { delimiter }, StringSplitOptions.None);
                while (parts.Count < pieces.Length)
                {
                    var nb = new DictBuilder();
                    for (int i = 0; i < r; i++) nb.Add(null);
                    parts.Add(nb);
                }
                for (int p = 0; p < parts.Count; p++)
                    parts[p].Add(p < pieces.Length ? pieces[p] : null);
            }
            var r2 = new ColumnBatch { Columns = new List<Column>(b.Columns) };
            int at = r2.Columns.IndexOf(c);
            r2.Columns.RemoveAt(at);
            for (int p = 0; p < parts.Count; p++)
                r2.Columns.Insert(at + p, TypeInference.Infer((DictColumn)parts[p].Finish(c.Name + "." + (p + 1))));
            return r2;
        }

        /// <summary>Adds a column computed from an LQL row expression. Each run builds a throwaway one-table model, so the expression sees only this batch.
        /// The new type comes from the first non-blank result; later values coerce to it.</summary>
        private static ColumnBatch AddCustom(ColumnBatch b, TransformStepDef s, CancellationToken ct)
        {
            string name = Arg(s, "name", "Custom");
            string tableName = Arg(s, "table", "Table");
            var def = new TableDef { Id = "t", Name = tableName };
            var data = new TableData("t", tableName);
            int i = 0;
            foreach (var c in b.Columns)
            {
                string id = "c" + i++;
                def.Columns.Add(new ColumnDef { Id = id, Name = c.Name, DataType = c.Type });
                data.Add(id, c);
            }
            var model = new SemanticModel { Tables = { def } };
            var ds = new Dataset(model, new[] { data });
            Eval eval;
            try { eval = ds.Compiler.CompileRowExpression(Arg(s, "expression"), "t"); }
            catch (LqlException ex) { throw new InvalidOperationException(ex.Message); }

            var scope = new QueryScope(ds, ct);
            var it = new EvalCtx(scope, FilterContext.Empty).Iterator("t");
            int rows = b.RowCount;
            var values = new Value[rows];
            var kind = ValueKind.Blank;
            for (int r = 0; r < rows; r++)
            {
                it.SetRow(r);
                values[r] = eval(it);
                if (kind == ValueKind.Blank) kind = values[r].Kind;
            }
            LumenDataType type;
            switch (kind)
            {
                case ValueKind.Integer: type = LumenDataType.Int64; break;
                case ValueKind.Number: type = LumenDataType.Double; break;
                case ValueKind.Boolean: type = LumenDataType.Boolean; break;
                case ValueKind.Date: type = LumenDataType.Date; break;
                default: type = LumenDataType.String; break;
            }
            var builder = ColumnBuilder.Create(type, rows);
            foreach (var v in values) builder.AddValue(v);
            var result = new ColumnBatch { Columns = new List<Column>(b.Columns) };
            result.Columns.Add(builder.Finish(name));
            return result;
        }

        /// <summary>Keeps the first row per key. The key packs each column's dictionary code into two chars, a compact way to hash a tuple without boxing.</summary>
        private static ColumnBatch RemoveDuplicates(ColumnBatch b, TransformStepDef s)
        {
            var names = ListArg(s, "columns");
            var cols = names.Count == 0 ? b.Columns : names.Select(n => Require(b, n)).ToList();
            var codes = cols.Select(c => c.Encoded.Codes).ToArray();
            var seen = new HashSet<string>();
            var keep = new List<int>();
            var buffer = new char[codes.Length * 2];
            for (int r = 0; r < b.RowCount; r++)
            {
                for (int j = 0; j < codes.Length; j++)
                {
                    int code = codes[j][r];
                    buffer[j * 2] = (char)(code & 0xFFFF);
                    buffer[j * 2 + 1] = (char)(code >> 16);
                }
                if (seen.Add(new string(buffer))) keep.Add(r);
            }
            return TakeRows(b, keep.ToArray());
        }

        /// <summary>Turns columns into attribute/value rows, repeating the other columns. BLANK cells produce no row. Values are Double if all sources are numeric, else String.</summary>
        private static ColumnBatch Unpivot(ColumnBatch b, TransformStepDef s)
        {
            var names = ListArg(s, "columns");
            var unpivoted = names.Select(n => Require(b, n)).ToList();
            var keys = b.Columns.Where(c => !unpivoted.Contains(c)).ToList();
            if (unpivoted.Count == 0) return b;

            var attribute = new DictBuilder();
            var valueType = unpivoted.All(c => c.Type.IsNumeric()) ? LumenDataType.Double : LumenDataType.String;
            var value = ColumnBuilder.Create(valueType, b.RowCount * unpivoted.Count);
            var rows = new List<int>(b.RowCount * unpivoted.Count);
            for (int r = 0; r < b.RowCount; r++)
            {
                foreach (var c in unpivoted)
                {
                    var v = c.Get(r);
                    if (v.IsBlank) continue;
                    rows.Add(r);
                    attribute.Add(c.Name);
                    value.AddValue(Column.Coerce(v, valueType));
                }
            }
            var take = rows.ToArray();
            var result = new ColumnBatch { Columns = keys.Select(k => k.Take(take)).ToList() };
            result.Columns.Add(attribute.Finish(Arg(s, "attribute", "Attribute")));
            result.Columns.Add(value.Finish(Arg(s, "value", "Value")));
            return result;
        }
    }

    /// <summary>Source plus applied steps, turned into a TableData bound to a TableDef.</summary>
    public static class TableLoader
    {
        /// <summary>Reads a table's source, before any steps. Sample tables regenerate at 50,000 sales rows, whatever size they started at.</summary>
        public static ColumnBatch ReadSource(DataSourceRef src, CancellationToken ct)
        {
            switch (src.Kind)
            {
                case "csv": return CsvConnector.Read(src.Path, src.HasHeaders, ct);
                case "excel": return ExcelConnector.Read(src.Path, src.Sheet, src.HasHeaders, ct);
                case "json": return JsonConnector.Read(src.Path, ct);
                case "sql": return SqlConnector.Read(src.ConnectionString, src.Query, ct);
                case "sample": return SampleData.Batch(src.Query, 50000);
            }
            throw new InvalidOperationException($"Unknown source '{src.Kind}'");
        }

        /// <summary>Runs the steps in order.</summary>
        public static ColumnBatch ApplySteps(ColumnBatch batch, IEnumerable<TransformStepDef> steps, CancellationToken ct)
        {
            foreach (var s in steps) batch = TransformEngine.Apply(batch, s, ct);
            return batch;
        }

        /// <summary>Keeps existing column ids and settings by name, so reports survive a refresh.</summary>
        public static TableData Bind(TableDef def, ColumnBatch batch)
        {
            var data = new TableData(def.Id, def.Name);
            var old = def.Columns.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
            var columns = new List<ColumnDef>();
            foreach (var c in batch.Columns)
            {
                ColumnDef cd;
                if (!old.TryGetValue(c.Name, out cd))
                {
                    cd = new ColumnDef
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        Name = c.Name,
                        SummarizeBy = c.Type.IsNumeric() && !LooksLikeKey(c.Name) ? SummarizeBy.Sum : SummarizeBy.None
                    };
                }
                cd.DataType = c.Type;
                columns.Add(cd);
                data.Add(cd.Id, c);
            }
            def.Columns = columns;
            return data;
        }

        /// <summary>Key and id columns should group, not sum, so they default to Don't summarize.</summary>
        private static bool LooksLikeKey(string name) =>
            name.EndsWith("Key", StringComparison.OrdinalIgnoreCase) || name.EndsWith("Id", StringComparison.OrdinalIgnoreCase);

        /// <summary>The whole pipeline: read, transform, bind.</summary>
        public static TableData Load(TableDef def, CancellationToken ct) =>
            Bind(def, ApplySteps(ReadSource(def.Source, ct), def.Steps, ct));

        /// <summary>Relates tables whose columns share a name and type, with unique values on the one side.</summary>
        public static List<RelationshipDef> DetectRelationships(SemanticModel model, IDictionary<string, TableData> data)
        {
            var found = new List<RelationshipDef>();
            foreach (var a in model.Tables)
            foreach (var b in model.Tables)
            {
                if (a == b) continue;
                foreach (var ca in a.Columns)
                {
                    var cb = b.ColumnByName(ca.Name);
                    if (cb == null || cb.DataType != ca.DataType) continue;
                    if (model.Relationships.Concat(found).Any(r =>
                        (r.FromTableId == a.Id && r.ToTableId == b.Id) || (r.FromTableId == b.Id && r.ToTableId == a.Id))) continue;
                    TableData da, db;
                    if (!data.TryGetValue(a.Id, out da) || !data.TryGetValue(b.Id, out db)) continue;
                    var one = db.ById[cb.Id];
                    var many = da.ById[ca.Id];
                    // The one side must be the smaller table, with all-distinct values and no blanks.
                    if (many.RowCount < one.RowCount) continue;
                    if (one.Encoded.Values.Length - 1 != one.RowCount) continue;
                    found.Add(new RelationshipDef
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        FromTableId = a.Id, FromColumnId = ca.Id,
                        ToTableId = b.Id, ToColumnId = cb.Id,
                        Cardinality = Cardinality.ManyToOne
                    });
                }
            }
            return found;
        }
    }
}
