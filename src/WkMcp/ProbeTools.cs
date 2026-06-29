using System.ComponentModel;
using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;

namespace WkMcp;

/// <summary>
/// <b>game_probe</b> — a single liveness + readiness probe for a modded Cyberpunk 2077.
///
/// The other diagnostics are fragmented: <see cref="ModdingTools.DiagnoseLogs"/> reads
/// the on-disk logs, <see cref="ModdingTools.ModDoctor"/> checks frameworks/conflicts,
/// <see cref="LiveTools.LiveStatus"/> reports bridge connectivity. None answers, in one
/// call: <i>is the game running, is it healthy, and what is wrong right now?</i>
///
/// This tool correlates all of it — process liveness, crash signals, log diagnosis
/// (reusing the <see cref="ModdingTools"/> knowledge base), setup health, bridge status —
/// and, when the game is running with the CETBridge mod, an in-game live probe (RTTI
/// canaries + runtime snapshot that no log file can show) — into one prioritized verdict.
///
/// Works whether the game is off (offline correlation) or running (adds the live section).
/// </summary>
[McpServerToolType]
public static class ProbeTools
{
    internal static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static string Err(string summary) => JsonSerializer.Serialize(new
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

    // ── Crash-signal knowledge base ───────────────────────────────────────────
    // Markers that strongly indicate a real crash (kept tight to avoid false
    // positives on benign "error"/"failed" lines that diagnose_logs already covers).
    private sealed record CrashMarker(string Pattern, string Label, string Hint);
    private static readonly CrashMarker[] CrashKb =
    {
        new("EXCEPTION_ACCESS_VIOLATION",
            "EXCEPTION_ACCESS_VIOLATION",
            "Access violation — a native RED4ext plugin likely faulted. The last plugin loaded in red4ext.log is the prime suspect; update or remove it."),
        new("Unhandled exception",
            "unhandled exception",
            "Unhandled native exception — usually a RED4ext plugin incompatible with the current game version. Check the surrounding stack."),
        new(@"has crashed|application crashed|game crashed|the game has stopped",
            "crash report",
            "A crash was logged — inspect the surrounding lines for the faulting module."),
        new("Fatal [Ee]rror|FATAL",
            "fatal error",
            "A fatal error was logged — inspect the surrounding lines to identify the offending module."),
    };

    /// <summary>Matches a single log line against the crash knowledge base (testable).</summary>
    internal static (string marker, string hint)? ClassifyCrashText(string text)
    {
        foreach (var c in CrashKb)
            if (Regex.IsMatch(text, c.Pattern, RegexOptions.IgnoreCase))
                return (c.Label, c.Hint);
        return null;
    }

    internal sealed record CrashSignal(
        string File, int Line, string Marker, string Hint, string LastModified, bool Recent);

    /// <summary>Scans the trailing lines of one log for crash markers (pure / testable).</summary>
    internal static IEnumerable<CrashSignal> ScanTextForCrash(
        string file, IReadOnlyList<string> lines, DateTime lastWriteUtc, DateTime nowUtc, int windowMinutes)
    {
        bool recent = (nowUtc - lastWriteUtc).TotalMinutes <= windowMinutes;
        var lastWrite = lastWriteUtc.ToString("u");
        int found = 0;
        for (int i = lines.Count - 1; i >= 0 && found < 10; i--)
        {
            if (ClassifyCrashText(lines[i]) is { } h)
            {
                yield return new CrashSignal(file, i + 1, h.marker, h.hint, lastWrite, recent);
                found++;
            }
        }
    }

    /// <summary>Scans the crash-prone logs + minidumps under the game root.</summary>
    internal static List<CrashSignal> ScanCrashSignals(string gamePath, int windowMinutes, DateTime nowUtc)
    {
        var signals = new List<CrashSignal>();

        var candidates = new List<string>();
        foreach (var dir in new[] { @"r6\logs", @"red4ext\logs" })
        {
            var d = Path.Combine(gamePath, dir);
            if (Directory.Exists(d))
                try { candidates.AddRange(Directory.EnumerateFiles(d, "*.log")); } catch { }
        }
        var cetLog = Path.Combine(gamePath, @"bin\x64\plugins\cyber_engine_tweaks\cyber_engine_tweaks.log");
        if (File.Exists(cetLog)) candidates.Add(cetLog);
        candidates = candidates.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        foreach (var f in candidates)
        {
            DateTime mtime; string[] lines;
            try { mtime = File.GetLastWriteTimeUtc(f); lines = File.ReadAllLines(f); } catch { continue; }
            signals.AddRange(ScanTextForCrash(f, lines, mtime, nowUtc, windowMinutes));
            if (signals.Count >= 50) break;
        }

        // Minidumps: their presence means the game wrote a crash dump.
        foreach (var dir in new[] { gamePath, Path.Combine(gamePath, "bin", "x64") })
        {
            if (!Directory.Exists(dir)) continue;
            IEnumerable<string> dumps;
            try { dumps = Directory.EnumerateFiles(dir, "*.dmp"); } catch { continue; }
            foreach (var dmp in dumps)
            {
                DateTime mtime; try { mtime = File.GetLastWriteTimeUtc(dmp); } catch { continue; }
                bool recent = (nowUtc - mtime).TotalMinutes <= windowMinutes;
                signals.Add(new CrashSignal(dmp, 0, "crash minidump (.dmp)",
                    "A crash minidump exists — the game crashed and wrote a dump. Open it (or clear it) to confirm the timing.",
                    mtime.ToString("u"), recent));
            }
        }
        return signals;
    }

    // ── Game process detection ────────────────────────────────────────────────
    private static readonly string[] GameProcessNames = { "Cyberpunk2077" };

    internal static (bool running, int? pid, string? startUtc) DetectGameProcess()
    {
        try
        {
            var procs = GameProcessNames.SelectMany(Process.GetProcessesByName).ToArray();
            try
            {
                var p = procs.FirstOrDefault();
                if (p == null) return (false, null, null);
                string? start = null;
                try { start = p.StartTime.ToUniversalTime().ToString("u"); } catch { /* access denied */ }
                return (true, p.Id, start);
            }
            finally { foreach (var p in procs) p.Dispose(); }
        }
        catch { return (false, null, null); }
    }

    // ── Live probe parsing (result of the CET `probe` handler) ─────────────────
    internal sealed record LiveCanary(string Name, bool Ok, string Detail);
    internal sealed record LiveFramework(string Name, bool Loaded, string? Version);
    internal sealed record LiveProbe(
        bool PlayerLoaded, int CanariesOk, int CanariesTotal,
        IReadOnlyList<LiveCanary> Canaries, IReadOnlyList<LiveFramework> Frameworks);

    /// <summary>Tolerantly parses the JSON returned by the in-game `probe` handler.</summary>
    internal static LiveProbe? ParseLiveProbe(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            bool playerLoaded = root.TryGetProperty("playerLoaded", out var pl) && pl.ValueKind == JsonValueKind.True;
            int okC = root.TryGetProperty("canariesOk", out var ok) && ok.TryGetInt32(out var oi) ? oi : 0;
            int totC = root.TryGetProperty("canariesTotal", out var tt) && tt.TryGetInt32(out var ti) ? ti : 0;

            var canaries = new List<LiveCanary>();
            if (root.TryGetProperty("canaries", out var cs) && cs.ValueKind == JsonValueKind.Array)
                foreach (var c in cs.EnumerateArray())
                    canaries.Add(new LiveCanary(
                        c.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                        c.TryGetProperty("ok", out var co) && co.ValueKind == JsonValueKind.True,
                        c.TryGetProperty("detail", out var d) ? d.GetString() ?? "" : ""));

            var fws = new List<LiveFramework>();
            if (root.TryGetProperty("frameworks", out var fs) && fs.ValueKind == JsonValueKind.Array)
                foreach (var f in fs.EnumerateArray())
                    fws.Add(new LiveFramework(
                        f.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                        f.TryGetProperty("loaded", out var l) && l.ValueKind == JsonValueKind.True,
                        f.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null));

            return new LiveProbe(playerLoaded, okC, totC, canaries, fws);
        }
        catch { return null; }
    }

    // ── Verdict synthesis ─────────────────────────────────────────────────────
    internal sealed record ProbeIssue(string Severity, string Area, string Detail, string Action);
    internal sealed record Verdict(string State, IReadOnlyList<ProbeIssue> Issues);

    /// <summary>Synthesizes a prioritized verdict from the gathered sections (pure / testable).
    /// State ∈ healthy | degraded | broken (worst severity present).</summary>
    internal static Verdict BuildVerdict(
        bool gameRunning, bool bridgeConnected,
        ModdingTools.LogDiagnosis logs, ModdingTools.DoctorReport doctor,
        IReadOnlyList<CrashSignal> crashSignals, LiveProbe? live)
    {
        var issues = new List<ProbeIssue>();

        // Missing frameworks — the #1 cause of a modded crash.
        foreach (var dep in doctor.MissingDependencies)
            issues.Add(new("high", "dependency",
                $"Required framework not installed: {dep}.", $"Install {dep}."));

        // Known log diagnoses already carry a concrete fix.
        foreach (var (problem, fix) in logs.Diagnoses)
            issues.Add(new("high", "logs", problem, fix));

        // Recent crash signals (within the window).
        foreach (var s in crashSignals.Where(s => s.Recent).Take(5))
            issues.Add(new("high", "crash",
                $"Crash signal in {Path.GetFileName(s.File)} ({s.LastModified}): {s.Marker}.", s.Hint));

        // Error lines with no known-error match → softer.
        if (logs.TotalErrors > 0 && logs.Diagnoses.Count == 0)
            issues.Add(new("medium", "logs",
                $"{logs.TotalErrors} error line(s) across {logs.LogsFound} log(s) with no known-error match.",
                "Inspect them with diagnose_logs / tail_game_logs."));

        // Archive conflicts.
        if (doctor.ConflictCount is > 0)
            issues.Add(new("medium", "conflicts",
                $"{doctor.ConflictCount} archive conflict(s).",
                "Check load order with analyze_conflicts; bisect with toggle_mods."));

        // Live runtime breakage — only meaningful with a save loaded (canaries are
        // expected to fail at the main menu, so we never flag that as broken).
        if (live is { PlayerLoaded: true })
        {
            var failed = live.Canaries.Where(c => !c.Ok).ToList();
            if (failed.Count > 0)
                issues.Add(new("high", "runtime",
                    $"In-game runtime canaries failed ({failed.Count}/{live.CanariesTotal}): " +
                    string.Join(", ", failed.Select(c => c.Name)) + ".",
                    "The script runtime looks broken despite a loaded save — check redscript/RED4ext compilation (diagnose_logs)."));
        }

        var rank = new Dictionary<string, int> { ["high"] = 0, ["medium"] = 1, ["info"] = 2 };
        var ordered = issues.OrderBy(i => rank.GetValueOrDefault(i.Severity, 3)).ToList();
        var state = ordered.Any(i => i.Severity == "high") ? "broken"
                  : ordered.Any(i => i.Severity == "medium") ? "degraded"
                  : "healthy";
        return new Verdict(state, ordered);
    }

    // ── The tool ──────────────────────────────────────────────────────────────
    [McpServerTool(Name = "game_probe", ReadOnly = true, Destructive = false, Idempotent = true)]
    [Description("One-call health probe for a modded Cyberpunk 2077: correlates game-process " +
                 "liveness, crash signals (logs + minidumps), on-disk log diagnosis (known-error " +
                 "database), setup health (frameworks/conflicts/dependencies) and bridge " +
                 "connectivity — and, when the game is running with the CETBridge mod, an in-game " +
                 "live probe (RTTI canary checks + runtime snapshot that no log file can show) — " +
                 "into a single prioritized verdict with next actions. Works game on or off. " +
                 "Start here to debug 'why is my game/mod broken' instead of chaining mod_doctor + " +
                 "diagnose_logs + live_status by hand.")]
    public static async Task<string> GameProbe(
        CetBridge bridge,
        Cp77ToolsRunner runner,
        [Description("Cyberpunk 2077 installation root folder.")] string gamePath,
        [Description("Also run the in-game live probe when the bridge is connected (canary checks + " +
                     "runtime snapshot). Default true.")] bool includeLive = true,
        [Description("Crash signals from files modified within this many minutes are flagged as " +
                     "'recent' (default 60).")] int recentWindowMinutes = 60,
        CancellationToken ct = default)
    {
        if (!Directory.Exists(gamePath))
            return Err($"Game folder not found: {gamePath}");

        // 1) Liveness — process + bridge.
        var (gameRunning, pid, startUtc) = DetectGameProcess();
        var bs = bridge.StatusSnapshot(gamePath);

        // 2) Logs + 3) setup (reuse the existing cores; no duplication).
        var logs = ModdingTools.DiagnoseLogsCore(gamePath);
        var doctor = await ModdingTools.ModDoctorCore(gamePath, runner, ct);

        // 4) Crash signals.
        var crashSignals = ScanCrashSignals(gamePath, recentWindowMinutes, DateTime.UtcNow);

        // 5) Live in-game probe (only when connected and requested).
        LiveProbe? liveTyped = null;
        object? liveSection = null;
        if (includeLive && bs.Connected)
        {
            var sw = Stopwatch.StartNew();
            var r = await bridge.QueryAsync("probe", new { }, gamePath, ct);
            sw.Stop();
            if (r.Ok && r.Result != null)
            {
                liveTyped = ParseLiveProbe(r.Result);
                try
                {
                    using var jd = JsonDocument.Parse(r.Result);
                    var el = jd.RootElement.Clone(); // Clone is safe to use after the document is disposed.
                    liveSection = new { connected = true, transport = r.Transport, latencyMs = sw.ElapsedMilliseconds, probe = el };
                }
                catch
                {
                    liveSection = new { connected = true, transport = r.Transport, latencyMs = sw.ElapsedMilliseconds, error = "could not parse live probe result", raw = r.Result };
                }
            }
            else
            {
                liveSection = new { connected = true, transport = r.Transport, error = r.Error ?? "no result", timedOut = r.TimedOut };
            }
        }

        // 6) Verdict.
        var verdict = BuildVerdict(gameRunning, bs.Connected, logs, doctor, crashSignals, liveTyped);
        var suspectedCrash = crashSignals.Any(s => s.Recent);

        var warnings = new List<string>();
        if (verdict.State != "healthy")
            warnings.Add($"Probe verdict: {verdict.State} — {verdict.Issues.Count} issue(s).");

        return JsonSerializer.Serialize(new
        {
            ok = true,
            status = verdict.State == "healthy" ? "success" : "partial",
            summary = $"Probe: {verdict.State} — game {(gameRunning ? "running" : "not running")}, " +
                      $"bridge {(bs.Connected ? $"connected ({bs.Transport})" : "offline")}; " +
                      $"{verdict.Issues.Count} issue(s)" + (suspectedCrash ? ", recent crash suspected" : "") + ".",
            gamePath,
            verdict = new
            {
                state = verdict.State,
                gameRunning,
                bridgeConnected = bs.Connected,
                issues = verdict.Issues.Select(i => new { severity = i.Severity, area = i.Area, detail = i.Detail, action = i.Action }),
            },
            liveness = new
            {
                gameRunning,
                pid,
                processStartUtc = startUtc,
                bridge = new
                {
                    connected = bs.Connected,
                    transport = bs.Transport,
                    tcpListening = bs.TcpListening,
                    tcpPort = bs.TcpPort,
                    lastHeartbeat = bs.LastHeartbeatUtc,
                    bridgeDir = bs.BridgeDir,
                },
            },
            logs = new
            {
                logsFound = logs.LogsFound,
                totalErrors = logs.TotalErrors,
                sources = logs.Sources.Select(s => new
                {
                    source = s.Source,
                    logPath = s.LogPath,
                    errorCount = s.ErrorCount,
                    lastModified = s.LastModified,
                    errors = s.Errors,
                }),
                diagnoses = logs.Diagnoses.Select(x => new { problem = x.Problem, fix = x.Fix }),
            },
            setup = new
            {
                installedFrameworks = doctor.InstalledFrameworks,
                missingDependencies = doctor.MissingDependencies,
                conflictCount = doctor.ConflictCount,
                conflictNote = doctor.ConflictNote,
                mods = new { archiveMods = doctor.ArchiveMods, redMods = doctor.RedMods },
                recommendations = doctor.Recommendations,
            },
            crash = new
            {
                suspectedRecent = suspectedCrash,
                windowMinutes = recentWindowMinutes,
                signals = crashSignals.Select(s => new
                {
                    file = s.File,
                    line = s.Line,
                    marker = s.Marker,
                    hint = s.Hint,
                    lastModified = s.LastModified,
                    recent = s.Recent,
                }),
            },
            live = liveSection,
            warnings,
            errors = Array.Empty<string>(),
        }, JsonOpts);
    }
}
