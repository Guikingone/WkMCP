using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace WolvenKitMcp;

/// <summary>
/// MCP tools for modding Cyberpunk 2077 — inspection, conversion and
/// mod creation. Most delegate to a <c>cp77tools</c> command
/// (the WolvenKit CLI); a few operate directly on the file system.
///
/// Each tool returns a structured JSON object (cf. <see cref="Structured"/>):
/// <c>{ ok, status, summary, produced, warnings, errors, exitCode, log }</c> —
/// far more reliable for an agent to analyze than raw log text. Success
/// is determined from the files actually produced, not from a simple
/// log marker (which a non-fatal error, e.g. exporting a mesh's materials,
/// could throw off).
/// </summary>
[McpServerToolType]
public static class WolvenKitTools
{
    /// <summary>Adapts the daemon's text progress (one log line every
    /// ~500 ms) into MCP progress notifications. Since the total is unknown on the
    /// daemon side, Progress is a simple increasing counter + the message.</summary>
    private sealed class ProgressRelay : IProgress<string>
    {
        private readonly IProgress<ProgressNotificationValue> _target;
        private int _n;
        public ProgressRelay(IProgress<ProgressNotificationValue> target) => _target = target;
        public void Report(string message)
            => _target.Report(new ProgressNotificationValue
            {
                Progress = Interlocked.Increment(ref _n),
                Message = message,
            });
    }

    internal static IProgress<string> Relay(IProgress<ProgressNotificationValue> progress)
        => new ProgressRelay(progress);

    // ── Diagnostic ────────────────────────────────────────────────────────

    [McpServerTool(Name = "wolvenkit_status", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Checks that the WolvenKit CLI (cp77tools) is available and functional " +
                 "on this machine, and returns its version + LRU cache stats for archive " +
                 "listings (hits/misses since server startup). Call this first " +
                 "to diagnose the installation.")]
    public static async Task<string> Status(Cp77ToolsRunner runner, CancellationToken ct)
    {
        if (!runner.ToolExists)
            return Err($"cp77tools not found: {runner.ToolPath}. " +
                       "Install with: dotnet tool install -g WolvenKit.CLI");

        var r = await runner.RunAsync(new[] { "--version" }, ct);
        var (hits, misses, entries) = runner.CacheStats;
        var total = hits + misses;
        var hitRate = total > 0 ? Math.Round((double)hits / total, 3) : 0.0;
        var metricsTop = runner.MetricsSnapshot().Take(10)
            .Select(m => new { verb = m.verb, calls = m.calls, totalMs = m.totalMs,
                               p50 = m.p50, p95 = m.p95 })
            .ToList();
        var log = (r.Stdout + r.Stderr).Trim();

        return JsonSerializer.Serialize(new
        {
            ok = r.ExitCode == 0,
            status = r.ExitCode == 0 ? "success" : "error",
            summary = r.ExitCode == 0
                ? $"cp77tools operational — {runner.ToolPath}"
                : $"cp77tools present but --version failed (exit={r.ExitCode}) — {runner.ToolPath}",
            produced = Array.Empty<string>(),
            warnings = LogLines(log, "Warning"),
            errors = LogLines(log, "Error"),
            toolPath = runner.ToolPath,
            cache = new { hits, misses, entries, hitRate },
            metrics = metricsTop,
            exitCode = r.ExitCode,
            log = Truncate(log, 12_000),
        }, JsonOpts);
    }

    [McpServerTool(Name = "extract_localization", ReadOnly = false, Destructive = false, Idempotent = true)]
    [Description("Extracts from a tweakdb.bin all \"translatable\" fields of records " +
                 "(displayName, localizedDescription, description, etc.) — basis for making a " +
                 "UI translation mod. Output: JSON `{recordId: {field: value}}`. The optional " +
                 "filter (substring on the recordId) lets you target part of the game " +
                 "(e.g. `Items.` to extract only items). Limitation: only covers " +
                 "UI strings in TweakDB; audio subtitles (.opusinfo) remain on the " +
                 "roadmap.")]
    public static async Task<string> ExtractLocalization(
        Cp77ToolsRunner runner,
        [Description("Path to the tweakdb.bin file (typically <game>/r6/cache/tweakdb.bin).")] string tweakdbPath,
        [Description("Path to the output JSON file (will be created/overwritten).")] string outputJson,
        [Description("Optional filter: substring to search for in the recordId (e.g. \"Items.\").")] string? filter = null,
        CancellationToken ct = default)
    {
        if (!File.Exists(tweakdbPath))
            return Err($"tweakdb.bin file not found: {tweakdbPath}");

        Directory.CreateDirectory(Path.GetDirectoryName(outputJson) ?? ".");
        var args = new List<string>
        {
            "tweakdb-extract-localization", tweakdbPath, outputJson,
        };
        if (!string.IsNullOrWhiteSpace(filter)) args.Add(filter);
        var r = await runner.RunAsync(args, ct);
        var produced = File.Exists(outputJson)
            ? new List<string> { outputJson }
            : new List<string>();
        return Structured(
            $"TweakDB localization extraction → {outputJson}" +
            (string.IsNullOrWhiteSpace(filter) ? "" : $" (filter: {filter})"),
            r, produced);
    }

    [McpServerTool(Name = "build_localization", ReadOnly = false, Destructive = false, Idempotent = true)]
    [Description("Builds a .tweak file (TweakXL) that overrides displayName / " +
                 "localizedDescription / etc. from a translations JSON file in the format " +
                 "`{recordId: {field: \"Translation\"}}` produced by extract_localization then edited. " +
                 "The .tweak can then be installed via install_tweak. The `lang` parameter is " +
                 "purely informative (added as a comment; the game has no per-language localization " +
                 "in TweakDB).")]
    public static async Task<string> BuildLocalization(
        [Description("Path to the translations JSON file (from extract_localization, edited).")] string translationsJson,
        [Description("Path to the .tweak file to produce.")] string outputTweak,
        [Description("Language code (informative, added as a comment). E.g. fr-fr, de-de.")] string lang = "fr-fr",
        CancellationToken ct = default)
    {
        if (!File.Exists(translationsJson))
            return Err($"Translations file not found: {translationsJson}");

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(await File.ReadAllTextAsync(translationsJson, ct));
        }
        catch (JsonException ex)
        {
            return Err($"Invalid JSON: {ex.Message}");
        }
        using var _ = doc;
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            return Err("translationsJson must have an object at the root ({recordId: {field: value}}).");

        var sb = new StringBuilder();
        sb.Append("# Localization mod — lang ").AppendLine(lang);
        sb.AppendLine("# Generated by build_localization (WolvenKit MCP)");
        sb.AppendLine();

        int recordCount = 0, fieldCount = 0;
        foreach (var rec in doc.RootElement.EnumerateObject())
        {
            if (rec.Value.ValueKind != JsonValueKind.Object) continue;
            sb.Append(rec.Name).AppendLine(":");
            foreach (var field in rec.Value.EnumerateObject())
            {
                if (field.Value.ValueKind != JsonValueKind.String) continue;
                var value = field.Value.GetString() ?? "";
                sb.Append("  ").Append(field.Name).Append(": ")
                  .AppendLine(FormatYamlValue(value));
                fieldCount++;
            }
            sb.AppendLine();
            recordCount++;
        }

        if (recordCount == 0)
            return Err("No translatable record found in the JSON (each entry must be an object).");

        Directory.CreateDirectory(Path.GetDirectoryName(outputTweak) ?? ".");
        await File.WriteAllTextAsync(outputTweak, sb.ToString(), ct);

        return JsonSerializer.Serialize(new
        {
            ok = true,
            status = "success",
            summary = $"Localization mod built: {outputTweak} " +
                      $"({recordCount} record(s), {fieldCount} translated field(s), lang {lang})",
            produced = new[] { outputTweak },
            warnings = Array.Empty<string>(),
            errors = Array.Empty<string>(),
            outputTweak,
            recordCount,
            fieldCount,
            lang,
        }, JsonOpts);
    }

    [McpServerTool(Name = "clear_cache", ReadOnly = false, Destructive = false, Idempotent = true)]
    [Description("Manually clears the server's caches. scope = `archives` (default, clears " +
                 "the LRU cache of archive listings), `metrics` (clears the per-verb " +
                 "metrics counters), or `all` (both). Useful after out-of-band " +
                 "changes that would invalidate the cache, or to reset stats before " +
                 "a benchmark.")]
    public static string ClearCache(
        Cp77ToolsRunner runner,
        [Description("Scope to clear: archives | metrics | all.")] string scope = "archives")
    {
        scope = scope.ToLowerInvariant();
        if (scope is not ("archives" or "metrics" or "all"))
            return Err($"Unknown scope: {scope} (archives | metrics | all).");

        var cleared = new Dictionary<string, int>();
        if (scope is "archives" or "all")
        {
            var (_, _, entriesBefore) = runner.CacheStats;
            runner.InvalidateArchiveCache();
            cleared["archives"] = entriesBefore;
        }
        if (scope is "metrics" or "all")
        {
            var metricsBefore = runner.MetricsSnapshot().Count;
            runner.ResetMetrics();
            cleared["metrics"] = metricsBefore;
        }

        return JsonSerializer.Serialize(new
        {
            ok = true,
            status = "success",
            summary = $"Cache cleared (scope {scope}): " +
                      string.Join(", ", cleared.Select(kv => $"{kv.Value} {kv.Key}")),
            produced = Array.Empty<string>(),
            warnings = Array.Empty<string>(),
            errors = Array.Empty<string>(),
            scope,
            cleared,
        }, JsonOpts);
    }

    [McpServerTool(Name = "compute_hash", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Computes the FNV1a64 hash used by REDengine for each string provided " +
                 "(typically game file paths).")]
    public static async Task<string> ComputeHash(
        Cp77ToolsRunner runner,
        [Description("One or more strings to hash.")] string[] inputs,
        CancellationToken ct)
    {
        if (inputs is null || inputs.Length == 0)
            return Err("Provide at least one string to hash.");

        var args = new List<string> { "hash" };
        args.AddRange(inputs);
        var r = await runner.RunAsync(args, ct);
        return Structured($"FNV1a64 hash of {inputs.Length} entry(ies)", r);
    }

    [McpServerTool(Name = "resolve_hash", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Reverse lookup: finds the game file path corresponding " +
                 "to an FNV1a64 hash. The inverse of compute_hash.")]
    public static async Task<string> ResolveHash(
        Cp77ToolsRunner runner,
        [Description("One or more FNV1a64 hashes (unsigned integers).")] string[] hashes,
        CancellationToken ct)
    {
        if (hashes is null || hashes.Length == 0)
            return Err("Provide at least one hash.");

        var args = new List<string> { "resolve-hash" };
        args.AddRange(hashes);
        var r = await runner.RunAsync(args, ct);
        return Structured($"Reverse lookup of {hashes.Length} hash", r);
    }

    [McpServerTool(Name = "tweakdb_resolve", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Reverse lookup of TweakDB identifiers: a hash → the name of " +
                 "the identifier. Uses the TweakDB name database loaded at startup.")]
    public static async Task<string> TweakDbResolve(
        Cp77ToolsRunner runner,
        [Description("One or more TweakDB identifier hashes (unsigned integers).")] string[] hashes,
        CancellationToken ct)
    {
        if (hashes is null || hashes.Length == 0)
            return Err("Provide at least one hash.");

        var args = new List<string> { "tweakdb-resolve" };
        args.AddRange(hashes);
        var r = await runner.RunAsync(args, ct);
        return Structured($"Resolution of {hashes.Length} TweakDB identifier(s)", r);
    }

    [McpServerTool(Name = "tweakdb_query", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Queries the Cyberpunk 2077 TweakDB: loads a tweakdb.bin file and " +
                 "lists records and flats whose identifier contains the filter — to " +
                 "discover the game's tuning identifiers. Results are capped " +
                 "at 100 records + 100 flats; refine the filter if the truncated field says so.")]
    public static async Task<string> TweakDbQuery(
        Cp77ToolsRunner runner,
        [Description("Path to a tweakdb.bin file (extracted from the game archives).")] string tweakdbPath,
        [Description("Substring to search for in the identifiers (empty = all, 100 max).")] string filter = "",
        CancellationToken ct = default)
    {
        if (!File.Exists(tweakdbPath))
            return Err($"TweakDB file not found: {tweakdbPath}");

        var r = await runner.RunAsync(new[] { "tweakdb-query", tweakdbPath, filter }, ct);
        var log = (r.Stdout + r.Stderr).Trim();
        var (recordsTruncated, flatsTruncated) = ParseTweakHeader(log);
        var truncated = recordsTruncated || flatsTruncated;
        var status = r.ExitCode != 0
                     || log.Contains("Daemon error", StringComparison.Ordinal)
                     || log.Contains("Unhandled", StringComparison.OrdinalIgnoreCase)
                ? "error" : "success";

        return JsonSerializer.Serialize(new
        {
            ok = status == "success",
            status,
            summary = $"TweakDB — search \"{filter}\" in {tweakdbPath}" +
                      (truncated ? " (results truncated — refine the filter)" : ""),
            produced = Array.Empty<string>(),
            warnings = LogLines(log, "Warning"),
            errors = LogLines(log, "Error"),
            truncated = new { records = recordsTruncated, flats = flatsTruncated },
            exitCode = r.ExitCode,
            log = Truncate(log, 12_000),
        }, JsonOpts);
    }

    // ── Read / inspection ──────────────────────────────────────────────

    [McpServerTool(Name = "archive_info", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Displays information about a Cyberpunk 2077 .archive file: " +
                 "file count, and optional filtered listing of its contents. " +
                 "Listing served by an LRU cache (key = path + mtime): successive calls " +
                 "are near-instant.")]
    public static async Task<string> ArchiveInfo(
        Cp77ToolsRunner runner,
        [Description("Absolute path of the .archive file.")] string archivePath,
        [Description("List the archive contents (otherwise summary only).")] bool list = false,
        [Description("Optional glob filter on names, e.g. *.mesh")] string? pattern = null,
        [Description("Max number of files returned in list mode (default 500). " +
                     "filteredCount always gives the real total.")] int maxFiles = 500,
        CancellationToken ct = default)
    {
        if (!File.Exists(archivePath))
            return Err($"Archive not found: {archivePath}");

        if (!list)
        {
            var args = new List<string> { "archive", archivePath };
            if (!string.IsNullOrWhiteSpace(pattern)) { args.Add("--pattern"); args.Add(pattern); }
            var r = await runner.RunAsync(args, ct);
            return Structured($"Archive : {archivePath}", r);
        }

        // List mode: goes through the runner mtime cache.
        var (entries, fromCache, raw) = await runner.GetArchiveListingAsync(archivePath, ct);
        var filtered = string.IsNullOrWhiteSpace(pattern)
            ? entries.ToList()
            : entries.Where(e => MatchesGlob(e, pattern)).ToList();

        // Bounded response: base archives exceed 10,000 entries.
        var cap = Math.Max(1, maxFiles);
        var truncated = filtered.Count > cap;
        var shown = truncated ? filtered.Take(cap).ToList() : filtered;

        var log = $"{entries.Count} file(s) in {Path.GetFileName(archivePath)}" +
                  (fromCache ? " (from cache)" : "") +
                  (filtered.Count != entries.Count ? $" — {filtered.Count} after filter" : "") +
                  "\n" + string.Join("\n", shown);

        return JsonSerializer.Serialize(new
        {
            ok = entries.Count > 0,
            status = entries.Count > 0 ? "success" : "error",
            summary = $"Archive: {archivePath} — {entries.Count} file(s)" +
                      (filtered.Count != entries.Count ? $", {filtered.Count} after filter" : "") +
                      (truncated ? $", {cap} returned" : "") +
                      (fromCache ? " (cache)" : ""),
            produced = Array.Empty<string>(),
            warnings = truncated
                ? new[] { $"{filtered.Count} files after filter, only the first {cap} " +
                          "are returned — use pattern or increase maxFiles." }
                : Array.Empty<string>(),
            errors = entries.Count > 0 ? Array.Empty<string>()
                                       : new[] { $"Empty listing for {archivePath}." },
            archivePath,
            fileCount = entries.Count,
            filteredCount = filtered.Count,
            truncated,
            fromCache,
            files = shown,
            exitCode = raw.ExitCode,
            log = Truncate(log, 12_000),
        }, JsonOpts);
    }

    [McpServerTool(Name = "archive_stats", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Gives the breakdown of a .archive's contents by file " +
                 "extension (how many .mesh, .ent, .xbm, .app...). Quick overview of what " +
                 "an archive contains without listing it entirely. Listing served by the " +
                 "LRU cache: successive calls are near-instant.")]
    public static async Task<string> ArchiveStats(
        Cp77ToolsRunner runner,
        [Description("Absolute path of the .archive file.")] string archivePath,
        [Description("Max number of extension categories returned (default 100). " +
                     "categoryCount always gives the real total.")] int maxCategories = 100,
        CancellationToken ct = default)
    {
        if (!File.Exists(archivePath))
            return Err($"Archive not found: {archivePath}");

        // Goes through the runner mtime cache (same source as archive_info).
        var (entries, fromCache, raw) = await runner.GetArchiveListingAsync(archivePath, ct);
        var histogram = HistogramByExtension(entries);
        var cap = Math.Max(1, maxCategories);
        var truncated = histogram.Count > cap;
        var shown = truncated ? histogram.Take(cap).ToList() : histogram;

        var log = $"{entries.Count} file(s) in {Path.GetFileName(archivePath)}, " +
                  $"{histogram.Count} extension type(s)" + (fromCache ? " (from cache)" : "") +
                  "\n" + string.Join("\n", shown.Select(h => $"{h.Extension,-16} {h.Count}"));

        return JsonSerializer.Serialize(new
        {
            ok = entries.Count > 0,
            status = entries.Count > 0 ? "success" : "error",
            summary = $"Archive: {archivePath} — {entries.Count} file(s), " +
                      $"{histogram.Count} extension type(s)" + (fromCache ? " (cache)" : ""),
            produced = Array.Empty<string>(),
            warnings = truncated
                ? new[] { $"{histogram.Count} extension types, only the first {cap} " +
                          "are returned — increase maxCategories." }
                : Array.Empty<string>(),
            errors = entries.Count > 0 ? Array.Empty<string>()
                                       : new[] { $"Empty listing for {archivePath}." },
            archivePath,
            fileCount = entries.Count,
            categoryCount = histogram.Count,
            truncated,
            fromCache,
            byExtension = shown.Select(h => new { extension = h.Extension, count = h.Count }),
            exitCode = raw.ExitCode,
            log = Truncate(log, 12_000),
        }, JsonOpts);
    }

    /// <summary>Breakdown of a list of paths by extension (lowercase), sorted by
    /// descending count then extension. Paths without an extension are grouped under
    /// "(no extension)". Pure logic, tested in isolation.</summary>
    internal static IReadOnlyList<ExtensionCount> HistogramByExtension(IEnumerable<string> entries)
        => entries
            .GroupBy(e =>
            {
                var ext = Path.GetExtension(e);
                return string.IsNullOrEmpty(ext) ? "(no extension)" : ext.ToLowerInvariant();
            })
            .Select(g => new ExtensionCount(g.Key, g.Count()))
            .OrderByDescending(x => x.Count).ThenBy(x => x.Extension, StringComparer.Ordinal)
            .ToList();

    internal readonly record struct ExtensionCount(string Extension, int Count);

    [McpServerTool(Name = "find_in_archives", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Searches for files across all .archive files in a folder " +
                 "(typically the game content folder). Indicates which archive each " +
                 "matching file is found in. Listings served by an LRU cache: " +
                 "subsequent calls on the same folder are near-instant.")]
    public static async Task<string> FindInArchives(
        Cp77ToolsRunner runner,
        [Description("Folder containing .archive files, e.g. <game>/archive/pc/content.")] string archivesFolder,
        [Description("Glob pattern to search for, e.g. *player*.ent")] string? pattern = null,
        [Description("Regular expression to search for (alternative to glob).")] string? regex = null,
        [Description("Max number of matches returned (default 500). matchCount " +
                     "always gives the real total; refine the pattern if truncated=true.")] int maxMatches = 500,
        CancellationToken ct = default)
    {
        if (!Directory.Exists(archivesFolder))
            return Err($"Folder not found: {archivesFolder}");
        if (string.IsNullOrWhiteSpace(pattern) && string.IsNullOrWhiteSpace(regex))
            return Err("Provide a glob pattern (pattern) or a regular expression (regex).");

        Regex? rx = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(regex))
                rx = new Regex(regex, RegexOptions.IgnoreCase);
        }
        catch (ArgumentException ex)
        {
            return Err($"Invalid regular expression: {ex.Message}");
        }

        var archives = Directory.GetFiles(archivesFolder, "*.archive");
        var matches = new List<string>();
        var cacheHits = 0;
        var errors = new List<string>();

        try
        {
            foreach (var archive in archives)
            {
                ct.ThrowIfCancellationRequested();
                var (entries, fromCache, raw) = await runner.GetArchiveListingAsync(archive, ct);
                if (fromCache) cacheHits++;
                if (entries.Count == 0 && raw.ExitCode != 0)
                {
                    errors.Add($"{Path.GetFileName(archive)}: empty listing (exit={raw.ExitCode})");
                    continue;
                }
                foreach (var e in entries)
                {
                    var match = rx is not null ? rx.IsMatch(e) : MatchesGlob(e, pattern!);
                    if (match)
                        matches.Add($"{e}  ({Path.GetFileName(archive)})");
                }
            }
        }
        catch (OperationCanceledException) { return Cancelled("find_in_archives"); }

        // Bounded response: a broad pattern over base content can produce
        // tens of thousands of entries — enough to saturate the agent context.
        var cap = Math.Max(1, maxMatches);
        var truncated = matches.Count > cap;
        var shown = truncated ? matches.Take(cap).ToList() : matches;
        var warnings = truncated
            ? new[] { $"{matches.Count} matches, only the first {cap} are " +
                      "returned — refine the pattern or increase maxMatches." }
            : Array.Empty<string>();

        var log = $"{matches.Count} match(es) in {archives.Length} archive(s) " +
                  $"(cache: {cacheHits}/{archives.Length})\n" +
                  string.Join("\n", shown);

        return JsonSerializer.Serialize(new
        {
            ok = true,
            status = "success",
            summary = $"Search in {archivesFolder}: {matches.Count} match(es) " +
                      $"across {archives.Length} archive(s) (cache: {cacheHits})" +
                      (truncated ? $" — {cap} returned" : ""),
            produced = Array.Empty<string>(),
            warnings,
            errors,
            archivesScanned = archives.Length,
            cacheHits,
            matchCount = matches.Count,
            truncated,
            matches = shown,
            exitCode = 0,
            log = Truncate(log, 12_000),
        }, JsonOpts);
    }

    [McpServerTool(Name = "diff_archives", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Compares two .archive files and lists the files added (present in B " +
                 "only) and removed (present in A only) — useful for diagnosing " +
                 "exactly what a mod changes relative to the base game. The " +
                 "`archive --diff` verb of cp77tools only dumps a manifest; here we compute " +
                 "a real diff by cross-referencing the two listings.")]
    public static async Task<string> DiffArchives(
        Cp77ToolsRunner runner,
        [Description("First archive (reference, e.g. base game version).")] string archiveA,
        [Description("Second archive (to compare, e.g. modded version).")] string archiveB,
        CancellationToken ct = default)
    {
        if (!File.Exists(archiveA))
            return Err($"Archive A not found: {archiveA}");
        if (!File.Exists(archiveB))
            return Err($"Archive B not found: {archiveB}");

        var (entriesA, cacheA, _) = await runner.GetArchiveListingAsync(archiveA, ct);
        var (entriesB, cacheB, _) = await runner.GetArchiveListingAsync(archiveB, ct);

        var setA = new HashSet<string>(entriesA, StringComparer.OrdinalIgnoreCase);
        var setB = new HashSet<string>(entriesB, StringComparer.OrdinalIgnoreCase);
        var added = setB.Except(setA, StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
        var removed = setA.Except(setB, StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
        var common = setA.Intersect(setB, StringComparer.OrdinalIgnoreCase).Count();

        var ok = setA.Count > 0 || setB.Count > 0;
        var log = $"A: {Path.GetFileName(archiveA)} ({setA.Count} file(s){(cacheA ? ", cache" : "")})\n" +
                  $"B: {Path.GetFileName(archiveB)} ({setB.Count} file(s){(cacheB ? ", cache" : "")})\n" +
                  $"Common: {common} · Added in B: {added.Count} · Removed in B: {removed.Count}";

        // Bounded response: a diff between two game versions can list thousands of entries.
        const int DiffCap = 500;
        var truncated = added.Count > DiffCap || removed.Count > DiffCap;

        return JsonSerializer.Serialize(new
        {
            ok,
            status = ok ? "success" : "error",
            summary = $"Diff archives: {Path.GetFileName(archiveA)} ↔ " +
                      $"{Path.GetFileName(archiveB)} (+{added.Count} / -{removed.Count})",
            produced = Array.Empty<string>(),
            warnings = truncated
                ? new[] { $"Added/removed lists capped at {DiffCap} entries each " +
                          "(addedCount/removedCount give the real totals)." }
                : Array.Empty<string>(),
            errors = ok ? Array.Empty<string>() : new[] { "Empty listings — check the archives." },
            archiveA = new { path = archiveA, count = setA.Count },
            archiveB = new { path = archiveB, count = setB.Count },
            commonCount = common,
            addedCount = added.Count,
            removedCount = removed.Count,
            truncated,
            added = added.Count > DiffCap ? added.Take(DiffCap).ToList() : added,
            removed = removed.Count > DiffCap ? removed.Take(DiffCap).ToList() : removed,
            log = Truncate(log, 12_000),
        }, JsonOpts);
    }

    [McpServerTool(Name = "extract_files", ReadOnly = false, Destructive = false, Idempotent = true)]
    [Description("Extracts files from a .archive file to a folder. " +
                 "Optional filtering by glob pattern or regular expression.")]
    public static async Task<string> ExtractFiles(
        Cp77ToolsRunner runner,
        [Description("Absolute path of the .archive file.")] string archivePath,
        [Description("Destination folder for the extracted files.")] string outputPath,
        [Description("Optional glob filter, e.g. *.mesh")] string? pattern = null,
        [Description("Optional regex filter (alternative to glob).")] string? regex = null,
        [Description("If true, returns the full log (no truncation) — for debugging.")] bool verbose = false,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken ct = default)
    {
        if (!File.Exists(archivePath))
            return Err($"Archive not found: {archivePath}");

        var args = new List<string> { "unbundle", archivePath, "--outpath", outputPath };
        if (!string.IsNullOrWhiteSpace(pattern)) { args.Add("--pattern"); args.Add(pattern); }
        if (!string.IsNullOrWhiteSpace(regex)) { args.Add("--regex"); args.Add(regex); }

        return await WithSnapshot(outputPath,
            $"Extraction of {archivePath} → {outputPath}",
            () => runner.RunAsync(args, ct, progress is null ? null : Relay(progress)), verbose);
    }

    [McpServerTool(Name = "uncook", ReadOnly = false, Destructive = false, Idempotent = true)]
    [Description("Extracts and converts in a single pass the files of an archive to " +
                 "usable formats (mesh → glTF, textures → image). Combines extraction and " +
                 "conversion, unlike extract_files which only extracts.")]
    public static async Task<string> Uncook(
        Cp77ToolsRunner runner,
        [Description("Path of a .archive file (or a folder of archives).")] string archivePath,
        [Description("Destination folder for the converted files.")] string outputPath,
        [Description("Optional glob filter, e.g. *.mesh")] string? pattern = null,
        [Description("Image format for textures: png, dds, tga, bmp or jpg.")] string? textureFormat = null,
        [Description("Mesh export type: MeshOnly, WithRig, Multimesh (default: WithMaterials).")] string? meshExportType = null,
        [Description("Mesh exporter: Default, Experimental, REDmod.")] string? meshExporterType = null,
        [Description("Filters the mesh export LODs (reduces noise).")] bool meshExportLodFilter = false,
        [Description("If true, returns the full log (no truncation) — for debugging.")] bool verbose = false,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken ct = default)
    {
        if (!File.Exists(archivePath) && !Directory.Exists(archivePath))
            return Err($"Path not found: {archivePath}");

        var args = new List<string> { "uncook", archivePath, "--outpath", outputPath };
        if (!string.IsNullOrWhiteSpace(pattern)) { args.Add("--pattern"); args.Add(pattern); }
        if (!string.IsNullOrWhiteSpace(textureFormat)) { args.Add("--uext"); args.Add(textureFormat); }
        if (!string.IsNullOrWhiteSpace(meshExportType)) { args.Add("--mesh-export-type"); args.Add(meshExportType); }
        if (!string.IsNullOrWhiteSpace(meshExporterType)) { args.Add("--mesh-exporter-type"); args.Add(meshExporterType); }
        if (meshExportLodFilter) args.Add("--mesh-export-lod-filter");

        return await WithSnapshot(outputPath,
            $"Extraction + conversion: {archivePath} → {outputPath}",
            () => runner.RunAsync(args, ct, progress is null ? null : Relay(progress)), verbose);
    }

    // ── Conversion ────────────────────────────────────────────────────────

    [McpServerTool(Name = "cr2w_to_json", ReadOnly = false, Destructive = false, Idempotent = true)]
    [Description("Converts already-extracted REDengine files (CR2W: .mesh, .ent, .app...) " +
                 "to readable, editable JSON.")]
    public static async Task<string> Cr2wToJson(
        Cp77ToolsRunner runner,
        [Description("Path of a CR2W file, or a folder containing some.")] string path,
        [Description("Destination folder for the JSON files.")] string outputPath,
        CancellationToken ct = default)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
            return Err($"Path not found: {path}");

        return await WithSnapshot(outputPath,
            $"Serialization CR2W → JSON: {path} → {outputPath}",
            () => runner.RunAsync(
                new[] { "convert", "serialize", path, "--outpath", outputPath }, ct));
    }

    [McpServerTool(Name = "json_to_cr2w", ReadOnly = false, Destructive = false, Idempotent = true)]
    [Description("Converts JSON files (produced by cr2w_to_json) back into " +
                 "binary REDengine CR2W files.")]
    public static async Task<string> JsonToCr2w(
        Cp77ToolsRunner runner,
        [Description("Path of a JSON file, or a folder containing some.")] string path,
        [Description("Destination folder for the CR2W files.")] string outputPath,
        CancellationToken ct = default)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
            return Err($"Path not found: {path}");

        return await WithSnapshot(outputPath,
            $"Deserialization JSON → CR2W: {path} → {outputPath}",
            () => runner.RunAsync(
                new[] { "convert", "deserialize", path, "--outpath", outputPath }, ct));
    }

    [McpServerTool(Name = "export_files", ReadOnly = false, Destructive = false, Idempotent = true)]
    [Description("Exports already-extracted REDengine files to raw formats " +
                 "(mesh → glTF, texture → image, etc.).")]
    public static async Task<string> ExportFiles(
        Cp77ToolsRunner runner,
        [Description("Path of a REDengine file, or a folder containing some.")] string path,
        [Description("Destination folder for the raw files.")] string outputPath,
        [Description("Image format for textures: png, dds, tga, bmp or jpg.")] string? textureFormat = null,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken ct = default)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
            return Err($"Path not found: {path}");

        var args = new List<string> { "export", path, "--outpath", outputPath };
        if (!string.IsNullOrWhiteSpace(textureFormat)) { args.Add("--uext"); args.Add(textureFormat); }

        return await WithSnapshot(outputPath,
            $"Export REDengine → raw: {path} → {outputPath}",
            () => runner.RunAsync(args, ct, progress is null ? null : Relay(progress)));
    }

    [McpServerTool(Name = "export_animation", ReadOnly = false, Destructive = false, Idempotent = true)]
    [Description("Exports an extracted REDengine animation (.anims) to binary glTF (.glb), " +
                 "usable in Blender. ⚠ WolvenKit exports animations from their " +
                 "rig/skeleton: a .anims provided ALONE (without its associated .rig) may produce " +
                 "nothing, or even fail. For a reliable export, also extract the corresponding rig.")]
    public static async Task<string> ExportAnimation(
        Cp77ToolsRunner runner,
        [Description("Path of a .anims file (or a folder containing some).")] string path,
        [Description("Destination folder for the produced .glb files.")] string outputPath,
        [Description("If true, returns the full log (no truncation) — for debugging.")] bool verbose = false,
        CancellationToken ct = default)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
            return Err($"Path not found: {path}");

        return await WithSnapshot(outputPath,
            $"Export animation (.anims) → glTF: {path} → {outputPath}",
            () => runner.RunAsync(new[] { "export", path, "--outpath", outputPath }, ct), verbose);
    }

    [McpServerTool(Name = "export_morphtarget", ReadOnly = false, Destructive = false, Idempotent = true)]
    [Description("Exports a REDengine morphtarget (.morphtarget — face shapes / blendshapes) " +
                 "extracted to binary glTF (.glb). A dedicated, explicit tool on top of the generic " +
                 "export (format determined by the .morphtarget extension).")]
    public static async Task<string> ExportMorphTarget(
        Cp77ToolsRunner runner,
        [Description("Path of a .morphtarget file (or a folder containing some).")] string path,
        [Description("Destination folder for the produced .glb files.")] string outputPath,
        [Description("If true, returns the full log (no truncation) — for debugging.")] bool verbose = false,
        CancellationToken ct = default)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
            return Err($"Path not found: {path}");

        return await WithSnapshot(outputPath,
            $"Export morphtarget → glTF: {path} → {outputPath}",
            () => runner.RunAsync(new[] { "export", path, "--outpath", outputPath }, ct), verbose);
    }

    [McpServerTool(Name = "export_mlmask", ReadOnly = false, Destructive = false, Idempotent = true)]
    [Description("Exports an extracted REDengine multilayer mask (.mlmask) to images " +
                 "(one per layer). The image format is configurable via textureFormat " +
                 "(png by default, or dds/tga/bmp/jpg/tiff).")]
    public static async Task<string> ExportMlmask(
        Cp77ToolsRunner runner,
        [Description("Path to a .mlmask file (or a folder containing some).")] string path,
        [Description("Destination folder for the produced images.")] string outputPath,
        [Description("Image format: png (default), dds, tga, bmp, jpg or tiff.")] string? textureFormat = null,
        [Description("If true, returns the full log (no truncation) — for debugging.")] bool verbose = false,
        CancellationToken ct = default)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
            return Err($"Path not found: {path}");

        var args = new List<string> { "export", path, "--outpath", outputPath };
        if (!string.IsNullOrWhiteSpace(textureFormat)) { args.Add("--uext"); args.Add(textureFormat); }

        return await WithSnapshot(outputPath,
            $"Export mlmask → images: {path} → {outputPath}",
            () => runner.RunAsync(args, ct), verbose);
    }

    [McpServerTool(Name = "export_entity", ReadOnly = false, Destructive = false, Idempotent = true)]
    [Description("Exports a REDengine entity (.ent) appearance to glTF (.glb) via " +
                 "IModTools.ExportEntity. First discovers the entity's appearances: if " +
                 "`appearance` is empty, takes the first; if invalid, returns the available " +
                 "list. ⚠ EXPERIMENTAL: WolvenKit refuses headless export of certain entity types " +
                 "(\"can not be exported\") — use list_entity_appearances to " +
                 "inspect, and uncook on the referenced .mesh to view them reliably.")]
    public static async Task<string> ExportEntity(
        Cp77ToolsRunner runner,
        [Description("Path to an extracted .ent (entity) file.")] string entFile,
        [Description("Output .glb file.")] string outputPath,
        [Description("Appearance name (the entity's `name`). Empty = the first.")] string? appearance = null,
        [Description("Game root (loads archives to resolve meshes/materials).")] string? gamePath = null,
        CancellationToken ct = default)
    {
        if (!File.Exists(entFile))
            return Err($"File .ent not found: {entFile}");

        // Discover the appearances to validate/choose and give clear errors.
        var tmp = Path.Combine(Path.GetTempPath(), "wolvenkit-mcp-entappr", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        List<string> available;
        try
        {
            await runner.RunAsync(new[] { "convert", "serialize", entFile, "--outpath", tmp }, ct);
            var json = Directory.EnumerateFiles(tmp, "*.json", SearchOption.AllDirectories).FirstOrDefault();
            available = json is null ? new List<string>()
                : ModdingTools.ParseEntityAppearances(await File.ReadAllTextAsync(json, ct))
                    .Select(a => a.Name).ToList();
        }
        catch { available = new List<string>(); }
        finally { try { Directory.Delete(tmp, true); } catch { /* best-effort */ } }

        if (available.Count == 0)
            return Err($"Entity {Path.GetFileName(entFile)} exposes no appearance " +
                       "(component/proxy entity?) — nothing to export. See list_entity_appearances.");

        var chosen = appearance;
        if (string.IsNullOrWhiteSpace(chosen)) chosen = available[0];
        else if (!available.Contains(chosen, StringComparer.OrdinalIgnoreCase))
            return Err($"Appearance '{chosen}' not found in {Path.GetFileName(entFile)}. " +
                       $"Available: {string.Join(", ", available.Take(20))}.");

        var args = new List<string> { "export-entity", entFile, "--out", outputPath, "--appearance", chosen };
        if (!string.IsNullOrWhiteSpace(gamePath)) { args.Add("--game"); args.Add(gamePath); }
        var r = await runner.RunAsync(args, ct);
        var produced = File.Exists(outputPath) ? new List<string> { outputPath } : new List<string>();

        var log = (r.Stdout + r.Stderr).Trim();
        var notExportable = log.Contains("can not be exported", StringComparison.OrdinalIgnoreCase);
        var ok = produced.Count > 0;
        return JsonSerializer.Serialize(new
        {
            ok,
            status = ok ? "success" : "error",
            summary = ok
                ? $"Entity exported [{chosen}] → {outputPath}"
                : notExportable
                    ? $"WolvenKit refuses headless export of this entity [{chosen}] (\"can not be exported\")."
                    : $"Entity export failed [{chosen}].",
            entFile,
            chosenAppearance = chosen,
            availableAppearances = available,
            produced,
            warnings = notExportable
                ? new[] { "Entity type not exportable headless — see list_entity_appearances + uncook the .mesh." }
                : Array.Empty<string>(),
            errors = ok ? new List<string>() : LogLines(log, "Error"),
            log = Truncate(log, 8_000),
        }, JsonOpts);
    }

    [McpServerTool(Name = "export_materials", ReadOnly = false, Destructive = false, Idempotent = true)]
    [Description("Exports the materials of a REDengine mesh (.mesh) to JSON + textures " +
                 "(via IModTools.ExportMaterials). gamePath loads the archives to resolve the " +
                 "base material dependencies.")]
    public static async Task<string> ExportMaterials(
        Cp77ToolsRunner runner,
        [Description("Path to an extracted .mesh file.")] string meshFile,
        [Description("Output file (materials JSON).")] string outputPath,
        [Description("Game root (loads archives to resolve the base materials).")] string? gamePath = null,
        CancellationToken ct = default)
    {
        if (!File.Exists(meshFile))
            return Err($"File .mesh not found: {meshFile}");

        var args = new List<string> { "export-materials", meshFile, "--out", outputPath };
        if (!string.IsNullOrWhiteSpace(gamePath)) { args.Add("--game"); args.Add(gamePath); }

        // ExportMaterials writes several files (JSON + textures) into the output
        // folder, not just outputPath: we capture everything that is produced.
        var dir = Path.GetDirectoryName(Path.GetFullPath(outputPath))!;
        Directory.CreateDirectory(dir);
        var before = Snapshot(dir);
        var r = await runner.RunAsync(args, ct);
        return Structured($"Export materials: {meshFile} → {outputPath}", r, ProducedIn(dir, before));
    }

    // ── Direct read / write of a game file ────────────────────

    [McpServerTool(Name = "read_game_file", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Reads a game file in a single call: extracts the file from the archive, " +
                 "converts it to REDengine JSON and returns its content — instead of chaining " +
                 "extract_files then cr2w_to_json. The full JSON is also written to disk " +
                 "(jsonFile field), to read separately if the returned content is truncated.")]
    public static async Task<string> ReadGameFile(
        Cp77ToolsRunner runner,
        [Description("Path to the .archive file containing the wanted file.")] string archivePath,
        [Description("Internal path of the file within the archive (e.g. base\\gameplay\\...\\x.ent). " +
                     "Locate it if needed with find_in_archives.")] string gameFilePath,
        CancellationToken ct = default)
    {
        if (!File.Exists(archivePath))
            return Err($"Archive not found: {archivePath}");

        // DETERMINISTIC work folder per (archive, file): re-reading the same
        // file rewrites it to the same place instead of accumulating a GUID folder per
        // call (leak). jsonFile remains readable after the call as documented.
        var work = Path.Combine(Path.GetTempPath(), "wolvenkit-mcp-read",
                                StableHash(archivePath + "|" + gameFilePath));
        // Wipe any prior extraction first: the folder is reused per (archive,file),
        // so a stale *.json from an earlier call (e.g. the archive changed, or this
        // extraction yields nothing) must not be picked up by FirstOrDefault below.
        try { if (Directory.Exists(work)) Directory.Delete(work, recursive: true); }
        catch { /* best-effort; recreation below still proceeds */ }
        var rawDir = Path.Combine(work, "raw");
        var jsonDir = Path.Combine(work, "json");
        Directory.CreateDirectory(rawDir);
        Directory.CreateDirectory(jsonDir);

        var ext = await runner.RunAsync(
            new[] { "unbundle", archivePath, "--outpath", rawDir, "--pattern", gameFilePath }, ct);
        var extracted = Directory
            .EnumerateFiles(rawDir, "*", SearchOption.AllDirectories).FirstOrDefault();
        if (extracted is null)
            return Err($"File not found in archive: {gameFilePath} " +
                       "(check the internal path with find_in_archives).");

        var conv = await runner.RunAsync(
            new[] { "convert", "serialize", extracted, "--outpath", jsonDir }, ct);
        var jsonFile = Directory
            .EnumerateFiles(jsonDir, "*.json", SearchOption.AllDirectories).FirstOrDefault();
        var log = (ext.Stdout + "\n" + conv.Stdout).Trim();

        if (jsonFile is null)
            return JsonSerializer.Serialize(new
            {
                ok = true,
                status = "success",
                summary = $"File extracted (non-CR2W type: not converted to JSON): {gameFilePath}",
                gameFilePath,
                rawFile = extracted,
                jsonFile = (string?)null,
                truncated = false,
                content = (string?)null,
                warnings = LogLines(log, "Warning"),
                errors = LogLines(log, "Error"),
            }, JsonOpts);

        var content = await File.ReadAllTextAsync(jsonFile, ct);
        var truncated = content.Length > ReadContentCap;
        return JsonSerializer.Serialize(new
        {
            ok = true,
            status = "success",
            summary = $"Game file read: {gameFilePath}"
                      + (truncated ? " (content truncated — read jsonFile in full)" : ""),
            gameFilePath,
            jsonFile,
            truncated,
            content = truncated ? content[..ReadContentCap] : content,
            warnings = LogLines(log, "Warning"),
            errors = LogLines(log, "Error"),
        }, JsonOpts);
    }

    [McpServerTool(Name = "write_game_file", ReadOnly = false, Destructive = true, Idempotent = true)]
    [Description("Writes an edited game file: converts a JSON (produced by read_game_file " +
                 "then modified) into a binary REDengine CR2W file, placed in a mod folder " +
                 "at the right internal path — ready to be packed by pack_archive.")]
    public static async Task<string> WriteGameFile(
        Cp77ToolsRunner runner,
        [Description("Path to the edited JSON file (from read_game_file).")] string jsonFile,
        [Description("Target internal path within the game (e.g. base\\...\\x.ent) — determines " +
                     "the location of the produced CR2W.")] string gameFilePath,
        [Description("Folder where to place the CR2W (typically the source/archive of a mod project).")] string modArchiveFolder,
        CancellationToken ct = default)
    {
        if (!File.Exists(jsonFile))
            return Err($"JSON file not found: {jsonFile}");

        // Containment guard: gameFilePath is agent-controlled and documented as an
        // "internal path". A rooted or ..\-laden value would otherwise let
        // Path.Combine write outside the mod folder (arbitrary overwrite).
        if (!PathSafety.TryResolveInside(modArchiveFolder, gameFilePath, out var dest))
            return Err($"Refused: gameFilePath '{gameFilePath}' escapes the mod folder " +
                       $"'{modArchiveFolder}' (must be a relative path inside it).");

        var tmp = Path.Combine(Path.GetTempPath(), "wolvenkit-mcp-write",
                               Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        var conv = await runner.RunAsync(
            new[] { "convert", "deserialize", jsonFile, "--outpath", tmp }, ct);
        var cr2w = Directory
            .EnumerateFiles(tmp, "*", SearchOption.AllDirectories)
            .FirstOrDefault(f => !f.EndsWith(".json", StringComparison.OrdinalIgnoreCase));

        if (cr2w is null)
        {
            try { Directory.Delete(tmp, recursive: true); } catch { /* best-effort cleanup */ }
            return Structured($"JSON → CR2W conversion failed: {jsonFile}", conv,
                new List<string>());
        }

        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.Copy(cr2w, dest, overwrite: true);
        try { Directory.Delete(tmp, recursive: true); } catch { /* best-effort cleanup */ }

        return Structured($"Game file written: {gameFilePath} → {dest}", conv,
            new List<string> { dest });
    }

    // ── Low-level audio / compression ─────────────────────────────────────

    [McpServerTool(Name = "wwise_export", ReadOnly = false, Destructive = false, Idempotent = true)]
    [Description("Converts Wwise WEM audio files to OGG. Requires the native audio " +
                 "binaries — present on Windows, unavailable on macOS. Conversions " +
                 "run in parallel (up to 4 simultaneously); the real gain appears " +
                 "when the daemon supports pipelining (otherwise I/O overlap only).")]
    public static async Task<string> WwiseExport(
        Cp77ToolsRunner runner,
        [Description("Path to a .wem file, or a folder containing some.")] string path,
        [Description("Destination folder for the OGG files.")] string outputPath,
        [Description("If true, returns the full log (no truncation) — for debugging.")] bool verbose = false,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken ct = default)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
            return Err($"Path not found: {path}");

        Directory.CreateDirectory(outputPath);

        // The `wwise` verb expects an output FILE .ogg (not a folder);
        // so it converts each .wem into an explicitly named .ogg in outputPath.
        var wems = Directory.Exists(path)
            ? Directory.GetFiles(path, "*.wem", SearchOption.AllDirectories)
            : new[] { path };
        if (wems.Length == 0)
            return Err($"No .wem file found in: {path}");

        var before = Snapshot(outputPath);
        var logs = new ConcurrentBag<string>();
        var errorCodes = new ConcurrentBag<int>();
        var done = 0;

        try
        {
            await Parallel.ForEachAsync(wems,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = Math.Min(4, Environment.ProcessorCount),
                    CancellationToken = ct,
                },
                async (wem, token) =>
                {
                    var ogg = Path.Combine(outputPath,
                        Path.GetFileNameWithoutExtension(wem) + ".ogg");
                    var r = await runner.RunAsync(new[] { "wwise", wem, ogg, "--wem" }, token);
                    logs.Add(
                        $"{Path.GetFileName(wem)} → {Path.GetFileName(ogg)}\n" +
                        (r.Stdout + r.Stderr).Trim());
                    if (r.ExitCode != 0) errorCodes.Add(r.ExitCode);
                    var n = Interlocked.Increment(ref done);
                    progress?.Report(new ProgressNotificationValue
                    {
                        Progress = n,
                        Total = wems.Length,
                        Message = Path.GetFileName(wem),
                    });
                });
        }
        catch (OperationCanceledException) { return Cancelled("wwise_export"); }

        var aggregate = new CliResult(
            // Deterministic aggregate exit code (ConcurrentBag has no defined order).
            errorCodes.IsEmpty ? 0 : errorCodes.Max(),
            string.Join("\n", logs), "", false);
        return Structured(
            $"Wwise WEM → OGG conversion: {wems.Length} file(s) → {outputPath} (parallel)",
            aggregate, ProducedIn(outputPath, before), verbose);
    }

    [McpServerTool(Name = "extract_audio", ReadOnly = false, Destructive = false, Idempotent = true)]
    [Description("Extracts the voice-over audio (opus) from a Cyberpunk 2077 voice archive " +
                 "(typically lang_xx_voice.archive). By default extracts ALL opus from " +
                 "the archive; opusHashes (comma-separated list of uint hashes) targets " +
                 "specific clips. Combines the opusinfo archive + its opuspak via the uncook pipeline.")]
    public static async Task<string> ExtractAudio(
        Cp77ToolsRunner runner,
        [Description("Path to the voice .archive (containing the opusinfo).")] string archivePath,
        [Description("Destination folder for the produced audio files.")] string outputPath,
        [Description("Optional: specific opus hashes to extract (comma-separated uint). " +
                     "Empty = extract everything.")] string? opusHashes = null,
        [Description("If true, returns the full log (no truncation) — for debugging.")] bool verbose = false,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken ct = default)
    {
        if (!File.Exists(archivePath) && !Directory.Exists(archivePath))
            return Err($"Path not found: {archivePath}");

        var args = new List<string> { "uncook", archivePath, "--outpath", outputPath };
        if (!string.IsNullOrWhiteSpace(opusHashes)) { args.Add("--opus-hashes"); args.Add(opusHashes); }
        else args.Add("--opus-export-all");

        return await WithSnapshot(outputPath,
            $"Opus audio extraction: {archivePath} → {outputPath}",
            () => runner.RunAsync(args, ct, progress is null ? null : Relay(progress)), verbose);
    }

    [McpServerTool(Name = "import_audio", ReadOnly = false, Destructive = false, Idempotent = true)]
    [Description("Imports WAV files (named by their opus hash, e.g. 123456.wav) into repacked " +
                 ".opus audio in a mod folder — voice-over replacement. The WAV files are " +
                 "encoded via opusenc and reinjected into the game's OpusPak. ⚠ EXPERIMENTAL: loads " +
                 "the game archives (a few seconds); requires the installation path.")]
    public static async Task<string> ImportAudio(
        Cp77ToolsRunner runner,
        [Description("Root folder of the Cyberpunk 2077 installation (or path to the .exe).")] string gamePath,
        [Description("Folder containing the .wav to import (file names = opus hashes).")] string wavFolder,
        [Description("Mod output folder (will receive the modified OpusPak).")] string outputPath,
        [Description("If true, returns the full log (no truncation) — for debugging.")] bool verbose = false,
        CancellationToken ct = default)
    {
        if (!Directory.Exists(wavFolder))
            return Err($"WAV folder not found: {wavFolder}");

        var args = new List<string>
            { "opus-import", gamePath, "--wav-dir", wavFolder, "--out", outputPath };
        return await WithSnapshot(outputPath,
            $"WAV → opus audio import: {wavFolder} → {outputPath}",
            () => runner.RunAsync(args, ct), verbose);
    }

    [McpServerTool(Name = "loc_resolve", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Resolves a localization key (LocKey: uint64 hash or text secondary key) into " +
                 "its localized text (masculine/feminine variants), via the game on-screens — " +
                 "without loading the whole TweakDB. ⚠ EXPERIMENTAL: loads the game archives " +
                 "(a few seconds); requires the installation path.")]
    public static async Task<string> LocResolve(
        Cp77ToolsRunner runner,
        [Description("Root folder of the Cyberpunk 2077 installation (or path to the .exe).")] string gamePath,
        [Description("Key to resolve: uint64 hash (e.g. 12345) or text secondary key.")] string key,
        [Description("Language (REDengine code: en_us, fr_fr, de_de, jp_jp...). Default: en_us.")] string? language = null,
        CancellationToken ct = default)
    {
        var args = new List<string> { "loc-resolve", gamePath, "--key", key };
        if (!string.IsNullOrWhiteSpace(language)) { args.Add("--lang"); args.Add(language); }
        var r = await runner.RunAsync(args, ct);
        return Structured($"LocKey resolution '{key}' ({language ?? "en_us"})", r);
    }

    [McpServerTool(Name = "oodle_compress", ReadOnly = false, Destructive = false, Idempotent = true)]
    [Description("Compresses a file with the Oodle Kraken codec (low-level utility).")]
    public static async Task<string> OodleCompress(
        Cp77ToolsRunner runner,
        [Description("Input file to compress.")] string inputPath,
        [Description("Compressed output file.")] string outputPath,
        CancellationToken ct = default)
    {
        if (!File.Exists(inputPath))
            return Err($"File not found: {inputPath}");

        var r = await runner.RunAsync(new[] { "oodle", "compress", inputPath, outputPath }, ct);
        return Structured($"Kraken compression: {inputPath} → {outputPath}", r,
            File.Exists(outputPath) ? new List<string> { outputPath } : new List<string>());
    }

    [McpServerTool(Name = "oodle_decompress", ReadOnly = false, Destructive = false, Idempotent = true)]
    [Description("Decompresses a file compressed with the Oodle Kraken codec (low-level utility).")]
    public static async Task<string> OodleDecompress(
        Cp77ToolsRunner runner,
        [Description("Compressed input file.")] string inputPath,
        [Description("Decompressed output file.")] string outputPath,
        CancellationToken ct = default)
    {
        if (!File.Exists(inputPath))
            return Err($"File not found: {inputPath}");

        var r = await runner.RunAsync(new[] { "oodle", "decompress", inputPath, outputPath }, ct);

        // WolvenKit writes the output of `oodle decompress` to <outputPath>.bin;
        // we move it back to the requested path to honor the tool's contract.
        var written = outputPath + ".bin";
        if (!File.Exists(outputPath) && File.Exists(written))
        {
            try { File.Move(written, outputPath, overwrite: true); }
            catch { /* failing that, the output stays at <outputPath>.bin */ }
        }
        return Structured($"Kraken decompression: {inputPath} → {outputPath}", r,
            File.Exists(outputPath) ? new List<string> { outputPath } : new List<string>());
    }

    // ── Writing / creating mods ───────────────────────────────────────

    [McpServerTool(Name = "pack_archive", ReadOnly = false, Destructive = false, Idempotent = true)]
    [Description("Packs a folder of REDengine resource files into a .archive " +
                 "of Cyberpunk 2077 (Kraken compression).")]
    public static async Task<string> PackArchive(
        Cp77ToolsRunner runner,
        [Description("Folder containing the resource files to pack.")] string folderPath,
        [Description("Destination folder for the produced .archive.")] string outputPath,
        [Description("If true, returns the full log (no truncation) — for debugging.")] bool verbose = false,
        CancellationToken ct = default)
    {
        if (!Directory.Exists(folderPath))
            return Err($"Folder not found: {folderPath}");

        return await WithSnapshot(outputPath,
            $"Packing to .archive: {folderPath} → {outputPath}",
            () => runner.RunAsync(
                new[] { "pack", folderPath, "--outpath", outputPath }, ct), verbose);
    }

    [McpServerTool(Name = "import_raw", ReadOnly = false, Destructive = false, Idempotent = true)]
    [Description("Imports raw files (textures, glTF meshes...) into REDengine CR2W files, " +
                 "ready to be packed into a mod.")]
    public static async Task<string> ImportRaw(
        Cp77ToolsRunner runner,
        [Description("Path to a raw file, or a folder containing some.")] string path,
        [Description("Destination folder for the REDengine files.")] string outputPath,
        [Description("If true, returns the full log (no truncation) — for debugging.")] bool verbose = false,
        CancellationToken ct = default)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
            return Err($"Path not found: {path}");

        return await WithSnapshot(outputPath,
            $"Raw → REDengine import: {path} → {outputPath}",
            () => runner.RunAsync(
                new[] { "import", path, "--outpath", outputPath }, ct), verbose);
    }

    [McpServerTool(Name = "build_project", ReadOnly = false, Destructive = false, Idempotent = true)]
    [Description("Compiles the WolvenKit projects (.cpmodproj) found in the given folder, " +
                 "producing a mod ready to install.")]
    public static async Task<string> BuildProject(
        Cp77ToolsRunner runner,
        [Description("Folder containing one or more .cpmodproj projects.")] string projectFolder,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken ct = default)
    {
        if (!Directory.Exists(projectFolder))
            return Err($"Project folder not found: {projectFolder}");

        var r = await runner.RunAsync(new[] { "build", projectFolder }, ct,
            progress is null ? null : Relay(progress));
        return Structured($"Build of .cpmodproj projects in: {projectFolder}", r);
    }

    [McpServerTool(Name = "detect_conflicts", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Detects conflicts between installed mods (the same game file provided by " +
                 "several mods). Takes the game root folder and analyzes its archive/pc/mod. " +
                 "Structured JSON output, easy to parse.")]
    public static async Task<string> DetectConflicts(
        Cp77ToolsRunner runner,
        [Description("Root folder of the Cyberpunk 2077 installation.")] string gamePath,
        CancellationToken ct = default)
    {
        if (!Directory.Exists(gamePath))
            return Err($"Game folder not found: {gamePath}");

        // The `conflicts` verb expects the game ROOT folder (it locates
        // archive/pc/mod itself there) and not the mods folder directly.
        var r = await runner.RunAsync(new[] { "conflicts", gamePath, "--structured" }, ct);
        return Structured($"Mod conflict analysis: {gamePath}", r);
    }

    // ── Project workflow (filesystem, without cp77tools) ────────────────

    [McpServerTool(Name = "list_installed_mods", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Lists installed mods in a Cyberpunk 2077 game folder: " +
                 ".archive archives in archive/pc/mod and REDmod mods in mods/.")]
    public static string ListInstalledMods(
        [Description("Root folder of the Cyberpunk 2077 installation.")] string gamePath)
    {
        if (!Directory.Exists(gamePath))
            return Err($"Game folder not found: {gamePath}");

        var archiveDir = Path.Combine(gamePath, "archive", "pc", "mod");
        var archiveMods = Directory.Exists(archiveDir)
            ? Directory.GetFiles(archiveDir, "*.archive").OrderBy(f => f)
                .Select(f => new { name = Path.GetFileName(f), sizeKo = new FileInfo(f).Length / 1024 })
                .ToList()
            : null;

        var redmodDir = Path.Combine(gamePath, "mods");
        var redMods = Directory.Exists(redmodDir)
            ? Directory.GetDirectories(redmodDir).OrderBy(d => d)
                .Select(Path.GetFileName).ToList()
            : null;

        var result = new
        {
            ok = true,
            status = "success",
            summary = $"Installed mods in {gamePath}",
            archiveMods = archiveMods ?? new(),
            archiveModsCount = archiveMods?.Count ?? 0,
            redMods = redMods ?? new(),
            redModsCount = redMods?.Count ?? 0,
            warnings = (archiveMods is null ? new[] { $"Folder missing: {archiveDir}" }
                        : redMods is null ? new[] { $"Folder missing: {redmodDir}" }
                        : Array.Empty<string>()),
        };
        return JsonSerializer.Serialize(result, JsonOpts);
    }

    [McpServerTool(Name = "create_mod_project", ReadOnly = false, Destructive = false, Idempotent = false)]
    [Description("Creates the folder structure of a WolvenKit mod project " +
                 "(source/archive, source/raw, source/resources, source/customSounds, packed) " +
                 "AND a <modName>.cpmodproj file at the root — directly compilable by " +
                 "build_project. Ready for the workflow: extract_files/uncook → editing → " +
                 "import_raw → build_project.")]
    public static string CreateModProject(
        [Description("Parent folder where to create the project.")] string parentFolder,
        [Description("Name of the mod / project.")] string modName,
        [Description("Mod author (optional).")] string? author = null,
        [Description("Mod version (optional, e.g. 1.0.0).")] string? version = null,
        [Description("Mod description (optional).")] string? description = null)
    {
        if (!Directory.Exists(parentFolder))
            return Err($"Parent folder not found: {parentFolder}");
        if (string.IsNullOrWhiteSpace(modName)
            || modName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return Err("Invalid mod name.");

        var root = Path.Combine(parentFolder, modName);
        if (Directory.Exists(root))
            return Err($"The folder already exists: {root}");

        string[] subdirs =
        {
            Path.Combine("source", "archive"),
            Path.Combine("source", "raw"),
            Path.Combine("source", "resources"),
            Path.Combine("source", "customSounds"),
            "packed",
        };
        foreach (var s in subdirs)
            Directory.CreateDirectory(Path.Combine(root, s));

        File.WriteAllText(Path.Combine(root, "README.txt"),
            $"WolvenKit mod project: {modName}\n\n" +
            "<modName>.cpmodproj  WolvenKit project file (compilable by build_project).\n" +
            "source/archive/    Cooked REDengine files (.mesh, .ent, .xbm...).\n" +
            "                   This is the folder packed with pack_archive.\n" +
            "source/raw/        Raw files (glTF, images, .blend...) to pass through import_raw.\n" +
            "source/resources/  Free files copied as-is.\n" +
            "source/customSounds/  Custom sounds (REDmod audio).\n" +
            "packed/            Output: build_project drops the compiled mod here.\n\n" +
            "Workflow: extract_files / uncook -> editing -> import_raw -> build_project.\n");

        var projFile = Path.Combine(root, modName + ".cpmodproj");
        File.WriteAllText(projFile,
            BuildCpmodprojXml(modName, author, version, description));

        var produced = subdirs.Select(s => s + Path.DirectorySeparatorChar)
            .Append(modName + ".cpmodproj").ToArray();
        var result = new
        {
            ok = true,
            status = "success",
            summary = $"Mod project created: {root}",
            produced,
            warnings = Array.Empty<string>(),
            errors = Array.Empty<string>(),
        };
        return JsonSerializer.Serialize(result, JsonOpts);
    }

    [McpServerTool(Name = "generate_modproj", ReadOnly = false, Destructive = false, Idempotent = true)]
    [Description("Generates a WolvenKit project file (.cpmodproj) in a project folder " +
                 "EXISTING (created manually or by a workflow), so that build_project can " +
                 "compile it. Useful to make compilable a project that does not have one. The format " +
                 "is the <CP77Mod> XML expected by WolvenKit (only the name is required).")]
    public static string GenerateModProj(
        [Description("Root folder of the project (where to drop the .cpmodproj).")] string projectFolder,
        [Description("Name of the mod / project.")] string modName,
        [Description("Mod author (optional).")] string? author = null,
        [Description("Mod version (optional, e.g. 1.0.0).")] string? version = null,
        [Description("Mod description (optional).")] string? description = null,
        [Description("Overwrite an existing .cpmodproj (default: false).")] bool overwrite = false)
    {
        if (!Directory.Exists(projectFolder))
            return Err($"Project folder not found: {projectFolder}");
        if (string.IsNullOrWhiteSpace(modName)
            || modName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return Err("Invalid mod name.");

        var projFile = Path.Combine(projectFolder, modName + ".cpmodproj");
        if (File.Exists(projFile) && !overwrite)
            return Err($"The project already exists: {projFile} (pass overwrite=true to overwrite).");

        File.WriteAllText(projFile, BuildCpmodprojXml(modName, author, version, description));

        var result = new
        {
            ok = true,
            status = "success",
            summary = $".cpmodproj project generated: {projFile}",
            produced = new[] { modName + ".cpmodproj" },
            warnings = Array.Empty<string>(),
            errors = Array.Empty<string>(),
        };
        return JsonSerializer.Serialize(result, JsonOpts);
    }

    /// <summary>Builds the XML content of a .cpmodproj (WolvenKit CP77Mod DTO).
    /// Only &lt;Name&gt; is required; ModName falls back to Name if absent. WolvenKit's
    /// XmlSerializer loader ignores unknown elements, so this subset
    /// is enough for build_project.</summary>
    /// <summary>Stable hash (FNV-1a 64-bit, hex) — deterministic across runs,
    /// unlike string.GetHashCode(). Used to name reusable temp
    /// folders.</summary>
    internal static string StableHash(string s)
    {
        ulong h = 1469598103934665603UL;
        foreach (var c in s) { h ^= c; h *= 1099511628211UL; }
        return h.ToString("x16");
    }

    internal static string BuildCpmodprojXml(string modName, string? author,
        string? version, string? description)
    {
        var sb = new StringBuilder();
        var settings = new XmlWriterSettings { Indent = true, Encoding = new UTF8Encoding(false) };
        using (var sw = new StringWriter(sb))
        using (var w = XmlWriter.Create(sw, settings))
        {
            w.WriteStartDocument();
            w.WriteStartElement("CP77Mod");
            w.WriteAttributeString("xmlns", "xsi", null, "http://www.w3.org/2001/XMLSchema-instance");
            w.WriteAttributeString("xmlns", "xsd", null, "http://www.w3.org/2001/XMLSchema");
            w.WriteElementString("Name", modName);
            w.WriteElementString("ModName", modName);
            w.WriteElementString("Author", author ?? "");
            w.WriteElementString("Email", "");
            w.WriteElementString("Description", description ?? "");
            w.WriteElementString("Version", string.IsNullOrWhiteSpace(version) ? "1.0.0" : version);
            w.WriteEndElement();
            w.WriteEndDocument();
        }
        return sb.ToString();
    }

    // ── Quick inspection (summaries without heavy conversion) ──────────────

    [McpServerTool(Name = "inspect_mesh", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Inspects a REDengine .mesh file and returns a compact summary: " +
                 "number of LODs, sub-meshes, materials, bones. Serializes the CR2W to " +
                 "JSON via the daemon then extracts only the aggregates — far lighter " +
                 "than a full uncook or a read_game_file that returns the whole tree.")]
    public static async Task<string> InspectMesh(
        Cp77ToolsRunner runner,
        [Description("Path to a REDengine .mesh file (already extracted from an archive).")] string meshFile,
        CancellationToken ct = default)
    {
        if (!File.Exists(meshFile))
            return Err($".mesh file not found: {meshFile}");

        var tmp = Path.Combine(Path.GetTempPath(), "wolvenkit-mcp-inspect",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            var r = await runner.RunAsync(
                new[] { "convert", "serialize", meshFile, "--outpath", tmp }, ct);
            var jsonFile = Directory
                .EnumerateFiles(tmp, "*.json", SearchOption.AllDirectories)
                .FirstOrDefault();
            if (jsonFile is null)
                return Structured($"Mesh inspection failed: {meshFile}", r,
                    new List<string>());

            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(jsonFile, ct));
            var stats = ScanMeshStats(doc.RootElement);

            return JsonSerializer.Serialize(new
            {
                ok = true,
                status = "success",
                summary = $"Mesh inspected: {Path.GetFileName(meshFile)} — " +
                          $"{stats.LodCount} LOD(s), {stats.SubMeshCount} sub-mesh, " +
                          $"{stats.MaterialCount} material(s), {stats.BoneCount} bone(s)",
                produced = Array.Empty<string>(),
                warnings = LogLines(r.Stdout, "Warning"),
                errors = LogLines(r.Stdout, "Error"),
                meshFile,
                lodCount = stats.LodCount,
                subMeshCount = stats.SubMeshCount,
                materialCount = stats.MaterialCount,
                boneCount = stats.BoneCount,
                materials = stats.MaterialNames.Take(40).ToList(),
                bones = stats.BoneNames.Take(40).ToList(),
                exitCode = r.ExitCode,
                log = Truncate(r.Stdout, 2_000),
            }, JsonOpts);
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); }
            catch { /* best-effort cleanup */ }
        }
    }

    [McpServerTool(Name = "inspect_texture", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Inspects a REDengine .xbm file (texture) and returns its metadata: " +
                 "resolution, format, compression, mipmaps, texture group — without conversion " +
                 "to PNG/DDS. Serializes the CR2W to JSON via the daemon then extracts only the " +
                 "setup.* fields.")]
    public static async Task<string> InspectTexture(
        Cp77ToolsRunner runner,
        [Description("Path to a REDengine .xbm file (already extracted from an archive).")] string xbmFile,
        CancellationToken ct = default)
    {
        if (!File.Exists(xbmFile))
            return Err($".xbm file not found: {xbmFile}");

        var tmp = Path.Combine(Path.GetTempPath(), "wolvenkit-mcp-inspect",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            var r = await runner.RunAsync(
                new[] { "convert", "serialize", xbmFile, "--outpath", tmp }, ct);
            var jsonFile = Directory
                .EnumerateFiles(tmp, "*.json", SearchOption.AllDirectories)
                .FirstOrDefault();
            if (jsonFile is null)
                return Structured($"Texture inspection failed: {xbmFile}", r,
                    new List<string>());

            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(jsonFile, ct));
            var props = ScanTextureProps(doc.RootElement);

            return JsonSerializer.Serialize(new
            {
                ok = true,
                status = "success",
                summary = $"Texture inspected: {Path.GetFileName(xbmFile)} — " +
                          $"{props.Width}x{props.Height} {props.Format}",
                produced = Array.Empty<string>(),
                warnings = LogLines(r.Stdout, "Warning"),
                errors = LogLines(r.Stdout, "Error"),
                xbmFile,
                width = props.Width,
                height = props.Height,
                format = props.Format,
                compression = props.Compression,
                mipLevels = props.MipLevels,
                textureGroup = props.TextureGroup,
                exitCode = r.ExitCode,
                log = Truncate(r.Stdout, 2_000),
            }, JsonOpts);
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); }
            catch { /* best-effort cleanup */ }
        }
    }

    [McpServerTool(Name = "describe_tweak_record", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("For a given TweakDB identifier (record), lists all its flats with " +
                 "their types and current values — the inverse of tweakdb_query, which only " +
                 "searches identifiers. Essential before editing a record via " +
                 "write_tweak: lets you know which fields exist and their values.")]
    public static async Task<string> DescribeTweakRecord(
        Cp77ToolsRunner runner,
        [Description("Path to a tweakdb.bin file (typically <game>/r6/cache/tweakdb.bin).")] string tweakdbPath,
        [Description("TweakDB identifier of the record (e.g. Items.Preset_Achilles_Collectible_inline0).")] string recordId,
        CancellationToken ct = default)
    {
        if (!File.Exists(tweakdbPath))
            return Err($"TweakDB file not found: {tweakdbPath}");
        if (string.IsNullOrWhiteSpace(recordId))
            return Err("recordId empty.");

        var r = await runner.RunAsync(
            new[] { "tweakdb-describe", tweakdbPath, recordId }, ct);
        return Structured($"TweakDB record description: {recordId}", r);
    }

    // ── Structured TweakDB (TweakXL — YAML format) ──────────────────────

    [McpServerTool(Name = "read_tweak", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Reads a .tweak file (TweakXL — YAML format) and returns its content as " +
                 "editable JSON. Lets you inspect and modify tweaks while staying in " +
                 "a structured format, without handling raw YAML.")]
    public static async Task<string> ReadTweak(
        Cp77ToolsRunner runner,
        [Description("Path to a .tweak file (TweakXL).")] string tweakFile,
        CancellationToken ct = default)
    {
        if (!File.Exists(tweakFile))
            return Err($".tweak file not found: {tweakFile}");

        var jsonFile = Path.Combine(Path.GetTempPath(), "wolvenkit-mcp-tweak",
            Guid.NewGuid().ToString("N") + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(jsonFile)!);

        var r = await runner.RunAsync(new[] { "tweak", "read", tweakFile, jsonFile }, ct);
        if (r.ExitCode != 0 || !File.Exists(jsonFile))
        {
            return Structured($".tweak read failed: {tweakFile}", r,
                new List<string>());
        }

        var content = await File.ReadAllTextAsync(jsonFile, ct);
        var truncated = content.Length > ReadContentCap;
        return JsonSerializer.Serialize(new
        {
            ok = true,
            status = "success",
            summary = $".tweak read: {tweakFile}" +
                      (truncated ? " (content truncated — read jsonFile in full)" : ""),
            produced = new[] { jsonFile },
            warnings = LogLines(r.Stdout + r.Stderr, "Warning"),
            errors = LogLines(r.Stdout + r.Stderr, "Error"),
            tweakFile,
            jsonFile,
            truncated,
            content = truncated ? content[..ReadContentCap] : content,
            exitCode = r.ExitCode,
        }, JsonOpts);
    }

    [McpServerTool(Name = "write_tweak", ReadOnly = false, Destructive = true, Idempotent = true)]
    [Description("Reconverts a JSON (produced by read_tweak then edited) into a .tweak file " +
                 "(TweakXL YAML) ready to be copied into <game>/r6/tweaks/ via install_tweak.")]
    public static async Task<string> WriteTweak(
        Cp77ToolsRunner runner,
        [Description("Path to the edited JSON file (from read_tweak).")] string jsonFile,
        [Description("Path to the .tweak file to produce.")] string outputTweakFile,
        CancellationToken ct = default)
    {
        if (!File.Exists(jsonFile))
            return Err($"JSON file not found: {jsonFile}");

        Directory.CreateDirectory(Path.GetDirectoryName(outputTweakFile) ?? ".");
        var r = await runner.RunAsync(
            new[] { "tweak", "write", jsonFile, outputTweakFile }, ct);
        return Structured($".tweak write: {jsonFile} → {outputTweakFile}", r,
            File.Exists(outputTweakFile) ? new List<string> { outputTweakFile }
                                          : new List<string>());
    }

    [McpServerTool(Name = "validate_tweak", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Checks a .tweak file against a TweakDB: each key in the file must " +
                 "exist in TweakDB (record or flat), unless it declares $instanceOf " +
                 "(new derived record). Returns the list of unknown keys — useful " +
                 "before install_tweak.")]
    public static async Task<string> ValidateTweak(
        Cp77ToolsRunner runner,
        [Description("Path to the .tweak file to validate.")] string tweakFile,
        [Description("Path to the reference tweakdb.bin (typically <game>/r6/cache/tweakdb.bin).")] string tweakdbBin,
        CancellationToken ct = default)
    {
        if (!File.Exists(tweakFile))
            return Err($".tweak file not found: {tweakFile}");
        if (!File.Exists(tweakdbBin))
            return Err($"tweakdb.bin not found: {tweakdbBin}");

        var r = await runner.RunAsync(
            new[] { "tweak", "validate", tweakFile, tweakdbBin }, ct);
        return Structured($".tweak validation: {tweakFile} against {tweakdbBin}", r);
    }

    [McpServerTool(Name = "generate_redscript_template", ReadOnly = false, Destructive = false, Idempotent = true)]
    [Description("Generates a .reds (RED4Script) file ready to edit, from a catalog " +
                 "of common patterns: add_method (@addMethod), wrap_method (@wrapMethod), " +
                 "replace_method (@replaceMethod), add_field (@addField), new_class. Avoids " +
                 "writing the annotation syntax by hand.")]
    public static string GenerateRedscriptTemplate(
        [Description("Pattern: add_method | wrap_method | replace_method | add_field | new_class.")] string pattern,
        [Description("Template parameters as JSON (see the pattern description).")] string parametersJson,
        [Description("Path to the .reds file to produce.")] string outputFile)
    {
        Dictionary<string, object?> p;
        try
        {
            using var doc = JsonDocument.Parse(parametersJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return Err("parametersJson must be a JSON object.");
            p = doc.RootElement.EnumerateObject().ToDictionary(
                k => k.Name,
                v => v.Value.ValueKind switch
                {
                    JsonValueKind.String => (object?)v.Value.GetString(),
                    JsonValueKind.Number => v.Value.TryGetInt64(out var l)
                        ? (object?)l : v.Value.GetDouble(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Null => null,
                    _ => v.Value.GetRawText(),
                });
        }
        catch (JsonException ex)
        {
            return Err($"Invalid parametersJson: {ex.Message}");
        }

        string? Str(string key)
            => p.TryGetValue(key, out var v) ? v?.ToString() : null;

        static string ExtractArgNames(string args)
        {
            if (string.IsNullOrWhiteSpace(args)) return "";
            return string.Join(", ", args.Split(',')
                .Select(a => a.Trim().Split(':')[0].Trim())
                .Where(s => s.Length > 0));
        }

        string reds;
        string desc;
        switch (pattern.ToLowerInvariant())
        {
            case "add_method":
            {
                var target = Str("targetClass");
                var name = Str("methodName");
                if (string.IsNullOrEmpty(target) || string.IsNullOrEmpty(name))
                    return Err("add_method: 'targetClass' and 'methodName' required.");
                var args = Str("args") ?? "";
                var ret = Str("returnType") ?? "Void";
                var body = Str("body") ?? "// TODO";
                reds = $"@addMethod({target})\npublic func {name}({args}) -> {ret} {{\n  {body}\n}}\n";
                desc = $"add_method {name} on {target}";
                break;
            }
            case "wrap_method":
            {
                var target = Str("targetClass");
                var name = Str("methodName");
                if (string.IsNullOrEmpty(target) || string.IsNullOrEmpty(name))
                    return Err("wrap_method: 'targetClass' and 'methodName' required.");
                var args = Str("args") ?? "";
                var ret = Str("returnType") ?? "Void";
                var passthrough = ExtractArgNames(args);
                var body = ret == "Void"
                    ? $"  wrappedMethod({passthrough});\n  // TODO: extra logic"
                    : $"  let result = wrappedMethod({passthrough});\n  // TODO: post-process\n  return result;";
                reds = $"@wrapMethod({target})\npublic func {name}({args}) -> {ret} {{\n{body}\n}}\n";
                desc = $"wrap_method {name} on {target}";
                break;
            }
            case "replace_method":
            {
                var target = Str("targetClass");
                var name = Str("methodName");
                if (string.IsNullOrEmpty(target) || string.IsNullOrEmpty(name))
                    return Err("replace_method: 'targetClass' and 'methodName' required.");
                var args = Str("args") ?? "";
                var ret = Str("returnType") ?? "Void";
                var body = Str("body") ?? "// TODO: full replacement";
                reds = $"@replaceMethod({target})\npublic func {name}({args}) -> {ret} {{\n  {body}\n}}\n";
                desc = $"replace_method {name} on {target}";
                break;
            }
            case "add_field":
            {
                var target = Str("targetClass");
                var name = Str("fieldName");
                var type = Str("fieldType") ?? "Int32";
                if (string.IsNullOrEmpty(target) || string.IsNullOrEmpty(name))
                    return Err("add_field: 'targetClass' and 'fieldName' required.");
                reds = $"@addField({target})\nlet {name}: {type};\n";
                desc = $"add_field {name}: {type} on {target}";
                break;
            }
            case "new_class":
            {
                var name = Str("className");
                if (string.IsNullOrEmpty(name))
                    return Err("new_class: 'className' required.");
                var extends = Str("extends");
                var modName = Str("moduleName");
                var sb = new StringBuilder();
                if (!string.IsNullOrEmpty(modName))
                    sb.Append("module ").Append(modName).AppendLine().AppendLine();
                sb.Append("public class ").Append(name);
                if (!string.IsNullOrEmpty(extends)) sb.Append(" extends ").Append(extends);
                sb.AppendLine(" {");
                sb.AppendLine("  // TODO: fields and methods");
                sb.AppendLine("}");
                reds = sb.ToString();
                desc = $"new_class {name}" + (extends is not null ? $" extends {extends}" : "");
                break;
            }
            default:
                return Err($"Unknown pattern: {pattern} " +
                           "(add_method, wrap_method, replace_method, add_field, new_class).");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputFile) ?? ".");
        File.WriteAllText(outputFile, reds);

        return JsonSerializer.Serialize(new
        {
            ok = true,
            status = "success",
            summary = $".reds template: {desc} → {outputFile}",
            produced = new[] { outputFile },
            warnings = Array.Empty<string>(),
            errors = Array.Empty<string>(),
            pattern,
            outputFile,
            content = reds,
        }, JsonOpts);
    }

    [McpServerTool(Name = "generate_tweak_template", ReadOnly = false, Destructive = false, Idempotent = true)]
    [Description("Generates a .tweak file (TweakXL — YAML) ready to edit, from a " +
                 "catalog of common patterns. Avoids knowing the TweakXL syntax by hand. " +
                 "Supported patterns: override_field (modifies a field of an existing record), " +
                 "new_record (creates a new record via $instanceOf), boost_stat (modifies a " +
                 "numeric stat with a new value).")]
    public static string GenerateTweakTemplate(
        [Description("Pattern: override_field | new_record | boost_stat.")] string pattern,
        [Description("Template parameters as JSON (keys depending on the pattern, see description).")] string parametersJson,
        [Description("Path to the .tweak file to produce.")] string outputFile)
    {
        Dictionary<string, object?> p;
        try
        {
            using var doc = JsonDocument.Parse(parametersJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return Err("parametersJson must be a JSON object.");
            p = doc.RootElement.EnumerateObject().ToDictionary(
                k => k.Name,
                v => v.Value.ValueKind switch
                {
                    JsonValueKind.String => (object?)v.Value.GetString(),
                    JsonValueKind.Number => v.Value.TryGetInt64(out var l)
                        ? (object?)l : v.Value.GetDouble(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Null => null,
                    _ => v.Value.GetRawText(),
                });
        }
        catch (JsonException ex)
        {
            return Err($"Invalid parametersJson: {ex.Message}");
        }

        string? Str(string key)
            => p.TryGetValue(key, out var v) ? v?.ToString() : null;
        object? Val(string key)
            => p.TryGetValue(key, out var v) ? v : null;

        string yaml;
        string description;
        switch (pattern.ToLowerInvariant())
        {
            case "override_field":
            {
                var id = Str("recordId");
                var field = Str("field");
                var value = Val("value");
                if (string.IsNullOrEmpty(id))
                    return Err("override_field: 'recordId' required (e.g. Items.w_melee_001).");
                if (string.IsNullOrEmpty(field))
                    return Err("override_field: 'field' required (e.g. damage).");
                if (value is null)
                    return Err("override_field: 'value' required.");
                yaml = $"{id}:\n  {field}: {FormatYamlValue(value)}\n";
                description = $"Override: {id}.{field} = {value}";
                break;
            }
            case "new_record":
            {
                var newId = Str("newId");
                var baseId = Str("baseId");
                if (string.IsNullOrEmpty(newId))
                    return Err("new_record: 'newId' required (e.g. MyMod.NewWeapon).");
                if (string.IsNullOrEmpty(baseId))
                    return Err("new_record: 'baseId' required (existing record to instantiate).");
                var sb = new StringBuilder();
                sb.Append(newId).AppendLine(":");
                sb.Append("  $instanceOf: ").AppendLine(baseId);
                if (p.TryGetValue("overrides", out var ov) && ov is string ovJson)
                {
                    // overrides as sub-JSON {field: value, ...}
                    try
                    {
                        using var ovDoc = JsonDocument.Parse(ovJson);
                        foreach (var prop in ovDoc.RootElement.EnumerateObject())
                            sb.Append("  ").Append(prop.Name).Append(": ")
                              .AppendLine(FormatYamlValue(prop.Value.ToString()));
                    }
                    catch { /* malformed overrides: continue without */ }
                }
                yaml = sb.ToString();
                description = $"New record: {newId} <- $instanceOf {baseId}";
                break;
            }
            case "boost_stat":
            {
                var id = Str("recordId");
                var stat = Str("stat") ?? "damage";
                var value = Val("value");
                if (string.IsNullOrEmpty(id))
                    return Err("boost_stat: 'recordId' required.");
                if (value is null)
                    return Err("boost_stat: 'value' required (new stat value).");
                yaml = $"{id}:\n  {stat}: {FormatYamlValue(value)}\n";
                description = $"Boost stat: {id}.{stat} = {value}";
                break;
            }
            default:
                return Err($"Unknown pattern: {pattern} " +
                           "(override_field, new_record, boost_stat).");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputFile) ?? ".");
        File.WriteAllText(outputFile, yaml);

        return JsonSerializer.Serialize(new
        {
            ok = true,
            status = "success",
            summary = $"{description} → {outputFile}",
            produced = new[] { outputFile },
            warnings = Array.Empty<string>(),
            errors = Array.Empty<string>(),
            pattern,
            outputFile,
            content = yaml,
        }, JsonOpts);
    }

    [McpServerTool(Name = "install_tweak", ReadOnly = false, Destructive = true, Idempotent = true)]
    [Description("Installs a .tweak file into Cyberpunk 2077: copies to " +
                 "<game>/r6/tweaks/<name>.tweak. Taken into account at the next game launch, " +
                 "without rebuild or redeployment (TweakXL is hot-loaded).")]
    public static string InstallTweak(
        [Description("Path to the .tweak file to install.")] string tweakFile,
        [Description("Root folder of the Cyberpunk 2077 installation.")] string gamePath)
    {
        if (!File.Exists(tweakFile))
            return Err($".tweak file not found: {tweakFile}");
        if (!gamePath.Contains(Path.DirectorySeparatorChar)
            && !gamePath.Contains(Path.AltDirectorySeparatorChar))
            return Err("gamePath must be a folder path (not a plain name).");
        if (!Directory.Exists(gamePath))
            return Err($"Game folder not found: {gamePath}");

        var tweaksDir = Path.Combine(gamePath, "r6", "tweaks");
        Directory.CreateDirectory(tweaksDir);
        var dest = Path.Combine(tweaksDir, Path.GetFileName(tweakFile));
        var existed = File.Exists(dest);
        File.Copy(tweakFile, dest, overwrite: true);

        return JsonSerializer.Serialize(new
        {
            ok = true,
            status = "success",
            summary = (existed ? ".tweak reinstalled: " : ".tweak installed: ") + dest,
            produced = new[] { dest },
            warnings = existed
                ? new[] { "A .tweak with the same name already existed; it has been replaced." }
                : Array.Empty<string>(),
            errors = Array.Empty<string>(),
            installedPath = dest,
        }, JsonOpts);
    }

    // ── REDscript scripts (.reds) — reading and textual lint ─────────────

    [McpServerTool(Name = "read_script", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Reads a REDscript script file (.reds, .script, .swift, .redscript) and " +
                 "returns its content + its structure extracted by regex: func/" +
                 "class declarations, @addMethod/@addField/@wrapMethod annotations, module/import. " +
                 "Textual analysis only — no semantic validation.")]
    public static async Task<string> ReadScript(
        [Description("Path to a script file (.reds / .script / .swift / .redscript).")] string scriptFile,
        CancellationToken ct = default)
    {
        if (!File.Exists(scriptFile))
            return Err($"Script file not found: {scriptFile}");
        var ext = Path.GetExtension(scriptFile).TrimStart('.').ToLowerInvariant();
        if (ext is not ("reds" or "script" or "swift" or "redscript"))
            return Err($"Unsupported extension: .{ext} (.reds, .script, .swift, .redscript).");

        var content = await File.ReadAllTextAsync(scriptFile, ct);
        var (declarations, moduleName) = ScanScriptDeclarations(content);
        var truncated = content.Length > ReadContentCap;

        return JsonSerializer.Serialize(new
        {
            ok = true,
            status = "success",
            summary = $"Script read: {scriptFile} ({content.Length} chars, " +
                      $"{declarations.Count} declaration(s))" +
                      (truncated ? " — content truncated" : ""),
            produced = Array.Empty<string>(),
            warnings = Array.Empty<string>(),
            errors = Array.Empty<string>(),
            scriptFile,
            moduleName,
            declarations,
            truncated,
            content = truncated ? content[..ReadContentCap] : content,
        }, JsonOpts);
    }

    [McpServerTool(Name = "lint_script", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Syntax analysis of a REDscript file via a real grammar parser " +
                 "(tokenizer + recursive descent): (1) syntax ERRORS with line:column " +
                 "(malformed signatures/types/generics, unmatched braces/parens, " +
                 "unterminated strings, invalid declarations), (2) semantic WARNINGS " +
                 "(@addMethod/@wrapMethod/@replaceMethod annotations well placed and targeting a " +
                 "class, @wrapMethod calling wrappedMethod(), duplicate declarations). " +
                 "Calibrated to zero false positives on the real REDscript corpus. Does NOT perform " +
                 "type checking (resolution of external types/methods = scc compiler " +
                 "+ ecosystem, out of scope).")]
    public static async Task<string> LintScript(
        [Description("Path to a script file (.reds / .script / .swift / .redscript).")] string scriptFile,
        CancellationToken ct = default)
    {
        if (!File.Exists(scriptFile))
            return Err($"Script file not found: {scriptFile}");
        var ext = Path.GetExtension(scriptFile).TrimStart('.').ToLowerInvariant();
        if (ext is not ("reds" or "script" or "swift" or "redscript"))
            return Err($"Unsupported extension: .{ext} (.reds, .script, .swift, .redscript).");

        var content = await File.ReadAllTextAsync(scriptFile, ct);
        var parse = RedscriptParser.Parse(content);
        var (decls, moduleName) = ScanScriptDeclarations(content);

        // Syntax errors (parser) + semantic warnings (heuristics).
        var errors = parse.Diagnostics
            .Where(d => d.Severity == "ERROR")
            .Select(d => $"ERROR L{d.Line}:{d.Col} — {d.Message}")
            .ToList();
        var warnings = LintScriptSemantics(content);

        var status = errors.Count > 0 ? "error" : warnings.Count > 0 ? "partial" : "success";
        return JsonSerializer.Serialize(new
        {
            ok = status != "error",
            status,
            summary = $"Script lint: {scriptFile} — {decls.Count} declaration(s), " +
                      $"{errors.Count} error(s), {warnings.Count} warning(s)",
            produced = Array.Empty<string>(),
            warnings,
            errors,
            scriptFile,
            moduleName = parse.Module ?? moduleName,
            declarations = decls,
            parsedDeclarations = parse.Declarations.Count,
            limitation = "Syntax analysis + light semantics: no type checking " +
                         "(external resolution = scc compiler + ecosystem, out of scope).",
        }, JsonOpts);
    }

    // ── REDmod packaging (post-1.6) ──────────────────────────────────────

    [McpServerTool(Name = "create_redmod_project", ReadOnly = false, Destructive = false, Idempotent = false)]
    [Description("Creates a mod project in REDmod format (post-1.6): structure " +
                 "mods/<name>/info.json + sub-folders archives/, scripts/, tweaks/, " +
                 "customSounds/. Distinct from the .archive format (archive/pc/mod/) — the " +
                 "REDmod format also allows scripts (.reds) and tweaks (.tweak).")]
    public static string CreateRedmodProject(
        [Description("Parent folder where to create the REDmod project.")] string parentFolder,
        [Description("Name of the REDmod (becomes the sub-folder).")] string modName,
        [Description("Description visible in the REDmod launcher.")] string description = "",
        [Description("Semantic version of the mod.")] string version = "1.0.0")
    {
        if (!Directory.Exists(parentFolder))
            return Err($"Parent folder not found: {parentFolder}");
        if (string.IsNullOrWhiteSpace(modName)
            || modName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return Err("Invalid REDmod name.");

        var root = Path.Combine(parentFolder, modName);
        if (Directory.Exists(root))
            return Err($"The folder already exists: {root}");

        string[] subdirs = { "archives", "scripts", "tweaks", "customSounds" };
        foreach (var s in subdirs)
            Directory.CreateDirectory(Path.Combine(root, s));

        var info = new
        {
            name = modName,
            version,
            description = string.IsNullOrWhiteSpace(description)
                ? $"REDmod {modName}" : description,
            customSounds = Array.Empty<object>(),
        };
        var infoPath = Path.Combine(root, "info.json");
        File.WriteAllText(infoPath, JsonSerializer.Serialize(info, JsonOpts));

        var produced = subdirs
            .Select(s => s + Path.DirectorySeparatorChar)
            .Append("info.json")
            .ToArray();

        return JsonSerializer.Serialize(new
        {
            ok = true,
            status = "success",
            summary = $"REDmod project created: {root}",
            produced,
            warnings = Array.Empty<string>(),
            errors = Array.Empty<string>(),
            redmodRoot = root,
        }, JsonOpts);
    }

    [McpServerTool(Name = "pack_redmod", ReadOnly = false, Destructive = false, Idempotent = true)]
    [Description("Packs a REDmod project into a .zip for distribution. The zip contains " +
                 "the <name>/ folder with its info.json at the root; the end user " +
                 "extracts into <game>/mods/. Validates the presence of info.json before zipping.")]
    public static string PackRedmod(
        [Description("Source folder of the REDmod (contains info.json at its root).")] string modSourceFolder,
        [Description("Destination folder of the produced .zip.")] string outputPath)
    {
        if (!Directory.Exists(modSourceFolder))
            return Err($"REDmod folder not found: {modSourceFolder}");
        var info = Path.Combine(modSourceFolder, "info.json");
        if (!File.Exists(info))
            return Err($"info.json missing at the root: {info}");

        var modName = Path.GetFileName(modSourceFolder.TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        Directory.CreateDirectory(outputPath);
        var zipPath = Path.Combine(outputPath, $"{modName}.zip");

        if (File.Exists(zipPath)) File.Delete(zipPath);
        ZipFile.CreateFromDirectory(modSourceFolder, zipPath,
            CompressionLevel.Optimal, includeBaseDirectory: true);

        return JsonSerializer.Serialize(new
        {
            ok = true,
            status = "success",
            summary = $"REDmod packed: {zipPath} " +
                      $"({new FileInfo(zipPath).Length / 1024} KiB)",
            produced = new[] { zipPath },
            warnings = Array.Empty<string>(),
            errors = Array.Empty<string>(),
            zipPath,
            modName,
        }, JsonOpts);
    }

    [McpServerTool(Name = "install_redmod", ReadOnly = false, Destructive = true, Idempotent = true)]
    [Description("Installs a REDmod project into Cyberpunk 2077: recursive copy of the " +
                 "source folder to <game>/mods/<name>/. The mod will be taken into account at the next " +
                 "launch via the REDmod launcher (or redMod.exe deploy).")]
    public static string InstallRedmod(
        [Description("Source folder of the REDmod (with info.json at its root).")] string modSourceFolder,
        [Description("Root folder of the Cyberpunk 2077 installation.")] string gamePath)
    {
        if (!Directory.Exists(modSourceFolder))
            return Err($"REDmod folder not found: {modSourceFolder}");
        if (!Directory.Exists(gamePath))
            return Err($"Game folder not found: {gamePath}");
        var info = Path.Combine(modSourceFolder, "info.json");
        if (!File.Exists(info))
            return Err($"info.json missing at the root: {info}");

        var modName = Path.GetFileName(modSourceFolder.TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var dest = Path.Combine(gamePath, "mods", modName);
        var existed = Directory.Exists(dest);

        CopyDirectoryRecursive(modSourceFolder, dest);

        return JsonSerializer.Serialize(new
        {
            ok = true,
            status = "success",
            summary = (existed ? "REDmod reinstalled: " : "REDmod installed: ") + dest,
            produced = new[] { dest },
            warnings = existed
                ? new[] { "A REDmod with the same name already existed; its files have been replaced." }
                : Array.Empty<string>(),
            errors = Array.Empty<string>(),
            installedPath = dest,
        }, JsonOpts);
    }

    [McpServerTool(Name = "backup_mods", ReadOnly = false, Destructive = false, Idempotent = false)]
    [Description("Backs up the state of installed mods of a Cyberpunk 2077 installation into " +
                 "a timestamped .zip: archive/pc/mod/ (.archive mods), mods/ (REDmods), " +
                 "r6/tweaks/ (.tweak). Safety net before a modding session. " +
                 "The ZIP preserves sub-folders relative to the game folder.")]
    public static string BackupMods(
        [Description("Root folder of the Cyberpunk 2077 installation.")] string gamePath,
        [Description("Folder where to drop the produced .zip.")] string outputDir,
        [Description("ZIP file name (default: wkmcp-mods-backup-<YYYYMMDD-HHmmss>.zip).")] string? backupName = null)
    {
        if (!Directory.Exists(gamePath))
            return Err($"Game folder not found: {gamePath}");

        Directory.CreateDirectory(outputDir);
        var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var fileName = backupName ?? $"wkmcp-mods-backup-{timestamp}.zip";
        var zipPath = Path.Combine(outputDir, fileName);

        var staged = Path.Combine(Path.GetTempPath(),
            "wkmcp-backup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staged);
        try
        {
            int archiveCount = 0, redmodCount = 0, tweakCount = 0;
            var warnings = new List<string>();

            var srcArchive = Path.Combine(gamePath, "archive", "pc", "mod");
            if (Directory.Exists(srcArchive))
            {
                var dst = Path.Combine(staged, "archive", "pc", "mod");
                CopyDirectoryRecursive(srcArchive, dst);
                archiveCount = Directory.EnumerateFiles(dst, "*.archive",
                    SearchOption.AllDirectories).Count();
            }
            else warnings.Add($"Folder missing: {srcArchive}");

            var srcRedmod = Path.Combine(gamePath, "mods");
            if (Directory.Exists(srcRedmod))
            {
                var dst = Path.Combine(staged, "mods");
                CopyDirectoryRecursive(srcRedmod, dst);
                redmodCount = Directory.EnumerateDirectories(dst).Count();
            }
            else warnings.Add($"Folder missing: {srcRedmod}");

            var srcTweak = Path.Combine(gamePath, "r6", "tweaks");
            if (Directory.Exists(srcTweak))
            {
                var dst = Path.Combine(staged, "r6", "tweaks");
                CopyDirectoryRecursive(srcTweak, dst);
                tweakCount = Directory.EnumerateFiles(dst, "*",
                    SearchOption.AllDirectories).Count();
            }
            else warnings.Add($"Folder missing: {srcTweak}");

            if (archiveCount + redmodCount + tweakCount == 0)
                return Err($"No mod to back up in {gamePath}.");

            if (File.Exists(zipPath)) File.Delete(zipPath);
            ZipFile.CreateFromDirectory(staged, zipPath,
                CompressionLevel.Optimal, includeBaseDirectory: false);
            var size = new FileInfo(zipPath).Length;

            return JsonSerializer.Serialize(new
            {
                ok = true,
                status = "success",
                summary = $"Backup created: {zipPath} ({size / 1024} KiB · " +
                          $"{archiveCount} archives + {redmodCount} REDmods + {tweakCount} tweaks)",
                produced = new[] { zipPath },
                warnings,
                errors = Array.Empty<string>(),
                zipPath,
                size,
                archiveCount,
                redmodCount,
                tweakCount,
            }, JsonOpts);
        }
        finally
        {
            try { Directory.Delete(staged, recursive: true); }
            catch { /* best-effort cleanup */ }
        }
    }

    [McpServerTool(Name = "restore_mods", ReadOnly = false, Destructive = true, Idempotent = false)]
    [Description("Restores a mods backup produced by backup_mods. `merge` mode (default): " +
                 "extracts over the existing without deleting. `replace` mode: first empties " +
                 "the target folders (archive/pc/mod, mods, r6/tweaks) then extracts — " +
                 "destructive, ideally preceded by a fresh `backup_mods` for safety.")]
    public static string RestoreMods(
        [Description("Path to the backup ZIP to restore.")] string backupZip,
        [Description("Root folder of the target Cyberpunk 2077 installation.")] string gamePath,
        [Description("merge (default) | replace.")] string mode = "merge")
    {
        if (!File.Exists(backupZip))
            return Err($"Backup ZIP not found: {backupZip}");
        if (!Directory.Exists(gamePath))
            return Err($"Game folder not found: {gamePath}");
        if (mode is not ("merge" or "replace"))
            return Err($"Unknown mode: {mode} (merge | replace).");

        var warnings = new List<string>();
        if (mode == "replace")
        {
            foreach (var sub in new[]
            {
                Path.Combine("archive", "pc", "mod"),
                "mods",
                Path.Combine("r6", "tweaks"),
            })
            {
                var p = Path.Combine(gamePath, sub);
                if (Directory.Exists(p))
                {
                    try { Directory.Delete(p, recursive: true); }
                    catch (Exception ex) { warnings.Add($"Failed to delete {p}: {ex.Message}"); }
                }
            }
        }

        var extractedCount = 0;
        // Safeguard against "Zip Slip": a backupZip provided by the user is not
        // reliable; an entry "../../x" would write outside the game folder.
        var gameFull = Path.GetFullPath(gamePath) + Path.DirectorySeparatorChar;
        using (var zip = ZipFile.OpenRead(backupZip))
        {
            foreach (var entry in zip.Entries)
            {
                var rel = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
                var dest = Path.Combine(gamePath, rel);
                var destFull = Path.GetFullPath(dest);
                if (!destFull.StartsWith(gameFull, StringComparison.OrdinalIgnoreCase))
                {
                    warnings.Add($"Entry skipped (outside the game folder): {entry.FullName}");
                    continue;
                }
                // Folder (empty entry ending with /)
                if (string.IsNullOrEmpty(entry.Name))
                {
                    Directory.CreateDirectory(destFull);
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(destFull)!);
                entry.ExtractToFile(destFull, overwrite: true);
                extractedCount++;
            }
        }

        return JsonSerializer.Serialize(new
        {
            ok = true,
            status = "success",
            summary = $"Restore from {Path.GetFileName(backupZip)} → {gamePath} " +
                      $"(mode {mode}, {extractedCount} file(s) extracted)",
            produced = Array.Empty<string>(),
            warnings,
            errors = Array.Empty<string>(),
            mode,
            gamePath,
            extractedCount,
        }, JsonOpts);
    }

    [McpServerTool(Name = "lint_mod", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Checks a .archive mod before installation: reports extensions not " +
                 "recognized by REDengine (that the game would silently ignore) and, if the " +
                 "game folder is provided, detects conflicts with other already " +
                 "installed mods (shared internal paths). Safety net to call before " +
                 "install_mod.")]
    public static async Task<string> LintMod(
        Cp77ToolsRunner runner,
        [Description("Path to the .archive of the mod to check.")] string archivePath,
        [Description("Root folder of Cyberpunk 2077 (optional; enables conflict detection " +
                     "with already installed mods).")] string? gamePath = null,
        CancellationToken ct = default)
    {
        if (!File.Exists(archivePath))
            return Err($"Mod archive not found: {archivePath}");
        if (!archivePath.EndsWith(".archive", StringComparison.OrdinalIgnoreCase))
            return Err($"Not a .archive: {archivePath}");

        var (entries, fromCache, raw) = await runner.GetArchiveListingAsync(archivePath, ct);
        if (entries.Count == 0)
            return Err($"Empty listing for {archivePath} (exit={raw.ExitCode})");

        var warnings = new List<string>();
        var errors = new List<string>();
        var unknownExtCount = 0;
        var conflictCount = 0;
        var conflicts = new List<object>();

        // 1. Unknown REDengine extensions — the game will ignore them.
        foreach (var e in entries)
        {
            var ext = Path.GetExtension(e).TrimStart('.').ToLowerInvariant();
            if (string.IsNullOrEmpty(ext))
            {
                warnings.Add($"File without extension: {e}");
                unknownExtCount++;
            }
            else if (!RedEngineExtensions.Contains(ext))
            {
                warnings.Add($"Non-REDengine extension ({ext}): {e} — ignored by the game");
                unknownExtCount++;
            }
        }

        // 2. Conflicts with installed mods (shared internal paths).
        if (!string.IsNullOrWhiteSpace(gamePath) && Directory.Exists(gamePath))
        try
        {
            var modsDir = Path.Combine(gamePath, "archive", "pc", "mod");
            if (Directory.Exists(modsDir))
            {
                var myFull = Path.GetFullPath(archivePath);
                var mySet = new HashSet<string>(entries, StringComparer.OrdinalIgnoreCase);
                foreach (var other in Directory.GetFiles(modsDir, "*.archive"))
                {
                    ct.ThrowIfCancellationRequested();
                    if (string.Equals(Path.GetFullPath(other), myFull, StringComparison.OrdinalIgnoreCase))
                        continue; // do not compare against itself

                    var (otherEntries, _, _) = await runner.GetArchiveListingAsync(other, ct);
                    var shared = otherEntries
                        .Where(e => mySet.Contains(e))
                        .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    if (shared.Count > 0)
                    {
                        conflictCount += shared.Count;
                        conflicts.Add(new
                        {
                            mod = Path.GetFileName(other),
                            sharedCount = shared.Count,
                            sharedFiles = shared.Take(20).ToList(),
                            truncated = shared.Count > 20,
                        });
                        warnings.Add(
                            $"Conflict with {Path.GetFileName(other)}: {shared.Count} shared path(s)");
                    }
                }
            }
            else
            {
                warnings.Add($"Mod folder absent: {modsDir} (conflict detection disabled)");
            }
        }
        catch (OperationCanceledException) { return Cancelled("lint_mod"); }

        var ok = errors.Count == 0;
        var status = errors.Count > 0 ? "error"
                   : warnings.Count > 0 ? "partial"
                   : "success";

        return JsonSerializer.Serialize(new
        {
            ok,
            status,
            summary = $"Lint mod {Path.GetFileName(archivePath)} — {entries.Count} file(s), " +
                      $"{unknownExtCount} unknown extension(s), {conflictCount} conflict(s)" +
                      (fromCache ? " (cache)" : ""),
            produced = Array.Empty<string>(),
            warnings,
            errors,
            archivePath,
            fileCount = entries.Count,
            unknownExtCount,
            conflictCount,
            conflicts,
            fromCache,
            exitCode = 0,
            log = "",
        }, JsonOpts);
    }

    [McpServerTool(Name = "mod_summary", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Compact summary of what a mod does. Accepts a .archive file (summarized " +
                 "by file extension) or a REDmod folder (parses info.json, enumerates " +
                 "archives/, scripts/, tweaks/, customSounds/, extracts the top-level keys of the " +
                 ".tweak and the declarations of the .reds). Avoids chaining lint_mod + read_tweak + " +
                 "read_script by hand.")]
    public static async Task<string> ModSummary(
        Cp77ToolsRunner runner,
        [Description("Path of a .archive OR of a REDmod folder (with info.json at the root).")] string modPath,
        CancellationToken ct = default)
    {
        if (File.Exists(modPath) && modPath.EndsWith(".archive", StringComparison.OrdinalIgnoreCase))
            return await ModSummaryArchive(runner, modPath, ct);
        if (Directory.Exists(modPath))
            return await ModSummaryRedmod(modPath, ct);
        return Err($"Invalid path or unknown type: {modPath} (expected: .archive or REDmod folder)");
    }

    private static async Task<string> ModSummaryArchive(
        Cp77ToolsRunner runner, string archive, CancellationToken ct)
    {
        var (entries, fromCache, _) = await runner.GetArchiveListingAsync(archive, ct);
        if (entries.Count == 0)
            return Err($"Empty listing for: {archive}");

        var byExt = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var nonRed = 0;
        foreach (var e in entries)
        {
            var ext = Path.GetExtension(e).TrimStart('.').ToLowerInvariant();
            if (string.IsNullOrEmpty(ext)) ext = "(no-ext)";
            byExt[ext] = byExt.GetValueOrDefault(ext) + 1;
            if (!RedEngineExtensions.Contains(ext) && ext != "(no-ext)")
                nonRed++;
        }
        var top = byExt.OrderByDescending(kv => kv.Value).ToDictionary(kv => kv.Key, kv => kv.Value);

        return JsonSerializer.Serialize(new
        {
            ok = true,
            status = "success",
            summary = $"Mod (archive): {Path.GetFileName(archive)} — " +
                      $"{entries.Count} file(s)" +
                      (nonRed > 0 ? $" ({nonRed} non-REDengine)" : ""),
            produced = Array.Empty<string>(),
            warnings = nonRed > 0
                ? new[] { $"{nonRed} non-REDengine file(s) — ignored by the game." }
                : Array.Empty<string>(),
            errors = Array.Empty<string>(),
            kind = "archive",
            path = archive,
            fileCount = entries.Count,
            fileCountsByExtension = top,
            fromCache,
        }, JsonOpts);
    }

    private static async Task<string> ModSummaryRedmod(string folder, CancellationToken ct)
    {
        var infoPath = Path.Combine(folder, "info.json");
        if (!File.Exists(infoPath))
            return Err($"Not a REDmod: info.json missing at the root of {folder}");

        string? name = null, version = null, description = null;
        try
        {
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(infoPath, ct));
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                if (doc.RootElement.TryGetProperty("name", out var n)) name = n.GetString();
                if (doc.RootElement.TryGetProperty("version", out var v)) version = v.GetString();
                if (doc.RootElement.TryGetProperty("description", out var d)) description = d.GetString();
            }
        }
        catch { /* malformed info.json: continue */ }

        var archives = ListRelative(folder, "archives", new[] { ".archive" });
        var scripts = ListRelative(folder, "scripts",
            new[] { ".reds", ".script", ".swift", ".redscript" });
        var tweaksFiles = ListRelative(folder, "tweaks",
            new[] { ".tweak", ".yaml", ".yml" });
        var customSounds = ListRelative(folder, "customSounds", Array.Empty<string>());

        // Extract top-level keys from each .tweak (fast line-by-line read).
        var tweakKeys = new List<string>();
        foreach (var rel in tweaksFiles)
        {
            var full = Path.Combine(folder, rel);
            try
            {
                foreach (var rawLine in await File.ReadAllLinesAsync(full, ct))
                {
                    var line = rawLine.TrimEnd('\r');
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    if (char.IsWhiteSpace(line[0])) continue;
                    var idx = line.IndexOf(':');
                    if (idx > 0)
                    {
                        var key = line[..idx].Trim();
                        if (!string.IsNullOrEmpty(key)) tweakKeys.Add(key);
                    }
                }
            }
            catch { /* unreadable file: continue */ }
        }

        // Parse declarations in each .reds.
        var scriptDecls = new List<object>();
        foreach (var rel in scripts)
        {
            var full = Path.Combine(folder, rel);
            try
            {
                var text = await File.ReadAllTextAsync(full, ct);
                var (decls, _) = ScanScriptDeclarations(text);
                foreach (var d in decls)
                    scriptDecls.Add(new { file = rel, kind = d.Kind, name = d.Name, line = d.Line });
            }
            catch { /* unreadable file: continue */ }
        }

        return JsonSerializer.Serialize(new
        {
            ok = true,
            status = "success",
            summary = $"REDmod: {name ?? Path.GetFileName(folder)}" +
                      (version is not null ? $" v{version}" : "") +
                      $" — {archives.Count} archive(s), {scripts.Count} script(s), " +
                      $"{tweaksFiles.Count} tweak(s), {customSounds.Count} sound(s)",
            produced = Array.Empty<string>(),
            warnings = Array.Empty<string>(),
            errors = Array.Empty<string>(),
            kind = "redmod",
            path = folder,
            name,
            version,
            description,
            fileCounts = new
            {
                archives = archives.Count,
                scripts = scripts.Count,
                tweaks = tweaksFiles.Count,
                customSounds = customSounds.Count,
            },
            archives,
            scripts,
            tweaks = tweaksFiles,
            customSounds,
            tweakKeys,
            scriptDeclarations = scriptDecls,
        }, JsonOpts);
    }

    [McpServerTool(Name = "dump_records", ReadOnly = false, Destructive = false, Idempotent = true)]
    [Description("Exports all TweakDB records of a given type to JSON Lines (.jsonl) or " +
                 "CSV — for balance analysis in a spreadsheet. Ex. recordType=" +
                 "\"gamedataWeaponItem_Record\" produces a table of all weapons with " +
                 "their flats (damage, attacksPerSecond, etc.).")]
    public static async Task<string> DumpRecords(
        Cp77ToolsRunner runner,
        [Description("Path of the tweakdb.bin file (typically <game>/r6/cache/tweakdb.bin).")] string tweakdbPath,
        [Description("Full name of the record CLR type (ex. gamedataWeaponItem_Record).")] string recordType,
        [Description("Path of the output file (.jsonl or .csv depending on format).")] string outputFile,
        [Description("Format: jsonl (default, 1 record per line) or csv (superset of columns).")] string format = "jsonl",
        CancellationToken ct = default)
    {
        if (!File.Exists(tweakdbPath))
            return Err($"tweakdb.bin file not found: {tweakdbPath}");
        if (string.IsNullOrWhiteSpace(recordType))
            return Err("recordType empty.");
        format = format.ToLowerInvariant();
        if (format is not ("jsonl" or "csv"))
            return Err($"Unknown format: {format} (jsonl | csv).");

        Directory.CreateDirectory(Path.GetDirectoryName(outputFile) ?? ".");
        var r = await runner.RunAsync(
            new[] { "tweakdb-dump-records", tweakdbPath, recordType, outputFile, format }, ct);
        var produced = File.Exists(outputFile)
            ? new List<string> { outputFile }
            : new List<string>();
        return Structured(
            $"Dump records {recordType} → {outputFile} (format {format})", r, produced);
    }

    [McpServerTool(Name = "launch_game", ReadOnly = false, Destructive = true, Idempotent = false)]
    [Description("⚠ Launches Cyberpunk 2077: runs <game>/bin/x64/Cyberpunk2077.exe (visible " +
                 "action that is hard to cancel — the game really starts). If " +
                 "deployRedmod=true (default), runs redMod.exe deploy first. " +
                 "The game is launched detached; this tool does not block waiting.")]
    public static async Task<string> LaunchGame(
        [Description("Root folder of the Cyberpunk 2077 installation.")] string gamePath,
        [Description("Runs redMod.exe deploy before starting the game (recommended if REDmods/.tweak modified).")] bool deployRedmod = true,
        [Description("Additional arguments to pass to Cyberpunk2077.exe (rare).")] string? extraArgs = null,
        CancellationToken ct = default)
    {
        if (!Directory.Exists(gamePath))
            return Err($"Game folder not found: {gamePath}");
        var exe = Path.Combine(gamePath, "bin", "x64", "Cyberpunk2077.exe");
        if (!File.Exists(exe))
            return Err($"Cyberpunk2077.exe not found: {exe}");

        string? deploySummary = null;
        var warnings = new List<string>
        {
            "Game is actually being launched — visible action that is hard to cancel.",
        };
        if (deployRedmod)
        {
            var deployResult = await DeployRedmod(gamePath, ct);
            using var doc = JsonDocument.Parse(deployResult);
            var root = doc.RootElement;
            if (!root.GetProperty("ok").GetBoolean())
            {
                // We flag it but launch anyway — the agent can decide.
                warnings.Add("Deploy REDmod failed: "
                    + root.GetProperty("summary").GetString());
            }
            else
            {
                deploySummary = root.GetProperty("summary").GetString();
            }
        }

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
        };
        if (!string.IsNullOrWhiteSpace(extraArgs)) psi.Arguments = extraArgs;

        Process? proc;
        try { proc = Process.Start(psi); }
        catch (Exception ex) { return Err($"Launch failed: {ex.Message}"); }
        if (proc is null) return Err($"Process.Start returned null for: {exe}");

        return JsonSerializer.Serialize(new
        {
            ok = true,
            status = "success",
            summary = $"Cyberpunk 2077 launched (PID {proc.Id})",
            produced = Array.Empty<string>(),
            warnings,
            errors = Array.Empty<string>(),
            gameExe = exe,
            pid = proc.Id,
            deploySummary,
        }, JsonOpts);
    }

    [McpServerTool(Name = "tail_game_logs", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("Reads the tail of the Cyberpunk 2077 logs. log = game (r6/logs/*.log except redscript) | " +
                 "redmod (tools/redmod/logs/*.log) | redscript (r6/logs/*redscript*.log) | all. " +
                 "Returns the last N lines after an optional filter (case-insensitive substring).")]
    public static string TailGameLogs(
        [Description("Root folder of the Cyberpunk 2077 installation.")] string gamePath,
        [Description("Log category: game | redmod | redscript | all.")] string log = "game",
        [Description("Number of lines to return (default 200).")] int lines = 200,
        [Description("Substring filter (case-insensitive) applied before the tail.")] string? filter = null)
    {
        if (!Directory.Exists(gamePath))
            return Err($"Game folder not found: {gamePath}");
        if (lines < 1) lines = 200;

        var logFiles = ResolveLogFiles(gamePath, log);
        if (logFiles.Count == 0)
        {
            return JsonSerializer.Serialize(new
            {
                ok = true,
                status = "partial",
                summary = $"No log found for category: {log}",
                produced = Array.Empty<string>(),
                warnings = new[] { $"No log file in category '{log}' " +
                                   $"(the game may never have been launched)." },
                errors = Array.Empty<string>(),
                logFiles = Array.Empty<string>(),
                lineCount = 0,
                content = "",
            }, JsonOpts);
        }

        var all = new List<string>();
        foreach (var f in logFiles)
        {
            all.Add($"=== {Path.GetFileName(f)} ===");
            try
            {
                foreach (var line in File.ReadLines(f))
                    all.Add(line);
            }
            catch (IOException ex)
            {
                all.Add($"(unable to read: {ex.Message})");
            }
        }

        if (!string.IsNullOrWhiteSpace(filter))
        {
            var f = filter;
            all = all.Where(l =>
                l.StartsWith("===", StringComparison.Ordinal) ||
                l.Contains(f, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        var tail = all.Skip(Math.Max(0, all.Count - lines)).ToList();
        return JsonSerializer.Serialize(new
        {
            ok = true,
            status = "success",
            summary = $"Tail logs ({log}) — {tail.Count} line(s) of {all.Count}",
            produced = Array.Empty<string>(),
            warnings = Array.Empty<string>(),
            errors = Array.Empty<string>(),
            logFiles,
            lineCount = tail.Count,
            content = string.Join("\n", tail),
        }, JsonOpts);
    }

    [McpServerTool(Name = "uninstall_mod", ReadOnly = false, Destructive = true, Idempotent = true)]
    [Description("Uninstalls a mod: removes a .archive archive from <game>/archive/pc/mod/. " +
                 "Accepts an absolute path OR just the file name (resolved on the game side). " +
                 "Refuses to delete a file outside the mod folder (safeguard).")]
    public static string UninstallMod(
        [Description("Absolute path of the .archive OR just its name (ex. mymod.archive).")] string archivePathOrName,
        [Description("Root folder of the Cyberpunk 2077 installation.")] string gamePath)
    {
        if (!Directory.Exists(gamePath))
            return Err($"Game folder not found: {gamePath}");
        var modsDir = Path.Combine(gamePath, "archive", "pc", "mod");
        if (!Directory.Exists(modsDir))
            return Err($"Mod folder absent: {modsDir}");

        var target = Path.IsPathRooted(archivePathOrName)
            ? archivePathOrName
            : Path.Combine(modsDir, archivePathOrName);
        if (!File.Exists(target))
            return Err($"Archive not found: {target}");
        // Safeguard: the target must be INSIDE modsDir.
        var full = Path.GetFullPath(target);
        var modsDirFull = Path.GetFullPath(modsDir) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(modsDirFull, StringComparison.OrdinalIgnoreCase))
            return Err($"Refused: {full} is not under {modsDir}.");

        File.Delete(target);
        return JsonSerializer.Serialize(new
        {
            ok = true,
            status = "success",
            summary = $"Mod uninstalled: {Path.GetFileName(target)}",
            produced = Array.Empty<string>(),
            warnings = Array.Empty<string>(),
            errors = Array.Empty<string>(),
            removedPath = target,
        }, JsonOpts);
    }

    [McpServerTool(Name = "uninstall_redmod", ReadOnly = false, Destructive = true, Idempotent = true)]
    [Description("Uninstalls a REDmod: recursively deletes <game>/mods/<modName>/. " +
                 "Safeguard: refuses to delete outside the mods/ folder.")]
    public static string UninstallRedmod(
        [Description("Name of the REDmod (the subfolder under mods/).")] string modName,
        [Description("Root folder of the Cyberpunk 2077 installation.")] string gamePath)
    {
        if (!Directory.Exists(gamePath))
            return Err($"Game folder not found: {gamePath}");
        if (string.IsNullOrWhiteSpace(modName)
            || modName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return Err($"Invalid REDmod name: {modName}");

        var modsRoot = Path.Combine(gamePath, "mods");
        if (!Directory.Exists(modsRoot))
            return Err($"REDmod folder absent: {modsRoot}");

        var dir = Path.Combine(modsRoot, modName);
        if (!Directory.Exists(dir))
            return Err($"REDmod not found: {dir}");
        var full = Path.GetFullPath(dir);
        var modsRootFull = Path.GetFullPath(modsRoot) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(modsRootFull, StringComparison.OrdinalIgnoreCase))
            return Err($"Refused: {full} is not under {modsRoot}.");

        Directory.Delete(dir, recursive: true);
        return JsonSerializer.Serialize(new
        {
            ok = true,
            status = "success",
            summary = $"REDmod uninstalled: {modName}",
            produced = Array.Empty<string>(),
            warnings = Array.Empty<string>(),
            errors = Array.Empty<string>(),
            removedPath = dir,
        }, JsonOpts);
    }

    [McpServerTool(Name = "uninstall_tweak", ReadOnly = false, Destructive = true, Idempotent = true)]
    [Description("Uninstalls a .tweak: deletes <game>/r6/tweaks/<tweakName>. " +
                 "Safeguard: refuses to delete outside the r6/tweaks/ folder.")]
    public static string UninstallTweak(
        [Description("Name of the .tweak file (ex. mytweak.tweak).")] string tweakName,
        [Description("Root folder of the Cyberpunk 2077 installation.")] string gamePath)
    {
        if (!Directory.Exists(gamePath))
            return Err($"Game folder not found: {gamePath}");
        if (string.IsNullOrWhiteSpace(tweakName)
            || tweakName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return Err($"Invalid .tweak name: {tweakName}");

        var tweaksDir = Path.Combine(gamePath, "r6", "tweaks");
        if (!Directory.Exists(tweaksDir))
            return Err($"Tweaks folder absent: {tweaksDir}");

        var target = Path.Combine(tweaksDir, tweakName);
        if (!File.Exists(target))
            return Err($"Tweak not found: {target}");
        var full = Path.GetFullPath(target);
        var tweaksDirFull = Path.GetFullPath(tweaksDir) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(tweaksDirFull, StringComparison.OrdinalIgnoreCase))
            return Err($"Refused: {full} is not under {tweaksDir}.");

        File.Delete(target);
        return JsonSerializer.Serialize(new
        {
            ok = true,
            status = "success",
            summary = $".tweak uninstalled: {tweakName}",
            produced = Array.Empty<string>(),
            warnings = Array.Empty<string>(),
            errors = Array.Empty<string>(),
            removedPath = target,
        }, JsonOpts);
    }

    [McpServerTool(Name = "deploy_redmod", ReadOnly = false, Destructive = false, Idempotent = true)]
    [Description("Runs <game>/tools/redmod/bin/redMod.exe deploy — the official step to " +
                 "activate the installed REDmods (compiles their scripts + applies their " +
                 "tweaks). To be run after install_redmod / install_tweak before playing.")]
    public static async Task<string> DeployRedmod(
        [Description("Root folder of the Cyberpunk 2077 installation.")] string gamePath,
        CancellationToken ct = default)
    {
        if (!Directory.Exists(gamePath))
            return Err($"Game folder not found: {gamePath}");
        var exe = Path.Combine(gamePath, "tools", "redmod", "bin", "redMod.exe");
        if (!File.Exists(exe))
            return Err($"redMod.exe not found: {exe}. " +
                       "REDmod must be installed via the launcher (Cyberpunk 2077 > REDmod DLC).");

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
        };
        psi.ArgumentList.Add("deploy");
        psi.ArgumentList.Add("-root=" + gamePath);

        using var proc = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        try { proc.Start(); }
        catch (Exception ex) { return Err($"Failed to launch redMod.exe: {ex.Message}"); }
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromMinutes(5));
        try { await proc.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* already dead */ }
            return Structured($"Deploy REDmod interrupted (>5 min): {gamePath}",
                new CliResult(-1, stdout.ToString(), stderr.ToString(), true));
        }
        proc.WaitForExit();

        var r = new CliResult(proc.ExitCode, stdout.ToString(), stderr.ToString(), false);
        return Structured($"Deploy REDmod: {gamePath} (exit={proc.ExitCode})", r);
    }

    [McpServerTool(Name = "install_mod", ReadOnly = false, Destructive = true, Idempotent = true)]
    [Description("Installs a mod: copies a .archive archive (produced by pack_archive) into " +
                 "the archive/pc/mod folder of the Cyberpunk 2077 installation — final step of " +
                 "the modding loop. The mod is active at the next launch of the game.")]
    public static string InstallMod(
        [Description("Path of the .archive archive of the mod to install.")] string archivePath,
        [Description("Root folder of the Cyberpunk 2077 installation.")] string gamePath)
    {
        if (!File.Exists(archivePath))
            return Err($"Mod archive not found: {archivePath}");
        if (!archivePath.EndsWith(".archive", StringComparison.OrdinalIgnoreCase))
            return Err($"The file is not a .archive archive: {archivePath}");
        if (!Directory.Exists(gamePath))
            return Err($"Game folder not found: {gamePath}");

        var modDir = Path.Combine(gamePath, "archive", "pc", "mod");
        Directory.CreateDirectory(modDir);
        var dest = Path.Combine(modDir, Path.GetFileName(archivePath));
        var existed = File.Exists(dest);
        File.Copy(archivePath, dest, overwrite: true);

        var result = new
        {
            ok = true,
            status = "success",
            summary = (existed ? "Mod reinstalled (archive replaced): " : "Mod installed: ") + dest,
            produced = new[] { dest },
            warnings = existed
                ? new[] { $"An archive of the same name already existed and was replaced." }
                : Array.Empty<string>(),
            errors = Array.Empty<string>(),
        };
        return JsonSerializer.Serialize(result, JsonOpts);
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    /// <summary>Cap of the JSON content returned inline by read_game_file
    /// (beyond it, the content is truncated and only jsonFile gives the full file).</summary>
    private const int ReadContentCap = 50_000;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Set of files present (recursively) under a folder.</summary>
    private static HashSet<string> Snapshot(string dir)
        => Directory.Exists(dir)
            ? new HashSet<string>(Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            : new HashSet<string>();

    /// <summary>Files that appeared in <paramref name="dir"/> since the snapshot
    /// <paramref name="before"/> — paths relative to <paramref name="dir"/>, sorted.</summary>
    private static List<string> ProducedIn(string dir, HashSet<string> before)
    {
        var after = Snapshot(dir);
        after.ExceptWith(before);
        return after.Select(p => Path.GetRelativePath(dir, p)).OrderBy(p => p).ToList();
    }

    /// <summary>Pattern "snapshot before / op / diff after" used by all the
    /// tools that write files into an output folder: creates the folder,
    /// snapshots, runs the operation, judges success on the files actually
    /// produced (cf. <see cref="Structured"/>). When <paramref name="verbose"/>
    /// is <c>true</c>, the returned log is not truncated.</summary>
    private static async Task<string> WithSnapshot(
        string outputPath, string summary, Func<Task<CliResult>> op,
        bool verbose = false)
    {
        Directory.CreateDirectory(outputPath);
        var before = Snapshot(outputPath);
        var r = await op();
        return Structured(summary, r, ProducedIn(outputPath, before), verbose);
    }

    private static readonly Regex TweakHeaderRegex = new(
        @"(?<r>\d+)(?<rp>\+?) record\(s\), (?<f>\d+)(?<fp>\+?) flat\(s\)",
        RegexOptions.Compiled);

    /// <summary>Simple glob: converts <c>*</c>/<c>?</c> to regex, matches
    /// the entire string (case-insensitive). Sufficient for the patterns of
    /// file searches (ex. <c>*.mesh</c>, <c>*player*.ent</c>).</summary>
    internal static bool MatchesGlob(string path, string pattern)
    {
        var rx = "^" + Regex.Escape(pattern)
            .Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
        return Regex.IsMatch(path, rx, RegexOptions.IgnoreCase);
    }

    // ── Inspection helpers (traversal of CR2W JSON trees) ──────────────

    private sealed record MeshStats(
        int LodCount, int SubMeshCount, int MaterialCount, int BoneCount,
        List<string> MaterialNames, List<string> BoneNames);

    /// <summary>Extracts the aggregates of a CR2W mesh serialized to JSON. Traverses
    /// the tree recursively looking for <c>renderLODs</c>, <c>chunkMaterials</c>,
    /// <c>materialEntries</c>, <c>boneNames</c> — without depending on the exact path.</summary>
    private static MeshStats ScanMeshStats(JsonElement root)
    {
        int lods = 0, subMeshes = 0, materials = 0, bones = 0;
        var matNames = new List<string>();
        var boneNames = new List<string>();

        WalkJson(root, (name, el) =>
        {
            switch (name)
            {
                case "renderLODs":
                    if (el.ValueKind == JsonValueKind.Array && lods == 0)
                        lods = el.GetArrayLength();
                    break;
                case "materialEntries":
                    if (el.ValueKind == JsonValueKind.Array && materials == 0)
                    {
                        materials = el.GetArrayLength();
                        foreach (var m in el.EnumerateArray())
                        {
                            var n = GetCNameValueAt(m, "name") ?? GetCNameValueAt(m, "Name");
                            if (n is not null) matNames.Add(n);
                        }
                    }
                    break;
                case "boneNames":
                    if (el.ValueKind == JsonValueKind.Array && bones == 0)
                    {
                        bones = el.GetArrayLength();
                        foreach (var b in el.EnumerateArray())
                        {
                            var s = ReadCName(b);
                            if (s is not null) boneNames.Add(s);
                        }
                    }
                    break;
                case "chunkMaterials":
                    if (el.ValueKind == JsonValueKind.Array)
                        subMeshes += el.GetArrayLength();
                    break;
            }
        });
        return new MeshStats(lods, subMeshes, materials, bones, matNames, boneNames);
    }

    private sealed record TextureProps(
        int Width, int Height, string? Format, string? Compression,
        int MipLevels, string? TextureGroup);

    /// <summary>Extracts the metadata of a .xbm CR2W texture serialized to JSON.</summary>
    private static TextureProps ScanTextureProps(JsonElement root)
    {
        int w = 0, h = 0, mips = 0;
        string? format = null, compression = null, group = null;

        WalkJson(root, (name, el) =>
        {
            switch (name)
            {
                case "width":
                    if (el.ValueKind == JsonValueKind.Number && w == 0)
                        w = el.GetInt32();
                    break;
                case "height":
                    if (el.ValueKind == JsonValueKind.Number && h == 0)
                        h = el.GetInt32();
                    break;
                case "rawFormat":
                case "format":
                    if (format is null) format = ReadEnumString(el);
                    break;
                case "compression":
                    if (compression is null) compression = ReadEnumString(el);
                    break;
                case "mipMapInfo":
                    if (el.ValueKind == JsonValueKind.Array && mips == 0)
                        mips = el.GetArrayLength();
                    break;
                case "numMipLevels":
                case "mipLevels":
                    if (el.ValueKind == JsonValueKind.Number && mips == 0)
                        mips = el.GetInt32();
                    break;
                case "textureGroup":
                    if (group is null) group = ReadCName(el);
                    break;
            }
        });
        return new TextureProps(w, h, format, compression, mips, group);
    }

    /// <summary>Recursive traversal of a JSON tree; <paramref name="visit"/> is
    /// called for each (property name, value).</summary>
    private static void WalkJson(JsonElement el, Action<string, JsonElement> visit)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in el.EnumerateObject())
                {
                    visit(prop.Name, prop.Value);
                    WalkJson(prop.Value, visit);
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in el.EnumerateArray())
                    WalkJson(item, visit);
                break;
        }
    }

    /// <summary>Reads a CName WolvenKit, which can be serialized as a raw string,
    /// as object <c>{ "$value": "Name" }</c>, or as object <c>{ "Value": "Name" }</c>.</summary>
    private static string? ReadCName(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.String) return el.GetString();
        if (el.ValueKind != JsonValueKind.Object) return null;
        if (el.TryGetProperty("$value", out var v) && v.ValueKind == JsonValueKind.String)
            return v.GetString();
        if (el.TryGetProperty("Value", out var v2) && v2.ValueKind == JsonValueKind.String)
            return v2.GetString();
        return null;
    }

    private static string? GetCNameValueAt(JsonElement el, string property)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        if (el.TryGetProperty(property, out var v)) return ReadCName(v);
        // Often wrapped in { "Data": { ...property... } }
        if (el.TryGetProperty("Data", out var data) && data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty(property, out var v2))
            return ReadCName(v2);
        return null;
    }

    /// <summary>Reads an enum serialized as a string or as a WolvenKit object (enum CNames).</summary>
    private static string? ReadEnumString(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.String) return el.GetString();
        if (el.ValueKind == JsonValueKind.Number) return el.GetRawText();
        return ReadCName(el);
    }

    // ── Script helpers (lint + textual analysis .reds) ─────────────────

    internal sealed record ScriptDeclaration(string Kind, string Name, int Line);

    private static readonly Regex ScriptDeclRegex = new(
        @"^\s*(?:@(addMethod|addField|wrapMethod|replaceMethod)\([^)]*\)\s*)?" +
        @"(?:public|private|protected|static|native|final|abstract|persistent)?\s*" +
        @"(?<kind>func|class|struct|enum|module|import)\s+(?<name>[A-Za-z_][\w.<>]*)",
        RegexOptions.Compiled | RegexOptions.Multiline);

    internal static (List<ScriptDeclaration> declarations, string? moduleName) ScanScriptDeclarations(string content)
    {
        var decls = new List<ScriptDeclaration>();
        string? moduleName = null;
        var lines = content.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var m = ScriptDeclRegex.Match(lines[i]);
            if (!m.Success) continue;
            var kind = m.Groups["kind"].Value;
            var name = m.Groups["name"].Value;
            if (kind == "module" && moduleName is null)
                moduleName = name;
            decls.Add(new ScriptDeclaration(kind, name, i + 1));
        }
        return (decls, moduleName);
    }

    internal static List<string> LintScriptTextually(string content)
    {
        var issues = new List<string>();
        int braces = 0, parens = 0, brackets = 0;
        int braceLine = 0, parenLine = 0, bracketLine = 0;
        int line = 1;
        var inString = false;
        var inChar = false;
        var inLineComment = false;
        var inBlockComment = false;

        for (var i = 0; i < content.Length; i++)
        {
            var c = content[i];
            var next = i + 1 < content.Length ? content[i + 1] : '\0';

            if (c == '\n')
            {
                line++;
                inLineComment = false;
                continue;
            }
            if (inLineComment) continue;
            if (inBlockComment)
            {
                if (c == '*' && next == '/') { inBlockComment = false; i++; }
                continue;
            }
            if (inString)
            {
                if (c == '\\' && next != '\0') { i++; continue; }
                if (c == '"') inString = false;
                continue;
            }
            if (inChar)
            {
                if (c == '\\' && next != '\0') { i++; continue; }
                if (c == '\'') inChar = false;
                continue;
            }

            if (c == '/' && next == '/') { inLineComment = true; i++; continue; }
            if (c == '/' && next == '*') { inBlockComment = true; i++; continue; }
            if (c == '"') { inString = true; continue; }
            if (c == '\'') { inChar = true; continue; }

            switch (c)
            {
                case '{': if (braces == 0) braceLine = line; braces++; break;
                case '}': braces--; if (braces < 0) { issues.Add($"ERROR L{line}: '}}' without matching '{{'"); braces = 0; } break;
                case '(': if (parens == 0) parenLine = line; parens++; break;
                case ')': parens--; if (parens < 0) { issues.Add($"ERROR L{line}: ')' without matching '('"); parens = 0; } break;
                case '[': if (brackets == 0) bracketLine = line; brackets++; break;
                case ']': brackets--; if (brackets < 0) { issues.Add($"ERROR L{line}: ']' without matching '['"); brackets = 0; } break;
            }
        }

        if (braces > 0) issues.Add($"ERROR: {braces} unclosed brace(s) '{{' (opened around L{braceLine})");
        if (parens > 0) issues.Add($"ERROR: {parens} unclosed paren(s) '(' (opened around L{parenLine})");
        if (brackets > 0) issues.Add($"ERROR: {brackets} unclosed bracket(s) '[' (opened around L{bracketLine})");
        if (inString) issues.Add($"ERROR: unclosed quote \" (line {line})");
        if (inChar) issues.Add($"ERROR: unclosed quote ' (line {line})");
        if (inBlockComment) issues.Add($"ERROR: unclosed comment /* */ (line {line})");

        return issues;
    }

    private static readonly Regex AnnotationRegex = new(
        @"^\s*@(?<ann>addMethod|wrapMethod|replaceMethod|addField|replaceGlobal)\s*\((?<arg>[^)]*)\)",
        RegexOptions.Compiled);

    private static readonly Regex FuncStartRegex = new(
        @"^\s*(?:public|private|protected|static|native|final|cb|exec)?\s*(?:func|let|const)\s",
        RegexOptions.Compiled);

    /// <summary>Light semantic analysis (textual but REDscript-aware):
    /// well-placed and targeted annotations, <c>@wrapMethod</c> calling
    /// <c>wrappedMethod</c>, duplicate declarations. Returns warnings
    /// prefixed "WARN" (never "ERROR": this is not a full parser).</summary>
    internal static List<string> LintScriptSemantics(string content)
    {
        var issues = new List<string>();
        var lines = content.Split('\n');

        // Duplicate declarations of the same nature.
        var (decls, _) = ScanScriptDeclarations(content);
        foreach (var g in decls
                     .Where(d => d.Kind is "func" or "class" or "struct" or "enum")
                     .GroupBy(d => (d.Kind, d.Name))
                     .Where(g => g.Count() > 1))
            issues.Add($"WARN: {g.Key.Kind} '{g.Key.Name}' declared {g.Count()} times " +
                       $"(lines {string.Join(", ", g.Select(x => x.Line))})");

        for (var i = 0; i < lines.Length; i++)
        {
            var m = AnnotationRegex.Match(lines[i]);
            if (!m.Success) continue;
            var ann = m.Groups["ann"].Value;
            var arg = m.Groups["arg"].Value.Trim();

            // The annotation must target a class (except replaceGlobal).
            if (ann != "replaceGlobal" && arg.Length == 0)
                issues.Add($"WARN L{i + 1}: @{ann} without target class — expected @{ann}(ClassName).");

            // The next declaration (excluding empty lines / comments) must be a func/field.
            var j = i + 1;
            while (j < lines.Length)
            {
                var t = lines[j].Trim();
                if (t.Length == 0 || t.StartsWith("//") || t.StartsWith("@")) { j++; continue; }
                break;
            }
            if (j >= lines.Length || !FuncStartRegex.IsMatch(lines[j]))
                issues.Add($"WARN L{i + 1}: @{ann} is not followed by a func/let declaration.");
            else if (ann == "wrapMethod")
            {
                // The body of a @wrapMethod must call wrappedMethod(...) otherwise the
                // original chain is broken (very common modding error).
                var body = ExtractBraceBlock(content, lines, j);
                if (body is not null && !body.Contains("wrappedMethod"))
                    issues.Add($"WARN L{j + 1}: @wrapMethod does not call wrappedMethod() — " +
                               "the original method will never run.");
            }
        }
        return issues;
    }

    /// <summary>Extracts the block { ... } that follows the line <paramref name="declLine"/>
    /// (0-based index in <paramref name="lines"/>), by brace balancing.
    /// Returns null if no block found.</summary>
    private static string? ExtractBraceBlock(string content, string[] lines, int declLine)
    {
        // Character position of the start of the declaration line.
        var startChar = 0;
        for (var k = 0; k < declLine && k < lines.Length; k++)
            startChar += lines[k].Length + 1;
        var open = content.IndexOf('{', Math.Min(startChar, content.Length));
        if (open < 0) return null;
        int depth = 0;
        for (var p = open; p < content.Length; p++)
        {
            if (content[p] == '{') depth++;
            else if (content[p] == '}') { depth--; if (depth == 0) return content[open..(p + 1)]; }
        }
        return null;
    }

    /// <summary>Lists the files of a subfolder of a REDmod, filtering by
    /// extensions (or everything if empty), as paths relative to the root folder.</summary>
    private static List<string> ListRelative(string root, string subdir, string[] extensions)
    {
        var dir = Path.Combine(root, subdir);
        if (!Directory.Exists(dir)) return new List<string>();
        var files = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories);
        if (extensions.Length > 0)
        {
            var extSet = new HashSet<string>(extensions, StringComparer.OrdinalIgnoreCase);
            files = files.Where(f => extSet.Contains(Path.GetExtension(f)));
        }
        return files
            .Select(f => Path.GetRelativePath(root, f))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Resolves the log paths according to the category requested by
    /// <c>tail_game_logs</c>. Returns a possibly empty list if the game
    /// has never run or if REDmod is not installed.</summary>
    private static List<string> ResolveLogFiles(string gamePath, string category)
    {
        var paths = new List<string>();
        var r6logs = Path.Combine(gamePath, "r6", "logs");
        var redmodLogs = Path.Combine(gamePath, "tools", "redmod", "logs");

        switch (category.ToLowerInvariant())
        {
            case "game":
                if (Directory.Exists(r6logs))
                    foreach (var f in Directory.GetFiles(r6logs, "*.log"))
                        if (!Path.GetFileName(f).Contains("redscript", StringComparison.OrdinalIgnoreCase))
                            paths.Add(f);
                break;
            case "redmod":
                if (Directory.Exists(redmodLogs))
                    paths.AddRange(Directory.GetFiles(redmodLogs, "*.log"));
                break;
            case "redscript":
                if (Directory.Exists(r6logs))
                    foreach (var f in Directory.GetFiles(r6logs, "*.log"))
                        if (Path.GetFileName(f).Contains("redscript", StringComparison.OrdinalIgnoreCase))
                            paths.Add(f);
                break;
            case "all":
                if (Directory.Exists(r6logs))
                    paths.AddRange(Directory.GetFiles(r6logs, "*.log"));
                if (Directory.Exists(redmodLogs))
                    paths.AddRange(Directory.GetFiles(redmodLogs, "*.log"));
                break;
        }
        return paths;
    }

    /// <summary>Formats a value of any kind into a correct YAML scalar: integers
    /// and floats bare, strings enclosed in double quotes if non-trivial,
    /// booleans as true/false.</summary>
    private static string FormatYamlValue(object? value)
    {
        if (value is null) return "null";
        if (value is bool b) return b ? "true" : "false";
        // Invariant culture: on a fr-FR machine value.ToString() would emit "1,5"
        // for 1.5, producing invalid YAML.
        if (value is IFormattable f && value is long or int or double or float)
            return f.ToString(null, System.Globalization.CultureInfo.InvariantCulture);
        var s = value.ToString() ?? "";
        // Parsable numbers → written bare.
        if (long.TryParse(s, out _) || double.TryParse(s,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out _))
            return s;
        if (s is "true" or "false" or "null") return s;
        // Quoted string to prevent a ":" or a "#" from disrupting the YAML parser.
        return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }

    /// <summary>Recursive copy of a folder (used by install_redmod). Creates
    /// the missing subfolders and overwrites the existing files at the destination.</summary>
    private static void CopyDirectoryRecursive(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), overwrite: true);
        foreach (var dir in Directory.GetDirectories(source))
            CopyDirectoryRecursive(dir, Path.Combine(dest, Path.GetFileName(dir)));
    }

    /// <summary>Canonical list of REDengine extensions accepted by Cyberpunk 2077,
    /// extracted from <c>WolvenKit.RED4.Archive.ERedExtension</c> (137 values, excluding
    /// "unknown"). An archive containing a file with an unlisted extension will see
    /// that file silently ignored by the engine.</summary>
    private static readonly HashSet<string> RedEngineExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "acousticdata", "actionanimdb", "aiarch", "animgraph", "anims", "app", "archetypes", "areas",
        "audio_metadata", "audiovehcurveset", "behavior", "bikecurveset", "bk2", "bnk", "camcurveset", "ccstate",
        "cfoliage", "charcustpreset", "chromaset", "cminimap", "community", "conversations", "cooked_mlsetup", "cookedanims",
        "cookedapp", "cookedprefab", "credits", "csv", "cubemap", "curveresset", "curveset", "dat",
        "devices", "dlc_manifest", "dtex", "effect", "ent", "env", "envparam", "envprobe",
        "es", "facialcustom", "facialsetup", "fb2tl", "fnt", "folbrush", "foldest", "fp",
        "game", "gamedef", "garmentlayerparams", "genericanimdb", "geometry_cache", "gidata", "gradient", "hitrepresentation",
        "hp", "ies", "inkanim", "inkatlas", "inkcharcustomization", "inkenginesettings", "inkfontfamily", "inkfullscreencomposition",
        "inkgamesettings", "inkhud", "inklayers", "inkmenu", "inkshapecollection", "inkstyle", "inktypography", "inkwidget",
        "interaction", "journal", "journaldesc", "json", "lane_connections", "lane_polygons", "lane_spots", "lights",
        "lipmap", "location", "locopaths", "loot", "mappins", "matlib", "mesh", "mi",
        "mlmask", "mlsetup", "mltemplate", "morphtarget", "mt", "null_areas", "opusinfo", "opuspak",
        "particle", "phys", "physicalscene", "physmatlib", "poimappins", "psrep", "quest", "questphase",
        "redphysics", "regionset", "remt", "reps", "reslist", "rig", "scene", "scenerid",
        "scenesversions", "smartobject", "smartobjects", "sp", "spatial_representation", "streamingblock", "streamingquerydata", "streamingsector",
        "streamingsector_inplace", "streamingworld", "terrainsetup", "texarray", "traffic_collisions", "traffic_persistent", "vehcommoncurveset", "vehcurveset",
        "voicetags", "w2mesh", "w2mi", "wdyn", "wem", "workspot", "worldlist", "xbm",
        "xcube",
    };

    /// <summary>Locates in the daemon log the header "N(+)? record(s), M(+)? flat(s)"
    /// — the "+" indicates that the cap (100) was reached and that there was more.</summary>
    private static (bool records, bool flats) ParseTweakHeader(string log)
    {
        foreach (var line in log.Split('\n'))
        {
            var m = TweakHeaderRegex.Match(line);
            if (m.Success)
                return (m.Groups["rp"].Value == "+", m.Groups["fp"].Value == "+");
        }
        return (false, false);
    }

    /// <summary>
    /// Formats the result of a cp77tools call into a structured JSON object.
    ///
    /// <paramref name="produced"/>: for a tool that writes files, the list
    /// of those it produced (the reliable success signal); <c>null</c> for an
    /// information tool (success judged on the exit code).
    ///
    /// <paramref name="verbose"/>: if <c>true</c>, the log is not truncated (for
    /// debug — potentially very large output).
    /// </summary>
    private static string Structured(string summary, CliResult r,
                                     IReadOnlyList<string>? produced = null,
                                     bool verbose = false)
    {
        var log = (r.Stdout + r.Stderr).Trim();
        var warnings = LogLines(log, "Warning");
        var errors = LogLines(log, "Error");

        string status;
        if (r.TimedOut)
            status = "timeout";
        else if (produced is not null)
            // File-producing tool: success is judged on the actual output.
            status = produced.Count > 0 ? (errors.Count > 0 ? "partial" : "success") : "error";
        else
            // Information tool: no file expected, we rely on the exit code.
            status = r.ExitCode != 0
                     || log.Contains("Daemon error", StringComparison.Ordinal)
                     || log.Contains("Unhandled", StringComparison.OrdinalIgnoreCase)
                ? "error" : "success";

        var result = new
        {
            ok = status is "success" or "partial",
            status,
            summary,
            produced = produced ?? (IReadOnlyList<string>)Array.Empty<string>(),
            warnings,
            errors,
            exitCode = r.ExitCode,
            log = verbose ? log : Truncate(log, 12_000),
        };
        return JsonSerializer.Serialize(result, JsonOpts);
    }

    /// <summary>JSON result for a cancelled/timed-out call — same shape as Err so
    /// the agent never sees a raw OperationCanceledException leak out of a tool.</summary>
    private static string Cancelled(string op)
        => JsonSerializer.Serialize(new
        {
            ok = false,
            status = "cancelled",
            summary = $"{op} was cancelled or timed out before completion.",
            produced = Array.Empty<string>(),
            warnings = Array.Empty<string>(),
            errors = new[] { "cancelled" },
            exitCode = -1,
            log = "",
        }, JsonOpts);

    /// <summary>JSON failure result — for argument validation errors.</summary>
    private static string Err(string summary)
        => JsonSerializer.Serialize(new
        {
            ok = false,
            status = "error",
            summary,
            produced = Array.Empty<string>(),
            warnings = Array.Empty<string>(),
            errors = new[] { summary },
            exitCode = -1,
            log = "",
        }, JsonOpts);

    /// <summary>Extracts the messages from the log lines of a given level
    /// ("[ 0: Warning ] - message" → "message").</summary>
    private static List<string> LogLines(string log, string level)
    {
        var marker = $": {level} ]";
        var lines = new List<string>();
        foreach (var raw in log.Split('\n'))
        {
            if (!raw.Contains(marker, StringComparison.Ordinal))
                continue;
            var i = raw.IndexOf("] - ", StringComparison.Ordinal);
            lines.Add((i >= 0 ? raw[(i + 4)..] : raw).Trim());
        }
        return lines;
    }

    /// <summary>Truncates a large log while preserving the useful context:
    /// the first 20 lines (which contextualize), the error lines in the
    /// middle (the most valuable info), and the last 20 lines (final
    /// result). If still too large, fallback to truncating the start of the string.</summary>
    internal static string Truncate(string s, int max)
    {
        if (s.Length <= max) return s;

        var lines = s.Split('\n');
        // Too short for a split into sections: simple fallback.
        if (lines.Length <= 40)
            return "…(start of output truncated)…\n" + s[^max..];

        const int head = 20, tail = 20;
        var midErrors = new List<string>();
        for (var i = head; i < lines.Length - tail; i++)
        {
            if (lines[i].Contains(": Error ]", StringComparison.Ordinal))
                midErrors.Add(lines[i]);
        }

        var sb = new StringBuilder();
        for (var i = 0; i < head; i++) sb.AppendLine(lines[i].TrimEnd('\r'));
        var omitted = lines.Length - head - tail;
        sb.Append("…(middle: ").Append(omitted).Append(" line(s) omitted");
        if (midErrors.Count > 0) sb.Append(", ").Append(midErrors.Count).Append(" error(s) preserved");
        sb.AppendLine(")…");
        foreach (var e in midErrors) sb.AppendLine(e.TrimEnd('\r'));
        if (midErrors.Count > 0) sb.AppendLine("…");
        for (var i = lines.Length - tail; i < lines.Length; i++) sb.AppendLine(lines[i].TrimEnd('\r'));

        var result = sb.ToString().TrimEnd();
        return result.Length <= max ? result : "…(start of output truncated)…\n" + result[^max..];
    }
}
