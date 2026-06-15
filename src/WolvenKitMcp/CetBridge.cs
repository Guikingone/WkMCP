using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace WolvenKitMcp;

/// <summary>Réponse d'une requête au pont in-game (mod CETBridge).</summary>
/// <param name="Transport">"tcp" ou "file" — par quel canal la réponse est passée.</param>
public sealed record BridgeResponse(
    string Id, bool Ok, string? Result, string? Error, bool TimedOut, string Transport);

/// <summary>État de connectivité du pont — exposé par l'outil <c>live_status</c>.</summary>
public sealed record BridgeStatus(
    bool Connected, string Transport, bool TcpListening, int TcpPort,
    bool TcpClientConnected, string? BridgeDir, string? LastHeartbeatUtc, bool FileHeartbeatFresh);

/// <summary>
/// Pont « live » vers un jeu Cyberpunk 2077 en cours d'exécution, via le mod Lua
/// <b>CETBridge</b> (Cyber Engine Tweaks). À l'inverse des 85 outils offline qui
/// opèrent sur des fichiers, ce service parle à la VM Lua du jeu vivant.
///
/// Protocole (identique à l'upstream Y4rd13/cyber-engine-tweak-mcp, pour réutiliser
/// le mod Lua tel quel) :
///   requête  : { id, type:"exec"|"eval"|"query", code?|expr?|handler?+args? }
///   réponse  : { id, ok, result?, error? }
///
/// Deux transports, avec bascule automatique :
///   • <b>TCP</b> (recommandé, ~1 ms) : ce service est le <i>listener</i> sur
///     127.0.0.1:27010 ; le mod Lua se connecte (plugin RED4ext RedSocket). Trames
///     JSON délimitées par "\r\n". Messages {type:"heartbeat"} ignorés (liveness).
///   • <b>Fichier</b> (repli, ~16-33 ms, sans RedSocket) : on écrit <c>command.json</c>
///     (tmp + rename atomique) dans le dossier du mod, puis on poll <c>response.json</c>.
///     Liveness via <c>heartbeat.json</c> (frais &lt; 3 s).
///
/// Le démarrage du listener est <b>paresseux mais idempotent</b> (<see cref="EnsureStarted"/>) :
/// un serveur purement offline n'ouvre jamais le port tant qu'aucun outil <c>live_*</c>
/// (ni le préchauffage de Program.cs) n'est sollicité.
///
/// Variables d'environnement (toutes optionnelles) :
///   CET_TRANSPORT        "tcp" (défaut) | "file" (force le repli, n'ouvre pas le port)
///   CET_TCP_PORT         port TCP du listener (défaut 27010)
///   CET_BRIDGE_DIR       dossier du mod CETBridge (sinon dérivé du gamePath des outils)
///   CET_BRIDGE_TIMEOUT_MS délai max d'une requête (défaut 5000)
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
    private bool _tcpAvailable; // a-t-on réussi à binder le port ?
    private CancellationTokenSource? _cts;

    private volatile TcpClient? _client;
    private NetworkStream? _stream;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    // Requêtes TCP en vol, indexées par id ; complétées par la boucle de lecture.
    private readonly ConcurrentDictionary<string, TaskCompletionSource<BridgeResponse>> _pending = new();

    private long _lastHeartbeatTicks; // DateTime.UtcNow.Ticks du dernier heartbeat TCP (0 = jamais)

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

    /// <summary>Démarre le listener TCP si nécessaire. Idempotent et thread-safe.
    /// N'ouvre rien si CET_TRANSPORT=file. Une erreur de bind (port déjà pris par une
    /// autre session) est non fatale : on retombe sur le transport fichier.</summary>
    public void EnsureStarted()
    {
        if (_started) return;
        lock (_startLock)
        {
            if (_started) return;
            _started = true; // marqué tôt : même en cas d'échec de bind, on ne re-tente pas

            if (_transportPref == "file")
            {
                _log?.LogInformation("[CetBridge] CET_TRANSPORT=file — listener TCP non démarré.");
                return;
            }

            try
            {
                _listener = new TcpListener(IPAddress.Loopback, _port);
                _listener.Start();
                _tcpAvailable = true;
                _cts = new CancellationTokenSource();
                _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
                _log?.LogInformation("[CetBridge] listener TCP sur 127.0.0.1:{Port}", _port);
            }
            catch (SocketException ex)
            {
                _tcpAvailable = false;
                _log?.LogWarning("[CetBridge] bind 127.0.0.1:{Port} impossible ({Msg}) — transport fichier seul.",
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

            // Une seule connexion à la fois : la nouvelle remplace l'ancienne (comme l'upstream).
            var old = Interlocked.Exchange(ref _client, client);
            if (old is not null)
            {
                // Les requêtes en vol appartiennent à l'ancienne connexion : on les échoue
                // ICI, avec une erreur explicite — l'appelant sait que c'est une reconnexion
                // (jeu relancé / mod rechargé) et qu'un simple réessai suffit.
                FailAllPending("Connexion CETBridge remplacée (jeu relancé ou mod rechargé) — réessayer l'appel.");
                old.Dispose();
            }
            _stream = client.GetStream();
            Volatile.Write(ref _lastHeartbeatTicks, DateTime.UtcNow.Ticks);
            _log?.LogInformation("[CetBridge] CETBridge connecté (TCP).");
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
            _log?.LogDebug("[CetBridge] lecture socket terminée : {Msg}", ex.Message);
        }
        finally
        {
            if (Interlocked.CompareExchange(ref _client, null, client) == client)
            {
                // Déconnexion réelle (le jeu/mod a fermé) : les requêtes en vol ne
                // recevront jamais de réponse. Si la connexion a été REMPLACÉE,
                // AcceptLoopAsync a déjà échoué les requêtes de l'ancienne connexion —
                // ne pas toucher ici à celles de la nouvelle.
                _stream = null;
                _log?.LogInformation("[CetBridge] CETBridge déconnecté.");
                FailAllPending("Pont déconnecté (le jeu/mod a fermé la connexion).");
            }
            client.Dispose();
        }
    }

    private void DispatchMessage(string json)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch { _log?.LogDebug("[CetBridge] trame JSON illisible ignorée."); return; }
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

    // ── API publique : un appel = une requête au jeu ───────────────────────────

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

        // Repli fichier
        var dir = ResolveBridgeDir(gamePath);
        if (dir == null)
            return new BridgeResponse(id, false, null,
                "Pont non connecté (TCP) et dossier du mod inconnu : passe `gamePath`, ou définis " +
                "CET_BRIDGE_DIR, ou installe RedSocket pour le transport TCP.", false, "file");
        if (!Directory.Exists(dir))
            return new BridgeResponse(id, false, null,
                $"Dossier du mod introuvable : {dir}. Le jeu tourne-t-il avec CET + le mod CETBridge installé ?",
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
            return new BridgeResponse(id, false, null, $"Écriture socket échouée : {ex.Message}", false, "tcp");
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var completed = await Task.WhenAny(tcs.Task, Task.Delay(_timeout, timeoutCts.Token));
        if (completed == tcs.Task)
        {
            timeoutCts.Cancel(); // arrête le Task.Delay
            return await tcs.Task;
        }
        _pending.TryRemove(id, out _);
        return new BridgeResponse(id, false, null,
            $"Timeout du pont : aucune réponse en {_timeout.TotalMilliseconds:n0} ms. " +
            "Le jeu est-il actif et réactif (pas en pause/chargement) ?", true, "tcp");
    }

    private async Task WriteFrameAsync(string json, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(json + "\r\n");
        await _writeLock.WaitAsync(ct);
        try
        {
            var s = _stream ?? throw new IOException("flux TCP indisponible (déconnecté).");
            await s.WriteAsync(bytes, ct);
            await s.FlushAsync(ct);
        }
        finally { _writeLock.Release(); }
    }

    // ── État ───────────────────────────────────────────────────────────────────

    private bool TcpClientConnected
    {
        get { var c = _client; return c is { Connected: true }; }
    }

    /// <summary>Instantané de connectivité pour <c>live_status</c>. Ne requiert pas le
    /// jeu (diagnostique aussi quand il est éteint).</summary>
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

    /// <summary>Dossier du mod CETBridge : CET_BRIDGE_DIR sinon dérivé du gamePath
    /// (&lt;jeu&gt;/bin/x64/plugins/cyber_engine_tweaks/mods/CETBridge).</summary>
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
        FailAllPending("Serveur en arrêt.");
        try { _listener?.Stop(); } catch { /* ignore */ }
        _client?.Dispose();
        _writeLock.Dispose();
        _cts?.Dispose();
    }
}

/// <summary>
/// Découpe un flux d'octets en messages JSON délimités par "\r\n" (gère les trames
/// fragmentées sur plusieurs lectures socket). Travaille en octets pour ne jamais
/// couper un caractère UTF-8 multi-octet au milieu.
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

/// <summary>Helpers protocole purs (testables sans socket ni jeu).</summary>
internal static class BridgeProtocol
{
    /// <summary>Construit un <see cref="BridgeResponse"/> depuis l'élément JSON racine
    /// d'une réponse <c>{ id, ok, result?, error? }</c>.</summary>
    internal static BridgeResponse ToResponse(JsonElement root, string transport)
    {
        string id = root.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.String
            ? (i.GetString() ?? "") : "";
        bool ok = root.TryGetProperty("ok", out var o) && o.ValueKind == JsonValueKind.True;
        string? result = ReadStringLike(root, "result");
        string? error = ReadStringLike(root, "error");
        if (!ok && error == null) error = "erreur inconnue (champ 'error' absent).";
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
            return new BridgeResponse("", false, null, "Réponse JSON malformée du pont.", false, transport);
        }
    }

    /// <summary>Transport fichier : écrit la requête dans <c>command.json</c> (tmp +
    /// rename atomique), purge l'ancienne <c>response.json</c>, puis attend la réponse.
    /// L'attente est pilotée par un <see cref="FileSystemWatcher"/> (latence ~ms) avec
    /// un poll de filet toutes les 250 ms ; si le watcher ne peut pas s'installer
    /// (répertoire réseau exotique), on retombe sur le poll pur à 50 ms de l'upstream.</summary>
    internal static async Task<BridgeResponse> FileSendAsync(
        string id, string requestJson, string bridgeDir, TimeSpan timeout, CancellationToken ct)
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
                void Signal() { try { responseReady.Release(); } catch { /* sémaphore saturé */ } }
                watcher.Created += (_, _) => Signal();
                watcher.Changed += (_, _) => Signal();
                watcher.Renamed += (_, _) => Signal();
                watcher.EnableRaisingEvents = true;
            }
            catch
            {
                watcher?.Dispose();
                watcher = null; // poll pur
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
                        SafeDelete(res);
                        if (!string.IsNullOrWhiteSpace(data))
                        {
                            var resp = ParseResponse(data, "file");
                            if (resp.Id == id) return resp;
                            // Réponse périmée d'une autre requête — on continue d'attendre.
                        }
                    }
                }
                catch (IOException) { /* fichier en cours d'écriture : on retente */ }
                await responseReady.WaitAsync(pollInterval, ct);
            }

            SafeDelete(cmd);
            return new BridgeResponse(id, false, null,
                $"Timeout du pont : aucune réponse en {timeout.TotalMilliseconds:n0} ms (transport fichier). " +
                "Le jeu tourne-t-il avec le mod CETBridge chargé ?", true, "file");
        }
        finally
        {
            watcher?.Dispose();
        }
    }

    private static void SafeDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* ignore */ }
    }
}
