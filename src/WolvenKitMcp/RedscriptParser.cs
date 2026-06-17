using System.Text;

namespace WolvenKitMcp;

// ════════════════════════════════════════════════════════════════════════
// REDscript parser (.reds) — tokenizer + recursive-descent analysis.
//
// Goal: SYNTACTIC validation of an isolated file (without game or base) —
// typed signatures, generics, classes/structs/enums, annotations, blocks.
// This is NOT a type-checker (no resolution of external types/methods:
// that requires the scc compiler + the whole mod ecosystem, cf. WINDOWS-VALIDATION).
//
// Anti-false-positive philosophy: strict on stable structure (declarations,
// types, statement headers); permissive but balanced on expressions
// (we validate the balancing of () [] {} and operator separation, without
// imposing a complete expression grammar that could reject valid
// REDscript). Error recovery via synchronization on ; } and
// declaration keywords, to avoid cascades.
// ════════════════════════════════════════════════════════════════════════

internal enum TokKind
{
    Ident, Keyword, Int, Float, String, Name, Annotation,
    LParen, RParen, LBrace, RBrace, LBracket, RBracket,
    Comma, Semicolon, Colon, Arrow, Dot, Op, Eof,
}

internal readonly record struct Tok(TokKind Kind, string Text, int Line, int Col);

internal sealed record RedDiagnostic(int Line, int Col, string Severity, string Message)
{
    public override string ToString() =>
        $"{Severity} L{Line}:{Col} — {Message}";
}

internal sealed record RedDeclaration(string Kind, string Name, int Line);

internal sealed class RedParseResult
{
    public List<RedDiagnostic> Diagnostics { get; } = new();
    public List<RedDeclaration> Declarations { get; } = new();
    public List<string> Imports { get; } = new();
    public string? Module { get; set; }
}

internal static class RedscriptParser
{
    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "module", "import", "class", "struct", "enum", "func", "let", "const",
        "new", "if", "else", "while", "for", "in", "switch", "case", "default",
        "break", "continue", "return", "this", "super", "null", "true", "false",
        "as", "public", "private", "protected", "final", "static", "native",
        "abstract", "quest", "exec", "cb", "callback", "persistent", "importonly",
        "extends", "out", "opt", "wrapped", "inline", "edit", "rep", "savetime",
    };

    // Modifiers that can precede a declaration (field/function/class).
    private static readonly HashSet<string> Modifiers = new(StringComparer.Ordinal)
    {
        "public", "private", "protected", "final", "static", "native", "abstract",
        "quest", "exec", "cb", "callback", "persistent", "importonly", "const",
        "inline", "edit", "rep", "savetime",
    };

    public static RedParseResult Parse(string source)
    {
        var res = new RedParseResult();
        List<Tok> toks;
        try { toks = Lex(source, res); }
        catch (Exception ex)
        {
            res.Diagnostics.Add(new RedDiagnostic(1, 1, "ERROR", "tokenizer failed: " + ex.Message));
            return res;
        }
        new Impl(toks, res).ParseFile();
        return res;
    }

    // ── Lexer ────────────────────────────────────────────────────────────
    private static List<Tok> Lex(string s, RedParseResult res)
    {
        var toks = new List<Tok>(Math.Max(16, s.Length / 4)); // pre-sized (~1 token/4 chars)
        int i = 0, line = 1, col = 1;
        int n = s.Length;

        void Adv(int k = 1)
        {
            for (var z = 0; z < k && i < n; z++)
            {
                if (s[i] == '\n') { line++; col = 1; } else { col++; }
                i++;
            }
        }

        while (i < n)
        {
            char c = s[i];
            // whitespace
            if (c is ' ' or '\t' or '\r' or '\n') { Adv(); continue; }
            // comments
            if (c == '/' && i + 1 < n && s[i + 1] == '/')
            {
                while (i < n && s[i] != '\n') Adv();
                continue;
            }
            if (c == '/' && i + 1 < n && s[i + 1] == '*')
            {
                int sl = line, sc = col; Adv(2);
                var closed = false;
                while (i < n)
                {
                    if (s[i] == '*' && i + 1 < n && s[i + 1] == '/') { Adv(2); closed = true; break; }
                    Adv();
                }
                if (!closed) res.Diagnostics.Add(new RedDiagnostic(sl, sc, "ERROR", "unclosed /* */ comment"));
                continue;
            }
            int tl = line, tc = col;
            // string (optional prefix n/r/t/l/s...) — handles interpolation
            // s"...\(expr)..." (nested quotes) and multi-line strings.
            if (c == '"' || (char.IsLetter(c) && i + 1 < n && s[i + 1] == '"' && "nrtls".IndexOf(char.ToLowerInvariant(c)) >= 0))
            {
                var prefixed = c != '"';
                if (prefixed) Adv(); // consume the prefix
                Adv(); // "
                var ok = false;
                while (i < n)
                {
                    if (s[i] == '\\')
                    {
                        // Interpolation \( ... ): may contain nested "...".
                        if (i + 1 < n && s[i + 1] == '(')
                        {
                            Adv(2);
                            int depth = 1;
                            while (i < n && depth > 0)
                            {
                                if (s[i] == '(') depth++;
                                else if (s[i] == ')') depth--;
                                else if (s[i] == '"')
                                {
                                    // skip a nested string (with its escapes)
                                    Adv();
                                    while (i < n && s[i] != '"')
                                    { if (s[i] == '\\') Adv(2); else Adv(); }
                                }
                                if (i < n && depth > 0) Adv();
                            }
                            if (i < n) Adv(); // consume the ')'
                            continue;
                        }
                        Adv(2); continue; // normal escape \", \n, ...
                    }
                    if (s[i] == '"') { Adv(); ok = true; break; }
                    Adv(); // literal \n is allowed (multi-line strings)
                }
                if (!ok) res.Diagnostics.Add(new RedDiagnostic(tl, tc, "ERROR", "unterminated string"));
                toks.Add(new Tok(prefixed ? TokKind.Name : TokKind.String, "\"\"", tl, tc));
                continue;
            }
            // number
            if (char.IsDigit(c))
            {
                int st = i; var isFloat = false;
                while (i < n && char.IsDigit(s[i])) Adv();
                if (i < n && s[i] == '.' && i + 1 < n && char.IsDigit(s[i + 1]))
                {
                    isFloat = true; Adv();
                    while (i < n && char.IsDigit(s[i])) Adv();
                }
                // suffixes (u, l, d, ul, f...)

                while (i < n && char.IsLetter(s[i])) { if (char.ToLowerInvariant(s[i]) is 'f' or 'd') isFloat = true; Adv(); }
                toks.Add(new Tok(isFloat ? TokKind.Float : TokKind.Int, s[st..i], tl, tc));
                continue;
            }
            // annotation
            if (c == '@')
            {
                Adv(); int st = i;
                while (i < n && (char.IsLetterOrDigit(s[i]) || s[i] == '_')) Adv();
                toks.Add(new Tok(TokKind.Annotation, s[st..i], tl, tc));
                continue;
            }
            // identifier / keyword
            if (char.IsLetter(c) || c == '_')
            {
                int st = i;
                while (i < n && (char.IsLetterOrDigit(s[i]) || s[i] == '_')) Adv();
                var word = s[st..i];
                toks.Add(new Tok(Keywords.Contains(word) ? TokKind.Keyword : TokKind.Ident, word, tl, tc));
                continue;
            }
            // punctuation / operators
            switch (c)
            {
                case '(': toks.Add(new Tok(TokKind.LParen, "(", tl, tc)); Adv(); continue;
                case ')': toks.Add(new Tok(TokKind.RParen, ")", tl, tc)); Adv(); continue;
                case '{': toks.Add(new Tok(TokKind.LBrace, "{", tl, tc)); Adv(); continue;
                case '}': toks.Add(new Tok(TokKind.RBrace, "}", tl, tc)); Adv(); continue;
                case '[': toks.Add(new Tok(TokKind.LBracket, "[", tl, tc)); Adv(); continue;
                case ']': toks.Add(new Tok(TokKind.RBracket, "]", tl, tc)); Adv(); continue;
                case ',': toks.Add(new Tok(TokKind.Comma, ",", tl, tc)); Adv(); continue;
                case ';': toks.Add(new Tok(TokKind.Semicolon, ";", tl, tc)); Adv(); continue;
                case '.': toks.Add(new Tok(TokKind.Dot, ".", tl, tc)); Adv(); continue;
                case ':': toks.Add(new Tok(TokKind.Colon, ":", tl, tc)); Adv(); continue;
            }
            if (c == '-' && i + 1 < n && s[i + 1] == '>') { toks.Add(new Tok(TokKind.Arrow, "->", tl, tc)); Adv(2); continue; }
            // generic operator (sequence of symbols)
            const string opChars = "+-*/%=<>!&|^~?";
            if (opChars.IndexOf(c) >= 0)
            {
                int st = i;
                while (i < n && opChars.IndexOf(s[i]) >= 0) Adv();
                toks.Add(new Tok(TokKind.Op, s[st..i], tl, tc));
                continue;
            }
            // unknown character
            res.Diagnostics.Add(new RedDiagnostic(tl, tc, "ERROR", $"unexpected character: '{c}'"));
            Adv();
        }
        toks.Add(new Tok(TokKind.Eof, "", line, col));
        return toks;
    }

    // ── Parser ─────────────────────────────────────────────────────────────
    private sealed class Impl
    {
        private readonly List<Tok> _t;
        private readonly RedParseResult _r;
        private int _p;
        private int _errors;
        private const int MaxErrors = 60;

        public Impl(List<Tok> toks, RedParseResult res) { _t = toks; _r = res; }

        private Tok Cur => _t[_p];
        private Tok Peek(int k = 1) => _t[Math.Min(_p + k, _t.Count - 1)];
        private bool Is(TokKind k) => Cur.Kind == k;
        private bool IsKw(string w) => Cur.Kind == TokKind.Keyword && Cur.Text == w;
        private bool IsOp(string o) => Cur.Kind == TokKind.Op && Cur.Text == o;
        private Tok Next() { var t = Cur; if (_p < _t.Count - 1) _p++; return t; }

        private void Err(string msg, Tok? at = null)
        {
            if (_errors >= MaxErrors) return;
            _errors++;
            var tk = at ?? Cur;
            _r.Diagnostics.Add(new RedDiagnostic(tk.Line, tk.Col, "ERROR", msg));
        }

        private bool Expect(TokKind k, string what)
        {
            if (Is(k)) { Next(); return true; }
            Err($"expected {what}, found \"{Describe(Cur)}\"");
            return false;
        }

        private static string Describe(Tok t) => t.Kind == TokKind.Eof ? "end of file"
            : string.IsNullOrEmpty(t.Text) ? t.Kind.ToString() : t.Text;

        public void ParseFile()
        {
            // optional module
            if (IsKw("module"))
            {
                Next();
                var name = ParseDottedName();
                _r.Module = name;
                // module may be followed by an optional ; or nothing
                if (Is(TokKind.Semicolon)) Next();
            }
            while (!Is(TokKind.Eof) && _errors < MaxErrors)
            {
                int before = _p;
                ParseTopLevel();
                if (_p == before) { Next(); } // progress guarantee
            }
        }

        private string ParseDottedName()
        {
            var sb = new StringBuilder();
            if (Is(TokKind.Ident) || Is(TokKind.Keyword)) { sb.Append(Next().Text); }
            else { Err("name expected"); return ""; }
            while (Is(TokKind.Dot)) { Next(); if (Is(TokKind.Ident)) sb.Append('.').Append(Next().Text); else { if (IsOp("*")) { Next(); sb.Append(".*"); } break; } }
            return sb.ToString();
        }

        private void ParseTopLevel()
        {
            if (Is(TokKind.Semicolon)) { Next(); return; } // stray ";" at top-level
            var anns = ParseAnnotations();      // @if(...) may precede an import
            if (IsKw("import")) { ParseImport(); return; }
            ParseModifiers();
            if (IsKw("class") || IsKw("struct")) { ParseClass(); return; }
            if (IsKw("enum")) { ParseEnum(); return; }
            if (IsKw("func")) { ParseFunc(topLevel: true); return; }
            if (IsKw("let") || IsKw("const")) { ParseField(); return; }
            // nothing recognized
            if (!Is(TokKind.Eof))
            {
                Err($"declaration expected (class/struct/enum/func/let), found \"{Describe(Cur)}\"");
                Synchronize();
            }
        }

        private void ParseImport()
        {
            Next(); // import
            var target = ParseDottedName();
            if (!string.IsNullOrEmpty(target)) _r.Imports.Add(target);
            if (Is(TokKind.Dot)) Next();
            if (Is(TokKind.LBrace)) // import a.b.{C, D}
            {
                Next();
                while (!Is(TokKind.RBrace) && !Is(TokKind.Eof))
                {
                    if (Is(TokKind.Ident)) Next(); else break;
                    if (Is(TokKind.Comma)) Next(); else break;
                }
                Expect(TokKind.RBrace, "\"}\"");
            }
            if (Is(TokKind.Semicolon)) Next();
        }

        private List<string> ParseAnnotations()
        {
            var anns = new List<string>();
            while (Is(TokKind.Annotation))
            {
                anns.Add(Cur.Text);
                Next();
                if (Is(TokKind.LParen)) SkipBalanced(TokKind.LParen, TokKind.RParen);
            }
            return anns;
        }

        private void ParseModifiers()
        {
            while (Cur.Kind == TokKind.Keyword && Modifiers.Contains(Cur.Text)) Next();
        }

        private void ParseClass()
        {
            Next(); // class/struct
            var nameTok = Cur;
            if (!Expect(TokKind.Ident, "class name")) { Synchronize(); return; }
            _r.Declarations.Add(new RedDeclaration("class", nameTok.Text, nameTok.Line));
            if (IsKw("extends")) { Next(); ParseType(); }
            if (!Expect(TokKind.LBrace, "\"{\"")) return;
            while (!Is(TokKind.RBrace) && !Is(TokKind.Eof) && _errors < MaxErrors)
            {
                int before = _p;
                ParseMember();
                if (_p == before) Next();
            }
            Expect(TokKind.RBrace, "\"}\"");
        }

        private void ParseMember()
        {
            if (Is(TokKind.Semicolon)) { Next(); return; } // stray ";" after a member
            ParseAnnotations();
            ParseModifiers();
            if (IsKw("func")) { ParseFunc(topLevel: false); return; }
            if (IsKw("let") || IsKw("const")) { ParseField(); return; }
            if (Is(TokKind.RBrace)) return;
            Err($"member expected (func/let), found \"{Describe(Cur)}\"");
            Synchronize();
        }

        private void ParseEnum()
        {
            Next();
            var nameTok = Cur;
            if (!Expect(TokKind.Ident, "enum name")) { Synchronize(); return; }
            _r.Declarations.Add(new RedDeclaration("enum", nameTok.Text, nameTok.Line));
            if (!Expect(TokKind.LBrace, "\"{\"")) return;
            while (!Is(TokKind.RBrace) && !Is(TokKind.Eof))
            {
                if (!(Is(TokKind.Ident) || Is(TokKind.Keyword))) { if (Is(TokKind.Comma)) { Next(); continue; } break; }
                Next();
                // permissive value: integer, hex, negative, expression — up to , or }
                if (IsOp("=")) { Next(); while (!Is(TokKind.Comma) && !Is(TokKind.RBrace) && !Is(TokKind.Eof)) Next(); }
                if (Is(TokKind.Comma)) Next();
            }
            Expect(TokKind.RBrace, "\"}\"");
        }

        private void ParseField()
        {
            Next(); // let/const
            var nameTok = Cur;
            // some contextual keywords (quest, wrapped...) serve as names.
            if (Is(TokKind.Ident) || Is(TokKind.Keyword)) Next();
            else { Err($"field name expected, found \"{Describe(Cur)}\""); Synchronize(); return; }
            _r.Declarations.Add(new RedDeclaration("field", nameTok.Text, nameTok.Line));
            if (Is(TokKind.Colon)) { Next(); ParseType(); }
            if (IsOp("=")) { Next(); SkipExpression(); }
            Expect(TokKind.Semicolon, "\";\"");
        }

        private void ParseFunc(bool topLevel)
        {
            Next(); // func
            var nameTok = Cur;
            if (!(Is(TokKind.Ident) || Is(TokKind.Keyword)))
            { Err("function name expected"); Synchronize(); return; }
            Next();
            _r.Declarations.Add(new RedDeclaration("func", nameTok.Text, nameTok.Line));
            // optional generics <T, U>
            if (IsOp("<")) SkipAngles();
            if (!Expect(TokKind.LParen, "\"(\"")) { Synchronize(); return; }
            ParseParams();
            Expect(TokKind.RParen, "\")\"");
            if (Is(TokKind.Arrow)) { Next(); ParseType(); }
            if (Is(TokKind.LBrace)) ParseBlock();
            else if (Is(TokKind.Semicolon)) Next();        // native func with ";"
            else if (IsOp("=")) { Next(); SkipExprBody(); } // expression-bodied func: = expr
            // native func WITHOUT body or ";": tolerated if followed by a declaration
            // boundary (next member/annotation/"}"/EOF).
            else if (Is(TokKind.Annotation) || Is(TokKind.RBrace) || Is(TokKind.Eof)
                     || (Cur.Kind == TokKind.Keyword && IsMemberStart(Cur.Text)))
            { /* native declaration without body — OK */ }
            else Err("\"{\" (body), \"=\" (expression-body) or \";\" (native) expected after the signature");
        }

        private void ParseParams()
        {
            while (!Is(TokKind.RParen) && !Is(TokKind.Eof))
            {
                // parameter modifiers: const out opt
                while (IsKw("const") || IsKw("out") || IsKw("opt")) Next();
                if (!(Is(TokKind.Ident) || Is(TokKind.Keyword))) { Err("parameter name expected"); break; }
                Next();
                if (Expect(TokKind.Colon, "\":\" (parameter type)")) ParseType();
                if (Is(TokKind.Comma)) Next(); else break;
            }
        }

        // Type: Name, a.b.Name, Generic<T, U>, [T] (array shorthand), nested. Tolerant.
        private void ParseType()
        {
            // REDscript array shorthand: [T] or sized array [T; N]
            if (Is(TokKind.LBracket))
            {
                Next(); ParseType();
                if (Is(TokKind.Semicolon)) { Next(); if (Is(TokKind.Int)) Next(); else Err("size (integer) expected in [T; N]"); }
                Expect(TokKind.RBracket, "\"]\" (end of array type)");
                return;
            }
            if (!(Is(TokKind.Ident) || Is(TokKind.Keyword)))
            { Err($"type expected, found \"{Describe(Cur)}\""); return; }
            Next();
            // Qualified segment after ".": also accept contextual keywords
            // (consistent with the first segment), to avoid false positives.
            while (Is(TokKind.Dot)) { Next(); if (Is(TokKind.Ident) || Is(TokKind.Keyword)) Next(); else { Err("type expected after \".\""); break; } }
            if (IsOp("<")) SkipAngles();
            // optional suffixed arrays [n]
            while (Is(TokKind.LBracket)) SkipBalanced(TokKind.LBracket, TokKind.RBracket);
        }

        private void ParseBlock()
        {
            Expect(TokKind.LBrace, "\"{\"");
            while (!Is(TokKind.RBrace) && !Is(TokKind.Eof) && _errors < MaxErrors)
            {
                int before = _p;
                ParseStatement();
                if (_p == before) Next();
            }
            Expect(TokKind.RBrace, "\"}\"");
        }

        private void ParseStatement()
        {
            if (Is(TokKind.LBrace)) { ParseBlock(); return; }
            if (Is(TokKind.Semicolon)) { Next(); return; }
            if (IsKw("let") || IsKw("const"))
            {
                Next();
                if (Is(TokKind.Ident) || Is(TokKind.Keyword)) Next(); else Err("variable name expected");
                if (Is(TokKind.Colon)) { Next(); ParseType(); }
                if (IsOp("=")) { Next(); SkipExpression(); }
                Expect(TokKind.Semicolon, "\";\"");
                return;
            }
            if (IsKw("if"))
            {
                // REDscript: no mandatory parentheses (if cond { } else { }).
                Next(); SkipExprUntilBlock();
                ParseBlock();
                if (IsKw("else")) { Next(); if (IsKw("if")) ParseStatement(); else ParseBlock(); }
                return;
            }
            if (IsKw("while"))
            {
                Next(); SkipExprUntilBlock();
                ParseBlock();
                return;
            }
            if (IsKw("for"))
            {
                Next();
                if (Is(TokKind.LParen)) Next(); // for (x in y) tolerated
                if (Is(TokKind.Ident) || Is(TokKind.Keyword)) Next(); else Err("iteration variable expected");
                if (IsKw("in")) Next(); else Err("\"in\" expected in for-in");
                SkipExprUntilBlock();
                ParseBlock();
                return;
            }
            if (IsKw("switch"))
            {
                Next(); SkipExprUntilBlock();
                Expect(TokKind.LBrace, "\"{\"");
                while (!Is(TokKind.RBrace) && !Is(TokKind.Eof) && _errors < MaxErrors)
                {
                    if (IsKw("case")) { Next(); SkipExpressionUntil(TokKind.Colon); Expect(TokKind.Colon, "\":\""); }
                    else if (IsKw("default")) { Next(); Expect(TokKind.Colon, "\":\""); }
                    else
                    {
                        int before = _p; ParseStatement(); if (_p == before) Next();
                    }
                }
                Expect(TokKind.RBrace, "\"}\"");
                return;
            }
            if (IsKw("return")) { Next(); if (!Is(TokKind.Semicolon)) SkipExpression(); Expect(TokKind.Semicolon, "\";\""); return; }
            if (IsKw("break") || IsKw("continue")) { Next(); Expect(TokKind.Semicolon, "\";\""); return; }
            // otherwise: expression statement
            SkipExpression();
            Expect(TokKind.Semicolon, "\";\"");
        }

        // ── Expressions: permissive validation via balancing ──────────────
        // We consume up to the ; or } ending the statement, checking the
        // balancing of () [] {} (lambdas/array literals) and rejecting tokens
        // clearly invalid in expression position. We do NOT model precedence:
        // any infix/prefix operator is accepted.
        private void SkipExpression() => SkipExpressionUntil(TokKind.Semicolon);

        // Expression body of a function "= expr" (without terminal ";" in
        // REDscript): we consume up to the next member/declaration boundary
        // (declaration keyword/modifier, annotation, "}"), balancing
        // the parentheses/brackets/braces (calls, lambdas, array literals).
        private void SkipExprBody()
        {
            if (Is(TokKind.Semicolon)) { Next(); return; }
            while (!Is(TokKind.Eof))
            {
                switch (Cur.Kind)
                {
                    case TokKind.LParen: SkipBalanced(TokKind.LParen, TokKind.RParen); continue;
                    case TokKind.LBracket: SkipBalanced(TokKind.LBracket, TokKind.RBracket); continue;
                    case TokKind.LBrace: SkipBalanced(TokKind.LBrace, TokKind.RBrace); continue;
                    case TokKind.RBrace: return;            // end of the enclosing class
                    case TokKind.Semicolon: Next(); return; // optional terminator
                    case TokKind.Annotation: return;        // next annotated member
                    case TokKind.Keyword:
                        if (IsMemberStart(Cur.Text)) return; // func/let/modifier → next member
                        Next(); continue;
                    default: Next(); continue;
                }
            }
        }

        private static bool IsMemberStart(string kw) =>
            kw is "func" or "let" or "const" or "class" or "struct" or "enum"
               or "public" or "private" or "protected" or "final" or "static"
               or "native" or "abstract" or "quest" or "exec" or "cb" or "callback"
               or "persistent" or "importonly" or "import";

        // Consumes an expression condition up to the block's "{" (REDscript
        // does not require parentheses). Handles body lambdas "-> { }" so as
        // not to confuse their brace with the start of the block.
        private void SkipExprUntilBlock()
        {
            while (!Is(TokKind.Eof))
            {
                switch (Cur.Kind)
                {
                    case TokKind.LParen: SkipBalanced(TokKind.LParen, TokKind.RParen); break;
                    case TokKind.LBracket: SkipBalanced(TokKind.LBracket, TokKind.RBracket); break;
                    case TokKind.LBrace: return; // start of the block
                    case TokKind.Semicolon: return; // safety (missing body)
                    case TokKind.Arrow:
                        Next();
                        if (Is(TokKind.LBrace)) SkipBalanced(TokKind.LBrace, TokKind.RBrace); // body lambda
                        break;
                    default: Next(); break;
                }
            }
        }

        private void SkipExpressionUntil(TokKind stop)
        {
            while (!Is(stop) && !Is(TokKind.Eof))
            {
                switch (Cur.Kind)
                {
                    case TokKind.LParen: SkipBalanced(TokKind.LParen, TokKind.RParen); break;
                    case TokKind.LBracket: SkipBalanced(TokKind.LBracket, TokKind.RBracket); break;
                    case TokKind.LBrace: SkipBalanced(TokKind.LBrace, TokKind.RBrace); break;
                    case TokKind.RParen:
                    case TokKind.RBracket:
                    case TokKind.RBrace:
                        // unmatched closer encountered out of context
                        if (stop == TokKind.Semicolon) return; // let the statement end
                        Err($"\"{Cur.Text}\" unmatched in the expression");
                        return;
                    case TokKind.Semicolon:
                        return;
                    default:
                        Next(); break;
                }
            }
        }

        private void SkipBalanced(TokKind open, TokKind close)
        {
            if (!Is(open)) return;
            var openTok = Cur; Next();
            int depth = 1;
            while (depth > 0 && !Is(TokKind.Eof))
            {
                if (Cur.Kind == open) depth++;
                else if (Cur.Kind == close) depth--;
                else if (Cur.Kind == TokKind.LParen) { SkipBalanced(TokKind.LParen, TokKind.RParen); continue; }
                else if (Cur.Kind == TokKind.LBracket) { SkipBalanced(TokKind.LBracket, TokKind.RBracket); continue; }
                else if (Cur.Kind == TokKind.LBrace) { SkipBalanced(TokKind.LBrace, TokKind.RBrace); continue; }
                Next();
            }
            if (depth != 0) Err($"\"{openTok.Text}\" unclosed", openTok);
        }

        // Generics <...>: we skip while balancing < and > (compound operators
        // like >> are handled character by character via the text).
        private void SkipAngles()
        {
            // Cur is an Op starting with '<'
            int depth = 0;
            // decompose the current op token
            void Count(string op) { foreach (var ch in op) { if (ch == '<') depth++; else if (ch == '>') depth--; } }
            Count(Next().Text);
            while (depth > 0 && !Is(TokKind.Eof))
            {
                if (Cur.Kind == TokKind.Op && (Cur.Text.Contains('<') || Cur.Text.Contains('>'))) { Count(Next().Text); continue; }
                if (Is(TokKind.LParen)) { SkipBalanced(TokKind.LParen, TokKind.RParen); continue; }
                // identifiers, commas, dots, ':' (script_ref) tolerated
                Next();
            }
        }

        private void Synchronize()
        {
            // advance to a safe recovery point
            while (!Is(TokKind.Eof))
            {
                if (Is(TokKind.Semicolon)) { Next(); return; }
                if (Is(TokKind.RBrace)) return;
                if (Cur.Kind == TokKind.Keyword && Cur.Text is "func" or "class" or "struct" or "enum" or "import") return;
                if (Is(TokKind.Annotation)) return;
                Next();
            }
        }
    }
}
