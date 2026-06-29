using WkMcp;
using Xunit;

namespace WkMcp.Tests;

/// <summary>
/// Pure-helper tests for game_probe: crash classification/scanning, verdict
/// synthesis (severity ordering + the player-loaded gate on live canaries), and
/// tolerant parsing of the in-game probe result. No game, daemon, or bridge.
/// </summary>
public class ProbeToolsTests
{
    // ── Crash classification ──────────────────────────────────────────────
    [Theory]
    [InlineData("[error] EXCEPTION_ACCESS_VIOLATION at 0x7ff...", true)]
    [InlineData("Unhandled exception while loading plugin Foo.dll", true)]
    [InlineData("Fatal Error: could not initialize RED4ext", true)]
    [InlineData("the game has stopped working", true)]
    [InlineData("[info] ArchiveXL loaded 12 archives", false)]
    [InlineData("warning: deprecated API used", false)]
    public void ClassifyCrashText_matches_only_real_crash_markers(string line, bool expected)
        => Assert.Equal(expected, ProbeTools.ClassifyCrashText(line) is not null);

    [Fact]
    public void ScanTextForCrash_reports_line_and_recency()
    {
        var now = DateTime.UtcNow;
        var lines = new[]
        {
            "[info] boot",
            "[error] EXCEPTION_ACCESS_VIOLATION at 0xdead",
            "[info] shutdown",
        };
        // Modified 5 min ago, window 60 → recent.
        var recent = ProbeTools.ScanTextForCrash("red4ext.log", lines, now.AddMinutes(-5), now, 60).ToList();
        Assert.Single(recent);
        Assert.Equal(2, recent[0].Line);
        Assert.True(recent[0].Recent);

        // Modified 2 h ago, window 60 → not recent (still surfaced as a signal).
        var old = ProbeTools.ScanTextForCrash("red4ext.log", lines, now.AddHours(-2), now, 60).ToList();
        Assert.Single(old);
        Assert.False(old[0].Recent);
    }

    [Fact]
    public void ScanTextForCrash_ignores_clean_logs()
    {
        var now = DateTime.UtcNow;
        var lines = new[] { "[info] ok", "warning: minor", "[info] done" };
        Assert.Empty(ProbeTools.ScanTextForCrash("x.log", lines, now, now, 60));
    }

    // ── Verdict synthesis ─────────────────────────────────────────────────
    private static ModdingTools.LogDiagnosis CleanLogs()
        => new(0, 0, new List<ModdingTools.LogSourceResult>(), new List<(string, string)>());

    private static ModdingTools.DoctorReport CleanDoctor()
        => new(new List<string>(), new List<string>(), new List<string>(),
               null, "", 0, 0, new List<string>());

    [Fact]
    public void Verdict_is_healthy_when_nothing_wrong()
    {
        var v = ProbeTools.BuildVerdict(false, false, CleanLogs(), CleanDoctor(),
            new List<ProbeTools.CrashSignal>(), null);
        Assert.Equal("healthy", v.State);
        Assert.Empty(v.Issues);
    }

    [Fact]
    public void Missing_dependency_makes_it_broken()
    {
        var doctor = CleanDoctor() with { MissingDependencies = new[] { "Codeware" } };
        var v = ProbeTools.BuildVerdict(false, false, CleanLogs(), doctor,
            new List<ProbeTools.CrashSignal>(), null);
        Assert.Equal("broken", v.State);
        Assert.Contains(v.Issues, i => i.Severity == "high" && i.Area == "dependency");
    }

    [Fact]
    public void Conflicts_only_make_it_degraded()
    {
        var doctor = CleanDoctor() with { ConflictCount = 3 };
        var v = ProbeTools.BuildVerdict(true, false, CleanLogs(), doctor,
            new List<ProbeTools.CrashSignal>(), null);
        Assert.Equal("degraded", v.State);
    }

    [Fact]
    public void Recent_crash_signal_is_high_but_old_one_is_not_flagged()
    {
        var recent = new[] { new ProbeTools.CrashSignal("red4ext.log", 2, "EXCEPTION_ACCESS_VIOLATION", "hint", "2026-06-22", true) };
        var vRecent = ProbeTools.BuildVerdict(false, false, CleanLogs(), CleanDoctor(), recent, null);
        Assert.Equal("broken", vRecent.State);

        var old = new[] { recent[0] with { Recent = false } };
        var vOld = ProbeTools.BuildVerdict(false, false, CleanLogs(), CleanDoctor(), old, null);
        Assert.Equal("healthy", vOld.State); // surfaced in the report, but not escalated
    }

    [Fact]
    public void Live_canary_failure_breaks_only_when_a_save_is_loaded()
    {
        var failing = new List<ProbeTools.LiveCanary>
        {
            new("player_present", false, "nil"),
            new("stats_system", false, "nil"),
        };

        var loaded = new ProbeTools.LiveProbe(true, 0, 2, failing, new List<ProbeTools.LiveFramework>());
        var vLoaded = ProbeTools.BuildVerdict(true, true, CleanLogs(), CleanDoctor(),
            new List<ProbeTools.CrashSignal>(), loaded);
        Assert.Equal("broken", vLoaded.State);
        Assert.Contains(vLoaded.Issues, i => i.Area == "runtime");

        // Same failures at the main menu (playerLoaded=false) are EXPECTED, not a fault.
        var atMenu = loaded with { PlayerLoaded = false };
        var vMenu = ProbeTools.BuildVerdict(true, true, CleanLogs(), CleanDoctor(),
            new List<ProbeTools.CrashSignal>(), atMenu);
        Assert.Equal("healthy", vMenu.State);
    }

    [Fact]
    public void Issues_are_ordered_high_before_medium()
    {
        var logs = new ModdingTools.LogDiagnosis(1, 5, new List<ModdingTools.LogSourceResult>(),
            new List<(string, string)>()); // 5 errors, no known diagnosis → medium
        var doctor = CleanDoctor() with { MissingDependencies = new[] { "ArchiveXL" } }; // high
        var v = ProbeTools.BuildVerdict(false, false, logs, doctor,
            new List<ProbeTools.CrashSignal>(), null);
        Assert.Equal("broken", v.State);
        Assert.Equal("high", v.Issues[0].Severity);
    }

    // ── Live probe parsing ────────────────────────────────────────────────
    [Fact]
    public void ParseLiveProbe_reads_canaries_and_frameworks()
    {
        const string json = """
        {
          "playerLoaded": true,
          "canariesOk": 3,
          "canariesTotal": 4,
          "canaries": [
            {"name":"player_present","ok":true,"detail":"player entity present"},
            {"name":"tweakdb_lookup","ok":false,"detail":"nil"}
          ],
          "frameworks": [
            {"name":"Codeware","loaded":true,"version":"1.2.3"},
            {"name":"AppearanceMenuMod","loaded":false,"version":null}
          ],
          "snapshot": {"gameState":{"time":"12:00"}}
        }
        """;
        var p = ProbeTools.ParseLiveProbe(json);
        Assert.NotNull(p);
        Assert.True(p!.PlayerLoaded);
        Assert.Equal(3, p.CanariesOk);
        Assert.Equal(4, p.CanariesTotal);
        Assert.Equal(2, p.Canaries.Count);
        Assert.False(p.Canaries[1].Ok);
        Assert.Equal("Codeware", p.Frameworks[0].Name);
        Assert.Equal("1.2.3", p.Frameworks[0].Version);
        Assert.Null(p.Frameworks[1].Version);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("[1,2,3]")]
    public void ParseLiveProbe_is_tolerant_of_garbage(string json)
        => Assert.Null(ProbeTools.ParseLiveProbe(json));
}
