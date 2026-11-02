using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ExcelDataReader;
using Lumen.Engine;
using Lumen.Model;
using Newtonsoft.Json.Linq;

namespace Lumen.Connectors
{
    /// <summary>Columns on their way through the transform pipeline.</summary>
    public sealed class ColumnBatch
    {
        /// <summary>The columns, in order.</summary>
        public List<Column> Columns = new List<Column>();
        /// <summary>Rows, taken from the first column. All columns are the same length.</summary>
        public int RowCount => Columns.Count == 0 ? 0 : Columns[0].RowCount;
        /// <summary>Finds a column by name, ignoring case, or null.</summary>
        public Column Find(string name) =>
            Columns.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Turns all-text columns into typed ones. CSV reads everything as strings first; this runs at the end.</summary>
    public static class TypeInference
    {
        /// <summary>Picks a type by looking at distinct values only, so a late outlier in a big file
        /// is still seen, and each distinct string is parsed once.</summary>
        public static Column Infer(DictColumn raw)
        {
            var values = raw.Dictionary.Skip(1).Select(v => v.String).ToList();
            if (values.Count == 0) return raw;
            var type = Guess(values);
            if (type == LumenDataType.String) return raw;

            var converted = new Value[raw.Dictionary.Length];
            for (int i = 1; i < converted.Length; i++)
                converted[i] = Column.Coerce(raw.Dictionary[i], type);

            var builder = ColumnBuilder.Create(type, raw.RowCount);
            var codes = raw.Codes;
            for (int r = 0; r < codes.Length; r++) builder.AddValue(converted[codes[r]]);
            return builder.Finish(raw.Name);
        }

        /// <summary>Tests every candidate type against each value and drops those that fail. The first survivor in the order Int64, Double, Boolean, Date wins.
        /// Dates need at least eight characters, so that a short value such as "1/2" is not read as a date.</summary>
        private static LumenDataType Guess(List<string> values)
        {
            bool isLong = true, isDouble = true, isBool = true, isDate = true;
            foreach (var raw in values)
            {
                var s = raw.Trim();
                if (s.Length == 0) continue;
                long l; double d; DateTime dt; bool b;
                if (isLong && !long.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out l)) isLong = false;
                if (isDouble && !double.TryParse(s, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out d)) isDouble = false;
                if (isBool && !bool.TryParse(s, out b)) isBool = false;
                if (isDate && !(s.Length >= 8 && DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out dt))) isDate = false;
                if (!isLong && !isDouble && !isBool && !isDate) return LumenDataType.String;
            }
            if (isLong) return LumenDataType.Int64;
            if (isDouble) return LumenDataType.Double;
            if (isBool) return LumenDataType.Boolean;
            if (isDate) return LumenDataType.Date;
            return LumenDataType.String;
        }
    }

    /// <summary>Reads delimited text. Values are interned per column as they stream in, so memory follows the distinct count, not the file size.</summary>
    public static class CsvConnector
    {
        /// <summary>Reads a file. The StreamReader detects a BOM and defaults to UTF-8.</summary>
        public static ColumnBatch Read(string path, bool hasHeaders, CancellationToken ct)
        {
            using (var reader = new StreamReader(path, System.Text.Encoding.UTF8, true, 1 << 16))
                return Read(reader, hasHeaders, ct);
        }

        /// <summary>Reads from any text source; tests use a StringReader. The first row fixes the column count, and extra cells in later rows are ignored.</summary>
        public static ColumnBatch Read(TextReader reader, bool hasHeaders, CancellationToken ct)
        {
            var text = new CsvTokenizer(reader);
            List<string> first = text.NextRow();
            if (first == null) return new ColumnBatch();

            var names = new List<string>();
            var builders = new List<DictBuilder>();
            for (int i = 0; i < first.Count; i++)
            {
                names.Add(hasHeaders ? Unique(first[i], names) : "Column" + (i + 1));
                builders.Add(new DictBuilder());
            }
            if (!hasHeaders) AddRow(first, builders);

            List<string> row;
            int n = 0;
            while ((row = text.NextRow()) != null)
            {
                if ((++n & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();
                // A blank line.
                if (row.Count == 1 && row[0].Length == 0) continue;
                AddRow(row, builders);
            }

            var batch = new ColumnBatch();
            for (int i = 0; i < builders.Count; i++)
                batch.Columns.Add(TypeInference.Infer((DictColumn)builders[i].Finish(names[i])));
            return batch;
        }

        /// <summary>Cleans a header and makes it unique: "Name", "Name.2", "Name.3".</summary>
        private static string Unique(string name, List<string> taken)
        {
            name = string.IsNullOrWhiteSpace(name) ? "Column" + (taken.Count + 1) : name.Trim();
            string candidate = name;
            for (int i = 2; taken.Contains(candidate, StringComparer.OrdinalIgnoreCase); i++) candidate = name + "." + i;
            return candidate;
        }

        /// <summary>Appends a row. Missing and empty cells become BLANK.</summary>
        private static void AddRow(List<string> row, List<DictBuilder> builders)
        {
            for (int i = 0; i < builders.Count; i++)
            {
                string v = i < row.Count ? row[i] : null;
                builders[i].Add(string.IsNullOrEmpty(v) ? null : v);
            }
        }
    }

    /// <summary>RFC 4180 tokenizer. The delimiter (comma, semicolon or tab) comes from the first line.</summary>
    internal sealed class CsvTokenizer
    {
        private readonly TextReader _r;
        private readonly char[] _buf = new char[1 << 16];
        private int _pos, _len;
        private char _delimiter;
        private readonly StringBuilder _field = new StringBuilder();

        /// <summary>Wraps a reader. The buffer stands in for per-char virtual calls on the reader.</summary>
        public CsvTokenizer(TextReader r) { _r = r; }

        /// <summary>The next char, or -1 at the end.</summary>
        private int Read()
        {
            if (_pos == _len)
            {
                _len = _r.Read(_buf, 0, _buf.Length);
                _pos = 0;
                if (_len <= 0) { _len = 0; return -1; }
            }
            return _buf[_pos++];
        }

        /// <summary>The next char without consuming it, or -1 at the end.</summary>
        private int Peek()
        {
            if (_pos == _len)
            {
                _len = _r.Read(_buf, 0, _buf.Length);
                _pos = 0;
                if (_len <= 0) { _len = 0; return -1; }
            }
            return _buf[_pos];
        }

        /// <summary>Counts candidates on the first line in the buffer. Ties go to the comma.</summary>
        private void DetectDelimiter()
        {
            Peek();
            int comma = 0, semi = 0, tab = 0;
            for (int i = _pos; i < _len && _buf[i] != '\n'; i++)
            {
                if (_buf[i] == ',') comma++;
                else if (_buf[i] == ';') semi++;
                else if (_buf[i] == '\t') tab++;
            }
            _delimiter = tab > comma && tab > semi ? '\t' : semi > comma ? ';' : ',';
        }

        /// <summary>The next row's fields, or null at the end. Quoted fields may contain delimiters, doubled quotes and line breaks.</summary>
        public List<string> NextRow()
        {
            if (_delimiter == '\0') DetectDelimiter();
            if (Peek() < 0) return null;

            var row = new List<string>();
            while (true)
            {
                _field.Clear();
                int c = Read();
                if (c == '"')
                {
                    while (true)
                    {
                        // Past the closing quote: anything up to the next delimiter or line end is dropped.
                        c = Read();
                        if (c < 0) break;
                        if (c == '"')
                        {
                            if (Peek() == '"') { _field.Append('"'); Read(); continue; }
                            break;
                        }
                        _field.Append((char)c);
                    }
                    c = Read();
                    while (c >= 0 && c != _delimiter && c != '\n' && c != '\r') c = Read();
                }
                else
                {
                    while (c >= 0 && c != _delimiter && c != '\n' && c != '\r')
                    {
                        _field.Append((char)c);
                        c = Read();
                    }
                }
                row.Add(_field.ToString());
                if (c == _delimiter) continue;
                if (c == '\r' && Peek() == '\n') Read();
                return row;
            }
        }
    }

    /// <summary>Holds boxed values for sources that hand us objects; acceptable at Excel and JSON sizes.</summary>
    internal sealed class ObjectColumns
    {
        private readonly List<string> _names = new List<string>();
        private readonly List<List<object>> _cols = new List<List<object>>();
        /// <summary>Rows finished so far.</summary>
        public int Rows;

        /// <summary>The index of a column, adding it if new. A column that first appears late gets nulls for the rows before it.</summary>
        public int ColumnIndex(string name)
        {
            int i = _names.FindIndex(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) return i;
            _names.Add(name);
            var list = new List<object>(new object[Rows]);
            _cols.Add(list);
            return _names.Count - 1;
        }

        /// <summary>Sets a cell of the current row.</summary>
        public void Set(int col, object v)
        {
            var list = _cols[col];
            while (list.Count < Rows) list.Add(null);
            list.Add(v);
        }

        /// <summary>Finishes the row, padding columns the row did not mention.</summary>
        public void EndRow()
        {
            Rows++;
            foreach (var c in _cols) while (c.Count < Rows) c.Add(null);
        }

        /// <summary>Types each column and builds the batch. String columns still go through inference, since Excel often stores numbers as text.</summary>
        public ColumnBatch ToBatch()
        {
            var batch = new ColumnBatch();
            for (int i = 0; i < _names.Count; i++)
            {
                var type = Pick(_cols[i]);
                if (type == LumenDataType.String)
                {
                    var b = new DictBuilder();
                    foreach (var o in _cols[i]) b.AddValue(Value.FromObject(o));
                    batch.Columns.Add(TypeInference.Infer((DictColumn)b.Finish(_names[i])));
                }
                else
                {
                    var b = ColumnBuilder.Create(type, _cols[i].Count);
                    foreach (var o in _cols[i]) b.AddValue(Value.FromObject(o));
                    batch.Columns.Add(b.Finish(_names[i]));
                }
            }
            return batch;
        }

        /// <summary>Picks a type from CLR types of the values. Excel reports all numbers as double, so whole doubles count as Int64.</summary>
        private static LumenDataType Pick(List<object> values)
        {
            bool any = false, allLong = true, allNumber = true, allBool = true, allDate = true;
            foreach (var o in values)
            {
                if (o == null || o is DBNull) continue;
                any = true;
                bool isLong = o is long || o is int || o is short || o is byte;
                if (o is double d && d == Math.Floor(d) && Math.Abs(d) < 1e15) isLong = true;
                allLong &= isLong;
                allNumber &= isLong || o is double || o is float || o is decimal;
                allBool &= o is bool;
                allDate &= o is DateTime;
            }
            if (!any) return LumenDataType.String;
            if (allLong) return LumenDataType.Int64;
            if (allNumber) return LumenDataType.Double;
            if (allBool) return LumenDataType.Boolean;
            if (allDate) return LumenDataType.Date;
            return LumenDataType.String;
        }
    }

    /// <summary>Reads .xlsx and .xls through ExcelDataReader.</summary>
    public static class ExcelConnector
    {
        /// <summary>Lists the worksheets, for the sheet picker.</summary>
        public static List<string> SheetNames(string path)
        {
            var names = new List<string>();
            using (var s = OpenShared(path))
            using (var r = ExcelReaderFactory.CreateReader(s))
                do names.Add(r.Name); while (r.NextResult());
            return names;
        }

        /// <summary>Opens with shared read/write, so a workbook open in Excel can still be read.</summary>
        private static FileStream OpenShared(string path) =>
            new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        /// <summary>Reads one sheet, or the first if <paramref name="sheet"/> is null.</summary>
        public static ColumnBatch Read(string path, string sheet, bool hasHeaders, CancellationToken ct)
        {
            using (var s = OpenShared(path))
            using (var r = ExcelReaderFactory.CreateReader(s))
            {
                do
                {
                    if (sheet == null || string.Equals(r.Name, sheet, StringComparison.OrdinalIgnoreCase))
                        return ReadSheet(r, hasHeaders, ct);
                } while (r.NextResult());
            }
            throw new InvalidOperationException($"Sheet '{sheet}' was not found");
        }

        /// <summary>Reads rows into <see cref="ObjectColumns"/>. Rows with no values at all are skipped.</summary>
        private static ColumnBatch ReadSheet(IExcelDataReader r, bool hasHeaders, CancellationToken ct)
        {
            var o = new ObjectColumns();
            var headers = new List<string>();
            bool first = true;
            while (r.Read())
            {
                ct.ThrowIfCancellationRequested();
                if (first && hasHeaders)
                {
                    for (int i = 0; i < r.FieldCount; i++)
                    {
                        var h = Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture);
                        headers.Add(string.IsNullOrWhiteSpace(h) ? "Column" + (i + 1) : h.Trim());
                    }
                    first = false;
                    continue;
                }
                first = false;
                bool any = false;
                for (int i = 0; i < r.FieldCount; i++)
                {
                    var v = r.GetValue(i);
                    if (v == null) continue;
                    any = true;
                    o.Set(o.ColumnIndex(i < headers.Count ? headers[i] : "Column" + (i + 1)), v);
                }
                if (any) o.EndRow();
            }
            return o.ToBatch();
        }
    }

    /// <summary>Reads a JSON array of objects, or the first array property of an object, such as {"data": [...]}.</summary>
    public static class JsonConnector
    {
        /// <summary>Reads a file, whole, into a JToken tree. Fine at JSON sizes people actually load, but not streaming.</summary>
        public static ColumnBatch Read(string path, CancellationToken ct)
        {
            var token = JToken.Parse(File.ReadAllText(path));
            if (token is JObject obj)
            {
                var arrayProp = obj.Properties().FirstOrDefault(p => p.Value is JArray);
                if (arrayProp == null) throw new InvalidOperationException("The JSON has no array of records");
                token = arrayProp.Value;
            }
            var array = token as JArray;
            if (array == null) throw new InvalidOperationException("The JSON must be an array of objects");

            var o = new ObjectColumns();
            foreach (var item in array.OfType<JObject>())
            {
                ct.ThrowIfCancellationRequested();
                foreach (var kv in Flatten(item, ""))
                    o.Set(o.ColumnIndex(kv.Key), kv.Value);
                o.EndRow();
            }
            return o.ToBatch();
        }

        /// <summary>Walks an object. Nested objects become dotted names, as in address.city. Nested arrays are dropped.</summary>
        private static IEnumerable<KeyValuePair<string, object>> Flatten(JObject item, string prefix)
        {
            foreach (var p in item.Properties())
            {
                if (p.Value is JObject inner)
                {
                    foreach (var kv in Flatten(inner, prefix + p.Name + ".")) yield return kv;
                }
                else if (p.Value is JValue v)
                {
                    yield return new KeyValuePair<string, object>(prefix + p.Name, v.Value);
                }
            }
        }
    }

    /// <summary>Reads SQL Server through System.Data.SqlClient, always with Windows authentication as the dialog builds it.</summary>
    public static class SqlConnector
    {
        /// <summary>Databases the current Windows user can open: user databases first, then the system ones.</summary>
        public static async Task<List<string>> DatabasesAsync(string server, CancellationToken ct)
        {
            var cs = new SqlConnectionStringBuilder { DataSource = server, InitialCatalog = "master", IntegratedSecurity = true, ConnectTimeout = 5 };
            const string sql = "SELECT name FROM sys.databases WHERE state = 0 AND HAS_DBACCESS(name) = 1 " +
                               "ORDER BY CASE WHEN database_id <= 4 THEN 1 ELSE 0 END, name";
            var names = new List<string>();
            using (var cn = new SqlConnection(cs.ConnectionString))
            using (var cmd = new SqlCommand(sql, cn) { CommandTimeout = 10 })
            {
                await cn.OpenAsync(ct);
                using (var r = await cmd.ExecuteReaderAsync(ct))
                    while (await r.ReadAsync(ct)) names.Add(r.GetString(0));
            }
            return names;
        }

        /// <summary>Runs a query or reads a table. Text starting with SELECT or WITH is passed through as written, so the file's author decides what runs.
        /// Anything else is treated as a table name and quoted.</summary>
        public static ColumnBatch Read(string connectionString, string queryOrTable, CancellationToken ct)
        {
            string sql = queryOrTable.TrimStart().StartsWith("select", StringComparison.OrdinalIgnoreCase)
                      || queryOrTable.TrimStart().StartsWith("with", StringComparison.OrdinalIgnoreCase)
                ? queryOrTable
                : "SELECT * FROM " + QuoteName(queryOrTable);

            using (var cn = new SqlConnection(connectionString))
            using (var cmd = new SqlCommand(sql, cn) { CommandTimeout = 300 })
            {
                cn.Open();
                // cmd.Cancel is the only way to interrupt a synchronous read in flight.
                using (ct.Register(cmd.Cancel))
                using (var r = cmd.ExecuteReader(CommandBehavior.SequentialAccess))
                {
                    var builders = new ColumnBuilder[r.FieldCount];
                    var names = new string[r.FieldCount];
                    var types = new LumenDataType[r.FieldCount];
                    for (int i = 0; i < r.FieldCount; i++)
                    {
                        names[i] = r.GetName(i);
                        types[i] = Map(r.GetFieldType(i));
                        builders[i] = ColumnBuilder.Create(types[i], 1024);
                    }
                    // Streamed into typed builders; a DataTable would box every value.
                    while (r.Read())
                        for (int i = 0; i < builders.Length; i++)
                            builders[i].AddValue(r.IsDBNull(i) ? Value.Blank : Value.FromObject(r.GetValue(i)));

                    var batch = new ColumnBatch();
                    for (int i = 0; i < builders.Length; i++) batch.Columns.Add(builders[i].Finish(names[i]));
                    return batch;
                }
            }
        }

        /// <summary>Accepts schema.table and brackets it. A user-typed table name never reaches SQL unquoted.</summary>
        private static string QuoteName(string name) =>
            string.Join(".", name.Split('.').Select(p => "[" + p.Trim().Trim('[', ']').Replace("]", "]]") + "]"));

        /// <summary>Maps a CLR field type to a column type. Types not listed, such as Guid, arrive as strings.</summary>
        private static LumenDataType Map(Type t)
        {
            if (t == typeof(long) || t == typeof(int) || t == typeof(short) || t == typeof(byte)) return LumenDataType.Int64;
            if (t == typeof(decimal)) return LumenDataType.Decimal;
            if (t == typeof(double) || t == typeof(float)) return LumenDataType.Double;
            if (t == typeof(bool)) return LumenDataType.Boolean;
            if (t == typeof(DateTime) || t == typeof(DateTimeOffset)) return LumenDataType.DateTime;
            return LumenDataType.String;
        }
    }
}
