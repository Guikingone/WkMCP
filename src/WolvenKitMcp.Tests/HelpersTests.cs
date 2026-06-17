using System.Linq;
using System.Text.Json;
using System.Xml.Linq;
using WolvenKitMcp;
using Xunit;

namespace WolvenKitMcp.Tests;

public class TruncateTests
{
    [Fact]
    public void ShortStringIsReturnedUnchanged()
    {
        const string s = "ligne courte";
        Assert.Equal(s, WolvenKitTools.Truncate(s, 1000));
    }

    [Fact]
    public void LongMultilineKeepsHeadAndTail()
    {
        var lines = Enumerable.Range(1, 100).Select(i => $"application diagnostic log line number {i}");
        var input = string.Join("\n", lines);
        // max large enough to keep the sectioned format (head + omitted middle + tail).
        var result = WolvenKitTools.Truncate(input, 2000);

        Assert.True(result.Length < input.Length); // actually truncated
        Assert.Contains("application diagnostic log line number 1", result);   // head preserved
        Assert.Contains("application diagnostic log line number 100", result); // tail preserved
        Assert.Contains("omitted", result);              // omitted-section marker
    }

    [Fact]
    public void MidErrorsArePreserved()
    {
        var lines = Enumerable.Range(1, 100)
            .Select(i => i == 50 ? "[ 2026 : Error ] boom" : $"ligne {i}");
        var input = string.Join("\n", lines);
        var result = WolvenKitTools.Truncate(input, 500);
        Assert.Contains("boom", result); // the error in the middle is kept
    }
}

public class MatchesGlobTests
{
    [Theory]
    [InlineData("foo.mesh", "*.mesh", true)]
    [InlineData("foo.xbm", "*.mesh", false)]
    [InlineData("base/x/foo.ent", "*.ent", true)]
    [InlineData("weapon_01.mesh", "weapon_??.mesh", true)]
    [InlineData("weapon_1.mesh", "weapon_??.mesh", false)]
    public void Matches(string path, string pattern, bool expected)
        => Assert.Equal(expected, WolvenKitTools.MatchesGlob(path, pattern));
}

public class CpmodprojTests
{
    [Fact]
    public void ProducesWellFormedXmlWithRequiredName()
    {
        var xml = WolvenKitTools.BuildCpmodprojXml("MyMod", author: "Me",
            version: "2.1.0", description: "desc");
        var doc = XDocument.Parse(xml); // throws if malformed

        Assert.Equal("CP77Mod", doc.Root!.Name.LocalName);
        Assert.Equal("MyMod", doc.Root.Element("Name")!.Value);
        Assert.Equal("MyMod", doc.Root.Element("ModName")!.Value);
        Assert.Equal("Me", doc.Root.Element("Author")!.Value);
        Assert.Equal("2.1.0", doc.Root.Element("Version")!.Value);
    }

    [Fact]
    public void DefaultsVersionWhenMissing()
    {
        var xml = WolvenKitTools.BuildCpmodprojXml("M", null, null, null);
        var doc = XDocument.Parse(xml);
        Assert.Equal("1.0.0", doc.Root!.Element("Version")!.Value);
    }

    [Fact]
    public void EscapesSpecialCharacters()
    {
        var xml = WolvenKitTools.BuildCpmodprojXml("A & B <mod>", null, null, "d\"e");
        var doc = XDocument.Parse(xml); // must not break the XML
        Assert.Equal("A & B <mod>", doc.Root!.Element("Name")!.Value);
    }
}

public class ScriptLintTests
{
    [Fact]
    public void BalancedScriptHasNoTextualErrors()
    {
        const string src = "module Foo\nfunc Bar() {\n  let x = 1;\n}\n";
        Assert.Empty(WolvenKitTools.LintScriptTextually(src));
    }

    [Fact]
    public void UnbalancedBracesAreReported()
    {
        const string src = "func Bar() {\n  let x = 1;\n";
        var issues = WolvenKitTools.LintScriptTextually(src);
        Assert.Contains(issues, i => i.StartsWith("ERROR"));
    }

    [Fact]
    public void BracesInStringsAreIgnored()
    {
        const string src = "func Bar() {\n  let s = \"} not a brace {\";\n}\n";
        Assert.Empty(WolvenKitTools.LintScriptTextually(src));
    }

    [Fact]
    public void WrapMethodWithoutWrappedMethodWarns()
    {
        const string src =
            "@wrapMethod(PlayerPuppet)\nfunc OnGameAttached() {\n  let y = 2;\n}\n";
        var issues = WolvenKitTools.LintScriptSemantics(src);
        Assert.Contains(issues, i => i.Contains("wrappedMethod"));
    }

    [Fact]
    public void WrapMethodCallingWrappedMethodIsClean()
    {
        const string src =
            "@wrapMethod(PlayerPuppet)\nfunc OnGameAttached() {\n  wrappedMethod();\n}\n";
        var issues = WolvenKitTools.LintScriptSemantics(src);
        Assert.DoesNotContain(issues, i => i.Contains("wrappedMethod"));
    }

    [Fact]
    public void AnnotationWithoutTargetClassWarns()
    {
        const string src = "@addMethod()\nfunc Extra() {\n}\n";
        var issues = WolvenKitTools.LintScriptSemantics(src);
        Assert.Contains(issues, i => i.Contains("target class"));
    }

    [Fact]
    public void DuplicateFunctionIsReported()
    {
        const string src = "func Dup() {\n}\nfunc Dup() {\n}\n";
        var issues = WolvenKitTools.LintScriptSemantics(src);
        Assert.Contains(issues, i => i.Contains("Dup") && i.Contains("2 times"));
    }

    [Fact]
    public void ScanFindsDeclarations()
    {
        const string src = "module Foo.Bar\nclass Baz {\n  func Qux() {}\n}\n";
        var (decls, module) = WolvenKitTools.ScanScriptDeclarations(src);
        Assert.Equal("Foo.Bar", module);
        Assert.Contains(decls, d => d.Kind == "class" && d.Name == "Baz");
        Assert.Contains(decls, d => d.Kind == "func" && d.Name == "Qux");
    }
}

public class RedscriptParserTests
{
    private static int ErrorCount(string src) =>
        RedscriptParser.Parse(src).Diagnostics.Count(d => d.Severity == "ERROR");

    [Fact]
    public void AcceptsRealisticRedscript()
    {
        // Covers: module, @if-annotated import, abstract class + extends, fields
        // (including array type [T] and sized [T; N]), native func without body,
        // expression-body func "=", if/while/for/switch without parentheses,
        // interpolated string s"\(...)" with nested quotes, contextual keywords.
        const string src = @"
module My.Mod
@if(ModuleExists(""Other""))
import Other.*

native func Log(const text: script_ref<String>) -> Void

public abstract class Foo extends Bar {
  public let id: Uint32;
  public let pts: [Int32];
  private let grid: [Float; 5];
  public let quest: Int32;

  public static func Label() -> String = ""hello""

  public final func Run(opt n: Int32) -> Void {
    let x: array<Int32> = [1, 2, 3];
    if IsDefined(this) && n > 0 {
      LogChannel(n""DEBUG"", s""val=\(ToString(this.GetX(""k"")))*\n"");
    } else {
      x += 1;
    }
    while n > 0 { n -= 1; }
    for item in x { Log(s""\(item)""); }
    switch n {
      case 0: break;
      default: return;
    }
  }
}

enum Color { Red = 0, Green = 1, Blue, }
";
        Assert.Equal(0, ErrorCount(src));
    }

    [Theory]
    [InlineData("func F() { let x = 1;")]                       // unclosed brace
    [InlineData("func F() -> Int32 { return (1 + 2; }")]        // unclosed parenthesis
    [InlineData("class { }")]                                   // missing class name
    [InlineData("func F() { let x = \"unterminated; }")]        // unterminated string
    [InlineData("func F( -> Int32 { }")]                        // malformed signature
    public void DetectsSyntaxErrors(string src)
        => Assert.True(ErrorCount(src) > 0, $"error expected for: {src}");

    [Fact]
    public void ExtractsModuleAndDeclarations()
    {
        var r = RedscriptParser.Parse("module A.B\nclass C { func M() {} let f: Int32; }\nenum E { X }\n");
        Assert.Equal("A.B", r.Module);
        Assert.Contains(r.Declarations, d => d.Kind == "class" && d.Name == "C");
        Assert.Contains(r.Declarations, d => d.Kind == "func" && d.Name == "M");
        Assert.Contains(r.Declarations, d => d.Kind == "enum" && d.Name == "E");
    }

    [Fact]
    public void CapturesImports()
    {
        var r = RedscriptParser.Parse("import Codeware.UI\nimport Audioware.*\nfunc F() {}\n");
        Assert.Contains("Codeware.UI", r.Imports);
        Assert.Contains(r.Imports, i => i.StartsWith("Audioware"));
    }
}

public class JsonDiffTests
{
    [Fact]
    public void IdenticalContentWithDifferingHeaderHasNoDiff()
    {
        // The $.Header (extraction path, timestamp) must be ignored.
        const string b = "{\"Header\":{\"ArchiveFileName\":\"/tmp/a\",\"ExportedDateTime\":\"t1\"},\"Data\":{\"x\":1}}";
        const string m = "{\"Header\":{\"ArchiveFileName\":\"/tmp/b\",\"ExportedDateTime\":\"t2\"},\"Data\":{\"x\":1}}";
        var (added, removed, changed) = ModdingTools.DiffJson(b, m);
        Assert.Empty(added);
        Assert.Empty(removed);
        Assert.Empty(changed);
    }

    [Fact]
    public void DetectsAddedRemovedAndChanged()
    {
        const string b = "{\"Data\":{\"keep\":1,\"old\":2,\"val\":10}}";
        const string m = "{\"Data\":{\"keep\":1,\"new\":3,\"val\":99}}";
        var (added, removed, changed) = ModdingTools.DiffJson(b, m);
        Assert.Contains(added, p => p.EndsWith(".new"));
        Assert.Contains(removed, p => p.EndsWith(".old"));
        Assert.Contains(changed, c => c.Path.EndsWith(".val") && c.Base == "10" && c.Mod == "99");
    }
}

public class JournalTests
{
    // Mini-journal reproducing the real structure (gameJournalResource -> root
    // folder -> first-level folders -> leaf entries).
    private const string Journal = @"{
      ""Data"": { ""RootChunk"": {
        ""$type"": ""gameJournalResource"",
        ""entry"": { ""HandleId"": ""0"", ""Data"": {
          ""$type"": ""gameJournalRootFolderEntry"",
          ""descriptor"": { ""DepotPath"": { ""$value"": ""base\\journal\\descriptor.journaldesc"" } },
          ""entries"": [
            { ""HandleId"": ""1"", ""Data"": { ""$type"": ""gameJournalPrimaryFolderEntry"", ""id"": ""quests"", ""entries"": [
              { ""HandleId"": ""2"", ""Data"": { ""$type"": ""gameJournalQuest"", ""id"": ""holofixer_used_1"", ""title"": { ""value"": """" }, ""entries"": [] } },
              { ""HandleId"": ""3"", ""Data"": { ""$type"": ""gameJournalQuest"", ""id"": ""sq001"", ""title"": { ""value"": ""Side Quest"" }, ""entries"": [] } }
            ] } },
            { ""HandleId"": ""4"", ""Data"": { ""$type"": ""gameJournalPrimaryFolderEntry"", ""id"": ""codex"", ""entries"": [
              { ""HandleId"": ""5"", ""Data"": { ""$type"": ""gameJournalCodexEntry"", ""id"": ""codex_arasaka"", ""entries"": [] } }
            ] } }
          ]
        } }
      } }
    }";

    [Fact]
    public void SummarizeCountsEntriesAndCategories()
    {
        using var doc = JsonDocument.Parse(Journal);
        var s = ModdingTools.SummarizeJournal(doc.RootElement);
        Assert.NotNull(s);
        Assert.Equal(6, s!.TotalEntries);          // root + 2 folders + 3 leaves
        Assert.Equal(2, s.MaxDepth);
        Assert.Equal(2, s.ByType["gameJournalQuest"]);
        Assert.Contains(s.TopLevel, e => e.Id == "quests" && e.ChildCount == 2);
        Assert.Contains(s.TopLevel, e => e.Id == "codex" && e.ChildCount == 1);
        Assert.EndsWith("descriptor.journaldesc", s.Descriptor);
    }

    [Fact]
    public void FindByIdReturnsExactJsonPath()
    {
        using var doc = JsonDocument.Parse(Journal);
        var (matches, _, isJournal) = ModdingTools.FindInJournal(doc.RootElement, "holofixer", "id", 100);
        Assert.True(isJournal);
        var m = Assert.Single(matches);
        Assert.Equal("gameJournalQuest", m.Type);
        Assert.Equal("holofixer_used_1", m.Id);
        Assert.Equal("Data.RootChunk.entry.Data.entries[0].Data.entries[0].Data", m.Path);
    }

    [Fact]
    public void FindByTypeAndTitle()
    {
        using var doc = JsonDocument.Parse(Journal);
        var (byType, _, _) = ModdingTools.FindInJournal(doc.RootElement, "Codex", "type", 100);
        Assert.Contains(byType, e => e.Id == "codex_arasaka");
        var (byTitle, _, _) = ModdingTools.FindInJournal(doc.RootElement, "Side", "title", 100);
        Assert.Contains(byTitle, e => e.Id == "sq001" && e.Title == "Side Quest");
    }

    [Fact]
    public void NonJournalIsRejected()
    {
        using var doc = JsonDocument.Parse("{\"Data\":{\"RootChunk\":{\"$type\":\"CMesh\"}}}");
        Assert.Null(ModdingTools.SummarizeJournal(doc.RootElement));
        var (_, _, isJournal) = ModdingTools.FindInJournal(doc.RootElement, "x", "id", 10);
        Assert.False(isJournal);
    }
}

public class Cr2wNavTests
{
    private const string Cr2w = @"{
      ""Data"": { ""RootChunk"": {
        ""$type"": ""questQuestPhaseResource"",
        ""nodes"": [
          { ""$type"": ""questSceneNodeDefinition"", ""id"": ""5"" },
          { ""$type"": ""questPauseConditionNodeDefinition"", ""sockets"": [
            { ""$type"": ""questSocketDefinition"", ""name"": ""Out"" }
          ] },
          { ""$type"": ""questSceneNodeDefinition"", ""id"": ""9"" }
        ]
      } }
    }";

    [Fact]
    public void SummarizeCountsByType()
    {
        using var doc = JsonDocument.Parse(Cr2w);
        var (rootType, total, maxDepth, byType) = ModdingTools.SummarizeCr2w(doc.RootElement);
        Assert.Equal("questQuestPhaseResource", rootType);
        Assert.Equal(2, byType["questSceneNodeDefinition"]);
        Assert.True(total >= 5);
        Assert.True(maxDepth >= 4);
    }

    [Fact]
    public void FindByTypeReturnsPaths()
    {
        using var doc = JsonDocument.Parse(Cr2w);
        var (matches, _) = ModdingTools.FindInCr2wTree(doc.RootElement, "SceneNode", "$type", 100);
        Assert.Equal(2, matches.Count);
        Assert.Equal("Data.RootChunk.nodes[0]", matches[0].Path);
        Assert.Equal("Data.RootChunk.nodes[2]", matches[1].Path);
    }

    [Fact]
    public void FindByNamedFieldAndWildcard()
    {
        using var doc = JsonDocument.Parse(Cr2w);
        var (byName, _) = ModdingTools.FindInCr2wTree(doc.RootElement, "Out", "name", 100);
        Assert.Single(byName);
        Assert.EndsWith(".name", byName[0].Path);
        var (any, _) = ModdingTools.FindInCr2wTree(doc.RootElement, "Out", "*", 100);
        Assert.NotEmpty(any);
    }
}

public class LogDiagnosisTests
{
    [Theory]
    [InlineData("[ERROR] scc invocation failed with an error")]
    [InlineData("field with this name is already defined: foo")]
    [InlineData("Failed to resolve address for hash 12345")]
    [InlineData("RED4ext error 1114 vcruntime")]
    public void ClassifiesKnownErrors(string line)
    {
        var r = ModdingTools.ClassifyLogText(line);
        Assert.NotNull(r);
        Assert.True(r!.Value.problem.Length > 0 && r.Value.fix.Length > 0);
    }

    [Fact]
    public void UnknownLineIsNotClassified()
        => Assert.Null(ModdingTools.ClassifyLogText("everything is fine, mod loaded ok"));
}

public class ItemModValidationTests
{
    [Fact]
    public void ParsesRecordsCsvAndLocalization()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wk-item-test-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        try
        {
            var yaml = Path.Combine(dir, "item.yaml");
            File.WriteAllText(yaml,
                "Items.my_shirt:\n  $base: Items.GenericClothing\n  entityName: my_shirt_ent\n" +
                "  appearanceName: black\n  displayName: MyMod-Shirt-Name\n");
            var records = ModdingTools.ParseItemRecords(yaml);
            var it = Assert.Single(records);
            Assert.Equal("Items.my_shirt", it.Record);
            Assert.Equal("my_shirt_ent", it.EntityName);
            Assert.Equal("MyMod-Shirt-Name", it.DisplayName);

            var names = ModdingTools.ParseTweakRecordNames(yaml);
            Assert.Contains("Items.my_shirt", names);

            var csv = Path.Combine(dir, "factory.csv");
            File.WriteAllText(csv, "name, path\nmy_shirt_ent, my\\mod\\shirt.ent\n");
            var rows = ModdingTools.ParseFactoryCsv(csv);
            Assert.Contains(rows, r => r.name == "my_shirt_ent" && r.path.EndsWith("shirt.ent"));

            var json = Path.Combine(dir, "loc.json");
            File.WriteAllText(json,
                "{\"onScreenEntries\":[{\"secondaryKey\":\"MyMod-Shirt-Name\",\"femaleVariant\":\"Chemise\"}]}");
            Assert.Contains("MyMod-Shirt-Name", ModdingTools.CollectSecondaryKeys(json));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}

public class TweakLintTests
{
    [Fact]
    public void TabIndentIsError()
    {
        var (errors, _) = ModdingTools.LintTweakText(new[] { "Items.Foo:", "\tmagazineCapacity: 24" });
        Assert.Contains(errors, e => e.Contains("TAB"));
    }

    [Fact]
    public void InlineBaseAndDuplicatesAreWarnings()
    {
        var (_, warnings) = ModdingTools.LintTweakText(new[]
        {
            "Items.Foo:",
            "  $base: Items.inline12",
            "Items.Foo:",
            "  damage: 5",
        });
        Assert.Contains(warnings, w => w.Contains("inline"));
        Assert.Contains(warnings, w => w.Contains("Items.Foo") && w.Contains("2 times"));
    }

    [Fact]
    public void CleanTweakHasNoIssues()
    {
        var (errors, warnings) = ModdingTools.LintTweakText(new[] { "Items.Foo:", "  damage: 5" });
        Assert.Empty(errors);
        Assert.Empty(warnings);
    }
}

public class DynamicAppearanceTests
{
    [Fact]
    public void ExpandsGenderAndCamera()
    {
        var r = ModdingTools.ExpandDynamicPattern(@"*base\item_{gender}_{camera}.mesh");
        Assert.Equal(4, r.Count);
        Assert.Contains(r, x => x.EndsWith(@"item_m_fpp.mesh"));
        Assert.Contains(r, x => x.EndsWith(@"item_w_tpp.mesh"));
    }

    [Fact]
    public void LeavesUnknownPlaceholders()
    {
        var r = ModdingTools.ExpandDynamicPattern(@"*base\item_{gender}_{body}.mesh");
        Assert.Equal(2, r.Count);
        Assert.All(r, x => Assert.Contains("{body}", x));
    }
}

public class AppearanceChainTests
{
    [Fact]
    public void ParsesEntityAppearances()
    {
        const string ent = @"{""Data"":{""RootChunk"":{""$type"":""entEntityTemplate"",""appearances"":[
          {""$type"":""entTemplateAppearance"",
           ""name"":{""$type"":""CName"",""$value"":""inst_club_all_wa_02""},
           ""appearanceName"":{""$type"":""CName"",""$value"":""wa_02""},
           ""appearanceResource"":{""DepotPath"":{""$value"":""base\\x\\inst.app""},""Flags"":""Soft""}}
        ]}}}";
        var apps = ModdingTools.ParseEntityAppearances(ent);
        var a = Assert.Single(apps);
        Assert.Equal("inst_club_all_wa_02", a.Name);
        Assert.Equal("wa_02", a.AppearanceName);
        Assert.EndsWith("inst.app", a.AppResource);
    }

    [Fact]
    public void ParsesAppMeshRefs()
    {
        const string app = @"{""Data"":{""RootChunk"":{""$type"":""appearanceAppearanceResource"",""appearances"":[
          {""Data"":{""$type"":""appearanceAppearanceDefinition"",""name"":{""$value"":""Base""},""components"":[
            {""Data"":{""$type"":""entMeshComponent"",
              ""mesh"":{""DepotPath"":{""$value"":""base\\m\\a.mesh""}},
              ""meshAppearance"":{""$value"":""default""}}}
          ]}}
        ]}}}";
        var refs = ModdingTools.ParseAppMeshRefs(app);
        var r = Assert.Single(refs);
        Assert.Equal("Base", r.AppAppearance);
        Assert.EndsWith("a.mesh", r.MeshPath);
        Assert.Equal("default", r.MeshAppearance);
    }

    [Fact]
    public void ParsesMeshAppearancesAndMaterials()
    {
        const string mesh = @"{""Data"":{""RootChunk"":{""$type"":""CMesh"",
          ""appearances"":[{""Data"":{""$type"":""meshMeshAppearance"",""name"":{""$value"":""default""},
            ""chunkMaterials"":[{""$type"":""CName"",""$value"":""lambert1""}]}}],
          ""materialEntries"":[{""name"":{""$value"":""lambert1""}}]}}}";
        var (apps, mats) = ModdingTools.ParseMeshAppearancesAndMaterials(mesh);
        Assert.Contains("default", apps);
        Assert.Contains("lambert1", mats);
    }
}
