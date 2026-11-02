using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Lumen.Engine
{
    /// <summary>An error with its place in the source, for squiggles in the measure editor.</summary>
    public sealed class LqlDiagnostic
    {
        /// <summary>Text for the user.</summary>
        public string Message;
        /// <summary>Zero-based character span, and the one-based line and column of its start.</summary>
        public int Offset, Length, Line, Column;
        /// <summary>Formats the problem with its position, for the editor's message line.</summary>
        public override string ToString() => $"({Line},{Column}) {Message}";
    }

    /// <summary>Raised by the lexer, parser and binder. Never by evaluation, which fails soft to BLANK.</summary>
    public sealed class LqlException : Exception
    {
        /// <summary>Where and what.</summary>
        public readonly LqlDiagnostic Diagnostic;

        /// <summary>Works out line and column from the offset.</summary>
        public LqlException(string message, int offset, int length, string source) : base(message)
        {
            int line = 1, col = 1;
            for (int i = 0; i < offset && i < source.Length; i++)
            {
                if (source[i] == '\n') { line++; col = 1; } else col++;
            }
            Diagnostic = new LqlDiagnostic { Message = message, Offset = offset, Length = Math.Max(1, length), Line = line, Column = col };
        }
    }

    /// <summary>Token kinds. Quoted is a 'table name', Bracket is [column], Text is a "string" literal.</summary>
    internal enum Tok { End, Ident, Number, Text, Quoted, Bracket, Op, LParen, RParen, LBrace, RBrace, Comma }

    /// <summary>One token. <see cref="Text"/> holds the unescaped content; Pos and Len keep the source span.</summary>
    internal struct Token
    {
        public Tok Kind;
        public string Text;
        public int Pos, Len;
    }

    /// <summary>Turns source into tokens. Keywords are not special here: VAR, RETURN and IN arrive as identifiers, and the parser decides.</summary>
    internal sealed class Lexer
    {
        private readonly string _s;
        private int _i;

        public Lexer(string s) { _s = s ?? ""; }

        /// <summary>Reads to the end. The End token is included, so the parser never runs off the list.</summary>
        public List<Token> All()
        {
            var list = new List<Token>();
            while (true)
            {
                var t = Next();
                list.Add(t);
                if (t.Kind == Tok.End) return list;
            }
        }

        private Token Make(Tok k, string text, int start) => new Token { Kind = k, Text = text, Pos = start, Len = _i - start };

        /// <summary>Skips whitespace and comments, then reads one token.</summary>
        private Token Next()
        {
            while (_i < _s.Length)
            {
                char ch = _s[_i];
                if (char.IsWhiteSpace(ch)) { _i++; continue; }
                if (ch == '/' && _i + 1 < _s.Length && _s[_i + 1] == '/')
                {
                    while (_i < _s.Length && _s[_i] != '\n') _i++;
                    continue;
                }
                if (ch == '/' && _i + 1 < _s.Length && _s[_i + 1] == '*')
                {
                    int end = _s.IndexOf("*/", _i + 2, StringComparison.Ordinal);
                    _i = end < 0 ? _s.Length : end + 2;
                    continue;
                }
                break;
            }

            int start = _i;
            if (_i >= _s.Length) return Make(Tok.End, "", start);
            char c = _s[_i];

            if (char.IsDigit(c) || (c == '.' && _i + 1 < _s.Length && char.IsDigit(_s[_i + 1])))
            {
                // Permissive on purpose: 1.2.3 lexes as one token and double.TryParse rejects it with a clear message.
                while (_i < _s.Length && (char.IsDigit(_s[_i]) || _s[_i] == '.')) _i++;
                if (_i < _s.Length && (_s[_i] == 'e' || _s[_i] == 'E'))
                {
                    _i++;
                    if (_i < _s.Length && (_s[_i] == '+' || _s[_i] == '-')) _i++;
                    while (_i < _s.Length && char.IsDigit(_s[_i])) _i++;
                }
                return Make(Tok.Number, _s.Substring(start, _i - start), start);
            }
            if (char.IsLetter(c) || c == '_')
            {
                while (_i < _s.Length && (char.IsLetterOrDigit(_s[_i]) || _s[_i] == '_' || _s[_i] == '.')) _i++;
                return Make(Tok.Ident, _s.Substring(start, _i - start), start);
            }
            if (c == '"') return ReadDelimited('"', Tok.Text, start);
            if (c == '\'') return ReadDelimited('\'', Tok.Quoted, start);
            if (c == '[')
            {
                int end = _s.IndexOf(']', _i);
                if (end < 0) throw new LqlException("Unclosed '['", _i, 1, _s);
                _i = end + 1;
                return Make(Tok.Bracket, _s.Substring(start + 1, end - start - 1), start);
            }

            _i++;
            switch (c)
            {
                case '(': return Make(Tok.LParen, "(", start);
                case ')': return Make(Tok.RParen, ")", start);
                case '{': return Make(Tok.LBrace, "{", start);
                case '}': return Make(Tok.RBrace, "}", start);
                case ',': return Make(Tok.Comma, ",", start);
                case '+': case '-': case '*': case '/': case '^': case '=':
                    if (c == '=' && Peek('=')) _i++;
                    return Make(Tok.Op, c == '=' ? "=" : c.ToString(), start);
                case '&':
                    if (Peek('&')) { _i++; return Make(Tok.Op, "&&", start); }
                    return Make(Tok.Op, "&", start);
                case '|':
                    if (Peek('|')) { _i++; return Make(Tok.Op, "||", start); }
                    break;
                case '<':
                    if (Peek('=')) { _i++; return Make(Tok.Op, "<=", start); }
                    if (Peek('>')) { _i++; return Make(Tok.Op, "<>", start); }
                    return Make(Tok.Op, "<", start);
                case '>':
                    if (Peek('=')) { _i++; return Make(Tok.Op, ">=", start); }
                    return Make(Tok.Op, ">", start);
            }
            throw new LqlException($"Unexpected character '{c}'", start, 1, _s);
        }

        private bool Peek(char c) => _i < _s.Length && _s[_i] == c;

        /// <summary>Reads a quoted literal. A doubled delimiter stands for one, as in DAX and SQL.</summary>
        private Token ReadDelimited(char q, Tok kind, int start)
        {
            var sb = new StringBuilder();
            _i++;
            while (true)
            {
                if (_i >= _s.Length) throw new LqlException("Unterminated literal", start, 1, _s);
                if (_s[_i] == q)
                {
                    if (_i + 1 < _s.Length && _s[_i + 1] == q) { sb.Append(q); _i += 2; continue; }
                    _i++;
                    return Make(kind, sb.ToString(), start);
                }
                sb.Append(_s[_i++]);
            }
        }
    }

    /// <summary>AST base. Pos and Len let the binder point at the offending text.</summary>
    internal abstract class Node { public int Pos, Len; }
    internal sealed class NumberNode : Node { public double Value; }
    internal sealed class TextNode : Node { public string Value; }
    internal sealed class ColumnNode : Node { public string Table, Column; }     // Table null for [X]
    internal sealed class TableNode : Node { public string Name; }               // bare identifier, or VAR reference
    internal sealed class BinaryNode : Node { public string Op; public Node Left, Right; }
    internal sealed class UnaryNode : Node { public string Op; public Node Operand; }
    internal sealed class CallNode : Node { public string Name; public List<Node> Args = new List<Node>(); }
    internal sealed class ListNode : Node { public List<Node> Items = new List<Node>(); }
    internal sealed class VarBlockNode : Node
    {
        public List<KeyValuePair<string, Node>> Vars = new List<KeyValuePair<string, Node>>();
        public Node Body;
    }

    /// <summary>Pratt parser for the DAX subset.</summary>
    internal sealed class Parser
    {
        private readonly List<Token> _t;
        private readonly string _src;
        private int _p;

        public Parser(string source)
        {
            _src = source ?? "";
            _t = new Lexer(_src).All();
        }

        // The End token is always last, so Cur is safe at any point the grammar can reach.
        private Token Cur => _t[_p];
        private Token Take() => _t[_p++];

        private LqlException Err(string msg, Token t) => new LqlException(msg, t.Pos, t.Len, _src);

        /// <summary>Parses one whole expression and insists nothing is left over.</summary>
        public Node ParseAll()
        {
            if (Cur.Kind == Tok.End) throw Err("The expression is empty", Cur);
            var n = ParseExpr(0);
            if (Cur.Kind != Tok.End) throw Err($"Unexpected '{Cur.Text}'", Cur);
            return n;
        }

        /// <summary>Binding power of an infix token, or -1 if the token ends the expression.
        /// IN sits with the comparisons.</summary>
        private static int Prec(Token t)
        {
            if (t.Kind == Tok.Ident && string.Equals(t.Text, "IN", StringComparison.OrdinalIgnoreCase)) return 3;
            if (t.Kind != Tok.Op) return -1;
            switch (t.Text)
            {
                case "||": return 1;
                case "&&": return 2;
                case "=": case "<>": case "<": case "<=": case ">": case ">=": return 3;
                case "&": return 4;
                case "+": case "-": return 5;
                case "*": case "/": return 6;
                case "^": return 7;
                default: return -1;
            }
        }

        /// <summary>Precedence climbing: parse a prefix, then keep absorbing operators at least as strong as <paramref name="minPrec"/>.
        /// The right side recurses one level higher for left association; ^ recurses at the same level for right association.</summary>
        private Node ParseExpr(int minPrec)
        {
            var left = ParsePrefix();
            while (true)
            {
                var t = Cur;
                int prec = Prec(t);
                if (prec < 0 || prec < minPrec) return left;
                Take();
                var right = ParseExpr(t.Text == "^" ? prec : prec + 1);
                left = new BinaryNode
                {
                    Op = t.Kind == Tok.Ident ? "IN" : t.Text,
                    Left = left,
                    Right = right,
                    Pos = left.Pos,
                    Len = Math.Max(1, right.Pos + right.Len - left.Pos)
                };
            }
        }

        /// <summary>Everything that can start an expression.</summary>
        private Node ParsePrefix()
        {
            var t = Cur;
            switch (t.Kind)
            {
                case Tok.Number:
                    Take();
                    double d;
                    if (!double.TryParse(t.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out d))
                        throw Err($"Invalid number '{t.Text}'", t);
                    return new NumberNode { Value = d, Pos = t.Pos, Len = t.Len };
                case Tok.Text:
                    Take();
                    return new TextNode { Value = t.Text, Pos = t.Pos, Len = t.Len };
                case Tok.Bracket:
                    Take();
                    return new ColumnNode { Column = t.Text, Pos = t.Pos, Len = t.Len };
                case Tok.Quoted:
                    Take();
                    return AfterTableName(t);
                case Tok.LParen:
                    Take();
                    var inner = ParseExpr(0);
                    if (Cur.Kind != Tok.RParen) throw Err("Expected ')'", Cur);
                    Take();
                    return inner;
                case Tok.LBrace:
                    return ParseList();
                case Tok.Op:
                    if (t.Text == "-" || t.Text == "+")
                    {
                        Take();
                        // 8 binds tighter than ^, so -2^2 is (-2)^2 = 4 here. DAX gives ^ the higher precedence and reads it as -4.
                        var operand = ParseExpr(8);
                        return new UnaryNode { Op = t.Text, Operand = operand, Pos = t.Pos, Len = operand.Pos + operand.Len - t.Pos };
                    }
                    break;
                case Tok.Ident:
                    if (string.Equals(t.Text, "VAR", StringComparison.OrdinalIgnoreCase)) return ParseVarBlock();
                    Take();
                    if (Cur.Kind == Tok.LParen) return ParseCall(t);
                    return AfterTableName(t);
            }
            throw Err(t.Kind == Tok.End ? "Unexpected end of expression" : $"Unexpected '{t.Text}'", t);
        }

        /// <summary>An identifier or 'quoted name' followed by [column] is a column reference; alone it is a table or variable.</summary>
        private Node AfterTableName(Token t)
        {
            if (Cur.Kind == Tok.Bracket)
            {
                var c = Take();
                return new ColumnNode { Table = t.Text, Column = c.Text, Pos = t.Pos, Len = c.Pos + c.Len - t.Pos };
            }
            return new TableNode { Name = t.Text, Pos = t.Pos, Len = t.Len };
        }

        /// <summary>Parses a function call. The name is upper-cased here, which is how function names ignore case.</summary>
        private Node ParseCall(Token name)
        {
            Take(); // (
            var call = new CallNode { Name = name.Text.ToUpperInvariant(), Pos = name.Pos, Len = name.Len };
            if (Cur.Kind == Tok.RParen) { Take(); return call; }
            while (true)
            {
                call.Args.Add(ParseExpr(0));
                if (Cur.Kind == Tok.Comma) { Take(); continue; }
                if (Cur.Kind == Tok.RParen) { Take(); return call; }
                throw Err("Expected ',' or ')'", Cur);
            }
        }

        /// <summary>Parses {a, b, c}, the right side of IN.</summary>
        private Node ParseList()
        {
            var open = Take();
            var list = new ListNode { Pos = open.Pos, Len = 1 };
            while (Cur.Kind != Tok.RBrace)
            {
                list.Items.Add(ParseExpr(0));
                if (Cur.Kind == Tok.Comma) Take();
                else if (Cur.Kind != Tok.RBrace) throw Err("Expected ',' or '}'", Cur);
            }
            Take();
            return list;
        }

        /// <summary>Parses VAR x = ... RETURN .... Each variable may use the ones before it.</summary>
        private Node ParseVarBlock()
        {
            var block = new VarBlockNode { Pos = Cur.Pos, Len = Cur.Len };
            while (string.Equals(Cur.Text, "VAR", StringComparison.OrdinalIgnoreCase) && Cur.Kind == Tok.Ident)
            {
                Take();
                var name = Cur;
                if (name.Kind != Tok.Ident) throw Err("Expected a variable name", name);
                Take();
                if (!(Cur.Kind == Tok.Op && Cur.Text == "=")) throw Err("Expected '='", Cur);
                Take();
                block.Vars.Add(new KeyValuePair<string, Node>(name.Text, ParseExpr(0)));
            }
            if (!(Cur.Kind == Tok.Ident && string.Equals(Cur.Text, "RETURN", StringComparison.OrdinalIgnoreCase)))
                throw Err("Expected RETURN", Cur);
            Take();
            block.Body = ParseExpr(0);
            return block;
        }
    }
}
