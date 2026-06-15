using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace WolvenKitMcp;

/// <summary>Résultat d'une invocation de cp77tools (daemon ou sous-processus).</summary>
public sealed record CliResult(int ExitCode, string Stdout, string Stderr, bool TimedOut)
{
    public bool Success => !TimedOut && ExitCode == 0;
}

/// <summary>
/// Exécute les commandes WolvenKit pour le serveur MCP.
///
/// Chemin rapide : un <c>WolvenKitDaemon</c> persistant est démarré une fois
/// (HashService chargé une seule fois ~6 s) ; les requêtes suivantes lui sont
/// envoyées par IPC stdio et coûtent quelques millisecondes.
///
/// Repli : si le daemon est indisponible, chaque appel relance <c>cp77tools</c>
/// en sous-processus (comportement d'origine, ~6 s/appel mais fonctionnel).
///
/// Variables d'environnement (toutes optionnelles) :
///   WOLVENKIT_DAEMON               chemin de WolvenKitDaemon.dll
///   WOLVENKIT_CP77TOOLS            chemin de l'exécutable cp77tools (repli)
///   DOTNET_ROOT / WOLVENKIT_DOTNET_ROOT   racine du runtime .NET
///   WOLVENKIT_CLI_TIMEOUT_SECONDS  délai max d'une commande (défaut 300)
/// </summary>
public sealed class Cp77ToolsRunner : IDisposable
{
    private readonly string _cp77tools;
    private readonly string? _dotnetRoot;
    private readonly string _dotnetExe;
    private readonly string _daemonDll;
    private readonly TimeSpan _timeout;

    private readonly SemaphoreSlim _initLock = new(1, 1); // sérialise le démarrage du daemon
    private readonly SemaphoreSlim _writeLock = new(1, 1); // sérialise les écritures stdin
    private Process? _daemon;
    private StreamWriter? _toDaemon;
    private StreamReader? _fromDaemon;
    private Task? _readLoop;
    private bool _daemonDisabled;
    private int _nextId;

    // Requêtes en vol, indexées par id ; renseignées par SendToDaemonAsync,
    // complétées par la boucle de lecture quand la réponse arrive. LastActivity
    // est rafraîchi à chaque message de progression (timeout d'inactivité).
    private sealed class Outstanding
    {
        public required TaskCompletionSource<CliResult> Tcs { get; init; }
        public IProgress<string>? Progress;
        public long LastActivity = Environment.TickCount64;
    }

    private readonly ConcurrentDictionary<int, Outstanding> _outstanding = new();

    // Cache des listings d'archives (clé = chemin absolu). Invalidé par mtime + taille
    // (le mtime seul peut survivre à un repack très rapide).
    private readonly ConcurrentDictionary<string, ArchiveListing> _archiveCache = new();
    private long _cacheHits;
    private long _cacheMisses;

    // Métriques par verbe (count + percentiles via anneau circulaire des 100 dernières durées).
    private readonly ConcurrentDictionary<string, RunnerMetrics> _metrics = new();

    private sealed record ArchiveListing(DateTime Mtime, long Size, IReadOnlyList<string> Entries);

    // Plafonds d'inactivité par verbe : les verbes lourds (uncook d'une grosse archive,
    // build d'un projet complet) dépassent légitimement le délai par défaut. Le délai
    // est ré-armé par la progression du daemon — c'est un timeout d'INACTIVITÉ, pas de
    // durée totale. La variable WOLVENKIT_CLI_TIMEOUT_SECONDS reste le plancher.
    private static readonly Dictionary<string, int> LongVerbSeconds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["uncook"] = 900,
        ["unbundle"] = 600,
        ["export"] = 600,
        ["import"] = 600,
        ["pack"] = 600,
        ["build"] = 900,
        ["wwise"] = 600,
        ["opus-import"] = 900,
        ["export-entity"] = 600,
        ["export-materials"] = 600,
        ["conflicts"] = 600,
    };

    private TimeSpan TimeoutFor(string verb)
        => LongVerbSeconds.TryGetValue(verb, out var s)
            ? TimeSpan.FromSeconds(Math.Max(s, _timeout.TotalSeconds))
            : _timeout;

    private sealed class RunnerMetrics
    {
        public long Count;
        public long TotalMs;
        private const int Capacity = 100;
        private readonly long[] _samples = new long[Capacity];
        private int _index;
        private readonly object _lock = new();

        public void Record(long ms)
        {
            Interlocked.Increment(ref Count);
            Interlocked.Add(ref TotalMs, ms);
            lock (_lock)
            {
                _samples[_index % Capacity] = ms;
                _index++;
            }
        }

        public (long p50, long p95, int sampleCount) Percentiles()
        {
            long[] copy;
            int n;
            lock (_lock)
            {
                n = Math.Min(_index, Capacity);
                copy = new long[n];
                Array.Copy(_samples, copy, n);
            }
            if (n == 0) return (0, 0, 0);
            Array.Sort(copy);
            return (copy[(int)(n * 0.50)], copy[Math.Min(n - 1, (int)(n * 0.95))], n);
        }
    }

    /// <summary>Stats du cache des listings d'archives (hits / misses depuis le
    /// démarrage du serveur, plus la taille courante). Exposé via
    /// <c>wolvenkit_status</c>.</summary>
    public (long hits, long misses, int entries) CacheStats
        => (Interlocked.Read(ref _cacheHits),
            Interlocked.Read(ref _cacheMisses),
            _archiveCache.Count);

    /// <summary>Snapshot des métriques par verbe (top par count). Exposé via
    /// <c>wolvenkit_status</c> sous la clé <c>metrics</c>.</summary>
    public IReadOnlyList<(string verb, long calls, long totalMs, long p50, long p95)> MetricsSnapshot()
        => _metrics
            .Select(kv =>
            {
                var (p50, p95, _) = kv.Value.Percentiles();
                return (kv.Key, kv.Value.Count, kv.Value.TotalMs, p50, p95);
            })
            .OrderByDescending(t => t.Count)
            .ToList();

    /// <summary>Réinitialise les compteurs de métriques (utilisé par
    /// <c>clear_cache(scope=metrics|all)</c>).</summary>
    public void ResetMetrics() => _metrics.Clear();

    /// <summary>Instance partagée — utilisée aussi par les ressources MCP.</summary>
    public static Cp77ToolsRunner Shared { get; } = new();

    public Cp77ToolsRunner()
    {
        _cp77tools = ResolveCp77Tools();
        _dotnetRoot = ResolveDotnetRoot();
        _daemonDll = ResolveDaemonDll();

        var dotnetExeName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "dotnet.exe" : "dotnet";
        _dotnetExe = _dotnetRoot is not null && File.Exists(Path.Combine(_dotnetRoot, dotnetExeName))
            ? Path.Combine(_dotnetRoot, dotnetExeName)
            : dotnetExeName; // sinon : depuis le PATH

        _timeout = TimeSpan.FromSeconds(
            int.TryParse(Environment.GetEnvironmentVariable("WOLVENKIT_CLI_TIMEOUT_SECONDS"),
                out var s) && s > 0 ? s : 300);
    }

    public string ToolPath => _cp77tools;
    public bool ToolExists => File.Exists(_cp77tools);

    /// <summary>
    /// Renvoie la liste des chemins internes d'une archive .archive, depuis le
    /// cache si présent et frais (comparé au <c>LastWriteTimeUtc</c> du fichier),
    /// sinon en interrogeant le daemon (<c>archive --list</c>) puis en mettant
    /// le résultat en cache.
    /// </summary>
    public async Task<(IReadOnlyList<string> entries, bool fromCache, CliResult raw)>
        GetArchiveListingAsync(string archivePath, CancellationToken ct)
    {
        if (!File.Exists(archivePath))
            return (Array.Empty<string>(), false, new CliResult(-1, "", $"Archive introuvable : {archivePath}", false));

        var fi = new FileInfo(archivePath);
        var mtime = fi.LastWriteTimeUtc;
        var size = fi.Length;
        if (_archiveCache.TryGetValue(archivePath, out var cached)
            && cached.Mtime == mtime && cached.Size == size)
        {
            Interlocked.Increment(ref _cacheHits);
            return (cached.Entries, true, new CliResult(0, "", "", false));
        }

        Interlocked.Increment(ref _cacheMisses);
        var r = await RunAsync(new[] { "archive", archivePath, "--list" }, ct);
        var entries = ExtractListingEntries(r.Stdout + r.Stderr);
        if (entries.Count > 0)
            _archiveCache[archivePath] = new ArchiveListing(mtime, size, entries);
        return (entries, false, r);
    }

    /// <summary>Nettoie le cache des listings d'archives (par défaut tout, ou
    /// une entrée précise).</summary>
    public void InvalidateArchiveCache(string? archivePath = null)
    {
        if (archivePath is null) _archiveCache.Clear();
        else _archiveCache.TryRemove(archivePath, out _);
    }

    /// <summary>Extrait les chemins internes d'un listing « archive --list ».
    /// Chaque ligne non vide ne commençant pas par <c>[</c> et contenant un
    /// séparateur est considérée comme un chemin REDengine.</summary>
    private static List<string> ExtractListingEntries(string log)
    {
        var list = new List<string>();
        foreach (var raw in log.Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trim();
            if (line.Length == 0 || line.StartsWith('[')) continue;
            if (line.Contains('\\') || line.Contains('/'))
                list.Add(line);
        }
        return list;
    }

    /// <summary>Exécute une commande WolvenKit (verbe + arguments façon cp77tools).
    /// Plusieurs appels peuvent être en vol simultanément : ils sont pipelinés vers
    /// le daemon et ré-appariés par ID. Le daemon, côté exécution, reste sérialisé
    /// (les bibliothèques WolvenKit ne sont pas thread-safe), mais l'IPC ne bloque
    /// plus l'enchaînement.</summary>
    public async Task<CliResult> RunAsync(IEnumerable<string> args, CancellationToken ct,
        IProgress<string>? progress = null)
    {
        var argv = args as string[] ?? args.ToArray();
        var verb = argv.Length > 0 ? argv[0] : "(empty)";
        var sw = Stopwatch.StartNew();
        try
        {
            if (!_daemonDisabled)
            {
                try
                {
                    await EnsureDaemonAsync(ct);
                    return await SendToDaemonAsync(argv, ct, progress);
                }
                catch (Exception) when (!ct.IsCancellationRequested)
                {
                    // Daemon en panne : on le tue ; il sera relancé au prochain appel.
                    // Si le DLL est carrément absent, repli définitif.
                    KillDaemon();
                    if (!File.Exists(_daemonDll))
                        _daemonDisabled = true;
                }
            }
            // Repli : sous-processus cp77tools.
            return await RunViaSubprocessAsync(argv, ct);
        }
        finally
        {
            sw.Stop();
            _metrics.GetOrAdd(verb, _ => new RunnerMetrics()).Record(sw.ElapsedMilliseconds);
        }
    }

    // ── Daemon ────────────────────────────────────────────────────────────

    private async Task EnsureDaemonAsync(CancellationToken ct)
    {
        // Fast path sans lock — déjà vivant.
        if (_daemon is { HasExited: false } && _toDaemon is not null
            && _fromDaemon is not null && _readLoop is { IsCompleted: false })
            return;

        await _initLock.WaitAsync(ct);
        try
        {
            // Re-check sous lock.
            if (_daemon is { HasExited: false } && _toDaemon is not null
                && _fromDaemon is not null && _readLoop is { IsCompleted: false })
                return;

            KillDaemon();

            if (!File.Exists(_daemonDll))
                throw new FileNotFoundException("WolvenKitDaemon introuvable", _daemonDll);

            var psi = new ProcessStartInfo
            {
                FileName = _dotnetExe,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add(_daemonDll);

            var proc = Process.Start(psi)
                       ?? throw new InvalidOperationException("échec du démarrage du daemon");
            _daemon = proc;
            _toDaemon = proc.StandardInput;
            _fromDaemon = proc.StandardOutput;

            // Vider la sortie d'erreur en continu (sinon le tampon du pipe bloque le daemon).
            _ = Task.Run(async () =>
            {
                try
                {
                    while (await proc.StandardError.ReadLineAsync() is not null) { }
                }
                catch { /* daemon arrêté */ }
            });

            // Attendre {"ready":true} (préchauffage de HashService, ~6-8 s).
            using var readyCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            readyCts.CancelAfter(TimeSpan.FromSeconds(90));
            var ready = await _fromDaemon.ReadLineAsync(readyCts.Token);
            if (ready is null || !ready.Contains("\"ready\"", StringComparison.Ordinal))
                throw new InvalidOperationException("le daemon n'a pas signalé sa disponibilité");

            // Démarre la boucle de lecture : aiguille chaque réponse par ID
            // vers le TaskCompletionSource correspondant.
            _readLoop = Task.Run(ReadResponseLoopAsync);
            StartWatchdog(proc);
        }
        finally
        {
            _initLock.Release();
        }
    }

    /// <summary>Watchdog : ping périodique du daemon quand il est inactif. Un daemon
    /// figé (process vivant mais bloqué) ne se détectait qu'à la requête suivante,
    /// qui partait en timeout complet ; ici on le tue proactivement pour que le
    /// prochain appel reparte sur un daemon frais. Le verbe « ping » est traité par
    /// le daemon hors execLock, donc un uncook long ne déclenche pas de faux positif.</summary>
    private void StartWatchdog(Process proc)
    {
        _ = Task.Run(async () =>
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(60));
                if (!ReferenceEquals(_daemon, proc) || proc.HasExited)
                    return; // daemon remplacé ou déjà mort : le watchdog suit ce process-ci
                if (!_outstanding.IsEmpty)
                    continue; // des requêtes en vol attestent déjà de la liveness

                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    var r = await SendToDaemonAsync(new[] { "ping" }, cts.Token);
                    if (r.TimedOut && ReferenceEquals(_daemon, proc))
                    {
                        KillDaemon();
                        return;
                    }
                }
                catch
                {
                    if (ReferenceEquals(_daemon, proc))
                        KillDaemon();
                    return;
                }
            }
        });
    }

    /// <summary>Purge les dossiers temporaires du serveur (wolvenkit-mcp-*, wkmcp-*)
    /// plus vieux que 24 h. Les dossiers déterministes (read/write/inspect) ne sont
    /// jamais nettoyés en cours de session ; sans cette purge au démarrage, ils
    /// s'accumulent indéfiniment entre les sessions.</summary>
    public static void PurgeStaleTempDirs()
    {
        var cutoff = DateTime.UtcNow - TimeSpan.FromHours(24);
        var temp = Path.GetTempPath();
        foreach (var pattern in new[] { "wolvenkit-mcp-*", "wkmcp-*" })
        {
            string[] roots;
            try { roots = Directory.GetDirectories(temp, pattern); }
            catch { continue; }
            foreach (var root in roots)
            {
                try
                {
                    if (Directory.GetLastWriteTimeUtc(root) < cutoff)
                        Directory.Delete(root, recursive: true);
                    else
                        // Racine récente : purger ses sous-dossiers anciens (les
                        // dossiers déterministes par hash s'accumulent dedans).
                        foreach (var child in Directory.GetDirectories(root))
                            if (Directory.GetLastWriteTimeUtc(child) < cutoff)
                                Directory.Delete(child, recursive: true);
                }
                catch { /* verrouillé ou déjà supprimé : au prochain démarrage */ }
            }
        }
    }

    /// <summary>Boucle de lecture des réponses du daemon. Tourne pendant toute la
    /// durée de vie du daemon ; à sa mort, échoue toutes les requêtes en attente.</summary>
    private async Task ReadResponseLoopAsync()
    {
        var reader = _fromDaemon;
        if (reader is null) return;

        try
        {
            string? line;
            while ((line = await reader.ReadLineAsync()) is not null)
            {
                int id;
                CliResult? result;
                string? progressMsg;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    id = root.TryGetProperty("id", out var i) ? i.GetInt32() : 0;
                    if (root.TryGetProperty("progress", out var p))
                    {
                        progressMsg = p.GetString();
                        result = null;
                    }
                    else
                    {
                        progressMsg = null;
                        var exit = root.TryGetProperty("exit", out var e) ? e.GetInt32() : -1;
                        var output = root.TryGetProperty("output", out var o) ? o.GetString() ?? "" : "";
                        result = new CliResult(exit, output, "", false);
                    }
                }
                catch
                {
                    // Ligne malformée ; ignore (on perdrait une réponse, mais on
                    // veut surtout ne pas casser la boucle de lecture).
                    continue;
                }

                if (result is null)
                {
                    // Message de progression : ré-arme le timeout d'inactivité et
                    // relaie au client MCP si l'outil a fourni un IProgress.
                    if (_outstanding.TryGetValue(id, out var inflight))
                    {
                        inflight.LastActivity = Environment.TickCount64;
                        if (progressMsg is not null)
                            inflight.Progress?.Report(progressMsg);
                    }
                    continue;
                }

                if (_outstanding.TryRemove(id, out var entry))
                    entry.Tcs.TrySetResult(result);
            }
        }
        catch { /* daemon est mort */ }

        // Daemon fermé : on échoue toutes les requêtes en vol.
        foreach (var kvp in _outstanding)
            kvp.Value.Tcs.TrySetException(new IOException("le daemon a fermé sa sortie"));
        _outstanding.Clear();
    }

    private async Task<CliResult> SendToDaemonAsync(string[] argv, CancellationToken ct,
        IProgress<string>? progress = null)
    {
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<CliResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entry = new Outstanding { Tcs = tcs, Progress = progress };
        _outstanding[id] = entry;

        // Sérialise les écritures stdin : un seul writer à la fois pour ne pas
        // entrelacer deux JSON-lines.
        await _writeLock.WaitAsync(ct);
        try
        {
            await _toDaemon!.WriteLineAsync(JsonSerializer.Serialize(new { id, argv }));
            await _toDaemon.FlushAsync();
        }
        finally
        {
            _writeLock.Release();
        }

        // Timeout d'INACTIVITÉ : tant que le daemon émet de la progression, le verbe
        // travaille — on ré-arme le délai au lieu de tuer un uncook long mais vivant.
        var timeout = TimeoutFor(argv.Length > 0 ? argv[0] : "");
        while (true)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            try
            {
                return await tcs.Task.WaitAsync(cts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                var idle = Environment.TickCount64 - entry.LastActivity;
                if (idle < timeout.TotalMilliseconds)
                    continue; // de la progression est arrivée : on laisse travailler

                _outstanding.TryRemove(id, out _);
                KillDaemon();
                return new CliResult(-1, "",
                    $"Délai d'inactivité dépassé ({timeout.TotalSeconds:F0} s sans réponse " +
                    "ni progression) — daemon interrompu.", true);
            }
        }
    }

    private void KillDaemon()
    {
        try { _daemon?.Kill(entireProcessTree: true); } catch { /* déjà mort */ }
        try { _daemon?.Dispose(); } catch { /* ignore */ }
        _daemon = null;
        _toDaemon = null;
        _fromDaemon = null;
        // _readLoop sortira naturellement quand le stream se ferme et fera échouer
        // les requêtes en vol via _outstanding. Pas besoin d'attendre ici.
        _readLoop = null;
    }

    // ── Repli : sous-processus cp77tools ──────────────────────────────────

    private async Task<CliResult> RunViaSubprocessAsync(string[] argv, CancellationToken ct)
    {
        if (!ToolExists)
        {
            return new CliResult(-1, "",
                $"cp77tools introuvable : {_cp77tools}\n" +
                "Installer avec : dotnet tool install -g WolvenKit.CLI " +
                "(ou définir la variable WOLVENKIT_CP77TOOLS).", false);
        }

        var psi = new ProcessStartInfo
        {
            FileName = _cp77tools,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in argv)
            psi.ArgumentList.Add(a);
        if (_dotnetRoot is not null)
            psi.Environment["DOTNET_ROOT"] = _dotnetRoot;

        using var proc = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        try
        {
            proc.Start();
        }
        catch (Exception ex)
        {
            return new CliResult(-1, "", $"Échec du lancement de cp77tools : {ex.Message}", false);
        }

        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_timeout);
        try
        {
            await proc.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* déjà mort */ }
            return new CliResult(-1, stdout.ToString(), stderr.ToString(), true);
        }

        proc.WaitForExit(); // garantit le drainage des flux redirigés
        return new CliResult(proc.ExitCode, stdout.ToString(), stderr.ToString(), false);
    }

    // ── Résolution des chemins ────────────────────────────────────────────

    /// <summary>Localise WolvenKitDaemon.dll (variable WOLVENKIT_DAEMON, sinon projet frère).</summary>
    private static string ResolveDaemonDll()
    {
        var explicitPath = Environment.GetEnvironmentVariable("WOLVENKIT_DAEMON");
        if (!string.IsNullOrWhiteSpace(explicitPath))
            return explicitPath;

        // Défaut : projet frère wolvenkit-mcp/src/WolvenKitDaemon, même configuration.
        try
        {
            var net8 = new DirectoryInfo(AppContext.BaseDirectory);   // .../WolvenKitMcp/bin/<cfg>/net8.0
            var config = net8.Parent?.Name ?? "Debug";
            var src = net8.Parent?.Parent?.Parent?.Parent;            // .../src
            if (src is not null)
                return Path.Combine(src.FullName, "WolvenKitDaemon", "bin", config, "net8.0",
                    "WolvenKitDaemon.dll");
        }
        catch { /* repli ci-dessous */ }

        return "WolvenKitDaemon.dll"; // introuvable → repli sous-processus
    }

    private static string ResolveCp77Tools()
    {
        var explicitPath = Environment.GetEnvironmentVariable("WOLVENKIT_CP77TOOLS");
        if (!string.IsNullOrWhiteSpace(explicitPath))
            return explicitPath;

        var exeName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "cp77tools.exe"
            : "cp77tools";

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var defaultPath = Path.Combine(home, ".dotnet", "tools", exeName);
        if (File.Exists(defaultPath))
            return defaultPath;

        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathVar.Split(Path.PathSeparator,
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(dir, exeName);
                if (File.Exists(candidate))
                    return candidate;
            }
            catch { /* entrée PATH invalide */ }
        }

        return defaultPath;
    }

    private static string? ResolveDotnetRoot()
    {
        var explicitRoot = Environment.GetEnvironmentVariable("WOLVENKIT_DOTNET_ROOT")
                        ?? Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrWhiteSpace(explicitRoot) && Directory.Exists(explicitRoot))
            return explicitRoot;

        try
        {
            var coreLib = typeof(object).Assembly.Location;
            if (!string.IsNullOrEmpty(coreLib))
            {
                var root = Directory.GetParent(coreLib)?.Parent?.Parent?.Parent;
                if (root is not null && Directory.Exists(Path.Combine(root.FullName, "shared")))
                    return root.FullName;
            }
        }
        catch { /* repli */ }

        return null;
    }

    public void Dispose()
    {
        KillDaemon();
        _initLock.Dispose();
        _writeLock.Dispose();
    }
}
