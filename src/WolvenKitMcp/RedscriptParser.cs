using System.Text;

namespace WolvenKitMcp;

// ════════════════════════════════════════════════════════════════════════
// Parser REDscript (.reds) — tokenizer + analyse récursive-descendante.
//
// Objectif : validation SYNTAXIQUE d'un fichier isolé (sans jeu ni base) —
// signatures typées, génériques, classes/structs/enums, annotations, blocs.
// Ce n'est PAS un type-checker (pas de résolution des types/méthodes externes :
// ça exige le compilateur scc + tout l'écosystème de mods, cf. WINDOWS-VALIDATION).
//
// Philosophie anti-faux-positifs : strict sur la structure stable (déclarations,
// types, en-têtes de statements) ; permissif mais équilibré sur les expressions
// (on valide l'équilibrage () [] {} et la séparation par opérateurs, sans
// imposer une grammaire d'expression complète qui risquerait de rejeter du
// REDscript valide). Récupération d'erreur par synchronisation sur ; } et
// mots-clés de déclaration, pour éviter les cascades.
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

    // Modificateurs pouvant précéder une déclaration (champ/fonction/classe).
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
            res.Diagnostics.Add(new RedDiagnostic(1, 1, "ERROR", "échec du tokenizer : " + ex.Message));
            return res;
        }
        new Impl(toks, res).ParseFile();
        return res;
    }

    // ── Lexer ────────────────────────────────────────────────────────────
    private static List<Tok> Lex(string s, RedParseResult res)
    {
        var toks = new List<Tok>(Math.Max(16, s.Length / 4)); // pré-dimensionné (~1 token/4 car.)
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
            // espaces
            if (c is ' ' or '\t' or '\r' or '\n') { Adv(); continue; }
            // commentaires
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
                if (!closed) res.Diagnostics.Add(new RedDiagnostic(sl, sc, "ERROR", "commentaire /* */ non fermé"));
                continue;
            }
            int tl = line, tc = col;
            // chaîne (préfixe optionnel n/r/t/l/s...) — gère l'interpolation
            // s"...\(expr)..." (guillemets imbriqués) et les chaînes multi-lignes.
            if (c == '"' || (char.IsLetter(c) && i + 1 < n && s[i + 1] == '"' && "nrtls".IndexOf(char.ToLowerInvariant(c)) >= 0))
            {
                var prefixed = c != '"';
                if (prefixed) Adv(); // consomme le préfixe
                Adv(); // "
                var ok = false;
                while (i < n)
                {
                    if (s[i] == '\\')
                    {
                        // Interpolation \( ... ) : peut contenir des "..." imbriqués.
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
                                    // saute une chaîne imbriquée (avec ses échappements)
                                    Adv();
                                    while (i < n && s[i] != '"')
                                    { if (s[i] == '\\') Adv(2); else Adv(); }
                                }
                                if (i < n && depth > 0) Adv();
                            }
                            if (i < n) Adv(); // consomme le ')'
                            continue;
                        }
                        Adv(2); continue; // échappement normal \", \n, ...
                    }
                    if (s[i] == '"') { Adv(); ok = true; break; }
                    Adv(); // les \n littéraux sont autorisés (chaînes multi-lignes)
                }
                if (!ok) res.Diagnostics.Add(new RedDiagnostic(tl, tc, "ERROR", "chaîne non terminée"));
                toks.Add(new Tok(prefixed ? TokKind.Name : TokKind.String, "\"\"", tl, tc));
                continue;
            }
            // nombre
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
            // identifiant / mot-clé
            if (char.IsLetter(c) || c == '_')
            {
                int st = i;
                while (i < n && (char.IsLetterOrDigit(s[i]) || s[i] == '_')) Adv();
                var word = s[st..i];
                toks.Add(new Tok(Keywords.Contains(word) ? TokKind.Keyword : TokKind.Ident, word, tl, tc));
                continue;
            }
            // ponctuation / opérateurs
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
            // opérateur générique (séquence de symboles)
            const string opChars = "+-*/%=<>!&|^~?";
            if (opChars.IndexOf(c) >= 0)
            {
                int st = i;
                while (i < n && opChars.IndexOf(s[i]) >= 0) Adv();
                toks.Add(new Tok(TokKind.Op, s[st..i], tl, tc));
                continue;
            }
            // caractère inconnu
            res.Diagnostics.Add(new RedDiagnostic(tl, tc, "ERROR", $"caractère inattendu : '{c}'"));
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
            Err($"attendu {what}, trouvé « {Describe(Cur)} »");
            return false;
        }

        private static string Describe(Tok t) => t.Kind == TokKind.Eof ? "fin de fichier"
            : string.IsNullOrEmpty(t.Text) ? t.Kind.ToString() : t.Text;

        public void ParseFile()
        {
            // module optionnel
            if (IsKw("module"))
            {
                Next();
                var name = ParseDottedName();
                _r.Module = name;
                // module peut être suivi d'un ; optionnel ou rien
                if (Is(TokKind.Semicolon)) Next();
            }
            while (!Is(TokKind.Eof) && _errors < MaxErrors)
            {
                int before = _p;
                ParseTopLevel();
                if (_p == before) { Next(); } // garantie de progression
            }
        }

        private string ParseDottedName()
        {
            var sb = new StringBuilder();
            if (Is(TokKind.Ident) || Is(TokKind.Keyword)) { sb.Append(Next().Text); }
            else { Err("nom attendu"); return ""; }
            while (Is(TokKind.Dot)) { Next(); if (Is(TokKind.Ident)) sb.Append('.').Append(Next().Text); else { if (IsOp("*")) { Next(); sb.Append(".*"); } break; } }
            return sb.ToString();
        }

        private void ParseTopLevel()
        {
            if (Is(TokKind.Semicolon)) { Next(); return; } // « ; » parasite au top-level
            var anns = ParseAnnotations();      // @if(...) peut précéder un import
            if (IsKw("import")) { ParseImport(); return; }
            ParseModifiers();
            if (IsKw("class") || IsKw("struct")) { ParseClass(); return; }
            if (IsKw("enum")) { ParseEnum(); return; }
            if (IsKw("func")) { ParseFunc(topLevel: true); return; }
            if (IsKw("let") || IsKw("const")) { ParseField(); return; }
            // rien de reconnu
            if (!Is(TokKind.Eof))
            {
                Err($"déclaration attendue (class/struct/enum/func/let), trouvé « {Describe(Cur)} »");
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
                Expect(TokKind.RBrace, "« } »");
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
            if (!Expect(TokKind.Ident, "nom de classe")) { Synchronize(); return; }
            _r.Declarations.Add(new RedDeclaration("class", nameTok.Text, nameTok.Line));
            if (IsKw("extends")) { Next(); ParseType(); }
            if (!Expect(TokKind.LBrace, "« { »")) return;
            while (!Is(TokKind.RBrace) && !Is(TokKind.Eof) && _errors < MaxErrors)
            {
                int before = _p;
                ParseMember();
                if (_p == before) Next();
            }
            Expect(TokKind.RBrace, "« } »");
        }

        private void ParseMember()
        {
            if (Is(TokKind.Semicolon)) { Next(); return; } // « ; » parasite après un membre
            ParseAnnotations();
            ParseModifiers();
            if (IsKw("func")) { ParseFunc(topLevel: false); return; }
            if (IsKw("let") || IsKw("const")) { ParseField(); return; }
            if (Is(TokKind.RBrace)) return;
            Err($"membre attendu (func/let), trouvé « {Describe(Cur)} »");
            Synchronize();
        }

        private void ParseEnum()
        {
            Next();
            var nameTok = Cur;
            if (!Expect(TokKind.Ident, "nom d'enum")) { Synchronize(); return; }
            _r.Declarations.Add(new RedDeclaration("enum", nameTok.Text, nameTok.Line));
            if (!Expect(TokKind.LBrace, "« { »")) return;
            while (!Is(TokKind.RBrace) && !Is(TokKind.Eof))
            {
                if (!(Is(TokKind.Ident) || Is(TokKind.Keyword))) { if (Is(TokKind.Comma)) { Next(); continue; } break; }
                Next();
                // valeur permissive : entier, hex, négatif, expression — jusqu'à , ou }
                if (IsOp("=")) { Next(); while (!Is(TokKind.Comma) && !Is(TokKind.RBrace) && !Is(TokKind.Eof)) Next(); }
                if (Is(TokKind.Comma)) Next();
            }
            Expect(TokKind.RBrace, "« } »");
        }

        private void ParseField()
        {
            Next(); // let/const
            var nameTok = Cur;
            // certains mots-clés contextuels (quest, wrapped...) servent de noms.
            if (Is(TokKind.Ident) || Is(TokKind.Keyword)) Next();
            else { Err($"nom de champ attendu, trouvé « {Describe(Cur)} »"); Synchronize(); return; }
            _r.Declarations.Add(new RedDeclaration("field", nameTok.Text, nameTok.Line));
            if (Is(TokKind.Colon)) { Next(); ParseType(); }
            if (IsOp("=")) { Next(); SkipExpression(); }
            Expect(TokKind.Semicolon, "« ; »");
        }

        private void ParseFunc(bool topLevel)
        {
            Next(); // func
            var nameTok = Cur;
            if (!(Is(TokKind.Ident) || Is(TokKind.Keyword)))
            { Err("nom de fonction attendu"); Synchronize(); return; }
            Next();
            _r.Declarations.Add(new RedDeclaration("func", nameTok.Text, nameTok.Line));
            // génériques optionnels <T, U>
            if (IsOp("<")) SkipAngles();
            if (!Expect(TokKind.LParen, "« ( »")) { Synchronize(); return; }
            ParseParams();
            Expect(TokKind.RParen, "« ) »");
            if (Is(TokKind.Arrow)) { Next(); ParseType(); }
            if (Is(TokKind.LBrace)) ParseBlock();
            else if (Is(TokKind.Semicolon)) Next();        // native func avec « ; »
            else if (IsOp("=")) { Next(); SkipExprBody(); } // func à corps d'expression : = expr
            // native func SANS corps ni « ; » : tolérée si suivie d'une frontière de
            // déclaration (prochain membre/annotation/« } »/EOF).
            else if (Is(TokKind.Annotation) || Is(TokKind.RBrace) || Is(TokKind.Eof)
                     || (Cur.Kind == TokKind.Keyword && IsMemberStart(Cur.Text)))
            { /* déclaration native sans corps — OK */ }
            else Err("« { » (corps), « = » (corps-expression) ou « ; » (native) attendu après la signature");
        }

        private void ParseParams()
        {
            while (!Is(TokKind.RParen) && !Is(TokKind.Eof))
            {
                // modificateurs de paramètre : const out opt
                while (IsKw("const") || IsKw("out") || IsKw("opt")) Next();
                if (!(Is(TokKind.Ident) || Is(TokKind.Keyword))) { Err("nom de paramètre attendu"); break; }
                Next();
                if (Expect(TokKind.Colon, "« : » (type du paramètre)")) ParseType();
                if (Is(TokKind.Comma)) Next(); else break;
            }
        }

        // Type : Name, a.b.Name, Generic<T, U>, [T] (tableau raccourci), imbriqués. Tolérant.
        private void ParseType()
        {
            // Raccourci tableau REDscript : [T] ou tableau dimensionné [T; N]
            if (Is(TokKind.LBracket))
            {
                Next(); ParseType();
                if (Is(TokKind.Semicolon)) { Next(); if (Is(TokKind.Int)) Next(); else Err("taille (entier) attendue dans [T; N]"); }
                Expect(TokKind.RBracket, "« ] » (fin de type tableau)");
                return;
            }
            if (!(Is(TokKind.Ident) || Is(TokKind.Keyword)))
            { Err($"type attendu, trouvé « {Describe(Cur)} »"); return; }
            Next();
            // Segment qualifié après « . » : accepter aussi les mots-clés contextuels
            // (cohérent avec le premier segment), pour éviter des faux positifs.
            while (Is(TokKind.Dot)) { Next(); if (Is(TokKind.Ident) || Is(TokKind.Keyword)) Next(); else { Err("type attendu après « . »"); break; } }
            if (IsOp("<")) SkipAngles();
            // tableaux suffixés [n] éventuels
            while (Is(TokKind.LBracket)) SkipBalanced(TokKind.LBracket, TokKind.RBracket);
        }

        private void ParseBlock()
        {
            Expect(TokKind.LBrace, "« { »");
            while (!Is(TokKind.RBrace) && !Is(TokKind.Eof) && _errors < MaxErrors)
            {
                int before = _p;
                ParseStatement();
                if (_p == before) Next();
            }
            Expect(TokKind.RBrace, "« } »");
        }

        private void ParseStatement()
        {
            if (Is(TokKind.LBrace)) { ParseBlock(); return; }
            if (Is(TokKind.Semicolon)) { Next(); return; }
            if (IsKw("let") || IsKw("const"))
            {
                Next();
                if (Is(TokKind.Ident) || Is(TokKind.Keyword)) Next(); else Err("nom de variable attendu");
                if (Is(TokKind.Colon)) { Next(); ParseType(); }
                if (IsOp("=")) { Next(); SkipExpression(); }
                Expect(TokKind.Semicolon, "« ; »");
                return;
            }
            if (IsKw("if"))
            {
                // REDscript : pas de parenthèses obligatoires (if cond { } else { }).
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
                if (Is(TokKind.LParen)) Next(); // for (x in y) toléré
                if (Is(TokKind.Ident) || Is(TokKind.Keyword)) Next(); else Err("variable d'itération attendue");
                if (IsKw("in")) Next(); else Err("« in » attendu dans for-in");
                SkipExprUntilBlock();
                ParseBlock();
                return;
            }
            if (IsKw("switch"))
            {
                Next(); SkipExprUntilBlock();
                Expect(TokKind.LBrace, "« { »");
                while (!Is(TokKind.RBrace) && !Is(TokKind.Eof) && _errors < MaxErrors)
                {
                    if (IsKw("case")) { Next(); SkipExpressionUntil(TokKind.Colon); Expect(TokKind.Colon, "« : »"); }
                    else if (IsKw("default")) { Next(); Expect(TokKind.Colon, "« : »"); }
                    else
                    {
                        int before = _p; ParseStatement(); if (_p == before) Next();
                    }
                }
                Expect(TokKind.RBrace, "« } »");
                return;
            }
            if (IsKw("return")) { Next(); if (!Is(TokKind.Semicolon)) SkipExpression(); Expect(TokKind.Semicolon, "« ; »"); return; }
            if (IsKw("break") || IsKw("continue")) { Next(); Expect(TokKind.Semicolon, "« ; »"); return; }
            // sinon : statement d'expression
            SkipExpression();
            Expect(TokKind.Semicolon, "« ; »");
        }

        // ── Expressions : validation permissive par équilibrage ──────────────
        // On consomme jusqu'au ; ou } de fin de statement, en vérifiant
        // l'équilibrage des () [] {} (lambdas/array literals) et en rejetant les
        // jetons clairement invalides en position d'expression. On NE modélise PAS
        // la précédence : tout opérateur infixe/préfixe est accepté.
        private void SkipExpression() => SkipExpressionUntil(TokKind.Semicolon);

        // Corps d'expression d'une fonction « = expr » (sans « ; » terminal en
        // REDscript) : on consomme jusqu'à la prochaine frontière de membre/déclaration
        // (mot-clé de déclaration/modificateur, annotation, « } »), en équilibrant
        // les parenthèses/crochets/accolades (appels, lambdas, array literals).
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
                    case TokKind.RBrace: return;            // fin de la classe englobante
                    case TokKind.Semicolon: Next(); return; // terminateur optionnel
                    case TokKind.Annotation: return;        // prochain membre annoté
                    case TokKind.Keyword:
                        if (IsMemberStart(Cur.Text)) return; // func/let/modificateur → prochain membre
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

        // Consomme une condition d'expression jusqu'au « { » du bloc (REDscript
        // n'exige pas de parenthèses). Gère les lambdas à corps « -> { } » pour
        // ne pas confondre leur accolade avec le début du bloc.
        private void SkipExprUntilBlock()
        {
            while (!Is(TokKind.Eof))
            {
                switch (Cur.Kind)
                {
                    case TokKind.LParen: SkipBalanced(TokKind.LParen, TokKind.RParen); break;
                    case TokKind.LBracket: SkipBalanced(TokKind.LBracket, TokKind.RBracket); break;
                    case TokKind.LBrace: return; // début du bloc
                    case TokKind.Semicolon: return; // sécurité (corps manquant)
                    case TokKind.Arrow:
                        Next();
                        if (Is(TokKind.LBrace)) SkipBalanced(TokKind.LBrace, TokKind.RBrace); // lambda à corps
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
                        // fermeture non appariée rencontrée hors contexte
                        if (stop == TokKind.Semicolon) return; // laisse le statement se terminer
                        Err($"« {Cur.Text} » non apparié dans l'expression");
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
            if (depth != 0) Err($"« {openTok.Text} » non fermé", openTok);
        }

        // Génériques <...> : on saute en équilibrant < et > (les opérateurs
        // composés comme >> sont gérés caractère par caractère via le texte).
        private void SkipAngles()
        {
            // Cur est un Op commençant par '<'
            int depth = 0;
            // décompose le token op courant
            void Count(string op) { foreach (var ch in op) { if (ch == '<') depth++; else if (ch == '>') depth--; } }
            Count(Next().Text);
            while (depth > 0 && !Is(TokKind.Eof))
            {
                if (Cur.Kind == TokKind.Op && (Cur.Text.Contains('<') || Cur.Text.Contains('>'))) { Count(Next().Text); continue; }
                if (Is(TokKind.LParen)) { SkipBalanced(TokKind.LParen, TokKind.RParen); continue; }
                // identifiants, virgules, points, ':' (script_ref) tolérés
                Next();
            }
        }

        private void Synchronize()
        {
            // avance jusqu'à un point de reprise sûr
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
