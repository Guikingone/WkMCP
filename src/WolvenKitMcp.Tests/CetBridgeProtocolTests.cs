using System.Text;
using System.Text.Json;
using WolvenKitMcp;
using Xunit;

namespace WolvenKitMcp.Tests;

/// <summary>Découpage des trames JSON délimitées "\r\n" du transport TCP du pont live.</summary>
public class FrameSplitterTests
{
    private static byte[] B(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public void SingleCompleteFrame()
    {
        var s = new FrameSplitter();
        var msgs = s.Append(B("{\"id\":\"a\"}\r\n"));
        Assert.Equal(new[] { "{\"id\":\"a\"}" }, msgs);
    }

    [Fact]
    public void TwoFramesInOneBuffer()
    {
        var s = new FrameSplitter();
        var msgs = s.Append(B("alpha\r\nbeta\r\n"));
        Assert.Equal(new[] { "alpha", "beta" }, msgs);
    }

    [Fact]
    public void FragmentedFrameAcrossAppends()
    {
        var s = new FrameSplitter();
        Assert.Empty(s.Append(B("{\"id\":")));        // pas encore de délimiteur
        var msgs = s.Append(B("\"a\"}\r\n"));
        Assert.Equal(new[] { "{\"id\":\"a\"}" }, msgs);
    }

    [Fact]
    public void DelimiterSplitAcrossAppends()
    {
        var s = new FrameSplitter();
        Assert.Empty(s.Append(B("ab\r")));            // \r seul : trame incomplète
        var msgs = s.Append(B("\ncd\r\n"));
        Assert.Equal(new[] { "ab", "cd" }, msgs);
    }

    [Fact]
    public void EmptyFramesAreSkipped()
    {
        var s = new FrameSplitter();
        var msgs = s.Append(B("\r\n\r\nx\r\n"));
        Assert.Equal(new[] { "x" }, msgs);
    }

    [Fact]
    public void MultibyteUtf8NotSplitMidCharacter()
    {
        // "héllo" : 'é' = 0xC3 0xA9. On coupe l'octet de tête de la seconde lecture.
        var s = new FrameSplitter();
        Assert.Empty(s.Append(new byte[] { 0x68, 0xC3 }, 2));       // "h" + tête de 'é'
        var msgs = s.Append(new byte[] { 0xA9, 0x6C, 0x6C, 0x6F, 0x0D, 0x0A }, 6);
        Assert.Equal(new[] { "héllo" }, msgs);
    }
}

/// <summary>Parsing des réponses {id, ok, result?, error?} du pont.</summary>
public class BridgeProtocolParseTests
{
    [Fact]
    public void OkResultParsed()
    {
        var r = BridgeProtocol.ParseResponse("{\"id\":\"x\",\"ok\":true,\"result\":\"42\"}", "tcp");
        Assert.True(r.Ok);
        Assert.Equal("x", r.Id);
        Assert.Equal("42", r.Result);
        Assert.Null(r.Error);
        Assert.Equal("tcp", r.Transport);
    }

    [Fact]
    public void ErrorParsed()
    {
        var r = BridgeProtocol.ParseResponse("{\"id\":\"x\",\"ok\":false,\"error\":\"boom\"}", "file");
        Assert.False(r.Ok);
        Assert.Equal("boom", r.Error);
    }

    [Fact]
    public void NotOkWithoutErrorGetsDefaultMessage()
    {
        var r = BridgeProtocol.ParseResponse("{\"id\":\"x\",\"ok\":false}", "tcp");
        Assert.False(r.Ok);
        Assert.False(string.IsNullOrWhiteSpace(r.Error));
    }

    [Fact]
    public void NonStringResultKeptAsRawJson()
    {
        var r = BridgeProtocol.ParseResponse("{\"id\":\"x\",\"ok\":true,\"result\":{\"a\":1}}", "tcp");
        Assert.True(r.Ok);
        Assert.Equal("{\"a\":1}", r.Result);
    }

    [Fact]
    public void MalformedJsonProducesError()
    {
        var r = BridgeProtocol.ParseResponse("ceci n'est pas du json", "tcp");
        Assert.False(r.Ok);
        Assert.Contains("malform", r.Error, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Transport fichier (command.json / response.json) — round-trip réel sur un dossier temporaire.</summary>
public class FileSendTests
{
    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cetbridge-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void WriteResponse(string dir, string json)
    {
        var tmp = Path.Combine(dir, "response.json.tmp");
        var res = Path.Combine(dir, "response.json");
        File.WriteAllText(tmp, json);
        File.Move(tmp, res, overwrite: true);
    }

    private static async Task<string> WaitForCommandAsync(string dir)
    {
        var cmd = Path.Combine(dir, "command.json");
        for (int i = 0; i < 150 && !File.Exists(cmd); i++) await Task.Delay(20);
        Assert.True(File.Exists(cmd), "command.json aurait dû être écrit par FileSendAsync");
        var text = await File.ReadAllTextAsync(cmd);
        File.Delete(cmd); // comme le fait le mod Lua après lecture
        return text;
    }

    [Fact]
    public async Task RoundTripReturnsMatchingResponse()
    {
        var dir = NewTempDir();
        try
        {
            var send = BridgeProtocol.FileSendAsync(
                "req-1", "{\"id\":\"req-1\",\"type\":\"eval\",\"expr\":\"1+1\"}",
                dir, TimeSpan.FromSeconds(3), CancellationToken.None);

            var cmdText = await WaitForCommandAsync(dir);
            using (var doc = JsonDocument.Parse(cmdText))
                Assert.Equal("req-1", doc.RootElement.GetProperty("id").GetString());

            WriteResponse(dir, "{\"id\":\"req-1\",\"ok\":true,\"result\":\"2\"}");

            var resp = await send;
            Assert.True(resp.Ok);
            Assert.Equal("2", resp.Result);
            Assert.Equal("file", resp.Transport);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task TimesOutWhenNoResponder()
    {
        var dir = NewTempDir();
        try
        {
            var resp = await BridgeProtocol.FileSendAsync(
                "req-2", "{\"id\":\"req-2\"}", dir, TimeSpan.FromMilliseconds(300), CancellationToken.None);
            Assert.False(resp.Ok);
            Assert.True(resp.TimedOut);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task StaleResponseFromOtherRequestIsSkipped()
    {
        var dir = NewTempDir();
        try
        {
            var send = BridgeProtocol.FileSendAsync(
                "req-3", "{\"id\":\"req-3\"}", dir, TimeSpan.FromSeconds(3), CancellationToken.None);

            await WaitForCommandAsync(dir);
            WriteResponse(dir, "{\"id\":\"OTHER\",\"ok\":true,\"result\":\"périmé\"}");
            await Task.Delay(150); // laisse FileSendAsync lire puis écarter la réponse périmée
            WriteResponse(dir, "{\"id\":\"req-3\",\"ok\":true,\"result\":\"frais\"}");

            var resp = await send;
            Assert.True(resp.Ok);
            Assert.Equal("frais", resp.Result);
        }
        finally { Directory.Delete(dir, true); }
    }
}

/// <summary>Coercition valeur texte + hint de type → valeur JSON typée pour tweakdb_set.</summary>
public class CoerceTweakValueTests
{
    [Theory]
    [InlineData("5", "Int")]
    [InlineData("42", null)]      // auto : entier
    public void IntLikeBecomesLong(string value, string? type)
        => Assert.IsType<long>(LiveTools.CoerceTweakValue(value, type));

    [Theory]
    [InlineData("3.14", "Float")]
    [InlineData("2.5", null)]     // auto : décimal
    public void FloatLikeBecomesDouble(string value, string? type)
        => Assert.IsType<double>(LiveTools.CoerceTweakValue(value, type));

    [Theory]
    [InlineData("true", "Bool")]
    [InlineData("false", null)]   // auto : booléen
    public void BoolLikeBecomesBool(string value, string? type)
        => Assert.IsType<bool>(LiveTools.CoerceTweakValue(value, type));

    [Theory]
    [InlineData("Foo", "String")]
    [InlineData("Items.Preset", "CName")] // CName reste une string (le Lua fait CName.new)
    [InlineData("hello", null)]           // auto : non numérique → string
    public void StringLikeStaysString(string value, string? type)
        => Assert.IsType<string>(LiveTools.CoerceTweakValue(value, type));

    [Fact]
    public void IntHintParsesExactValue()
        => Assert.Equal(5L, LiveTools.CoerceTweakValue("5", "Int"));
}
