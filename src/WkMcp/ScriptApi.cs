using System;
using System.Collections.Generic;
using System.Linq;

namespace WkMcp;

// ════════════════════════════════════════════════════════════════════════
// REDscript symbol index (R1) — "ctags for .reds".
//
// Builds a queryable index of the declarations across a folder of REDscript
// sources (a decompiled game-scripts dump, a mod's r6/scripts, …) so an agent
// can look up the exact SIGNATURE + file:line it needs to @wrapMethod /
// @replaceMethod a game method, or find which class declares a field.
//
// Pure core: maps RedscriptParser declarations to ScriptSymbol and filters
// them. File IO lives in the tool; everything here is unit-testable.
// NOT a type resolver — syntactic index only (the parser does no type check).
// ════════════════════════════════════════════════════════════════════════
internal static class ScriptApi
{
    internal sealed record ScriptSymbol(
        string Kind,                          // class | struct | enum | func | field
        string Name,
        string? Parent,                       // method/field: enclosing class; class: extends; annotated free func: @target
        string? Signature,                    // func: "name(p: T, …) -> Ret"; field: "name: Type"
        IReadOnlyList<string> Annotations,
        string File,
        int Line);

    /// <summary>Parses one source file and projects its declarations to symbols.</summary>
    public static List<ScriptSymbol> SymbolsOf(string file, string source)
    {
        var res = RedscriptParser.Parse(source);
        var list = new List<ScriptSymbol>(res.Declarations.Count);
        foreach (var d in res.Declarations)
            list.Add(new ScriptSymbol(
                d.Kind, d.Name, d.Parent, d.Signature,
                d.Annotations ?? Array.Empty<string>(), file, d.Line));
        return list;
    }

    /// <summary>
    /// Filters symbols by a case-insensitive name substring, an optional kind
    /// (the synthetic kinds <c>method</c> = func with an enclosing/target class
    /// and <c>global</c> = free func are recognised), and an optional class.
    /// Results are sorted (name, file, line) and capped.
    /// </summary>
    public static List<ScriptSymbol> Query(
        IEnumerable<ScriptSymbol> symbols, string? query, string? kind, string? ofClass, int max)
    {
        IEnumerable<ScriptSymbol> q = symbols;

        if (!string.IsNullOrWhiteSpace(query))
            q = q.Where(s => s.Name.Contains(query, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(kind))
        {
            var k = kind.Trim().ToLowerInvariant();
            q = k switch
            {
                "method" => q.Where(s => s.Kind == "func" && !string.IsNullOrEmpty(s.Parent)),
                "global" => q.Where(s => s.Kind == "func" && string.IsNullOrEmpty(s.Parent)),
                _ => q.Where(s => string.Equals(s.Kind, k, StringComparison.OrdinalIgnoreCase)),
            };
        }

        if (!string.IsNullOrWhiteSpace(ofClass))
            q = q.Where(s => string.Equals(s.Parent, ofClass, StringComparison.OrdinalIgnoreCase));

        return q
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.File, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.Line)
            .Take(max <= 0 ? 200 : max)
            .ToList();
    }
}
