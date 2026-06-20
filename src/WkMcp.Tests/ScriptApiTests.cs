using System.Linq;
using WkMcp;
using Xunit;

namespace WkMcp.Tests;

// R1 — REDscript symbol index. These exercise the enriched parser (signatures,
// enclosing class, extends, @wrapMethod targets) through the pure ScriptApi core.

public class ScriptApiSymbolTests
{
    private const string Source = """
        module My.Mod

        public class PlayerPuppet extends ScriptedPuppet {
          public let m_id: Uint32;
          public func GetMaxHealth() -> Float {
            return 100.0;
          }
          private func Reset() {
          }
        }

        @wrapMethod(PlayerPuppet)
        func OnGameAttached() -> Void {
          wrappedMethod();
        }

        func Helper(x: Int32, opt y: Float) -> Bool {
          return true;
        }

        enum EColor {
          Red = 0,
          Green = 1,
        }
        """;

    [Fact]
    public void Captures_class_extends_and_member_signatures()
    {
        var syms = ScriptApi.SymbolsOf("a.reds", Source);

        var cls = Assert.Single(syms, s => s.Kind == "class" && s.Name == "PlayerPuppet");
        Assert.Equal("ScriptedPuppet", cls.Parent);

        var field = Assert.Single(syms, s => s.Kind == "field" && s.Name == "m_id");
        Assert.Equal("PlayerPuppet", field.Parent);
        Assert.Equal("m_id: Uint32", field.Signature);

        var method = Assert.Single(syms, s => s.Name == "GetMaxHealth");
        Assert.Equal("func", method.Kind);
        Assert.Equal("PlayerPuppet", method.Parent);
        Assert.Equal("GetMaxHealth() -> Float", method.Signature);
    }

    [Fact]
    public void Captures_wrapMethod_target_as_the_enclosing_class()
    {
        var syms = ScriptApi.SymbolsOf("a.reds", Source);
        var hook = Assert.Single(syms, s => s.Name == "OnGameAttached");
        Assert.Equal("PlayerPuppet", hook.Parent);                 // from @wrapMethod(PlayerPuppet)
        Assert.Contains("wrapMethod", hook.Annotations);
        Assert.Equal("OnGameAttached() -> Void", hook.Signature);
    }

    [Fact]
    public void A_free_function_has_no_parent_and_keeps_its_param_list()
    {
        var syms = ScriptApi.SymbolsOf("a.reds", Source);
        var helper = Assert.Single(syms, s => s.Name == "Helper");
        Assert.Null(helper.Parent);
        Assert.Equal("Helper(x: Int32, opt y: Float) -> Bool", helper.Signature);
    }

    [Fact]
    public void Captures_enums()
        => Assert.Single(ScriptApi.SymbolsOf("a.reds", Source), s => s.Kind == "enum" && s.Name == "EColor");
}

public class ScriptApiQueryTests
{
    private static System.Collections.Generic.List<ScriptApi.ScriptSymbol> Sample() => new()
    {
        new("class", "PlayerPuppet", "ScriptedPuppet", null, new[] { "" }, "a.reds", 1),
        new("func",  "GetMaxHealth", "PlayerPuppet",  "GetMaxHealth() -> Float", new[] { "wrapMethod" }, "a.reds", 3),
        new("func",  "GetHealth",    "PlayerPuppet",  "GetHealth() -> Float",    System.Array.Empty<string>(), "a.reds", 7),
        new("func",  "Helper",       null,            "Helper() -> Bool",        System.Array.Empty<string>(), "b.reds", 2),
        new("field", "m_health",     "PlayerPuppet",  "m_health: Float",         System.Array.Empty<string>(), "a.reds", 2),
    };

    [Fact]
    public void Substring_name_filter_is_case_insensitive()
    {
        var r = ScriptApi.Query(Sample(), "gethealth", null, null, 100);
        var hit = Assert.Single(r);
        Assert.Equal("GetHealth", hit.Name);
    }

    [Fact]
    public void Method_kind_is_a_func_with_a_class_global_is_a_free_func()
    {
        Assert.All(ScriptApi.Query(Sample(), null, "method", null, 100),
            s => Assert.False(string.IsNullOrEmpty(s.Parent)));
        var global = Assert.Single(ScriptApi.Query(Sample(), null, "global", null, 100));
        Assert.Equal("Helper", global.Name);
    }

    [Fact]
    public void OfClass_filter_narrows_to_one_class()
    {
        var r = ScriptApi.Query(Sample(), null, null, "PlayerPuppet", 100);
        // The 3 members whose enclosing/target class is PlayerPuppet (2 funcs + field).
        // The class declaration itself has Parent "ScriptedPuppet" (its extends), so it
        // is correctly NOT matched by an ofClass=PlayerPuppet filter.
        Assert.Equal(3, r.Count);
        Assert.DoesNotContain(r, s => s.Name == "Helper");
        Assert.DoesNotContain(r, s => s.Kind == "class");
    }

    [Fact]
    public void Results_are_capped()
        => Assert.Equal(2, ScriptApi.Query(Sample(), null, null, null, 2).Count);
}
