using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace WkMcp;

// ════════════════════════════════════════════════════════════════════════
// R2 — type-check REDscript via the bundled `scc` compiler.
//
// Pure parsing of scc's console output into structured diagnostics, calibrated
// against the real format emitted by the scc that ships with the game:
//
//   [ERROR - <ts>] [UNRESOLVED_REF] At C:/…/r6\scripts\Mod\File.reds:4:1:
//   @addMethod(TweakDBManager)
//   ^^^^^^^^^^^^^^^^^^^^^^^^^^
//   unresolved reference 'TweakDBManager'
//
//   [WARN - <ts>] At C:/…/File.reds:1:1:           (no error code)
//   …
//
//   [ERROR - <ts>] REDScript compilation has failed.   (final summary, no location)
//
// Each header line ("[SEVERITY …]") opens a diagnostic; the human-readable
// message is the last non-empty, non-caret line of the block that follows.
// Note the path embeds spaces ("Cyberpunk 2077") and mixed slashes — handled.
// The process plumbing (locating scc, the safe temp-output invocation) lives in
// the tool; this stays unit-testable.
// ════════════════════════════════════════════════════════════════════════
internal static class SccDiagnostics
{
    internal sealed record Diagnostic(string Severity, string? Code, string Message, string? File, int? Line, int? Col);

    // "[ERROR - …]" / "[WARN - …]" / "[INFO - …]" opening a line, then the rest.
    private static readonly Regex Header = new(
        @"^\s*\[\s*(?<sev>ERROR|ERR|WARNING|WARN|INFO)\b[^\]]*\]\s*(?<rest>.*)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Optional "[CODE]" then optional "At <path>.reds:line:col" inside the rest.
    private static readonly Regex Code = new(@"^\[(?<code>[A-Za-z0-9_]+)\]\s*", RegexOptions.Compiled);
    private static readonly Regex Location = new(
        @"\bAt\s+(?<file>.+?\.reds):(?<line>\d+)(?::(?<col>\d+))?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Parses scc console output into structured diagnostics.</summary>
    public static List<Diagnostic> Parse(string output)
    {
        var diags = new List<Diagnostic>();
        if (string.IsNullOrEmpty(output)) return diags;

        var lines = output.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var h = Header.Match(lines[i]);
            if (!h.Success) continue;

            var severity = Normalize(h.Groups["sev"].Value);
            if (severity is null) continue;                     // INFO and friends are not diagnostics

            var rest = h.Groups["rest"].Value;
            string? code = null;
            var cm = Code.Match(rest);
            if (cm.Success) { code = cm.Groups["code"].Value; rest = rest[cm.Length..]; }

            string? file = null; int? line = null, col = null;
            var loc = Location.Match(rest);
            if (loc.Success)
            {
                file = loc.Groups["file"].Value.Trim();
                if (int.TryParse(loc.Groups["line"].Value, out var l)) line = l;
                if (loc.Groups["col"].Success && int.TryParse(loc.Groups["col"].Value, out var c)) col = c;
                rest = rest[(loc.Index + loc.Length)..];
            }

            // Message: trailing prose on the header line (e.g. "… compilation has
            // failed."), else the last meaningful line of the following block.
            var inline = rest.Trim().TrimEnd(':').Trim();
            var message = inline.Length > 0 ? inline : BlockMessage(lines, i) ?? code ?? lines[i].Trim();

            diags.Add(new Diagnostic(severity, code, message, file, line, col));
        }
        return diags;
    }

    // Last non-empty, non-caret line of the block between this header and the
    // next blank line / next header — the actual error text in scc's layout.
    private static string? BlockMessage(string[] lines, int headerIndex)
    {
        string? msg = null;
        for (var j = headerIndex + 1; j < lines.Length; j++)
        {
            var t = lines[j].Trim();
            if (t.Length == 0) break;
            if (Header.IsMatch(lines[j])) break;
            if (t.All(ch => ch == '^')) continue;               // caret underline
            msg = t;
        }
        return msg;
    }

    private static string? Normalize(string sev) => sev.ToUpperInvariant() switch
    {
        "ERROR" or "ERR" => "error",
        "WARNING" or "WARN" => "warning",
        _ => null,                                              // INFO, etc.
    };

    public static int Count(IEnumerable<Diagnostic> diags, string severity)
        => diags.Count(d => string.Equals(d.Severity, severity, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Heuristic: did scc reject our command line itself (wrong flag form for
    /// this scc generation), as opposed to reporting script errors? Lets the
    /// tool surface "the CLI form wasn't accepted" instead of a false
    /// "your scripts are broken". scc prints e.g.
    /// "Expected &lt;-compile&gt;, pass --help for usage information".
    /// </summary>
    public static bool LooksLikeUsageError(string output)
        => !string.IsNullOrEmpty(output) && Regex.IsMatch(
            output,
            @"\b(usage:|pass --help for usage|expected <|unrecognized option|unexpected argument|unknown (?:flag|option|argument)|invalid (?:option|argument))",
            RegexOptions.IgnoreCase);
}
