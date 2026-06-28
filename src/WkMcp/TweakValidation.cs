using System.Collections;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;

namespace WkMcp;

/// <summary>
/// Pure, dependency-free core for the typed validation of TweakXL files (T1).
/// The daemon owns the TweakDB model; this side only classifies the *kind* of a
/// tweak value and of a flat (the latter parsed from `tweakdb-describe` output),
/// and decides whether they are compatible. Deliberately lenient: it only flags
/// high-confidence mismatches (a non-number into a numeric flat, an array/scalar
/// dimension mismatch, …) so that TweakXL's ambiguous *implicit* value formats
/// never produce false positives.
/// </summary>
internal static class TweakValidation
{
    internal enum Kind { Unknown, Int, Float, Bool, String, CName, TweakDBID, LocKey, ResRef, Array, Struct }

    /// <summary>One `record.field: value` assignment discovered in a .tweak.</summary>
    internal readonly record struct Assignment(string Record, string Field, object? Value, string? BaseRecord);

    // ── value classification ────────────────────────────────────────────────

    /// <summary>Classifies a YAML-parsed tweak value (scalar string, sequence or
    /// mapping) into a <see cref="Kind"/>, honoring TweakXL's explicit value
    /// formats. Barewords fall back to <see cref="Kind.String"/> (string-like).</summary>
    internal static Kind ClassifyValue(object? node)
    {
        switch (node)
        {
            case null: return Kind.Unknown;
            case IDictionary: return Kind.Struct;
            case string s: return ClassifyScalar(s);
            case bool: return Kind.Bool;
            case sbyte or byte or short or ushort or int or uint or long or ulong: return Kind.Int;
            case float or double or decimal: return Kind.Float;
            case IEnumerable: return Kind.Array; // any non-string sequence
            default: return Kind.Unknown;
        }
    }

    private static Kind ClassifyScalar(string raw)
    {
        var s = raw.Trim();
        if (s.Length == 0) return Kind.String;

        // Explicit type formats (unambiguous).
        if (s.StartsWith("n\"") || s.StartsWith("CName(") || s == "None") return Kind.CName;
        if (s.StartsWith("t\"") || s.StartsWith("TweakDBID(") || s.StartsWith("<TDBID:")) return Kind.TweakDBID;
        if (s.StartsWith("l\"") || s.StartsWith("LocKey(") || s.StartsWith("LocKey#")) return Kind.LocKey;
        if (s.StartsWith("r\"") || s.StartsWith("ResRef(")) return Kind.ResRef;
        if (s.StartsWith("\"")) return Kind.String;

        if (s is "true" or "false") return Kind.Bool;
        if (Regex.IsMatch(s, @"^[+-]?0x[0-9A-Fa-f]+$")) return Kind.Int;
        if (Regex.IsMatch(s, @"^[+-]?\d+(\.\d+)?[eE][+-]?\d+$") || Regex.IsMatch(s, @"^[+-]?\d+\.\d+$"))
            return Kind.Float;
        if (Regex.IsMatch(s, @"^[+-]?\d+$")) return Kind.Int;

        // Implicit bareword (could be a CName/TweakDBID/String/…): treat as string-like.
        return Kind.String;
    }

    // ── flat type (from the daemon's describe output) ───────────────────────

    /// <summary>Maps a flat's type token — either a WolvenKit RED type name
    /// (<c>CName</c>, <c>TweakDBID</c>, <c>CFloat</c>, <c>CArray`1</c>, …) or a CLR
    /// fallback (<c>Int64</c>, <c>Double</c>, <c>String</c>, <c>List</c>) — to a
    /// <see cref="Kind"/>. Unknown/complex types (handles, records, structs) map to
    /// <see cref="Kind.Unknown"/> so they are never flagged.</summary>
    internal static Kind RedTypeToKind(string? typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName)) return Kind.Unknown;
        var n = typeName.Trim();
        var tick = n.IndexOf('`');
        if (tick >= 0) n = n[..tick];

        if (n.StartsWith("CArray") || n.StartsWith("CStatic") || n == "List" || n.EndsWith("[]"))
            return Kind.Array;
        switch (n)
        {
            case "CName": return Kind.CName;
            case "TweakDBID": return Kind.TweakDBID;
            case "CBool": case "Boolean": return Kind.Bool;
            case "CString": case "String": return Kind.String;
            case "CFloat": case "CDouble": case "Single": case "Double": return Kind.Float;
            case "gamedataLocKeyWrapper": case "LocKey": return Kind.LocKey;
        }
        if (n.StartsWith("CInt") || n.StartsWith("CUint") || n.StartsWith("CUInt")
            || n is "Int16" or "Int32" or "Int64" or "UInt16" or "UInt32" or "UInt64" or "Byte" or "SByte")
            return Kind.Int;
        if (n.Contains("ResourceAsyncReference") || n.Contains("ResourceReference") || n.StartsWith("raRef"))
            return Kind.ResRef;
        return Kind.Unknown;
    }

    private static readonly Regex FlatLine =
        new(@"\bflat\s+(?<field>\S+)\s*:\s*(?<type>[^=\s]+)\s*=", RegexOptions.Compiled);

    /// <summary>Parses the per-flat lines of a <c>tweakdb-describe</c> run
    /// (<c>… flat &lt;field&gt; : &lt;type&gt; = &lt;value&gt;</c>) into a field → kind map.</summary>
    internal static Dictionary<string, Kind> ParseDescribedFlats(string? describeOutput)
    {
        var map = new Dictionary<string, Kind>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(describeOutput)) return map;
        foreach (Match m in FlatLine.Matches(describeOutput))
            map[m.Groups["field"].Value] = RedTypeToKind(m.Groups["type"].Value);
        return map;
    }

    private static readonly Regex FlatValueLine =
        new(@"\bflat\s+(?<field>\S+)\s*:\s*\S+\s*=\s*(?<value>.*)$",
            RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>Parses the same describe lines into a field → current-value map
    /// (the "before" side of a preview).</summary>
    internal static Dictionary<string, string> ParseDescribedValues(string? describeOutput)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(describeOutput)) return map;
        foreach (Match m in FlatValueLine.Matches(describeOutput))
            map[m.Groups["field"].Value] = m.Groups["value"].Value.Trim();
        return map;
    }

    /// <summary>True for a scalar tweak value (i.e. not a sequence/array or a
    /// mapping/inline-record); used to scope the v1 scalar preview.</summary>
    internal static bool IsScalarValue(object? node)
        => ClassifyValue(node) is not (Kind.Array or Kind.Struct);

    /// <summary>Renders a scalar tweak value as it appears in the file (the "after").</summary>
    internal static string RenderValue(object? node) => node?.ToString()?.Trim() ?? "(null)";

    // ── array mutation operators (T2 full) ──────────────────────────────────

    internal enum ArrayOpKind { Append, Prepend, AppendOnce, PrependOnce, Remove, AppendFrom, PrependFrom }
    internal readonly record struct ArrayOp(ArrayOpKind Kind, string Operand);

    /// <summary>An array flat the tweak mutates or replaces. <see cref="Ops"/> is non-empty
    /// for a mutation; otherwise <see cref="Replacement"/> holds the literal new list.</summary>
    internal readonly record struct ArrayAssignment(
        string Record, string Field, IReadOnlyList<ArrayOp> Ops,
        IReadOnlyList<string> Replacement, string? BaseRecord);

    private static readonly Regex ElemLine =
        new(@"\belem\s+(?<field>\S+)\s+\[(?<idx>\d+|\.\.)\]\s*=\s*(?<value>.*)$",
            RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>Parses the `elem field [i] = value` lines of a describe run into a
    /// field → current-elements map (the "before" side of an array preview).</summary>
    internal static Dictionary<string, List<string>> ParseDescribedArrayElements(string? describeOutput)
    {
        var map = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(describeOutput)) return map;
        foreach (Match m in ElemLine.Matches(describeOutput))
        {
            if (m.Groups["idx"].Value == "..") continue; // truncation marker
            var field = m.Groups["field"].Value;
            if (!map.TryGetValue(field, out var list)) { list = new(); map[field] = list; }
            list.Add(m.Groups["value"].Value.Trim());
        }
        return map;
    }

    private static bool TryOpKind(string tag, out ArrayOpKind kind)
    {
        kind = tag switch
        {
            "!append" => ArrayOpKind.Append,
            "!prepend" => ArrayOpKind.Prepend,
            "!append-once" => ArrayOpKind.AppendOnce,
            "!prepend-once" => ArrayOpKind.PrependOnce,
            "!remove" => ArrayOpKind.Remove,
            "!append-from" => ArrayOpKind.AppendFrom,
            "!prepend-from" => ArrayOpKind.PrependFrom,
            _ => (ArrayOpKind)(-1),
        };
        return (int)kind >= 0;
    }

    private static string ItemOperand(YamlNode item) => item switch
    {
        YamlScalarNode s => s.Value ?? "",
        YamlMappingNode => "(inline record)",
        YamlSequenceNode => "(nested array)",
        _ => item.ToString() ?? "",
    };

    /// <summary>Parses array-flat assignments from a .tweak, reading each sequence item's
    /// YAML tag (`!append`, `!remove`, `!append-from`, …) via the representation model
    /// (which, unlike <c>Deserialize&lt;object&gt;</c>, preserves tags). A sequence with no
    /// op tags is a full replacement.</summary>
    internal static List<ArrayAssignment> EnumerateArrayOps(string yamlText)
    {
        var result = new List<ArrayAssignment>();
        var stream = new YamlStream();
        try { stream.Load(new StringReader(yamlText)); }
        catch { return result; }
        if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode top)
            return result;

        foreach (var (k, v) in top.Children)
        {
            var recordId = (k as YamlScalarNode)?.Value?.Trim();
            if (string.IsNullOrEmpty(recordId) || recordId.StartsWith("$") || recordId.StartsWith("#")) continue;
            if (v is not YamlMappingNode body) continue;

            string? baseRec = null;
            foreach (var (fk, fv) in body.Children)
            {
                var field = (fk as YamlScalarNode)?.Value?.Trim();
                if (string.IsNullOrEmpty(field)) continue;
                if (field is "$base" or "$instanceOf") { baseRec = (fv as YamlScalarNode)?.Value?.Trim(); continue; }
                if (field.StartsWith("$")) continue;
                if (fv is not YamlSequenceNode seq) continue;

                var ops = new List<ArrayOp>();
                var replacement = new List<string>();
                var anyTag = false;
                foreach (var item in seq.Children)
                {
                    var tag = item.Tag.IsEmpty ? null : item.Tag.Value;
                    if (tag is not null && tag.StartsWith("!") && TryOpKind(tag, out var kind))
                    {
                        anyTag = true;
                        ops.Add(new ArrayOp(kind, ItemOperand(item)));
                    }
                    else
                    {
                        replacement.Add(ItemOperand(item));
                    }
                }
                result.Add(new ArrayAssignment(recordId!, field!, ops,
                    anyTag ? Array.Empty<string>() : replacement, baseRec));
            }
        }
        return result;
    }

    /// <summary>Applies array mutation operators to the current element list (pure). Append/
    /// prepend add; *-once dedupe; remove drops all matches; *-from pulls (deduped) from the
    /// list returned by <paramref name="resolveFrom"/>.</summary>
    internal static List<string> ApplyArrayOps(
        IReadOnlyList<string> current, IReadOnlyList<ArrayOp> ops,
        Func<string, IReadOnlyList<string>>? resolveFrom = null)
    {
        var list = new List<string>(current);
        foreach (var op in ops)
        {
            switch (op.Kind)
            {
                case ArrayOpKind.Append: list.Add(op.Operand); break;
                case ArrayOpKind.Prepend: list.Insert(0, op.Operand); break;
                case ArrayOpKind.AppendOnce: if (!list.Contains(op.Operand)) list.Add(op.Operand); break;
                case ArrayOpKind.PrependOnce: if (!list.Contains(op.Operand)) list.Insert(0, op.Operand); break;
                case ArrayOpKind.Remove: list.RemoveAll(x => x == op.Operand); break;
                case ArrayOpKind.AppendFrom:
                    foreach (var e in resolveFrom?.Invoke(op.Operand) ?? Array.Empty<string>())
                        if (!list.Contains(e)) list.Add(e);
                    break;
                case ArrayOpKind.PrependFrom:
                    var src = (resolveFrom?.Invoke(op.Operand) ?? Array.Empty<string>())
                        .Where(e => !list.Contains(e)).ToList();
                    list.InsertRange(0, src);
                    break;
            }
        }
        return list;
    }

    // ── compatibility ───────────────────────────────────────────────────────

    /// <summary>True if a value of <paramref name="value"/> kind may legitimately be
    /// assigned to a flat of <paramref name="flat"/> kind. Lenient by design: only
    /// strict-scalar flats (Int/Float/Bool) and the array↔scalar dimension are
    /// enforced; name/string-family flats accept any scalar (their implicit formats
    /// make almost anything plausible), and Unknown on either side always passes.</summary>
    internal static bool AreCompatible(Kind value, Kind flat)
    {
        if (flat == Kind.Unknown || value == Kind.Unknown) return true;

        var valueIsArray = value == Kind.Array;
        var flatIsArray = flat == Kind.Array;
        if (valueIsArray != flatIsArray) return false; // array ↔ scalar mismatch
        if (valueIsArray) return true;                 // both arrays (element types not inspected in v1)

        return flat switch
        {
            Kind.Int => value == Kind.Int,                       // a float/string literal into an Int flat is wrong
            Kind.Float => value is Kind.Int or Kind.Float,       // ints are valid floats
            Kind.Bool => value == Kind.Bool,
            _ => true,                                           // CName/TweakDBID/String/LocKey/ResRef: lenient
        };
    }

    /// <summary>Human label for a kind, used in findings.</summary>
    internal static string Label(Kind k) => k.ToString();

    // ── tweak walking ───────────────────────────────────────────────────────

    /// <summary>Enumerates the `record.field → value` assignments of a parsed
    /// .tweak (YamlDotNet object tree). Handles both the nested form
    /// (<c>Record:\n  field: v</c>) and the flattened form (<c>Record.field: v</c>),
    /// captures a <c>$base</c>/<c>$instanceOf</c> base per record, and skips
    /// directives and top-level template blocks.</summary>
    internal static List<Assignment> EnumerateAssignments(object? root)
    {
        var result = new List<Assignment>();
        if (root is not IDictionary top) return result;

        foreach (DictionaryEntry e in top)
        {
            var key = e.Key?.ToString()?.Trim() ?? "";
            if (key.Length == 0 || key.StartsWith("#") || key.StartsWith("$")) continue;

            if (e.Value is IDictionary body)
            {
                // Record body: the key is the record id; iterate its fields.
                var baseRec = DirectiveValue(body, "$base") ?? DirectiveValue(body, "$instanceOf");
                foreach (DictionaryEntry fe in body)
                {
                    var field = fe.Key?.ToString()?.Trim() ?? "";
                    if (field.Length == 0 || field.StartsWith("$")) continue; // directive
                    result.Add(new Assignment(key, field, fe.Value, baseRec));
                }
            }
            else
            {
                // Flattened "record.field: value" (last dotted segment = field).
                var dot = key.LastIndexOf('.');
                if (dot <= 0 || dot >= key.Length - 1) continue; // bare record / malformed → skip
                result.Add(new Assignment(key[..dot], key[(dot + 1)..], e.Value, null));
            }
        }
        return result;
    }

    private static string? DirectiveValue(IDictionary body, string directive)
    {
        foreach (DictionaryEntry e in body)
            if (string.Equals(e.Key?.ToString(), directive, StringComparison.Ordinal))
                return e.Value?.ToString();
        return null;
    }
}
