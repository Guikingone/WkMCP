using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace WolvenKitMcp;

/// <summary>Response to a request to the in-game bridge (CETBridge mod).</summary>
/// <param name="Transport">"tcp" or "file" — through which channel the response was passed.</param>
public sealed record BridgeResponse(
    string Id, bool Ok, string? Result, string? Error, bool TimedOut, string Transport);

/// <summary>Bridge connectivity state — exposed by the <c>live_status</c> tool.</summary>
public sealed record BridgeStatus(
    bool Connected, string Transport, bool TcpListening, int TcpPort,
    bool TcpClientConnected, string? BridgeDir, string? LastHeartbeatUtc, bool FileHeartbeatFresh);

/// <summary>
/// "Live" bridge to a running Cyberpunk 2077 game, via the Lua mod
/// <b>CETBridge</b> (Cyber Engine Tweaks). Unlike the 85 offline tools that
/// operate on files, this service talks to the Lua VM of the live game.
///
/// Protocol (identical to upstream Y4rd13/cyber-engine-tweak-mcp, to reuse
/// the Lua mod as-is):
///   request  : { id, type:"exec"|"eval"|"query", code?|expr?|handler?+args? }
///   response : { id, ok, result?, error? }
///
/// Two transports, with automatic switching:
///   • <b>TCP</b> (recommended, ~1 ms): this service is the <i>listener</i> on
///     127.0.0.1:27010; the Lua mod connects (RED4ext RedSocket plugin). JSON
///     frames delimited by "\r\n". {type:"heartbeat"} messages ignored (liveness).
///   • <b>File</b> (fallback, ~16-33 ms, without RedSocket): we write <c>command.json</c>
///     (tmp + atomic rename) into the mod folder, then poll <c>response.json</c>.
///     Liveness via <c>heartbeat.json</c> (fresh &lt; 3 s).
///
/// The listener startup is <b>lazy but idempotent</b> (<see cref="EnsureStarted"/>):
/// a purely offline server never opens the port as long as no <c>live_*</c> tool
/// (nor the Program.cs warmup) is invoked.
///
/// Environment variables (all optional):
///   CET_TRANSPORT        "tcp" (default) | "file" (forces fallback, does not open the port)
///   CET_TCP_PORT         listener TCP port (default 27010)
///   CET_BRIDGE_DIR       CETBridge mod folder (otherwise derived from the tools' gamePath)
///   CET_BRIDGE_TIMEOUT_MS max delay of a request (default 5000)
/// </summary>
public sealed class CetBridge : IDisposable
{
    private readonly ILogger? _log;
    private readonly int _port;
    private readonly string _transportPref; // "tcp" | "file"
    private readonly string? _bridgeDirEnv;
    private readonly TimeSpan _timeout;

    private readonly object _startLock = new();
    private volatile bool _started;
    private TcpListener? _listener;
    private bool _tcpAvailable; // did we manage to bind the port?
    private CancellationTokenSource? _cts;

    private volatile TcpClient? _client;
    // volatile: assigned in AcceptLoop, read in WriteFrameAsync on another thread.
    // Without it a writer could observe a stale stream after a reconnect (torn read).
    private volatile NetworkStream? _stream;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    // In-flight TCP requests, indexed by id; completed by the read loop.
    private readonly ConcurrentDictionary<string, TaskCompletionSource<BridgeResponse>> _pending = new();

    private long _lastHeartbeatTicks; // DateTime.UtcNow.Ticks of the last TCP heartbeat (0 = never)

    private static readonly JsonSerializerOptions RequestJson = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public CetBridge(ILogger<CetBridge>? log = null)
    {
        _log = log;
        _transportPref = (Environment.GetEnvironmentVariable("CET_TRANSPORT") ?? "tcp").Trim().ToLowerInvariant();
        _port = int.TryParse(Environment.GetEnvironmentVariable("CET_TCP_PORT"), out var p) && p > 0 ? p : 27010;
        var dir = Environment.GetEnvironmentVariable("CET_BRIDGE_DIR");
        _bridgeDirEnv = string.IsNullOrWhiteSpace(dir) ? null : dir;
        _timeout = int.TryParse(Environment.GetEnvironmentVariable("CET_BRIDGE_TIMEOUT_MS"), out var ms) && ms > 0
            ? TimeSpan.FromMilliseconds(ms)
            : TimeSpan.FromSeconds(5);
    }

    public int TcpPort => _port;

    /// <summary>Starts the TCP listener if needed. Idempotent and thread-safe.
    /// Opens nothing if CET_TRANSPORT=file. A bind error (port already taken by
    /// another session) is non-fatal: we fall back to the file transport.</summary>
    public void EnsureStarted()
    {
        if (_started) return;
        lock (_startLock)
        {
            if (_started) return;
            _started = true; // marked early: even on bind failure, we do not retry

            if (_transportPref == "file")
            {
                _log?.LogInformation("[CetBridge] CET_TRANSPORT=file — TCP listener not started.");
                return;
            }

            try
            {
                _listener = new TcpListener(IPAddress.Loopback, _port);
                _listener.Start();
                _tcpAvailable = true;
                _cts = new CancellationTokenSource();
                _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
                _log?.LogInformation("[CetBridge] TCP listener on 127.0.0.1:{Port}", _port);
            }
            catch (SocketException ex)
            {
                _tcpAvailable = false;
                _log?.LogWarning("[CetBridge] cannot bind 127.0.0.1:{Port} ({Msg}) — file transport only.",
                    _port, ex.Message);
            }
        }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener != null)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex) { _log?.LogWarning("[CetBridge] accept: {Msg}", ex.Message); continue; }

            // One connection at a time: the new one replaces the old (like upstream).
            var old = Interlocked.Exchange(ref _client, client);
            if (old is not null)
            {
                // In-flight requests belong to the old connection: we fail them
                // HERE, with an explicit error — the caller knows it is a reconnection
                // (game restarted / mod reloaded) and that a simple retry suffices.
                FailAllPending("CETBridge connection replaced (game restarted or mod reloaded) — retry the call.");
                old.Dispose();
            }
            _stream = client.GetStream();
            Volatile.Write(ref _lastHeartbeatTicks, DateTime.UtcNow.Ticks);
            _log?.LogInformation("[CetBridge] CETBridge connected (TCP).");
            _ = Task.Run(() => ReadLoopAsync(client, ct));
        }
    }

    private async Task ReadLoopAsync(TcpClient client, CancellationToken ct)
    {
        var splitter = new FrameSplitter();
        var buf = new byte[8192];
        try
        {
            var stream = client.GetStream();
            while (!ct.IsCancellationRequested)
            {
                int n = await stream.ReadAsync(buf, ct);
                if (n <= 0) break;
                foreach (var msg in splitter.Append(buf, n)) DispatchMessage(msg);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log?.LogDebug("[CetBridge] socket read ended: {Msg}", ex.Message);
        }
        finally
        {
            if (Interlocked.CompareExchange(ref _client, null, client) == client)
            {
                // Real disconnection (the game/mod closed): in-flight requests will
                // never receive a response. If the connection was REPLACED,
                // AcceptLoopAsync has already failed the old connection's requests —
                // do not touch the new one's here.
                _stream = null;
                _log?.LogInformation("[CetBridge] CETBridge disconnected.");
                FailAllPending("Bridge disconnected (the game/mod closed the connection).");
            }
            client.Dispose();
        }
    }

    private void DispatchMessage(string json)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch { _log?.LogDebug("[CetBridge] unreadable JSON frame ignored."); return; }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String
                && t.GetString() == "heartbeat")
            {
                Volatile.Write(ref _lastHeartbeatTicks, DateTime.UtcNow.Ticks);
                return;
            }
            if (!root.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.String) return;
            var id = idEl.GetString();
            if (id != null && _pending.TryRemove(id, out var tcs))
                tcs.TrySetResult(BridgeProtocol.ToResponse(root, "tcp"));
        }
    }

    private void FailAllPending(string error)
    {
        foreach (var id in _pending.Keys.ToList())
            if (_pending.TryRemove(id, out var tcs))
                tcs.TrySetResult(new BridgeResponse(id, false, null, error, false, "tcp"));
    }

    // ── Public API: one call = one request to the game ───────────────────────────

    public Task<BridgeResponse> ExecAsync(string code, string? gamePath, CancellationToken ct)
        => SendAsync(new Dictionary<string, object?> { ["type"] = "exec", ["code"] = code }, gamePath, ct);

    public Task<BridgeResponse> EvalAsync(string expr, string? gamePath, CancellationToken ct)
        => SendAsync(new Dictionary<string, object?> { ["type"] = "eval", ["expr"] = expr }, gamePath, ct);

    public Task<BridgeResponse> QueryAsync(string handler, object? args, string? gamePath, CancellationToken ct)
        => SendAsync(new Dictionary<string, object?>
        { ["type"] = "query", ["handler"] = handler, ["args"] = args ?? new Dictionary<string, object?>() },
            gamePath, ct);

    private async Task<BridgeResponse> SendAsync(Dictionary<string, object?> fields, string? gamePath, CancellationToken ct)
    {
        EnsureStarted();
        var id = Guid.NewGuid().ToString("N");
        fields["id"] = id;
        string json = JsonSerializer.Serialize(fields, RequestJson);

        bool useTcp = _transportPref != "file" && TcpClientConnected;
        if (useTcp)
            return await SendTcpAsync(id, json, ct);

        // File fallback
        var dir = ResolveBridgeDir(gamePath);
        if (dir == null)
            return new BridgeResponse(id, false, null,
                "Bridge not connected (TCP) and mod folder unknown: pass `gamePath`, or set " +
                "CET_BRIDGE_DIR, or install RedSocket for the TCP transport.", false, "file");
        if (!Directory.Exists(dir))
            return new BridgeResponse(id, false, null,
                $"Mod folder not found: {dir}. Is the game running with CET + the CETBridge mod installed?",
                false, "file");
        return await BridgeProtocol.FileSendAsync(id, json, dir, _timeout, ct);
    }

    private async Task<BridgeResponse> SendTcpAsync(string id, string json, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<BridgeResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        try
        {
            await WriteFrameAsync(json, ct);
        }
        catch (Exception ex)
        {
            _pending.TryRemove(id, out _);
            return new BridgeResponse(id, false, null, $"Socket write failed: {ex.Message}", false, "tcp");
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var completed = await Task.WhenAny(tcs.Task, Task.Delay(_timeout, timeoutCts.Token));
        if (completed == tcs.Task)
        {
            timeoutCts.Cancel(); // stops the Task.Delay
            return await tcs.Task;
        }
        _pending.TryRemove(id, out _);
        return new BridgeResponse(id, false, null,
            $"Bridge timeout: no response within {_timeout.TotalMilliseconds:n0} ms. " +
            "Is the game active and responsive (not paused/loading)?", true, "tcp");
    }

    private async Task WriteFrameAsync(string json, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(json + "\r\n");
        await _writeLock.WaitAsync(ct);
        try
        {
            var s = _stream ?? throw new IOException("TCP stream unavailable (disconnected).");
            await s.WriteAsync(bytes, ct);
            await s.FlushAsync(ct);
        }
        finally { _writeLock.Release(); }
    }

    // ── State ───────────────────────────────────────────────────────────────────

    private bool TcpClientConnected
    {
        get { var c = _client; return c is { Connected: true }; }
    }

    /// <summary>Connectivity snapshot for <c>live_status</c>. Does not require the
    /// game (also diagnoses when it is off).</summary>
    public BridgeStatus StatusSnapshot(string? gamePath)
    {
        EnsureStarted();
        var dir = ResolveBridgeDir(gamePath);
        bool tcpClient = TcpClientConnected;

        string? lastHb = null;
        var ticks = Volatile.Read(ref _lastHeartbeatTicks);
        if (tcpClient && ticks > 0)
            lastHb = new DateTime(ticks, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

        bool fileFresh = TryReadFileHeartbeat(dir, out var fileHbUtc);
        if (!tcpClient && fileFresh && fileHbUtc != null)
            lastHb = fileHbUtc.Value.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

        string transport = tcpClient ? "tcp" : "file";
        bool connected = tcpClient || fileFresh;
        return new BridgeStatus(connected, transport, _tcpAvailable, _port, tcpClient, dir, lastHb, fileFresh);
    }

    private static bool TryReadFileHeartbeat(string? dir, out DateTime? utc)
    {
        utc = null;
        if (dir == null) return false;
        var path = Path.Combine(dir, "heartbeat.json");
        try
        {
            if (!File.Exists(path)) return false;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("timestamp", out var ts) || ts.ValueKind != JsonValueKind.String)
                return false;
            var t = DateTime.Parse(ts.GetString()!, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
            utc = t;
            return (DateTime.UtcNow - t) < TimeSpan.FromSeconds(3);
        }
        catch { return false; }
    }

    /// <summary>CETBridge mod folder: CET_BRIDGE_DIR otherwise derived from gamePath
    /// (&lt;game&gt;/bin/x64/plugins/cyber_engine_tweaks/mods/CETBridge).</summary>
    internal string? ResolveBridgeDir(string? gamePath)
    {
        if (_bridgeDirEnv != null) return _bridgeDirEnv;
        if (!string.IsNullOrWhiteSpace(gamePath))
            return Path.Combine(gamePath, "bin", "x64", "plugins", "cyber_engine_tweaks", "mods", "CETBridge");
        return null;
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { /* ignore */ }
        FailAllPending("Server shutting down.");
        try { _listener?.Stop(); } catch { /* ignore */ }
        _client?.Dispose();
        _writeLock.Dispose();
        _cts?.Dispose();
    }
}

/// <summary>
/// Splits a byte stream into JSON messages delimited by "\r\n" (handles frames
/// fragmented across multiple socket reads). Works in bytes so as never to
/// cut a multi-byte UTF-8 character in the middle.
/// </summary>
internal sealed class FrameSplitter
{
    private readonly List<byte> _buf = new();

    public List<string> Append(byte[] data, int len)
    {
        for (int i = 0; i < len; i++) _buf.Add(data[i]);
        var msgs = new List<string>();
        int idx;
        while ((idx = IndexOfDelim()) >= 0)
        {
            if (idx > 0) msgs.Add(Encoding.UTF8.GetString(_buf.GetRange(0, idx).ToArray()));
            _buf.RemoveRange(0, idx + 2);
        }
        return msgs;
    }

    public List<string> Append(byte[] data) => Append(data, data.Length);

    private int IndexOfDelim()
    {
        for (int i = 0; i + 1 < _buf.Count; i++)
            if (_buf[i] == (byte)'\r' && _buf[i + 1] == (byte)'\n') return i;
        return -1;
    }
}

/// <summary>Pure protocol helpers (testable without socket or game).</summary>
internal static class BridgeProtocol
{
    /// <summary>Builds a <see cref="BridgeResponse"/> from the root JSON element
    /// of a response <c>{ id, ok, result?, error? }</c>.</summary>
    internal static BridgeResponse ToResponse(JsonElement root, string transport)
    {
        string id = root.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.String
            ? (i.GetString() ?? "") : "";
        bool ok = root.TryGetProperty("ok", out var o) && o.ValueKind == JsonValueKind.True;
        string? result = ReadStringLike(root, "result");
        string? error = ReadStringLike(root, "error");
        if (!ok && error == null) error = "unknown error ('error' field absent).";
        return new BridgeResponse(id, ok, result, error, false, transport);
    }

    private static string? ReadStringLike(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var el) || el.ValueKind == JsonValueKind.Null) return null;
        return el.ValueKind == JsonValueKind.String ? el.GetString() : el.GetRawText();
    }

    internal static BridgeResponse ParseResponse(string json, string transport)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return ToResponse(doc.RootElement, transport);
        }
        catch (JsonException)
        {
            return new BridgeResponse("", false, null, "Malformed JSON response from the bridge.", false, transport);
        }
    }

    /// <summary>File transport: writes the request into <c>command.json</c> (tmp +
    /// atomic rename), purges the old <c>response.json</c>, then waits for the response.
    /// The wait is driven by a <see cref="FileSystemWatcher"/> (~ms latency) with
    /// a safety-net poll every 250 ms; if the watcher cannot be installed
    /// (exotic network directory), we fall back to upstream's pure 50 ms poll.</summary>
    // Single-flight per bridge dir: the file transport shares one command.json /
    // response.json pair and the Lua side does NOT match by request id, so two
    // overlapping sends would scramble each other. Serialize them here.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> _fileGates = new();

    internal static async Task<BridgeResponse> FileSendAsync(
        string id, string requestJson, string bridgeDir, TimeSpan timeout, CancellationToken ct)
    {
        var gate = _fileGates.GetOrAdd(Path.GetFullPath(bridgeDir).ToLowerInvariant(), _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
        var cmd = Path.Combine(bridgeDir, "command.json");
        var tmp = Path.Combine(bridgeDir, "command.json.tmp");
        var res = Path.Combine(bridgeDir, "response.json");

        SafeDelete(res);

        using var responseReady = new SemaphoreSlim(0);
        FileSystemWatcher? watcher = null;
        try
        {
            try
            {
                watcher = new FileSystemWatcher(bridgeDir, "response.json")
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                };
                void Signal() { try { responseReady.Release(); } catch { /* semaphore saturated */ } }
                watcher.Created += (_, _) => Signal();
                watcher.Changed += (_, _) => Signal();
                watcher.Renamed += (_, _) => Signal();
                watcher.EnableRaisingEvents = true;
            }
            catch
            {
                watcher?.Dispose();
                watcher = null; // pure poll
            }

            await File.WriteAllTextAsync(tmp, requestJson, ct);
            File.Move(tmp, cmd, overwrite: true);

            var pollInterval = TimeSpan.FromMilliseconds(watcher is null ? 50 : 250);
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    if (File.Exists(res))
                    {
                        var data = await File.ReadAllTextAsync(res, ct);
                        if (!string.IsNullOrWhiteSpace(data))
                        {
                            // Only consume a complete payload. A partial/empty read means
                            // response.json is mid-write (atomic rename not yet flushed):
                            // leave it on disk so the next tick re-reads the full content,
                            // instead of deleting a response we never actually observed.
                            SafeDelete(res);
                            var resp = ParseResponse(data, "file");
                            if (resp.Id == id) return resp;
                            // Stale response from another request — discarded, keep waiting.
                        }
                    }
                }
                catch (IOException) { /* file being written: we retry */ }
                await responseReady.WaitAsync(pollInterval, ct);
            }

            SafeDelete(cmd);
            return new BridgeResponse(id, false, null,
                $"Bridge timeout: no response within {timeout.TotalMilliseconds:n0} ms (file transport). " +
                "Is the game running with the CETBridge mod loaded?", true, "file");
        }
        finally
        {
            watcher?.Dispose();
        }
        }
        finally { gate.Release(); }
    }

    private static void SafeDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* ignore */ }
    }
}
