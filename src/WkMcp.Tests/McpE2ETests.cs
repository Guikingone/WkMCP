using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace WkMcp.Tests;

/// <summary>
/// End-to-end MCP smoke test, without game or cp77tools: launches the actually
/// compiled stdio server, does the initialize handshake, then paginates tools/list and
/// verifies that the 123 tools register with their annotations. This is the
/// test that catches a reflection-based registration regression or a parameter
/// that the SDK cannot bind (the error would otherwise only appear at runtime on the
/// user's machine).
/// </summary>
public class McpE2ETests : IDisposable
{
    private readonly Process _server;
    private static readonly TimeSpan IoTimeout = TimeSpan.FromSeconds(30);

    public McpE2ETests()
    {
        // .../WkMcp.Tests/bin/<cfg>/net8.0 → .../WkMcp/bin/<cfg>/net8.0
        var baseDir = new DirectoryInfo(AppContext.BaseDirectory);
        var config = baseDir.Parent!.Name;
        var src = baseDir.Parent!.Parent!.Parent!.Parent!;
        var serverDll = Path.Combine(src.FullName, "WkMcp", "bin", config, "net8.0", "WkMcp.dll");
        Assert.True(File.Exists(serverDll), $"Server not compiled: {serverDll} (build WkMcp first)");

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
        psi.Environment["WKMCP_TRANSPORT"] = "stdio";
        // No daemon: the server must start and answer tools/list without it.
        psi.Environment["WKMCP_DAEMON"] = Path.Combine(Path.GetTempPath(), "inexistant-daemon.dll");
        // No TCP port shared between tests/sessions.
        psi.Environment["CET_TRANSPORT"] = "file";

        _server = Process.Start(psi)!;
        // Drain stderr (logs) so as not to block the pipe.
        _ = Task.Run(async () => { while (await _server.StandardError.ReadLineAsync() is not null) { } });
    }

    public void Dispose()
    {
        try { _server.Kill(entireProcessTree: true); } catch { /* already dead */ }
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
            Assert.False(line is null, "The server closed stdout prematurely.");
            if (string.IsNullOrWhiteSpace(line)) continue;
            var doc = JsonDocument.Parse(line!);
            if (doc.RootElement.TryGetProperty("id", out var id)
                && id.ValueKind == JsonValueKind.Number && id.GetInt32() == expectedId)
                return doc;
            doc.Dispose(); // notification or other response: continue
        }
    }

    private async Task NotifyAsync(object payload)
    {
        await _server.StandardInput.WriteLineAsync(JsonSerializer.Serialize(payload));
        await _server.StandardInput.FlushAsync();
    }

    [Fact]
    public async Task Handshake_then_full_tools_list()
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
        Assert.Equal("WkMcp", serverInfo.GetProperty("name").GetString());

        await NotifyAsync(new { jsonrpc = "2.0", method = "notifications/initialized" });

        // tools/list is paginated: we accumulate until the cursor is exhausted.
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

        // The expected count comes from the code itself (reflection), not a constant.
        var expected = new[] { typeof(WolvenKitTools), typeof(ModdingTools), typeof(LiveTools) }
            .SelectMany(WolvenKitResources.ToolNames).ToHashSet(StringComparer.Ordinal);
        Assert.True(expected.Count >= 123, $"suspicious tool count: {expected.Count}");
        var missing = expected.Where(n => !tools.ContainsKey(n)).ToList();
        Assert.True(missing.Count == 0,
            $"Tools not exposed by tools/list: {string.Join(", ", missing)}");

        // The annotations must travel through the protocol.
        var find = tools["find_in_archives"];
        Assert.True(find.TryGetProperty("annotations", out var ann),
            "find_in_archives without annotations in tools/list");
        Assert.True(ann.GetProperty("readOnlyHint").GetBoolean());
        var uninstall = tools["uninstall_mod"].GetProperty("annotations");
        Assert.True(uninstall.GetProperty("destructiveHint").GetBoolean());

        // The IProgress parameter of long-running tools must NOT leak into the inputSchema.
        var uncookSchema = tools["uncook"].GetProperty("inputSchema").GetRawText();
        Assert.DoesNotContain("progress", uncookSchema, StringComparison.OrdinalIgnoreCase);
    }
}
