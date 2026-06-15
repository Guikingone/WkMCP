using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace WolvenKitMcp.Tests;

/// <summary>
/// Smoke test MCP de bout en bout, sans jeu ni cp77tools : lance le serveur stdio
/// réellement compilé, fait le handshake initialize, puis pagine tools/list et
/// vérifie que les ~120 outils s'enregistrent avec leurs annotations. C'est le
/// test qui attrape une régression d'enregistrement par réflexion ou un paramètre
/// que le SDK ne sait pas lier (l'erreur n'apparaîtrait sinon qu'au runtime chez
/// l'utilisateur).
/// </summary>
public class McpE2ETests : IDisposable
{
    private readonly Process _server;
    private static readonly TimeSpan IoTimeout = TimeSpan.FromSeconds(30);

    public McpE2ETests()
    {
        // .../WolvenKitMcp.Tests/bin/<cfg>/net8.0 → .../WolvenKitMcp/bin/<cfg>/net8.0
        var baseDir = new DirectoryInfo(AppContext.BaseDirectory);
        var config = baseDir.Parent!.Name;
        var src = baseDir.Parent!.Parent!.Parent!.Parent!;
        var serverDll = Path.Combine(src.FullName, "WolvenKitMcp", "bin", config, "net8.0", "WolvenKitMcp.dll");
        Assert.True(File.Exists(serverDll), $"Serveur non compilé : {serverDll} (builder WolvenKitMcp d'abord)");

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(serverDll);
        psi.Environment["WOLVENKIT_MCP_TRANSPORT"] = "stdio";
        // Pas de daemon : le serveur doit démarrer et répondre à tools/list sans lui.
        psi.Environment["WOLVENKIT_DAEMON"] = Path.Combine(Path.GetTempPath(), "inexistant-daemon.dll");
        // Pas de port TCP partagé entre tests/sessions.
        psi.Environment["CET_TRANSPORT"] = "file";

        _server = Process.Start(psi)!;
        // Draine stderr (logs) pour ne pas bloquer le pipe.
        _ = Task.Run(async () => { while (await _server.StandardError.ReadLineAsync() is not null) { } });
    }

    public void Dispose()
    {
        try { _server.Kill(entireProcessTree: true); } catch { /* déjà mort */ }
        _server.Dispose();
    }

    private async Task<JsonDocument> RequestAsync(object payload, int expectedId)
    {
        await _server.StandardInput.WriteLineAsync(JsonSerializer.Serialize(payload));
        await _server.StandardInput.FlushAsync();
        while (true)
        {
            var readTask = _server.StandardOutput.ReadLineAsync();
            var line = await readTask.WaitAsync(IoTimeout);
            Assert.False(line is null, "Le serveur a fermé stdout prématurément.");
            if (string.IsNullOrWhiteSpace(line)) continue;
            var doc = JsonDocument.Parse(line!);
            if (doc.RootElement.TryGetProperty("id", out var id)
                && id.ValueKind == JsonValueKind.Number && id.GetInt32() == expectedId)
                return doc;
            doc.Dispose(); // notification ou autre réponse : on continue
        }
    }

    private async Task NotifyAsync(object payload)
    {
        await _server.StandardInput.WriteLineAsync(JsonSerializer.Serialize(payload));
        await _server.StandardInput.FlushAsync();
    }

    [Fact]
    public async Task Handshake_puis_tools_list_complet()
    {
        using var init = await RequestAsync(new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "initialize",
            @params = new
            {
                protocolVersion = "2024-11-05",
                capabilities = new { },
                clientInfo = new { name = "e2e-test", version = "0" },
            },
        }, expectedId: 1);

        var serverInfo = init.RootElement.GetProperty("result").GetProperty("serverInfo");
        Assert.Equal("WolvenKitMcp", serverInfo.GetProperty("name").GetString());

        await NotifyAsync(new { jsonrpc = "2.0", method = "notifications/initialized" });

        // tools/list est paginé : on accumule jusqu'à épuisement du curseur.
        var tools = new Dictionary<string, JsonElement>();
        string? cursor = null;
        var id = 2;
        do
        {
            object payload = cursor is null
                ? new { jsonrpc = "2.0", id, method = "tools/list", @params = new { } }
                : new { jsonrpc = "2.0", id, method = "tools/list", @params = new { cursor } };
            using var page = await RequestAsync(payload, expectedId: id);
            id++;
            var result = page.RootElement.GetProperty("result");
            foreach (var tool in result.GetProperty("tools").EnumerateArray())
                tools[tool.GetProperty("name").GetString()!] = tool.Clone();
            cursor = result.TryGetProperty("nextCursor", out var nc) ? nc.GetString() : null;
        } while (!string.IsNullOrEmpty(cursor));

        // Le compte attendu vient du code lui-même (réflexion), pas d'une constante.
        var expected = new[] { typeof(WolvenKitTools), typeof(ModdingTools), typeof(LiveTools) }
            .SelectMany(WolvenKitResources.ToolNames).ToHashSet(StringComparer.Ordinal);
        Assert.True(expected.Count >= 123, $"compte d'outils suspect : {expected.Count}");
        var missing = expected.Where(n => !tools.ContainsKey(n)).ToList();
        Assert.True(missing.Count == 0,
            $"Outils non exposés par tools/list : {string.Join(", ", missing)}");

        // Les annotations doivent traverser le protocole.
        var find = tools["find_in_archives"];
        Assert.True(find.TryGetProperty("annotations", out var ann),
            "find_in_archives sans annotations dans tools/list");
        Assert.True(ann.GetProperty("readOnlyHint").GetBoolean());
        var uninstall = tools["uninstall_mod"].GetProperty("annotations");
        Assert.True(uninstall.GetProperty("destructiveHint").GetBoolean());

        // Le paramètre IProgress des outils longs ne doit PAS fuir dans l'inputSchema.
        var uncookSchema = tools["uncook"].GetProperty("inputSchema").GetRawText();
        Assert.DoesNotContain("progress", uncookSchema, StringComparison.OrdinalIgnoreCase);
    }
}
