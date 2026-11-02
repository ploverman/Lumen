using System;
using System.Globalization;

namespace Lumen.Model
{
    /// <summary>Runtime type tag of a <see cref="Value"/>. <see cref="Blank"/> is zero, so default(Value) is BLANK.</summary>
    public enum ValueKind : byte { Blank, Number, Integer, String, Boolean, Date }

    /// <summary>A scalar in the LQL type system. BLANK is a real value (default(Value)), as in DAX.</summary>
    public readonly struct Value
    {
        /// <summary>Which payload field is meaningful.</summary>
        public readonly ValueKind Kind;
        /// <summary>Payload when <see cref="Kind"/> is <see cref="ValueKind.Number"/>.</summary>
        public readonly double Number;
        /// <summary>The integer payload, also used for date ticks and booleans.</summary>
        public readonly long Integer;   // Integer payload, Date ticks, Boolean as 0/1
        /// <summary>Payload when <see cref="Kind"/> is <see cref="ValueKind.String"/>.</summary>
        public readonly string String;

        private Value(ValueKind kind, double number, long integer, string text)
        {
            Kind = kind; Number = number; Integer = integer; String = text;
        }

        /// <summary>The BLANK value. Equal to default(Value).</summary>
        public static readonly Value Blank = default(Value);
        /// <summary>Wraps a double.</summary>
        public static Value FromNumber(double v) => new Value(ValueKind.Number, v, 0, null);
        /// <summary>Wraps a long.</summary>
        public static Value FromInteger(long v) => new Value(ValueKind.Integer, 0, v, null);
        /// <summary>Wraps a string. A null string becomes BLANK, so the String payload is never null on a String value.</summary>
        public static Value FromString(string v) => v == null ? Blank : new Value(ValueKind.String, 0, 0, v);
        /// <summary>Wraps a bool, stored as 0 or 1 in <see cref="Integer"/>.</summary>
        public static Value FromBoolean(bool v) => new Value(ValueKind.Boolean, 0, v ? 1 : 0, null);
        /// <summary>Wraps a DateTime, keeping its ticks. The Kind of the DateTime is dropped.</summary>
        public static Value FromDate(DateTime v) => new Value(ValueKind.Date, 0, v.Ticks, null);
        /// <summary>Wraps raw ticks. Lets date columns avoid a DateTime round trip per row.</summary>
        public static Value FromDateTicks(long ticks) => new Value(ValueKind.Date, 0, ticks, null);

        /// <summary>True for BLANK.</summary>
        public bool IsBlank => Kind == ValueKind.Blank;
        /// <summary>True for Number and Integer. Booleans and dates are not numeric here, though both convert.</summary>
        public bool IsNumeric => Kind == ValueKind.Number || Kind == ValueKind.Integer;
        /// <summary>The payload as a DateTime. Only meaningful when <see cref="Kind"/> is Date.</summary>
        public DateTime AsDate => new DateTime(Integer);

        /// <summary>Numeric view of the value. BLANK is 0, booleans are 0/1, dates are OLE days.</summary>
        public double AsDouble()
        {
            switch (Kind)
            {
                case ValueKind.Number: return Number;
                case ValueKind.Integer: return Integer;
                case ValueKind.Boolean: return Integer;
                case ValueKind.Date: return new DateTime(Integer).ToOADate();
                case ValueKind.String:
                    double d;
                    return double.TryParse(String, NumberStyles.Any, CultureInfo.InvariantCulture, out d) ? d : 0;
                default: return 0;
            }
        }

        /// <summary>Truthiness, as IF and FILTER read it: non-zero, or a non-empty string. BLANK and dates are false.</summary>
        public bool AsBool()
        {
            switch (Kind)
            {
                case ValueKind.Boolean: return Integer != 0;
                case ValueKind.Number: return Number != 0;
                case ValueKind.Integer: return Integer != 0;
                case ValueKind.String: return !string.IsNullOrEmpty(String);
                default: return false;
            }
        }

        /// <summary>Boxes the payload as its natural CLR type; BLANK becomes null. Used for filter values, which travel as JSON.</summary>
        public object ToObject()
        {
            switch (Kind)
            {
                case ValueKind.Number: return Number;
                case ValueKind.Integer: return Integer;
                case ValueKind.String: return String;
                case ValueKind.Boolean: return Integer != 0;
                case ValueKind.Date: return new DateTime(Integer);
                default: return null;
            }
        }

        /// <summary>Reverses <see cref="ToObject"/> and accepts what ADO.NET, Excel and Json.NET hand back.
        /// Any type not listed falls through to its invariant string form.</summary>
        public static Value FromObject(object o)
        {
            if (o == null || o is DBNull) return Blank;
            if (o is string s) return FromString(s);
            if (o is double d) return FromNumber(d);
            if (o is float f) return FromNumber(f);
            if (o is decimal m) return FromNumber((double)m);
            if (o is long l) return FromInteger(l);
            if (o is int i) return FromInteger(i);
            if (o is short sh) return FromInteger(sh);
            if (o is byte b) return FromInteger(b);
            if (o is bool bo) return FromBoolean(bo);
            if (o is DateTime dt) return FromDate(dt);
            if (o is DateTimeOffset dto) return FromDate(dto.DateTime);
            return FromString(Convert.ToString(o, CultureInfo.InvariantCulture));
        }

        /// <summary>DAX-style ordering: BLANK compares as the type's zero value.</summary>
        public static int Compare(Value a, Value b)
        {
            if (a.Kind == ValueKind.String || b.Kind == ValueKind.String)
            {
                string x = a.Kind == ValueKind.String ? a.String : a.IsBlank ? "" : a.ToDisplayString();
                string y = b.Kind == ValueKind.String ? b.String : b.IsBlank ? "" : b.ToDisplayString();
                return string.Compare(x, y, StringComparison.OrdinalIgnoreCase);
            }
            if (a.Kind == ValueKind.Date && b.Kind == ValueKind.Date) return a.Integer.CompareTo(b.Integer);
            return a.AsDouble().CompareTo(b.AsDouble());
        }

        /// <summary>Plain text for grids, row keys and CSV export. Format strings are applied elsewhere (PowerBiFormat).
        /// Dates at midnight drop the time part. BLANK reads "(Blank)".</summary>
        public string ToDisplayString()
        {
            switch (Kind)
            {
                case ValueKind.Number: return Number.ToString("G", CultureInfo.CurrentCulture);
                case ValueKind.Integer: return Integer.ToString(CultureInfo.CurrentCulture);
                case ValueKind.String: return String;
                case ValueKind.Boolean: return Integer != 0 ? "True" : "False";
                case ValueKind.Date:
                    var dt = new DateTime(Integer);
                    return dt.TimeOfDay == TimeSpan.Zero
                        ? dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                        : dt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
                default: return "(Blank)";
            }
        }

        /// <summary>The display string, for debugging.</summary>
        public override string ToString() => ToDisplayString();
    }
}
