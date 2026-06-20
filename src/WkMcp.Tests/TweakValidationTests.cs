using System.Collections.Generic;
using WkMcp;
using Xunit;
using K = WkMcp.TweakValidation.Kind;

namespace WkMcp.Tests;

// The Kind enum is internal, so [Theory] parameters use int (cast inside) to keep
// the public test signatures from exposing an internal type (CS0051).

public class TweakValueClassifyTests
{
    [Theory]
    [InlineData("50", (int)K.Int)]
    [InlineData("-1", (int)K.Int)]
    [InlineData("0xC0DE", (int)K.Int)]
    [InlineData("50.0", (int)K.Float)]
    [InlineData("-7.25", (int)K.Float)]
    [InlineData("1e5", (int)K.Float)]
    [InlineData("true", (int)K.Bool)]
    [InlineData("false", (int)K.Bool)]
    [InlineData("n\"Thing\"", (int)K.CName)]
    [InlineData("None", (int)K.CName)]
    [InlineData("t\"Package.Item\"", (int)K.TweakDBID)]
    [InlineData("<TDBID:12AB56CD:1F>", (int)K.TweakDBID)]
    [InlineData("l\"key\"", (int)K.LocKey)]
    [InlineData("LocKey#12345", (int)K.LocKey)]
    [InlineData("r\"base\\\\x.mesh\"", (int)K.ResRef)]
    [InlineData("\"some text\"", (int)K.String)]
    [InlineData("fast", (int)K.String)]          // implicit bareword → string-like
    public void Classifies_scalar_values(string raw, int expected)
        => Assert.Equal((K)expected, TweakValidation.ClassifyValue(raw));

    [Fact]
    public void Classifies_sequence_and_mapping()
    {
        Assert.Equal(K.Array, TweakValidation.ClassifyValue(new List<object> { "a", "b" }));
        Assert.Equal(K.Struct, TweakValidation.ClassifyValue(new Dictionary<object, object> { ["x"] = "1" }));
        Assert.Equal(K.Unknown, TweakValidation.ClassifyValue(null));
    }
}

public class TweakRedTypeToKindTests
{
    [Theory]
    [InlineData("CFloat", (int)K.Float)]
    [InlineData("Double", (int)K.Float)]
    [InlineData("CInt32", (int)K.Int)]
    [InlineData("Int64", (int)K.Int)]
    [InlineData("CBool", (int)K.Bool)]
    [InlineData("CName", (int)K.CName)]
    [InlineData("TweakDBID", (int)K.TweakDBID)]
    [InlineData("CString", (int)K.String)]
    [InlineData("String", (int)K.String)]
    [InlineData("gamedataLocKeyWrapper", (int)K.LocKey)]
    [InlineData("raRef:CResource", (int)K.ResRef)]
    [InlineData("CArray`1", (int)K.Array)]
    [InlineData("List", (int)K.Array)]
    [InlineData("ConstantStatModifier", (int)K.Unknown)]   // handle/struct → lenient
    [InlineData("", (int)K.Unknown)]
    public void Maps_flat_type_tokens(string token, int expected)
        => Assert.Equal((K)expected, TweakValidation.RedTypeToKind(token));
}

public class TweakCompatibilityTests
{
    [Theory]
    // The headline catch: a non-number into a numeric flat.
    [InlineData((int)K.String, (int)K.Float, false)]   // attacksPerSecond: "fast"
    [InlineData((int)K.String, (int)K.Int, false)]
    [InlineData((int)K.CName, (int)K.Float, false)]    // n"x" into a float
    [InlineData((int)K.Int, (int)K.Bool, false)]
    [InlineData((int)K.Float, (int)K.Int, false)]      // 50.0 into an Int flat
    // Lenient on the name/string family (a number is a plausible CName "50",
    // and numbers are valid implicit LocKey/ResRef) — not flagged.
    [InlineData((int)K.Int, (int)K.CName, true)]
    [InlineData((int)K.Int, (int)K.String, true)]
    // Valid assignments.
    [InlineData((int)K.Int, (int)K.Float, true)]       // ints are valid floats
    [InlineData((int)K.Int, (int)K.Int, true)]
    [InlineData((int)K.Float, (int)K.Float, true)]
    [InlineData((int)K.Bool, (int)K.Bool, true)]
    [InlineData((int)K.String, (int)K.CName, true)]    // lenient on the name/string family
    [InlineData((int)K.String, (int)K.TweakDBID, true)]
    [InlineData((int)K.CName, (int)K.CName, true)]
    // Array ↔ scalar dimension.
    [InlineData((int)K.Array, (int)K.Float, false)]
    [InlineData((int)K.String, (int)K.Array, false)]
    [InlineData((int)K.Array, (int)K.Array, true)]
    // Struct into a strict scalar, and the lenient escapes.
    [InlineData((int)K.Struct, (int)K.Float, false)]
    [InlineData((int)K.Struct, (int)K.Unknown, true)]
    [InlineData((int)K.Unknown, (int)K.Float, true)]   // can't judge → pass
    [InlineData((int)K.Float, (int)K.Unknown, true)]
    public void Compatibility_matrix(int value, int flat, bool expected)
        => Assert.Equal(expected, TweakValidation.AreCompatible((K)value, (K)flat));
}

public class TweakDescribeParseTests
{
    [Fact]
    public void Parses_flat_lines_from_describe_output()
    {
        const string log = """
        [ 0: Info ] - record Items.Foo (gamedataWeaponItem_Record)
        [ 0: Info ] -   flat  damage : CFloat = 50
        [ 0: Info ] -   flat  displayName : CName = n"X"
        [ 0: Info ] -   flat  parts : CArray`1 = [..]
        [ 0: Info ] - 3 flat(s) under Items.Foo
        """;
        var map = TweakValidation.ParseDescribedFlats(log);
        Assert.Equal(K.Float, map["damage"]);
        Assert.Equal(K.CName, map["displayName"]);
        Assert.Equal(K.Array, map["parts"]);
        Assert.Equal(3, map.Count);
    }

    [Fact]
    public void Parses_current_values_for_the_preview_before_side()
    {
        const string log = """
        [ 0: Info ] -   flat  damage : CFloat = 30
        [ 0: Info ] -   flat  displayName : CName = n"Old"
        """;
        var vals = TweakValidation.ParseDescribedValues(log);
        Assert.Equal("30", vals["damage"]);
        Assert.Equal("n\"Old\"", vals["displayName"]);
    }
}

public class TweakPreviewHelpersTests
{
    [Theory]
    [InlineData("50", true)]
    [InlineData("n\"X\"", true)]
    public void Scalar_strings_are_scalar(string v, bool scalar)
        => Assert.Equal(scalar, TweakValidation.IsScalarValue(v));

    [Fact]
    public void Arrays_and_mappings_are_not_scalar()
    {
        Assert.False(TweakValidation.IsScalarValue(new List<object> { "a" }));
        Assert.False(TweakValidation.IsScalarValue(new Dictionary<object, object> { ["x"] = "1" }));
    }

    [Fact]
    public void RenderValue_trims_and_handles_null()
    {
        Assert.Equal("50", TweakValidation.RenderValue("  50 "));
        Assert.Equal("(null)", TweakValidation.RenderValue(null));
    }
}

public class TweakEnumerateAssignmentsTests
{
    [Fact]
    public void Walks_nested_record_body_with_base()
    {
        var root = new Dictionary<object, object>
        {
            ["Items.Foo"] = new Dictionary<object, object>
            {
                ["$instanceOf"] = "Items.Base",
                ["damage"] = "50",
                ["displayName"] = "n\"X\"",
            },
        };
        var a = TweakValidation.EnumerateAssignments(root);
        Assert.Equal(2, a.Count);                                  // directive excluded
        Assert.All(a, x => Assert.Equal("Items.Foo", x.Record));
        Assert.All(a, x => Assert.Equal("Items.Base", x.BaseRecord));
        Assert.Contains(a, x => x.Field == "damage" && (string?)x.Value == "50");
    }

    [Fact]
    public void Walks_flattened_record_dot_field()
    {
        var root = new Dictionary<object, object> { ["Items.Foo.damage"] = "50" };
        var a = TweakValidation.EnumerateAssignments(root);
        var one = Assert.Single(a);
        Assert.Equal("Items.Foo", one.Record);
        Assert.Equal("damage", one.Field);
        Assert.Null(one.BaseRecord);
    }

    [Fact]
    public void Skips_top_level_directives_and_bare_keys()
    {
        var root = new Dictionary<object, object>
        {
            ["$instances"] = new List<object>(),   // template block → skipped
            ["BareRecordNoDot"] = "x",             // not record.field shaped → skipped
        };
        Assert.Empty(TweakValidation.EnumerateAssignments(root));
    }
}
