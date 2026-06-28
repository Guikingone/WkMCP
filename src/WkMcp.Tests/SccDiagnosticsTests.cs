using System.Linq;
using WkMcp;
using Xunit;

namespace WkMcp.Tests;

// R2 — scc output parsing. Samples are taken verbatim from the scc that ships
// with the game (see dev verification run), including the space in the game
// path ("Cyberpunk 2077") and the mixed / \ separators.

public class SccDiagnosticsTests
{
    private const string ErrorBlock = """
        [ERROR - Sat, 20 Jun 2026 20:56:38 +0200] [UNRESOLVED_REF] At C:/Cyberpunk/Cyberpunk 2077/r6\scripts\Atone\Atone_yml.reds:4:1:
        @addMethod(TweakDBManager)
        ^^^^^^^^^^^^^^^^^^^^^^^^^^
        unresolved reference 'TweakDBManager'

        """;

    private const string WarnBlock = """
        [WARN - Sat, 20 Jun 2026 20:56:38 +0200] At C:/Cyberpunk/Cyberpunk 2077/r6\scripts\Better Armor Tooltip\BetterArmorTooltip.reds:1:1:
        @replaceMethod(RipperDocGameController)
        ^^^
        this method replacement overwrites a previous annotation targeting the same method, only one replacement per method can be active at a time

        """;

    [Fact]
    public void Parses_an_error_block_with_code_spaced_path_and_message()
    {
        var d = Assert.Single(SccDiagnostics.Parse(ErrorBlock));
        Assert.Equal("error", d.Severity);
        Assert.Equal("UNRESOLVED_REF", d.Code);
        Assert.EndsWith("Atone_yml.reds", d.File);       // path with a space survived
        Assert.Equal(4, d.Line);
        Assert.Equal(1, d.Col);
        Assert.Equal("unresolved reference 'TweakDBManager'", d.Message);
    }

    [Fact]
    public void Parses_a_warning_block_without_a_code()
    {
        var d = Assert.Single(SccDiagnostics.Parse(WarnBlock));
        Assert.Equal("warning", d.Severity);              // "WARN" normalised, not dropped
        Assert.Null(d.Code);
        Assert.EndsWith("BetterArmorTooltip.reds", d.File);
        Assert.Equal(1, d.Line);
        Assert.StartsWith("this method replacement overwrites", d.Message);
    }

    [Fact]
    public void Captures_the_final_failure_summary_line_with_no_location()
    {
        const string log = "[ERROR - Sat, 20 Jun 2026 20:56:42 +0200] REDScript compilation has failed.";
        var d = Assert.Single(SccDiagnostics.Parse(log));
        Assert.Equal("error", d.Severity);
        Assert.Null(d.File);
        Assert.Equal("REDScript compilation has failed.", d.Message);
    }

    [Fact]
    public void Counts_severities_across_a_mixed_log_and_skips_info_and_file_listing()
    {
        const string log = """
            [INFO - ts] Compiling files in C:/Cyberpunk/Cyberpunk 2077/r6\scripts:
            SomeMod\NoWarningsHere.reds
            [WARN - ts] At C:/g/a.reds:1:1:
            ^
            a warning

            [ERROR - ts] [UNRESOLVED_TYPE] At C:/g/b.reds:7:5:
            ^
            unresolved type 'Foo'

            [ERROR - ts] REDScript compilation has failed.
            """;
        var diags = SccDiagnostics.Parse(log);
        Assert.Equal(2, SccDiagnostics.Count(diags, "error"));
        Assert.Equal(1, SccDiagnostics.Count(diags, "warning"));
        // "NoWarningsHere.reds" in a listing line must NOT be picked up as a diagnostic.
        Assert.DoesNotContain(diags, d => d.File is not null && d.File.Contains("NoWarningsHere"));
    }

    [Theory]
    [InlineData("Expected <-compile>, pass --help for usage information", true)]
    [InlineData("Expected <SCRIPT_PATH>, pass --help for usage information", true)]
    [InlineData("[ERROR - ts] [UNRESOLVED_REF] At C:/g/a.reds:4:1:\nunresolved reference 'X'", false)]
    public void Detects_a_cli_usage_error_distinct_from_script_errors(string output, bool expected)
        => Assert.Equal(expected, SccDiagnostics.LooksLikeUsageError(output));
}
